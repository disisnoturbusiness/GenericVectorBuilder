namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The rules behind the benchmark's own SQL Server and Qdrant containers and the CPU set every engine
/// container is started with, checked without Docker or a server: CPU lists, the compose override,
/// the images the compose files pin, what docker inspect is read for, the engine text each container
/// target reports (with its warnings), how a container process is told from a native one, and the
/// target names the factory offers.
/// Why these get tests: a cpuset that is malformed or silently different, or an engine that sized its
/// thread pools for more CPUs than it may use, makes every number from that engine incomparable, and
/// none of that shows in a latency figure.
/// </summary>
public class TargetsPinnedTests
{
   #region Data Members

   // Built to match "docker inspect gvb-mssql" on this box (2026-10-04); the environment values are
   // replaced by "x" so no password from secrets.env is ever copied into a test.
   private const string MSSQL_INSPECT = """[{"Id":"4f2d0c6a1b7e9d3c5a8b0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d","Name":"/gvb-mssql","State":{"Status":"running","Running":true},"HostConfig":{"CpusetCpus":"2-3,6-7","Memory":8589934592},"Config":{"Image":"mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04@sha256:2b5b581621126574f3d1f75e78d3eebe8d05aedb59ad0cfdf9aa42cb0634d726","Env":["MSSQL_SA_PASSWORD=x","ACCEPT_EULA=x","MSSQL_PID=x","PATH=x"]},"NetworkSettings":{"Ports":{"1433/tcp":[{"HostIp":"127.0.0.1","HostPort":"14330"}]},"Networks":{"gvb-mssql_default":{"IPAddress":"172.24.0.2"}}}}]""";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// CPU lists read the way the kernel and Docker write them; a descending range, a space, a trailing
   /// comma, letters and an empty text are all refused; two spellings of one set compare equal.
   /// </summary>
   [Fact]
   public void CpuSets_ReadStrictlyAndCompareBySet()
   {
      string[] lines = TargetsHarness.CallPinned( "CpuSets", (object)new[] { "2-3,6-7", "0", "1,5", "3-2", "2, 3", "2,", "a-b", "", "0-3,2" } );
      Assert.Equal( new[]
      {
         "2-3,6-7|True|2,3,6,7|4",
         "0|True|0|1",
         "1,5|True|1,5|2",
         "3-2|False|-|-",
         "2, 3|False|-|-",
         "2,|False|-|-",
         "a-b|False|-|-",
         "|False|-|-",
         "0-3,2|True|0,1,2,3|4",
      }, lines );
      Assert.True( (bool)TargetsHarness.CallPinned( "SameCpus", "2-3,6-7", "2,3,6,7" ) );
      Assert.False( (bool)TargetsHarness.CallPinned( "SameCpus", "2-3,6-7", "2-3" ) );
      Assert.False( (bool)TargetsHarness.CallPinned( "SameCpus", "", "" ) );
      Assert.False( (bool)TargetsHarness.CallPinned( "SameCpus", null, "1" ) );
   }

   /// <summary>
   /// The override the benchmark writes for a start gives every service of the file the cpuset, and
   /// nothing else; a malformed cpuset or an odd service name is refused before any file is written.
   /// </summary>
   [Fact]
   public void Override_PinsEveryServiceAndRefusesWhatComposeMightMisread()
   {
      string yaml = TargetsHarness.CallPinned( "Override", new[] { "oracle-seed", "oracle" }, "2-3,6-7" );
      Assert.Equal( "# Written by the benchmark for one start: every container of the engine gets the engine CPUs from its creation.\nservices:\n"
         + "  oracle-seed:\n    cpuset: \"2-3,6-7\"\n  oracle:\n    cpuset: \"2-3,6-7\"\n", yaml );
      Assert.StartsWith( "error|ArgumentException: '2-3,6-7\"\n  x' is not a CPU list", (string)TargetsHarness.CallPinned( "Override", new[] { "a" }, "2-3,6-7\"\n  x" ) );
      Assert.StartsWith( "error|ArgumentException: Unexpected service names", (string)TargetsHarness.CallPinned( "Override", new[] { "a: {}" }, "1" ) );
      Assert.StartsWith( "error|ArgumentException: Unexpected service names", (string)TargetsHarness.CallPinned( "Override", Array.Empty<string>(), "1" ) );
   }

   /// <summary>
   /// The two new compose files pin their images by digest to the builds of the native servers
   /// (SQL Server 2025 RTM-CU9 17.0.5005.3, Qdrant 1.17.0), publish on 127.0.0.1 only, start on the
   /// engine CPUs by default and carry a memory limit and a health check. SQL Server runs under
   /// Docker's init with sqlservr replacing the shell, so a stop is its own clean shutdown (the image's
   /// launcher swallowed the stop signal: 60 s, then a kill).
   /// </summary>
   [Fact]
   public void ComposeFiles_PinTheNativeBuildsAndBindOnlyToLoopback()
   {
      string root = TargetsHarness.RepoRoot();
      Assert.Equal( "mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04@sha256:2b5b581621126574f3d1f75e78d3eebe8d05aedb59ad0cfdf9aa42cb0634d726", (string)TargetsHarness.CallPinned( "ImageOf", root, "mssql.compose.yaml" ) );
      Assert.Equal( "qdrant/qdrant:v1.17.0@sha256:f1c7272cdac52b38c1a0e89313922d940ba50afd90d593a1605dbbc214e66ffb", (string)TargetsHarness.CallPinned( "ImageOf", root, "qdrant.compose.yaml" ) );
      Assert.StartsWith( "not read from", (string)TargetsHarness.CallPinned( "ImageOf", root, "nosuch.compose.yaml" ) );
      foreach( string file in new[] { "mssql.compose.yaml", "qdrant.compose.yaml" } )
      {
         string[] lines = File.ReadAllLines( Path.Combine( root, "deploy", "engines", file ) ).Where( l => !l.TrimStart().StartsWith( '#' ) ).ToArray();
         string[] ports = lines.Where( l => l.TrimStart().StartsWith( "- \"", StringComparison.Ordinal ) && l.Contains( ':' ) && !l.Contains( '/' ) ).ToArray();
         Assert.NotEmpty( ports );
         Assert.All( ports, p => Assert.StartsWith( "- \"127.0.0.1:", p.Trim() ) );
         Assert.Contains( "    cpuset: \"${GVB_ENGINE_CPUS:-2-3,6-7}\"", lines );
         Assert.Contains( lines, l => l.StartsWith( "    mem_limit: ", StringComparison.Ordinal ) );
         Assert.Contains( lines, l => l.StartsWith( "    healthcheck:", StringComparison.Ordinal ) );
         Assert.Contains( lines, l => l.StartsWith( "name: gvb-", StringComparison.Ordinal ) );
      }

      string mssql = File.ReadAllText( Path.Combine( root, "deploy", "engines", "mssql.compose.yaml" ) );
      Assert.Contains( "\"127.0.0.1:14330:1433\"", mssql );
      Assert.Contains( "- /home/dan/gvb-data/engines/secrets.env", mssql );
      Assert.Contains( "$$MSSQL_SA_PASSWORD", mssql );
      Assert.Contains( "\n    init: true\n", mssql );
      Assert.Contains( "exec /opt/mssql/bin/sqlservr\"]", mssql );
      string qdrant = File.ReadAllText( Path.Combine( root, "deploy", "engines", "qdrant.compose.yaml" ) );
      Assert.Contains( "\"127.0.0.1:16333:6333\"", qdrant );
      Assert.Contains( "\"127.0.0.1:16334:6334\"", qdrant );
   }

   /// <summary>
   /// docker inspect is read for the container's cpuset, its image and the NAMES of its environment
   /// variables; the values (passwords among them) are dropped on reading.
   /// </summary>
   [Fact]
   public void Inspect_ReadsCpusetImageAndEnvironmentNamesOnly()
   {
      string[] lines = TargetsHarness.CallPinned( "InspectExtras", MSSQL_INSPECT );
      Assert.Equal( new[]
      {
         "cpuset|2-3,6-7",
         "image|mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04@sha256:2b5b581621126574f3d1f75e78d3eebe8d05aedb59ad0cfdf9aa42cb0634d726",
         "env|ACCEPT_EULA,MSSQL_PID,MSSQL_SA_PASSWORD,PATH",
      }, lines );
      string[] bare = TargetsHarness.CallPinned( "InspectExtras", """[{"Id":"x","Name":"/c","State":{"Status":"running","Running":true},"NetworkSettings":{}}]""" );
      Assert.Equal( new[] { "cpuset|", "image|", "env|" }, bare );
   }

   /// <summary>
   /// The SQL Server container target names its build, edition, image, cpuset and the CPUs and schedulers
   /// SQL Server itself reports, and warns when the schedulers do not match the CPUs it may use (the
   /// sign of a container moved to fewer CPUs after it started) or when it has no cpuset.
   /// </summary>
   [Fact]
   public void SqlEngineText_StatesWhatTheServerSeesAndWarnsOnAMismatch()
   {
      string fine = TargetsHarness.CallPinned( "SqlEngineText", 4, 4, "2-3,6-7" );
      Assert.Equal( "Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64), Enterprise Developer Edition (64-bit), in container gvb-mssql "
         + "(image mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04@sha256:x; cpuset 2-3,6-7 from its creation; SQL Server counts 4 CPU(s) and runs 4 visible scheduler(s), affinity AUTO)", fine );
      Assert.Contains( "WARNING: 8 schedulers for 4 allowed CPUs", (string)TargetsHarness.CallPinned( "SqlEngineText", 8, 8, "2-3,6-7" ) );
      Assert.Contains( "NO cpuset, every CPU", (string)TargetsHarness.CallPinned( "SqlEngineText", 8, 8, "" ) );
      Assert.Contains( "cpuset not read from Docker", (string)TargetsHarness.CallPinned( "SqlEngineText", 4, 4, null ) );
   }

   /// <summary>
   /// The Qdrant container target names its version, image, cpuset and search-thread count, and warns
   /// when Qdrant runs more search threads than one fewer than its CPUs (pools sized for the host).
   /// </summary>
   [Fact]
   public void QdrantEngineText_CountsSearchThreadsAndWarnsOnPoolsSizedForTheHost()
   {
      Assert.Equal( "Qdrant 1.17.0 in container gvb-qdrant (image qdrant/qdrant:v1.17.0@sha256:x; cpuset 2-3,6-7 from its creation; 3 search thread(s))", (string)TargetsHarness.CallPinned( "QdrantEngineText", 3, "2-3,6-7" ) );
      Assert.Contains( "WARNING: 7 search threads for 4 allowed CPUs", (string)TargetsHarness.CallPinned( "QdrantEngineText", 7, "2-3,6-7" ) );
      Assert.DoesNotContain( "WARNING", (string)TargetsHarness.CallPinned( "QdrantEngineText", 1, "3" ) );
      Assert.Contains( "search threads not counted", (string)TargetsHarness.CallPinned( "QdrantEngineText", null, "2-3,6-7" ) );
   }

   /// <summary>
   /// A process in a Docker container (cgroup v2 scope, cgroup v1 path, containerd) is told from a native
   /// systemd service, so the native comparison targets never read a container's process.
   /// </summary>
   [Fact]
   public void HostProcess_TellsContainerCgroupsFromServices()
   {
      Assert.True( (bool)TargetsHarness.CallPinned( "IsContainer", "0::/system.slice/docker-6865232ec936a3d5640b7855e9a72dcf01d8d5cb475e6f02735464c72decfbbd.scope" ) );
      Assert.True( (bool)TargetsHarness.CallPinned( "IsContainer", "12:cpuset:/docker/6865232ec936\n0::/" ) );
      Assert.True( (bool)TargetsHarness.CallPinned( "IsContainer", "0::/system.slice/containerd.service/kubepods-x" ) );
      Assert.False( (bool)TargetsHarness.CallPinned( "IsContainer", "0::/system.slice/qdrant.service" ) );
      Assert.False( (bool)TargetsHarness.CallPinned( "IsContainer", "0::/system.slice/mssql-server.service" ) );
   }

   /// <summary>
   /// A container's SQL Server without an mssql.conf is described as running with SQL Server's defaults,
   /// not as "unknown".
   /// </summary>
   [Fact]
   public void SqlDurability_AbsentContainerConfMeansDefaults()
   {
      string text = TargetsHarness.CallPinned( "SqlDurabilityWithoutConf" );
      Assert.Contains( "(/var/opt/mssql/mssql.conf) does not exist, so it sets: nothing; it has no [control] or [traceflag] entry", text );
      Assert.DoesNotContain( "could not be read", text );
   }

   /// <summary>
   /// The default target list holds the four container targets first and never the native comparison
   /// targets, which are offered by name, refused before the server reads like the built-in ones, and
   /// listed in the message for an unknown name.
   /// </summary>
   [Fact]
   public async Task Factory_OffersNativeTargetsOnlyByName()
   {
      string[] lines = await TargetsHarness.CallPinnedAsync( "FactoryNamesAsync", TimeSpan.FromMinutes( 2 ), TargetsHarness.RepoRoot() );
      string Line( string prefix ) => lines.Single( l => l.StartsWith( prefix, StringComparison.Ordinal ) );
      Assert.Equal( "names|sql,sql-diskann,qdrant,qdrant-hnsw", Line( "names|" ) );
      Assert.Equal( "names include native|False", Line( "names include native|" ) );
      Assert.Equal( "comparison|sql-native,qdrant-native", Line( "comparison|" ) );
      Assert.Contains( "InvalidOperationException: Target 'sql-native' was requested before InitializeAsync finished", Line( "sql-native before" ) );
      Assert.Contains( "InvalidOperationException: Target 'qdrant-native' was requested before InitializeAsync finished", Line( "qdrant-native before" ) );
      Assert.Contains( "ArgumentException: Unknown target 'nosuch'.", Line( "unknown|" ) );
      Assert.Contains( "native comparison targets: sql-native, qdrant-native.", Line( "unknown|" ) );
   }

   #endregion Public Methods
}
