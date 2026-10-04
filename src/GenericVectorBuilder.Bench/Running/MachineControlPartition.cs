using System.Globalization;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// One logical CPU as "lscpu -e" lists it.
/// </summary>
/// <param name="Cpu">Logical CPU number, as the kernel numbers it.</param>
/// <param name="Socket">Physical package, or -1 when lscpu printed "-".</param>
/// <param name="Core">Physical core inside the package, or -1 when lscpu printed "-".</param>
/// <param name="Online">False when lscpu says the CPU is offline.</param>
public sealed record LogicalCpu( int Cpu, int Socket, int Core, bool Online );

/// <summary>
/// Splits the box's CPUs between the engine under test and the benchmark client by physical
/// core: the engine gets the upper half of the cores with both hyperthreads of each, the client
/// gets the lower half.
/// Why by physical core: the two hyperthreads of a core share its execution units and caches,
/// so a client thread on the sibling of an engine thread slows the engine directly, and the
/// lscpu numbering on this box (CPU 0 and 4 are one core) puts siblings far apart.
/// Why the engine gets the upper cores: CPU 0 takes most of the kernel's housekeeping (timer and
/// device interrupts), and the engine is what is being measured.
/// Why the client gets cores of its own at all: the review measured the client using 0.37 to
/// 1.0 ms of CPU per search, so with 8 searchers it competed with the engine for the same cores.
/// </summary>
public sealed class CpuPartition
{
   #region Data Members

   private readonly List<IReadOnlyList<int>> _cores;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a partition from cores already ordered by package and core.
   /// </summary>
   /// <param name="cores">Each core's logical CPUs (ascending), cores in package and core order.</param>
   /// <param name="problem">Why no split was made, or null when the CPUs were split.</param>
   private CpuPartition( List<IReadOnlyList<int>> cores, string? problem )
   {
      _cores = cores;
      Problem = problem;
      int clientCores = problem == null ? cores.Count / 2 : 0;
      ClientCpus = problem == null ? cores.Take( clientCores ).SelectMany( c => c ).OrderBy( c => c ).ToList() : Array.Empty<int>();
      EngineCpus = problem == null ? cores.Skip( clientCores ).SelectMany( c => c ).OrderBy( c => c ).ToList() : Array.Empty<int>();
      OnlineCpus = cores.SelectMany( c => c ).OrderBy( c => c ).ToList();
      ThreadSiblings = cores.Select( c => string.Join( ",", c ) ).ToList();
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Logical CPUs the engine under test may use (empty when not split).</summary>
   public IReadOnlyList<int> EngineCpus { get; }

   /// <summary>Logical CPUs the benchmark client may use (empty when not split).</summary>
   public IReadOnlyList<int> ClientCpus { get; }

   /// <summary>Every online logical CPU.</summary>
   public IReadOnlyList<int> OnlineCpus { get; }

   /// <summary>Hyperthread siblings per physical core, e.g. "0,4", in package and core order.</summary>
   public IReadOnlyList<string> ThreadSiblings { get; }

   /// <summary>Why the CPUs were not split, or null when they were.</summary>
   public string? Problem { get; }

   /// <summary>True when the CPUs were split between engine and client.</summary>
   public bool IsSplit => Problem == null;

   /// <summary>
   /// Reads the table "lscpu -e" prints. Columns are found by their header names, so the
   /// default column set and an explicit "-e=CPU,SOCKET,CORE,ONLINE" both work. A missing
   /// SOCKET column means one package; a missing ONLINE column means every CPU is online.
   /// </summary>
   /// <param name="text">lscpu output.</param>
   /// <returns>One entry per row.</returns>
   /// <exception cref="FormatException">There is no header with CPU and CORE, or a row is not numeric where it must be.</exception>
   public static IReadOnlyList<LogicalCpu> ParseLscpu( string text )
   {
      string[] lines = text.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
      string[] header = lines.Length > 0 ? lines[0].Split( ' ', StringSplitOptions.RemoveEmptyEntries ) : Array.Empty<string>();
      int cpu = Array.IndexOf( header, "CPU" );
      int core = Array.IndexOf( header, "CORE" );
      int socket = Array.IndexOf( header, "SOCKET" );
      int online = Array.IndexOf( header, "ONLINE" );
      if( cpu < 0 || core < 0 )
      {
         throw new FormatException( $"lscpu -e output has no CPU and CORE columns; header was '{( lines.Length > 0 ? lines[0] : string.Empty )}'." );
      }

      var cpus = new List<LogicalCpu>();
      foreach( string line in lines.Skip( 1 ) )
      {
         string[] cells = line.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
         cpus.Add( new LogicalCpu(
            int.Parse( cells[cpu], CultureInfo.InvariantCulture ),
            socket < 0 ? 0 : Number( cells, socket ),
            Number( cells, core ),
            online < 0 || ( online < cells.Length && cells[online] == "yes" ) ) );
      }

      return cpus;
   }

   /// <summary>
   /// Chooses the split: online CPUs grouped by package and core, the lower half of the cores
   /// (rounded down) to the client, the rest to the engine. Fewer than two cores cannot be
   /// split; the result then says why and leaves both lists empty.
   /// </summary>
   /// <param name="cpus">CPUs from <see cref="ParseLscpu"/>.</param>
   /// <returns>The partition.</returns>
   public static CpuPartition Choose( IReadOnlyList<LogicalCpu> cpus )
   {
      List<IReadOnlyList<int>> cores = cpus.Where( c => c.Online && c.Core >= 0 && c.Socket >= 0 )
         .GroupBy( c => ( c.Socket, c.Core ) )
         .OrderBy( g => g.Key.Socket ).ThenBy( g => g.Key.Core )
         .Select( g => (IReadOnlyList<int>)g.Select( c => c.Cpu ).OrderBy( c => c ).ToList() )
         .ToList();
      string? problem = cores.Count switch
      {
         0 => "lscpu listed no online CPU with a core number",
         1 => "only one physical core is online, so there is nothing to split between engine and client",
         _ => null,
      };
      return new CpuPartition( cores, problem );
   }

   /// <summary>
   /// The CPU numbers SQL Server uses for a set of the kernel's logical CPUs. SQL Server on
   /// Linux numbers CPUs core by core with the hyperthread siblings next to each other (on this
   /// box its CPU 1 is the kernel's CPU 4), not the way the kernel does. Measured on linus7795
   /// 2026-10-04: "PROCESS AFFINITY CPU = 1" bound its worker threads to kernel CPU 4, "4,5" to
   /// kernel 2 and 6, "2,6" to kernel 1 and 3.
   /// </summary>
   /// <param name="kernelCpus">Kernel logical CPU numbers.</param>
   /// <returns>SQL Server's CPU ids for the same CPUs, ascending.</returns>
   /// <exception cref="ArgumentException">A CPU is not online.</exception>
   public IReadOnlyList<int> SqlServerIds( IEnumerable<int> kernelCpus )
   {
      List<int> order = _cores.SelectMany( c => c ).ToList();
      return kernelCpus.Select( cpu => order.IndexOf( cpu ) is int id && id >= 0 ? id : throw new ArgumentException( $"CPU {cpu} is not an online CPU." ) )
         .OrderBy( id => id ).ToList();
   }

   /// <summary>
   /// One line for logs and notes.
   /// </summary>
   /// <returns>e.g. "engine CPUs 2-3,6-7 (cores 2,6 and 3,7), client CPUs 0-1,4-5 (cores 0,4 and 1,5)".</returns>
   public string Describe()
   {
      if( !IsSplit )
      {
         return $"not split: {Problem}";
      }

      int clientCores = _cores.Count / 2;
      string Cores( IEnumerable<IReadOnlyList<int>> cores ) => string.Join( " and ", cores.Select( c => string.Join( ",", c ) ) );
      return $"engine CPUs {CpuList.Format( EngineCpus )} (cores {Cores( _cores.Skip( clientCores ) )}), client CPUs {CpuList.Format( ClientCpus )} (cores {Cores( _cores.Take( clientCores ) )})";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A numeric cell, or -1 for "-" (lscpu's mark for an offline CPU) or a missing cell.
   /// </summary>
   /// <param name="cells">Row cells.</param>
   /// <param name="index">Column index.</param>
   /// <returns>The number, or -1.</returns>
   private static int Number( string[] cells, int index )
   {
      return index < cells.Length && int.TryParse( cells[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value ) ? value : -1;
   }

   #endregion Private Methods
}

/// <summary>
/// CPU lists in the kernel's text form ("0-3,6"), as taskset, docker and /sys print them.
/// Why one canonical form: lists are compared between the state file, docker, taskset and
/// /proc, and "2,3,6,7" must equal "2-3,6-7".
/// </summary>
public static class CpuList
{
   #region Public Methods

   /// <summary>
   /// Formats CPUs as ascending ranges: 0,1,2,5 becomes "0-2,5".
   /// </summary>
   /// <param name="cpus">CPU numbers, any order, duplicates allowed.</param>
   /// <returns>The canonical text; empty for no CPUs.</returns>
   public static string Format( IEnumerable<int> cpus )
   {
      List<int> sorted = cpus.Distinct().OrderBy( c => c ).ToList();
      var parts = new List<string>();
      for( int i = 0; i < sorted.Count; )
      {
         int j = i;
         while( j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1 )
         {
            j++;
         }

         parts.Add( j == i ? sorted[i].ToString( CultureInfo.InvariantCulture ) : $"{sorted[i]}-{sorted[j]}" );
         i = j + 1;
      }

      return string.Join( ",", parts );
   }

   /// <summary>
   /// Parses "0-3,6" (spaces allowed) into CPU numbers.
   /// </summary>
   /// <param name="text">The list.</param>
   /// <returns>Ascending distinct CPU numbers; empty for empty text.</returns>
   /// <exception cref="FormatException">Not a CPU list.</exception>
   public static IReadOnlyList<int> Parse( string text )
   {
      var cpus = new SortedSet<int>();
      foreach( string part in text.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
      {
         string[] ends = part.Split( '-' );
         if( ends.Length > 2 || !ends.All( e => int.TryParse( e, NumberStyles.None, CultureInfo.InvariantCulture, out _ ) ) )
         {
            throw new FormatException( $"'{text}' is not a CPU list like 0-3,6." );
         }

         int first = int.Parse( ends[0], CultureInfo.InvariantCulture );
         int last = int.Parse( ends[^1], CultureInfo.InvariantCulture );
         for( int cpu = first; cpu <= last; cpu++ )
         {
            cpus.Add( cpu );
         }
      }

      return cpus.ToList();
   }

   /// <summary>
   /// CPUs as a plain comma list, "4,5,6,7", the form SQL Server's ALTER SERVER CONFIGURATION
   /// accepts (it rejects "4-7"; its own range form is "4 TO 7"). Found by the live run of
   /// 2026-10-04, where "CPU = 4-7" failed with "Incorrect syntax near '-'".
   /// </summary>
   /// <param name="cpus">CPU numbers.</param>
   /// <returns>Ascending distinct numbers joined by commas.</returns>
   public static string Plain( IEnumerable<int> cpus )
   {
      return string.Join( ",", cpus.Distinct().OrderBy( c => c ) );
   }

   /// <summary>
   /// True when two lists name the same CPUs.
   /// </summary>
   /// <param name="a">One list.</param>
   /// <param name="b">The other.</param>
   /// <returns>True when equal as sets.</returns>
   public static bool Same( string a, string b )
   {
      return Parse( a ).SequenceEqual( Parse( b ) );
   }

   #endregion Public Methods
}
