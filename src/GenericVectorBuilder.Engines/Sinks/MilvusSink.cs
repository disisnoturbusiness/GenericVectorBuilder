using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to Milvus over its REST API v2 (/v2/vectordb/...): one collection per
/// pipeline, named gvb_{pipeline}, with the chunk id as a VarChar primary key, a FloatVector
/// field, and the chunk text, document key, table, origin and ordinal as scalar fields.
/// Metadata (meta_ fields) goes to Milvus's dynamic field, so any key is accepted.
/// Index: HNSW with M 16 and efConstruction 128, metric COSINE (Milvus returns the cosine
/// similarity itself, higher is better). Query ef is 100 unless set in the options (see
/// <see cref="MilvusSinkOptions"/> for why not Milvus's own default). Milvus has no per-query
/// exact mode, so this sink does not implement <see cref="IExactSearchSink"/>.
/// Why CountAsync flushes first and asks for Strong consistency: Milvus keeps new rows in growing
/// segments and only a flush makes the stored row count final, and under its default (Bounded)
/// consistency a count can lag five seconds behind writes and deletes. The count itself is a
/// count(*) query, which respects deletes.
/// Why searches use Strong consistency by default: see <see cref="MilvusSinkOptions"/>.
/// Why <see cref="FinishLoadAsync"/> and <see cref="GetIndexStateAsync"/> exist: Milvus builds the
/// HNSW index in the background after a segment is sealed, and searches over a segment without its
/// index scan it instead. Measured 2026-10-04 on this box, 2,000 random 1024-dimension vectors:
/// the index description said Finished 3.7 s after the flush, the query node listed the sealed,
/// indexed segment 2 s after that, and for about 9 s more it still listed the raw growing segment
/// beside it and answered every search from the growing segment (query node counter
/// milvus_querynode_sq_segment_latency_count: 20 of 20 searches Growing, 0 Sealed). Only once the
/// growing segment was gone did the same counter show the searches on the sealed segment. So "Ready"
/// needs four readings from Milvus itself: the index description (state Finished, pendingRows 0,
/// indexedRows equal to every sealed row), the stored row count (a Strong count(*)), the collection
/// load state (LoadStateLoaded), and the query node's own segment list from the management port
/// (/api/v1/_qn/segments), where every segment holding rows must be Sealed with its index is_loaded
/// and no growing segment may hold rows. The state must hold on two reads one second apart.
/// A bare "indexedRows >= totalRows and pendingRows == 0" check is true on an unflushed collection
/// (all three are 0 until the first flush seals a segment), so it proves nothing by itself.
/// Why <see cref="GetIndexStateAsync"/> also keeps a search ledger (see <see cref="MilvusSearchLedger"/>):
/// the readiness above is a reading taken before the timed searches, and does not show where those
/// searches ran. <see cref="FinishLoadAsync"/> starts the ledger (the query node's search counters,
/// the searches this sink has sent, the sealed segments with their index builds) and every later
/// read compares against it, so a read after the timed passes says how many searches ran on sealed
/// segments, that none ran on a growing one, and that the same indexed segments were served the
/// whole time. The index description must also name type HNSW. Every search this sink sends carries
/// searchParams.params.ef (100 unless set), which only an HNSW index reads.
/// Why <see cref="FinishLoadAsync"/> also compacts: measured 2026-10-04, a collection loaded with
/// upserts (which write delete records) was compacted about 80 seconds after the load, in the middle
/// of the timed passes (sealed and indexed 19 s after the load, level-zero compaction at 48 s, mix
/// compaction at 98 s). For about 10 seconds the query node then served the old and the new segment
/// together (2 sealed segments covering 1,048 rows for 524 stored), and a new segment can have no
/// loaded index until its build runs, so searches in that window can scan instead of using HNSW. So
/// after the flush and the index build the sink waits for Milvus to apply the delete records, then
/// triggers a compaction itself, waits for the job, waits for the new segment to be indexed and
/// served, and returns only when the layout has stayed unchanged for
/// <see cref="MilvusSinkOptions.LayoutQuietSeconds"/> (see <see cref="SettleLayoutAsync"/>). "Ready"
/// needs the rows covered by the served indexed segments to EQUAL the rows stored (more is as wrong
/// as fewer), and the ledger flags any segment of the collection compacted away after that.
/// Why errors are read from the body: Milvus answers HTTP 200 and reports failure in a "code"
/// field, so a successful status alone proves nothing.
/// </summary>
public sealed class MilvusSink : ISink, IEngineDescription, IIndexFinisher, IDisposable
{
   #region Data Members

   private const int UPSERT_BATCH = 500;
   private const int DELETE_BATCH = 500;
   private const int MAX_ATTEMPTS = 3;
   private const int ID_CHARS = 36;
   private const int KEY_CHARS = 1024;
   private const int ORIGIN_CHARS = 4096;
   private const int TEXT_BYTES = 65000;
   private const string VECTOR_FIELD = "vector";
   private const string INDEX_NAME = "vector_hnsw";
   private const int NOT_LOADED_CODE = 106;
   private const int RATE_LIMITED_CODE = 1807;
   private const int STEADY_READS = 2;
   private const long NO_COMPACTION_PLAN = -1;
   private static readonly TimeSpan FLUSH_INTERVAL = TimeSpan.FromSeconds( 10 );
   private static readonly TimeSpan PROGRESS_INTERVAL = TimeSpan.FromSeconds( 15 );
   private static readonly TimeSpan CALL_LIMIT = TimeSpan.FromSeconds( 30 );

   private readonly MilvusSinkOptions _options;
   private readonly HttpClient _http;
   private readonly ConcurrentDictionary<string, bool> _unflushed = new( StringComparer.Ordinal );
   private readonly ConcurrentDictionary<string, long> _searchesSent = new( StringComparer.Ordinal );
   private readonly ConcurrentDictionary<string, MilvusLedgerStart> _ledgers = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink with the local container defaults (http://127.0.0.1:19530).
   /// </summary>
   public MilvusSink() : this( new MilvusSinkOptions() )
   {
   }

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="options">Server address and index settings.</param>
   public MilvusSink( MilvusSinkOptions options )
   {
      _options = options;
      _http = new HttpClient { BaseAddress = new Uri( options.BaseUrl.TrimEnd( '/' ) + "/" ), Timeout = TimeSpan.FromMinutes( 5 ) };
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "milvus";

   /// <inheritdoc />
   public string Engine => "Milvus 2.6.25 (standalone, embedded etcd, local storage, REST API v2)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW M={_options.M} efConstruction={_options.EfConstruction}, ef={( _options.Ef.HasValue ? _options.Ef.Value.ToString( CultureInfo.InvariantCulture ) : "Milvus default" )}, metric COSINE, {_options.SearchConsistency} consistency searches; approximate only (no exact mode)";

   /// <inheritdoc />
   public string ComposeFile => "milvus.compose.yaml";

   /// <inheritdoc />
   public string Durability =>
      "Writes are not fsynced. Standalone uses the default message queue rocksmq (RocksDB, /var/lib/milvus/rdb_data, mq.type default) and flushed segments go to local-disk "
      + "object storage (COMMON_STORAGETYPE=local in milvus.compose.yaml). Measured 2026-10-04 with strace over 30 acknowledged single-row upserts and one flush: fdatasync "
      + "ran only on the embedded etcd files (member/wal, member/snap/db), never on rdb_data or the segment files. A host power loss or kernel crash can lose acknowledged "
      + "rows that are still in the OS page cache; a Milvus process crash alone should not (inferred, not tested). Metadata in embedded etcd is fdatasynced on every commit.";

   /// <summary>
   /// Optional receiver for progress lines while <see cref="FinishLoadAsync"/> waits (one line
   /// every 15 seconds). Why: a wait that can last many minutes must show it is moving.
   /// </summary>
   public Action<string>? Progress { get; set; }

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string name = CollectionName( collection );
      if( await HasCollectionAsync( name, ct ) )
      {
         int stored = await StoredDimensionAsync( name, ct );
         if( stored != dimension )
         {
            throw new InvalidOperationException( $"Milvus collection {name} holds {stored}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
         }

         await LoadAsync( name, ct );
         return false;
      }

      await PostAsync( "v2/vectordb/collections/create", BuildCreateBody( name, dimension ), ct );
      await LoadAsync( name, ct );
      return true;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string name = CollectionName( collection );
      foreach( VectorRecord[] batch in records.Chunk( UPSERT_BATCH ) )
      {
         await PostAsync( "v2/vectordb/entities/upsert", BuildUpsertBody( name, batch ), ct );
         _unflushed[name] = true;
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string name = CollectionName( collection );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         string filter = "id in [" + string.Join( ",", batch.Select( g => $"\"{g}\"" ) ) + "]";
         await PostAsync( "v2/vectordb/entities/delete", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name, filter } ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      if( _unflushed.ContainsKey( name ) )
      {
         await FlushAsync( name, ct );
      }

      return await CountRowsAsync( name, ct );
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      string name = CollectionName( collection );
      byte[] body = BuildSearchBody( name, vector, top );
      JsonDocument result;
      try
      {
         result = await PostAsync( "v2/vectordb/entities/search", body, ct );
      }
      catch( MilvusException ex ) when( ex.Code == NOT_LOADED_CODE )
      {
         await LoadAsync( name, ct );
         result = await PostAsync( "v2/vectordb/entities/search", body, ct );
      }

      using( result )
      {
         var hits = new List<SearchHit>();
         foreach( JsonElement row in result.RootElement.GetProperty( "data" ).EnumerateArray() )
         {
            hits.Add( new SearchHit( Guid.Parse( row.GetProperty( "id" ).GetString()! ), Text( row, "doc_key" ), Text( row, "table" ), Text( row, "text" ),
               row.GetProperty( "distance" ).GetDouble() ) );
         }

         _searchesSent.AddOrUpdate( name, 1, ( _, sent ) => sent + 1 );
         return hits;
      }
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      if( await HasCollectionAsync( name, ct ) )
      {
         await PostAsync( "v2/vectordb/collections/drop", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } ), ct );
      }

      _ledgers.TryRemove( name, out _ );
      _searchesSent.TryRemove( name, out _ );
      _unflushed.TryRemove( name, out _ );
   }

   /// <summary>
   /// Flushes the collection, waits until Milvus reports the HNSW index complete for every stored
   /// vector and the query node serves it (see the class remarks for the readings), then compacts
   /// and waits until the segment layout is final (see <see cref="SettleLayoutAsync"/>). Throws a
   /// plain message when either step does not finish within its limit
   /// (<see cref="MilvusSinkOptions.IndexWaitMinutes"/>, <see cref="MilvusSinkOptions.CompactionWaitSeconds"/>),
   /// or at once when the management port cannot be read, because waiting longer cannot fix that.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine's own evidence, plus how long each step took. Also starts the search ledger (see <see cref="GetIndexStateAsync"/>).</returns>
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      var clock = Stopwatch.StartNew();
      Readout readout = await WaitUntilReadyAsync( name, TimeSpan.FromMinutes( _options.IndexWaitMinutes ), ct );
      if( !readout.State.Ready )
      {
         throw new InvalidOperationException( $"Milvus did not finish indexing {name} within {_options.IndexWaitMinutes} minutes. Last reading: {readout.State.Detail}" );
      }

      double indexSeconds = clock.Elapsed.TotalSeconds;
      Settled settled = await SettleLayoutAsync( name, ct );
      await StartLedgerAsync( name, settled.Readout, ct );
      return $"{settled.Readout.State.Detail}; flush, index build and load took {indexSeconds:F1} s, then {settled.Note}; search ledger started, later state reads count the searches against the query node's own counters";
   }

   /// <summary>
   /// Reads the index state once, from Milvus itself, without flushing or waiting. IndexedVectors
   /// is what the query node serves from loaded indexes (the rows covered, which is more than the
   /// rows stored while a compaction hands over from an old segment to a new one); TotalVectors is
   /// the Strong count(*) (the rows stored).
   /// Ready only when covered equals stored (see <see cref="MilvusLayout.Judge"/>). Detail names the
   /// sealed segments the query node serves, the rows they cover against the rows stored, and what
   /// the data coordinator lists. When the management port cannot be read, Ready is false and Detail
   /// says why.
   /// Also reports the search ledger: how many searches this sink has sent since
   /// <see cref="FinishLoadAsync"/> (or, when it was never called on this sink, since the first
   /// ready read), what the query node counted for them, and whether the same indexed segments
   /// were served throughout. A read made after the timed searches therefore proves they used the
   /// index, and it is not Ready when they did not.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine's account of its index.</returns>
   public async Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      Readout readout = await ReadStateAsync( name, ct );
      if( readout.Node is null || readout.Datacoord is null )
      {
         return readout.State;
      }

      MilvusSearchCounters? now = await TryReadCountersAsync( readout, ct );
      if( now is null )
      {
         return readout.State with { Ready = false, Detail = $"{readout.State.Detail}; the query node's search counters could not be read from {_options.ManagementUrl}/metrics" };
      }

      if( !_ledgers.TryGetValue( name, out MilvusLedgerStart? start ) )
      {
         if( !readout.State.Ready )
         {
            return readout.State;
         }

         _ledgers[name] = new MilvusLedgerStart( now, _searchesSent.GetValueOrDefault( name ), readout.Layout );
         return readout.State with { Detail = $"{readout.State.Detail}; search ledger started by this read (FinishLoadAsync was not called on this sink)" };
      }

      MilvusLedgerReading reading = MilvusSearchLedger.Evaluate( start, now, _searchesSent.GetValueOrDefault( name ), readout.Layout, SearchParamsText() );
      return readout.State with { Ready = readout.State.Ready && reading.Problems.Count == 0, Detail = $"{readout.State.Detail}; {reading.Detail}" };
   }

   /// <summary>
   /// Waits until the index is complete and served. Kept for callers that want a yes or no instead
   /// of an exception. Flushes first so that the last rows are sealed and indexed too.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="timeout">How long to wait before giving up.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the index covers every row and the query node serves it, false on timeout.</returns>
   public async Task<bool> WaitForIndexAsync( string collection, TimeSpan timeout, CancellationToken ct )
   {
      Readout readout = await WaitUntilReadyAsync( CollectionName( collection ), timeout, ct );
      return readout.State.Ready;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// How long the wait loops sleep between readings (<see cref="MilvusSinkOptions.PollMilliseconds"/>).
   /// </summary>
   private TimeSpan Poll => TimeSpan.FromMilliseconds( _options.PollMilliseconds );

   /// <summary>
   /// What one reading of the engine found, with the fact the wait loop acts on.
   /// </summary>
   /// <param name="State">The engine's account of its index.</param>
   /// <param name="Unsealed">True when stored rows are still in growing segments, so another flush is needed.</param>
   /// <param name="EvidenceMissing">True when a source of evidence could not be read, which waiting cannot fix.</param>
   /// <param name="Node">What the query node served at this reading, or null when it could not be read.</param>
   /// <param name="CollectionId">Milvus's internal id of the collection, 0 when not read.</param>
   /// <param name="Datacoord">What the data coordinator listed at this reading, or null when it was not read.</param>
   private sealed record Readout( IndexState State, bool Unsealed, bool EvidenceMissing, MilvusNodeView? Node = null, long CollectionId = 0, MilvusDatacoordView? Datacoord = null )
   {
      /// <summary>
      /// One text for the whole layout (what the query node serves and which segments exist), so two
      /// readings can be compared for "nothing changed".
      /// </summary>
      public string Layout => $"query node [{Node?.Segments ?? "unread"}] datacoord [{Datacoord?.Fingerprint ?? "unread"}]";
   }

   /// <summary>
   /// How far the compaction step has got, shared by the loop and its steps.
   /// </summary>
   private sealed class SettleProgress
   {
      /// <summary>The latest reading.</summary>
      public Readout? Last { get; set; }

      /// <summary>What the step is waiting for, for the progress line and the failure message.</summary>
      public string Stage { get; set; } = "starting";

      /// <summary>The layout the last reading showed; a different one restarts the quiet window.</summary>
      public string Seen { get; set; } = string.Empty;

      /// <summary>True once Milvus was asked to compact since the delete-log segments were last live, so each generation of delete records is asked about once.</summary>
      public bool Asked { get; set; }

      /// <summary>When the layout last changed or last stopped being ready.</summary>
      public DateTime StableSince { get; set; } = DateTime.UtcNow;

      /// <summary>Compaction jobs this step triggered that Milvus accepted.</summary>
      public int Jobs { get; set; }

      /// <summary>When the next progress line is due.</summary>
      public DateTime NextProgress { get; set; } = DateTime.UtcNow + PROGRESS_INTERVAL;
   }

   /// <summary>
   /// The outcome of the compaction step.
   /// </summary>
   /// <param name="Readout">The final reading.</param>
   /// <param name="Note">One plain sentence for the report: how many jobs ran and how long the layout then stayed unchanged.</param>
   private sealed record Settled( Readout Readout, string Note );

   /// <summary>
   /// Flushes, then polls until the state is ready on <see cref="STEADY_READS"/> reads in a row or
   /// the deadline passes. Re-flushes every ten seconds while rows are still unsealed (Milvus
   /// allows one flush per collection per ten seconds, and an early flush can be refused). The
   /// deadline also ends a call that hangs, so the wait can never outlive it.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="timeout">Longest wait.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The last reading; the caller checks its State.Ready flag. Throws at once when the evidence cannot be read.</returns>
   private async Task<Readout> WaitUntilReadyAsync( string name, TimeSpan timeout, CancellationToken ct )
   {
      DateTime start = DateTime.UtcNow;
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( timeout );
      Readout last = new( new IndexState( false, null, null, "no reading taken yet" ), Unsealed: false, EvidenceMissing: false );
      try
      {
         await FlushAsync( name, limit.Token );
         DateTime nextFlush = start + FLUSH_INTERVAL;
         DateTime nextProgress = start + PROGRESS_INTERVAL;
         int steady = 0;
         while( true )
         {
            Readout readout = await ReadStateAsync( name, limit.Token );
            last = readout;
            if( readout.EvidenceMissing )
            {
               throw new InvalidOperationException( last.State.Detail );
            }

            steady = last.State.Ready ? steady + 1 : 0;
            if( steady >= STEADY_READS )
            {
               return last;
            }

            DateTime now = DateTime.UtcNow;
            if( now >= nextProgress )
            {
               Progress?.Invoke( $"Milvus {name}: waiting for the index, {( now - start ).TotalSeconds:F0} s so far. {last.State.Detail}" );
               nextProgress = now + PROGRESS_INTERVAL;
            }

            if( readout.Unsealed && now >= nextFlush )
            {
               await FlushAsync( name, limit.Token );
               nextFlush = now + FLUSH_INTERVAL;
            }

            await Task.Delay( Poll, limit.Token );
         }
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         return last with { State = last.State with { Ready = false } };
      }
   }

   /// <summary>
   /// Makes the segment layout final before any timed pass. Milvus compacts by itself after a load
   /// that used upserts, in two steps and in the middle of the timed passes: a level-zero
   /// compaction applies the delete records the upserts wrote (measured 2026-10-04, 48 s after the
   /// load), and a mix compaction then rewrites the segment that received them (58 s to 98 s after
   /// the load, the next 60-second check). So this step waits until no delete-log (L0) segment is
   /// left, then triggers one compaction itself, waits for the job, waits for the new segment to be
   /// indexed and served and the old one released, and returns only when the reading has been ready
   /// with the same layout for <see cref="MilvusSinkOptions.LayoutQuietSeconds"/>.
   /// Why only one request per generation of delete records: measured 2026-10-04, a manual
   /// compaction of a collection with one finished segment and nothing to purge still returns a job
   /// and rewrites that segment under a new id, so asking again after every layout change never ends.
   /// Why the request waits until the reading is ready: the pinned Milvus compacts only segments
   /// whose index build is finished (dataCoord.compaction.indexBasedCompaction), so a request made
   /// while a new segment is still being indexed answers "nothing to compact" and proves nothing.
   /// Throws a plain message naming what it was waiting for when
   /// <see cref="MilvusSinkOptions.CompactionWaitSeconds"/> passes.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The final reading and a sentence for the report.</returns>
   private async Task<Settled> SettleLayoutAsync( string name, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      var progress = new SettleProgress();
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( TimeSpan.FromSeconds( _options.CompactionWaitSeconds ) );
      try
      {
         while( true )
         {
            Readout readout = await ReadStateAsync( name, limit.Token );
            progress.Last = readout;
            if( readout.EvidenceMissing )
            {
               throw new InvalidOperationException( readout.State.Detail );
            }

            if( await SettleStepAsync( name, progress, readout, limit.Token ) )
            {
               return new Settled( readout, $"compaction: {progress.Jobs} job(s) accepted by Milvus, layout ready and unchanged for {_options.LayoutQuietSeconds} s, {clock.Elapsed.TotalSeconds:F1} s for the whole step" );
            }

            await Task.Delay( Poll, limit.Token );
         }
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         throw new InvalidOperationException( $"Milvus did not finish compacting {name} within {_options.CompactionWaitSeconds} s. It was {progress.Stage}. Last reading: {progress.Last?.State.Detail ?? "none"}" );
      }
   }

   /// <summary>
   /// One turn of the compaction step: restarts the quiet window when the layout changed or is not
   /// ready, waits while delete-log segments are still live, asks Milvus once to compact (and waits
   /// for the job), and says whether the layout has now been ready and unchanged long enough.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="progress">The step's state.</param>
   /// <param name="readout">The reading just taken.</param>
   /// <param name="ct">Cancellation, which carries the step's deadline.</param>
   /// <returns>True when the layout is final.</returns>
   private async Task<bool> SettleStepAsync( string name, SettleProgress progress, Readout readout, CancellationToken ct )
   {
      DateTime now = DateTime.UtcNow;
      int deleteLogs = readout.Datacoord?.DeleteLogSegments ?? 0;
      if( readout.Layout != progress.Seen || !readout.State.Ready || deleteLogs > 0 )
      {
         progress.Seen = readout.Layout;
         progress.StableSince = now;
      }

      if( !readout.State.Ready || deleteLogs > 0 )
      {
         progress.Asked = progress.Asked && deleteLogs == 0;
         progress.Stage = readout.State.Ready
            ? $"waiting for Milvus to apply {deleteLogs} delete-log (L0) segment(s) with its own level-zero compaction"
            : "waiting for the layout to be ready again (the index of a new segment is built and served before it counts)";
         ReportProgress( name, progress, readout, now );
         return false;
      }

      if( !progress.Asked )
      {
         progress.Asked = true;
         long job = await StartCompactionAsync( name, ct );
         if( job != NO_COMPACTION_PLAN )
         {
            progress.Jobs++;
            progress.Stage = $"waiting for compaction job {job} to complete";
            await WaitForCompactionAsync( job, ct );
            progress.StableSince = DateTime.UtcNow;
            return false;
         }
      }

      progress.Stage = $"waiting for the ready layout to stay unchanged for {_options.LayoutQuietSeconds} s";
      ReportProgress( name, progress, readout, now );
      return now - progress.StableSince >= TimeSpan.FromSeconds( _options.LayoutQuietSeconds );
   }

   /// <summary>
   /// Sends a progress line when one is due (one every 15 seconds).
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="progress">The step's state.</param>
   /// <param name="readout">The latest reading.</param>
   /// <param name="now">The time of the reading.</param>
   private void ReportProgress( string name, SettleProgress progress, Readout readout, DateTime now )
   {
      if( now >= progress.NextProgress )
      {
         Progress?.Invoke( $"Milvus {name}: {progress.Stage}. {readout.State.Detail}" );
         progress.NextProgress = now + PROGRESS_INTERVAL;
      }
   }

   /// <summary>
   /// Asks Milvus to compact the collection now (POST /v2/vectordb/collections/compact).
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The job id, or <see cref="NO_COMPACTION_PLAN"/> (-1) when Milvus has nothing to compact.</returns>
   private async Task<long> StartCompactionAsync( string name, CancellationToken ct )
   {
      using JsonDocument result = await PostAsync( "v2/vectordb/collections/compact", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } ), ct, CALL_LIMIT );
      return result.RootElement.GetProperty( "data" ).TryGetProperty( "compactionID", out JsonElement id ) ? id.GetInt64() : NO_COMPACTION_PLAN;
   }

   /// <summary>
   /// Polls a compaction job until Milvus says Completed. Throws at once when Milvus reports a
   /// failed or timed-out plan, because waiting longer cannot fix that. Runs until the caller's
   /// deadline otherwise.
   /// </summary>
   /// <param name="jobId">The job id from <see cref="StartCompactionAsync"/>.</param>
   /// <param name="ct">Cancellation, which carries the step's deadline.</param>
   private async Task WaitForCompactionAsync( long jobId, CancellationToken ct )
   {
      byte[] body = JsonSerializer.SerializeToUtf8Bytes( new { jobId } );
      while( true )
      {
         using JsonDocument result = await PostAsync( "v2/vectordb/collections/get_compaction_state", body, ct, CALL_LIMIT );
         JsonElement data = result.RootElement.GetProperty( "data" );
         long failed = Number( data, "failedPlanNumber" ) + Number( data, "timeoutPlanNumber" );
         if( failed > 0 )
         {
            throw new InvalidOperationException( $"Milvus reports {failed} failed or timed-out plan(s) in compaction job {jobId}: {data.GetRawText()}" );
         }

         if( data.TryGetProperty( "state", out JsonElement state ) && state.GetString() == "Completed" )
         {
            return;
         }

         await Task.Delay( Poll, ct );
      }
   }

   /// <summary>
   /// Reads the evidence (see the class remarks): the index description, the stored row count, the
   /// load state, what the query node serves and what the data coordinator lists, checks that the
   /// index is HNSW, and decides with <see cref="MilvusLayout.Judge"/> whether searches now use the
   /// finished index over exactly the stored rows. Segments without rows are left out of the segment
   /// fingerprint, because an empty growing segment can come and go without meaning anything.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   private async Task<Readout> ReadStateAsync( string name, CancellationToken ct )
   {
      long stored = await CountRowsAsync( name, ct );
      using JsonDocument described = await PostAsync( "v2/vectordb/indexes/describe", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name, indexName = INDEX_NAME } ), ct, CALL_LIMIT );
      JsonElement indexes = described.RootElement.GetProperty( "data" );
      if( indexes.GetArrayLength() == 0 )
      {
         return new Readout( new IndexState( false, null, stored, $"Milvus reports no index named {INDEX_NAME} on {name}" ), Unsealed: false, EvidenceMissing: false );
      }

      JsonElement index = indexes[0];
      string indexState = index.TryGetProperty( "indexState", out JsonElement st ) ? st.GetString() ?? "unknown" : "unknown";
      string indexType = index.TryGetProperty( "indexType", out JsonElement it ) ? it.GetString() ?? "unknown" : "unknown";
      long sealedRows = Number( index, "totalRows" );
      long indexedRows = Number( index, "indexedRows" );
      long pendingRows = Number( index, "pendingRows" );
      string loadState = await LoadStateAsync( name, ct );
      long collectionId = await CollectionIdAsync( name, ct );
      ( MilvusNodeView? node, string nodeDetail ) = await QueryNodeAsync( collectionId, stored, ct );
      ( MilvusDatacoordView? datacoord, string datacoordDetail ) = node is null ? ( null, "not read" ) : await DatacoordAsync( collectionId, ct );
      string detail = $"index {indexState}, type {indexType} {DescribeIndexParams( index )}, indexedRows {indexedRows} of {sealedRows} sealed rows, pendingRows {pendingRows}; stored rows {stored}; {loadState}; query node: {nodeDetail}; datacoord: {datacoordDetail}";
      if( node is null || datacoord is null )
      {
         return new Readout( new IndexState( false, indexedRows, stored, node is null ? detail : $"{detail}. Milvus's management port must be reachable; set MilvusSinkOptions.ManagementUrl" ), Unsealed: false, EvidenceMissing: true );
      }

      MilvusVerdict verdict = MilvusLayout.Judge( indexState, indexType, indexedRows, sealedRows, pendingRows, stored, loadState, node, datacoord );
      string text = verdict.Ready ? detail : $"{detail}; not ready: {string.Join( "; ", verdict.Waiting )}";
      return new Readout( new IndexState( verdict.Ready, node.IndexedRows, stored, text ), verdict.Unsealed, EvidenceMissing: false, node, collectionId, datacoord );
   }

   /// <summary>
   /// Names the metric and the build parameters Milvus lists for the index, e.g. "(COSINE, {"M":16,"efConstruction":128})".
   /// </summary>
   /// <param name="index">One entry of the index description.</param>
   /// <returns>The text, or "(parameters not listed)" when Milvus lists none.</returns>
   private static string DescribeIndexParams( JsonElement index )
   {
      string metric = index.TryGetProperty( "metricType", out JsonElement mt ) ? mt.GetString() ?? "?" : "?";
      string build = string.Empty;
      if( index.TryGetProperty( "indexParams", out JsonElement list ) && list.ValueKind == JsonValueKind.Array )
      {
         foreach( JsonElement item in list.EnumerateArray() )
         {
            if( item.TryGetProperty( "key", out JsonElement key ) && key.GetString() == "params" && item.TryGetProperty( "value", out JsonElement value ) )
            {
               build = value.GetString() ?? string.Empty;
            }
         }
      }

      return build.Length == 0 ? $"({metric}, parameters not listed)" : $"({metric}, {build})";
   }

   /// <summary>
   /// Starts the search ledger of a collection from a ready reading: reads the query node's search
   /// counters now and remembers them with the searches this sink has sent, the served segments and
   /// how many segments of the collection were already compacted away.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ready">The ready reading the ledger starts from.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">The counters cannot be read, which no wait can fix.</exception>
   private async Task StartLedgerAsync( string name, Readout ready, CancellationToken ct )
   {
      MilvusSearchCounters counters = await TryReadCountersAsync( ready, ct )
         ?? throw new InvalidOperationException( $"Milvus is ready for {name} but its search counters cannot be read from {_options.ManagementUrl}/metrics, so the searches cannot be shown to use the index. Set MilvusSinkOptions.ManagementUrl." );
      _ledgers[name] = new MilvusLedgerStart( counters, _searchesSent.GetValueOrDefault( name ), ready.Layout );
   }

   /// <summary>
   /// Reads the query node's search counters from the management port's /metrics page and adds the
   /// per-collection count of compacted-away segments from the reading.
   /// </summary>
   /// <param name="reading">A reading that has the collection id and the data coordinator's view.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The counters, or null when the page did not answer.</returns>
   private async Task<MilvusSearchCounters?> TryReadCountersAsync( Readout reading, CancellationToken ct )
   {
      try
      {
         MilvusSearchCounters counters = MilvusSearchLedger.Parse( await GetTextAsync( $"{_options.ManagementUrl.TrimEnd( '/' )}/metrics", ct ), reading.CollectionId );
         return counters with { SegmentsCompactedAway = reading.Datacoord?.CompactedAway ?? 0 };
      }
      catch( Exception ex ) when( ex is HttpRequestException or TimeoutException )
      {
         return null;
      }
   }

   /// <summary>
   /// How this sink's searches name their effort, for the report: the HNSW parameter ef, or the
   /// fact that none is sent.
   /// </summary>
   /// <returns>Text such as "searchParams.params.ef=100, Strong consistency".</returns>
   private string SearchParamsText()
   {
      string ef = _options.Ef.HasValue ? $"searchParams.params.ef={_options.Ef.Value.ToString( CultureInfo.InvariantCulture )}" : "no ef sent, Milvus default";
      return $"{ef}, {_options.SearchConsistency} consistency";
   }

   /// <summary>
   /// Counts stored rows with Strong consistency (the count respects deletes). Does not flush.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The row count.</returns>
   private async Task<long> CountRowsAsync( string name, CancellationToken ct )
   {
      byte[] body = JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name, filter = "", outputFields = new[] { "count(*)" }, consistencyLevel = "Strong" } );
      using JsonDocument result = await PostAsync( "v2/vectordb/entities/query", body, ct, CALL_LIMIT );
      return result.RootElement.GetProperty( "data" )[0].GetProperty( "count(*)" ).GetInt64();
   }

   /// <summary>
   /// Reads the collection's load state, e.g. LoadStateLoaded.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The state name.</returns>
   private async Task<string> LoadStateAsync( string name, CancellationToken ct )
   {
      using JsonDocument state = await PostAsync( "v2/vectordb/collections/get_load_state", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } ), ct, CALL_LIMIT );
      return state.RootElement.GetProperty( "data" ).GetProperty( "loadState" ).GetString() ?? "unknown";
   }

   /// <summary>
   /// Reads Milvus's internal id for a collection, which the management port uses.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The collection id.</returns>
   private async Task<long> CollectionIdAsync( string name, CancellationToken ct )
   {
      using JsonDocument result = await PostAsync( "v2/vectordb/collections/describe", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } ), ct, CALL_LIMIT );
      return result.RootElement.GetProperty( "data" ).GetProperty( "collectionID" ).GetInt64();
   }

   /// <summary>
   /// Asks the query node (through the management port) which segments of the collection it
   /// serves. Rows of growing segments are counted apart from rows of sealed segments whose index
   /// is loaded, because searches scan growing segments and use the index only on sealed ones.
   /// </summary>
   /// <param name="collectionId">Milvus collection id.</param>
   /// <param name="stored">Rows stored, for the "covering X rows against Y stored rows" wording. Why that wording: the consolidated report reads the first "N sealed segment(s) with the index loaded covering R rows" out of this text (SegmentLayout), so that phrase stays intact and the stored rows follow it.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What the node serves (null when the port did not answer) and a short description.</returns>
   private async Task<( MilvusNodeView? Node, string Detail )> QueryNodeAsync( long collectionId, long stored, CancellationToken ct )
   {
      string url = $"{_options.ManagementUrl.TrimEnd( '/' )}/api/v1/_qn/segments?collection_id={collectionId}&in=qn";
      string text;
      try
      {
         text = await GetTextAsync( url, ct );
      }
      catch( Exception ex ) when( ex is HttpRequestException or TimeoutException )
      {
         return ( null, $"unreadable ({url} did not answer: {ex.Message}). Milvus's management port must be reachable; set MilvusSinkOptions.ManagementUrl" );
      }

      long indexedRows = 0;
      long growingRows = 0;
      int withIndex = 0;
      int withoutIndex = 0;
      var served = new List<( string SegmentId, string State, IEnumerable<string> IndexBuilds )>();
      using JsonDocument segments = JsonDocument.Parse( text );
      if( segments.RootElement.ValueKind == JsonValueKind.Array )
      {
         foreach( JsonElement segment in segments.RootElement.EnumerateArray() )
         {
            long rows = long.Parse( segment.GetProperty( "loaded_insert_row_count" ).GetString() ?? "0", CultureInfo.InvariantCulture );
            if( rows > 0 )
            {
               served.Add( ( segment.TryGetProperty( "segment_id", out JsonElement sid ) ? sid.GetString() ?? "?" : "?", segment.GetProperty( "state" ).GetString() ?? "?", LoadedBuilds( segment ) ) );
            }

            if( segment.GetProperty( "state" ).GetString() != "Sealed" )
            {
               growingRows += rows;
            }
            else if( HasLoadedIndex( segment ) )
            {
               withIndex++;
               indexedRows += rows;
            }
            else
            {
               withoutIndex++;
            }
         }
      }

      return ( new MilvusNodeView( indexedRows, growingRows, withIndex, withoutIndex, MilvusSearchLedger.Fingerprint( served ) ),
         $"{withIndex} sealed segment(s) with the index loaded covering {indexedRows} rows against {stored} stored rows, {withoutIndex} sealed without it, {growingRows} rows in growing segments" );
   }

   /// <summary>
   /// Asks the data coordinator (through the management port) which segments of the collection
   /// exist, which are level-zero delete logs, and how many were compacted away.
   /// </summary>
   /// <param name="collectionId">Milvus collection id.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The view (null when the port did not answer) and a short description.</returns>
   private async Task<( MilvusDatacoordView? View, string Detail )> DatacoordAsync( long collectionId, CancellationToken ct )
   {
      string url = $"{_options.ManagementUrl.TrimEnd( '/' )}/api/v1/_dc/segments?collection_id={collectionId}&in=dc";
      string text;
      try
      {
         text = await GetTextAsync( url, ct );
      }
      catch( Exception ex ) when( ex is HttpRequestException or TimeoutException )
      {
         return ( null, $"unreadable ({url} did not answer: {ex.Message})" );
      }

      MilvusDatacoordView view = MilvusLayout.ParseDatacoord( text );
      return ( view, $"{view.LiveSegments} live segment(s), {view.FlushedIndexedSegments} flushed and indexed covering {view.FlushedRows} rows, {view.DeleteLogSegments} delete-log (L0) segment(s) waiting for a compaction, {view.CompactedAway} segment(s) compacted away so far" );
   }

   /// <summary>
   /// True when a query node segment lists at least one index and every listed index is loaded.
   /// </summary>
   /// <param name="segment">One entry of the management port's segment list.</param>
   /// <returns>Whether searches over this segment use its index.</returns>
   private static bool HasLoadedIndex( JsonElement segment )
   {
      if( !segment.TryGetProperty( "index_fields", out JsonElement fields ) || fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() == 0 )
      {
         return false;
      }

      return fields.EnumerateArray().All( f => f.TryGetProperty( "is_loaded", out JsonElement loaded ) && loaded.GetString() == "true" );
   }

   /// <summary>
   /// The build ids of the loaded indexes of a query node segment, so a swapped index shows up as a
   /// different fingerprint.
   /// </summary>
   /// <param name="segment">One entry of the management port's segment list.</param>
   /// <returns>The build ids; empty when the segment lists no index.</returns>
   private static IEnumerable<string> LoadedBuilds( JsonElement segment )
   {
      if( !segment.TryGetProperty( "index_fields", out JsonElement fields ) || fields.ValueKind != JsonValueKind.Array )
      {
         return [];
      }

      return fields.EnumerateArray().Where( f => f.TryGetProperty( "is_loaded", out JsonElement loaded ) && loaded.GetString() == "true" )
         .Select( f => f.TryGetProperty( "build_id", out JsonElement build ) ? build.GetString() ?? "?" : "?" ).ToArray();
   }

   /// <summary>
   /// Reads a number field that Milvus may omit when it is zero.
   /// </summary>
   /// <param name="element">The JSON object.</param>
   /// <param name="key">Field name.</param>
   /// <returns>The value, or 0 when missing.</returns>
   private static long Number( JsonElement element, string key )
   {
      return element.TryGetProperty( key, out JsonElement value ) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
   }

   /// <summary>
   /// GETs a URL with a thirty-second limit per attempt and a short bounded retry on connection
   /// failures and timeouts.
   /// </summary>
   /// <param name="url">Absolute address.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The response body.</returns>
   private async Task<string> GetTextAsync( string url, CancellationToken ct )
   {
      for( int attempt = 1; ; attempt++ )
      {
         using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
         limit.CancelAfter( CALL_LIMIT );
         try
         {
            using HttpResponseMessage response = await _http.GetAsync( url, limit.Token );
            string text = await response.Content.ReadAsStringAsync( limit.Token );
            return response.IsSuccessStatusCode ? text : throw new HttpRequestException( $"HTTP {(int)response.StatusCode}: {Shorten( text )}" );
         }
         catch( Exception ex ) when( ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested )
         {
            if( attempt >= MAX_ATTEMPTS )
            {
               throw ex is OperationCanceledException ? new TimeoutException( $"no answer from {url} within {CALL_LIMIT.TotalSeconds:0} s" ) : ex;
            }

            await Task.Delay( TimeSpan.FromSeconds( attempt ), ct );
         }
      }
   }

   /// <summary>
   /// Builds the collection definition: schema, HNSW index on the vector field, cosine metric.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="dimension">Embedding dimension.</param>
   /// <returns>UTF-8 JSON.</returns>
   private byte[] BuildCreateBody( string name, int dimension )
   {
      return JsonSerializer.SerializeToUtf8Bytes( new Dictionary<string, object>
      {
         ["collectionName"] = name,
         ["schema"] = new Dictionary<string, object>
         {
            ["autoId"] = false,
            ["enableDynamicField"] = true,
            ["fields"] = new object[]
            {
               Field( "id", "VarChar", isPrimary: true, ( "max_length", ID_CHARS ) ),
               Field( VECTOR_FIELD, "FloatVector", isPrimary: false, ( "dim", dimension ) ),
               Field( "doc_key", "VarChar", isPrimary: false, ( "max_length", KEY_CHARS ) ),
               Field( "table", "VarChar", isPrimary: false, ( "max_length", KEY_CHARS ) ),
               Field( "origin", "VarChar", isPrimary: false, ( "max_length", ORIGIN_CHARS ) ),
               Field( "ordinal", "Int32", isPrimary: false ),
               Field( "text", "VarChar", isPrimary: false, ( "max_length", 65535 ) ),
            },
         },
         ["indexParams"] = new object[]
         {
            new Dictionary<string, object>
            {
               ["fieldName"] = VECTOR_FIELD,
               ["indexName"] = INDEX_NAME,
               ["metricType"] = "COSINE",
               ["indexType"] = "HNSW",
               ["params"] = new Dictionary<string, object> { ["M"] = _options.M, ["efConstruction"] = _options.EfConstruction },
            },
         },
      } );
   }

   /// <summary>
   /// One schema field definition.
   /// </summary>
   /// <param name="name">Field name.</param>
   /// <param name="dataType">Milvus data type.</param>
   /// <param name="isPrimary">True for the primary key.</param>
   /// <param name="typeParams">Type parameters such as max_length or dim.</param>
   /// <returns>An object ready for JSON serialization.</returns>
   private static object Field( string name, string dataType, bool isPrimary, params ( string Key, int Value )[] typeParams )
   {
      var field = new Dictionary<string, object> { ["fieldName"] = name, ["dataType"] = dataType };
      if( isPrimary )
      {
         field["isPrimary"] = true;
      }

      if( typeParams.Length > 0 )
      {
         field["elementTypeParams"] = typeParams.ToDictionary( p => p.Key, p => (object)p.Value.ToString( CultureInfo.InvariantCulture ) );
      }

      return field;
   }

   /// <summary>
   /// Builds one upsert body: a row object per record.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="batch">Records for this call.</param>
   /// <returns>UTF-8 JSON.</returns>
   private static byte[] BuildUpsertBody( string name, VectorRecord[] batch )
   {
      using var stream = new MemoryStream();
      using( var writer = new Utf8JsonWriter( stream ) )
      {
         writer.WriteStartObject();
         writer.WriteString( "collectionName", name );
         writer.WriteStartArray( "data" );
         foreach( VectorRecord r in batch )
         {
            writer.WriteStartObject();
            writer.WriteString( "id", r.Chunk.ChunkId.ToString() );
            writer.WriteStartArray( VECTOR_FIELD );
            foreach( float f in r.Vector )
            {
               writer.WriteNumberValue( f );
            }

            writer.WriteEndArray();
            writer.WriteString( "doc_key", Clamp( r.Document.DocKey, KEY_CHARS ) );
            writer.WriteString( "table", Clamp( r.Document.Table, KEY_CHARS ) );
            writer.WriteString( "origin", Clamp( r.Document.Origin, ORIGIN_CHARS ) );
            writer.WriteNumber( "ordinal", r.Chunk.Ordinal );
            writer.WriteString( "text", ClampBytes( r.Chunk.Text, TEXT_BYTES ) );
            foreach( KeyValuePair<string, string> pair in r.Document.Metadata )
            {
               writer.WriteString( $"meta_{pair.Key}", pair.Value );
            }

            writer.WriteEndObject();
         }

         writer.WriteEndArray();
         writer.WriteEndObject();
      }

      return stream.ToArray();
   }

   /// <summary>
   /// Builds one search body: the query vector, the result limit, the fields to return and the
   /// ef (only when one is configured).
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">How many hits.</param>
   /// <returns>UTF-8 JSON.</returns>
   private byte[] BuildSearchBody( string name, float[] vector, int top )
   {
      using var stream = new MemoryStream();
      using( var writer = new Utf8JsonWriter( stream ) )
      {
         writer.WriteStartObject();
         writer.WriteString( "collectionName", name );
         writer.WriteStartArray( "data" );
         writer.WriteStartArray();
         foreach( float f in vector )
         {
            writer.WriteNumberValue( f );
         }

         writer.WriteEndArray();
         writer.WriteEndArray();
         writer.WriteString( "annsField", VECTOR_FIELD );
         writer.WriteString( "consistencyLevel", _options.SearchConsistency );
         writer.WriteNumber( "limit", top );
         writer.WriteStartArray( "outputFields" );
         foreach( string field in new[] { "doc_key", "table", "text" } )
         {
            writer.WriteStringValue( field );
         }

         writer.WriteEndArray();
         if( _options.Ef.HasValue )
         {
            writer.WriteStartObject( "searchParams" );
            writer.WriteStartObject( "params" );
            writer.WriteNumber( "ef", _options.Ef.Value );
            writer.WriteEndObject();
            writer.WriteEndObject();
         }

         writer.WriteEndObject();
      }

      return stream.ToArray();
   }

   /// <summary>
   /// Seals the collection's growing segments so they are final and get indexed. Milvus allows
   /// one flush per collection every ten seconds and answers "rate limit exceeded" to more.
   /// Why a refused flush is tolerated: a Strong count(*) already includes unsealed rows, so a
   /// refused flush costs a count nothing, and the index wait loop asks again every ten seconds
   /// until the rows are sealed.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task FlushAsync( string name, CancellationToken ct )
   {
      byte[] body = JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } );
      try
      {
         using( await PostAsync( "v2/vectordb/collections/flush", body, ct, CALL_LIMIT ) )
         {
         }

         _unflushed.TryRemove( name, out _ );
      }
      catch( MilvusException ex ) when( ex.Code == RATE_LIMITED_CODE )
      {
         // Refused because another flush ran in the last ten seconds; the caller asks again later.
      }
   }

   /// <summary>
   /// Asks Milvus whether a collection exists.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when it exists.</returns>
   private async Task<bool> HasCollectionAsync( string name, CancellationToken ct )
   {
      using JsonDocument result = await PostAsync( "v2/vectordb/collections/has", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } ), ct );
      return result.RootElement.GetProperty( "data" ).GetProperty( "has" ).GetBoolean();
   }

   /// <summary>
   /// Reads the vector dimension out of a collection's schema.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The stored dimension.</returns>
   private async Task<int> StoredDimensionAsync( string name, CancellationToken ct )
   {
      using JsonDocument result = await PostAsync( "v2/vectordb/collections/describe", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } ), ct );
      foreach( JsonElement field in result.RootElement.GetProperty( "data" ).GetProperty( "fields" ).EnumerateArray() )
      {
         if( field.GetProperty( "name" ).GetString() != VECTOR_FIELD || !field.TryGetProperty( "params", out JsonElement typeParams ) )
         {
            continue;
         }

         foreach( JsonElement p in typeParams.EnumerateArray() )
         {
            if( p.GetProperty( "key" ).GetString() == "dim" )
            {
               return int.Parse( p.GetProperty( "value" ).GetString()!, CultureInfo.InvariantCulture );
            }
         }
      }

      throw new InvalidOperationException( $"Milvus collection {name} has no '{VECTOR_FIELD}' field, so it was not created by this tool. Use a new name." );
   }

   /// <summary>
   /// Loads the collection into memory for searching and waits until Milvus says it is loaded.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task LoadAsync( string name, CancellationToken ct )
   {
      byte[] body = JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } );
      using( await PostAsync( "v2/vectordb/collections/load", body, ct ) )
      {
      }

      for( int i = 0; i < 120; i++ )
      {
         using JsonDocument state = await PostAsync( "v2/vectordb/collections/get_load_state", body, ct );
         if( state.RootElement.GetProperty( "data" ).GetProperty( "loadState" ).GetString() == "LoadStateLoaded" )
         {
            return;
         }

         await Task.Delay( 500, ct );
      }

      throw new InvalidOperationException( $"Milvus did not finish loading collection {name} within a minute." );
   }

   /// <summary>
   /// Posts a JSON body and checks Milvus's own result code, with a short retry on connection
   /// failures, timeouts and 5xx answers. Every call this sink makes is safe to repeat (upsert by
   /// id, delete by filter, queries, load).
   /// </summary>
   /// <param name="path">Path below the base address.</param>
   /// <param name="body">UTF-8 JSON body.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="limit">Longest one attempt may take, or null for the HTTP client's own five-minute limit.</param>
   /// <returns>The parsed response; the caller disposes it.</returns>
   private async Task<JsonDocument> PostAsync( string path, byte[] body, CancellationToken ct, TimeSpan? limit = null )
   {
      for( int attempt = 1; ; attempt++ )
      {
         using var attemptLimit = CancellationTokenSource.CreateLinkedTokenSource( ct );
         if( limit.HasValue )
         {
            attemptLimit.CancelAfter( limit.Value );
         }

         try
         {
            using var request = new HttpRequestMessage( HttpMethod.Post, path ) { Content = new ByteArrayContent( body ) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "application/json" );
            using HttpResponseMessage response = await _http.SendAsync( request, attemptLimit.Token );
            string text = await response.Content.ReadAsStringAsync( attemptLimit.Token );
            if( (int)response.StatusCode >= 500 && attempt < MAX_ATTEMPTS )
            {
               await Task.Delay( TimeSpan.FromSeconds( attempt ), ct );
               continue;
            }

            if( !response.IsSuccessStatusCode )
            {
               throw new InvalidOperationException( $"Milvus answered {(int)response.StatusCode} to POST {path}: {Shorten( text )}" );
            }

            JsonDocument doc = JsonDocument.Parse( text );
            int code = doc.RootElement.TryGetProperty( "code", out JsonElement c ) ? c.GetInt32() : 0;
            if( code != 0 )
            {
               string message = doc.RootElement.TryGetProperty( "message", out JsonElement m ) ? m.GetString() ?? string.Empty : string.Empty;
               doc.Dispose();
               throw new MilvusException( code, $"Milvus refused POST {path} (code {code}): {Shorten( message )}" );
            }

            return doc;
         }
         catch( Exception ex ) when( ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested )
         {
            if( attempt >= MAX_ATTEMPTS )
            {
               throw ex is OperationCanceledException ? new TimeoutException( $"Milvus did not answer POST {path} within {limit?.TotalSeconds:0} s" ) : ex;
            }

            await Task.Delay( TimeSpan.FromSeconds( attempt ), ct );
         }
      }
   }

   /// <summary>
   /// Reads a string field from a search row, or empty when it is missing or null.
   /// </summary>
   /// <param name="row">One result row.</param>
   /// <param name="key">Field name.</param>
   /// <returns>The value.</returns>
   private static string Text( JsonElement row, string key )
   {
      return row.TryGetProperty( key, out JsonElement value ) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
   }

   /// <summary>
   /// Shortens a string to at most the given number of characters (a VarChar limit counts
   /// characters). Only a search result's display text is affected.
   /// </summary>
   /// <param name="value">The text.</param>
   /// <param name="maxChars">Column length.</param>
   /// <returns>The text, cut if needed.</returns>
   private static string Clamp( string value, int maxChars )
   {
      return value.Length <= maxChars ? value : value[..maxChars];
   }

   /// <summary>
   /// Shortens a string so its UTF-8 form fits in the given number of bytes, never cutting a
   /// character in half. Why bytes: Milvus measures VarChar length in bytes for the text field.
   /// </summary>
   /// <param name="value">The text.</param>
   /// <param name="maxBytes">Column length in bytes.</param>
   /// <returns>The text, cut if needed.</returns>
   private static string ClampBytes( string value, int maxBytes )
   {
      if( Encoding.UTF8.GetByteCount( value ) <= maxBytes )
      {
         return value;
      }

      int length = Math.Min( value.Length, maxBytes / 3 );
      while( length < value.Length && Encoding.UTF8.GetByteCount( value.AsSpan( 0, length + 1 ) ) <= maxBytes )
      {
         length++;
      }

      return char.IsHighSurrogate( value[length - 1] ) ? value[..( length - 1 )] : value[..length];
   }

   /// <summary>
   /// Cuts a server error message down so an exception stays readable.
   /// </summary>
   /// <param name="text">Response text.</param>
   /// <returns>At most 300 characters.</returns>
   private static string Shorten( string text )
   {
      return text.Length <= 300 ? text : text[..300] + "...";
   }

   /// <summary>
   /// Collection name for a pipeline. Pipeline names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The Milvus collection name.</returns>
   private static string CollectionName( string collection )
   {
      return $"gvb_{collection}";
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Releases the HTTP client.
   /// </summary>
   public void Dispose()
   {
      _http.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// A failure Milvus reported in the body of an HTTP 200 answer.
/// Why a type: the sink reacts to one specific code (collection not loaded) and rethrows the rest.
/// </summary>
public sealed class MilvusException : InvalidOperationException
{
   /// <summary>
   /// Creates the exception.
   /// </summary>
   /// <param name="code">Milvus result code.</param>
   /// <param name="message">Plain-English message.</param>
   public MilvusException( int code, string message ) : base( message )
   {
      Code = code;
   }

   /// <summary>
   /// Milvus result code, e.g. 106 for "collection not loaded".
   /// </summary>
   public int Code { get; }
}
