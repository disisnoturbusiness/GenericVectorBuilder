using System.Collections.Concurrent;
using System.Diagnostics;
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
/// Why <see cref="FinishLoadAsync"/> and <see cref="GetIndexStateAsync"/> exist, and why the index
/// status is not enough: measured 2026-10-04 on this box, 2,000 random 1024-dimension vectors,
/// listSearchIndexes said status READY and queryable true from the first read, 0.6 s after the
/// insert, while mongot had indexed 0 documents; an exact $vectorSearch counted 1,155 documents
/// 0.8 s in and 2,000 at 2.0 s. So "Ready" needs three readings from the engine: the index status
/// (READY and queryable), mongot's own document count from the explain of an approximate
/// $vectorSearch (metadata.lucene.totalDocs, which must equal the collection's count), and the
/// per-segment execution type in the same explain (luceneVectorSegmentStats: every segment must say
/// "Approximate", which is the HNSW graph, not "Exact"). The state must hold on two reads.
/// Why the segment layout is watched as well: measured 2026-10-05, a set of three loads of 524
/// vectors through this sink ended with 4, 2 and 3 segments (the v5 runs: 4, 4 and 2), a later set of
/// three with 2, 2 and 2, and each layout stood unchanged for as long as it was watched (90 s, 15 s).
/// A READY index that holds every document can still be moving its segments, so
/// <see cref="FinishLoadAsync"/> also waits until the layout (the segment count and each segment's
/// document count, from the same explain) has been the same, with the index ready, for
/// <see cref="MongoDbSinkOptions.LayoutSteadySeconds"/> seconds (<see cref="MongoDbLayoutWatch"/>), and
/// its note and the index state say the layout and how long it stood. The wait cannot make two loads
/// end alike: when the layouts of two runs differ the consolidated report flags the engine and shows
/// it anyway.
/// </summary>
public sealed class MongoDbSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher
{
   #region Data Members

   private const string VECTOR_FIELD = "embedding";
   private const string INDEX_NAME = "gvb_vec";
   private const int DELETE_BATCH = 1000;
   private const int MAX_ATTEMPTS = 3;
   private const int INDEX_WAIT_SECONDS = 180;
   private const int CATCH_UP_SECONDS = 900;
   private const int MAX_NUM_CANDIDATES = 10000;
   private const int STEADY_READS = 2;
   private static readonly TimeSpan CALL_LIMIT = TimeSpan.FromSeconds( 60 );
   private const int MAX_FAILED_READS = 20;
   private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds( 1 );
   private static readonly TimeSpan PROGRESS_INTERVAL = TimeSpan.FromSeconds( 15 );

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
   public string Durability =>
      "Acknowledged writes are journaled before the acknowledgement. The sink sets no write concern, so the server default applies: getDefaultRWConcern gives w majority and the "
      + "one-member replica set (--replSet gvbmongo in the image) has writeConcernMajorityJournalDefault true, with WiredTiger journaling on (journalCommitInterval 100 ms is "
      + "only the interval for unacknowledged work). Measured 2026-10-04: 30 acknowledged single-document inserts raised WiredTiger 'log sync operations' by 30 and strace showed "
      + "fdatasync on /data/db/journal/WiredTigerLog files. A crash loses no acknowledged write. mongot's search index is not part of that promise: it follows the collection "
      + "asynchronously and is rebuilt from it.";

   /// <summary>
   /// Optional receiver for progress lines while <see cref="FinishLoadAsync"/> waits (one line
   /// every 15 seconds). Why: a wait that can last many minutes must show it is moving.
   /// </summary>
   public Action<string>? Progress { get; set; }

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

   /// <summary>
   /// Waits until mongot reports its index READY and queryable, holding a document count equal to
   /// the collection's, searched through the HNSW graph on every segment (see the class remarks
   /// for the three readings), and then until its segment layout (the segment count and each
   /// segment's document count) has stayed the same for
   /// <see cref="MongoDbSinkOptions.LayoutSteadySeconds"/> seconds and two readings. Throws a plain
   /// message when that does not happen within <see cref="MongoDbSinkOptions.IndexWaitSeconds"/>,
   /// or when the readings keep failing.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine's own evidence, plus how long the wait took.</returns>
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      var clock = Stopwatch.StartNew();
      Waited waited = await WaitUntilReadyAsync( name, TimeSpan.FromSeconds( _options.IndexWaitSeconds ), ct );
      return waited.State.Ready
         ? $"{waited.State.Detail}; {waited.LayoutNote}; wait took {clock.Elapsed.TotalSeconds:F1} s"
         : throw new InvalidOperationException( $"MongoDB did not finish indexing {name} within {_options.IndexWaitSeconds} seconds. Last reading: {waited.State.Detail}" );
   }

   /// <summary>
   /// Reads the index state once from mongot itself, without waiting. IndexedVectors is mongot's
   /// own document count (metadata.lucene.totalDocs) and TotalVectors is the collection's count.
   /// When mongot cannot be read, Ready is false and Detail says why.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine's account of its index.</returns>
   public async Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      Readout readout = await ReadStateAsync( CollectionName( collection ), ct );
      return readout.State;
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
   /// What one reading found.
   /// </summary>
   /// <param name="State">The engine's account of its index.</param>
   /// <param name="Failed">True when a command failed, so the reading says nothing about the index.</param>
   /// <param name="Layout">The segment layout as one comparable text (empty when the reading did not get as far as the segments).</param>
   private sealed record Readout( IndexState State, bool Failed, string Layout = "" );

   /// <summary>
   /// How a wait for the index ended.
   /// </summary>
   /// <param name="State">The last state read; its Ready flag says whether the wait succeeded.</param>
   /// <param name="LayoutNote">One sentence on how steady the segment layout was.</param>
   private sealed record Waited( IndexState State, string LayoutNote );

   /// <summary>
   /// Polls the state until it is ready and the segment layout has been steady (see
   /// <see cref="MongoDbLayoutWatch"/>: <see cref="STEADY_READS"/> ready reads in a row with the same
   /// layout, over <see cref="MongoDbSinkOptions.LayoutSteadySeconds"/> seconds) or the deadline
   /// passes. Gives up early, with the last message, after <see cref="MAX_FAILED_READS"/> failed reads
   /// in a row, because a command that keeps failing will not start working by waiting. The deadline
   /// also ends a read that hangs, so the wait can never outlive it.
   /// Why the layout is watched and not only the index status: a READY index that holds every document
   /// can still be rearranging its segments, and the v5 runs ended MongoDB with 4 segments in two runs
   /// and 2 in the third; a timed search should not start while the layout may still move.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="timeout">Longest wait.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The last state read (the caller checks its Ready flag) and a sentence on the layout.</returns>
   private async Task<Waited> WaitUntilReadyAsync( string name, TimeSpan timeout, CancellationToken ct )
   {
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( timeout );
      var watch = new MongoDbLayoutWatch( TimeSpan.FromSeconds( _options.LayoutSteadySeconds ), STEADY_READS );
      IndexState last = new( false, null, null, "no reading taken yet" );
      int failed = 0;
      var clock = Stopwatch.StartNew();
      DateTime nextProgress = DateTime.UtcNow + PROGRESS_INTERVAL;
      try
      {
         while( true )
         {
            Readout readout = await ReadStateAsync( name, limit.Token );
            last = readout.State;
            bool steady = watch.Observe( last.Ready, readout.Layout );
            failed = readout.Failed ? failed + 1 : 0;
            if( steady || failed >= MAX_FAILED_READS )
            {
               return new Waited( last, watch.Describe() );
            }

            if( DateTime.UtcNow >= nextProgress )
            {
               Progress?.Invoke( $"MongoDB {name}: waiting for mongot, {clock.Elapsed.TotalSeconds:F0} s so far. {last.Detail}{( last.Ready ? $"; {watch.Describe()}" : string.Empty )}" );
               nextProgress = DateTime.UtcNow + PROGRESS_INTERVAL;
            }

            await Task.Delay( POLL_INTERVAL, limit.Token );
         }
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         string layout = last.Ready ? $"{last.Detail}; the segment layout did not stay unchanged for {_options.LayoutSteadySeconds} s within the wait ({watch.Describe()})" : last.Detail;
         return new Waited( last with { Ready = false, Detail = layout }, watch.Describe() );
      }
   }

   /// <summary>
   /// Reads the three kinds of evidence (index status, mongot's document count, per-segment
   /// execution type) and decides whether searches now use a finished HNSW index.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   private async Task<Readout> ReadStateAsync( string name, CancellationToken ct )
   {
      long stored = await _database.Value.GetCollection<BsonDocument>( name ).CountDocumentsAsync( FilterDefinition<BsonDocument>.Empty, cancellationToken: ct );
      BsonDocument? index = await FindIndexAsync( name, ct );
      if( index is null )
      {
         return new Readout( new IndexState( false, null, stored, $"MongoDB lists no search index named {INDEX_NAME} on {name}" ), Failed: false );
      }

      string status = index.GetValue( "status", "unknown" ).ToString()!;
      bool queryable = index.GetValue( "queryable", false ).ToBoolean();
      int dimension = IndexDimensionOf( index ) ?? 0;
      BsonDocument explain;
      try
      {
         explain = await ExplainSearchAsync( name, dimension, ct );
      }
      catch( Exception ex ) when( ex is MongoException or InvalidOperationException or TimeoutException )
      {
         return new Readout( new IndexState( false, null, stored, $"index status {status}, queryable {queryable}; the explain of $vectorSearch failed: {ex.Message.Split( '\n' )[0]}" ), Failed: true );
      }

      return ToReadout( explain, status, queryable, stored );
   }

   /// <summary>
   /// Turns the explain of an approximate $vectorSearch into the engine's account of its index.
   /// </summary>
   /// <param name="explain">The explain document.</param>
   /// <param name="status">Index status from listSearchIndexes.</param>
   /// <param name="queryable">Queryable flag from listSearchIndexes.</param>
   /// <param name="stored">Documents in the collection.</param>
   /// <returns>The reading.</returns>
   private static Readout ToReadout( BsonDocument explain, string status, bool queryable, long stored )
   {
      BsonDocument search = explain["stages"].AsBsonArray[0]["$vectorSearch"]["explain"].AsBsonDocument;
      if( !search.Contains( "metadata" ) || !search["metadata"].AsBsonDocument.Contains( "lucene" ) )
      {
         return new Readout( new IndexState( false, null, stored, $"index status {status}, queryable {queryable}; the explain has no mongot lucene metadata (totalDocs), so the indexed count cannot be read" ), Failed: true );
      }

      long indexed = search["metadata"]["lucene"]["totalDocs"].ToInt64();
      BsonArray segments = search.GetValue( "luceneVectorSegmentStats", new BsonArray() ).AsBsonArray;
      int approximate = segments.Count( s => s["executionType"].AsString == "Approximate" );
      long[] documents = DocumentCounts( segments );
      string held = documents.Length == 0 ? string.Empty : $" ({string.Join( " + ", documents )} documents)";
      string detail = $"index status {status}, queryable {queryable}; mongot holds {indexed} of {stored} documents in {segments.Count} segment(s){held}, {approximate} searched through the HNSW graph (Approximate)";
      bool ready = status == "READY" && queryable && indexed == stored && approximate == segments.Count && ( stored == 0 || segments.Count > 0 );
      return new Readout( new IndexState( ready, indexed, stored, detail ), Failed: false, MongoDbLayoutWatch.Signature( segments.Count, documents ) );
   }

   /// <summary>
   /// The document count of each segment the explain lists, largest first. Empty when any segment
   /// does not state one (the layout is then the segment count alone).
   /// </summary>
   /// <param name="segments">The luceneVectorSegmentStats array.</param>
   /// <returns>The counts, or none.</returns>
   private static long[] DocumentCounts( BsonArray segments )
   {
      var counts = new List<long>();
      foreach( BsonValue segment in segments )
      {
         if( !segment.IsBsonDocument || !segment.AsBsonDocument.TryGetValue( "docCount", out BsonValue? count ) || !count.IsNumeric )
         {
            return Array.Empty<long>();
         }

         counts.Add( count.ToInt64() );
      }

      return counts.OrderByDescending( c => c ).ToArray();
   }

   /// <summary>
   /// Runs the explain (executionStats) of an approximate $vectorSearch for a fixed unit vector,
   /// which asks mongot itself how many documents it holds and how it searched them. Nothing is
   /// written; the unit vector is only a valid query.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="dimension">Vector length of the index.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The explain document.</returns>
   private async Task<BsonDocument> ExplainSearchAsync( string name, int dimension, CancellationToken ct )
   {
      if( dimension <= 0 )
      {
         throw new InvalidOperationException( "the index definition does not state numDimensions" );
      }

      var unit = new float[dimension];
      unit[0] = 1f;
      var search = new BsonDocument
      {
         { "index", INDEX_NAME },
         { "path", VECTOR_FIELD },
         { "queryVector", ToBinary( unit ) },
         { "numCandidates", _options.MinNumCandidates },
         { "limit", 10 },
      };
      var command = new BsonDocument
      {
         { "explain", new BsonDocument { { "aggregate", name }, { "pipeline", new BsonArray { new BsonDocument( "$vectorSearch", search ) } }, { "cursor", new BsonDocument() } } },
         { "verbosity", "executionStats" },
      };
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( CALL_LIMIT );
      try
      {
         return await _database.Value.RunCommandAsync( new BsonDocumentCommand<BsonDocument>( command ), cancellationToken: limit.Token );
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         throw new TimeoutException( $"MongoDB did not answer the explain within {CALL_LIMIT.TotalSeconds:0} s" );
      }
   }

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
      return index is null ? null : IndexDimensionOf( index );
   }

   /// <summary>
   /// Reads the dimension out of an index description.
   /// </summary>
   /// <param name="index">The description from listSearchIndexes.</param>
   /// <returns>The dimension, or null when the definition does not state one.</returns>
   private static int? IndexDimensionOf( BsonDocument index )
   {
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
   /// vector and mongot's own document count has reached the collection's count. The first test
   /// alone proves too little: measured 2026-10-04, mongot applied the last write while it still
   /// held 961 of 2,000 documents (4 of its 8 segments), and an exact search at that moment
   /// returned 0.96 recall against brute force. When mongot's count cannot be read, the first test
   /// decides, as before. Does nothing when this sink has written nothing since it started.
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
         if( hits.Count > 0 && hits[0].ChunkId == probe.Chunk.ChunkId && await MongotHasEveryDocumentAsync( collection, ct ) )
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
   /// True when mongot's own document count equals the collection's count, or when mongot's count
   /// cannot be read (the caller then relies on its other test).
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Whether mongot has indexed every stored document.</returns>
   private async Task<bool> MongotHasEveryDocumentAsync( string collection, CancellationToken ct )
   {
      Readout readout = await ReadStateAsync( CollectionName( collection ), ct );
      return readout.Failed || readout.State.IndexedVectors is null || readout.State.IndexedVectors >= readout.State.TotalVectors;
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
