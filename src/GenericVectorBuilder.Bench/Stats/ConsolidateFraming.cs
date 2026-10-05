using System.Globalization;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The words that frame the speed ranking in every place it is shown: the title that says what
/// was ranked and on how big a collection, and the one line that says what the numbers do not
/// measure.
/// Why one place: at 524 vectors the timings are request cost (connection, driver, parsing, a
/// scan over a few hundred rows), not how an index scales, and a ranking shown without that is
/// read as an index ranking. Every search is timed end to end through the engine's own .NET client
/// library, and for the fastest engines the client's own CPU per search (0.49 to 0.87 ms in the v4
/// review of 2026-10-05) is about as large as the latency, so the order partly reflects the client
/// library; the line says so, and the tables show
/// the client CPU per search where the results carry it. The markdown report, the results.md of a
/// run and the summary page must say the same thing; the web page keeps a copy of these strings
/// (it does not reference the benchmark) and a test compares the two.
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
   public const string LINE_SMALL = "Measured end to end through each engine's .NET client; at this size it reflects per-request cost including the client library, not index scaling.";

   /// <summary>The one line under a larger-collection title; {0} is the vector count.</summary>
   public const string LINE_LARGE = "Measured end to end through each engine's .NET client at {0} vectors; the order applies to this size only.";

   /// <summary>The one line when the collection size is unknown.</summary>
   public const string LINE_UNKNOWN = "The collection size was not recorded in these results. Measured end to end through each engine's .NET client.";

   /// <summary>What a band is and what its order means, printed under every banded table.</summary>
   public const string BANDS_LINE = "Engines in different bands never overlap: every run of an engine in a faster band beat every run of an engine in a slower band, and the medians on either side of a band boundary are at least 3% apart. Engines in one band are linked by overlapping slowest-to-fastest ranges or by neighboring medians less than 3% apart (an engine varies about 2% from run to run), so these runs do not separate them cleanly. Inside a band they are listed by median, and that order is not a ranking.";

   /// <summary>What the client CPU per search column is and why it sits beside the latency; printed under a table that shows it.</summary>
   public const string CLIENT_CPU_LINE = "Client CPU per search is the CPU time the test's .NET client itself used for each search, measured in the same pass as the figure beside it. Where it is close to the latency, the client library is a large part of what is measured.";

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
