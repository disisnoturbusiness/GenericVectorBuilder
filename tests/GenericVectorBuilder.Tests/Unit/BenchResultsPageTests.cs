using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The /bench-results pages read files from disk on request, so the path rules and the
/// HTML escaping are pinned here: nothing outside the results folder, no injected markup.
/// Also pins the page layout: summary first, per-engine detail folded closed, and the list page
/// falling back to the run list when the published numbers are missing.
/// Each test that needs files gets its own folder under the test output, removed afterwards.
/// </summary>
public class BenchResultsPageTests : IDisposable
{
   #region Data Members

   private const string ROOT = "/srv/bench-results";
   private const string RUN = "20261004-132735-eshoponweb";
   private const string OLD_PUBLISHED = "published-2026-10-04";
   private const string RESULTS = """{ "startedUtc": "2026-10-04T13:27:35Z", "pipeline": "eshoponweb", "rows": 524, "queries": "golden: 20 labelled", "queryCount": 20, "targets": [ { "name": "sql", "search": { "qps": { "8": 764.68 }, "p50Ms": 6.81, "recall": 1 } }, { "name": "mariadb", "search": { "qps": { "8": 4131.7 }, "p50Ms": 1.98, "recall": 1 } } ] }""";
   private const string REPORT = "# Vector engine benchmark: eshoponweb\n\n- Machine: linus\n\n| engine | QPS@8 |\n|---|---|\n| sql | 764.7 |\n\n## Details per target\n\n### sql\n\n- Index: exact\n\n### mariadb\n\n- Index: HNSW\n\n## Notes\n\n- Latency is client side.\n";
   private const string CONSOLIDATED = """{ "mariadb": { "runs": 3, "qps8": { "median": 4558.07, "min": 4131.7, "max": 4559 }, "p50": { "median": 1.98 }, "recall": { "median": 1 } }, "redis": { "runs": 3, "qps8": { "median": 2107, "min": 2011, "max": 3154 }, "p50": { "median": 1.51 }, "recall": { "median": 1 } } }""";

   private const string NEWER = """{ "settings": { "pipeline": "eshoponweb", "rows": 524, "dimension": 1024, "queryKind": "golden", "queryCount": 20, "top": 10, "governor": "performance", "cpuPartition": "client 0-1,4-5; engines 2-3,6-7", "buildConfiguration": "Release" }, "runs": [ { "name": "a" }, { "name": "b" }, { "name": "c" } ], "targetSummaries": [ { "name": "redis", "qps": { "1": { "median": 2700, "min": 2600, "max": 2800, "n": 3, "perRun": [ 2600, 2700, 2800 ] }, "8": { "median": 5000, "min": 4900, "max": 5100, "n": 3, "perRun": [ 4900, 5000, 5100 ] } }, "p50Ms": { "median": 0.35, "min": 0.34, "max": 0.36, "n": 3, "perRun": [ 0.34, 0.35, 0.36 ] }, "recall": { "median": 1, "min": 1, "max": 1, "n": 3, "perRun": [ 1, 1, 1 ] } } ], "flags": [] }""";

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public BenchResultsPageTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-page-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Normal run and file names resolve inside the root.
   /// </summary>
   [Theory]
   [InlineData( "20261004-132735-eshoponweb", null, "/srv/bench-results/20261004-132735-eshoponweb" )]
   [InlineData( "published-2026-10-04", "consolidated.json", "/srv/bench-results/published-2026-10-04/consolidated.json" )]
   public void Resolve_AcceptsNormalNames( string run, string? file, string expected )
   {
      Assert.Equal( expected, BenchResultsEndpoints.Resolve( ROOT, run, file ) );
   }

   /// <summary>
   /// Climbing, hidden, empty, slash and odd-character names are refused.
   /// </summary>
   [Theory]
   [InlineData( "..", null )]
   [InlineData( "run", ".." )]
   [InlineData( "a..b", null )]
   [InlineData( ".hidden", null )]
   [InlineData( "", null )]
   [InlineData( "run/../../etc", null )]
   [InlineData( "run", "../../etc/passwd" )]
   [InlineData( "run", "a b.md" )]
   [InlineData( "run%2f", null )]
   public void Resolve_RefusesUnsafeNames( string run, string? file )
   {
      Assert.Null( BenchResultsEndpoints.Resolve( ROOT, run, file ) );
   }

   /// <summary>
   /// Tables, headings, bullets and code render; markup inside the report is escaped.
   /// </summary>
   [Fact]
   public void RenderMarkdown_RendersTheReportSubsetAndEscapes()
   {
      string md = "# Title\n\n- Machine: `linus`\n\n| a | b |\n|---|---|\n| 1 | <script>x</script> |\n\n```\ncmd --x\n```\n**bold** line";
      string html = BenchResultsEndpoints.RenderMarkdown( md );

      Assert.Contains( "<h1>Title</h1>", html );
      Assert.Contains( "<li>Machine: <code>linus</code></li>", html );
      Assert.Contains( "<tr><th>a</th><th>b</th></tr>", html );
      Assert.Contains( "<td>&lt;script&gt;x&lt;/script&gt;</td>", html );
      Assert.DoesNotContain( "<script>", html );
      Assert.Contains( "<pre>cmd --x</pre>", html );
      Assert.Contains( "<p><strong>bold</strong> line</p>", html );
      Assert.DoesNotContain( "<td>---</td>", html );
   }

   /// <summary>
   /// The run page and the raw file route refuse every unsafe name, even when a matching
   /// folder or file exists outside the root.
   /// </summary>
   [Theory]
   [InlineData( ".." )]
   [InlineData( "a..b" )]
   [InlineData( ".hidden" )]
   [InlineData( "run/../../etc" )]
   [InlineData( "" )]
   public void Pages_RefuseUnsafeRunNames( string run )
   {
      WriteRun();
      Assert.Null( BenchResultsEndpoints.RunPageHtml( _root, run ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, run, "results.json" ) );
   }

   /// <summary>
   /// Raw files: only .md and .json inside a run folder, never a climbing name.
   /// </summary>
   [Fact]
   public void RawFilePath_ServesOnlyReportFilesInsideTheRoot()
   {
      WriteRun();
      File.WriteAllText( Path.Combine( _root, RUN, "notes.txt" ), "x" );

      Assert.NotNull( BenchResultsEndpoints.RawFilePath( _root, RUN, "results.json" ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, RUN, "notes.txt" ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, RUN, "../../etc/passwd" ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, RUN, "missing.json" ) );
      Assert.Null( BenchResultsEndpoints.RunPageHtml( _root, "no-such-run" ) );
   }

   /// <summary>
   /// Every engine's section under "Details per target" is its own closed details element, and
   /// the headings around it are not folded.
   /// </summary>
   [Fact]
   public void RenderMarkdown_FoldsEachTargetClosed()
   {
      string html = BenchResultsEndpoints.RenderMarkdown( "### outside\n\n## Details per target\n\n### sql\n\n- a\n\n### <x>\n\n- b\n\n## Notes\n\n- n" );

      Assert.Equal( 2, Regex.Matches( html, "<details" ).Count );
      Assert.Equal( 2, Regex.Matches( html, "</details>" ).Count );
      Assert.DoesNotContain( " open", html );
      Assert.Contains( "<h3>outside</h3>", html );
      Assert.Contains( "<details class=\"bench-target\"><summary>SQL Server 2025 <span class=\"muted\">sql</span></summary><ul><li>a</li></ul></details>", html );
      Assert.Contains( "<summary>&lt;x&gt;</summary>", html );
      Assert.Contains( "</details><h2>Notes</h2>", html );
   }

   /// <summary>
   /// A run page: title, then the summary for that run, then the full report with every
   /// target folded closed, then raw files.
   /// </summary>
   [Fact]
   public void RunPage_PutsSummaryFirstAndFoldsDetails()
   {
      WriteRun();
      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      int title = html.IndexOf( "<h1>Vector engine benchmark: eshoponweb</h1>", StringComparison.Ordinal );
      int lead = html.IndexOf( "<p class=\"bench-lead\">MariaDB was fastest in this run: 4,132 searches per second with 8 at once.</p>", StringComparison.Ordinal );
      int chart = html.IndexOf( "<svg class=\"bench-chart\"", StringComparison.Ordinal );
      int full = html.IndexOf( "<h2>Full results</h2>", StringComparison.Ordinal );
      int details = html.IndexOf( "<details class=\"bench-target\">", StringComparison.Ordinal );
      Assert.True( title >= 0 && lead > title && chart > lead && full > chart && details > full, html );
      Assert.Equal( 2, Regex.Matches( html, "<details class=\"bench-target\">" ).Count );
      Assert.DoesNotContain( "<details open", html );
      Assert.Contains( "Run started 4 Oct 2026, 13:27 UTC. Data: eShopOnWeb, 524 vectors. Queries: 20 labelled questions.", html );
      Assert.Contains( "href=\"/bench-results/20261004-132735-eshoponweb/results.json\"", html );
   }

   /// <summary>
   /// Without the published consolidated.json the list page is just the run list.
   /// </summary>
   [Fact]
   public void ListPage_FallsBackToRunListWithoutPublishedNumbers()
   {
      WriteRun();
      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "<h1>Benchmark results</h1>", html );
      Assert.DoesNotContain( "bench-chart", html );
      Assert.Contains( "<a href=\"/bench-results/20261004-132735-eshoponweb\" title=\"20261004-132735-eshoponweb\">4 Oct 2026, 13:27</a>", html );
      Assert.Contains( "<td>eShopOnWeb, 524 vectors</td><td class=\"n\">2</td><td>20 labelled questions</td>", html );
   }

   /// <summary>
   /// With the published numbers: headline, chart, table, the known problems, then all runs.
   /// </summary>
   [Fact]
   public void ListPage_LeadsWithSummaryThenCaveatsThenRuns()
   {
      WriteRun();
      WritePublished( CONSOLIDATED );
      string html = BenchResultsEndpoints.ListPageHtml( _root );

      int lead = html.IndexOf( "<p class=\"bench-lead\">MariaDB was fastest: 4,558 searches per second with 8 at once (median of 3 runs).</p>", StringComparison.Ordinal );
      int chart = html.IndexOf( "<svg class=\"bench-chart\"", StringComparison.Ordinal );
      int table = html.IndexOf( "<table class=\"bench-table\">", StringComparison.Ordinal );
      int caveats = html.IndexOf( "<aside class=\"bench-caveats\">", StringComparison.Ordinal );
      int runs = html.IndexOf( "<h2>All runs</h2>", StringComparison.Ordinal );
      Assert.True( lead >= 0 && chart > lead && table > chart && caveats > table && runs > caveats, html );
      Assert.Contains( "Qdrant (HNSW) never built its HNSW index at 524 points", html );
      Assert.Contains( "Redis holds everything in memory.", html );
      Assert.Contains( "<td>Published medians of 3 runs</td>", html );
   }

   /// <summary>
   /// A broken consolidated.json shows the error where the chart would be and still lists runs.
   /// </summary>
   [Fact]
   public void ListPage_ShowsUnreadableNumbersLoudly()
   {
      WriteRun();
      WritePublished( "{ not json" );
      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "<p class=\"errors\">The summary numbers could not be read:", html );
      Assert.DoesNotContain( "bench-chart", html );
      Assert.Contains( "4 Oct 2026, 13:27</a>", html );
   }

   /// <summary>
   /// Published folders are ordered newest first by the date in their name, and only the exact shape
   /// "published-yyyy-mm-dd" with a consolidated.json is a candidate: a folder with a suffix
   /// (published-2026-10-05b, published-2026-10-05-b), a blocked set (blocked-2026-10-05-v6), no valid
   /// date, no file or any other name is never served as the summary.
   /// </summary>
   [Fact]
   public void PublishedFolders_AreOnlyTheExactDatedNames_NewestFirst()
   {
      foreach( string name in new[] { "published-2026-10-04", "published-2026-10-05", "published-2026-10-05b", "published-2026-10-05-b", "blocked-2026-10-05-v6", "blocked-2026-10-05-v5", "published-2025-12-31", "published-2026-13-40", "published-2026-9-30", "published-latest", "published-2026-10-06", "unpublished-2026-10-07", "published-2026-10-08.old" } )
      {
         string folder = Path.Combine( _root, name );
         Directory.CreateDirectory( folder );
         if( name != "published-2026-10-06" )
         {
            File.WriteAllText( Path.Combine( folder, "consolidated.json" ), CONSOLIDATED );
         }
      }

      Directory.CreateDirectory( Path.Combine( _root, "20261007-120000-eshoponweb" ) );

      Assert.Equal( new[] { "published-2026-10-05", "published-2026-10-04", "published-2025-12-31" }, BenchResultsEndpoints.PublishedFolders( _root ) );
      Assert.Empty( BenchResultsEndpoints.PublishedFolders( Path.Combine( _root, "does-not-exist" ) ) );
   }

   /// <summary>
   /// A blocked set is not the summary: with only blocked-* and suffixed folders next to an older
   /// published one, the page shows the older published folder and none of the blocked numbers.
   /// </summary>
   [Fact]
   public void ListPage_NeverServesABlockedFolder()
   {
      WriteRun();
      WritePublished( CONSOLIDATED );
      WritePublishedFolder( "blocked-2026-10-05-v6", NEWER );
      WritePublishedFolder( "published-2026-10-05b", NEWER );

      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "Numbers from <a href=\"/bench-results/published-2026-10-04\">published-2026-10-04</a>.", html );
      Assert.Contains( "MariaDB was fastest", html );
      Assert.DoesNotContain( "Redis was fastest", html );
   }

   /// <summary>
   /// The summary page is built from the newest published folder, its own caveats follow it, and a
   /// line names the folder; the older folder's numbers and problems are not shown.
   /// </summary>
   [Fact]
   public void ListPage_ServesTheNewestPublishedFolder()
   {
      WriteRun();
      WritePublished( CONSOLIDATED );
      WritePublishedFolder( "published-2026-10-05", NEWER );

      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "Numbers from <a href=\"/bench-results/published-2026-10-05\">published-2026-10-05</a>.", html );
      Assert.Contains( "<p class=\"bench-lead\">Redis was fastest: 5,000 searches per second with 8 at once (median of 3 runs).</p>", html );
      Assert.Contains( "Data: eshoponweb pipeline, 524 vectors of 1024 dimensions, 20 labelled questions, top 10.", html );
      Assert.Contains( "These are medians of 3 runs.", html );
      Assert.Contains( "524 vectors measures the cost of each call", html );
      Assert.Contains( "(client 0-1,4-5; engines 2-3,6-7)", html );
      Assert.DoesNotContain( "MongoDB is not in this table", html );
      Assert.DoesNotContain( "MariaDB was fastest", html );
      Assert.DoesNotContain( "Qdrant (HNSW) never built its HNSW index", html );
      Assert.DoesNotContain( "could not be read", html );
   }

   /// <summary>
   /// When the newest published folder has no consolidated.json (a publish still being written) it
   /// is not a candidate, and the page shows the older folder without an error.
   /// </summary>
   [Fact]
   public void ListPage_SkipsANewerFolderWithNoFile()
   {
      WriteRun();
      WritePublished( CONSOLIDATED );
      Directory.CreateDirectory( Path.Combine( _root, "published-2026-10-05" ) );

      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "MariaDB was fastest", html );
      Assert.Contains( "Numbers from <a href=\"/bench-results/published-2026-10-04\">published-2026-10-04</a>.", html );
      Assert.DoesNotContain( "class=\"errors\"", html );
   }

   /// <summary>
   /// When the newest published file cannot be read, the page falls back to the older folder under
   /// a visible note that names the folder that failed and why; the older numbers are not passed
   /// off as the newest.
   /// </summary>
   [Fact]
   public void ListPage_FallsBackToTheOlderFolder_AndSaysTheNewestFailed()
   {
      WriteRun();
      WritePublished( CONSOLIDATED );
      WritePublishedFolder( "published-2026-10-05", "{ not json" );

      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "<p class=\"errors\">The newest published results, published-2026-10-05, could not be read (", html );
      Assert.Contains( "so the page shows the older published-2026-10-04.", html );
      Assert.Contains( "MariaDB was fastest", html );
      Assert.Contains( "Qdrant (HNSW) never built its HNSW index at 524 points", html );
   }

   /// <summary>
   /// A published folder's own page describes its data from its own settings.
   /// </summary>
   [Fact]
   public void RunPage_OfAPublishedFolder_DescribesItsOwnData()
   {
      WritePublishedFolder( "published-2026-10-05", NEWER );
      WritePublished( CONSOLIDATED );

      string newer = BenchResultsEndpoints.RunPageHtml( _root, "published-2026-10-05" )!;
      string older = BenchResultsEndpoints.RunPageHtml( _root, "published-2026-10-04" )!;

      Assert.Contains( "Data: eshoponweb pipeline, 524 vectors of 1024 dimensions, 20 labelled questions, top 10.", newer );
      Assert.Contains( "Data: eShopOnWeb, 254 C# files cut into 524 chunks", older );
   }

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

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Writes one run folder with results.json and results.md.
   /// </summary>
   private void WriteRun()
   {
      string folder = Path.Combine( _root, RUN );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "results.json" ), RESULTS );
      File.WriteAllText( Path.Combine( folder, "results.md" ), REPORT );
   }

   /// <summary>
   /// Writes the 4 Oct published folder's consolidated.json.
   /// </summary>
   /// <param name="json">File text.</param>
   private void WritePublished( string json )
   {
      WritePublishedFolder( OLD_PUBLISHED, json );
   }

   /// <summary>
   /// Writes a published folder's consolidated.json.
   /// </summary>
   /// <param name="name">Folder name, e.g. "published-2026-10-05".</param>
   /// <param name="json">File text.</param>
   private void WritePublishedFolder( string name, string json )
   {
      string folder = Path.Combine( _root, name );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "consolidated.json" ), json );
   }

   #endregion Private Methods
}
