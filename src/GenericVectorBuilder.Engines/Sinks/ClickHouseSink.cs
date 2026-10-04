using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to ClickHouse over its HTTP interface: one MergeTree table per pipeline named
/// gvb.gvb_{pipeline}, ordered by chunk id, with the payload in plain columns (doc_key, table,
/// origin, ordinal, text, meta as a Map) and the vector in an Array(Float32) column.
/// Default search: a vector_similarity skip index (HNSW, cosineDistance, quantization from
/// <see cref="ClickHouseSinkOptions.Quantization"/>, M 16, ef_construction 128) queried with
/// ORDER BY cosineDistance(...) LIMIT n and ClickHouse's own beam width
/// (hnsw_candidate_list_size_for_search, default 256).
/// Why bf16 quantization and no rescoring (bf16 is the documented default; rescoring 0 is what
/// system.settings shows on this server): measured 2026-10-03 on this
/// box, 30,000 clustered vectors of 1024 dimensions (300 centres plus noise), 20 queries,
/// recall@10 against brute force, 6 data parts as loaded and 1 part after OPTIMIZE FINAL:
///   setting             as loaded recall / ms p50    merged recall / ms p50
///   bf16, no rescoring  0.985 / 105                  1.00 / 30
///   f32, no rescoring   0.995 / 55                   1.00 / 15
///   bf16, rescoring     1.00 / 309                   1.00 / 138
/// bf16 gave up nothing measurable, and rescoring cost four to five times the latency.
/// On 100,000 uniform random vectors (the worst case, used by the scale test, loaded as 100
/// inserts of 1,000 rows): as loaded recall 0.61 at p50 173 ms, merged 0.15 at 26 ms, exact
/// scan 554 ms. Many small graphs searched together find more than one large graph at the same
/// beam width, and cost more per query.
/// Exact search: the same query with skip indexes switched off, so every row's distance is
/// computed and sorted.
/// Why RowBinary: it ships the vector as raw float32 bytes. JSON would send each float as text,
/// roughly three times the bytes and a text parse on the server for every dimension.
/// Why each insert is its own HNSW index: ClickHouse builds a vector index per data part, and
/// every INSERT makes a part. Parts are merged in the background (which rebuilds the index), so
/// a freshly loaded table is searched through many small graphs until the merges finish.
/// <see cref="OptimizeAsync"/> forces them into one, for a steady-state measurement.
/// Why upserts check before they write: a MergeTree has no unique key. The sink counts which of
/// a batch's chunk ids already exist, deletes only those rows with a lightweight DELETE, then
/// inserts, so loading new data never pays for deletes and re-sending a batch never duplicates.
/// Rows removed that way stay in their old parts (and old indexes) until a merge, and the
/// vector index may then return fewer than the requested number of hits for a search.
/// Why <see cref="FinishLoadAsync"/> and <see cref="GetIndexStateAsync"/> exist although ClickHouse
/// builds each part's index inside the INSERT: a part that lacks the index (a table whose index was
/// added with ALTER and never materialized, or an old part) is silently scanned, and the plan still
/// lists the index, so the plan alone proves nothing. "Ready" needs three readings from the engine:
/// system.parts (secondary_indices_uncompressed_bytes above 0 on every active part, which means the
/// part has its index files), the live rows of each part (SELECT _part, count() ... GROUP BY _part,
/// which respects deletes) so the rows covered by an index can be counted against the table's
/// count(), and EXPLAIN indexes = 1 of the default search, which must list the Skip index vec_idx.
/// Measured 2026-10-04 on this box: 3 inserts of 700 random 1024-dimension vectors gave 3 parts with
/// 31 KB of compressed index each and USearchSearchCount (system.events) grew by 3 per search, one
/// graph search per part; the same search with skip indexes off left the counter unchanged.
/// </summary>
public sealed class ClickHouseSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher, IDisposable
{
   #region Data Members

   private const int DELETE_BATCH = 1000;
   private const int MAX_ATTEMPTS = 3;
   private const int STEADY_READS = 2;
   private const int MAX_FAILED_READS = 5;
   private const int MAX_MATERIALIZE_REQUESTS = 3;
   private const string INDEX_NAME = "vec_idx";
   private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds( 1 );
   private static readonly TimeSpan PROGRESS_INTERVAL = TimeSpan.FromSeconds( 15 );
   private static readonly TimeSpan CALL_LIMIT = TimeSpan.FromSeconds( 60 );
   private static readonly string[] QUANTIZATIONS = { "f32", "f16", "bf16", "i8", "b1" };
   private static readonly Regex DIMENSION_PATTERN = new( @"vector_similarity\(\s*'hnsw'\s*,\s*'cosineDistance'\s*,\s*(\d+)", RegexOptions.Compiled );

   private readonly ClickHouseSinkOptions _options;
   private readonly HttpClient _http;
   private bool _databaseReady;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a sink for the local ClickHouse container with the password from the secrets file.
   /// </summary>
   public ClickHouseSink() : this( ClickHouseSinkOptions.LocalDefaults() )
   {
   }

   /// <summary>
   /// Creates a sink with explicit settings.
   /// </summary>
   /// <param name="options">Connection and index settings.</param>
   public ClickHouseSink( ClickHouseSinkOptions options )
   {
      if( !QUANTIZATIONS.Contains( options.Quantization ) )
      {
         throw new ArgumentException( $"Quantization must be one of {string.Join( ", ", QUANTIZATIONS )}." );
      }

      _options = options;
      _http = new HttpClient { BaseAddress = new Uri( $"http://{options.Host}:{options.Port}/" ), Timeout = TimeSpan.FromHours( 1 ) };
      _http.DefaultRequestHeaders.Add( "X-ClickHouse-User", options.User );
      if( !string.IsNullOrEmpty( options.Password ) )
      {
         _http.DefaultRequestHeaders.Add( "X-ClickHouse-Key", options.Password );
      }
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "clickhouse";

   /// <inheritdoc />
   public string Engine => "ClickHouse 26.3.39.7 (MergeTree + vector_similarity index)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"vector_similarity HNSW cosineDistance, quantization {_options.Quantization}, M={_options.HnswM} ef_construction={_options.HnswEfConstruction}, "
      + $"hnsw_candidate_list_size_for_search={_options.HnswEfSearch}, rescoring {( _options.Rescoring == 1 ? "on" : "off" )}; exact mode = full scan with skip indexes off";

   /// <inheritdoc />
   public string ComposeFile => "clickhouse.compose.yaml";

   /// <inheritdoc />
   public string Durability =>
      "Acknowledged inserts are not fsynced. The sink creates plain MergeTree tables, so the MergeTree settings are the defaults: fsync_after_insert 0 and fsync_part_directory 0 "
      + "(system.merge_tree_settings), and async_insert 1 with wait_for_async_insert 1 is the server default for the gvb login (system.settings), so an insert is acknowledged once its part is "
      + "written, not once it is on disk. Measured 2026-10-04 with strace over 30 acknowledged single-row inserts (30 Ok rows in system.asynchronous_insert_log): zero fsync, fdatasync or "
      + "sync_file_range calls, while the control, a table created with fsync_after_insert=1, made 61 fdatasync calls for 5 inserts. A host power loss or kernel crash can lose acknowledged "
      + "rows that are still in the OS page cache; a ClickHouse process crash alone should not (inferred, not tested).";

   /// <summary>
   /// Optional receiver for progress lines while <see cref="FinishLoadAsync"/> waits (one line
   /// every 15 seconds). Why: a wait that can last many minutes must show it is moving.
   /// </summary>
   public Action<string>? Progress { get; set; }

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      await EnsureDatabaseAsync( ct );
      int? existing = await ExistingDimensionAsync( collection, ct );
      if( existing.HasValue )
      {
         if( existing.Value != dimension )
         {
            throw new InvalidOperationException( $"ClickHouse table {TableName( collection )} holds {existing.Value}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
         }

         return false;
      }

      await ExecuteAsync( CreateTableSql( collection, dimension ), ct );
      return true;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string table = Table( collection );
      IEnumerable<VectorRecord> latest = records.Reverse().DistinctBy( r => r.Chunk.ChunkId ).Reverse();
      foreach( VectorRecord[] batch in latest.Chunk( _options.UpsertBatch ) )
      {
         await WithRetryAsync( () => WriteBatchAsync( table, batch, ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string table = Table( collection );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         await WithRetryAsync( () => ExecuteAsync( $"DELETE FROM {table} WHERE chunk_id IN ({IdList( batch )})", ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      string count = await QueryAsync( $"SELECT count() FROM {Table( collection )} FORMAT TSVRaw", ct );
      return long.Parse( count, CultureInfo.InvariantCulture );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      string settings = $"hnsw_candidate_list_size_for_search = {_options.HnswEfSearch}, vector_search_with_rescoring = {_options.Rescoring}";
      return RunSearchAsync( collection, vector, top, settings, ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return RunSearchAsync( collection, vector, top, "use_skip_indexes = 0, query_plan_try_use_vector_search = 0", ct );
   }

   /// <inheritdoc />
   public Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      return ExecuteAsync( $"DROP TABLE IF EXISTS {Table( collection )} SYNC", ct );
   }

   /// <summary>
   /// Merges every data part of the table into one, which rebuilds the vector index as a single
   /// graph. Not part of the sink contract; the scale test uses it to time search on a
   /// fully merged table next to the freshly loaded one.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   public Task OptimizeAsync( string collection, CancellationToken ct )
   {
      return ExecuteAsync( $"OPTIMIZE TABLE {Table( collection )} FINAL", ct );
   }

   /// <summary>
   /// Waits until every data part carries the vector index, the index covers every stored row, the
   /// default search's plan lists it, and the set of parts has stopped changing (no merge running
   /// for <see cref="ClickHouseSinkOptions.MergeSettleSeconds"/>). Materializes the index on parts
   /// that lack it. Does not merge parts into one: that is <see cref="OptimizeAsync"/>, a
   /// different steady state, and the returned note says how many parts the search will cross.
   /// Throws a plain message when this does not happen within
   /// <see cref="ClickHouseSinkOptions.IndexWaitSeconds"/>, when a mutation fails, when the index
   /// is still missing after it was materialized, or when readings keep failing.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine's own evidence, plus how long the wait took.</returns>
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      Readout last = await WaitUntilSettledAsync( collection, TimeSpan.FromSeconds( _options.IndexWaitSeconds ), ct );
      return last.State.Ready
         ? $"{last.State.Detail}; wait took {clock.Elapsed.TotalSeconds:F1} s"
         : throw new InvalidOperationException( $"ClickHouse did not finish indexing {TableName( collection )} within {_options.IndexWaitSeconds} seconds. Last reading: {last.State.Detail}" );
   }

   /// <summary>
   /// Reads the index state once from ClickHouse itself, without changing anything. IndexedVectors
   /// is the number of live rows in parts that have the index files, TotalVectors is count().
   /// A merge that is running does not make this false (the parts being merged stay indexed and
   /// searchable); it is reported in Detail. When the table does not exist, Ready is false.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine's account of its index.</returns>
   public async Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      Readout readout = await ReadStateAsync( collection, ct );
      return readout.State;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// What one reading found.
   /// </summary>
   /// <param name="State">The engine's account of its index.</param>
   /// <param name="PartsSignature">The active part names, so a change in the part set shows.</param>
   /// <param name="Merges">Merges running on the table.</param>
   /// <param name="PendingMutations">Mutations (deletes, index materialization) not finished.</param>
   /// <param name="UnindexedParts">Active parts without the vector index files.</param>
   /// <param name="MutationFailure">Why the latest unfinished mutation failed, or null.</param>
   /// <param name="PlanRejectsIndex">True when every part has the index but the plan of the default search does not use it, which waiting cannot fix.</param>
   private sealed record Readout( IndexState State, string PartsSignature, int Merges, int PendingMutations, int UnindexedParts, string? MutationFailure, bool PlanRejectsIndex = false );

   /// <summary>
   /// One active data part: its name and whether it carries index files.
   /// </summary>
   /// <param name="Name">Part name, e.g. all_1_1_0.</param>
   /// <param name="Indexed">True when system.parts shows index bytes for it.</param>
   private sealed record PartInfo( string Name, bool Indexed );

   /// <summary>
   /// Runs the settle poll under a deadline. When the deadline passes, returns the last reading
   /// with Ready false so the caller can say what it saw; the deadline also ends a call that
   /// hangs, so the wait can never outlive it.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="timeout">Longest wait.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The last reading; the caller checks its Ready flag.</returns>
   private async Task<Readout> WaitUntilSettledAsync( string collection, TimeSpan timeout, CancellationToken ct )
   {
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( timeout );
      Readout last = new( new IndexState( false, null, null, "no reading taken yet" ), string.Empty, 0, 0, 0, null );
      try
      {
         return await PollUntilSettledAsync( collection, reading => last = reading, limit.Token );
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         return last with { State = last.State with { Ready = false } };
      }
   }

   /// <summary>
   /// Polls the state until the index is complete and the part set has been unchanged, with no
   /// merge running, for the settle time on <see cref="STEADY_READS"/> reads in a row. Asks for
   /// the index to be materialized when parts lack it.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="seen">Called with every reading, so the caller keeps the last one.</param>
   /// <param name="ct">Cancellation and deadline.</param>
   /// <returns>The last reading, which says ready.</returns>
   private async Task<Readout> PollUntilSettledAsync( string collection, Action<Readout> seen, CancellationToken ct )
   {
      int steady = 0;
      int materializeRequests = 0;
      string signature = string.Empty;
      DateTime start = DateTime.UtcNow;
      DateTime settledSince = start;
      DateTime nextProgress = start + PROGRESS_INTERVAL;
      while( true )
      {
         Readout readout = await ReadWithRetryAsync( collection, ct );
         seen( readout );
         if( readout.MutationFailure is not null )
         {
            throw new InvalidOperationException( $"ClickHouse mutation on {TableName( collection )} failed: {readout.MutationFailure}" );
         }

         if( readout.PlanRejectsIndex )
         {
            throw new InvalidOperationException( $"ClickHouse has the vector index on every part of {TableName( collection )} but the default search does not use it. {readout.State.Detail}" );
         }

         DateTime now = DateTime.UtcNow;
         if( readout.PartsSignature != signature || readout.Merges > 0 || readout.PendingMutations > 0 )
         {
            signature = readout.PartsSignature;
            settledSince = now;
         }

         if( readout.UnindexedParts > 0 && readout.PendingMutations == 0 )
         {
            if( materializeRequests >= MAX_MATERIALIZE_REQUESTS )
            {
               throw new InvalidOperationException( $"ClickHouse still has {readout.UnindexedParts} part(s) of {TableName( collection )} without the vector index after {MAX_MATERIALIZE_REQUESTS} MATERIALIZE INDEX requests. {readout.State.Detail}" );
            }

            await QueryAsync( $"ALTER TABLE {Table( collection )} MATERIALIZE INDEX {INDEX_NAME} SETTINGS mutations_sync = 0", ct, CALL_LIMIT );
            materializeRequests++;
         }

         steady = readout.State.Ready && now - settledSince >= TimeSpan.FromSeconds( _options.MergeSettleSeconds ) ? steady + 1 : 0;
         if( steady >= STEADY_READS )
         {
            return readout;
         }

         if( now >= nextProgress )
         {
            Progress?.Invoke( $"ClickHouse {TableName( collection )}: waiting for the index to settle, {( now - start ).TotalSeconds:F0} s so far. {readout.State.Detail}" );
            nextProgress = now + PROGRESS_INTERVAL;
         }

         await Task.Delay( POLL_INTERVAL, ct );
      }
   }

   /// <summary>
   /// Reads the state, repeating a reading that failed on the network or ran past its limit, up
   /// to <see cref="MAX_FAILED_READS"/> times in a row, then fails with a plain message.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   private async Task<Readout> ReadWithRetryAsync( string collection, CancellationToken ct )
   {
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            return await ReadStateAsync( collection, ct );
         }
         catch( Exception ex ) when( ex is HttpRequestException or TimeoutException )
         {
            if( attempt >= MAX_FAILED_READS )
            {
               throw new InvalidOperationException( $"ClickHouse could not be read {MAX_FAILED_READS} times in a row: {ex.Message}" );
            }

            await Task.Delay( POLL_INTERVAL, ct );
         }
      }
   }

   /// <summary>
   /// Reads the three kinds of evidence (parts and their index files, live rows per part, plan of
   /// the default search) and decides whether searches now use a finished index on every part.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   private async Task<Readout> ReadStateAsync( string collection, CancellationToken ct )
   {
      string name = TableName( collection );
      int? dimension = await ExistingDimensionAsync( collection, ct );
      if( !dimension.HasValue )
      {
         return new Readout( new IndexState( false, null, null, $"ClickHouse has no table {name} in database {_options.Database}" ), string.Empty, 0, 0, 0, null );
      }

      long stored = long.Parse( await QueryAsync( $"SELECT count() FROM {Table( collection )} FORMAT TSVRaw", ct, CALL_LIMIT ), CultureInfo.InvariantCulture );
      List<PartInfo> parts = await ReadPartsAsync( name, ct );
      Dictionary<string, long> live = await ReadLiveRowsAsync( collection, ct );
      ( int merges, int pending, string? failure ) = await ReadWorkAsync( name, ct );
      long indexed = parts.Where( p => p.Indexed ).Sum( p => live.GetValueOrDefault( p.Name ) );
      int unindexed = parts.Count( p => !p.Indexed );
      string plan = stored == 0 ? "table is empty, nothing to search" : await PlanAsync( collection, dimension.Value, ct );
      bool planUsesIndex = stored == 0 || plan.StartsWith( "uses", StringComparison.Ordinal );
      string detail = $"{parts.Count} data part(s), index files on {parts.Count - unindexed} of them, {indexed} of {stored} live rows in indexed parts; "
         + $"{merges} merge(s) running, {pending} unfinished mutation(s); plan of the default search {plan}";
      bool ready = unindexed == 0 && indexed == stored && planUsesIndex;
      return new Readout( new IndexState( ready, indexed, stored, detail ), string.Join( ",", parts.Select( p => p.Name ) ), merges, pending, unindexed, failure,
         PlanRejectsIndex: !planUsesIndex && unindexed == 0 && indexed == stored );
   }

   /// <summary>
   /// Lists the table's active parts and whether each has index files. system.parts reports index
   /// bytes in secondary_indices_uncompressed_bytes, which is above 0 only when the part has the
   /// files of its skip indexes.
   /// </summary>
   /// <param name="name">Table name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The parts, by name.</returns>
   private async Task<List<PartInfo>> ReadPartsAsync( string name, CancellationToken ct )
   {
      string body = await QueryAsync(
         $"SELECT name, secondary_indices_uncompressed_bytes FROM system.parts WHERE database = {Literal( _options.Database )} AND table = {Literal( name )} AND active ORDER BY name FORMAT TSV", ct, CALL_LIMIT );
      var parts = new List<PartInfo>();
      foreach( string line in body.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
      {
         string[] cells = line.Split( '\t' );
         parts.Add( new PartInfo( cells[0], long.Parse( cells[1], CultureInfo.InvariantCulture ) > 0 ) );
      }

      return parts;
   }

   /// <summary>
   /// Counts live rows (deleted rows excluded) per data part through the _part virtual column.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Live rows by part name.</returns>
   private async Task<Dictionary<string, long>> ReadLiveRowsAsync( string collection, CancellationToken ct )
   {
      string body = await QueryAsync( $"SELECT _part, count() FROM {Table( collection )} GROUP BY _part FORMAT TSV", ct, CALL_LIMIT );
      var live = new Dictionary<string, long>();
      foreach( string line in body.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
      {
         string[] cells = line.Split( '\t' );
         live[cells[0]] = long.Parse( cells[1], CultureInfo.InvariantCulture );
      }

      return live;
   }

   /// <summary>
   /// Reads the background work on the table: running merges, unfinished mutations and the
   /// failure reason of an unfinished mutation that has failed.
   /// </summary>
   /// <param name="name">Table name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Merge count, unfinished mutation count, and the failure reason or null.</returns>
   private async Task<( int Merges, int Pending, string? Failure )> ReadWorkAsync( string name, CancellationToken ct )
   {
      string where = $"database = {Literal( _options.Database )} AND table = {Literal( name )}";
      string body = await QueryAsync(
         $"SELECT ( SELECT count() FROM system.merges WHERE {where} ), ( SELECT count() FROM system.mutations WHERE {where} AND NOT is_done ), "
         + $"( SELECT anyIf( latest_fail_reason, latest_fail_reason != '' ) FROM system.mutations WHERE {where} AND NOT is_done ) FORMAT TSV", ct, CALL_LIMIT );
      string[] cells = body.Split( '\t' );
      string failure = cells.Length > 2 ? cells[2].Replace( "\\n", " " ) : string.Empty;
      return ( int.Parse( cells[0], CultureInfo.InvariantCulture ), int.Parse( cells[1], CultureInfo.InvariantCulture ), failure.Length > 0 ? failure : null );
   }

   /// <summary>
   /// Asks ClickHouse how it plans the default search (EXPLAIN indexes = 1) and reports whether the
   /// plan lists the vector index. The query vector is a unit vector, which is only a valid query.
   /// EXPLAIN runs the index analysis, one graph search per part, but reads and returns no rows.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="dimension">Vector length of the table.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>"uses the Skip index vec_idx on a of b parts", or why it does not.</returns>
   private async Task<string> PlanAsync( string collection, int dimension, CancellationToken ct )
   {
      var unit = new float[dimension];
      unit[0] = 1f;
      string settings = $"hnsw_candidate_list_size_for_search = {_options.HnswEfSearch}, vector_search_with_rescoring = {_options.Rescoring}";
      string plan = await QueryAsync( $"EXPLAIN json = 1, indexes = 1 WITH {VectorLiteral( unit )} AS q SELECT chunk_id, cosineDistance( embedding, q ) AS dist "
         + $"FROM {Table( collection )} ORDER BY dist ASC LIMIT 10 SETTINGS {settings} FORMAT TSVRaw", ct, CALL_LIMIT );
      using JsonDocument document = JsonDocument.Parse( plan );
      JsonElement? skip = FindSkipIndex( document.RootElement );
      return skip is null
         ? $"does not list the Skip index {INDEX_NAME}, so the search scans every part"
         : $"uses the Skip index {INDEX_NAME} on {skip.Value.GetProperty( "Selected Parts" ).GetInt32()} of {skip.Value.GetProperty( "Initial Parts" ).GetInt32()} parts";
   }

   /// <summary>
   /// Finds the plan entry of the vector skip index anywhere in an EXPLAIN json tree.
   /// </summary>
   /// <param name="node">A json node of the plan.</param>
   /// <returns>The index entry, or null when the plan has none.</returns>
   private static JsonElement? FindSkipIndex( JsonElement node )
   {
      if( node.ValueKind == JsonValueKind.Object )
      {
         if( node.TryGetProperty( "Type", out JsonElement type ) && type.GetString() == "Skip" && node.TryGetProperty( "Name", out JsonElement name ) && name.GetString() == INDEX_NAME )
         {
            return node;
         }

         foreach( JsonProperty property in node.EnumerateObject() )
         {
            JsonElement? found = FindSkipIndex( property.Value );
            if( found is not null )
            {
               return found;
            }
         }
      }
      else if( node.ValueKind == JsonValueKind.Array )
      {
         foreach( JsonElement child in node.EnumerateArray() )
         {
            JsonElement? found = FindSkipIndex( child );
            if( found is not null )
            {
               return found;
            }
         }
      }

      return null;
   }

   /// <summary>
   /// Reads the vector dimension out of the table's definition.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The dimension (0 when the definition has no vector index), or null when the table does not exist.</returns>
   private async Task<int?> ExistingDimensionAsync( string collection, CancellationToken ct )
   {
      string definition = await QueryAsync(
         $"SELECT create_table_query FROM system.tables WHERE database = {Literal( _options.Database )} AND name = {Literal( TableName( collection ) )} FORMAT TSVRaw", ct );
      if( definition.Length == 0 )
      {
         return null;
      }

      Match match = DIMENSION_PATTERN.Match( definition );
      return match.Success ? int.Parse( match.Groups[1].Value, CultureInfo.InvariantCulture ) : 0;
   }

   /// <summary>
   /// Runs one nearest-neighbour query and converts the rows to hits.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">Hits wanted.</param>
   /// <param name="settings">SETTINGS clause body for this query.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity.</returns>
   private async Task<IReadOnlyList<SearchHit>> RunSearchAsync( string collection, float[] vector, int top, string settings, CancellationToken ct )
   {
      string sql = $"WITH {VectorLiteral( vector )} AS q SELECT toString( chunk_id ) AS id, doc_key, `table` AS tbl, text, cosineDistance( embedding, q ) AS dist "
         + $"FROM {Table( collection )} ORDER BY dist ASC LIMIT {top} SETTINGS {settings} FORMAT JSONEachRow";
      string body = await QueryAsync( sql, ct );
      var hits = new List<SearchHit>( top );
      foreach( string line in body.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
      {
         using JsonDocument row = JsonDocument.Parse( line );
         JsonElement root = row.RootElement;
         hits.Add( new SearchHit( Guid.Parse( root.GetProperty( "id" ).GetString()! ), root.GetProperty( "doc_key" ).GetString()!,
            root.GetProperty( "tbl" ).GetString()!, root.GetProperty( "text" ).GetString()!, 1.0 - root.GetProperty( "dist" ).GetDouble() ) );
      }

      return hits;
   }

   /// <summary>
   /// Writes one batch: delete the rows that already exist, then insert the whole batch.
   /// </summary>
   /// <param name="table">Quoted table name.</param>
   /// <param name="batch">Records to write.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task WriteBatchAsync( string table, VectorRecord[] batch, CancellationToken ct )
   {
      string ids = IdList( batch.Select( r => r.Chunk.ChunkId ) );
      string existing = await QueryAsync( $"SELECT count() FROM {table} WHERE chunk_id IN ({ids}) FORMAT TSVRaw", ct );
      if( existing != "0" )
      {
         await ExecuteAsync( $"DELETE FROM {table} WHERE chunk_id IN ({ids})", ct );
      }

      string insert = $"INSERT INTO {table} ( chunk_id, doc_key, `table`, origin, ordinal, text, meta, embedding ) FORMAT RowBinary";
      using var content = new ByteArrayContent( ToRowBinary( batch ) );
      await SendAsync( "?query=" + Uri.EscapeDataString( insert ), content, ct );
   }

   /// <summary>
   /// Encodes records in ClickHouse's RowBinary format, column order as in the INSERT.
   /// </summary>
   /// <param name="batch">Records.</param>
   /// <returns>The request body.</returns>
   private static byte[] ToRowBinary( VectorRecord[] batch )
   {
      using var stream = new MemoryStream( batch.Length * ( batch[0].Vector.Length * 4 + 512 ) );
      foreach( VectorRecord r in batch )
      {
         WriteUuid( stream, r.Chunk.ChunkId );
         WriteString( stream, r.Document.DocKey );
         WriteString( stream, r.Document.Table );
         WriteString( stream, r.Document.Origin );
         stream.Write( BitConverter.GetBytes( r.Chunk.Ordinal ) );
         WriteString( stream, r.Chunk.Text );
         WriteVarint( stream, (ulong)r.Document.Metadata.Count );
         foreach( KeyValuePair<string, string> pair in r.Document.Metadata )
         {
            WriteString( stream, pair.Key );
            WriteString( stream, pair.Value );
         }

         WriteVarint( stream, (ulong)r.Vector.Length );
         stream.Write( MemoryMarshal.AsBytes( r.Vector.AsSpan() ) );
      }

      return stream.ToArray();
   }

   /// <summary>
   /// Writes a UUID the way RowBinary stores it: the two 8-byte halves of the canonical byte
   /// order, each reversed.
   /// </summary>
   /// <param name="stream">Destination.</param>
   /// <param name="id">The id.</param>
   private static void WriteUuid( MemoryStream stream, Guid id )
   {
      byte[] bytes = id.ToByteArray( bigEndian: true );
      Array.Reverse( bytes, 0, 8 );
      Array.Reverse( bytes, 8, 8 );
      stream.Write( bytes );
   }

   /// <summary>
   /// Writes a RowBinary string: length as a varint, then the UTF-8 bytes.
   /// </summary>
   /// <param name="stream">Destination.</param>
   /// <param name="value">The text.</param>
   private static void WriteString( MemoryStream stream, string value )
   {
      byte[] bytes = Encoding.UTF8.GetBytes( value );
      WriteVarint( stream, (ulong)bytes.Length );
      stream.Write( bytes );
   }

   /// <summary>
   /// Writes an unsigned LEB128 integer.
   /// </summary>
   /// <param name="stream">Destination.</param>
   /// <param name="value">The number.</param>
   private static void WriteVarint( MemoryStream stream, ulong value )
   {
      while( value >= 0x80 )
      {
         stream.WriteByte( (byte)( value | 0x80 ) );
         value >>= 7;
      }

      stream.WriteByte( (byte)value );
   }

   /// <summary>
   /// Builds the CREATE TABLE statement for a pipeline.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="dimension">Vector length.</param>
   /// <returns>The statement.</returns>
   private string CreateTableSql( string collection, int dimension )
   {
      return $@"CREATE TABLE {Table( collection )} (
   chunk_id UUID,
   doc_key String,
   `table` String,
   origin String,
   ordinal Int32,
   text String,
   meta Map(String, String),
   embedding Array(Float32),
   INDEX vec_idx embedding TYPE vector_similarity( 'hnsw', 'cosineDistance', {dimension}, '{_options.Quantization}', {_options.HnswM}, {_options.HnswEfConstruction} )
) ENGINE = MergeTree ORDER BY chunk_id";
   }

   /// <summary>
   /// Creates the database on first use.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   private async Task EnsureDatabaseAsync( CancellationToken ct )
   {
      if( !_databaseReady )
      {
         await ExecuteAsync( $"CREATE DATABASE IF NOT EXISTS {Quote( _options.Database )}", ct );
         _databaseReady = true;
      }
   }

   /// <summary>
   /// Runs a statement and returns its text result, trimmed.
   /// </summary>
   /// <param name="sql">Statement.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="limit">Longest the call may take, or null for the HTTP client's own one-hour limit (loads and merges can run long).</param>
   /// <returns>The response body.</returns>
   private async Task<string> QueryAsync( string sql, CancellationToken ct, TimeSpan? limit = null )
   {
      using var content = new StringContent( sql, Encoding.UTF8, "text/plain" );
      return ( await SendAsync( string.Empty, content, ct, limit ) ).Trim();
   }

   /// <summary>
   /// Runs a statement that returns nothing.
   /// </summary>
   /// <param name="sql">Statement.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task ExecuteAsync( string sql, CancellationToken ct )
   {
      await QueryAsync( sql, ct );
   }

   /// <summary>
   /// Posts a request and returns the body, turning an error response into a plain message
   /// (the first line of ClickHouse's explanation, never the credentials).
   /// </summary>
   /// <param name="query">Query string, with its leading question mark, or empty.</param>
   /// <param name="content">Request body.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="limit">Longest the call may take, or null for the HTTP client's own limit.</param>
   /// <returns>The response body.</returns>
   private async Task<string> SendAsync( string query, HttpContent content, CancellationToken ct, TimeSpan? limit = null )
   {
      using var attempt = CancellationTokenSource.CreateLinkedTokenSource( ct );
      if( limit.HasValue )
      {
         attempt.CancelAfter( limit.Value );
      }

      try
      {
         using HttpResponseMessage response = await _http.PostAsync( query, content, attempt.Token );
         string body = await response.Content.ReadAsStringAsync( attempt.Token );
         if( !response.IsSuccessStatusCode )
         {
            string firstLine = body.Split( '\n', 2 )[0];
            throw new InvalidOperationException( $"ClickHouse refused the request ({(int)response.StatusCode}): {firstLine[..Math.Min( firstLine.Length, 400 )]}" );
         }

         return body;
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested && limit.HasValue )
      {
         throw new TimeoutException( $"ClickHouse did not answer within {limit.Value.TotalSeconds:0} s" );
      }
   }

   /// <summary>
   /// Retries a call on network failures and timeouts with a short backoff.
   /// </summary>
   /// <param name="call">The call. Must be safe to repeat.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task WithRetryAsync( Func<Task> call, CancellationToken ct )
   {
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            await call();
            return;
         }
         catch( Exception ex ) when( attempt < MAX_ATTEMPTS && ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
      }
   }

   /// <summary>
   /// Formats a vector as a ClickHouse array literal, round-trip float formatting with the
   /// invariant culture so no locale can turn 0.5 into "0,5". A whole number gets ".0".
   /// Why: [1,0,0] is an Array(UInt8) to ClickHouse, and measured 2026-10-04 on 26.3.39.7 the
   /// planner then drops the vector index and scans every row, with no error (EXPLAIN lost its
   /// Skip vec_idx entry); [1.0,0.0,0.0] is a float array and uses the index.
   /// </summary>
   /// <param name="vector">The vector.</param>
   /// <returns>The literal, e.g. [0.1,0.2].</returns>
   private static string VectorLiteral( float[] vector )
   {
      var text = new StringBuilder( vector.Length * 12 ).Append( '[' );
      for( int i = 0; i < vector.Length; i++ )
      {
         string number = vector[i].ToString( "R", CultureInfo.InvariantCulture );
         text.Append( i == 0 ? string.Empty : "," ).Append( number ).Append( number.AsSpan().IndexOfAnyExcept( "-0123456789" ) < 0 ? ".0" : string.Empty );
      }

      return text.Append( ']' ).ToString();
   }

   /// <summary>
   /// Formats chunk ids as a comma-separated list of quoted UUIDs.
   /// </summary>
   /// <param name="ids">The ids.</param>
   /// <returns>The list body.</returns>
   private static string IdList( IEnumerable<Guid> ids )
   {
      return string.Join( ",", ids.Select( id => $"'{id:D}'" ) );
   }

   /// <summary>
   /// Quotes a string as a ClickHouse literal.
   /// </summary>
   /// <param name="value">The text.</param>
   /// <returns>The quoted text.</returns>
   private static string Literal( string value )
   {
      return "'" + value.Replace( "\\", "\\\\" ).Replace( "'", "\\'" ) + "'";
   }

   /// <summary>
   /// Backtick-quotes an identifier, escaping any backtick or backslash in it.
   /// </summary>
   /// <param name="identifier">The identifier.</param>
   /// <returns>The quoted identifier.</returns>
   private static string Quote( string identifier )
   {
      return "`" + identifier.Replace( "\\", "\\\\" ).Replace( "`", "\\`" ) + "`";
   }

   /// <summary>
   /// Fully qualified, quoted table name for a pipeline.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>database.table, quoted.</returns>
   private string Table( string collection )
   {
      return $"{Quote( _options.Database )}.{Quote( TableName( collection ) )}";
   }

   /// <summary>
   /// Table name for a pipeline. Pipeline names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The table name.</returns>
   private static string TableName( string collection )
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
