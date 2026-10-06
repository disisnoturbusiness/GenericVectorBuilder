// Compiled only by MachineControlTests, MachineControlClockTests and MachineControlLiveTests,
// together with the benchmark's own sources and MachineControlScenarios.cs (this is a third part
// of the same static class). BENCH_UNDER_TEST is defined only in that compilation, so the test
// project itself sees an empty file.
#if BENCH_UNDER_TEST
using System.Globalization;
using System.Text.Json;
using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Running;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// The pinned CPU clock: turbo off and the uncore limit pinned for the run (recorded before the
/// change), every CPU's ceiling the same, each pass's average clock per CPU group in the notes
/// and a pass more than 1% off the pinned clock flagged, and the clock put back exactly by the
/// run's own restore, by the next start after a crash, and by restore-machine. Fake machine,
/// plus one live scenario.
/// </summary>
public static partial class MachineControlScenarios
{
   #region Data Members

   private static readonly int[] CPUS = Enumerable.Range( 0, 8 ).ToArray();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// A run on the fake machine with two embedded targets: "steady" timed at the base clock
   /// (3491 MHz read) and "turbo" timed while every CPU reads 3592 MHz (as if turbo were still on).
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <param name="mode">"intel" (no_turbo), "boost" (cpufreq/boost only), "already-off" (no_turbo already 1), "no-switch" (neither file), "write-fails" (the no_turbo write is refused), "uneven" (CPU 3 has a 2000 MHz ceiling of its own), "changed-under" (something else turns turbo back on between the two targets), "uncore-missing" (no msr module: the run must refuse, never load it), "uncore-write-fails" (wrmsr is refused) or "uncore-changed-under" (something else sets the uncore limit back during the run).</param>
   /// <returns>Everything the tests check.</returns>
   public static async Task<ClockRunResult> ClockRunAsync( string folder, string mode )
   {
      FakeMachine machine = ClockMachine( mode );
      var store = new MachineStateStore( Path.Combine( folder, "state.json" ) );
      var clockStore = new ClockStore( store.Path );
      machine.ClockFile = clockStore.Path;
      string before = ClockControl.Read( machine, CPUS ).Key();
      long uncoreBefore = machine.UncoreLimitMsr;
      var logs = new List<string>();
      var options = new MachineControlOptions { StateFile = store.Path, PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 20 ), BusyWait = TimeSpan.FromMilliseconds( 200 ), BusyPoll = TimeSpan.FromMilliseconds( 20 ) };
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "steady" } ) );
      MachineControl control;
      try
      {
         control = await MachineControl.StartAsync( conditions, options, machine, logs.Add, CancellationToken.None );
      }
      catch( InvalidOperationException ex )
      {
         return new ClockRunResult( ex.Message, before, "none", "none", ClockControl.Read( machine, CPUS ).Key(), false, clockStore.Exists, File.Exists( store.Path ),
            machine.ClockAtCall.ToArray(), machine.Calls.ToArray(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), logs.ToArray(), "{}", machine.Governors(),
            Hex( uncoreBefore ), "none", Hex( machine.UncoreLimitMsr ) );
      }

      ClockSettings during = ClockControl.Read( machine, CPUS );
      long uncoreDuring = machine.UncoreLimitMsr;
      bool fileDuring = clockStore.Exists;
      Action<string> log = control.WrapLog( logs.Add );
      await ClockPassAsync( control, machine, log, "steady", 3_491_000 );
      if( mode == "changed-under" )
      {
         machine.Set( ClockControl.NO_TURBO_PATH, "0\n" );
      }

      if( mode == "uncore-changed-under" )
      {
         machine.UncoreLimitMsr = 0xc1e;
      }

      await ClockPassAsync( control, machine, log, "turbo", 3_592_000 );
      await control.RestoreAsync();
      control.Dispose();
      var report = new BenchReport { Targets = { new TargetReport { Name = "steady", PassOrder = new List<string> { "default@1" } }, new TargetReport { Name = "turbo", PassOrder = new List<string> { "default@1" } } } };
      control.Annotate( report );
      report.Targets.ForEach( control.AnnotateTarget );
      return new ClockRunResult( null, before, during.Key(), during.Describe(), ClockControl.Read( machine, CPUS ).Key(), fileDuring, clockStore.Exists, File.Exists( store.Path ),
         machine.ClockAtCall.ToArray(), machine.Calls.ToArray(), report.Notes.ToArray(), report.Targets[0].Notes.ToArray(), report.Targets[1].Notes.ToArray(), logs.ToArray(),
         JsonSerializer.Serialize( control.Conditions, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } ), machine.Governors(),
         Hex( uncoreBefore ), Hex( uncoreDuring ), Hex( machine.UncoreLimitMsr ) );
   }

   /// <summary>
   /// A crashed run's clock record on a fake machine still at turbo off, then the restore a new
   /// start (or restore-machine) does first.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <param name="mode">"clock-only" (the state file was already removed), "with-state" (the state file holds the governors too), "live-owner", "write-fails", "tampered" (the record names another file), "ceiling-moved" (one CPU's ceiling does not come back with turbo), "with-uncore" (the run had pinned the uncore limit too) or "uncore-tampered" (the record's uncore limit sets reserved bits).</param>
   /// <returns>The error, the calls, the clock before the crash and after the restore, and which files are left.</returns>
   public static async Task<ClockRestoreResult> ClockRestoreAfterCrashAsync( string folder, string mode )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.AddProcess( 777, "dotnet", 5000, 1, "0-7", "/user.slice/x.scope" );
      string before = ClockControl.Read( machine, CPUS ).Key();
      if( mode == "ceiling-moved" )
      {
         machine.Set( ClockControl.MaxPath( 5 ), "3000000\n" );
      }

      machine.Set( ClockControl.NO_TURBO_PATH, "1\n" );
      CPUS.Where( c => !( mode == "ceiling-moved" && c == 5 ) ).ToList().ForEach( c => machine.Set( ClockControl.MaxPath( c ), "3500000\n" ) );
      var store = new MachineStateStore( Path.Combine( folder, "state.json" ) );
      var clockStore = new ClockStore( store.Path );
      clockStore.Save( new ClockState
      {
         OwnerPid = 777, OwnerStartTicks = mode == "live-owner" ? 5000 : 4999, StartedUtc = "2026-10-05T12:00:00Z",
         SwitchPath = mode == "tampered" ? "/etc/sudoers" : ClockControl.NO_TURBO_PATH, PreviousValue = "0", PinnedValue = "1",
         MaxKhz = CPUS.ToDictionary( c => c, _ => 3_600_000L ), MinKhz = CPUS.ToDictionary( c => c, _ => 1_200_000L ),
         PreviousUncoreLimit = mode switch { "with-uncore" => "c1e", "uncore-tampered" => "ffff0c1e", _ => null },
         PinnedUncoreLimit = mode is "with-uncore" or "uncore-tampered" ? "1e1e" : null,
      } );
      if( mode is "with-uncore" or "uncore-tampered" )
      {
         machine.UncoreLimitMsr = 0x1e1e;
      }

      if( mode == "with-state" )
      {
         CPUS.ToList().ForEach( c => machine.Set( GovernorControl.GovernorPath( c ), "performance\n" ) );
         store.Save( new MachineState { OwnerPid = 777, OwnerStartTicks = 4999, StartedUtc = "2026-10-05T12:00:00Z", Governors = CPUS.ToDictionary( c => c, _ => "schedutil" ) } );
      }

      if( mode == "write-fails" )
      {
         machine.Fail = call => call.Contains( "no_turbo", StringComparison.Ordinal ) ? new ShellResult( 1, string.Empty, "permission denied" ) : null;
      }

      var logs = new List<string>();
      string? error = null;
      try
      {
         await MachineControl.RestoreStaleAsync( store, machine, logs.Add, CancellationToken.None );
      }
      catch( InvalidOperationException ex )
      {
         error = ex.Message;
      }

      return new ClockRestoreResult( error, machine.Calls.ToArray(), before, ClockControl.Read( machine, CPUS ).Key(), clockStore.Exists, File.Exists( store.Path ), logs.ToArray(), machine.Governors(), clockStore.Path, Hex( machine.UncoreLimitMsr ) );
   }

   /// <summary>
   /// The average clock of a CPU group over a window, from hand-set samples.
   /// </summary>
   /// <returns>Mean of CPUs 0 and 1 over three samples, mean of CPU 1 alone, mean of a CPU never sampled, and the mean of a window shorter than one sample.</returns>
   public static double?[] SamplerMeans()
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.StaticCounters = true;
      var sampler = new MachineSampler( machine, new[] { 0, 1 }, 100, TimeSpan.FromMilliseconds( 250 ) );
      var t0 = new DateTime( 2026, 10, 5, 12, 0, 0, DateTimeKind.Utc );
      int[][] mhz = { new[] { 3492, 3492 }, new[] { 3592, 3492 }, new[] { 3492, 3592 } };
      for( int step = 0; step < mhz.Length; step++ )
      {
         machine.Set( GovernorControl.FrequencyPath( 0 ), $"{mhz[step][0] * 1000}\n" );
         machine.Set( GovernorControl.FrequencyPath( 1 ), $"{mhz[step][1] * 1000}\n" );
         sampler.Tick( t0.AddMilliseconds( 250 * step ) );
      }

      return new[] { sampler.MeanMhz( t0, t0.AddMilliseconds( 500 ), new[] { 0, 1 } ), sampler.MeanMhz( t0, t0.AddMilliseconds( 500 ), new[] { 1 } ),
         sampler.MeanMhz( t0, t0.AddMilliseconds( 500 ), new[] { 7 } ), sampler.MeanMhz( t0.AddMilliseconds( 260 ), t0.AddMilliseconds( 270 ), new[] { 0 } ) };
   }

   /// <summary>
   /// The clock warnings and the clock note for hand-made passes pinned at 3500 MHz: one at
   /// 3491.8 (in), one with the engine CPUs at 3540 (+1.1%, out), one with the client CPUs at
   /// 3464 (-1.0%, in), one with no reading, and one that was not pinned.
   /// </summary>
   /// <returns>The run's flags, the warnings for the passes, and the clock note.</returns>
   public static string[][] ClockFlags()
   {
      var passes = new List<PassConditions>
      {
         new() { Target = "a", Pass = "default@1", Governor = "performance", EngineMhzMedian = 3492, ClientMhzMedian = 3492, EngineMhzMean = 3491.8, ClientMhzMean = 3491.8, PinnedMhz = 3500 },
         new() { Target = "a", Pass = "default@8", Governor = "performance", EngineMhzMedian = 3540, ClientMhzMedian = 3492, EngineMhzMean = 3540, ClientMhzMean = 3491.8, PinnedMhz = 3500 },
         new() { Target = "b", Pass = "default@1", Governor = "performance", EngineMhzMean = 3500, ClientMhzMean = 3465, PinnedMhz = 3500 },
         new() { Target = "b", Pass = "exact", Governor = "performance", PinnedMhz = 3500 },
         new() { Target = "c", Pass = "default@1", Governor = "performance", EngineMhzMean = 3592, ClientMhzMean = 3592 },
      };
      var c = new MachineConditions { BuildConfiguration = "Release", MachineControl = "on", Governor = "performance", GovernorAtEnd = "performance", StateFile = "/s.json" };
      c.Passes.AddRange( passes );
      return new[] { MachineFlags.Compute( c, new[] { "a", "b", "c" } ).ToArray(), MachineFlags.ClockWarnings( passes ).ToArray(), new[] { MachineFlags.ClockLine( passes.Take( 2 ) ) ?? "null", MachineFlags.ClockLine( passes.Skip( 4 ) ) ?? "null" } };
   }

   /// <summary>
   /// The uncore limit's pinned form and its refusals.
   /// </summary>
   /// <returns>"pinned hex|found|pinned|hex of 0x1e1e", then "refused X" or "accepted X" for each bad value.</returns>
   public static string[] UncoreParsing()
   {
      UncoreLimit found = UncoreLimit.Parse( "c1e" );
      var result = new List<string> { $"{found.Pinned().Hex}|{found.Describe()}|{found.Pinned().Describe()}|{UncoreLimit.Parse( "0x1e1e" ).Hex}" };
      foreach( string bad in new[] { "ffff0c1e", "0", "1e0c", "zz" } )
      {
         try
         {
            UncoreLimit.Parse( bad );
            result.Add( "accepted " + bad );
         }
         catch( InvalidOperationException )
         {
            result.Add( "refused " + bad );
         }
      }

      return result.ToArray();
   }

   /// <summary>
   /// Where the clock record of a state file lives.
   /// </summary>
   /// <returns>The record paths of "/x/bench-machine-state.json" and "/x/state".</returns>
   public static string[] ClockPaths()
   {
      return new[] { new ClockStore( "/x/bench-machine-state.json" ).Path, new ClockStore( "/x/state" ).Path };
   }

   /// <summary>
   /// Live: pins the real clock through machine control and puts it back, then does it again as a
   /// run that dies without its finally block, and lets the next start put it back. Uses the real
   /// state file, so if this test is killed half-way the next benchmark start (or restore-machine)
   /// still puts the clock back. Refuses, changing nothing, while another run owns the machine.
   /// </summary>
   /// <returns>The clock before, during (with its text and the record), after, and the crash leg's figures.</returns>
   public static async Task<LiveClockResult> LiveClockAsync()
   {
      var system = new LinuxMachineSystem( () => throw new InvalidOperationException( "the clock test must not touch SQL Server" ) );
      IReadOnlyList<int> cpus = CpuList.Parse( MachineRestorer.OnlineCpus( system ) );
      string before = ClockControl.Read( system, cpus ).Key();
      string uncoreBefore = ( await UncoreControl.ReadAsync( system, CancellationToken.None ) ).Hex;
      var options = new MachineControlOptions { PinClient = false };
      var logs = new List<string>();
      MachineControl control = await MachineControl.StartAsync( MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "t" } ) ), options, system, logs.Add, CancellationToken.None );
      ClockSettings during = ClockControl.Read( system, cpus );
      string uncoreDuring = ( await UncoreControl.ReadAsync( system, CancellationToken.None ) ).Hex;
      var clockStore = new ClockStore( options.StateFile );
      ClockState? recorded = clockStore.Load();
      await Task.Delay( 600 );
      await control.RestoreAsync();
      control.Dispose();
      string after = ClockControl.Read( system, cpus ).Key() + ";uncore=" + ( await UncoreControl.ReadAsync( system, CancellationToken.None ) ).Hex;
      bool filesAfter = clockStore.Exists || File.Exists( options.StateFile );
      ( string crashDuring, string crashAfter, bool crashFilesAfter ) = await ClockCrashLegAsync( system, cpus, options.StateFile, logs );
      return new LiveClockResult( before + ";uncore=" + uncoreBefore, during.Key() + ";uncore=" + uncoreDuring, during.Describe(),
         recorded == null ? "no record" : $"{recorded.SwitchPath} {recorded.PreviousValue} -> {recorded.PinnedValue}, {recorded.MaxKhz.Count} CPUs, uncore 0x{recorded.PreviousUncoreLimit} -> 0x{recorded.PinnedUncoreLimit}",
         after, filesAfter, crashDuring, crashAfter, crashFilesAfter, logs.ToArray() );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A register value as rdmsr prints it.
   /// </summary>
   /// <param name="value">The value.</param>
   /// <returns>Lower-case hex without prefix.</returns>
   private static string Hex( long value )
   {
      return value.ToString( "x", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// The fake linus7795 set up for one clock mode.
   /// </summary>
   /// <param name="mode">See <see cref="ClockRunAsync"/>.</param>
   /// <returns>The machine.</returns>
   private static FakeMachine ClockMachine( string mode )
   {
      FakeMachine machine = FakeMachine.Linus();
      switch( mode )
      {
         case "boost":
            machine.Remove( ClockControl.NO_TURBO_PATH );
            machine.Set( ClockControl.BOOST_PATH, "1\n" );
            break;
         case "already-off":
            machine.Set( ClockControl.NO_TURBO_PATH, "1\n" );
            CPUS.ToList().ForEach( c => machine.Set( ClockControl.MaxPath( c ), "3500000\n" ) );
            break;
         case "no-switch":
            machine.Remove( ClockControl.NO_TURBO_PATH );
            break;
         case "write-fails":
            machine.Fail = call => call.Contains( "no_turbo", StringComparison.Ordinal ) ? new ShellResult( 1, string.Empty, "permission denied" ) : null;
            break;
         case "uneven":
            machine.Set( ClockControl.MaxPath( 3 ), "2000000\n" );
            break;
         case "uncore-missing":
            machine.MsrLoaded = false;
            break;
         case "uncore-write-fails":
            machine.Fail = call => call.Contains( "wrmsr", StringComparison.Ordinal ) ? new ShellResult( 1, string.Empty, "wrmsr: CPU 0 cannot set MSR 0x00000620 to 0x0000000000001e1e" ) : null;
            break;
      }

      return machine;
   }

   /// <summary>
   /// One embedded target with one timed pass while every CPU reads the given clock.
   /// </summary>
   /// <param name="control">Machine control.</param>
   /// <param name="machine">The fake machine.</param>
   /// <param name="log">The wrapped log.</param>
   /// <param name="target">Target name.</param>
   /// <param name="khz">Every CPU's scaling_cur_freq during the pass.</param>
   private static async Task ClockPassAsync( MachineControl control, FakeMachine machine, Action<string> log, string target, int khz )
   {
      await control.EnterTargetAsync( target, "embedded", null, CancellationToken.None );
      CPUS.ToList().ForEach( c => machine.Set( GovernorControl.FrequencyPath( c ), khz.ToString( CultureInfo.InvariantCulture ) + "\n" ) );
      log( $"  {target}: warm-up before default@1, 7 searches" );
      log( $"  {target}: timing default@1" );
      await Task.Delay( 150 );
      log( $"  {target}: p50 1.00 ms, p95 2.00 ms over 300 searches, 9.0 QPS with one searcher, recall@10 1.000" );
      await control.LeaveTargetAsync( target );
      CPUS.ToList().ForEach( c => machine.Set( GovernorControl.FrequencyPath( c ), "3491000\n" ) );
   }

   /// <summary>
   /// The crash leg of the live clock test: pins the clock as a run would, with the state's owner
   /// marked as a process that no longer exists (a dead run), then lets RestoreStaleAsync, which
   /// every start and restore-machine call first, put it back.
   /// </summary>
   /// <param name="system">The real machine.</param>
   /// <param name="cpus">Online CPUs.</param>
   /// <param name="stateFile">The real state file.</param>
   /// <param name="logs">Receives the log.</param>
   /// <returns>The clock while "crashed" and after the restore, and whether a file is left.</returns>
   /// <exception cref="InvalidOperationException">Another run holds the machine (nothing was changed).</exception>
   private static async Task<(string During, string After, bool FilesAfter)> ClockCrashLegAsync( IMachineSystem system, IReadOnlyList<int> cpus, string stateFile, List<string> logs )
   {
      var store = new MachineStateStore( stateFile );
      var clockStore = new ClockStore( stateFile );
      if( File.Exists( stateFile ) || clockStore.Exists )
      {
         throw new InvalidOperationException( $"{stateFile} or {clockStore.Path} exists: another run holds the machine; the crash leg changed nothing." );
      }

      MachineState dead = MachineStateKeeper.NewState( system );
      dead.OwnerStartTicks = -1;
      try
      {
         await ClockControl.ApplyAsync( system, clockStore, dead, cpus, CancellationToken.None );
         string during = ClockControl.Read( system, cpus ).Key() + ";uncore=" + ( await UncoreControl.ReadAsync( system, CancellationToken.None ) ).Hex;
         await MachineControl.RestoreStaleAsync( store, system, logs.Add, CancellationToken.None );
         string after = ClockControl.Read( system, cpus ).Key() + ";uncore=" + ( await UncoreControl.ReadAsync( system, CancellationToken.None ) ).Hex;
         return ( during, after, clockStore.Exists || File.Exists( stateFile ) );
      }
      finally
      {
         if( clockStore.Exists )
         {
            await MachineControl.RestoreStaleAsync( store, system, logs.Add, CancellationToken.None );
         }
      }
   }

   #endregion Private Methods
}

/// <summary>
/// What a clock run on the fake machine shows.
/// </summary>
/// <param name="Error">The start's error, or null.</param>
/// <param name="Before">Clock key before the run.</param>
/// <param name="During">Clock key during the run ("none" when the start failed).</param>
/// <param name="DuringText">The clock during the run as text.</param>
/// <param name="After">Clock key after the run.</param>
/// <param name="ClockFileDuring">True when the clock record existed during the run.</param>
/// <param name="ClockFileAfter">True when the clock record is left.</param>
/// <param name="StateFileAfter">True when the state file is left.</param>
/// <param name="ClockAtCall">The clock record at each call.</param>
/// <param name="Calls">Every command.</param>
/// <param name="RunNotes">The run's notes.</param>
/// <param name="SteadyNotes">Notes of the target timed at the base clock.</param>
/// <param name="TurboNotes">Notes of the target timed at the turbo clock.</param>
/// <param name="Log">The log.</param>
/// <param name="ConditionsJson">The conditions as results.json writes them.</param>
/// <param name="GovernorsAfter">Governors after the run.</param>
/// <param name="UncoreBefore">The uncore limit before the run (hex).</param>
/// <param name="UncoreDuring">The uncore limit during the run ("none" when the start failed).</param>
/// <param name="UncoreAfter">The uncore limit after the run.</param>
public sealed record ClockRunResult( string? Error, string Before, string During, string DuringText, string After, bool ClockFileDuring, bool ClockFileAfter, bool StateFileAfter,
   string[] ClockAtCall, string[] Calls, string[] RunNotes, string[] SteadyNotes, string[] TurboNotes, string[] Log, string ConditionsJson, string[] GovernorsAfter,
   string UncoreBefore, string UncoreDuring, string UncoreAfter );

/// <summary>
/// What restoring a crashed run's clock shows.
/// </summary>
/// <param name="Error">The restore's error, or null.</param>
/// <param name="Calls">Every command.</param>
/// <param name="Before">Clock key before the crashed run changed it.</param>
/// <param name="After">Clock key after the restore.</param>
/// <param name="ClockFileLeft">True when the clock record is left.</param>
/// <param name="StateFileLeft">True when the state file is left.</param>
/// <param name="Log">The log.</param>
/// <param name="Governors">Governors after.</param>
/// <param name="ClockPath">The clock record's path.</param>
/// <param name="UncoreAfter">The uncore limit after the restore (hex).</param>
public sealed record ClockRestoreResult( string? Error, string[] Calls, string Before, string After, bool ClockFileLeft, bool StateFileLeft, string[] Log, string[] Governors, string ClockPath, string UncoreAfter );

/// <summary>
/// What the live clock test shows.
/// </summary>
/// <param name="Before">Clock key before.</param>
/// <param name="During">Clock key while machine control held it.</param>
/// <param name="DuringText">The clock while held, as text.</param>
/// <param name="Recorded">What the clock record held while held.</param>
/// <param name="After">Clock key after the run's own restore.</param>
/// <param name="FilesAfter">True when the state file or clock record is left after it.</param>
/// <param name="CrashDuring">Clock key while the "crashed" run held it.</param>
/// <param name="CrashAfter">Clock key after the next start's restore.</param>
/// <param name="CrashFilesAfter">True when a file is left after the crash leg.</param>
/// <param name="Log">The log.</param>
public sealed record LiveClockResult( string Before, string During, string DuringText, string Recorded, string After, bool FilesAfter, string CrashDuring, string CrashAfter, bool CrashFilesAfter, string[] Log );
#endif
