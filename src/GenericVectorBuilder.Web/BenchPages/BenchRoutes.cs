namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The addresses of the two benchmark pages that link to each other: the Vector search benchmark page (what the numbers are of and the engines in order of
/// searches per second with eight at once) and the full results page (every table, the flags and every run).
/// Why one place: the benchmark page links to the full results, the full results page links back from its heading, and the route table maps both; a link
/// written in three places can point at an address nothing serves.
/// </summary>
public static class BenchRoutes
{
   #region Data Members

   /// <summary>Address of the Vector search benchmark page.</summary>
   public const string BENCHMARK = "/benchmark";

   /// <summary>Address of the full results page: the newest published set drawn in full, then every run.</summary>
   public const string FULL_RESULTS = "/bench-results";

   /// <summary>The name of the Vector search benchmark page: its title and its heading, and the text of the link back to it from the full results page.</summary>
   public const string BENCHMARK_NAME = "Vector search benchmark";

   #endregion Data Members
}
