using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the Weaviate sink's index-readiness report against the real gvb-weaviate container:
/// after loading 2,000 random 1024-dimension vectors into a gvbbench_ class, every shard and its
/// vector index must report READY with an empty queue, the Aggregate count must equal what was
/// loaded, and a vector search must reach nearly all of them. Weaviate's own node status object
/// count is printed but not required, because it is refreshed only when a memtable is flushed, 60
/// seconds after the last write. A second test asks about a class that does not exist and
/// requires not ready.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class WeaviateReadinessTests
{
   #region Data Members

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the timings and evidence are printed.</param>
   public WeaviateReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Load 2,000 vectors, finish, and require Ready: every shard READY with an empty vector queue,
   /// the Aggregate count equal to what was loaded, and a vector search reaching nearly all of
   /// them. Weaviate reports no count of vectors in the HNSW index, so IndexedVectors must be null
   /// and the detail must say so.
   /// </summary>
   [Fact]
   public async Task AfterLoad_IndexIsReady_AndObjectCountsAgree()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "wv" );
      using var sink = new WeaviateSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
         double loadSeconds = await GroupCReadinessData.LoadAsync( sink, collection, GroupCReadinessData.MakeRecords( GroupCReadinessData.COUNT, 31 ), ct );
         IndexState before = await sink.GetIndexStateAsync( collection, ct );
         var finish = Stopwatch.StartNew();
         string note = await sink.FinishLoadAsync( collection, ct );
         double finishSeconds = finish.Elapsed.TotalSeconds;
         IndexState state = await sink.GetIndexStateAsync( collection, ct );
         _output.WriteLine( $"load {loadSeconds:F1} s, finish {finishSeconds:F1} s" );
         _output.WriteLine( $"state straight after the writes: ready={before.Ready} indexed={before.IndexedVectors} total={before.TotalVectors} detail={before.Detail}" );
         _output.WriteLine( $"note: {note}" );
         _output.WriteLine( $"state: ready={state.Ready} indexed={state.IndexedVectors} total={state.TotalVectors} detail={state.Detail}" );
         _output.WriteLine( $"durability: {sink.Durability}" );
         Assert.True( state.Ready, state.Detail );
         Assert.Equal( GroupCReadinessData.COUNT, state.TotalVectors );
         Assert.Null( state.IndexedVectors );
         Assert.Contains( "vectorIndexingStatus READY, vectorQueueLength 0", state.Detail );
         Assert.Contains( $"Aggregate count {GroupCReadinessData.COUNT}", state.Detail );
         Assert.Contains( "reports no count of vectors", state.Detail );
         Assert.NotEqual( "not stated", sink.Durability );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A class that was never created is not ready, and the answer says so in plain words.
   /// </summary>
   [Fact]
   public async Task WhenClassIsMissing_StateIsNotReady()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      using var sink = new WeaviateSink();
      string collection = GroupCReadinessData.NewCollection( "wvx" );
      IndexState state = await sink.GetIndexStateAsync( collection, deadline.Token );
      _output.WriteLine( $"state: ready={state.Ready} indexed={state.IndexedVectors} total={state.TotalVectors} detail={state.Detail}" );
      Assert.False( state.Ready );
      Assert.Contains( "no class", state.Detail );
   }

   #endregion Public Methods
}
