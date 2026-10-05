namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// Result files for the page tests, written out here so that no test depends on a folder under
/// bench-results: a published or blocked folder comes and goes with the review of each run, and a
/// test that reads one by its dated name fails the day it is renamed. The numbers are the 19 engines of
/// the 4 Oct 2026 publication and of its third run, copied in as data so the checks (band rules, one
/// row per engine, the median order) still run over a realistic spread.
/// </summary>
internal static class BenchResultsFixtures
{
   #region Data Members

   /// <summary>The first published shape of consolidated.json (an object keyed by engine, no flags, settings or notes), 19 engines, three runs each.</summary>
   public const string OLD_PUBLISHED_JSON = """
   {"sql":{"runs":3,"qps8":{"median":806.2,"min":764.7,"max":914.0},"p50":{"median":6.72},"recall":{"median":1}},"qdrant":{"runs":3,"qps8":{"median":3213.1,"min":3051.4,"max":3443.8},"p50":{"median":2.3},"recall":{"median":1}},"qdrant-hnsw":{"runs":3,"qps8":{"median":3355.3,"min":3040.5,"max":3432.3},"p50":{"median":2.42},"recall":{"median":1}},"pgvector":{"runs":3,"qps8":{"median":3822.7,"min":3312.1,"max":3830.9},"p50":{"median":2.87},"recall":{"median":1}},"mariadb":{"runs":3,"qps8":{"median":4558.4,"min":4131.7,"max":4559.3},"p50":{"median":1.98},"recall":{"median":1}},"oracle":{"runs":3,"qps8":{"median":670.6,"min":508.7,"max":695.1},"p50":{"median":5.56},"recall":{"median":1}},"redis":{"runs":3,"qps8":{"median":2106.8,"min":2011.0,"max":3154.2},"p50":{"median":1.51},"recall":{"median":1}},"mongodb":{"runs":3,"qps8":{"median":2807.4,"min":2426.7,"max":2927.7},"p50":{"median":5.51},"recall":{"median":1}},"clickhouse":{"runs":3,"qps8":{"median":665.0,"min":625.3,"max":711.3},"p50":{"median":6.25},"recall":{"median":0.995}},"milvus":{"runs":3,"qps8":{"median":1690.9,"min":1493.2,"max":1723.6},"p50":{"median":7.42},"recall":{"median":1}},"weaviate":{"runs":3,"qps8":{"median":523.4,"min":484.6,"max":541.1},"p50":{"median":15.36},"recall":{"median":1}},"chroma":{"runs":3,"qps8":{"median":600.3,"min":330.9,"max":609.8},"p50":{"median":6.01},"recall":{"median":1}},"elasticsearch":{"runs":3,"qps8":{"median":2544.2,"min":2543.4,"max":2631.9},"p50":{"median":11.76},"recall":{"median":1}},"opensearch":{"runs":3,"qps8":{"median":1874.6,"min":1702.4,"max":1966.6},"p50":{"median":10.62},"recall":{"median":1}},"typesense":{"runs":3,"qps8":{"median":944.0,"min":896.5,"max":974.4},"p50":{"median":8.92},"recall":{"median":1}},"vespa":{"runs":3,"qps8":{"median":1552.9,"min":1489.0,"max":1563.8},"p50":{"median":6.41},"recall":{"median":1}},"duckdb":{"runs":3,"qps8":{"median":181.5,"min":171.2,"max":199.5},"p50":{"median":4.05},"recall":{"median":1}},"sqlitevec":{"runs":3,"qps8":{"median":457.8,"min":457.3,"max":528.1},"p50":{"median":2.16},"recall":{"median":1}},"sql-diskann":{"runs":3,"qps8":{"median":1002.5,"min":890.2,"max":1025.4},"p50":{"median":4.5},"recall":{"median":0.965}}}
   """;

   /// <summary>A results.json holding only what the page reads: 19 targets with QPS at 8, p50 and recall.</summary>
   public const string RUN_RESULTS_JSON = """
   { "targets": [{"name":"sql","search":{"qps":{"8":764.7},"p50Ms":6.81,"recall":1}},{"name":"sql-diskann","search":{"qps":{"8":890.2},"p50Ms":4.5,"recall":0.965}},{"name":"qdrant","search":{"qps":{"8":3051.4},"p50Ms":1.56,"recall":1}},{"name":"qdrant-hnsw","search":{"qps":{"8":3040.5},"p50Ms":2.57,"recall":1}},{"name":"pgvector","search":{"qps":{"8":3312.1},"p50Ms":1.97,"recall":1}},{"name":"mariadb","search":{"qps":{"8":4131.7},"p50Ms":1.98,"recall":1}},{"name":"oracle","search":{"qps":{"8":670.6},"p50Ms":5.56,"recall":1}},{"name":"redis","search":{"qps":{"8":3154.2},"p50Ms":1.22,"recall":1}},{"name":"mongodb","search":{"qps":{"8":2426.7},"p50Ms":4.96,"recall":0.965}},{"name":"clickhouse","search":{"qps":{"8":625.3},"p50Ms":6.88,"recall":0.995}},{"name":"milvus","search":{"qps":{"8":1493.2},"p50Ms":8.27,"recall":1}},{"name":"weaviate","search":{"qps":{"8":484.6},"p50Ms":16.11,"recall":1}},{"name":"chroma","search":{"qps":{"8":609.8},"p50Ms":6.63,"recall":1}},{"name":"elasticsearch","search":{"qps":{"8":2543.4},"p50Ms":11.03,"recall":1}},{"name":"opensearch","search":{"qps":{"8":1702.4},"p50Ms":9.83,"recall":1}},{"name":"typesense","search":{"qps":{"8":896.5},"p50Ms":5.59,"recall":1}},{"name":"vespa","search":{"qps":{"8":1489.0},"p50Ms":6.38,"recall":1}},{"name":"duckdb","search":{"qps":{"8":181.5},"p50Ms":4.0,"recall":1}},{"name":"sqlitevec","search":{"qps":{"8":457.3},"p50Ms":2.16,"recall":1}}] }
   """;

   #endregion Data Members
}

/// <summary>
/// The fixtures themselves are checked, so a typo in them cannot make the tests that use them pass by
/// reading nothing.
/// </summary>
public class BenchResultsFixtureTests
{
   #region Public Methods

   /// <summary>
   /// Both fixtures hold 19 engines and parse into 19 ranked rows with every number the page reads.
   /// </summary>
   [Fact]
   public void Fixtures_HoldNineteenEngines_WithTheNumbersThePageReads()
   {
      Web.BenchPages.BenchSummary old = Web.BenchPages.BenchSummaryReader.FromConsolidated( BenchResultsFixtures.OLD_PUBLISHED_JSON );
      Web.BenchPages.BenchSummary run = Web.BenchPages.BenchSummaryReader.FromRunResults( BenchResultsFixtures.RUN_RESULTS_JSON );

      Assert.Equal( 19, old.Ranked.Count );
      Assert.Equal( 19, run.Ranked.Count );
      Assert.Equal( 3, old.Runs );
      Assert.True( old.OldFormat );
      Assert.All( old.Ranked, r => Assert.True( r.Qps8Min <= r.Qps8 && r.Qps8 <= r.Qps8Max && r.P50Ms > 0 && r.Recall > 0 ) );
      Assert.Equal( "MariaDB", old.Ranked[0].Name );
   }

   #endregion Public Methods
}
