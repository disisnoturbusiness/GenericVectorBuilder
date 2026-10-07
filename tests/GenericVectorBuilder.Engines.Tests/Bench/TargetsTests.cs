using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using GenericVectorBuilder.Engines.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// What the four built-in benchmark targets (sql, sql-diskann, qdrant, qdrant-hnsw) claim about
/// their index and their crash safety, checked without any server: the verdict code is fed
/// facts built by hand and facts captured from the real servers (a Qdrant 1.17.0 telemetry
/// answer and two SQL Server 2025 actual plans, trimmed to the parts the code reads).
/// Why these rules get tests: a published comparison was killed because "qdrant-hnsw" never
/// built a graph and nothing noticed. Every way an index can be missing, half built, bypassed
/// or disabled must come out as "not ready" with a reason, and every ready verdict must name
/// its evidence.
/// Why the sources are compiled here with Roslyn: the benchmark is a console project this test
/// project does not reference. Compiling the repository's Bench target sources together with
/// TargetsScenarios.cs (built only in this compilation) tests the code the benchmark runs.
/// </summary>
public class TargetsTests
{
   #region Data Members

   private const string QDRANT_CONFIG = """
      storage:
        storage_path: /home/dan/qdrant/storage
        snapshots_path: /home/dan/qdrant/snapshots
      service:
        host: 0.0.0.0
        http_port: 6333
        grpc_port: 6334
      """;

   private const string MSSQL_CONF = """
      [sqlagent]
      enabled = false

      [EULA]
      accepteula = Y

      [memory]
      memorylimitmb = 24576
      """;

   // Trimmed from GET /telemetry?details_level=10 of Qdrant 1.17.0: 400 vectors of 32 dimensions,
   // created with indexing_threshold_kb 1 and full_scan_threshold_kb 10, then 6 default and 2
   // exact searches. One empty appendable segment and three HNSW segments.
   private const string TELEMETRY = """{"result":{"collections":{"collections":[{"id":"gvbbench_fixture_tel","shards":[{"id":0,"local":{"segments":[{"info":{"num_vectors":0,"num_indexed_vectors":0,"vectors_size_bytes":0},"config":{"vector_data":{"":{"index":{"type":"plain","options":{}}}}},"vector_index_searches":[{"index_name":""}]},{"info":{"num_vectors":208,"num_indexed_vectors":208,"vectors_size_bytes":26624},"config":{"vector_data":{"":{"index":{"type":"hnsw","options":{"m":16,"ef_construct":100,"full_scan_threshold":10,"max_indexing_threads":0,"on_disk":false}}}}},"vector_index_searches":[{"index_name":"","unfiltered_hnsw":{"count":6},"unfiltered_exact":{"count":2}}]},{"info":{"num_vectors":96,"num_indexed_vectors":96,"vectors_size_bytes":12288},"config":{"vector_data":{"":{"index":{"type":"hnsw","options":{"m":16,"ef_construct":100,"full_scan_threshold":10,"max_indexing_threads":0,"on_disk":false}}}}},"vector_index_searches":[{"index_name":"","unfiltered_hnsw":{"count":6},"unfiltered_exact":{"count":2}}]},{"info":{"num_vectors":96,"num_indexed_vectors":96,"vectors_size_bytes":12288},"config":{"vector_data":{"":{"index":{"type":"hnsw","options":{"m":16,"ef_construct":100,"full_scan_threshold":10,"max_indexing_threads":0,"on_disk":false}}}}},"vector_index_searches":[{"index_name":"","unfiltered_hnsw":{"count":6},"unfiltered_exact":{"count":2}}]}]}}]},{"id":"other","shards":[]}]}}}""";

   // Trimmed from SET STATISTICS XML ON of a real VECTOR_SEARCH (DiskANN) and of the exact search on the same table (SQL Server 2025 CU, 200 rows).
   private const string PLAN_SEEK = """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.599"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan><RelOp PhysicalOp="Nested Loops" LogicalOp="Inner Join"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="10" ActualExecutions="1" /></RunTimeInformation><NestedLoops><RelOp PhysicalOp="Vector Index Seek" LogicalOp="Vector Index Seek"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="10" ActualExecutions="1" /></RunTimeInformation><IndexScan Ordered="0"><Object Database="[GvbBenchTgtFixturea40ab667]" Schema="[dbo]" Table="[gvb_gvbbench_tgtprobea40ab667_ann]" Index="[vix_gvb_gvbbench_tgtprobea40ab667]" IndexKind="DiskANN" /></IndexScan></RelOp><RelOp PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="10" ActualExecutions="10" /></RunTimeInformation><IndexScan Ordered="1"><Object Database="[GvbBenchTgtFixturea40ab667]" Schema="[dbo]" Table="[gvb_gvbbench_tgtprobea40ab667_ann]" Index="[PK_gvb_gvbbench_tgtprobea40ab667_ann]" IndexKind="Clustered" /></IndexScan></RelOp></NestedLoops></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";

   private const string PLAN_SCAN = """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.599"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan><RelOp PhysicalOp="Top" LogicalOp="Top"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="10" ActualExecutions="1" /></RunTimeInformation><Top><RelOp PhysicalOp="Sort" LogicalOp="Sort"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="10" ActualExecutions="1" /></RunTimeInformation><Sort><RelOp PhysicalOp="Compute Scalar" LogicalOp="Compute Scalar"><ComputeScalar><RelOp PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="200" ActualExecutions="1" /></RunTimeInformation><IndexScan Ordered="0"><Object Database="[GvbBenchTgtFixturea40ab667]" Schema="[dbo]" Table="[gvb_gvbbench_tgtprobea40ab667_ann]" Index="[PK_gvb_gvbbench_tgtprobea40ab667_ann]" IndexKind="Clustered" /></IndexScan></RelOp></ComputeScalar></RelOp></Sort></RelOp></Top></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";

   private static readonly string[] HEALTHY = { "plain:0:0:0:-:0:0:0", "hnsw:300:300:307200:10:0:0:0", "hnsw:224:224:229376:10:0:0:0" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The real Qdrant config reads as dotted keys; comments, quotes and a "#" inside a value are
   /// handled; list items and tab-indented lines are counted, never silently dropped.
   /// </summary>
   [Fact]
   public void Yaml_ReadsNestedKeysAndCountsWhatItCannotRead()
   {
      dynamic real = TargetsHarness.Call( "ParseYaml", QDRANT_CONFIG );
      Assert.Equal( new[]
      {
         "service.grpc_port=6334", "service.host=0.0.0.0", "service.http_port=6333", "storage.snapshots_path=/home/dan/qdrant/snapshots", "storage.storage_path=/home/dan/qdrant/storage",
      }, (string[])real.Lines );
      Assert.Equal( 0, (int)real.Skipped );

      string text = "# top\nstorage:\n  wal:\n    wal_capacity_mb: 64   # bigger\n    note: \"a # b\"\n  optimizers:\n    flush_interval_sec: 30\nservice:\n  api_key: 'k#1'\n  cors:\n    - x\n\tbad: 1\n";
      dynamic odd = TargetsHarness.Call( "ParseYaml", text );
      Assert.Equal( new[] { "service.api_key=k#1", "storage.optimizers.flush_interval_sec=30", "storage.wal.note=a # b", "storage.wal.wal_capacity_mb=64" }, (string[])odd.Lines );
      Assert.Equal( 2, (int)odd.Skipped );
   }

   /// <summary>
   /// mssql.conf reads as section.key entries, whatever the letter case of a section.
   /// </summary>
   [Fact]
   public void Ini_ReadsSectionsAndKeys()
   {
      dynamic real = TargetsHarness.Call( "ParseIni", MSSQL_CONF );
      Assert.Equal( new[] { "EULA.accepteula=Y", "memory.memorylimitmb=24576", "sqlagent.enabled=false" }, (string[])real.Lines );
      Assert.Equal( 0, (int)real.Skipped );
      dynamic odd = TargetsHarness.Call( "ParseIni", "; c\n[control]\nwritethrough = 1\nnonsense\n" );
      Assert.Equal( new[] { "control.writethrough=1" }, (string[])odd.Lines );
      Assert.Equal( 1, (int)odd.Skipped );
   }

   /// <summary>
   /// The Qdrant sentence states the rule that makes a write durable, the real settings with
   /// defaults marked as defaults, the environment (names only), and that power loss was not tested.
   /// </summary>
   [Fact]
   public void QdrantDurability_NamesWhatTheServerDoes()
   {
      string text = TargetsHarness.Call( "QdrantDurabilityText", QDRANT_CONFIG, Array.Empty<string>(), "1.17.0" );
      Assert.Contains( "What was measured (strace -f on the Qdrant server process", text );
      Assert.Contains( "with wait=true each upsert had exactly one msync(MS_SYNC) of the write-ahead-log segment inside the request, before the reply (30 of 30)", text );
      Assert.Contains( "with wait=false the 30 upserts were acknowledged within 0.26 s with no sync call, and the first WAL msync", text );
      Assert.Contains( "came 2.7 s after the last reply", text );
      Assert.Contains( "the log is flushed to disk under both settings, but with wait=false the flush comes after the acknowledgement", text );
      Assert.Contains( "an operating-system crash or power cut in that gap loses acknowledged writes", text );
      Assert.Contains( "(inferred, not tested)", text );
      Assert.Contains( "Every upsert here is sent with wait=true in batches of 256 over gRPC", text );
      Assert.Contains( "one flush per batch is inferred, not measured", text );
      Assert.DoesNotContain( "update_worker.rs", text );
      Assert.DoesNotContain( "survives a process or OS crash by log replay", text );
      Assert.Contains( "Segment files are flushed every 5 s (default)", text );
      Assert.Contains( "log segments 32 MB (default)", text );
      Assert.Contains( "storage.storage_path = /home/dan/qdrant/storage", text );
      Assert.Contains( "no QDRANT__ environment overrides", text );
      Assert.Contains( "Not tested by cutting power", text );
      Assert.DoesNotContain( "re-check it", text );
   }

   /// <summary>
   /// Settings from the file replace the defaults and lose the "(default)" mark; an unreadable
   /// config, an unreadable environment, a named override and a newer server each say so.
   /// </summary>
   [Fact]
   public void QdrantDurability_ReportsSettingsGapsAndOverrides()
   {
      string set = TargetsHarness.Call( "QdrantDurabilityText", "storage:\n  optimizers:\n    flush_interval_sec: 30\n  wal:\n    wal_capacity_mb: 64\n", Array.Empty<string>(), "1.17.0" );
      Assert.Contains( "Segment files are flushed every 30 s;", set );
      Assert.Contains( "log segments 64 MB,", set );
      Assert.DoesNotContain( "30 s (default)", set );

      string noFile = TargetsHarness.Call( "QdrantDurabilityText", null, null, "1.17.0" );
      Assert.Contains( "could not be read, so every value shown is the server default and may be wrong", noFile );
      Assert.Contains( "environment could not be read", noFile );

      string env = TargetsHarness.Call( "QdrantDurabilityText", QDRANT_CONFIG, new[] { "QDRANT__STORAGE__WAL__WAL_CAPACITY_MB", "QDRANT__SERVICE__API_KEY" }, "1.17.0" );
      Assert.Contains( "(names only): QDRANT__STORAGE__WAL__WAL_CAPACITY_MB, QDRANT__SERVICE__API_KEY", env );

      string newer = TargetsHarness.Call( "QdrantDurabilityText", QDRANT_CONFIG, Array.Empty<string>(), "1.18.0" );
      Assert.Contains( "The flush behaviour was measured on 1.17.0 and this server is 1.18.0, so re-measure it.", newer );
   }

   /// <summary>
   /// The SQL Server sentence carries the recovery model a new database copies, delayed
   /// durability, trace flags and mssql.conf, and says what it could not read.
   /// </summary>
   [Fact]
   public void SqlDurability_NamesWhatTheServerDoes()
   {
      string[] rows = { "model|FULL|DISABLED|CHECKSUM" };
      string text = TargetsHarness.Call( "SqlDurabilityText", rows, Array.Empty<int>(), MSSQL_CONF );
      Assert.Contains( "A commit returns after its transaction-log records are written to disk", text );
      Assert.Contains( "recovery model FULL, page_verify CHECKSUM", text );
      Assert.Contains( "no global trace flags are enabled", text );
      Assert.Contains( "memory.memorylimitmb = 24576", text );
      Assert.Contains( "no [control] or [traceflag] entry", text );
      Assert.Contains( "Not tested by cutting power", text );

      string delayed = TargetsHarness.Call( "SqlDurabilityText", new[] { "model|FULL|DISABLED|CHECKSUM", "GvbBench|SIMPLE|FORCED|CHECKSUM" }, new[] { 3979 }, MSSQL_CONF );
      Assert.Contains( "Delayed durability is on (GvbBench FORCED), so the last commits can be lost in a crash", delayed );
      Assert.Contains( "existing benchmark databases: GvbBench SIMPLE/FORCED/CHECKSUM", delayed );
      Assert.Contains( "global trace flags enabled: 3979", delayed );
   }

   /// <summary>
   /// A missing mssql.conf, a missing model database and a flush-tuning entry are each stated.
   /// </summary>
   [Fact]
   public void SqlDurability_ReportsGaps()
   {
      string none = TargetsHarness.Call( "SqlDurabilityText", Array.Empty<string>(), Array.Empty<int>(), null );
      Assert.Contains( "the model database could not be read", none );
      Assert.Contains( "/var/opt/mssql/mssql.conf could not be read", none );
      string tuned = TargetsHarness.Call( "SqlDurabilityText", new[] { "model|FULL|DISABLED|CHECKSUM" }, Array.Empty<int>(), "[control]\nwritethrough = 0\n" );
      Assert.Contains( "it has [control] or [traceflag] entries, which change how writes are flushed", tuned );
      Assert.DoesNotContain( "no [control] or [traceflag] entry", tuned );
   }

   /// <summary>
   /// A real telemetry answer is read per segment: index type, sizes, full-scan threshold and
   /// the search counters, with the other collection's entry ignored and an unknown one reported as missing.
   /// </summary>
   [Fact]
   public void Telemetry_ReadsARealAnswer()
   {
      string[]? segments = TargetsHarness.Call( "Telemetry", TELEMETRY, "gvbbench_fixture_tel" );
      Assert.Equal( new[] { "plain:0:0:0:-:0:0:0", "hnsw:208:208:26624:10:6:0:2", "hnsw:96:96:12288:10:6:0:2", "hnsw:96:96:12288:10:6:0:2" }, segments );
      Assert.Equal( Array.Empty<string>(), (string[]?)TargetsHarness.Call( "Telemetry", TELEMETRY, "other" ) );
      Assert.Null( (string[]?)TargetsHarness.Call( "Telemetry", TELEMETRY, "nothing" ) );
      Assert.Null( (string[]?)TargetsHarness.Call( "Telemetry", "{\"result\":{}}", "x" ) );
   }

   /// <summary>
   /// The HNSW target is ready only when every vector is in a graph segment above the full-scan
   /// threshold and no search has scanned; the evidence is in the text either way.
   /// </summary>
   [Fact]
   public void QdrantHnsw_ReadyOnlyWithACompleteGraphThatSearchesWalk()
   {
      IndexState ready = GraphVerdict( "Green", null, 524, 524, HEALTHY );
      Assert.True( ready.Ready );
      Assert.Equal( (long?)524, ready.IndexedVectors );
      Assert.Equal( (long?)524, ready.TotalVectors );
      Assert.Contains( "indexed_vectors_count 524 of 524 points", ready.Detail );
      Assert.Contains( "every one of 2 non-empty segments has a complete HNSW graph above its full-scan threshold", ready.Detail );
      Assert.Contains( "no search has run yet", ready.Detail );

      IndexState searched = GraphVerdict( "Green", null, 524, 524, new[] { "plain:0:0:0:-:0:0:0", "hnsw:300:300:307200:10:60:0:15", "hnsw:224:224:229376:10:60:0:15" } );
      Assert.True( searched.Ready );
      Assert.Contains( "120 segment searches walked the graph and none scanned", searched.Detail );
      Assert.Contains( "unfiltered_exact 30", searched.Detail );
   }

   /// <summary>
   /// Each way the HNSW target can fail to search a graph is not ready, and the text names the
   /// reason: the old behaviour (no graph at all), a graph below the full-scan threshold, searches
   /// that scanned, a busy or failed optimiser, an empty collection, and no telemetry.
   /// </summary>
   [Fact]
   public void QdrantHnsw_EveryWayToMissTheGraphIsNotReady()
   {
      IndexState none = GraphVerdict( "Green", null, 524, 0, new[] { "plain:100:0:409600:-:0:0:0", "plain:424:0:1736704:-:0:0:0" } );
      Assert.False( none.Ready );
      Assert.Contains( "NOT a graph search", none.Detail );
      Assert.Contains( "not every vector is in an HNSW segment", none.Detail );
      Assert.Contains( "2 of 2 segments have no complete graph", none.Detail );

      IndexState small = GraphVerdict( "Green", null, 524, 524, new[] { "hnsw:524:524:2146304:10000:0:0:0" } );
      Assert.False( small.Ready );
      Assert.Contains( "1 of 1 segments are smaller than full_scan_threshold, so searches scan them", small.Detail );

      IndexState scanned = GraphVerdict( "Green", null, 524, 524, new[] { "hnsw:524:524:2146304:10:7:3:0" } );
      Assert.False( scanned.Ready );
      Assert.Contains( "3 segment searches scanned instead of walking the graph", scanned.Detail );

      IndexState partial = GraphVerdict( "Green", null, 524, 524, new[] { "hnsw:300:300:307200:10:0:0:0", "hnsw:224:100:229376:10:0:0:0" } );
      Assert.False( partial.Ready );
      Assert.Contains( "1 of 2 segments have no complete graph", partial.Detail );

      IndexState busy = GraphVerdict( "Yellow", null, 524, 524, HEALTHY );
      Assert.False( busy.Ready );
      Assert.Contains( "the collection is not idle", busy.Detail );

      IndexState broken = GraphVerdict( "Green", "out of disk", 524, 524, HEALTHY );
      Assert.False( broken.Ready );
      Assert.Contains( "optimizer out of disk", broken.Detail );

      IndexState empty = GraphVerdict( "Green", null, 0, 0, new[] { "plain:0:0:0:-:0:0:0" } );
      Assert.False( empty.Ready );
      Assert.Contains( "the collection holds no points", empty.Detail );

      IndexState blind = TargetsHarness.Call( "QdrantVerdict", true, "Green", null, 524L, 524L, 4, 1L, 10L, "the server's telemetry could not be read (timeout)", null );
      Assert.False( blind.Ready );
      Assert.Contains( "so what searches used is unproven", blind.Detail );
      Assert.Contains( "timeout", blind.Detail );
   }

   /// <summary>
   /// The exact Qdrant target is ready when no search walked a graph, whether or not Qdrant built
   /// one, and says which; a search that walked a graph, or a busy server, makes it not ready.
   /// </summary>
   [Fact]
   public void QdrantExact_ReadyOnlyWhenNoSearchWalkedAGraph()
   {
      IndexState noGraph = ScanVerdict( "Green", 524, 0, new[] { "plain:300:0:1228800:-:0:80:0", "plain:224:0:917504:-:0:80:0" } );
      Assert.True( noGraph.Ready );
      Assert.Contains( "no index used, exact scan by design", noGraph.Detail );
      Assert.Contains( "no graph was built", noGraph.Detail );
      Assert.Contains( "unfiltered_plain 160", noGraph.Detail );

      IndexState bypassed = ScanVerdict( "Green", 524, 524, new[] { "hnsw:524:524:2146304:10000:0:0:40" } );
      Assert.True( bypassed.Ready );
      Assert.Contains( "a graph exists for 524 vectors but exact searches bypass it", bypassed.Detail );

      IndexState walked = ScanVerdict( "Green", 524, 524, new[] { "hnsw:524:524:2146304:10:9:0:0" } );
      Assert.False( walked.Ready );
      Assert.Contains( "9 segment searches walked an HNSW graph, so these are not exact scans", walked.Detail );

      IndexState busy = ScanVerdict( "Yellow", 524, 0, new[] { "plain:524:0:2146304:-:0:0:0" } );
      Assert.False( busy.Ready );
      Assert.Contains( "the collection is not idle (status Yellow, optimizer ok)", busy.Detail );

      IndexState blind = TargetsHarness.Call( "QdrantVerdict", false, "Green", null, 524L, 0L, 4, 10000L, 10000L, "no telemetry", null );
      Assert.False( blind.Ready );
   }

   /// <summary>
   /// A real VECTOR_SEARCH plan has a vector index operator on the DiskANN index; a real exact plan
   /// is a clustered index scan that read every row; the readers see the right table and counts.
   /// </summary>
   [Fact]
   public void Plan_ReadsRealSqlServerPlans()
   {
      dynamic seek = TargetsHarness.Call( "Plan", PLAN_SEEK );
      Assert.True( (bool)seek.VectorSeek );
      Assert.False( (bool)seek.Scan );
      Assert.Equal( new[]
      {
         "Nested Loops|||||10", "Vector Index Seek|GvbBenchTgtFixturea40ab667|gvb_gvbbench_tgtprobea40ab667_ann|vix_gvb_gvbbench_tgtprobea40ab667|DiskANN|10",
         "Clustered Index Seek|GvbBenchTgtFixturea40ab667|gvb_gvbbench_tgtprobea40ab667_ann|PK_gvb_gvbbench_tgtprobea40ab667_ann|Clustered|10",
      }, (string[])seek.Operators );
      Assert.Contains( "Vector Index Seek on index vix_gvb_gvbbench_tgtprobea40ab667", (string)seek.Describe );
      Assert.Contains( "IndexKind DiskANN", (string)seek.Describe );

      dynamic scan = TargetsHarness.Call( "Plan", PLAN_SCAN );
      Assert.False( (bool)scan.VectorSeek );
      Assert.True( (bool)scan.Scan );
      Assert.Contains( "Clustered Index Scan of GvbBenchTgtFixturea40ab667.gvb_gvbbench_tgtprobea40ab667_ann", (string)scan.Describe );
      Assert.Contains( "200 rows read, no vector index operator", (string)scan.Describe );
   }

   /// <summary>
   /// The DiskANN target is ready only when the index is an enabled DiskANN index covering every
   /// row, the real VECTOR_SEARCH plan uses it, and the exact search on the SAME table scans it.
   /// </summary>
   [Fact]
   public void SqlDiskAnn_ReadyOnlyWhenTheIndexExistsAndIsUsed()
   {
      const string SEEK = "Vector Index Seek|vix|DiskANN|10;Clustered Index Seek|pk|Clustered|10";
      const string SCAN = "Clustered Index Scan|pk|Clustered|200";
      IndexState ready = DiskAnn( 200, 200, false, "DiskANN", SEEK, 10, SCAN, 10 );
      Assert.True( ready.Ready );
      Assert.Equal( (long?)200, ready.IndexedVectors );
      Assert.Equal( (long?)200, ready.TotalVectors );
      Assert.Contains( "DiskANN index built and used", ready.Detail );
      Assert.Contains( "graph table rows 200 of 200 table rows", ready.Detail );
      Assert.Contains( "The exact mode searches the SAME table (dbo.t_ann, database Db)", ready.Detail );

      Assert.Contains( "the graph covers 150 of 200 rows", NotReady( DiskAnn( 200, 150, false, "DiskANN", SEEK, 10, SCAN, 10 ) ) );
      Assert.Contains( "the graph covers an unknown number of 200 rows", NotReady( DiskAnn( 200, null, false, "DiskANN", SEEK, 10, SCAN, 10 ) ) );
      Assert.Contains( "the index is disabled", NotReady( DiskAnn( 200, 200, true, "DiskANN", SEEK, 10, SCAN, 10 ) ) );
      Assert.Contains( "the index is Other", NotReady( DiskAnn( 200, 200, false, "Other", SEEK, 10, SCAN, 10 ) ) );
      Assert.Contains( "no vector index operator on that index", NotReady( DiskAnn( 200, 200, false, "DiskANN", SCAN, 10, SCAN, 10 ) ) );
      Assert.Contains( "no vector index operator on that index", NotReady( DiskAnn( 200, 200, false, "DiskANN", "Vector Index Seek|other|DiskANN|10", 10, SCAN, 10 ) ) );
      Assert.Contains( "or it returned no hits", NotReady( DiskAnn( 200, 200, false, "DiskANN", SEEK, 0, SCAN, 10 ) ) );
      Assert.Contains( "the exact search did not scan the table", NotReady( DiskAnn( 200, 200, false, "DiskANN", SEEK, 10, SEEK, 10 ) ) );
      Assert.Contains( "the exact search did not scan the table", NotReady( DiskAnn( 200, 200, false, "DiskANN", SEEK, 10, "Top|||10", 10 ) ) );
   }

   /// <summary>
   /// The plain SQL target is ready only when its table has no vector index and a real exact
   /// search is a table scan that returned hits.
   /// </summary>
   [Fact]
   public void Sql_ReadyOnlyWhenNoIndexIsUsed()
   {
      const string SCAN = "Clustered Index Scan|pk|Clustered|524";
      IndexState ready = TargetsHarness.Call( "ExactVerdict", 524L, false, SCAN, 10 );
      Assert.True( ready.Ready );
      Assert.Contains( "no index used, exact scan by design", ready.Detail );
      Assert.Contains( "sys.vector_indexes lists no index on the table", ready.Detail );
      Assert.Contains( "524 rows read, no vector index operator", ready.Detail );

      IndexState indexed = TargetsHarness.Call( "ExactVerdict", 524L, true, SCAN, 10 );
      Assert.False( indexed.Ready );
      Assert.Contains( "NOT an exact scan", indexed.Detail );
      Assert.Contains( "lists vix (DiskANN)", indexed.Detail );

      Assert.False( ( (IndexState)TargetsHarness.Call( "ExactVerdict", 524L, false, "Vector Index Seek|vix|DiskANN|10", 10 ) ).Ready );
      Assert.False( ( (IndexState)TargetsHarness.Call( "ExactVerdict", 524L, false, SCAN, 0 ) ).Ready );
      Assert.False( ( (IndexState)TargetsHarness.Call( "ExactVerdict", 524L, false, "Top|||10", 10 ) ).Ready );
   }

   /// <summary>
   /// The target sources keep the house rule of no em or en dashes, which the owner strips by hand.
   /// </summary>
   [Fact]
   public void Sources_HaveNoDashes()
   {
      string bench = Path.Combine( TargetsHarness.RepoRoot(), "src", "GenericVectorBuilder.Bench", "Targets" );
      string tests = Path.Combine( TargetsHarness.RepoRoot(), "tests", "GenericVectorBuilder.Engines.Tests", "Bench" );
      foreach( string file in Directory.EnumerateFiles( bench, "*.cs" ).Concat( Directory.EnumerateFiles( tests, "Targets*.cs" ) ) )
      {
         string text = File.ReadAllText( file );
         Assert.False( text.Contains( '\u2014' ) || text.Contains( '\u2013' ), $"{Path.GetFileName( file )} contains an em or en dash" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>Verdict of the HNSW target's expectation for a 4-segment collection with the target's thresholds.</summary>
   /// <param name="status">Server status.</param>
   /// <param name="optimizerError">Optimiser error or null.</param>
   /// <param name="points">points_count.</param>
   /// <param name="indexed">indexed_vectors_count.</param>
   /// <param name="segments">Segment specs.</param>
   /// <returns>The verdict.</returns>
   private static IndexState GraphVerdict( string status, string? optimizerError, long points, long indexed, string[] segments )
   {
      return TargetsHarness.Call( "QdrantVerdict", true, status, optimizerError, points, indexed, 4, 1L, 10L, null, segments );
   }

   /// <summary>Verdict of the exact target's expectation for a collection with server default thresholds.</summary>
   /// <param name="status">Server status.</param>
   /// <param name="points">points_count.</param>
   /// <param name="indexed">indexed_vectors_count.</param>
   /// <param name="segments">Segment specs.</param>
   /// <returns>The verdict.</returns>
   private static IndexState ScanVerdict( string status, long points, long indexed, string[] segments )
   {
      return TargetsHarness.Call( "QdrantVerdict", false, status, null, points, indexed, 4, 10000L, 10000L, null, segments );
   }

   /// <summary>DiskANN verdict from hand-made facts.</summary>
   /// <param name="rows">Table rows.</param>
   /// <param name="graphRows">Graph rows or null.</param>
   /// <param name="disabled">Index disabled.</param>
   /// <param name="type">Index type.</param>
   /// <param name="seek">Seek plan operators.</param>
   /// <param name="seekHits">Seek hits.</param>
   /// <param name="exact">Exact plan operators.</param>
   /// <param name="exactHits">Exact hits.</param>
   /// <returns>The verdict.</returns>
   private static IndexState DiskAnn( long rows, long? graphRows, bool disabled, string type, string seek, int seekHits, string exact, int exactHits )
   {
      return TargetsHarness.Call( "DiskAnnVerdict", rows, graphRows, disabled, type, "vix", seek, seekHits, exact, exactHits );
   }

   /// <summary>Asserts a verdict is not ready and returns its text.</summary>
   /// <param name="state">The verdict.</param>
   /// <returns>The detail.</returns>
   private static string NotReady( IndexState state )
   {
      Assert.False( state.Ready );
      Assert.StartsWith( "NOT ready:", state.Detail );
      return state.Detail;
   }

   #endregion Private Methods
}

/// <summary>
/// Compiles the repository's Bench target sources with the test scenarios (TargetsScenarios.cs,
/// TargetsContainerScenarios.cs, TargetsPinnedScenarios.cs and TargetsSettingsScenarios.cs) once, and calls the scenarios by
/// name. Shared by the pure and the live target tests.
/// </summary>
internal static class TargetsHarness
{
   #region Data Members

   private const string SCENARIOS_TYPE = "GenericVectorBuilder.Bench.UnderTest.TargetsScenarios";
   private const string CONTAINER_TYPE = "GenericVectorBuilder.Bench.UnderTest.TargetsContainerScenarios";
   private const string PINNED_TYPE = "GenericVectorBuilder.Bench.UnderTest.TargetsPinnedScenarios";
   private const string SETTINGS_TYPE = "GenericVectorBuilder.Bench.UnderTest.TargetsSettingsScenarios";
   private const string IMPLICIT_USINGS = "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; "
      + "global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;";
   private static readonly Lazy<Assembly> COMPILED = new( Compile );

   /// <summary>Longest a settings scenario may take.</summary>
   public static readonly TimeSpan SCENARIO_LIMIT = TimeSpan.FromMinutes( 3 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Calls a synchronous scenario method.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result, usable as dynamic or cast.</returns>
   public static dynamic Call( string method, params object?[] arguments )
   {
      return COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
   }

   /// <summary>
   /// Calls a synchronous method of the container-address scenarios.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result, usable as dynamic or cast.</returns>
   public static dynamic CallContainer( string method, params object?[] arguments )
   {
      return COMPILED.Value.GetType( CONTAINER_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
   }

   /// <summary>
   /// Calls an asynchronous method of the container-address scenarios and waits for it, within a limit.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="limit">Longest wait.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The task's result.</returns>
   public static async Task<dynamic> CallContainerAsync( string method, TimeSpan limit, params object?[] arguments )
   {
      var task = (Task)COMPILED.Value.GetType( CONTAINER_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
      Task finished = await Task.WhenAny( task, Task.Delay( limit ) );
      Assert.True( finished == task, $"{method} did not finish within {limit.TotalMinutes:0.#} minutes" );
      await task;
      return task.GetType().GetProperty( "Result" )!.GetValue( task )!;
   }

   /// <summary>
   /// Calls a synchronous method of the pinned-container scenarios (the benchmark's own SQL Server and
   /// Qdrant containers and the cpuset every engine container starts with).
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result, usable as dynamic or cast.</returns>
   public static dynamic CallPinned( string method, params object?[] arguments )
   {
      return COMPILED.Value.GetType( PINNED_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
   }

   /// <summary>
   /// Calls a method of the settings, image, data-folder and ClickHouse-reset scenarios and waits for it
   /// for at most <see cref="SCENARIO_LIMIT"/>. These scenarios run their own asynchronous work to the end
   /// before they return, so the call runs on a pool thread and the test fails, instead of hanging, when the
   /// code under test never returns.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result, usable as dynamic or cast.</returns>
   public static dynamic CallSettings( string method, params object?[] arguments )
   {
      MethodInfo call = COMPILED.Value.GetType( SETTINGS_TYPE )!.GetMethod( method )!;
      Task<object?> run = Task.Run( () => call.Invoke( null, arguments ) );
      Assert.True( run.Wait( SCENARIO_LIMIT ), $"{method} did not finish within {SCENARIO_LIMIT.TotalMinutes:0} minutes" );
      return run.Result!;
   }

   /// <summary>
   /// Calls an asynchronous method of the pinned-container scenarios and waits for it, within a limit.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="limit">Longest wait.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The task's result.</returns>
   public static async Task<dynamic> CallPinnedAsync( string method, TimeSpan limit, params object?[] arguments )
   {
      var task = (Task)COMPILED.Value.GetType( PINNED_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
      Task finished = await Task.WhenAny( task, Task.Delay( limit ) );
      Assert.True( finished == task, $"{method} did not finish within {limit.TotalMinutes:0.#} minutes" );
      await task;
      return task.GetType().GetProperty( "Result" )!.GetValue( task )!;
   }

   /// <summary>
   /// Calls an asynchronous scenario method and waits for it, within a limit.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="limit">Longest wait.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The task's result.</returns>
   public static async Task<dynamic> CallAsync( string method, TimeSpan limit, params object?[] arguments )
   {
      var task = (Task)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
      Task finished = await Task.WhenAny( task, Task.Delay( limit ) );
      Assert.True( finished == task, $"{method} did not finish within {limit.TotalMinutes:0} minutes" );
      await task;
      return task.GetType().GetProperty( "Result" )!.GetValue( task )!;
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file.
   /// </summary>
   /// <returns>The repository root.</returns>
   public static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found." );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Compiles the Bench target sources and the scenarios into an in-memory assembly.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string root = RepoRoot();
      string bench = Path.Combine( root, "src", "GenericVectorBuilder.Bench" );
      var parse = new CSharpParseOptions( LanguageVersion.Latest );
      List<SyntaxTree> trees = Directory.EnumerateFiles( Path.Combine( bench, "Targets" ), "*.cs" ).Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), parse, f ) ).ToList();
      string scenarios = Path.Combine( root, "tests", "GenericVectorBuilder.Engines.Tests", "Bench", "TargetsScenarios.cs" );
      trees.Add( CSharpSyntaxTree.ParseText( File.ReadAllText( scenarios ), parse.WithPreprocessorSymbols( "BENCH_UNDER_TEST" ), scenarios ) );
      foreach( string file in new[] { "TargetsContainerScenarios.cs", "TargetsPinnedScenarios.cs", "TargetsSettingsScenarios.cs" } )
      {
         string path = Path.Combine( root, "tests", "GenericVectorBuilder.Engines.Tests", "Bench", file );
         trees.Add( CSharpSyntaxTree.ParseText( File.ReadAllText( path ), parse.WithPreprocessorSymbols( "BENCH_UNDER_TEST" ), path ) );
      }

      trees.Add( CSharpSyntaxTree.ParseText( IMPLICIT_USINGS, parse ) );
      List<string> platform = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! ).Split( Path.PathSeparator ).ToList();
      List<string> extra = BenchOnlyPackages( bench, platform.Select( Path.GetFileName ).ToHashSet( StringComparer.OrdinalIgnoreCase ) );
      AssemblyLoadContext.Default.Resolving += ( context, name ) =>
         extra.FirstOrDefault( p => Path.GetFileNameWithoutExtension( p ).Equals( name.Name, StringComparison.OrdinalIgnoreCase ) ) is string path ? context.LoadFromAssemblyPath( path ) : null;
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "BenchTargetsUnderTest", trees, platform.Concat( extra ).Select( p => MetadataReference.CreateFromFile( p ) ), options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   /// <summary>
   /// Runtime files of the packages the benchmark restored that this test host does not
   /// already have, read from the benchmark's project.assets.json.
   /// </summary>
   /// <param name="bench">Benchmark project folder.</param>
   /// <param name="loaded">File names already on the test host's platform list.</param>
   /// <returns>Full paths of the extra assemblies.</returns>
   private static List<string> BenchOnlyPackages( string bench, HashSet<string?> loaded )
   {
      string assets = Path.Combine( bench, "obj", "project.assets.json" );
      Assert.True( File.Exists( assets ), $"Restore the benchmark project first (no {assets})." );
      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( assets ) );
      string packages = doc.RootElement.GetProperty( "packageFolders" ).EnumerateObject().First().Name;
      JsonElement libraries = doc.RootElement.GetProperty( "libraries" );
      var extra = new List<string>();
      foreach( JsonProperty library in doc.RootElement.GetProperty( "targets" ).EnumerateObject().First().Value.EnumerateObject() )
      {
         if( !library.Value.TryGetProperty( "runtime", out JsonElement runtime ) || library.Value.GetProperty( "type" ).GetString() != "package" )
         {
            continue;
         }

         string folder = libraries.GetProperty( library.Name ).GetProperty( "path" ).GetString()!;
         extra.AddRange( runtime.EnumerateObject().Select( r => r.Name ).Where( r => r.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) && !loaded.Contains( Path.GetFileName( r ) ) )
            .Select( r => Path.Combine( packages, folder, r ) ) );
      }

      return extra;
   }

   #endregion Private Methods
}
