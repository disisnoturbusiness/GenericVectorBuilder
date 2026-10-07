using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The table at the top of a run page: one run's figures per engine, in alphabetical order with no rank, no band and
/// no headline, with the flags the run's own records give and the conditions it was measured under.
/// </summary>
public class BenchResultsRunTableTests
{
   #region Public Methods

   /// <summary>
   /// Engines are listed alphabetically by name, an engine with no throughput figure is listed as having no result, and the table says
   /// that it does not rank.
   /// </summary>
   [Fact]
   public void Rows_AreAlphabetical_AndTheTableDoesNotRank()
   {
      BenchRunSummary run = BenchRunTable.Read( BenchResultsFixtures.RUN_RESULTS_V8_JSON );
      string html = BenchRunTable.Block( run );

      Assert.Equal( new[] { "Oracle 23ai Free", "Redis", "SQL Server 2025" }, run.Rows.Select( r => r.Name ) );
      Assert.Equal( new[] { "Weaviate" }, run.NoResult );
      Assert.Contains( BenchLegends.RUN_ORDER, html );
      Assert.DoesNotContain( ">Order<", html );
      Assert.DoesNotContain( ">Band<", html );
      Assert.DoesNotContain( "bench-lead", html );
      Assert.Contains( "<td>Weaviate</td><td colspan=\"7\" class=\"muted\">no result</td>", html );
   }

   /// <summary>
   /// The figures of a row are the run's own: latency with two decimals, searches per second whole, the exact-mode latency, and the client
   /// CPU per search in either spelling of the one-searcher level.
   /// </summary>
   [Fact]
   public void Figures_AreTheRunsOwn()
   {
      string html = BenchRunTable.Block( BenchRunTable.Read( BenchResultsFixtures.RUN_RESULTS_V8_JSON ) );
      string sql = Regex.Match( html, "<tr data-target=\"sql\">.*?</tr>" ).Value;
      string oracle = Regex.Match( html, "<tr data-target=\"oracle\">.*?</tr>" ).Value;

      Assert.Contains( "<td class=\"n\">4.06</td><td class=\"n\">241</td><td class=\"n\">612</td><td class=\"n\">4.20</td><td class=\"n\">1.22</td>", sql );
      Assert.Contains( "<td class=\"n\">0.93</td><td class=\"n\">1,051</td><td class=\"n\">2,802</td><td class=\"n\">-</td><td class=\"n\">0.88</td>", oracle );
   }

   /// <summary>
   /// Recall prints as hits out of the perfect count: the recorded count when the run has one, and the share times the perfect count when
   /// that is a whole number.
   /// </summary>
   [Fact]
   public void Recall_IsHits_RecordedOrDerivedFromAWholeNumber()
   {
      string html = BenchRunTable.Block( BenchRunTable.Read( BenchResultsFixtures.RUN_RESULTS_V8_JSON ) );

      Assert.Contains( "<td>200 of 200</td>", Regex.Match( html, "<tr data-target=\"sql\">.*?</tr>" ).Value );
      Assert.Contains( "<td>199 of 200</td>", Regex.Match( html, "<tr data-target=\"redis\">.*?</tr>" ).Value );
   }

   /// <summary>
   /// A recall share that is not a whole number of hits prints as the share, never as a rounded count.
   /// </summary>
   [Fact]
   public void Recall_ThatIsNotAWholeNumberOfHits_PrintsAsTheShare()
   {
      string json = """{ "queryCount": 20, "top": 10, "targets": [ { "name": "redis", "search": { "qps": { "8": 100 }, "recall": 0.9937 } } ] }""";

      Assert.Contains( "<td>0.994</td>", BenchRunTable.Block( BenchRunTable.Read( json ) ) );
   }

   /// <summary>
   /// The flags of a run come from its own records: a pass whose clock was off, a pass whose clock was not read, and a pass run on a busy
   /// box, each on the target it belongs to; a pass recorded as not busy gives none.
   /// </summary>
   [Fact]
   public void Flags_ComeFromThePerPassRecords()
   {
      BenchRunSummary run = BenchRunTable.Read( BenchResultsFixtures.RUN_RESULTS_V8_JSON );

      Assert.Equal( new[] { "clock-off" }, run.Rows.First( r => r.Key == "oracle" ).Flags.Select( f => f.Code ) );
      Assert.Equal( new[] { "clock-not-read" }, run.Rows.First( r => r.Key == "redis" ).Flags.Select( f => f.Code ) );
      BenchFlagEntry busy = Assert.Single( run.Rows.First( r => r.Key == "sql" ).Flags );
      Assert.Equal( "busy-box", busy.Code );
      Assert.Equal( "exact: CPUs busy outside the benchmark 0.31", busy.Text );
      Assert.Equal( "default@8: engine CPUs median 3121 MHz, client CPUs median 3141 MHz", run.Rows.First( r => r.Key == "oracle" ).Flags[0].Text );
   }

   /// <summary>
   /// The run's flags are marked in its table and each code used is listed once with its fixed legend.
   /// </summary>
   [Fact]
   public void Flags_AreMarked_AndEachCodeIsLegendedOnce()
   {
      string html = BenchRunTable.Block( BenchRunTable.Read( BenchResultsFixtures.RUN_RESULTS_V8_JSON ) );

      Assert.Contains( ">Ck</abbr>", Regex.Match( html, "<tr data-target=\"oracle\">.*?</tr>" ).Value );
      Assert.Single( Regex.Matches( html, Regex.Escape( BenchLegends.Legend( "clock-off" ) ) ) );
      Assert.Single( Regex.Matches( html, Regex.Escape( BenchLegends.Legend( "busy-box" ) ) ) );
   }

   /// <summary>
   /// A target with an unsettled note and a p50 that disagrees with the mean implied by the one-searcher throughput is flagged.
   /// </summary>
   [Fact]
   public void Flags_IncludeUnsettledAndP50AgainstMean()
   {
      string json = """{ "targets": [ { "name": "a", "notes": [ "WARNING: latency had NOT settled in 3 trials" ], "search": { "qps": { "1": 100, "8": 500 }, "p50Ms": 20.0, "recall": 1 } } ] }""";

      BenchRunSummary run = BenchRunTable.Read( json );

      Assert.Equal( new[] { "unsettled-target", "p50-mean-inconsistent" }, run.Rows[0].Flags.Select( f => f.Code ) );
      Assert.Equal( "latency had NOT settled in 3 trials", run.Rows[0].Flags[0].Text );
   }

   /// <summary>
   /// The conditions line is made of the run's own fields: machine, governor, partition, build, exact seconds, then the warm-up line and the clock
   /// block when the run recorded one; a governor other than performance and a Debug build are marked.
   /// </summary>
   [Fact]
   public void Conditions_ArePrintedFromTheRunsFields()
   {
      string html = BenchRunTable.Block( BenchRunTable.Read( BenchResultsFixtures.RUN_RESULTS_V8_JSON ) );

      Assert.Contains( "Machine: CPU Intel Xeon E5-1620 v3; Logical CPUs 8; RAM (GiB) 62.7; OS Ubuntu 24.04.5 LTS; Governor performance; Partition client 0-1,4-5; engines 2-3,6-7; Release build; Exact mode seconds 60.", html );
      Assert.DoesNotContain( "Warm-up searches", html );
      Assert.Contains( "Warm-up: the run recorded the field warmupSearches with the value 20 and no note that describes the method.", html );
      Assert.Contains( "Clock: pinned true; pinnedMhz 3500; noTurbo before=1, during=1; uncoreMsr620 before=0xc1e, during=0x1e1e.", html );

      string wrong = BenchRunTable.Block( BenchRunTable.Read( """{ "conditions": { "governor": "powersave", "buildConfiguration": "Debug" }, "targets": [] }""" ) );
      Assert.Contains( "<span class=\"bench-no\">powersave</span>", wrong );
      Assert.Contains( "<span class=\"bench-no\">Debug build</span>", wrong );
      Assert.Contains( "Machine: Governor", BenchRunTable.Block( BenchRunTable.Read( """{ "targets": [] }""" ) ) );
      Assert.Contains( "Build not recorded", BenchRunTable.Block( BenchRunTable.Read( """{ "targets": [] }""" ) ) );
   }

   /// <summary>
   /// Names are printed as text.
   /// </summary>
   [Fact]
   public void Names_AreEncoded()
   {
      string json = """{ "targets": [ { "name": "<b>x&y</b>", "search": { "qps": { "8": 100 } } } ] }""";

      string html = BenchRunTable.Block( BenchRunTable.Read( json ) );

      Assert.DoesNotContain( "<b>x", html );
      Assert.Contains( "&lt;b&gt;x&amp;y&lt;/b&gt;", html );
   }

   /// <summary>
   /// A file with no targets list, a file with too many targets and one that is not JSON are refused.
   /// </summary>
   [Fact]
   public void MalformedRuns_AreRefused()
   {
      Assert.Throws<InvalidDataException>( () => BenchRunTable.Read( "{}" ) );
      Assert.Throws<InvalidDataException>( () => BenchRunTable.Read( "[]" ) );
      Assert.ThrowsAny<JsonException>( () => BenchRunTable.Read( "{ nope" ) );
      string many = "{ \"targets\": [" + string.Join( ",", Enumerable.Range( 0, BenchRunTable.MAX_ENGINES + 1 ).Select( i => $"{{ \"name\": \"t{i}\", \"search\": {{ \"qps\": {{ \"8\": 1 }} }} }}" ) ) + "] }";
      Assert.Throws<InvalidDataException>( () => BenchRunTable.Read( many ) );
   }

   #endregion Public Methods
}
