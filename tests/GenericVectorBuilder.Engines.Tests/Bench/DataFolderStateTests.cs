namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The data-folder measurer (Targets/DataFolderState.cs): which folder a target gets, that the
/// container's mounts are checked against it, how du's answers are read (exit 1 with a total is retried
/// once, anything else fails loud, a hung du ends at its time limit), and what counts as an embedded
/// engine's bench file.
/// Why: a ClickHouse run's start state (4.22 GiB of its own old logs in one v7 run, 99 KiB in another)
/// went unrecorded and moved its figures; the record is only worth having when it is the folder the
/// engine really writes to and a size that was really read.
/// </summary>
public sealed class DataFolderStateTests
{
   #region Public Methods

   /// <summary>
   /// du's total is the first number of its first line; exit 0 is read at once, with one call and the limit it was given.
   /// </summary>
   [Fact]
   public void Du_ReadsTheTotalOfTheFirstLine()
   {
      string[] lines = Du( new[] { "0|7519477248\\t/home/dan/gvb-data/engines/clickhouse|" } );
      Assert.Equal( new[] { "bytes|7519477248", "call|-n du -sb " + Existing() + " @5000ms" }, lines );
   }

   /// <summary>
   /// du exits 1 when files vanished while it walked (ClickHouse and Milvus delete files as they run) but still prints the total of
   /// what it saw: that is retried once and the second total is kept, whichever of 0 and 1 it exits with.
   /// </summary>
   [Fact]
   public void Du_RetriesOnceWhenFilesVanishedAndKeepsTheSecondTotal()
   {
      string[] lines = Du( new[] { "1|100\\t/x|du: cannot access '/x/a': No such file or directory", "1|120\\t/x|du: cannot access '/x/b': No such file or directory" } );
      Assert.Equal( "bytes|120", lines[0] );
      Assert.Equal( 2, lines.Count( l => l.StartsWith( "call|" ) ) );
      string[] clean = Du( new[] { "1|100\\t/x|vanished", "0|110\\t/x|" } );
      Assert.Equal( "bytes|110", clean[0] );
   }

   /// <summary>
   /// Any other failure is an error that names the folder and the exit code: exit 2 at once with no retry, exit 1 with no total, a second
   /// failure on the retry, and an answer that holds no number. Never a zero.
   /// </summary>
   [Fact]
   public void Du_AnyOtherFailureIsAnErrorAndNeverAZero()
   {
      string[] two = Du( new[] { "2||du: permission denied" } );
      Assert.Equal( $"error|du of {Existing()} failed with exit code 2: du: permission denied", two[0] );
      Assert.Single( two, l => l.StartsWith( "call|" ) );
      Assert.StartsWith( "error|du of", Du( new[] { "1||no total at all" } )[0] );
      string[] again = Du( new[] { "1|100\\t/x|vanished", "3||broken" } );
      Assert.Equal( $"error|du of {Existing()} failed again with exit code 3 on its one retry: broken", again[0] );
      Assert.StartsWith( "error|du of", Du( new[] { "0|not a number|" } )[0] );
   }

   /// <summary>
   /// A du that does not return is ended at its limit and the error names the folder (the real shell throws a TimeoutException, which
   /// is turned into one that says which folder it was), so a stuck disk read cannot hold a run.
   /// </summary>
   [Fact]
   public void Du_AHungCallEndsAtItsTimeLimitAndNamesTheFolder()
   {
      string[] lines = (string[])TargetsHarness.CallSettings( "Du", new[] { "hang" }, 200, Existing() );
      Assert.StartsWith( $"error|du of {Existing()} did not finish: sudo -n du -sb {Existing()} did not finish within", lines[0] );
      Assert.Equal( $"call|-n du -sb {Existing()} @200ms", lines[1] );
   }

   /// <summary>
   /// A folder that does not exist holds nothing: 0 bytes, and du is not asked (an embedded engine's folder before its first run).
   /// </summary>
   [Fact]
   public void AFolderThatDoesNotExistIsZeroAndDuIsNotAsked()
   {
      string[] lines = (string[])TargetsHarness.CallSettings( "Du", new[] { "0|999\\t/x|" }, 5000, "/home/dan/gvb-work/does-not-exist-" + Guid.NewGuid().ToString( "N" ) );
      Assert.Equal( new[] { "bytes|0" }, lines );
   }

   /// <summary>
   /// The data folder of a container target is ~/gvb-data/engines/NAME with NAME from its compose file (sql and sql-diskann: mssql;
   /// the benchmark's MariaDB: mariadb-bench); DuckDB and sqlite-vec use the sinks' own default folders; a target with no container
   /// (a fake, the native servers) has none, and a compose file with an odd name is an error.
   /// </summary>
   [Fact]
   public void TheFolderComesFromTheComposeFileOrTheSinksDefault()
   {
      string root = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ) + "/gvb-data/engines";
      Assert.Equal( "folder|" + root + "/mssql", Folder( "sql-diskann", "compose", "mssql.compose.yaml", true ) );
      Assert.Equal( "folder|" + root + "/mariadb-bench", Folder( "mariadb", "compose", "mariadb-bench.compose.yaml", true ) );
      Assert.Equal( "folder|" + root + "/clickhouse", Folder( "clickhouse", "compose", "clickhouse.compose.yaml", true ) );
      Assert.Equal( "folder|" + root + "/duckdb", Folder( "duckdb", "embedded", null, false ) );
      Assert.Equal( "folder|" + root + "/sqlitevec", Folder( "sqlitevec", "embedded", null, false ) );
      Assert.Equal( "folder|none", Folder( "fake", "embedded", null, false ) );
      Assert.Equal( "folder|none", Folder( "fake", "compose", "fake.compose.yaml", false ) );
      Assert.Equal( "folder|none", Folder( "sql-native", "always-on", null, false ) );
      Assert.StartsWith( "error|The compose file 'weird.yml' of fake does not end in .compose.yaml", Folder( "fake", "compose", "weird.yml", true ) );
   }

   /// <summary>
   /// The container's mounts must agree with the folder: at least one writable bind inside it; none in the engines' data root but outside it
   /// (that would be another engine's folder, and part of the writes would go uncounted); read-only binds and writable binds outside the
   /// data root (Milvus's generated config file) are ignored; ClickHouse's two binds, Oracle's two and MongoDB's and Vespa's two writable
   /// ones inside the parent folder are accepted.
   /// </summary>
   [Fact]
   public void TheContainersMountsMustAgreeWithTheFolder()
   {
      const string ROOT = "/home/dan/gvb-data/engines";
      Assert.Equal( "ok", Verify( ROOT + "/clickhouse", ROOT, new[] { ROOT + "/clickhouse:/var/lib/clickhouse:rw", "/repo/deploy/engines/clickhouse-config/gvb-system-logs.xml:/etc/clickhouse-server/config.d/gvb-system-logs.xml:ro" } ) );
      Assert.Equal( "ok", Verify( ROOT + "/mongodb", ROOT, new[] { ROOT + "/mongodb/db:/data/db:rw", ROOT + "/mongodb/configdb:/data/configdb:rw" } ) );
      Assert.Equal( "ok", Verify( ROOT + "/vespa", ROOT, new[] { ROOT + "/vespa/var:/opt/vespa/var:rw", ROOT + "/vespa/logs:/opt/vespa/logs:rw" } ) );
      Assert.Equal( "ok", Verify( ROOT + "/milvus", ROOT, new[] { ROOT + "/milvus:/var/lib/milvus:rw", "/tmp/compose-configs/embed_etcd:/milvus/configs/embedEtcd.yaml:rw" } ) );
      Assert.StartsWith( "error|Container gvb-x can write to /home/dan/gvb-data/engines/other (mounted at /data), which is in the engines' data root but outside the data folder /home/dan/gvb-data/engines/redis",
         Verify( ROOT + "/redis", ROOT, new[] { ROOT + "/redis:/data:rw", ROOT + "/other:/data:rw" } ) );
      Assert.StartsWith( "error|Container gvb-x has no writable bind mount inside the data folder /home/dan/gvb-data/engines/redis",
         Verify( ROOT + "/redis", ROOT, new[] { ROOT + "/redis:/data:ro" } ) );
      Assert.StartsWith( "error|Container gvb-x has no writable bind mount inside", Verify( ROOT + "/redis", ROOT, Array.Empty<string>() ) );
   }

   /// <summary>
   /// The bench file of an embedded engine: DuckDB's gvb_{collection}.duckdb with its .wal, sqlite-vec's gvb_{collection}.sqlite with
   /// its -wal and -shm; other files in the folder (other collections) are not counted, and a missing bench file gives none.
   /// </summary>
   [Fact]
   public void TheBenchFileIsTheOneCollectionsFileAndItsSidecars()
   {
      string folder = Path.Combine( Path.GetTempPath(), "gvb-benchfile-" + Guid.NewGuid().ToString( "N" ) );
      try
      {
         string[] duck = (string[])TargetsHarness.CallSettings( "BenchFile", "duckdb", folder, new[] { "=1000", ".wal=500", ".tmp=77" } );
         Assert.Equal( new[] { "file|gvb_gvbbench_t.duckdb", "bytes|1500" }, duck );
         string[] lite = (string[])TargetsHarness.CallSettings( "BenchFile", "sqlitevec", folder, new[] { "=2000", "-wal=300", "-shm=32768", "-journal=5" } );
         Assert.Equal( new[] { "file|gvb_gvbbench_t.sqlite", "bytes|35068" }, lite );
         string[] absent = (string[])TargetsHarness.CallSettings( "BenchFile", "sqlitevec", folder + "-empty", Array.Empty<string>() );
         Assert.Equal( new[] { "file|gvb_gvbbench_t.sqlite", "bytes|none" }, absent );
      }
      finally
      {
         foreach( string path in new[] { folder, folder + "-empty" }.Where( Directory.Exists ) )
         {
            Directory.Delete( path, true );
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A folder that exists (the test's own binary folder).
   /// </summary>
   /// <returns>The path.</returns>
   private static string Existing()
   {
      return AppContext.BaseDirectory.TrimEnd( '/' );
   }

   /// <summary>
   /// Runs the du scenario on an existing folder with a 5 s limit.
   /// </summary>
   /// <param name="script">du answers.</param>
   /// <returns>The scenario's lines.</returns>
   private static string[] Du( string[] script )
   {
      return (string[])TargetsHarness.CallSettings( "Du", script, 5000, Existing() );
   }

   /// <summary>
   /// Runs the folder scenario.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="hosting">Hosting.</param>
   /// <param name="compose">Compose file name or null.</param>
   /// <param name="container">True for a container binding.</param>
   /// <returns>The scenario's line.</returns>
   private static string Folder( string name, string hosting, string? compose, bool container )
   {
      return (string)TargetsHarness.CallSettings( "Folder", name, hosting, compose, container );
   }

   /// <summary>
   /// Runs the mount check scenario.
   /// </summary>
   /// <param name="folder">Chosen folder.</param>
   /// <param name="root">Data root.</param>
   /// <param name="mounts">Mounts as "source:destination:rw|ro".</param>
   /// <returns>"ok" or the error line.</returns>
   private static string Verify( string folder, string root, string[] mounts )
   {
      return (string)TargetsHarness.CallSettings( "Verify", folder, root, mounts );
   }

   #endregion Private Methods
}
