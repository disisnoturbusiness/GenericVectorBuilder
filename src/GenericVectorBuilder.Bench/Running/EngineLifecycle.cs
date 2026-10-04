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
/// </summary>
public sealed class EngineLifecycle
{
   #region Data Members

   private readonly IEngineHost _host;
   private readonly bool _runAll;
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
   public EngineLifecycle( IEngineHost host, bool runAll, Action<string> log )
   {
      _host = host;
      _runAll = runAll;
      _log = log;
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

      _log( $"  starting {Path.GetFileName( path )}" );
      await _host.UpAsync( path, ct );
      notes.Add( _runningBefore[path]
         ? "Engine was running before the run but was down at its turn; run-all started it and leaves it running, as it found it."
         : "Started by run-all for this measurement and stopped afterwards." );
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
}
