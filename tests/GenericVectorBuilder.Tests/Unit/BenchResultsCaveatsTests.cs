using System.Text.Json.Nodes;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The known-problems box, the warm-up line and the engine settings block of the summary page are built
/// from the data of the consolidated.json being shown, never from a table keyed by a folder's name.
/// Why: the 5 Oct list was attached to the name "published-2026-10-05"; the folder was renamed after the
/// independent review blocked it, and a page served under another name would have shown the general list
/// under numbers the list was written against. Every file the tests read here is built in the test or
/// written to a folder of its own under the test output, so none depends on a folder of bench-results.
/// </summary>
public partial class BenchResultsSummaryTests
{
   #region Data Members

   private const string DEFAULT_METHOD = "warm-up at the pass's own concurrency for at least 15 s and at least 20 searches (at most 120 s); then a 3 s trial that must land within 10% of the settled figure; one extension of 30 s to 120 s if it does not; rehearsal of every pass type for 30 s before the first timed pass";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Each caveat comes from the file: the run count, the collection size, the CPU partition, one sentence for
   /// every kind of flag with the engines that carry it, the runs left out, the engines left out of the table, and
   /// what the file records about an in-memory engine's durability. A sentence the file gives no data for is not
   /// there.
   /// </summary>
   [Fact]
   public void Caveats_AreBuiltFromTheFilesOwnData()
   {
      BenchSummary summary = BenchSummaryReader.FromConsolidated( ModernFile().ToJsonString() );

      string html = BenchSummaryHtml.Caveats( summary );

      Assert.Contains( "<li>These are medians of 3 runs. The small markers after an engine name carry the warnings the consolidate command computed for them; the report&#39;s Flags and Notes sections have the evidence.</li>", html );
      Assert.Contains( "<li>524 vectors measures the cost of each call, through each engine&#39;s .NET client library, more than how an engine scales.</li>", html );
      Assert.Contains( "(client 0-1,4-5; engines 2-3,6-7)", html );
      Assert.Equal( 1, System.Text.RegularExpressions.Regex.Matches( html, "did not report a finished index" ).Count );
      Assert.Contains( "so the search may have been a scan or a half-built index. Engines: Oracle 23ai Free.", html );
      Assert.Contains( "Unsettled: the engine was still starting, building or compacting when it was timed. Engines: Redis and Oracle 23ai Free.", html );
      Assert.Contains( "The test client and the engine ran on the same CPU cores and may have slowed each other. Engines: sqlite-vec.", html );
      Assert.Contains( "<li>1 run was left out of the medians: run4: settings differ from the runs used: warmupMethod missing vs a time based method.</li>", html );
      Assert.Contains( "<li>MongoDB Atlas Local was measured but is not in the table; the reason is listed under it.</li>", html );
      Assert.Contains( "<li>Redis holds everything in memory. As recorded: save &quot;300 1&quot; and appendonly no: a crash loses every write since the last snapshot.</li>", html );
      Assert.DoesNotContain( "Qdrant (HNSW) never built", html );
      Assert.DoesNotContain( "one connection that runs one search at a time", html );
   }

   /// <summary>
   /// The same data gives the same caveats whatever the folder is called, and the page keeps no list of
   /// caveats for a dated folder: no member of the summary class is named for a date.
   /// </summary>
   [Fact]
   public void Caveats_DoNotDependOnAFolderName()
   {
      string first = BenchSummaryHtml.Caveats( BenchSummaryReader.FromConsolidated( ModernFile().ToJsonString() ) );
      string second = BenchSummaryHtml.Caveats( BenchSummaryReader.FromConsolidated( ModernFile().ToJsonString() ) );

      Assert.Equal( first, second );
      Assert.DoesNotContain( typeof( BenchSummaryHtml ).GetFields().Select( f => f.Name ), n => n.Contains( "2026", StringComparison.Ordinal ) );
      Assert.DoesNotContain( typeof( BenchSummaryHtml ).GetMethods().Select( m => m.Name ), n => n.Contains( "2026", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A file in the first published shape records nothing about itself, so it gets the list written for that
   /// shape; with no summary at all (the numbers could not be read) the box says no list could be derived;
   /// a one-run file says there is no spread.
   /// </summary>
   [Fact]
   public void Caveats_OfAnOldFormatFile_ANullSummary_AndOneRun()
   {
      string old = BenchSummaryHtml.Caveats( BenchSummaryReader.FromConsolidated( BenchResultsFixtures.OLD_PUBLISHED_JSON ) );
      string none = BenchSummaryHtml.Caveats( null );
      string oneRun = BenchSummaryHtml.Caveats( BenchSummaryReader.FromConsolidated( ReportJson( 1, Settings( "performance", PARTITION, "Release", 524 ), new[] { Summary( "qdrant", 3000, 3000, 3000, 1.5, 1.0 ) }, Array.Empty<( string, string, string, int )>() ) ) );

      Assert.Contains( "Qdrant (HNSW) never built its HNSW index at 524 points", old );
      Assert.Contains( "Redis holds everything in memory. As set up here it snapshots every 5 minutes", old );
      Assert.Contains( "<li>The numbers above could not be read, so no list of problems could be derived from them.</li>", none );
      Assert.Contains( "This is one run, so no spread is known.", oneRun );
      Assert.DoesNotContain( "Qdrant (HNSW) never built", oneRun );
   }

   /// <summary>
   /// The conditions line of a consolidated file states the warm-up method from the file's own settings, in the
   /// run's numbers, and never calls the minimum count of searches the method; a file that only has the count
   /// says so.
   /// </summary>
   [Fact]
   public void WarmupLine_FromAConsolidatedFile_IsTheRecordedMethod()
   {
      JsonObject settings = Settings( "performance", PARTITION, "Release", 524 );
      settings["warmupMethod"] = new JsonObject { ["description"] = DEFAULT_METHOD, ["timeBased"] = true };

      BenchSummary summary = BenchSummaryReader.FromConsolidated( ReportJson( 3, settings, new[] { Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 ) }, Array.Empty<( string, string, string, int )>() ) );
      string html = BenchSummaryHtml.Block( summary, "note", null );

      Assert.Equal( DEFAULT_METHOD, summary.Conditions!.WarmupMethod );
      Assert.Contains( $"<p class=\"bench-warmup muted\">Before each timed pass: {DEFAULT_METHOD.Replace( "'", "&#39;" )}.</p>", html );
      Assert.DoesNotContain( "20 warm-up searches before each timed pass", html );
      string countOnly = BenchSummaryHtml.Block( BenchSummaryReader.FromConsolidated( ReportJson( 3, Settings( "performance", PARTITION, "Release", 524 ), new[] { Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 ) }, Array.Empty<( string, string, string, int )>() ) ), "note", null );
      Assert.Contains( "at least 20 warm-up searches (the rest of the warm-up method was not recorded in these results)", countOnly );
   }

   /// <summary>
   /// A single run's page describes the method from the run's own method notes with the same words as the
   /// consolidated report (the same text is asserted in the Bench tool's tests), and takes its numbers from the
   /// notes, so a run made with another method says so.
   /// </summary>
   [Fact]
   public void WarmupLine_FromARunsNotes_FollowsTheNotesNumbers()
   {
      string[] notes = MethodNotes( 15, 20, 120, 3, 10, 30, 120, 30 );
      string run = new JsonObject { ["notes"] = new JsonArray( notes.Select( n => (JsonNode)JsonValue.Create( n )! ).ToArray() ), ["targets"] = new JsonArray() }.ToJsonString();
      string other = new JsonObject { ["notes"] = new JsonArray( MethodNotes( 12, 40, 90, 4, 8, 25, 80, 45 ).Select( n => (JsonNode)JsonValue.Create( n )! ).ToArray() ), ["targets"] = new JsonArray() }.ToJsonString();

      Assert.Equal( DEFAULT_METHOD, BenchSummaryReader.FromRunResults( run ).Conditions!.WarmupMethod );
      Assert.Equal( "warm-up at the pass's own concurrency for at least 12 s and at least 40 searches (at most 90 s); then a 4 s trial that must land within 8% of the settled figure; one extension of 25 s to 80 s if it does not; rehearsal of every pass type for 45 s before the first timed pass",
         BenchSummaryReader.FromRunResults( other ).Conditions!.WarmupMethod );
      Assert.Null( BenchSummaryReader.FromRunResults( """{ "notes": [ "Warm-up: every timed pass started with its own untimed warm-up of 25 searches." ], "targets": [] }""" ).Conditions!.WarmupMethod );
   }

   /// <summary>
   /// The engine settings block lists, for every engine in the file, what the runs recorded (description, index,
   /// search settings, engine settings, durability, engine files) with "not recorded" for the rest, escapes
   /// every text, is closed by default, and is absent from a file that carries none.
   /// </summary>
   [Fact]
   public void EngineSettings_ArePrintedFromTheFile_AndEscaped()
   {
      BenchSummary summary = BenchSummaryReader.FromConsolidated( ModernFile().ToJsonString() );

      string html = BenchSummaryHtml.EngineSettings( summary );

      Assert.StartsWith( "<details class=\"bench-engine-config\"><summary>Engine settings as the runs recorded them</summary>", html );
      Assert.DoesNotContain( "<details open", html );
      Assert.Contains( "<td>ClickHouse</td><td>ClickHouse 26.3.39.7 (MergeTree, system log tables off)</td><td>vector_similarity HNSW &lt;b&gt;</td><td>M=16</td><td>logger.level=warning</td><td>Acknowledged inserts are not fsynced.</td><td>clickhouse.compose.yaml 0123456789ab</td>", html );
      Assert.Contains( "<td>Redis</td><td>Redis 8.10.2</td><td><span class=\"muted\">not recorded</span></td>", html );
      Assert.DoesNotContain( "<b>", html );
      Assert.Equal( string.Empty, BenchSummaryHtml.EngineSettings( BenchSummaryReader.FromConsolidated( SYNTHETIC ) ) );
      Assert.Contains( "Runs that differ in any of it are refused for that engine, never merged", html );
   }

   /// <summary>
   /// The list page takes its caveats from the newest published folder's own data, written to a folder of its own
   /// under the test output: a folder name the page has never heard of gets the same derived list, the settings
   /// block and the warm-up line, and the blocked folder beside it is not read.
   /// </summary>
   [Fact]
   public void ListPage_ServesCaveatsFromThePublishedFolderOwnData()
   {
      string root = Path.Combine( AppContext.BaseDirectory, "bench-caveat-tests", Guid.NewGuid().ToString( "N" ) );
      try
      {
         WriteFolder( root, "published-2031-01-02", ModernFile().ToJsonString() );
         WriteFolder( root, "blocked-2031-01-03-v9", """{ "redis": { "runs": 3, "qps8": { "median": 1 }, "p50": { "median": 1 }, "recall": { "median": 1 } } }""" );

         string html = BenchResultsEndpoints.ListPageHtml( root );

         Assert.Contains( "Numbers from <a href=\"/bench-results/published-2031-01-02\">published-2031-01-02</a>.", html );
         Assert.Contains( "<li>1 run was left out of the medians:", html );
         Assert.Contains( "Engine settings as the runs recorded them", html );
         Assert.Contains( "Before each timed pass: warm-up at the pass&#39;s own concurrency for at least 15 s", html );
         Assert.DoesNotContain( "Qdrant (HNSW) never built", html );
         Assert.DoesNotContain( "one connection that runs one search at a time", html );
      }
      finally
      {
         Directory.Delete( root, true );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A consolidated.json in the shape the consolidate command writes, with what the caveats read: settings with a
   /// partition, rows and the warm-up method, five engines (Redis, Oracle, sqlite-vec, ClickHouse and Qdrant)
   /// with flags, durability and engine texts, a run left out, and an engine left out of the table.
   /// </summary>
   /// <returns>The file as a JSON tree.</returns>
   private static JsonObject ModernFile()
   {
      JsonObject settings = Settings( "performance", "client 0-1,4-5; engines 2-3,6-7", "Release", 524 );
      settings["warmupMethod"] = new JsonObject { ["description"] = DEFAULT_METHOD, ["timeBased"] = true };
      settings["searchSettings"] = new JsonObject { ["clickhouse"] = "M=16" };
      JsonObject redis = Summary( "redis", 5000, 4900, 5100, 0.35, 1.0 );
      redis["engines"] = new JsonArray( "Redis 8.10.2" );
      redis["durabilities"] = new JsonArray( "save \"300 1\" and appendonly no: a crash loses every write since the last snapshot." );
      JsonObject oracle = Summary( "oracle", 700, 690, 710, 5.5, 1.0 );
      JsonObject sqlite = Summary( "sqlitevec", 450, 440, 460, 2.2, 1.0 );
      JsonObject clickhouse = Summary( "clickhouse", 480, 470, 495, 6.2, 0.99 );
      clickhouse["engines"] = new JsonArray( "ClickHouse 26.3.39.7 (MergeTree, system log tables off)" );
      clickhouse["indexes"] = new JsonArray( "vector_similarity HNSW <b>" );
      clickhouse["engineSettings"] = new JsonArray( "logger.level=warning" );
      clickhouse["engineFiles"] = new JsonArray( "clickhouse.compose.yaml 0123456789ab" );
      clickhouse["perRun"] = new JsonArray( new JsonObject { ["run"] = "run1", ["durability"] = "Acknowledged inserts are not fsynced." } );
      JsonNode root = JsonNode.Parse( ReportJson( 3, settings, new[] { redis, oracle, sqlite, clickhouse }, new[]
      {
         ( "redis", "unsettled-target", "not settled", 3 ), ( "oracle", "unsettled-target", "not settled", 3 ), ( "sqlitevec", "client-engine-share-cores", "embedded engine runs inside the client process", 3 ),
         ( "oracle", "index-not-ready-after-load", "ready false", 3 ), ( "oracle", "index-not-ready-after-search", "ready false", 3 ),
      } ) )!;
      root["dropped"] = new JsonArray( new JsonObject { ["name"] = "run4", ["folder"] = "/x/run4", ["reason"] = "settings differ from the runs used: warmupMethod missing vs a time based method" } );
      root["withheld"] = new JsonArray( new JsonObject { ["target"] = "mongodb", ["kind"] = "refused", ["runs"] = new JsonArray( "run1" ), ["reason"] = "refused: engine files of mongodb differ" } );
      return root.AsObject();
   }

   /// <summary>
   /// The two method notes a current run records, built from the numbers given (the defaults of the call in the
   /// first test are the real method's). The same text is used by the Bench tool's tests.
   /// </summary>
   /// <param name="warm">Shortest warm-up, seconds.</param>
   /// <param name="searches">Fewest searches in a warm-up.</param>
   /// <param name="warmCap">Longest warm-up, seconds.</param>
   /// <param name="trial">Settle trial, seconds.</param>
   /// <param name="tolerance">How far the trial may lie from the settled figure, percent.</param>
   /// <param name="extensionMin">Shortest extension, seconds.</param>
   /// <param name="extensionCap">Longest extension, seconds.</param>
   /// <param name="rehearsal">Rehearsal of each pass type, seconds.</param>
   /// <returns>The rehearsal note and the warm-up note.</returns>
   private static string[] MethodNotes( double warm, int searches, double warmCap, double trial, double tolerance, double extensionMin, double extensionCap, double rehearsal )
   {
      return new[]
      {
         $"Preparation, untimed, before any timed pass: a rehearsal of every pass type at its own concurrency for {rehearsal} s each, through the same code the passes use.",
         $"Warm-up and settle check, untimed, right before every timed pass: the pass's own search at the pass's own number of searchers for at least {warm} s and at least {searches} searches (at most {warmCap} s), "
            + $"read in windows of at least 2 s and 100 searches; then a {trial} s trial of the same pass. The trial's figure (p50 with one searcher, QPS with several) must lie within {tolerance}% "
            + $"of the warm-up's settled figure (the median of its last 3 windows, which must agree within 5%); if not, the warm-up is extended once (at least {extensionMin} s, until its windows agree, at most {extensionCap} s), "
            + "a second trial is taken and the warm-up runs again before the pass.",
      };
   }

   /// <summary>
   /// Writes a folder with a consolidated.json under a root.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="name">Folder name.</param>
   /// <param name="json">File text.</param>
   private static void WriteFolder( string root, string name, string json )
   {
      string folder = Path.Combine( root, name );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "consolidated.json" ), json );
   }

   #endregion Private Methods
}
