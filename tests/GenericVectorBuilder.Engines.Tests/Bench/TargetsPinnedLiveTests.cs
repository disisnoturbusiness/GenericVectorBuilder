using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The benchmark's own SQL Server and Qdrant containers and the cpuset every engine container starts
/// with, on the real Docker of this box. Starts gvb-mssql and gvb-qdrant only when they are not
/// running (and then on a CPU set that is not the compose files' default, so the override is what is
/// proven) and stops what it started; everything it writes is named gvbbench_pin* and dropped.
/// Why live: only Docker can show that compose reads the override the way it is written, that a
/// container really is created on those CPUs, and that the engines inside size themselves for them;
/// only /proc can show where SQL Server's threads actually run while it is searched.
/// </summary>
[Trait( "Category", "Live" )]
public class TargetsPinnedLiveTests
{
   #region Data Members

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public TargetsPinnedLiveTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Every engine compose file under deploy/engines, read by Docker compose together with the
   /// override the benchmark writes for a start, gives every one of its services the cpuset; nothing
   /// is started. This is what makes run-all start every engine container on the engine CPUs.
   /// </summary>
   [Fact]
   public async Task Override_EveryEngineFileGivesEveryServiceTheCpuset()
   {
      string[] lines = await TargetsHarness.CallPinnedAsync( "ComposeOverrideAsync", TimeSpan.FromMinutes( 5 ), TargetsHarness.RepoRoot(), "1,5" );
      Print( lines );
      string[] files = Directory.EnumerateFiles( Path.Combine( TargetsHarness.RepoRoot(), "deploy", "engines" ), "*.compose.yaml" ).Select( Path.GetFileName ).ToArray()!;
      Assert.Equal( 15, files.Length );
      Assert.All( files, f => Assert.Contains( lines, l => l.StartsWith( f + "|", StringComparison.Ordinal ) ) );
      Assert.DoesNotContain( lines, l => l.Contains( "|ERROR|", StringComparison.Ordinal ) );
      Assert.All( lines, l => Assert.EndsWith( "|1,5", l ) );
      Assert.Contains( "oracle.compose.yaml|oracle-seed|1,5", lines );
      Assert.Contains( "mssql.compose.yaml|mssql|1,5", lines );
   }

   /// <summary>
   /// The four built-in targets on the benchmark's own containers, end to end: each container is
   /// created on the CPU set it was started with (read back from Docker), SQL Server counts and
   /// schedules exactly those CPUs and Qdrant runs one fewer search thread than it has CPUs, every
   /// target connects to the container's own bridge address (never docker-proxy), states its build and
   /// durability from the server, loads 524 vectors and proves its index (or its scan) from the server.
   /// </summary>
   [Fact]
   public async Task Containers_StartPinnedAndEveryTargetWorksOnThem()
   {
      string[] lines = await TargetsHarness.CallPinnedAsync( "LiveContainersAsync", TimeSpan.FromMinutes( 13 ), TargetsHarness.RepoRoot() );
      Print( lines );
      string Line( string prefix ) => lines.Single( l => l.StartsWith( prefix, StringComparison.Ordinal ) );
      Assert.DoesNotContain( lines, l => l.StartsWith( "factory note|", StringComparison.Ordinal ) );
      foreach( string start in lines.Where( l => l.StartsWith( "start|", StringComparison.Ordinal ) && !l.EndsWith( "not restarted", StringComparison.Ordinal ) ) )
      {
         Assert.Contains( "with every container created on CPUs 1-2,5-6", start );
         Assert.Matches( @"gvb-(mssql|qdrant) cpuset 1-2,5-6 \(read back from docker inspect\)$", start );
      }

      Match sql = Regex.Match( Line( "sql engine|" ), @"17\.0\.5005\.3 \(X64\), Enterprise Developer Edition \(64-bit\), in container gvb-mssql \(image mcr\.microsoft\.com/mssql/server:2025-CU9-ubuntu-24\.04@sha256:2b5b5816\w+; cpuset (?<set>[\d,-]+) from its creation; SQL Server counts (?<cpus>\d) CPU\(s\) and runs (?<sched>\d) visible scheduler\(s\)" );
      Assert.True( sql.Success, Line( "sql engine|" ) );
      int allowed = CountCpus( sql.Groups["set"].Value );
      Assert.Equal( allowed.ToString(), sql.Groups["cpus"].Value );
      Assert.Equal( allowed.ToString(), sql.Groups["sched"].Value );
      Match qdrant = Regex.Match( Line( "qdrant engine|" ), @"^qdrant engine\|Qdrant 1\.17\.0 in container gvb-qdrant \(image qdrant/qdrant:v1\.17\.0@sha256:f1c7272c\w+; cpuset (?<set>[\d,-]+) from its creation; (?<threads>\d+) search thread\(s\)\)$" );
      Assert.True( qdrant.Success, Line( "qdrant engine|" ) );
      Assert.Equal( CountCpus( qdrant.Groups["set"].Value ) - 1, int.Parse( qdrant.Groups["threads"].Value ) );
      CheckTargets( lines );
   }

   /// <summary>
   /// The proof the review asked for: while eight searchers run against the "sql" container target,
   /// every thread of the container's SQL Server (the SQLPAL host threads included, which the native
   /// server's process affinity leaves on all 8 CPUs) is allowed only the container's CPUs, every
   /// sample of a thread that used CPU time shows it on one of them, and the threads did real work.
   /// Starts gvb-mssql (on 1-2,5-6) only when it is not running, and stops it again.
   /// </summary>
   [Fact]
   public async Task SqlServerThreads_StayOnTheContainerCpusUnderEightSearchers()
   {
      bool started = await EnsureMssqlAsync();
      try
      {
         string[] lines = await TargetsHarness.CallPinnedAsync( "SqlThreadsAsync", TimeSpan.FromMinutes( 6 ), TargetsHarness.RepoRoot(), 8 );
         Print( lines );
         string Line( string prefix ) => lines.Single( l => l.StartsWith( prefix, StringComparison.Ordinal ) );
         string cpuset = Regex.Match( Line( "container|" ), @"cpuset (?<set>[\d,-]+)\|" ).Groups["set"].Value;
         Assert.NotEmpty( cpuset );
         Assert.Matches( @"^sqlservr processes\|\d+,\d+$", Line( "sqlservr processes|" ) );
         Assert.Equal( $"allowed lists of every thread|{cpuset}", Line( "allowed lists of every thread|" ) );
         Assert.Equal( $"allowed lists of busy threads|{cpuset}", Line( "allowed lists of busy threads|" ) );
         Assert.Equal( "busy samples outside the cpuset|0", Line( "busy samples outside the cpuset|" ) );
         Assert.Matches( @"^burst\|8 s\|8 searchers\|[1-9]\d{2,} searches\|", Line( "burst|" ) );
         Assert.Matches( @"\|[1-9]\d* used CPU during the burst\|[1-9]\d* ticks of CPU time$", Line( "threads|" ) );
      }
      finally
      {
         if( started )
         {
            await StopMssqlAsync();
         }
      }
   }

   /// <summary>
   /// With the benchmark's Qdrant and SQL Server containers running beside the native services, the
   /// native comparison targets still find exactly one native process each, the systemd service's.
   /// </summary>
   [Fact]
   public void Native_ProcessesAreTheServicesNotTheContainers()
   {
      string[] lines = TargetsHarness.CallPinned( "NativeProcesses" );
      Print( lines );
      Assert.Single( lines, l => l.StartsWith( "qdrant|", StringComparison.Ordinal ) );
      Assert.EndsWith( "0::/system.slice/qdrant.service", lines.Single( l => l.StartsWith( "qdrant|", StringComparison.Ordinal ) ) );
      Assert.All( lines.Where( l => l.StartsWith( "sqlservr|", StringComparison.Ordinal ) ), l => Assert.EndsWith( "0::/system.slice/mssql-server.service", l ) );
      Assert.Contains( lines, l => l.StartsWith( "sqlservr|", StringComparison.Ordinal ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Checks the per-target lines of the live container scenario.
   /// </summary>
   /// <param name="lines">The scenario's lines.</param>
   private static void CheckTargets( string[] lines )
   {
      string Line( string prefix ) => lines.Single( l => l.StartsWith( prefix, StringComparison.Ordinal ) );
      Assert.Equal( "sql|hosting compose|compose mssql.compose.yaml|finisher False|pair none", Line( "sql|" ) );
      Assert.Equal( "sql-diskann|hosting compose|compose mssql.compose.yaml|finisher True|pair exact", Line( "sql-diskann|" ) );
      Assert.Equal( "qdrant|hosting compose|compose qdrant.compose.yaml|finisher False|pair none", Line( "qdrant|" ) );
      Assert.Equal( "qdrant-hnsw|hosting compose|compose qdrant.compose.yaml|finisher True|pair none", Line( "qdrant-hnsw|" ) );
      foreach( string name in new[] { "sql", "sql-diskann" } )
      {
         Assert.Matches( @"^\w[\w-]* connection\|gvb-mssql \([0-9a-f]{12}\) on network gvb-mssql_default at 172\.\d+\.\d+\.\d+:1433, not through docker-proxy 127\.0\.0\.1:14330$", Line( $"{name} connection|" ) );
         Assert.Contains( "A commit returns after its transaction-log records are written to disk", Line( $"{name} durability|" ) );
         Assert.Contains( "Container settings from its environment (names only): MSSQL_AGENT_ENABLED, MSSQL_MEMORY_LIMIT_MB, MSSQL_PID", Line( $"{name} durability|" ) );
         Assert.Matches( @"\|count 524\|top1 10 of 10\|ready True\|", Line( $"{name} load|" ) );
      }

      foreach( string name in new[] { "qdrant", "qdrant-hnsw" } )
      {
         Assert.Matches( @"^[\w-]+ connection\|gvb-qdrant \([0-9a-f]{12}\) on network gvb-qdrant_default at 172\.\d+\.\d+\.\d+:(6334|6333), not through docker-proxy 127\.0\.0\.1:1633[34]; gvb-qdrant .* at 172\.\d+\.\d+\.\d+:(6333|6334), not through docker-proxy 127\.0\.0\.1:1633[34]$", Line( $"{name} connection|" ) );
         Assert.Contains( "/qdrant/config/config.yaml in container gvb-qdrant", Line( $"{name} durability|" ) );
         Assert.Contains( "no QDRANT__ environment overrides", Line( $"{name} durability|" ) );
         Assert.Matches( @"\|count 524\|top1 (9|10) of 10\|ready True\|", Line( $"{name} load|" ) );
      }

      Assert.Contains( "DiskANN index built and used", Line( "sql-diskann state|" ) );
      Assert.Contains( "no index used, exact scan by design", Line( "sql state|" ) );
      Assert.Contains( "none scanned", Line( "qdrant-hnsw state|" ) );
      Assert.Contains( "no index used, exact scan by design", Line( "qdrant state|" ) );
      Assert.Contains( "(collection folder)", Line( "qdrant disk|" ) );
   }

   /// <summary>
   /// Starts gvb-mssql on the test CPU set when it is not running.
   /// </summary>
   /// <returns>True when this test started it (and must stop it).</returns>
   private async Task<bool> EnsureMssqlAsync()
   {
      string compose = Path.Combine( TargetsHarness.RepoRoot(), "deploy", "engines", "mssql.compose.yaml" );
      ( int code, string output ) = await RunAsync( "sudo", "-n", "docker", "compose", "-f", compose, "ps", "-q", "--status", "running", "--orphans=false" );
      Assert.Equal( 0, code );
      if( output.Trim().Length > 0 )
      {
         _output.WriteLine( "gvb-mssql was already running; not restarted" );
         return false;
      }

      string[] lines = await TargetsHarness.CallPinnedAsync( "StartAsync", TimeSpan.FromMinutes( 5 ), compose );
      Print( lines );
      return true;
   }

   /// <summary>
   /// Stops gvb-mssql (data kept).
   /// </summary>
   private async Task StopMssqlAsync()
   {
      string compose = Path.Combine( TargetsHarness.RepoRoot(), "deploy", "engines", "mssql.compose.yaml" );
      ( int code, string output ) = await RunAsync( "sudo", "-n", "docker", "compose", "-f", compose, "down" );
      _output.WriteLine( $"stopped gvb-mssql: exit {code} {output.Trim()}" );
   }

   /// <summary>
   /// Runs a command with a five-minute limit.
   /// </summary>
   /// <param name="program">Program.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>Exit code and standard output.</returns>
   private static async Task<(int Code, string Output)> RunAsync( string program, params string[] arguments )
   {
      var start = new System.Diagnostics.ProcessStartInfo( program ) { RedirectStandardOutput = true, RedirectStandardError = true };
      arguments.ToList().ForEach( start.ArgumentList.Add );
      using var process = System.Diagnostics.Process.Start( start )!;
      using var limit = new CancellationTokenSource( TimeSpan.FromMinutes( 5 ) );
      Task<string> output = process.StandardOutput.ReadToEndAsync( limit.Token );
      await process.WaitForExitAsync( limit.Token );
      return ( process.ExitCode, await output );
   }

   /// <summary>
   /// How many CPUs a CPU list such as "1-2,5-6" names.
   /// </summary>
   /// <param name="set">The list.</param>
   /// <returns>The count.</returns>
   private static int CountCpus( string set )
   {
      return set.Split( ',' ).Sum( p => p.Contains( '-' ) ? int.Parse( p.Split( '-' )[1] ) - int.Parse( p.Split( '-' )[0] ) + 1 : 1 );
   }

   /// <summary>
   /// Prints evidence lines.
   /// </summary>
   /// <param name="lines">Lines.</param>
   private void Print( IEnumerable<string> lines )
   {
      foreach( string line in lines )
      {
         _output.WriteLine( line );
      }
   }

   #endregion Private Methods
}
