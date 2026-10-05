using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The start retry on the real Docker of this box, through the same
/// <c>ComposeRunner.UpAsync</c> the benchmark calls: a throwaway container that exits with 134 on its first
/// start and then stays up, one that stays up but never becomes healthy, and one that exits every time.
/// Each project is named gvbbench-composetest-*, built from an image already on the box, and removed in a
/// finally; no engine's container, data folder or port is touched.
/// Why live: only Docker can show that "compose up --wait" really reports a container that exited, that
/// "ps --status exited --format" prints what the retry reads, that the clean-up between attempts removes the
/// container and leaves a bind-mounted folder alone, and that the second container really starts.
/// Needs passwordless sudo and the mariadb:11.8.9 image (used only as a shell).
/// </summary>
[Trait( "Category", "Live" )]
public class ComposeLiveTests
{
   #region Data Members

   private static readonly TimeSpan LIMIT = TimeSpan.FromMinutes( 5 );
   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public ComposeLiveTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// A container that exits with 134 on its first start (and writes a file into its bind-mounted folder), then
   /// stays up: the start succeeds on the second attempt, the line names both attempts with the panic line from
   /// the container's log, the file is still in the folder (the clean-up did not touch it) and the container runs.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Live_ARealContainerThatExitsOnItsFirstStartIsStartedOnTheSecondAttempt()
   {
      string[] lines = await Run( "RecoversAsync" );
      Assert.StartsWith( "line|started compose.yaml with every container created on CPUs 0: gvbbench-composetest-recover cpuset 0 (read back from docker inspect); start attempts: "
         + "attempt 1 of 3: gvbbench-composetest-recover exited with code 134 during start-up (compose said: ", lines[0] );
      Assert.Contains( "(compose said: container gvbbench-composetest-recover exited (134); log: panic: etcdserver: leader changed); removed the failed container only (docker compose down -v), the data folder was not touched; attempt 2 of 3: healthy after ", lines[0] );
      Assert.Equal( new[] { "flag|True", "running|True" }, lines.Skip( 1 ).ToArray() );
   }

   /// <summary>
   /// A container that stays up but whose health check never passes did not exit: one attempt, compose's own
   /// words, no attempt text in the error, and no data lost.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Live_ARealUnhealthyContainerThatKeepsRunningIsNotRetried()
   {
      string[] lines = await Run( "UnhealthyIsNotRetriedAsync" );
      Assert.StartsWith( "error|InvalidOperationException: Could not start compose.yaml: ", lines[0] );
      Assert.Contains( "unhealthy", lines[0] );
      Assert.DoesNotContain( "attempt", lines[0] );
      Assert.Equal( new[] { "flag|True", "running|True" }, lines.Skip( 1 ).ToArray() );
   }

   /// <summary>
   /// A container that exits with 134 every time: three attempts, all three in the error, then it gives up
   /// ("no attempts left after 3"), and the data folder still holds the file.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Live_ARealContainerThatAlwaysExitsGivesUpAfterThreeAttempts()
   {
      string[] lines = await Run( "ExitsEveryTimeAsync" );
      Assert.StartsWith( "error|InvalidOperationException: Could not start compose.yaml: attempt 1 of 3: gvbbench-composetest-always exited with code 134 during start-up (compose said: container gvbbench-composetest-always exited (134); log: panic: always)", lines[0] );
      Assert.Contains( " | attempt 2 of 3: ", lines[0] );
      Assert.Contains( " | attempt 3 of 3: ", lines[0] );
      Assert.EndsWith( "no attempts left after 3", lines[0] );
      Assert.Equal( 2, lines[0].Split( "removed the failed container only" ).Length - 1 );
      Assert.Equal( "flag|True", lines[1] );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs one live scenario and prints its lines as evidence.
   /// </summary>
   /// <param name="method">Scenario method name.</param>
   /// <returns>The scenario's lines.</returns>
   private async Task<string[]> Run( string method )
   {
      string scratch = Environment.GetEnvironmentVariable( "GVB_TEST_SCRATCH" ) ?? Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "gvb-work", "compose-live" );
      Directory.CreateDirectory( scratch );
      string[] lines = await ComposeHarness.CallLiveAsync( method, LIMIT, scratch.TrimEnd( '/' ) );
      foreach( string line in lines )
      {
         _output.WriteLine( line );
      }

      return lines;
   }

   #endregion Private Methods
}
