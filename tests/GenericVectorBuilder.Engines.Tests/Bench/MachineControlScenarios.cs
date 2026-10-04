// Compiled only by MachineControlTests and MachineControlLiveTests, together with the
// benchmark's own sources (the test project does not reference the benchmark console project).
// BENCH_UNDER_TEST is defined only in that compilation, so the test project itself sees an
// empty file.
#if BENCH_UNDER_TEST
using System.Globalization;
using System.Text.Json;
using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Running;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// Runs the benchmark's real machine-control code against a fake machine (and, for the live
/// test, the real one) and hands back plain results for the tests to check.
/// Why a fake machine: the rules under test (record before change, restore after a crash,
/// refuse while another run owns the machine, which CPUs each engine gets) decide whether the
/// box is left changed, and a fake makes every command visible without touching the box.
/// </summary>
public static class MachineControlScenarios
{
   #region Data Members

   /// <summary>"lscpu -e" as printed on linus7795 (E5-1620 v3, 4 cores, 2 threads each).</summary>
   public const string LINUS_LSCPU = @"CPU NODE SOCKET CORE L1d:L1i:L2:L3 ONLINE    MAXMHZ    MINMHZ       MHZ
  0    0      0    0 0:0:0:0          yes 3600.0000 1200.0000 2394.4451
  1    0      0    1 1:1:1:0          yes 3600.0000 1200.0000 1197.2480
  2    0      0    2 2:2:2:0          yes 3600.0000 1200.0000 1256.8440
  3    0      0    3 3:3:3:0          yes 3600.0000 1200.0000 2394.3879
  4    0      0    0 0:0:0:0          yes 3600.0000 1200.0000 1441.8380
  5    0      0    1 1:1:1:0          yes 3600.0000 1200.0000 1656.7130
  6    0      0    2 2:2:2:0          yes 3600.0000 1200.0000 3491.7571
  7    0      0    3 3:3:3:0          yes 3600.0000 1200.0000 1197.3669
";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Splits CPUs from lscpu text.
   /// </summary>
   /// <param name="lscpu">lscpu -e output.</param>
   /// <returns>The split.</returns>
   public static PartitionResult Partition( string lscpu )
   {
      CpuPartition p = CpuPartition.Choose( CpuPartition.ParseLscpu( lscpu ) );
      return new PartitionResult( CpuList.Format( p.EngineCpus ), CpuList.Format( p.ClientCpus ), p.ThreadSiblings.ToArray(), p.Problem,
         p.IsSplit ? CpuList.Format( p.SqlServerIds( p.EngineCpus ) ) : string.Empty, p.Describe() );
   }

   /// <summary>
   /// CpuList.Format of CpuList.Parse.
   /// </summary>
   /// <param name="text">A CPU list.</param>
   /// <returns>The canonical text.</returns>
   public static string CanonicalCpus( string text )
   {
      return CpuList.Format( CpuList.Parse( text ) );
   }

   /// <summary>
   /// Parses a command line with the real options.
   /// </summary>
   /// <param name="args">Arguments.</param>
   /// <returns>Machine control switch, state file, command.</returns>
   public static OptionsResult Options( string[] args )
   {
      BenchOptions o = BenchOptions.Parse( args );
      return new OptionsResult( o.MachineControl, o.MachineStateFile, o.Command );
   }

   /// <summary>
   /// Saves and loads a full state, checks the temporary file is gone, and reads a missing and a broken file.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>The checks.</returns>
   public static StateResult StateRoundTrip( string folder )
   {
      var store = new MachineStateStore( Path.Combine( folder, "state.json" ) );
      bool missingIsNull = store.Load() == null;
      MachineState state = FullState( 1234, 99 );
      store.Save( state );
      MachineState back = store.Load()!;
      bool same = JsonSerializer.Serialize( back ) == JsonSerializer.Serialize( state );
      bool tmpLeft = File.Exists( store.Path + ".tmp" );
      File.WriteAllText( store.Path, "{ not json" );
      string corrupt;
      try
      {
         store.Load();
         corrupt = "no error";
      }
      catch( InvalidDataException ex )
      {
         corrupt = ex.Message;
      }

      return new StateResult( same, tmpLeft, missingIsNull, corrupt, store.Path, back.Describe() );
   }

   /// <summary>
   /// A crashed run's state file on a fake machine whose settings are still changed, then the
   /// restore a new start does first.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <param name="mode">"dead-owner", "live-owner", "docker-fails", "gone" or "sql-manual" (SQL Server had a manual affinity before).</param>
   /// <returns>Commands run, whether the file is left and what it holds, the error, governors after.</returns>
   public static async Task<RestoreResult> RestoreAfterCrashAsync( string folder, string mode )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.AddProcess( 777, "dotnet", 5000, 1, "0-7", "/user.slice/x.scope" );
      Enumerable.Range( 0, 8 ).ToList().ForEach( cpu => machine.Set( GovernorControl.GovernorPath( cpu ), "performance\n" ) );
      machine.Containers["c0ffee"] = new FakeContainer( "gvb-redis", "2-3,6-7", 900, mode != "gone" );
      machine.SqlType = "MANUAL";
      machine.SqlOnline = new[] { 4, 5, 6, 7 };
      machine.AddProcess( 1688, "qdrant", mode == "gone" ? 1 : 3000, 3, "2-3,6-7", "/system.slice/qdrant.service" );
      if( mode == "docker-fails" )
      {
         machine.Fail = call => call.Contains( "docker update", StringComparison.Ordinal ) ? new ShellResult( 1, string.Empty, "permission denied" ) : null;
      }

      var store = new MachineStateStore( Path.Combine( folder, "state.json" ) );
      MachineState crashed = FullState( 777, mode == "live-owner" ? 5000 : 4999 );
      crashed.SqlServer = mode == "sql-manual" ? new SqlAffinityPin( "MANUAL", "0-3", "sql" ) : crashed.SqlServer;
      store.Save( crashed );
      string? error = null;
      try
      {
         await MachineControl.RestoreStaleAsync( store, machine, _ => { }, CancellationToken.None );
      }
      catch( InvalidOperationException ex )
      {
         error = ex.Message;
      }

      string[] governors = Enumerable.Range( 0, 8 ).Select( cpu => machine.ReadFile( GovernorControl.GovernorPath( cpu ) )!.Trim() ).ToArray();
      return new RestoreResult( machine.Calls.ToArray(), File.Exists( store.Path ), File.Exists( store.Path ) ? File.ReadAllText( store.Path ) : null, error, governors,
         machine.Containers["c0ffee"].Cpuset, machine.SqlType, machine.ThreadAffinity( 1688 ) );
   }

   /// <summary>
   /// A whole run on the fake machine: start (stale file restored first, governors recorded
   /// before they change), pin the client, measure a SQL, a Qdrant, a compose and an embedded
   /// target with their pass lines, then put everything back.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <param name="wrongSqlMapping">True to make the fake SQL Server bind threads as if its CPU ids were the kernel's.</param>
   /// <returns>Everything the tests check.</returns>
   public static async Task<RunResult> RunAsync( string folder, bool wrongSqlMapping )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.WrongSqlMapping = wrongSqlMapping;
      machine.AddProcess( 1449, "sqlservr", 2000, 4, "0-7", "/system.slice/mssql-server.service" );
      machine.AddProcess( 1688, "qdrant", 3000, 3, "0-7", "/system.slice/qdrant.service" );
      machine.AddProcess( 1680, "dockerd", 1000, 2, "0-7", "/system.slice/docker.service" );
      machine.Containers["beef01"] = new FakeContainer( "gvb-redis", string.Empty, 901, true );
      machine.AddProcess( 901, "redis-server", 6000, 2, "0-7", "/system.slice/docker-beef01.scope" );
      machine.ComposeRunning["/x/redis.compose.yaml"] = new[] { "beef01" };
      var store = new MachineStateStore( Path.Combine( folder, "state.json" ) );
      machine.StateFile = store.Path;
      var logs = new List<string>();
      var options = new MachineControlOptions { StateFile = store.Path, SampleInterval = TimeSpan.FromMilliseconds( 20 ), BusyWait = TimeSpan.FromMilliseconds( 200 ), BusyPoll = TimeSpan.FromMilliseconds( 20 ) };
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "sql", "--warmup", "7", "--exact-seconds", "9" } ) );
      MachineControl control = await MachineControl.StartAsync( conditions, options, machine, line => logs.Add( line ), CancellationToken.None );
      string[] governorsDuring = machine.Governors();
      bool fileDuring = File.Exists( store.Path );
      await control.PinClientAsync( CancellationToken.None );
      Action<string> log = control.WrapLog( line => logs.Add( line ) );
      foreach( (string name, string hosting, string? compose) in new[] { ( "sql", "always-on", (string?)null ), ( "qdrant-hnsw", "always-on", null ), ( "redis", "compose", "/x/redis.compose.yaml" ), ( "duckdb", "embedded", null ) } )
      {
         await control.EnterTargetAsync( name, hosting, compose, CancellationToken.None );
         machine.Snapshot( $"during {name}" );
         log( $"  {name}: warm-up before default@1, 7 searches" );
         log( $"  {name}: timing default@1" );
         await Task.Delay( 60 );
         log( $"  {name}: p50 1.00 ms, p95 2.00 ms, recall@10 1.000" );
         await control.LeaveTargetAsync( name );
      }

      await control.RestoreAsync();
      control.Dispose();
      var report = new BenchReport { Targets = { new TargetReport { Name = "sql", PassOrder = new List<string> { "default@1" } }, new TargetReport { Name = "never-seen", PassOrder = new List<string> { "default@1" } } } };
      control.Annotate( report );
      control.AnnotateTarget( report.Targets[0] );
      return new RunResult( machine.Calls.ToArray(), machine.StateAtCall.ToArray(), governorsDuring, machine.Governors(), fileDuring, File.Exists( store.Path ),
         JsonSerializer.Serialize( control.Conditions, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } ),
         report.Notes.ToArray(), report.Targets[0].Notes.ToArray(), machine.Snapshots.ToArray(), logs.ToArray() );
   }

   /// <summary>
   /// The busy-box rule on a fake machine whose outside work is switched on and off: a pass
   /// that starts while 3 CPUs of outside work run waits its limit and is flagged; a pass that
   /// starts on a quiet box starts at once.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>Each pass as "target pass busy waited endedBy", and the log.</returns>
   public static async Task<GateResult> GateAsync( string folder )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.OutsideCpus = 3;
      var options = new MachineControlOptions
      {
         StateFile = Path.Combine( folder, "state.json" ), PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 25 ),
         BusyWindow = TimeSpan.FromSeconds( 1 ), BusyWait = TimeSpan.FromMilliseconds( 400 ), BusyPoll = TimeSpan.FromMilliseconds( 25 ),
      };
      var logs = new List<string>();
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "t" } ) );
      using MachineControl control = await MachineControl.StartAsync( conditions, options, machine, logs.Add, CancellationToken.None );
      Action<string> log = control.WrapLog( logs.Add );
      await Task.Delay( 900 );
      log( "  t: timing default@8" );
      await Task.Delay( 100 );
      log( "  t: 812.5 QPS at concurrency 8" );
      machine.OutsideCpus = 0;
      log( "  t: warm-up before exact, 20 searches" );
      await Task.Delay( 1300 );
      log( "  t: timing exact" );
      log( "  t: exact mode p50 0.80 ms over 20 queries, recall 1.000" );
      log( "  t: warm-up before default@1, 20 searches" );
      log( "  t: timing default@1" );
      await control.LeaveTargetAsync( "t" );
      await control.RestoreAsync();
      string[] passes = control.Conditions.Passes.Select( p => string.Create( CultureInfo.InvariantCulture, $"{p.Target} {p.Pass} busy={p.BusyBox} waited={p.WaitedSeconds:0.0} by={p.EndedBy} load={MachineFlags.Load( p.OutsideLoadAtStart )}" ) ).ToArray();
      return new GateResult( passes, logs.ToArray(), control.Conditions.Passes.Select( p => p.BusyReason ?? string.Empty ).ToArray() );
   }

   /// <summary>
   /// The sampler's arithmetic on hand-set counters: outside CPU time is all busy time minus
   /// this process minus the engine's cgroup, and per-CPU min/median/max over a window.
   /// </summary>
   /// <returns>The figures.</returns>
   public static SamplerResult SamplerMath()
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.StaticCounters = true;
      var sampler = new MachineSampler( machine, new[] { 0, 1 }, 100, TimeSpan.FromMilliseconds( 250 ) );
      sampler.SetEngineGroups( new[] { "/sys/fs/cgroup/system.slice/qdrant.service" } );
      var t0 = new DateTime( 2026, 10, 4, 12, 0, 0, DateTimeKind.Utc );
      int[][] mhz = { new[] { 3400, 1200 }, new[] { 3500, 1300 }, new[] { 3600, 1250 }, new[] { 3450, 1400 } };
      for( int step = 0; step <= 8; step++ )
      {
         int second = step / 4;
         machine.SetCounters( busyTicks: 10_000 + second * 300, selfTicks: 500 + second * 50, groupUsec: 7_000_000 + second * 1_500_000 );
         machine.Set( GovernorControl.FrequencyPath( 0 ), $"{mhz[step % 4][0] * 1000}\n" );
         machine.Set( GovernorControl.FrequencyPath( 1 ), $"{mhz[step % 4][1] * 1000}\n" );
         sampler.Tick( t0.AddMilliseconds( 250 * step ) );
      }

      double? outside = sampler.OutsideLoadBetween( t0, t0.AddSeconds( 2 ) );
      ( List<CpuMhz> cpus, int samples, _ ) = sampler.Frequencies( t0, t0.AddMilliseconds( 750 ) );
      ( _, int shortSamples, bool nearest ) = sampler.Frequencies( t0.AddMilliseconds( 1010 ), t0.AddMilliseconds( 1030 ) );
      return new SamplerResult( outside, cpus.Select( c => $"cpu{c.Cpu} {c.Min}/{c.Median}/{c.Max}" ).ToArray(), samples, shortSamples, nearest, sampler.LastError );
   }

   /// <summary>
   /// Pins SQL Server on a fake machine whose SQL Server rejects the change: the pin fails as a
   /// problem, but SQL Server's cgroup is still counted as the engine's (not outside work).
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>Engine groups and problems.</returns>
   public static async Task<string[][]> GroupsWhenPinFailsAsync( string folder )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.AddProcess( 1449, "sqlservr", 2000, 2, "0-7", "/system.slice/mssql-server.service" );
      machine.RejectSqlAlter = true;
      var keeper = new MachineStateKeeper( new MachineStateStore( Path.Combine( folder, "state.json" ) ), MachineStateKeeper.NewState( machine ) );
      var pinner = new EnginePinner( machine, keeper, CpuPartition.Choose( CpuPartition.ParseLscpu( LINUS_LSCPU ) ) );
      TargetPinning pinning = await pinner.PinTargetAsync( "sql", "always-on", null, CancellationToken.None );
      return new[] { pinner.EngineGroups.ToArray(), pinning.Problems.ToArray(), new[] { keeper.State.SqlServer?.PreviousType ?? "none" } };
   }

   /// <summary>
   /// The flags for hand-made conditions.
   /// </summary>
   /// <param name="scenario">"clean" or "bad".</param>
   /// <returns>The flags.</returns>
   public static string[] Flags( string scenario )
   {
      var c = new MachineConditions { BuildConfiguration = "Release", MachineControl = "on", Governor = "performance", GovernorAtEnd = "performance", StateFile = "/s.json" };
      c.Passes.Add( new PassConditions { Target = "a", Pass = "default@1", Governor = "performance" } );
      c.Engines.Add( new TargetPinning { Target = "a", Hosting = "always-on", Cpus = "2-3,6-7" } );
      if( scenario == "bad" )
      {
         c.BuildConfiguration = "Debug";
         c.JitOptimizerDisabled = true;
         c.GovernorAtEnd = "mixed: cpu0 performance, cpu1 schedutil";
         c.PartitionProblem = "only one physical core is online";
         c.Passes.Add( new PassConditions { Target = "a", Pass = "default@8", Governor = "schedutil", BusyBox = true, BusyReason = "outside 2.10 CPUs", NearestSample = true } );
         c.Engines.Add( new TargetPinning { Target = "duckdb", Hosting = "embedded", Cpus = "0-1,4-5" } );
         c.Engines.Add( new TargetPinning { Target = "b", Hosting = "compose", Problems = { "docker update failed" } } );
         c.RestoreProblems.Add( "container x cpuset: permission denied" );
         c.SamplingError = "12:00:00: cannot read /proc/stat";
      }

      if( scenario == "off" )
      {
         c.MachineControl = "off (--no-machine-control)";
         c.Governor = "schedutil";
      }

      return MachineFlags.Compute( c, scenario == "bad" ? new[] { "a", "b" } : new[] { "a" } ).ToArray();
   }

   /// <summary>
   /// Writes conditions into a results.json written by the real results writer, and reads it back.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>The file.</returns>
   public static string ConditionsJson( string folder )
   {
      var report = new BenchReport { Pipeline = "p", RunSeed = 5 };
      ResultsWriter.Write( report, folder );
      var c = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "sql", "--warmup", "7", "--exact-seconds", "9", "--hnsw-ef", "100" } ) );
      c.MachineControl = "on";
      c.Governor = "performance";
      c.GovernorAtEnd = "performance";
      c.ClientCpus = "0-1,4-5";
      c.EngineCpus = "2-3,6-7";
      c.ThreadSiblings = new List<string> { "0,4", "1,5", "2,6", "3,7" };
      c.BuildConfiguration = "Release";
      MachineFlags.WriteInto( folder, c );
      return File.ReadAllText( Path.Combine( folder, "results.json" ) );
   }

   /// <summary>
   /// Reads the conditions written by <see cref="ConditionsJson"/> back with the consolidation's
   /// own reader (RunConditions), so the two pieces agree on names and shapes.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>Build, governor, client CPUs, engine CPUs, warm-up, exact seconds, siblings, and the names the reader found missing.</returns>
   public static string[] ConditionsReadBack( string folder )
   {
      using JsonDocument doc = JsonDocument.Parse( ConditionsJson( folder ) );
      RunConditions read = RunConditions.Parse( doc.RootElement, null, null );
      return new[] { read.BuildConfiguration ?? "null", read.Governor ?? "null", read.ClientCpus ?? "null", read.EngineCpus ?? "null",
         read.WarmupSearches?.ToString( CultureInfo.InvariantCulture ) ?? "null", read.ExactSeconds?.ToString( CultureInfo.InvariantCulture ) ?? "null",
         string.Join( ";", read.ThreadSiblings ), string.Join( ";", read.MissingNames() ) };
   }

   /// <summary>
   /// Live: sets the real governor through machine control and puts it back, then does it again
   /// as a run that dies without its finally block, and lets the next start put it back.
   /// Uses the real state file, so if this test itself is killed half-way the next benchmark
   /// start (or restore-machine) still puts the governor back.
   /// </summary>
   /// <returns>Governors before, during, after; the state file's governors during; and the crash leg's figures.</returns>
   public static async Task<LiveGovernorResult> LiveGovernorAsync()
   {
      var system = new LinuxMachineSystem( () => throw new InvalidOperationException( "the governor test must not touch SQL Server" ) );
      IReadOnlyList<int> cpus = CpuList.Parse( MachineRestorer.OnlineCpus( system ) );
      string[] Read() => cpus.Select( c => system.ReadFile( GovernorControl.GovernorPath( c ) )?.Trim() ?? "missing" ).ToArray();
      string[] before = Read();
      var options = new MachineControlOptions { PinClient = false };
      var logs = new List<string>();
      MachineControl control = await MachineControl.StartAsync( MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "t" } ) ), options, system, logs.Add, CancellationToken.None );
      string[] during = Read();
      MachineState? recorded = new MachineStateStore( options.StateFile ).Load();
      string[] recordedGovernors = recorded == null ? Array.Empty<string>() : cpus.Select( c => recorded.Governors.GetValueOrDefault( c ) ?? "none" ).ToArray();
      await Task.Delay( 600 );
      int samplesDuring = control.Conditions.Passes.Count;
      await control.RestoreAsync();
      control.Dispose();
      string[] after = Read();
      bool fileAfter = File.Exists( options.StateFile );
      ( string[] crashDuring, string[] crashAfter, bool crashFileAfter, string[] crashLog ) = await CrashLegAsync( system, cpus, options.StateFile, Read );
      return new LiveGovernorResult( before, during, recordedGovernors, after, fileAfter, crashDuring, crashAfter, crashFileAfter, logs.Concat( crashLog ).ToArray(), samplesDuring );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The crash leg of the live test: records and sets the governors as a run would, then
   /// marks the state's owner as a process that no longer exists (a dead run) and lets
   /// RestoreStaleAsync, which every start calls first, put the machine back.
   /// </summary>
   /// <param name="system">The real machine.</param>
   /// <param name="cpus">Online CPUs.</param>
   /// <param name="stateFile">The real state file.</param>
   /// <param name="read">Reads every governor.</param>
   /// <returns>Governors during and after, whether the file is left, and the log.</returns>
   private static async Task<(string[] During, string[] After, bool FileAfter, string[] Log)> CrashLegAsync( IMachineSystem system, IReadOnlyList<int> cpus, string stateFile, Func<string[]> read )
   {
      var store = new MachineStateStore( stateFile );
      MachineState state = MachineStateKeeper.NewState( system );
      state.OwnerStartTicks = -1;
      var keeper = new MachineStateKeeper( store, state );
      var logs = new List<string>();
      try
      {
         await GovernorControl.ApplyAsync( system, keeper, cpus, CancellationToken.None );
         string[] during = read();
         await MachineControl.RestoreStaleAsync( store, system, logs.Add, CancellationToken.None );
         return ( during, read(), File.Exists( stateFile ), logs.ToArray() );
      }
      finally
      {
         if( File.Exists( stateFile ) )
         {
            await MachineControl.RestoreStaleAsync( store, system, logs.Add, CancellationToken.None );
         }
      }
   }

   /// <summary>
   /// A state holding one record of every kind.
   /// </summary>
   /// <param name="ownerPid">Owner process id.</param>
   /// <param name="ownerTicks">Owner start time.</param>
   /// <returns>The state.</returns>
   private static MachineState FullState( int ownerPid, long ownerTicks )
   {
      return new MachineState
      {
         OwnerPid = ownerPid,
         OwnerStartTicks = ownerTicks,
         StartedUtc = "2026-10-04T12:00:00Z",
         Governors = Enumerable.Range( 0, 8 ).ToDictionary( c => c, _ => "schedutil" ),
         Containers = { new ContainerPin( "c0ffee", "gvb-redis", string.Empty, "redis" ) },
         SqlServer = new SqlAffinityPin( "AUTO", null, "sql" ),
         Processes = { new ProcessPin( 1688, 3000, "qdrant", "0-7", "qdrant-hnsw" ) },
      };
   }

   #endregion Private Methods
}

/// <summary>
/// A container on the fake machine.
/// </summary>
/// <param name="Name">Name.</param>
/// <param name="Cpuset">HostConfig.CpusetCpus.</param>
/// <param name="Pid">Main process id.</param>
/// <param name="Exists">False when removed.</param>
public sealed record FakeContainer( string Name, string Cpuset, int Pid, bool Exists )
{
   /// <summary>Current cpuset (changed by docker update).</summary>
   public string Cpuset { get; set; } = Cpuset;
}

/// <summary>
/// A machine made of a dictionary of files and a handler per command, recording every call.
/// Its outside work is simulated: /proc/stat's busy time grows at <see cref="OutsideCpus"/>
/// CPUs per real second.
/// </summary>
public sealed class FakeMachine : IMachineSystem
{
   #region Data Members

   private static readonly int[] SQL_ORDER = { 0, 4, 1, 5, 2, 6, 3, 7 };

   private readonly Dictionary<string, Func<string>> _files = new( StringComparer.Ordinal );
   private readonly object _lock = new();
   private readonly DateTime _born = DateTime.UtcNow;
   private string _counters = string.Empty;

   #endregion Data Members

   #region Public Methods

   /// <summary>This process id on the fake machine.</summary>
   public int ProcessId { get; set; } = 4242;

   /// <summary>Every command and SQL batch, in order.</summary>
   public List<string> Calls { get; } = new();

   /// <summary>The state file's governors at each call (when <see cref="StateFile"/> is set): "call => recorded governors".</summary>
   public List<string> StateAtCall { get; } = new();

   /// <summary>Named snapshots of container cpusets, SQL affinity and thread affinities.</summary>
   public List<string> Snapshots { get; } = new();

   /// <summary>State file to snapshot at each call, or null.</summary>
   public string? StateFile { get; set; }

   /// <summary>Containers by id.</summary>
   public Dictionary<string, FakeContainer> Containers { get; } = new( StringComparer.Ordinal );

   /// <summary>Running container ids by compose file.</summary>
   public Dictionary<string, string[]> ComposeRunning { get; } = new( StringComparer.Ordinal );

   /// <summary>SQL Server affinity type.</summary>
   public string SqlType { get; set; } = "AUTO";

   /// <summary>SQL Server's online CPU ids.</summary>
   public int[] SqlOnline { get; set; } = Enumerable.Range( 0, 8 ).ToArray();

   /// <summary>True to bind SQL threads as if SQL's ids were the kernel's (a wrong mapping).</summary>
   public bool WrongSqlMapping { get; set; }

   /// <summary>CPUs of outside work /proc/stat shows.</summary>
   public double OutsideCpus { get; set; }

   /// <summary>True to make ALTER SERVER CONFIGURATION fail as a real SQL error would.</summary>
   public bool RejectSqlAlter { get; set; }

   /// <summary>True when the test sets the CPU counters by hand.</summary>
   public bool StaticCounters { get; set; }

   /// <summary>Makes a command fail: returns a result for a call to fail, null to run it normally.</summary>
   public Func<string, ShellResult?>? Fail { get; set; }

   /// <summary>
   /// The fake linus7795: 8 CPUs on schedutil, lscpu, this process.
   /// </summary>
   /// <returns>The machine.</returns>
   public static FakeMachine Linus()
   {
      var machine = new FakeMachine();
      machine.Set( "/sys/devices/system/cpu/online", "0-7\n" );
      for( int cpu = 0; cpu < 8; cpu++ )
      {
         machine.Set( GovernorControl.GovernorPath( cpu ), "schedutil\n" );
         machine.Set( GovernorControl.FrequencyPath( cpu ), "3491000\n" );
      }

      machine.AddProcess( machine.ProcessId, "dotnet", 4242, 3, "0-7", "/user.slice/bench.scope" );
      machine.SetCounters( 0, 0, 0 );
      return machine;
   }

   /// <summary>
   /// Sets a file.
   /// </summary>
   /// <param name="path">Path.</param>
   /// <param name="text">Content.</param>
   public void Set( string path, string text )
   {
      lock( _lock )
      {
         _files[path] = () => text;
      }
   }

   /// <summary>
   /// Sets the CPU counters (when <see cref="StaticCounters"/>): busy ticks of /proc/stat, this
   /// process's user ticks, and the qdrant cgroup's usage.
   /// </summary>
   /// <param name="busyTicks">Busy ticks.</param>
   /// <param name="selfTicks">This process's ticks.</param>
   /// <param name="groupUsec">qdrant.service usage_usec.</param>
   public void SetCounters( long busyTicks, long selfTicks, long groupUsec )
   {
      _counters = string.Create( CultureInfo.InvariantCulture, $"cpu  {busyTicks} 0 0 99999 0 0 0 0 0 0\ncpu0 0 0 0 0\n" );
      Set( "/proc/stat", _counters );
      Set( $"/proc/{ProcessId}/stat", Stat( ProcessId, "dotnet", 4242, selfTicks ) );
      Set( "/sys/fs/cgroup/system.slice/qdrant.service/cpu.stat", $"usage_usec {groupUsec}\nuser_usec 0\n" );
      if( !StaticCounters )
      {
         lock( _lock )
         {
            _files["/proc/stat"] = () => string.Create( CultureInfo.InvariantCulture, $"cpu  {(long)( ( DateTime.UtcNow - _born ).TotalSeconds * 100 * OutsideCpus + Drift() )} 0 0 99999 0 0 0 0 0 0\n" );
         }
      }
   }

   /// <summary>
   /// Adds a process: stat, comm, cgroup, and threads with one affinity.
   /// </summary>
   /// <param name="pid">Process id.</param>
   /// <param name="name">Command name.</param>
   /// <param name="startTicks">Start time.</param>
   /// <param name="threads">Thread count.</param>
   /// <param name="affinity">Affinity of every thread.</param>
   /// <param name="cgroup">cgroup path.</param>
   public void AddProcess( int pid, string name, long startTicks, int threads, string affinity, string cgroup )
   {
      Set( $"/proc/{pid}/stat", Stat( pid, name, startTicks, 0 ) );
      Set( $"/proc/{pid}/comm", name + "\n" );
      Set( $"/proc/{pid}/cgroup", $"0::{cgroup}\n" );
      for( int t = 0; t < threads; t++ )
      {
         Set( $"/proc/{pid}/task/{pid + t}/status", $"Name:\t{name}\nCpus_allowed_list:\t{affinity}\n" );
      }
   }

   /// <summary>
   /// Thread affinities of a process as "list xN" text.
   /// </summary>
   /// <param name="pid">Process id.</param>
   /// <returns>The text.</returns>
   public string ThreadAffinity( int pid )
   {
      return string.Join( ";", ProcessInfo.ThreadAffinities( this, pid ).OrderBy( p => p.Key ).Select( p => $"{p.Key} x{p.Value}" ) );
   }

   /// <summary>
   /// Every CPU's governor.
   /// </summary>
   /// <returns>Governors in CPU order.</returns>
   public string[] Governors()
   {
      return Enumerable.Range( 0, 8 ).Select( c => ReadFile( GovernorControl.GovernorPath( c ) )!.Trim() ).ToArray();
   }

   /// <summary>
   /// Records container cpusets, SQL affinity and qdrant/sqlservr/client thread affinities.
   /// </summary>
   /// <param name="name">Snapshot name.</param>
   public void Snapshot( string name )
   {
      string containers = string.Join( ",", Containers.Select( c => $"{c.Value.Name}={c.Value.Cpuset}" ) );
      Snapshots.Add( $"{name}: containers {containers}; sql {SqlType} {CpuList.Format( SqlOnline )}; sqlservr {ThreadAffinity( 1449 )}; qdrant {ThreadAffinity( 1688 )}; client {ThreadAffinity( ProcessId )}" );
   }

   /// <summary>
   /// Reads a file.
   /// </summary>
   /// <param name="path">Path.</param>
   /// <returns>Content or null.</returns>
   public string? ReadFile( string path )
   {
      lock( _lock )
      {
         return _files.TryGetValue( path, out Func<string>? read ) ? read() : null;
      }
   }

   /// <summary>
   /// Entries of a folder, derived from the file paths.
   /// </summary>
   /// <param name="path">Folder.</param>
   /// <returns>Entry names.</returns>
   public IReadOnlyList<string> ListDirectory( string path )
   {
      lock( _lock )
      {
         string prefix = path.TrimEnd( '/' ) + "/";
         return _files.Keys.Where( k => k.StartsWith( prefix, StringComparison.Ordinal ) ).Select( k => k[prefix.Length..].Split( '/' )[0] ).Distinct().ToList();
      }
   }

   /// <summary>
   /// Runs a fake command.
   /// </summary>
   /// <param name="program">Program.</param>
   /// <param name="arguments">Arguments.</param>
   /// <param name="timeout">Ignored.</param>
   /// <param name="ct">Ignored.</param>
   /// <returns>The result.</returns>
   public Task<ShellResult> RunAsync( string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct )
   {
      string call = program + " " + string.Join( " ", arguments );
      Record( call );
      return Task.FromResult( Fail?.Invoke( call ) ?? Run( program == "sudo" ? arguments.Skip( 1 ).ToArray() : arguments.Prepend( program ).ToArray() ) );
   }

   /// <summary>
   /// Runs a fake SQL batch.
   /// </summary>
   /// <param name="sql">The batch.</param>
   /// <param name="ct">Ignored.</param>
   /// <returns>Rows.</returns>
   public Task<IReadOnlyList<string[]>> SqlQueryAsync( string sql, CancellationToken ct )
   {
      Record( "SQL " + sql );
      if( sql.Contains( "affinity_type_desc", StringComparison.Ordinal ) )
      {
         return Rows( new[] { SqlType } );
      }

      if( sql.Contains( "dm_os_schedulers", StringComparison.Ordinal ) )
      {
         return Rows( SqlOnline.Select( i => new[] { i.ToString( CultureInfo.InvariantCulture ) } ).ToArray() );
      }

      string value = sql[( sql.IndexOf( '=' ) + 1 )..].Trim().TrimEnd( ';' ).Trim();
      if( value.Contains( '-' ) || RejectSqlAlter )
      {
         throw new InvalidOperationException( "Incorrect syntax near '-'." );
      }
      SqlType = value == "AUTO" ? "AUTO" : "MANUAL";
      SqlOnline = value == "AUTO" ? Enumerable.Range( 0, 8 ).ToArray() : CpuList.Parse( value ).ToArray();
      string bound = value == "AUTO" ? "0-7" : CpuList.Format( SqlOnline.Select( i => WrongSqlMapping ? i : SQL_ORDER[i] ) );
      Set( "/proc/1449/task/1449/status", $"Name:\tsqlservr\nCpus_allowed_list:\t{bound}\n" );
      return Rows();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs a command without its sudo prefix.
   /// </summary>
   /// <param name="a">Command and arguments.</param>
   /// <returns>The result.</returns>
   private ShellResult Run( string[] a )
   {
      string line = string.Join( " ", a );
      return a[0] switch
      {
         "lscpu" => Ok( MachineControlScenarios.LINUS_LSCPU ),
         "getconf" => Ok( "100\n" ),
         "sh" => Write( a[^1], a[^2] ),
         "taskset" when a.Length == 4 => Ok( $"pid {a[3]}'s current affinity list: {ProcessInfo.ThreadAffinities( this, int.Parse( a[3], CultureInfo.InvariantCulture ) ).Keys.FirstOrDefault() ?? "0-7"}\n" ),
         "taskset" => SetThreads( int.Parse( a[^1], CultureInfo.InvariantCulture ), a[^2] ),
         "docker" when a[1] == "compose" => Ok( string.Join( "\n", ComposeRunning.GetValueOrDefault( a[3] ) ?? Array.Empty<string>() ) + "\n" ),
         "docker" when a[1] == "inspect" => Inspect( a[^1], a[3] ),
         "docker" when a[1] == "update" => Update( a[^1], a[3] ),
         _ => new ShellResult( 1, string.Empty, $"fake machine: unexpected command {line}" ),
      };
   }

   /// <summary>
   /// Writes a file as the fake root.
   /// </summary>
   /// <param name="path">Path.</param>
   /// <param name="value">Line.</param>
   /// <returns>Success.</returns>
   private ShellResult Write( string path, string value )
   {
      Set( path, value + "\n" );
      return Ok( string.Empty );
   }

   /// <summary>
   /// Sets every thread of a process to one affinity.
   /// </summary>
   /// <param name="pid">Process id.</param>
   /// <param name="list">CPU list.</param>
   /// <returns>Success.</returns>
   private ShellResult SetThreads( int pid, string list )
   {
      foreach( string tid in ListDirectory( $"/proc/{pid}/task" ) )
      {
         Set( $"/proc/{pid}/task/{tid}/status", $"Name:\tx\nCpus_allowed_list:\t{list}\n" );
      }

      return Ok( string.Empty );
   }

   /// <summary>
   /// docker inspect of a container.
   /// </summary>
   /// <param name="id">Container id.</param>
   /// <param name="format">The -f format.</param>
   /// <returns>The result.</returns>
   private ShellResult Inspect( string id, string format )
   {
      if( !Containers.TryGetValue( id, out FakeContainer? c ) || !c.Exists )
      {
         return new ShellResult( 1, string.Empty, $"Error: No such object: {id}" );
      }

      return Ok( format.Contains( "{{.Name}}", StringComparison.Ordinal ) ? $"/{c.Name}|{c.Cpuset}|{c.Pid}\n" : c.Cpuset + "\n" );
   }

   /// <summary>
   /// docker update of a container's cpuset (and its main process's threads).
   /// </summary>
   /// <param name="id">Container id.</param>
   /// <param name="list">CPU list.</param>
   /// <returns>The result.</returns>
   private ShellResult Update( string id, string list )
   {
      if( !Containers.TryGetValue( id, out FakeContainer? c ) || !c.Exists )
      {
         return new ShellResult( 1, string.Empty, $"Error response from daemon: No such container: {id}" );
      }

      c.Cpuset = list;
      SetThreads( c.Pid, list );
      return Ok( id + "\n" );
   }

   /// <summary>
   /// Records a call and, when a state file is watched, its governors at that moment.
   /// </summary>
   /// <param name="call">The call.</param>
   private void Record( string call )
   {
      lock( _lock )
      {
         Calls.Add( call );
      }

      if( StateFile != null )
      {
         string recorded = File.Exists( StateFile ) ? string.Join( ",", new MachineStateStore( StateFile ).Load()!.Governors.OrderBy( g => g.Key ).Select( g => g.Value ) ) : "no file";
         StateAtCall.Add( $"{call} => {recorded}" );
      }
   }

   /// <summary>
   /// A little busy time that always grows, so /proc/stat is never flat.
   /// </summary>
   /// <returns>Ticks.</returns>
   private double Drift()
   {
      return 1000;
   }

   /// <summary>
   /// A /proc/PID/stat line.
   /// </summary>
   /// <param name="pid">Process id.</param>
   /// <param name="name">Command name.</param>
   /// <param name="startTicks">Start time (field 22).</param>
   /// <param name="userTicks">User time (field 14).</param>
   /// <returns>The line.</returns>
   private static string Stat( int pid, string name, long startTicks, long userTicks )
   {
      string[] after = Enumerable.Repeat( "0", 30 ).ToArray();
      after[0] = "S";
      after[11] = userTicks.ToString( CultureInfo.InvariantCulture );
      after[19] = startTicks.ToString( CultureInfo.InvariantCulture );
      return $"{pid} ({name}) {string.Join( " ", after )}\n";
   }

   /// <summary>
   /// A successful result.
   /// </summary>
   /// <param name="output">Output.</param>
   /// <returns>The result.</returns>
   private static ShellResult Ok( string output )
   {
      return new ShellResult( 0, output, string.Empty );
   }

   /// <summary>
   /// Rows as a finished task.
   /// </summary>
   /// <param name="rows">Rows.</param>
   /// <returns>The task.</returns>
   private static Task<IReadOnlyList<string[]>> Rows( params string[][] rows )
   {
      return Task.FromResult<IReadOnlyList<string[]>>( rows );
   }

   #endregion Private Methods
}

/// <summary>CPU split as text.</summary>
/// <param name="Engine">Engine CPUs.</param>
/// <param name="Client">Client CPUs.</param>
/// <param name="Siblings">Siblings per core.</param>
/// <param name="Problem">Why not split.</param>
/// <param name="SqlEngineIds">SQL Server ids of the engine CPUs.</param>
/// <param name="Description">Describe().</param>
public sealed record PartitionResult( string Engine, string Client, string[] Siblings, string? Problem, string SqlEngineIds, string Description );

/// <summary>Parsed machine options.</summary>
/// <param name="MachineControl">On or off.</param>
/// <param name="StateFile">State file.</param>
/// <param name="Command">Command.</param>
public sealed record OptionsResult( bool MachineControl, string StateFile, string Command );

/// <summary>State file checks.</summary>
/// <param name="Same">Round trip kept every field.</param>
/// <param name="TmpLeft">The temporary file was left behind.</param>
/// <param name="MissingIsNull">A missing file loads as null.</param>
/// <param name="CorruptMessage">Message for a broken file.</param>
/// <param name="Path">The file.</param>
/// <param name="Describe">Describe() of the loaded state.</param>
public sealed record StateResult( bool Same, bool TmpLeft, bool MissingIsNull, string CorruptMessage, string Path, string Describe );

/// <summary>Restore-after-crash results.</summary>
/// <param name="Calls">Commands run.</param>
/// <param name="FileLeft">State file still there.</param>
/// <param name="FileText">Its content when left.</param>
/// <param name="Error">Error message, if any.</param>
/// <param name="Governors">Governors after.</param>
/// <param name="ContainerCpuset">Container cpuset after.</param>
/// <param name="SqlType">SQL affinity after.</param>
/// <param name="QdrantThreads">Qdrant thread affinities after.</param>
public sealed record RestoreResult( string[] Calls, bool FileLeft, string? FileText, string? Error, string[] Governors, string ContainerCpuset, string SqlType, string QdrantThreads );

/// <summary>Whole-run results on the fake machine.</summary>
/// <param name="Calls">Commands and SQL, in order.</param>
/// <param name="StateAtCall">Recorded governors at each call.</param>
/// <param name="GovernorsDuring">Governors after start.</param>
/// <param name="GovernorsAfter">Governors after restore.</param>
/// <param name="FileDuring">State file existed during the run.</param>
/// <param name="FileAfter">State file exists after.</param>
/// <param name="ConditionsJson">Conditions as JSON.</param>
/// <param name="RunNotes">Run-level notes.</param>
/// <param name="TargetNotes">Notes of the first target.</param>
/// <param name="Snapshots">Machine snapshots during each target.</param>
/// <param name="Log">Progress output.</param>
public sealed record RunResult( string[] Calls, string[] StateAtCall, string[] GovernorsDuring, string[] GovernorsAfter, bool FileDuring, bool FileAfter, string ConditionsJson, string[] RunNotes, string[] TargetNotes, string[] Snapshots, string[] Log );

/// <summary>Busy-box results.</summary>
/// <param name="Passes">Each pass as text.</param>
/// <param name="Log">Progress output.</param>
/// <param name="Reasons">Busy reasons.</param>
public sealed record GateResult( string[] Passes, string[] Log, string[] Reasons );

/// <summary>Sampler arithmetic.</summary>
/// <param name="Outside">Outside CPUs.</param>
/// <param name="Cpus">"cpuN min/median/max".</param>
/// <param name="Samples">Samples in the window.</param>
/// <param name="ShortSamples">Samples in a 20 ms window.</param>
/// <param name="Nearest">The nearest sample stood in for the short window.</param>
/// <param name="Error">Sampling error.</param>
public sealed record SamplerResult( double? Outside, string[] Cpus, int Samples, int ShortSamples, bool Nearest, string? Error );

/// <summary>Live governor results.</summary>
/// <param name="Before">Governors before.</param>
/// <param name="During">Governors while controlled.</param>
/// <param name="Recorded">Governors the state file recorded.</param>
/// <param name="After">Governors after restore.</param>
/// <param name="FileAfter">State file left after restore.</param>
/// <param name="CrashDuring">Governors after the crash leg set them.</param>
/// <param name="CrashAfter">Governors after the stale restore.</param>
/// <param name="CrashFileAfter">State file left after the stale restore.</param>
/// <param name="Log">Progress output.</param>
/// <param name="Passes">Passes recorded (none expected).</param>
public sealed record LiveGovernorResult( string[] Before, string[] During, string[] Recorded, string[] After, bool FileAfter, string[] CrashDuring, string[] CrashAfter, bool CrashFileAfter, string[] Log, int Passes );
#endif
