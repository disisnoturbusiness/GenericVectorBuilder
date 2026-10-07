using System.Text.Json;
using GenericVectorBuilder.Web.BenchPages;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// Result files for the page tests, written out here so that no test depends on a folder under
/// bench-results: a published or blocked folder comes and goes with the review of each run, and a
/// test that reads one by its dated name fails the day it is renamed. The numbers are the 19 engines of
/// the 4 Oct 2026 publication and of its third run, copied in as data so the checks (one row per engine, the
/// older-format notice, the run table) still run over a realistic spread. The v8 shape is built in code by
/// <see cref="BenchResultsV8Fixture"/>.
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

   /// <summary>The shape the consolidate command wrote before v8: targetSummaries, settings, runs, flags.</summary>
   public const string REPORT_SHAPE_JSON = """
   { "settings": { "pipeline": "eshoponweb", "rows": 524 }, "runs": [ { "name": "20261006-130619-eshoponweb" }, { "name": "20261006-142724-eshoponweb" }, { "name": "20261006-154837-eshoponweb" } ],
     "targetSummaries": [ { "name": "redis", "qps": { "8": { "median": 5000, "min": 4900, "max": 5100 } } }, { "name": "mariadb", "qps": { "8": { "median": 4000, "min": 3900, "max": 4100 } } } ], "flags": [], "bands": [] }
   """;

   /// <summary>A results.json with the fields a v8 run page reads: counts, per-pass clock records, a clock block and recall hits.</summary>
   public const string RUN_RESULTS_V8_JSON = """
   { "startedUtc": "2026-10-06T13:06:19Z", "pipeline": "eshoponweb", "rows": 524, "queries": "golden: 20 labelled", "queryCount": 20, "top": 10,
     "machine": { "cpu": "Intel Xeon E5-1620 v3", "logicalCpus": 8, "ramGiB": 62.7, "os": "Ubuntu 24.04.5 LTS" },
     "conditions": { "buildConfiguration": "Release", "governor": "performance", "clientCpus": "0-1,4-5", "engineCpus": "2-3,6-7", "warmupSearches": 20, "exactSeconds": 60,
       "clock": { "pinned": true, "pinnedMhz": 3500, "noTurbo": { "before": 1, "during": 1 }, "uncoreMsr620": { "before": "0xc1e", "during": "0x1e1e" } },
       "passes": [ { "target": "oracle", "pass": "default@8", "clockRead": true, "clockOff": true, "engineMhzMedian": 3121, "clientMhzMedian": 3141 },
                   { "target": "redis", "pass": "default@1", "clockRead": false },
                   { "target": "sql", "pass": "exact", "busyBox": true, "outsideLoadDuring": 0.31 },
                   { "target": "sql", "pass": "default@1", "busyBox": false } ] },
     "targets": [
       { "name": "sql", "search": { "qps": { "1": 241.0, "8": 611.6 }, "p50Ms": 4.06, "exactP50Ms": 4.2, "recall": 1, "recallHits": 200, "clientCpuMsPerSearch": { "1": 1.22, "8": 1.18 } } },
       { "name": "redis", "search": { "qps": { "1": 2527.0, "8": 5120.1 }, "p50Ms": 0.40, "exactP50Ms": 0.9, "recall": 0.995, "clientCpuMsPerSearch": { "1": 0.57, "8": 0.36 } } },
       { "name": "oracle", "search": { "qps": { "1": 1050.9, "8": 2802.2 }, "p50Ms": 0.93, "recall": 1, "clientCpuMsPerSearch": { "default@1": 0.88 } } },
       { "name": "weaviate", "search": { "p50Ms": 5.36 } }
     ] }
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
   /// The older shapes hold nineteen engines and are told apart from the v8 shape.
   /// </summary>
   [Fact]
   public void OlderFixtures_AreRecognisedAsOlder_AndHoldNineteenEngines()
   {
      Assert.Equal( BenchShape.KeyedByEngine, BenchConsolidatedReader.Detect( BenchResultsFixtures.OLD_PUBLISHED_JSON ) );
      Assert.Equal( BenchShape.Report, BenchConsolidatedReader.Detect( BenchResultsFixtures.REPORT_SHAPE_JSON ) );
      using JsonDocument doc = JsonDocument.Parse( BenchResultsFixtures.OLD_PUBLISHED_JSON );
      Assert.Equal( 19, doc.RootElement.EnumerateObject().Count() );
      BenchRunSummary run = BenchRunTable.Read( BenchResultsFixtures.RUN_RESULTS_JSON );
      Assert.Equal( 19, run.Rows.Count );
   }

   /// <summary>
   /// A keyed file in which one engine has no throughput figure is still the first published shape.
   /// </summary>
   [Fact]
   public void AKeyedFile_WithOneEngineWithoutAFigure_IsStillTheOlderShape()
   {
      Assert.Equal( BenchShape.KeyedByEngine, BenchConsolidatedReader.Detect( """{ "redis": { "runs": 3, "qps8": { "median": 1 } }, "broken": { "runs": 3, "p50": { "median": 1 } } }""" ) );
   }

   /// <summary>
   /// The built v8 file is in the v8 shape, reads without a refusal, and holds six engines in each of
   /// its tables but the exact one, which leaves the exact-only engine out.
   /// </summary>
   [Fact]
   public void V8Fixture_ReadsClean_WithSixEnginesAndFiveInTheExactTable()
   {
      string json = BenchResultsV8Fixture.Json();
      Assert.Equal( BenchShape.Consolidated, BenchConsolidatedReader.Detect( json ) );
      BenchConsolidated model = BenchConsolidatedReader.Read( json );

      Assert.Equal( new[] { "p50Ms", "qps1", "qps8", "exactP50Ms" }, model.Metrics.Select( m => m.Metric ) );
      Assert.Equal( new[] { 6, 6, 6, 5 }, model.Metrics.Select( m => m.Rows.Count ) );
      Assert.Equal( 2, model.Sessions.Count );
      Assert.Equal( 3500, model.TBp );
   }

   /// <summary>
   /// The fixture's separations come out of the claim rule: Redis is separated from every engine on p50,
   /// and MariaDB and pgvector, which sit 1.33 apart in the worst case, are not.
   /// </summary>
   [Fact]
   public void V8Fixture_SeparationsFollowTheRule()
   {
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() );
      BenchMetric p50 = model.Metrics[0];

      Assert.Equal( new[] { "redis", "mariadb", "pgvector", "qdrant", "sqlitevec", "sql" }, p50.Rows.Select( r => r.Target ) );
      Assert.Empty( p50.Rows[0].NotSeparatedFrom );
      Assert.Contains( "pgvector", p50.Rows[1].NotSeparatedFrom );
      Assert.Equal( new[] { "mariadb", "qdrant" }, p50.Rows[2].NotSeparatedFrom.OrderBy( x => x ) );
   }

   #endregion Public Methods
}
