using System.Globalization;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// CPU lists as the kernel writes them ("0-3,8,10-11"): parsing, one canonical spelling, and the
/// overlap test that decides whether the benchmark client and the engines shared cores.
/// Why hyperthread siblings are counted: on a 4 core, 8 thread box CPUs 0 and 4 are one core, so
/// a client on CPU 0 and an engine on CPU 4 still compete for the same execution units; a
/// partition has to be checked per physical core, not per logical CPU.
/// </summary>
public static class CpuSet
{
   #region Data Members

   /// <summary>Largest CPU number accepted; a bigger one means the text is not a CPU list.</summary>
   public const int MAX_CPU = 4095;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Parses a CPU list.
   /// </summary>
   /// <param name="text">E.g. "0-3,8", "0,4" or "5".</param>
   /// <returns>The CPU numbers, or null when the text is not a CPU list.</returns>
   public static SortedSet<int>? TryParse( string? text )
   {
      if( string.IsNullOrWhiteSpace( text ) )
      {
         return null;
      }

      var cpus = new SortedSet<int>();
      foreach( string part in text.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
      {
         string[] ends = part.Split( '-', StringSplitOptions.TrimEntries );
         if( ends.Length is < 1 or > 2 || !TryNumber( ends[0], out int first ) || !TryNumber( ends[^1], out int last ) || last < first )
         {
            return null;
         }

         for( int cpu = first; cpu <= last; cpu++ )
         {
            cpus.Add( cpu );
         }
      }

      return cpus.Count == 0 ? null : cpus;
   }

   /// <summary>
   /// The canonical spelling of a CPU list: sorted, no duplicates, runs written as ranges
   /// ("0,1,2,3,8" becomes "0-3,8"); text that is not a CPU list is returned trimmed and unchanged.
   /// Why: "0-3" and "0,1,2,3" are the same partition and must not make two runs look different.
   /// </summary>
   /// <param name="text">The recorded text, or null.</param>
   /// <returns>The canonical text, or null for null or empty input.</returns>
   public static string? Normalize( string? text )
   {
      if( string.IsNullOrWhiteSpace( text ) )
      {
         return null;
      }

      SortedSet<int>? cpus = TryParse( text );
      return cpus == null ? text.Trim() : Format( cpus );
   }

   /// <summary>
   /// Writes CPU numbers as a canonical list.
   /// </summary>
   /// <param name="cpus">Sorted CPU numbers.</param>
   /// <returns>E.g. "0-3,8".</returns>
   public static string Format( IReadOnlyCollection<int> cpus )
   {
      var parts = new List<string>();
      int[] sorted = cpus.Distinct().OrderBy( c => c ).ToArray();
      for( int i = 0; i < sorted.Length; )
      {
         int end = i;
         while( end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1 )
         {
            end++;
         }

         parts.Add( end == i ? sorted[i].ToString( CultureInfo.InvariantCulture ) : $"{sorted[i].ToString( CultureInfo.InvariantCulture )}-{sorted[end].ToString( CultureInfo.InvariantCulture )}" );
         i = end + 1;
      }

      return string.Join( ",", parts );
   }

   /// <summary>
   /// The CPUs two lists have in common, and the physical cores they share.
   /// </summary>
   /// <param name="client">The client's CPU list.</param>
   /// <param name="engine">The engines' CPU list.</param>
   /// <param name="siblings">Thread sibling lists, one per physical core ("0,4"); empty when unknown.</param>
   /// <returns>
   /// Logical CPUs in both lists, then the CPUs of physical cores that hold a client CPU and an
   /// engine CPU (a superset of the first when siblings are known, equal to it otherwise);
   /// both null when either list cannot be read.
   /// </returns>
   public static (string? SameCpus, string? SameCores) Overlap( string? client, string? engine, IReadOnlyList<string> siblings )
   {
      SortedSet<int>? c = TryParse( client );
      SortedSet<int>? e = TryParse( engine );
      if( c == null || e == null )
      {
         return ( null, null );
      }

      int[] same = c.Intersect( e ).ToArray();
      var cores = new SortedSet<int>( same );
      foreach( SortedSet<int> core in siblings.Select( TryParse ).OfType<SortedSet<int>>() )
      {
         if( core.Overlaps( c ) && core.Overlaps( e ) )
         {
            cores.UnionWith( core );
         }
      }

      return ( same.Length == 0 ? null : Format( same ), cores.Count == 0 ? null : Format( cores ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Parses one CPU number.
   /// </summary>
   /// <param name="text">Digits.</param>
   /// <param name="value">The number.</param>
   /// <returns>True when it is a CPU number from 0 to <see cref="MAX_CPU"/>.</returns>
   private static bool TryNumber( string text, out int value )
   {
      return int.TryParse( text, NumberStyles.None, CultureInfo.InvariantCulture, out value ) && value <= MAX_CPU;
   }

   #endregion Private Methods
}
