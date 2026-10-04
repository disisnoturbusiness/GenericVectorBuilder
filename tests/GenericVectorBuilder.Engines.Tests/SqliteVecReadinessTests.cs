using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the sqlite-vec sink's index-readiness report on a real database file in a throwaway
/// folder (SQLite is embedded, so there is no container): sqlite-vec has no ANN index, so the
/// report must be ready with the words "no index, exact scan by design", the exact row count and
/// the plan SQLite gives the search. A second test turns the table into an ordinary one and
/// requires the report to refuse it.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class SqliteVecReadinessTests : IDisposable
{
   #region Data Members

   private readonly ITestOutputHelper _output;
   private readonly string _folder = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "gvb-work", "gvbbench-sqlitevec-ready-" + Guid.NewGuid().ToString( "N" )[..8] );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the timings and evidence are printed.</param>
   public SqliteVecReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Load 2,000 vectors, finish, and require Ready with the "no index" wording and the exact count.
   /// IndexedVectors is null because there is no index to cover anything.
   /// </summary>
   [Fact]
   public async Task AfterLoad_ReportsNoIndexByDesign_WithExactCount()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "svec" );
      using var sink = new SqliteVecSink( new SqliteVecSinkOptions { DirectoryPath = _folder } );
      await sink.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
      double loadSeconds = await GroupCReadinessData.LoadAsync( sink, collection, GroupCReadinessData.MakeRecords( GroupCReadinessData.COUNT, 61 ), ct );
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
      Assert.Contains( "no index, exact scan by design", state.Detail );
      Assert.NotEqual( "not stated", sink.Durability );
   }

   /// <summary>
   /// Replace the vec0 table with an ordinary table of the same name while the sink is closed: a new
   /// sink must refuse to call that ready.
   /// </summary>
   [Fact]
   public async Task WhenTableIsNotVec0_StateIsNotReady()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "svecx" );
      var options = new SqliteVecSinkOptions { DirectoryPath = _folder };
      using( var writer = new SqliteVecSink( options ) )
      {
         await writer.EnsureCollectionAsync( collection, GroupCReadinessData.DIMENSION, ct );
      }

      ReplaceWithPlainTable( Path.Combine( _folder, $"gvb_{collection}.sqlite" ), $"gvb_{collection}" );
      using var reader = new SqliteVecSink( options );
      IndexState state = await reader.GetIndexStateAsync( collection, ct );
      _output.WriteLine( $"state: ready={state.Ready} indexed={state.IndexedVectors} total={state.TotalVectors} detail={state.Detail}" );
      Assert.False( state.Ready );
      Assert.Contains( "not a vec0", state.Detail );
   }

   /// <summary>
   /// Removes the throwaway folder with its database files.
   /// </summary>
   public void Dispose()
   {
      if( Directory.Exists( _folder ) && Path.GetFileName( _folder ).StartsWith( "gvbbench-sqlitevec-ready-", StringComparison.Ordinal ) )
      {
         Directory.Delete( _folder, recursive: true );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Opens a database file directly, with no extension loaded, and swaps the table for a plain one.
   /// Dropping a vec0 table needs the extension, so the file is rewritten from scratch instead:
   /// the old file is deleted and an ordinary table with the same name and an embedding column of
   /// the same declared type is created.
   /// </summary>
   /// <param name="file">Database file in the throwaway folder.</param>
   /// <param name="table">Table name.</param>
   private static void ReplaceWithPlainTable( string file, string table )
   {
      foreach( string suffix in new[] { string.Empty, "-wal", "-shm" } )
      {
         if( File.Exists( file + suffix ) )
         {
            File.Delete( file + suffix );
         }
      }

      using var connection = new SqliteConnection( new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ConnectionString );
      connection.Open();
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = $"CREATE TABLE \"{table}\" ( chunk_id text primary key, embedding float[{GroupCReadinessData.DIMENSION}], doc_key text );";
      command.ExecuteNonQuery();
   }

   #endregion Private Methods
}
