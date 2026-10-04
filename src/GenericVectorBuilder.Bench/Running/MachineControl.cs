using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Settings of machine control. The defaults are the run's; tests shorten the waits.
/// </summary>
public sealed class MachineControlOptions
{
   #region Public Methods

   /// <summary>False to change nothing on the machine and only record what it was.</summary>
   public bool Enabled { get; init; } = true;

   /// <summary>Why machine control is off (for the results), when it is.</summary>
   public string DisabledReason { get; init; } = "--no-machine-control";

   /// <summary>State file.</summary>
   public string StateFile { get; init; } = MachineStateStore.DEFAULT_PATH;

   /// <summary>CPUs of outside work above which the box counts as busy.</summary>
   public double BusyThreshold { get; init; } = 1.5;

   /// <summary>Window of the outside-load mean.</summary>
   public TimeSpan BusyWindow { get; init; } = TimeSpan.FromMinutes( 1 );

   /// <summary>Longest a timed pass waits for a busy box to clear.</summary>
   public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds( 60 );

   /// <summary>How often the waiting pass looks again.</summary>
   public TimeSpan BusyPoll { get; init; } = TimeSpan.FromSeconds( 1 );

   /// <summary>Time between clock samples.</summary>
   public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds( 250 );

   /// <summary>False to leave this process's affinity alone (tests that run inside the test host).</summary>
   public bool PinClient { get; init; } = true;

   #endregion Public Methods
}

/// <summary>
/// Puts the machine into a known state for a timed run and back afterwards: every CPU on the
/// "performance" governor, the engine under test on two physical cores and the client on the
/// other two, and no timed pass started while other work keeps the box busy. It records the
/// governor, the split, each engine's pinning and every CPU's clock per pass in results.json.
/// Every change is written to a state file before it is made and put back in a finally block;
/// a run that dies without its finally leaves the file, and the next start (or the
/// restore-machine command) puts the machine back before changing anything.
/// Why it watches the progress lines: the timed passes run inside the search runner, which
/// announces each with "NAME: timing PASS" and reports each result in a line; that line is the
/// hook where a pass can be held for a quiet box and its clock window opened, without the
/// runner knowing about machine control. If a searched target shows no pass, the results say so.
/// </summary>
public sealed class MachineControl : IDisposable
{
   #region Data Members

   private static readonly Regex TIMING = new( @"^\s*(?<target>[^\s:]+): timing (?<pass>\S+)\s*$", RegexOptions.Compiled );
   private static readonly Regex WARMUP = new( @"^\s*(?<target>[^\s:]+): warm-up before (?<pass>\S+)", RegexOptions.Compiled );
   private static readonly Regex RESULT = new( @"^\s*(?<target>[^\s:]+): (?:p50 |exact mode p50 |[0-9.,]+ QPS at concurrency )", RegexOptions.Compiled );
   private static readonly TimeSpan LSCPU_TIMEOUT = TimeSpan.FromSeconds( 30 );
   private const double DEFAULT_CLOCK_TICKS = 100;

   private readonly MachineControlOptions _options;
   private readonly IMachineSystem _system;
   private readonly Action<string> _log;
   private readonly MachineStateKeeper? _keeper;
   private readonly CpuPartition? _partition;
   private readonly EnginePinner? _pinner;
   private readonly object _passLock = new();
   private MachineSampler? _sampler;
   private OpenPass? _open;
   private TargetPinning? _current;
   private string? _clientPrevious;
   private CancellationToken _ct;
   private bool _restored;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the control; <see cref="StartAsync"/> is the way in.
   /// </summary>
   /// <param name="conditions">The record to fill.</param>
   /// <param name="options">Settings.</param>
   /// <param name="system">The machine.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="keeper">The run's state, or null when machine control is off.</param>
   /// <param name="partition">The CPU split, or null when off.</param>
   private MachineControl( MachineConditions conditions, MachineControlOptions options, IMachineSystem system, Action<string> log, MachineStateKeeper? keeper, CpuPartition? partition )
   {
      Conditions = conditions;
      _options = options;
      _system = system;
      _log = log;
      _keeper = keeper;
      _partition = partition;
      _pinner = keeper != null && partition is { IsSplit: true } ? new EnginePinner( system, keeper, partition ) : null;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>What the run was measured under; written into results.json.</summary>
   public MachineConditions Conditions { get; }

   /// <summary>True when this run controls the machine.</summary>
   public bool IsOn => _keeper != null;

   /// <summary>
   /// Starts machine control: puts back anything an earlier run left changed, splits the CPUs,
   /// records and sets the governor, and starts sampling. When off, only reads the governor.
   /// </summary>
   /// <param name="conditions">The record to fill (build and settings already in it).</param>
   /// <param name="options">Settings.</param>
   /// <param name="system">The machine.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation; also ends a wait for a quiet box.</param>
   /// <returns>The control; dispose it (or call <see cref="RestoreAsync"/>) to put the machine back.</returns>
   /// <exception cref="InvalidOperationException">Another run controls the machine, an earlier run's changes cannot be put back, or the governor cannot be set.</exception>
   public static async Task<MachineControl> StartAsync( MachineConditions conditions, MachineControlOptions options, IMachineSystem system, Action<string> log, CancellationToken ct )
   {
      conditions.StateFile = Path.GetFullPath( options.StateFile );
      if( !options.Enabled )
      {
         conditions.MachineControl = "off (" + options.DisabledReason + ")";
         conditions.Governor = GovernorControl.Summarize( GovernorControl.Read( system, AllCpus( system ) ) );
         return new MachineControl( conditions, options, system, log, null, null ) { _ct = ct };
      }

      var store = new MachineStateStore( options.StateFile );
      await RestoreStaleAsync( store, system, log, ct );
      ShellResult lscpu = await system.RunAsync( "lscpu", new[] { "-e=CPU,SOCKET,CORE,ONLINE" }, LSCPU_TIMEOUT, ct );
      ProcessInfo.Require( lscpu, "read the CPU layout with lscpu" );
      CpuPartition partition = CpuPartition.Choose( CpuPartition.ParseLscpu( lscpu.Output ) );
      var keeper = new MachineStateKeeper( store, MachineStateKeeper.NewState( system ) );
      keeper.Record( _ => { } );
      var control = new MachineControl( conditions, options, system, log, keeper, partition ) { _ct = ct };
      try
      {
         await control.TakeOverAsync( ct );
      }
      catch
      {
         control.Dispose();
         throw;
      }

      return control;
   }

   /// <summary>
   /// Puts back everything a run that did not finish left changed, from its state file. Used at
   /// start and by the restore-machine command.
   /// </summary>
   /// <param name="store">The state file.</param>
   /// <param name="system">The machine.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when there was something to put back.</returns>
   /// <exception cref="InvalidOperationException">The owning run is still alive, or something could not be put back (the file is kept).</exception>
   public static async Task<bool> RestoreStaleAsync( MachineStateStore store, IMachineSystem system, Action<string> log, CancellationToken ct )
   {
      MachineState? stale = store.Load();
      if( stale == null )
      {
         return false;
      }

      if( stale.OwnerPid != system.ProcessId && ProcessInfo.IsSameProcess( system, stale.OwnerPid, stale.OwnerStartTicks ) )
      {
         throw new InvalidOperationException( $"Another benchmark run (pid {stale.OwnerPid}, started {stale.StartedUtc}) is controlling this machine; its state file is {store.Path}. "
            + "Wait for it to end, or stop it with Ctrl+C so it puts the machine back. Do not delete the state file while it runs." );
      }

      log( $"Machine: a run that did not finish (pid {stale.OwnerPid}, started {stale.StartedUtc}) left changes; putting back {stale.Describe()} from {store.Path}" );
      var keeper = new MachineStateKeeper( store, stale );
      List<string> problems = await MachineRestorer.RestoreAllAsync( system, keeper, log, ct );
      keeper.Finish();
      if( problems.Count > 0 )
      {
         throw new InvalidOperationException( $"Could not put the machine back from {store.Path}: {string.Join( "; ", problems )}. "
            + "This run changed nothing. Fix the cause, then run 'GenericVectorBuilder.Bench restore-machine'." );
      }

      log( "Machine: the earlier run's changes are put back and its state file removed." );
      return true;
   }

   /// <summary>
   /// Wraps the engine host so a compose engine run-all starts is pinned right away.
   /// </summary>
   /// <param name="inner">The real host.</param>
   /// <returns>The host to use.</returns>
   public IEngineHost WrapHost( IEngineHost inner )
   {
      return _pinner == null ? inner : new PinningEngineHost( inner, AfterEngineStartedAsync );
   }

   /// <summary>
   /// Wraps the progress output so timed passes are seen (and held for a quiet box) as they start.
   /// </summary>
   /// <param name="log">The real output.</param>
   /// <returns>The output to give the runners.</returns>
   public Action<string> WrapLog( Action<string> log )
   {
      return !IsOn ? log : line =>
      {
         ObserveLog( line );
         log( line );
      };
   }

   /// <summary>
   /// Pins this process (the client, and embedded engines in it) to the client CPUs.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   public async Task PinClientAsync( CancellationToken ct )
   {
      if( _pinner == null || !_options.PinClient )
      {
         return;
      }

      ( _clientPrevious, string verified ) = await _pinner.PinClientAsync( ct );
      Conditions.ClientPinning = verified;
      _log( $"  machine: client pinned to CPUs {Conditions.ClientCpus} ({verified})" );
   }

   /// <summary>
   /// Pins a target's engine before it is measured. Problems become warnings on the target.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="hosting">compose, always-on or embedded.</param>
   /// <param name="composePath">Compose file or null.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task EnterTargetAsync( string name, string hosting, string? composePath, CancellationToken ct )
   {
      if( !IsOn )
      {
         return;
      }

      _current = _pinner != null
         ? await _pinner.PinTargetAsync( name, hosting, composePath, ct )
         : new TargetPinning { Target = name, Hosting = hosting, Method = "none: the CPUs were not split" };
      _sampler?.SetEngineGroups( _pinner?.EngineGroups ?? new List<string>() );
      lock( _passLock )
      {
         Conditions.Engines.Add( _current );
      }

      _log( $"  machine: {name}: {_current.Method}{( _current.Changes.Count > 0 ? "; " + string.Join( "; ", _current.Changes ) : string.Empty )}{( _current.Problems.Count > 0 ? "; PROBLEM: " + string.Join( "; ", _current.Problems ) : string.Empty )}" );
   }

   /// <summary>
   /// Puts back a target's pins after it was measured (failures stay in the state file for the
   /// end of the run to try again).
   /// </summary>
   /// <param name="name">Target name.</param>
   public async Task LeaveTargetAsync( string name )
   {
      ClosePass( DateTime.UtcNow, "end of the target" );
      if( _keeper == null )
      {
         return;
      }

      TargetPinning? pinning = _current;
      List<string> problems = await MachineRestorer.RestorePinsAsync( _system, _keeper, name, line => Restored( line, pinning ), CancellationToken.None );
      pinning?.Problems.AddRange( problems.Select( p => "could not put back yet (retried at the end): " + p ) );
      _sampler?.SetEngineGroups( Array.Empty<string>() );
      _current = null;
   }

   /// <summary>
   /// Reads one progress line: "NAME: timing PASS" opens a pass (after waiting for a quiet
   /// box), a result line of the open pass moves its end, and the next warm-up or target
   /// closes it.
   /// </summary>
   /// <param name="line">The progress line.</param>
   public void ObserveLog( string line )
   {
      if( !IsOn )
      {
         return;
      }

      DateTime now = DateTime.UtcNow;
      Match timing = TIMING.Match( line );
      if( timing.Success )
      {
         ClosePass( now, "the next pass" );
         PassGate gate = WaitForQuietBox( timing.Groups["target"].Value, timing.Groups["pass"].Value );
         lock( _passLock )
         {
            _open = new OpenPass( timing.Groups["target"].Value, timing.Groups["pass"].Value, DateTime.UtcNow, gate );
         }

         return;
      }

      Match result = RESULT.Match( line );
      lock( _passLock )
      {
         if( result.Success && _open != null && _open.Target == result.Groups["target"].Value )
         {
            _open.LastResult = now;
            return;
         }
      }

      if( WARMUP.IsMatch( line ) || line.StartsWith( "== ", StringComparison.Ordinal ) )
      {
         ClosePass( now, "the next warm-up" );
      }
   }

   /// <summary>
   /// Adds the run-level machine notes and flags to the report (once, at the end).
   /// </summary>
   /// <param name="report">The run.</param>
   public void Annotate( BenchReport report )
   {
      report.Notes.Add( SummaryNote() );
      Conditions.Flags = MachineFlags.Compute( Conditions, report.Targets.Where( t => t.PassOrder is { Count: > 0 } ).Select( t => t.Name ) );
      report.Notes.AddRange( Conditions.Flags );
   }

   /// <summary>
   /// Adds a target's pinning, clock per pass and busy-box warnings to its notes.
   /// </summary>
   /// <param name="result">The target's results.</param>
   public void AnnotateTarget( TargetReport result )
   {
      List<PassConditions> passes;
      TargetPinning? pinning;
      lock( _passLock )
      {
         passes = Conditions.Passes.Where( p => p.Target == result.Name ).ToList();
         pinning = Conditions.Engines.LastOrDefault( e => e.Target == result.Name );
      }

      if( pinning != null )
      {
         result.Notes.Add( $"CPU pinning: {pinning.Method}{( pinning.Cpus != null ? $", CPUs {pinning.Cpus}" : string.Empty )}."
            + $"{( pinning.Changes.Count > 0 ? " Changed: " + string.Join( "; ", pinning.Changes ) + "." : string.Empty )}"
            + $"{( pinning.Verified.Count > 0 ? " Read back: " + string.Join( "; ", pinning.Verified ) + "." : string.Empty )}" );
         result.Notes.AddRange( pinning.Problems.Select( p => $"WARNING: CPU pinning problem: {p}" ) );
      }

      if( MachineFlags.ClockLine( passes ) is string clock )
      {
         result.Notes.Add( clock );
      }

      result.Notes.AddRange( passes.Where( p => p.BusyBox ).Select( p => $"WARNING: busy box during {p.Pass}: {p.BusyReason}." ) );
   }

   /// <summary>
   /// Puts the machine back: the client's affinity, every pin still recorded, the governors.
   /// Records what was done and what failed; removes the state file only when everything is
   /// back. Safe to call twice.
   /// </summary>
   public async Task RestoreAsync()
   {
      if( _restored )
      {
         return;
      }

      _restored = true;
      ClosePass( DateTime.UtcNow, "the end of the run" );
      _sampler?.Stop();
      Conditions.SamplingError ??= _sampler?.LastError;
      Conditions.LoadAverageEnd = MachineFacts.ReadLoadAverage();
      if( _keeper == null )
      {
         return;
      }

      Conditions.GovernorAtEnd = GovernorControl.Summarize( GovernorControl.Read( _system, _partition!.OnlineCpus ) );
      await RestoreClientAsync();
      Conditions.RestoreProblems.AddRange( await MachineRestorer.RestoreAllAsync( _system, _keeper, line => Restored( line, null ), CancellationToken.None ) );
      Conditions.GovernorAfterRestore = GovernorControl.Summarize( GovernorControl.Read( _system, _partition.OnlineCpus ) );
      bool removed = _keeper.Finish();
      _log( removed
         ? $"Machine: put back (governor now {Conditions.GovernorAfterRestore}); state file removed."
         : $"WARNING: the machine is NOT fully put back: {string.Join( "; ", Conditions.RestoreProblems )}. State kept in {_keeper.Store.Path}; fix the cause, then run 'GenericVectorBuilder.Bench restore-machine'." );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Sets the governor (recorded first), fills the CPU facts and starts sampling.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   private async Task TakeOverAsync( CancellationToken ct )
   {
      CpuPartition partition = _partition!;
      Conditions.MachineControl = "on";
      Conditions.ThreadSiblings = partition.ThreadSiblings.ToList();
      Conditions.PartitionProblem = partition.Problem;
      Conditions.ClientCpus = partition.IsSplit ? CpuList.Format( partition.ClientCpus ) : null;
      Conditions.EngineCpus = partition.IsSplit ? CpuList.Format( partition.EngineCpus ) : null;
      Conditions.BusyRule = string.Create( CultureInfo.InvariantCulture, $"a timed pass waits up to {_options.BusyWait.TotalSeconds:0} s while processes outside the benchmark (everything but this client and the engine under test) use more than {_options.BusyThreshold:0.0#} CPUs on average over the last {_options.BusyWindow.TotalSeconds:0} s; if it does not clear the pass runs anyway, flagged 'busy box', as is a pass whose own outside load is above the limit" );
      Dictionary<int, string?> before = await GovernorControl.ApplyAsync( _system, _keeper!, partition.OnlineCpus, ct );
      Conditions.GovernorBefore = GovernorControl.Summarize( before );
      Conditions.Governor = GovernorControl.Summarize( GovernorControl.Read( _system, partition.OnlineCpus ) );
      _sampler = new MachineSampler( _system, partition.OnlineCpus, await ClockTicksAsync( ct ), _options.SampleInterval );
      _sampler.Start();
      _log( $"Machine: governor {Conditions.Governor} on CPUs {CpuList.Format( partition.OnlineCpus )} (was {Conditions.GovernorBefore}); {partition.Describe()}; changes recorded in {_keeper!.Store.Path} before they are made." );
   }

   /// <summary>
   /// Pins the containers run-all just started, for the target being measured.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task AfterEngineStartedAsync( string composePath, CancellationToken ct )
   {
      if( _pinner == null || _current is not { Hosting: "compose" } pinning )
      {
         return;
      }

      try
      {
         await _pinner.PinContainersAsync( pinning, composePath, ct );
         _sampler?.SetEngineGroups( _pinner.EngineGroups );
         _log( $"  machine: {pinning.Target} started and pinned: {string.Join( "; ", pinning.Changes )}" );
      }
      catch( Exception ex ) when( ex is InvalidOperationException or TimeoutException or IOException or FormatException )
      {
         pinning.Problems.Add( ex.Message );
         _log( $"  machine: {pinning.Target} could not be pinned after its start: {ex.Message}" );
      }
   }

   /// <summary>
   /// Holds a pass while the box is busy, up to the wait limit. Runs inside the progress
   /// callback, so it blocks the runner's thread on purpose: the pass must not start. A pass
   /// announced before the sampler has 3 s of history (the first pass of a quick run) first
   /// waits for that history, within the same limit, so it is never let through unchecked.
   /// </summary>
   /// <param name="target">Target name.</param>
   /// <param name="pass">Pass name.</param>
   /// <returns>The load seen, the wait, and whether the box stayed busy.</returns>
   private PassGate WaitForQuietBox( string target, string pass )
   {
      var waited = Stopwatch.StartNew();
      double? load = _sampler?.OutsideLoad( DateTime.UtcNow, _options.BusyWindow );
      while( load == null && _sampler != null && waited.Elapsed < _options.BusyWait && !_ct.WaitHandle.WaitOne( _options.BusyPoll ) )
      {
         load = _sampler.OutsideLoad( DateTime.UtcNow, _options.BusyWindow );
      }

      if( load is not double busy || busy <= _options.BusyThreshold )
      {
         return new PassGate( load, waited.Elapsed.TotalSeconds, null );
      }

      _log( string.Create( CultureInfo.InvariantCulture, $"  machine: box busy before {target} {pass}: processes outside the benchmark use {busy:0.00} CPUs (mean over {_options.BusyWindow.TotalSeconds:0} s, limit {_options.BusyThreshold:0.0#}); waiting up to {_options.BusyWait.TotalSeconds:0} s" ) );
      while( waited.Elapsed < _options.BusyWait && !_ct.WaitHandle.WaitOne( _options.BusyPoll ) )
      {
         load = _sampler!.OutsideLoad( DateTime.UtcNow, _options.BusyWindow );
         if( load is not double still || still <= _options.BusyThreshold )
         {
            _log( string.Create( CultureInfo.InvariantCulture, $"  machine: box quiet after {waited.Elapsed.TotalSeconds:0} s (outside load {MachineFlags.Load( load )}); timing {pass}" ) );
            return new PassGate( load, waited.Elapsed.TotalSeconds, null );
         }
      }

      string reason = string.Create( CultureInfo.InvariantCulture, $"processes outside the benchmark still used {MachineFlags.Load( load )} CPUs (mean over {_options.BusyWindow.TotalSeconds:0} s) after waiting {waited.Elapsed.TotalSeconds:0} s, above the limit of {_options.BusyThreshold:0.0#}; timed anyway" );
      _log( $"  machine: BUSY BOX: {reason}" );
      return new PassGate( load, waited.Elapsed.TotalSeconds, reason );
   }

   /// <summary>
   /// Closes the open pass, if any, and records its clock and load.
   /// </summary>
   /// <param name="now">Now (UTC).</param>
   /// <param name="closedBy">What closed it, for a pass that showed no result line.</param>
   private void ClosePass( DateTime now, string closedBy )
   {
      OpenPass? open;
      lock( _passLock )
      {
         open = _open;
         _open = null;
      }

      if( open == null || _sampler == null )
      {
         return;
      }

      DateTime end = open.LastResult ?? now;
      ( List<CpuMhz> cpus, int samples, bool nearest ) = _sampler.Frequencies( open.Start, end );
      double? during = _sampler.OutsideLoadBetween( open.Start, end );
      bool busyDuring = during > _options.BusyThreshold;
      var pass = new PassConditions
      {
         Target = open.Target,
         Pass = open.Pass,
         StartUtc = open.Start.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture ),
         EndUtc = end.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture ),
         Seconds = Math.Round( ( end - open.Start ).TotalSeconds, 3 ),
         EndedBy = open.LastResult != null ? "result line" : closedBy + " (no result line seen)",
         Governor = GovernorControl.Summarize( GovernorControl.Read( _system, _sampler.Cpus ) ),
         FrequencySamples = samples,
         NearestSample = nearest,
         EngineMhzMedian = MedianOf( cpus, _partition?.EngineCpus ),
         ClientMhzMedian = MedianOf( cpus, _partition?.ClientCpus ),
         CpuMhz = cpus,
         OutsideLoadAtStart = open.Gate.Load.HasValue ? Math.Round( open.Gate.Load.Value, 3 ) : null,
         OutsideLoadDuring = during.HasValue ? Math.Round( during.Value, 3 ) : null,
         WaitedSeconds = Math.Round( open.Gate.WaitedSeconds, 1 ),
         BusyBox = open.Gate.BusyReason != null || busyDuring,
         BusyReason = open.Gate.BusyReason ?? ( busyDuring ? string.Create( CultureInfo.InvariantCulture, $"processes outside the benchmark used {during:0.00} CPUs on average during the pass, above the limit of {_options.BusyThreshold:0.0#}" ) : null ),
      };
      lock( _passLock )
      {
         Conditions.Passes.Add( pass );
      }
   }

   /// <summary>
   /// The median of the per-CPU medians of a set of CPUs.
   /// </summary>
   /// <param name="cpus">Per CPU figures.</param>
   /// <param name="set">The CPUs to take, or null.</param>
   /// <returns>MHz, or null when none of the CPUs was sampled.</returns>
   private static int? MedianOf( List<CpuMhz> cpus, IReadOnlyList<int>? set )
   {
      List<int> medians = cpus.Where( c => set != null && set.Contains( c.Cpu ) ).Select( c => c.Median ).OrderBy( m => m ).ToList();
      return medians.Count == 0 ? null : MachineSampler.Median( medians );
   }

   /// <summary>
   /// Puts this process's affinity back, if it was changed.
   /// </summary>
   private async Task RestoreClientAsync()
   {
      if( _pinner == null || _clientPrevious == null )
      {
         return;
      }

      try
      {
         await _pinner.RestoreClientAsync( _clientPrevious, CancellationToken.None );
         Conditions.Restored.Add( $"client affinity back to {_clientPrevious}" );
      }
      catch( Exception ex ) when( ex is InvalidOperationException or TimeoutException )
      {
         Conditions.RestoreProblems.Add( $"client affinity back to {_clientPrevious}: {ex.Message}" );
      }
   }

   /// <summary>
   /// Logs and records one thing put back.
   /// </summary>
   /// <param name="line">What was put back.</param>
   /// <param name="pinning">The target it belonged to, or null at the end of the run.</param>
   private void Restored( string line, TargetPinning? pinning )
   {
      _log( line );
      lock( _passLock )
      {
         ( pinning?.Restored ?? Conditions.Restored ).Add( line.Trim().Replace( "machine: ", string.Empty, StringComparison.Ordinal ) );
      }
   }

   /// <summary>
   /// USER_HZ from getconf; 100 (the Linux value) when getconf fails, noted as a sampling error.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Clock ticks per second.</returns>
   private async Task<double> ClockTicksAsync( CancellationToken ct )
   {
      ShellResult result = await _system.RunAsync( "getconf", new[] { "CLK_TCK" }, LSCPU_TIMEOUT, ct );
      if( result.ExitCode == 0 && double.TryParse( result.Output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ticks ) && ticks > 0 )
      {
         return ticks;
      }

      Conditions.SamplingError = $"getconf CLK_TCK failed (exit {result.ExitCode}); assumed {DEFAULT_CLOCK_TICKS} ticks per second";
      return DEFAULT_CLOCK_TICKS;
   }

   /// <summary>
   /// The run-level summary note.
   /// </summary>
   /// <returns>The note.</returns>
   private string SummaryNote()
   {
      MachineConditions c = Conditions;
      string build = $"Client build {c.BuildConfiguration}, {c.DotNet}{( c.ServerGc ? ", server GC" : string.Empty )}.";
      if( !IsOn )
      {
         return $"Machine control {c.MachineControl}: governor {c.Governor}, nothing pinned, passes not held for a quiet box. {build}";
      }

      string split = _partition is { IsSplit: true } ? _partition.Describe() : $"CPUs not split ({c.PartitionProblem})";
      return $"Machine control on: governor {c.Governor} on every CPU during the run (before: {c.GovernorBefore}; at the end: {c.GovernorAtEnd ?? "not read"}; after putting it back: {c.GovernorAfterRestore ?? "not read"}). "
         + $"{split}; the client process{( c.ClientPinning != null ? " was pinned" : " was NOT pinned" )}; each engine was pinned to the engine CPUs for its turn and put back after (conditions.engines). "
         + $"Busy box: {c.BusyRule}. CPU clocks were sampled every {_options.SampleInterval.TotalMilliseconds:0} ms; each pass's min/median/max per CPU is in conditions.passes. {build}";
   }

   /// <summary>
   /// Every online CPU, or 0 to N-1 when the kernel's list cannot be read.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <returns>CPU numbers.</returns>
   private static IReadOnlyList<int> AllCpus( IMachineSystem system )
   {
      try
      {
         return CpuList.Parse( MachineRestorer.OnlineCpus( system ) );
      }
      catch( Exception ex ) when( ex is InvalidOperationException or FormatException )
      {
         return Enumerable.Range( 0, Environment.ProcessorCount ).ToList();
      }
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Puts the machine back if <see cref="RestoreAsync"/> has not, and stops sampling.
   /// </summary>
   public void Dispose()
   {
      try
      {
         RestoreAsync().GetAwaiter().GetResult();
      }
      catch( Exception ex ) when( ex is InvalidOperationException or TimeoutException or IOException )
      {
         _log( $"WARNING: putting the machine back failed: {ex.Message}. State kept in {Conditions.StateFile}; run 'GenericVectorBuilder.Bench restore-machine'." );
      }

      _sampler?.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// What the quiet-box check found before a pass.
/// </summary>
/// <param name="Load">CPUs busy outside the benchmark (mean over the window), or null when not known yet.</param>
/// <param name="WaitedSeconds">Seconds the pass was held.</param>
/// <param name="BusyReason">Why the pass ran on a busy box, or null when it did not.</param>
public sealed record PassGate( double? Load, double WaitedSeconds, string? BusyReason );

/// <summary>
/// A timed pass that has started and not yet been recorded.
/// </summary>
public sealed class OpenPass
{
   #region Constructor

   /// <summary>
   /// Opens a pass.
   /// </summary>
   /// <param name="target">Target name.</param>
   /// <param name="pass">Pass name.</param>
   /// <param name="start">Start (UTC, after the quiet-box wait).</param>
   /// <param name="gate">What the quiet-box check found.</param>
   public OpenPass( string target, string pass, DateTime start, PassGate gate )
   {
      Target = target;
      Pass = pass;
      Start = start;
      Gate = gate;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Target name.</summary>
   public string Target { get; }

   /// <summary>Pass name.</summary>
   public string Pass { get; }

   /// <summary>Start (UTC).</summary>
   public DateTime Start { get; }

   /// <summary>What the quiet-box check found.</summary>
   public PassGate Gate { get; }

   /// <summary>Time of the pass's latest result line, or null before the first.</summary>
   public DateTime? LastResult { get; set; }

   #endregion Public Methods
}
