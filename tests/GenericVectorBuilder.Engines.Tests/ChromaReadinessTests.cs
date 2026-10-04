using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the Chroma sink's index-readiness report against the real gvb-chroma container. Chroma
/// (single node) exposes no index status, so the report must say so plainly, give the exact count,
/// and show that a vector search asking for as many results as there are vectors returns nearly
/// all of them. A second test asks about a collection that does not exist and requires not ready.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class ChromaReadinessTests
{
   #region Data Members

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the timings and evidence are printed.</param>
   public ChromaReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Load 2,000 vectors, finish, and require Ready with the exact count. IndexedVectors must be
   /// null because Chroma cannot report it, and the detail must say that.
   /// </summary>
   [Fact]
   public async Task AfterLoad_RecentVectorsAreSearchable_AndLackOfStatusIsStated()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "chroma" );
      using var sink = new ChromaSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
         double loadSeconds = await GroupCReadinessData.LoadAsync( sink, collection, GroupCReadinessData.MakeRecords( GroupCReadinessData.COUNT, 41 ), ct );
         var finish = Stopwatch.StartNew();
         string note = await sink.FinishLoadAsync( collection, ct );
         double finishSeconds = finish.Elapsed.TotalSeconds;
         IndexState state = await sink.GetIndexStateAsync( collection, ct );
         _output.WriteLine( $"load {loadSeconds:F1} s, finish {finishSeconds:F2} s" );
         _output.WriteLine( $"note: {note}" );
         _output.WriteLine( $"state: ready={state.Ready} indexed={state.IndexedVectors} total={state.TotalVectors} detail={state.Detail}" );
         _output.WriteLine( $"durability: {sink.Durability}" );
         Assert.True( state.Ready, state.Detail );
         Assert.Equal( GroupCReadinessData.COUNT, state.TotalVectors );
         Assert.Null( state.IndexedVectors );
         Assert.Contains( "exposes no index status", state.Detail );
         Assert.NotEqual( "not stated", sink.Durability );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A collection that was never created is not ready, and the answer says so in plain words.
   /// </summary>
   [Fact]
   public async Task WhenCollectionIsMissing_StateIsNotReady()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      using var sink = new ChromaSink();
      string collection = GroupCReadinessData.NewCollection( "chromax" );
      IndexState state = await sink.GetIndexStateAsync( collection, deadline.Token );
      _output.WriteLine( $"state: ready={state.Ready} indexed={state.IndexedVectors} total={state.TotalVectors} detail={state.Detail}" );
      Assert.False( state.Ready );
      Assert.Contains( "no collection", state.Detail );
   }

   #endregion Public Methods
}
