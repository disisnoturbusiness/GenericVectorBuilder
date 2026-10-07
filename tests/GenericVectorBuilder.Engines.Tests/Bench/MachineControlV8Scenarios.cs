// Compiled only by MachineControlTests' compiler, together with the benchmark's own sources and
// the other MachineControl*Scenarios.cs files (this is a fourth part of the same static class).
// BENCH_UNDER_TEST is defined only in that compilation, so the test project itself sees an empty
// file.
#if BENCH_UNDER_TEST
using System.Globalization;
using System.Text.Json;
using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Running;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// The v8 machine-state records: the median clock rule (on the v7 passes and on made-up ones),
/// the structured clock record, the thermal throttle counters, the engine CPU ledger with
/// dockerd's cgroup left out of the engine figure and still subtracted from outside load, and
/// the field names results.json carries. Fake machine only.
/// </summary>
public static partial class MachineControlScenarios
{
   #region Data Members

   private const string ENGINE_GROUP = "/sys/fs/cgroup/system.slice/docker-beef01.scope";
   private const string DOCKERD_GROUP = "/sys/fs/cgroup/system.slice/docker.service";
   private const double SEARCHES_PER_SECOND = 1000.0; // the compose pass's search count follows its measured window, so timer jitter cannot move the ms per search
   private static readonly DateTime T0 = new( 2026, 10, 7, 12, 0, 0, DateTimeKind.Utc );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Judges every v7 pass of the fixture with the median rule, pinned at the given clock, and
   /// recomputes each pass's group medians from its per-CPU medians with the code's own
   /// <see cref="ClockRule.GroupMedian"/>.
   /// </summary>
   /// <param name="fixture">MachineControlV7ClockFixture.PASSES.</param>
   /// <param name="engineCpus">Engine CPUs the runs recorded.</param>
   /// <param name="clientCpus">Client CPUs the runs recorded.</param>
   /// <param name="pinnedMhz">The pinned clock to inject (results.json leaves it out).</param>
   /// <returns>Counts, the Oracle default@8 verdicts, the warnings, and the passes whose recomputed medians differ from the recorded ones.</returns>
   public static V7JudgeResult V7ClockJudge( string fixture, string engineCpus, string clientCpus, int pinnedMhz )
   {
      IReadOnlyList<int> engine = CpuList.Parse( engineCpus );
      IReadOnlyList<int> client = CpuList.Parse( clientCpus );
      var passes = new List<PassConditions>();
      var differ = new List<string>();
      var verdicts = new List<ClockVerdict>();
      foreach( string line in fixture.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
      {
         string[] f = line.Split( '|' );
         List<CpuMhz> cpus = f[5].Split( ',' ).Select( ( m, i ) => new CpuMhz( i, int.Parse( m, CultureInfo.InvariantCulture ), int.Parse( m, CultureInfo.InvariantCulture ), int.Parse( m, CultureInfo.InvariantCulture ) ) ).ToList();
         var pass = new PassConditions { Target = f[1], Pass = f[2], StartUtc = f[0], EngineMhzMedian = int.Parse( f[3], CultureInfo.InvariantCulture ), ClientMhzMedian = int.Parse( f[4], CultureInfo.InvariantCulture ), CpuMhz = cpus, PinnedMhz = pinnedMhz };
         if( ClockRule.GroupMedian( cpus, engine ) != pass.EngineMhzMedian || ClockRule.GroupMedian( cpus, client ) != pass.ClientMhzMedian )
         {
            differ.Add( line );
         }

         passes.Add( pass );
         verdicts.Add( ClockRule.Judge( pass, true ) );
      }

      string[] oracle = passes.Zip( verdicts ).Where( p => p.First.Target == "oracle" && p.First.Pass == "default@8" )
         .Select( p => $"{p.First.StartUtc} {p.First.Target} {p.First.Pass} evaluated={p.Second.Evaluated} read={p.Second.Read} off={p.Second.Off}" ).ToArray();
      return new V7JudgeResult( passes.Count, verdicts.Count( v => v.Evaluated ), verdicts.Count( v => v.Off == true ), verdicts.Count( v => !v.Read ), oracle,
         MachineFlags.ClockWarnings( passes, true ).ToArray(), differ.ToArray() );
   }

   /// <summary>
   /// The rule on made-up passes: an engine median of 3300 MHz against 3500 (flagged off), no
   /// reading (flagged not read), and the unsplit judging (the all-CPU figure in ClientMhzMedian).
   /// </summary>
   /// <returns>One "name read off warning" line per case.</returns>
   public static string[] SyntheticClock()
   {
      PassConditions[] passes =
      {
         new() { Target = "low", Pass = "default@1", EngineMhzMedian = 3300, ClientMhzMedian = 3492, PinnedMhz = 3500 },
         new() { Target = "unread", Pass = "default@1", ClientMhzMedian = 3492, PinnedMhz = 3500 },
         new() { Target = "fallback", Pass = "default@1", ClientMhzMedian = 3492, PinnedMhz = 3500 },
         new() { Target = "fallbacklow", Pass = "default@1", ClientMhzMedian = 3300, PinnedMhz = 3500 },
      };
      bool[] split = { true, true, false, false };
      return passes.Select( ( p, i ) =>
      {
         ClockVerdict v = ClockRule.Judge( p, split[i] );
         return $"{p.Target} read={v.Read} off={v.Off} warning={MachineFlags.ClockWarnings( new[] { p }, split[i] ).FirstOrDefault() ?? "none"}";
      } ).ToArray();
   }

   /// <summary>
   /// A whole run on a fake box whose CPUs are not split (one physical core, two threads): passes
   /// are judged on the all-CPU fallback, at the base clock and then at 3300 MHz.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>Each pass as "pass engine client read off", the run's clock rule, and the warnings.</returns>
   public static async Task<string[][]> UnsplitRunAsync( string folder )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.Lscpu = "CPU SOCKET CORE ONLINE\n0 0 0 yes\n1 0 0 yes\n";
      var options = new MachineControlOptions { StateFile = Path.Combine( folder, "state.json" ), PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 20 ), BusyWait = TimeSpan.FromMilliseconds( 200 ), BusyPoll = TimeSpan.FromMilliseconds( 20 ) };
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "t" } ) );
      MachineControl control = await MachineControl.StartAsync( conditions, options, machine, _ => { }, CancellationToken.None );
      Action<string> log = control.WrapLog( _ => { } );
      await control.EnterTargetAsync( "t", "embedded", null, CancellationToken.None );
      foreach( ( string pass, int khz ) in new[] { ( "default@1", 3_491_000 ), ( "default@8", 3_300_000 ) } )
      {
         Enumerable.Range( 0, 8 ).ToList().ForEach( c => machine.Set( GovernorControl.FrequencyPath( c ), khz.ToString( CultureInfo.InvariantCulture ) + "\n" ) );
         log( $"  t: warm-up before {pass}, 7 searches" );
         log( $"  t: timing {pass}" );
         await Task.Delay( 150 );
         log( "  t: p50 1.00 ms, p95 2.00 ms over 300 searches, 9.0 QPS with one searcher, recall@10 1.000" );
      }

      await control.LeaveTargetAsync( "t" );
      await control.RestoreAsync();
      control.Dispose();
      string[] passes = control.Conditions.Passes.Select( p => $"{p.Pass} engine={p.EngineMhzMedian?.ToString( CultureInfo.InvariantCulture ) ?? "null"} client={p.ClientMhzMedian} read={p.ClockRead} off={p.ClockOff}" ).ToArray();
      return new[] { passes, new[] { control.Conditions.Clock?.Rule ?? "no rule" }, MachineFlags.ClockWarnings( control.Conditions.Passes, MachineFlags.IsSplit( control.Conditions ) ).ToArray() };
   }

   /// <summary>
   /// The thermal throttle reading and rises on hand-set counters: siblings counted once, the
   /// package once, a rise on one sibling only, a counter that went down, an unreadable counter,
   /// and a CPU whose topology cannot be read.
   /// </summary>
   /// <returns>One line per case.</returns>
   public static string[] ThrottleCases()
   {
      FakeMachine machine = FakeMachine.Linus();
      int[] cpus = Enumerable.Range( 0, 8 ).ToArray();
      Enumerable.Range( 0, 8 ).ToList().ForEach( c => machine.SetThrottle( c, c % 4 == 0 ? 5 : 0, 7 ) );
      ThrottleCounts first = ThermalThrottle.Read( machine, cpus );
      machine.SetThrottle( 4, 6, 7 );
      ThrottleCounts siblingRose = ThermalThrottle.Read( machine, cpus );
      machine.SetThrottle( 1, 0, 3 );
      ThrottleCounts wentDown = ThermalThrottle.Read( machine, cpus );
      machine.Remove( ThermalThrottle.CorePath( 3 ) );
      ThrottleCounts unreadable = ThermalThrottle.Read( machine, cpus );
      machine.SetThrottle( 3, 0, 7 );
      machine.Remove( ThermalThrottle.SiblingsPath( 2 ) );
      ThrottleCounts noTopology = ThermalThrottle.Read( machine, cpus );
      return new[]
      {
         $"first core={first.CoreEvents} package={first.PackageEvents} unreadable={first.Unreadable ?? "none"}",
         $"sibling rise={ThrottleRise.Between( first, siblingRose ).Describe()} rose={ThrottleRise.Between( first, siblingRose ).Rose}",
         $"none rise={ThrottleRise.Between( first, first ).Describe()} rose={ThrottleRise.Between( first, first ).Rose}",
         $"down rise={ThrottleRise.Between( siblingRose, wentDown ).Describe()} rose={ThrottleRise.Between( siblingRose, wentDown ).Rose}",
         $"unreadable core={unreadable.CoreEvents?.ToString( CultureInfo.InvariantCulture ) ?? "null"} why={unreadable.Unreadable} rise={ThrottleRise.Between( first, unreadable ).Describe()} risecore={ThrottleRise.Between( first, unreadable ).CoreRise?.ToString( CultureInfo.InvariantCulture ) ?? "null"}",
         $"notopology core={noTopology.CoreEvents} note={noTopology.Note}",
      };
   }

   /// <summary>
   /// The throttle counters in a run: read at the start, at the target's start, at its first
   /// warm-up line (a second warm-up line of the same target does not read again), at its end and
   /// at the end of the run, and never while a pass is timed. A rise during the load (before the
   /// first warm-up) shows only in the whole-target figure, a rise during the passes in both.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>The throttleByTarget JSON, the flags, and the throttle files read while a pass was timed.</returns>
   public static async Task<string[]> ThrottleRunAsync( string folder )
   {
      FakeMachine machine = FakeMachine.Linus();
      var options = new MachineControlOptions { StateFile = Path.Combine( folder, "state.json" ), PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 20 ), BusyWait = TimeSpan.FromMilliseconds( 200 ), BusyPoll = TimeSpan.FromMilliseconds( 20 ) };
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "a,b" } ) );
      MachineControl control = await MachineControl.StartAsync( conditions, options, machine, _ => { }, CancellationToken.None );
      Action<string> log = control.WrapLog( _ => { } );
      var timedReads = new List<string>();
      await control.EnterTargetAsync( "a", "embedded", null, CancellationToken.None );
      machine.SetThrottle( 5, 1, 0 );
      await ThrottlePassAsync( log, machine, "a", timedReads );
      await control.LeaveTargetAsync( "a" );
      await control.EnterTargetAsync( "b", "embedded", null, CancellationToken.None );
      log( "  b: warm-up before default@1, 7 searches" );
      machine.SetThrottle( 6, 1, 2 );
      await ThrottlePassAsync( log, machine, "b", timedReads );
      await control.LeaveTargetAsync( "b" );
      await control.RestoreAsync();
      control.Dispose();
      var report = new BenchReport();
      control.Annotate( report );
      var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
      return new[] { JsonSerializer.Serialize( control.Conditions.ThrottleByTarget, json ), JsonSerializer.Serialize( control.Conditions.Clock!.Throttle, json ), string.Join( "\n", report.Notes ), string.Join( "\n", timedReads ) };
   }

   /// <summary>
   /// The engine CPU ledger on hand-set counters, sampled every 250 ms for 5 s: the engine's
   /// cgroup uses 2.0 CPUs, dockerd's 0.08, this process 0.5 and an outside process 0.1. The engine
   /// figure leaves dockerd out, dockerd's is its own, and the outside load still subtracts both.
   /// Then the cases with no figure: a window past the samples, one shorter than four samples, one
   /// across a change of the followed cgroups, one with a cgroup unread at an edge, one with no
   /// engine cgroup, one whose counter went down; and a real zero (an idle engine) that stays 0.
   /// </summary>
   /// <returns>One line per case.</returns>
   public static string[] LedgerCases()
   {
      ( MachineSampler sampler, FakeMachine machine ) = LedgerRun( 21, ( step, m ) => SetLedgerStep( m, step, 2_000_000, 80_000 ) );
      EngineCpu window = sampler.EngineCpuBetween( T0.AddSeconds( 1.1 ), T0.AddSeconds( 3.3 ) );
      EngineCpu edges = sampler.EngineCpuBetween( T0.AddSeconds( 0.13 ), T0.AddSeconds( 4.87 ) );
      double? outside = sampler.OutsideLoadBetween( T0, T0.AddSeconds( 5 ) );
      string Line( string name, EngineCpu e ) => string.Create( CultureInfo.InvariantCulture, $"{name} engine={e.EngineSeconds?.ToString( "0.000000", CultureInfo.InvariantCulture ) ?? "null"} dockerd={e.DockerdSeconds?.ToString( "0.000000", CultureInfo.InvariantCulture ) ?? "null"} problem={e.Problem ?? "none"}" );
      var lines = new List<string> { Line( "window", window ), Line( "edges", edges ), string.Create( CultureInfo.InvariantCulture, $"outside={outside:0.000000}" ),
         Line( "past", sampler.EngineCpuBetween( T0.AddSeconds( 4 ), T0.AddSeconds( 6 ) ) ), Line( "before", sampler.EngineCpuBetween( T0.AddSeconds( -1 ), T0.AddSeconds( 2 ) ) ),
         Line( "short", sampler.EngineCpuBetween( T0.AddSeconds( 1 ), T0.AddSeconds( 1.5 ) ) ) };
      ( MachineSampler changed, _ ) = LedgerRun( 21, ( step, m ) => SetLedgerStep( m, step, 2_000_000, 80_000 ), step => step == 10 ? new[] { ENGINE_GROUP, DOCKERD_GROUP, "/sys/fs/cgroup/system.slice/docker-cafe02.scope" } : null );
      lines.Add( Line( "changed", changed.EngineCpuBetween( T0.AddSeconds( 1.1 ), T0.AddSeconds( 3.3 ) ) ) );
      lines.Add( Line( "beforechange", changed.EngineCpuBetween( T0.AddSeconds( 0.1 ), T0.AddSeconds( 2.2 ) ) ) );
      ( MachineSampler missing, _ ) = LedgerRun( 21, ( step, m ) => { SetLedgerStep( m, step, 2_000_000, 80_000 ); if( step == 13 ) { m.Remove( ENGINE_GROUP + "/cpu.stat" ); } } );
      lines.Add( Line( "missing", missing.EngineCpuBetween( T0.AddSeconds( 1.1 ), T0.AddSeconds( 3.3 ) ) ) );
      lines.Add( Line( "missingelsewhere", missing.EngineCpuBetween( T0.AddSeconds( 0.1 ), T0.AddSeconds( 2.2 ) ) ) );
      ( MachineSampler dockerdOnly, _ ) = LedgerRun( 21, ( step, m ) => SetLedgerStep( m, step, 2_000_000, 80_000 ), step => step == 0 ? new[] { DOCKERD_GROUP } : null );
      lines.Add( Line( "nogroup", dockerdOnly.EngineCpuBetween( T0.AddSeconds( 1.1 ), T0.AddSeconds( 3.3 ) ) ) );
      ( MachineSampler down, _ ) = LedgerRun( 21, ( step, m ) => SetLedgerStep( m, step, step >= 9 ? -4_000_000 : 2_000_000, 80_000 ) );
      lines.Add( Line( "down", down.EngineCpuBetween( T0.AddSeconds( 1.1 ), T0.AddSeconds( 3.3 ) ) ) );
      ( MachineSampler idle, _ ) = LedgerRun( 21, ( step, m ) => SetLedgerStep( m, step, 0, 0 ) );
      lines.Add( Line( "idle", idle.EngineCpuBetween( T0.AddSeconds( 1.1 ), T0.AddSeconds( 3.3 ) ) ) );
      lines.Add( machine.ReadFile( DOCKERD_GROUP + "/cpu.stat" )?.Split( '\n' )[0] ?? "no dockerd stat" );
      return lines.ToArray();
   }

   /// <summary>
   /// The pass-level figures from the ledger and a pass note written by the real
   /// TargetRunner.DescribePass, and the null-with-reason cases: an embedded engine, and a pass
   /// with no note.
   /// </summary>
   /// <returns>One line per pass: name, engine ms per search, dockerd ms per search, CPUs busy, reason.</returns>
   public static string[] LedgerPasses()
   {
      ( MachineSampler sampler, _ ) = LedgerRun( 21, ( step, m ) => SetLedgerStep( m, step, 2_000_000, 80_000 ) );
      string note = TargetRunner.DescribePass( new PassRecord( "default@8", "settle", T0.AddMilliseconds( 1100 ), T0.AddMilliseconds( 3300 ), 2.2, 4400, 0, 1.0, 1.1, 2.0, 2000 ) );
      PassConditions[] passes = { new() { Target = "t", Pass = "default@8", StartUtc = "2026-10-07T12:00:01.100Z" }, new() { Target = "t", Pass = "exact" } };
      sampler.AttachEngineCpu( passes, new[] { note }, null );
      PassConditions[] embedded = { new() { Target = "duckdb", Pass = "default@8", StartUtc = "2026-10-07T12:00:01.100Z" } };
      sampler.AttachEngineCpu( embedded, new[] { note }, "embedded engine: test" );
      return passes.Concat( embedded ).Select( p => string.Create( CultureInfo.InvariantCulture,
         $"{p.Target} {p.Pass} engine={p.EngineCpuMsPerSearch?.ToString( "0.0000", CultureInfo.InvariantCulture ) ?? "null"} dockerd={p.DockerdCpuMsPerSearch?.ToString( "0.0000", CultureInfo.InvariantCulture ) ?? "null"} busy={p.EngineCpusBusy?.ToString( "0.0000", CultureInfo.InvariantCulture ) ?? "null"} reason={p.EngineCpuNullReason ?? "none"}" ) ).ToArray();
   }

   /// <summary>
   /// What the sampler allocates per sample for the ledgers: 10,000 samples it keeps against the
   /// same 10,000 reads refused as not newer than the last (the control, which does every read and
   /// keeps nothing), with two cgroups followed; and 10,000 ledger appends on their own.
   /// </summary>
   /// <returns>Bytes per kept sample above the control, bytes of the 10,000 appends, and the ledger's count.</returns>
   public static double[] LedgerAllocation()
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.StaticCounters = true;
      SetLedgerStep( machine, 0, 2_000_000, 80_000 );
      var sampler = new MachineSampler( machine, Enumerable.Range( 0, 8 ).ToArray(), 100, TimeSpan.FromMilliseconds( 250 ) );
      sampler.SetEngineGroups( new[] { DOCKERD_GROUP, ENGINE_GROUP }, DOCKERD_GROUP );
      sampler.Tick( T0 );
      for( int warm = 1; warm <= 50; warm++ )
      {
         sampler.Tick( T0.AddMilliseconds( 250 * warm ) );
      }

      long before = GC.GetAllocatedBytesForCurrentThread();
      for( int i = 0; i < 10_000; i++ )
      {
         sampler.Tick( T0 );
      }

      long control = GC.GetAllocatedBytesForCurrentThread() - before;
      before = GC.GetAllocatedBytesForCurrentThread();
      for( int i = 0; i < 10_000; i++ )
      {
         sampler.Tick( T0.AddMilliseconds( 250 * ( 51 + i ) ) );
      }

      long kept = GC.GetAllocatedBytesForCurrentThread() - before;
      var ledger = new EngineCpuLedger( 1 << 15 );
      before = GC.GetAllocatedBytesForCurrentThread();
      for( int i = 0; i < 10_000; i++ )
      {
         ledger.Append( new EngineCpuSample( i, 1, i * 0.5, 1, 1, 0 ) );
      }

      long appends = GC.GetAllocatedBytesForCurrentThread() - before;
      return new[] { ( kept - control ) / 10_000.0, appends, ledger.Count, sampler.EngineCpuBetween( T0.AddSeconds( 2 ), T0.AddSeconds( 100 ) ).EngineSeconds ?? -1 };
   }

   /// <summary>
   /// The outside load against the v7 code's own arithmetic (f662f82 MachineControlSampler.cs,
   /// AddLoad and ReadCpu, copied below as <see cref="V7Outside"/>): 2,000 samples of seeded random
   /// counters with dockerd's cgroup and the engine's followed as in a compose engine's turn, the
   /// followed set changing every 300 samples and a cgroup unreadable now and then. Every interval's
   /// outside load must be the same double.
   /// </summary>
   /// <returns>Intervals compared, intervals that differ, and the first difference.</returns>
   public static string[] OutsideParity()
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.StaticCounters = true;
      var sampler = new MachineSampler( machine, new[] { 0 }, 100, TimeSpan.FromMilliseconds( 250 ) );
      var random = new Random( 801 );
      long busy = 10_000, self = 500, child = 30, engine = 7_000_000, dockerd = 900_000;
      var v7 = new V7Outside( 100 );
      var mine = new List<(DateTime From, DateTime To)>();
      for( int step = 0; step < 2000; step++ )
      {
         string[] groups = ( step / 300 ) % 2 == 0 ? new[] { DOCKERD_GROUP, ENGINE_GROUP } : new[] { ENGINE_GROUP };
         sampler.SetEngineGroups( groups, groups.Contains( DOCKERD_GROUP ) ? DOCKERD_GROUP : null );
         busy += random.Next( 0, 120 );
         self += random.Next( 0, 30 );
         child += random.Next( 0, 3 );
         engine += random.Next( 0, 600_000 );
         dockerd += random.Next( 0, 40_000 );
         machine.SetCounters( busy, self, 0, softirqTicks: step * 3, irqTicks: step, childTicks: child );
         machine.SetGroupUsage( ENGINE_GROUP, engine );
         machine.SetGroupUsage( DOCKERD_GROUP, dockerd );
         if( step % 97 == 50 )
         {
            machine.Remove( DOCKERD_GROUP + "/cpu.stat" );
         }

         DateTime at = T0.AddMilliseconds( 250 * step );
         sampler.Tick( at );
         v7.Add( at, machine, groups.ToList(), sampler.Accounting );
      }

      return Compare( sampler, v7 );
   }

   /// <summary>
   /// A whole run on the fake machine with a compose target (its container's cgroup burns 1.5
   /// CPUs during its turn and dockerd's 0.1 all the time), an embedded target, and an always-on
   /// target with no lever: the compose pass gets engine and dockerd CPU per search and CPUs busy,
   /// and its outside load stays near zero (dockerd subtracted as benchmark work, as in v7); the
   /// others get null with a reason, and dockerd's 0.1 CPU is outside load during their turns (as
   /// in v7). Writes results.json with the real results writer before and after the end of the run.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>results.json after the last target and after the run, the engine CPU rule, and the run's flags.</returns>
   public static async Task<string[]> ContractRunAsync( string folder )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.AddProcess( 1680, "dockerd", 1000, 2, "0-7", "/system.slice/docker.service" );
      machine.Set( DOCKERD_GROUP + "/cgroup.procs", "1680\n1999\n" );
      machine.AddProcess( 1999, "docker-proxy", 1100, 1, "0-7", "/system.slice/docker.service" );
      machine.AddProcess( 2100, "containerd-shim", 1200, 1, "0-7", "/system.slice/containerd.service" );
      machine.Containers["beef01"] = new FakeContainer( "gvb-redis", string.Empty, 901, true );
      machine.AddProcess( 901, "redis-server", 6000, 2, "0-7", "/system.slice/docker-beef01.scope" );
      machine.ComposeRunning["/x/redis.compose.yaml"] = new[] { "beef01" };
      machine.SetGroupRate( ENGINE_GROUP, 1.5 );
      machine.SetGroupRate( DOCKERD_GROUP, 0.1 );
      var options = new MachineControlOptions { StateFile = Path.Combine( folder, "state.json" ), PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 20 ), BusyWindow = TimeSpan.FromSeconds( 1 ), RecentWindow = TimeSpan.FromMilliseconds( 300 ), BusyWait = TimeSpan.FromMilliseconds( 400 ), BusyPoll = TimeSpan.FromMilliseconds( 20 ) };
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "redis,duckdb,other" } ) );
      MachineControl control = await MachineControl.StartAsync( conditions, options, machine, _ => { }, CancellationToken.None );
      Action<string> log = control.WrapLog( _ => { } );
      var report = new BenchReport { Pipeline = "p", RunSeed = 801 };
      foreach( ( string name, string hosting, string? compose ) in new[] { ( "redis", "compose", (string?)"/x/redis.compose.yaml" ), ( "duckdb", "embedded", null ), ( "other", "always-on", null ) } )
      {
         report.Targets.Add( await ContractTargetAsync( control, log, name, hosting, compose ) );
         machine.SetGroupRate( ENGINE_GROUP, 0 );
         ResultsWriter.Write( report, folder );
         MachineFlags.WriteInto( folder, control.Conditions );
      }

      string beforeEnd = File.ReadAllText( Path.Combine( folder, "results.json" ) );
      await control.RestoreAsync();
      control.Dispose();
      control.Annotate( report );
      ResultsWriter.Write( report, folder );
      MachineFlags.WriteInto( folder, control.Conditions );
      return new[] { beforeEnd, File.ReadAllText( Path.Combine( folder, "results.json" ) ), control.Conditions.EngineCpu?.Rule ?? "no rule", string.Join( "\n", control.Conditions.Flags ) };
   }

   /// <summary>
   /// A target whose containers cannot be listed (docker compose ps fails when it is pinned): the
   /// pinning problem becomes every pass's reason for no engine CPU per search.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>The pass's engine CPU figure and reason, and the run's flags.</returns>
   public static async Task<string[]> PinFailsRunAsync( string folder )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.AddProcess( 1680, "dockerd", 1000, 2, "0-7", "/system.slice/docker.service" );
      machine.Fail = call => call.Contains( "compose -f /x/redis.compose.yaml ps", StringComparison.Ordinal ) ? new ShellResult( 1, string.Empty, "compose ps failed" ) : null;
      var options = new MachineControlOptions { StateFile = Path.Combine( folder, "state.json" ), PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 20 ), BusyWait = TimeSpan.FromMilliseconds( 200 ), BusyPoll = TimeSpan.FromMilliseconds( 20 ) };
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "redis" } ) );
      MachineControl control = await MachineControl.StartAsync( conditions, options, machine, _ => { }, CancellationToken.None );
      TargetReport result = await ContractTargetAsync( control, control.WrapLog( _ => { } ), "redis", "compose", "/x/redis.compose.yaml" );
      await control.RestoreAsync();
      control.Dispose();
      var report = new BenchReport { Targets = { result } };
      control.Annotate( report );
      PassConditions pass = control.Conditions.Passes.Single();
      return new[] { pass.EngineCpuMsPerSearch?.ToString( CultureInfo.InvariantCulture ) ?? "null", pass.EngineCpuNullReason ?? "none", string.Join( "\n", control.Conditions.Flags ) };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// One target of <see cref="ContractRunAsync"/>: entered, one timed pass of about 400 ms with
   /// its result line, left, and annotated with a pass note from the real TargetRunner.DescribePass.
   /// </summary>
   /// <param name="control">Machine control.</param>
   /// <param name="log">The wrapped log.</param>
   /// <param name="name">Target name.</param>
   /// <param name="hosting">compose, embedded or always-on.</param>
   /// <param name="compose">Compose file or null.</param>
   /// <returns>The annotated target report.</returns>
   private static async Task<TargetReport> ContractTargetAsync( MachineControl control, Action<string> log, string name, string hosting, string? compose )
   {
      await control.EnterTargetAsync( name, hosting, compose, CancellationToken.None );
      await Task.Delay( 300 );
      log( $"  {name}: warm-up before default@8, 7 searches" );
      log( $"  {name}: timing default@8" );
      DateTime start = DateTime.UtcNow;
      await Task.Delay( 400 );
      DateTime end = DateTime.UtcNow;
      log( $"  {name}: 812.5 QPS at concurrency 8" );
      await control.LeaveTargetAsync( name );
      var result = new TargetReport { Name = name, Search = new SearchReport(), PassOrder = new List<string> { "default@8" } };
      double seconds = ( end - start ).TotalSeconds;
      int searches = (int)Math.Round( seconds * SEARCHES_PER_SECOND );
      result.Notes.Add( TargetRunner.DescribePass( new PassRecord( "default@8", "settle", start, end, seconds, searches, 0, 1.2, 1.3, 2.0, searches / seconds ) ) );
      control.AnnotateTarget( result );
      return result;
   }

   /// <summary>
   /// One timed pass whose warm-up, timing and result lines are logged while the fake machine
   /// records which files are read; the throttle files read between the timing and the result
   /// line are added to the list.
   /// </summary>
   /// <param name="log">The wrapped log.</param>
   /// <param name="machine">The fake machine.</param>
   /// <param name="target">Target name.</param>
   /// <param name="timedReads">Receives throttle files read inside the timed window.</param>
   private static async Task ThrottlePassAsync( Action<string> log, FakeMachine machine, string target, List<string> timedReads )
   {
      log( $"  {target}: warm-up before default@1, 7 searches" );
      machine.TrackReads = true;
      log( $"  {target}: timing default@1" );
      await Task.Delay( 150 );
      log( $"  {target}: p50 1.00 ms, p95 2.00 ms over 300 searches, 9.0 QPS with one searcher, recall@10 1.000" );
      machine.TrackReads = false;
      lock( machine.Reads )
      {
         timedReads.AddRange( machine.Reads.Select( r => r.Path ).Where( p => p.Contains( "thermal_throttle", StringComparison.Ordinal ) || p.Contains( "topology", StringComparison.Ordinal ) ) );
         machine.Reads.Clear();
      }
   }

   /// <summary>
   /// A sampler over hand-set counters, ticked every 250 ms from <see cref="T0"/>, following
   /// dockerd's cgroup and the engine's.
   /// </summary>
   /// <param name="steps">Samples.</param>
   /// <param name="set">Sets the counters for a step.</param>
   /// <param name="groupsAt">The followed cgroups to set before a step, or null to keep them.</param>
   /// <returns>The sampler and its machine.</returns>
   private static (MachineSampler Sampler, FakeMachine Machine) LedgerRun( int steps, Action<int, FakeMachine> set, Func<int, string[]?>? groupsAt = null )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.StaticCounters = true;
      var sampler = new MachineSampler( machine, new[] { 0, 1 }, 100, TimeSpan.FromMilliseconds( 250 ) );
      sampler.SetEngineGroups( new[] { DOCKERD_GROUP, ENGINE_GROUP }, DOCKERD_GROUP );
      for( int step = 0; step < steps; step++ )
      {
         if( groupsAt?.Invoke( step ) is string[] groups )
         {
            sampler.SetEngineGroups( groups, groups.Contains( DOCKERD_GROUP ) ? DOCKERD_GROUP : null );
         }

         set( step, machine );
         sampler.Tick( T0.AddMilliseconds( 250 * step ) );
      }

      return ( sampler, machine );
   }

   /// <summary>
   /// The ledger counters at a step (250 ms each): the engine's cgroup at its rate, dockerd's at
   /// its rate, this process 0.5 CPU, and the machine busy with all of them plus 0.1 CPU outside.
   /// </summary>
   /// <param name="machine">The fake machine.</param>
   /// <param name="step">The step.</param>
   /// <param name="engineUsecPerSecond">The engine cgroup's microseconds per second.</param>
   /// <param name="dockerdUsecPerSecond">dockerd's microseconds per second.</param>
   private static void SetLedgerStep( FakeMachine machine, int step, long engineUsecPerSecond, long dockerdUsecPerSecond )
   {
      double second = step / 4.0;
      long engine = 7_000_000 + (long)Math.Round( second * engineUsecPerSecond );
      long dockerd = 900_000 + (long)Math.Round( second * dockerdUsecPerSecond );
      machine.SetCounters( busyTicks: (long)Math.Round( 10_000 + second * ( 10 + 50 + Math.Max( 0, engineUsecPerSecond ) / 10_000.0 + dockerdUsecPerSecond / 10_000.0 ) ), selfTicks: (long)Math.Round( 500 + second * 50 ), groupUsec: 0 );
      machine.SetGroupUsage( ENGINE_GROUP, engine );
      machine.SetGroupUsage( DOCKERD_GROUP, dockerd );
   }

   /// <summary>
   /// Compares every interval's outside load of the sampler with the v7 arithmetic's.
   /// </summary>
   /// <param name="sampler">The sampler.</param>
   /// <param name="v7">The v7 arithmetic's intervals.</param>
   /// <returns>Intervals compared, intervals that differ, and the first difference.</returns>
   private static string[] Compare( MachineSampler sampler, V7Outside v7 )
   {
      int differ = 0;
      string first = "none";
      foreach( ( DateTime from, DateTime to, double load ) in v7.Intervals )
      {
         double? mine = sampler.OutsideLoadBetween( from, to );
         if( mine is not double value || BitConverter.DoubleToInt64Bits( value ) != BitConverter.DoubleToInt64Bits( load ) )
         {
            differ++;
            first = first == "none" ? string.Create( CultureInfo.InvariantCulture, $"{to:HH:mm:ss.fff}: v7 {load:R}, now {mine?.ToString( "R", CultureInfo.InvariantCulture ) ?? "null"}" ) : first;
         }
      }

      int none = 0;
      for( int step = 1; step < 2000; step++ )
      {
         DateTime to = T0.AddMilliseconds( 250 * step );
         none += v7.Intervals.Any( i => i.To == to ) || sampler.OutsideLoadBetween( to.AddMilliseconds( -250 ), to ) == null ? 0 : 1;
      }

      return new[] { v7.Intervals.Count.ToString( CultureInfo.InvariantCulture ), differ.ToString( CultureInfo.InvariantCulture ), first, none.ToString( CultureInfo.InvariantCulture ) };
   }

   #endregion Private Methods
}

/// <summary>
/// The outside-load arithmetic of the v7 code (f662f82, MachineControlSampler.cs ReadCpu, Busy
/// and AddLoad), copied as it was, fed the same files the sampler reads. Why a copy: the v8 code
/// rewrote AddLoad as loops to stop allocating per sample, and the busy flags and outside loads
/// of v8 must stay the v7 accounting (dockerd's cgroup subtracted as benchmark work).
/// </summary>
public sealed class V7Outside
{
   #region Data Members

   private readonly double _clockTicks;
   private (DateTime At, double Busy, double Self, Dictionary<string, double> Groups)? _last;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the copy.
   /// </summary>
   /// <param name="clockTicks">USER_HZ.</param>
   public V7Outside( double clockTicks )
   {
      _clockTicks = clockTicks;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Each kept interval: its start, end and outside load (CPUs).</summary>
   public List<(DateTime From, DateTime To, double Load)> Intervals { get; } = new();

   /// <summary>
   /// Reads the counters the way v7's ReadCpu did and files the interval the way v7's AddLoad did.
   /// </summary>
   /// <param name="now">Sample time.</param>
   /// <param name="system">The machine.</param>
   /// <param name="groups">The followed cgroups.</param>
   /// <param name="accounting">Which columns count as busy.</param>
   public void Add( DateTime now, IMachineSystem system, List<string> groups, CpuAccounting accounting )
   {
      string[] total = system.ReadFile( "/proc/stat" )!.Split( '\n' )[0].Split( ' ', StringSplitOptions.RemoveEmptyEntries );
      string[] self = ProcessInfo.StatFields( system, system.ProcessId )!;
      double client = ( Ticks( self[11] ) + Ticks( self[12] ) ) / _clockTicks;
      double own = client + ( Ticks( self[13] ) + Ticks( self[14] ) ) / _clockTicks;
      var usage = new Dictionary<string, double>( StringComparer.Ordinal );
      foreach( string group in groups )
      {
         string? line = system.ReadFile( group + "/cpu.stat" )?.Split( '\n' ).FirstOrDefault( l => l.StartsWith( "usage_usec ", StringComparison.Ordinal ) );
         if( line != null )
         {
            usage[group] = Ticks( line["usage_usec ".Length..] ) / 1_000_000.0;
         }
      }

      double Column( int index ) => index < total.Length ? Ticks( total[index] ) : 0;
      double busy = Column( 1 ) + Column( 2 ) + Column( 3 );
      busy += accounting.CountIrq ? Column( 6 ) + Column( 7 ) : 0;
      busy += accounting.CountSteal ? Column( 8 ) : 0;
      busy /= _clockTicks;
      if( _last is { } last && usage.Keys.ToHashSet( StringComparer.Ordinal ).SetEquals( last.Groups.Keys ) )
      {
         double engine = usage.Where( g => last.Groups.ContainsKey( g.Key ) ).Sum( g => g.Value - last.Groups[g.Key] );
         double outside = ( busy - last.Busy ) - ( own - last.Self ) - engine;
         Intervals.Add( ( last.At, now, outside / ( now - last.At ).TotalSeconds ) );
      }

      _last = ( now, busy, own, usage );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Parses a counter as v7 did.
   /// </summary>
   /// <param name="text">Digits.</param>
   /// <returns>The value.</returns>
   private static double Ticks( string text )
   {
      return long.Parse( text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture );
   }

   #endregion Private Methods
}

/// <summary>What judging the v7 passes with the median rule found.</summary>
/// <param name="Passes">Passes in the fixture.</param>
/// <param name="Evaluated">Passes judged against a pinned clock.</param>
/// <param name="Off">Passes judged off.</param>
/// <param name="Unread">Passes with a group unread.</param>
/// <param name="Oracle">The Oracle default@8 verdicts.</param>
/// <param name="Warnings">The clock warnings the passes give.</param>
/// <param name="Differ">Fixture lines whose recomputed group medians differ from the recorded ones.</param>
public sealed record V7JudgeResult( int Passes, int Evaluated, int Off, int Unread, string[] Oracle, string[] Warnings, string[] Differ );
#endif
