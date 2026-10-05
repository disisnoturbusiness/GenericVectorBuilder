namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// Tie bands for the summary: engines whose slowest-to-fastest ranges over the runs overlap on
/// searches per second with 8 at once share a band, and so do neighbours whose medians are less
/// than <see cref="MIN_MEDIAN_GAP"/> apart; bands are numbered from the fastest (band 1).
/// Why bands and not strict ranks: at 524 vectors the engines within a few percent of each other
/// swap places from run to run, so "3rd" and "4th" claim a difference the runs do not show.
/// Why the overlap is chained: then two engines whose ranges overlap are always in one band, and
/// two engines in different bands never overlap, so "band 1 is faster than band 2" is true of
/// every pair across them. The price is that a band can be linked through an engine with a wide
/// range, so its two ends may be separated even though the band as a whole is not.
/// Why a minimum gap as well: ranges drawn from three runs can fail to overlap by a hair, and the
/// v4 review found band boundaries resting on gaps of 2.2% or less, inside the roughly 2%
/// repeatability the same engine shows from run to run. Neighbours (next to each other in median
/// order) whose medians differ by less than 3%, the measured repeatability plus a margin, share a
/// band even when their ranges do not overlap. A band boundary now means no overlap and nearest
/// medians at least 3% apart (the faster is at least 3% faster than the slower).
/// Why a second copy of the rule: this project does not reference the benchmark, whose
/// consolidate command prints the same bands in consolidated.md (Stats/ConsolidateBands.cs); the
/// tests of both feed the same hand-made cases and expect the same bands, and a test compares the
/// two gap constants.
/// Why computed here from each row's min and max and not read from consolidated.json: the older
/// published files carry no bands but do carry min and max, and one rule applied to every file
/// keeps the pages consistent.
/// </summary>
public static class BenchBands
{
   #region Data Members

   /// <summary>
   /// Smallest gap between the medians of two neighbouring engines that may separate them into two
   /// bands, as a share of the slower median: the faster median must be at least 3% higher. It sits
   /// above the roughly 2% run-to-run repeatability measured on the same engine (same value as the
   /// Bench tool's ConsolidateBands.MIN_MEDIAN_GAP).
   /// </summary>
   public const double MIN_MEDIAN_GAP = 0.03;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Sets <see cref="BenchEngineRow.Band"/> on every row from the rows' ranges (overlapping ranges
   /// are chained into one band) and medians (neighbouring bands whose nearest medians are less than
   /// <see cref="MIN_MEDIAN_GAP"/> apart are joined). A row without a min and a max counts as a
   /// single point at its median. The order of the rows is not changed:
   /// it is already median order, which is also band order because bands never overlap.
   /// </summary>
   /// <param name="rows">Rows with their median, min and max searches per second.</param>
   /// <returns>The same rows, in the same order, with their band.</returns>
   public static List<BenchEngineRow> Assign( List<BenchEngineRow> rows )
   {
      var bandOf = new int[rows.Count];
      int band = 0;
      double reach = double.NaN;
      IEnumerable<int> order = Enumerable.Range( 0, rows.Count )
         .OrderByDescending( i => Best( rows[i] ) ).ThenByDescending( i => rows[i].Qps8 ).ThenBy( i => rows[i].Key, StringComparer.Ordinal );
      foreach( int i in order )
      {
         if( band == 0 || Best( rows[i] ) < reach )
         {
            band++;
            reach = Worst( rows[i] );
         }

         bandOf[i] = band;
         reach = Math.Min( reach, Worst( rows[i] ) );
      }

      int[] joined = MergeCloseMedians( rows, bandOf, band );
      return rows.Select( ( r, i ) => r with { Band = joined[bandOf[i]] } ).ToList();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Maps each chained band to its number after neighbouring bands with close medians are joined.
   /// Why only the two medians at the boundary: the bands do not overlap, so every median of a band
   /// is above every median of the band after it, and the two engines next to each other in median
   /// order across a boundary are the band's lowest median and the next band's highest.
   /// </summary>
   /// <param name="rows">The rows.</param>
   /// <param name="bandOf">Each row's chained band, 1-based.</param>
   /// <param name="bands">How many chained bands there are.</param>
   /// <returns>For each chained band number (index 0 unused), its number after joining.</returns>
   private static int[] MergeCloseMedians( List<BenchEngineRow> rows, int[] bandOf, int bands )
   {
      var lowest = Enumerable.Repeat( double.PositiveInfinity, bands + 1 ).ToArray();
      var highest = Enumerable.Repeat( double.NegativeInfinity, bands + 1 ).ToArray();
      for( int i = 0; i < rows.Count; i++ )
      {
         lowest[bandOf[i]] = Math.Min( lowest[bandOf[i]], rows[i].Qps8 );
         highest[bandOf[i]] = Math.Max( highest[bandOf[i]], rows[i].Qps8 );
      }

      var joined = new int[bands + 1];
      int number = 0;
      double lowestSoFar = double.NaN;
      for( int b = 1; b <= bands; b++ )
      {
         bool apart = number == 0 || Gap( highest[b], lowestSoFar ) >= MIN_MEDIAN_GAP;
         number += apart ? 1 : 0;
         lowestSoFar = apart ? lowest[b] : Math.Min( lowestSoFar, lowest[b] );
         joined[b] = number;
      }

      return joined;
   }

   /// <summary>
   /// How much higher the larger of two searches-per-second medians is than the smaller, as a share
   /// of the smaller (0.03 is 3% higher).
   /// </summary>
   /// <param name="a">One median.</param>
   /// <param name="b">The other median.</param>
   /// <returns>The gap; infinite when the smaller is zero and the larger is not, 0 when equal.</returns>
   private static double Gap( double a, double b )
   {
      double high = Math.Max( a, b );
      double low = Math.Min( a, b );
      return high == low ? 0 : low <= 0 ? double.PositiveInfinity : high / low - 1;
   }

   /// <summary>
   /// The fastest run's searches per second (the median when only one value is known).
   /// </summary>
   /// <param name="row">The engine.</param>
   /// <returns>The largest value.</returns>
   private static double Best( BenchEngineRow row )
   {
      return Math.Max( row.Qps8, row.Qps8Max ?? row.Qps8 );
   }

   /// <summary>
   /// The slowest run's searches per second (the median when only one value is known).
   /// </summary>
   /// <param name="row">The engine.</param>
   /// <returns>The smallest value.</returns>
   private static double Worst( BenchEngineRow row )
   {
      return Math.Min( row.Qps8, row.Qps8Min ?? row.Qps8 );
   }

   #endregion Private Methods
}
