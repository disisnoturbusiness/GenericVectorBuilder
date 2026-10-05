using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
/// Every start note keeps what the host reported about the start, whether or not a CPU set was asked for: a
/// start that needed more than one attempt (Milvus exits while starting and is started again) lists every
/// attempt in the note, in all three cases.
/// <see cref="FilesNote"/> records which files the engine is configured from (its compose file and every
/// read-only config file or folder it mounts) with a short SHA-256 each, so a change to a config file shows
/// in the results and the consolidate command refuses to merge runs made under different files.
/// </summary>
public sealed class EngineLifecycle
{
   #region Data Members

   /// <summary>Hex digits of each file's SHA-256 that the engine-files note keeps (enough to tell two versions of a file apart).</summary>
   public const int HASH_DIGITS = 12;

   private const int MAX_FILES = 200;
   private const long MAX_FILE_BYTES = 4 * 1024 * 1024;

   /// <summary>A read-only bind mount of a file or folder next to the compose file ("- ./clickhouse-config/x.xml:/etc/x.xml:ro"): the benchmark's config files; data folders and env files are never read-only relative mounts.</summary>
   private static readonly Regex READ_ONLY_MOUNT = new( @"^\s*-\s*[""']?(?<host>\.{1,2}/[^:""'\s]+):[^:""'\s]+:ro[""']?\s*$", RegexOptions.Compiled | RegexOptions.Multiline );

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
   /// The note that says which files the target's engine is configured from: the compose file and every
   /// file under each read-only relative mount in it (config files and folders, never data folders or
   /// env files), each with the first <see cref="HASH_DIGITS"/> hex digits of its SHA-256, in a bracketed
   /// list, followed by a sentence that says whether this run created the engine from them.
   /// Why a note and not a field: the result writer's target record is not this class's to change, and
   /// the consolidate command reads this note into the target's engine files (TargetResult.EngineFiles), so
   /// runs made under different files are refused. Why the hashes: ClickHouse's server configuration
   /// changed between two runs (21 system log tables switched off, QPS@8 up 16%) and no field of the
   /// results said so.
   /// Call it after <see cref="EnsureRunningAsync"/>, so the sentence about who created the engine is true.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <returns>The note; null when the target has no compose file on disk (the host already fails on a missing file); a note that says the files could not be read when they exist but cannot be.</returns>
   public string? FilesNote( BenchTarget target )
   {
      if( target.ComposePath is not string path || !File.Exists( path ) )
      {
         return null;
      }

      string origin = _startedByRun.Contains( path )
         ? "This run created the engine from these files."
         : "The engine was already running at its turn, so it may have been created from other files than these.";
      try
      {
         return $"Engine files (SHA-256, first {HASH_DIGITS} hex digits): [{string.Join( "; ", FileHashes( path ).Select( p => $"{p.Key} {p.Value}" ) )}]. {origin}";
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return $"Engine files could not be recorded for {Path.GetFileName( path )}: {ex.Message}";
      }
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
   /// The compose file and every file under its read-only relative mounts, with the short SHA-256 of each,
   /// sorted by the path relative to the compose file's folder.
   /// </summary>
   /// <param name="composePath">The compose file.</param>
   /// <returns>Path and hash pairs; a mount that does not exist is listed as "missing", a file over the size limit as "too large to hash", and files past the count limit as one "more files" entry.</returns>
   private static SortedDictionary<string, string> FileHashes( string composePath )
   {
      string folder = Path.GetDirectoryName( Path.GetFullPath( composePath ) ) ?? ".";
      var hashes = new SortedDictionary<string, string>( StringComparer.Ordinal ) { [Path.GetFileName( composePath )] = Hash( composePath ) };
      foreach( Match mount in READ_ONLY_MOUNT.Matches( File.ReadAllText( composePath ) ) )
      {
         string full = Path.GetFullPath( Path.Combine( folder, mount.Groups["host"].Value.TrimEnd( '/' ) ) );
         string host = Path.GetRelativePath( folder, full );
         if( File.Exists( full ) )
         {
            hashes[host] = Hash( full );
         }
         else if( Directory.Exists( full ) )
         {
            AddFolder( hashes, folder, full );
         }
         else
         {
            hashes[host] = "missing";
         }
      }

      return hashes;
   }

   /// <summary>
   /// Adds every file under a mounted folder (at most <see cref="MAX_FILES"/>, in path order).
   /// </summary>
   /// <param name="hashes">Receives the entries.</param>
   /// <param name="baseFolder">The compose file's folder, which the entries are relative to.</param>
   /// <param name="mounted">The mounted folder.</param>
   private static void AddFolder( SortedDictionary<string, string> hashes, string baseFolder, string mounted )
   {
      List<string> files = Directory.EnumerateFiles( mounted, "*", SearchOption.AllDirectories ).OrderBy( f => f, StringComparer.Ordinal ).ToList();
      foreach( string file in files.Take( MAX_FILES ) )
      {
         hashes[Path.GetRelativePath( baseFolder, file )] = Hash( file );
      }

      if( files.Count > MAX_FILES )
      {
         hashes[Path.GetRelativePath( baseFolder, mounted ) + "/"] = $"{files.Count - MAX_FILES} more files not hashed";
      }
   }

   /// <summary>
   /// The first <see cref="HASH_DIGITS"/> hex digits of a file's SHA-256, or a short reason when the file is over the size limit.
   /// </summary>
   /// <param name="path">The file.</param>
   /// <returns>The digits, lower case.</returns>
   private static string Hash( string path )
   {
      return new FileInfo( path ).Length > MAX_FILE_BYTES
         ? "too large to hash"
         : Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( path ) ) )[..HASH_DIGITS].ToLowerInvariant();
   }

   /// <summary>
   /// The note for an engine run-all started.
   /// </summary>
   /// <param name="runningBefore">True when it was running before the run (so it is left running).</param>
   /// <param name="cpuset">The CPUs it was asked to be created on, or null.</param>
   /// <param name="started">What the host reported about the start (the CPU set it read back, and every attempt when the start needed more than one), or null when it reported nothing.</param>
   /// <returns>The note; it always carries <paramref name="started"/>, so a start that was retried says so in every case.</returns>
   private static string StartNote( bool runningBefore, string? cpuset, string? started )
   {
      string host = started == null ? string.Empty : $" What the host reported about the start: {started.TrimEnd( '.' )}.";
      if( runningBefore )
      {
         return "Engine was running before the run but was down at its turn; run-all started it unrestricted (as it was) and leaves it running, as it found it." + host;
      }

      if( cpuset == null )
      {
         return "Started by run-all for this measurement, unrestricted (machine control off or the CPUs not split), and stopped afterwards." + host;
      }

      return started != null
         ? $"Started by run-all for this measurement with every container created on CPUs {cpuset} ({started.TrimEnd( '.' )}); stopped afterwards."
         : $"Started by run-all for this measurement and stopped afterwards; it was to be created on CPUs {cpuset}, but the host did not apply a CPU set at creation, so machine control moved it there after the start (the CPU pinning note says so).";
   }

   #endregion Private Methods
}
