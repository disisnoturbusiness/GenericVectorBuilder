using System.Globalization;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// The CPU idle (C-state) settings as the kernel reports them: the cpuidle driver and governor,
/// intel_idle's max_cstate, and per idle state its name, exit latency, target residency and the
/// CPUs it is disabled on. Written into results.json as conditions.cpuIdle (start of the run) and
/// conditions.cpuIdleAtEnd.
/// Why recorded: a CPU that sleeps in a deep C-state between searches takes longer to wake, so
/// single-searcher latency depends on which states are allowed; the review could not tell two
/// runs' idle settings apart because nothing recorded them. Why only recorded, never changed:
/// the box is shared and its idle settings are its owner's; a benchmark that changed them would
/// measure a machine nobody uses.
/// </summary>
public sealed class CpuIdleSettings
{
   #region Public Methods

   /// <summary>cpuidle driver, e.g. "intel_idle"; null when not readable.</summary>
   public string? Driver { get; set; }

   /// <summary>cpuidle governor, e.g. "menu"; null when not readable.</summary>
   public string? Governor { get; set; }

   /// <summary>/sys/module/intel_idle/parameters/max_cstate, or null when intel_idle is not loaded.</summary>
   public string? IntelIdleMaxCstate { get; set; }

   /// <summary>Each idle state, in the kernel's order (state0 first).</summary>
   public List<IdleStateSetting> States { get; set; } = new();

   /// <summary>What could not be read, or null when everything was.</summary>
   public string? Problem { get; set; }

   /// <summary>
   /// One line for a note: "intel_idle/menu, max_cstate 9; POLL, C1, C1E, C6 enabled; C3 disabled on 0-7 (default disabled)".
   /// </summary>
   /// <returns>The text.</returns>
   public string Describe()
   {
      if( States.Count == 0 )
      {
         return $"not read ({Problem ?? "no cpuidle states"})";
      }

      IEnumerable<string> states = States.Select( s => s.DisabledOn.Length == 0
         ? $"{s.Name} on"
         : $"{s.Name} OFF on CPUs {s.DisabledOn}{( s.DefaultStatus != null ? $" (default {s.DefaultStatus})" : string.Empty )}" );
      return $"driver {Driver ?? "?"}, governor {Governor ?? "?"}, intel_idle max_cstate {IntelIdleMaxCstate ?? "n/a"}; {string.Join( ", ", states )}";
   }

   /// <summary>
   /// The settings that matter for a comparison (not the usage counters), as one string, so two
   /// readings can be compared.
   /// </summary>
   /// <returns>The key.</returns>
   public string Key()
   {
      return $"{Driver}|{Governor}|{IntelIdleMaxCstate}|" + string.Join( ";", States.Select( s => $"{s.Index}:{s.Name}:{s.LatencyUs}:{s.ResidencyUs}:{s.DisabledOn}" ) );
   }

   #endregion Public Methods
}

/// <summary>
/// One idle state across every CPU.
/// </summary>
public sealed class IdleStateSetting
{
   #region Public Methods

   /// <summary>State number (stateN).</summary>
   public int Index { get; set; }

   /// <summary>Name, e.g. "C6".</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>Exit latency in microseconds, as CPU 0 reports it.</summary>
   public int? LatencyUs { get; set; }

   /// <summary>Target residency in microseconds, as CPU 0 reports it.</summary>
   public int? ResidencyUs { get; set; }

   /// <summary>CPUs this state is disabled on, as a CPU list; empty when enabled everywhere.</summary>
   public string DisabledOn { get; set; } = string.Empty;

   /// <summary>The kernel's default for this state ("enabled" or "disabled"), when it says.</summary>
   public string? DefaultStatus { get; set; }

   #endregion Public Methods
}

/// <summary>
/// Reads <see cref="CpuIdleSettings"/> from /sys. Reads only: every file it touches is world
/// readable, so it needs no sudo and changes nothing.
/// </summary>
public static class CpuIdleReader
{
   #region Data Members

   private const string CPUIDLE = "/sys/devices/system/cpu/cpuidle";
   private const string MAX_CSTATE = "/sys/module/intel_idle/parameters/max_cstate";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the idle settings of the given CPUs.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="cpus">CPUs to read (every online CPU in a run).</param>
   /// <returns>The settings; <see cref="CpuIdleSettings.Problem"/> says what was missing.</returns>
   public static CpuIdleSettings Read( IMachineSystem system, IReadOnlyList<int> cpus )
   {
      var settings = new CpuIdleSettings
      {
         Driver = system.ReadFile( CPUIDLE + "/current_driver" )?.Trim(),
         Governor = ( system.ReadFile( CPUIDLE + "/current_governor" ) ?? system.ReadFile( CPUIDLE + "/current_governor_ro" ) )?.Trim(),
         IntelIdleMaxCstate = system.ReadFile( MAX_CSTATE )?.Trim(),
      };
      List<int> indexes = cpus.Count == 0 ? new List<int>() : StateIndexes( system, cpus[0] );
      foreach( int index in indexes )
      {
         settings.States.Add( ReadState( system, cpus, index ) );
      }

      settings.Problem = indexes.Count == 0 ? $"no idle states under /sys/devices/system/cpu/cpu{( cpus.Count > 0 ? cpus[0] : 0 )}/cpuidle" : null;
      return settings;
   }

   /// <summary>
   /// The path of one idle-state file of one CPU.
   /// </summary>
   /// <param name="cpu">Logical CPU.</param>
   /// <param name="state">State number.</param>
   /// <param name="file">File name, e.g. "disable".</param>
   /// <returns>The path.</returns>
   public static string StatePath( int cpu, int state, string file )
   {
      return string.Create( CultureInfo.InvariantCulture, $"/sys/devices/system/cpu/cpu{cpu}/cpuidle/state{state}/{file}" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The idle state numbers a CPU has (state0, state1, ...), ascending.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>State numbers.</returns>
   private static List<int> StateIndexes( IMachineSystem system, int cpu )
   {
      return system.ListDirectory( string.Create( CultureInfo.InvariantCulture, $"/sys/devices/system/cpu/cpu{cpu}/cpuidle" ) )
         .Where( e => e.StartsWith( "state", StringComparison.Ordinal ) )
         .Select( e => int.TryParse( e["state".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int n ) ? n : -1 )
         .Where( n => n >= 0 ).OrderBy( n => n ).ToList();
   }

   /// <summary>
   /// One idle state: name, latency and residency from the first CPU, and every CPU it is
   /// disabled on.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="cpus">CPUs.</param>
   /// <param name="index">State number.</param>
   /// <returns>The setting.</returns>
   private static IdleStateSetting ReadState( IMachineSystem system, IReadOnlyList<int> cpus, int index )
   {
      int first = cpus[0];
      List<int> disabled = cpus.Where( cpu => system.ReadFile( StatePath( cpu, index, "disable" ) )?.Trim() == "1" ).ToList();
      return new IdleStateSetting
      {
         Index = index,
         Name = system.ReadFile( StatePath( first, index, "name" ) )?.Trim() ?? $"state{index}",
         LatencyUs = Number( system.ReadFile( StatePath( first, index, "latency" ) ) ),
         ResidencyUs = Number( system.ReadFile( StatePath( first, index, "residency" ) ) ),
         DisabledOn = CpuList.Format( disabled ),
         DefaultStatus = system.ReadFile( StatePath( first, index, "default_status" ) )?.Trim(),
      };
   }

   /// <summary>
   /// A whole number from a /sys file.
   /// </summary>
   /// <param name="text">The file's text, or null.</param>
   /// <returns>The number, or null.</returns>
   private static int? Number( string? text )
   {
      return int.TryParse( text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value ) ? value : null;
   }

   #endregion Private Methods
}
