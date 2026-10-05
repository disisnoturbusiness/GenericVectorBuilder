using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Sinks;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// The concurrent-search tests (<see cref="EmbeddedSinkConcurrencyTests"/>) run against SQLite
/// with the sqlite-vec extension, in a throwaway folder, plus a check of the one thing sqlite-vec
/// concurrency depends on: the file is in WAL mode.
/// </summary>
[Trait( "Category", "Live" )]
[Collection( "EmbeddedConcurrency" )]
public sealed class SqliteVecConcurrencyTests : EmbeddedSinkConcurrencyTests
{
   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public SqliteVecConcurrencyTests( ITestOutputHelper output ) : base( output, "svec" )
   {
   }

   #endregion Constructor

   #region Overrides

   /// <inheritdoc />
   protected override ISink CreateSink( string folder, bool serialized, int maxSearchConnections, int waitSeconds )
   {
      return new SqliteVecSink( new SqliteVecSinkOptions
      {
         DirectoryPath = folder,
         SerializeSearches = serialized,
         MaxSearchConnections = maxSearchConnections,
         ConnectionWaitSeconds = waitSeconds
      } );
   }

   /// <inheritdoc />
   protected override int ConnectionsOpened( ISink sink, string collection )
   {
      return ( (SqliteVecSink)sink ).SearchConnectionsOpened( collection );
   }

   /// <inheritdoc />
   protected override int LongUpsertCount => 20000;

   /// <inheritdoc />
   protected override string FileOf( string folder, string collection )
   {
      return Path.Combine( folder, $"gvb_{collection}.sqlite" );
   }

   #endregion Overrides

   #region Public Methods

   /// <summary>
   /// SQLite lets readers run beside a writer only in WAL mode, so the file the sink creates must
   /// report journal_mode wal to a brand new connection that did not set it.
   /// </summary>
   [Fact]
   public async Task CollectionFile_IsInWalMode_ForAnyConnection()
   {
      string collection = $"gvbbench_svec_conc_{Guid.NewGuid().ToString( "N" )[..8]}";
      using var sink = new SqliteVecSink( new SqliteVecSinkOptions { DirectoryPath = Folder } );
      try
      {
         await sink.EnsureCollectionAsync( collection, 16, CancellationToken.None );
         using var raw = new SqliteConnection( new SqliteConnectionStringBuilder { DataSource = FileOf( Folder, collection ), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString );
         raw.Open();
         using SqliteCommand command = raw.CreateCommand();
         command.CommandText = "PRAGMA journal_mode;";
         Assert.Equal( "wal", command.ExecuteScalar() );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods
}
