using System.Globalization;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The words that frame the speed ranking in every place it is shown: the title that says what
/// was ranked and on how big a collection, and the one line that says what the numbers do not
/// measure.
/// Why one place: at 524 vectors the timings are request cost (connection, driver, parsing, a
/// scan over a few hundred rows), not how an index scales, and a ranking shown without that is
/// read as an index ranking. The markdown report, the results.md of a run and the summary page
/// must say the same thing; the web page keeps a copy of these strings (it does not reference
/// the benchmark) and a test compares the two.
/// </summary>
public static class ConsolidateFraming
{
   #region Data Members

   /// <summary>Largest collection, in vectors, that is called small. Above it the framing names the size and does not claim the numbers are per-request cost.</summary>
   public const int SMALL_COLLECTION_MAX_ROWS = 10000;

   /// <summary>Title for a small collection; {0} is the vector count.</summary>
   public const string TITLE_SMALL = "Request speed on a small collection ({0} vectors)";

   /// <summary>Title for a larger collection; {0} is the vector count.</summary>
   public const string TITLE_LARGE = "Request speed at {0} vectors";

   /// <summary>Title when the results did not record the collection size.</summary>
   public const string TITLE_UNKNOWN = "Request speed (collection size not recorded)";

   /// <summary>The one line under a small-collection title.</summary>
   public const string LINE_SMALL = "At this size the numbers measure per-request cost, not index scaling.";

   /// <summary>The one line under a larger-collection title; {0} is the vector count.</summary>
   public const string LINE_LARGE = "Measured at {0} vectors; the order applies to this size only.";

   /// <summary>The one line when the collection size is unknown.</summary>
   public const string LINE_UNKNOWN = "The collection size was not recorded in these results.";

   /// <summary>What a band is and what its order means, printed under every banded table.</summary>
   public const string BANDS_LINE = "Engines in different bands never overlap: every run of an engine in a faster band beat every run of an engine in a slower band. Engines in one band are linked by overlapping slowest-to-fastest ranges, so these runs do not separate them cleanly. Inside a band they are listed by median, and that order is not a ranking.";

   /// <summary>Printed instead of <see cref="BANDS_LINE"/> when only one run was used.</summary>
   public const string ONE_RUN_LINE = "One run: no spread is known, so no band can be drawn. The order shows this run only.";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The ranking's title.
   /// </summary>
   /// <param name="rows">Vectors in the collection, or null when not recorded.</param>
   /// <returns>E.g. "Request speed on a small collection (524 vectors)".</returns>
   public static string Title( int? rows )
   {
      if( rows is not > 0 )
      {
         return TITLE_UNKNOWN;
      }

      return string.Format( CultureInfo.InvariantCulture, rows.Value <= SMALL_COLLECTION_MAX_ROWS ? TITLE_SMALL : TITLE_LARGE, rows.Value.ToString( "N0", CultureInfo.InvariantCulture ) );
   }

   /// <summary>
   /// The one line under the title: what the numbers measure at this size.
   /// </summary>
   /// <param name="rows">Vectors in the collection, or null when not recorded.</param>
   /// <returns>One sentence.</returns>
   public static string Line( int? rows )
   {
      if( rows is not > 0 )
      {
         return LINE_UNKNOWN;
      }

      return rows.Value <= SMALL_COLLECTION_MAX_ROWS ? LINE_SMALL : string.Format( CultureInfo.InvariantCulture, LINE_LARGE, rows.Value.ToString( "N0", CultureInfo.InvariantCulture ) );
   }

   #endregion Public Methods
}
