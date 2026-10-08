using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The page of a v8 consolidated.json: what it prints, in what order, and what it refuses.
/// The page states nothing of its own, so these tests check that every sentence of the file is printed once with its
/// sources, that rows keep the file's order, that the separations printed are the file's, that a stopped set
/// prints no headline and no table, and that a file that does not hold together fails at the reader.
/// </summary>
public class BenchResultsConsolidatedTests
{
   #region Data Members

   private static readonly Regex TABLE_ROWS = new( "data-target=\"([^\"]*)\"", RegexOptions.Compiled );

   private static readonly string[] OLD_CLAIMS = { "was fastest", "fastest together", "Not final", "not final", "about 2%", "bench-chart" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The headline is the file's own sentence, and none of the claims of the earlier page is printed.
   /// </summary>
   [Fact]
   public void Headline_IsTheFilesSentence_AndNoEarlierClaimIsPrinted()
   {
      string html = Html();

      Assert.Contains( "<p class=\"bench-lead\">Every engine below Redis, which holds its data in memory, took at least 1.35 times as long per search in each session.</p>", html );
      Assert.All( OLD_CLAIMS, claim => Assert.DoesNotContain( claim, html ) );
      Assert.DoesNotMatch( new Regex( @"\bbands?\b|\btied\b", RegexOptions.IgnoreCase ), Plain( html ) );
   }

   /// <summary>
   /// Every table lists its rows in the order the file gives, which for a latency table is lowest first and for a throughput table highest
   /// first; the page never sorts.
   /// </summary>
   [Theory]
   [InlineData( "p50Ms" )]
   [InlineData( "qps1" )]
   [InlineData( "qps8" )]
   [InlineData( "exactP50Ms" )]
   public void Rows_KeepTheFilesOrder( string metric )
   {
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() );
      string html = BenchConsolidatedHtml.Block( model );

      string[] printed = TableOf( html, metric ).Select( m => m.Groups[1].Value ).ToArray();

      Assert.Equal( model.Metrics.First( m => m.Metric == metric ).Rows.Select( r => r.Target ), printed );
   }

   /// <summary>
   /// The first rows differ by direction: the lowest latency is first in the latency table and the highest throughput first in the
   /// throughput table, so an engine that is first in both is first because it is better, not because of a sort.
   /// </summary>
   [Fact]
   public void Rows_PutTheBetterEngineFirst_ForBothDirections()
   {
      string html = Html();

      Assert.Equal( "redis", TableOf( html, "p50Ms" ).First().Groups[1].Value );
      Assert.Equal( "mariadb", TableOf( html, "qps8" ).First().Groups[1].Value );
   }

   /// <summary>
   /// A row names the engines it is not separated from by their display names, and a row separated from every engine prints "none" (v8j: a dash means "no figure" on this page).
   /// </summary>
   [Fact]
   public void NotSeparated_NamesTheEngines_AndAFullySeparatedRowPrintsNone()
   {
      string html = Html();
      string mariadb = RowOf( html, "p50Ms", "mariadb" );
      string redis = RowOf( html, "p50Ms", "redis" );

      Assert.Contains( "<td>pgvector</td>", mariadb );
      Assert.Contains( "<td>none</td>", redis );
      Assert.DoesNotContain( "Redis, ", mariadb );
   }

   /// <summary>
   /// Latency shows two decimals, throughput whole numbers with separators, each session shows min to max, and the median is of all runs.
   /// </summary>
   [Fact]
   public void Figures_AreFormattedPerMetric_AndEachSessionShowsMinToMax()
   {
      string html = Html();

      Assert.Contains( "<th class=\"n\">v7 min to max</th><th class=\"n\">v8 min to max</th><th class=\"n\">Median of all runs</th>", html );
      Assert.Contains( "1.90 to 1.94", RowOf( html, "p50Ms", "sqlitevec" ) );
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() );
      double mariadb = model.Metrics.First( m => m.Metric == "qps8" ).Rows.First( r => r.Target == "mariadb" ).Median!.Value;
      Assert.Contains( $">{BenchFormat.Count( mariadb )}<", RowOf( html, "qps8", "mariadb" ) );
      Assert.Matches( @">5,9\d\d<", RowOf( html, "qps8", "mariadb" ) );
      Assert.Contains( ">0.40<", RowOf( html, "p50Ms", "redis" ) );
   }

   /// <summary>
   /// A row's note is printed under the engine's name in its own row, so the in-memory note is where the figure is.
   /// </summary>
   [Fact]
   public void RowNote_IsPrintedInTheRow()
   {
      string html = Html();

      Assert.Contains( BenchResultsV8Fixture.REDIS_NOTE, RowOf( html, "p50Ms", "redis" ) );
      Assert.Contains( BenchResultsV8Fixture.REDIS_NOTE, RowOf( html, "qps8", "redis" ) );
      Assert.DoesNotContain( BenchResultsV8Fixture.REDIS_NOTE, RowOf( html, "p50Ms", "mariadb" ) );
   }

   /// <summary>
   /// Each sentence of the file is printed exactly once, in its own section, with its sources in a closed details that names the
   /// path of each source; none is printed twice and none is dropped.
   /// </summary>
   [Fact]
   public void EverySentence_IsPrintedExactlyOnce_WithItsSources()
   {
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() );
      string text = Plain( BenchConsolidatedHtml.Block( model ) );

      Assert.All( model.Sentences, s => Assert.Equal( 1, Count( text, s.Text ) ) );
      string html = BenchConsolidatedHtml.Block( model );
      Assert.Contains( "<details class=\"bench-sources muted\"><summary>sources</summary><ul><li>results <code>targets[redis].index#HNSW</code> = HNSW</li></ul></details>", html );
      Assert.DoesNotContain( "<details open", html );
   }

   /// <summary>
   /// A sentence whose slot names no section of the page is not dropped: it is printed, once, under the heading for statements with
   /// no place, with a notice.
   /// </summary>
   [Fact]
   public void ASentenceWithAnUnknownSlot_IsPrintedOnce_UnderTheUnplacedHeading()
   {
      string html = Html( root => ( (JsonArray)root["sentences"]! ).Add( BenchResultsV8Fixture.Sentence( "surprise.thing", "An unexpected sentence." ) ) );

      Assert.Equal( 1, Count( Plain( html ), "An unexpected sentence." ) );
      Assert.Contains( BenchLegends.H_UNPLACED, html );
      Assert.Contains( BenchLegends.UNPLACED, html );
      Assert.DoesNotContain( BenchLegends.H_UNPLACED, Html() );
   }

   /// <summary>
   /// A sentence with no sources says so on the page, in the place a reader would look for them.
   /// </summary>
   [Fact]
   public void ASentenceWithNoSources_SaysSo()
   {
      string html = Html( root => ( (JsonArray)root["sentences"]! ).Add( new JsonObject { ["slot"] = "disclosure.bare", ["text"] = "A bare sentence.", ["sources"] = new JsonArray() } ) );

      Assert.Contains( "A bare sentence.", html );
      Assert.Contains( "no sources recorded", html );
   }

   /// <summary>
   /// A recorded text is printed as a quotation, and the legend that says what a quotation is follows its section once.
   /// </summary>
   [Fact]
   public void QuotedSentences_AreBlockquotes_WithTheQuoteLegendOncePerSection()
   {
      string html = Html();

      Assert.Contains( "<blockquote class=\"bench-quote\">Redis: save &quot;300 1&quot; and appendonly no", html );
      Assert.Equal( 2, Count( html, BenchLegends.QUOTE ) );
   }

   /// <summary>
   /// A recorded text keeps its own words however long, and a source value past the print limit is cut with an ellipsis.
   /// </summary>
   [Fact]
   public void ALongSourceValue_IsCut_AndTheSentenceIsNot()
   {
      string longText = string.Join( " ", Enumerable.Repeat( "word", 300 ) );
      string html = Html( root => ( (JsonArray)root["sentences"]! ).Add( new JsonObject { ["slot"] = "method.long", ["text"] = longText, ["quote"] = true,
         ["sources"] = new JsonArray( new JsonObject { ["kind"] = "results", ["ref"] = "x", ["value"] = new string( 'v', 900 ) } ) } ) );

      Assert.Contains( longText, html );
      Assert.Contains( new string( 'v', BenchConsolidatedHtml.MAX_SOURCE_VALUE ) + "...", html );
      Assert.DoesNotContain( new string( 'v', BenchConsolidatedHtml.MAX_SOURCE_VALUE + 1 ), html );
   }

   /// <summary>
   /// A stopped set prints its stopped block and its sentences and no headline and no metric table.
   /// </summary>
   [Fact]
   public void AStoppedSet_PrintsNoHeadlineAndNoTables()
   {
      string html = Html( root =>
      {
         root["guards"] = new JsonObject { ["g2"] = new JsonObject { ["stopped"] = true, ["pairs"] = new JsonArray( "mariadb over pgvector on p50" ) }, ["g3"] = new JsonObject { ["targets"] = new JsonArray() } };
         var sentences = (JsonArray)root["sentences"]!;
         sentences.RemoveAt( 0 );
         sentences.Add( BenchResultsV8Fixture.Sentence( "stopped.reason", "The threshold rose above the expected value." ) );
      } );

      Assert.Contains( BenchLegends.STOPPED, html );
      Assert.Contains( "The threshold rose above the expected value.", html );
      Assert.Contains( "mariadb over pgvector on p50", html );
      Assert.DoesNotContain( "bench-lead", html );
      Assert.DoesNotContain( "data-metric=", html );
      Assert.DoesNotContain( BenchLegends.H_RECALL, html );
      Assert.DoesNotContain( BenchLegends.H_WHY, html );
   }

   /// <summary>
   /// A set the first or third guard or the file's own status marks stopped is drawn as stopped (no headline, no table), and the page names the field.
   /// </summary>
   [Theory]
   [InlineData( "status" )]
   [InlineData( "guards.g1.stopped" )]
   [InlineData( "guards.g3.stopped" )]
   public void AStoppedSet_IsRecognisedFromEveryPlaceTheFileMarksIt( string field )
   {
      string html = Html( root =>
      {
         ( (JsonArray)root["sentences"]! ).RemoveAt( 0 );
         switch( field )
         {
            case "status": root["status"] = "stopped"; break;
            case "guards.g1.stopped": root["guards"] = new JsonObject { ["g1"] = new JsonObject { ["stopped"] = true } }; break;
            default: root["guards"] = new JsonObject { ["g3"] = new JsonObject { ["stopped"] = true, ["targets"] = new JsonArray() } }; break;
         }
      } );

      Assert.Contains( BenchLegends.STOPPED, html );
      Assert.Contains( $"{BenchLegends.STOPPED_IN} <code>{field}</code>", html );
      Assert.DoesNotContain( "data-metric=", html );
      Assert.DoesNotContain( "bench-lead", html );
   }

   /// <summary>
   /// A row that is not ranked says so in its cell, the legend of that status follows the tables, and the order checks skip it.
   /// </summary>
   [Fact]
   public void AOneSessionRow_SaysSo_AndIsLeftOutOfTheOrderChecks()
   {
      string html = Html( root =>
      {
         JsonObject row = Row( root, "qps8", "sqlitevec" );
         row["status"] = "one-session";
         row["notSeparatedFrom"] = new JsonArray();
         row["perSession"] = new JsonObject { ["v8"] = row["perSession"]!["v8"]!.DeepClone() };
         row["median"] = 99999.0;
      } );

      Assert.Contains( "<span class=\"bench-no\">one session</span>", RowOf( html, "qps8", "sqlitevec" ) );
      Assert.Contains( "<td class=\"n\">-</td>", RowOf( html, "qps8", "sqlitevec" ) );
      Assert.Contains( System.Net.WebUtility.HtmlEncode( BenchLegends.ONE_SESSION ), html );
      Assert.DoesNotContain( System.Net.WebUtility.HtmlEncode( BenchLegends.ONE_SESSION ), Html() );
   }

   /// <summary>
   /// Targets whose setup differs between sessions are listed with the differing fields under their own heading.
   /// </summary>
   [Fact]
   public void SetupDifferences_AreListedWithTheirFields()
   {
      string html = Html( root => root["guards"] = new JsonObject
      {
         ["g2"] = new JsonObject { ["stopped"] = false, ["pairs"] = new JsonArray() },
         ["g3"] = new JsonObject { ["targets"] = new JsonArray( new JsonObject { ["target"] = "redis", ["fields"] = new JsonArray( "durability", "engineSettings" ) } ) },
      } );

      Assert.Contains( BenchLegends.H_SETUP_DIFFERS, html );
      Assert.Contains( "<strong>Redis</strong> <code>durability, engineSettings</code>", html );
   }

   /// <summary>
   /// A flag prints a marker after the engine's name with its evidence as hover text, the flags section lists each code once with its
   /// fixed legend and the number of engines that have it, and a code the page does not know prints "??" with the unknown-flag legend.
   /// </summary>
   [Fact]
   public void Flags_PrintMarkers_AndEachCodeOnceInTheLegend_AndUnknownCodesStayVisible()
   {
      string html = Html( root =>
      {
         ( (JsonArray)Row( root, "p50Ms", "redis" )["flags"]! ).Add( new JsonObject { ["code"] = "clock-off", ["text"] = "default@8 engine CPUs median 3121 MHz" } );
         ( (JsonArray)Row( root, "qps8", "redis" )["flags"]! ).Add( new JsonObject { ["code"] = "clock-off", ["text"] = "default@1" } );
         ( (JsonArray)Row( root, "p50Ms", "mariadb" )["flags"]! ).Add( new JsonObject { ["code"] = "brand-new-code", ["text"] = "evidence" } );
      } );

      Assert.Contains( "title=\"clock-off: default@8 engine CPUs median 3121 MHz\"", html );
      Assert.Contains( ">Ck</abbr>", RowOf( html, "p50Ms", "redis" ) );
      Assert.Contains( ">??</abbr>", RowOf( html, "p50Ms", "mariadb" ) );
      Assert.Equal( 1, Count( html, BenchLegends.Legend( "clock-off" ) ) );
      Assert.Equal( 1, Count( html, BenchLegends.FLAG_UNKNOWN ) );
      Assert.Contains( "(1 of 6 engines)", html );
      Assert.Contains( "<code>brand-new-code</code>", html );
   }

   /// <summary>
   /// The facts and costs table keeps the file's order, prints each fact with its confidence and source, and prints a dash where the
   /// file has no figure.
   /// </summary>
   [Fact]
   public void Why_PrintsFactsWithConfidenceAndSource_AndDashesForMissingCosts()
   {
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() );
      string html = BenchConsolidatedHtml.Block( model );
      string why = html[html.IndexOf( BenchLegends.H_WHY, StringComparison.Ordinal )..];

      Assert.Equal( model.Why.Select( w => w.Target ), TABLE_ROWS.Matches( why[..why.IndexOf( BenchLegends.H_THRESHOLD, StringComparison.Ordinal )] ).Select( m => m.Groups[1].Value ) );
      Assert.Contains( "(<span class=\"bench-label\">recorded</span>; <code>results:targets[redis].index#HNSW</code>)", why );
      string sqlitevec = Regex.Match( why, "<tr data-target=\"sqlitevec\">.*?</tr>" ).Value;
      Assert.Contains( "<td class=\"n\">-</td><td class=\"n\">-</td>", sqlitevec );
      Assert.Contains( "<td class=\"n\">0.57</td><td class=\"n\">0.36</td>", sqlitevec );
   }

   /// <summary>
   /// Recall prints as hits per run for each session out of the perfect count, and says whether the runs differ.
   /// </summary>
   [Fact]
   public void Recall_PrintsHitsPerRun_AndWhetherTheyDiffer()
   {
      string html = Html();
      string recall = html[html.IndexOf( BenchLegends.H_RECALL, StringComparison.Ordinal )..];
      string qdrant = Regex.Match( recall, "<tr data-target=\"qdrant\">.*?</tr>" ).Value;

      Assert.Contains( "<td>198, 198, 198 of 200</td><td>198, 198, 197 of 200</td><td class=\"bench-no\">yes</td>", qdrant );
      Assert.Contains( "<td>200, 200, 200 of 200</td><td>200, 200, 200 of 200</td><td>no</td>", Regex.Match( recall, "<tr data-target=\"redis\">.*?</tr>" ).Value );
   }

   /// <summary>
   /// The threshold figures, the basis moves with the differences between their runs, the exclusions and the folders left out are
   /// printed from the file's fields.
   /// </summary>
   [Fact]
   public void Threshold_AndBasis_ArePrintedFromFields()
   {
      string html = Html();

      Assert.Contains( "Threshold 35.00%; basis points 3500; ratio 1.35", html );
      Assert.Contains( "<td>oneEngine</td><td>p50Ms</td><td class=\"n\">29.08%</td><td>mongodb</td><td>20261005-023459-eshoponweb, 20261006-130619-eshoponweb</td>", html );
      Assert.Contains( "turbo: on, off; uncore: not pinned, 0x1e1e", html );
      Assert.Contains( "oneEngine without machine control", html );
      Assert.Contains( "log tables switched off", html );
      Assert.Contains( "machine control off", html );
   }

   /// <summary>
   /// The largest move, the moves of the exclusions kept in and a move nested in a metric's kept-in list are found wherever the file puts them and
   /// listed under the path of their kind; a recorded measurement is listed among the differences.
   /// </summary>
   [Fact]
   public void Basis_ListsEveryMoveTheFileHolds_UnderTheKindThatHoldsIt()
   {
      JsonObject Move( int bp, string who ) => new() { ["moveBp"] = bp, ["target"] = who, ["runs"] = new JsonArray( "r1", "r2" ), ["values"] = new JsonArray( 1.0, 2.0 ), ["differences"] = new JsonArray( "turbo: on, off" ), ["measured"] = new JsonArray( "median MHz 3200, 3492" ) };
      string html = Html( root =>
      {
         var basis = (JsonObject)root["basis"]!;
         basis["maxMove"] = Move( 2908, "mongodb" );
         basis["exclusionsKept"] = new JsonArray( new JsonObject { ["kind"] = "method defect", ["oneEngine"] = Move( 7440, "vespa" ), ["pair"] = Move( 9560, "vespa" ), ["tBpIfKept"] = 10000 } );
         ( (JsonObject)( (JsonObject)basis["perMetric"]! )["p50Ms"]! )["oneEngineExclusionsKept"] = new JsonArray( new JsonObject { ["kind"] = "unrecorded config change", ["oneEngine"] = Move( 3070, "clickhouse" ) } );
      } );

      Assert.Contains( "<td>maxMove</td>", html );
      Assert.Contains( "<td>exclusionsKept.oneEngine</td>", html );
      Assert.Contains( "<td>exclusionsKept.pair</td>", html );
      Assert.Contains( "<td>oneEngineExclusionsKept.oneEngine</td><td>p50Ms</td><td class=\"n\">30.70%</td><td>clickhouse</td>", html );
      Assert.Contains( "turbo: on, off; measured: median MHz 3200, 3492", html );
   }

   /// <summary>
   /// Drift prints its sessions, the median and largest move, a move for every engine and metric, and the lists of unconfirmed and
   /// close pairs by display name.
   /// </summary>
   [Fact]
   public void Drift_PrintsMoves_AndTheLists()
   {
      string html = Html();

      Assert.Contains( "First session v7; second session v8; median absolute move 1.50%; largest move sqlitevec, qps8, 3.90%", html );
      Assert.Contains( "<td class=\"n\">1.50%</td>", html );
      Assert.Contains( "<code>p50Ms</code> MariaDB ahead of pgvector, held in v7", html );
      Assert.Contains( $"<code>qps8</code> Redis and MariaDB, {BenchLegends.L_MIN_RATIO} 1.3100", html );
   }

   /// <summary>
   /// The machine line is made from the file's machine block, the clock table from its clock block, a dropped warning with the recomputed
   /// median figures, and the images table from its images with the id cut to twelve characters.
   /// </summary>
   [Fact]
   public void MachineClockAndImages_ArePrintedFromTheirBlocks()
   {
      string html = Html();

      Assert.Contains( "Machine: CPU Intel(R) Xeon(R) CPU E5-1620 v3 @ 3.50GHz; Logical CPUs 8; RAM (GiB) 62.7; OS Ubuntu 24.04.5 LTS; Governor performance; Partition client 0-1,4-5; engines 2-3,6-7", html );
      Assert.Contains( "<td>clock off its pinned value during oracle default@8: the engine CPUs averaged 3121 MHz</td><td>oracle</td><td>default@8</td><td>3492</td><td>3492</td>", html );
      Assert.Contains( "Clock v7: pinned true; pinnedMhz 3500; noTurbo 1; uncore 0x1e1e; clockOffPasses 0; clockUnreadPasses 0; legacyParsed true " + System.Net.WebUtility.HtmlEncode( BenchLegends.L_LEGACY_PARSED ), html );
      Assert.Contains( "Clock v8: pinned true; pinnedMhz 3500; noTurbo 1; uncore 0x1e1e; clockOffPasses 0; clockUnreadPasses 0; legacyParsed false", html );
      Assert.Contains( "<code>aaaaaaaaaaaa</code>", html );
   }

   /// <summary>
   /// Run folders of a session link to their pages when the name is safe, and print as plain text when it is not.
   /// </summary>
   [Fact]
   public void RunFolders_LinkOnlyWhenTheNameIsSafe()
   {
      string html = Html( root => root["sessions"]![0]!["runs"]![0]!["folder"] = "../../etc/passwd" );

      Assert.Contains( "<a href=\"/bench-results/20261006-142724-eshoponweb\">20261006-142724-eshoponweb</a>", html );
      Assert.Contains( "<a href=\"/bench-results/passwd\">passwd</a>", html );
      Assert.DoesNotContain( "href=\"/bench-results/../", html );
   }

   /// <summary>
   /// Names, notes, flag evidence, sentences and facts that carry markup are printed as text.
   /// </summary>
   [Fact]
   public void Markup_InAnyField_IsEncoded()
   {
      string html = Html( root =>
      {
         JsonObject row = Row( root, "p50Ms", "redis" );
         row["display"] = "<b>Redis</b>";
         row["note"] = "<script>alert(1)</script>";
         ( (JsonArray)row["flags"]! ).Add( new JsonObject { ["code"] = "<i>x</i>", ["text"] = "\"><img src=x>" } );
         ( (JsonArray)root["sentences"]! ).Add( BenchResultsV8Fixture.Sentence( "disclosure.x", "<u>bad</u>" ) );
         root["why"]![0]!["facts"]![0]!["text"] = "<em>fact</em>";
      } );

      Assert.DoesNotContain( "<script>", html );
      Assert.DoesNotContain( "<img", html );
      Assert.DoesNotContain( "<u>bad", html );
      Assert.DoesNotContain( "<em>fact", html );
      Assert.Contains( "&lt;b&gt;Redis&lt;/b&gt;", html );
   }


   /// <summary>
   /// Every section of the slot catalogue (the stopped block aside) has a sentence in the fixture, and none of them lands under the heading
   /// for statements with no place: each section prints its own sentences.
   /// </summary>
   [Fact]
   public void EverySectionOfTheCatalogue_PrintsItsOwnSentences()
   {
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() );
      string html = BenchConsolidatedHtml.Block( model );

      foreach( string section in BenchSlots.Sections.Where( s => s != BenchSlots.STOPPED ) )
      {
         Assert.Contains( model.Sentences, s => BenchSlotSection( s.Slot ) == section );
      }

      Assert.DoesNotContain( BenchLegends.H_UNPLACED, html );
   }

   /// <summary>
   /// A flag of every code the page describes prints its marker and its legend, so a code added to the list shows.
   /// </summary>
   [Fact]
   public void EveryKnownFlagCode_PrintsItsMarkerAndLegend()
   {
      string html = Html( root =>
      {
         var flags = (JsonArray)Row( root, "p50Ms", "redis" )["flags"]!;
         foreach( string code in BenchLegends.KnownFlagCodes )
         {
            flags.Add( new JsonObject { ["code"] = code, ["text"] = "evidence of " + code } );
         }
      } );

      foreach( string code in BenchLegends.KnownFlagCodes )
      {
         Assert.Contains( $"<code>{code}</code>", html );
         Assert.Contains( $"evidence of {code}", html );
      }

      foreach( string legend in BenchLegends.KnownFlagCodes.Select( BenchLegends.Legend ).Distinct() )
      {
         Assert.Equal( 1, Count( html, WebUtility.HtmlEncode( legend ) ) );
      }

      Assert.DoesNotContain( "??", RowOf( html, "p50Ms", "redis" ) );
   }

   /// <summary>
   /// A file that leaves an image id out, writes a count with a fraction of zero, or marks a quotation on its source instead of on the
   /// sentence is still read: an id prints as a dash, the count as a whole number, and the sentence as a quotation.
   /// </summary>
   [Fact]
   public void TheReader_ToleratesTheSpellingsAFileMayUse()
   {
      string html = Html( root =>
      {
         root["images"]![0]!.AsObject().Remove( "id" );
         root["drift"]!["closeToLine"]![0]!["minRatioBp"] = 13508.0;
         ( (JsonArray)root["sentences"]! ).Add( new JsonObject { ["slot"] = "method.src", ["text"] = "Recorded words.", ["sources"] = new JsonArray( new JsonObject { ["kind"] = "quote", ["ref"] = "x", ["value"] = "y" } ) } );
      } );

      Assert.Contains( "<code>-</code>", html );
      Assert.Contains( $"{BenchLegends.L_MIN_RATIO} 1.3508", html );
      Assert.Contains( "<blockquote class=\"bench-quote\">Recorded words.</blockquote>", html );
   }

   /// <summary>
   /// Pages of two sessions show a column for each, and a one-session file shows one.
   /// </summary>
   [Fact]
   public void ASingleSessionFile_ReadsWithoutTheBlocksOnlyTwoSessionsNeed()
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         ( (JsonArray)root["sessions"]! ).RemoveAt( 1 );
         root.Remove( "basis" );
         root.Remove( "drift" );
         root.Remove( "clock" );
         root.Remove( "why" );
         foreach( JsonNode? metric in (JsonArray)root["metrics"]! )
         {
            foreach( JsonNode? row in (JsonArray)metric!["rows"]! )
            {
               ( (JsonObject)row!["perSession"]! ).Remove( "v8" );
            }
         }
      } );

      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) );

      Assert.Contains( "v7 min to max", html );
      Assert.DoesNotContain( "v8 min to max", html );
      Assert.DoesNotContain( BenchLegends.H_WHY, html );
   }

   /// <summary>
   /// A file that does not hold together is refused with the path of what is wrong, never drawn.
   /// </summary>
   [Theory]
   [InlineData( "no-sessions", "sessions" )]
   [InlineData( "no-threshold", "threshold" )]
   [InlineData( "no-headline", "exactly one headline" )]
   [InlineData( "two-headlines", "exactly one headline" )]
   [InlineData( "long-headline", "words" )]
   [InlineData( "stopped-with-headline", "no headline" )]
   [InlineData( "bad-status", "status" )]
   [InlineData( "ranked-without-median", "no median" )]
   [InlineData( "unknown-session", "names session" )]
   [InlineData( "unknown-target", "not a row of the table" )]
   [InlineData( "self-reference", "not a row of the table" )]
   [InlineData( "one-sided-separation", "disagree" )]
   [InlineData( "order-contradicts", "not in median order" )]
   [InlineData( "stopped-by-status-with-headline", "no headline" )]
   [InlineData( "stopped-by-g3-with-headline", "no headline" )]
   [InlineData( "stopped-by-g1-with-headline", "no headline" )]
   [InlineData( "duplicate-row", "twice" )]
   [InlineData( "no-drift-for-two-sessions", "drift" )]
   [InlineData( "no-basis-for-two-sessions", "basis" )]
   [InlineData( "no-clock-for-two-sessions", "clock" )]
   [InlineData( "no-why-for-two-sessions", "why" )]
   [InlineData( "bad-number", "perSession" )]
   [InlineData( "bad-lower-is-better", "lowerIsBetter" )]
   [InlineData( "sentence-without-text", "sentences[0].text" )]
   public void TheReader_RefusesAFileThatDoesNotHoldTogether( string defect, string expected )
   {
      string json = BenchResultsV8Fixture.Json( root => Break( root, defect ) );

      var ex = Assert.Throws<InvalidDataException>( () => BenchConsolidatedReader.Read( json ) );
      Assert.Contains( expected, ex.Message );
   }

   /// <summary>
   /// A file that is not an object, and an object in no shape the page knows, are refused.
   /// </summary>
   [Theory]
   [InlineData( "[]" )]
   [InlineData( "{}" )]
   [InlineData( "{\"a\": 1}" )]
   public void Detect_RefusesWhatIsNoShapeOfTheFile( string json )
   {
      Assert.Throws<InvalidDataException>( () => BenchConsolidatedReader.Detect( json ) );
   }

   /// <summary>
   /// Malformed JSON is refused by the parser, which the pages catch with the same list of exceptions.
   /// </summary>
   [Fact]
   public void Detect_PassesAParserErrorThrough()
   {
      Assert.ThrowsAny<JsonException>( () => BenchConsolidatedReader.Detect( "{ not json" ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The section of a slot.
   /// </summary>
   private static string BenchSlotSection( string slot )
   {
      int dot = slot.IndexOf( '.' );
      return dot < 0 ? slot : slot[..dot];
   }

   /// <summary>
   /// The block of the fixture after an optional change.
   /// </summary>
   private static string Html( Action<JsonObject>? change = null )
   {
      return BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( change ) ) );
   }

   /// <summary>
   /// Text with tags removed and entities decoded, so a sentence can be searched as a reader reads it.
   /// </summary>
   private static string Plain( string html )
   {
      return WebUtility.HtmlDecode( Regex.Replace( html, "<[^>]+>", " " ) );
   }

   /// <summary>
   /// How many times a text occurs in another.
   /// </summary>
   private static int Count( string text, string part )
   {
      return Regex.Matches( text, Regex.Escape( part ) ).Count;
   }

   /// <summary>
   /// The rows (data-target attributes) of one metric's table, in the order printed.
   /// </summary>
   private static MatchCollection TableOf( string html, string metric )
   {
      int start = html.IndexOf( $"data-metric=\"{metric}\"", StringComparison.Ordinal );
      Assert.True( start >= 0, $"no table for {metric}" );
      int end = html.IndexOf( "</table>", start, StringComparison.Ordinal );
      return TABLE_ROWS.Matches( html[start..end] );
   }

   /// <summary>
   /// One engine's row of one metric table.
   /// </summary>
   private static string RowOf( string html, string metric, string target )
   {
      int start = html.IndexOf( $"data-metric=\"{metric}\"", StringComparison.Ordinal );
      int end = html.IndexOf( "</table>", start, StringComparison.Ordinal );
      Match row = Regex.Match( html[start..end], $"<tr data-target=\"{Regex.Escape( target )}\">.*?</tr>" );
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
   /// Puts one defect into the fixture's JSON.
   /// </summary>
   private static void Break( JsonObject root, string defect )
   {
      var sentences = (JsonArray)root["sentences"]!;
      switch( defect )
      {
         case "no-sessions": root.Remove( "sessions" ); break;
         case "no-threshold": root.Remove( "threshold" ); break;
         case "no-headline": sentences.RemoveAt( 0 ); break;
         case "two-headlines": sentences.Add( BenchResultsV8Fixture.Sentence( "headline", "Another." ) ); break;
         case "long-headline": sentences[0]!["text"] = string.Join( " ", Enumerable.Repeat( "word", 35 ) ); break;
         case "stopped-with-headline": root["guards"] = new JsonObject { ["g2"] = new JsonObject { ["stopped"] = true } }; break;
         case "stopped-by-status-with-headline": root["status"] = "stopped"; break;
         case "stopped-by-g3-with-headline": root["guards"] = new JsonObject { ["g3"] = new JsonObject { ["stopped"] = true, ["targets"] = new JsonArray() } }; break;
         case "stopped-by-g1-with-headline": root["guards"] = new JsonObject { ["g1"] = new JsonObject { ["stopped"] = true } }; break;
         case "bad-status": Row( root, "p50Ms", "redis" )["status"] = "tied"; break;
         case "ranked-without-median": Row( root, "p50Ms", "redis" ).Remove( "median" ); break;
         case "unknown-session": Row( root, "p50Ms", "redis" )["perSession"]!.AsObject()["v9"] = new JsonObject { ["min"] = 1, ["max"] = 2, ["runs"] = new JsonArray() }; break;
         case "unknown-target": ( (JsonArray)Row( root, "p50Ms", "redis" )["notSeparatedFrom"]! ).Add( "nonesuch" ); break;
         case "self-reference": ( (JsonArray)Row( root, "p50Ms", "redis" )["notSeparatedFrom"]! ).Add( "redis" ); break;
         case "one-sided-separation": ( (JsonArray)Row( root, "p50Ms", "redis" )["notSeparatedFrom"]! ).Add( "sql" ); break;
         case "order-contradicts": Row( root, "p50Ms", "redis" )["median"] = 50.0; break;
         case "duplicate-row": ( (JsonArray)root["metrics"]![0]!["rows"]! ).Add( Row( root, "p50Ms", "redis" ).DeepClone() ); break;
         case "no-drift-for-two-sessions": root.Remove( "drift" ); break;
         case "no-basis-for-two-sessions": root.Remove( "basis" ); break;
         case "no-clock-for-two-sessions": root.Remove( "clock" ); break;
         case "no-why-for-two-sessions": root.Remove( "why" ); break;
         case "bad-number": Row( root, "p50Ms", "redis" )["perSession"]!["v7"]!["min"] = "fast"; break;
         case "bad-lower-is-better": root["metrics"]![0]!["lowerIsBetter"] = "yes"; break;
         case "sentence-without-text": sentences[0]!.AsObject().Remove( "text" ); break;
         default: throw new ArgumentException( defect );
      }
   }

   #endregion Private Methods
}
