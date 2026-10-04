using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Mapping;

namespace GenericVectorBuilder.Core.Pipeline;

/// <summary>
/// Runs one pipeline end to end: source, mapper, chunker, embedder, then every sink at once.
/// The rules that make re-runs cheap and safe:
/// - A document is embedded only when at least one sink lacks its current content hash.
/// - Each sink commits on its own. Before a write, state records every chunk id the sink may
///   end up holding; after the sink acknowledged it, state records the final ids. So a failed
///   or cancelled write never leaves a chunk that state does not know about, and a failed sink
///   simply gets those documents again next run.
/// - A failing sink is dropped from the run while the others finish; the run ends Partial.
/// - Deletes are a whitelist. A stored document the run did not see is deleted only when its
///   origin was read completely this run, or the source positively confirms the origin is gone.
///   Anything else (a rejected file, a file with skipped rows, a folder that could not be
///   listed) keeps its old vectors instead of looking like "all rows removed".
/// - A guard refuses every delete when the source produced no rows at all, or when a sink
///   would lose more than half of what it held before the run, because both usually mean an
///   unmounted share or the wrong folder rather than real removals.
/// - A sink whose collection was recreated or emptied behind the pipeline's back has its state
///   discarded, so it is rewritten instead of staying empty while every row looks unchanged.
/// - A pipeline remembers its embedder fingerprint and refuses a different one, because
///   vectors from two models in one index give meaningless distances.
/// </summary>
public sealed class PipelineRunner
{
   #region Data Members

   private const int FLUSH_CHUNKS = 64;
   private const int DELETE_BATCH = 500;

   /// <summary>Content hash stored for a document whose first write is not acknowledged yet; never matches a real hash.</summary>
   private const string PENDING_HASH = "";

   private readonly IEmbedder _embedder;
   private readonly IReadOnlyList<ISink> _sinks;
   private readonly IStateStore _state;
   private readonly IChunker _chunker;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the runner with the default text chunker. Kept so every existing caller compiles
   /// and behaves exactly as before.
   /// </summary>
   /// <param name="embedder">Embedder for this run.</param>
   /// <param name="sinks">Sinks to write to.</param>
   /// <param name="state">State store.</param>
   /// <param name="chunker">Chunker.</param>
   public PipelineRunner( IEmbedder embedder, IReadOnlyList<ISink> sinks, IStateStore state, TextChunker chunker )
      : this( embedder, sinks, state, (IChunker)chunker )
   {
   }

   /// <summary>
   /// Creates the runner with any chunker, for example the code chunker that splits source files
   /// by type and member instead of by line.
   /// </summary>
   /// <param name="embedder">Embedder for this run.</param>
   /// <param name="sinks">Sinks to write to.</param>
   /// <param name="state">State store.</param>
   /// <param name="chunker">Chunker.</param>
   public PipelineRunner( IEmbedder embedder, IReadOnlyList<ISink> sinks, IStateStore state, IChunker chunker )
   {
      _embedder = embedder;
      _sinks = sinks;
      _state = state;
      _chunker = chunker;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Turns off the mass-delete guard for this run, for when the user confirmed that most of the
   /// data really was removed. Off by default.
   /// </summary>
   public bool AllowLargeDeletes { get; init; }

   /// <summary>
   /// Runs the pipeline. Never throws for run-level failures; the outcome is in
   /// <paramref name="progress"/>. Cancellation ends the run as Cancelled, with a message that
   /// says truthfully whether anything was already written or deleted.
   /// </summary>
   /// <param name="pipeline">Sanitized pipeline name.</param>
   /// <param name="source">Source to read.</param>
   /// <param name="mapper">Mapper for this run.</param>
   /// <param name="progress">Progress to update.</param>
   /// <param name="ct">Cancellation.</param>
   public Task RunAsync( string pipeline, ISource source, RowDocumentMapper mapper, RunProgress progress, CancellationToken ct )
   {
      return RunAsync( pipeline, source, (IDocumentMapper)mapper, progress, ct );
   }

   /// <summary>
   /// Runs the pipeline with any mapper, for example the code mapper that turns a source file
   /// into one document keyed by its path. Same rules and outcomes as the table-row overload;
   /// the row-only counters (rows without a key, unused table settings) are reported only when
   /// the mapper is a <see cref="RowDocumentMapper"/>.
   /// </summary>
   /// <param name="pipeline">Sanitized pipeline name.</param>
   /// <param name="source">Source to read.</param>
   /// <param name="mapper">Mapper for this run.</param>
   /// <param name="progress">Progress to update.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task RunAsync( string pipeline, ISource source, IDocumentMapper mapper, RunProgress progress, CancellationToken ct )
   {
      progress.Status = RunStatus.Running;
      try
      {
         RunContext context = await PrepareAsync( pipeline, progress, ct );
         progress.Message = $"reading {source.Description}";
         await StreamAsync( context, source, mapper, progress, ct );
         progress.Message = "removing rows that left the source";
         await DeleteStaleAsync( context, source, progress, ct );
         Publish( source, mapper, progress );
         FinishCompleted( context, progress );
      }
      catch( OperationCanceledException ) when( ct.IsCancellationRequested )
      {
         Publish( source, mapper, progress );
         progress.Finish( RunStatus.Cancelled, CancelMessage( progress ) );
      }
      catch( Exception ex )
      {
         Publish( source, mapper, progress );
         progress.Finish( RunStatus.Failed, ex.Message );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Copies the source's and mapper's findings into progress. Called before every Finish so a
   /// finished snapshot always carries its reject list and counters.
   /// </summary>
   /// <param name="source">Source.</param>
   /// <param name="mapper">Mapper.</param>
   /// <param name="progress">Progress.</param>
   private static void Publish( ISource source, IDocumentMapper mapper, RunProgress progress )
   {
      progress.Rejected = source.Report.Rejected.ToList();
      progress.SkippedRows = source.Report.SkippedRows;
      progress.MissingKeyRows = MissingKeyRows( mapper );
   }

   /// <summary>
   /// Rows whose key column was empty, which only a table-row mapper can have.
   /// </summary>
   /// <param name="mapper">Mapper.</param>
   /// <returns>The count, or 0 for any other mapper.</returns>
   private static long MissingKeyRows( IDocumentMapper mapper )
   {
      return mapper is RowDocumentMapper rows ? rows.MissingKeyRows : 0;
   }

   /// <summary>
   /// Ends a run that got through every phase: Completed, or Partial when anything was dropped,
   /// skipped, kept without explanation, or refused.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="progress">Progress.</param>
   private static void FinishCompleted( RunContext context, RunProgress progress )
   {
      bool partial = context.FailedSinks > 0 || progress.DocErrors > 0 || progress.SkippedRows > 0
         || context.UnconfirmedOrigins > 0 || context.DeletesRefused != null;
      string message = context.DeletesRefused ?? ( partial ? "finished with problems, see errors" : "finished" );
      progress.Finish( partial ? RunStatus.Partial : RunStatus.Completed, message );
   }

   /// <summary>
   /// Builds the cancel message from what actually reached the sinks, so it never claims that
   /// nothing changed when chunks were written or rows removed before the stop.
   /// </summary>
   /// <param name="progress">Progress.</param>
   /// <returns>The message.</returns>
   private static string CancelMessage( RunProgress progress )
   {
      RunSnapshot snapshot = progress.Snapshot();
      long written = snapshot.ChunksWritten.Values.Sum();
      if( written == 0 && snapshot.DocsDeleted == 0 )
      {
         return "cancelled; nothing was written or deleted";
      }

      return $"cancelled part way: {written} chunk(s) were written and {snapshot.DocsDeleted} row(s) were removed before it stopped. Run again to finish.";
   }

   /// <summary>
   /// Checks the embedder, the pipeline's fingerprint and every sink, then loads sink state.
   /// A sink that cannot be prepared is dropped; if none are left the run fails.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="progress">Progress.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The run context.</returns>
   private async Task<RunContext> PrepareAsync( string pipeline, RunProgress progress, CancellationToken ct )
   {
      progress.Message = "checking the embedding service";
      await _embedder.PreflightAsync( ct );
      string? stored = await _state.GetFingerprintAsync( pipeline, ct );
      if( stored != null && stored != _embedder.Fingerprint )
      {
         throw new InvalidOperationException( $"Pipeline '{pipeline}' was built with a different embedder ({stored}). Reset it or use a new pipeline name." );
      }

      var context = new RunContext( pipeline );
      progress.Message = "checking destinations";
      foreach( ISink sink in _sinks )
      {
         try
         {
            await PrepareSinkAsync( context, sink, progress, ct );
         }
         catch( Exception ex ) when( ex is not OperationCanceledException )
         {
            context.FailedSinks++;
            progress.SinkFailed( sink.Name, ex.Message );
         }
      }

      if( context.ActiveSinks.Count == 0 )
      {
         throw new InvalidOperationException( "No destination is reachable. See the sink errors." );
      }

      await _state.SetFingerprintAsync( pipeline, _embedder.Fingerprint, ct );
      return context;
   }

   /// <summary>
   /// Makes sure one sink's collection exists and loads what state says it holds. When the
   /// collection had to be created, or still exists but holds fewer chunks than state proves
   /// were written, the old state no longer describes the sink: it is discarded so every
   /// document is written to it again instead of being skipped as unchanged forever. Records
   /// the document count the mass-delete guard measures against.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="sink">The sink.</param>
   /// <param name="progress">Progress.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task PrepareSinkAsync( RunContext context, ISink sink, RunProgress progress, CancellationToken ct )
   {
      string pipeline = context.Pipeline;
      bool created = await sink.EnsureCollectionAsync( pipeline, _embedder.Dimension, ct );
      Dictionary<string, DocState> states = await _state.LoadAsync( pipeline, sink.Name, ct );
      if( states.Count > 0 && ( created || await LostChunksAsync( pipeline, sink, states, progress, ct ) ) )
      {
         await _state.ForgetSinkAsync( pipeline, sink.Name, ct );
         states = new Dictionary<string, DocState>( StringComparer.Ordinal );
         if( created )
         {
            progress.Note( $"{sink.Name}: this pipeline's collection was missing and has been created again, so every row is written again." );
         }
      }

      context.States[sink.Name] = states;
      context.StoredBefore[sink.Name] = states.Count;
      context.ActiveSinks.Add( sink );
   }

   /// <summary>
   /// Compares the sink's real chunk count with state and reports what it finds.
   /// Fewer chunks than the documents state has a real hash for means the collection was
   /// emptied behind the pipeline's back (truncated, points deleted by hand): every such
   /// document has at least one chunk in the sink even after an interrupted write, because a
   /// write upserts before it deletes old chunks. The test deliberately does not use the full
   /// chunk-id total, because state lists a superset of the sink after a failed or cancelled
   /// write, and treating that as "emptied" would re-embed the whole pipeline after every
   /// cancel. The one ordinary event that can still trip it is a crash between a sink delete
   /// and the state update that follows it; the cost then is a full rewrite, never lost data.
   /// More chunks than every id state records means chunks state does not know about; that is
   /// only reported, because deleting what state cannot account for could destroy real data.
   /// A sink that cannot count skips the check.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="sink">The sink.</param>
   /// <param name="states">The sink's loaded states.</param>
   /// <param name="progress">Progress, for the notes.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the sink lost chunks and its state must be discarded.</returns>
   private static async Task<bool> LostChunksAsync( string pipeline, ISink sink, Dictionary<string, DocState> states, RunProgress progress, CancellationToken ct )
   {
      long held;
      try
      {
         held = await sink.CountAsync( pipeline, ct );
      }
      catch( NotSupportedException )
      {
         return false;
      }

      long proven = states.Values.LongCount( s => s.ContentHash != PENDING_HASH );
      if( held < proven )
      {
         progress.Note( $"{sink.Name}: holds {held} chunk(s) but this pipeline wrote at least {proven} there, so it was emptied or cleared outside this app. Every row is written to it again." );
         return true;
      }

      long recorded = states.Values.Sum( s => (long)s.ChunkIds.Count );
      if( held > recorded )
      {
         progress.Note( $"{sink.Name}: holds {held} chunk(s), {held - recorded} more than this pipeline has a record of. They were left alone; reset the pipeline if they should go." );
      }

      return false;
   }

   /// <summary>
   /// Reads every record, skips what is unchanged everywhere, and batches the rest for
   /// embedding. Then saves origin moves, removes leftover chunks of unchanged documents, and
   /// tells the user about skipped rows and settings that did not apply.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="source">Source.</param>
   /// <param name="mapper">Mapper.</param>
   /// <param name="progress">Progress.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task StreamAsync( RunContext context, ISource source, IDocumentMapper mapper, RunProgress progress, CancellationToken ct )
   {
      await foreach( SourceRecord record in source.ReadAsync( ct ) )
      {
         progress.RecordRead();
         context.RecordsRead++;
         Document? document = MapRecord( context, mapper, record, progress );
         progress.SkippedRows = source.Report.SkippedRows;
         if( document == null )
         {
            continue;
         }

         context.Seen.Add( document.DocKey );
         IReadOnlyList<Chunk> chunks = _chunker.Split( context.Pipeline, document );
         List<ISink> needing = SinksNeeding( context, document, chunks );
         if( needing.Count == 0 )
         {
            progress.Unchanged();
            continue;
         }

         context.Pending.Add( new PendingDocument( document, chunks, needing ) );
         context.PendingChunks += chunks.Count;
         if( context.PendingChunks >= FLUSH_CHUNKS )
         {
            await FlushAsync( context, progress, ct );
         }
      }

      await FlushAsync( context, progress, ct );
      await SaveMovedOriginsAsync( context, ct );
      await CleanUpExtraChunksAsync( context, progress, ct );
      ReportReadProblems( source.Report, mapper, progress );
   }

   /// <summary>
   /// Maps one record, turning a mapping failure into a row error that also protects the
   /// record's origin from deletes.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="mapper">Mapper.</param>
   /// <param name="record">The record.</param>
   /// <param name="progress">Progress.</param>
   /// <returns>The document, or null for an empty or failed row.</returns>
   private static Document? MapRecord( RunContext context, IDocumentMapper mapper, SourceRecord record, RunProgress progress )
   {
      Document? document;
      try
      {
         document = mapper.Map( record );
      }
      catch( Exception ex )
      {
         context.ErrorOrigins.Add( record.Origin );
         progress.DocError( $"{record.Origin} row {record.RowNumber}: {ex.Message}" );
         return null;
      }
      finally
      {
         progress.MissingKeyRows = MissingKeyRows( mapper );
      }

      if( document == null )
      {
         progress.EmptyRow();
      }

      return document;
   }

   /// <summary>
   /// Works out which active sinks lack this exact content. A sink needs the document when it
   /// has no state for it, a different hash, or is missing any of the current chunk ids. When a
   /// sink already has everything, only bookkeeping is left: a moved origin is updated in state
   /// (so deletes stay scoped to the right file without re-embedding), and chunk ids the current
   /// chunking no longer produces are queued for cleanup.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="document">The document.</param>
   /// <param name="chunks">The document's chunks under the current chunker.</param>
   /// <returns>Sinks that need the document written.</returns>
   private static List<ISink> SinksNeeding( RunContext context, Document document, IReadOnlyList<Chunk> chunks )
   {
      List<Guid> ids = chunks.Select( c => c.ChunkId ).ToList();
      var needing = new List<ISink>();
      foreach( ISink sink in context.ActiveSinksSnapshot() )
      {
         Dictionary<string, DocState> states = context.States[sink.Name];
         if( !states.TryGetValue( document.DocKey, out DocState? existing ) || existing.ContentHash != document.ContentHash
            || !ids.All( existing.ChunkIds.Contains ) )
         {
            needing.Add( sink );
            continue;
         }

         List<Guid> extra = existing.ChunkIds.Except( ids ).ToList();
         if( existing.Origin == document.Origin && extra.Count == 0 )
         {
            continue;
         }

         DocState updated = existing with { Origin = document.Origin, ChunkIds = ids };
         states[document.DocKey] = updated;
         if( extra.Count > 0 )
         {
            context.Cleanups.Add( new ChunkCleanup( sink, updated, extra ) );
         }
         else
         {
            context.MovedOrigins.Add( (sink.Name, updated) );
         }
      }

      return needing;
   }

   /// <summary>
   /// Embeds the pending batch once and writes it to every sink that needs it, in parallel.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="progress">Progress.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task FlushAsync( RunContext context, RunProgress progress, CancellationToken ct )
   {
      if( context.Pending.Count == 0 )
      {
         return;
      }

      var items = context.Pending.SelectMany( p => p.Chunks.Select( c => (Pending: p, Chunk: c) ) ).ToList();
      progress.Message = "embedding and writing";
      float[][] vectors = await _embedder.EmbedDocumentsAsync( items.Select( i => i.Chunk.Text ).ToList(), ct );
      progress.Embedded( context.Pending.Count, items.Count );
      var records = items.Select( ( item, i ) => new VectorRecord( item.Pending.Document, item.Chunk, vectors[i] ) ).ToList();

      List<PendingDocument> batch = context.Pending.ToList();
      await Task.WhenAll( context.ActiveSinksSnapshot().Select( sink => WriteToSinkAsync( context, sink, batch, records, progress, ct ) ) );
      context.Pending.Clear();
      context.PendingChunks = 0;
      if( context.ActiveSinks.Count == 0 )
      {
         throw new InvalidOperationException( "Every destination failed. See the sink errors." );
      }
   }

   /// <summary>
   /// Writes a batch to one sink in four steps: record every chunk id the sink may end up
   /// holding (old and new), upsert, delete chunk ids the new content no longer produces, then
   /// record the final state. A failure at any step leaves state listing a superset of what the
   /// sink holds under the OLD hash, so the next run rewrites the document and deletes every
   /// leftover id; nothing is orphaned. Any failure drops this sink from the rest of the run.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="sink">The sink.</param>
   /// <param name="batch">Pending documents.</param>
   /// <param name="records">Embedded records for the whole batch.</param>
   /// <param name="progress">Progress.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task WriteToSinkAsync( RunContext context, ISink sink, List<PendingDocument> batch, List<VectorRecord> records, RunProgress progress, CancellationToken ct )
   {
      List<PendingDocument> docs = batch.Where( p => p.Sinks.Contains( sink ) ).ToList();
      if( docs.Count == 0 )
      {
         return;
      }

      var keys = docs.Select( d => d.Document.DocKey ).ToHashSet( StringComparer.Ordinal );
      try
      {
         Dictionary<string, DocState> states = context.States[sink.Name];
         (List<DocState> pending, List<DocState> final, List<Guid> stale) = PlanWrite( states, docs );
         await _state.SaveAsync( context.Pipeline, sink.Name, pending, ct );
         List<VectorRecord> mine = records.Where( r => keys.Contains( r.Document.DocKey ) ).ToList();
         await sink.UpsertAsync( context.Pipeline, mine, ct );
         progress.Written( sink.Name, mine.Count );
         if( stale.Count > 0 )
         {
            await sink.DeleteAsync( context.Pipeline, stale, ct );
         }

         // The sink has acknowledged everything; record it even if a cancel arrives now.
         await _state.SaveAsync( context.Pipeline, sink.Name, final, CancellationToken.None );
         final.ForEach( s => states[s.DocKey] = s );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         context.Drop( sink );
         progress.SinkFailed( sink.Name, ex.Message );
      }
   }

   /// <summary>
   /// Works out the three lists a sink write needs: the pending states saved before the write
   /// (old hash and origin, old plus new chunk ids; a brand-new document gets a hash that never
   /// matches), the final states saved after it, and the old chunk ids to delete.
   /// </summary>
   /// <param name="states">The sink's current states.</param>
   /// <param name="docs">Documents being written to the sink.</param>
   /// <returns>Pending states, final states and stale chunk ids.</returns>
   private static (List<DocState> Pending, List<DocState> Final, List<Guid> Stale) PlanWrite( Dictionary<string, DocState> states, List<PendingDocument> docs )
   {
      var pending = new List<DocState>( docs.Count );
      var final = new List<DocState>( docs.Count );
      var stale = new List<Guid>();
      foreach( PendingDocument doc in docs )
      {
         List<Guid> ids = doc.Chunks.Select( c => c.ChunkId ).ToList();
         if( states.TryGetValue( doc.Document.DocKey, out DocState? old ) )
         {
            stale.AddRange( old.ChunkIds.Except( ids ) );
            pending.Add( old with { ChunkIds = old.ChunkIds.Union( ids ).ToList() } );
         }
         else
         {
            pending.Add( new DocState( doc.Document.DocKey, PENDING_HASH, doc.Document.Origin, ids ) );
         }

         final.Add( new DocState( doc.Document.DocKey, doc.Document.ContentHash, doc.Document.Origin, ids ) );
      }

      return (pending, final, stale);
   }

   /// <summary>
   /// Persists origin-only updates collected while streaming.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task SaveMovedOriginsAsync( RunContext context, CancellationToken ct )
   {
      foreach( var group in context.MovedOrigins.GroupBy( m => m.Sink ) )
      {
         await _state.SaveAsync( context.Pipeline, group.Key, group.Select( g => g.State ).ToList(), ct );
      }
   }

   /// <summary>
   /// Deletes leftover chunk ids of unchanged documents (from an interrupted earlier write or a
   /// chunker change), then saves the trimmed state. A sink whose cleanup fails is dropped; its
   /// state still lists the leftovers, so the next run tries again.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="progress">Progress.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task CleanUpExtraChunksAsync( RunContext context, RunProgress progress, CancellationToken ct )
   {
      foreach( IGrouping<ISink, ChunkCleanup> group in context.Cleanups.GroupBy( c => c.Sink ) )
      {
         if( !context.ActiveSinksSnapshot().Contains( group.Key ) )
         {
            continue;
         }

         try
         {
            foreach( ChunkCleanup[] batch in group.Chunk( DELETE_BATCH ) )
            {
               await group.Key.DeleteAsync( context.Pipeline, batch.SelectMany( c => c.Extra ).ToList(), ct );
               await _state.SaveAsync( context.Pipeline, group.Key.Name, batch.Select( c => c.State ).ToList(), CancellationToken.None );
            }
         }
         catch( Exception ex ) when( ex is not OperationCanceledException )
         {
            context.Drop( group.Key );
            progress.SinkFailed( group.Key.Name, $"removing leftover chunks failed: {ex.Message}" );
         }
      }
   }

   /// <summary>
   /// Tells the user about rows the source skipped and, for a table-row mapper, mapping settings
   /// that matched no table.
   /// </summary>
   /// <param name="report">The source's read report.</param>
   /// <param name="mapper">Mapper.</param>
   /// <param name="progress">Progress.</param>
   private static void ReportReadProblems( SourceReadReport report, IDocumentMapper mapper, RunProgress progress )
   {
      foreach( KeyValuePair<string, long> skipped in report.SkippedByOrigin.OrderBy( s => s.Key, StringComparer.Ordinal ) )
      {
         progress.Note( $"{skipped.Key}: skipped {skipped.Value} row(s) with more cells than the header. Nothing from this file was deleted; fix those rows and run again." );
      }

      IReadOnlyList<string> unused = mapper is RowDocumentMapper rows ? rows.UnusedMappings : Array.Empty<string>();
      foreach( string table in unused )
      {
         progress.Note( $"The row id or template chosen for table '{table}' matched no table in this run. If the table was renamed, its rows were keyed by content instead." );
      }
   }

   /// <summary>
   /// Deletes documents that are no longer in the source, from every sink still healthy.
   /// Skipped entirely unless the source finished reading. Only documents whose origin may drive
   /// deletes are removed (see <see cref="MayDeleteFromAsync"/>), and the mass-delete guard can
   /// refuse the whole delete.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="source">The source, for its read report and gone confirmations.</param>
   /// <param name="progress">Progress.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task DeleteStaleAsync( RunContext context, ISource source, RunProgress progress, CancellationToken ct )
   {
      if( !source.Report.Completed )
      {
         progress.DocError( "the source did not finish reading, so nothing was deleted" );
         return;
      }

      Dictionary<ISink, List<DocState>> plan = await PlanDeletesAsync( context, source, progress, ct );
      string? refusal = CheckDeleteGuard( context, plan );
      if( refusal != null )
      {
         context.DeletesRefused = refusal;
         progress.Note( refusal );
         return;
      }

      foreach( KeyValuePair<ISink, List<DocState>> entry in plan )
      {
         await DeleteFromSinkAsync( context, entry.Key, entry.Value, progress, ct );
      }
   }

   /// <summary>
   /// Sorts every stored document the run did not see into "delete" or "keep" per sink, asking
   /// once per origin. Kept documents are counted, and origins kept for no visible reason get a
   /// note, because nothing else would tell the user that part of the folder was not read.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="source">The source.</param>
   /// <param name="progress">Progress.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Documents to delete, per active sink.</returns>
   private async Task<Dictionary<ISink, List<DocState>>> PlanDeletesAsync( RunContext context, ISource source, RunProgress progress, CancellationToken ct )
   {
      var verdicts = new Dictionary<string, bool>( StringComparer.Ordinal );
      var kept = new Dictionary<string, HashSet<string>>( StringComparer.Ordinal );
      var plan = new Dictionary<ISink, List<DocState>>();
      foreach( ISink sink in context.ActiveSinksSnapshot() )
      {
         var gone = new List<DocState>();
         foreach( DocState state in context.States[sink.Name].Values.Where( s => !context.Seen.Contains( s.DocKey ) ).ToList() )
         {
            if( !verdicts.TryGetValue( state.Origin, out bool deletable ) )
            {
               deletable = await MayDeleteFromAsync( context, source, state.Origin, ct );
               verdicts[state.Origin] = deletable;
            }

            if( deletable )
            {
               gone.Add( state );
            }
            else if( !kept.TryGetValue( state.Origin, out HashSet<string>? keys ) )
            {
               kept[state.Origin] = new HashSet<string>( StringComparer.Ordinal ) { state.DocKey };
            }
            else
            {
               keys.Add( state.DocKey );
            }
         }

         plan[sink] = gone;
      }

      ReportKept( context, source.Report, kept, progress );
      return plan;
   }

   /// <summary>
   /// The whitelist: documents from an origin may be deleted only when the origin was read
   /// completely this run with no row errors, or the source confirms the origin is gone.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="source">The source.</param>
   /// <param name="origin">The stored documents' origin.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when its unseen documents may be deleted.</returns>
   private static async Task<bool> MayDeleteFromAsync( RunContext context, ISource source, string origin, CancellationToken ct )
   {
      SourceReadReport report = source.Report;
      if( context.ErrorOrigins.Contains( origin ) || report.IsRejected( origin ) || report.HasSkippedRows( origin ) )
      {
         return false;
      }

      return report.IsClean( origin ) || await source.ConfirmGoneAsync( origin, ct );
   }

   /// <summary>
   /// Counts kept documents and adds a note for each origin whose kept rows nothing else
   /// explains (not rejected, no skipped rows, no row errors). An origin the scan skipped on
   /// purpose gets a note with that reason; any other unexplained origin makes the run Partial,
   /// because it means part of the folder could not be read and nothing else says so.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="report">The source's read report.</param>
   /// <param name="kept">Kept document keys by origin.</param>
   /// <param name="progress">Progress.</param>
   private static void ReportKept( RunContext context, SourceReadReport report, Dictionary<string, HashSet<string>> kept, RunProgress progress )
   {
      progress.Kept( kept.Values.Sum( k => k.Count ) );
      foreach( KeyValuePair<string, HashSet<string>> entry in kept.OrderBy( k => k.Key, StringComparer.Ordinal ) )
      {
         if( report.IsRejected( entry.Key ) || report.HasSkippedRows( entry.Key ) || context.ErrorOrigins.Contains( entry.Key ) )
         {
            continue;
         }

         string? ignored = report.IgnoredReason( entry.Key );
         if( ignored != null )
         {
            progress.Note( $"{entry.Key}: kept {entry.Value.Count} stored row(s) because the folder scan skipped it ({ignored}). Reset the pipeline to remove them." );
            continue;
         }

         context.UnconfirmedOrigins++;
         progress.Note( $"{entry.Key}: kept {entry.Value.Count} stored row(s). It was not read this run and could not be confirmed as removed, for example a folder without read permission." );
      }
   }

   /// <summary>
   /// The mass-delete guard. Refuses every delete when the source produced no rows at all, or
   /// when any sink would lose more than half of the documents its state held before this run
   /// wrote anything, unless <see cref="AllowLargeDeletes"/> is set.
   /// Why the count from before the run: this run's own writes are not what is at risk. Counted
   /// in, a pipeline pointed at a different folder of similar size would see "10 of 22" instead
   /// of "10 of 10" and delete everything it held.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="plan">Documents to delete per sink.</param>
   /// <returns>A plain-English refusal, or null when the deletes may go ahead.</returns>
   private string? CheckDeleteGuard( RunContext context, Dictionary<ISink, List<DocState>> plan )
   {
      if( AllowLargeDeletes || plan.Values.All( g => g.Count == 0 ) )
      {
         return null;
      }

      if( context.RecordsRead == 0 )
      {
         return "Nothing was deleted: the source produced no rows this run, which usually means the folder is empty or not mounted. "
            + "If the data really is gone, reset the pipeline.";
      }

      foreach( KeyValuePair<ISink, List<DocState>> entry in plan )
      {
         int before = context.StoredBefore[entry.Key.Name];
         if( entry.Value.Count * 2L > before )
         {
            return $"Nothing was deleted: this run would remove {entry.Value.Count} of the {before} rows {entry.Key.Name} held before the run started, more than half. "
               + "Check that this is the right folder and that it is complete. If the removal is expected, run again with \"Allow large deletes\" ticked.";
         }
      }

      return null;
   }

   /// <summary>
   /// Deletes documents from one sink in batches. Once the sink has acknowledged a batch, state
   /// is updated even if a cancel arrives, so state and sink never disagree about it.
   /// </summary>
   /// <param name="context">Run context.</param>
   /// <param name="sink">The sink.</param>
   /// <param name="gone">Documents to delete.</param>
   /// <param name="progress">Progress.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task DeleteFromSinkAsync( RunContext context, ISink sink, List<DocState> gone, RunProgress progress, CancellationToken ct )
   {
      Dictionary<string, DocState> states = context.States[sink.Name];
      try
      {
         foreach( DocState[] batch in gone.Chunk( DELETE_BATCH ) )
         {
            await sink.DeleteAsync( context.Pipeline, batch.SelectMany( b => b.ChunkIds ).ToList(), ct );
            await _state.RemoveAsync( context.Pipeline, sink.Name, batch.Select( b => b.DocKey ).ToList(), CancellationToken.None );
            Array.ForEach( batch, b => states.Remove( b.DocKey ) );

            // Count each document once, however many sinks it was removed from.
            progress.Deleted( batch.Count( b => context.DeletedKeys.Add( b.DocKey ) ) );
         }
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         context.Drop( sink );
         progress.SinkFailed( sink.Name, $"delete failed: {ex.Message}" );
      }
   }

   #endregion Private Methods
}
