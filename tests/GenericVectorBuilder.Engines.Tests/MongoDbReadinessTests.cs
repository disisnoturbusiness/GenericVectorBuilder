using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the MongoDB sink's index readiness check against the real container
/// (deploy/engines/mongodb.compose.yaml must be up).
/// Why: mongot reports a search index READY and queryable while it has indexed none of the
/// documents just written, so the status says nothing about coverage. The sink must read mongot's
/// own document count. The proof that "Ready" means what it says does not come from the sink's own
/// reading: an exact $vectorSearch over the whole collection, counted by the server, must return
/// every document.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class MongoDbReadinessTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 2000;
   private const int PROBE_SEARCHES = 20;
   private const int WRITES = 30;

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer.
   /// </summary>
   /// <param name="output">Where timings and the engine's evidence are printed.</param>
   public MongoDbReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 2,000 random vectors, waits with FinishLoadAsync, requires Ready with mongot holding
   /// every vector, and then cross-checks with an exact $vectorSearch counted by the server.
   /// </summary>
   [Fact]
   public async Task FinishLoad_Waits_UntilMongotHoldsEveryVector()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      var sink = new MongoDbSink { Progress = line => _output.WriteLine( line ) };
      List<VectorRecord> records = EnginesAReadinessData.Records( COUNT, DIMENSION, seed: 41 );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         var clock = Stopwatch.StartNew();
         await sink.UpsertAsync( collection, records, CancellationToken.None );
         double loadSeconds = clock.Elapsed.TotalSeconds;

         BsonDocument listed = await ListedIndexAsync( collection );
         IndexState early = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"MongoDB right after the writes: listSearchIndexes status {listed["status"]} queryable {listed["queryable"]}; sink Ready={early.Ready}, {early.Detail}" );

         clock.Restart();
         string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
         double finishSeconds = clock.Elapsed.TotalSeconds;

         IndexState after = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"MongoDB {COUNT} x {DIMENSION}: load {loadSeconds:F1} s, FinishLoadAsync {finishSeconds:F1} s. Note: {note}" );
         _output.WriteLine( $"MongoDB after: Ready={after.Ready}, indexed {after.IndexedVectors} of {after.TotalVectors}, {after.Detail}" );
         Assert.True( after.Ready, after.Detail );
         Assert.Equal( COUNT, after.TotalVectors );
         Assert.Equal( COUNT, after.IndexedVectors );

         long exactCount = await ExactSearchCountAsync( collection, records[0].Vector );
         _output.WriteLine( $"MongoDB exact $vectorSearch with limit {COUNT * 5}, counted by the server: {exactCount}" );
         Assert.Equal( COUNT, exactCount );
         for( int i = 0; i < PROBE_SEARCHES; i++ )
         {
            IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, records[i].Vector, 10, CancellationToken.None );
            Assert.Equal( records[i].Chunk.ChunkId, hits[0].ChunkId );
         }
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A collection with no search index never becomes ready. The sink must say so, and
   /// FinishLoadAsync must fail with a plain message when its deadline passes instead of waiting
   /// for ever. (Documents without a vector do not make a good stand-in for "never indexed": mongot
   /// counts them in totalDocs about a second after the insert, measured 2026-10-04.)
   /// </summary>
   [Fact]
   public async Task FinishLoad_Fails_WithAPlainMessage_WhenThereIsNoSearchIndex()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      MongoDbSinkOptions options = MongoDbSinkOptions.LocalDefaults();
      options.IndexWaitSeconds = 4;
      var sink = new MongoDbSink( options );
      try
      {
         IMongoCollection<BsonDocument> raw = RawCollection( collection );
         await raw.InsertManyAsync( Enumerable.Range( 0, 50 ).Select( i => new BsonDocument { { "_id", $"novector{i}" }, { "text", "no vector here" } } ) );
         IndexState state = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"Sink state without a search index: Ready={state.Ready}, {state.Detail}" );
         Assert.False( state.Ready );
         Assert.Equal( 50, state.TotalVectors );

         var clock = Stopwatch.StartNew();
         InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, CancellationToken.None ) );
         _output.WriteLine( error.Message );
         Assert.Contains( "did not finish indexing", error.Message );
         Assert.Contains( "no search index", error.Message );
         Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 30 ), $"took {clock.Elapsed.TotalSeconds:F0} s to fail" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// The durability statement says every acknowledged write is journaled. Prove it on the sink's
   /// own write path: thirty single-document upserts must raise WiredTiger's 'log sync operations'
   /// counter by at least thirty.
   /// </summary>
   [Fact]
   public async Task Durability_EveryAcknowledgedUpsertIsSyncedToTheJournal()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      var sink = new MongoDbSink();
      Assert.NotEqual( "not stated", ( (IEngineDescription)sink ).Durability );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         List<VectorRecord> records = EnginesAReadinessData.Records( WRITES, DIMENSION, seed: 43 );
         long before = await JournalSyncsAsync();
         foreach( VectorRecord record in records )
         {
            await sink.UpsertAsync( collection, new[] { record }, CancellationToken.None );
         }

         long delta = await JournalSyncsAsync() - before;
         _output.WriteLine( $"MongoDB WiredTiger 'log sync operations' grew by {delta} over {WRITES} single-document upserts" );
         Assert.True( delta >= WRITES, $"only {delta} journal syncs for {WRITES} acknowledged writes" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Opens a client with the same login the sink uses.
   /// </summary>
   /// <returns>The client.</returns>
   private static MongoClient RawClient()
   {
      MongoDbSinkOptions options = MongoDbSinkOptions.LocalDefaults();
      return new MongoClient( new MongoClientSettings
      {
         Server = new MongoServerAddress( options.Host, options.Port ),
         DirectConnection = true,
         Credential = MongoCredential.CreateCredential( "admin", options.User, options.Password ),
         ServerSelectionTimeout = TimeSpan.FromSeconds( 15 ),
      } );
   }

   /// <summary>
   /// The physical collection the sink uses for a benchmark collection name.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <returns>The raw collection handle.</returns>
   private static IMongoCollection<BsonDocument> RawCollection( string collection )
   {
      return RawClient().GetDatabase( MongoDbSinkOptions.LocalDefaults().Database ).GetCollection<BsonDocument>( "gvb_" + collection );
   }

   /// <summary>
   /// Reads the sink's search index description straight from listSearchIndexes.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <returns>The index description.</returns>
   private static async Task<BsonDocument> ListedIndexAsync( string collection )
   {
      using IAsyncCursor<BsonDocument> cursor = await RawCollection( collection ).SearchIndexes.ListAsync( "gvb_vec" );
      return ( await cursor.ToListAsync() ).Single();
   }

   /// <summary>
   /// Counts the documents an exact $vectorSearch returns when asked for far more than exist.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="queryVector">Any stored vector.</param>
   /// <returns>The number of documents the search returned.</returns>
   private static async Task<long> ExactSearchCountAsync( string collection, float[] queryVector )
   {
      var search = new BsonDocument
      {
         { "index", "gvb_vec" },
         { "path", "embedding" },
         { "queryVector", new BinaryVectorFloat32( queryVector ).ToBsonBinaryData() },
         { "exact", true },
         { "limit", COUNT * 5 },
      };
      BsonDocument[] stages = { new( "$vectorSearch", search ), new( "$count", "n" ) };
      List<BsonDocument> rows = await RawCollection( collection ).Aggregate( PipelineDefinition<BsonDocument, BsonDocument>.Create( stages ) ).ToListAsync();
      return rows.Count == 0 ? 0 : rows[0]["n"].ToInt64();
   }

   /// <summary>
   /// Reads WiredTiger's count of journal syncs from serverStatus.
   /// </summary>
   /// <returns>The 'log sync operations' counter.</returns>
   private static async Task<long> JournalSyncsAsync()
   {
      BsonDocument status = await RawClient().GetDatabase( "admin" ).RunCommandAsync<BsonDocument>( new BsonDocument( "serverStatus", 1 ) );
      return status["wiredTiger"]["log"]["log sync operations"].ToInt64();
   }

   #endregion Private Methods
}
