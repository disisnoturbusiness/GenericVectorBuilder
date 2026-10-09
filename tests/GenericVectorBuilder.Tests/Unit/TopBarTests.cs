using System.Net;
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
/// The bar at the top of every page: "GenericVectorBuilder" at the left and, at the right, a link that says "Benchmark" and goes to /benchmark (it used to say
/// "Benchmark results" and go to /bench-results).
/// What these tests hold the bar to, on each kind of page: the builder page at "/" (a static file), the Vector search benchmark page, the full results page,
/// a set page, a run page, and the pages drawn with nothing to show (an empty results folder, no results folder, a file the reader refuses). On every one the
/// bar is the same text, the link is the only link at the right, and no link in the bar goes to /bench-results.
/// Why the pages are fetched over HTTP: the route table and the static file middleware are where "the link is on this page" can fail on its own.
/// </summary>
public sealed class TopBarTests : IDisposable
{
   #region Data Members

   private const string FOLDER = "published-2026-10-08";
   private const string RUN = "20261006-130619-eshoponweb";
   private const string LINK = "<a href=\"/benchmark\"";
   private const string TEXT = ">Benchmark</a>";
   private const string BRAND = "GenericVectorBuilder";
   private const int SERVER_SECONDS = 60;
   private const int REQUEST_SECONDS = 15;

   private static readonly Regex BAR = new( "<header class=\"topbar\">(.*?)</header>", RegexOptions.Singleline | RegexOptions.Compiled );
   private static readonly Regex LINKS = new( "<a href=\"([^\"]*)\"[^>]*>([^<]*)</a>", RegexOptions.Compiled );

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public TopBarTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "top-bar-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The link at the right of the bar is the Vector search benchmark page's address, and its text is one word, "Benchmark".
   /// </summary>
   [Fact]
   public void TheLink_IsCalledBenchmark_AndGoesToTheBenchmarkPage()
   {
      Assert.Equal( "Benchmark", BenchRoutes.TOP_BAR_LINK );
      Assert.Equal( "/benchmark", BenchRoutes.BENCHMARK );
   }

   /// <summary>
   /// The builder page's bar, read from the file the web app serves: the brand at the left, then the one link, "Benchmark" to /benchmark, with the words
   /// "Benchmark results" and the address /bench-results gone from the bar.
   /// </summary>
   [Fact]
   public void TheBuilderPageFile_HasTheBenchmarkLink()
   {
      string bar = BarOf( File.ReadAllText( PageFile( "index.html" ) ) );

      Assert.Contains( "<h1>" + BRAND + "</h1>", bar );
      Assert.Contains( "<a href=\"/benchmark\" class=\"toplink\">Benchmark</a>", bar );
      AssertTheBar( bar, expectBrandLink: false );
   }

   /// <summary>
   /// "/" over HTTP, from the static file middleware the app uses (default files and static files from the web root), carries the same bar as the file.
   /// </summary>
   [Fact]
   public async Task TheBuilderPage_OverHttp_HasTheBenchmarkLink()
   {
      await WithServerAsync( async client =>
      {
         using HttpResponseMessage response = await client.GetAsync( "/" );
         string body = await response.Content.ReadAsStringAsync();

         Assert.Equal( HttpStatusCode.OK, response.StatusCode );
         Assert.Equal( BarOf( File.ReadAllText( PageFile( "index.html" ) ) ), BarOf( body ) );
         AssertTheBar( BarOf( body ), expectBrandLink: false );
      } );
   }

   /// <summary>
   /// Every page the benchmark routes draw, with results published: the Vector search benchmark page, the full results page, the page of a set and the
   /// page of a run. Each has the bar, and the bar is the same on all of them.
   /// </summary>
   [Theory]
   [InlineData( "/benchmark" )]
   [InlineData( "/bench-results" )]
   [InlineData( "/bench-results/" + FOLDER )]
   [InlineData( "/bench-results/" + RUN )]
   public async Task EveryBenchmarkPage_HasTheBenchmarkLink( string path )
   {
      WriteFixtureRoot();
      await WithServerAsync( async client =>
      {
         using HttpResponseMessage response = await client.GetAsync( path );
         string body = await response.Content.ReadAsStringAsync();

         Assert.Equal( HttpStatusCode.OK, response.StatusCode );
         string bar = BarOf( body );
         Assert.Equal( BrandAndLinkOnly(), bar );
         AssertTheBar( bar, expectBrandLink: true );
      } );
   }

   /// <summary>
   /// The pages drawn when there is nothing to show (an empty results folder, no results folder, a folder with a file the reader refuses) carry the same bar.
   /// </summary>
   [Fact]
   public void ThePagesWithNothingToShow_HaveTheBenchmarkLink()
   {
      string empty = BenchResultsEndpoints.ListPageHtml( _root );
      string missing = BenchResultsEndpoints.ListPageHtml( Path.Combine( _root, "no-such-folder" ) );
      string none = BenchResultsEndpoints.BenchmarkPageHtml( _root );
      Directory.CreateDirectory( Path.Combine( _root, FOLDER ) );
      File.WriteAllText( Path.Combine( _root, FOLDER, "consolidated.json" ), "{ not json" );
      string bad = BenchResultsEndpoints.BenchmarkPageHtml( _root );

      Assert.All( new[] { empty, missing, none, bad }, page =>
      {
         Assert.Equal( BrandAndLinkOnly(), BarOf( page ) );
         AssertTheBar( BarOf( page ), expectBrandLink: true );
      } );
   }

   /// <summary>
   /// The words "Benchmark results" are still the heading of the full results page with nothing published (only the link in the bar changed), and the
   /// bar's link is not that heading.
   /// </summary>
   [Fact]
   public void TheHeading_OfTheFullResultsPageWithNothingPublished_IsUnchanged()
   {
      string empty = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "<main class=\"card bench\"><h1>Benchmark results</h1>", empty );
      Assert.DoesNotContain( "Benchmark results", BarOf( empty ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The bar of the page, which must be there exactly once.
   /// </summary>
   /// <param name="page">The page's HTML.</param>
   /// <returns>The bar's HTML, between the header tags.</returns>
   private static string BarOf( string page )
   {
      MatchCollection bars = BAR.Matches( page );
      Assert.Single( bars );
      return bars[0].Groups[1].Value;
   }

   /// <summary>
   /// What the bar of a page the server draws holds, between the header tags: the brand as a link to "/" and the one link at the right.
   /// </summary>
   /// <returns>The expected bar.</returns>
   private static string BrandAndLinkOnly()
   {
      return "<h1><a href=\"/\">" + BRAND + "</a></h1><a href=\"/benchmark\">Benchmark</a>";
   }

   /// <summary>
   /// The checks every bar passes: the brand, then a link "Benchmark" to /benchmark, once; no link in the bar goes to /bench-results and none says "Benchmark
   /// results".
   /// </summary>
   /// <param name="bar">The bar's HTML.</param>
   /// <param name="expectBrandLink">True when the brand is a link to "/" (the pages the server draws); false when it is plain text (the builder page).</param>
   private static void AssertTheBar( string bar, bool expectBrandLink )
   {
      MatchCollection links = LINKS.Matches( bar );
      Match benchmark = Assert.Single( links, l => l.Groups[1].Value == "/benchmark" );

      Assert.Equal( "Benchmark", benchmark.Groups[2].Value );
      Assert.Contains( LINK, bar );
      Assert.Contains( TEXT, bar );
      Assert.Equal( expectBrandLink ? 2 : 1, links.Count );
      Assert.DoesNotContain( "bench-results", bar );
      Assert.DoesNotContain( "Benchmark results", bar );
      Assert.True( bar.IndexOf( BRAND, StringComparison.Ordinal ) < bar.IndexOf( LINK, StringComparison.Ordinal ), "the brand is not at the left of the link" );
   }

   /// <summary>
   /// Puts the stored fixture's inputs in the results root: the set with its report, and the run the set uses.
   /// </summary>
   private void WriteFixtureRoot()
   {
      Directory.CreateDirectory( Path.Combine( _root, FOLDER ) );
      File.WriteAllText( Path.Combine( _root, FOLDER, "consolidated.json" ), BenchResultsFixtureFiles.Text( "bench-split.consolidated.json" ) );
      File.WriteAllText( Path.Combine( _root, FOLDER, "consolidated.md" ), BenchResultsFixtureFiles.Text( "bench-split.consolidated.md" ) );
      Directory.CreateDirectory( Path.Combine( _root, RUN ) );
      File.WriteAllText( Path.Combine( _root, RUN, "results.json" ), BenchResultsFixtureFiles.Text( "bench-split.results.json" ) );
   }

   /// <summary>
   /// The path of a file in the web app's wwwroot, found from the solution folder.
   /// </summary>
   /// <param name="name">File name inside wwwroot.</param>
   /// <returns>The full path.</returns>
   private static string PageFile( string name )
   {
      string folder = AppContext.BaseDirectory;
      while( !File.Exists( Path.Combine( folder, "GenericVectorBuilder.slnx" ) ) )
      {
         folder = Path.GetDirectoryName( folder ) ?? throw new InvalidOperationException( "Could not find the solution folder." );
      }

      return Path.Combine( folder, "src", "GenericVectorBuilder.Web", "wwwroot", name );
   }

   /// <summary>
   /// Runs the static files and the benchmark routes in a real web host on a free loopback port, with the web root of the app and the results root of this
   /// test, and hands a client to the body. The host and the client have timeouts, and the host is stopped when the body ends, whether it passes or fails.
   /// </summary>
   /// <param name="body">What to do with the client.</param>
   private async Task WithServerAsync( Func<HttpClient, Task> body )
   {
      WebApplicationBuilder builder = WebApplication.CreateBuilder( new WebApplicationOptions { WebRootPath = Path.GetDirectoryName( PageFile( "index.html" ) ) } );
      builder.WebHost.UseUrls( "http://127.0.0.1:0" );
      builder.Logging.ClearProviders();
      builder.Configuration["Gvb:BenchResultsPath"] = _root;
      await using WebApplication app = builder.Build();
      app.UseDefaultFiles();
      app.UseStaticFiles();
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
