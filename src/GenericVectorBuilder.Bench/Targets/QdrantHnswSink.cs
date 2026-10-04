using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Qdrant searched through its HNSW graph, the way most people run it, as a benchmark target
/// beside the builder's own Qdrant sink (which searches exactly by design).
/// Why its own collection ("gvb_" + collection + "_hnsw"): both targets live on the same
/// Qdrant server, and sharing one collection would make the second load an overwrite and
/// its timing meaningless.
/// The index is Qdrant's default (m=16, ef_construct=100, stated explicitly so a changed
/// server default cannot change the benchmark). The search beam (hnsw_ef) is configurable;
/// unset means the server's default.
/// The payload is the same as the builder's Qdrant sink: doc_key, table, origin, ordinal,
/// text and meta_{key}.
/// </summary>
public sealed class QdrantHnswSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher
{
   #region Data Members

   private const int UPSERT_BATCH = 256;
   private const ulong HNSW_M = 16;
   private const ulong HNSW_EF_CONSTRUCT = 100;
   private const string SUFFIX = "_hnsw";
   private static readonly TimeSpan INDEX_WAIT = TimeSpan.FromHours( 2 );

   private readonly QdrantClient _client;
   private readonly ulong? _ef;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the target.
   /// </summary>
   /// <param name="client">Qdrant client; the caller owns it.</param>
   /// <param name="ef">Search beam width (hnsw_ef), or null for the server default.</param>
   /// <param name="serverVersion">Qdrant server version, for the report.</param>
   public QdrantHnswSink( QdrantClient client, int? ef, string serverVersion )
   {
      _client = client;
      _ef = ef.HasValue ? (ulong)ef.Value : null;
      Engine = $"Qdrant {serverVersion} (systemd, local)";
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "qdrant-hnsw";

   /// <inheritdoc />
   public string Engine { get; }

   /// <inheritdoc />
   public string IndexDescription => $"HNSW m={HNSW_M} ef_construct={HNSW_EF_CONSTRUCT}, hnsw_ef={( _ef.HasValue ? _ef.Value.ToString() : "server default" )}, cosine";

   /// <inheritdoc />
   public string ComposeFile => "always-on";

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

      var hnsw = new HnswConfigDiff { M = HNSW_M, EfConstruct = HNSW_EF_CONSTRUCT };
      await _client.CreateCollectionAsync( name, new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine }, hnswConfig: hnsw, cancellationToken: ct );
      return true;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string name = CollectionName( collection );
      foreach( VectorRecord[] batch in records.Chunk( UPSERT_BATCH ) )
      {
         await _client.UpsertAsync( name, batch.Select( ToPoint ).ToList(), wait: true, cancellationToken: ct );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      foreach( Guid[] batch in chunkIds.Chunk( UPSERT_BATCH ) )
      {
         await _client.DeleteAsync( CollectionName( collection ), batch, wait: true, cancellationToken: ct );
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
   /// Waits until Qdrant reports the collection green (no optimisation, so the HNSW graph is
   /// built) on two reads a second apart.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Indexed and total point counts.</returns>
   public Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      return WaitForGreenAsync( _client, CollectionName( collection ), ct );
   }

   /// <summary>
   /// Waits until Qdrant reports a collection green on two reads a second apart: no optimiser
   /// is running, so segments are merged and HNSW graphs are built. Also used for the
   /// builder's own Qdrant sink, whose collection builds the same graph in the background
   /// even though its searches are exact; measuring while that build runs would charge its
   /// CPU to the search.
   /// </summary>
   /// <param name="client">Qdrant client.</param>
   /// <param name="name">Qdrant collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Status, indexed and total point counts.</returns>
   public static async Task<string> WaitForGreenAsync( QdrantClient client, string name, CancellationToken ct )
   {
      DateTime deadline = DateTime.UtcNow + INDEX_WAIT;
      int green = 0;
      CollectionInfo info;
      do
      {
         await Task.Delay( TimeSpan.FromSeconds( 1 ), ct );
         info = await client.GetCollectionInfoAsync( name, ct );
         green = info.Status == CollectionStatus.Green ? green + 1 : 0;
      }
      while( green < 2 && DateTime.UtcNow < deadline );

      return $"status {info.Status}, {info.IndexedVectorsCount:N0} of {info.PointsCount:N0} vectors in HNSW segments ({info.SegmentsCount} segments)";
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
