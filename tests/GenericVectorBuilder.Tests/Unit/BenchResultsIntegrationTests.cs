using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// A fact that is skipped, with the reason named, when a file it reads is not on this machine: these tests read the folders of real runs
/// and must not fail on a machine that has none of them.
/// A skipped test shows as skipped, so the missing check is visible and never reported as a pass.
/// </summary>
public sealed class FactIfPathExistsAttribute : FactAttribute
{
   #region Constructor

   /// <summary>
   /// Marks the test skipped when the path does not exist.
   /// </summary>
   /// <param name="path">A file or folder the test reads.</param>
   public FactIfPathExistsAttribute( string path )
   {
      if( !File.Exists( path ) && !Directory.Exists( path ) )
      {
         Skip = $"{path} is not on this machine";
      }
   }

   #endregion Constructor
}

/// <summary>
/// The page against real files: the consolidate command's own output (the dry check's consolidated.json, kept in the BenchResultsFixtures folder
/// so no test depends on a path outside the repository; read and drawn without a refusal, every sentence printed once) and the folders of real runs
/// (the report of a real run drawn with its latency-split sentences left out, every table of a real consolidated report keeping its cell count).
/// Why real files as well as the built fixture: the fixture is written from the contract, and only the command's own output shows that
/// the contract and the page read the same thing.
/// </summary>
public class BenchResultsIntegrationTests : IDisposable
{
   #region Data Members

   private const string DRY_JSON = "v8-dry.consolidated.json";
   private const string DRY_MD = "v8-dry.consolidated.md";
   private const string BENCH_RESULTS = "/home/dan/ForClaude/GenericVectorBuilder/bench-results";
   private const string REAL_RUN = BENCH_RESULTS + "/20261006-130619-eshoponweb";
   private const string REAL_BLOCKED = BENCH_RESULTS + "/blocked-2026-10-06-v7";

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public BenchResultsIntegrationTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-page-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The command's own output is read without a refusal, every sentence of it is printed exactly once, none lands under the heading
   /// for statements with no place, and no claim of the earlier page is printed. A sentence is counted by its text; a quoted clause of a
   /// recorded text is counted by its own block instead, because its words are also inside the whole recorded texts that the setup-split
   /// table prints, so a count of its text cannot tell a second printing from that.
   /// </summary>
   [Fact]
   public void TheOutputOfTheConsolidateCommand_IsReadAndDrawn()
   {
      string json = BenchResultsFixtureFiles.Text( DRY_JSON );
      Assert.Equal( BenchShape.Consolidated, BenchConsolidatedReader.Detect( json ) );
      BenchConsolidated model = BenchConsolidatedReader.Read( json );
      string html = BenchConsolidatedHtml.Block( model );
      string text = WebUtility.HtmlDecode( Regex.Replace( html, "<[^>]+>", " " ) );

      Assert.NotEmpty( model.Sentences );
      Assert.All( model.Sentences.Select( s => s.Text ).Distinct(), t => Assert.True( Regex.Matches( text, Regex.Escape( t ) ).Count >= 1, $"not printed: {t}" ) );
      Assert.All( model.Sentences.Where( s => !s.Quote ), s => Assert.True( Regex.Matches( text, Regex.Escape( s.Text ) ).Count == model.Sentences.Count( o => o.Text == s.Text ) + SetupChangesHolding( json, s.Text ),
         $"printed a different number of times than it is in the file (and in the setup-split table, which prints each changed text of the file as it is): {s.Text}" ) );
      Assert.All( model.Sentences.Where( s => s.Quote ), s => Assert.True( Regex.Matches( html, Regex.Escape( QuoteBlock( s ) ) ).Count == model.Sentences.Count( o => o.Slot == s.Slot ),
         $"the quoted clause {s.Slot} is not printed once in its own block, with its own words: {s.Text}" ) );
      Assert.Equal( model.Sentences.Count, model.Sentences.Select( s => s.Slot ).Distinct( StringComparer.Ordinal ).Count() );
      Assert.DoesNotContain( BenchLegends.H_UNPLACED, html );
      Assert.DoesNotMatch( new Regex( @"was fastest|fastest together|Not final|about 2%|\bbands?\b|\btied\b", RegexOptions.IgnoreCase ), text );
      Assert.All( model.Metrics, m => Assert.Contains( $"data-metric=\"{m.Metric}\"", html ) );
   }

   /// <summary>
   /// The command's output, put in a folder under its published name, is drawn as the summary page and as its own page with a banner-free header.
   /// </summary>
   [Fact]
   public void TheOutputOfTheConsolidateCommand_IsServedAsTheSummaryOfAPublishedFolder()
   {
      string folder = Path.Combine( _root, "published-2026-10-07" );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "consolidated.json" ), BenchResultsFixtureFiles.Text( DRY_JSON ) );
      File.WriteAllText( Path.Combine( folder, "consolidated.md" ), BenchResultsFixtureFiles.Text( DRY_MD ) );

      string list = BenchResultsEndpoints.ListPageHtml( _root );
      string own = BenchResultsEndpoints.RunPageHtml( _root, "published-2026-10-07" )!;

      Assert.Contains( "Numbers from <a href=\"/bench-results/published-2026-10-07\">published-2026-10-07</a>.", list );
      Assert.Contains( "class=\"bench-lead\"", list );
      Assert.DoesNotContain( "bench-banner", own );
      Assert.DoesNotContain( BenchLegends.UNREADABLE, list );
   }

   /// <summary>
   /// The page of a real run draws its table, leaves out the sentences that relate the client CPU figure to the latency, and says so; the
   /// raw report keeps them.
   /// </summary>
   [FactIfPathExists( REAL_RUN )]
   public void ARealRunPage_LeavesOutTheLatencySplitSentences()
   {
      string folder = Path.Combine( _root, "20261006-130619-eshoponweb" );
      Directory.CreateDirectory( folder );
      foreach( string file in new[] { "results.json", "results.md" } )
      {
         File.Copy( Path.Combine( REAL_RUN, file ), Path.Combine( folder, file ) );
      }

      string html = BenchResultsEndpoints.RunPageHtml( _root, "20261006-130619-eshoponweb" )!;

      Assert.DoesNotContain( "Where it is close to the latency", html );
      Assert.DoesNotContain( "not client overhead", html );
      Assert.Contains( WebUtility.HtmlEncode( BenchLegends.STRIPPED ), html );
      Assert.Equal( 19, Regex.Matches( html, "<tr data-target=" ).Count );
      Assert.Contains( "Where it is close to the latency", File.ReadAllText( BenchResultsEndpoints.RawFilePath( _root, "20261006-130619-eshoponweb", "results.md" )! ) );
   }

   /// <summary>
   /// Every table of a real consolidated report keeps its header's cell count in every row, including the rows whose recorded text holds an
   /// escaped pipe.
   /// </summary>
   [FactIfPathExists( REAL_BLOCKED + "/consolidated.md" )]
   public void ARealConsolidatedReport_KeepsEveryTableRowAtItsHeadersWidth()
   {
      string md = File.ReadAllText( REAL_BLOCKED + "/consolidated.md" );
      string html = BenchMarkdown.Render( md );

      foreach( Match table in Regex.Matches( html, "<table>(.*?)</table>", RegexOptions.Singleline ) )
      {
         int[] widths = Regex.Matches( table.Groups[1].Value, "<tr>(.*?)</tr>", RegexOptions.Singleline ).Select( r => Regex.Matches( r.Groups[1].Value, "<t[hd]>" ).Count ).ToArray();
         Assert.NotEmpty( widths );
         Assert.All( widths, w => Assert.Equal( widths[0], w ) );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The block the page prints for one quoted sentence: its slot, then the quotation holding exactly its text.
   /// </summary>
   /// <param name="sentence">The quoted sentence.</param>
   /// <returns>The start of the block, as the page writes it.</returns>
   private static string QuoteBlock( BenchSentence sentence )
   {
      return $"<div class=\"bench-sentence-block\" data-slot=\"{BenchFormat.Enc( sentence.Slot )}\"><blockquote class=\"bench-quote\">{BenchFormat.Enc( sentence.Text )}</blockquote>";
   }

   /// <summary>
   /// How many texts of the setup-split table equal a sentence: the table prints the text each changed field recorded before and after, and a recorded
   /// text that the file also quotes as a sentence (a durability text) is on the page once as the sentence and once for each change that holds it.
   /// Read from the file's own JSON, so the count does not depend on the page's model.
   /// </summary>
   /// <param name="json">The file's text.</param>
   /// <param name="text">The sentence's text.</param>
   /// <returns>The number of before and after texts equal to it.</returns>
   private static int SetupChangesHolding( string json, string text )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      if( !doc.RootElement.TryGetProperty( "basis", out JsonElement basis ) || !basis.TryGetProperty( "setupSplits", out JsonElement splits ) )
      {
         return 0;
      }

      return splits.EnumerateArray().SelectMany( s => s.GetProperty( "changes" ).EnumerateArray() )
         .Sum( c => ( c.GetProperty( "before" ).GetString() == text ? 1 : 0 ) + ( c.GetProperty( "after" ).GetString() == text ? 1 : 0 ) );
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
