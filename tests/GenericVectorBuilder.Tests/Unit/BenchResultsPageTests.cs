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
   private const string RESULTS = """{ "startedUtc": "2026-10-04T13:27:35Z", "pipeline": "eshoponweb", "rows": 524, "queries": "golden: 20 labelled", "queryCount": 20, "targets": [ { "name": "sql", "search": { "qps": { "8": 764.68 }, "p50Ms": 6.81, "recall": 1 } }, { "name": "mariadb", "search": { "qps": { "8": 4131.7 }, "p50Ms": 1.98, "recall": 1 } } ] }""";
   private const string REPORT = "# Vector engine benchmark: eshoponweb\n\n- Machine: linus\n\n| engine | QPS@8 |\n|---|---|\n| sql | 764.7 |\n\n## Details per target\n\n### sql\n\n- Index: exact\n\n### mariadb\n\n- Index: HNSW\n\n## Notes\n\n- Latency is client side.\n";
   private const string CONSOLIDATED = """{ "mariadb": { "runs": 3, "qps8": { "median": 4558.07, "min": 4131.7, "max": 4559 }, "p50": { "median": 1.98 }, "recall": { "median": 1 } }, "redis": { "runs": 3, "qps8": { "median": 2107, "min": 2011, "max": 3154 }, "p50": { "median": 1.51 }, "recall": { "median": 1 } } }""";

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
   /// Writes the published folder's consolidated.json.
   /// </summary>
   /// <param name="json">File text.</param>
   private void WritePublished( string json )
   {
      string folder = Path.Combine( _root, BenchResultsEndpoints.PUBLISHED_FOLDER );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "consolidated.json" ), json );
   }

   #endregion Private Methods
}
