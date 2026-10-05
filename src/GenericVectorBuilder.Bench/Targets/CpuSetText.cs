using System.Globalization;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Reads and compares CPU lists in the kernel's text form ("2-3,6-7"), the form Docker takes for
/// a container's cpuset and prints back in HostConfig.CpusetCpus.
/// Why the Targets code has its own copy of this small parser: the target sources are compiled
/// on their own by the tests (they do not see the machine-control code), and a cpuset handed to
/// "docker compose" must be checked before it is written into a compose override, so a malformed
/// value is refused with words instead of becoming YAML that compose reads differently.
/// </summary>
public static class CpuSetText
{
   #region Data Members

   private static readonly Regex SHAPE = new( @"^\d{1,4}(-\d{1,4})?(,\d{1,4}(-\d{1,4})?)*$", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// True when the text is a well-formed CPU list: numbers and ascending ranges joined by commas, no spaces.
   /// </summary>
   /// <param name="text">The text, e.g. "2-3,6-7".</param>
   /// <returns>True when well formed.</returns>
   public static bool IsValid( string? text )
   {
      if( text == null || !SHAPE.IsMatch( text ) )
      {
         return false;
      }

      return text.Split( ',' ).All( part => !part.Contains( '-' ) || Bound( part, 0 ) <= Bound( part, 1 ) );
   }

   /// <summary>
   /// The CPUs of a list, ascending and without repeats.
   /// </summary>
   /// <param name="text">The text, e.g. "2-3,6-7".</param>
   /// <returns>The CPU numbers, e.g. 2, 3, 6, 7.</returns>
   /// <exception cref="FormatException">The text is not a CPU list.</exception>
   public static IReadOnlyList<int> Parse( string text )
   {
      string trimmed = text.Trim();
      if( !IsValid( trimmed ) )
      {
         throw new FormatException( $"'{text}' is not a CPU list such as 2-3,6-7." );
      }

      var cpus = new SortedSet<int>();
      foreach( string part in trimmed.Split( ',' ) )
      {
         int low = Bound( part, 0 );
         int high = part.Contains( '-' ) ? Bound( part, 1 ) : low;
         for( int cpu = low; cpu <= high; cpu++ )
         {
            cpus.Add( cpu );
         }
      }

      return cpus.ToList();
   }

   /// <summary>
   /// True when two CPU lists name the same CPUs, whatever their spelling ("2,3,6,7" and "2-3,6-7").
   /// An empty or malformed side never matches.
   /// </summary>
   /// <param name="a">One list.</param>
   /// <param name="b">The other.</param>
   /// <returns>True when they are the same set.</returns>
   public static bool Same( string? a, string? b )
   {
      return IsValid( a?.Trim() ) && IsValid( b?.Trim() ) && Parse( a! ).SequenceEqual( Parse( b! ) );
   }

   /// <summary>
   /// How many CPUs a list names.
   /// </summary>
   /// <param name="text">The list.</param>
   /// <returns>The count, or null when the text is not a CPU list.</returns>
   public static int? Count( string? text )
   {
      return IsValid( text?.Trim() ) ? Parse( text! ).Count : null;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// One end of a "low-high" part, or the single number of a part without a dash.
   /// </summary>
   /// <param name="part">The part.</param>
   /// <param name="index">0 for the low end, 1 for the high end.</param>
   /// <returns>The number.</returns>
   private static int Bound( string part, int index )
   {
      return int.Parse( part.Split( '-' )[index], NumberStyles.None, CultureInfo.InvariantCulture );
   }

   #endregion Private Methods
}
