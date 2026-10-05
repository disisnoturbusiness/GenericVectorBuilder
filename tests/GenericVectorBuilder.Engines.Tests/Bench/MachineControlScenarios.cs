// Compiled only by MachineControlTests and MachineControlLiveTests, together with the
// benchmark's own sources (the test project does not reference the benchmark console project).
// BENCH_UNDER_TEST is defined only in that compilation, so the test project itself sees an
// empty file.
#if BENCH_UNDER_TEST
using System.Globalization;
using System.Text.Json;
using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Core.Contracts;
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
public static partial class MachineControlScenarios
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
   /// <param name="mode">"dead-owner", "live-owner", "docker-fails", "gone", "sql-manual" (SQL Server had a manual affinity before) or "pinned-start" (containers of a pinned start still exist).</param>
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

      machine.Containers["e5e5"] = new FakeContainer( "gvb-elasticsearch", "2-3,6-7", 950, mode == "pinned-start" );
      machine.Containers["d0d0"] = new FakeContainer( "gvb-es-other", "1", 951, mode == "pinned-start" );
      machine.ComposeRunning["/x/es.compose.yaml"] = mode == "pinned-start" ? new[] { "e5e5", "d0d0" } : Array.Empty<string>();

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
         machine.Containers["c0ffee"].Cpuset, machine.SqlType, machine.ThreadAffinity( 1688 ), machine.Containers["e5e5"].Cpuset + "|" + machine.Containers["d0d0"].Cpuset );
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
   /// The busy-box rule on a fake machine whose outside work is switched on and off. The check
   /// is made at each pass's warm-up line: a warm-up announced while 3 CPUs of outside work run
   /// waits its limit, and its pass is flagged even though the box goes quiet before the timing
   /// line; a warm-up announced on a quiet box goes on at once, and outside work that starts
   /// between the warm-up and the timing line does not hold the pass (no second wait there, not
   /// even at the "warm-up stopped" line of a warm-up cut short); a pass announced with no
   /// warm-up line is checked at its timing line instead.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>Each pass as "target pass busy waited endedBy before", the log, and the busy reasons.</returns>
   public static async Task<GateResult> GateAsync( string folder )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.OutsideCpus = 3;
      var options = new MachineControlOptions
      {
         StateFile = Path.Combine( folder, "state.json" ), PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 25 ),
         BusyWindow = TimeSpan.FromSeconds( 1 ), RecentWindow = TimeSpan.FromMilliseconds( 300 ), BusyWait = TimeSpan.FromMilliseconds( 400 ), BusyPoll = TimeSpan.FromMilliseconds( 25 ),
      };
      var logs = new List<string>();
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "t" } ) );
      using MachineControl control = await MachineControl.StartAsync( conditions, options, machine, logs.Add, CancellationToken.None );
      Action<string> log = control.WrapLog( logs.Add );
      await Task.Delay( 900 );
      log( "  t: warm-up before default@8, 20 searches" );
      machine.OutsideCpus = 0;
      log( "  t: timing default@8" );
      await Task.Delay( 100 );
      log( "  t: 812.5 QPS at concurrency 8" );
      await Task.Delay( 1300 );
      log( "  t: warm-up before exact, 20 searches" );
      machine.OutsideCpus = 3;
      await Task.Delay( 200 );
      log( "  t: warm-up before exact stopped after 3 searches (time budget)" );
      log( "  t: timing exact" );
      log( "  t: exact mode p50 0.80 ms over 20 queries, recall 1.000" );
      await Task.Delay( 600 );
      log( "  t: timing default@1" );
      await control.LeaveTargetAsync( "t" );
      await control.RestoreAsync();
      string[] passes = control.Conditions.Passes.Select( p => string.Create( CultureInfo.InvariantCulture, $"{p.Target} {p.Pass} busy={p.BusyBox} waited={p.WaitedSeconds:0.0} by={p.EndedBy} before={p.QuietCheckBefore} load={MachineFlags.Load( p.OutsideLoadAtStart )}" ) ).ToArray();
      return new GateResult( passes, logs.ToArray(), control.Conditions.Passes.Select( p => p.BusyReason ?? string.Empty ).ToArray() );
   }

   /// <summary>
   /// The deadline is per pass: a pass whose warm-up is announced twice (again after a settle
   /// extension) while 3 CPUs of outside work run waits the deadline once in all, not once per
   /// announcement, and is flagged.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>The pass as "pass busy waited", the wall seconds from the first warm-up line to the timing line, and the log.</returns>
   public static async Task<RecheckResult> RecheckAsync( string folder )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.OutsideCpus = 3;
      var options = new MachineControlOptions
      {
         StateFile = Path.Combine( folder, "state.json" ), PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 25 ),
         BusyWindow = TimeSpan.FromSeconds( 1 ), RecentWindow = TimeSpan.FromMilliseconds( 300 ), BusyWait = TimeSpan.FromMilliseconds( 600 ), BusyPoll = TimeSpan.FromMilliseconds( 25 ),
      };
      var logs = new List<string>();
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "t" } ) );
      using MachineControl control = await MachineControl.StartAsync( conditions, options, machine, logs.Add, CancellationToken.None );
      Action<string> log = control.WrapLog( logs.Add );
      await Task.Delay( 900 );
      var wall = System.Diagnostics.Stopwatch.StartNew();
      log( "  t: warm-up before default@1, 20 searches" );
      log( "  t: warm-up before default@1, 20 searches (again, after the settle extension)" );
      log( "  t: timing default@1" );
      double seconds = wall.Elapsed.TotalSeconds;
      log( "  t: p50 1.00 ms, p95 2.00 ms over 300 searches, 9.0 QPS with one searcher, recall@10 1.000" );
      await control.LeaveTargetAsync( "t" );
      await control.RestoreAsync();
      string[] passes = control.Conditions.Passes.Select( p => string.Create( CultureInfo.InvariantCulture, $"{p.Pass} busy={p.BusyBox} waited={p.WaitedSeconds:0.0}" ) ).ToArray();
      return new RecheckResult( passes, seconds, logs.ToArray() );
   }

   /// <summary>
   /// An engine's own start-up is not outside work: run-all starts a compose engine through the
   /// machine-control host while its containers burn 3 CPUs (before their cgroup is known, so the
   /// kernel counters show it as outside work); the next warm-up goes on at once. The same burn
   /// from a process outside the benchmark, during another target's turn, holds that target's
   /// warm-up and flags its pass. Also shows the pinned start: recorded in the state file before
   /// the start, the container created on the engine CPUs needs no docker update, and leaving the
   /// target gives a container still on those CPUs every CPU back.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <returns>The passes, the state during the start, the calls, the pinning record and the log.</returns>
   public static async Task<StartupResult> StartupAsync( string folder )
   {
      FakeMachine machine = FakeMachine.Linus();
      machine.AddProcess( 1680, "dockerd", 1000, 2, "0-7", "/system.slice/docker.service" );
      var store = new MachineStateStore( Path.Combine( folder, "state.json" ) );
      var options = new MachineControlOptions
      {
         StateFile = store.Path, PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 25 ),
         BusyWindow = TimeSpan.FromSeconds( 2 ), RecentWindow = TimeSpan.FromMilliseconds( 300 ), BusyWait = TimeSpan.FromMilliseconds( 400 ), BusyPoll = TimeSpan.FromMilliseconds( 25 ),
      };
      var logs = new List<string>();
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "run-all", "--pipeline", "p" } ) );
      MachineControl control = await MachineControl.StartAsync( conditions, options, machine, logs.Add, CancellationToken.None );
      Action<string> log = control.WrapLog( logs.Add );
      var inner = new StartingHost( machine, store.Path );
      IEngineHost host = control.WrapHost( inner );
      await control.EnterTargetAsync( "es", "compose", StartingHost.COMPOSE, CancellationToken.None );
      await host.UpAsync( StartingHost.COMPOSE, "2-3,6-7", CancellationToken.None );
      await TimedPassAsync( log, "es" );
      await control.LeaveTargetAsync( "es" );
      await control.EnterTargetAsync( "other", "embedded", null, CancellationToken.None );
      machine.OutsideCpus = 3;
      await Task.Delay( 600 );
      machine.OutsideCpus = 0;
      await TimedPassAsync( log, "other" );
      await control.LeaveTargetAsync( "other" );
      await control.RestoreAsync();
      control.Dispose();
      string[] passes = control.Conditions.Passes.Select( p => string.Create( CultureInfo.InvariantCulture, $"{p.Target} busy={p.BusyBox} waited={p.WaitedSeconds:0.0} load={MachineFlags.Load( p.OutsideLoadAtStart )}" ) ).ToArray();
      TargetPinning es = control.Conditions.Engines.First( e => e.Target == "es" );
      return new StartupResult( passes, inner.StateDuringUp ?? "no state read", machine.Calls.ToArray(), es.Method, es.Changes.ToArray(), control.Conditions.Restored.Concat( es.Restored ).ToArray(),
         File.Exists( store.Path ), logs.ToArray() );
   }

   /// <summary>
   /// The CPU idle settings with machine control on (or off): read at the start and the end,
   /// never written; a change made under the run (someone disables C6 on CPU 5) is flagged.
   /// </summary>
   /// <param name="folder">Scratch folder.</param>
   /// <param name="on">True with machine control on.</param>
   /// <returns>The settings at start and end as text and JSON, the calls, and the flags.</returns>
   public static async Task<IdleResult> IdleAsync( string folder, bool on )
   {
      FakeMachine machine = FakeMachine.Linus();
      var options = new MachineControlOptions { Enabled = on, StateFile = Path.Combine( folder, "state.json" ), PinClient = false, SampleInterval = TimeSpan.FromMilliseconds( 25 ) };
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "t" } ) );
      MachineControl control = await MachineControl.StartAsync( conditions, options, machine, _ => { }, CancellationToken.None );
      machine.Set( CpuIdleReader.StatePath( 5, 4, "disable" ), "1\n" );
      await control.RestoreAsync();
      control.Dispose();
      var report = new BenchReport();
      control.Annotate( report );
      string json = JsonSerializer.Serialize( control.Conditions, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } );
      return new IdleResult( control.Conditions.CpuIdle?.Describe() ?? "null", control.Conditions.CpuIdleAtEnd?.Describe() ?? "null", json, machine.Calls.ToArray(), report.Notes.ToArray() );
   }

   /// <summary>
   /// Connection records: /proc/net/tcp addresses decoded, this process's established sockets
   /// found through its file descriptors, and the record of a container target (bound through a
   /// fake docker inspect), the always-on SQL Server and an embedded engine, each with the
   /// connections seen classified; the docker-proxy connection is flagged.
   /// </summary>
   /// <returns>Decoded addresses, peers, notes, the conditions JSON and the flags.</returns>
   public static async Task<ConnectionResult> ConnectionsAsync()
   {
      string[] decoded = new[] { "0100007F:0599", "0000000000000000FFFF00000100007F:18BE", "020016AC:23F0", "00000000000000000000000001000000:1F90" }
         .Select( h => { ( string a, int port ) = ConnectionRecorder.ParseEndpoint( h ); return $"{a}:{port}"; } ).ToArray();
      FakeMachine machine = FakeMachine.Linus();
      machine.AddSockets();
      Dictionary<(string Address, int Port), int> peers = ConnectionRecorder.EstablishedPeers( machine, machine.ProcessId );
      var recorder = new ConnectionRecorder( machine, "localhost", "localhost", 6334 );
      Func<string, CancellationToken, Task<Measurement>> none = ( _, _ ) => Task.FromResult( Measurement.None( "fake" ) );
      var es = new BenchTarget( new NullSink( "elasticsearch" ), "ES", "flat", "compose", "/x/es.compose.yaml", none, none )
      {
         Container = new ContainerBinding( new EngineRoute( new[] { "gvb-elasticsearch" }, router => { router.Address( "gvb-elasticsearch", 9200 ); return new NullSink( "elasticsearch" ); } ),
            new JsonInspector(), TimeSpan.FromSeconds( 5 ), TimeSpan.FromMilliseconds( 50 ) ),
      };
      await es.BindAsync( CancellationToken.None );
      var sql = new BenchTarget( new NullSink( "sql" ), "SQL Server", "flat", "always-on", null, none, none );
      var duck = new BenchTarget( new NullSink( "duckdb" ), "DuckDB", "flat", "embedded", null, none, none );
      var conditions = new MachineConditions();
      foreach( BenchTarget target in new[] { es, sql, duck } )
      {
         if( target.Hosting != "embedded" )
         {
            recorder.Observe( target );
         }

         conditions.Connections.Add( recorder.Describe( target ) );
      }

      return new ConnectionResult( decoded, peers.OrderBy( p => p.Key.Address, StringComparer.Ordinal ).ThenBy( p => p.Key.Port ).Select( p => $"{p.Key.Address}:{p.Key.Port} x{p.Value}" ).ToArray(),
         conditions.Connections.Select( ConnectionRecorder.Note ).ToArray(), JsonSerializer.Serialize( conditions.Connections, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } ),
         MachineFlags.Compute( conditions, Array.Empty<string>() ).ToArray(), ConnectionRecorder.SqlEndpoint( "tcp:db1,1500" ).ToString(), ConnectionRecorder.SqlEndpoint( @"db2\inst" ).ToString() );
   }

   /// <summary>
   /// The busy-box defaults and the rule text a run records.
   /// </summary>
   /// <returns>Limit, deadline seconds, window seconds, recent window seconds, rule text.</returns>
   public static string[] BusyDefaults()
   {
      var o = new MachineControlOptions();
      return new[] { o.BusyThreshold.ToString( CultureInfo.InvariantCulture ), o.BusyWait.TotalSeconds.ToString( CultureInfo.InvariantCulture ),
         o.BusyWindow.TotalSeconds.ToString( CultureInfo.InvariantCulture ), o.RecentWindow.TotalSeconds.ToString( CultureInfo.InvariantCulture ), RuleText().GetAwaiter().GetResult() };
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
      var first = new PassConditions { Target = "a", Pass = "default@1", Governor = "performance", OutsideLoadDuring = 0.07 };
      first.ApplyClientCpu( 0.5, 1000, "basis", null );
      c.Passes.Add( first );
      c.Engines.Add( new TargetPinning { Target = "a", Hosting = "always-on", Cpus = "2-3,6-7" } );
      if( scenario == "bad" )
      {
         c.BuildConfiguration = "Debug";
         c.JitOptimizerDisabled = true;
         c.GovernorAtEnd = "mixed: cpu0 performance, cpu1 schedutil";
         c.PartitionProblem = "only one physical core is online";
         c.Passes.Add( new PassConditions { Target = "a", Pass = "default@8", Governor = "schedutil", BusyBox = true, BusyReason = "outside 2.10 CPUs", NearestSample = true, OutsideLoadDuring = -0.4 } );
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

   /// <summary>
   /// Live: starts a throwaway compose project (one redis:8.10.2 container named
   /// gvbbench-cpuset-live, no ports, no volumes, no cpuset of its own) through the real
   /// <see cref="ComposeEngineHost"/> asking for the engine CPU set, reads what the host said, what
   /// Docker and the kernel say the container got and how many CPUs the process inside sees, and
   /// always stops and removes it again.
   /// </summary>
   /// <param name="folder">Scratch folder for the compose file.</param>
   /// <returns>The host's read-back ("none" for null), the cpuset, the main process's allowed CPUs, nproc inside, and the containers left afterwards.</returns>
   public static async Task<string[]> LivePinnedStartAsync( string folder )
   {
      string compose = Path.Combine( folder, "gvbbench-cpuset-live.compose.yaml" );
      File.WriteAllText( compose, "name: gvbbench-cpuset-live\nservices:\n  probe:\n    image: redis:8.10.2\n    container_name: gvbbench-cpuset-live\n"
         + "    command: [\"redis-server\", \"--save\", \"\", \"--appendonly\", \"no\"]\n    mem_limit: 256m\n    restart: \"no\"\n" );
      var system = new LinuxMachineSystem( () => throw new InvalidOperationException( "the live start test must not touch SQL Server" ) );
      var host = new ComposeEngineHost();
      using var limit = new CancellationTokenSource( TimeSpan.FromMinutes( 5 ) );
      try
      {
         string? readBack = await host.UpAsync( compose, "2-3,6-7", limit.Token );
         string inspect = ( await ProcessInfo.SudoAsync( system, new[] { "docker", "inspect", "-f", "{{.HostConfig.CpusetCpus}}|{{.State.Pid}}", "gvbbench-cpuset-live" }, limit.Token ) ).Output.Trim();
         int pid = int.Parse( inspect.Split( '|' )[1], CultureInfo.InvariantCulture );
         string nproc = ( await ProcessInfo.SudoAsync( system, new[] { "docker", "exec", "gvbbench-cpuset-live", "nproc" }, limit.Token ) ).Output.Trim();
         return new[] { readBack ?? "none", inspect.Split( '|' )[0], string.Join( ";", ProcessInfo.ThreadAffinities( system, pid ).Select( t => $"{t.Key} x{t.Value}" ) ), nproc, await LeftAsync( system, host, compose ) };
      }
      finally
      {
         await host.DownAsync( compose, CancellationToken.None );
      }
   }

   /// <summary>
   /// Live: the busy-box rule on the real machine with the real sampler (/proc/stat, this
   /// process, cgroups) and the real gate, with a limit of 2 CPUs and short windows (10 s and 5 s)
   /// so it runs in about a minute and a half. Why 2 CPUs and not the run's 0.3: the shared box's
   /// own background (other sessions' engines and tests) was 0.5 to 1 CPU while this was written,
   /// so a lower limit would never clear; the test first checks (for at most 30 s) that the
   /// background is under 1.2 CPUs, and says what it was. Leg 1: 4 busy loops for 4 s start 3 s
   /// before a warm-up line; the warm-up is held until the box is quiet again, then the pass
   /// opens. Leg 2: 3 busy loops for 28 s start 4 s before a warm-up line; the warm-up is held
   /// for the whole 20 s deadline and the pass runs flagged. The busy loops are started
   /// detached (not children of this process), so their CPU time is outside work and never
   /// folded into this process's own counters; each is stopped by its own timeout and by pid at
   /// the end.
   /// </summary>
   /// <returns>Background, limit, both passes, the log and the governors before and after.</returns>
   public static async Task<LiveGateResult> LiveBusyBoxAsync()
   {
      var system = new LinuxMachineSystem( () => throw new InvalidOperationException( "the live busy-box test must not touch SQL Server" ) );
      IReadOnlyList<int> cpus = CpuList.Parse( MachineRestorer.OnlineCpus( system ) );
      string[] before = cpus.Select( c => system.ReadFile( GovernorControl.GovernorPath( c ) )?.Trim() ?? "missing" ).ToArray();
      double background = await QuietBackgroundAsync( system );
      var options = new MachineControlOptions
      {
         PinClient = false, BusyThreshold = 2.0, BusyWindow = TimeSpan.FromSeconds( 10 ), RecentWindow = TimeSpan.FromSeconds( 5 ),
         BusyWait = TimeSpan.FromSeconds( 20 ), BusyPoll = TimeSpan.FromMilliseconds( 500 ),
      };
      var logs = new List<string>();
      var loops = new List<int>();
      MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "live" } ) );
      MachineControl control = await MachineControl.StartAsync( conditions, options, system, line => logs.Add( $"{DateTime.UtcNow:HH:mm:ss.f} {line}" ), CancellationToken.None );
      try
      {
         Action<string> log = control.WrapLog( line => logs.Add( $"{DateTime.UtcNow:HH:mm:ss.f} {line}" ) );
         await Task.Delay( TimeSpan.FromSeconds( 8 ) );
         await LivePassAsync( system, log, logs, loops, "default@1", 4, 4, 3 );
         await Task.Delay( TimeSpan.FromSeconds( 12 ) );
         await LivePassAsync( system, log, logs, loops, "exact", 3, 28, 4 );
      }
      finally
      {
         await StopLoopsAsync( system, loops );
         await control.RestoreAsync();
         control.Dispose();
      }

      string[] after = cpus.Select( c => system.ReadFile( GovernorControl.GovernorPath( c ) )?.Trim() ?? "missing" ).ToArray();
      string[] passes = control.Conditions.Passes.Select( p => string.Create( CultureInfo.InvariantCulture,
         $"{p.Pass} before={p.QuietCheckBefore} waited={p.WaitedSeconds:0.0} busy={p.BusyBox} windowLoad={MachineFlags.Load( p.OutsideLoadAtStart )} recentLoad={MachineFlags.Load( p.OutsideLoadRecentAtStart )} checked={p.QuietCheckUtc} start={p.StartUtc} reason={p.BusyReason}" ) ).ToArray();
      return new LiveGateResult( background, options.BusyThreshold, passes, logs.ToArray(), before, after, File.Exists( options.StateFile ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// One live pass: starts detached busy loops, announces the warm-up a few seconds later (the
   /// gate holds it while the box is busy), then the timing and a result line.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="log">The wrapped log.</param>
   /// <param name="logs">The plain log, for the loop start time.</param>
   /// <param name="loops">Receives the pids of the loops' timeout processes.</param>
   /// <param name="pass">Pass name.</param>
   /// <param name="count">Busy loops.</param>
   /// <param name="seconds">How long the busy loops run.</param>
   /// <param name="lead">Seconds between starting the loops and the warm-up line.</param>
   private static async Task LivePassAsync( IMachineSystem system, Action<string> log, List<string> logs, List<int> loops, string pass, int count, int seconds, int lead )
   {
      string script = string.Create( CultureInfo.InvariantCulture, $"for i in $(seq {count}); do setsid timeout {seconds} bash -c 'while :; do :; done' >/dev/null 2>&1 < /dev/null & echo $!; done" );
      ShellResult started = await system.RunAsync( "bash", new[] { "-c", script }, TimeSpan.FromSeconds( 10 ), CancellationToken.None );
      loops.AddRange( started.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries ).Select( p => int.Parse( p.Trim(), CultureInfo.InvariantCulture ) ) );
      logs.Add( $"{DateTime.UtcNow:HH:mm:ss.f} live: {count} busy loops started for {seconds} s (pids {started.Output.Replace( '\n', ' ' ).Trim()})" );
      await Task.Delay( TimeSpan.FromSeconds( lead ) );
      log( $"  live: warm-up before {pass}, 20 searches" );
      log( $"  live: timing {pass}" );
      await Task.Delay( TimeSpan.FromSeconds( 2 ) );
      log( pass == "exact" ? "  live: exact mode p50 1.00 ms over 20 searches, recall 1.000" : "  live: p50 1.00 ms, p95 2.00 ms over 300 searches, 9.0 QPS with one searcher, recall@10 1.000" );
   }

   /// <summary>
   /// Stops the busy loops this test started, by pid (they also end on their own timeout).
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="loops">Pids of the loops' timeout processes.</param>
   private static async Task StopLoopsAsync( IMachineSystem system, List<int> loops )
   {
      foreach( int pid in loops.Where( p => system.ReadFile( $"/proc/{p}/comm" )?.Trim() == "timeout" ) )
      {
         await system.RunAsync( "kill", new[] { pid.ToString( CultureInfo.InvariantCulture ) }, TimeSpan.FromSeconds( 10 ), CancellationToken.None );
      }
   }

   /// <summary>
   /// Waits, at most 30 s (three 10 s readings), until the box's busy CPUs over 10 s are under 1.2.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <returns>The background that let the test go on.</returns>
   /// <exception cref="TimeoutException">The box stayed busier than that for all three readings.</exception>
   private static async Task<double> QuietBackgroundAsync( IMachineSystem system )
   {
      double background = 0;
      for( int reading = 0; reading < 3; reading++ )
      {
         background = await BusyCpusAsync( system, TimeSpan.FromSeconds( 10 ) );
         if( background < 1.2 )
         {
            return background;
         }
      }

      throw new TimeoutException( $"the box was at {background} busy CPUs (1.2 or more in three 10 s readings); the live busy-box test needs it quieter" );
   }

   /// <summary>
   /// CPUs busy on the whole box (user + nice + system) over a span, from /proc/stat.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="span">How long to measure.</param>
   /// <returns>Busy CPUs.</returns>
   private static async Task<double> BusyCpusAsync( IMachineSystem system, TimeSpan span )
   {
      static double Busy( IMachineSystem m )
      {
         string[] f = m.ReadFile( "/proc/stat" )!.Split( '\n' )[0].Split( ' ', StringSplitOptions.RemoveEmptyEntries );
         return double.Parse( f[1], CultureInfo.InvariantCulture ) + double.Parse( f[2], CultureInfo.InvariantCulture ) + double.Parse( f[3], CultureInfo.InvariantCulture );
      }

      double first = Busy( system );
      await Task.Delay( span );
      return Math.Round( ( Busy( system ) - first ) / 100 / span.TotalSeconds, 2 );
   }

   /// <summary>
   /// Stops the throwaway project and lists any gvbbench-cpuset-live container still there.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="host">The host.</param>
   /// <param name="compose">Compose file.</param>
   /// <returns>Containers left ("none" when gone).</returns>
   private static async Task<string> LeftAsync( IMachineSystem system, ComposeEngineHost host, string compose )
   {
      string? problem = await host.DownAsync( compose, CancellationToken.None );
      string left = ( await ProcessInfo.SudoAsync( system, new[] { "docker", "ps", "-a", "--filter", "name=gvbbench-cpuset-live", "--format", "{{.Names}}" }, CancellationToken.None ) ).Output.Trim();
      return problem ?? ( left.Length == 0 ? "none" : left );
   }

   /// <summary>
   /// The busy rule a run with default options records (started on a fake machine).
   /// </summary>
   /// <returns>The rule.</returns>
   private static async Task<string> RuleText()
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "machine-control-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( folder );
      try
      {
         MachineConditions conditions = MachineConditions.ForRun( BenchOptions.Parse( new[] { "bench", "--pipeline", "p", "--targets", "t" } ) );
         using MachineControl control = await MachineControl.StartAsync( conditions, new MachineControlOptions { StateFile = Path.Combine( folder, "state.json" ), PinClient = false }, FakeMachine.Linus(), _ => { }, CancellationToken.None );
         return conditions.BusyRule ?? "none";
      }
      finally
      {
         Directory.Delete( folder, true );
      }
   }

   /// <summary>
   /// Sends a warm-up, a timing and a result line for one target's default@1 pass.
   /// </summary>
   /// <param name="log">The wrapped log.</param>
   /// <param name="target">Target name.</param>
   private static async Task TimedPassAsync( Action<string> log, string target )
   {
      await Task.Delay( 300 );
      log( $"  {target}: warm-up before default@1, 20 searches" );
      log( $"  {target}: timing default@1" );
      await Task.Delay( 50 );
      log( $"  {target}: p50 1.00 ms, p95 2.00 ms over 300 searches, 9.0 QPS with one searcher, recall@10 1.000" );
   }

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
         PinnedStarts = { new PinnedStart( "/x/es.compose.yaml", "2-3,6-7", "elasticsearch" ) },
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
   private readonly Dictionary<string, string> _links = new( StringComparer.Ordinal );
   private readonly object _lock = new();
   private DateTime _lastChange = DateTime.UtcNow;
   private double _outsideCpus;
   private double _busyBase;
   private double _ownCpus;
   private double _ownBase;
   private DateTime _ownChange = DateTime.UtcNow;
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

   /// <summary>
   /// CPUs of outside work /proc/stat shows from now on. The busy counter integrates over time,
   /// so switching the work on and off gives a counter that only grows, as the kernel's does.
   /// </summary>
   public double OutsideCpus
   {
      get => _outsideCpus;
      set
      {
         lock( _lock )
         {
            DateTime now = DateTime.UtcNow;
            _busyBase += ( now - _lastChange ).TotalSeconds * 100 * _outsideCpus;
            _lastChange = now;
            _outsideCpus = value;
         }
      }
   }

   /// <summary>
   /// CPUs of work this (client) process does from now on: its utime grows at this rate in
   /// /proc/PID/stat, and /proc/stat's busy time grows with it, so it is never outside work.
   /// Integrates over time like <see cref="OutsideCpus"/>.
   /// </summary>
   public double OwnCpus
   {
      get => _ownCpus;
      set
      {
         lock( _lock )
         {
            DateTime now = DateTime.UtcNow;
            _ownBase += ( now - _ownChange ).TotalSeconds * 100 * _ownCpus;
            _ownChange = now;
            _ownCpus = value;
         }
      }
   }

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
      machine.AddIdleStates();
      return machine;
   }

   /// <summary>
   /// The idle states of linus7795 on every CPU: POLL, C1, C1E, C3 (disabled, its default), C6;
   /// intel_idle with the menu governor and max_cstate 9.
   /// </summary>
   public void AddIdleStates()
   {
      Set( "/sys/devices/system/cpu/cpuidle/current_driver", "intel_idle\n" );
      Set( "/sys/devices/system/cpu/cpuidle/current_governor", "menu\n" );
      Set( "/sys/module/intel_idle/parameters/max_cstate", "9\n" );
      (string Name, int Latency, int Residency, bool Off)[] states = { ( "POLL", 0, 0, false ), ( "C1", 2, 2, false ), ( "C1E", 10, 20, false ), ( "C3", 33, 100, true ), ( "C6", 133, 400, false ) };
      for( int cpu = 0; cpu < 8; cpu++ )
      {
         for( int i = 0; i < states.Length; i++ )
         {
            Set( CpuIdleReader.StatePath( cpu, i, "name" ), states[i].Name + "\n" );
            Set( CpuIdleReader.StatePath( cpu, i, "latency" ), states[i].Latency.ToString( CultureInfo.InvariantCulture ) + "\n" );
            Set( CpuIdleReader.StatePath( cpu, i, "residency" ), states[i].Residency.ToString( CultureInfo.InvariantCulture ) + "\n" );
            Set( CpuIdleReader.StatePath( cpu, i, "disable" ), states[i].Off ? "1\n" : "0\n" );
            Set( CpuIdleReader.StatePath( cpu, i, "default_status" ), states[i].Off ? "disabled\n" : "enabled\n" );
         }
      }
   }

   /// <summary>
   /// This process's sockets: fd 3 to the Elasticsearch container 172.22.0.2:9200 (twice, fds 3
   /// and 8), fd 4 to SQL Server 127.0.0.1:1433, fd 7 over IPv6 to the published port
   /// 127.0.0.1:9200 (docker-proxy), fd 5 a file, fd 6 a listening socket; and one socket of
   /// another process to 127.0.0.1:9200 that must not count.
   /// </summary>
   public void AddSockets()
   {
      string fd = $"/proc/{ProcessId}/fd";
      foreach( (string n, string link) in new[] { ( "3", "socket:[111]" ), ( "8", "socket:[112]" ), ( "4", "socket:[222]" ), ( "5", "/dev/null" ), ( "6", "socket:[333]" ), ( "7", "socket:[555]" ) } )
      {
         Set( $"{fd}/{n}", string.Empty );
         _links[$"{fd}/{n}"] = link;
      }

      const string HEAD = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n";
      Set( $"/proc/{ProcessId}/net/tcp", HEAD
         + "   0: 0100007F:D431 020016AC:23F0 01 00000000:00000000 00:00000000 00000000  1000        0 111 1 0 20 4 30 10 -1\n"
         + "   1: 0100007F:D432 020016AC:23F0 01 00000000:00000000 00:00000000 00000000  1000        0 112 1 0 20 4 30 10 -1\n"
         + "   2: 0100007F:D433 0100007F:0599 01 00000000:00000000 00:00000000 00000000  1000        0 222 1 0 20 4 30 10 -1\n"
         + "   3: 00000000:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 333 1 0 20 4 30 10 -1\n"
         + "   4: 0100007F:D434 0100007F:23F0 01 00000000:00000000 00:00000000 00000000  1000        0 999 1 0 20 4 30 10 -1\n" );
      Set( $"/proc/{ProcessId}/net/tcp6", HEAD
         + "   0: 0000000000000000FFFF00000100007F:D435 0000000000000000FFFF00000100007F:23F0 01 00000000:00000000 00:00000000 00000000  1000        0 555 1 0 20 4 30 10 -1\n" );
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
   /// <param name="busyTicks">user + nice + system ticks.</param>
   /// <param name="selfTicks">This process's own (utime) ticks.</param>
   /// <param name="groupUsec">qdrant.service usage_usec.</param>
   /// <param name="softirqTicks">softirq ticks (the kernel keeps them out of user + nice + system).</param>
   /// <param name="irqTicks">hardirq ticks.</param>
   /// <param name="stealTicks">steal ticks.</param>
   /// <param name="childTicks">Ticks of this process's finished children (cutime).</param>
   public void SetCounters( long busyTicks, long selfTicks, long groupUsec, long softirqTicks = 0, long irqTicks = 0, long stealTicks = 0, long childTicks = 0 )
   {
      _counters = string.Create( CultureInfo.InvariantCulture, $"cpu  {busyTicks} 0 0 99999 0 {irqTicks} {softirqTicks} {stealTicks} 0 0\ncpu0 0 0 0 0\n" );
      Set( "/proc/stat", _counters );
      Set( $"/proc/{ProcessId}/stat", Stat( ProcessId, "dotnet", 4242, selfTicks, 0, childTicks ) );
      Set( "/sys/fs/cgroup/system.slice/qdrant.service/cpu.stat", $"usage_usec {groupUsec}\nuser_usec 0\n" );
      if( !StaticCounters )
      {
         lock( _lock )
         {
            _files["/proc/stat"] = () => string.Create( CultureInfo.InvariantCulture, $"cpu  {(long)( _busyBase + ( DateTime.UtcNow - _lastChange ).TotalSeconds * 100 * _outsideCpus + OwnTicks() + Drift() )} 0 0 99999 0 0 0 0 0 0\n" );
            _files[$"/proc/{ProcessId}/stat"] = () => Stat( ProcessId, "dotnet", 4242, (long)OwnTicks(), 0, 0 );
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
   /// Where a fake link points.
   /// </summary>
   /// <param name="path">Path.</param>
   /// <returns>The target, or null.</returns>
   public string? ReadLink( string path )
   {
      lock( _lock )
      {
         return _links.TryGetValue( path, out string? target ) ? target : null;
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
   /// Ticks of this process's own work so far (see <see cref="OwnCpus"/>).
   /// </summary>
   /// <returns>Ticks.</returns>
   private double OwnTicks()
   {
      return _ownBase + ( DateTime.UtcNow - _ownChange ).TotalSeconds * 100 * _ownCpus;
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
   /// <param name="systemTicks">System time (field 15).</param>
   /// <param name="childTicks">Finished children's user time (field 16).</param>
   /// <returns>The line.</returns>
   private static string Stat( int pid, string name, long startTicks, long userTicks, long systemTicks = 0, long childTicks = 0 )
   {
      string[] after = Enumerable.Repeat( "0", 30 ).ToArray();
      after[0] = "S";
      after[11] = userTicks.ToString( CultureInfo.InvariantCulture );
      after[12] = systemTicks.ToString( CultureInfo.InvariantCulture );
      after[13] = childTicks.ToString( CultureInfo.InvariantCulture );
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
/// <param name="PinnedStartCpusets">Cpusets after of the pinned start's two containers (one on the recorded CPUs, one changed by someone else).</param>
public sealed record RestoreResult( string[] Calls, bool FileLeft, string? FileText, string? Error, string[] Governors, string ContainerCpuset, string SqlType, string QdrantThreads, string PinnedStartCpusets );

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

/// <summary>What <see cref="MachineControlScenarios.RecheckAsync"/> saw.</summary>
/// <param name="Passes">The pass as "pass busy waited".</param>
/// <param name="Seconds">Wall seconds from the first warm-up line to the timing line.</param>
/// <param name="Log">The log.</param>
public sealed record RecheckResult( string[] Passes, double Seconds, string[] Log );

/// <summary>Start-up exclusion and pinned start results.</summary>
/// <param name="Passes">"target busy waited load" per pass.</param>
/// <param name="StateDuringUp">The state file's pinned starts while compose was starting the engine.</param>
/// <param name="Calls">Commands run.</param>
/// <param name="Method">How the started engine was pinned.</param>
/// <param name="Changes">What was changed on it.</param>
/// <param name="Restored">What was put back.</param>
/// <param name="FileAfter">The state file is left after the run.</param>
/// <param name="Log">Progress output.</param>
public sealed record StartupResult( string[] Passes, string StateDuringUp, string[] Calls, string Method, string[] Changes, string[] Restored, bool FileAfter, string[] Log );

/// <summary>Idle settings results.</summary>
/// <param name="Start">Settings at the start, as text.</param>
/// <param name="End">Settings at the end, as text.</param>
/// <param name="ConditionsJson">The conditions as JSON.</param>
/// <param name="Calls">Commands run.</param>
/// <param name="RunNotes">Run-level notes and flags.</param>
public sealed record IdleResult( string Start, string End, string ConditionsJson, string[] Calls, string[] RunNotes );

/// <summary>Connection record results.</summary>
/// <param name="Decoded">Decoded /proc/net/tcp addresses.</param>
/// <param name="Peers">This process's established peers with socket counts.</param>
/// <param name="Notes">Per target, its connection note.</param>
/// <param name="Json">The records as JSON.</param>
/// <param name="Flags">Machine flags of conditions holding the records.</param>
/// <param name="SqlWithPort">SqlEndpoint of "tcp:db1,1500".</param>
/// <param name="SqlNamed">SqlEndpoint of a named instance.</param>
public sealed record ConnectionResult( string[] Decoded, string[] Peers, string[] Notes, string Json, string[] Flags, string SqlWithPort, string SqlNamed );

/// <summary>
/// A fake compose host that, when asked to start the engine, records what the state file holds,
/// burns 3 CPUs of (not yet attributable) start-up work for 600 ms, and then creates the
/// container on the CPU set it was given.
/// </summary>
public sealed class StartingHost : IEngineHost
{
   #region Data Members

   /// <summary>The compose file it starts.</summary>
   public const string COMPOSE = "/x/es.compose.yaml";

   private readonly FakeMachine _machine;
   private readonly string _stateFile;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the host.
   /// </summary>
   /// <param name="machine">The fake machine.</param>
   /// <param name="stateFile">The run's state file.</param>
   public StartingHost( FakeMachine machine, string stateFile )
   {
      _machine = machine;
      _stateFile = stateFile;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The pinned starts the state file held when the start began.</summary>
   public string? StateDuringUp { get; private set; }

   /// <summary>
   /// Running when the container exists.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when running.</returns>
   public Task<bool> IsRunningAsync( string composePath, CancellationToken ct )
   {
      return Task.FromResult( _machine.ComposeRunning.ContainsKey( composePath ) );
   }

   /// <summary>
   /// Starts the fake engine as described on the class.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="cpuset">CPU set.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The read-back line a host that applies the CPU set gives, or null without one.</returns>
   public async Task<string?> UpAsync( string composePath, string? cpuset, CancellationToken ct )
   {
      MachineState? state = new MachineStateStore( _stateFile ).Load();
      StateDuringUp = state == null ? "no file" : string.Join( ";", state.PinnedStarts.Select( p => $"{p.ComposePath} {p.Cpuset} {p.Target}" ) );
      _machine.OutsideCpus = 3;
      await Task.Delay( 600, ct );
      _machine.OutsideCpus = 0;
      _machine.Containers["e5e5"] = new FakeContainer( "gvb-elasticsearch", cpuset ?? string.Empty, 950, true );
      _machine.AddProcess( 950, "java", 7000, 4, cpuset ?? "0-7", "/system.slice/docker-e5e5.scope" );
      _machine.ComposeRunning[composePath] = new[] { "e5e5" };
      return cpuset == null ? null : $"gvb-elasticsearch on CPUs {cpuset}";
   }

   /// <summary>
   /// Not used.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Null.</returns>
   public Task<string?> DownAsync( string composePath, CancellationToken ct )
   {
      return Task.FromResult<string?>( null );
   }

   #endregion Public Methods
}

/// <summary>
/// A sink that only has a name; its calls are never made in these scenarios.
/// </summary>
public sealed class NullSink : ISink
{
   #region Constructor

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="name">Name.</param>
   public NullSink( string name )
   {
      Name = name;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Sink name.</summary>
   public string Name { get; }

   /// <summary>Not used.</summary>
   /// <param name="collection">Collection.</param>
   /// <param name="dimension">Dimension.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True.</returns>
   public Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct ) => Task.FromResult( true );

   /// <summary>Not used.</summary>
   /// <param name="collection">Collection.</param>
   /// <param name="records">Records.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A completed task.</returns>
   public Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct ) => Task.CompletedTask;

   /// <summary>Not used.</summary>
   /// <param name="collection">Collection.</param>
   /// <param name="chunkIds">Ids.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A completed task.</returns>
   public Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct ) => Task.CompletedTask;

   /// <summary>Not used.</summary>
   /// <param name="collection">Collection.</param>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>No hits.</returns>
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct ) => Task.FromResult<IReadOnlyList<SearchHit>>( Array.Empty<SearchHit>() );

   /// <summary>Not used.</summary>
   /// <param name="collection">Collection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A completed task.</returns>
   public Task DropCollectionAsync( string collection, CancellationToken ct ) => Task.CompletedTask;

   #endregion Public Methods
}

/// <summary>
/// A docker inspect that knows one running container, gvb-elasticsearch at 172.22.0.2 on
/// network engines_default, publishing 9200 on 127.0.0.1:9200.
/// </summary>
public sealed class JsonInspector : IContainerInspector
{
   #region Public Methods

   /// <summary>
   /// The inspect text of the known container, or null.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The JSON, or null.</returns>
   public Task<string?> InspectAsync( string container, CancellationToken ct )
   {
      const string JSON = "[{\"Id\":\"e5e5e5e5e5e5e5e5\",\"Name\":\"/gvb-elasticsearch\",\"State\":{\"Status\":\"running\",\"Running\":true},"
         + "\"NetworkSettings\":{\"Networks\":{\"engines_default\":{\"IPAddress\":\"172.22.0.2\"}},\"Ports\":{\"9200/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"9200\"}]}}}]";
      return Task.FromResult( container == "gvb-elasticsearch" ? JSON : null );
   }

   #endregion Public Methods
}

/// <summary>Sampler arithmetic.</summary>
/// <param name="Outside">Outside CPUs.</param>
/// <param name="Cpus">"cpuN min/median/max".</param>
/// <param name="Samples">Samples in the window.</param>
/// <param name="ShortSamples">Samples in a 20 ms window.</param>
/// <param name="Nearest">The nearest sample stood in for the short window.</param>
/// <param name="Error">Sampling error.</param>
public sealed record SamplerResult( double? Outside, string[] Cpus, int Samples, int ShortSamples, bool Nearest, string? Error );

/// <summary>Live busy-box results.</summary>
/// <param name="Background">Busy CPUs of the box before the test, over 10 s.</param>
/// <param name="Limit">The limit used.</param>
/// <param name="Passes">Each pass's quiet check, wait and flag.</param>
/// <param name="Log">Progress output with times.</param>
/// <param name="GovernorsBefore">Governors before.</param>
/// <param name="GovernorsAfter">Governors after.</param>
/// <param name="StateFileLeft">The state file is left.</param>
public sealed record LiveGateResult( double Background, double Limit, string[] Passes, string[] Log, string[] GovernorsBefore, string[] GovernorsAfter, bool StateFileLeft );

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
