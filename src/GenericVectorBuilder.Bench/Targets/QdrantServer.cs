using System.Diagnostics;
using System.Text.Json;
using GenericVectorBuilder.Engines.Common;
using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// What the two Qdrant targets need from the Qdrant server: wait until a collection is idle (and
/// for the HNSW target fully indexed), read back from the server which search path searches
/// really took, and describe the server's durability from its real config.
/// Why the search path is read from the server's telemetry and not inferred: measured on
/// Qdrant 1.17.0 with 524 vectors of 1024 dimensions, three things decide whether a default
/// search walks an HNSW graph. (1) With the default indexing_threshold_kb of 10,000 no graph is
/// built at all (indexed_vectors_count 0 of 524), and setting it to 0 does not help, because 0
/// DISABLES indexing (also 0 of 524). (2) At indexing_threshold_kb 1 the graph is built (524 of
/// 524) but a default search still scans, because each segment is smaller than the default
/// full_scan_threshold_kb of 10,000; the server counts those searches as unfiltered_plain.
/// (3) Only with full_scan_threshold_kb at its minimum of 10 do the searches walk the graph
/// (unfiltered_hnsw counted, unfiltered_plain 0). "indexed_vectors_count equals points_count"
/// therefore proves a graph exists, not that searches use it; the per-segment search counters
/// and index types in GET /telemetry are the server's own record of what ran.
/// </summary>
public sealed class QdrantServer : IDisposable
{
   #region Data Members

   /// <summary>Qdrant version the durability text was read against (the source cited is from this tag).</summary>
   public const string SOURCE_VERSION = "1.17.0";

   private const int MAX_ATTEMPTS = 3;
   private const int STALL_POLLS = 30;
   private static readonly TimeSpan HTTP_TIMEOUT = TimeSpan.FromSeconds( 30 );
   private static readonly TimeSpan POLL_EVERY = TimeSpan.FromSeconds( 1 );
   private static readonly TimeSpan LOG_EVERY = TimeSpan.FromSeconds( 15 );
   private static readonly TimeSpan DEFAULT_WAIT = TimeSpan.FromMinutes( 100 );
   private static readonly TimeSpan PROCESS_LOOKUP_LIMIT = TimeSpan.FromSeconds( 10 );

   private readonly QdrantClient _client;
   private readonly HttpClient _http;
   private readonly Action<string> _log;
   private readonly TimeSpan _wait;
   private readonly int _stallPolls;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the helper.
   /// </summary>
   /// <param name="client">gRPC client; the caller owns it and should give it a call deadline.</param>
   /// <param name="host">Server host, for the HTTP telemetry call.</param>
   /// <param name="httpPort">Server HTTP port.</param>
   /// <param name="log">Progress output, or null for the console.</param>
   /// <param name="indexWait">Longest wait for the server to settle, or null for 100 minutes (inside the runner's two-hour backstop).</param>
   /// <param name="stallPolls">Seconds of idle (green) with vectors outside a graph before the wait gives up; 30 unless a test shortens it.</param>
   public QdrantServer( QdrantClient client, string host, int httpPort, Action<string>? log, TimeSpan? indexWait = null, int stallPolls = STALL_POLLS )
   {
      _stallPolls = stallPolls;
      _client = client;
      _http = new HttpClient { BaseAddress = new Uri( $"http://{host}:{httpPort}/" ), Timeout = HTTP_TIMEOUT };
      _log = log ?? ( line => Console.WriteLine( $"{DateTime.UtcNow:HH:mm:ss} {line}" ) );
      _wait = indexWait ?? DEFAULT_WAIT;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Reads a collection's state from the server, retrying a dropped connection a few times.
   /// Each attempt is bounded by the client's gRPC deadline. A cancelled call surfaces as an
   /// <see cref="OperationCanceledException"/>, not as a gRPC status, so callers can tell a stop
   /// request from a server problem.
   /// </summary>
   /// <param name="name">Qdrant collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The collection info.</returns>
   public async Task<CollectionInfo> InfoAsync( string name, CancellationToken ct )
   {
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            return await _client.GetCollectionInfoAsync( name, ct );
         }
         catch( RpcException ex ) when( ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested )
         {
            throw new OperationCanceledException( "The call to Qdrant was cancelled.", ex, ct );
         }
         catch( RpcException ex ) when( attempt < MAX_ATTEMPTS && ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
      }
   }

   /// <summary>
   /// Waits until the collection is idle, and (when asked) until every vector sits in a segment
   /// with an HNSW graph. Idle means status green on two reads a second apart, so no optimiser
   /// is still merging segments or building graphs beside the searches.
   /// Fails with a plain message when the optimiser reports an error, when the collection turns
   /// red, when it sits idle for 30 s with vectors still outside a graph (nothing will move
   /// them), or when the time limit passes.
   /// </summary>
   /// <param name="label">Target name, for progress lines.</param>
   /// <param name="name">Qdrant collection name.</param>
   /// <param name="requireGraph">True when indexed_vectors_count must reach points_count.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A note for the report: status, indexed and total counts, segments, seconds waited.</returns>
   public async Task<string> WaitUntilSettledAsync( string label, string name, bool requireGraph, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      TimeSpan nextLog = LOG_EVERY;
      int stable = 0;
      int idleButIncomplete = 0;
      while( true )
      {
         CollectionInfo info = await ReadForWaitAsync( name, ct );
         ThrowIfBroken( name, info );
         bool green = info.Status == CollectionStatus.Green;
         bool complete = !requireGraph || info.PointsCount == 0 || info.IndexedVectorsCount == info.PointsCount;
         stable = green && complete ? stable + 1 : 0;
         idleButIncomplete = green && !complete ? idleButIncomplete + 1 : 0;
         if( stable >= 2 )
         {
            return $"status {info.Status}, {info.IndexedVectorsCount:N0} of {info.PointsCount:N0} vectors in HNSW segments ({info.SegmentsCount} segments), waited {clock.Elapsed.TotalSeconds:0.0} s";
         }

         ThrowIfStuck( name, info, idleButIncomplete, clock.Elapsed );
         if( clock.Elapsed >= nextLog )
         {
            _log( $"  {label}: waiting for Qdrant, status {info.Status}, {info.IndexedVectorsCount:N0} of {info.PointsCount:N0} vectors indexed, {clock.Elapsed.TotalSeconds:0} s" );
            nextLog += LOG_EVERY;
         }

         await Task.Delay( POLL_EVERY, ct );
      }
   }

   /// <summary>
   /// Reads the per-segment index types and search counters of a collection from the server's
   /// telemetry.
   /// </summary>
   /// <param name="name">Qdrant collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The facts, or null when the collection is not in the telemetry.</returns>
   /// <exception cref="HttpRequestException">The server did not answer within 30 s.</exception>
   /// <exception cref="JsonException">The answer was not the expected JSON.</exception>
   public async Task<CollectionFacts?> ReadFactsAsync( string name, CancellationToken ct )
   {
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( HTTP_TIMEOUT );
      string json = await _http.GetStringAsync( "telemetry?details_level=10", limit.Token );
      return ParseTelemetry( json, name );
   }

   /// <summary>
   /// Builds the index state of a collection from the server's own numbers. Never throws for a
   /// server problem: a collection that is missing, or a server that does not answer, becomes a
   /// not-ready state saying so.
   /// </summary>
   /// <param name="name">Qdrant collection name.</param>
   /// <param name="expectation">Whether searches must walk the HNSW graph or scan every vector.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The state, with the numbers it rests on in <see cref="IndexState.Detail"/>.</returns>
   public async Task<IndexState> StateAsync( string name, SearchExpectation expectation, CancellationToken ct )
   {
      InfoFacts info;
      try
      {
         info = Summarize( await InfoAsync( name, ct ) );
      }
      catch( RpcException ex )
      {
         return new IndexState( false, null, null, ex.StatusCode == StatusCode.NotFound ? $"Qdrant has no collection {name}" : $"Qdrant did not answer for collection {name}: {ex.Status.Detail}" );
      }

      CollectionFacts? facts = null;
      string? problem = null;
      try
      {
         facts = await ReadFactsAsync( name, ct );
         problem = facts == null ? "the collection is missing from the server telemetry" : null;
      }
      catch( Exception ex ) when( ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested )
      {
         problem = $"the server's telemetry could not be read ({ex.Message})";
      }

      return Judge( info, facts, problem, expectation );
   }

   /// <summary>
   /// Reads what the server's telemetry says about one collection: for every segment its index
   /// type, full-scan threshold, sizes, and how many unfiltered searches took each path.
   /// </summary>
   /// <param name="json">The body of GET /telemetry?details_level=10.</param>
   /// <param name="collection">Qdrant collection name.</param>
   /// <returns>The facts, or null when the collection is not listed.</returns>
   public static CollectionFacts? ParseTelemetry( string json, string collection )
   {
      using JsonDocument document = JsonDocument.Parse( json );
      if( !document.RootElement.TryGetProperty( "result", out JsonElement result )
         || !result.TryGetProperty( "collections", out JsonElement outer )
         || !outer.TryGetProperty( "collections", out JsonElement list ) )
      {
         return null;
      }

      foreach( JsonElement entry in list.EnumerateArray().Where( e => e.TryGetProperty( "id", out JsonElement id ) && id.GetString() == collection ) )
      {
         var segments = new List<SegmentFacts>();
         foreach( JsonElement shard in entry.TryGetProperty( "shards", out JsonElement shards ) ? shards.EnumerateArray() : Enumerable.Empty<JsonElement>() )
         {
            JsonElement local = shard.TryGetProperty( "local", out JsonElement l ) ? l : default;
            foreach( JsonElement segment in local.ValueKind == JsonValueKind.Object && local.TryGetProperty( "segments", out JsonElement s ) ? s.EnumerateArray() : Enumerable.Empty<JsonElement>() )
            {
               segments.Add( ReadSegment( segment ) );
            }
         }

         return new CollectionFacts( segments );
      }

      return null;
   }

   /// <summary>
   /// Decides whether a collection's state is what its target promises, from numbers already
   /// read. Pure, so every verdict is tested without a server.
   /// Graph expectation: ready only when every vector is in an HNSW segment, every non-empty
   /// segment is larger than its full-scan threshold (so a search walks the graph), and no
   /// search so far fell back to scanning. Scan expectation: ready only when no search so far
   /// walked an HNSW graph; a graph that exists but is bypassed is fine and is reported.
   /// </summary>
   /// <param name="info">Collection-level numbers.</param>
   /// <param name="facts">Per-segment telemetry, or null when it could not be read.</param>
   /// <param name="problem">Why the telemetry is missing, when it is.</param>
   /// <param name="expectation">What the target promises.</param>
   /// <returns>The verdict and its evidence.</returns>
   public static IndexState Judge( InfoFacts info, CollectionFacts? facts, string? problem, SearchExpectation expectation )
   {
      string head = $"status {info.Status}, optimizer {info.OptimizerError ?? "ok"}, indexed_vectors_count {info.Indexed:N0} of {info.Points:N0} points, {info.Segments} segments, "
         + $"indexing_threshold_kb {info.IndexingThresholdKb?.ToString( "N0" ) ?? "?"}, full_scan_threshold_kb {info.FullScanThresholdKb?.ToString( "N0" ) ?? "?"}";
      bool idle = info.Status == "Green" && info.OptimizerError == null;
      if( facts == null )
      {
         return new IndexState( false, info.Indexed, info.Points, $"{head}; {problem ?? "no telemetry"}, so what searches used is unproven" );
      }

      string searches = $"searches counted since the collection was created: unfiltered_hnsw {facts.Hnsw:N0}, unfiltered_plain {facts.Plain:N0}, unfiltered_exact {facts.Exact:N0}";
      return expectation == SearchExpectation.ScanEveryVector
         ? JudgeScan( head, searches, idle, info, facts )
         : JudgeGraph( head, searches, idle, info, facts );
   }

   /// <summary>
   /// Reads the server's durability settings and describes what a crash can lose.
   /// </summary>
   /// <param name="configPath">Absolute path of the server's config.yaml.</param>
   /// <param name="version">Server version.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The sentence for the report.</returns>
   public static async Task<string> ReadDurabilityAsync( string configPath, string version, CancellationToken ct )
   {
      string? text = ConfigFiles.TryReadFile( configPath );
      ParsedConfig? config = text == null ? null : ConfigFiles.FlattenYaml( text );
      return DescribeDurability( config, configPath, await ReadEnvOverridesAsync( ct ), version );
   }

   /// <summary>
   /// Describes what a crash can lose, from the config file, the server's environment and the
   /// source of the version cited.
   /// </summary>
   /// <param name="config">config.yaml flattened to dotted paths, or null when the file could not be read.</param>
   /// <param name="configPath">Where the config file was looked for.</param>
   /// <param name="envOverrides">Names of QDRANT__ environment variables in the server process (values are never read into the report), or null when they could not be read.</param>
   /// <param name="version">Server version.</param>
   /// <returns>The sentence for the report.</returns>
   public static string DescribeDurability( ParsedConfig? config, string configPath, IReadOnlyList<string>? envOverrides, string version )
   {
      string flush = Setting( config, "storage.optimizers.flush_interval_sec", "5", "s" );
      string walMb = Setting( config, "storage.wal.wal_capacity_mb", "32", "MB" );
      string ahead = Setting( config, "storage.wal.wal_segments_ahead", "0", string.Empty );
      string file = config == null
         ? $"{configPath} could not be read, so every value shown is the server default and may be wrong"
         : $"{configPath} sets: {ConfigFiles.List( config )}{( config.SkippedLines > 0 ? $" ({config.SkippedLines} line(s) the reader did not understand)" : string.Empty )}";
      string env = envOverrides == null ? "the server's QDRANT__ environment could not be read"
         : envOverrides.Count == 0 ? "no QDRANT__ environment overrides in the server process"
         : $"QDRANT__ environment overrides present in the server process (names only): {string.Join( ", ", envOverrides )}";
      string sourceNote = version == SOURCE_VERSION ? string.Empty : $" The source was read at {SOURCE_VERSION} and this server is {version}, so re-check it.";
      return "Every upsert here is sent with wait=true, and Qdrant flushes the open write-ahead-log segment (msync) before it applies a write sent with wait=true, "
         + $"so an acknowledged write survives a process or OS crash by log replay (update_worker.rs and the wal crate's segment.rs, source tag v{SOURCE_VERSION}). "
         + $"Segment files are flushed every {flush}; log segments {walMb}, {ahead} created ahead. {file}; {env}. "
         + $"Not tested by cutting power; whether the disk's own write cache reaches the media was not checked.{sourceNote}";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Verdict for a target that must walk the HNSW graph.
   /// </summary>
   /// <param name="head">Collection-level evidence text.</param>
   /// <param name="searches">Search counter text.</param>
   /// <param name="idle">True when the server is green with no optimiser error.</param>
   /// <param name="info">Collection numbers.</param>
   /// <param name="facts">Segment telemetry.</param>
   /// <returns>The verdict.</returns>
   private static IndexState JudgeGraph( string head, string searches, bool idle, InfoFacts info, CollectionFacts facts )
   {
      var problems = new List<string>();
      SegmentFacts[] occupied = facts.Segments.Where( s => s.Vectors > 0 ).ToArray();
      if( !idle )
      {
         problems.Add( "the collection is not idle" );
      }

      if( info.Points == 0 )
      {
         problems.Add( "the collection holds no points" );
      }

      if( info.Indexed != info.Points )
      {
         problems.Add( "not every vector is in an HNSW segment" );
      }

      int noGraph = occupied.Count( s => s.IndexType != "hnsw" || s.IndexedVectors != s.Vectors );
      int tooSmall = occupied.Count( s => s.IndexType == "hnsw" && !s.WalksGraph );
      if( noGraph > 0 )
      {
         problems.Add( $"{noGraph} of {occupied.Length} segments have no complete graph" );
      }

      if( tooSmall > 0 )
      {
         problems.Add( $"{tooSmall} of {occupied.Length} segments are smaller than full_scan_threshold, so searches scan them" );
      }

      if( facts.Plain > 0 )
      {
         problems.Add( $"{facts.Plain:N0} segment searches scanned instead of walking the graph" );
      }

      string verdict = problems.Count == 0
         ? $"every one of {occupied.Length} non-empty segments has a complete HNSW graph above its full-scan threshold; {( facts.Hnsw > 0 ? $"{facts.Hnsw:N0} segment searches walked the graph and none scanned" : "no search has run yet, so the walk is proven by the segment settings only" )}"
         : $"NOT a graph search: {string.Join( "; ", problems )}";
      return new IndexState( problems.Count == 0, info.Indexed, info.Points, $"{head}; {verdict}; {searches}" );
   }

   /// <summary>
   /// Verdict for a target that must scan every vector (exact search by design).
   /// </summary>
   /// <param name="head">Collection-level evidence text.</param>
   /// <param name="searches">Search counter text.</param>
   /// <param name="idle">True when the server is green with no optimiser error.</param>
   /// <param name="info">Collection numbers.</param>
   /// <param name="facts">Segment telemetry.</param>
   /// <returns>The verdict.</returns>
   private static IndexState JudgeScan( string head, string searches, bool idle, InfoFacts info, CollectionFacts facts )
   {
      string graph = info.Indexed > 0 ? $"a graph exists for {info.Indexed:N0} vectors but exact searches bypass it" : "no graph was built";
      if( facts.Hnsw > 0 )
      {
         return new IndexState( false, info.Indexed, info.Points, $"{head}; {facts.Hnsw:N0} segment searches walked an HNSW graph, so these are not exact scans; {searches}" );
      }

      string notIdle = idle ? string.Empty : $"; NOT ready: the collection is not idle (status {info.Status}, optimizer {info.OptimizerError ?? "ok"})";
      return new IndexState( idle, info.Indexed, info.Points, $"{head}; no index used, exact scan by design (the builder's sink sends exact=true on every search); {graph}; {searches}{notIdle}" );
   }

   /// <summary>
   /// Reads one segment's entry of the telemetry.
   /// </summary>
   /// <param name="segment">The segment's JSON object.</param>
   /// <returns>Its facts.</returns>
   private static SegmentFacts ReadSegment( JsonElement segment )
   {
      JsonElement info = segment.TryGetProperty( "info", out JsonElement i ) ? i : default;
      string indexType = "unknown";
      long? fullScan = null;
      if( segment.TryGetProperty( "config", out JsonElement config ) && config.TryGetProperty( "vector_data", out JsonElement vectorData ) )
      {
         foreach( JsonProperty named in vectorData.EnumerateObject().Take( 1 ) )
         {
            JsonElement index = named.Value.TryGetProperty( "index", out JsonElement x ) ? x : default;
            indexType = index.ValueKind == JsonValueKind.Object && index.TryGetProperty( "type", out JsonElement type ) ? type.GetString() ?? "unknown" : "unknown";
            fullScan = index.ValueKind == JsonValueKind.Object && index.TryGetProperty( "options", out JsonElement options ) && options.TryGetProperty( "full_scan_threshold", out JsonElement f ) ? f.GetInt64() : null;
         }
      }

      long hnsw = 0;
      long plain = 0;
      long exact = 0;
      foreach( JsonElement search in segment.TryGetProperty( "vector_index_searches", out JsonElement searches ) ? searches.EnumerateArray() : Enumerable.Empty<JsonElement>() )
      {
         hnsw += Counter( search, "unfiltered_hnsw" );
         plain += Counter( search, "unfiltered_plain" );
         exact += Counter( search, "unfiltered_exact" );
      }

      return new SegmentFacts( indexType, Number( info, "num_vectors" ), Number( info, "num_indexed_vectors" ), Number( info, "vectors_size_bytes" ), fullScan, hnsw, plain, exact );
   }

   /// <summary>
   /// Reads one search counter object's "count", or 0 when the segment never ran that kind of search.
   /// </summary>
   /// <param name="search">One vector index's search stats.</param>
   /// <param name="key">Counter name.</param>
   /// <returns>The count.</returns>
   private static long Counter( JsonElement search, string key )
   {
      return search.TryGetProperty( key, out JsonElement counter ) && counter.TryGetProperty( "count", out JsonElement count ) ? count.GetInt64() : 0;
   }

   /// <summary>
   /// Reads a whole-number property, or 0 when missing.
   /// </summary>
   /// <param name="parent">The object (may be default when absent).</param>
   /// <param name="key">Property name.</param>
   /// <returns>The value.</returns>
   private static long Number( JsonElement parent, string key )
   {
      return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty( key, out JsonElement value ) ? value.GetInt64() : 0;
   }

   /// <summary>
   /// Pulls the numbers a verdict needs out of a collection-info answer.
   /// </summary>
   /// <param name="info">The gRPC answer.</param>
   /// <returns>The numbers.</returns>
   private static InfoFacts Summarize( CollectionInfo info )
   {
      return new InfoFacts( info.Status.ToString(), info.OptimizerStatus.Ok ? null : info.OptimizerStatus.Error, (long)info.PointsCount, (long)info.IndexedVectorsCount, (int)info.SegmentsCount,
         info.Config.OptimizerConfig.HasIndexingThreshold ? (long)info.Config.OptimizerConfig.IndexingThreshold : null,
         info.Config.HnswConfig.HasFullScanThreshold ? (long)info.Config.HnswConfig.FullScanThreshold : null );
   }

   /// <summary>
   /// Reads the collection for a wait loop, turning "no such collection" into a plain message.
   /// </summary>
   /// <param name="name">Qdrant collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The collection info.</returns>
   /// <exception cref="InvalidOperationException">The collection does not exist.</exception>
   private async Task<CollectionInfo> ReadForWaitAsync( string name, CancellationToken ct )
   {
      try
      {
         return await InfoAsync( name, ct );
      }
      catch( RpcException ex ) when( ex.StatusCode == StatusCode.NotFound )
      {
         throw new InvalidOperationException( $"Qdrant has no collection {name}, so there is nothing to wait for." );
      }
   }

   /// <summary>
   /// Throws when waiting is pointless: the collection is idle with vectors outside a graph
   /// (nothing will index them), or the time limit passed.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="info">Latest info.</param>
   /// <param name="idleButIncomplete">Consecutive idle polls with vectors outside a graph.</param>
   /// <param name="elapsed">Time waited.</param>
   private void ThrowIfStuck( string name, CollectionInfo info, int idleButIncomplete, TimeSpan elapsed )
   {
      if( idleButIncomplete >= _stallPolls )
      {
         throw new InvalidOperationException( $"Qdrant collection {name} has been idle (green) for {_stallPolls} s with only {info.IndexedVectorsCount:N0} of {info.PointsCount:N0} vectors in HNSW segments, so nothing will index the rest. "
            + $"A segment smaller than indexing_threshold_kb ({info.Config.OptimizerConfig.IndexingThreshold:N0}) is never indexed; 1 KB is one vector of 256 dimensions." );
      }

      if( elapsed > _wait )
      {
         throw new TimeoutException( $"Qdrant collection {name} was not ready {Describe( _wait )} after the load: status {info.Status}, {info.IndexedVectorsCount:N0} of {info.PointsCount:N0} vectors indexed." );
      }
   }

   /// <summary>
   /// Throws when the server says the collection is broken, so a wait does not spin on it.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="info">Its info.</param>
   private static void ThrowIfBroken( string name, CollectionInfo info )
   {
      if( !info.OptimizerStatus.Ok )
      {
         throw new InvalidOperationException( $"Qdrant collection {name}: the optimiser reports an error: {info.OptimizerStatus.Error}" );
      }

      if( info.Status == CollectionStatus.Red )
      {
         throw new InvalidOperationException( $"Qdrant collection {name} is red (the server reports it as failed)." );
      }
   }

   /// <summary>
   /// A time limit in words: seconds below two minutes, else minutes.
   /// </summary>
   /// <param name="span">The limit.</param>
   /// <returns>For example "3 s" or "100 minutes".</returns>
   private static string Describe( TimeSpan span )
   {
      return span < TimeSpan.FromMinutes( 2 ) ? $"{span.TotalSeconds:0} s" : $"{span.TotalMinutes:0} minutes";
   }

   /// <summary>
   /// A config value with its unit, marked "(default)" when the file does not set it and the
   /// server default applies.
   /// </summary>
   /// <param name="config">Flattened config, or null.</param>
   /// <param name="key">Dotted key.</param>
   /// <param name="fallback">Server default.</param>
   /// <param name="unit">Unit text such as "s", or empty.</param>
   /// <returns>For example "5 s (default)" or "10 s".</returns>
   private static string Setting( ParsedConfig? config, string key, string fallback, string unit )
   {
      bool set = config != null && config.Values.ContainsKey( key );
      string value = set ? config!.Values[key] : fallback;
      return $"{value}{( unit.Length > 0 ? " " + unit : string.Empty )}{( set ? string.Empty : " (default)" )}";
   }

   /// <summary>
   /// Lists the names of QDRANT__ variables in the running server's environment, never their
   /// values (one of them can be an API key).
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The names, or null when the server process or its environment could not be read.</returns>
   private static async Task<IReadOnlyList<string>?> ReadEnvOverridesAsync( CancellationToken ct )
   {
      try
      {
         ShellResult pid = await Shell.RunAsync( "pgrep", new[] { "-x", "qdrant" }, PROCESS_LOOKUP_LIMIT, ct );
         string first = pid.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries )[0].Trim();
         string environ = await File.ReadAllTextAsync( $"/proc/{first}/environ", ct );
         return environ.Split( '\0', StringSplitOptions.RemoveEmptyEntries )
            .Select( entry => entry.Split( '=', 2 )[0] )
            .Where( name => name.StartsWith( "QDRANT__", StringComparison.Ordinal ) && name != "QDRANT__CONFIG_PATH" )
            .OrderBy( name => name, StringComparer.Ordinal ).ToList();
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException or IndexOutOfRangeException or TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException )
      {
         return null;
      }
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Disposes the HTTP client (the gRPC client belongs to the caller).
   /// </summary>
   public void Dispose()
   {
      _http.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// What a target's searches must do on its collection.
/// </summary>
public enum SearchExpectation
{
   /// <summary>Every search walks the HNSW graph (the "qdrant-hnsw" target).</summary>
   WalkGraph,

   /// <summary>Every search compares every vector, with no index shortcut (the "qdrant" target).</summary>
   ScanEveryVector,
}

/// <summary>
/// Collection-level numbers from a Qdrant collection-info answer.
/// </summary>
/// <param name="Status">Server status text: Green, Yellow, Grey or Red.</param>
/// <param name="OptimizerError">The optimiser's error text, or null when it is ok.</param>
/// <param name="Points">points_count.</param>
/// <param name="Indexed">indexed_vectors_count.</param>
/// <param name="Segments">segments_count.</param>
/// <param name="IndexingThresholdKb">The collection's optimizer indexing_threshold in KB, or null when unset.</param>
/// <param name="FullScanThresholdKb">The collection's HNSW full_scan_threshold in KB, or null when unset.</param>
public sealed record InfoFacts( string Status, string? OptimizerError, long Points, long Indexed, int Segments, long? IndexingThresholdKb, long? FullScanThresholdKb );

/// <summary>
/// One segment of a collection as the server's telemetry describes it.
/// </summary>
/// <param name="IndexType">"hnsw" or "plain".</param>
/// <param name="Vectors">Vectors stored in the segment.</param>
/// <param name="IndexedVectors">Vectors covered by the segment's graph.</param>
/// <param name="VectorBytes">Size of the segment's vectors in bytes.</param>
/// <param name="FullScanThresholdKb">The graph's full-scan threshold in KB, or null for a plain segment.</param>
/// <param name="Hnsw">Unfiltered searches that walked the graph.</param>
/// <param name="Plain">Unfiltered searches that scanned the segment because it has no graph or is below the full-scan threshold.</param>
/// <param name="Exact">Unfiltered searches asked to be exact that skipped a graph the segment has.</param>
public sealed record SegmentFacts( string IndexType, long Vectors, long IndexedVectors, long VectorBytes, long? FullScanThresholdKb, long Hnsw, long Plain, long Exact )
{
   /// <summary>
   /// True when a default search of this segment walks its graph: the segment's vectors are
   /// larger than the full-scan threshold (1 KB is one vector of 256 dimensions).
   /// </summary>
   public bool WalksGraph => IndexType == "hnsw" && FullScanThresholdKb.HasValue && VectorBytes > FullScanThresholdKb.Value * 1024;
}

/// <summary>
/// A collection's segments as the server's telemetry lists them, with the search counters added up.
/// </summary>
/// <param name="Segments">Every segment, including the empty appendable one.</param>
public sealed record CollectionFacts( IReadOnlyList<SegmentFacts> Segments )
{
   /// <summary>Searches that walked an HNSW graph, over every segment.</summary>
   public long Hnsw => Segments.Sum( s => s.Hnsw );

   /// <summary>Searches that scanned a segment without walking a graph.</summary>
   public long Plain => Segments.Sum( s => s.Plain );

   /// <summary>Exact searches on a segment that has a graph they skipped.</summary>
   public long Exact => Segments.Sum( s => s.Exact );
}
