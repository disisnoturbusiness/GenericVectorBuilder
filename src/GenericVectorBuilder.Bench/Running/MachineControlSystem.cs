using System.Globalization;
using GenericVectorBuilder.Bench.Targets;
using Microsoft.Data.SqlClient;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// The parts of the machine that machine control reads and changes: files under /proc and
/// /sys, outside programs (sudo, docker, taskset, lscpu), and SQL Server.
/// Why an interface: restoring a machine after a crash decides whether the box is left in a
/// changed state, so every rule is tested against a fake machine that records each call,
/// instead of being trusted untested or tested only on the real box.
/// </summary>
public interface IMachineSystem
{
   /// <summary>This process's id.</summary>
   int ProcessId { get; }

   /// <summary>
   /// Reads a file.
   /// </summary>
   /// <param name="path">Absolute path.</param>
   /// <returns>Its text, or null when it does not exist or cannot be read.</returns>
   string? ReadFile( string path );

   /// <summary>
   /// Names of the entries of a folder.
   /// </summary>
   /// <param name="path">Absolute path.</param>
   /// <returns>Entry names (not paths); empty when the folder does not exist.</returns>
   IReadOnlyList<string> ListDirectory( string path );

   /// <summary>
   /// Where a symbolic link points, as the link holds it (for /proc/PID/fd/N, "socket:[INODE]").
   /// </summary>
   /// <param name="path">Absolute path of the link.</param>
   /// <returns>The target text, or null when it is not a link or cannot be read.</returns>
   string? ReadLink( string path );

   /// <summary>
   /// Runs a program with a time limit.
   /// </summary>
   /// <param name="program">Program.</param>
   /// <param name="arguments">Arguments, each passed as one argument.</param>
   /// <param name="timeout">Longest wait before the program is killed and a TimeoutException thrown.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Exit code and output.</returns>
   Task<ShellResult> RunAsync( string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct );

   /// <summary>
   /// Runs a SQL batch on SQL Server and returns every row of its last result set as text.
   /// </summary>
   /// <param name="sql">The batch.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Rows (empty for a statement without results).</returns>
   Task<IReadOnlyList<string[]>> SqlQueryAsync( string sql, CancellationToken ct );
}

/// <summary>
/// The real machine. Every program call goes through <see cref="Shell.RunAsync"/>, which kills
/// the program at its time limit; every SQL call has a 15 s connect limit and a 60 s command limit.
/// </summary>
public sealed class LinuxMachineSystem : IMachineSystem
{
   #region Data Members

   private const int SQL_COMMAND_SECONDS = 60;

   private readonly Func<string> _sqlConnection;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the real machine.
   /// </summary>
   /// <param name="sqlConnection">Builds the SQL Server connection string when SQL is first needed (it reads a password file).</param>
   public LinuxMachineSystem( Func<string> sqlConnection )
   {
      _sqlConnection = sqlConnection;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>This process's id.</summary>
   public int ProcessId => Environment.ProcessId;

   /// <summary>
   /// Reads a file; null when missing or unreadable (a process can exit between list and read).
   /// </summary>
   /// <param name="path">Absolute path.</param>
   /// <returns>Text or null.</returns>
   public string? ReadFile( string path )
   {
      try
      {
         return File.ReadAllText( path );
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return null;
      }
   }

   /// <summary>
   /// Entry names of a folder; empty when missing or unreadable.
   /// </summary>
   /// <param name="path">Absolute path.</param>
   /// <returns>Names.</returns>
   public IReadOnlyList<string> ListDirectory( string path )
   {
      try
      {
         return Directory.EnumerateFileSystemEntries( path ).Select( p => Path.GetFileName( p ) ).ToList();
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return Array.Empty<string>();
      }
   }

   /// <summary>
   /// A link's target text; null when missing, not a link, or unreadable (a descriptor can close
   /// between list and read).
   /// </summary>
   /// <param name="path">Absolute path.</param>
   /// <returns>Target text or null.</returns>
   public string? ReadLink( string path )
   {
      try
      {
         return new FileInfo( path ).LinkTarget;
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return null;
      }
   }

   /// <summary>
   /// Runs a program through <see cref="Shell.RunAsync"/>.
   /// </summary>
   /// <param name="program">Program.</param>
   /// <param name="arguments">Arguments.</param>
   /// <param name="timeout">Time limit.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Exit code and output.</returns>
   public Task<ShellResult> RunAsync( string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct )
   {
      return Shell.RunAsync( program, arguments, timeout, ct );
   }

   /// <summary>
   /// Runs a batch and returns the rows of its last result set.
   /// </summary>
   /// <param name="sql">The batch.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Rows as text.</returns>
   public async Task<IReadOnlyList<string[]>> SqlQueryAsync( string sql, CancellationToken ct )
   {
      await using var connection = new SqlConnection( _sqlConnection() );
      await connection.OpenAsync( ct );
      await using var command = new SqlCommand( sql, connection ) { CommandTimeout = SQL_COMMAND_SECONDS };
      await using SqlDataReader reader = await command.ExecuteReaderAsync( ct );
      var rows = new List<string[]>();
      do
      {
         rows.Clear();
         while( await reader.ReadAsync( ct ) )
         {
            rows.Add( Enumerable.Range( 0, reader.FieldCount ).Select( i => Convert.ToString( reader.GetValue( i ), CultureInfo.InvariantCulture ) ?? string.Empty ).ToArray() );
         }
      }
      while( await reader.NextResultAsync( ct ) );
      return rows;
   }

   #endregion Public Methods
}

/// <summary>
/// Facts about processes read from /proc, and the root commands machine control runs.
/// Why "sudo -n": it fails at once instead of waiting for a password nobody will type.
/// </summary>
public static class ProcessInfo
{
   #region Data Members

   /// <summary>Time limit for sudo, taskset, docker inspect and docker update.</summary>
   public static readonly TimeSpan COMMAND_TIMEOUT = TimeSpan.FromSeconds( 60 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The fields of /proc/PID/stat after the command name, so index 0 is field 3 (state).
   /// Why after the name: the name is in brackets and may itself contain spaces.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="pid">Process id.</param>
   /// <returns>The fields, or null when the process does not exist.</returns>
   public static string[]? StatFields( IMachineSystem system, int pid )
   {
      string? stat = system.ReadFile( $"/proc/{pid}/stat" );
      int close = stat?.LastIndexOf( ')' ) ?? -1;
      return close < 0 ? null : stat![( close + 1 )..].Split( ' ', StringSplitOptions.RemoveEmptyEntries );
   }

   /// <summary>
   /// The process's start time in clock ticks since boot (field 22).
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="pid">Process id.</param>
   /// <returns>The start time, or null when the process does not exist.</returns>
   public static long? StartTicks( IMachineSystem system, int pid )
   {
      string[]? fields = StatFields( system, pid );
      return fields != null && fields.Length > 19 && long.TryParse( fields[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks ) ? ticks : null;
   }

   /// <summary>
   /// True when the process exists and is the same process that was recorded (same start time).
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="pid">Process id.</param>
   /// <param name="startTicks">Recorded start time.</param>
   /// <returns>True when it is still the recorded process.</returns>
   public static bool IsSameProcess( IMachineSystem system, int pid, long startTicks )
   {
      return pid > 0 && StartTicks( system, pid ) == startTicks;
   }

   /// <summary>
   /// The process's command name.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="pid">Process id.</param>
   /// <returns>The name, or null.</returns>
   public static string? Name( IMachineSystem system, int pid )
   {
      return system.ReadFile( $"/proc/{pid}/comm" )?.Trim();
   }

   /// <summary>
   /// The cgroup v2 folder of a process under /sys/fs/cgroup, e.g.
   /// "/sys/fs/cgroup/system.slice/qdrant.service". Null for the root cgroup (which holds the
   /// whole machine) or when unknown.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="pid">Process id.</param>
   /// <returns>The folder, or null.</returns>
   public static string? CgroupFolder( IMachineSystem system, int pid )
   {
      string? line = system.ReadFile( $"/proc/{pid}/cgroup" )?.Split( '\n' ).FirstOrDefault( l => l.StartsWith( "0::", StringComparison.Ordinal ) );
      string? path = line?[3..].Trim();
      return string.IsNullOrEmpty( path ) || path == "/" ? null : "/sys/fs/cgroup" + path;
   }

   /// <summary>
   /// Process ids whose command name is exactly <paramref name="name"/>, found by reading /proc.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="name">Command name, e.g. "qdrant".</param>
   /// <returns>Ids, ascending.</returns>
   public static IReadOnlyList<int> FindByName( IMachineSystem system, string name )
   {
      return system.ListDirectory( "/proc" )
         .Select( e => int.TryParse( e, NumberStyles.None, CultureInfo.InvariantCulture, out int pid ) ? pid : -1 )
         .Where( pid => pid > 0 && Name( system, pid ) == name )
         .OrderBy( pid => pid ).ToList();
   }

   /// <summary>
   /// Like <see cref="FindByName"/>, leaving out processes inside a container (their cgroup runs
   /// through a Docker or containerd scope). Why: the benchmark's own SQL Server and Qdrant
   /// containers run processes named "sqlservr" and "qdrant" beside the native services, and the
   /// native comparison targets must pin and read only the native ones.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="name">Command name.</param>
   /// <returns>Ids, ascending.</returns>
   public static IReadOnlyList<int> FindNativeByName( IMachineSystem system, string name )
   {
      return FindByName( system, name ).Where( pid => system.ReadFile( $"/proc/{pid}/cgroup" ) is not string cgroup || !HostProcess.IsContainer( cgroup ) ).ToList();
   }

   /// <summary>
   /// The affinity of every thread of a process, from /proc/PID/task/TID/status.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="pid">Process id.</param>
   /// <returns>Thread count per canonical CPU list.</returns>
   public static Dictionary<string, int> ThreadAffinities( IMachineSystem system, int pid )
   {
      var counts = new Dictionary<string, int>( StringComparer.Ordinal );
      foreach( string tid in system.ListDirectory( $"/proc/{pid}/task" ) )
      {
         string? line = system.ReadFile( $"/proc/{pid}/task/{tid}/status" )?.Split( '\n' ).FirstOrDefault( l => l.StartsWith( "Cpus_allowed_list:", StringComparison.Ordinal ) );
         if( line != null )
         {
            string list = CpuList.Format( CpuList.Parse( line["Cpus_allowed_list:".Length..].Trim() ) );
            counts[list] = counts.GetValueOrDefault( list ) + 1;
         }
      }

      return counts;
   }

   /// <summary>
   /// Runs a command as root with "sudo -n" and the standard time limit.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="arguments">The command and its arguments.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Exit code and output.</returns>
   public static Task<ShellResult> SudoAsync( IMachineSystem system, IReadOnlyList<string> arguments, CancellationToken ct )
   {
      return system.RunAsync( "sudo", new[] { "-n" }.Concat( arguments ).ToArray(), COMMAND_TIMEOUT, ct );
   }

   /// <summary>
   /// Writes one line to a file as root (a /sys setting). The value and the path are passed to
   /// a fixed script as arguments, never pasted into it, so neither can be read as a command.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="path">File to write.</param>
   /// <param name="value">The line.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">The write failed; the message says what sudo printed.</exception>
   public static async Task WriteAsRootAsync( IMachineSystem system, string path, string value, CancellationToken ct )
   {
      ShellResult result = await SudoAsync( system, new[] { "sh", "-c", "printf '%s\\n' \"$1\" > \"$2\"", "gvb-bench", value, path }, ct );
      Require( result, $"write '{value}' to {path}" );
   }

   /// <summary>
   /// Throws a plain message when a command failed.
   /// </summary>
   /// <param name="result">Command result.</param>
   /// <param name="what">What the command was for.</param>
   /// <exception cref="InvalidOperationException">Exit code not 0.</exception>
   public static void Require( ShellResult result, string what )
   {
      if( result.ExitCode != 0 )
      {
         string said = ( result.Error.Trim().Length > 0 ? result.Error : result.Output ).Trim();
         throw new InvalidOperationException( $"Could not {what}: exit {result.ExitCode}{( said.Length > 0 ? ": " + said : string.Empty )}" );
      }
   }

   #endregion Public Methods
}

/// <summary>
/// Reads the kernel's thermal throttle counters of every CPU (thermal_throttle/core_throttle_count
/// and package_throttle_count) with each CPU's physical core (topology/thread_siblings_list) and
/// package (topology/physical_package_id). It only reads files under /sys.
/// Why it is called only at a run's start and end and at a target's start, first warm-up line
/// and end: the clock sampler's reads must stay what they were (its CPU time is part of the
/// client's CPU per search), and a read inside a timed pass would add work to it.
/// </summary>
public static class ThermalThrottle
{
   #region Public Methods

   /// <summary>
   /// The file counting a CPU's core thermal throttle events.
   /// </summary>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>The path.</returns>
   public static string CorePath( int cpu )
   {
      return $"/sys/devices/system/cpu/cpu{cpu}/thermal_throttle/core_throttle_count";
   }

   /// <summary>
   /// The file counting the package thermal throttle events, as a CPU reports them.
   /// </summary>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>The path.</returns>
   public static string PackagePath( int cpu )
   {
      return $"/sys/devices/system/cpu/cpu{cpu}/thermal_throttle/package_throttle_count";
   }

   /// <summary>
   /// The file naming a CPU's hyperthread siblings (its physical core).
   /// </summary>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>The path.</returns>
   public static string SiblingsPath( int cpu )
   {
      return $"/sys/devices/system/cpu/cpu{cpu}/topology/thread_siblings_list";
   }

   /// <summary>
   /// The file naming a CPU's package.
   /// </summary>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>The path.</returns>
   public static string PackageIdPath( int cpu )
   {
      return $"/sys/devices/system/cpu/cpu{cpu}/topology/physical_package_id";
   }

   /// <summary>
   /// Reads every CPU's counters now. A counter that cannot be read leaves the totals null and is
   /// named in <see cref="ThrottleCounts.Unreadable"/>; a CPU whose topology cannot be read is
   /// counted alone (which can only count an event twice, never miss one) and named in the note.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="cpus">CPUs to read.</param>
   /// <returns>The reading.</returns>
   public static ThrottleCounts Read( IMachineSystem system, IReadOnlyList<int> cpus )
   {
      var per = new List<CpuThrottle>();
      var unreadable = new List<int>();
      var ownCore = new List<int>();
      var ownPackage = new List<int>();
      foreach( int cpu in cpus )
      {
         long? core = Count( system, CorePath( cpu ) );
         long? package = Count( system, PackagePath( cpu ) );
         if( core == null || package == null )
         {
            unreadable.Add( cpu );
         }

         string? siblings = Siblings( system, cpu );
         string? packageId = system.ReadFile( PackageIdPath( cpu ) )?.Trim();
         string self = "cpu" + cpu.ToString( CultureInfo.InvariantCulture );
         if( siblings == null )
         {
            ownCore.Add( cpu );
         }

         if( string.IsNullOrEmpty( packageId ) )
         {
            ownPackage.Add( cpu );
         }

         per.Add( new CpuThrottle( cpu, core, package, siblings ?? self, string.IsNullOrEmpty( packageId ) ? self : packageId ) );
      }

      bool complete = unreadable.Count == 0 && per.Count > 0;
      return new ThrottleCounts
      {
         ReadUtc = DateTime.UtcNow.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture ),
         CoreEvents = complete ? per.GroupBy( c => c.Siblings, StringComparer.Ordinal ).Sum( g => g.Max( c => c.CoreCount!.Value ) ) : null,
         PackageEvents = complete ? per.GroupBy( c => c.PackageId, StringComparer.Ordinal ).Sum( g => g.Max( c => c.PackageCount!.Value ) ) : null,
         PerCpu = per,
         Unreadable = per.Count == 0 ? "no CPU to read" : unreadable.Count > 0 ? $"no readable core_throttle_count or package_throttle_count under {CorePath( unreadable[0] ).Replace( "/core_throttle_count", string.Empty, StringComparison.Ordinal )} on CPU(s) {CpuList.Format( unreadable )}" : null,
         Note = TopologyNote( ownCore, ownPackage ),
      };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Says which CPUs' topology could not be read and so were counted as their own core or package.
   /// </summary>
   /// <param name="ownCore">CPUs whose thread_siblings_list could not be read.</param>
   /// <param name="ownPackage">CPUs whose physical_package_id could not be read.</param>
   /// <returns>The note, or null when every CPU's topology was read.</returns>
   private static string? TopologyNote( List<int> ownCore, List<int> ownPackage )
   {
      var parts = new List<string>();
      if( ownCore.Count > 0 )
      {
         parts.Add( $"the thread siblings of CPU(s) {CpuList.Format( ownCore )} could not be read, so each is counted as its own core" );
      }

      if( ownPackage.Count > 0 )
      {
         parts.Add( $"the package of CPU(s) {CpuList.Format( ownPackage )} could not be read, so each is counted as its own package" );
      }

      return parts.Count > 0 ? string.Join( "; ", parts ) : null;
   }

   /// <summary>
   /// A counter file's value.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="path">The file.</param>
   /// <returns>The count, or null when missing or not a whole number.</returns>
   private static long? Count( IMachineSystem system, string path )
   {
      return long.TryParse( system.ReadFile( path )?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long count ) ? count : null;
   }

   /// <summary>
   /// A CPU's siblings in canonical form.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>"0,4", or null when unreadable or not a CPU list.</returns>
   private static string? Siblings( IMachineSystem system, int cpu )
   {
      string? text = system.ReadFile( SiblingsPath( cpu ) )?.Trim();
      if( string.IsNullOrEmpty( text ) )
      {
         return null;
      }

      try
      {
         return CpuList.Format( CpuList.Parse( text ) );
      }
      catch( FormatException )
      {
         // Counted alone instead; Read names the CPU in its note.
         return null;
      }
   }

   #endregion Private Methods
}
