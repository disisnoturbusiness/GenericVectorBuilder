using System.Globalization;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// How one target's engine was held to the engine CPUs, for results.json and the notes.
/// </summary>
public sealed class TargetPinning
{
   #region Public Methods

   /// <summary>Target name.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>compose, always-on or embedded.</summary>
   public string Hosting { get; set; } = string.Empty;

   /// <summary>How it was pinned, e.g. "docker update --cpuset-cpus".</summary>
   public string Method { get; set; } = string.Empty;

   /// <summary>Kernel CPUs the engine was held to, or null when not pinned.</summary>
   public string? Cpus { get; set; }

   /// <summary>What was changed, one line per container or process.</summary>
   public List<string> Changes { get; set; } = new();

   /// <summary>What was read back to prove the change took.</summary>
   public List<string> Verified { get; set; } = new();

   /// <summary>What went wrong; any entry means the engine was not (fully) pinned.</summary>
   public List<string> Problems { get; set; } = new();

   /// <summary>What was put back after the target.</summary>
   public List<string> Restored { get; set; } = new();

   /// <summary>Which lever pinned it: "embedded", "compose", "sql", "qdrant" or "none" (see <see cref="EnginePinner.Kind"/>).</summary>
   public string Kind { get; set; } = string.Empty;

   /// <summary>
   /// The cgroup folders this target's turn subtracted from outside load as benchmark work (dockerd's
   /// included for a compose engine), as last set for the sampler. Why recorded: which cgroups count
   /// as the engine and which as outside work decides the outside load and the engine CPU figures,
   /// and the engine CPU rule text is generated from this list.
   /// </summary>
   public List<string> Cgroups { get; set; } = new();

   /// <summary>
   /// Why this target's cgroups may be incomplete (finding or pinning its containers threw), or null.
   /// Kept out of results.json on purpose: it is a problem already, and it becomes every pass's
   /// engineCpuNullReason.
   /// </summary>
   [System.Text.Json.Serialization.JsonIgnore]
   public string? EngineCpuProblem { get; set; }

   #endregion Public Methods
}

/// <summary>
/// Holds the engine under test to the engine CPUs and the benchmark client to the client CPUs.
/// Each engine kind needs its own lever: containers through "docker update --cpuset-cpus",
/// SQL Server through ALTER SERVER CONFIGURATION SET PROCESS AFFINITY (it schedules its own
/// threads and would undo an outside taskset), Qdrant (a systemd process) through taskset on
/// every thread. An embedded engine runs inside the client and so shares the client CPUs.
/// Every previous value is recorded in the state file before the change.
/// </summary>
public sealed class EnginePinner
{
   #region Data Members

   private const string SQL_ONLINE_IDS = "SELECT cpu_id FROM sys.dm_os_schedulers WHERE status = 'VISIBLE ONLINE' ORDER BY cpu_id;";

   private readonly IMachineSystem _system;
   private readonly MachineStateKeeper _keeper;
   private readonly CpuPartition _partition;
   private readonly string _engineCpus;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the pinner.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="keeper">The run's state (written before each change).</param>
   /// <param name="partition">The CPU split; must be split.</param>
   public EnginePinner( IMachineSystem system, MachineStateKeeper keeper, CpuPartition partition )
   {
      _system = system;
      _keeper = keeper;
      _partition = partition;
      _engineCpus = CpuList.Format( partition.EngineCpus );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// cgroup folders whose CPU time belongs to the engine under test (for the busy-box rule,
   /// which counts only work outside the benchmark). Empty for an embedded engine. Filled
   /// before any pin is attempted, so an engine that could not be pinned is still not counted
   /// as outside work (the live run of 2026-10-04 counted SQL Server's own searches as outside
   /// load when its pin failed).
   /// </summary>
   public List<string> EngineGroups { get; } = new();

   /// <summary>
   /// dockerd's cgroup folder once a compose engine was pinned (it is one of <see cref="EngineGroups"/>
   /// for that target), or null. Why separate: dockerd's CPU is subtracted from outside load as
   /// benchmark work but is not the engine's, so the engine CPU figure leaves this folder out by
   /// string equality.
   /// </summary>
   public string? DockerdGroup { get; private set; }

   /// <summary>
   /// Pins one target's engine, as far as it is running now. A compose engine that is not
   /// running yet is pinned by <see cref="PinContainersAsync"/> once run-all has started it.
   /// Problems are recorded in the result, not thrown: the target is still measured and its
   /// notes carry a warning.
   /// </summary>
   /// <param name="target">Target name.</param>
   /// <param name="hosting">compose, always-on or embedded.</param>
   /// <param name="composePath">Compose file, for compose targets.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was done.</returns>
   public async Task<TargetPinning> PinTargetAsync( string target, string hosting, string? composePath, CancellationToken ct )
   {
      EngineGroups.Clear();
      string kind = Kind( target, hosting, composePath );
      var pinning = new TargetPinning { Target = target, Hosting = hosting, Kind = kind };
      try
      {
         switch( kind )
         {
            case "embedded":
               pinning.Method = "none: an embedded engine runs inside the benchmark client process, so it shares the client CPUs";
               pinning.Cpus = CpuList.Format( _partition.ClientCpus );
               break;
            case "compose":
               pinning.Method = "cpuset from the container's creation when run-all started it on the engine CPUs, else docker update --cpuset-cpus (each container's change line says which)";
               pinning.Cpus = _engineCpus;
               await PinContainersAsync( pinning, composePath!, ct );
               break;
            case "sql":
               pinning.Method = "ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU";
               pinning.Cpus = _engineCpus;
               await PinSqlServerAsync( pinning, ct );
               break;
            case "qdrant":
               pinning.Method = "taskset -a -p (every thread)";
               pinning.Cpus = _engineCpus;
               await PinProcessAsync( pinning, "qdrant", ct );
               break;
            default:
               pinning.Method = "none";
               pinning.Problems.Add( $"no way to pin an always-on engine that is not SQL Server or Qdrant ({target}); it ran on every CPU" );
               break;
         }
      }
      catch( Exception ex ) when( ex is InvalidOperationException or TimeoutException or IOException or FormatException or Microsoft.Data.SqlClient.SqlException )
      {
         pinning.Problems.Add( ex.Message );
         MarkIncompleteCgroups( pinning, ex.Message );
      }

      pinning.Cgroups = EngineGroups.ToList();
      return pinning;
   }

   /// <summary>
   /// Pins every running container of a compose file that is not pinned yet. A container that
   /// is already on exactly the engine CPUs (run-all had it created there) is left alone and not
   /// recorded: nothing was changed on it, so nothing needs putting back (the pinned start itself
   /// is recorded before the start, see <see cref="PinnedStart"/>). One moved with "docker update"
   /// says in its change line that it sized its thread pools for the CPUs it had before.
   /// Why "--orphans=false": four engine files (elasticsearch, opensearch, typesense, vespa) share
   /// one compose project, and without it compose lists the other three engines' containers too.
   /// </summary>
   /// <param name="pinning">The target's record.</param>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task PinContainersAsync( TargetPinning pinning, string composePath, CancellationToken ct )
   {
      if( ProcessInfo.FindByName( _system, "dockerd" ).FirstOrDefault() is int dockerd and > 0 )
      {
         AddGroup( dockerd );
         DockerdGroup = ProcessInfo.CgroupFolder( _system, dockerd ) ?? DockerdGroup;
      }

      ShellResult ps = await ProcessInfo.SudoAsync( _system, new[] { "docker", "compose", "-f", composePath, "ps", "-q", "--status", "running", "--orphans=false" }, ct );
      ProcessInfo.Require( ps, $"list the running containers of {Path.GetFileName( composePath )}" );
      foreach( string id in ps.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
      {
         if( _keeper.Read( s => s.Containers.Any( c => c.Id.StartsWith( id, StringComparison.Ordinal ) || id.StartsWith( c.Id, StringComparison.Ordinal ) ) ) )
         {
            continue;
         }

         ShellResult inspect = await ProcessInfo.SudoAsync( _system, new[] { "docker", "inspect", "-f", "{{.Name}}|{{.HostConfig.CpusetCpus}}|{{.State.Pid}}", id }, ct );
         ProcessInfo.Require( inspect, $"read container {id}" );
         string[] parts = inspect.Output.Trim().Split( '|' );
         if( parts.Length != 3 || !int.TryParse( parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid ) )
         {
            throw new InvalidOperationException( $"docker inspect of container {id} printed '{inspect.Output.Trim()}', not name|cpuset|pid" );
         }

         string name = parts[0].TrimStart( '/' );
         AddGroup( pid );
         if( parts[1].Length > 0 && CpuList.Same( parts[1], _engineCpus ) )
         {
            pinning.Changes.Add( $"container {name}: already on CPUs {_engineCpus} (its cpuset since creation), not changed" );
            pinning.Verified.Add( $"container {name}: {ContainerThreads( pid )}" );
            continue;
         }

         _keeper.Record( s => s.Containers.Add( new ContainerPin( id, name, parts[1], pinning.Target ) ) );
         ShellResult update = await ProcessInfo.SudoAsync( _system, new[] { "docker", "update", "--cpuset-cpus", _engineCpus, id }, ct );
         ProcessInfo.Require( update, $"pin container {name} to CPUs {_engineCpus}" );
         pinning.Changes.Add( $"container {name}: cpuset '{parts[1]}' -> {_engineCpus} with docker update after it had started, so it sized its thread pools for "
            + $"{( parts[1].Length == 0 ? "every CPU" : "CPUs " + parts[1] )}" );
         pinning.Verified.Add( $"container {name}: {ContainerThreads( pid )}" );
      }
   }

   /// <summary>
   /// Pins this process (the benchmark client, and any embedded engine inside it) to the client
   /// CPUs on every thread. Threads started later inherit the affinity of the thread that
   /// starts them, so every thread the client ever has stays on the client CPUs.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The affinity before, to put back at the end, and what was read back.</returns>
   public async Task<(string Previous, string Verified)> PinClientAsync( CancellationToken ct )
   {
      string previous = await AffinityAsync( _system.ProcessId, ct );
      string client = CpuList.Format( _partition.ClientCpus );
      await SetAffinityAsync( _system.ProcessId, client, false, ct );
      return ( previous, $"client pid {_system.ProcessId}: {Describe( ProcessInfo.ThreadAffinities( _system, _system.ProcessId ) )}" );
   }

   /// <summary>
   /// Puts this process's affinity back.
   /// </summary>
   /// <param name="previous">Affinity from <see cref="PinClientAsync"/>.</param>
   /// <param name="ct">Cancellation.</param>
   public Task RestoreClientAsync( string previous, CancellationToken ct )
   {
      return SetAffinityAsync( _system.ProcessId, previous, false, ct );
   }

   /// <summary>
   /// Which lever pins a target: "embedded", "compose", "sql", "qdrant" or "none".
   /// </summary>
   /// <param name="target">Target name.</param>
   /// <param name="hosting">compose, always-on or embedded.</param>
   /// <param name="composePath">Compose file or null.</param>
   /// <returns>The kind.</returns>
   public static string Kind( string target, string hosting, string? composePath )
   {
      return hosting switch
      {
         "embedded" => "embedded",
         "compose" when composePath != null => "compose",
         "always-on" when target.StartsWith( "sql", StringComparison.OrdinalIgnoreCase ) => "sql",
         "always-on" when target.StartsWith( "qdrant", StringComparison.OrdinalIgnoreCase ) => "qdrant",
         _ => "none",
      };
   }

   /// <summary>
   /// Marks a compose target's followed cgroups as possibly incomplete after finding or pinning
   /// its containers threw, so its engine CPU per search is null with this reason, never a
   /// figure summed over some of its containers. Why compose only: SQL Server's and Qdrant's
   /// cgroup is added before any step that can throw (or, when the process is not found, no
   /// cgroup of the engine is followed and the figure is null for that reason), while a compose
   /// file's containers are added one by one, so a failure part way leaves some of them out.
   /// </summary>
   /// <param name="pinning">The target's record.</param>
   /// <param name="problem">What failed.</param>
   public static void MarkIncompleteCgroups( TargetPinning pinning, string problem )
   {
      if( pinning.Kind == "compose" )
      {
         pinning.EngineCpuProblem = problem;
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Sets SQL Server's process affinity to the engine CPUs (in SQL Server's own numbering),
   /// then proves it from both sides: the online schedulers, and the kernel affinity of the
   /// sqlservr threads it bound. Why both: the numbering differs from the kernel's, and a wrong
   /// mapping would pin SQL Server onto the client cores without any error.
   /// </summary>
   /// <param name="pinning">The target's record.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task PinSqlServerAsync( TargetPinning pinning, CancellationToken ct )
   {
      IReadOnlyList<int> sqlservr = ProcessInfo.FindNativeByName( _system, "sqlservr" );
      sqlservr.ToList().ForEach( AddGroup );
      string type = ( await _system.SqlQueryAsync( "SELECT affinity_type_desc FROM sys.dm_os_sys_info;", ct ) ).Select( r => r[0] ).FirstOrDefault() ?? "unknown";
      string? ids = type == "MANUAL" ? CpuList.Format( ( await _system.SqlQueryAsync( SQL_ONLINE_IDS, ct ) ).Select( r => int.Parse( r[0], CultureInfo.InvariantCulture ) ) ) : null;
      if( type is not ( "AUTO" or "MANUAL" ) )
      {
         throw new InvalidOperationException( $"SQL Server reports affinity type '{type}', which this tool cannot put back, so it left it alone" );
      }

      _keeper.Record( s => s.SqlServer ??= new SqlAffinityPin( type, ids, pinning.Target ) );
      IReadOnlyList<int> sqlIds = _partition.SqlServerIds( _partition.EngineCpus );
      string wanted = CpuList.Format( sqlIds );
      await _system.SqlQueryAsync( $"ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU = {CpuList.Plain( sqlIds )};", ct );
      pinning.Changes.Add( $"SQL Server affinity {type}{( ids != null ? " " + ids : string.Empty )} -> SQL CPU ids {wanted} (kernel CPUs {_engineCpus})" );
      string online = CpuList.Format( ( await _system.SqlQueryAsync( SQL_ONLINE_IDS, ct ) ).Select( r => int.Parse( r[0], CultureInfo.InvariantCulture ) ) );
      pinning.Verified.Add( $"SQL Server schedulers online on SQL CPU ids {online}; SQL Server binds only the worker threads of those schedulers, so its other threads (the SQLPAL host and I/O threads) stay on every CPU" );
      if( online != wanted )
      {
         pinning.Problems.Add( $"SQL Server schedulers are online on SQL CPU ids {online}, not {wanted}" );
      }

      foreach( int pid in sqlservr )
      {
         Dictionary<string, int> threads = ProcessInfo.ThreadAffinities( _system, pid );
         string all = CpuList.Format( _partition.OnlineCpus );
         List<string> bound = threads.Keys.Where( k => k != all ).ToList();
         pinning.Verified.Add( $"sqlservr pid {pid}: {Describe( threads )}" );
         if( bound.Any( b => b != _engineCpus ) )
         {
            pinning.Problems.Add( $"sqlservr pid {pid} has threads bound to kernel CPUs {string.Join( " / ", bound )}, not {_engineCpus}: SQL Server's CPU numbering did not map as expected" );
         }
      }
   }

   /// <summary>
   /// Pins every thread of the one process with this command name, recording its affinity first.
   /// </summary>
   /// <param name="pinning">The target's record.</param>
   /// <param name="name">Command name, e.g. "qdrant".</param>
   /// <param name="ct">Cancellation.</param>
   private async Task PinProcessAsync( TargetPinning pinning, string name, CancellationToken ct )
   {
      IReadOnlyList<int> pids = ProcessInfo.FindNativeByName( _system, name );
      if( pids.Count != 1 )
      {
         throw new InvalidOperationException( $"expected one {name} process, found {pids.Count}" );
      }

      int pid = pids[0];
      AddGroup( pid );
      string previous = await AffinityAsync( pid, ct );
      long ticks = ProcessInfo.StartTicks( _system, pid ) ?? throw new InvalidOperationException( $"{name} pid {pid} exited" );
      _keeper.Record( s => s.Processes.Add( new ProcessPin( pid, ticks, name, previous, pinning.Target ) ) );
      await SetAffinityAsync( pid, _engineCpus, true, ct );
      pinning.Changes.Add( $"{name} pid {pid}: affinity {previous} -> {_engineCpus}" );
      Dictionary<string, int> threads = ProcessInfo.ThreadAffinities( _system, pid );
      pinning.Verified.Add( $"{name} pid {pid}: {Describe( threads )}" );
      if( threads.Keys.Any( k => k != _engineCpus ) )
      {
         pinning.Problems.Add( $"{name} pid {pid} still has threads outside CPUs {_engineCpus}: {Describe( threads )}" );
      }
   }

   /// <summary>
   /// A process's affinity list, read with taskset.
   /// </summary>
   /// <param name="pid">Process id.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Canonical CPU list.</returns>
   private async Task<string> AffinityAsync( int pid, CancellationToken ct )
   {
      ShellResult result = await _system.RunAsync( "taskset", new[] { "-c", "-p", pid.ToString( CultureInfo.InvariantCulture ) }, ProcessInfo.COMMAND_TIMEOUT, ct );
      ProcessInfo.Require( result, $"read the affinity of pid {pid}" );
      string output = result.Output.Trim();
      return CpuList.Format( CpuList.Parse( output[( output.LastIndexOf( ':' ) + 1 )..] ) );
   }

   /// <summary>
   /// Sets the affinity of every thread of a process.
   /// </summary>
   /// <param name="pid">Process id.</param>
   /// <param name="cpus">CPU list.</param>
   /// <param name="root">True to run taskset through sudo (another user's process).</param>
   /// <param name="ct">Cancellation.</param>
   private async Task SetAffinityAsync( int pid, string cpus, bool root, CancellationToken ct )
   {
      string[] arguments = { "taskset", "-a", "-c", "-p", cpus, pid.ToString( CultureInfo.InvariantCulture ) };
      ShellResult result = root ? await ProcessInfo.SudoAsync( _system, arguments, ct ) : await _system.RunAsync( arguments[0], arguments[1..], ProcessInfo.COMMAND_TIMEOUT, ct );
      ProcessInfo.Require( result, $"set the affinity of pid {pid} to {cpus}" );
   }

   /// <summary>
   /// The affinity of every thread in a container (from its cgroup's cgroup.threads), so the
   /// record covers the engine's own threads and not only the container's first process (which
   /// is Docker's init or a launcher script for several engines). Falls back to the main
   /// process's threads when the cgroup cannot be read.
   /// </summary>
   /// <param name="pid">The container's main process.</param>
   /// <returns>For example "83 thread(s) on CPUs 2-3,6-7 (every thread of the container)".</returns>
   private string ContainerThreads( int pid )
   {
      string? group = ProcessInfo.CgroupFolder( _system, pid );
      string[] tids = ( group != null ? _system.ReadFile( $"{group}/cgroup.threads" ) : null )?
         .Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) ?? Array.Empty<string>();
      var counts = new Dictionary<string, int>( StringComparer.Ordinal );
      foreach( string tid in tids )
      {
         string? line = _system.ReadFile( $"/proc/{tid}/status" )?.Split( '\n' ).FirstOrDefault( l => l.StartsWith( "Cpus_allowed_list:", StringComparison.Ordinal ) );
         if( line != null )
         {
            string list = CpuList.Format( CpuList.Parse( line["Cpus_allowed_list:".Length..].Trim() ) );
            counts[list] = counts.GetValueOrDefault( list ) + 1;
         }
      }

      return counts.Count > 0
         ? $"{Describe( counts )} (every thread of the container)"
         : $"main process {pid}: {Describe( ProcessInfo.ThreadAffinities( _system, pid ) )}";
   }

   /// <summary>
   /// Adds a process's cgroup to the engine's groups.
   /// </summary>
   /// <param name="pid">Process id.</param>
   private void AddGroup( int pid )
   {
      if( ProcessInfo.CgroupFolder( _system, pid ) is string group && !EngineGroups.Contains( group ) )
      {
         EngineGroups.Add( group );
      }
   }

   /// <summary>
   /// Thread affinities as text: "93 threads on 2-3,6-7, 4 on 0-7".
   /// </summary>
   /// <param name="threads">Thread count per CPU list.</param>
   /// <returns>The text.</returns>
   private static string Describe( Dictionary<string, int> threads )
   {
      return threads.Count == 0 ? "no threads read" : string.Join( ", ", threads.OrderByDescending( t => t.Value ).Select( t => $"{t.Value} thread(s) on CPUs {t.Key}" ) );
   }

   #endregion Private Methods
}
