using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The benchmark's "mariadb" target against the real Docker on this box: its own container
/// gvbbench-mariadb is started on the engine CPUs (and stopped again when this test started it), a
/// throwaway table is loaded and searched through the target, and the daily container gvb-mariadb is
/// read before and after.
/// What it proves, each from a source other than the code under test: the target's recorded address is
/// the benchmark container's own (docker inspect), the kernel's socket table (sudo ss) shows this
/// process connected to that address and to nothing of the daily container, every thread of the
/// container's server may run only on the engine CPUs (/proc), and the daily container is the same
/// container with the same start time, restart count and cpuset, with no Docker event of its own and no
/// more new connections than its health check explains.
/// Every table it creates is gvb.gvb_gvbbench_mariabench* in the benchmark's container and is dropped
/// at the end; the daily container's database is never read or written (only its status counters).
/// </summary>
[Trait( "Category", "Live" )]
public class MariaBenchLiveTests
{
   #region Data Members

   private static readonly TimeSpan LIMIT = TimeSpan.FromMinutes( 13 );
   private static readonly Lazy<Task<dynamic>> LIVE = new( () => MariaBenchHarness.CallAsync( "LiveAsync", LIMIT, TargetsHarness.RepoRoot() ) );

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public MariaBenchLiveTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The target runs on the benchmark's own container, created on the engine CPUs, reached on its own
   /// address and never through docker-proxy, and its searches complete.
   /// </summary>
   [Fact]
   public async Task Target_RunsInItsOwnContainerOnTheEngineCpus_WithoutDockerProxy()
   {
      string[] lines = await Evidence();
      string Line( string prefix ) => lines.Single( l => l.StartsWith( prefix, StringComparison.Ordinal ) );
      Assert.Matches( @"^connection\|gvbbench-mariadb \([0-9a-f]{12}\) on network gvbbench-mariadb_default at [\d.]+:3306, not through docker-proxy 127\.0\.0\.1:13306$", Line( "connection|" ) );
      string container = Line( "container|" );
      Assert.Contains( "cpuset=2-3,6-7 ", container );
      Assert.Contains( "image=mariadb:11.8.9 ", container );
      Assert.Contains( "\"3306/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"13306\"}]", container );
      string[] threads = Line( "threads|" ).Split( '|' );
      Assert.True( int.Parse( threads[1] ) >= 5, Line( "threads|" ) );
      Assert.Equal( "CPUs allowed 2-3,6-7", threads[2] );

      string[] samples = lines.Where( l => l.StartsWith( "sockets sample ", StringComparison.Ordinal ) ).ToArray();
      Assert.Equal( 3, samples.Length );
      Assert.All( samples, l => Assert.Matches( @"\|to the benchmark container [\d.]+:3306 [1-9]\d*\|to its published port 127\.0\.0\.1:13306 0\|to the daily container 172\.18\.0\.2:3306 0\|to the daily's published port 127\.0\.0\.1:3306 0$", l ) );
      Assert.Matches( @"^searches\|completed [1-9]\d{2,}\|failed 0$", Line( "searches|" ) );
      Assert.Contains( lines, l => l.StartsWith( "cleanup|dropped gvb.gvb_gvbbench_mariabench", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The daily container is the same container before and after (id, start time, restart count, cpuset
   /// all as they were: it was not moved or restarted), Docker recorded no update, restart, stop or kill
   /// event for it, and the connections its server accepted are within what its own health check makes.
   /// </summary>
   [Fact]
   public async Task DailyContainer_IsUntouchedWhileTheBenchmarkContainerRuns()
   {
      string[] lines = await Evidence();
      string Line( string prefix ) => lines.Single( l => l.StartsWith( prefix, StringComparison.Ordinal ) );
      Assert.StartsWith( "daily same container, start time, restarts, cpuset|yes (", Line( "daily same container" ) );
      Assert.Equal( "daily events|none", Line( "daily events|" ) );
      Assert.StartsWith( "daily connections|within the health check's share|", Line( "daily connections|" ) );
      Assert.Matches( @"cpuset=\S+ ", Line( "daily before|" ) );
      Assert.Equal( Regex.Match( Line( "daily before|" ), @"cpuset=\S+" ).Value, Regex.Match( Line( "daily after|" ), @"cpuset=\S+" ).Value );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs the live scenario once for both tests and prints every line as evidence.
   /// </summary>
   /// <returns>The lines.</returns>
   private async Task<string[]> Evidence()
   {
      string[] lines = await LIVE.Value;
      foreach( string line in lines )
      {
         _output.WriteLine( line );
      }

      return lines;
   }

   #endregion Private Methods
}
