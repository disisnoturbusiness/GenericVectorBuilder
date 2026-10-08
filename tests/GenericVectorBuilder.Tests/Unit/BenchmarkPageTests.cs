using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The split of the benchmark summary into two pages: the Vector search benchmark page at /benchmark, which holds the header (what the numbers are of, the
/// engines in order of searches per second with eight at once) and links to the full results, and the full results page at /bench-results, which holds no
/// header, is the page it was before the header was put on it, and has its "Vector search benchmark" heading as a link to the first page.
/// What these tests hold the split to: the new route serves the header; the full results page holds no header block; its heading links to the new route; the
/// new page links to the full results page; the full results page, with its heading's link taken out, is byte for byte the page the code before the header
/// (commit 3f16f85, the parent of 6865c80) rendered for the same inputs; the header on the new page is the header that was live at 6865c80, with only the
/// words that said "below" and the link to the anchor changed; the set page and the run page are the pages they were; and both pages draw the same set.
/// Why the page that was is a stored file: the code of the page before the header no longer exists in the tree, so a test that built the expected page
/// from today's code would show only that the code agrees with itself. The files are the output of the old code (see BenchResultsFixtures/README.txt).
/// Why the routes are called over HTTP as well as the page builders: the route table is where "the new route renders the header" can fail on its own.
/// </summary>
public sealed class BenchmarkPageTests : IDisposable
{
   #region Data Members

   private const string FOLDER = "published-2026-10-08";
   private const string OLDER_FOLDER = "published-2026-10-07";
   private const string RUN = "20261006-130619-eshoponweb";
   private const string HEADING_LINK = "<h1><a href=\"/benchmark\">Vector search benchmark</a></h1>";
   private const string HEADING_PLAIN = "<h1>Vector search benchmark</h1>";
   private const string FULL_RESULTS_LINK = "<a href=\"/bench-results\">Full results: every table, the flags and every run</a>";
   private const int SERVER_SECONDS = 60;
   private const int REQUEST_SECONDS = 15;
   private const int LONG_LEGEND = 25;

   private static readonly Regex HEADER_SECTION = new( "<section class=\"bench-header\".*?</section>", RegexOptions.Singleline | RegexOptions.Compiled );

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public BenchmarkPageTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-page-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The two routes are the two addresses the pages link to each other by.
   /// </summary>
   [Fact]
   public void TheRoutes_AreTheAddressesTheTwoPagesLinkBy()
   {
      Assert.Equal( "/benchmark", BenchRoutes.BENCHMARK );
      Assert.Equal( "/bench-results", BenchRoutes.FULL_RESULTS );
      Assert.Equal( "Vector search benchmark", BenchRoutes.BENCHMARK_NAME );
   }

   /// <summary>
   /// The new route answers 200 with an HTML page titled "Vector search benchmark" that holds the header: the first sentence, the order of the engines, the
   /// bold Redis block and the link to the full results. The page the route serves is the page the builder draws.
   /// </summary>
   [Fact]
   public async Task TheNewRoute_RendersTheHeader()
   {
      WriteFixtureRoot();
      await WithServerAsync( async client =>
      {
         using HttpResponseMessage response = await client.GetAsync( "/benchmark" );
         string body = await response.Content.ReadAsStringAsync();

         Assert.Equal( HttpStatusCode.OK, response.StatusCode );
         Assert.Equal( "text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString() );
         Assert.Contains( "<title>Vector search benchmark</title>", body );
         Assert.Contains( "<section class=\"bench-header\">", body );
         Assert.Contains( BenchLegends.L_HEADER_FROM, body );
         Assert.Contains( "<strong class=\"bench-header-memory\">redis holds its data in memory</strong>", body );
         Assert.Contains( "data-engine=\"mariadb\"", body );
         Assert.Contains( FULL_RESULTS_LINK, body );
         Assert.Equal( BenchResultsEndpoints.BenchmarkPageHtml( _root ), body );
      } );
   }

   /// <summary>
   /// The full results route answers 200 with no header block, and its heading links to the new route; following that link reaches the page with the
   /// header, and the new page's link leads back to a page with the full results.
   /// </summary>
   [Fact]
   public async Task TheTwoRoutes_LinkToEachOther_AndOnlyTheNewOneHoldsTheHeader()
   {
      WriteFixtureRoot();
      await WithServerAsync( async client =>
      {
         string results = await client.GetStringAsync( "/bench-results" );
         string href = Regex.Match( results, "<h1><a href=\"([^\"]*)\">Vector search benchmark</a></h1>" ).Groups[1].Value;
         string benchmark = await client.GetStringAsync( href );
         string back = Regex.Match( benchmark, "<a href=\"([^\"]*)\">Full results: every table, the flags and every run</a>" ).Groups[1].Value;
         string again = await client.GetStringAsync( back );

         Assert.DoesNotContain( "bench-header", results );
         Assert.Equal( "/benchmark", href );
         Assert.Contains( "<section class=\"bench-header\">", benchmark );
         Assert.Equal( "/bench-results", back );
         Assert.Equal( results, again );
         Assert.Contains( "<h2>All runs</h2>", again );
      } );
   }

   /// <summary>
   /// The page is titled and headed "Vector search benchmark", the heading is plain text on the page it names, and the header follows it directly.
   /// </summary>
   [Fact]
   public void TheNewPage_IsTitledAndHeaded_VectorSearchBenchmark()
   {
      string page = BenchmarkPage();

      Assert.Contains( "<title>Vector search benchmark</title>", page );
      Assert.Contains( "<main class=\"card bench\">" + HEADING_PLAIN + "<section class=\"bench-header\">", page );
      Assert.DoesNotContain( HEADING_LINK, page );
      Assert.Single( Regex.Matches( page, Regex.Escape( HEADING_PLAIN ) ) );
   }

   /// <summary>
   /// The new page contains exactly the header that was live at 6865c80, for the same file: the same words, numbers, bold block, not-held row, notes and
   /// small text. Three things differ, each once and each because the full results are no longer below the header: the link goes to /bench-results and
   /// no longer says "below", the anchor it pointed at is gone, and the sentence about the figures of the rows that are not ranked no longer says "below".
   /// </summary>
   [Fact]
   public void TheNewPage_HoldsTheHeaderThatWasLiveAt6865c80_ExceptThePlacesThatSaidBelow()
   {
      string live = BenchResultsFixtureFiles.Text( "bench-split.header.at-6865c80.html" );
      string expected = Swap( live, "<a href=\"#bench-full-results\">Full results below: every table, the flags and every run</a>", FULL_RESULTS_LINK );
      expected = Swap( expected, "<a id=\"bench-full-results\"></a>", string.Empty );
      expected = Swap( expected, "are in the full results below.", "are in the full results." );

      Assert.Equal( expected, HeaderOf( BenchmarkPage() ) );
      Assert.Contains( "Most searches per second first.", expected );
      Assert.Contains( "bench-header-memory", expected );
      Assert.Contains( "not held, not ranked", expected );
   }

   /// <summary>
   /// The new page links to the full results page at /bench-results, with one link in the header, in the header's own words, and has no anchor to a place on
   /// itself. (The bar at the top of every page has its own link to the same address.)
   /// </summary>
   [Fact]
   public void TheNewPage_LinksToTheFullResultsPage()
   {
      string page = BenchmarkPage();

      Assert.Single( Regex.Matches( HeaderOf( page ), "href=" ) );
      Assert.Single( Regex.Matches( HeaderOf( page ), "href=\"/bench-results\"" ) );
      Assert.Contains( "<p class=\"bench-header-link\">" + FULL_RESULTS_LINK + "</p>", page );
      Assert.DoesNotContain( "href=\"#", page );
   }

   /// <summary>
   /// The full results page holds no header block: no header section, none of the header's own sentences and legends, and no order of the engines by
   /// searches per second with eight at once.
   /// </summary>
   [Fact]
   public void TheFullResultsPage_HoldsNoHeaderBlock()
   {
      WriteFixtureRoot();
      string page = BenchResultsEndpoints.ListPageHtml( _root );
      string[] headerLegends = BenchLegends.All.Where( l => l.Key.StartsWith( "L_HEADER_", StringComparison.Ordinal ) && l.Value.Length >= LONG_LEGEND && l.Key != "L_HEADER_ONE_SESSION" ).Select( l => l.Value ).ToArray();

      Assert.NotEmpty( headerLegends );
      Assert.DoesNotContain( "bench-header", page );
      Assert.All( headerLegends, legend => Assert.DoesNotContain( legend, WebUtility.HtmlDecode( page ) ) );
      Assert.DoesNotContain( "These benchmarks were taken from", page );
   }

   /// <summary>
   /// The "Vector search benchmark" text at the top of the full results page is a link, once, and the link points at the new route.
   /// </summary>
   [Fact]
   public void TheHeadingOfTheFullResultsPage_LinksToTheNewRoute()
   {
      WriteFixtureRoot();
      string page = BenchResultsEndpoints.ListPageHtml( _root );
      MatchCollection links = Regex.Matches( page, "<h1><a href=\"([^\"]*)\">Vector search benchmark</a></h1>" );

      Assert.Single( links );
      Assert.Equal( BenchRoutes.BENCHMARK, links[0].Groups[1].Value );
      Assert.Contains( "<main class=\"card bench\">" + HEADING_LINK + "<p class=\"bench-source muted\">", page );
   }

   /// <summary>
   /// The full results page, with the link of its heading taken out, is byte for byte the page the code before the header rendered for the same inputs
   /// (a set published on 8 Oct with its report, and a run it uses).
   /// </summary>
   [Fact]
   public void TheFullResultsPage_MinusTheLink_IsThePageBeforeTheHeader()
   {
      WriteFixtureRoot();
      string was = BenchResultsFixtureFiles.Text( "bench-split.list-page.pre-6865c80.html" );
      string page = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Single( Regex.Matches( page, Regex.Escape( HEADING_LINK ) ) );
      Assert.Equal( was, page.Replace( HEADING_LINK, HEADING_PLAIN ) );
      Assert.Contains( "<h2>All runs</h2>", was );
      Assert.DoesNotContain( "bench-header", was );
   }

   /// <summary>
   /// A results folder that holds nothing published prints the page it always printed: its heading is "Benchmark results" and is not a link, because the
   /// words "Vector search benchmark" are not on the page; a missing folder prints its notice.
   /// </summary>
   [Fact]
   public void TheFullResultsPage_WithNothingPublished_IsAsItWas()
   {
      string empty = BenchResultsEndpoints.ListPageHtml( _root );
      string missing = BenchResultsEndpoints.ListPageHtml( Path.Combine( _root, "no-such-folder" ) );

      Assert.Contains( "<main class=\"card bench\"><h1>Benchmark results</h1>", empty );
      Assert.DoesNotContain( "href=\"/benchmark\"", empty );
      Assert.Contains( "<h1>Benchmark results</h1><p class=\"muted\">No results folder yet.</p>", missing );
      Assert.DoesNotContain( "href=\"/benchmark\"", missing );
   }

   /// <summary>
   /// The page of a set and the page of a run are the pages they were: byte for byte what the code before the header rendered, and they carry no link to
   /// the new route.
   /// </summary>
   [Fact]
   public void TheSetPage_AndTheRunPage_AreAsTheyWere()
   {
      WriteFixtureRoot();
      string set = BenchResultsEndpoints.RunPageHtml( _root, FOLDER )!;
      string run = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.Equal( BenchResultsFixtureFiles.Text( "bench-split.set-page.pre-6865c80.html" ), set );
      Assert.Equal( BenchResultsFixtureFiles.Text( "bench-split.run-page.pre-6865c80.html" ), run );
      Assert.DoesNotContain( "href=\"/benchmark\"", set + run );
      Assert.DoesNotContain( "Vector search benchmark", set + run );
   }

   /// <summary>
   /// When the newest published file cannot be read, both pages show the same older set and name the failure in the same words: a page that showed the
   /// older numbers with no notice would pass for the newest.
   /// </summary>
   [Fact]
   public void BothPages_DrawTheSameSet_WhenTheNewestFileCannotBeRead()
   {
      WriteSet( OLDER_FOLDER, BenchResultsFixtureFiles.Text( "bench-split.consolidated.json" ) );
      WriteSet( FOLDER, "{ not json" );
      string benchmark = BenchResultsEndpoints.BenchmarkPageHtml( _root );
      string results = BenchResultsEndpoints.ListPageHtml( _root );
      Regex notice = new( "<p class=\"errors\">The newest published results.*?</p>", RegexOptions.Singleline );

      Assert.True( notice.IsMatch( benchmark ), "no notice on the benchmark page" );
      Assert.Equal( notice.Match( results ).Value, notice.Match( benchmark ).Value );
      Assert.Contains( $"so the page shows the older {OLDER_FOLDER}.", benchmark );
      Assert.Contains( $"Set: {OLDER_FOLDER}.", benchmark );
      Assert.Contains( $"Numbers from <a href=\"/bench-results/{OLDER_FOLDER}\">{OLDER_FOLDER}</a>.", results );
      Assert.Contains( "<section class=\"bench-header\">", benchmark );
   }

   /// <summary>
   /// With no published folder the new page says so and still links to the full results; with a file the reader refuses it prints the error and the
   /// link, and no header.
   /// </summary>
   [Fact]
   public void TheNewPage_WithNothingToShow_SaysSo_AndStillLinksToTheFullResults()
   {
      string none = BenchResultsEndpoints.BenchmarkPageHtml( _root );
      WriteSet( FOLDER, "{ not json" );
      string bad = BenchResultsEndpoints.BenchmarkPageHtml( _root );

      Assert.Contains( HEADING_PLAIN + $"<p class=\"muted\">{BenchLegends.L_BENCHMARK_NONE}</p>", none );
      Assert.Contains( FULL_RESULTS_LINK, none );
      Assert.Contains( $"<p class=\"errors\">{BenchLegends.UNREADABLE}:", bad );
      Assert.Contains( FULL_RESULTS_LINK, bad );
      Assert.DoesNotContain( "<section class=\"bench-header\"", none + bad );
   }

   /// <summary>
   /// A file in a shape older than v8 gets the older-format notice the full results page gives it, and the link to the full results; no header is made
   /// from it.
   /// </summary>
   [Fact]
   public void TheNewPage_ForAFileOfAnOlderShape_PrintsTheOlderFormatNotice_AndTheLink()
   {
      WriteSet( FOLDER, BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.json" ) );
      string page = BenchResultsEndpoints.BenchmarkPageHtml( _root );

      Assert.Contains( BenchLegends.OLDER_FORMAT, page );
      Assert.Contains( FULL_RESULTS_LINK, page );
      Assert.DoesNotContain( "<section class=\"bench-header\"", page );
   }

   /// <summary>
   /// A set marked stopped gets the stopped notice on the new page, with the link to the full results where the stopped block is.
   /// </summary>
   [Fact]
   public void TheNewPage_ForAStoppedSet_LinksToTheFullResults_WhereTheStoppedBlockIs()
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         root["guards"] = new JsonObject { ["g2"] = new JsonObject { ["stopped"] = true, ["pairs"] = new JsonArray( "mariadb over pgvector on p50" ) }, ["g3"] = new JsonObject { ["targets"] = new JsonArray() } };
         ( (JsonArray)root["sentences"]! ).RemoveAt( 0 );
      } );
      WriteSet( FOLDER, json );
      string page = BenchResultsEndpoints.BenchmarkPageHtml( _root );

      Assert.Contains( BenchLegends.L_HEADER_STOPPED, page );
      Assert.Contains( FULL_RESULTS_LINK, page );
      Assert.DoesNotContain( "bench-header-row", page );
      Assert.DoesNotContain( "below", WebUtility.HtmlDecode( HeaderOf( page ) ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Replaces a string that must be in the text exactly once.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <param name="old">What to replace.</param>
   /// <param name="replacement">What to put.</param>
   /// <returns>The changed text.</returns>
   private static string Swap( string text, string old, string replacement )
   {
      Assert.Single( Regex.Matches( text, Regex.Escape( old ) ) );
      return text.Replace( old, replacement );
   }

   /// <summary>
   /// The header section of a page.
   /// </summary>
   /// <param name="page">The page.</param>
   /// <returns>The section's HTML.</returns>
   private static string HeaderOf( string page )
   {
      Match match = HEADER_SECTION.Match( page );
      Assert.True( match.Success, "no header section on the page" );
      return match.Value;
   }

   /// <summary>
   /// The new page for the stored fixture, put under its published name.
   /// </summary>
   /// <returns>Full HTML page.</returns>
   private string BenchmarkPage()
   {
      WriteFixtureRoot();
      return BenchResultsEndpoints.BenchmarkPageHtml( _root );
   }

   /// <summary>
   /// Puts the stored fixture's inputs in the results root: the set with its report, and the run the set uses.
   /// </summary>
   private void WriteFixtureRoot()
   {
      WriteSet( FOLDER, BenchResultsFixtureFiles.Text( "bench-split.consolidated.json" ) );
      File.WriteAllText( Path.Combine( _root, FOLDER, "consolidated.md" ), BenchResultsFixtureFiles.Text( "bench-split.consolidated.md" ) );
      Directory.CreateDirectory( Path.Combine( _root, RUN ) );
      File.WriteAllText( Path.Combine( _root, RUN, "results.json" ), BenchResultsFixtureFiles.Text( "bench-split.results.json" ) );
   }

   /// <summary>
   /// Writes a consolidated folder holding a file.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <param name="json">The file's text.</param>
   private void WriteSet( string name, string json )
   {
      string folder = Path.Combine( _root, name );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "consolidated.json" ), json );
   }

   /// <summary>
   /// Runs the routes in a real web host on a free loopback port, with the results root of this test, and hands a client to the body. The host and the
   /// client have timeouts, and the host is stopped when the body ends, whether it passes or fails.
   /// </summary>
   /// <param name="body">What to do with the client.</param>
   private async Task WithServerAsync( Func<HttpClient, Task> body )
   {
      WebApplicationBuilder builder = WebApplication.CreateBuilder();
      builder.WebHost.UseUrls( "http://127.0.0.1:0" );
      builder.Logging.ClearProviders();
      builder.Configuration["Gvb:BenchResultsPath"] = _root;
      await using WebApplication app = builder.Build();
      BenchResultsEndpoints.Map( app );
      using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( SERVER_SECONDS ) );
      await app.StartAsync( timeout.Token );
      try
      {
         string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
         using var client = new HttpClient { BaseAddress = new Uri( address ), Timeout = TimeSpan.FromSeconds( REQUEST_SECONDS ) };
         await body( client );
      }
      finally
      {
         await app.StopAsync( timeout.Token );
      }
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
