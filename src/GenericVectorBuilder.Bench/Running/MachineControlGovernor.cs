using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Reads and sets the CPU frequency governor of every CPU.
/// Why "performance" for a timed run: under schedutil the clock follows the load, so a single
/// searcher ran at 2.1 to 2.5 GHz while eight searchers ran at 3.49 GHz (review probe/gov,
/// 2026-10-04). That nearly halved QPS at one searcher and inflated p50, so the clock, not the
/// engine, decided the single-searcher numbers. "performance" holds every CPU at its top clock.
/// </summary>
public static class GovernorControl
{
   #region Data Members

   /// <summary>The governor every CPU runs under during a timed run.</summary>
   public const string TARGET = "performance";

   private static readonly Regex GOVERNOR_NAME = new( "^[a-z_]{1,32}$", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The sysfs file holding a CPU's governor.
   /// </summary>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>The path.</returns>
   public static string GovernorPath( int cpu )
   {
      return $"/sys/devices/system/cpu/cpu{cpu}/cpufreq/scaling_governor";
   }

   /// <summary>
   /// The sysfs file holding a CPU's current clock in kHz.
   /// </summary>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>The path.</returns>
   public static string FrequencyPath( int cpu )
   {
      return $"/sys/devices/system/cpu/cpu{cpu}/cpufreq/scaling_cur_freq";
   }

   /// <summary>
   /// Reads each CPU's governor.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="cpus">CPUs to read.</param>
   /// <returns>Governor by CPU; null where the CPU has no frequency scaling.</returns>
   public static Dictionary<int, string?> Read( IMachineSystem system, IEnumerable<int> cpus )
   {
      return cpus.ToDictionary( cpu => cpu, cpu => system.ReadFile( GovernorPath( cpu ) )?.Trim() );
   }

   /// <summary>
   /// One text for a set of governors: the name when every CPU has the same one, else each
   /// CPU's. Why the full list when they differ: a run where one CPU stayed on schedutil is
   /// not a performance run, and the text must not hide which CPU it was.
   /// </summary>
   /// <param name="governors">Governor by CPU.</param>
   /// <returns>"performance", "mixed: cpu0 performance, cpu1 schedutil", or "unknown".</returns>
   public static string Summarize( IReadOnlyDictionary<int, string?> governors )
   {
      List<string?> distinct = governors.Values.Distinct().ToList();
      if( governors.Count == 0 || distinct.Contains( null ) && distinct.Count == 1 )
      {
         return "unknown";
      }

      return distinct.Count == 1 ? distinct[0]! : "mixed: " + string.Join( ", ", governors.OrderBy( g => g.Key ).Select( g => $"cpu{g.Key} {g.Value ?? "unknown"}" ) );
   }

   /// <summary>
   /// Sets every CPU to <see cref="TARGET"/>, recording each CPU's governor in the state file
   /// first, then reads them back.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="keeper">The run's state (written before the change).</param>
   /// <param name="cpus">Every online CPU.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The governors before the change.</returns>
   /// <exception cref="InvalidOperationException">A CPU has no governor, a write failed, or a CPU did not take the change.</exception>
   public static async Task<Dictionary<int, string?>> ApplyAsync( IMachineSystem system, MachineStateKeeper keeper, IReadOnlyList<int> cpus, CancellationToken ct )
   {
      Dictionary<int, string?> before = Read( system, cpus );
      List<int> missing = before.Where( g => g.Value == null ).Select( g => g.Key ).ToList();
      if( missing.Count > 0 )
      {
         throw new InvalidOperationException( $"CPU(s) {CpuList.Format( missing )} have no {GovernorPath( missing[0] )}, so the governor cannot be set. Run with --no-machine-control to measure anyway (the results will say so)." );
      }

      keeper.Record( s =>
      {
         foreach( KeyValuePair<int, string?> g in before )
         {
            s.Governors.TryAdd( g.Key, g.Value! );
         }
      } );
      foreach( int cpu in cpus.Where( c => before[c] != TARGET ) )
      {
         await ProcessInfo.WriteAsRootAsync( system, GovernorPath( cpu ), TARGET, ct );
      }

      Dictionary<int, string?> after = Read( system, cpus );
      if( after.Any( g => g.Value != TARGET ) )
      {
         throw new InvalidOperationException( $"The governor did not change to {TARGET}: {Summarize( after )}." );
      }

      return before;
   }

   /// <summary>
   /// Puts each recorded CPU back on its recorded governor and removes the record of each CPU
   /// that is back. A CPU that fails stays in the state file.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="keeper">The state holding the recorded governors.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Problems; empty when every CPU is back.</returns>
   public static async Task<List<string>> RestoreAsync( IMachineSystem system, MachineStateKeeper keeper, CancellationToken ct )
   {
      var problems = new List<string>();
      foreach( KeyValuePair<int, string> recorded in keeper.Read( s => s.Governors.OrderBy( g => g.Key ).ToList() ) )
      {
         try
         {
            if( !GOVERNOR_NAME.IsMatch( recorded.Value ) )
            {
               throw new InvalidOperationException( $"the recorded governor '{recorded.Value}' is not a governor name" );
            }

            if( system.ReadFile( GovernorPath( recorded.Key ) )?.Trim() != recorded.Value )
            {
               await ProcessInfo.WriteAsRootAsync( system, GovernorPath( recorded.Key ), recorded.Value, ct );
            }

            string? now = system.ReadFile( GovernorPath( recorded.Key ) )?.Trim();
            if( now != recorded.Value )
            {
               throw new InvalidOperationException( $"it reads {now ?? "nothing"} after the write" );
            }

            keeper.Record( s => s.Governors.Remove( recorded.Key ) );
         }
         catch( Exception ex ) when( ex is InvalidOperationException or TimeoutException or IOException or FormatException )
         {
            problems.Add( $"CPU {recorded.Key} governor back to {recorded.Value}: {ex.Message}" );
         }
      }

      return problems;
   }

   #endregion Public Methods
}
