using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The summary block on /bench-results and each run page: the chart has one bar per engine in
/// median order with every name escaped, the numbers are the consolidated.json medians, and the
/// headline, table and "yes/no" column read as intended.
/// The ranking is titled as request speed on a small collection and shown as tie bands (engines
/// whose min to max ranges overlap share a band) instead of strict ranks; the band cases here are
/// the ones the Bench tool's ConsolidateTests expect, so the two copies of the rule agree.
/// </summary>
public partial class BenchResultsSummaryTests
{
   #region Data Members

   private const string OLD_PUBLISHED = "published-2026-10-04";
   private const int MAX_UP = 10;
   private const string PARTITION = "client 0,4; engines 1-3,5-7";
   private static readonly Regex ENGINE_ATTR = new( "<g class=\"bc-row\" data-engine=\"([^\"]*)\">", RegexOptions.Compiled );

   private const string FRAMING_SOURCE = "src/GenericVectorBuilder.Bench/Stats/ConsolidateFraming.cs";
   private const string BANDS_SOURCE = "src/GenericVectorBuilder.Bench/Stats/ConsolidateBands.cs";
   private const string SMALL_TITLE = "<h2 class=\"bench-title\">Request speed on a small collection (524 vectors)</h2><p class=\"bench-framing muted\">Measured end to end through each engine&#39;s .NET client; at this size it reflects per-request cost including the client library, not index scaling.</p>";

   /// <summary>The Bench tool's hand-made band case, QPS at 8 as median, min, max over three runs.</summary>
   private static readonly (string Name, double Median, double Min, double Max)[] BAND_CASE =
   {
      ( "a", 1050, 1000, 1100 ), ( "b", 1150, 1090, 1200 ), ( "c", 850, 800, 900 ), ( "d", 910, 880, 950 ), ( "e", 305, 300, 310 ),
   };

   /// <summary>The Bench tool's chained case: x overlaps y, y overlaps z, u touches v, w is alone.</summary>
   private static readonly (string Name, double Median, double Min, double Max)[] CHAIN_CASE =
   {
      ( "x", 150, 100, 200 ), ( "y", 250, 190, 300 ), ( "z", 350, 290, 400 ), ( "u", 15, 10, 20 ), ( "v", 25, 20, 30 ), ( "w", 1.5, 1, 2 ),
   };

   private const string SYNTHETIC = """
   {
     "redis":   { "runs": 3, "qps8": { "median": 2000.4, "min": 1900, "max": 3100 }, "p50": { "median": 1.51 }, "recall": { "median": 1 } },
     "<b>x&y</b>": { "runs": 3, "qps8": { "median": 4000, "min": 3500, "max": 4100 }, "p50": { "median": 2.04 }, "recall": { "median": 0.9650000000000002 } },
     "sqlitevec": { "runs": 3, "qps8": { "median": 500.6, "min": 450, "max": 510 }, "p50": { "median": 12.345 }, "recall": { "median": 0.9999999 } },
     "sql-diskann": { "runs": 3, "qps8": { "median": 1234.6, "min": 1000, "max": 1300 }, "p50": { "median": 4.5 }, "recall": { "median": 1 } },
     "broken": { "runs": 3, "p50": { "median": 1 } }
   }
   """;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// One bar group per engine with a result, in rank order (fastest first), and the engine
   /// that has no throughput number gets no bar.
   /// </summary>
   [Fact]
   public void Chart_HasOneBarPerEngineInRankOrder()
   {
      BenchSummary summary = BenchSummaryReader.FromConsolidated( SYNTHETIC );
      string svg = BenchChart.Svg( summary );

      List<string> order = ENGINE_ATTR.Matches( svg ).Select( m => m.Groups[1].Value ).ToList();
      Assert.Equal( new[] { "&lt;b&gt;x&amp;y&lt;/b&gt;", "redis", "sql-diskann", "sqlitevec" }, order );
      Assert.Equal( 4, Regex.Matches( svg, "<rect class=\"bc-bar[^\"]*\" x=\"0\" y=\"\\d+\" width=\"[0-9.]+%\" height=\"12\" rx=\"4\"/>" ).Count );
      Assert.Equal( new[] { "broken" }, summary.NoResult );
      Assert.StartsWith( "<svg class=\"bench-chart\"", svg );
      Assert.DoesNotContain( "<script", svg );
   }

   /// <summary>
   /// Engine names from the data are escaped in the chart, its hover text and the table.
   /// </summary>
   [Fact]
   public void Chart_And_Table_EscapeNames()
   {
      BenchSummary summary = BenchSummaryReader.FromConsolidated( SYNTHETIC );
      string svg = BenchChart.Svg( summary );
      string table = BenchSummaryHtml.Table( summary );

      Assert.DoesNotContain( "<b>", svg );
      Assert.DoesNotContain( "<b>", table );
      Assert.Contains( ">&lt;b&gt;x&amp;y&lt;/b&gt;<tspan class=\"bc-note\" dx=\"8\">band 1</tspan></text>", svg );
      Assert.Contains( "<title>&lt;b&gt;x&amp;y&lt;/b&gt;: 4,000 searches/s", svg );
      Assert.Contains( "<td>&lt;b&gt;x&amp;y&lt;/b&gt;</td>", table );
   }

   /// <summary>
   /// The longest reach (bar or max line) sets the scale; Redis is hatched and labelled; the
   /// value label sits past the range line when the range reaches further than the bar.
   /// </summary>
   [Fact]
   public void Chart_ScalesToLongestReach_AndMarksRedisInMemory()
   {
      string svg = BenchChart.Svg( BenchSummaryReader.FromConsolidated( SYNTHETIC ) );

      Assert.Contains( "x2=\"78%\"", svg );
      Assert.Contains( "<rect class=\"bc-bar bc-mem\"", svg );
      Assert.Contains( ">Redis<tspan class=\"bc-note\" dx=\"8\">in memory</tspan><tspan class=\"bc-note\" dx=\"8\">band 2</tspan></text>", svg );
      double redisMax = 3100 * BenchChart.PLOT_PERCENT / 4100;
      Assert.Contains( $"<text class=\"bc-value\" x=\"{redisMax.ToString( "0.##", CultureInfo.InvariantCulture )}%\" dx=\"8\"", svg );
   }

   /// <summary>
   /// Single run pages draw no range lines, and the headline says "in this run".
   /// </summary>
   [Fact]
   public void RunResults_HaveNoRanges_AndSayInThisRun()
   {
      string json = """{ "targets": [ { "name": "sql", "search": { "qps": { "1": 10, "8": 50 }, "p50Ms": 6.81, "recall": 1 } }, { "name": "mariadb", "search": { "qps": { "8": 99.6 }, "p50Ms": 1.9, "recall": 1 } }, { "name": "oracle" } ] }""";
      BenchSummary summary = BenchSummaryReader.FromRunResults( json );

      Assert.False( summary.HasRanges );
      Assert.DoesNotContain( "bc-range", BenchChart.Svg( summary ) );
      Assert.Equal( "MariaDB was fastest in this run: 100 searches per second with 8 at once.", BenchSummaryHtml.Headline( summary ) );
      Assert.Equal( new[] { "Oracle 23ai Free" }, summary.NoResult );
   }

   /// <summary>
   /// Table cells: rank, friendly name, whole searches per second with commas, p50 with one
   /// decimal, and yes or no from recall (float noise under 1 still counts as yes).
   /// </summary>
   [Fact]
   public void Table_ShowsRoundedNumbersAndSameAnswers()
   {
      string table = BenchSummaryHtml.Table( BenchSummaryReader.FromConsolidated( SYNTHETIC ) );

      Assert.Contains( "<tr><td class=\"n\">1</td><td>&lt;b&gt;x&amp;y&lt;/b&gt;</td><td class=\"n\">4,000</td><td class=\"n\">2.0</td><td class=\"bench-no\">no (96.5%)</td></tr>", table );
      Assert.Contains( "<tr><td class=\"n\">2</td><td>Redis <span class=\"muted\">(in memory)</span></td><td class=\"n\">2,000</td><td class=\"n\">1.5</td><td>yes</td></tr>", table );
      Assert.Contains( "<tr><td class=\"n\">3</td><td>SQL Server 2025 + DiskANN</td><td class=\"n\">1,235</td><td class=\"n\">4.5</td><td>yes</td></tr>", table );
      Assert.Contains( "<tr><td class=\"n\">4</td><td>sqlite-vec</td><td class=\"n\">501</td><td class=\"n\">12.3</td><td>yes</td></tr>", table );
      Assert.Contains( "<td colspan=\"3\" class=\"muted\">no result</td>", table );
   }

   /// <summary>
   /// The headline names the leader and, when the leader missed exact answers, says so.
   /// </summary>
   [Fact]
   public void Headline_NamesLeaderAndWarnsOnRecall()
   {
      string headline = BenchSummaryHtml.Headline( BenchSummaryReader.FromConsolidated( SYNTHETIC ) );

      Assert.Equal( "<b>x&y</b> was fastest: 4,000 searches per second with 8 at once (median of 3 runs), returning 96.5% of the exact top 10.", headline );
   }

   /// <summary>
   /// Against the published-shape fixture (19 engines): every engine's summary numbers equal its consolidated.json
   /// medians, the rank order is fastest median first, and the table shows each median.
   /// </summary>
   [Fact]
   public void Summary_EqualsPublishedConsolidatedMedians()
   {
      string json = BenchResultsFixtures.OLD_PUBLISHED_JSON;
      BenchSummary summary = BenchSummaryReader.FromConsolidated( json );
      using JsonDocument doc = JsonDocument.Parse( json );
      var medians = doc.RootElement.EnumerateObject().ToDictionary( p => p.Name, p => p.Value );

      Assert.Equal( medians.Count, summary.Ranked.Count );
      Assert.Equal( 3, summary.Runs );
      foreach( BenchEngineRow row in summary.Ranked )
      {
         JsonElement e = medians[row.Key];
         Assert.Equal( e.GetProperty( "qps8" ).GetProperty( "median" ).GetDouble(), row.Qps8 );
         Assert.Equal( e.GetProperty( "qps8" ).GetProperty( "min" ).GetDouble(), row.Qps8Min );
         Assert.Equal( e.GetProperty( "qps8" ).GetProperty( "max" ).GetDouble(), row.Qps8Max );
         Assert.Equal( e.GetProperty( "p50" ).GetProperty( "median" ).GetDouble(), row.P50Ms );
         Assert.Equal( e.GetProperty( "recall" ).GetProperty( "median" ).GetDouble(), row.Recall );
      }

      List<string> expected = medians.OrderByDescending( m => m.Value.GetProperty( "qps8" ).GetProperty( "median" ).GetDouble() ).Select( m => m.Key ).ToList();
      Assert.Equal( expected, summary.Ranked.Select( r => r.Key ).ToList() );
      string table = BenchSummaryHtml.Table( summary );
      foreach( BenchEngineRow row in summary.Ranked )
      {
         Assert.Contains( $"<td class=\"n\">{BenchChart.Count( medians[row.Key].GetProperty( "qps8" ).GetProperty( "median" ).GetDouble() )}</td>", table );
      }

      BenchEngineRow leader = summary.Ranked[0];
      Assert.StartsWith( $"{BenchSummaryReader.FriendlyName( expected[0] )} was fastest: {BenchChart.Count( leader.Qps8 )} searches per second", BenchSummaryHtml.Headline( summary ) );
   }

   /// <summary>
   /// Against a run fixture: one row per target, values from search.qps["8"].
   /// </summary>
   [Fact]
   public void Summary_EqualsRunResults()
   {
      string json = BenchResultsFixtures.RUN_RESULTS_JSON;
      BenchSummary summary = BenchSummaryReader.FromRunResults( json );
      using JsonDocument doc = JsonDocument.Parse( json );
      var qps = doc.RootElement.GetProperty( "targets" ).EnumerateArray()
         .ToDictionary( t => t.GetProperty( "name" ).GetString()!, t => t.GetProperty( "search" ).GetProperty( "qps" ).GetProperty( "8" ).GetDouble() );

      Assert.Equal( qps.Count, summary.Ranked.Count );
      Assert.All( summary.Ranked, r => Assert.Equal( qps[r.Key], r.Qps8 ) );
      Assert.Equal( qps.MaxBy( p => p.Value ).Key, summary.Ranked[0].Key );
      Assert.Equal( summary.Ranked.Count, ENGINE_ATTR.Matches( BenchChart.Svg( summary ) ).Count );
   }

   /// <summary>
   /// The consolidated.json the consolidate command writes (targetSummaries, settings, flags) is
   /// read: engines ranked by the median of QPS with 8 at once with their min and max, the engine
   /// without that level listed as no result, each flag attached to its engine with the number of
   /// runs it covers, and the settings read into the machine conditions.
   /// </summary>
   [Fact]
   public void ConsolidateOutput_IsRead_WithFlagsAndConditions()
   {
      string json = ReportJson( 2, Settings( "performance", PARTITION, "Release" ),
         new[]
         {
            Summary( "qdrant", 3338.5, 3300.4, 3376.5, 1.35, 1.0 ), Summary( "sql", 900, 850.5, 989.4, 4.5, 1.0 ), Summary( "oracle", null, 0, 0, 2.0, 1.0 ),
         },
         new[]
         {
            ( "qdrant", "spread", "p50 ms 1.28 to 1.41 (x1.10 over 2 runs)", 2 ), ( "sql", "governor-not-performance", "governor schedutil", 1 ), ( "sql", "spread", "QPS@8 850.5 to 989.4", 2 ),
         } );

      BenchSummary summary = BenchSummaryReader.FromConsolidated( json );

      Assert.Equal( new[] { "qdrant", "sql" }, summary.Ranked.Select( r => r.Key ).ToArray() );
      Assert.Equal( new[] { "Oracle 23ai Free" }, summary.NoResult );
      Assert.Equal( 2, summary.Runs );
      BenchEngineRow qdrant = summary.Ranked[0];
      Assert.Equal( 3338.5, qdrant.Qps8 );
      Assert.Equal( 3300.4, qdrant.Qps8Min );
      Assert.Equal( 3376.5, qdrant.Qps8Max );
      Assert.Equal( 1.35, qdrant.P50Ms );
      Assert.Equal( new[] { "spread" }, qdrant.Flags!.Select( f => f.Kind ).ToArray() );
      Assert.Equal( "p50 ms 1.28 to 1.41 (x1.10 over 2 runs) [2 of 2 runs]", qdrant.Flags![0].Detail );
      Assert.Equal( new[] { "governor-not-performance", "spread" }, summary.Ranked[1].Flags!.Select( f => f.Kind ).ToArray() );
      Assert.Equal( "governor schedutil [1 of 2 runs]", summary.Ranked[1].Flags![0].Detail );
      Assert.Equal( new BenchConditions( "performance", PARTITION, "Release", "20", "60" ), summary.Conditions );
   }

   /// <summary>
   /// An engine with warnings shows one small marker per kind after its name, with the evidence as
   /// hover text; the evidence is HTML-escaped; an engine without warnings renders exactly as it
   /// did before markers existed.
   /// </summary>
   [Fact]
   public void Table_ShowsOneMarkerPerKind_WithEscapedEvidence()
   {
      string json = ReportJson( 2, Settings( "performance", PARTITION, "Release" ),
         new[] { Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 ), Summary( "redis", 2000, 1900, 2100, 1.5, 1.0 ) },
         new[] { ( "qdrant", "spread", "p50 <script>alert(1)</script>", 2 ), ( "qdrant", "p50-mean-inconsistent", "p50 6.72 ms, mean 5.22 ms", 1 ), ( "qdrant", "index-not-ready-after-load", "ready false", 2 ), ( "qdrant", "index-not-ready-after-search", "ready false", 2 ) } );

      string table = BenchSummaryHtml.Table( BenchSummaryReader.FromConsolidated( json ) );

      Assert.DoesNotContain( "<script>", table );
      Assert.Equal( 3, Regex.Matches( table, "<abbr class=\"bench-mark\"" ).Count );
      Assert.Contains( ">Sp</abbr>", table );
      Assert.Contains( ">Pm</abbr>", table );
      Assert.Contains( ">Ix</abbr>", table );
      Assert.Contains( "title=\"spread: p50 &lt;script&gt;alert(1)&lt;/script&gt; [2 of 2 runs]\"", table );
      Assert.Contains( "index-not-ready-after-load: ready false [2 of 2 runs]\nindex-not-ready-after-search: ready false [2 of 2 runs]", table );
      Assert.Contains( "<tr><td class=\"n\">2</td><td>Redis <span class=\"muted\">(in memory)</span></td><td class=\"n\">2,000</td><td class=\"n\">1.5</td><td>yes</td></tr>", table );
   }

   /// <summary>
   /// The legend names each marker once with its plain-words meaning and how many engines carry
   /// it, keeps the evidence engine by engine in a closed details element (so it works without
   /// hover), and is absent when no engine has a warning.
   /// </summary>
   [Fact]
   public void Legend_ExplainsEachMarkerOnce_AndKeepsTheEvidence()
   {
      string json = ReportJson( 3, Settings( "performance", PARTITION, "Release" ),
         new[] { Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 ), Summary( "sql", 900, 800, 950, 4.5, 1.0 ), Summary( "mariadb", 4000, 3900, 4100, 2.0, 1.0 ) },
         new[] { ( "qdrant", "spread", "QPS@8 2900.0 to 3400.0", 3 ), ( "sql", "spread", "p50 ms 4.0 to 5.0", 3 ), ( "sql", "unsettled-target", "not settled: compaction running", 1 ) } );
      BenchSummary summary = BenchSummaryReader.FromConsolidated( json );

      string legend = BenchSummaryHtml.Legend( summary );

      Assert.Single( Regex.Matches( legend, ">Sp</abbr>" ) );
      Assert.Contains( "Spread: the best and worst runs differ by more than 15%", legend );
      Assert.Contains( "(2 of 3 engines)", legend );
      Assert.Contains( "Unsettled: the engine was still starting, building or compacting when it was timed. <span class=\"muted\">(1 of 3 engines)</span>", legend );
      Assert.True( legend.IndexOf( ">Sp</abbr>", StringComparison.Ordinal ) < legend.IndexOf( ">Un</abbr>", StringComparison.Ordinal ) );
      Assert.Contains( "<details class=\"bench-evidence\"><summary>Evidence behind the markers</summary>", legend );
      Assert.Contains( "<li><strong>SQL Server 2025</strong> Un unsettled-target: not settled: compaction running [1 of 3 runs]</li>", legend );
      Assert.Equal( string.Empty, BenchSummaryHtml.Legend( BenchSummaryReader.FromConsolidated( SYNTHETIC ) ) );
      Assert.DoesNotContain( "bench-legend", BenchSummaryHtml.Block( summary with { Ranked = summary.Ranked.Select( r => r with { Flags = null } ).ToList() }, "n", null ) );
   }

   /// <summary>
   /// The machine conditions are one line: governor, CPU partition, build, and the warm-up and
   /// exact-mode settings when recorded; a wrong governor or a non-Release build is marked; a
   /// field the result files lack says "not recorded".
   /// </summary>
   [Fact]
   public void ConditionsLine_StatesGovernorPartitionAndBuild()
   {
      Assert.Equal( "<p class=\"bench-conditions muted\">Machine: CPU governor performance; CPU partition client 0,4; engines 1-3,5-7; Release build; exact mode up to 60 s.</p>"
         + "<p class=\"bench-warmup muted\">Before each timed pass: at least 20 warm-up searches (the rest of the warm-up method was not recorded in these results).</p>",
         BenchSummaryHtml.ConditionsLine( new BenchConditions( "performance", PARTITION, "Release", "20", "60" ) ) );
      Assert.Equal( "<p class=\"bench-conditions muted\">Machine: CPU governor performance; CPU partition client 0,4; engines 1-3,5-7; Release build; exact mode up to 60 s.</p>"
         + "<p class=\"bench-warmup muted\">Before each timed pass: warm-up for at least 15 s and 20 searches; then a 3 s trial.</p>",
         BenchSummaryHtml.ConditionsLine( new BenchConditions( "performance", PARTITION, "Release", "20", "60", "warm-up for at least 15 s and 20 searches; then a 3 s trial" ) ) );
      Assert.Equal( "<p class=\"bench-conditions muted\">Machine: CPU governor <span class=\"bench-no\">schedutil</span>; CPU partition not recorded; <span class=\"bench-no\">Debug build</span>.</p>",
         BenchSummaryHtml.ConditionsLine( new BenchConditions( "schedutil", null, "Debug", null, null ) ) );
      Assert.Equal( "<p class=\"bench-conditions muted\">Machine: CPU governor performance; CPU partition not recorded; build not recorded.</p>",
         BenchSummaryHtml.ConditionsLine( new BenchConditions( "performance", null, null, null, null ) ) );
      Assert.Equal( "<p class=\"bench-conditions muted\">Machine conditions (CPU governor, CPU partition, build) were not recorded in these results.</p>", BenchSummaryHtml.ConditionsLine( null ) );
      Assert.Contains( "&lt;b&gt;", BenchSummaryHtml.ConditionsLine( new BenchConditions( "<b>", null, null, null, null ) ) );
   }

   /// <summary>
   /// The published file from before the conditions existed still renders: no markers, no legend,
   /// and the conditions line says they were not recorded.
   /// </summary>
   [Fact]
   public void OldPublishedFile_StillRenders_AndSaysConditionsWereNotRecorded()
   {
      BenchSummary summary = BenchSummaryReader.FromConsolidated( BenchResultsFixtures.OLD_PUBLISHED_JSON );

      string html = BenchSummaryHtml.Block( summary, "note", null );

      Assert.Null( summary.Conditions );
      Assert.Contains( "Machine conditions (CPU governor, CPU partition, build) were not recorded in these results.", html );
      Assert.DoesNotContain( "bench-mark", html );
      Assert.DoesNotContain( "bench-legend", html );
      Assert.All( summary.Ranked, r => Assert.Null( r.Flags ) );
   }

   /// <summary>
   /// One run's results.json gives the conditions from its machine section (an old run recovers
   /// its build from the binary path in the command line) and the flags a single run can show on
   /// its own: an engine recorded as unsettled, and a p50 that disagrees with the mean from QPS
   /// at one searcher.
   /// </summary>
   [Fact]
   public void RunResults_ShowConditions_AndSingleRunFlags()
   {
      string json = """
      { "commandLine": "/x/bin/Debug/net10.0/Bench.dll run-all", "notes": [ "Warm-up: every timed pass started with its own untimed warm-up of 25 searches" ],
        "machine": { "governor": "schedutil", "governorAtEnd": "performance", "clientCpus": "0-7" },
        "targets": [
          { "name": "a", "settled": false, "search": { "qps": { "1": 500, "8": 3000 }, "p50Ms": 2.0, "recall": 1 } },
          { "name": "b", "search": { "qps": { "1": 191.6, "8": 2000 }, "p50Ms": 6.72, "recall": 1 } },
          { "name": "c", "search": { "qps": { "1": 500, "8": 1000 }, "p50Ms": 2.0, "recall": 1 } } ] }
      """;

      BenchSummary summary = BenchSummaryReader.FromRunResults( json );

      Assert.Equal( new BenchConditions( "schedutil, then performance", "client 0-7; engines not recorded", "Debug", "25", null ), summary.Conditions );
      Assert.True( summary.Conditions!.GovernorIsWrong );
      Assert.True( summary.Conditions.BuildIsWrong );
      Assert.Equal( new[] { "unsettled-target" }, summary.Ranked.Single( r => r.Key == "a" ).Flags!.Select( f => f.Kind ).ToArray() );
      BenchFlag mismatch = Assert.Single( summary.Ranked.Single( r => r.Key == "b" ).Flags! );
      Assert.Equal( "p50-mean-inconsistent", mismatch.Kind );
      Assert.Equal( "p50 6.72 ms, mean 5.22 ms, p50/mean 1.29", mismatch.Detail );
      Assert.Empty( summary.Ranked.Single( r => r.Key == "c" ).Flags! );
   }

   /// <summary>
   /// The conditions the machine-control step writes (a top-level "conditions" object) are read
   /// for the conditions line; a value written as "unknown" counts as not recorded; the
   /// measurement step's "NOT settled" warning in a target's notes flags that target.
   /// </summary>
   [Fact]
   public void RunResults_ReadMachineControlConditions_AndSettleNotes()
   {
      string json = """
      { "machine": { "host": "linus7795", "logicalCpus": 8 },
        "conditions": { "buildConfiguration": "Release", "machineControl": "on", "governor": "performance", "governorAtEnd": "performance",
                        "clientCpus": "0,4", "engineCpus": "1-3,5-7", "warmupSearches": 20, "exactSeconds": 60, "search": { "seed": 11 } },
        "targets": [
          { "name": "a", "notes": [ "WARNING: latency had NOT settled when timing began (the 90 s cap ran out) after 90.0 s." ], "search": { "qps": { "1": 500, "8": 3000 }, "p50Ms": 2.0, "recall": 1 } },
          { "name": "b", "notes": [ "Settle: settled after 3.0 s and 900 searches" ], "search": { "qps": { "1": 500, "8": 2000 }, "p50Ms": 2.0, "recall": 1 } } ] }
      """;

      BenchSummary summary = BenchSummaryReader.FromRunResults( json );

      Assert.Equal( new BenchConditions( "performance", PARTITION, "Release", "20", "60" ), summary.Conditions );
      BenchFlag flag = Assert.Single( summary.Ranked.Single( r => r.Key == "a" ).Flags! );
      Assert.Equal( "unsettled-target", flag.Kind );
      Assert.Equal( "latency had NOT settled when timing began (the 90 s cap ran out) after 90.0 s.", flag.Detail );
      Assert.Empty( summary.Ranked.Single( r => r.Key == "b" ).Flags! );

      string unknown = """{ "conditions": { "buildConfiguration": "unknown", "governor": "unknown" }, "targets": [ { "name": "a", "search": { "qps": { "8": 1 } } } ] }""";
      Assert.True( BenchSummaryReader.FromRunResults( unknown ).Conditions!.NoneRecorded );
   }

   /// <summary>
   /// The summary page and a run page carry the markers, the legend and the conditions line end
   /// to end: a published consolidate output on /bench-results.
   /// </summary>
   [Fact]
   public void ListPage_ShowsMarkersLegendAndConditions_FromAConsolidateOutput()
   {
      string root = Path.Combine( AppContext.BaseDirectory, "bench-summary-tests", Guid.NewGuid().ToString( "N" ) );
      try
      {
         string folder = Path.Combine( root, OLD_PUBLISHED );
         Directory.CreateDirectory( folder );
         File.WriteAllText( Path.Combine( folder, "consolidated.json" ), ReportJson( 2, Settings( "schedutil", PARTITION, "Release" ),
            new[] { Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 ) }, new[] { ( "qdrant", "governor-not-performance", "governor schedutil", 2 ) } ) );

         string html = BenchResultsEndpoints.ListPageHtml( root );

         Assert.Contains( "CPU governor <span class=\"bench-no\">schedutil</span>; CPU partition client 0,4; engines 1-3,5-7; Release build", html );
         Assert.Contains( ">Gv</abbr>", html );
         Assert.Contains( "CPU governor was not performance", html );
         Assert.True( html.IndexOf( "bench-conditions", StringComparison.Ordinal ) < html.IndexOf( "<svg class=\"bench-chart\"", StringComparison.Ordinal ) );
         Assert.True( html.IndexOf( "bench-legend", StringComparison.Ordinal ) < html.IndexOf( "bench-caveats", StringComparison.Ordinal ) );
      }
      finally
      {
         if( Directory.Exists( root ) )
         {
            Directory.Delete( root, true );
         }
      }
   }

   /// <summary>
   /// The page and the Bench tool agree: the two thresholds are the same numbers, the spread
   /// threshold is the 15% the legend says, and every flag kind the Bench tool can write has a
   /// marker code and a meaning here, so a flag never shows as "??".
   /// </summary>
   [Fact]
   public void FlagKinds_AndThresholds_MatchTheBenchTool()
   {
      string source = File.ReadAllText( RepoFile( "src/GenericVectorBuilder.Bench/Stats/ConsolidateFlags.cs" ) );

      Assert.Equal( BenchFlagInfo.P50_ABOVE_MEAN_RATIO, Constant( source, "P50_ABOVE_MEAN_RATIO" ) );
      Assert.Equal( BenchFlagInfo.MEAN_ABOVE_P50_RATIO, Constant( source, "MEAN_ABOVE_P50_RATIO" ) );
      Assert.Equal( 1.15, Constant( source, "SPREAD_RATIO" ) );
      Assert.Contains( "15%", BenchFlagInfo.Meaning( "spread" ) );
      List<string> kinds = Regex.Matches( source, "(?:AddFlag\\(\\s*flags,\\s*[\\w.]+,\\s*|Kind = )\"([a-z0-9-]+)\"" ).Select( m => m.Groups[1].Value ).Distinct().ToList();
      Assert.True( kinds.Count >= 13, string.Join( ", ", kinds ) );
      Assert.All( kinds, k => Assert.NotEqual( "??", BenchFlagInfo.Code( k ) ) );
      Assert.All( kinds, k => Assert.DoesNotContain( "see consolidated.md)\"", BenchFlagInfo.Meaning( k ) + "\"" ) );
      Assert.Equal( "??", BenchFlagInfo.Code( "some-future-flag" ) );
   }

   /// <summary>
   /// Engines whose min to max ranges overlap share a band, chained through the overlaps, numbered
   /// from the fastest; the rows stay in median order; the numbers are the ones the Bench tool's
   /// ConsolidateTests expect for the same cases.
   /// </summary>
   [Fact]
   public void Bands_FollowTheSameCasesAsTheBenchTool()
   {
      BenchSummary summary = BenchSummaryReader.FromConsolidated( PublishedShape( BAND_CASE ) );
      Assert.Equal( new[] { "b:1", "a:1", "d:2", "c:2", "e:3" }, summary.Ranked.Select( r => $"{r.Key}:{r.Band}" ).ToArray() );

      BenchSummary chain = BenchSummaryReader.FromConsolidated( PublishedShape( CHAIN_CASE ) );
      Assert.Equal( new[] { "z:1", "y:1", "x:1", "v:2", "u:2", "w:3" }, chain.Ranked.Select( r => $"{r.Key}:{r.Band}" ).ToArray() );
   }

   /// <summary>
   /// Neighbours whose medians are less than 3% apart share a band even when their ranges do not
   /// overlap, and a chain of such neighbours is one band; 2.9% apart share, 3.1% apart do not;
   /// these are the cases the Bench tool's ConsolidateTests expect, and each engine here varies by
   /// only 0.1% so that no two ranges touch.
   /// </summary>
   [Fact]
   public void Bands_JoinNeighboursWithinThreePercent_LikeTheBenchTool()
   {
      (string Name, double Median, double Min, double Max)[] Tight( params (string Name, double Median)[] cases )
      {
         return cases.Select( c => ( c.Name, c.Median, c.Median * 0.999, c.Median * 1.001 ) ).ToArray();
      }

      BenchSummary pairs = BenchSummaryReader.FromConsolidated( PublishedShape( Tight( ( "a", 1000 ), ( "b", 1020 ), ( "c", 1100 ), ( "d", 1130 ) ) ) );
      Assert.Equal( new[] { "d:1", "c:1", "b:2", "a:2" }, pairs.Ranked.Select( r => $"{r.Key}:{r.Band}" ).ToArray() );
      Assert.True( pairs.Ranked[2].Qps8Min > pairs.Ranked[3].Qps8Max, "b and a must not overlap, or the case proves nothing" );

      BenchSummary limit = BenchSummaryReader.FromConsolidated( PublishedShape( Tight( ( "s", 3093 ), ( "r", 3000 ), ( "q", 2058 ), ( "p", 2000 ), ( "w", 1090 ), ( "z", 1050 ), ( "y", 1025 ), ( "x", 1000 ) ) ) );
      Assert.Equal( new[] { "s:1", "r:2", "q:3", "p:3", "w:4", "z:5", "y:5", "x:5" }, limit.Ranked.Select( r => $"{r.Key}:{r.Band}" ).ToArray() );
      Assert.Equal( new[] { "s", "r", "q", "p", "w", "z", "y", "x" }, limit.Ranked.Select( r => r.Key ).ToArray() );
   }

   /// <summary>
   /// Engines with no spread (no min and max) are joined by the same 3% rule on their medians: 100
   /// and 102 share a band, 100 and 104 do not.
   /// </summary>
   [Fact]
   public void Bands_ApplyTheThreePercentRuleToPointRows_Too()
   {
      var rows = new List<BenchEngineRow>
      {
         new( "a", "a", 104, null, null, null, null, false ), new( "b", "b", 102, null, null, null, null, false ), new( "c", "c", 100, null, null, null, null, false ),
      };

      Assert.Equal( new[] { 1, 1, 1 }, BenchBands.Assign( rows ).Select( r => r.Band!.Value ).ToArray() );
      Assert.Equal( new[] { 1, 2 }, BenchBands.Assign( new List<BenchEngineRow> { rows[0], rows[2] } ).Select( r => r.Band!.Value ).ToArray() );
   }

   /// <summary>
   /// Two engines in different bands never overlap, over the real published file: for every pair
   /// in different bands the slower engine's fastest run is slower than the faster engine's slowest
   /// run; band numbers start at 1 and run without gaps in table order.
   /// </summary>
   [Fact]
   public void Bands_OfThePublishedFile_NeverOverlapAcrossBands()
   {
      BenchSummary summary = BenchSummaryReader.FromConsolidated( BenchResultsFixtures.OLD_PUBLISHED_JSON );

      List<int> bands = summary.Ranked.Select( r => r.Band!.Value ).ToList();
      Assert.Equal( 1, bands[0] );
      Assert.Equal( bands.Order().ToList(), bands );
      Assert.All( bands.Zip( bands.Skip( 1 ) ), p => Assert.InRange( p.Second - p.First, 0, 1 ) );
      for( int i = 0; i < summary.Ranked.Count; i++ )
      {
         for( int j = i + 1; j < summary.Ranked.Count; j++ )
         {
            if( summary.Ranked[i].Band != summary.Ranked[j].Band )
            {
               Assert.True( summary.Ranked[j].Qps8Max < summary.Ranked[i].Qps8Min, $"{summary.Ranked[i].Key} and {summary.Ranked[j].Key} overlap but are in different bands" );
            }
         }
      }

      Assert.True( bands.Max() < summary.Ranked.Count, "the published runs overlap, so some engines must share a band" );
   }

   /// <summary>
   /// Rows with no min and max count as points: equal values share a band, different values do not.
   /// </summary>
   [Fact]
   public void Bands_TreatMissingRangesAsPoints()
   {
      var rows = new List<BenchEngineRow>
      {
         new( "a", "a", 100, null, null, null, null, false ), new( "b", "b", 100, null, null, null, null, false ), new( "c", "c", 90, null, null, null, null, false ),
      };

      Assert.Equal( new[] { 1, 1, 2 }, BenchBands.Assign( rows ).Select( r => r.Band!.Value ).ToArray() );
   }

   /// <summary>
   /// The table's first column is the band when the numbers are medians of several runs (engines
   /// that share a band show the same number) and the order in this run when there is one run; a
   /// line under the table says what that column means.
   /// </summary>
   [Fact]
   public void Table_ShowsBands_NotRanks_AndSaysWhatTheyMean()
   {
      BenchSummary summary = BenchSummaryReader.FromConsolidated( PublishedShape( BAND_CASE ) );

      string table = BenchSummaryHtml.Table( summary );
      string note = BenchSummaryHtml.BandsNote( summary );

      Assert.Contains( "<th class=\"n\">Band</th><th>Engine</th>", table );
      Assert.DoesNotContain( ">Rank<", table );
      Assert.Equal( new[] { "1", "1", "2", "2", "3" }, Regex.Matches( table, "<tr><td class=\"n\">(\\d+)</td>" ).Select( m => m.Groups[1].Value ).ToArray() );
      Assert.Equal( "<p class=\"bench-bands muted\">Engines in different bands never overlap: every run of an engine in a faster band beat every run of an engine in a slower band, and the medians on either side of a band boundary are at least 3% apart. Engines in one band are linked by overlapping slowest-to-fastest ranges or by neighboring medians less than 3% apart (an engine varies about 2% from run to run), so these runs do not separate them cleanly. Inside a band they are listed by median, and that order is not a ranking.</p>", note );

      BenchSummary one = BenchSummaryReader.FromRunResults( """{ "targets": [ { "name": "sql", "search": { "qps": { "8": 50 } } }, { "name": "mariadb", "search": { "qps": { "8": 99 } } } ] }""" );
      string oneTable = BenchSummaryHtml.Table( one );
      Assert.Contains( "<th class=\"n\">Order</th>", oneTable );
      Assert.Equal( new[] { "1", "2" }, Regex.Matches( oneTable, "<tr><td class=\"n\">(\\d+)</td>" ).Select( m => m.Groups[1].Value ).ToArray() );
      Assert.All( one.Ranked, r => Assert.Null( r.Band ) );
      Assert.Contains( "One run: no spread is known, so no band can be drawn. The order shows this run only.", BenchSummaryHtml.BandsNote( one ) );
      Assert.DoesNotContain( "band", BenchChart.Svg( one ) );
      Assert.Equal( string.Empty, BenchSummaryHtml.BandsNote( new BenchSummary( Array.Empty<BenchEngineRow>(), Array.Empty<string>(), 1 ) ) );
   }

   /// <summary>
   /// When several engines share the fastest band the headline names all of them, gives each
   /// median and says their runs overlap, instead of naming one winner.
   /// </summary>
   [Fact]
   public void Headline_NamesEveryEngineOfTheFastestBand()
   {
      Assert.Equal( "b and a were fastest together, too close to tell apart in these runs: 1,150 and 1,050 searches per second with 8 at once (median of 3 runs).",
         BenchSummaryHtml.Headline( BenchSummaryReader.FromConsolidated( PublishedShape( BAND_CASE ) ) ) );
      Assert.Equal( "z, y and x were fastest together, too close to tell apart in these runs: 350, 250 and 150 searches per second with 8 at once (median of 3 runs).",
         BenchSummaryHtml.Headline( BenchSummaryReader.FromConsolidated( PublishedShape( CHAIN_CASE ) ) ) );
      string json = """{ "m": { "runs": 3, "qps8": { "median": 1000, "min": 900, "max": 1100 }, "recall": { "median": 0.9 } }, "n": { "runs": 3, "qps8": { "median": 990, "min": 950, "max": 1010 }, "recall": { "median": 1 } } }""";
      Assert.Equal( "m and n were fastest together, too close to tell apart in these runs: 1,000 and 990 searches per second with 8 at once (median of 3 runs), m returned 90% of the exact top 10.",
         BenchSummaryHtml.Headline( BenchSummaryReader.FromConsolidated( json ) ) );
   }

   /// <summary>
   /// The block opens with the title "Request speed on a small collection (524 vectors)" and the
   /// one line that at this size it measures per-request cost, before the headline: from the
   /// consolidate output's settings, from one run's results, and, for the old published file that
   /// records no row count, from the data line printed next to it.
   /// </summary>
   [Fact]
   public void Block_OpensWithTheSmallCollectionFraming_FromEveryShape()
   {
      string report = ReportJson( 2, Settings( "performance", PARTITION, "Release", 524 ), new[] { Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 ) }, Array.Empty<( string, string, string, int )>() );
      string fromReport = BenchSummaryHtml.Block( BenchSummaryReader.FromConsolidated( report ), "note", null );
      string fromRun = BenchSummaryHtml.Block( BenchSummaryReader.FromRunResults( """{ "rows": 524, "targets": [ { "name": "sql", "search": { "qps": { "8": 50 } } } ] }""" ), "note", null );
      string published = BenchSummaryHtml.Block( BenchSummaryReader.FromConsolidated( BenchResultsFixtures.OLD_PUBLISHED_JSON ), "note", BenchSummaryHtml.PUBLISHED_DATA );

      foreach( string html in new[] { fromReport, fromRun, published } )
      {
         Assert.Contains( SMALL_TITLE, html );
         Assert.True( html.IndexOf( SMALL_TITLE, StringComparison.Ordinal ) < html.IndexOf( "bench-lead", StringComparison.Ordinal ) );
      }

      Assert.Equal( 524, BenchSummaryReader.FromConsolidated( report ).Rows );
      Assert.Null( BenchSummaryReader.FromConsolidated( BenchResultsFixtures.OLD_PUBLISHED_JSON ).Rows );
   }

   /// <summary>
   /// The framing claims "small collection" only for a small one: above the limit it names the
   /// size and does not say per-request cost; with no recorded size it says so, and an old results
   /// file still renders.
   /// </summary>
   [Fact]
   public void Framing_NamesTheSize_AndClaimsSmallOnlyWhenSmall()
   {
      Assert.Equal( "Request speed on a small collection (524 vectors)", BenchSummaryHtml.FramingTitle( 524 ) );
      Assert.Equal( "Request speed on a small collection (10,000 vectors)", BenchSummaryHtml.FramingTitle( 10000 ) );
      Assert.Equal( "Request speed at 100,000 vectors", BenchSummaryHtml.FramingTitle( 100000 ) );
      Assert.Equal( "Measured end to end through each engine's .NET client; at this size it reflects per-request cost including the client library, not index scaling.", BenchSummaryHtml.FramingLine( 524 ) );
      Assert.Equal( "Measured end to end through each engine's .NET client at 100,000 vectors; the order applies to this size only.", BenchSummaryHtml.FramingLine( 100000 ) );
      Assert.DoesNotContain( "per-request", BenchSummaryHtml.FramingLine( 100000 ) );
      Assert.Equal( "Request speed (collection size not recorded)", BenchSummaryHtml.FramingTitle( null ) );
      Assert.Equal( "Request speed (collection size not recorded)", BenchSummaryHtml.FramingTitle( 0 ) );
      Assert.Equal( "The collection size was not recorded in these results. Measured end to end through each engine's .NET client.", BenchSummaryHtml.FramingLine( null ) );
      string old = BenchSummaryHtml.Block( BenchSummaryReader.FromConsolidated( SYNTHETIC ), "note", null );
      Assert.Contains( "<h2 class=\"bench-title\">Request speed (collection size not recorded)</h2>", old );
      string big = BenchSummaryHtml.Block( BenchSummaryReader.FromRunResults( """{ "rows": 50000, "targets": [ { "name": "sql", "search": { "qps": { "8": 50 } } } ] }""" ), "note", null );
      Assert.Contains( "<h2 class=\"bench-title\">Request speed at 50,000 vectors</h2>", big );
      Assert.DoesNotContain( "small collection", big );
   }

   /// <summary>
   /// The page and the Bench tool say the same words: every framing sentence, the band sentence
   /// and the size limit are the same text and number in the Bench tool's ConsolidateFraming.cs.
   /// </summary>
   [Fact]
   public void FramingText_MatchesTheBenchTool()
   {
      string source = File.ReadAllText( RepoFile( FRAMING_SOURCE ) );

      Assert.Equal( BenchSummaryHtml.TITLE_SMALL, StringConstant( source, "TITLE_SMALL" ) );
      Assert.Equal( BenchSummaryHtml.TITLE_LARGE, StringConstant( source, "TITLE_LARGE" ) );
      Assert.Equal( BenchSummaryHtml.TITLE_UNKNOWN, StringConstant( source, "TITLE_UNKNOWN" ) );
      Assert.Equal( BenchSummaryHtml.LINE_SMALL, StringConstant( source, "LINE_SMALL" ) );
      Assert.Equal( BenchSummaryHtml.LINE_LARGE, StringConstant( source, "LINE_LARGE" ) );
      Assert.Equal( BenchSummaryHtml.LINE_UNKNOWN, StringConstant( source, "LINE_UNKNOWN" ) );
      Assert.Equal( BenchSummaryHtml.BANDS_LINE, StringConstant( source, "BANDS_LINE" ) );
      Assert.Equal( BenchSummaryHtml.ONE_RUN_LINE, StringConstant( source, "ONE_RUN_LINE" ) );
      Assert.Equal( BenchSummaryHtml.CLIENT_CPU_LINE, StringConstant( source, "CLIENT_CPU_LINE" ) );
      Assert.Equal( BenchBands.MIN_MEDIAN_GAP, Constant( File.ReadAllText( RepoFile( BANDS_SOURCE ) ), "MIN_MEDIAN_GAP" ) );
      Assert.Equal( 0.03, BenchBands.MIN_MEDIAN_GAP );
      Assert.Equal( BenchSummaryHtml.SMALL_COLLECTION_MAX_ROWS, (int)Constant( source, "SMALL_COLLECTION_MAX_ROWS" ) );
   }

   /// <summary>
   /// The client's CPU per search is a table column (with a line saying what it is) only when the
   /// results carry it: from one run's search section (a number per level), or from a consolidate
   /// output (a median per level, of which the median is shown); an engine without it shows "-";
   /// results with none show no column and no line, and the "no result" cell spans the right number
   /// of columns either way.
   /// </summary>
   [Fact]
   public void ClientCpu_IsAColumnOnlyWhenTheResultsCarryIt()
   {
      string run = """{ "targets": [ { "name": "qdrant", "search": { "qps": { "1": 500, "8": 900 }, "p50Ms": 0.9, "clientCpuMsPerSearch": { "1": 0.4912, "8": 0.3 } } }, { "name": "sql", "search": { "qps": { "8": 50 }, "p50Ms": 6.8 } }, { "name": "oracle" } ] }""";
      BenchSummary one = BenchSummaryReader.FromRunResults( run );
      string table = BenchSummaryHtml.Table( one );

      Assert.Contains( "<th class=\"n\">Single search p50 ms</th><th class=\"n\">Client CPU per search ms</th><th>Same answers", table );
      Assert.Contains( "<td class=\"n\">0.90</td><td class=\"n\">0.49</td>", table );
      Assert.Contains( "<td class=\"n\">6.80</td><td class=\"n\">-</td>", table );
      Assert.Contains( "<td colspan=\"4\" class=\"muted\">no result</td>", table );
      Assert.Equal( 0.4912, one.Ranked.Single( r => r.Key == "qdrant" ).ClientCpuMs );
      Assert.Equal( "<p class=\"bench-client-cpu muted\">Client CPU per search is the CPU time the test&#39;s .NET client itself used for each search, measured in the same pass as the figure beside it. Where it is close to the latency, the client library is a large part of what is measured. For an embedded engine (DuckDB, sqlite-vec) the engine runs inside the client process, so its figure is the engine&#39;s own CPU time, not client overhead.</p>", BenchSummaryHtml.ClientCpuNote( one ) );
      Assert.Contains( BenchSummaryHtml.ClientCpuNote( one ), BenchSummaryHtml.Block( one, "note", null ) );

      JsonObject target = Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 );
      target["clientCpuMsPerSearch"] = new JsonObject { ["1"] = Spread( 0.6, 0.55, 0.65 ), ["8"] = Spread( 0.3, 0.25, 0.35 ) };
      BenchSummary report = BenchSummaryReader.FromConsolidated( ReportJson( 2, Settings( "performance", PARTITION, "Release", 524 ), new[] { target, Summary( "sql", 900, 850, 950, 4.5, 1.0 ) }, Array.Empty<( string, string, string, int )>() ) );
      Assert.Equal( 0.6, report.Ranked.Single( r => r.Key == "qdrant" ).ClientCpuMs );
      Assert.Null( report.Ranked.Single( r => r.Key == "sql" ).ClientCpuMs );
      Assert.Contains( "Client CPU per search ms", BenchSummaryHtml.Table( report ) );

      BenchSummary none = BenchSummaryReader.FromRunResults( """{ "targets": [ { "name": "sql", "search": { "qps": { "8": 50 }, "p50Ms": 6.8 } }, { "name": "oracle" } ] }""" );
      string plain = BenchSummaryHtml.Table( none );
      Assert.DoesNotContain( "Client CPU", plain );
      Assert.Contains( "<td class=\"n\">6.8</td>", plain );
      Assert.Contains( "<td colspan=\"3\" class=\"muted\">no result</td>", plain );
      Assert.Equal( string.Empty, BenchSummaryHtml.ClientCpuNote( none ) );
      Assert.DoesNotContain( "bench-client-cpu", BenchSummaryHtml.Block( BenchSummaryReader.FromConsolidated( BenchResultsFixtures.OLD_PUBLISHED_JSON ), "note", null ) );
   }

   /// <summary>
   /// A damaged client CPU figure (negative, or not a number) reads as not recorded, and the level
   /// may be spelled "default@1".
   /// </summary>
   [Fact]
   public void ClientCpu_IgnoresDamagedValues_AndReadsDefaultSpelling()
   {
      BenchSummary summary = BenchSummaryReader.FromRunResults( """{ "targets": [ { "name": "a", "search": { "qps": { "8": 50 }, "clientCpuMsPerSearch": { "1": -2 } } }, { "name": "b", "search": { "qps": { "8": 40 }, "clientCpuMsPerSearch": { "1": "fast" } } }, { "name": "c", "search": { "qps": { "8": 30 }, "clientCpuMsPerSearch": { "default@1": 0.7 } } } ] }""" );

      Assert.Equal( new double?[] { null, null, 0.7 }, summary.Ranked.Select( r => r.ClientCpuMs ).ToArray() );
   }

   /// <summary>
   /// The native comparison targets say so in their names ("SQL Server 2025 (native service)",
   /// "Qdrant (native service, exact)"), the bench MariaDB target keeps the name MariaDB whether it
   /// is called "mariadb" or "mariadb-bench" (the container is where it runs, not what is compared),
   /// and a name that is neither known nor a known engine in a container is shown as written.
   /// </summary>
   [Fact]
   public void FriendlyNames_SayNativeServices_AndKeepMariaDb()
   {
      Assert.Equal( "SQL Server 2025 (native service)", BenchSummaryReader.FriendlyName( "sql-native" ) );
      Assert.Equal( "Qdrant (native service, exact)", BenchSummaryReader.FriendlyName( "qdrant-native" ) );
      Assert.Equal( "SQL Server 2025", BenchSummaryReader.FriendlyName( "sql" ) );
      Assert.Equal( "Qdrant (exact)", BenchSummaryReader.FriendlyName( "qdrant" ) );
      Assert.Equal( "MariaDB", BenchSummaryReader.FriendlyName( "mariadb" ) );
      Assert.Equal( "MariaDB", BenchSummaryReader.FriendlyName( "mariadb-bench" ) );
      Assert.Equal( "MariaDB", BenchSummaryReader.FriendlyName( "MariaDB-Container" ) );
      Assert.Equal( "mystery-bench", BenchSummaryReader.FriendlyName( "mystery-bench" ) );
      Assert.Equal( "-bench", BenchSummaryReader.FriendlyName( "-bench" ) );
      BenchSummary summary = BenchSummaryReader.FromRunResults( """{ "targets": [ { "name": "sql-native", "search": { "qps": { "8": 60 } } }, { "name": "mariadb", "search": { "qps": { "8": 50 } } } ] }""" );
      Assert.Equal( new[] { "SQL Server 2025 (native service)", "MariaDB" }, summary.Ranked.Select( r => r.Name ).ToArray() );
      Assert.Contains( "<summary>SQL Server 2025 (native service) <span class=\"muted\">sql-native</span></summary>", BenchMarkdown.Render( "## Details per target\n\n### sql-native\n\n- Engine: x\n" ) );
   }

   /// <summary>
   /// The notes the consolidate command wrote for an engine are shown under the table with their
   /// source (from the results, or a documented limit the run did not record) and are HTML-escaped;
   /// an older file with no notes shows no notes block.
   /// </summary>
   [Fact]
   public void EngineNotes_AreShownWithTheirSource_AndEscaped()
   {
      JsonObject oracle = Summary( "oracle", 3000, 2900, 3100, 1.5, 1.0 );
      oracle["engineNotes"] = new JsonArray( new JsonObject { ["kind"] = "cpu-cap", ["source"] = "known", ["text"] = "Oracle Free caps itself at 2 CPUs <b>x</b>." } );
      JsonObject sql = Summary( "sql", 900, 850, 950, 4.5, 1.0 );
      sql["engineNotes"] = new JsonArray( new JsonObject { ["kind"] = "exact-by-design", ["source"] = "results", ["text"] = "Exact by design: scans every vector." }, new JsonObject { ["kind"] = "future-kind", ["text"] = "Something new." } );
      string json = ReportJson( 2, Settings( "performance", PARTITION, "Release" ), new[] { oracle, sql, Summary( "qdrant", 2000, 1900, 2100, 1.5, 1.0 ) }, Array.Empty<( string, string, string, int )>() );

      BenchSummary summary = BenchSummaryReader.FromConsolidated( json );
      string html = BenchSummaryHtml.EngineNotes( summary );

      Assert.Equal( new[] { "cpu-cap" }, summary.Ranked.Single( r => r.Key == "oracle" ).Notes!.Select( n => n.Kind ).ToArray() );
      Assert.Null( summary.Ranked.Single( r => r.Key == "qdrant" ).Notes );
      Assert.DoesNotContain( "<b>", html );
      Assert.Contains( "<li><strong>Oracle 23ai Free</strong> Oracle Free caps itself at 2 CPUs &lt;b&gt;x&lt;/b&gt;. <span class=\"muted\">(known limit, not recorded in these results)</span></li>", html );
      Assert.Contains( "<li><strong>SQL Server 2025</strong> Exact by design: scans every vector. <span class=\"muted\">(from the results)</span></li>", html );
      Assert.Contains( "<li><strong>SQL Server 2025</strong> Something new. <span class=\"muted\">(from the results)</span></li>", html );
      Assert.DoesNotContain( "Qdrant (exact)", html );
      Assert.Equal( string.Empty, BenchSummaryHtml.EngineNotes( BenchSummaryReader.FromConsolidated( SYNTHETIC ) ) );
      Assert.DoesNotContain( "bench-engine-notes", BenchSummaryHtml.Block( BenchSummaryReader.FromConsolidated( BenchResultsFixtures.OLD_PUBLISHED_JSON ), "note", null ) );
   }

   /// <summary>
   /// The whole page for a consolidate output: title, then headline, chart with band labels, the
   /// table headed Band, the band sentence, the engine notes, and the legend, in that order; the
   /// segment-layout flag has a marker and a meaning.
   /// </summary>
   [Fact]
   public void ListPage_ShowsFramingBandsAndNotes_InOrder()
   {
      string root = Path.Combine( AppContext.BaseDirectory, "bench-summary-tests", Guid.NewGuid().ToString( "N" ) );
      try
      {
         string folder = Path.Combine( root, OLD_PUBLISHED );
         Directory.CreateDirectory( folder );
         JsonObject qdrant = Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 );
         qdrant["engineNotes"] = new JsonArray( new JsonObject { ["kind"] = "exact-by-design", ["source"] = "results", ["text"] = "Exact by design." } );
         File.WriteAllText( Path.Combine( folder, "consolidated.json" ), ReportJson( 2, Settings( "performance", PARTITION, "Release", 524 ),
            new[] { qdrant, Summary( "sql", 900, 850, 950, 4.5, 1.0 ) }, new[] { ( "sql", "segment-layout-changed", "1 segment after the load, 2 segments after the searches", 2 ) } ) );

         string html = BenchResultsEndpoints.ListPageHtml( root );

         int title = html.IndexOf( SMALL_TITLE, StringComparison.Ordinal );
         int chart = html.IndexOf( "<svg class=\"bench-chart\"", StringComparison.Ordinal );
         int table = html.IndexOf( "<th class=\"n\">Band</th>", StringComparison.Ordinal );
         int bands = html.IndexOf( "bench-bands", StringComparison.Ordinal );
         int notes = html.IndexOf( "bench-engine-notes", StringComparison.Ordinal );
         int legend = html.IndexOf( "bench-legend", StringComparison.Ordinal );
         Assert.True( title >= 0 && chart > title && table > chart && bands > table && notes > bands && legend > notes, html );
         Assert.Contains( "band 1</tspan>", html );
         Assert.Contains( ">Sg</abbr>", html );
         Assert.Contains( "different segment layout after the searches", html );
      }
      finally
      {
         if( Directory.Exists( root ) )
         {
            Directory.Delete( root, true );
         }
      }
   }

   /// <summary>
   /// Malformed input fails loud instead of drawing an empty chart.
   /// </summary>
   [Theory]
   [InlineData( "[1,2]" )]
   [InlineData( "not json" )]
   public void Reader_ThrowsOnMalformed( string json )
   {
      Assert.ThrowsAny<JsonException>( () => BenchSummaryReader.FromConsolidated( json ) );
      Assert.ThrowsAny<JsonException>( () => BenchSummaryReader.FromRunResults( json ) );
   }

   /// <summary>
   /// An engine the consolidated report names as left out is shown under the table, by its friendly
   /// name, with the report's reason (escaped); a file with no such list shows nothing.
   /// </summary>
   [Fact]
   public void Withheld_EnginesAreShownBelowTheTable_WithTheReasonTheReportGives()
   {
      JsonNode root = JsonNode.Parse( ReportJson( 2, Settings( "performance", PARTITION, "Release", 524 ), new[] { Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 ) }, Array.Empty<( string, string, string, int )>() ) )!;
      root["withheld"] = new JsonArray( new JsonObject { ["target"] = "mongodb", ["kind"] = "not-listed", ["runs"] = new JsonArray( "run1", "run2" ), ["reason"] = "it is in 2 of 2 runs but not in --targets <b>x</b>" } );

      BenchSummary summary = BenchSummaryReader.FromConsolidated( root.ToJsonString() );
      string html = BenchSummaryHtml.Block( summary, "note", null );

      BenchWithheld engine = Assert.Single( summary.Withheld! );
      Assert.Equal( "MongoDB Atlas Local", engine.Name );
      Assert.Contains( "<p class=\"muted\">Measured, but not in the table above:</p><ul><li><strong>MongoDB Atlas Local</strong> it is in 2 of 2 runs but not in --targets &lt;b&gt;x&lt;/b&gt;</li></ul>", html );
      Assert.True( html.IndexOf( "bench-table", StringComparison.Ordinal ) < html.IndexOf( "bench-withheld", StringComparison.Ordinal ) );

      root["withheld"] = new JsonArray();
      Assert.Null( BenchSummaryReader.FromConsolidated( root.ToJsonString() ).Withheld );
      Assert.DoesNotContain( "bench-withheld", BenchSummaryHtml.Block( BenchSummaryReader.FromConsolidated( root.ToJsonString() ), "note", null ) );
      Assert.Null( BenchSummaryReader.FromConsolidated( ReportJson( 2, Settings( "performance", PARTITION, "Release", 524 ), new[] { Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 ) }, Array.Empty<( string, string, string, int )>() ) ).Withheld );
   }

   /// <summary>
   /// A segment layout that differs between runs shows as its own marker with a plain meaning, next
   /// to the engine that carries it.
   /// </summary>
   [Fact]
   public void LayoutDifference_HasItsOwnMarkerAndMeaning()
   {
      string json = ReportJson( 3, Settings( "performance", PARTITION, "Release", 524 ), new[] { Summary( "mongodb", 2000, 1900, 2100, 3.6, 1.0 ) },
         new[] { ( "mongodb", "segment-layout-differs-between-runs", "after the load: 4 segments (r1, r2); 2 segments (r3)", 3 ) } );

      BenchSummary summary = BenchSummaryReader.FromConsolidated( json );
      string html = BenchSummaryHtml.Block( summary, "note", null );

      Assert.Equal( "Sl", BenchFlagInfo.Code( "segment-layout-differs-between-runs" ) );
      Assert.Contains( ">Sl</abbr>", html );
      Assert.Contains( "different segment layout in different runs", BenchFlagInfo.Meaning( "segment-layout-differs-between-runs" ) );
      Assert.Contains( "after the load: 4 segments (r1, r2); 2 segments (r3) [3 of 3 runs]", html );
   }

   /// <summary>
   /// The data line is built from the file's own settings, so a newer folder is never described in
   /// another folder's words; the old published shape (no settings) keeps its fixed line.
   /// </summary>
   [Fact]
   public void DataLine_IsBuiltFromTheSettings_AndTheOldShapeKeepsItsFixedLine()
   {
      var settings = (JsonObject)Settings( "performance", PARTITION, "Release", 524 );
      settings["pipeline"] = "eshoponweb";
      settings["dimension"] = 1024;
      settings["queryKind"] = "golden";
      settings["queryCount"] = 20;
      settings["top"] = 10;
      string json = ReportJson( 3, settings, new[] { Summary( "qdrant", 3000, 2900, 3100, 1.5, 1.0 ) }, Array.Empty<( string, string, string, int )>() );

      Assert.Equal( "Data: eshoponweb pipeline, 524 vectors of 1024 dimensions, 20 labelled questions, top 10.", BenchSummaryReader.DataLine( json ) );
      Assert.Equal( BenchSummaryHtml.PUBLISHED_DATA, BenchSummaryReader.DataLine( PublishedShape( new[] { ( "sql", 600.0, 590.0, 610.0 ) } ) ) );
      Assert.Null( BenchSummaryReader.DataLine( """{ "targetSummaries": [] }""" ) );
      Assert.Contains( "524 vectors", BenchSummaryReader.DataLine( json )! );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The "settings" object of a consolidate output with the three conditions the page states and
   /// the warm-up and exact-mode settings.
   /// </summary>
   /// <param name="governor">Governor.</param>
   /// <param name="partition">CPU partition text.</param>
   /// <param name="build">Build configuration.</param>
   /// <param name="rows">Vectors in the collection, or null to leave the field out (as an older consolidate output does).</param>
   /// <returns>The settings object.</returns>
   private static JsonObject Settings( string governor, string partition, string build, int? rows = null )
   {
      var settings = new JsonObject { ["governor"] = governor, ["cpuPartition"] = partition, ["buildConfiguration"] = build, ["warmupSearches"] = 20, ["exactSeconds"] = 60 };
      if( rows.HasValue )
      {
         settings["rows"] = rows.Value;
      }

      return settings;
   }

   /// <summary>
   /// One targetSummaries entry in the shape the consolidate command writes.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="qps8">Median QPS with 8 at once, or null for none.</param>
   /// <param name="min">Smallest QPS with 8.</param>
   /// <param name="max">Largest QPS with 8.</param>
   /// <param name="p50">Median p50, ms.</param>
   /// <param name="recall">Median recall.</param>
   /// <returns>The entry.</returns>
   private static JsonObject Summary( string name, double? qps8, double min, double max, double p50, double recall )
   {
      var qps = new JsonObject { ["1"] = Spread( 400, 380, 420 ) };
      if( qps8.HasValue )
      {
         qps["8"] = Spread( qps8.Value, min, max );
      }
      else
      {
         qps["8"] = new JsonObject { ["median"] = null, ["min"] = null, ["max"] = null, ["n"] = 0, ["perRun"] = new JsonArray( null, null ) };
      }

      return new JsonObject { ["name"] = name, ["qps"] = qps, ["p50Ms"] = Spread( p50, p50, p50 ), ["recall"] = Spread( recall, recall, recall ) };
   }

   /// <summary>
   /// A Spread object as the consolidate command writes it.
   /// </summary>
   /// <param name="median">Median.</param>
   /// <param name="min">Smallest.</param>
   /// <param name="max">Largest.</param>
   /// <returns>The object.</returns>
   private static JsonObject Spread( double median, double min, double max )
   {
      return new JsonObject { ["median"] = median, ["min"] = min, ["max"] = max, ["n"] = 2, ["perRun"] = new JsonArray( min, max ) };
   }

   /// <summary>
   /// A whole consolidate output with the parts the page reads.
   /// </summary>
   /// <param name="runs">Runs used.</param>
   /// <param name="settings">The settings object.</param>
   /// <param name="summaries">targetSummaries entries.</param>
   /// <param name="flags">(target, kind, detail, runs covered) per flag.</param>
   /// <returns>The JSON text.</returns>
   private static string ReportJson( int runs, JsonObject settings, JsonObject[] summaries, ( string Target, string Kind, string Detail, int Runs )[] flags )
   {
      var root = new JsonObject
      {
         ["settings"] = settings,
         ["runs"] = new JsonArray( Enumerable.Range( 1, runs ).Select( i => (JsonNode)new JsonObject { ["name"] = $"run{i}" } ).ToArray() ),
         ["targetSummaries"] = new JsonArray( summaries.Select( s => (JsonNode)s ).ToArray() ),
         ["flags"] = new JsonArray( flags.Select( f => (JsonNode)new JsonObject
         {
            ["target"] = f.Target, ["kind"] = f.Kind, ["detail"] = f.Detail, ["runs"] = new JsonArray( Enumerable.Range( 1, f.Runs ).Select( i => (JsonNode)JsonValue.Create( $"run{i}" )! ).ToArray() ),
         } ).ToArray() ),
      };
      return root.ToJsonString();
   }

   /// <summary>
   /// The published consolidated.json shape (an object keyed by engine) for cases given as
   /// median, min and max QPS at 8 over three runs.
   /// </summary>
   /// <param name="cases">Name, median, min, max.</param>
   /// <returns>The JSON text.</returns>
   private static string PublishedShape( (string Name, double Median, double Min, double Max)[] cases )
   {
      var root = new JsonObject();
      foreach( ( string name, double median, double min, double max ) in cases )
      {
         root[name] = new JsonObject { ["runs"] = 3, ["qps8"] = new JsonObject { ["median"] = median, ["min"] = min, ["max"] = max }, ["p50"] = new JsonObject { ["median"] = 2.0 }, ["recall"] = new JsonObject { ["median"] = 1 } };
      }

      return root.ToJsonString();
   }

   /// <summary>
   /// The text of a "const string NAME = "x";" in a C# source text.
   /// </summary>
   /// <param name="source">Source text.</param>
   /// <param name="name">Constant name.</param>
   /// <returns>The string.</returns>
   private static string StringConstant( string source, string name )
   {
      Match match = Regex.Match( source, "const string " + name + "\\s*=\\s*\"([^\"]*)\";" );
      Assert.True( match.Success, $"{name} not found in the Bench tool's ConsolidateFraming.cs" );
      return match.Groups[1].Value;
   }

   /// <summary>
   /// The numeric value of a "const double NAME = x;" in a C# source text.
   /// </summary>
   /// <param name="source">Source text.</param>
   /// <param name="name">Constant name.</param>
   /// <returns>The value.</returns>
   private static double Constant( string source, string name )
   {
      Match match = Regex.Match( source, name + "\\s*=\\s*([0-9.]+);" );
      Assert.True( match.Success, $"{name} not found in the Bench tool's ConsolidateFlags.cs" );
      return double.Parse( match.Groups[1].Value, CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Finds a file in the repository by walking up from the test binaries; fails when missing so
   /// the test never passes without having checked the real data.
   /// </summary>
   /// <param name="relative">Path under the repository root.</param>
   /// <returns>Full path.</returns>
   private static string RepoFile( string relative )
   {
      DirectoryInfo? dir = new( AppContext.BaseDirectory );
      for( int i = 0; i < MAX_UP && dir != null; i++, dir = dir.Parent )
      {
         string candidate = Path.Combine( dir.FullName, relative );
         if( File.Exists( candidate ) )
         {
            return candidate;
         }
      }

      Assert.Fail( $"{relative} not found above {AppContext.BaseDirectory}." );
      return string.Empty;
   }

   #endregion Private Methods
}
