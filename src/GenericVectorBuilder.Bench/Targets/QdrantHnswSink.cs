using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Qdrant searched through its HNSW graph, the way most people run it, as a benchmark target
/// beside the builder's own Qdrant sink (which searches exactly by design).
/// Why its own collection ("gvb_" + collection + "_hnsw"): both targets live on the same
/// Qdrant server, and sharing one collection would make the second load an overwrite and
/// its timing meaningless.
/// Why two thresholds are lowered: measured on Qdrant 1.17.0 with 524 vectors of 1024
/// dimensions, the server defaults (indexing_threshold_kb 10,000, full_scan_threshold_kb 10,000)
/// build no graph at all below 10 MB of vectors and scan instead, and indexing_threshold_kb 0
/// does not help because 0 DISABLES indexing. indexing_threshold_kb 1 builds the graph, and
/// full_scan_threshold_kb 10 (the minimum the server accepts) makes searches walk it instead of
/// scanning the small segments. At the 760,000-point size both thresholds are far below the
/// segment sizes, so they change nothing there. <see cref="GetIndexStateAsync"/> proves the
/// result from the server's own counters instead of trusting these settings.
/// The graph is Qdrant's default (m=16, ef_construct=100, stated explicitly so a changed
/// server default cannot change the benchmark). The search beam (hnsw_ef) is configurable;
/// unset means the server's default.
/// The payload is the same as the builder's Qdrant sink: doc_key, table, origin, ordinal,
/// text and meta_{key}.
/// </summary>
public sealed class QdrantHnswSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher
{
   #region Data Members

   /// <summary>Optimizer indexing_threshold_kb: a segment above 1 KB gets a graph (0 would disable indexing).</summary>
   public const ulong INDEXING_THRESHOLD_KB = 1;

   /// <summary>HNSW full_scan_threshold_kb: 10 is the smallest value the server accepts.</summary>
   public const ulong FULL_SCAN_THRESHOLD_KB = 10;

   private const int UPSERT_BATCH = 256;
   private const int MAX_ATTEMPTS = 3;
   private const ulong HNSW_M = 16;
   private const ulong HNSW_EF_CONSTRUCT = 100;
   private const string SUFFIX = "_hnsw";

   private readonly QdrantClient _client;
   private readonly QdrantServer _server;
   private readonly ulong? _ef;
   private readonly string _durability;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the target.
   /// </summary>
   /// <param name="client">Qdrant client, with a call deadline; the caller owns it.</param>
   /// <param name="server">Reads the collection's state from the server; the caller owns it.</param>
   /// <param name="ef">Search beam width (hnsw_ef), or null for the server default.</param>
   /// <param name="serverVersion">Qdrant server version, for the report.</param>
   /// <param name="durability">What a crash can lose, read from the server's config (see <see cref="QdrantServer.ReadDurabilityAsync"/>).</param>
   /// <param name="engine">Engine text for the report, or null for the native server's ("Qdrant VERSION (systemd, local)").
   /// The benchmark's own Qdrant container passes its own, which names the container and its CPUs.</param>
   /// <param name="composeFile">Compose file that starts the server, or "always-on" for the native service.</param>
   public QdrantHnswSink( QdrantClient client, QdrantServer server, int? ef, string serverVersion, string durability, string? engine = null, string composeFile = "always-on" )
   {
      _client = client;
      _server = server;
      _ef = ef.HasValue ? (ulong)ef.Value : null;
      _durability = durability;
      Engine = engine ?? $"Qdrant {serverVersion} (systemd, local)";
      ComposeFile = composeFile;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "qdrant-hnsw";

   /// <inheritdoc />
   public string Engine { get; }

   /// <inheritdoc />
   public string IndexDescription => DescribeIndex( _ef.HasValue ? (int)_ef.Value : null );

   /// <inheritdoc />
   public string Durability => _durability;

   /// <inheritdoc />
   public string ComposeFile { get; }

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string name = CollectionName( collection );
      if( await _client.CollectionExistsAsync( name, ct ) )
      {
         CollectionInfo info = await _client.GetCollectionInfoAsync( name, ct );
         ulong size = info.Config.Params.VectorsConfig.Params.Size;
         if( size != (ulong)dimension )
         {
            throw new InvalidOperationException( $"Qdrant collection {name} holds {size}-dim vectors, not {dimension}." );
         }

         return false;
      }

      var hnsw = new HnswConfigDiff { M = HNSW_M, EfConstruct = HNSW_EF_CONSTRUCT, FullScanThreshold = FULL_SCAN_THRESHOLD_KB };
      var optimizers = new OptimizersConfigDiff { IndexingThreshold = INDEXING_THRESHOLD_KB };
      await WithRetryAsync( () => _client.CreateCollectionAsync( name, new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine }, hnswConfig: hnsw, optimizersConfig: optimizers, cancellationToken: ct ), ct );
      return true;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string name = CollectionName( collection );
      foreach( VectorRecord[] batch in records.Chunk( UPSERT_BATCH ) )
      {
         List<PointStruct> points = batch.Select( ToPoint ).ToList();
         await WithRetryAsync( () => _client.UpsertAsync( name, points, wait: true, cancellationToken: ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      foreach( Guid[] batch in chunkIds.Chunk( UPSERT_BATCH ) )
      {
         await WithRetryAsync( () => _client.DeleteAsync( CollectionName( collection ), batch, wait: true, cancellationToken: ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      return (long)await _client.CountAsync( CollectionName( collection ), exact: true, cancellationToken: ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      var search = new SearchParams { Exact = false };
      if( _ef.HasValue )
      {
         search.HnswEf = _ef.Value;
      }

      return SearchWithAsync( collection, vector, top, search, ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return SearchWithAsync( collection, vector, top, new SearchParams { Exact = true }, ct );
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      if( await _client.CollectionExistsAsync( name, ct ) )
      {
         await _client.DeleteCollectionAsync( name, cancellationToken: ct );
      }
   }

   /// <summary>
   /// Waits until every vector sits in an HNSW segment and the collection is green, then checks
   /// the server's own account that searches will walk the graph.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The thresholds used and the indexed and total point counts.</returns>
   /// <exception cref="InvalidOperationException">The collection settled without a usable graph.</exception>
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      string settled = await _server.WaitUntilSettledAsync( Name, name, true, ct );
      IndexState state = await GetIndexStateAsync( collection, ct );
      if( !state.Ready )
      {
         throw new InvalidOperationException( $"Qdrant collection {name} settled but searches will not walk an HNSW graph: {state.Detail}" );
      }

      return $"indexing_threshold_kb {INDEXING_THRESHOLD_KB}, full_scan_threshold_kb {FULL_SCAN_THRESHOLD_KB}; {settled}";
   }

   /// <summary>
   /// Reads the server's own account of the collection: vectors in HNSW segments, each
   /// segment's index type and size against its full-scan threshold, and how many searches so
   /// far walked the graph or scanned instead.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The state and the numbers it rests on.</returns>
   public Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      return _server.StateAsync( CollectionName( collection ), SearchExpectation.WalkGraph, ct );
   }

   /// <summary>
   /// The index text for a search beam, the same whether or not the sink exists yet (a container
   /// target shows it before its container is reached).
   /// </summary>
   /// <param name="ef">Search beam width (hnsw_ef), or null for the server default.</param>
   /// <returns>The description.</returns>
   public static string DescribeIndex( int? ef )
   {
      return $"HNSW m={HNSW_M} ef_construct={HNSW_EF_CONSTRUCT}, hnsw_ef={( ef.HasValue ? ef.Value.ToString() : "server default" )}, cosine; "
         + $"indexing_threshold_kb {INDEXING_THRESHOLD_KB} and full_scan_threshold_kb {FULL_SCAN_THRESHOLD_KB} (server defaults are 10,000 each) so a small collection builds and walks its graph";
   }

   /// <summary>
   /// The Qdrant collection a benchmark collection maps to.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <returns>The Qdrant collection name.</returns>
   public static string CollectionName( string collection )
   {
      return $"gvb_{collection}{SUFFIX}";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Retries a Qdrant call on gRPC transport failures with a short backoff, at most
   /// <see cref="MAX_ATTEMPTS"/> attempts. Why bounded: one dropped connection must not cost a
   /// load that took an hour, and a server that stays down must fail the load with its reason.
   /// </summary>
   /// <param name="call">The call.</param>
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
   /// Runs one search with the given parameters and maps the payload back.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <param name="search">Search parameters.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   private async Task<IReadOnlyList<SearchHit>> SearchWithAsync( string collection, float[] vector, int top, SearchParams search, CancellationToken ct )
   {
      IReadOnlyList<ScoredPoint> points = await _client.SearchAsync( CollectionName( collection ), vector, searchParams: search, limit: (ulong)top,
         payloadSelector: true, cancellationToken: ct );
      return points.Select( p => new SearchHit( Guid.Parse( p.Id.Uuid ), Text( p, "doc_key" ), Text( p, "table" ), Text( p, "text" ), p.Score ) ).ToList();
   }

   /// <summary>
   /// Converts a record to a point with the builder's payload layout.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <returns>The point.</returns>
   private static PointStruct ToPoint( VectorRecord record )
   {
      var point = new PointStruct { Id = record.Chunk.ChunkId, Vectors = record.Vector };
      point.Payload["doc_key"] = record.Document.DocKey;
      point.Payload["table"] = record.Document.Table;
      point.Payload["origin"] = record.Document.Origin;
      point.Payload["ordinal"] = record.Chunk.Ordinal;
      point.Payload["text"] = record.Chunk.Text;
      foreach( KeyValuePair<string, string> pair in record.Document.Metadata )
      {
         point.Payload[$"meta_{pair.Key}"] = pair.Value;
      }

      return point;
   }

   /// <summary>
   /// Reads a string payload field, or empty when missing.
   /// </summary>
   /// <param name="point">Scored point.</param>
   /// <param name="key">Payload key.</param>
   /// <returns>The value.</returns>
   private static string Text( ScoredPoint point, string key )
   {
      return point.Payload.TryGetValue( key, out Value? value ) ? value.StringValue : string.Empty;
   }

   #endregion Private Methods
}
