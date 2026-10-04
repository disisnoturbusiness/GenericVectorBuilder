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
   private static readonly TimeSpan FLUSH_INTERVAL = TimeSpan.FromSeconds( 10 );
   private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds( 1 );
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
   /// Flushes the collection, then waits until Milvus reports the HNSW index complete for every
   /// stored vector and the query node serves it (see the class remarks for the four readings).
   /// Throws a plain message when that does not happen within
   /// <see cref="MilvusSinkOptions.IndexWaitMinutes"/>, or at once when the management port
   /// cannot be read, because waiting longer cannot fix that.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine's own evidence, plus how long the wait took. Also starts the search ledger (see <see cref="GetIndexStateAsync"/>).</returns>
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      var clock = Stopwatch.StartNew();
      Readout readout = await WaitUntilReadyAsync( name, TimeSpan.FromMinutes( _options.IndexWaitMinutes ), ct );
      if( !readout.State.Ready )
      {
         throw new InvalidOperationException( $"Milvus did not finish indexing {name} within {_options.IndexWaitMinutes} minutes. Last reading: {readout.State.Detail}" );
      }

      await StartLedgerAsync( name, readout, ct );
      return $"{readout.State.Detail}; flush, index build and load took {clock.Elapsed.TotalSeconds:F1} s; search ledger started, later state reads count the searches against the query node's own counters";
   }

   /// <summary>
   /// Reads the index state once, from Milvus itself, without flushing or waiting. IndexedVectors
   /// is the smaller of what the index description says is indexed and what the query node serves
   /// from loaded indexes; TotalVectors is the Strong count(*). When the management port cannot be
   /// read, Ready is false and Detail says why.
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
      if( readout.Node is null )
      {
         return readout.State;
      }

      MilvusSearchCounters? now = await TryReadCountersAsync( readout.CollectionId, ct );
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

         _ledgers[name] = new MilvusLedgerStart( now, _searchesSent.GetValueOrDefault( name ), readout.Node.Segments );
         return readout.State with { Detail = $"{readout.State.Detail}; search ledger started by this read (FinishLoadAsync was not called on this sink)" };
      }

      MilvusLedgerReading reading = MilvusSearchLedger.Evaluate( start, now, _searchesSent.GetValueOrDefault( name ), readout.Node.Segments, SearchParamsText() );
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
   /// What one reading of the engine found, with the fact the wait loop acts on.
   /// </summary>
   /// <param name="State">The engine's account of its index.</param>
   /// <param name="Unsealed">True when stored rows are still in growing segments, so another flush is needed.</param>
   /// <param name="EvidenceMissing">True when a source of evidence could not be read, which waiting cannot fix.</param>
   /// <param name="Node">What the query node served at this reading, or null when it could not be read.</param>
   /// <param name="CollectionId">Milvus's internal id of the collection, 0 when not read.</param>
   private sealed record Readout( IndexState State, bool Unsealed, bool EvidenceMissing, NodeView? Node = null, long CollectionId = 0 );

   /// <summary>
   /// What the query node serves for one collection.
   /// </summary>
   /// <param name="IndexedRows">Rows in sealed segments whose index is loaded.</param>
   /// <param name="GrowingRows">Rows still served from growing segments (searches scan these).</param>
   /// <param name="SealedWithIndex">Sealed segments with their index loaded.</param>
   /// <param name="SealedWithoutIndex">Sealed segments without a loaded index.</param>
   /// <param name="Segments">Fingerprint of every served segment and its index builds (see <see cref="MilvusSearchLedger.Fingerprint"/>).</param>
   private sealed record NodeView( long IndexedRows, long GrowingRows, int SealedWithIndex, int SealedWithoutIndex, string Segments );

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

            await Task.Delay( POLL_INTERVAL, limit.Token );
         }
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         return last with { State = last.State with { Ready = false } };
      }
   }

   /// <summary>
   /// Reads the four pieces of evidence (see the class remarks), checks that the index is HNSW,
   /// and decides whether searches now use the finished index. Segments without rows are left out
   /// of the segment fingerprint, because an empty growing segment can come and go without
   /// meaning anything.
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
      ( NodeView? node, string nodeDetail ) = await QueryNodeAsync( collectionId, ct );
      string detail = $"index {indexState}, type {indexType} {DescribeIndexParams( index )}, indexedRows {indexedRows} of {sealedRows} sealed rows, pendingRows {pendingRows}; stored rows {stored}; {loadState}; query node: {nodeDetail}";
      if( node is null )
      {
         return new Readout( new IndexState( false, indexedRows, stored, detail ), Unsealed: false, EvidenceMissing: true );
      }

      bool ready = indexState == "Finished" && indexType == "HNSW" && pendingRows == 0 && indexedRows == sealedRows && sealedRows >= stored && loadState == "LoadStateLoaded"
         && node.GrowingRows == 0 && node.SealedWithoutIndex == 0 && node.IndexedRows >= stored;
      bool unsealed = sealedRows < stored || node.GrowingRows > 0;
      return new Readout( new IndexState( ready, Math.Min( indexedRows, node.IndexedRows ), stored, indexType == "HNSW" ? detail : $"{detail}; the index type is {indexType}, not HNSW" ),
         unsealed, EvidenceMissing: false, node, collectionId );
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
   /// counters now and remembers them with the searches this sink has sent and the served segments.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ready">The ready reading the ledger starts from.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">The counters cannot be read, which no wait can fix.</exception>
   private async Task StartLedgerAsync( string name, Readout ready, CancellationToken ct )
   {
      MilvusSearchCounters counters = await TryReadCountersAsync( ready.CollectionId, ct )
         ?? throw new InvalidOperationException( $"Milvus is ready for {name} but its search counters cannot be read from {_options.ManagementUrl}/metrics, so the searches cannot be shown to use the index. Set MilvusSinkOptions.ManagementUrl." );
      _ledgers[name] = new MilvusLedgerStart( counters, _searchesSent.GetValueOrDefault( name ), ready.Node!.Segments );
   }

   /// <summary>
   /// Reads the query node's search counters from the management port's /metrics page.
   /// </summary>
   /// <param name="collectionId">Milvus's internal id of the collection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The counters, or null when the page did not answer.</returns>
   private async Task<MilvusSearchCounters?> TryReadCountersAsync( long collectionId, CancellationToken ct )
   {
      try
      {
         return MilvusSearchLedger.Parse( await GetTextAsync( $"{_options.ManagementUrl.TrimEnd( '/' )}/metrics", ct ), collectionId );
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
   /// <param name="ct">Cancellation.</param>
   /// <returns>What the node serves (null when the port did not answer) and a short description.</returns>
   private async Task<( NodeView? Node, string Detail )> QueryNodeAsync( long collectionId, CancellationToken ct )
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

      return ( new NodeView( indexedRows, growingRows, withIndex, withoutIndex, MilvusSearchLedger.Fingerprint( served ) ),
         $"{withIndex} sealed segment(s) with the index loaded covering {indexedRows} rows, {withoutIndex} sealed without it, {growingRows} rows in growing segments" );
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
