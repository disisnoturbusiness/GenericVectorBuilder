using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Puts back what a run changed, record by record, from the run's state: container cpusets,
/// SQL Server's affinity, process affinities, then the governors. Used at the end of a run, at
/// the end of each target (for that target's pins), and at the next start after a crash.
/// Each record is removed from the state file only once it is back, so a record that fails
/// stays for the next attempt and is never silently lost.
/// Why a thing that no longer exists counts as restored: a container that compose removed, or
/// a Qdrant that was restarted, comes back with default settings; there is nothing left to undo.
/// </summary>
public static class MachineRestorer
{
   #region Data Members

   private static readonly Regex CPU_LIST = new( "^[0-9][0-9,\\-]*$", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Restores every record in the state.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="keeper">The state.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Problems; empty when everything is back.</returns>
   public static async Task<List<string>> RestoreAllAsync( IMachineSystem system, MachineStateKeeper keeper, Action<string> log, CancellationToken ct )
   {
      List<string> problems = await RestorePinsAsync( system, keeper, null, log, ct );
      Dictionary<int, string> governors = keeper.Read( s => new Dictionary<int, string>( s.Governors ) );
      problems.AddRange( await GovernorControl.RestoreAsync( system, keeper, ct ) );
      List<int> back = governors.Keys.Where( cpu => keeper.Read( s => !s.Governors.ContainsKey( cpu ) ) ).ToList();
      if( back.Count > 0 )
      {
         log( $"  machine: governor back to {string.Join( ", ", back.Select( c => governors[c] ).Distinct() )} on CPUs {CpuList.Format( back )}" );
      }

      return problems;
   }

   /// <summary>
   /// Restores the pins (containers, SQL Server, processes), all of them or one target's.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="keeper">The state.</param>
   /// <param name="target">Only this target's pins, or null for all.</param>
   /// <param name="log">Progress output (one line per record put back).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Problems; empty when everything is back.</returns>
   public static async Task<List<string>> RestorePinsAsync( IMachineSystem system, MachineStateKeeper keeper, string? target, Action<string> log, CancellationToken ct )
   {
      var problems = new List<string>();
      foreach( ContainerPin pin in keeper.Read( s => s.Containers.Where( c => target == null || c.Target == target ).ToList() ) )
      {
         await AttemptAsync( problems, log, $"container {pin.Name} cpuset", () => RestoreContainerAsync( system, pin, ct ), () => keeper.Record( s => s.Containers.Remove( pin ) ) );
      }

      if( keeper.Read( s => s.SqlServer ) is SqlAffinityPin sql && ( target == null || sql.Target == target ) )
      {
         await AttemptAsync( problems, log, "SQL Server affinity", () => RestoreSqlAsync( system, sql, ct ), () => keeper.Record( s => s.SqlServer = null ) );
      }

      foreach( ProcessPin pin in keeper.Read( s => s.Processes.Where( p => target == null || p.Target == target ).ToList() ) )
      {
         await AttemptAsync( problems, log, $"{pin.Name} (pid {pin.Pid}) affinity", () => RestoreProcessAsync( system, pin, ct ), () => keeper.Record( s => s.Processes.Remove( pin ) ) );
      }

      return problems;
   }

   /// <summary>
   /// Puts a container's cpuset back. Docker cannot clear the field once set ("docker update
   /// --cpuset-cpus ''" changes nothing; measured 2026-10-04), so a container that had no limit
   /// gets the list of every online CPU, which is the same set of CPUs.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="pin">The record.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was done.</returns>
   /// <exception cref="InvalidOperationException">Docker failed for a reason other than the container being gone.</exception>
   public static async Task<string> RestoreContainerAsync( IMachineSystem system, ContainerPin pin, CancellationToken ct )
   {
      ShellResult inspect = await ProcessInfo.SudoAsync( system, new[] { "docker", "inspect", "-f", "{{.HostConfig.CpusetCpus}}", pin.Id }, ct );
      if( IsGone( inspect ) )
      {
         return $"container {pin.Name} no longer exists; nothing to put back";
      }

      ProcessInfo.Require( inspect, $"read the cpuset of container {pin.Name}" );
      string wanted = pin.PreviousCpuset.Length > 0 ? pin.PreviousCpuset : OnlineCpus( system );
      if( !CPU_LIST.IsMatch( wanted ) )
      {
         throw new InvalidOperationException( $"the recorded cpuset '{wanted}' of {pin.Name} is not a CPU list" );
      }

      string current = inspect.Output.Trim();
      if( current.Length == 0 || !CpuList.Same( current, wanted ) )
      {
         ShellResult update = await ProcessInfo.SudoAsync( system, new[] { "docker", "update", "--cpuset-cpus", wanted, pin.Id }, ct );
         if( IsGone( update ) )
         {
            return $"container {pin.Name} no longer exists; nothing to put back";
         }

         ProcessInfo.Require( update, $"put the cpuset of container {pin.Name} back to {wanted}" );
      }

      return pin.PreviousCpuset.Length > 0
         ? $"container {pin.Name} cpuset back to {wanted}"
         : $"container {pin.Name} cpuset back to every CPU ({wanted}); it had no limit before, and docker cannot clear the field, so it now names all CPUs explicitly";
   }

   /// <summary>
   /// Puts SQL Server's process affinity back to AUTO or to the recorded CPU ids.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="pin">The record.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was done.</returns>
   /// <exception cref="InvalidOperationException">The recorded value is not an affinity.</exception>
   public static async Task<string> RestoreSqlAsync( IMachineSystem system, SqlAffinityPin pin, CancellationToken ct )
   {
      if( pin.PreviousType != "AUTO" && !CPU_LIST.IsMatch( pin.PreviousCpuIds ?? string.Empty ) )
      {
         throw new InvalidOperationException( $"the recorded SQL Server affinity '{pin.PreviousType} {pin.PreviousCpuIds}' is not one this tool can set" );
      }

      string value = pin.PreviousType == "AUTO" ? "AUTO" : CpuList.Plain( CpuList.Parse( pin.PreviousCpuIds! ) );

      await system.SqlQueryAsync( $"ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU = {value};", ct );
      IReadOnlyList<string[]> type = await system.SqlQueryAsync( "SELECT affinity_type_desc FROM sys.dm_os_sys_info;", ct );
      string now = type.Count > 0 ? type[0][0] : "unknown";
      if( now != pin.PreviousType )
      {
         throw new InvalidOperationException( $"SQL Server reports affinity {now} after setting it back to {value}" );
      }

      return $"SQL Server process affinity back to {value}";
   }

   /// <summary>
   /// Puts a process's affinity back on every one of its threads, if it is still the same process.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="pin">The record.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was done.</returns>
   /// <exception cref="InvalidOperationException">taskset failed or the recorded value is not a CPU list.</exception>
   public static async Task<string> RestoreProcessAsync( IMachineSystem system, ProcessPin pin, CancellationToken ct )
   {
      if( !ProcessInfo.IsSameProcess( system, pin.Pid, pin.StartTicks ) )
      {
         return $"{pin.Name} pid {pin.Pid} no longer runs (restarted or stopped), so it already has default affinity; nothing to put back";
      }

      if( !CPU_LIST.IsMatch( pin.PreviousCpus ) )
      {
         throw new InvalidOperationException( $"the recorded affinity '{pin.PreviousCpus}' is not a CPU list" );
      }

      ShellResult result = await ProcessInfo.SudoAsync( system, new[] { "taskset", "-a", "-c", "-p", pin.PreviousCpus, pin.Pid.ToString( System.Globalization.CultureInfo.InvariantCulture ) }, ct );
      ProcessInfo.Require( result, $"put the affinity of {pin.Name} (pid {pin.Pid}) back to {pin.PreviousCpus}" );
      return $"{pin.Name} (pid {pin.Pid}) affinity back to {pin.PreviousCpus} on every thread";
   }

   /// <summary>
   /// Every online CPU as the kernel lists it (/sys/devices/system/cpu/online).
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <returns>The list, e.g. "0-7".</returns>
   /// <exception cref="InvalidOperationException">The file is missing.</exception>
   public static string OnlineCpus( IMachineSystem system )
   {
      string? online = system.ReadFile( "/sys/devices/system/cpu/online" )?.Trim();
      return string.IsNullOrEmpty( online ) ? throw new InvalidOperationException( "cannot read /sys/devices/system/cpu/online" ) : CpuList.Format( CpuList.Parse( online ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs one restore; on success removes its record and logs what was done, on failure adds a
   /// problem and keeps the record.
   /// </summary>
   /// <param name="problems">Receives a failure.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="what">What is being restored, for the problem text.</param>
   /// <param name="restore">The restore; returns what it did.</param>
   /// <param name="forget">Removes the record.</param>
   private static async Task AttemptAsync( List<string> problems, Action<string> log, string what, Func<Task<string>> restore, Action forget )
   {
      try
      {
         log( "  machine: " + await restore() );
         forget();
      }
      catch( Exception ex ) when( ex is InvalidOperationException or TimeoutException or IOException or FormatException or Microsoft.Data.SqlClient.SqlException )
      {
         problems.Add( $"{what}: {ex.Message}" );
      }
   }

   /// <summary>
   /// True when docker said the container does not exist.
   /// </summary>
   /// <param name="result">Docker's result.</param>
   /// <returns>True for "No such container/object".</returns>
   private static bool IsGone( ShellResult result )
   {
      return result.ExitCode != 0 && result.Error.Contains( "No such", StringComparison.OrdinalIgnoreCase );
   }

   #endregion Private Methods
}
