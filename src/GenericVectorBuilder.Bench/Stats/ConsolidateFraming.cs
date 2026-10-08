using System.Globalization;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The words that frame the speed figures of a single run's results.md: the title that says what was
/// timed and on how big a collection, the one line that says what the numbers do not measure, and the
/// line that says what client CPU per search is.
/// Why one place: at 524 vectors the timings are request cost (connection, driver, parsing, a scan
/// over a few hundred rows), not how an index scales, and a figure shown without that is read as an
/// index ranking. The consolidated report no longer prints these lines: its prose comes from
/// <see cref="ConsolidateText"/>, audited sentence by sentence.
/// Why the lines name the client code and not "each engine's .NET client": eight of the 19 engines (elasticsearch, vespa, opensearch, chroma, milvus, typesense, clickhouse, weaviate) are
/// reached through the benchmark's own HttpClient REST code, not a .NET client package of the engine, so the older wording ("through each engine's .NET client") was false for them. The results.md of
/// the runs already written keep that line, and the run-page notes list (deploy/bench/run-page-notes.json) marks it on each of their pages.
/// Why <see cref="RETIRED"/>: the v5 to v7 results.md files carry a client-CPU line that read client
/// CPU per search as a part of the latency, which client CPU above the time per search (Redis, DuckDB,
/// sqlite-vec) shows it is not; the runs are read in place, so the consolidation lists the lines that
/// still hold those words, and the page marks those run pages (hole H4).
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
   public const string LINE_SMALL = "Measured end to end through the client code the benchmark uses for each engine (a vendor library for some engines, the benchmark's own HttpClient REST code for others); at this size it reflects per-request cost including that client code, not index scaling.";

   /// <summary>The one line under a larger-collection title; {0} is the vector count.</summary>
   public const string LINE_LARGE = "Measured end to end through the client code the benchmark uses for each engine (a vendor library for some engines, the benchmark's own HttpClient REST code for others) at {0} vectors; the order applies to this size only.";

   /// <summary>The one line when the collection size is unknown.</summary>
   public const string LINE_UNKNOWN = "The collection size was not recorded in these results. Measured end to end through the client code the benchmark uses for each engine (a vendor library for some engines, the benchmark's own HttpClient REST code for others).";

   /// <summary>
   /// What the client CPU per search column is; printed under a run's table that shows it. It says what
   /// the figure is and that it can exceed the time per search, never what share of the latency it is.
   /// </summary>
   public const string CLIENT_CPU_LINE = "Client CPU per search is the CPU time the test's .NET client itself used for each search, measured in the same pass as the figure beside it, summed over every thread, so it can exceed the time per search. For an embedded engine (DuckDB, sqlite-vec) the engine runs inside the client process, so its figure includes the engine's own CPU time.";

   /// <summary>Words of framing sentences this report retired; a run's results.md line holding one is listed for the page to mark.</summary>
   public static readonly IReadOnlyList<string> RETIRED = new[]
   {
      "Where it is close to the latency, the client library is a large part of what is measured.",
      "not client overhead",
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The run's title.
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
