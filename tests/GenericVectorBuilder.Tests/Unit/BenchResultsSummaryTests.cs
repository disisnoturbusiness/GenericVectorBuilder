using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;

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
