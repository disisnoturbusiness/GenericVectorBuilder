namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// How the benchmark finds a compose-hosted engine's container and connects to its own address
/// instead of the published 127.0.0.1 port that docker-proxy serves, checked without starting
/// anything: "docker inspect" text (real captures of gvb-mariadb and of the benchmark's gvbbench-mariadb, the rest built to match what
/// Docker prints) and the repository's own compose files stand in for Docker.
/// Why this matters: SQL Server and Qdrant run on this host and have no proxy hop, so a container
/// engine measured through docker-proxy pays for a hop they do not, and its latency and throughput
/// look worse than the engine is. The live proof that no docker-proxy socket carries a search is in
/// <see cref="TargetsLiveTests"/>.
/// Why the sources are compiled here with Roslyn: see <see cref="TargetsTests"/>.
/// </summary>
public class TargetsContainerTests
{
   #region Data Members

   // Trimmed from "docker inspect gvb-mariadb" on this box (the fields the benchmark reads, plus a few it ignores).
   private const string REAL_INSPECT = """[{"Id":"b115140380d05c5d4ab5d4e123b77af39bb22c23649fa35e3789e06bf0c3fbd0","Name":"/gvb-mariadb","State":{"Status":"running","Running":true,"Paused":false,"Restarting":false,"Dead":false,"ExitCode":0,"StartedAt":"2026-10-04T14:18:30.610916687Z","Health":{"Status":"healthy"}},"NetworkSettings":{"Ports":{"3306/tcp":[{"HostIp":"127.0.0.1","HostPort":"3306"}]},"Networks":{"gvb-mariadb_default":{"NetworkID":"fb3abb8901cdc12f2541614d0069535593bfc293a15ac1ea61fc39b6f5743fbc","Gateway":"172.18.0.1","IPAddress":"172.18.0.2","MacAddress":"8e:d4:fe:2e:81:6f"}}}}]""";

   // Trimmed from "docker inspect gvbbench-mariadb" on this box (2026-10-05), the benchmark's own MariaDB container
   // that the "mariadb" target binds to; the environment values are replaced by "x".
   private const string BENCH_INSPECT = """[{"Id":"8cefe0e722eddce51fa5e0fce4af968ea596214aab41f77fcda81c5d4bca4882","Name":"/gvbbench-mariadb","State":{"Status":"running","Running":true,"Paused":false,"Restarting":false,"Dead":false,"ExitCode":0,"StartedAt":"2026-10-05T01:20:59.991216637Z","Health":{"Status":"healthy"}},"HostConfig":{"CpusetCpus":"2-3,6-7","Memory":8589934592},"Config":{"Image":"mariadb:11.8.9","Env":["MARIADB_ROOT_PASSWORD=x","PATH=x"]},"NetworkSettings":{"Ports":{"3306/tcp":[{"HostIp":"127.0.0.1","HostPort":"13306"}]},"Networks":{"gvbbench-mariadb_default":{"NetworkID":"47a9006ed9604913dcdb9ed791fa1baa32b57215fad69944dd07022b8acd991b","Gateway":"172.24.0.1","IPAddress":"172.24.0.2","MacAddress":"36:50:ae:20:f6:e9"}}}}]""";

   // Captured from "sudo ss -tnpH state established" on this box while a client held one connection through
   // docker-proxy (127.0.0.1:3306, first three lines) and another straight to the container (last line).
   private const string SS_CAPTURE = """
      94     0                                      127.0.0.1:42202                                 127.0.0.1:3306  users:(("2.1.284",pid=936713,fd=3),("bash",pid=936712,fd=3))
      0      0                                     172.18.0.1:50912                                172.18.0.2:3306  users:(("docker-proxy",pid=430007,fd=4))
      0      0                                      127.0.0.1:3306                                  127.0.0.1:42202 users:(("docker-proxy",pid=430007,fd=3))
      94     0                                     172.18.0.1:50918                                172.18.0.2:3306  users:(("2.1.284",pid=936743,fd=4),("bash",pid=936742,fd=4))
      0      0                                    [::ffff:127.0.0.1]:3306                         [::ffff:127.0.0.1]:51000 users:(("docker-proxy",pid=430007,fd=9))
      """;

   private static readonly TimeSpan LIMIT = TimeSpan.FromMinutes( 3 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// A real "docker inspect" capture reads as the container's name, short id, state, its address on
   /// its network and the published port: host 3306 is container port 3306 on 127.0.0.1.
   /// </summary>
   [Fact]
   public void Inspect_ReadsARealDockerCapture()
   {
      string[] lines = TargetsHarness.CallContainer( "ParseInspect", REAL_INSPECT );
      Assert.Equal( new[]
      {
         "container|gvb-mariadb|b115140380d0|running|True",
         "network|gvb-mariadb_default|172.18.0.2",
         "port|3306|tcp|127.0.0.1|3306",
      }, lines );
   }

   /// <summary>
   /// Text that is not an inspect result stops with a plain message and never becomes a guessed
   /// address; a container on two networks gives the same first network every run (sorted by
   /// name); a network with no address is skipped; an exposed but unpublished port is not a published port.
   /// </summary>
   [Fact]
   public void Inspect_RefusesWhatItDoesNotUnderstand_AndIsDeterministic()
   {
      foreach( string bad in new[] { "not json", "{}", "[]", "[{\"Id\":\"x\"}]" } )
      {
         string[] lines = TargetsHarness.CallContainer( "ParseInspect", bad );
         Assert.Single( lines );
         Assert.StartsWith( "error|docker inspect did not return a container description", lines[0] );
      }

      string two = TargetsHarness.CallContainer( "InspectJson", "gvb-x", "running", new[] { "zeta=10.0.0.9", "alpha=10.0.0.3", "none=" }, new[] { "9201:9200" } );
      string[] facts = TargetsHarness.CallContainer( "ParseInspect", two );
      Assert.Equal( new[] { "network|alpha|10.0.0.3", "network|zeta|10.0.0.9" }, facts.Where( l => l.StartsWith( "network|", StringComparison.Ordinal ) ) );
      Assert.Contains( "port|9200|tcp|127.0.0.1|9201", facts );
   }

   /// <summary>
   /// A sink's default address (127.0.0.1 and the published host port) becomes the container's
   /// address and the port inside the container, also when the two ports differ (OpenSearch publishes
   /// 9201 for 9200), and keeps a path. A host port the container does not publish is refused with
   /// what it does publish, so a wrong default can never silently land on another port.
   /// </summary>
   [Fact]
   public void Router_MapsAHostPortToTheContainerPort()
   {
      Assert.Equal( "ok|http://172.18.0.2:3306", TargetsHarness.CallContainer( "RouterUrl", REAL_INSPECT, "gvb-mariadb", "http://127.0.0.1:3306" ) );
      string opensearch = TargetsHarness.CallContainer( "InspectJson", "gvb-opensearch", "running", new[] { "gvb-opensearch_default=172.19.0.3" }, new[] { "9201:9200" } );
      Assert.Equal( "ok|http://172.19.0.3:9200", TargetsHarness.CallContainer( "RouterUrl", opensearch, "gvb-opensearch", "http://127.0.0.1:9201" ) );
      Assert.Equal( "ok|http://172.19.0.3:9200/base?x=1", TargetsHarness.CallContainer( "RouterUrl", opensearch, "gvb-opensearch", "http://127.0.0.1:9201/base?x=1" ) );
      Assert.Equal( "ok|https://172.19.0.3:9200", TargetsHarness.CallContainer( "RouterUrl", opensearch, "gvb-opensearch", "https://localhost:9201" ) );

      string wrongPort = TargetsHarness.CallContainer( "RouterUrl", opensearch, "gvb-opensearch", "http://127.0.0.1:9200" );
      Assert.StartsWith( "error|Container gvb-opensearch does not publish host port 9200", wrongPort );
      Assert.Contains( "published: 127.0.0.1:9201 to 9200/tcp", wrongPort );
      Assert.StartsWith( "error|No facts for container gvb-other", (string)TargetsHarness.CallContainer( "RouterUrl", opensearch, "gvb-other", "http://127.0.0.1:9201" ) );
      string noAddress = TargetsHarness.CallContainer( "InspectJson", "gvb-x", "running", new[] { "net=" }, new[] { "1:1" } );
      Assert.StartsWith( "error|Container gvb-x has no address on any Docker network", (string)TargetsHarness.CallContainer( "RouterUrl", noAddress, "gvb-x", "http://127.0.0.1:1" ) );
   }

   /// <summary>
   /// A container that exists but is still coming up is asked again within the deadline and then
   /// used; one that never gets an address or never runs fails with its state in plain words; one
   /// that does not exist fails at once after a single ask, because waiting cannot create it.
   /// </summary>
   [Fact]
   public async Task Resolve_WaitsInsideADeadlineAndFailsPlainly()
   {
      string starting = TargetsHarness.CallContainer( "InspectJson", "gvb-mariadb", "created", Array.Empty<string>(), new[] { "3306:3306" } );
      Assert.Equal( "ok|3|172.18.0.2:3306", await TargetsHarness.CallContainerAsync( "ResolveAsync", LIMIT, "gvb-mariadb", new[] { starting, starting, REAL_INSPECT }, 5000, 5 ) );

      string never = await TargetsHarness.CallContainerAsync( "ResolveAsync", LIMIT, "gvb-mariadb", new[] { starting }, 80, 10 );
      Assert.StartsWith( "error|", never );
      Assert.Contains( "Container gvb-mariadb is not ready after waiting", never );
      Assert.Contains( "state created, no network address", never );
      Assert.Contains( "must be running with an address before it is measured", never );

      string exited = TargetsHarness.CallContainer( "InspectJson", "gvb-mariadb", "exited", new[] { "net=172.18.0.2" }, new[] { "3306:3306" } );
      string stopped = await TargetsHarness.CallContainerAsync( "ResolveAsync", LIMIT, "gvb-mariadb", new[] { exited }, 60, 10 );
      Assert.Contains( "state exited, address 172.18.0.2", stopped );

      Assert.Equal( "error|1|There is no container named gvb-mariadb. Start its engine first (sudo docker compose -f deploy/engines/<engine>.compose.yaml up -d, or use run-all).",
         await TargetsHarness.CallContainerAsync( "ResolveAsync", LIMIT, "gvb-mariadb", new[] { "missing" }, 5000, 5 ) );
   }

   /// <summary>
   /// Every compose-hosted engine's sink is built on its container's address and the port inside the
   /// container, three sources agreeing: the compose file (container port), the sink's own default
   /// options (the host port it would have used) and the options the built sink actually carries
   /// (read from the sink itself). No sink keeps 127.0.0.1, every address is recorded, and the
   /// engines whose host and container ports differ (OpenSearch, Vespa, Weaviate) come out on the
   /// container's port. A compose-hosted sink without a route, or a route without a sink, is a failure.
   /// </summary>
   [Fact]
   public async Task ComposeRoutes_EveryEngineConnectsToItsContainerAddress()
   {
      string[] lines = await TargetsHarness.CallContainerAsync( "ComposeRoutesAsync", LIMIT, TargetsHarness.RepoRoot() );
      foreach( string[] fields in lines.Where( l => !l.StartsWith( "coverage|", StringComparison.Ordinal ) ).Select( l => l.Split( '|' ) ) )
      {
         Assert.True( fields[1] == "ok", string.Join( " | ", fields ) );
         Assert.Matches( @"^actual (172\.30\.0\.\d+:\d+)(,172\.30\.0\.\d+:\d+)?$", fields[4] );
      }

      string Line( string name ) => lines.Single( l => l.StartsWith( name + "|", StringComparison.Ordinal ) );
      Assert.Equal( 13, lines.Count( l => !l.StartsWith( "coverage|", StringComparison.Ordinal ) ) );
      Assert.Contains( "remapped 9201->9200", Line( "opensearch" ) );
      Assert.Contains( "remapped 8090->8080", Line( "vespa" ) );
      Assert.Contains( "remapped 8085->8080", Line( "weaviate" ) );
      Assert.Contains( "remapped 13306->3306", Line( "mariadb" ) );
      Assert.Contains( "recorded 2", Line( "vespa" ) );
      Assert.Contains( "recorded 2", Line( "milvus" ) );
      Assert.Contains( "recorded 1", Line( "pgvector" ) );
      Assert.Contains( "coverage|hosted without a route|", lines );
      Assert.Contains( "coverage|routes without a hosted sink|", lines );
      Assert.Contains( "coverage|hosted|13", lines );
   }

   /// <summary>
   /// A target keeps asking nothing of Docker while it only answers questions about its engine (name,
   /// durability, index text, whether it has an index step); the first bind asks once per container, eight
   /// binds at once build one sink, the sink getter returns that same sink, and the connection is recorded.
   /// A container that does not exist fails with a plain message from the bind and the sink getter, and
   /// becomes a not-ready index state (never an exception) when the runner reads the state.
   /// </summary>
   [Fact]
   public async Task Target_BindsOnceAfterTheEngineIsUp()
   {
      string[] lines = await TargetsHarness.CallContainerAsync( "BindingAsync", LIMIT, TargetsHarness.RepoRoot(), BENCH_INSPECT );
      string Line( string prefix ) => lines.Single( l => l.StartsWith( prefix, StringComparison.Ordinal ) );
      Assert.Equal( "before binding|asks 0|connections 0|mariadb|True|compose|True|True|True", Line( "before binding" ) );
      Assert.Equal( "8 concurrent binds|distinct sinks 1|asks 1", Line( "8 concurrent binds" ) );
      Assert.Equal( "sink getter|same object True|asks 1", Line( "sink getter" ) );
      Assert.Equal( "connections|1|gvbbench-mariadb (8cefe0e722ed) on network gvbbench-mariadb_default at 172.24.0.2:3306, not through docker-proxy 127.0.0.1:13306", Line( "connections|" ) );
      Assert.Equal( "endpoints|172.24.0.2:3306", Line( "endpoints|" ) );
      Assert.StartsWith( "container missing, index state|ready False|could not read the index state: There is no container named gvbbench-mariadb", Line( "container missing, index state" ) );
      Assert.StartsWith( "container missing, sink getter|InvalidOperationException: There is no container named gvbbench-mariadb", Line( "container missing, sink getter" ) );
      Assert.StartsWith( "container missing, bind|InvalidOperationException: There is no container named gvbbench-mariadb", Line( "container missing, bind" ) );
      Assert.Equal( "container missing, name still works|mariadb", Line( "container missing, name still works" ) );
   }

   /// <summary>
   /// The built-in SQL and Qdrant targets refuse to be built before the factory has read the servers (they
   /// would carry no version and no durability text), a container engine does not need that read, and the
   /// default pair of passes is the DiskANN default search against the exact mode of the same table.
   /// </summary>
   [Fact]
   public async Task Factory_RefusesBuiltInsBeforeInitialize_AndNamesTheDefaultPair()
   {
      string[] lines = await TargetsHarness.CallContainerAsync( "FactoryGuardsAsync", LIMIT, TargetsHarness.RepoRoot() );
      foreach( string name in new[] { "sql", "sql-diskann", "qdrant", "qdrant-hnsw" } )
      {
         string line = lines.Single( l => l.StartsWith( $"{name} before InitializeAsync|", StringComparison.Ordinal ) );
         Assert.Contains( $"InvalidOperationException: Target '{name}' was requested before InitializeAsync finished", line );
         Assert.Contains( "Call InitializeAsync first.", line );
      }

      Assert.Contains( "mariadb before InitializeAsync|created|hosting compose|binding True|pair none", lines );
      Assert.Contains( "default pair|sql-diskann|default@1|exact", lines );
   }

   /// <summary>
   /// The socket table reader handles real "ss -tnpH" output: the process names and ids holding each
   /// socket, the local and peer address, and an IPv4-mapped IPv6 address. These are the lines the
   /// live test uses to say a search did or did not pass through docker-proxy.
   /// </summary>
   [Fact]
   public void Sockets_ParseRealSsOutput()
   {
      string[] rows = TargetsHarness.CallContainer( "ParseSockets", SS_CAPTURE );
      Assert.Equal( new[]
      {
         "127.0.0.1:42202|127.0.0.1:3306|2.1.284:936713,bash:936712",
         "172.18.0.1:50912|172.18.0.2:3306|docker-proxy:430007",
         "127.0.0.1:3306|127.0.0.1:42202|docker-proxy:430007",
         "172.18.0.1:50918|172.18.0.2:3306|2.1.284:936743,bash:936742",
         "127.0.0.1:3306|127.0.0.1:51000|docker-proxy:430007",
      }, rows );
   }

   #endregion Public Methods
}
