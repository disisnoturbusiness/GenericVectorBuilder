using GenericVectorBuilder.Core.Contracts;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace GenericVectorBuilder.Core.Sinks;

/// <summary>
/// Writes vectors to Qdrant over gRPC: one cosine collection per pipeline, named gvb_{pipeline},
/// with the chunk text, document key, table and origin in each point's payload.
/// Why Qdrant.Client is pinned to 1.17.0: Qdrant only guarantees compatibility within one minor
/// version of the server, and the server on linus7795 is 1.17.0.
/// Why upserts are split into batches of 256 with wait=true: a single huge gRPC call can hit
/// message size limits, and waiting for the write means "acknowledged" really means stored,
/// which the pipeline relies on before it records state.
/// Why searches are exact: measured on the 760,479-point AdventureWorks collection, no HNSW
/// beam width agreed with exact search on the top hit 99% of the time (see
/// <see cref="EXACT_SEARCH"/>).
/// </summary>
public sealed class QdrantSink : ISink
{
   #region Data Members

   private const int UPSERT_BATCH = 256;
   private const int MAX_ATTEMPTS = 3;

   /// <summary>
   /// Searches scan every vector instead of walking the HNSW graph.
   /// Why, measured 2026-10-03 on gvb_adventureworks (760,479 points, 1024 dims, cosine, HNSW
   /// m=16, ef_construct=100, read-only). Stored point vectors were the queries; each search
   /// asked for 11 hits so the query point itself could be set aside; "nearest other" is the
   /// best hit that is not the query point; recall is the top 10 against exact, ties counted.
   /// Server time is Qdrant's own reported time.
   /// 200 queries (the first 200 points by id):
   ///   setting      top-1 incl. self   top-1 nearest other   top-10 recall   ms p50 / p95
   ///   exact        100.0%             100.0%                1.000           112 / 134
   ///   ef unset      95.0%              99.0%                0.984           1.20 / 1.60
   ///   ef 64         94.5%              98.5%                0.979           1.14 / 1.35
   ///   ef 128        95.0%              99.0%                0.984           1.25 / 1.86
   ///   ef 256        95.5%              99.0%                0.984           1.74 / 2.77
   ///   ef 512        95.5%              99.0%                0.985           2.81 / 4.44
   /// 1,000 further queries (the next 1,000 points by id), to check the 200:
   ///   exact        100.0%             100.0%                1.000           122 / 140
   ///   ef unset      93.4%              96.2%                0.959           1.24 / 1.66
   ///   ef 64         93.4%              96.1%                0.958           1.18 / 1.45
   ///   ef 128        93.4%              96.1%                0.960           1.30 / 1.93
   ///   ef 256        93.8%              96.6%                0.964           1.88 / 2.89
   ///   ef 512        94.3%              97.0%                0.968           2.97 / 4.72
   ///   ef 1024       94.6%              97.1%                0.970           4.85 / 7.77
   ///   ef 2048       95.0%              97.5%                0.973           8.27 / 13.13
   /// The bar was the smallest ef with at least 99% top-1 agreement with exact search. No ef
   /// reaches it: ef 128 scraped 99.0% on the first 200 and fell to 96.1% on the next 1,000,
   /// and 16 times the beam (ef 2048) gains only 1.4 points. The misses look like parts of
   /// the graph the search cannot reach (the same wrong neighbours at ef 128 and 512, and 5 to 7%
   /// of points are not returned at all when queried with their own vector), which a wider beam
   /// does not fix. Exact search is the only setting that meets the bar. It costs about 120 ms
   /// a query at this size, growing in step with the point count, while the SQL sink's exact
   /// search on the same data took 470 to 780 ms. It also makes Qdrant and SQL answer the
   /// same question with the same rows. If speed ever matters more than agreement, use
   /// HnswEf = 512 instead (97.0%, about 3 ms).
   /// </summary>
   internal const bool EXACT_SEARCH = true;

   private readonly QdrantClient _client;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="client">Qdrant client. The caller owns its lifetime.</param>
   public QdrantSink( QdrantClient client )
   {
      _client = client;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "qdrant";

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
            throw new InvalidOperationException( $"Qdrant collection {name} holds {size}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
         }

         return false;
      }

      await _client.CreateCollectionAsync( name, new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine }, cancellationToken: ct );
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
      string name = CollectionName( collection );
      foreach( Guid[] batch in chunkIds.Chunk( UPSERT_BATCH ) )
      {
         await WithRetryAsync( () => _client.DeleteAsync( name, batch, wait: true, cancellationToken: ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      ulong count = 0;
      await WithRetryAsync( async () => count = await _client.CountAsync( CollectionName( collection ), exact: true, cancellationToken: ct ), ct );
      return (long)count;
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      var searchParams = new SearchParams { Exact = EXACT_SEARCH };
      IReadOnlyList<ScoredPoint> points = await _client.SearchAsync( CollectionName( collection ), vector, searchParams: searchParams, limit: (ulong)top,
         payloadSelector: true, cancellationToken: ct );
      return points.Select( p => new SearchHit( Guid.Parse( p.Id.Uuid ), Text( p, "doc_key" ), Text( p, "table" ), Text( p, "text" ), p.Score ) ).ToList();
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

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Converts a vector record into a Qdrant point. The chunk id is the point id, so an
   /// upsert of an unchanged chunk overwrites itself.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <returns>The point.</returns>
   private static PointStruct ToPoint( VectorRecord record )
   {
      var point = new PointStruct
      {
         Id = record.Chunk.ChunkId,
         Vectors = record.Vector,
      };

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
   /// Reads a string payload field, or empty when it is missing.
   /// </summary>
   /// <param name="point">Scored point.</param>
   /// <param name="key">Payload key.</param>
   /// <returns>The value.</returns>
   private static string Text( ScoredPoint point, string key )
   {
      return point.Payload.TryGetValue( key, out Value? value ) ? value.StringValue : string.Empty;
   }

   /// <summary>
   /// Retries a Qdrant call on gRPC transport failures with a short backoff.
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
         catch( Grpc.Core.RpcException ex ) when( attempt < MAX_ATTEMPTS && ex.StatusCode is Grpc.Core.StatusCode.Unavailable or Grpc.Core.StatusCode.DeadlineExceeded )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
      }
   }

   /// <summary>
   /// Collection name for a pipeline. Pipeline names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The Qdrant collection name.</returns>
   private static string CollectionName( string collection )
   {
      return $"gvb_{collection}";
   }

   #endregion Private Methods
}
