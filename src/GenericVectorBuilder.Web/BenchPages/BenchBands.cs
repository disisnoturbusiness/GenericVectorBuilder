namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// Tie bands for the summary: engines whose slowest-to-fastest ranges over the runs overlap on
/// searches per second with 8 at once share a band, numbered from the fastest (band 1).
/// Why bands and not strict ranks: at 524 vectors the engines within a few percent of each other
/// swap places from run to run, so "3rd" and "4th" claim a difference the runs do not show.
/// Why the overlap is chained: then two engines whose ranges overlap are always in one band, and
/// two engines in different bands never overlap, so "band 1 is faster than band 2" is true of
/// every pair across them. The price is that a band can be linked through an engine with a wide
/// range, so its two ends may be separated even though the band as a whole is not.
/// Why a second copy of the rule: this project does not reference the benchmark, whose
/// consolidate command prints the same bands in consolidated.md (Stats/ConsolidateBands.cs); the
/// tests of both feed the same hand-made cases and expect the same bands.
/// Why computed here from each row's min and max and not read from consolidated.json: the older
/// published files carry no bands but do carry min and max, and one rule applied to every file
/// keeps the pages consistent.
/// </summary>
public static class BenchBands
{
   #region Public Methods

   /// <summary>
   /// Sets <see cref="BenchEngineRow.Band"/> on every row from the rows' ranges. A row without a
   /// min and a max counts as a single point at its median. The order of the rows is not changed:
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

      return rows.Select( ( r, i ) => r with { Band = bandOf[i] } ).ToList();
   }

   #endregion Public Methods

   #region Private Methods

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
