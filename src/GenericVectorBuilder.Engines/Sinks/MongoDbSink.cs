using System.Collections.Concurrent;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to MongoDB with Atlas Vector Search (the mongodb-atlas-local image runs mongod
/// plus mongot): one collection per pipeline named gvb_{pipeline}, one document per chunk with
/// _id = the chunk id, the payload fields the other sinks use, and the vector in field
/// "embedding".
/// Default search: a vectorSearch index (cosine, Lucene HNSW through mongot, maxEdges 16,
/// numEdgeCandidates 128) queried with $vectorSearch and numCandidates = 20 times the hits
/// wanted (at least 100).
/// Why numCandidates is 20 times the hits (at least 100): MongoDB has no default, the field is
/// required, and its guidance is 10 to 20 times the limit. Measured 2026-10-03 on this box,
/// 100,000 clustered vectors of 1024 dimensions (500 centres plus noise), 20 queries, top 10:
///   numCandidates   recall@10   ms p50
///   50              0.985       14.7
///   100             1.00        11.2
///   200             1.00        11.4
///   500             1.00        18.0
///   1000            1.00        24.5
/// On uniform random vectors (the worst case, used by the scale test) numCandidates 200 gave
/// recall 0.55 at p50 117 ms.
/// Exact search: the same stage with exact: true, which mongot answers by comparing every vector.
/// Why vectors are stored as BSON binData float32: a plain array stores each float as a
/// 9-byte-plus-key double element (about 14 bytes a dimension), binData is 4 bytes a dimension.
/// Why scores are converted: Atlas reports a cosine score as (1 + cosine) / 2 so it stays between
/// 0 and 1; this sink returns 2 * score - 1 so every engine reports plain cosine similarity.
/// Why the index is waited for: createSearchIndex returns at once and mongot builds the index in
/// the background. EnsureCollectionAsync waits until the index is queryable, and CountAsync
/// waits until the most recent write is searchable, because mongot follows the collection
/// asynchronously. That wait is part of the load time on purpose: a document that cannot be
/// found yet is not loaded as far as a search is concerned.
/// </summary>
public sealed class MongoDbSink : ISink, IExactSearchSink, IEngineDescription
{
   #region Data Members

   private const string VECTOR_FIELD = "embedding";
   private const string INDEX_NAME = "gvb_vec";
   private const int DELETE_BATCH = 1000;
   private const int MAX_ATTEMPTS = 3;
   private const int INDEX_WAIT_SECONDS = 180;
   private const int CATCH_UP_SECONDS = 900;
   private const int MAX_NUM_CANDIDATES = 10000;

   private readonly MongoDbSinkOptions _options;
   private readonly Lazy<IMongoDatabase> _database;
   private readonly ConcurrentDictionary<string, VectorRecord> _lastWrite = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a sink for the local MongoDB container with the password from the secrets file.
   /// </summary>
   public MongoDbSink() : this( MongoDbSinkOptions.LocalDefaults() )
   {
   }

   /// <summary>
   /// Creates a sink with explicit settings.
   /// </summary>
   /// <param name="options">Connection and index settings.</param>
   public MongoDbSink( MongoDbSinkOptions options )
   {
      _options = options;
      _database = new Lazy<IMongoDatabase>( Connect );
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "mongodb";

   /// <inheritdoc />
   public string Engine => "MongoDB 8.0.32 Atlas Local (mongod + mongot Vector Search)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"vectorSearch index, HNSW maxEdges={_options.HnswMaxEdges} numEdgeCandidates={_options.HnswNumEdgeCandidates}, float32 binData, cosine, numCandidates={_options.NumCandidatesFactor}x hits (min {_options.MinNumCandidates}); exact mode = $vectorSearch exact:true";

   /// <inheritdoc />
   public string ComposeFile => "mongodb.compose.yaml";

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string name = CollectionName( collection );
      bool exists = await CollectionExistsAsync( name, ct );
      if( !exists )
      {
         await _database.Value.CreateCollectionAsync( name, cancellationToken: ct );
      }

      int? indexed = await IndexDimensionAsync( name, ct );
      if( indexed.HasValue && indexed.Value != dimension )
      {
         throw new InvalidOperationException( $"MongoDB collection {name} holds {indexed}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
      }

      if( !indexed.HasValue )
      {
         await CreateIndexAsync( name, dimension, ct );
      }

      await WaitForQueryableAsync( name, ct );
      return !exists;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      if( records.Count == 0 )
      {
         return;
      }

      IMongoCollection<BsonDocument> target = Collection( collection );
      var options = new BulkWriteOptions { IsOrdered = false };
      foreach( VectorRecord[] batch in records.Chunk( _options.UpsertBatch ) )
      {
         var writes = batch.Select( r => (WriteModel<BsonDocument>)new ReplaceOneModel<BsonDocument>(
            Builders<BsonDocument>.Filter.Eq( "_id", r.Chunk.ChunkId.ToString( "D" ) ), ToDocument( r ) ) { IsUpsert = true } ).ToList();
         await WithRetryAsync( () => target.BulkWriteAsync( writes, options, ct ), ct );
      }

      _lastWrite[collection] = records[^1];
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      IMongoCollection<BsonDocument> target = Collection( collection );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.In( "_id", batch.Select( id => id.ToString( "D" ) ) );
         await WithRetryAsync( () => target.DeleteManyAsync( filter, ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      await WaitForLastWriteAsync( collection, ct );
      return await Collection( collection ).CountDocumentsAsync( FilterDefinition<BsonDocument>.Empty, cancellationToken: ct );
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      await WaitForLastWriteAsync( collection, ct );
      int candidates = Math.Min( MAX_NUM_CANDIDATES, Math.Max( _options.MinNumCandidates, top * _options.NumCandidatesFactor ) );
      return await RunSearchAsync( collection, vector, top, candidates, exact: false, ct );
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      await WaitForLastWriteAsync( collection, ct );
      return await RunSearchAsync( collection, vector, top, candidates: 0, exact: true, ct );
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      _lastWrite.TryRemove( collection, out _ );
      await _database.Value.DropCollectionAsync( CollectionName( collection ), ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Opens the client. Nothing connects until the first command, so the engine catalog can
   /// create every sink without every engine being up.
   /// </summary>
   /// <returns>The database handle.</returns>
   private IMongoDatabase Connect()
   {
      var settings = new MongoClientSettings
      {
         Server = new MongoServerAddress( _options.Host, _options.Port ),
         DirectConnection = true,
         ServerSelectionTimeout = TimeSpan.FromSeconds( 15 ),
      };
      if( !string.IsNullOrEmpty( _options.Password ) )
      {
         settings.Credential = MongoCredential.CreateCredential( "admin", _options.User, _options.Password );
      }

      return new MongoClient( settings ).GetDatabase( _options.Database );
   }

   /// <summary>
   /// Runs one $vectorSearch and converts the rows to hits.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">Hits wanted.</param>
   /// <param name="candidates">numCandidates for the approximate search (ignored when exact).</param>
   /// <param name="exact">True to compare every vector.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity.</returns>
   private async Task<IReadOnlyList<SearchHit>> RunSearchAsync( string collection, float[] vector, int top, int candidates, bool exact, CancellationToken ct )
   {
      var search = new BsonDocument
      {
         { "index", INDEX_NAME },
         { "path", VECTOR_FIELD },
         { "queryVector", ToBinary( vector ) },
         { "limit", top },
      };
      if( exact )
      {
         search["exact"] = true;
      }
      else
      {
         search["numCandidates"] = candidates;
      }

      BsonDocument[] stages =
      {
         new( "$vectorSearch", search ),
         new( "$project", new BsonDocument { { "doc_key", 1 }, { "table", 1 }, { "text", 1 }, { "score", new BsonDocument( "$meta", "vectorSearchScore" ) } } ),
      };
      List<BsonDocument> rows = await Collection( collection ).Aggregate( PipelineDefinition<BsonDocument, BsonDocument>.Create( stages ) ).ToListAsync( ct );
      return rows.Select( ToHit ).ToList();
   }

   /// <summary>
   /// Converts one result row to a hit, turning Atlas's 0-to-1 cosine score back into cosine.
   /// </summary>
   /// <param name="row">Result row.</param>
   /// <returns>The hit.</returns>
   private static SearchHit ToHit( BsonDocument row )
   {
      double atlasScore = row["score"].ToDouble();
      return new SearchHit( Guid.Parse( row["_id"].AsString ), Text( row, "doc_key" ), Text( row, "table" ), Text( row, "text" ), 2.0 * atlasScore - 1.0 );
   }

   /// <summary>
   /// Reads a string field, or empty when it is missing.
   /// </summary>
   /// <param name="row">Result row.</param>
   /// <param name="key">Field name.</param>
   /// <returns>The value.</returns>
   private static string Text( BsonDocument row, string key )
   {
      return row.TryGetValue( key, out BsonValue? value ) && value.IsString ? value.AsString : string.Empty;
   }

   /// <summary>
   /// Builds the stored document for one record.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <returns>The BSON document.</returns>
   private static BsonDocument ToDocument( VectorRecord record )
   {
      var document = new BsonDocument
      {
         { "_id", record.Chunk.ChunkId.ToString( "D" ) },
         { "doc_key", record.Document.DocKey },
         { "table", record.Document.Table },
         { "origin", record.Document.Origin },
         { "ordinal", record.Chunk.Ordinal },
         { "text", record.Chunk.Text },
         { VECTOR_FIELD, ToBinary( record.Vector ) },
      };
      foreach( KeyValuePair<string, string> pair in record.Document.Metadata )
      {
         document[$"meta_{pair.Key}"] = pair.Value;
      }

      return document;
   }

   /// <summary>
   /// Packs a vector as a BSON binData float32 vector.
   /// </summary>
   /// <param name="vector">The vector.</param>
   /// <returns>The BSON value.</returns>
   private static BsonBinaryData ToBinary( float[] vector )
   {
      return new BinaryVectorFloat32( vector ).ToBsonBinaryData();
   }

   /// <summary>
   /// Creates the vector search index on a collection.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="dimension">Vector length.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task CreateIndexAsync( string name, int dimension, CancellationToken ct )
   {
      var model = new CreateVectorSearchIndexModel<BsonDocument>( VECTOR_FIELD, INDEX_NAME, VectorSimilarity.Cosine, dimension )
      {
         HnswMaxEdges = _options.HnswMaxEdges,
         HnswNumEdgeCandidates = _options.HnswNumEdgeCandidates,
      };
      await _database.Value.GetCollection<BsonDocument>( name ).SearchIndexes.CreateOneAsync( model, ct );
   }

   /// <summary>
   /// Reads the dimension an existing vector index was created with.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The dimension, or null when the collection has no vector index yet.</returns>
   private async Task<int?> IndexDimensionAsync( string name, CancellationToken ct )
   {
      BsonDocument? index = await FindIndexAsync( name, ct );
      if( index is null )
      {
         return null;
      }

      BsonValue definition = index.GetValue( "latestDefinition", index.GetValue( "definition", new BsonDocument() ) );
      BsonValue fields = definition.IsBsonDocument ? definition.AsBsonDocument.GetValue( "fields", new BsonArray() ) : new BsonArray();
      foreach( BsonValue field in fields.AsBsonArray )
      {
         if( field.IsBsonDocument && field.AsBsonDocument.TryGetValue( "numDimensions", out BsonValue? dimensions ) )
         {
            return dimensions.ToInt32();
         }
      }

      return null;
   }

   /// <summary>
   /// Finds this sink's vector index on a collection.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The index description, or null.</returns>
   private async Task<BsonDocument?> FindIndexAsync( string name, CancellationToken ct )
   {
      using IAsyncCursor<BsonDocument> cursor = await _database.Value.GetCollection<BsonDocument>( name ).SearchIndexes.ListAsync( INDEX_NAME, cancellationToken: ct );
      List<BsonDocument> found = await cursor.ToListAsync( ct );
      return found.FirstOrDefault();
   }

   /// <summary>
   /// Waits until mongot says the index can be queried.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task WaitForQueryableAsync( string name, CancellationToken ct )
   {
      DateTime deadline = DateTime.UtcNow.AddSeconds( INDEX_WAIT_SECONDS );
      while( true )
      {
         BsonDocument? index = await FindIndexAsync( name, ct );
         if( index is not null && index.GetValue( "queryable", false ).ToBoolean() )
         {
            return;
         }

         if( DateTime.UtcNow > deadline )
         {
            throw new TimeoutException( $"The vector index on MongoDB collection {name} was not ready after {INDEX_WAIT_SECONDS} seconds." );
         }

         await Task.Delay( 500, ct );
      }
   }

   /// <summary>
   /// Waits until the most recent document this sink wrote is the top exact hit for its own
   /// vector, which means mongot has applied every write up to it. Does nothing when this sink
   /// has written nothing since it started.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task WaitForLastWriteAsync( string collection, CancellationToken ct )
   {
      if( !_lastWrite.TryGetValue( collection, out VectorRecord? probe ) )
      {
         return;
      }

      DateTime deadline = DateTime.UtcNow.AddSeconds( CATCH_UP_SECONDS );
      while( true )
      {
         IReadOnlyList<SearchHit> hits = await RunSearchAsync( collection, probe.Vector, 1, candidates: 0, exact: true, ct );
         if( hits.Count > 0 && hits[0].ChunkId == probe.Chunk.ChunkId )
         {
            _lastWrite.TryRemove( collection, out _ );
            return;
         }

         if( DateTime.UtcNow > deadline )
         {
            throw new TimeoutException( $"MongoDB search index was still behind the collection after {CATCH_UP_SECONDS} seconds." );
         }

         await Task.Delay( 250, ct );
      }
   }

   /// <summary>
   /// Whether a collection already exists in the database.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when it exists.</returns>
   private async Task<bool> CollectionExistsAsync( string name, CancellationToken ct )
   {
      var filter = new BsonDocument( "name", name );
      using IAsyncCursor<string> cursor = await _database.Value.ListCollectionNamesAsync( new ListCollectionNamesOptions { Filter = filter }, ct );
      return await cursor.AnyAsync( ct );
   }

   /// <summary>
   /// Retries a call on connection and timeout failures with a short backoff.
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
         catch( Exception ex ) when( attempt < MAX_ATTEMPTS && ex is MongoConnectionException or TimeoutException )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
      }
   }

   /// <summary>
   /// Gets the collection handle for a pipeline.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The collection.</returns>
   private IMongoCollection<BsonDocument> Collection( string collection )
   {
      return _database.Value.GetCollection<BsonDocument>( CollectionName( collection ) );
   }

   /// <summary>
   /// MongoDB collection name for a pipeline. Pipeline names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The collection name.</returns>
   private static string CollectionName( string collection )
   {
      return $"gvb_{collection}";
   }

   #endregion Private Methods
}
