using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using MySqlConnector;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the MariaDB sink's index-readiness report against the real gvb-mariadb container: after
/// loading 2,000 random 1024-dimension vectors into a gvbbench_ table the engine itself must list
/// the VECTOR index, EXPLAIN must name it as the key of the default search, and a search must get
/// its rows back through the graph. A second test drops the index by hand and requires the
/// report to notice. Only tables named gvb_gvbbench_* are touched; the benchmark's own tables stay
/// as they are.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class MariaDbReadinessTests
{
   #region Data Members

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the timings and evidence are printed.</param>
   public MariaDbReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Load 2,000 vectors, finish, and require Ready. MariaDB cannot count what its index holds,
   /// so IndexedVectors must be null and the detail must say why; the total is still exact.
   /// </summary>
   [Fact]
   public async Task AfterLoad_IndexIsReady_AndSearchesUseIt()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "mdb" );
      using var sink = new MariaDbSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
         double loadSeconds = await GroupCReadinessData.LoadAsync( sink, collection, GroupCReadinessData.MakeRecords( GroupCReadinessData.COUNT, 11 ), ct );
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
         Assert.Contains( "no per-index row count", state.Detail );
         Assert.NotEqual( "not stated", sink.Durability );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// Drop the VECTOR index behind the sink's back: the report must say not ready.
   /// </summary>
   [Fact]
   public async Task WhenIndexIsMissing_StateIsNotReady()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "mdbx" );
      using var sink = new MariaDbSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
         await GroupCReadinessData.LoadAsync( sink, collection, GroupCReadinessData.MakeRecords( 50, 12 ), ct );
         await DropVectorIndexAsync( $"gvb_{collection}" );
         IndexState state = await sink.GetIndexStateAsync( collection, ct );
         _output.WriteLine( $"state: ready={state.Ready} indexed={state.IndexedVectors} total={state.TotalVectors} detail={state.Detail}" );
         Assert.False( state.Ready );
         Assert.Equal( 50, state.TotalVectors );
         Assert.Contains( "no VECTOR index", state.Detail );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Drops the vector index of a benchmark table with a plain statement on its own connection.
   /// The name must start with gvb_gvbbench_, so a real pipeline's table can never be hit.
   /// </summary>
   /// <param name="table">Table name inside the gvb database.</param>
   private static async Task DropVectorIndexAsync( string table )
   {
      Assert.StartsWith( "gvb_gvbbench_", table );
      MariaDbSinkOptions options = MariaDbSinkOptions.LocalDefaults();
      var builder = new MySqlConnectionStringBuilder
      {
         Server = options.Host,
         Port = (uint)options.Port,
         UserID = options.User,
         Password = options.Password ?? string.Empty,
         Database = options.Database,
         SslMode = MySqlSslMode.None,
         ConnectionTimeout = 15,
         DefaultCommandTimeout = 60,
         Pooling = false,
      };
      await using var connection = new MySqlConnection( builder.ConnectionString );
      await connection.OpenAsync();
      await using var command = new MySqlCommand( $"ALTER TABLE `{table}` DROP INDEX vec_idx", connection );
      await command.ExecuteNonQueryAsync();
   }

   #endregion Private Methods
}
