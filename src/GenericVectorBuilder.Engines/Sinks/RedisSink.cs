using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.DataTypes;
using NRedisStack.Search.Literals.Enums;
using StackExchange.Redis;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to Redis 8 and searches them with its built-in query engine: one hash per
/// chunk under the key gvb_{pipeline}:{chunkId}, and one vector index per pipeline named
/// gvb_{pipeline} that covers that key prefix.
/// Default search: an HNSW index (TYPE FLOAT32, DISTANCE_METRIC COSINE, M 16, EF_CONSTRUCTION
/// 128) queried with KNN and EF_RUNTIME from <see cref="RedisSinkOptions.HnswEfRuntime"/>.
/// Why EF_RUNTIME is 100: Redis defaults it to 10, equal to the 10 hits a typical search asks
/// for. Measured 2026-10-03 on this box, 100,000 vectors of 1024 dimensions, M 16,
/// EF_CONSTRUCTION 128, 20 queries, recall@10 against an in-memory brute force.
/// Clustered vectors (500 centres plus noise):
///   EF_RUNTIME   recall@10   ms p50
///   10           0.82        2.1
///   40           1.00        1.7
///   100          1.00        2.8
///   400          1.00        3.8
///   1600         1.00        14.8
/// Uniform random vectors, which give a graph nothing to exploit:
///   100          0.06        5.0
///   400          0.21        9.0
///   1000         0.46        29.0
///   3000         0.76        63.9
/// Random data is the worst case. The contract and scale tests use it, so their recall numbers
/// are a floor, not a forecast for real embeddings.
/// Exact search: a second index named gvb_{pipeline}:flat, FLAT algorithm, over the same
/// hashes. It is created on the first exact query, not at load time, so the load timing
/// measures the HNSW path alone; once it exists Redis keeps it current on every write.
/// Why HASH and not JSON: a hash holds the vector as a raw float32 blob (4 bytes a dimension),
/// where JSON would store each float as text.
/// Why the server TIMEOUT is passed on every query: Redis cuts a query off after 500 ms by
/// default and, with its default on-timeout policy, returns whatever it had found so far
/// without an error. An exact scan over a large set can pass that, and a silently partial
/// answer is worse than a failure.
/// Why upserts are plain HSET: the same chunk id always carries the same field names, so HSET
/// overwrites every value. The pipeline never shrinks a chunk's metadata field set.
/// </summary>
public sealed class RedisSink : ISink, IExactSearchSink, IEngineDescription, IDisposable
{
   #region Data Members

   private const string VECTOR_FIELD = "embedding";
   private const string DISTANCE_ALIAS = "gvb_dist";
   private const int DELETE_BATCH = 1000;
   private const int MAX_ATTEMPTS = 3;
   private const int SEARCH_TIMEOUT_MS = 30000;
   private const int EXACT_TIMEOUT_MS = 300000;
   private const int INDEX_WAIT_SECONDS = 600;
   private static readonly string[] MISSING_INDEX_WORDINGS = { "index not found", "no such index", "unknown index" };

   private readonly RedisSinkOptions _options;
   private readonly Lazy<ConnectionMultiplexer> _connection;
   private readonly ConcurrentDictionary<string, bool> _exactReady = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a sink for the local Redis container with the password from the secrets file.
   /// </summary>
   public RedisSink() : this( RedisSinkOptions.LocalDefaults() )
   {
   }

   /// <summary>
   /// Creates a sink with explicit settings.
   /// </summary>
   /// <param name="options">Connection and index settings.</param>
   public RedisSink( RedisSinkOptions options )
   {
      _options = options;
      _connection = new Lazy<ConnectionMultiplexer>( Connect );
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "redis";

   /// <inheritdoc />
   public string Engine => "Redis 8.10.2 (query engine, HASH + vector index)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW TYPE FLOAT32 M={_options.HnswM} EF_CONSTRUCTION={_options.HnswEfConstruction}, EF_RUNTIME={_options.HnswEfRuntime} per query, cosine; exact mode = FLAT index built on first exact query";

   /// <inheritdoc />
   public string ComposeFile => "redis.compose.yaml";

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      SearchCommands ft = Database().FT();
      string index = IndexName( collection );
      int? existing = await ExistingDimensionAsync( ft, index );
      if( existing.HasValue )
      {
         if( existing.Value != dimension )
         {
            throw new InvalidOperationException( $"Redis index {index} holds {existing}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
         }

         return false;
      }

      Schema schema = new Schema().AddVectorField( VECTOR_FIELD, Schema.VectorField.VectorAlgo.HNSW, new Dictionary<string, object>
      {
         ["TYPE"] = "FLOAT32",
         ["DIM"] = dimension,
         ["DISTANCE_METRIC"] = "COSINE",
         ["M"] = _options.HnswM,
         ["EF_CONSTRUCTION"] = _options.HnswEfConstruction,
      } );
      await ft.CreateAsync( index, new FTCreateParams().On( IndexDataType.HASH ).Prefix( KeyPrefix( collection ) ), schema );
      return true;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      IDatabase db = Database();
      string prefix = KeyPrefix( collection );
      foreach( VectorRecord[] batch in records.Chunk( _options.UpsertBatch ) )
      {
         await WithRetryAsync( async () =>
         {
            IBatch pipeline = db.CreateBatch();
            Task[] writes = batch.Select( r => pipeline.HashSetAsync( prefix + r.Chunk.ChunkId.ToString( "N" ), ToFields( r ) ) ).ToArray();
            pipeline.Execute();
            await Task.WhenAll( writes );
         }, ct );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      IDatabase db = Database();
      string prefix = KeyPrefix( collection );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         RedisKey[] keys = batch.Select( id => (RedisKey)( prefix + id.ToString( "N" ) ) ).ToArray();
         await WithRetryAsync( () => db.KeyDeleteAsync( keys ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      SearchCommands ft = Database().FT();
      string index = IndexName( collection );
      await WaitForIndexedAsync( ft, index, ct );
      SearchResult result = await ft.SearchAsync( index, new Query( "*" ).Limit( 0, 0 ).Timeout( SEARCH_TIMEOUT_MS ) );
      return result.TotalResults;
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return KnnAsync( IndexName( collection ), vector, top, $"EF_RUNTIME {Math.Max( _options.HnswEfRuntime, top )}", SEARCH_TIMEOUT_MS );
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      SearchCommands ft = Database().FT();
      string exactIndex = ExactIndexName( collection );
      if( !_exactReady.ContainsKey( collection ) )
      {
         if( await ExistingDimensionAsync( ft, exactIndex ) is null )
         {
            await CreateExactIndexAsync( ft, collection, vector.Length );
         }

         await WaitForIndexedAsync( ft, exactIndex, ct );
         _exactReady[collection] = true;
      }

      return await KnnAsync( exactIndex, vector, top, string.Empty, EXACT_TIMEOUT_MS );
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      SearchCommands ft = Database().FT();
      _exactReady.TryRemove( collection, out _ );
      await DropIndexIfPresentAsync( ft, ExactIndexName( collection ), deleteDocuments: false );
      await DropIndexIfPresentAsync( ft, IndexName( collection ), deleteDocuments: true );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Opens the shared connection. Failing to connect throws on the first command instead of
   /// at construction, so the engine catalog can create every sink without every engine up.
   /// </summary>
   /// <returns>The multiplexer.</returns>
   private ConnectionMultiplexer Connect()
   {
      var config = new ConfigurationOptions
      {
         AbortOnConnectFail = false,
         ConnectTimeout = 10000,
         SyncTimeout = 300000,
         AsyncTimeout = 300000,
         Password = _options.Password,
      };
      config.EndPoints.Add( _options.Host, _options.Port );
      return ConnectionMultiplexer.Connect( config );
   }

   /// <summary>
   /// Gets a database handle on the shared connection.
   /// </summary>
   /// <returns>Database 0.</returns>
   private IDatabase Database()
   {
      return _connection.Value.GetDatabase();
   }

   /// <summary>
   /// Runs a KNN query against one index and converts the rows to hits.
   /// </summary>
   /// <param name="index">Index to query.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">Hits wanted.</param>
   /// <param name="runtimeArgs">Extra KNN arguments such as "EF_RUNTIME 100", or empty.</param>
   /// <param name="timeoutMs">Server-side query timeout.</param>
   /// <returns>Hits, best first, with cosine similarity.</returns>
   private async Task<IReadOnlyList<SearchHit>> KnnAsync( string index, float[] vector, int top, string runtimeArgs, int timeoutMs )
   {
      string text = $"*=>[KNN {top} @{VECTOR_FIELD} $vec {runtimeArgs} AS {DISTANCE_ALIAS}]";
      Query query = new Query( text )
         .AddParam( "vec", ToBytes( vector ) )
         .ReturnFields( DISTANCE_ALIAS, "doc_key", "table", "text" )
         .SetSortBy( DISTANCE_ALIAS )
         .Limit( 0, top )
         .Timeout( timeoutMs )
         .Dialect( 2 );
      SearchResult result = await Database().FT().SearchAsync( index, query );
      var hits = new List<SearchHit>( result.Documents.Count );
      foreach( NRedisStack.Search.Document document in result.Documents )
      {
         double distance = double.Parse( document[DISTANCE_ALIAS].ToString(), CultureInfo.InvariantCulture );
         Guid id = Guid.ParseExact( document.Id[( document.Id.LastIndexOf( ':' ) + 1 )..], "N" );
         hits.Add( new SearchHit( id, document["doc_key"].ToString(), document["table"].ToString(), document["text"].ToString(), 1.0 - distance ) );
      }

      return hits;
   }

   /// <summary>
   /// Creates the FLAT (brute force) index over the same hashes as the HNSW index.
   /// </summary>
   /// <param name="ft">Search commands.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="dimension">Vector length.</param>
   private async Task CreateExactIndexAsync( SearchCommands ft, string collection, int dimension )
   {
      Schema schema = new Schema().AddVectorField( VECTOR_FIELD, Schema.VectorField.VectorAlgo.FLAT, new Dictionary<string, object>
      {
         ["TYPE"] = "FLOAT32",
         ["DIM"] = dimension,
         ["DISTANCE_METRIC"] = "COSINE",
      } );
      await ft.CreateAsync( ExactIndexName( collection ), new FTCreateParams().On( IndexDataType.HASH ).Prefix( KeyPrefix( collection ) ), schema );
   }

   /// <summary>
   /// Reads the vector dimension of an index, or null when the index does not exist.
   /// </summary>
   /// <param name="ft">Search commands.</param>
   /// <param name="index">Index name.</param>
   /// <returns>The dimension, or null.</returns>
   private static async Task<int?> ExistingDimensionAsync( SearchCommands ft, string index )
   {
      InfoResult info;
      try
      {
         info = await ft.InfoAsync( index );
      }
      catch( RedisServerException ex ) when( IsMissingIndex( ex ) )
      {
         return null;
      }

      foreach( Dictionary<string, RedisResult> attribute in info.Attributes )
      {
         if( attribute.TryGetValue( "dim", out RedisResult? dim ) )
         {
            return (int)dim;
         }
      }

      throw new InvalidOperationException( $"Redis index {index} exists but has no vector field. Drop it or use a new name." );
   }

   /// <summary>
   /// Waits until the index has finished scanning and ingesting every document, so a count or an
   /// exact query sees everything written so far. Redis builds a new index in the background and
   /// moves vectors from a write buffer into the HNSW graph on worker threads.
   /// </summary>
   /// <param name="ft">Search commands.</param>
   /// <param name="index">Index name.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task WaitForIndexedAsync( SearchCommands ft, string index, CancellationToken ct )
   {
      DateTime deadline = DateTime.UtcNow.AddSeconds( INDEX_WAIT_SECONDS );
      while( true )
      {
         InfoResult info = await ft.InfoAsync( index );
         if( info.PercentIndexed >= 1.0 )
         {
            return;
         }

         if( DateTime.UtcNow > deadline )
         {
            throw new TimeoutException( $"Redis index {index} was still building after {INDEX_WAIT_SECONDS} seconds." );
         }

         await Task.Delay( 200, ct );
      }
   }

   /// <summary>
   /// Drops an index, ignoring one that is already gone.
   /// </summary>
   /// <param name="ft">Search commands.</param>
   /// <param name="index">Index name.</param>
   /// <param name="deleteDocuments">True to delete the hashes the index covers as well.</param>
   private static async Task DropIndexIfPresentAsync( SearchCommands ft, string index, bool deleteDocuments )
   {
      try
      {
         await ft.DropIndexAsync( index, deleteDocuments );
      }
      catch( RedisServerException ex ) when( IsMissingIndex( ex ) )
      {
         // Already gone.
      }
   }

   /// <summary>
   /// True when a server error means "no such index" (the wording differs between versions).
   /// </summary>
   /// <param name="ex">The server error.</param>
   /// <returns>Whether the index is missing.</returns>
   private static bool IsMissingIndex( RedisServerException ex )
   {
      return MISSING_INDEX_WORDINGS.Any( w => ex.Message.Contains( w, StringComparison.OrdinalIgnoreCase ) );
   }

   /// <summary>
   /// Builds the hash fields for one record: the payload fields the other sinks use, plus the
   /// vector as a raw float32 blob.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <returns>Hash entries.</returns>
   private static HashEntry[] ToFields( VectorRecord record )
   {
      var fields = new List<HashEntry>( 6 + record.Document.Metadata.Count )
      {
         new( "doc_key", record.Document.DocKey ),
         new( "table", record.Document.Table ),
         new( "origin", record.Document.Origin ),
         new( "ordinal", record.Chunk.Ordinal ),
         new( "text", record.Chunk.Text ),
         new( VECTOR_FIELD, ToBytes( record.Vector ) ),
      };
      foreach( KeyValuePair<string, string> pair in record.Document.Metadata )
      {
         fields.Add( new HashEntry( $"meta_{pair.Key}", pair.Value ) );
      }

      return fields.ToArray();
   }

   /// <summary>
   /// Packs a vector as little-endian float32 bytes, the blob format Redis expects.
   /// </summary>
   /// <param name="vector">The vector.</param>
   /// <returns>The bytes.</returns>
   private static byte[] ToBytes( float[] vector )
   {
      return MemoryMarshal.AsBytes( vector.AsSpan() ).ToArray();
   }

   /// <summary>
   /// Retries a Redis call on connection and timeout failures with a short backoff.
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
         catch( Exception ex ) when( attempt < MAX_ATTEMPTS && ex is RedisConnectionException or RedisTimeoutException )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
      }
   }

   /// <summary>
   /// Key prefix of a pipeline's hashes. Pipeline names are sanitized to [a-z0-9_], so one
   /// prefix can never be the start of another.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The prefix, ending in a colon.</returns>
   private static string KeyPrefix( string collection )
   {
      return $"gvb_{collection}:";
   }

   /// <summary>
   /// Name of the HNSW index for a pipeline.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The index name.</returns>
   private static string IndexName( string collection )
   {
      return $"gvb_{collection}";
   }

   /// <summary>
   /// Name of the FLAT index for a pipeline. The colon cannot appear in a pipeline name, so it
   /// cannot collide with another pipeline's HNSW index.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The index name.</returns>
   private static string ExactIndexName( string collection )
   {
      return $"gvb_{collection}:flat";
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Closes the connection if it was ever opened.
   /// </summary>
   public void Dispose()
   {
      if( _connection.IsValueCreated )
      {
         _connection.Value.Dispose();
      }
   }

   #endregion IDisposable
}
