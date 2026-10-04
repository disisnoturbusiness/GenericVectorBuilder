namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The small pieces of arithmetic the consolidate command is built from: median, spread over
/// runs, and rank within a run.
/// Why separate from <see cref="BenchMath"/>: BenchMath scores one run; this file summarizes
/// many runs, and keeping it apart lets each be unit tested on its own.
/// </summary>
public static class ConsolidateMath
{
   #region Public Methods

   /// <summary>
   /// Median of the values that are numbers: the middle value, or the mean of the two middle
   /// values for an even count.
   /// </summary>
   /// <param name="values">Values in any order; null and NaN are skipped.</param>
   /// <returns>The median, or null when there are no numbers.</returns>
   public static double? Median( IEnumerable<double?> values )
   {
      double[] sorted = Numbers( values ).OrderBy( v => v ).ToArray();
      if( sorted.Length == 0 )
      {
         return null;
      }

      int middle = sorted.Length / 2;
      return sorted.Length % 2 == 1 ? sorted[middle] : ( sorted[middle - 1] + sorted[middle] ) / 2.0;
   }

   /// <summary>
   /// Ranks the values of one run, 1 for the best. Ties share the best rank they cover and the
   /// next rank is skipped (1, 2, 2, 4), so a tie never hides how many targets beat a value.
   /// </summary>
   /// <param name="values">One value per target, null or NaN when the target has none.</param>
   /// <param name="higherIsBetter">True for QPS, load, recall and nDCG; false for latency.</param>
   /// <returns>One rank per input value, null where the value was missing.</returns>
   public static int?[] CompetitionRanks( IReadOnlyList<double?> values, bool higherIsBetter )
   {
      var ranks = new int?[values.Count];
      for( int i = 0; i < values.Count; i++ )
      {
         if( !IsNumber( values[i] ) )
         {
            continue;
         }

         double mine = values[i]!.Value;
         int better = values.Count( v => IsNumber( v ) && ( higherIsBetter ? v!.Value > mine : v!.Value < mine ) );
         ranks[i] = better + 1;
      }

      return ranks;
   }

   /// <summary>
   /// a divided by b, or null when either is missing or b is zero.
   /// Why null instead of infinity: a ratio against nothing is not a measurement.
   /// </summary>
   /// <param name="a">Numerator.</param>
   /// <param name="b">Denominator.</param>
   /// <returns>The ratio, or null.</returns>
   public static double? Ratio( double? a, double? b )
   {
      return IsNumber( a ) && IsNumber( b ) && b!.Value != 0 ? a!.Value / b.Value : null;
   }

   /// <summary>
   /// a minus b, or null when either is missing.
   /// </summary>
   /// <param name="a">First value.</param>
   /// <param name="b">Second value.</param>
   /// <returns>The difference, or null.</returns>
   public static double? Difference( double? a, double? b )
   {
      return IsNumber( a ) && IsNumber( b ) ? a!.Value - b!.Value : null;
   }

   /// <summary>
   /// True when the value is present and a real number.
   /// </summary>
   /// <param name="value">Value.</param>
   /// <returns>True for a usable number.</returns>
   public static bool IsNumber( double? value )
   {
      return value.HasValue && !double.IsNaN( value.Value ) && !double.IsInfinity( value.Value );
   }

   /// <summary>
   /// The usable numbers among the values.
   /// </summary>
   /// <param name="values">Values.</param>
   /// <returns>Only the present, finite values.</returns>
   public static IEnumerable<double> Numbers( IEnumerable<double?> values )
   {
      return values.Where( IsNumber ).Select( v => v!.Value );
   }

   #endregion Public Methods
}

/// <summary>
/// One metric over the runs used: median, smallest, largest, how many runs had it, and the
/// value from each run in run order.
/// Why the per-run values are kept next to the summary: a reader (or a verifier) can recompute
/// the median and see which run produced the extreme, without opening every results.json.
/// </summary>
public sealed class Spread
{
   #region Public Methods

   /// <summary>Median over the runs that had a value.</summary>
   public double? Median { get; set; }

   /// <summary>Smallest value.</summary>
   public double? Min { get; set; }

   /// <summary>Largest value.</summary>
   public double? Max { get; set; }

   /// <summary>Runs that had a value.</summary>
   public int N { get; set; }

   /// <summary>The value from each run used, in run order; null where that run had none.</summary>
   public List<double?> PerRun { get; set; } = new();

   /// <summary>
   /// Summarizes the per-run values.
   /// </summary>
   /// <param name="perRun">One value per run used, in run order.</param>
   /// <returns>The spread.</returns>
   public static Spread Of( IEnumerable<double?> perRun )
   {
      List<double?> values = perRun.Select( v => ConsolidateMath.IsNumber( v ) ? v : null ).ToList();
      double[] numbers = ConsolidateMath.Numbers( values ).ToArray();
      return new Spread
      {
         Median = ConsolidateMath.Median( values ),
         Min = numbers.Length == 0 ? null : numbers.Min(),
         Max = numbers.Length == 0 ? null : numbers.Max(),
         N = numbers.Length,
         PerRun = values,
      };
   }

   #endregion Public Methods
}
