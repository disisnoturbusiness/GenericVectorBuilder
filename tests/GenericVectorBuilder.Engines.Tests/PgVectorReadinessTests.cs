using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Npgsql;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the pgvector sink's index-readiness report against the real container: after loading
/// 2,000 random 1024-dimension vectors the engine itself must say the HNSW index is valid, that
/// the default search plan scans it, and that search must return its rows. PostgreSQL keeps no
/// entry count for an HNSW index, so IndexedVectors is null and the detail says why. A second
/// test drops the index by hand and requires the report to notice, so the check cannot pass
/// by default.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class PgVectorReadinessTests
{
   #region Data Members

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the timings and evidence are printed.</param>
   public PgVectorReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Load 2,000 vectors, finish, and require Ready. The exact stored count is checked; the
   /// indexed count cannot be read from PostgreSQL, so it must be null and the detail must say so.
   /// </summary>
   [Fact]
   public async Task AfterLoad_IndexIsReady_AndSearchesUseIt()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "pg" );
      using var sink = new PgVectorSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
         double loadSeconds = await GroupCReadinessData.LoadAsync( sink, collection, GroupCReadinessData.MakeRecords( GroupCReadinessData.COUNT, 7 ), ct );
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
         Assert.Contains( "keeps no entry count", state.Detail );
         Assert.Contains( "uses Index Scan on it = True", state.Detail );
         Assert.NotEqual( "not stated", sink.Durability );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// Drop the HNSW index behind the sink's back: the report must say not ready.
   /// </summary>
   [Fact]
   public async Task WhenIndexIsMissing_StateIsNotReady()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "pgx" );
      using var sink = new PgVectorSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
         await GroupCReadinessData.LoadAsync( sink, collection, GroupCReadinessData.MakeRecords( 50, 8 ), ct );
         await DropIndexAsync( $"gvb_{collection}_hnsw" );
         IndexState state = await sink.GetIndexStateAsync( collection, ct );
         _output.WriteLine( $"state: ready={state.Ready} indexed={state.IndexedVectors} total={state.TotalVectors} detail={state.Detail}" );
         Assert.False( state.Ready );
         Assert.Equal( 50, state.TotalVectors );
         Assert.Contains( "no index", state.Detail );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Drops an index with a plain SQL statement on its own connection.
   /// </summary>
   /// <param name="index">Index name.</param>
   private static async Task DropIndexAsync( string index )
   {
      PgVectorSinkOptions options = PgVectorSinkOptions.LocalDefaults();
      var connectionString = new NpgsqlConnectionStringBuilder
      {
         Host = options.Host,
         Port = options.Port,
         Database = options.Database,
         Username = options.User,
         Password = options.Password,
         Pooling = false,
         Timeout = 15,
         CommandTimeout = 60,
      };
      await using var connection = new NpgsqlConnection( connectionString.ConnectionString );
      await connection.OpenAsync();
      await using var command = new NpgsqlCommand( $"DROP INDEX \"{index}\"", connection );
      await command.ExecuteNonQueryAsync();
   }

   #endregion Private Methods
}
