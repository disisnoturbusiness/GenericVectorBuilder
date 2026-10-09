namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The addresses of the benchmark pages that link to each other: the Vector search benchmark page (what the numbers are of and the engines in order of
/// searches per second with eight at once), the full results page (every table, the flags and every run) and the Golden Questions page (the questions the
/// speed test ran).
/// Why one place: the benchmark page links to the full results and to the questions, the full results page links back from its heading, the questions page
/// links back to the benchmark page, and the route table maps all three; a link written in three places can point at an address nothing serves.
/// </summary>
public static class BenchRoutes
{
   #region Data Members

   /// <summary>Address of the Vector search benchmark page.</summary>
   public const string BENCHMARK = "/benchmark";

   /// <summary>Address of the full results page: the newest published set drawn in full, then every run.</summary>
   public const string FULL_RESULTS = "/bench-results";

   /// <summary>Address of the Golden Questions page: the questions the speed test ran, as the published set's runs recorded them.</summary>
   public const string GOLDEN_QUESTIONS = "/benchmark/golden-questions";

   /// <summary>The name of the Golden Questions page: its title and its heading.</summary>
   public const string GOLDEN_QUESTIONS_NAME = "Golden Questions";

   /// <summary>The name of the Vector search benchmark page: its title and its heading, and the text of the link back to it from the full results page.</summary>
   public const string BENCHMARK_NAME = "Vector search benchmark";

   /// <summary>The text of the link at the right of the bar at the top of every page; it goes to <see cref="BENCHMARK"/>.</summary>
   public const string TOP_BAR_LINK = "Benchmark";

   #endregion Data Members
}
