using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The summary block on /bench-results and each run page: the chart has one bar per engine in
/// rank order with every name escaped, the numbers are the consolidated.json medians, and the
/// headline, table and "yes/no" column read as intended.
/// </summary>
public class BenchResultsSummaryTests
{
   #region Data Members

   private const string PUBLISHED = "bench-results/published-2026-10-04/consolidated.json";
   private const string RUN = "bench-results/20261004-132735-eshoponweb/results.json";
   private const int MAX_UP = 10;
   private const string PARTITION = "client 0,4; engines 1-3,5-7";
   private static readonly Regex ENGINE_ATTR = new( "<g class=\"bc-row\" data-engine=\"([^\"]*)\">", RegexOptions.Compiled );

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
      Assert.Contains( ">&lt;b&gt;x&amp;y&lt;/b&gt;</text>", svg );
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
      Assert.Contains( ">Redis<tspan class=\"bc-note\" dx=\"8\">in memory</tspan></text>", svg );
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
   /// Against the real published file: every engine's summary numbers equal its consolidated.json
   /// medians, the rank order is fastest median first, and the table shows each median.
   /// </summary>
   [Fact]
   public void Summary_EqualsPublishedConsolidatedMedians()
   {
      string json = File.ReadAllText( RepoFile( PUBLISHED ) );
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
   /// Against a real run: one row per target, values from search.qps["8"].
   /// </summary>
   [Fact]
   public void Summary_EqualsRealRunResults()
   {
      string json = File.ReadAllText( RepoFile( RUN ) );
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
      Assert.Equal( "<p class=\"bench-conditions muted\">Machine: CPU governor performance; CPU partition client 0,4; engines 1-3,5-7; Release build; 20 warm-up searches before each timed pass; exact mode up to 60 s.</p>",
         BenchSummaryHtml.ConditionsLine( new BenchConditions( "performance", PARTITION, "Release", "20", "60" ) ) );
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
      BenchSummary summary = BenchSummaryReader.FromConsolidated( File.ReadAllText( RepoFile( PUBLISHED ) ) );

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
         string folder = Path.Combine( root, BenchResultsEndpoints.PUBLISHED_FOLDER );
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

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The "settings" object of a consolidate output with the three conditions the page states and
   /// the warm-up and exact-mode settings.
   /// </summary>
   /// <param name="governor">Governor.</param>
   /// <param name="partition">CPU partition text.</param>
   /// <param name="build">Build configuration.</param>
   /// <returns>The settings object.</returns>
   private static JsonObject Settings( string governor, string partition, string build )
   {
      return new JsonObject { ["governor"] = governor, ["cpuPartition"] = partition, ["buildConfiguration"] = build, ["warmupSearches"] = 20, ["exactSeconds"] = 60 };
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
