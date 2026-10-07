using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The pages the second dry check of the v8 pipeline (commit 1ebbb3f, on the real v7 data) left open on the web side, proven through the rendered HTML only:
/// the summary tables show the search mode of each engine with the label of the fact behind it, never a stronger label than the report gave; the changes of
/// recorded setup the threshold basis does not compare across are a table; a run page flags what the summary flags from the same recorded fields; the raw
/// report of a run says it still holds the sentences the page leaves out; a folder named published whose file is a record carries a banner; and every
/// sentence-length text the page prints is the file's, a fixed legend, or a named template, so a clause typed into a page template fails a test.
/// Why the HTML and not the model: a reader sees the page, so each test reads what a reader sees. Each string a test uses that is new in this change is
/// written out here and not read from <see cref="BenchLegends"/>, so the same tests can be run against the tree before the change and shown to fail there.
/// The real files come from the BenchResultsFixtures folder (see <see cref="BenchResultsFixtureFiles"/>): the dry check's consolidated.json, which the frozen
/// consolidate command wrote, and the elasticsearch, mongodb and redis targets of a real v7 run.
/// </summary>
public class BenchResultsV8eFixTests : IDisposable
{
   #region Data Members

   private const string DRY = "v8-dry.consolidated.json";
   private const string V7_RUN = "20261006-130619-eshoponweb";
   private const string SET = "published-2026-10-07";
   private const string OWN_REPORT = "recorded (engine's own report)";
   private static readonly string[] APPROXIMATE_TABLES = { "p50Ms", "qps1", "qps8" };

   /// <summary>
   /// The page's named templates for text that mixes a fixed label with values from the file; every one of them is short, holds no claim and is listed here so
   /// a new template is a visible change to this test.
   /// </summary>
   private static readonly Regex[] TEMPLATES =
   {
      new( @"^Machine: .+$", RegexOptions.Compiled ),
      new( @"^Clock \S+: .+$", RegexOptions.Compiled ),
      new( @"^(Clock|Machine): .+\.$", RegexOptions.Compiled ),
      new( @"^First session \S+; second session \S+; median absolute move .+; largest move .+$", RegexOptions.Compiled ),
      new( @"^Threshold [0-9.]+%; basis points [0-9]+; ratio [0-9.]+$", RegexOptions.Compiled ),
      new( @"^[^:]+: [0-9.]+%, \S+$", RegexOptions.Compiled ),
      new( @"^runs \S+, \S+; values [0-9., ]+$", RegexOptions.Compiled ),
      new( @"^\S+ and \S+, minimum ratio [0-9.]+$", RegexOptions.Compiled ),
      new( @"^Run started .+ UTC\. Data: .+\. Queries: .+\.$", RegexOptions.Compiled ),
      new( @"^Numbers from .+\.$", RegexOptions.Compiled ),
      new( @"^.+ \(\d+ of \d+ engines\)$", RegexOptions.Compiled ),
      new( @"^(afterLoad|afterSearch): the engine's index state read not ready, .+$", RegexOptions.Compiled ),
      new( @"^\S+: CPUs busy outside the benchmark .+$", RegexOptions.Compiled ),
      new( @"^\S+: engine CPUs median .+ MHz, client CPUs median .+ MHz$", RegexOptions.Compiled ),
   };

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public BenchResultsV8eFixTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-page-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The three tables that rank by one searcher or eight carry a "Search" column and the exact-mode table, whose engines all ran exact, does not. The dry
   /// check found the summary tables without the column the report prints.
   /// </summary>
   [Fact]
   public void TheSearchColumn_IsOnTheThreeApproximateTables_AndNotOnTheExactTable()
   {
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsFixtureFiles.Text( DRY ) ) );

      foreach( string metric in APPROXIMATE_TABLES )
      {
         Assert.Contains( "<th data-column=\"search\">Search</th>", TableHead( html, metric ) );
      }

      Assert.DoesNotContain( "data-column=\"search\"", TableHead( html, "exactP50Ms" ) );
   }

   /// <summary>
   /// In the real output of the frozen command, every row of the three tables prints the mode its file gives it ("approximate" or "exact") in its search cell,
   /// 19 rows in each, and the cell is the one the file's search fact for the engine backs: the label under the mode is the confidence of that fact.
   /// </summary>
   [Fact]
   public void TheSearchCell_OfEveryRow_IsTheFilesModeAndTheLabelOfTheFactBehindIt()
   {
      string json = BenchResultsFixtureFiles.Text( DRY );
      using JsonDocument doc = JsonDocument.Parse( json );
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      int checkedRows = 0;

      foreach( JsonElement metric in doc.RootElement.GetProperty( "metrics" ).EnumerateArray().Where( m => APPROXIMATE_TABLES.Contains( m.GetProperty( "metric" ).GetString() ) ) )
      {
         string name = metric.GetProperty( "metric" ).GetString()!;
         foreach( JsonElement row in metric.GetProperty( "rows" ).EnumerateArray() )
         {
            string target = row.GetProperty( "target" ).GetString()!;
            string mode = row.GetProperty( "searchMode" ).GetString()!;
            string[] labels = FactsOf( doc.RootElement, target ).Where( f => f.GetProperty( "kind" ).GetString() == "search" && f.GetProperty( "mode" ).GetString() == mode )
               .Select( f => f.GetProperty( "confidence" ).GetString()! ).Distinct().ToArray();
            string cell = Regex.Match( RowOf( html, name, target ), "<td data-column=\"search\">(.*?)</td>", RegexOptions.Singleline ).Groups[1].Value;

            Assert.Equal( mode, Regex.Match( cell, "^[a-z]+" ).Value );
            Assert.NotEmpty( labels );
            Assert.Contains( $">{WebUtility.HtmlEncode( string.Join( ", ", labels ) )}</small>", cell );
            checkedRows++;
         }
      }

      Assert.Equal( 57, checkedRows );
   }

   /// <summary>
   /// A fact the report labels "recorded (engine's own report)" is printed under that label in the search column and in the facts table, and never under
   /// "measured": the page prints the label as written and does not map it to a stronger one. The dry check found the report call the same fact "measured" in
   /// one table and "the engine's own report" in another; the label is the report's to give, and the page must not upgrade it.
   /// </summary>
   [Fact]
   public void ALabelTheReportGivesAFact_IsPrintedAsWritten_AndNeverUpgraded()
   {
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() ) );
      string target = BenchResultsV8Fixture.OWN_REPORT_TARGET;

      foreach( string metric in APPROXIMATE_TABLES )
      {
         string cell = Regex.Match( RowOf( html, metric, target ), "<td data-column=\"search\">(.*?)</td>", RegexOptions.Singleline ).Groups[1].Value;
         Assert.Contains( ">recorded (engine&#39;s own report)</small>", cell );
         Assert.DoesNotContain( "measured", cell );
      }

      string facts = Regex.Match( html, $"<tr data-target=\"{target}\"><td>[^<]*</td><td><ul class=\"bench-facts\">.*?</ul>", RegexOptions.Singleline ).Value;
      Assert.Contains( "<span class=\"bench-label\">recorded (engine&#39;s own report)</span>", facts );
      Assert.DoesNotContain( "<span class=\"bench-label\">measured</span>", Regex.Match( facts, "<li data-kind=\"search\">.*?</li>", RegexOptions.Singleline ).Value );
      Assert.Equal( OWN_REPORT, BenchResultsV8Fixture.OWN_REPORT_LABEL );
   }

   /// <summary>
   /// In the real output every fact of every engine is printed with its kind, its mode when it has one, its label and its source as the file gives them:
   /// 116 facts, none dropped and none relabelled, so the confidence the page shows is the report's confidence for that fact.
   /// </summary>
   [Fact]
   public void EveryFact_OfTheRealOutput_IsPrintedWithItsKindModeLabelAndSourceAsWritten()
   {
      string json = BenchResultsFixtureFiles.Text( DRY );
      using JsonDocument doc = JsonDocument.Parse( json );
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      int facts = 0;

      foreach( JsonElement why in doc.RootElement.GetProperty( "why" ).EnumerateArray() )
      {
         string target = why.GetProperty( "target" ).GetString()!;
         string row = Regex.Match( html, $"<tr data-target=\"{Regex.Escape( target )}\"><td>[^<]*</td><td><ul class=\"bench-facts\">.*?</ul>", RegexOptions.Singleline ).Value;
         string[] printed = Regex.Matches( row, "<li data-kind=.*?</li>", RegexOptions.Singleline ).Select( m => Flat( m.Value ) ).ToArray();
         JsonElement[] expected = why.GetProperty( "facts" ).EnumerateArray().ToArray();

         Assert.Equal( expected.Length, printed.Length );
         for( int i = 0; i < expected.Length; i++ )
         {
            string kind = expected[i].GetProperty( "kind" ).GetString()!;
            string mode = expected[i].TryGetProperty( "mode", out JsonElement m ) && m.ValueKind == JsonValueKind.String ? $" ({m.GetString()})" : string.Empty;
            string label = expected[i].GetProperty( "confidence" ).GetString()!;
            Assert.StartsWith( $"{kind}{mode} {expected[i].GetProperty( "text" ).GetString()} ({label}; {expected[i].GetProperty( "source" ).GetString()}", printed[i] );
            facts++;
         }
      }

      Assert.Equal( 116, facts );
   }

   /// <summary>
   /// A metric row whose search mode the engine's own search fact contradicts is refused by the reader, naming the engine, the table and both modes: the
   /// column and the fact are two prints of one state, and a page that showed "approximate" beside a fact that says "exact" would show the reader a label
   /// its own facts table contradicts.
   /// </summary>
   [Fact]
   public void ASearchModeTheEnginesFactContradicts_IsRefused()
   {
      string json = BenchResultsV8Fixture.Json( root => Row( root, "p50Ms", "redis" )["searchMode"] = "exact" );

      string message = Assert.Throws<InvalidDataException>( () => BenchConsolidatedReader.Read( json ) ).Message;

      Assert.Contains( "metrics[p50Ms].redis.searchMode is \"exact\"", message );
      Assert.Contains( "\"approximate\"", message );
   }

   /// <summary>
   /// A mode the report words differently in its table and in its fact but means the same ("approximate" and "approximate (engine's own report)") is not a
   /// contradiction and the page draws it, with the fact's label under the row's mode; "exact" against "approximate" still is.
   /// </summary>
   [Fact]
   public void ASearchModeWordedTwoWays_IsOneMode_AndTheCellStillPrintsTheFactsLabel()
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         foreach( JsonNode? row in root["metrics"]!.AsArray().SelectMany( m => m!["rows"]!.AsArray() ).Where( r => r!["target"]!.GetValue<string>() == "redis" ) )
         {
            row!["searchMode"] = row["searchMode"] is null ? null : "approximate (engine's own report)";
         }
      } );

      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      string cell = Regex.Match( RowOf( html, "p50Ms", "redis" ), "<td data-column=\"search\">(.*?)</td>", RegexOptions.Singleline ).Groups[1].Value;

      Assert.StartsWith( "approximate (engine&#39;s own report)<br>", cell );
      Assert.Contains( ">measured</small>", cell );
      Assert.Throws<InvalidDataException>( () => BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( root => Row( root, "p50Ms", "redis" )["searchMode"] = "exact (engine's own report)" ) ) );
   }

   /// <summary>
   /// A file with no facts table still prints the mode of each row, with no label under it and a legend that does not point at a table that is not there.
   /// </summary>
   [Fact]
   public void WithoutAFactsTable_TheSearchCellHasNoLabel_AndTheLegendDoesNotPointAtOne()
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         root["sessions"]!.AsArray().RemoveAt( 1 );
         foreach( JsonNode? row in root["metrics"]!.AsArray().SelectMany( m => m!["rows"]!.AsArray() ) )
         {
            row!["perSession"]!.AsObject().Remove( "v8" );
         }

         root.Remove( "why" );
         root.Remove( "drift" );
         root.Remove( "basis" );
         root.Remove( "clock" );
         foreach( JsonNode? row in root["metrics"]!.AsArray().SelectMany( m => m!["rows"]!.AsArray() ) )
         {
            row!["notSeparatedFrom"]!.AsArray().Clear();
         }
      } );

      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      string cell = Regex.Match( RowOf( html, "p50Ms", "redis" ), "<td data-column=\"search\">(.*?)</td>", RegexOptions.Singleline ).Groups[1].Value;

      Assert.Equal( "approximate", cell );
      Assert.Contains( "The search column gives the mode the report states for the engine&#39;s search.", html );
      Assert.DoesNotContain( "The facts table below", html );
   }

   /// <summary>
   /// A note printed beside an engine's name (Redis holds its data in memory) prints the sources the file binds it to, in a closed block, on the summary
   /// tables and on the page of a run the set uses: every factual sentence the page prints has its source path printed. The dry-check build printed the
   /// note's words and not the path of the saved page or compose file behind them.
   /// </summary>
   [Fact]
   public void ARowNote_PrintsItsSources_OnTheSummaryAndOnTheRunPage()
   {
      string json = BenchResultsFixtureFiles.Text( DRY );
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement redis = doc.RootElement.GetProperty( "metrics" )[0].GetProperty( "rows" ).EnumerateArray().First( r => r.GetProperty( "target" ).GetString() == "redis" );
      string[] refs = redis.GetProperty( "noteSources" ).EnumerateArray().Select( s => s.GetProperty( "ref" ).GetString()! ).ToArray();
      Assert.Equal( 3, refs.Length );

      string summary = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      string cell = Regex.Match( RowOf( summary, "p50Ms", "redis" ), "<td>redis<br>.*?</details>", RegexOptions.Singleline ).Value;
      Assert.All( refs, r => Assert.Contains( $"<code>{WebUtility.HtmlEncode( r )}</code>", cell ) );

      Directory.CreateDirectory( Path.Combine( _root, SET ) );
      File.WriteAllText( Path.Combine( _root, SET, "consolidated.json" ), json );
      string run = RunPage( BenchResultsFixtureFiles.Text( "run-v7-trimmed.results.json" ) );
      string runCell = Regex.Match( run, "<tr data-target=\"redis\"><td>Redis<br>.*?</details>", RegexOptions.Singleline ).Value;
      Assert.All( refs, r => Assert.Contains( $"<code>{WebUtility.HtmlEncode( r )}</code>", runCell ) );
   }

   /// <summary>
   /// The changes of recorded setup that the basis does not compare across are a table, one row per change, with the engine, each field that changed with its
   /// text before and after, the sessions and number of runs on each side, the one-engine and pair moves it hides with their metric, and the threshold the
   /// basis would give with it counted. The dry check found them only in sentences, and the first dry check found the threshold depending on them unseen.
   /// </summary>
   [Fact]
   public void TheSetupSplits_AreDrawnAsATable()
   {
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() ) );
      string table = Regex.Match( html, "<table class=\"bench-table\" data-table=\"setup-splits\">.*?</table>", RegexOptions.Singleline ).Value;

      Assert.NotEmpty( table );
      Assert.Equal( new[] { "Engine", "What changed", "Runs before the change", "Runs after the change", "Largest one-engine move across the change", "Largest pair move across the change", "Threshold if counted" },
         Regex.Matches( table, "<th(?: [^>]*)?>(.*?)</th>" ).Select( m => m.Groups[1].Value ).ToArray() );
      string[] rows = Regex.Matches( table, "<tr data-target=\"([^\"]*)\">" ).Select( m => m.Groups[1].Value ).ToArray();
      Assert.Equal( new[] { "mariadb", "sqlitevec" }, rows );

      string mariadb = Visible( Regex.Match( table, "<tr data-target=\"mariadb\">.*?</tr>", RegexOptions.Singleline ).Value );
      Assert.Contains( "MariaDB durability Before: innodb_flush_log_at_trx_commit=2 (mariadb.compose.yaml) After: innodb_flush_log_at_trx_commit=2 (set in mariadb-bench.compose.yaml)", mariadb );
      Assert.Contains( "v6, 3 runs", mariadb );
      Assert.Contains( "v7, v8, 6 runs", mariadb );
      Assert.Contains( "Searches per second, eight searchers at once: 4.09%, mariadb", mariadb );
      Assert.Contains( "Searches per second, eight searchers at once: 17.85%, mariadb/mongodb", mariadb );
      Assert.EndsWith( "35.00%", mariadb.Trim() );
      Assert.EndsWith( "165.00%", Visible( Regex.Match( table, "<tr data-target=\"sqlitevec\">.*?</tr>", RegexOptions.Singleline ).Value ).Trim() );
      Assert.Contains( "<h3>Changes of recorded setup the threshold basis does not compare across</h3>", html );
   }

   /// <summary>
   /// In the real output the table lists the four engines the frozen command found (clickhouse, duckdb, mariadb, sqlitevec) with their changed fields and the
   /// thresholds the command computed (45%, 150%, 35% and 165%), each from the file's own figures.
   /// </summary>
   [Fact]
   public void TheSetupSplits_OfTheRealOutput_ListTheFourEnginesWithTheirThresholds()
   {
      string json = BenchResultsFixtureFiles.Text( DRY );
      using JsonDocument doc = JsonDocument.Parse( json );
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      string table = Regex.Match( html, "<table class=\"bench-table\" data-table=\"setup-splits\">.*?</table>", RegexOptions.Singleline ).Value;
      JsonElement[] splits = doc.RootElement.GetProperty( "basis" ).GetProperty( "setupSplits" ).EnumerateArray().ToArray();

      Assert.Equal( new[] { "clickhouse", "duckdb", "mariadb", "sqlitevec" }, splits.Select( s => s.GetProperty( "target" ).GetString() ).ToArray() );
      Assert.Equal( splits.Select( s => s.GetProperty( "target" ).GetString() ).ToArray(), Regex.Matches( table, "<tr data-target=\"([^\"]*)\">" ).Select( m => m.Groups[1].Value ).ToArray() );
      foreach( JsonElement split in splits )
      {
         string row = Visible( Regex.Match( table, $"<tr data-target=\"{split.GetProperty( "target" ).GetString()}\">.*?</tr>", RegexOptions.Singleline ).Value );
         foreach( JsonElement change in split.GetProperty( "changes" ).EnumerateArray() )
         {
            Assert.Contains( change.GetProperty( "field" ).GetString()!, row );
            Assert.Contains( change.GetProperty( "before" ).GetString()!, row );
            Assert.Contains( change.GetProperty( "after" ).GetString()!, row );
         }

         Assert.EndsWith( $"{split.GetProperty( "tBpIfCounted" ).GetInt64() / 100.0:0.00}%", row.Trim() );
      }
   }

   /// <summary>
   /// The table is drawn in the open, not in a closed block: the threshold depends on which changes the basis leaves out, so it must not need a click. A file
   /// with no change lists no table and no heading.
   /// </summary>
   [Fact]
   public void TheSetupSplitsTable_IsInTheOpen_AndAFileWithNoSplitHasNone()
   {
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() ) );
      int table = html.IndexOf( "data-table=\"setup-splits\"", StringComparison.Ordinal );
      int lastDetailsOpen = html.LastIndexOf( "<details", table, StringComparison.Ordinal );
      int lastDetailsClose = html.LastIndexOf( "</details>", table, StringComparison.Ordinal );
      Assert.True( table > 0 );
      Assert.True( lastDetailsOpen < lastDetailsClose, "the table sits inside a closed details block" );

      string none = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( root =>
      {
         root["basis"]!.AsObject().Remove( "setupSplits" );
         root["basis"]!.AsObject().Remove( "setupSplitCount" );
      } ) ) );
      Assert.DoesNotContain( "setup-splits", none );
      Assert.DoesNotContain( "does not compare across", none );
   }

   /// <summary>
   /// A file whose own count of setup splits disagrees with the splits it lists is refused: a table that shows fewer changes than the file counts would hide
   /// exactly what the threshold depends on.
   /// </summary>
   [Fact]
   public void ASetupSplitCount_ThatDisagreesWithTheList_IsRefused()
   {
      string json = BenchResultsV8Fixture.Json( root => root["basis"]!["setupSplitCount"] = 3 );

      string message = Assert.Throws<InvalidDataException>( () => BenchConsolidatedReader.Read( json ) ).Message;

      Assert.Contains( "basis.setupSplitCount is 3 and basis.setupSplits lists 2", message );
   }

   /// <summary>
   /// A split that names no changed field is refused, and the text of a change is encoded.
   /// </summary>
   [Fact]
   public void ASplitWithNoChangedField_IsRefused_AndChangedTextIsEncoded()
   {
      string none = BenchResultsV8Fixture.Json( root => root["basis"]!["setupSplits"]![0]!["changes"] = new JsonArray() );
      Assert.Contains( "basis.setupSplits[0].changes", Assert.Throws<InvalidDataException>( () => BenchConsolidatedReader.Read( none ) ).Message );

      string loud = BenchResultsV8Fixture.Json( root => root["basis"]!["setupSplits"]![0]!["changes"]![0]!["after"] = "<script>x</script> & more" );
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( loud ) );
      Assert.DoesNotContain( "<script>", html );
      Assert.Contains( "&lt;script&gt;x&lt;/script&gt; &amp; more", html );
   }

   /// <summary>
   /// The basis figures table prints a move once: the real output lists several moves under more than one kind (for example the largest p50 pair under both
   /// the pair kind and the kept-in kind with the same runs and figures); a row identical to an earlier row of its kind is not printed again.
   /// </summary>
   [Fact]
   public void TheBasisFigures_DoNotRepeatAnIdenticalRow()
   {
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsFixtureFiles.Text( DRY ) ) );
      string figures = html[html.IndexOf( "<summary>Basis figures</summary>", StringComparison.Ordinal )..];
      figures = figures[..figures.IndexOf( "</table>", StringComparison.Ordinal )];
      string[] rows = Regex.Matches( figures, "<tr><td>.*?</tr>", RegexOptions.Singleline ).Select( m => m.Value ).ToArray();

      Assert.NotEmpty( rows );
      Assert.Equal( rows.Length, rows.Distinct().Count() );
   }

   /// <summary>
   /// The run page of a real v7 run flags what the summary flags from the same recorded fields: Elasticsearch's index state read not ready after the load and
   /// after the searches (0 of 524 vectors indexed), so it carries the "Ix" marker and its evidence; Redis and MongoDB, whose index states are ready, carry
   /// none. The dry check found the summary flag with no counterpart on the run page.
   /// </summary>
   [Fact]
   public void ARunPage_FlagsAnIndexNotReady_FromTheRealRunsIndexState()
   {
      string html = RunPage( BenchResultsFixtureFiles.Text( "run-v7-trimmed.results.json" ) );

      Assert.Contains( ">Ix</abbr>", Regex.Match( html, "<tr data-target=\"elasticsearch\">.*?</tr>", RegexOptions.Singleline ).Value );
      Assert.DoesNotContain( ">Ix</abbr>", Regex.Match( html, "<tr data-target=\"redis\">.*?</tr>", RegexOptions.Singleline ).Value );
      Assert.DoesNotContain( ">Ix</abbr>", Regex.Match( html, "<tr data-target=\"mongodb\">.*?</tr>", RegexOptions.Singleline ).Value );
      string text = Visible( html );
      Assert.Contains( "Elasticsearch index-not-ready afterLoad: the engine's index state read not ready, 0 of 524 vectors indexed", text );
      Assert.Contains( "Elasticsearch index-not-ready afterSearch: the engine's index state read not ready, 0 of 524 vectors indexed", text );
      Assert.Contains( "index-not-ready The engine did not report a finished index after the load or after the searches.", text );
   }

   /// <summary>
   /// The run page also flags failed searches and warm-up searches, a rise of the throttle counters and a pass under another governor, each from its recorded
   /// field and each with the recorded figure as evidence; a recorded zero or a governor of "performance" raises none.
   /// </summary>
   [Fact]
   public void ARunPage_FlagsErrorsThrottleAndGovernor_FromRecordedFields()
   {
      string json = Edit( BenchResultsFixtures.RUN_RESULTS_V8_JSON, root =>
      {
         JsonObject targets = root["targets"]!.AsArray().First( t => t!["name"]!.GetValue<string>() == "sql" )!.AsObject();
         targets["search"]!["errors"] = 3;
         targets["warmupErrors"] = 2;
         root["conditions"]!["throttleByTarget"] = new JsonObject { ["sql"] = new JsonObject { ["coreRise"] = 4, ["packageRise"] = 0 }, ["redis"] = new JsonObject { ["coreRise"] = 0, ["packageRise"] = 0 } };
         root["conditions"]!["passes"]!.AsArray().Add( new JsonObject { ["target"] = "redis", ["pass"] = "default@8", ["governor"] = "powersave", ["clockRead"] = true } );
         root["conditions"]!["passes"]!.AsArray().Add( new JsonObject { ["target"] = "oracle", ["pass"] = "exact", ["governor"] = "performance", ["clockRead"] = true } );
      } );

      string text = Visible( RunPage( json ) );

      Assert.Contains( "SQL Server 2025 search-errors search errors 3", text );
      Assert.Contains( "SQL Server 2025 warmup-errors warm-up errors 2", text );
      Assert.Contains( "SQL Server 2025 throttle-rise coreRise 4, packageRise 0", text );
      Assert.Contains( "Redis governor-not-performance default@8: governor powersave", text );
      Assert.DoesNotContain( "Oracle 23ai Free governor-not-performance", text );
      Assert.DoesNotMatch( new Regex( @"Redis throttle-rise" ), text );
   }

   /// <summary>
   /// A throttle rise has a marker and a legend of its own and is never shown as an unknown flag ("??"): the consolidate command raises it, and the summary
   /// must say what it means.
   /// </summary>
   [Fact]
   public void AThrottleRiseFlag_HasItsOwnMarkerAndLegend_NotTheUnknownOne()
   {
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( root =>
         ( (JsonArray)Row( root, "p50Ms", "redis" )["flags"]! ).Add( new JsonObject { ["code"] = "throttle-rise", ["text"] = "coreRise 4, packageRise 0" } ) ) ) );

      Assert.Contains( ">Th</abbr>", RowOf( html, "p50Ms", "redis" ) );
      Assert.DoesNotContain( ">??</abbr>", html );
      Assert.Contains( "The CPU&#39;s thermal throttle counters rose between the start and the end of this engine&#39;s turn in the run. The evidence gives the rise.", html );
   }

   /// <summary>
   /// A flag legend says only what the evidence of that flag gives. The summary's "unsettled" flag (the consolidate command's evidence: the run and the engine)
   /// does not name the settle check, and its legend must not say it does; the run page's "unsettled-target" flag does print the run's own warning, which names
   /// it. The governor flag's evidence gives the pass and the governor, and the flag is raised for a timed pass, not for the run as a whole.
   /// </summary>
   [Fact]
   public void TheFlagLegends_SayWhatTheEvidenceOfThatFlagGives()
   {
      Assert.DoesNotContain( "names the check", BenchLegends.Legend( "unsettled" ) );
      Assert.Contains( "names the run", BenchLegends.Legend( "unsettled" ) );
      Assert.Contains( "names the check", BenchLegends.Legend( "unsettled-target" ) );
      Assert.Contains( "during a timed pass", BenchLegends.Legend( "governor-not-performance" ) );
      Assert.DoesNotContain( "during the run", BenchLegends.Legend( "governor-not-performance" ) );

      string json = """{ "targets": [ { "name": "a", "notes": [ "WARNING: latency had NOT settled in 3 trials" ], "search": { "qps": { "1": 100, "8": 500 }, "p50Ms": 10.0 } } ] }""";
      Assert.Contains( "unsettled-target latency had NOT settled in 3 trials", Visible( BenchRunTable.Block( BenchRunTable.Read( json ) ) ) );
   }

   /// <summary>
   /// The evidence of a p50-against-mean flag on a run page names both limits it is raised at, so the legend's words ("the limit named in the evidence") are
   /// true: the dry-check build printed the ratio and no limit.
   /// </summary>
   [Fact]
   public void TheP50AgainstMeanFlag_NamesItsLimitsInItsEvidence()
   {
      string json = """{ "targets": [ { "name": "a", "search": { "qps": { "1": 100, "8": 500 }, "p50Ms": 20.0, "recall": 1 } } ] }""";

      string text = Visible( BenchRunTable.Block( BenchRunTable.Read( json ) ) );

      Assert.Contains( "p50-mean-inconsistent p50 20.00 ms, mean 10.00 ms, p50/mean 2.00; limits: p50 above the mean by more than 1.15 times, or the mean above p50 by more than 1.5 times", text );
   }

   /// <summary>
   /// The raw report of a run is linked unedited, and the page says so beside the link: the file still holds the sentences the page leaves out of its copy,
   /// and how many. The dry check found the retired sentence reachable at the link with nothing said there.
   /// </summary>
   [Fact]
   public void TheRawFiles_SayTheUneditedReportStillHoldsTheSentencesTheCopyLeavesOut()
   {
      string md = "# Run\n\n## Results\n\nThe client CPU column is the client's CPU time per search." + BenchReportStrip.CLOSE_TO_LATENCY + BenchReportStrip.EMBEDDED_OVERHEAD + "\n";
      string html = RunPage( BenchResultsFixtures.RUN_RESULTS_V8_JSON, md );

      string raw = html[html.IndexOf( "<h2>Raw files</h2>", StringComparison.Ordinal )..];
      Assert.Contains( "<code>results.md</code> is kept unedited. It still holds the sentences this page leaves out of its copy. Count: 2", raw );
      Assert.DoesNotContain( "Where it is close to the latency", html );
      Assert.True( raw.IndexOf( "Count: 2", StringComparison.Ordinal ) < raw.IndexOf( "<a href=\"/bench-results/", StringComparison.Ordinal ), "the note must come before the links" );

      string clean = RunPage( BenchResultsFixtures.RUN_RESULTS_V8_JSON, "# Run\n\n## Results\n\nA line.\n" );
      Assert.DoesNotContain( "is kept unedited", clean );
   }

   /// <summary>
   /// A run's printed report is introduced as the run's own text, not checked by the page, with a pointer to the summary page's facts table for the label of
   /// each fact; a consolidated set's page has no such sentence. The dry check found hard-coded engine texts (a documentation default printed as the value
   /// used) in the run reports with nothing on the page to say they are the run's own words.
   /// </summary>
   [Fact]
   public void ARunsReport_IsIntroducedAsTheRunsOwnText_AndASetsReportIsNot()
   {
      string run = RunPage( BenchResultsFixtures.RUN_RESULTS_V8_JSON, "# Run\n\n## Results\n\nA line.\n" );
      Assert.Contains( "This report is printed as the run wrote it, except for any sentence a note above it says was left out. This page does not check the engine texts in it. The summary page&#39;s facts table gives the label of each fact it uses.", run );
      Assert.True( run.IndexOf( "<h2>Full results</h2>", StringComparison.Ordinal ) < run.IndexOf( "This report is printed as the run wrote it", StringComparison.Ordinal ) );

      Directory.CreateDirectory( Path.Combine( _root, SET ) );
      File.WriteAllText( Path.Combine( _root, SET, "consolidated.json" ), BenchResultsV8Fixture.Json() );
      File.WriteAllText( Path.Combine( _root, SET, "consolidated.md" ), BenchResultsV8Fixture.REPORT_MD );
      Assert.DoesNotContain( "This report is printed as the run wrote it", BenchResultsEndpoints.RunPageHtml( _root, SET )! );
   }

   /// <summary>
   /// A folder named "published-" whose file is in an older shape (the 4 Oct set before it is renamed) carries a banner on its own page saying so: the page
   /// draws no result from it, and the name alone says more than the page does. A published folder holding a current file carries none.
   /// </summary>
   [Fact]
   public void APublishedNamedFolderThatIsOnlyARecord_CarriesABanner()
   {
      WriteSet( "published-2026-10-04", BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.json" ), BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.md" ) );
      WriteSet( SET, BenchResultsV8Fixture.Json(), BenchResultsV8Fixture.REPORT_MD );

      string old = BenchResultsEndpoints.RunPageHtml( _root, "published-2026-10-04" )!;
      string current = BenchResultsEndpoints.RunPageHtml( _root, SET )!;

      Assert.Contains( "<p class=\"errors bench-banner\">This folder is named published, but its file is in an older format or cannot be read. The page does not show it as a result.</p>", old );
      Assert.DoesNotContain( "bench-banner", current );
      Assert.Contains( "Report text as it was written, kept for the record", old );
   }

   /// <summary>
   /// The name a run page gives an engine (the table of friendly names) carries no word, and above all no version, that the engine text the run recorded
   /// for it lacks: each word of the name before any bracket or plus sign is a part of the recorded engine text of that target in the real v7 run. A name
   /// typed into the table that a later image change made untrue would show here instead of on a page.
   /// </summary>
   [Fact]
   public void TheFriendlyNames_CarryNoWordTheRecordedEngineTextLacks()
   {
      using JsonDocument engines = JsonDocument.Parse( BenchResultsFixtureFiles.Text( "run-v7-engines.json" ) );
      int checkedNames = 0;

      foreach( JsonProperty target in engines.RootElement.EnumerateObject() )
      {
         string name = BenchNames.FriendlyName( target.Name );
         string baseName = Regex.Split( name, @" [(+]" )[0];
         string recorded = target.Value.GetString()!;
         foreach( string word in baseName.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
         {
            Assert.True( recorded.Contains( word, StringComparison.OrdinalIgnoreCase ), $"the name \"{name}\" of {target.Name} holds \"{word}\", which its recorded engine text lacks: {recorded}" );
         }

         checkedNames++;
      }

      Assert.Equal( 19, checkedNames );
   }

   /// <summary>
   /// The ordered pairs that clear the line by a hair (the pairs on the line) are on the page as a list, each saying which engine is ahead of which and its
   /// smallest ratio, in the order of the file: the report's own sentence says "they are listed as on the line", and the dry-check build printed that sentence
   /// and no list. In the real output there are four, and the closest of them (oracle ahead of mongodb at 1.3508) is a published order.
   /// </summary>
   [Fact]
   public void ThePairsOnTheLine_AreOnThePage_EachWithItsDirectionAndRatio()
   {
      string json = BenchResultsFixtureFiles.Text( DRY );
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement[] listed = doc.RootElement.GetProperty( "drift" ).GetProperty( "onLine" ).EnumerateArray().ToArray();
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      string section = html[html.IndexOf( "<h3>Ordered pairs on the line</h3>", StringComparison.Ordinal )..];
      string[] items = Regex.Matches( section[..section.IndexOf( "</ul>", StringComparison.Ordinal )], "<li>(.*?)</li>" ).Select( m => Visible( m.Groups[1].Value ).Trim() ).ToArray();

      Assert.Equal( 4, listed.Length );
      Assert.Equal( listed.Select( e => $"{e.GetProperty( "metric" ).GetString()} {e.GetProperty( "a" ).GetString()} ahead of {e.GetProperty( "b" ).GetString()}, minimum ratio {e.GetProperty( "minRatioBp" ).GetInt64() / 10000.0:0.0000}" ).ToArray(), items );
      Assert.Contains( "p50Ms oracle ahead of mongodb, minimum ratio 1.3508", items );
      Assert.Contains( "4 ordered pairs cleared 1.35 times by 25 bp or less in their lowest session; they are listed as on the line.", Visible( html ) );
   }

   /// <summary>
   /// An order that held in one session and not in the other names the session it held in and says which engine is ahead: the report's table has the
   /// "held in" column and the page had no field for it. A list of unordered pairs (close to the line) still names the two engines without an "ahead of".
   /// </summary>
   [Fact]
   public void AnUnconfirmedOrder_NamesTheSessionItHeldIn_AndTheCloseListStaysUnordered()
   {
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() ) );

      string unconfirmed = html[html.IndexOf( "<h3>Orders that held in one session and not in both</h3>", StringComparison.Ordinal )..];
      Assert.StartsWith( "<h3>Orders that held in one session and not in both</h3><ul><li><code>p50Ms</code> MariaDB ahead of pgvector, held in v7</li></ul>", unconfirmed );
      string close = html[html.IndexOf( "<h3>Pairs close to the line</h3>", StringComparison.Ordinal )..];
      Assert.StartsWith( "<h3>Pairs close to the line</h3><ul><li><code>qps8</code> Redis and MariaDB, minimum ratio 1.3100</li></ul>", close );
      Assert.Contains( "<h3>Ordered pairs on the line</h3><ul><li><code>p50Ms</code> MariaDB ahead of sqlite-vec, minimum ratio 1.3513</li></ul>", html );
   }

   /// <summary>
   /// The basis runs table gives each run's seed, session and start beside its folder and conditions, as the report's table of runs does: the six runs that only feed
   /// the threshold (the v5 and v6 runs) were on the page by folder name alone.
   /// </summary>
   [Fact]
   public void TheBasisRuns_ShowTheirSeedSessionAndStart()
   {
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsFixtureFiles.Text( DRY ) ) );
      string table = html[html.IndexOf( "<h3>Basis runs</h3>", StringComparison.Ordinal )..];
      table = table[..table.IndexOf( "</table>", StringComparison.Ordinal )];

      Assert.Equal( new[] { "folder", "seed", "session", "startedUtc", "turbo", "uncore", "warmup", "rehearsal", "build", "machineControl" }, Regex.Matches( table, "<th>(.*?)</th>" ).Select( m => m.Groups[1].Value ).ToArray() );
      Assert.Contains( "<td>20261005-023459-eshoponweb</td><td>501</td><td>v5</td>", table );
      Assert.Contains( "<td>20261005-101815-eshoponweb</td><td>603</td><td>v6</td>", table );
   }

   /// <summary>
   /// The "Runs used" table lists every run the set uses, as the report's own sentence says it does ("12 basis runs, listed below with their seeds and start
   /// times"): the six compared runs under their sessions, then the six runs that only feed the threshold under "v5 (basis)" and "v6 (basis)", each with the
   /// seed and start the file gives it. The dry-check page listed the compared runs only, so that sentence was false on it.
   /// </summary>
   [Fact]
   public void TheRunsUsedTable_ListsEveryRunTheSetUses_WithItsSeedAndStart()
   {
      string json = BenchResultsFixtureFiles.Text( DRY );
      using JsonDocument doc = JsonDocument.Parse( json );
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      string table = Regex.Match( html, "<table class=\"bench-table\" data-table=\"runs-used\">.*?</table>", RegexOptions.Singleline ).Value;
      string[][] rows = Regex.Matches( table, "<tr><td>(.*?)</tr>", RegexOptions.Singleline )
         .Select( m => Regex.Matches( "<td>" + m.Groups[1].Value, "<td[^>]*>(.*?)</td>" ).Select( c => Visible( c.Groups[1].Value ).Trim() ).ToArray() ).ToArray();

      Assert.Equal( 12, rows.Length );
      string[] claim = rows.Take( 6 ).Select( r => r[0] ).ToArray();
      Assert.Equal( new[] { "v7", "v7", "v7", "v8", "v8", "v8" }, claim );
      Assert.Equal( new[] { "v5 (basis)", "v5 (basis)", "v5 (basis)", "v6 (basis)", "v6 (basis)", "v6 (basis)" }, rows.Skip( 6 ).Select( r => r[0] ).ToArray() );
      foreach( JsonElement run in doc.RootElement.GetProperty( "basis" ).GetProperty( "runs" ).EnumerateArray().Where( r => r.GetProperty( "session" ).GetString() is "v5" or "v6" ) )
      {
         string[] row = rows.Single( r => r[1] == run.GetProperty( "folder" ).GetString() );
         Assert.Equal( run.GetProperty( "seed" ).GetInt64().ToString(), row[2] );
         Assert.Equal( run.GetProperty( "startedUtc" ).GetString(), row[3] );
      }
   }

   /// <summary>
   /// The table of dropped clock warnings names the engine and the pass each warning is about, from the file's fields, beside the text and the recomputed
   /// median clocks.
   /// </summary>
   [Fact]
   public void TheDroppedWarnings_NameTheEngineAndThePass()
   {
      string json = BenchResultsFixtureFiles.Text( DRY );
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      string block = html[html.IndexOf( "<summary>Dropped clock warnings</summary>", StringComparison.Ordinal )..];
      block = block[..block.IndexOf( "</details>", StringComparison.Ordinal )];

      Assert.Equal( new[] { "run", "text", "engine", "pass", "engine median MHz", "client median MHz" }, Regex.Matches( block, "<th>(.*?)</th>" ).Select( m => m.Groups[1].Value ).ToArray() );
      Assert.Equal( 6, Regex.Matches( block, "<td>oracle</td><td>default@8</td><td>3492</td><td>3492</td>" ).Count );
   }

   /// <summary>
   /// Each setting in the search effort column names the run it was read from, as the file's list gives it ("run"): in the real output the 19 entries each
   /// name a run, and the column printed the settings with no place they came from.
   /// </summary>
   [Fact]
   public void TheSearchEffortColumn_NamesTheRunEachSettingWasRecordedIn()
   {
      string json = BenchResultsFixtureFiles.Text( DRY );
      using JsonDocument doc = JsonDocument.Parse( json );
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      int entries = 0;

      foreach( JsonElement entry in doc.RootElement.GetProperty( "searchSettings" ).EnumerateArray() )
      {
         string target = entry.GetProperty( "target" ).GetString()!;
         string run = entry.GetProperty( "run" ).GetString()!;
         string cell = Regex.Match( html, $"<tr data-target=\"{Regex.Escape( target )}\"><td>[^<]*</td><td><ul class=\"bench-facts\">.*?</ul></td><td data-column=\"effort\">(.*?)</td><td class=\"n\">", RegexOptions.Singleline ).Groups[1].Value;
         foreach( JsonElement setting in entry.GetProperty( "settings" ).EnumerateArray() )
         {
            Assert.Contains( $"<li><code>{WebUtility.HtmlEncode( $"{setting.GetProperty( "key" ).GetString()}={setting.GetProperty( "value" ).GetString()}" )}</code> <span class=\"muted\">(<code>{run}</code>)</span></li>", cell );
         }

         entries++;
      }

      Assert.Equal( 19, entries );
   }

   /// <summary>
   /// The audit block prints everything the audit recorded: the SHA-256 of the observer summary, how many facts and how many row sentences it checked.
   /// </summary>
   [Fact]
   public void TheAuditBlock_PrintsWhatTheAuditRecorded()
   {
      string json = BenchResultsFixtureFiles.Text( DRY );
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement audit = doc.RootElement.GetProperty( "audit" );
      string text = Visible( BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) ) );

      Assert.Contains( $"Observer summary SHA-256 {audit.GetProperty( "observerSha256" ).GetString()}", text );
      Assert.Contains( $"Facts checked {audit.GetProperty( "factsChecked" ).GetInt64()}", text );
      Assert.Contains( $"Row sentences checked {audit.GetProperty( "rowSentencesChecked" ).GetInt64()}", text );
   }

   /// <summary>
   /// Every field of the real output is read by the page or left out on purpose, so the page lists no unread field and prints no such block; and the list
   /// covers every level the real output has an object at. This is the test that would have failed for the pairs on the line and the session of an
   /// unconfirmed order, before they were drawn.
   /// </summary>
   [Fact]
   public void TheRealOutput_HoldsNoFieldThePageNeitherReadsNorLeavesOutOnPurpose()
   {
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsFixtureFiles.Text( DRY ) ) );

      Assert.DoesNotContain( "data-block=\"unread-fields\"", html );
      Assert.DoesNotContain( "Fields this page does not read", html );
      Assert.DoesNotContain( "data-block=\"unread-fields\"", BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() ) ) );
   }

   /// <summary>
   /// A field the file holds and the page does not read is listed on the page by its path, once, under the heading for such fields, whatever level it sits
   /// at (the root, a row, a fact, the drift block, a setup split's move), in the order the walk meets them (the root's own fields, then each block in the
   /// file's order); a field left out on purpose is not listed.
   /// </summary>
   [Fact]
   public void AFieldThePageDoesNotRead_IsListedOnThePage_ByItsPath()
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         root["newTopLevel"] = 1;
         Row( root, "p50Ms", "redis" )["classification"] = "documented";
         Row( root, "qps1", "redis" )["classification"] = "documented";
         root["why"]!.AsArray()[0]!["facts"]![0]!["classification"] = "documented";
         root["drift"]!["onLineFraction"] = 0.1;
         root["basis"]!["setupSplits"]![0]!["oneEngine"]!["newMoveField"] = true;
         root["format"] = "v8c";
         root["drift"]!["onLineBp"] = 25;
      } );

      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );
      string block = Regex.Match( html, "<aside class=\"bench-caveats\" data-block=\"unread-fields\">.*?</aside>", RegexOptions.Singleline ).Value;

      Assert.Contains( "<h2>Fields this page does not read</h2>", block );
      Assert.Contains( "The file holds these fields, and this page does not read them. Anything they say is not on this page.", block );
      Assert.Equal( new[] { "newTopLevel", "basis.setupSplits[].oneEngine.newMoveField", "metrics[].rows[].classification", "drift.onLineFraction", "why[].facts[].classification" },
         Regex.Matches( block, "<li><code>(.*?)</code></li>" ).Select( m => m.Groups[1].Value ).ToArray() );
   }

   /// <summary>
   /// The summary page of the real output is built from the file's own strings and a fixed set of legends and templates: every text node of seven words or
   /// more is a part of a string of the file (its sentences, facts, notes, moves, sources or names), a fixed legend of <see cref="BenchLegends"/>, or one
   /// of the page's named templates. A sentence typed into a page template is none of these and fails here, so the web layer cannot print a clause that the
   /// report did not write and the legend allow-list did not check.
   /// </summary>
   [Fact]
   public void EveryLongTextOnTheSummaryPage_IsTheFilesOrALegendOrANamedTemplate()
   {
      string json = BenchResultsFixtureFiles.Text( DRY );
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );

      List<string> unexplained = Unexplained( html, json );

      Assert.True( unexplained.Count == 0, "text the page printed that is not the file's, a legend or a template:\n" + string.Join( "\n", unexplained ) );
   }

   /// <summary>
   /// The same rule on a run page and on the page of a withdrawn set: long text is the run's own record, a legend, or a named template.
   /// </summary>
   [Fact]
   public void EveryLongTextOnARunPageAndAWithdrawnPage_IsTheRecordsOrALegendOrANamedTemplate()
   {
      string run = BenchResultsFixtureFiles.Text( "run-v7-trimmed.results.json" );
      string md = "# Run\n\n## Results\n\nA line." + BenchReportStrip.CLOSE_TO_LATENCY + "\n";
      string runPage = RunPage( run, md );
      Assert.True( Unexplained( runPage, run, md ).Count == 0, "run page: " + string.Join( "\n", Unexplained( runPage, run, md ) ) );

      WriteSet( "withdrawn-2026-10-04", BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.json" ), BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.md" ) );
      string withdrawn = BenchResultsEndpoints.RunPageHtml( _root, "withdrawn-2026-10-04" )!;
      string[] source = { BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.json" ), BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.md" ) };
      Assert.True( Unexplained( withdrawn, source ).Count == 0, "withdrawn page: " + string.Join( "\n", Unexplained( withdrawn, source ) ) );
   }

   /// <summary>
   /// The provenance rule has teeth: a clause typed into a page text node (here appended to a rendered page) is reported as unexplained, and a clause that is
   /// a fixed legend or part of the file is not.
   /// </summary>
   [Fact]
   public void TheProvenanceRule_ReportsATypedClause_AndAcceptsALegendAndAFileString()
   {
      string json = BenchResultsV8Fixture.Json();
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );

      Assert.Empty( Unexplained( html, json ) );
      string typed = "Weaviate sets its effort to eight times the limit and clamps it between a hundred and five hundred";
      Assert.Equal( new[] { typed }, Unexplained( html + $"<p>{typed}</p>", json ) );
      Assert.Empty( Unexplained( html + $"<p>{BenchLegends.ROW_ORDER} {BenchLegends.ROW_SEPARATION}</p>", json ) );
      Assert.Empty( Unexplained( html + "<p>Every engine below Redis, which holds its data in memory, took at least 1.35 times as long per search in each session.</p>", json ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The sentences of a page of seven words or more that no source explains. A text node is cut into sentences; a sentence is explained when, with a leading
   /// "= " or "; " and a trailing ")" taken off, it is a part of a string of a source (the strings of a JSON file, or the text of a Markdown report), a part
   /// of a fixed legend, a legend followed or preceded by a part of a source, a list whose "; " or ", " separated parts are each explained, or one of the
   /// named templates. A sentence shorter than seven words is a label or a figure and is not checked: the legends carry every label that holds a claim, and
   /// the legend tests check those.
   /// </summary>
   /// <param name="html">The page.</param>
   /// <param name="sources">The texts of the files the page was drawn from.</param>
   /// <returns>The sentences no source explains, in page order, once each.</returns>
   private static List<string> Unexplained( string html, params string[] sources )
   {
      string stripped = Regex.Replace( html, "<(script|style)[^>]*>.*?</\\1>", string.Empty, RegexOptions.Singleline );
      string corpus = string.Join( "\n", sources.Select( Corpus ) );
      var found = new List<string>();
      foreach( Match match in Regex.Matches( stripped, ">([^<>]+)<" ) )
      {
         string node = Regex.Replace( WebUtility.HtmlDecode( match.Groups[1].Value ), @"\s+", " " ).Trim();
         foreach( string sentence in Regex.Split( node, @"(?<=[.!?]) (?=[A-Z])" ) )
         {
            if( !found.Contains( sentence ) && !Explained( sentence, corpus ) )
            {
               found.Add( sentence );
            }
         }
      }

      return found;
   }

   /// <summary>
   /// True when a sentence is short, or a source, a legend or a named template explains it (see <see cref="Unexplained"/>).
   /// </summary>
   private static bool Explained( string sentence, string corpus )
   {
      string core = Regex.Replace( Regex.Replace( sentence, @"^(= |; )", string.Empty ), @"\)$", string.Empty ).Trim();
      if( core.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Length < 7 || corpus.Contains( core, StringComparison.Ordinal ) )
      {
         return true;
      }

      string[] legends = BenchLegends.All.Values.Where( l => l.Length >= 12 ).ToArray();
      if( legends.Any( l => l.Contains( core, StringComparison.Ordinal ) ) || TEMPLATES.Any( t => t.IsMatch( core ) ) )
      {
         return true;
      }

      foreach( string legend in legends )
      {
         if( core.StartsWith( legend, StringComparison.Ordinal ) && Explained( core[legend.Length..].Trim(), corpus ) )
         {
            return true;
         }

         if( core.EndsWith( legend, StringComparison.Ordinal ) && Explained( core[..^legend.Length].Trim(), corpus ) )
         {
            return true;
         }
      }

      string[] parts = core.Split( new[] { "; ", ", " }, StringSplitOptions.RemoveEmptyEntries );
      return parts.Length > 1 && parts.All( p => Explained( p, corpus ) );
   }

   /// <summary>
   /// The text a source holds, for a parts-of-a-string search: every name and string of a JSON file on its own line, or a Markdown report with its escaped pipes
   /// unescaped and its white space collapsed.
   /// </summary>
   /// <param name="source">The file's text.</param>
   /// <returns>The searchable text.</returns>
   private static string Corpus( string source )
   {
      try
      {
         using JsonDocument doc = JsonDocument.Parse( source );
         var strings = new List<string>();
         Collect( doc.RootElement, strings );
         return string.Join( "\n", strings.Select( t => Regex.Replace( t, @"\s+", " " ) ) );
      }
      catch( JsonException )
      {
         return Regex.Replace( source.Replace( "\\|", "|" ), @"[ \t]+", " " );
      }
   }

   /// <summary>
   /// Adds every property name, string and number of a JSON value to a list.
   /// </summary>
   private static void Collect( JsonElement value, List<string> into )
   {
      switch( value.ValueKind )
      {
         case JsonValueKind.String: into.Add( value.GetString()! ); break;
         case JsonValueKind.Number: into.Add( value.GetRawText() ); break;
         case JsonValueKind.Array: value.EnumerateArray().ToList().ForEach( e => Collect( e, into ) ); break;
         case JsonValueKind.Object: value.EnumerateObject().ToList().ForEach( p => { into.Add( p.Name ); Collect( p.Value, into ); } ); break;
      }
   }

   /// <summary>
   /// Markup removed without putting a space where a tag was, entities decoded: the text of an element as one run, so a label inside a span reads in its sentence.
   /// </summary>
   private static string Flat( string html )
   {
      return WebUtility.HtmlDecode( Regex.Replace( html, "<[^>]+>", string.Empty ) );
   }

   /// <summary>
   /// The facts of one engine in a consolidated.json.
   /// </summary>
   private static IEnumerable<JsonElement> FactsOf( JsonElement root, string target )
   {
      return root.GetProperty( "why" ).EnumerateArray().Where( w => w.GetProperty( "target" ).GetString() == target ).SelectMany( w => w.GetProperty( "facts" ).EnumerateArray() );
   }

   /// <summary>
   /// The header row of one metric's table.
   /// </summary>
   private static string TableHead( string html, string metric )
   {
      int start = html.IndexOf( $"data-metric=\"{metric}\"", StringComparison.Ordinal );
      Assert.True( start >= 0, $"no table for {metric}" );
      int head = html.IndexOf( "<thead>", start, StringComparison.Ordinal );
      return html[head..html.IndexOf( "</thead>", head, StringComparison.Ordinal )];
   }

   /// <summary>
   /// One engine's row of one metric table.
   /// </summary>
   private static string RowOf( string html, string metric, string target )
   {
      int start = html.IndexOf( $"data-metric=\"{metric}\"", StringComparison.Ordinal );
      int end = html.IndexOf( "</table>", start, StringComparison.Ordinal );
      Match row = Regex.Match( html[start..end], $"<tr data-target=\"{Regex.Escape( target )}\">.*?</tr>", RegexOptions.Singleline );
      Assert.True( row.Success, $"no row {target} in {metric}" );
      return row.Value;
   }

   /// <summary>
   /// The row object of a target in a metric of the fixture's JSON.
   /// </summary>
   private static JsonObject Row( JsonObject root, string metric, string target )
   {
      JsonNode table = ( (JsonArray)root["metrics"]! ).First( m => m!["metric"]!.GetValue<string>() == metric )!;
      return (JsonObject)( (JsonArray)table["rows"]! ).First( r => r!["target"]!.GetValue<string>() == target )!;
   }

   /// <summary>
   /// The page of a run folder holding the given results.json and, when given, a results.md.
   /// </summary>
   private string RunPage( string json, string? md = null )
   {
      string folder = Path.Combine( _root, V7_RUN );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "results.json" ), json );
      if( md != null )
      {
         File.WriteAllText( Path.Combine( folder, "results.md" ), md );
      }

      return BenchResultsEndpoints.RunPageHtml( _root, V7_RUN )!;
   }

   /// <summary>
   /// Writes a folder holding a consolidated.json and a report.
   /// </summary>
   private void WriteSet( string name, string json, string md )
   {
      string folder = Path.Combine( _root, name );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "consolidated.json" ), json );
      File.WriteAllText( Path.Combine( folder, "consolidated.md" ), md );
   }

   /// <summary>
   /// A JSON text after an edit of its root.
   /// </summary>
   private static string Edit( string json, Action<JsonNode> edit )
   {
      JsonNode root = JsonNode.Parse( json )!;
      edit( root );
      return root.ToJsonString();
   }

   /// <summary>
   /// The text a reader sees: tags removed, entities decoded, white space collapsed.
   /// </summary>
   private static string Visible( string html )
   {
      return Regex.Replace( WebUtility.HtmlDecode( Regex.Replace( html, "<[^>]+>", " " ) ), @"\s+", " " );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes this test's folder.
   /// </summary>
   public void Dispose()
   {
      if( Directory.Exists( _root ) )
      {
         Directory.Delete( _root, true );
      }

      GC.SuppressFinalize( this );
   }

   #endregion IDisposable
}
