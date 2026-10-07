using System.Globalization;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Finds an engine's data folder on the host and measures it, so a run records how big the folder
/// was when the engine started, after any reset and when the searches ended.
/// Where the folder is: for a container engine, ~/gvb-data/engines/NAME where NAME is its compose
/// file's name without ".compose.yaml" (the same rule the disk figure uses); for DuckDB and
/// sqlite-vec, the sink's own default folder. The container engines are then checked against
/// "docker inspect": every folder the container may write to must lie inside the chosen folder, and
/// at least one must, otherwise part of the engine's writes would go uncounted and the record would
/// look complete.
/// How it is measured: "sudo -n du -sb" (the folders are owned by the container's user), within a
/// time limit. du exits 1 when a file vanished while it walked (ClickHouse and Milvus delete files
/// as they run) but still prints the total of what it saw, so exit 1 with a total is retried once
/// and the second total is kept; any other failure is an error, never a zero.
/// Why a seam for the command: the time limit and the exit-1 retry are tested with a fake runner,
/// since <see cref="Shell"/> is static.
/// </summary>
public sealed class DataFolderState
{
   #region Data Members

   /// <summary>Longest one du may take.</summary>
   public static readonly TimeSpan DU_TIMEOUT = TimeSpan.FromMinutes( 3 );

   private const string ENGINE_DATA_ROOT = "~/gvb-data/engines";
   private const string COMPOSE_SUFFIX = ".compose.yaml";

   private readonly Func<IEnumerable<string>, TimeSpan, CancellationToken, Task<ShellResult>> _run;
   private readonly TimeSpan _timeout;
   private readonly TimeSpan _retryPause;
   private readonly Func<string, bool> _exists;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the measurer.
   /// </summary>
   /// <param name="run">Runs "sudo" with the arguments within the time and returns its result; null for the real one.</param>
   /// <param name="timeout">Longest one du may take; null for <see cref="DU_TIMEOUT"/>.</param>
   /// <param name="retryPause">Wait before the one retry; null for one second.</param>
   /// <param name="exists">Tells whether a folder exists; null for Directory.Exists (a test passes one that does not look at the disk).</param>
   public DataFolderState( Func<IEnumerable<string>, TimeSpan, CancellationToken, Task<ShellResult>>? run = null, TimeSpan? timeout = null, TimeSpan? retryPause = null, Func<string, bool>? exists = null )
   {
      _run = run ?? ( ( arguments, limit, ct ) => Shell.RunAsync( "sudo", arguments, limit, ct ) );
      _timeout = timeout ?? DU_TIMEOUT;
      _retryPause = retryPause ?? TimeSpan.FromSeconds( 1 );
      _exists = exists ?? Directory.Exists;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The host folder that holds the target's data, or null for a target that has none of its own
   /// (an always-on server, or a target with no container, such as a fake in a test).
   /// </summary>
   /// <param name="target">The target.</param>
   /// <returns>The folder, or null.</returns>
   /// <exception cref="InvalidOperationException">A container target whose compose file name does not end in ".compose.yaml".</exception>
   public static string? FolderFor( BenchTarget target )
   {
      if( target.Hosting == "embedded" )
      {
         return target.Name switch
         {
            "duckdb" => DuckDbSinkOptions.DefaultDirectory(),
            "sqlitevec" => SqliteVecSinkOptions.DefaultDirectory(),
            _ => null,
         };
      }

      if( target.Container == null || target.ComposePath == null )
      {
         return null;
      }

      string file = Path.GetFileName( target.ComposePath );
      return file.EndsWith( COMPOSE_SUFFIX, StringComparison.Ordinal )
         ? Path.Combine( GvbSettings.Expand( ENGINE_DATA_ROOT ), file[..^COMPOSE_SUFFIX.Length] )
         : throw new InvalidOperationException( $"The compose file '{file}' of {target.Name} does not end in {COMPOSE_SUFFIX}, so its data folder cannot be named." );
   }

   /// <summary>
   /// Checks the folder against the container's mounts: at least one writable bind mount must lie
   /// inside the folder, and no writable bind mount may lie inside the engines' data root
   /// (~/gvb-data/engines) but outside the folder, because that would be another engine's folder or
   /// the root itself and part of what this engine writes would go uncounted.
   /// Why writable binds elsewhere are ignored: docker compose mounts a "configs: content:" block as
   /// a generated file outside the data root (Milvus has one), which is not engine data.
   /// </summary>
   /// <param name="folder">The chosen host folder.</param>
   /// <param name="container">The container as Docker describes it.</param>
   /// <param name="dataRoot">The engines' data root; null for ~/gvb-data/engines.</param>
   /// <exception cref="InvalidOperationException">A writable bind mount lies in the data root but outside the folder, or none lies inside the folder.</exception>
   public static void VerifyAgainstMounts( string folder, ContainerDescription container, string? dataRoot = null )
   {
      string root = folder.TrimEnd( '/' );
      string engines = ( dataRoot ?? GvbSettings.Expand( ENGINE_DATA_ROOT ) ).TrimEnd( '/' );
      List<ContainerMount> writable = container.Mounts.Where( m => m.Type == "bind" && m.ReadWrite ).ToList();
      ContainerMount? stray = writable.FirstOrDefault( m => IsInside( engines, m.Source ) && !IsInside( root, m.Source ) );
      if( stray != null )
      {
         throw new InvalidOperationException( $"Container {container.Name} can write to {stray.Source} (mounted at {stray.Destination}), which is in the engines' data root but outside the data folder {root} that the benchmark measures, so part of what the engine writes would not be counted." );
      }

      if( !writable.Any( m => IsInside( root, m.Source ) ) )
      {
         throw new InvalidOperationException( $"Container {container.Name} has no writable bind mount inside the data folder {root}, so the folder cannot be tied to it." );
      }
   }

   /// <summary>
   /// The size of a folder in bytes (du -sb, apparent size, as the disk figure measures). A folder
   /// that does not exist holds nothing, so it is 0 (an embedded engine's folder before its first run).
   /// </summary>
   /// <param name="path">The folder.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Bytes.</returns>
   /// <exception cref="InvalidOperationException">du failed (other than exit 1 with a total), printed no number, or did not finish in time.</exception>
   public async Task<long> BytesAsync( string path, CancellationToken ct )
   {
      if( !_exists( path ) )
      {
         return 0;
      }

      string[] arguments = { "-n", "du", "-sb", path };
      ShellResult first = await RunAsync( arguments, path, ct );
      if( first.ExitCode == 0 )
      {
         return ParseTotal( first.Output, path );
      }

      if( first.ExitCode != 1 || !TryParseTotal( first.Output, out _ ) )
      {
         throw new InvalidOperationException( $"du of {path} failed with exit code {first.ExitCode}: {Tail( first.Error )}" );
      }

      await Task.Delay( _retryPause, ct );
      ShellResult second = await RunAsync( arguments, path, ct );
      return second.ExitCode is 0 or 1 && TryParseTotal( second.Output, out long total )
         ? total
         : throw new InvalidOperationException( $"du of {path} failed again with exit code {second.ExitCode} on its one retry: {Tail( second.Error )}" );
   }

   /// <summary>
   /// The bytes of an embedded engine's bench database file and its write-ahead sidecars, or null
   /// when the target is not DuckDB or sqlite-vec or the file does not exist.
   /// Why only these: the engine's folder also holds other collections (1.3 GB and 293 MB of old
   /// scale-test files on this machine) that are not this run's.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="collection">The benchmark collection name.</param>
   /// <returns>Bytes, or null.</returns>
   public static long? BenchFileBytes( BenchTarget target, string collection )
   {
      string? file = BenchFile( target, collection );
      if( file == null || !File.Exists( file ) )
      {
         return null;
      }

      return SidecarSuffixes( target.Name ).Select( s => file + s ).Where( File.Exists ).Sum( f => new FileInfo( f ).Length );
   }

   /// <summary>
   /// The path of an embedded engine's bench database file (it may not exist yet), or null for any other target.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="collection">The benchmark collection name.</param>
   /// <returns>The path, or null.</returns>
   public static string? BenchFile( BenchTarget target, string collection )
   {
      string? folder = target.Hosting == "embedded" ? FolderFor( target ) : null;
      return folder == null ? null : Path.Combine( folder, $"gvb_{collection}" + ( target.Name == "duckdb" ? ".duckdb" : ".sqlite" ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The suffixes of the files that belong to one bench file, the file itself first.
   /// </summary>
   /// <param name="name">"duckdb" or "sqlitevec".</param>
   /// <returns>The suffixes.</returns>
   private static string[] SidecarSuffixes( string name )
   {
      return name == "duckdb" ? new[] { string.Empty, ".wal" } : new[] { string.Empty, "-wal", "-shm" };
   }

   /// <summary>
   /// True when a path is the folder or lies under it.
   /// </summary>
   /// <param name="root">The folder, without a trailing slash.</param>
   /// <param name="path">The path.</param>
   /// <returns>True when inside.</returns>
   private static bool IsInside( string root, string path )
   {
      return path.TrimEnd( '/' ) == root || path.StartsWith( root + "/", StringComparison.Ordinal );
   }

   /// <summary>
   /// Runs du within the time limit; a timeout or a failure to start becomes an error that names the folder.
   /// </summary>
   /// <param name="arguments">Arguments after "sudo".</param>
   /// <param name="path">The folder, for the message.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The result.</returns>
   private async Task<ShellResult> RunAsync( IEnumerable<string> arguments, string path, CancellationToken ct )
   {
      try
      {
         return await _run( arguments, _timeout, ct );
      }
      catch( Exception ex ) when( ex is TimeoutException or System.ComponentModel.Win32Exception )
      {
         throw new InvalidOperationException( $"du of {path} did not finish: {ex.Message}" );
      }
   }

   /// <summary>
   /// Reads the byte total from du's output ("123456\t/path").
   /// </summary>
   /// <param name="output">du's output.</param>
   /// <param name="path">The folder, for the message.</param>
   /// <returns>The total.</returns>
   /// <exception cref="InvalidOperationException">No number.</exception>
   private static long ParseTotal( string output, string path )
   {
      return TryParseTotal( output, out long total ) ? total : throw new InvalidOperationException( $"du of {path} printed no byte total: '{Tail( output )}'" );
   }

   /// <summary>
   /// Tries to read the byte total from du's output.
   /// </summary>
   /// <param name="output">du's output.</param>
   /// <param name="total">The total.</param>
   /// <returns>True when a number was found at the start of the first line.</returns>
   private static bool TryParseTotal( string output, out long total )
   {
      string first = output.Split( '\n', StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault() ?? string.Empty;
      return long.TryParse( first.Split( '\t', ' ' )[0], NumberStyles.None, CultureInfo.InvariantCulture, out total );
   }

   /// <summary>
   /// The last 200 characters of a message, on one line.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The tail.</returns>
   private static string Tail( string text )
   {
      string one = string.Join( ' ', text.Split( new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ) );
      return one.Length <= 200 ? one : "..." + one[^200..];
   }

   #endregion Private Methods
}
