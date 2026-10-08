using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The v8h web fixes against the consolidate command's own output of 2026-10-08 (the fixture v8h.consolidated.json: the real v7 and v8 runs and the v5 and v6
/// basis runs, consolidated by the integrated build). Why a second class beside <see cref="BenchResultsV8hWebTests"/>: that class proves each fix on a file
/// built from the contract, one change at a time; this one proves the page and the command read the same file, so a field the command writes and the page
/// does not read, or a sentence it writes and the page does not place, fails here.
/// </summary>
public class BenchResultsV8hFinalTests : IDisposable
{
   #region Data Members

   private const string JSON = "v8h.consolidated.json";
   private const string PUBLISHED = "published-2026-10-08";
   private const string REAL_RUN = "/home/dan/ForClaude/GenericVectorBuilder/bench-results/20261007-233551-eshoponweb";

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public BenchResultsV8hFinalTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-page-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The command's output is read without a refusal, no field of it is listed as one the page does not read, no sentence of it lacks a place, and every
   /// sentence that is not a quoted clause is printed exactly as many times as the file holds it.
   /// </summary>
   [Fact]
   public void TheOutputOfTheIntegratedBuild_IsReadAndPlacedWhole()
   {
      BenchConsolidated model = Model();

      string html = BenchConsolidatedHtml.Block( model );

      Assert.Empty( model.UnreadFields ?? Array.Empty<string>() );
      Assert.DoesNotContain( BenchLegends.H_UNPLACED, html );
      Assert.DoesNotContain( BenchLegends.H_UNREAD, html );
      Assert.Empty( model.Audit!.Failures );
      Assert.All( model.Sentences.Where( s => !s.Quote ).Select( s => s.Text ).Distinct( StringComparer.Ordinal ), text =>
         Assert.True( Regex.Matches( html, Regex.Escape( BenchFormat.Enc( text ) ) ).Count >= 1, $"not printed: {text}" ) );
   }

   /// <summary>
   /// ClickHouse's QPS@8 row, which the command marks not held, is drawn in the group of rows that are not ranked, after every ranked row; the command's
   /// sentence under the table says it is shown and not ranked; no ranked row names it; the other three tables rank ClickHouse as before.
   /// </summary>
   [Fact]
   public void ClickHousesEightSearcherRow_IsShownNotHeldAndNotRanked()
   {
      BenchConsolidated model = Model();
      BenchRow row = model.Metrics.Single( m => m.Metric == "qps8" ).Rows.Single( r => r.Target == "clickhouse" );
      string html = BenchConsolidatedHtml.Block( model );

      Assert.True( row.IsNotHeld );
      string table = TableHtml( html, "qps8" );
      int group = table.IndexOf( "data-group=\"not-ranked\"", StringComparison.Ordinal );
      Assert.True( group > 0 );
      List<string> order = Regex.Matches( table, "<tr data-target=\"([^\"]+)\"" ).Select( m => m.Groups[1].Value ).ToList();
      Assert.Equal( "clickhouse", order[^1] );
      Assert.Equal( 19, order.Count );
      Assert.Contains( BenchLegends.L_NOT_HELD, Regex.Match( table, "<tr data-target=\"clickhouse\">.*?</tr>", RegexOptions.Singleline ).Value );
      Assert.Contains( "clickhouse is shown and not ranked in this table", model.Sentences.Single( s => s.Slot.StartsWith( "table.qps8.notHeld", StringComparison.Ordinal ) ).Text );
      Assert.Contains( model.Sentences.Single( s => s.Slot.StartsWith( "table.qps8.notHeld", StringComparison.Ordinal ) ).Text, WebUtilityDecode( html ) );
      Assert.All( order.Take( 18 ), t => Assert.DoesNotContain( "clickhouse", Regex.Match( table, $"<tr data-target=\"{Regex.Escape( t )}\">.*?</tr>", RegexOptions.Singleline ).Value.Split( "</td>" )[^3] ) );
      foreach( string metric in new[] { "p50Ms", "qps1", "exactP50Ms" } )
      {
         Assert.DoesNotContain( "data-group=\"not-ranked\"", TableHtml( html, metric ) );
      }
   }

   /// <summary>
   /// Both builds are named in the lead: the sentence for the build that measured the v8 runs and the sentence for the build that consolidated the report are
   /// printed between the headline and the first table, and the threshold and its basis come after every table.
   /// </summary>
   [Fact]
   public void TheLead_NamesBothBuilds_AndTheResultsComeBeforeTheThreshold()
   {
      BenchConsolidated model = Model();
      string html = BenchConsolidatedHtml.Block( model );

      int headline = html.IndexOf( "data-slot=\"headline\"", StringComparison.Ordinal );
      int firstTable = html.IndexOf( "data-metric=\"p50Ms\"", StringComparison.Ordinal );
      foreach( string slot in new[] { "subtitle.build.v8", "subtitle.build.consolidated" } )
      {
         BenchSentence sentence = model.Sentences.Single( s => s.Slot == slot );
         int at = html.IndexOf( $"data-slot=\"{slot}\"", StringComparison.Ordinal );
         Assert.True( at > headline && at < firstTable, $"{slot} is not between the headline and the first table" );
         Assert.Contains( "build", sentence.Text );
      }

      int threshold = html.IndexOf( $"<h2>{BenchLegends.H_THRESHOLD}</h2>", StringComparison.Ordinal );
      Assert.True( threshold > html.IndexOf( "data-metric=\"exactP50Ms\"", StringComparison.Ordinal ) );
      Assert.True( threshold > html.IndexOf( $"<h2>{BenchLegends.H_WHY}</h2>", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// Every run of a session and every basis run shows the hash of the results.json the command read (twelve runs, each a 64-character hex hash), and every
   /// container image shows the reference its compose file names, the floating tag of Chroma included.
   /// </summary>
   [Fact]
   public void TheRunsAndTheImages_ShowTheirHashesAndReferences()
   {
      BenchConsolidated model = Model();
      string html = BenchConsolidatedHtml.Block( model );

      string runs = Regex.Match( html, "<table class=\"bench-table\" data-table=\"runs-used\">.*?</table>", RegexOptions.Singleline ).Value;
      Assert.Equal( 12, Regex.Matches( runs, "<td title=\"[0-9a-f]{64}\"><code>[0-9a-f]{12}</code></td>" ).Count );
      Assert.All( model.Images, image =>
      {
         Assert.False( string.IsNullOrEmpty( image.Ref ), $"{image.Target} has no image reference" );
         Assert.Contains( $"<code>{BenchFormat.Enc( image.Ref! )}</code>", html );
      } );
      Assert.Contains( model.Images, i => i.Target == "chroma" && i.Ref!.EndsWith( ":latest", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The clause table holds exactly the clauses of the recorded texts whose words are not a sentence of the file, so none is printed twice and none is
   /// dropped.
   /// </summary>
   [Fact]
   public void TheClauseTable_HoldsEveryClauseThatNoSentencePrints()
   {
      BenchConsolidated model = Model();
      var sentences = model.Sentences.Select( s => s.Text ).ToHashSet( StringComparer.Ordinal );
      int expected = model.RecordedTextList.SelectMany( t => t.Clauses ).Count( c => !sentences.Contains( c.Text ) );
      string html = BenchConsolidatedHtml.Block( model );

      string table = Regex.Match( html, "<table class=\"bench-table\" data-table=\"recorded-texts\">.*?</table>", RegexOptions.Singleline ).Value;

      Assert.True( expected > 0 );
      Assert.Equal( expected, Regex.Matches( table, "<tr data-target=" ).Count );
   }

   /// <summary>
   /// The run page of a real run, served from a folder holding the command's output next to that run's real report, carries the corrections of the
   /// repository's list file (when it is on this machine) and marks each statement in the report text; the page of a published set is the summary.
   /// </summary>
   [FactIfRepositoryFileExists( BenchRunNotes.DEFAULT_FILE, REAL_RUN )]
   public void TheRunPageOfARealRun_CarriesTheRepositoryCorrections()
   {
      const string RUN = "20261007-233551-eshoponweb";
      const string results = REAL_RUN;

      Directory.CreateDirectory( Path.Combine( _root, PUBLISHED ) );
      File.WriteAllText( Path.Combine( _root, PUBLISHED, "consolidated.json" ), BenchResultsFixtureFiles.Text( JSON ) );
      Directory.CreateDirectory( Path.Combine( _root, RUN ) );
      File.Copy( Path.Combine( results, "results.json" ), Path.Combine( _root, RUN, "results.json" ) );
      File.Copy( Path.Combine( results, "results.md" ), Path.Combine( _root, RUN, "results.md" ) );
      string list = BenchResultsV8hWebTests.FindRepositoryFile( BenchRunNotes.DEFAULT_FILE )!;

      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN, list )!;

      Assert.Equal( 10, Regex.Matches( html, @"<li data-note=""\d+"" data-kind=""\w+"" data-places=""1""" ).Count );
      Assert.Equal( 10, Regex.Matches( html, @"<strong>\[Correction \d+\]</strong>" ).Count );
      Assert.Contains( "weaviate.compose.yaml sets no persistence variable <strong>[Correction 1]</strong>", html );
      Assert.Contains( "4.67 GiB added by this load <strong>[Correction", html );
      Assert.Contains( "235.33 MiB added by this load <strong>[Correction", html );
      Assert.DoesNotContain( "Correction", File.ReadAllText( Path.Combine( results, "results.md" ) ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The command's output, read.
   /// </summary>
   private static BenchConsolidated Model()
   {
      return BenchConsolidatedReader.Read( BenchResultsFixtureFiles.Text( JSON ) );
   }

   /// <summary>
   /// The HTML of one metric's table (from its heading to the end of the table).
   /// </summary>
   private static string TableHtml( string html, string metric )
   {
      int start = html.IndexOf( $"data-metric=\"{metric}\"", StringComparison.Ordinal );
      return html[start..html.IndexOf( "</table>", start, StringComparison.Ordinal )];
   }

   /// <summary>
   /// The page's text with its entities decoded, tags removed and white space folded, so a sentence can be searched for as written.
   /// </summary>
   private static string WebUtilityDecode( string html )
   {
      return Regex.Replace( System.Net.WebUtility.HtmlDecode( Regex.Replace( html, "<[^>]+>", " " ) ), @"\s+", " " );
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
