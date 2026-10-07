// Compiled only by the Compose tests, together with the compose runner's own sources (ComposeRunner.cs
// and the three files it needs) because the test project does not reference the benchmark console project.
// BENCH_UNDER_TEST is defined only in that compilation, so the test project itself sees an empty file.
#if BENCH_UNDER_TEST
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// Runs the compose runner's start-and-retry rules against a fake "docker compose" and hands back plain
/// lines for the tests: which compose commands were issued and in what order, what each attempt wrote
/// into the run notes, and what the error said when the start never worked.
/// Why a fake: the rules decide whether a benchmark's engine turn is lost, and the cases that matter (a
/// container that exits twice then starts, a container that never exits but is unhealthy, a clean-up that
/// fails, a time budget that runs out) cannot be produced on demand with a real engine. The live tests
/// (<c>ComposeLiveScenarios</c>) prove the same rules on the real Docker of this box.
/// </summary>
public static class ComposeScenarios
{
   #region Data Members

   // What Milvus printed when it died on 2026-10-05, trimmed to the lines that matter (the Go goroutine dump follows the panic line).
   private const string MILVUS_PANIC_LOG = "[2026/10/05 01:18:40.401 +00:00] [INFO] [etcd/etcd_server.go:58] [\"finish init Etcd config\"]\n"
      + "{\"level\":\"warn\",\"msg\":\"apply request took too long\",\"error\":\"etcdserver: leader changed\"}\n"
      + "panic: etcdserver: leader changed\n\ngoroutine 270 gp=0xc001d03c00 m=11 mp=0xc001d00008 [running]:\n";

   private static readonly StartPolicy NO_PAUSE = new( 3, TimeSpan.FromMinutes( 20 ), TimeSpan.FromMinutes( 30 ), TimeSpan.Zero, TimeSpan.FromMinutes( 2 ) );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The first attempt exits with 134 (Milvus's panic), the second is healthy.
   /// </summary>
   /// <returns>The attempt notes, then "calls|" and the commands in order.</returns>
   public static string[] RecoversOnSecondAttempt()
   {
      var fake = new FakeCommands( n => n == 1 ? Failed( "container gvb-milvus exited (134)" ) : Healthy() ) { Exited = n => n == 1 ? new[] { new ExitedContainer( "gvb-milvus", 134 ) } : Array.Empty<ExitedContainer>(), Logs = MILVUS_PANIC_LOG };
      IReadOnlyList<string> notes = ComposeStart.RunAsync( fake, "milvus.compose.yaml", NO_PAUSE, CancellationToken.None, () => fake.Clock ).GetAwaiter().GetResult();
      return notes.Append( "calls|" + string.Join( ",", fake.Calls ) ).ToArray();
   }

   /// <summary>
   /// Every attempt exits; the start must give up after the third with all three in the error.
   /// </summary>
   /// <returns>"error|" and the message, then "calls|" and the commands in order.</returns>
   public static string[] ExitsEveryTime()
   {
      var fake = new FakeCommands( _ => Failed( "container gvb-milvus exited (134)" ) ) { Exited = _ => new[] { new ExitedContainer( "gvb-milvus", 134 ) }, Logs = MILVUS_PANIC_LOG };
      return Run( fake, NO_PAUSE );
   }

   /// <summary>
   /// The start fails but no container exited (an unhealthy one that keeps running): it must fail at once, not retry.
   /// </summary>
   /// <returns>"error|" and the message, then "calls|" and the commands in order.</returns>
   public static string[] FailsWithoutAnExit()
   {
      var fake = new FakeCommands( _ => Failed( "container gvb-x is unhealthy" ) ) { Exited = _ => Array.Empty<ExitedContainer>() };
      return Run( fake, NO_PAUSE );
   }

   /// <summary>
   /// The start fails and the only exited container exited with code 0 (a one-shot step that finished): not a crash, no retry.
   /// </summary>
   /// <returns>"error|" and the message, then "calls|" and the commands in order.</returns>
   public static string[] CleanExitIsNotACrash()
   {
      var fake = new FakeCommands( _ => Failed( "dependency failed to start: container gvb-oracle is unhealthy" ) ) { Exited = _ => new[] { new ExitedContainer( "gvb-oracle-seed", 0 ) } };
      return Run( fake, NO_PAUSE );
   }

   /// <summary>
   /// One attempt uses almost all of a 15-minute budget: the exit is reported and no second attempt is begun.
   /// </summary>
   /// <returns>"error|" and the message, then "calls|" and the commands in order.</returns>
   public static string[] NoTimeLeftForAnotherAttempt()
   {
      var fake = new FakeCommands( _ => Failed( "container gvb-milvus exited (134)" ), TimeSpan.FromMinutes( 14 ) ) { Exited = _ => new[] { new ExitedContainer( "gvb-milvus", 134 ) } };
      return Run( fake, new StartPolicy( 5, TimeSpan.FromMinutes( 10 ), TimeSpan.FromMinutes( 15 ), TimeSpan.Zero, TimeSpan.FromMinutes( 2 ) ) );
   }

   /// <summary>
   /// Two attempts of 8 minutes each leave 14 of a 30-minute budget: the third attempt is told to wait 14 minutes, not 20.
   /// </summary>
   /// <returns>The attempt notes, then "calls|" and the commands in order.</returns>
   public static string[] LastWaitIsCutToTheTimeLeft()
   {
      var fake = new FakeCommands( n => n < 3 ? Failed( "container gvb-milvus exited (134)" ) : Healthy(), TimeSpan.FromMinutes( 8 ) ) { Exited = _ => new[] { new ExitedContainer( "gvb-milvus", 134 ) } };
      IReadOnlyList<string> notes = ComposeStart.RunAsync( fake, "milvus.compose.yaml", NO_PAUSE, CancellationToken.None, () => fake.Clock ).GetAwaiter().GetResult();
      return notes.Append( "calls|" + string.Join( ",", fake.Calls ) ).ToArray();
   }

   /// <summary>
   /// The failed container cannot be removed: the start must fail loudly instead of starting on top of it.
   /// </summary>
   /// <returns>"error|" and the message, then "calls|" and the commands in order.</returns>
   public static string[] RemovalFails()
   {
      var fake = new FakeCommands( _ => Failed( "container gvb-milvus exited (134)" ) ) { Exited = _ => new[] { new ExitedContainer( "gvb-milvus", 134 ) }, RemoveProblem = "compose down said: permission denied" };
      return Run( fake, NO_PAUSE );
   }

   /// <summary>
   /// A compose call outlives its time limit (Shell throws TimeoutException): never retried.
   /// </summary>
   /// <returns>"error|" and the exception, then "calls|" and the commands in order.</returns>
   public static string[] TimeoutIsNeverRetried()
   {
      var fake = new FakeCommands( _ => Healthy() ) { UpThrows = new TimeoutException( "sudo docker compose up did not finish within 21 minutes." ) };
      return Run( fake, NO_PAUSE );
   }

   /// <summary>
   /// The caller cancels: never retried, and no clean-up command is issued behind its back.
   /// </summary>
   /// <returns>"error|" and the exception, then "calls|" and the commands in order.</returns>
   public static string[] CancellationIsNeverRetried()
   {
      var fake = new FakeCommands( _ => Failed( "container gvb-milvus exited (134)" ) ) { Exited = _ => new[] { new ExitedContainer( "gvb-milvus", 134 ) } };
      using var cancelled = new CancellationTokenSource();
      cancelled.Cancel();
      return Run( fake, NO_PAUSE, cancelled.Token );
   }

   /// <summary>
   /// Compose cannot say which containers exited: the start fails with both facts, never retries blind.
   /// </summary>
   /// <returns>"error|" and the message, then "calls|" and the commands in order.</returns>
   public static string[] CannotTellWhetherAContainerExited()
   {
      var fake = new FakeCommands( _ => Failed( "container gvb-milvus exited (134)" ) ) { ExitedThrows = new InvalidOperationException( "docker compose ps said: daemon not reachable" ) };
      return Run( fake, NO_PAUSE );
   }

   /// <summary>
   /// The policy refuses zero attempts.
   /// </summary>
   /// <returns>"error|" and the exception.</returns>
   public static string[] ZeroAttemptsRefused()
   {
      var fake = new FakeCommands( _ => Healthy() );
      return Run( fake, NO_PAUSE with { Attempts = 0 } );
   }

   /// <summary>
   /// The default policy as "attempts|attempt seconds|total seconds|pause seconds|minimum seconds".
   /// </summary>
   /// <returns>The text.</returns>
   public static string DefaultPolicy()
   {
      StartPolicy p = StartPolicy.Default;
      return $"{p.Attempts}|{p.AttemptTimeout.TotalSeconds}|{p.TotalTimeout.TotalSeconds}|{p.Pause.TotalSeconds}|{p.MinimumAttempt.TotalSeconds}";
   }

   /// <summary>
   /// Reads "name|code" lines the way the real implementation reads "docker compose ps --format".
   /// </summary>
   /// <param name="text">The output text.</param>
   /// <returns>"name:code" per container, or "error|" and the message.</returns>
   public static string[] ParseExited( string text )
   {
      try
      {
         return ComposeStart.ParseExited( text ).Select( c => $"{c.Name}:{c.ExitCode}" ).ToArray();
      }
      catch( InvalidOperationException ex )
      {
         return new[] { "error|" + ex.Message };
      }
   }

   /// <summary>
   /// Picks the failure line out of a container log.
   /// </summary>
   /// <param name="logs">The log text.</param>
   /// <returns>The line.</returns>
   public static string FailureLine( string logs )
   {
      return ComposeStart.FailureLine( logs );
   }

   /// <summary>
   /// The last non-empty line of a message.
   /// </summary>
   /// <param name="text">The message.</param>
   /// <param name="max">Characters to keep.</param>
   /// <returns>The line.</returns>
   public static string LastLine( string text, int max )
   {
      return ComposeStart.LastLine( text, max );
   }

   /// <summary>
   /// The tail of a message with whitespace collapsed.
   /// </summary>
   /// <param name="text">The message.</param>
   /// <param name="max">Characters to keep.</param>
   /// <returns>The tail.</returns>
   public static string Tail( string text, int max )
   {
      return ComposeStart.Tail( text, max );
   }

   /// <summary>
   /// The arguments of the clean-up command between attempts.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="overridePath">CPU override file or null.</param>
   /// <param name="removeVolumes">Whether "-v" is allowed.</param>
   /// <returns>The arguments after "sudo", space-joined.</returns>
   public static string DownArguments( string composePath, string? overridePath, bool removeVolumes )
   {
      return string.Join( ' ', DockerComposeCommands.DownArguments( composePath, overridePath, removeVolumes ) );
   }

   /// <summary>
   /// Whether a compose file's text allows the clean-up to use "-v".
   /// </summary>
   /// <param name="text">The compose text, or null.</param>
   /// <returns>True when "-v" is allowed.</returns>
   public static bool RemovesVolumes( string? text )
   {
      return ComposeRunner.RemovesVolumesOnRetry( text );
   }

   /// <summary>
   /// Whether the real clean-up command is described with "-v" when it is allowed and without when it is not.
   /// </summary>
   /// <returns>The two descriptions, joined with "|".</returns>
   public static string RemoveDescriptions()
   {
      return new DockerComposeCommands( "x.compose.yaml", null, true ).RemoveDescription + "|" + new DockerComposeCommands( "x.compose.yaml", null, false ).RemoveDescription;
   }

   /// <summary>
   /// Asks "is this engine running" through a fake command runner that answers "docker compose ps" as told.
   /// </summary>
   /// <param name="answer">"exit|stdout|stderr", "timeout" for a command that outlives its limit, or "missing" for a sudo that cannot be started.</param>
   /// <returns>"running|True", "running|False", or "error|" and the message, then "call|" and the command line as run.</returns>
   public static string[] IsRunning( string answer )
   {
      var lines = new List<string>();
      var calls = new List<string>();
      Task<ShellResult> Fake( IEnumerable<string> arguments, TimeSpan limit, CancellationToken ct )
      {
         calls.Add( $"sudo {string.Join( ' ', arguments )} (limit {limit.TotalMinutes:0} min)" );
         if( answer == "timeout" )
         {
            throw new TimeoutException( "sudo docker compose ps did not finish within 2 minutes." );
         }

         if( answer == "missing" )
         {
            throw new System.ComponentModel.Win32Exception( "No such file or directory" );
         }

         string[] parts = answer.Split( '|' );
         return Task.FromResult( new ShellResult( int.Parse( parts[0] ), parts[1].Replace( "\\n", "\n" ), parts.Length > 2 ? parts[2] : string.Empty ) );
      }

      try
      {
         lines.Add( "running|" + ComposeRunner.IsRunningAsync( "/x/deploy/engines/redis.compose.yaml", Fake, CancellationToken.None ).GetAwaiter().GetResult() );
      }
      catch( InvalidOperationException ex )
      {
         lines.Add( "error|" + ex.Message );
      }

      lines.AddRange( calls.Select( c => "call|" + c ) );
      return lines.ToArray();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs the retry rules and returns either the notes or the error, then the commands issued.
   /// </summary>
   /// <param name="fake">The fake compose.</param>
   /// <param name="policy">The policy.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Lines for the test.</returns>
   private static string[] Run( FakeCommands fake, StartPolicy policy, CancellationToken ct = default )
   {
      var lines = new List<string>();
      try
      {
         lines.AddRange( ComposeStart.RunAsync( fake, "x.compose.yaml", policy, ct, () => fake.Clock ).GetAwaiter().GetResult() );
      }
      catch( Exception ex )
      {
         lines.Add( $"error|{ex.GetType().Name}: {ex.Message}" );
      }

      lines.Add( "calls|" + string.Join( ",", fake.Calls ) );
      return lines.ToArray();
   }

   /// <summary>
   /// A compose "up" that failed.
   /// </summary>
   /// <param name="error">What compose wrote to standard error.</param>
   /// <returns>The result.</returns>
   private static ShellResult Failed( string error )
   {
      return new ShellResult( 1, string.Empty, error );
   }

   /// <summary>
   /// A compose "up" that succeeded.
   /// </summary>
   /// <returns>The result.</returns>
   private static ShellResult Healthy()
   {
      return new ShellResult( 0, string.Empty, string.Empty );
   }

   #endregion Private Methods

   /// <summary>
   /// A compose that answers from tables and keeps a fake clock and a record of every command, so a test
   /// can assert the order of commands and the wait each "up" was given.
   /// </summary>
   private sealed class FakeCommands : IComposeCommands
   {
      #region Data Members

      private readonly Func<int, ShellResult> _up;
      private readonly TimeSpan _upTakes;
      private int _ups;

      #endregion Data Members

      #region Constructor

      /// <summary>
      /// Creates the fake.
      /// </summary>
      /// <param name="up">Result of the nth "up" (1-based).</param>
      /// <param name="upTakes">Fake time each "up" takes.</param>
      public FakeCommands( Func<int, ShellResult> up, TimeSpan upTakes = default )
      {
         _up = up;
         _upTakes = upTakes;
      }

      #endregion Constructor

      #region Public Methods

      /// <summary>Fake time since the start began.</summary>
      public TimeSpan Clock { get; private set; }

      /// <summary>Commands issued, in order ("up:1200" is an "up" with a 1200 second wait).</summary>
      public List<string> Calls { get; } = new();

      /// <summary>Exited containers after the nth failed "up".</summary>
      public Func<int, IReadOnlyList<ExitedContainer>> Exited { get; init; } = _ => Array.Empty<ExitedContainer>();

      /// <summary>What the container log holds.</summary>
      public string Logs { get; init; } = string.Empty;

      /// <summary>What a failed removal says, or null for a removal that works.</summary>
      public string? RemoveProblem { get; init; }

      /// <summary>An exception the "up" throws, or null.</summary>
      public Exception? UpThrows { get; init; }

      /// <summary>An exception the exited-container query throws, or null.</summary>
      public Exception? ExitedThrows { get; init; }

      /// <summary>The clean-up command's description.</summary>
      public string RemoveDescription => "docker compose down -v";

      /// <summary>Records the call and answers from the table.</summary>
      /// <param name="waitTimeout">Wait the caller asked for.</param>
      /// <param name="ct">Cancellation.</param>
      /// <returns>The nth result.</returns>
      public Task<ShellResult> UpAsync( TimeSpan waitTimeout, CancellationToken ct )
      {
         _ups++;
         Calls.Add( $"up:{(int)waitTimeout.TotalSeconds}" );
         ct.ThrowIfCancellationRequested();
         if( UpThrows != null )
         {
            throw UpThrows;
         }

         Clock += _upTakes;
         return Task.FromResult( _up( _ups ) );
      }

      /// <summary>Records the call and answers from the table.</summary>
      /// <param name="ct">Cancellation.</param>
      /// <returns>The exited containers after the latest "up".</returns>
      public Task<IReadOnlyList<ExitedContainer>> ExitedAsync( CancellationToken ct )
      {
         Calls.Add( "exited" );
         if( ExitedThrows != null )
         {
            throw ExitedThrows;
         }

         return Task.FromResult( Exited( _ups ) );
      }

      /// <summary>Records the call and returns the canned log.</summary>
      /// <param name="container">Container name.</param>
      /// <param name="ct">Cancellation.</param>
      /// <returns>The log.</returns>
      public Task<string> LogsAsync( string container, CancellationToken ct )
      {
         Calls.Add( $"logs:{container}" );
         return Task.FromResult( Logs );
      }

      /// <summary>Records the call and answers.</summary>
      /// <param name="ct">Cancellation.</param>
      /// <returns>Null, or the canned problem.</returns>
      public Task<string?> RemoveContainersAsync( CancellationToken ct )
      {
         Calls.Add( "remove" );
         return Task.FromResult( RemoveProblem );
      }

      #endregion Public Methods
   }
}
#endif
