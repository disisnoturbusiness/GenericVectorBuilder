using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Starts and stops compose-hosted engines around their measurement, by one rule: run-all
/// stops only an engine it started itself, and never one that was already running before the
/// run began.
/// Why the "before" state is read once, at the start of the run: an engine someone was using
/// when the run began must be running when it ends, even if it went down in between and
/// run-all had to start it to measure it. Reading the state only at each target's turn could
/// not tell that engine from one run-all brought up.
/// The other commands (replicate, bench) promise not to start anything, so for them a stopped
/// engine is an error with the command that starts it.
/// An engine run-all starts for its measurement (and stops after) is created on the engine CPUs
/// when the run split them, so it sizes its thread pools for the CPUs it will run on. An engine
/// that was running before the run but down at its turn is started unrestricted, because run-all
/// leaves it running afterwards and must leave it as it was; machine control pins it with
/// "docker update" for the measurement and puts that back.
/// </summary>
public sealed class EngineLifecycle
{
   #region Data Members

   private readonly IEngineHost _host;
   private readonly bool _runAll;
   private readonly string? _engineCpus;
   private readonly Action<string> _log;
   private readonly Dictionary<string, bool> _runningBefore = new( StringComparer.Ordinal );
   private readonly HashSet<string> _startedByRun = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the lifecycle for one run.
   /// </summary>
   /// <param name="host">Starts, stops and checks engines.</param>
   /// <param name="runAll">True for run-all, the only command allowed to start engines.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="engineCpus">CPUs an engine this run starts (and stops) is created on, or null to start engines unrestricted (machine control off or CPUs not split).</param>
   public EngineLifecycle( IEngineHost host, bool runAll, Action<string> log, string? engineCpus = null )
   {
      _host = host;
      _runAll = runAll;
      _log = log;
      _engineCpus = string.IsNullOrWhiteSpace( engineCpus ) ? null : engineCpus;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Compose files that were running when the run began, as recorded by <see cref="SnapshotAsync"/>.</summary>
   public IReadOnlyCollection<string> RunningBefore => _runningBefore.Where( p => p.Value ).Select( p => p.Key ).ToList();

   /// <summary>
   /// Records which engines are running before anything is measured. Only run-all needs it.
   /// </summary>
   /// <param name="composePaths">Compose files of every target in the run (nulls are skipped).</param>
   /// <param name="ct">Cancellation.</param>
   public async Task SnapshotAsync( IEnumerable<string?> composePaths, CancellationToken ct )
   {
      if( !_runAll )
      {
         return;
      }

      foreach( string path in composePaths.OfType<string>().Distinct( StringComparer.Ordinal ) )
      {
         _runningBefore[path] = await _host.IsRunningAsync( path, ct );
      }

      int running = _runningBefore.Count( p => p.Value );
      _log( $"Engines running before the run (left running afterwards): {( running == 0 ? "none" : string.Join( ", ", RunningBefore.Select( Path.GetFileName ) ) )}" );
   }

   /// <summary>
   /// Makes sure a compose-hosted target's engine is up before it is measured.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="notes">The target's notes (what was started, and why it stays up).</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">Not run-all and the engine is not running, or it would not start.</exception>
   public async Task EnsureRunningAsync( BenchTarget target, List<string> notes, CancellationToken ct )
   {
      if( target.ComposePath is not string path )
      {
         return;
      }

      bool runningNow = await _host.IsRunningAsync( path, ct );
      if( !_runningBefore.ContainsKey( path ) )
      {
         _runningBefore[path] = runningNow;
      }

      if( runningNow )
      {
         notes.Add( _runningBefore[path] ? "Engine was already running before the run; left running." : "Engine was not running when the run began but was already up at its turn; run-all did not start it now, so it leaves it running." );
         return;
      }

      if( !_runAll )
      {
         throw new InvalidOperationException( $"{target.Name} is not running. Start it with: sudo docker compose -f {path} up -d (or use run-all)." );
      }

      if( !_runningBefore[path] )
      {
         _startedByRun.Add( path );
      }

      string? cpuset = _runningBefore[path] ? null : _engineCpus;
      _log( $"  starting {Path.GetFileName( path )}{( cpuset != null ? $" with every container created on CPUs {cpuset}" : string.Empty )}" );
      string? started = await _host.UpAsync( path, cpuset, ct );
      if( started != null )
      {
         _log( $"  {started}" );
      }

      notes.Add( StartNote( _runningBefore[path], cpuset, started ) );
   }

   /// <summary>
   /// Stops the target's engine when this run started it. Safe to call after a failed start:
   /// a half-started engine is stopped too. Problems become notes; nothing here throws.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="notes">The target's notes.</param>
   public async Task ReleaseAsync( BenchTarget target, List<string> notes )
   {
      if( target.ComposePath is not string path || !_startedByRun.Remove( path ) )
      {
         return;
      }

      _log( $"  stopping {Path.GetFileName( path )} (run-all started it)" );
      try
      {
         if( await _host.DownAsync( path, CancellationToken.None ) is string problem )
         {
            notes.Add( problem );
         }
      }
      catch( Exception ex )
      {
         notes.Add( $"Could not stop {Path.GetFileName( path )}: {ex.Message}" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The note for an engine run-all started.
   /// </summary>
   /// <param name="runningBefore">True when it was running before the run (so it is left running).</param>
   /// <param name="cpuset">The CPUs it was asked to be created on, or null.</param>
   /// <param name="started">What the host read back about the start, or null when it applied no CPU set.</param>
   /// <returns>The note.</returns>
   private static string StartNote( bool runningBefore, string? cpuset, string? started )
   {
      if( runningBefore )
      {
         return "Engine was running before the run but was down at its turn; run-all started it unrestricted (as it was) and leaves it running, as it found it.";
      }

      if( cpuset == null )
      {
         return "Started by run-all for this measurement, unrestricted (machine control off or the CPUs not split), and stopped afterwards.";
      }

      return started != null
         ? $"Started by run-all for this measurement with every container created on CPUs {cpuset} ({started.TrimEnd( '.' )}); stopped afterwards."
         : $"Started by run-all for this measurement and stopped afterwards; it was to be created on CPUs {cpuset}, but the host did not apply a CPU set at creation, so machine control moved it there after the start (the CPU pinning note says so).";
   }

   #endregion Private Methods
}
