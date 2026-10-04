using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the Redis sink's index-readiness report against the real gvb-redis container: after
/// loading 2,000 random 1024-dimension vectors into a gvbbench_ collection, FT.INFO must say
/// indexing 0, percent_indexed 1, an empty write buffer and num_docs equal to the hashes stored.
/// A second test drops the index but leaves the hashes, and the report must say not ready.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class RedisReadinessTests
{
   #region Data Members

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the timings and evidence are printed.</param>
   public RedisReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Load 2,000 vectors, finish, and require Ready with every vector in the graph.
   /// </summary>
   [Fact]
   public async Task AfterLoad_IndexIsReady_AndCoversEveryVector()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "redis" );
      using var sink = new RedisSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
         double loadSeconds = await GroupCReadinessData.LoadAsync( sink, collection, GroupCReadinessData.MakeRecords( GroupCReadinessData.COUNT, 21 ), ct );
         IndexState before = await sink.GetIndexStateAsync( collection, ct );
         var finish = Stopwatch.StartNew();
         string note = await sink.FinishLoadAsync( collection, ct );
         double finishSeconds = finish.Elapsed.TotalSeconds;
         IndexState state = await sink.GetIndexStateAsync( collection, ct );
         _output.WriteLine( $"load {loadSeconds:F1} s, finish {finishSeconds:F2} s" );
         _output.WriteLine( $"state straight after the writes: ready={before.Ready} indexed={before.IndexedVectors} total={before.TotalVectors} detail={before.Detail}" );
         _output.WriteLine( $"note: {note}" );
         _output.WriteLine( $"state: ready={state.Ready} indexed={state.IndexedVectors} total={state.TotalVectors} detail={state.Detail}" );
         _output.WriteLine( $"durability: {sink.Durability}" );
         Assert.True( state.Ready, state.Detail );
         Assert.Equal( GroupCReadinessData.COUNT, state.TotalVectors );
         Assert.Equal( state.TotalVectors, state.IndexedVectors );
         Assert.NotEqual( "not stated", sink.Durability );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// Drop the index but keep the hashes: the report must say not ready and name the missing index.
   /// </summary>
   [Fact]
   public async Task WhenIndexIsMissing_StateIsNotReady()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "redisx" );
      using var sink = new RedisSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
         await GroupCReadinessData.LoadAsync( sink, collection, GroupCReadinessData.MakeRecords( 50, 22 ), ct );
         await DropIndexKeepHashesAsync( $"gvb_{collection}" );
         IndexState state = await sink.GetIndexStateAsync( collection, ct );
         _output.WriteLine( $"state: ready={state.Ready} indexed={state.IndexedVectors} total={state.TotalVectors} detail={state.Detail}" );
         Assert.False( state.Ready );
         Assert.Equal( 50, state.TotalVectors );
         Assert.Contains( "no index", state.Detail );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
         await DeleteHashesAsync( $"gvb_{collection}:" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Opens a short-lived connection to the local Redis container.
   /// </summary>
   /// <returns>The connection; the caller disposes it.</returns>
   private static async Task<ConnectionMultiplexer> ConnectAsync()
   {
      RedisSinkOptions options = RedisSinkOptions.LocalDefaults();
      var config = new ConfigurationOptions { AbortOnConnectFail = true, ConnectTimeout = 10000, SyncTimeout = 30000, AsyncTimeout = 30000, Password = options.Password };
      config.EndPoints.Add( options.Host, options.Port );
      return await ConnectionMultiplexer.ConnectAsync( config );
   }

   /// <summary>
   /// Drops an index without deleting the documents it covers.
   /// </summary>
   /// <param name="index">Index name; must start with gvb_gvbbench_.</param>
   private static async Task DropIndexKeepHashesAsync( string index )
   {
      Assert.StartsWith( "gvb_gvbbench_", index );
      using ConnectionMultiplexer redis = await ConnectAsync();
      await redis.GetDatabase().ExecuteAsync( "FT.DROPINDEX", index );
   }

   /// <summary>
   /// Deletes the hashes of a benchmark collection after its index was dropped without them.
   /// </summary>
   /// <param name="prefix">Key prefix; must start with gvb_gvbbench_.</param>
   private static async Task DeleteHashesAsync( string prefix )
   {
      Assert.StartsWith( "gvb_gvbbench_", prefix );
      using ConnectionMultiplexer redis = await ConnectAsync();
      IServer server = redis.GetServer( redis.GetEndPoints()[0] );
      RedisKey[] keys = server.Keys( pattern: prefix + "*", pageSize: 1000 ).ToArray();
      if( keys.Length > 0 )
      {
         await redis.GetDatabase().KeyDeleteAsync( keys );
      }
   }

   #endregion Private Methods
}
