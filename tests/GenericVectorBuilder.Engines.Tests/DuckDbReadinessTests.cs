using System.Diagnostics;
using DuckDB.NET.Data;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the DuckDB sink's index-readiness report on a real database file in a throwaway folder
/// (DuckDB is embedded, so there is no container): after loading 2,000 random 1024-dimension
/// vectors, duckdb_indexes() must list the HNSW index, pragma_hnsw_index_info() must count every
/// vector, and EXPLAIN of the default search must show an HNSW_INDEX_SCAN on that index. A second
/// test drops the index by hand and requires the report to notice.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class DuckDbReadinessTests : IDisposable
{
   #region Data Members

   private readonly ITestOutputHelper _output;
   private readonly string _folder = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "gvb-work", "gvbbench-duckdb-ready-" + Guid.NewGuid().ToString( "N" )[..8] );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the timings and evidence are printed.</param>
   public DuckDbReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Load 2,000 vectors, finish, and require Ready with every vector in the index.
   /// </summary>
   [Fact]
   public async Task AfterLoad_IndexIsReady_AndCoversEveryVector()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "duck" );
      using var sink = new DuckDbSink( new DuckDbSinkOptions { DirectoryPath = _folder } );
      await sink.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
      double loadSeconds = await GroupCReadinessData.LoadAsync( sink, collection, GroupCReadinessData.MakeRecords( GroupCReadinessData.COUNT, 51 ), ct );
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
      Assert.Equal( state.TotalVectors, state.IndexedVectors );
      Assert.NotEqual( "not stated", sink.Durability );
   }

   /// <summary>
   /// Drop the HNSW index while the sink is closed: a new sink on the same file must report not ready.
   /// </summary>
   [Fact]
   public async Task WhenIndexIsMissing_StateIsNotReady()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "duckx" );
      var options = new DuckDbSinkOptions { DirectoryPath = _folder };
      using( var writer = new DuckDbSink( options ) )
      {
         await writer.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
         await GroupCReadinessData.LoadAsync( writer, collection, GroupCReadinessData.MakeRecords( 50, 52 ), ct );
      }

      DropIndex( Path.Combine( _folder, $"gvb_{collection}.duckdb" ), $"gvb_{collection}_hnsw" );
      using var reader = new DuckDbSink( options );
      IndexState state = await reader.GetIndexStateAsync( collection, ct );
      _output.WriteLine( $"state: ready={state.Ready} indexed={state.IndexedVectors} total={state.TotalVectors} detail={state.Detail}" );
      Assert.False( state.Ready );
      Assert.Equal( 50, state.TotalVectors );
      Assert.Contains( "no HNSW index", state.Detail );
   }

   /// <summary>
   /// Removes the throwaway folder with its database files.
   /// </summary>
   public void Dispose()
   {
      if( Directory.Exists( _folder ) && Path.GetFileName( _folder ).StartsWith( "gvbbench-duckdb-ready-", StringComparison.Ordinal ) )
      {
         Directory.Delete( _folder, recursive: true );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Opens a database file directly and drops one index.
   /// </summary>
   /// <param name="file">Database file in the throwaway folder.</param>
   /// <param name="index">Index name.</param>
   private static void DropIndex( string file, string index )
   {
      using var connection = new DuckDBConnection( $"Data Source={file}" );
      connection.Open();
      foreach( string sql in new[] { "LOAD vss;", "SET hnsw_enable_experimental_persistence = true;", $"DROP INDEX \"{index}\";" } )
      {
         using DuckDBCommand command = connection.CreateCommand();
         command.CommandText = sql;
         command.ExecuteNonQuery();
      }
   }

   #endregion Private Methods
}
