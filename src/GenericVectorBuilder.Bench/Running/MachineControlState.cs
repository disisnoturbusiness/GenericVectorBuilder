using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Everything a benchmark run has changed on the machine and not yet put back: the governor
/// each CPU had, the cpuset each pinned container had, SQL Server's affinity, and the affinity
/// of each pinned process. It is written to the state file BEFORE each change is made.
/// Why before: a run killed between recording and changing leaves a record of a change that
/// never happened, and putting back a value that is already there is harmless; the other
/// order could leave a change with no record, which nothing would ever undo.
/// </summary>
public sealed class MachineState
{
   #region Public Methods

   /// <summary>Process id of the run that owns this state.</summary>
   public int OwnerPid { get; set; }

   /// <summary>Start time of the owner in clock ticks since boot (/proc/PID/stat field 22), so a reused process id is not mistaken for the owner.</summary>
   public long OwnerStartTicks { get; set; }

   /// <summary>When the owner started controlling the machine (UTC, ISO 8601).</summary>
   public string StartedUtc { get; set; } = string.Empty;

   /// <summary>Governor each CPU had before the run changed it, by CPU number.</summary>
   public Dictionary<int, string> Governors { get; set; } = new();

   /// <summary>Containers whose cpuset the run changed.</summary>
   public List<ContainerPin> Containers { get; set; } = new();

   /// <summary>SQL Server's affinity before the run changed it, or null when untouched.</summary>
   public SqlAffinityPin? SqlServer { get; set; }

   /// <summary>Processes whose CPU affinity the run changed (Qdrant).</summary>
   public List<ProcessPin> Processes { get; set; } = new();

   /// <summary>Compose engines the run started with their containers created on the engine CPUs (recorded before the start).</summary>
   public List<PinnedStart> PinnedStarts { get; set; } = new();

   /// <summary>True when nothing needs putting back.</summary>
   [JsonIgnore]
   public bool IsEmpty => Governors.Count == 0 && Containers.Count == 0 && SqlServer == null && Processes.Count == 0 && PinnedStarts.Count == 0;

   /// <summary>
   /// What the state holds, for messages.
   /// </summary>
   /// <returns>e.g. "governor of 8 CPUs, 1 container, SQL Server affinity".</returns>
   public string Describe()
   {
      var parts = new List<string>();
      if( Governors.Count > 0 )
      {
         parts.Add( $"governor of {Governors.Count} CPUs ({string.Join( ", ", Governors.Values.Distinct() )})" );
      }

      if( Containers.Count > 0 )
      {
         parts.Add( $"cpuset of {Containers.Count} container(s) ({string.Join( ", ", Containers.Select( c => c.Name ) )})" );
      }

      if( SqlServer != null )
      {
         parts.Add( $"SQL Server affinity (was {SqlServer.PreviousType}{( SqlServer.PreviousCpuIds != null ? " " + SqlServer.PreviousCpuIds : string.Empty )})" );
      }

      if( Processes.Count > 0 )
      {
         parts.Add( $"affinity of {string.Join( ", ", Processes.Select( p => $"{p.Name} (pid {p.Pid})" ) )}" );
      }

      if( PinnedStarts.Count > 0 )
      {
         parts.Add( $"cpuset of the containers created pinned from {string.Join( ", ", PinnedStarts.Select( p => System.IO.Path.GetFileName( p.ComposePath ) ) )}" );
      }

      return parts.Count == 0 ? "nothing" : string.Join( "; ", parts );
   }

   #endregion Public Methods
}

/// <summary>
/// A container whose cpuset the run changed.
/// </summary>
/// <param name="Id">Container id.</param>
/// <param name="Name">Container name, for messages.</param>
/// <param name="PreviousCpuset">HostConfig.CpusetCpus before the change; empty means "no limit".</param>
/// <param name="Target">Benchmark target it was pinned for.</param>
public sealed record ContainerPin( string Id, string Name, string PreviousCpuset, string Target );

/// <summary>
/// A compose engine run-all started with its containers to be created already held to the
/// engine CPUs. Recorded before the start: a run killed between the start
/// and its own "compose down" leaves containers held to half the machine, and the next start
/// (or restore-machine) gives any container of this compose file still on exactly these CPUs
/// every CPU back.
/// </summary>
/// <param name="ComposePath">Compose file.</param>
/// <param name="Cpuset">The CPUs the containers were created on, e.g. "2-3,6-7".</param>
/// <param name="Target">Benchmark target it was started for.</param>
public sealed record PinnedStart( string ComposePath, string Cpuset, string Target );

/// <summary>
/// SQL Server's process affinity before the run changed it.
/// </summary>
/// <param name="PreviousType">"AUTO" or "MANUAL" (sys.dm_os_sys_info.affinity_type_desc).</param>
/// <param name="PreviousCpuIds">For MANUAL, SQL Server's CPU ids that were online, e.g. "0-3"; null for AUTO.</param>
/// <param name="Target">Benchmark target it was pinned for.</param>
public sealed record SqlAffinityPin( string PreviousType, string? PreviousCpuIds, string Target );

/// <summary>
/// A process whose CPU affinity the run changed.
/// </summary>
/// <param name="Pid">Process id.</param>
/// <param name="StartTicks">Its start time in clock ticks since boot, so a reused id is not touched.</param>
/// <param name="Name">Its command name (/proc/PID/comm).</param>
/// <param name="PreviousCpus">Its affinity list before the change, e.g. "0-7".</param>
/// <param name="Target">Benchmark target it was pinned for.</param>
public sealed record ProcessPin( int Pid, long StartTicks, string Name, string PreviousCpus, string Target );

/// <summary>
/// Reads and writes the state file. Why a file: Ctrl+C and a normal end restore the machine in
/// a finally block, but a kill -9, a crash or a power cut skips every finally; the next start
/// reads this file and restores the machine before it changes anything.
/// </summary>
public sealed class MachineStateStore
{
   #region Data Members

   /// <summary>Where the state lives by default: on the persistent disk, never under /tmp, which a reboot clears.</summary>
   public const string DEFAULT_PATH = "/home/dan/gvb-work/bench-machine-state.json";

   private static readonly JsonSerializerOptions JSON = new()
   {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
   };

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the store.
   /// </summary>
   /// <param name="path">State file path.</param>
   public MachineStateStore( string path )
   {
      Path = System.IO.Path.GetFullPath( path );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Full path of the state file.</summary>
   public string Path { get; }

   /// <summary>
   /// Reads the state file.
   /// </summary>
   /// <returns>The state, or null when there is no file.</returns>
   /// <exception cref="InvalidDataException">The file exists but cannot be read as a state; the message names the file.</exception>
   public MachineState? Load()
   {
      if( !File.Exists( Path ) )
      {
         return null;
      }

      try
      {
         return JsonSerializer.Deserialize<MachineState>( File.ReadAllText( Path ), JSON )
            ?? throw new InvalidDataException( $"The machine state file {Path} is empty." );
      }
      catch( JsonException ex )
      {
         throw new InvalidDataException( $"The machine state file {Path} is not readable ({ex.Message}). Put the machine back by hand (governors, container cpusets, SQL Server affinity, Qdrant affinity), then delete the file.", ex );
      }
   }

   /// <summary>
   /// Writes the state so that a crash at any moment leaves either the old file or the new
   /// one, never half of one: write a temporary file, flush it to the disk, rename it over.
   /// </summary>
   /// <param name="state">The state.</param>
   public void Save( MachineState state )
   {
      Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path )! );
      string temporary = Path + ".tmp";
      using( var stream = new FileStream( temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough ) )
      {
         JsonSerializer.Serialize( stream, state, JSON );
         stream.Flush( true );
      }

      File.Move( temporary, Path, true );
   }

   /// <summary>
   /// Removes the state file (nothing left to restore).
   /// </summary>
   public void Delete()
   {
      File.Delete( Path );
   }

   #endregion Public Methods
}

/// <summary>
/// The live state of one run and its file, changed only through <see cref="Record"/>, which
/// saves after every change. Thread safe: pins and restores can come from different tasks.
/// </summary>
public sealed class MachineStateKeeper
{
   #region Data Members

   private readonly object _lock = new();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the keeper around a state and its store.
   /// </summary>
   /// <param name="store">Where it is saved.</param>
   /// <param name="state">The state.</param>
   public MachineStateKeeper( MachineStateStore store, MachineState state )
   {
      Store = store;
      State = state;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The file.</summary>
   public MachineStateStore Store { get; }

   /// <summary>The state. Read it only under <see cref="Read{T}"/> when other tasks may change it.</summary>
   public MachineState State { get; }

   /// <summary>
   /// Changes the state and saves it before returning.
   /// </summary>
   /// <param name="change">The change.</param>
   public void Record( Action<MachineState> change )
   {
      lock( _lock )
      {
         change( State );
         Store.Save( State );
      }
   }

   /// <summary>
   /// Reads from the state under the lock.
   /// </summary>
   /// <typeparam name="T">Result type.</typeparam>
   /// <param name="read">The read.</param>
   /// <returns>Its result.</returns>
   public T Read<T>( Func<MachineState, T> read )
   {
      lock( _lock )
      {
         return read( State );
      }
   }

   /// <summary>
   /// Deletes the file when nothing is left to restore, else saves what is left.
   /// </summary>
   /// <returns>True when the file was deleted.</returns>
   public bool Finish()
   {
      lock( _lock )
      {
         if( State.IsEmpty )
         {
            Store.Delete();
            return true;
         }

         Store.Save( State );
         return false;
      }
   }

   /// <summary>
   /// A new state owned by this process.
   /// </summary>
   /// <param name="system">The machine (own process id and start time).</param>
   /// <returns>The empty state.</returns>
   public static MachineState NewState( IMachineSystem system )
   {
      return new MachineState
      {
         OwnerPid = system.ProcessId,
         OwnerStartTicks = ProcessInfo.StartTicks( system, system.ProcessId ) ?? 0,
         StartedUtc = DateTime.UtcNow.ToString( "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture ),
      };
   }

   #endregion Public Methods
}
