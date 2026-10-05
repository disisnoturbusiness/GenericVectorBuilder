// Compiled only by MachineControlTests and MachineControlLiveTests, together with the
// benchmark's own sources and MachineControlScenarios.cs (this is a second part of the same
// static class). BENCH_UNDER_TEST is defined only in that compilation, so the test project
// itself sees an empty file.
#if BENCH_UNDER_TEST
using System.Globalization;
using System.Net.Sockets;
using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Running;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// The outside-load accounting scenarios: which /proc/stat columns count as busy, negative
/// values kept and flagged, the client's CPU per search per pass, the usage text, and the live
/// proof that the figure tracks a synthetic outside load during an 8-searcher pass.
/// </summary>
public static partial class MachineControlScenarios
{
   #region Data Members

   private const string RELEASE = "6.8.0-test";
   private const string ENGINE_SCRIPT = @"import asyncio, signal, sys
signal.alarm(int(sys.argv[2]))
async def echo(reader, writer):
    try:
        while True:
            data = await reader.readexactly(2048)
            writer.write(data)
            await writer.drain()
    except Exception:
        writer.close()
async def main():
    server = await asyncio.start_server(echo, '127.0.0.1', 0)
    open(sys.argv[1], 'w').write(str(server.sockets[0].getsockname()[1]))
    async with server:
        await server.serve_forever()
asyncio.run(main())
";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The outside CPU on counters shaped like the real box: per second the machine's user + nice +
   /// system is 3.0 CPUs and its softirq column 0.4, this process used 0.9 and the engine's cgroup
   /// 2.4 (each including the softirq time that hit it, which is what a kernel without
   /// CONFIG_IRQ_TIME_ACCOUNTING counts), and one outside process used 0.1. A kernel built with it
   /// counts none of the softirq in the process and the cgroup (0.8 and 2.1). The right answer is
   /// 0.1 in every case; counting only user + nice + system gives -0.3 on the first.
   /// </summary>
   /// <param name="kernel">"default" (the option is not set), "irqtime" (CONFIG_IRQ_TIME_ACCOUNTING=y) or "unreadable" (no kernel config).</param>
   /// <returns>The outside CPUs over 4 s, the accounting's description, and whether it adds irq.</returns>
   public static AccountingResult SamplerAccounting( string kernel )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.StaticCounters = true;
      if( kernel != "unreadable" )
      {
         machine.Set( "/proc/sys/kernel/osrelease", RELEASE + "\n" );
         machine.Set( "/boot/config-" + RELEASE, "CONFIG_HZ=1000\n" + ( kernel == "irqtime" ? "CONFIG_IRQ_TIME_ACCOUNTING=y\n" : "# CONFIG_IRQ_TIME_ACCOUNTING is not set\n" ) + "# CONFIG_PARAVIRT_TIME_ACCOUNTING is not set\n" );
      }

      var sampler = new MachineSampler( machine, new[] { 0, 1 }, 100, TimeSpan.FromMilliseconds( 250 ) );
      sampler.SetEngineGroups( new[] { "/sys/fs/cgroup/system.slice/qdrant.service" } );
      bool irqInRunTime = kernel != "irqtime";
      var t0 = new DateTime( 2026, 10, 5, 12, 0, 0, DateTimeKind.Utc );
      for( int step = 0; step <= 16; step++ )
      {
         double second = step / 4.0;
         machine.SetCounters( busyTicks: (long)( 10_000 + second * 300 ), selfTicks: (long)( 500 + second * ( irqInRunTime ? 90 : 80 ) ),
            groupUsec: (long)( 7_000_000 + second * ( irqInRunTime ? 2_400_000 : 2_100_000 ) ), softirqTicks: (long)( 2_000 + second * 40 ) );
         sampler.Tick( t0.AddMilliseconds( 250 * step ) );
      }

      return new AccountingResult( sampler.OutsideLoadBetween( t0, t0.AddSeconds( 4 ) ), sampler.Accounting.Description, sampler.Accounting.CountIrq, sampler.LastError );
   }

   /// <summary>
   /// Counters that cannot be right (the process and the engine use 0.3 CPUs more than the machine
   /// was busy): the load comes back as -0.3, not 0, and it flags. Also the limit itself: a pass
   /// at -0.04 and one at exactly -0.05 are not mismatches, one at -0.051 is, and each of the
   /// three recorded loads is checked.
   /// </summary>
   /// <returns>The sampler's load, then per pass "name mismatch=bool flagged=bool".</returns>
   public static MismatchResult SamplerNegative()
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.StaticCounters = true;
      var sampler = new MachineSampler( machine, new[] { 0, 1 }, 100, TimeSpan.FromMilliseconds( 250 ) );
      sampler.SetEngineGroups( new[] { "/sys/fs/cgroup/system.slice/qdrant.service" } );
      var t0 = new DateTime( 2026, 10, 5, 12, 0, 0, DateTimeKind.Utc );
      for( int step = 0; step <= 16; step++ )
      {
         double second = step / 4.0;
         machine.SetCounters( busyTicks: (long)( 10_000 + second * 300 ), selfTicks: (long)( 500 + second * 90 ), groupUsec: (long)( 7_000_000 + second * 2_400_000 ) );
         sampler.Tick( t0.AddMilliseconds( 250 * step ) );
      }

      var c = new MachineConditions { BuildConfiguration = "Release", MachineControl = "on", Governor = "performance", GovernorAtEnd = "performance", StateFile = "/s.json" };
      ( string Name, double? Start, double? Recent, double? During )[] cases =
      {
         ( "ok", 0.1, 0.1, 0.1 ), ( "minus04", 0.1, 0.1, -0.04 ), ( "minus05", 0.1, 0.1, -0.05 ), ( "minus051", 0.1, 0.1, -0.051 ),
         ( "start", -0.2, 0.1, 0.1 ), ( "recent", 0.1, -0.2, 0.1 ), ( "during", 0.1, 0.1, -0.2 ),
      };
      foreach( ( string name, double? start, double? recent, double? during ) in cases )
      {
         var pass = new PassConditions { Target = "a", Pass = name, Governor = "performance", OutsideLoadAtStart = start, OutsideLoadRecentAtStart = recent, OutsideLoadDuring = during };
         pass.ApplyClientCpu( 0.5, 1000, "basis", null );
         c.Passes.Add( pass );
      }

      string[] flags = MachineFlags.Compute( c, new[] { "a" } ).ToArray();
      string[] perPass = c.Passes.Select( p => $"{p.Pass} mismatch={p.AccountingMismatch} flagged={flags.Any( f => f.StartsWith( $"WARNING: accounting mismatch in a {p.Pass}:", StringComparison.Ordinal ) )}" ).ToArray();
      return new MismatchResult( sampler.OutsideLoadBetween( t0, t0.AddSeconds( 4 ) ), perPass, flags, System.Text.Json.JsonSerializer.Serialize( c.Passes[3], new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase } ) );
   }

   /// <summary>
   /// This process's CPU time over a window from counters that grow 0.5 CPU per second for the
   /// process and 0.2 CPU per second more for its finished children: the figure is the process's
   /// own 0.5 per second (not 0.7), taken between samples by interpolation; a window shorter than
   /// four samples, and one past the last sample, say why they cannot be measured.
   /// </summary>
   /// <returns>The CPU seconds for 1.1 s to 3.3 s, the problems, and the same for the one-second window.</returns>
   public static ClientCpuResult ClientCpuLedger()
   {
      ( MachineSampler sampler, _, DateTime t0 ) = LedgerSampler( 17 );
      ClientCpu normal = sampler.ClientCpuBetween( t0.AddSeconds( 1.1 ), t0.AddSeconds( 3.3 ) );
      ClientCpu tooShort = sampler.ClientCpuBetween( t0.AddSeconds( 1.1 ), t0.AddSeconds( 1.6 ) );
      ClientCpu past = sampler.ClientCpuBetween( t0.AddSeconds( 1 ), t0.AddSeconds( 9 ) );
      ClientCpu before = sampler.ClientCpuBetween( t0.AddSeconds( -1 ), t0.AddSeconds( 3 ) );
      return new ClientCpuResult( normal.Seconds, normal.Samples, new[] { tooShort.Problem ?? string.Empty, past.Problem ?? string.Empty, before.Problem ?? string.Empty } );
   }

   /// <summary>
   /// Client CPU per search per pass from the target's own pass notes, which are made here by the
   /// real TargetRunner.DescribePass (so the reader and the writer cannot drift apart): one pass
   /// with a note, one with a note of no searches, one whose window is too short, and one with no
   /// note at all (a problem, never a blank).
   /// </summary>
   /// <returns>Each pass's searches, CPU seconds, CPU per search, basis and problem.</returns>
   public static PerSearchResult[] ClientCpuPerSearch()
   {
      ( MachineSampler sampler, FakeMachine machine, DateTime t0 ) = LedgerSampler( 17 );
      string Pass( string name, double from, double to, int searches ) => TargetRunner.DescribePass( new PassRecord( name, "settle", t0.AddSeconds( from ), t0.AddSeconds( to ), to - from, searches, 0, 2.345, 2.431, 4.247, searches / ( to - from ) ) );
      string[] notes = { "Settle: settled after 1.3 s", Pass( "default@8", 1.1, 3.3, 65759 ), Pass( "default@1", 0.5, 3.5, 0 ), Pass( "tiny", 1, 1.4, 20 ), "Passes in the order run: default@8." };
      PassConditions[] passes =
      {
         new() { Target = "t", Pass = "default@8", StartUtc = Utc( t0.AddSeconds( 1.2 ) ) }, new() { Target = "t", Pass = "default@1", StartUtc = Utc( t0.AddSeconds( 0.6 ) ) },
         new() { Target = "t", Pass = "tiny" }, new() { Target = "t", Pass = "exact" },
      };
      SetLedgerCounters( machine, 4.0 );
      sampler.AttachClientCpu( passes, notes, t0.AddSeconds( 4 ) );
      return passes.Select( p => new PerSearchResult( p.Pass, p.Searches, p.ClientCpuSeconds, p.ClientCpuMsPerSearch, p.ClientCpuBasis, p.ClientCpuProblem ) ).ToArray();
   }

   /// <summary>
   /// The pass-note reader on the lines the target runner writes for passes of different sizes,
   /// and on lines that are not pass notes.
   /// </summary>
   /// <returns>Each parsed pass as "name start end searches".</returns>
   public static string[] PassNotes()
   {
      var t0 = new DateTime( 2026, 10, 5, 0, 45, 47, 20, DateTimeKind.Utc );
      string Pass( string name, string? previous, int searches ) => TargetRunner.DescribePass( new PassRecord( name, previous, t0, t0.AddSeconds( 20 ), 20, searches, 3, 2.345, 2.431, 4.247, searches / 20.0 ) );
      string[] notes =
      {
         Pass( "default@8", "settle", 65759 ), Pass( "default@1", "default@8", 22681 ), Pass( "exact", null, 987 ), Pass( "default@8", "default@1", 1_234_567 ),
         "Pass without the shape", "Settle: settled after 1.3 s and 500 searches (0 failed)", "WARNING: default@8 timed only 50 searches (fewer than 100)",
      };
      return PassNoteReader.Read( notes ).Select( n => string.Create( CultureInfo.InvariantCulture, $"{n.Pass} {n.Start:HH:mm:ss.fff} {n.End:HH:mm:ss.fff} {n.Searches}" ) ).ToArray();
   }

   /// <summary>
   /// The client CPU per level that goes into a target's search section: "default@N" is level N, the
   /// exact pass has no level, and a pass without a figure is left out.
   /// </summary>
   /// <returns>The levels as "level=ms".</returns>
   public static string[] ClientCpuLevels()
   {
      PassConditions Pass( string name, double? ms )
      {
         var pass = new PassConditions { Target = "t", Pass = name };
         pass.ApplyClientCpu( ms * 2, 2000, ms.HasValue ? "basis" : null, ms.HasValue ? null : "no figure" );
         return pass;
      }

      Dictionary<int, double> levels = MachineFlags.ClientCpuByLevel( new[] { Pass( "default@8", 0.3 ), Pass( "default@1", 0.5 ), Pass( "exact", 0.7 ), Pass( "default@16", null ), Pass( "default@x", 0.9 ) } );
      return levels.OrderBy( l => l.Key ).Select( l => string.Create( CultureInfo.InvariantCulture, $"{l.Key}={l.Value:0.000}" ) ).ToArray();
   }

   /// <summary>
   /// The usage text and the busy-box defaults it must agree with.
   /// </summary>
   /// <returns>The usage text, then the defaults: threshold, deadline minutes, window seconds, recent seconds.</returns>
   public static string[] UsageAndDefaults()
   {
      var d = new MachineControlOptions();
      return new[]
      {
         BenchOptions.Usage(), d.BusyThreshold.ToString( "0.0#", CultureInfo.InvariantCulture ), d.BusyWait.TotalMinutes.ToString( "0.#", CultureInfo.InvariantCulture ),
         d.BusyWindow.TotalSeconds.ToString( "0", CultureInfo.InvariantCulture ), d.RecentWindow.TotalSeconds.ToString( "0", CultureInfo.InvariantCulture ),
      };
   }

   /// <summary>
   /// The whole path on a fake machine whose client process works 0.5 CPUs: machine control sees a
   /// pass's lines, the target's notes carry the pass note, and the pass comes out with its client
   /// CPU per search and an outside load near zero (the client's own work is not outside work).
   /// Needs the MachineControl wiring (AnnotateTarget attaches the client CPU).
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>The pass's recorded figures.</returns>
   public static async Task<WiringResult> ClientCpuWiringAsync( string folder )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.OwnCpus = 0.5;
      var options = new MachineControlOptions
      {
         StateFile = Path.Combine( folder, "state.json" ), PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 25 ),
         BusyWindow = TimeSpan.FromSeconds( 1 ), RecentWindow = TimeSpan.FromMilliseconds( 300 ), BusyWait = TimeSpan.FromMilliseconds( 400 ), BusyPoll = TimeSpan.FromMilliseconds( 25 ),
      };
      var logs = new List<string>();
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "t" } ) );
      using MachineControl control = await MachineControl.StartAsync( conditions, options, machine, logs.Add, CancellationToken.None );
      Action<string> log = control.WrapLog( logs.Add );
      await Task.Delay( 1500 );
      log( "  t: warm-up before default@8, 20 searches" );
      log( "  t: timing default@8" );
      DateTime start = DateTime.UtcNow;
      await Task.Delay( 2000 );
      DateTime end = DateTime.UtcNow;
      log( "  t: 812.5 QPS at concurrency 8" );
      await control.LeaveTargetAsync( "t" );
      var report = new TargetReport { Name = "t", Search = new SearchReport() };
      report.Notes.Add( TargetRunner.DescribePass( new PassRecord( "default@8", "settle", start, end, ( end - start ).TotalSeconds, 1000, 0, 1.2, 1.3, 2.0, 1000 / ( end - start ).TotalSeconds ) ) );
      control.AnnotateTarget( report );
      await control.RestoreAsync();
      PassConditions pass = control.Conditions.Passes.Single();
      return new WiringResult( pass.Searches, pass.ClientCpuSeconds, pass.ClientCpuMsPerSearch, pass.ClientCpuProblem, pass.OutsideLoadDuring, ( end - start ).TotalSeconds,
         control.Conditions.CpuAccounting, report.Notes.FirstOrDefault( n => n.StartsWith( "Clock per pass", StringComparison.Ordinal ) ),
         report.Search.ClientCpuMsPerSearch != null && report.Search.ClientCpuMsPerSearch.TryGetValue( 8, out double level ) ? level : null );
   }

   /// <summary>
   /// Live: the outside-load figure against a synthetic outside load during an 8-searcher pass, on
   /// the real box with the real sampler (/proc/stat, this process, the engine's cgroup).
   /// An "engine" (a loopback echo server) runs in its own systemd scope, which is the engine
   /// group; 8 searcher threads in this process send it 2 KB requests with a random 0 to 3 ms think time, so
   /// both sides spend kernel network time (softirq) as real searches do. Twenty-five phases (a 2 s
   /// settle and an 8 s window each): no stress, then six times stress at 0.2 CPU, none, stress at
   /// 0.5 CPU, none. Why so many short phases: the box's own background drifts by a few hundredths of
   /// a CPU between phases (and bursts by whole CPUs now and then), so one stress phase cannot show
   /// 0.05; the median over six can. The stress is a busy loop in its own scope held to its share by CPUQuota (so its CPU is
   /// exact, and read from its cgroup), started detached so its CPU is never folded into this
   /// process's counters. A fast ledger (every 50 ms) reads everything the phase compares, so every
   /// figure is over exactly the span of sampler intervals the tool's figure covers: the tool's
   /// load; the OLD formula's (user + nice + system only) on the same readings; an observer that
   /// never reads /proc/stat (the sum of the top-level cgroups, minus this process, minus the
   /// engine); and the stress's own CPU. Nothing on the machine is changed; every scope has a
   /// timeout and is stopped in finally.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>The phases and the idle sampler cost.</returns>
   public static async Task<LiveOutsideResult> LiveOutsideLoadAsync( string folder )
   {
      var system = new LinuxMachineSystem( () => throw new InvalidOperationException( "the live outside-load test must not touch SQL Server" ) );
      IReadOnlyList<int> cpus = CpuList.Parse( MachineRestorer.OnlineCpus( system ) );
      string id = Guid.NewGuid().ToString( "N" )[..8];
      var units = new List<string>();
      using var sampler = new MachineSampler( system, cpus, 100, TimeSpan.FromMilliseconds( 250 ) );
      using var searchers = new LiveSearchers();
      var ledger = new LiveLedger( system, searchers );
      var phases = new List<LivePhase>();
      double idleCpu;
      try
      {
         string engine = $"gvbbench_live_engine_{id}";
         int port = await StartEngineAsync( system, engine, folder, units );
         ledger.Engine = ScopePath( engine );
         sampler.SetEngineGroups( new[] { ledger.Engine } );
         sampler.Start();
         ledger.Start();
         await Task.Delay( TimeSpan.FromSeconds( 12 ) );
         DateTime idleFrom = DateTime.UtcNow.AddSeconds( -8 );
         idleCpu = ( sampler.ClientCpuBetween( idleFrom, DateTime.UtcNow.AddMilliseconds( -300 ) ).Seconds ?? double.NaN ) / 7.7;
         searchers.Start( port, 8 );
         await Task.Delay( TimeSpan.FromSeconds( 5 ) );
         var quotas = new List<double?> { null };
         for( int repeat = 0; repeat < 6; repeat++ )
         {
            quotas.AddRange( new double?[] { 0.2, null, 0.5, null } );
         }

         for( int i = 0; i < quotas.Count; i++ )
         {
            phases.Add( await RunPhaseAsync( system, sampler, ledger, $"{( quotas[i].HasValue ? "stress" + quotas[i]!.Value.ToString( "0.0", CultureInfo.InvariantCulture ) : "base" )}#{i}", quotas[i], id, units ) );
         }
      }
      finally
      {
         searchers.Stop();
         await StopUnitsAsync( system, units );
         sampler.Stop();
         ledger.Stop();
      }

      return new LiveOutsideResult( phases.ToArray(), idleCpu, sampler.Accounting.Description, sampler.LastError );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A sampler over counters that grow 0.5 CPU per second for this process and 0.2 more for its
   /// finished children, sampled every 250 ms from a fixed time.
   /// </summary>
   /// <param name="steps">Samples (the last is at 250 ms x (steps - 1)).</param>
   /// <returns>The sampler, its fake machine and the first sample's time.</returns>
   private static (MachineSampler Sampler, FakeMachine Machine, DateTime Start) LedgerSampler( int steps )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.StaticCounters = true;
      var sampler = new MachineSampler( machine, new[] { 0, 1 }, 100, TimeSpan.FromMilliseconds( 250 ) );
      var t0 = new DateTime( 2026, 10, 5, 12, 0, 0, DateTimeKind.Utc );
      for( int step = 0; step < steps; step++ )
      {
         SetLedgerCounters( machine, step / 4.0 );
         sampler.Tick( t0.AddMilliseconds( 250 * step ) );
      }

      return ( sampler, machine, t0 );
   }

   /// <summary>
   /// The ledger counters at a time: 3.0 CPUs busy in all, this process 0.5, its children 0.2.
   /// </summary>
   /// <param name="machine">The fake machine.</param>
   /// <param name="second">Seconds since the start.</param>
   private static void SetLedgerCounters( FakeMachine machine, double second )
   {
      machine.SetCounters( busyTicks: (long)Math.Round( 10_000 + second * 300 ), selfTicks: (long)Math.Round( 500 + second * 50 ), groupUsec: 7_000_000, childTicks: (long)Math.Round( 100 + second * 20 ) );
   }

   /// <summary>
   /// A time as the pass records write it.
   /// </summary>
   /// <param name="time">UTC time.</param>
   /// <returns>"2026-10-05T12:00:01.200Z".</returns>
   private static string Utc( DateTime time )
   {
      return time.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// The cgroup folder of a systemd scope started as root.
   /// </summary>
   /// <param name="unit">Scope name without ".scope".</param>
   /// <returns>The folder.</returns>
   private static string ScopePath( string unit )
   {
      return $"/sys/fs/cgroup/system.slice/{unit}.scope";
   }

   /// <summary>
   /// Starts the echo engine detached in its own scope and waits (at most 20 s) for its port.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="unit">Scope name.</param>
   /// <param name="folder">Scratch folder.</param>
   /// <param name="units">Receives the scope name, to be stopped at the end.</param>
   /// <returns>The port it listens on.</returns>
   /// <exception cref="TimeoutException">The engine did not report its port in time.</exception>
   private static async Task<int> StartEngineAsync( IMachineSystem system, string unit, string folder, List<string> units )
   {
      string script = Path.Combine( folder, "engine.py" );
      string portFile = Path.Combine( folder, "engine.port" );
      await File.WriteAllTextAsync( script, ENGINE_SCRIPT );
      units.Add( unit );
      await StartScopeAsync( system, unit, null, $"timeout 420 python3 '{script}' '{portFile}' 400" );
      DateTime deadline = DateTime.UtcNow.AddSeconds( 20 );
      while( DateTime.UtcNow < deadline )
      {
         if( File.Exists( portFile ) && int.TryParse( await File.ReadAllTextAsync( portFile ), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port ) )
         {
            return port;
         }

         await Task.Delay( 200 );
      }

      throw new TimeoutException( "the live engine did not report its port within 20 s" );
   }

   /// <summary>
   /// Starts a command detached (not a child of this process) in its own systemd scope.
   /// Why detached: a child's CPU time is added to this process's counters when it is reaped,
   /// which would subtract the stress from the outside load it is meant to show.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="unit">Scope name.</param>
   /// <param name="quota">CPUs the scope is held to, or null.</param>
   /// <param name="command">The shell command to run in it.</param>
   private static async Task StartScopeAsync( IMachineSystem system, string unit, double? quota, string command )
   {
      string property = quota.HasValue ? string.Create( CultureInfo.InvariantCulture, $"-p CPUQuota={quota.Value * 100:0}%" ) : string.Empty;
      string script = $"setsid sudo -n systemd-run --scope --quiet --unit={unit} {property} bash -c \"{command.Replace( "\"", "\\\"" )}\" >/dev/null 2>&1 < /dev/null &";
      ShellResult started = await system.RunAsync( "bash", new[] { "-c", script }, TimeSpan.FromSeconds( 15 ), CancellationToken.None );
      if( started.ExitCode != 0 )
      {
         throw new InvalidOperationException( $"could not start scope {unit}: {started.Output}" );
      }
   }

   /// <summary>
   /// Stops every scope this scenario started (a stopped or ended scope is not an error).
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="units">Scope names.</param>
   private static async Task StopUnitsAsync( IMachineSystem system, List<string> units )
   {
      foreach( string unit in units )
      {
         await system.RunAsync( "sudo", new[] { "-n", "systemctl", "stop", unit + ".scope" }, TimeSpan.FromSeconds( 20 ), CancellationToken.None );
      }
   }

   /// <summary>
   /// One phase: optionally starts the stress, lets it settle 2 s, then measures an 8 s window.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="sampler">The running sampler.</param>
   /// <param name="ledger">The fast ledger.</param>
   /// <param name="name">Phase name.</param>
   /// <param name="quota">CPUs of stress, or null for none.</param>
   /// <param name="id">Run id for the scope names.</param>
   /// <param name="units">Receives the stress scope's name.</param>
   /// <returns>The phase's figures.</returns>
   private static async Task<LivePhase> RunPhaseAsync( IMachineSystem system, MachineSampler sampler, LiveLedger ledger, string name, double? quota, string id, List<string> units )
   {
      string? stress = quota.HasValue ? $"gvbbench_live_stress_{id}_{name.Replace( ".", "_" ).Replace( "#", "_" )}" : null;
      if( stress != null )
      {
         units.Add( stress );
         ledger.Stress = ScopePath( stress );
         await StartScopeAsync( system, stress, quota, "timeout 100 bash -c 'while :; do :; done'" );
      }

      await Task.Delay( TimeSpan.FromSeconds( 2 ) );
      DateTime from = DateTime.UtcNow;
      await Task.Delay( TimeSpan.FromSeconds( 8 ) );
      DateTime to = DateTime.UtcNow;
      await Task.Delay( 300 );
      OutsideWindow window = sampler.OutsideWindowBetween( from, to ) ?? throw new InvalidOperationException( $"{name}: the sampler had no outside figure for the window" );
      CgroupReads d = ledger.Between( window.From, window.To );
      if( stress != null )
      {
         await system.RunAsync( "sudo", new[] { "-n", "systemctl", "stop", stress + ".scope" }, TimeSpan.FromSeconds( 20 ), CancellationToken.None );
         ledger.Stress = null;
      }

      double seconds = window.Seconds;
      double ownCpu = d.Self / seconds;
      double engineCpu = d.Engine / seconds;
      double? client = sampler.ClientCpuBetween( window.From, window.To ).Seconds;
      return new LivePhase( name, quota, Math.Round( window.Load, 4 ), Math.Round( d.BusyOld / seconds - ownCpu - engineCpu, 4 ), Math.Round( d.Cgroups / seconds - ownCpu - engineCpu, 4 ), Math.Round( d.Stress / seconds, 4 ),
         Math.Round( seconds, 2 ), window.Intervals, Math.Round( ownCpu, 3 ), Math.Round( engineCpu, 3 ), d.Searches / seconds, client.HasValue && d.Searches > 0 ? Math.Round( client.Value * 1000 / d.Searches, 4 ) : double.NaN );
   }

   /// <summary>
   /// Reads every counter the phases compare, close together.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="engine">The engine's cgroup folder, or null before it exists.</param>
   /// <param name="stress">The stress scope's cgroup folder, or null.</param>
   /// <param name="searchers">The searchers (completed search count).</param>
   /// <returns>The readings.</returns>
   internal static CgroupReads ReadAll( IMachineSystem system, string? engine, string? stress, LiveSearchers searchers )
   {
      string[] total = system.ReadFile( "/proc/stat" )!.Split( '\n' )[0].Split( ' ', StringSplitOptions.RemoveEmptyEntries );
      double Col( int i ) => double.Parse( total[i], CultureInfo.InvariantCulture ) / 100;
      string[] self = ProcessInfo.StatFields( system, system.ProcessId )!;
      double Own( int i ) => double.Parse( self[i], CultureInfo.InvariantCulture ) / 100;
      double top = Directory.GetDirectories( "/sys/fs/cgroup" ).Sum( d => Usage( system, d ) );
      return new CgroupReads( Col( 1 ) + Col( 2 ) + Col( 3 ), Own( 11 ) + Own( 12 ) + Own( 13 ) + Own( 14 ), engine == null ? 0 : Usage( system, engine ), stress == null ? 0 : Usage( system, stress ), top, searchers.Count );
   }

   /// <summary>
   /// A cgroup's CPU seconds.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="folder">cgroup folder.</param>
   /// <returns>usage_usec in seconds, or 0 when it has no cpu.stat.</returns>
   private static double Usage( IMachineSystem system, string folder )
   {
      string? line = system.ReadFile( folder + "/cpu.stat" )?.Split( '\n' ).FirstOrDefault( l => l.StartsWith( "usage_usec ", StringComparison.Ordinal ) );
      return line == null ? 0 : double.Parse( line["usage_usec ".Length..], CultureInfo.InvariantCulture ) / 1e6;
   }

   #endregion Private Methods
}

/// <summary>Result of <see cref="MachineControlScenarios.SamplerAccounting"/>.</summary>
/// <param name="Outside">Outside CPUs.</param>
/// <param name="Description">The accounting's description.</param>
/// <param name="CountIrq">True when irq and softirq were added to busy.</param>
/// <param name="Error">The sampler's first error, or null.</param>
public sealed record AccountingResult( double? Outside, string Description, bool CountIrq, string? Error );

/// <summary>Result of <see cref="MachineControlScenarios.SamplerNegative"/>.</summary>
/// <param name="Outside">The sampler's load for counters that cannot be right.</param>
/// <param name="PerPass">One line per hand-made pass.</param>
/// <param name="Flags">Every flag.</param>
/// <param name="Json">One pass as results.json writes it.</param>
public sealed record MismatchResult( double? Outside, string[] PerPass, string[] Flags, string Json );

/// <summary>Result of <see cref="MachineControlScenarios.ClientCpuLedger"/>.</summary>
/// <param name="Seconds">CPU seconds of the normal window.</param>
/// <param name="Samples">Samples it spans.</param>
/// <param name="Problems">The three problems.</param>
public sealed record ClientCpuResult( double? Seconds, int Samples, string[] Problems );

/// <summary>One pass of <see cref="MachineControlScenarios.ClientCpuPerSearch"/>.</summary>
/// <param name="Pass">Pass name.</param>
/// <param name="Searches">Searches attached, or null.</param>
/// <param name="Seconds">Client CPU seconds, or null.</param>
/// <param name="MsPerSearch">Client CPU per search, or null.</param>
/// <param name="Basis">What it was measured over, or null.</param>
/// <param name="Problem">Why there is no per-search figure, or null.</param>
public sealed record PerSearchResult( string Pass, int? Searches, double? Seconds, double? MsPerSearch, string? Basis, string? Problem );

/// <summary>Result of <see cref="MachineControlScenarios.ClientCpuWiringAsync"/>.</summary>
/// <param name="Searches">Searches attached to the pass.</param>
/// <param name="Seconds">Client CPU seconds.</param>
/// <param name="MsPerSearch">Client CPU per search.</param>
/// <param name="Problem">The problem, or null.</param>
/// <param name="OutsideDuring">Outside load during the pass.</param>
/// <param name="WindowSeconds">The pass window.</param>
/// <param name="Accounting">conditions.cpuAccounting.</param>
/// <param name="ClockLine">The target's clock note.</param>
/// <param name="SearchLevel8">search.clientCpuMsPerSearch at level 8 on the target's report, or null.</param>
public sealed record WiringResult( int? Searches, double? Seconds, double? MsPerSearch, string? Problem, double? OutsideDuring, double WindowSeconds, string? Accounting, string? ClockLine, double? SearchLevel8 );

/// <summary>Counters read together for the live phase (cumulative), or their change over a span.</summary>
/// <param name="BusyOld">user + nice + system, seconds, all CPUs.</param>
/// <param name="Self">This process's utime + stime + cutime + cstime.</param>
/// <param name="Engine">The engine cgroup's CPU seconds.</param>
/// <param name="Stress">The stress scope's CPU seconds.</param>
/// <param name="Cgroups">The CPU seconds of every top-level cgroup.</param>
/// <param name="Searches">Completed searches.</param>
public sealed record CgroupReads( double BusyOld, double Self, double Engine, double Stress, double Cgroups, long Searches );

/// <summary>One live phase.</summary>
/// <param name="Name">Phase name.</param>
/// <param name="Quota">CPUs of stress, or null.</param>
/// <param name="Tool">The sampler's outside load.</param>
/// <param name="OldFormula">What user + nice + system alone gives on the same readings.</param>
/// <param name="Observer">The cgroup observer's outside load.</param>
/// <param name="Truth">The stress's own CPUs.</param>
/// <param name="Seconds">Window length.</param>
/// <param name="Intervals">Sampler intervals used.</param>
/// <param name="OwnCpu">This process's CPUs.</param>
/// <param name="EngineCpu">The engine's CPUs.</param>
/// <param name="Qps">Searches per second.</param>
/// <param name="ClientMsPerSearch">Client CPU per search.</param>
public sealed record LivePhase( string Name, double? Quota, double Tool, double OldFormula, double Observer, double Truth, double Seconds, int Intervals, double OwnCpu, double EngineCpu, double Qps, double ClientMsPerSearch );

/// <summary>Result of <see cref="MachineControlScenarios.LiveOutsideLoadAsync"/>.</summary>
/// <param name="Phases">The 25 phases.</param>
/// <param name="IdleClientCpu">This process's CPUs with the sampler running and no searches.</param>
/// <param name="Accounting">The accounting description.</param>
/// <param name="SamplingError">The sampler's first error, or null.</param>
public sealed record LiveOutsideResult( LivePhase[] Phases, double IdleClientCpu, string Accounting, string? SamplingError );

/// <summary>
/// Reads the live phase's counters every 50 ms on a background thread, so any span can be
/// measured by interpolation between the readings either side of its ends. Why: the tool's
/// outside figure covers whole sampler intervals, which end a little inside the phase's window;
/// comparing it with figures over the window's own ends would put the difference between bursts of
/// other work in the 250 ms either side into the comparison.
/// </summary>
public sealed class LiveLedger
{
   #region Data Members

   private readonly IMachineSystem _system;
   private readonly LiveSearchers _searchers;
   private readonly object _lock = new();
   private readonly List<(DateTime At, CgroupReads Reads)> _readings = new();
   private readonly CancellationTokenSource _stop = new();
   private Task? _loop;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the ledger.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="searchers">The searchers whose count is read.</param>
   public LiveLedger( IMachineSystem system, LiveSearchers searchers )
   {
      _system = system;
      _searchers = searchers;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The engine's cgroup folder (set before <see cref="Start"/>).</summary>
   public string? Engine { get; set; }

   /// <summary>The cgroup folder of the stress running now, or null.</summary>
   public string? Stress { get; set; }

   /// <summary>
   /// Starts reading.
   /// </summary>
   public void Start()
   {
      _loop = Task.Run( async () =>
      {
         using var timer = new PeriodicTimer( TimeSpan.FromMilliseconds( 50 ) );
         try
         {
            do
            {
               DateTime at = DateTime.UtcNow;
               CgroupReads reads = MachineControlScenarios.ReadAll( _system, Engine, Stress, _searchers );
               lock( _lock )
               {
                  _readings.Add( ( at, reads ) );
               }
            }
            while( await timer.WaitForNextTickAsync( _stop.Token ) );
         }
         catch( OperationCanceledException )
         {
            // Stop() was called.
         }
      } );
   }

   /// <summary>
   /// Stops reading (waits at most 5 s).
   /// </summary>
   public void Stop()
   {
      _stop.Cancel();
      _loop?.Wait( TimeSpan.FromSeconds( 5 ) );
   }

   /// <summary>
   /// How much each counter changed between two times, interpolated between the readings either side of each.
   /// The stress counter is the change of the stress that ran at both ends.
   /// </summary>
   /// <param name="from">Start (UTC).</param>
   /// <param name="to">End (UTC).</param>
   /// <returns>The changes; Searches is the count of completed searches.</returns>
   public CgroupReads Between( DateTime from, DateTime to )
   {
      lock( _lock )
      {
         CgroupReads a = At( from );
         CgroupReads b = At( to );
         return new CgroupReads( b.BusyOld - a.BusyOld, b.Self - a.Self, b.Engine - a.Engine, b.Stress - a.Stress, b.Cgroups - a.Cgroups, b.Searches - a.Searches );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The counters at a time, interpolated.
   /// </summary>
   /// <param name="at">The time.</param>
   /// <returns>The readings' values at that time.</returns>
   private CgroupReads At( DateTime at )
   {
      int index = _readings.FindIndex( r => r.At >= at );
      if( index <= 0 )
      {
         throw new InvalidOperationException( $"the ledger has no readings either side of {at:HH:mm:ss.fff}" );
      }

      ( DateTime t0, CgroupReads a ) = _readings[index - 1];
      ( DateTime t1, CgroupReads b ) = _readings[index];
      double f = ( at - t0 ).TotalSeconds / Math.Max( 1e-9, ( t1 - t0 ).TotalSeconds );
      double Mix( double x, double y ) => x + ( y - x ) * f;
      return new CgroupReads( Mix( a.BusyOld, b.BusyOld ), Mix( a.Self, b.Self ), Mix( a.Engine, b.Engine ), Mix( a.Stress, b.Stress ), Mix( a.Cgroups, b.Cgroups ), (long)Math.Round( Mix( a.Searches, b.Searches ) ) );
   }

   #endregion Private Methods
}

/// <summary>
/// Eight searcher threads that send an engine 2 KB requests, think for 0 to 3 ms (random: a fixed
/// think time near the kernel's 1 ms tick would line the work up with the tick that samples it) and
/// read the 2 KB replies, counting completed searches.
/// </summary>
public sealed class LiveSearchers : IDisposable
{
   #region Data Members

   private const int PAYLOAD = 2048;

   private readonly List<Thread> _threads = new();
   private readonly CancellationTokenSource _stop = new();
   private long _count;

   #endregion Data Members

   #region Public Methods

   /// <summary>Completed searches so far.</summary>
   public long Count => Interlocked.Read( ref _count );

   /// <summary>
   /// Starts the searchers.
   /// </summary>
   /// <param name="port">The engine's loopback port.</param>
   /// <param name="count">Searcher threads.</param>
   public void Start( int port, int count )
   {
      for( int i = 0; i < count; i++ )
      {
         var thread = new Thread( () => Run( port ) ) { IsBackground = true, Name = "gvbbench-live-searcher" };
         _threads.Add( thread );
         thread.Start();
      }
   }

   /// <summary>
   /// Stops the searchers and waits (at most 10 s) for them.
   /// </summary>
   public void Stop()
   {
      _stop.Cancel();
      foreach( Thread thread in _threads )
      {
         thread.Join( TimeSpan.FromSeconds( 10 ) );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// One searcher: request, reply, think time, until stopped.
   /// </summary>
   /// <param name="port">The engine's port.</param>
   private void Run( int port )
   {
      var random = new Random( Environment.CurrentManagedThreadId );
      try
      {
         using var client = new TcpClient { NoDelay = true, ReceiveTimeout = 5000, SendTimeout = 5000 };
         client.Connect( "127.0.0.1", port );
         NetworkStream stream = client.GetStream();
         byte[] request = new byte[PAYLOAD];
         byte[] reply = new byte[PAYLOAD];
         while( !_stop.IsCancellationRequested )
         {
            stream.Write( request, 0, PAYLOAD );
            int got = 0;
            while( got < PAYLOAD )
            {
               int read = stream.Read( reply, got, PAYLOAD - got );
               got += read > 0 ? read : throw new IOException( "engine closed" );
            }

            Interlocked.Increment( ref _count );
            Thread.Sleep( random.Next( 0, 4 ) );
         }
      }
      catch( Exception ex ) when( ex is IOException or SocketException or ObjectDisposedException )
      {
         // The engine was stopped under the searcher at the end of the run.
      }
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Stops the searchers.
   /// </summary>
   public void Dispose()
   {
      Stop();
      _stop.Dispose();
   }

   #endregion IDisposable
}
#endif
