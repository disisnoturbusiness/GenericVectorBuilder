using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// How an engine start is retried when a container exits while starting, checked against a fake
/// "docker compose": which commands run and in what order, what each attempt writes into the run notes,
/// when the start gives up, and that the clean-up between attempts can never remove a data folder.
/// Why these get tests: on 2026-10-05 Milvus exited during start-up ("panic: etcdserver: leader changed",
/// exit 134) and a benchmark run lost that engine's turn. A retry that also retried a start which never
/// exited would wait out the same 20-minute fault three times, and a clean-up that removed volumes could
/// delete an engine's data, so both edges are pinned here.
/// Why the sources are compiled here with Roslyn: the benchmark is a console project this test project
/// does not reference. <see cref="ComposeHarness"/> compiles the compose runner's own source files together
/// with ComposeScenarios.cs (built only in that compilation), so the code the benchmark runs is the code tested.
/// </summary>
public class ComposeTests
{
   #region Public Methods

   /// <summary>
   /// The first attempt exits with 134, the second is healthy: the notes name the container, its exit code,
   /// what compose said and the panic line from the log, say the failed container alone was removed and the
   /// data folder untouched, and the commands run in the order up, exited, logs, remove, up.
   /// </summary>
   [Fact]
   public void Retry_RecoversOnTheSecondAttemptAndWritesBothAttemptsIntoTheNotes()
   {
      string[] lines = ComposeHarness.Call( "RecoversOnSecondAttempt" );
      Assert.Equal( 3, lines.Length );
      Assert.Equal( "attempt 1 of 3: gvb-milvus exited with code 134 during start-up (compose said: container gvb-milvus exited (134); log: panic: etcdserver: leader changed); "
         + "removed the failed container only (docker compose down -v), the data folder was not touched", lines[0] );
      Assert.Equal( "attempt 2 of 3: healthy after 0 s", lines[1] );
      Assert.Equal( "calls|up:1200,exited,logs:gvb-milvus,remove,up:1200", lines[2] );
   }

   /// <summary>
   /// Three exits in a row: the start gives up with all three attempts in the error, removes the container
   /// only between attempts (twice, never after the last, which is left for the caller's own stop), and
   /// never asks for a fourth.
   /// </summary>
   [Fact]
   public void Retry_GivesUpAfterThreeExitsAndTheErrorCarriesEveryAttempt()
   {
      string[] lines = ComposeHarness.Call( "ExitsEveryTime" );
      Assert.Equal( 2, lines.Length );
      Assert.StartsWith( "error|InvalidOperationException: Could not start x.compose.yaml: attempt 1 of 3: gvb-milvus exited with code 134", lines[0] );
      Assert.Contains( " | attempt 2 of 3: gvb-milvus exited with code 134", lines[0] );
      Assert.EndsWith( " | attempt 3 of 3: gvb-milvus exited with code 134 during start-up (compose said: container gvb-milvus exited (134); log: panic: etcdserver: leader changed); no attempts left after 3", lines[0] );
      Assert.Equal( 2, lines[0].Split( "removed the failed container only" ).Length - 1 );
      Assert.Equal( "calls|up:1200,exited,logs:gvb-milvus,remove,up:1200,exited,logs:gvb-milvus,remove,up:1200,exited,logs:gvb-milvus", lines[1] );
   }

   /// <summary>
   /// A start that fails while nothing exited (an unhealthy container that keeps running, a missing image, a
   /// taken port) fails on the first attempt with compose's own words and no clean-up: a retry would only
   /// wait out the same fault again, and 20 minutes three times is an hour of silence.
   /// </summary>
   [Fact]
   public void Retry_DoesNotRetryAStartThatFailedWithoutAnExit()
   {
      string[] lines = ComposeHarness.Call( "FailsWithoutAnExit" );
      Assert.Equal( new[] { "error|InvalidOperationException: Could not start x.compose.yaml: container gvb-x is unhealthy.", "calls|up:1200,exited" }, lines );
   }

   /// <summary>
   /// A container that exited with code 0 (a one-shot seeding step that finished) is not a crash: the start
   /// fails at once with compose's own words.
   /// </summary>
   [Fact]
   public void Retry_DoesNotTreatACleanExitAsACrash()
   {
      string[] lines = ComposeHarness.Call( "CleanExitIsNotACrash" );
      Assert.Equal( new[] { "error|InvalidOperationException: Could not start x.compose.yaml: dependency failed to start: container gvb-oracle is unhealthy.", "calls|up:1200,exited" }, lines );
   }

   /// <summary>
   /// The 30-minute total is a real limit: an attempt that used 14 of a 15-minute budget leaves less than the
   /// 2-minute minimum, so the exit is reported with "no time left" and no second attempt (and no removal) happens.
   /// </summary>
   [Fact]
   public void Retry_StopsWhenTheTimeBudgetIsUsed()
   {
      string[] lines = ComposeHarness.Call( "NoTimeLeftForAnotherAttempt" );
      Assert.Equal( 2, lines.Length );
      Assert.EndsWith( "(compose said: container gvb-milvus exited (134); log: no log lines); no time left for another attempt", lines[0] );
      Assert.Equal( "calls|up:600,exited,logs:gvb-milvus", lines[1] );
   }

   /// <summary>
   /// The wait handed to compose is the smaller of the attempt limit and the time left: attempts of 8 minutes
   /// leave 22 then 14 of 30, so the waits are 20, 20 and 14 minutes, never 20 for the third.
   /// </summary>
   [Fact]
   public void Retry_CutsTheLastWaitToTheTimeLeft()
   {
      string[] lines = ComposeHarness.Call( "LastWaitIsCutToTheTimeLeft" );
      Assert.Equal( "calls|up:1200,exited,logs:gvb-milvus,remove,up:1200,exited,logs:gvb-milvus,remove,up:840", lines[^1] );
      Assert.Equal( "attempt 3 of 3: healthy after 480 s", lines[^2] );
   }

   /// <summary>
   /// When the failed container cannot be removed the start fails loudly with every attempt so far and does
   /// not start a new container on top of the old one.
   /// </summary>
   [Fact]
   public void Retry_FailsLoudWhenTheFailedContainerCannotBeRemoved()
   {
      string[] lines = ComposeHarness.Call( "RemovalFails" );
      Assert.Equal( 2, lines.Length );
      Assert.StartsWith( "error|InvalidOperationException: Could not start x.compose.yaml, and the failed container could not be removed for another attempt: attempt 1 of 3: gvb-milvus exited with code 134", lines[0] );
      Assert.EndsWith( "could not remove it: compose down said: permission denied", lines[0] );
      Assert.Equal( "calls|up:1200,exited,logs:gvb-milvus,remove", lines[1] );
   }

   /// <summary>
   /// A compose call that outlives its time limit and a cancellation both end the start at once with their
   /// own exception, and neither is followed by a query or a clean-up.
   /// </summary>
   [Fact]
   public void Retry_NeverRetriesATimeoutOrACancellation()
   {
      Assert.Equal( new[] { "error|TimeoutException: sudo docker compose up did not finish within 21 minutes.", "calls|up:1200" }, (string[])ComposeHarness.Call( "TimeoutIsNeverRetried" ) );
      Assert.Equal( new[] { "error|OperationCanceledException: The operation was canceled.", "calls|up:1200" }, (string[])ComposeHarness.Call( "CancellationIsNeverRetried" ) );
   }

   /// <summary>
   /// When compose cannot say which containers exited, the error carries both facts and nothing is retried blind.
   /// </summary>
   [Fact]
   public void Retry_SaysSoWhenItCannotTellWhetherAContainerExited()
   {
      string[] lines = ComposeHarness.Call( "CannotTellWhetherAContainerExited" );
      Assert.Equal( "error|InvalidOperationException: Could not start x.compose.yaml: container gvb-milvus exited (134) (and could not tell whether a container exited: docker compose ps said: daemon not reachable).", lines[0] );
      Assert.Equal( "calls|up:1200,exited", lines[1] );
   }

   /// <summary>
   /// The default policy is bounded: three attempts of at most 20 minutes inside 30 minutes, 5 seconds apart,
   /// none begun with less than 2 minutes left; a policy of zero attempts is refused.
   /// </summary>
   [Fact]
   public void Policy_DefaultIsBoundedAndZeroAttemptsIsRefused()
   {
      Assert.Equal( "3|1200|1800|5|120", (string)ComposeHarness.Call( "DefaultPolicy" ) );
      Assert.Equal( new[] { "error|ArgumentException: A start needs at least one attempt. (Parameter 'policy')", "calls|" }, (string[])ComposeHarness.Call( "ZeroAttemptsRefused" ) );
   }

   /// <summary>
   /// The clean-up between attempts is "docker compose down" plus "-v" only for a compose file that was read
   /// and declares no named volumes; it names the compose file (and the CPU override) and nothing else, so no
   /// data folder path can reach it. A file that declares a named volume, or one that could not be read, is
   /// cleaned up without "-v". Every engine file under deploy/engines qualifies for "-v" today (all their data
   /// is bind-mounted, which "-v" never touches).
   /// </summary>
   [Fact]
   public void Cleanup_UsesMinusVOnlyForFilesWithoutNamedVolumesAndNamesNoDataPath()
   {
      Assert.Equal( "-n docker compose -f /x/milvus.compose.yaml down -v", (string)ComposeHarness.Call( "DownArguments", "/x/milvus.compose.yaml", null, true ) );
      Assert.Equal( "-n docker compose -f /x/milvus.compose.yaml -f /tmp/o.yaml down", (string)ComposeHarness.Call( "DownArguments", "/x/milvus.compose.yaml", "/tmp/o.yaml", false ) );
      Assert.Equal( "docker compose down -v|docker compose down", (string)ComposeHarness.Call( "RemoveDescriptions" ) );
      Assert.False( (bool)ComposeHarness.Call( "RemovesVolumes", (string?)null ) );
      Assert.False( (bool)ComposeHarness.Call( "RemovesVolumes", "name: x\nservices:\n  a:\n    image: y\nvolumes:\n  data:\n" ) );
      Assert.True( (bool)ComposeHarness.Call( "RemovesVolumes", "name: x\nservices:\n  a:\n    image: y\n    volumes:\n      - /data:/var/lib/x\n" ) );
      string[] files = Directory.EnumerateFiles( Path.Combine( ComposeHarness.RepoRoot(), "deploy", "engines" ), "*.compose.yaml" ).ToArray();
      Assert.True( files.Length >= 15 );
      Assert.All( files, f => Assert.True( (bool)ComposeHarness.Call( "RemovesVolumes", File.ReadAllText( f ) ), $"{Path.GetFileName( f )} declares a named volume" ) );
   }

   /// <summary>
   /// "docker compose ps --format" output is read strictly (a line that is not "name|number" is an error, not a
   /// guess), the failure line of a log is the Go panic or fatal error if there is one, else the last error
   /// line, else the last line, on one line and cut to 200 characters, and compose's own message is cut to its
   /// last line (its progress lines come first).
   /// </summary>
   [Fact]
   public void Reading_ExitedContainersAndFailureLinesIsStrictAndShort()
   {
      Assert.Equal( new[] { "gvb-milvus:134", "gvb-oracle-seed:0" }, (string[])ComposeHarness.Call( "ParseExited", "gvb-milvus|134\n\ngvb-oracle-seed|0\n" ) );
      Assert.Empty( (string[])ComposeHarness.Call( "ParseExited", "" ) );
      Assert.Equal( new[] { "error|Unexpected line from docker compose ps: 'gvb-milvus|abc'." }, (string[])ComposeHarness.Call( "ParseExited", "gvb-milvus|abc" ) );
      Assert.Equal( new[] { "error|Unexpected line from docker compose ps: 'just words'." }, (string[])ComposeHarness.Call( "ParseExited", "just words" ) );
      Assert.Equal( "panic: etcdserver: leader changed", (string)ComposeHarness.Call( "FailureLine", "ok\npanic: etcdserver: leader changed\n\ngoroutine 1 [running]:\nanother [ERROR] later" ) );
      Assert.Equal( "fatal error: out of memory", (string)ComposeHarness.Call( "FailureLine", "x\n  fatal error:   out of memory  \ny" ) );
      Assert.Equal( "[ERROR] second", (string)ComposeHarness.Call( "FailureLine", "[ERROR] first\nplain\n[ERROR] second\nlast plain" ) );
      Assert.Equal( "last plain", (string)ComposeHarness.Call( "FailureLine", "first\nlast plain\n" ) );
      Assert.Equal( "no log lines", (string)ComposeHarness.Call( "FailureLine", " \n\n" ) );
      Assert.Equal( "last plain", (string)ComposeHarness.Call( "FailureLine", "first\nlast plain\ngoroutine 266 gp=0xc001e8d6c0 [running]:\n\t/go/src/x/rocksmq_impl.go:346 +0xc0d\ngithub.com/x/y.(*Z).Init(0xc0011a4340, 0x5)\ncreated by a.b in goroutine 1\n" ) );
      Assert.Equal( "no log lines", (string)ComposeHarness.Call( "FailureLine", "goroutine 1 [running]:\n\t/go/src/x.go:1 +0x1\n" ) );
      string longLine = (string)ComposeHarness.Call( "FailureLine", "panic: " + new string( 'x', 500 ) );
      Assert.Equal( 203, longLine.Length );
      Assert.EndsWith( "...", longLine );
      Assert.Equal( "container x exited (134)", (string)ComposeHarness.Call( "LastLine", " Container x  Creating\n Container x  Started\n  container x   exited (134)\n\n", 150 ) );
      Assert.Equal( "abc...", (string)ComposeHarness.Call( "LastLine", "first\nabcdef", 3 ) );
      Assert.Equal( "nothing", (string)ComposeHarness.Call( "LastLine", " \n ", 10 ) );
      Assert.Equal( "...b c", (string)ComposeHarness.Call( "Tail", "a   b\n c", 3 ) );
      Assert.Equal( "a b c", (string)ComposeHarness.Call( "Tail", "a   b\n c", 50 ) );
   }

   /// <summary>
   /// "Is this engine running" asks "sudo -n docker compose ... ps -q --status running --orphans=false" with a 2-minute limit and says yes for
   /// any container id printed and no for none. Why the exact words: "--orphans=false" keeps the four engine files that share a project
   /// name from seeing each other's containers, and "-n" keeps sudo from waiting for a password.
   /// </summary>
   [Fact]
   public void IsRunning_AnswersFromWhatComposeListsWithTheExactCommand()
   {
      string[] yes = ComposeHarness.Call( "IsRunning", "0|4f2d0c6a1b7e\n|" );
      Assert.Equal( new[] { "running|True", "call|sudo -n docker compose -f /x/deploy/engines/redis.compose.yaml ps -q --status running --orphans=false (limit 2 min)" }, yes );
      Assert.Equal( "running|False", ( (string[])ComposeHarness.Call( "IsRunning", "0||" ) )[0] );
      Assert.Equal( "running|False", ( (string[])ComposeHarness.Call( "IsRunning", "0|  \n |" ) )[0] );
   }

   /// <summary>
   /// "Is this engine running" fails loud when compose itself fails: a failed "compose ps" is no answer, and the older code took it for "not
   /// running", which would have run-all start an engine that was up (or stop one it took for its own). The error names the file, the exit code
   /// and the end of compose's message; a command that outlives its limit and a sudo that cannot start are errors that name the file too.
   /// </summary>
   [Fact]
   public void IsRunning_AFailedComposePsIsAnErrorAndNeverNo()
   {
      Assert.Equal( "error|docker compose ps for redis.compose.yaml failed with exit code 1, so it is not known whether the engine is running: permission denied while trying to connect to the docker API", ( (string[])ComposeHarness.Call( "IsRunning", "1||permission denied while trying to connect to the docker API" ) )[0] );
      Assert.Equal( "error|docker compose ps for redis.compose.yaml failed with exit code 1, so it is not known whether the engine is running: sudo: a password is required", ( (string[])ComposeHarness.Call( "IsRunning", "1|4f2d0c6a1b7e|sudo: a password is required" ) )[0] );
      Assert.Equal( "error|Could not ask docker compose whether redis.compose.yaml is running: sudo docker compose ps did not finish within 2 minutes.", ( (string[])ComposeHarness.Call( "IsRunning", "timeout" ) )[0] );
      Assert.Equal( "error|Could not ask docker compose whether redis.compose.yaml is running: No such file or directory", ( (string[])ComposeHarness.Call( "IsRunning", "missing" ) )[0] );
   }

   /// <summary>
   /// The Milvus compose file keeps its image and its data folder (the folder is the benchmark's only Milvus
   /// state, so a start from an empty folder is always possible), and its health check waits longer than the
   /// 300-second session lease: a restart can find the previous run's lease still alive, which holds the
   /// new coordinator in standby until it lapses, and a check that gives up first reports a start as failed
   /// minutes before it would have worked (seen 2026-10-05: unhealthy at 230 s, healthy at 320 s).
   /// </summary>
   [Fact]
   public void MilvusCompose_KeepsItsDataFolderAndWaitsLongerThanTheSessionLease()
   {
      string text = File.ReadAllText( Path.Combine( ComposeHarness.RepoRoot(), "deploy", "engines", "milvus.compose.yaml" ) );
      Assert.Contains( "image: milvusdb/milvus:v2.6.25", text );
      Assert.Contains( "- /home/dan/gvb-data/engines/milvus:/var/lib/milvus", text );
      Assert.Contains( "COMMON_SESSION_TTL: \"300\"", text );
      Assert.DoesNotContain( "\nvolumes:", text );
      int start = Seconds( text, "start_period" );
      int interval = Seconds( text, "interval" );
      int retries = int.Parse( System.Text.RegularExpressions.Regex.Match( text, @"^\s+retries:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline ).Groups[1].Value );
      Assert.True( start + interval * retries >= 300 + 60, $"health check gives up after {start + interval * retries} s, less than the 300 s session lease plus a minute" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads a compose duration such as "15s" as seconds.
   /// </summary>
   /// <param name="text">Compose text.</param>
   /// <param name="key">Key name.</param>
   /// <returns>Seconds.</returns>
   private static int Seconds( string text, string key )
   {
      System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match( text, @"^\s+" + key + @":\s*(\d+)s", System.Text.RegularExpressions.RegexOptions.Multiline );
      Assert.True( m.Success, $"no {key} in the Milvus compose file" );
      return int.Parse( m.Groups[1].Value );
   }

   #endregion Private Methods
}

/// <summary>
/// Compiles the compose runner's sources together with a scenarios file into an in-memory assembly and
/// calls its methods by name.
/// Why only four source files: ComposeRunner.cs needs Shell.cs, CpuSetText.cs and ConfigFiles.cs and nothing
/// else, so no package the benchmark restored has to be resolved.
/// </summary>
internal static class ComposeHarness
{
   #region Data Members

   private const string IMPLICIT_USINGS = "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; "
      + "global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;";
   private static readonly string[] RUNNER_SOURCES = { "ComposeRunner.cs", "Shell.cs", "CpuSetText.cs", "ConfigFiles.cs" };
   private static readonly Lazy<Assembly> FAKE = new( () => Compile( "ComposeScenarios.cs" ) );
   private static readonly Lazy<Assembly> LIVE = new( () => Compile( "ComposeLiveScenarios.cs" ) );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Calls a synchronous method of the fake-compose scenarios.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result, usable as dynamic or cast.</returns>
   public static dynamic Call( string method, params object?[] arguments )
   {
      return FAKE.Value.GetType( "GenericVectorBuilder.Bench.UnderTest.ComposeScenarios" )!.GetMethod( method )!.Invoke( null, arguments )!;
   }

   /// <summary>
   /// Calls an asynchronous method of the live scenarios and waits for it, within a limit.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="limit">Longest wait.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The task's result.</returns>
   public static async Task<dynamic> CallLiveAsync( string method, TimeSpan limit, params object?[] arguments )
   {
      var task = (Task)LIVE.Value.GetType( "GenericVectorBuilder.Bench.UnderTest.ComposeLiveScenarios" )!.GetMethod( method )!.Invoke( null, arguments )!;
      Task finished = await Task.WhenAny( task, Task.Delay( limit ) );
      Assert.True( finished == task, $"{method} did not finish within {limit.TotalMinutes:0.#} minutes" );
      await task;
      return task.GetType().GetProperty( "Result" )!.GetValue( task )!;
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file.
   /// </summary>
   /// <returns>The repository root.</returns>
   public static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found." );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Compiles the runner sources and one scenarios file.
   /// </summary>
   /// <param name="scenarios">File name under tests/GenericVectorBuilder.Engines.Tests/Bench.</param>
   /// <returns>The assembly.</returns>
   private static Assembly Compile( string scenarios )
   {
      string root = RepoRoot();
      var parse = new CSharpParseOptions( LanguageVersion.Latest );
      List<SyntaxTree> trees = RUNNER_SOURCES.Select( f => Path.Combine( root, "src", "GenericVectorBuilder.Bench", "Targets", f ) )
         .Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), parse, f ) ).ToList();
      string path = Path.Combine( root, "tests", "GenericVectorBuilder.Engines.Tests", "Bench", scenarios );
      trees.Add( CSharpSyntaxTree.ParseText( File.ReadAllText( path ), parse.WithPreprocessorSymbols( "BENCH_UNDER_TEST" ), path ) );
      trees.Add( CSharpSyntaxTree.ParseText( IMPLICIT_USINGS, parse ) );
      string[] platform = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! ).Split( Path.PathSeparator );
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "ComposeUnderTest_" + Path.GetFileNameWithoutExtension( scenarios ), trees, platform.Select( p => MetadataReference.CreateFromFile( p ) ), options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   #endregion Private Methods
}
