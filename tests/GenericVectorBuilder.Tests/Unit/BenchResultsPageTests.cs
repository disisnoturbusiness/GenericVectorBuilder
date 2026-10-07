using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The /bench-results pages read files from disk on request, so the path rules and the HTML escaping are pinned
/// here: nothing outside the results folder, no injected markup.
/// Also pins the page layout: the summary of the newest published folder first, then the runs; each folder kind at
/// its own address with its own banner; an older-format file as a notice and never as a result; the runs a set uses
/// marked in the run list and on the run page; and the sentences that tie the client CPU figure to the latency left
/// out of a run report's copy.
/// Each test that needs files gets its own folder under the test output, removed afterwards.
/// </summary>
public class BenchResultsPageTests : IDisposable
{
   #region Data Members

   private const string ROOT = "/srv/bench-results";
   private const string RUN = "20261006-130619-eshoponweb";
   private const string PUBLISHED = "published-2026-10-07";
   private const string HEADLINE = "Every engine below Redis, which holds its data in memory, took at least 1.35 times as long per search in each session.";

   private const string REPORT = "# Vector engine benchmark: eshoponweb\n\n- Machine: linus\n- Client CPU per search is the CPU time the test's .NET client itself used for each search, measured in the same pass as the figure beside it. Where it is close to the latency, the client library is a large part of what is measured. For an embedded engine (DuckDB, sqlite-vec) the engine runs inside the client process, so its figure is the engine's own CPU time, not client overhead.\n\n| engine | QPS@8 |\n|---|---|\n| sql | 764.7 |\n\n## Details per target\n\n### sql\n\n- Index: exact\n\n### mariadb\n\n- Index: HNSW\n\n## Notes\n\n- Latency is client side.\n";

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
   /// A pipe the report escapes inside a cell stays in that cell, and the cells after it stay under their own columns.
   /// </summary>
   [Fact]
   public void RenderMarkdown_KeepsAnEscapedPipeInsideItsCell()
   {
      string html = BenchResultsEndpoints.RenderMarkdown( BenchResultsV8Fixture.REPORT_MD );

      Assert.Contains( "<tr><td>sqlitevec</td><td>PRAGMA journal_mode=WAL | USE TEMP B-TREE</td><td>after</td></tr>", html );
      Assert.Contains( "<tr><th>engine</th><th>durability</th><th>next</th></tr>", html );
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
   /// Raw files: only .md and .json inside a run folder, or one folder below it, never a climbing name and never a third level.
   /// </summary>
   [Fact]
   public void RawFilePath_ServesOnlyReportFilesInsideTheRoot()
   {
      WriteRun();
      File.WriteAllText( Path.Combine( _root, RUN, "notes.txt" ), "x" );
      Directory.CreateDirectory( Path.Combine( _root, RUN, "observer", "deep" ) );
      File.WriteAllText( Path.Combine( _root, RUN, "observer", "summary.json" ), "{}" );
      File.WriteAllText( Path.Combine( _root, RUN, "observer", "deep", "x.json" ), "{}" );
      File.WriteAllText( Path.Combine( _root, RUN, "observer", "x.txt" ), "x" );

      Assert.NotNull( BenchResultsEndpoints.RawFilePath( _root, RUN, "results.json" ) );
      Assert.NotNull( BenchResultsEndpoints.RawFilePath( _root, RUN, "observer/summary.json" ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, RUN, "notes.txt" ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, RUN, "observer/x.txt" ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, RUN, "observer/deep/x.json" ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, RUN, "../../etc/passwd" ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, RUN, "observer/../results.json" ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, RUN, "missing.json" ) );
      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, "..", "observer/summary.json" ) );
      Assert.Null( BenchResultsEndpoints.RunPageHtml( _root, "no-such-run" ) );
   }

   /// <summary>
   /// A raw file over the size limit is refused.
   /// </summary>
   [Fact]
   public void RawFilePath_RefusesAFileOverTheLimit()
   {
      WriteRun();
      File.WriteAllBytes( Path.Combine( _root, RUN, "big.json" ), new byte[BenchRunList.MAX_FILE_BYTES + 1] );

      Assert.Null( BenchResultsEndpoints.RawFilePath( _root, RUN, "big.json" ) );
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
   /// A run page: title, then the run's conditions and table, then the full report with every target folded closed, then raw files.
   /// </summary>
   [Fact]
   public void RunPage_PutsConditionsAndTableFirst_AndFoldsDetails()
   {
      WriteRun();
      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      int title = html.IndexOf( "<h1>Vector engine benchmark: eshoponweb</h1>", StringComparison.Ordinal );
      int data = html.IndexOf( "Run started 6 Oct 2026, 13:06 UTC. Data: eShopOnWeb, 524 vectors. Queries: 20 labelled questions.", StringComparison.Ordinal );
      int table = html.IndexOf( "<table class=\"bench-table\">", StringComparison.Ordinal );
      int full = html.IndexOf( "<h2>Full results</h2>", StringComparison.Ordinal );
      int details = html.IndexOf( "<details class=\"bench-target\">", StringComparison.Ordinal );
      Assert.True( title >= 0 && data > title && table > data && full > table && details > full, html );
      Assert.Equal( 2, Regex.Matches( html, "<details class=\"bench-target\">" ).Count );
      Assert.DoesNotContain( "<details open", html );
      Assert.Contains( "href=\"/bench-results/20261006-130619-eshoponweb/results.json\"", html );
      Assert.DoesNotContain( "bench-lead", html );
   }

   /// <summary>
   /// Without a published folder the list page is just the run list.
   /// </summary>
   [Fact]
   public void ListPage_FallsBackToRunListWithoutPublishedNumbers()
   {
      WriteRun();
      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "<h1>Benchmark results</h1>", html );
      Assert.DoesNotContain( "data-metric=", html );
      Assert.Contains( "<a href=\"/bench-results/20261006-130619-eshoponweb\" title=\"20261006-130619-eshoponweb\">6 Oct 2026, 13:06</a>", html );
      Assert.Contains( "<td>eShopOnWeb, 524 vectors</td><td class=\"n\">4</td><td>20 labelled questions</td>", html );
   }

   /// <summary>
   /// With a published set: the headline, the tables, the thresholds and the rest of the page, then all runs, with the folder named and the
   /// runs it uses marked.
   /// </summary>
   [Fact]
   public void ListPage_LeadsWithTheSummary_ThenAllRuns_WithTheRunsMarked()
   {
      WriteRun();
      WriteSet( PUBLISHED );
      string html = BenchResultsEndpoints.ListPageHtml( _root );

      int lead = html.IndexOf( $"<p class=\"bench-lead\">{HEADLINE}</p>", StringComparison.Ordinal );
      int table = html.IndexOf( "<table class=\"bench-table\">", StringComparison.Ordinal );
      int runs = html.IndexOf( "<h2>All runs</h2>", StringComparison.Ordinal );
      Assert.True( lead >= 0 && table > lead && runs > table, html );
      Assert.Contains( "Numbers from <a href=\"/bench-results/published-2026-10-07\">published-2026-10-07</a>.", html );
      Assert.Contains( "<td>Published medians of 6 runs</td>", html );
      Assert.Contains( "<a href=\"/bench-results/published-2026-10-07\">published-2026-10-07</a> (v7, basis)", html );
      Assert.DoesNotContain( "Not final", html );
      Assert.DoesNotContain( "Read before quoting", html );
   }

   /// <summary>
   /// A broken consolidated.json shows the error where the tables would be and still lists runs: bad JSON, a file the reader refuses, and
   /// a shape nobody knows.
   /// </summary>
   [Theory]
   [InlineData( "{ not json" )]
   [InlineData( "{\"a\": 1}" )]
   [InlineData( "{\"metrics\": [], \"sessions\": [], \"sentences\": []}" )]
   public void ListPage_ShowsUnreadableNumbersLoudly( string json )
   {
      WriteRun();
      WriteFolder( PUBLISHED, json );
      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( $"<p class=\"errors\">{BenchLegends.UNREADABLE}:", html );
      Assert.DoesNotContain( "data-metric=", html );
      Assert.Contains( "6 Oct 2026, 13:06</a>", html );
   }

   /// <summary>
   /// Published folders are ordered newest first by the date in their name, and only the exact shape
   /// "published-yyyy-mm-dd" with a consolidated.json is a candidate: a folder with a suffix, a candidate, withdrawn or blocked
   /// set, no valid date, no file or any other name is never served as the summary.
   /// </summary>
   [Fact]
   public void PublishedFolders_AreOnlyTheExactDatedNames_NewestFirst()
   {
      foreach( string name in new[] { "published-2026-10-04", "published-2026-10-05", "published-2026-10-05b", "published-2026-10-05-b", "blocked-2026-10-05-v6", "candidate-v8", "withdrawn-2026-10-04", "published-2025-12-31", "published-2026-13-40", "published-2026-9-30", "published-latest", "published-2026-10-06", "unpublished-2026-10-07", "published-2026-10-08.old" } )
      {
         string folder = Path.Combine( _root, name );
         Directory.CreateDirectory( folder );
         if( name != "published-2026-10-06" )
         {
            File.WriteAllText( Path.Combine( folder, "consolidated.json" ), "{}" );
         }
      }

      Directory.CreateDirectory( Path.Combine( _root, "20261007-120000-eshoponweb" ) );

      Assert.Equal( new[] { "published-2026-10-05", "published-2026-10-04", "published-2025-12-31" }, BenchResultsEndpoints.PublishedFolders( _root ) );
      Assert.Empty( BenchResultsEndpoints.PublishedFolders( Path.Combine( _root, "does-not-exist" ) ) );
   }

   /// <summary>
   /// A candidate, a withdrawn set and a blocked set are not the summary: next to an older published folder the page shows the older
   /// published folder and none of the others' headlines.
   /// </summary>
   [Fact]
   public void ListPage_NeverServesACandidateAWithdrawnOrABlockedFolder()
   {
      WriteSet( "published-2026-10-04", "The older headline stands." );
      WriteSet( "candidate-v8", "The candidate headline." );
      WriteSet( "withdrawn-2026-10-05", "The withdrawn headline." );
      WriteSet( "blocked-2026-10-06-v7", "The blocked headline." );
      WriteSet( "published-2026-10-05b", "The suffixed headline." );

      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "Numbers from <a href=\"/bench-results/published-2026-10-04\">published-2026-10-04</a>.", html );
      Assert.Contains( "The older headline stands.", html );
      Assert.DoesNotContain( "The candidate headline.", html );
      Assert.DoesNotContain( "The withdrawn headline.", html );
      Assert.DoesNotContain( "The blocked headline.", html );
      Assert.DoesNotContain( "The suffixed headline.", html );
   }

   /// <summary>
   /// The summary page is built from the newest published folder and a line names the folder; the older folder's headline is not shown.
   /// </summary>
   [Fact]
   public void ListPage_ServesTheNewestPublishedFolder()
   {
      WriteSet( "published-2026-10-04", "The older headline." );
      WriteSet( PUBLISHED, "The newer headline." );

      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "Numbers from <a href=\"/bench-results/published-2026-10-07\">published-2026-10-07</a>.", html );
      Assert.Contains( "The newer headline.", html );
      Assert.DoesNotContain( "The older headline.", html );
   }

   /// <summary>
   /// When the newest published folder has no consolidated.json (a publish still being written) it is not a candidate for the summary, and
   /// the page shows the older folder without an error.
   /// </summary>
   [Fact]
   public void ListPage_SkipsANewerFolderWithNoFile()
   {
      WriteSet( "published-2026-10-04", "The older headline." );
      Directory.CreateDirectory( Path.Combine( _root, PUBLISHED ) );

      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "The older headline.", html );
      Assert.Contains( "Numbers from <a href=\"/bench-results/published-2026-10-04\">published-2026-10-04</a>.", html );
      Assert.DoesNotContain( "class=\"errors\"", html );
   }

   /// <summary>
   /// When the newest published file cannot be read, the page falls back to the older folder under a visible note that names the folder that
   /// failed and why; the older numbers are not passed off as the newest.
   /// </summary>
   [Fact]
   public void ListPage_FallsBackToTheOlderFolder_AndSaysTheNewestFailed()
   {
      WriteSet( "published-2026-10-04", "The older headline." );
      WriteFolder( PUBLISHED, "{ not json" );

      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "<p class=\"errors\">The newest published results, published-2026-10-07, could not be read (", html );
      Assert.Contains( "so the page shows the older published-2026-10-04.", html );
      Assert.Contains( "The older headline.", html );
   }

   /// <summary>
   /// A published file in a shape older than v8 (the first published shape, or the shape before v8) is shown as the older-format notice with a link to
   /// its raw file, with no headline and no table of results.
   /// </summary>
   [Theory]
   [InlineData( "old" )]
   [InlineData( "report" )]
   public void AnOlderShape_IsShownAsANotice_NeverAsAResult( string which )
   {
      WriteFolder( "published-2026-10-04", which == "old" ? BenchResultsFixtures.OLD_PUBLISHED_JSON : BenchResultsFixtures.REPORT_SHAPE_JSON );
      string list = BenchResultsEndpoints.ListPageHtml( _root );
      string folder = BenchResultsEndpoints.RunPageHtml( _root, "published-2026-10-04" )!;

      foreach( string html in new[] { list, folder } )
      {
         Assert.Contains( BenchLegends.OLDER_FORMAT, html );
         Assert.Contains( "href=\"/bench-results/published-2026-10-04/consolidated.json\"", html );
         Assert.DoesNotContain( "bench-lead", html );
         Assert.DoesNotContain( "data-metric=", html );
         Assert.DoesNotContain( "was fastest", html );
      }
   }

   /// <summary>
   /// The "All runs" list names a folder of medians for what its name says it is: a published folder is published medians, a candidate says it is
   /// not published, a withdrawn one says it is not the current result, a blocked one says it is marked blocked, a file of an older
   /// shape says so, and any other name says it is not a published set.
   /// </summary>
   [Fact]
   public void RunList_LabelsAFolderOfMedians_ByItsNameAndShape()
   {
      WriteSet( PUBLISHED );
      WriteSet( "candidate-v8" );
      WriteSet( "withdrawn-2026-10-04" );
      WriteSet( "blocked-2026-10-06-v7" );
      WriteSet( "consolidated-scratch" );
      WriteFolder( "published-2026-10-03", BenchResultsFixtures.OLD_PUBLISHED_JSON );

      string html = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "<td>Published medians of 6 runs</td>", html );
      Assert.Contains( "<td>Candidate medians of 6 runs (not published)</td>", html );
      Assert.Contains( "<td>Withdrawn medians of 6 runs (not the current result)</td>", html );
      Assert.Contains( "<td>Blocked medians of 6 runs (marked blocked; not published)</td>", html );
      Assert.Contains( "<td>Medians of 6 runs (not a published set)</td>", html );
      Assert.Contains( "<td>Medians, older format (not shown as a result)</td>", html );
      Assert.DoesNotContain( "Published medians, older format", html );
   }

   /// <summary>
   /// The page of a candidate says at the top that it is a candidate, a withdrawn one that it was withdrawn, a blocked one that it was blocked;
   /// the page of a published folder and of a run say nothing of the kind.
   /// </summary>
   [Fact]
   public void SetPages_CarryTheBannerOfTheirKind()
   {
      WriteSet( "candidate-v8" );
      WriteSet( "withdrawn-2026-10-04" );
      WriteSet( "blocked-2026-10-06-v7" );
      WriteSet( PUBLISHED );
      WriteRun();

      Assert.Contains( $"<p class=\"errors bench-banner\">{BenchLegends.BANNER_CANDIDATE}</p>", BenchResultsEndpoints.RunPageHtml( _root, "candidate-v8" ) );
      Assert.Contains( $"<p class=\"errors bench-banner\">{BenchLegends.BANNER_WITHDRAWN}</p>", BenchResultsEndpoints.RunPageHtml( _root, "withdrawn-2026-10-04" ) );
      Assert.Contains( "The summary page does not use it", BenchResultsEndpoints.RunPageHtml( _root, "blocked-2026-10-06-v7" ) );
      foreach( string name in new[] { PUBLISHED, RUN } )
      {
         string html = BenchResultsEndpoints.RunPageHtml( _root, name )!;
         Assert.DoesNotContain( "bench-banner", html );
      }
   }

   /// <summary>
   /// A set's own page draws the whole v8 page from its consolidated.json, and puts its Markdown report in a closed details whose table keeps an
   /// escaped pipe inside its cell.
   /// </summary>
   [Fact]
   public void ASetPage_DrawsTheV8Page_AndFoldsItsReport()
   {
      WriteSet( "candidate-v8" );
      string html = BenchResultsEndpoints.RunPageHtml( _root, "candidate-v8" )!;

      Assert.Contains( $"<p class=\"bench-lead\">{HEADLINE}</p>", html );
      Assert.Contains( "<h1>Vector engine benchmark: consolidated</h1>", html );
      Assert.Contains( $"<details class=\"bench-target\"><summary>{BenchLegends.H_REPORT_TEXT}</summary>", html );
      Assert.Contains( "<td>PRAGMA journal_mode=WAL | USE TEMP B-TREE</td><td>after</td>", html );
      Assert.DoesNotContain( "<details open", html );
   }

   /// <summary>
   /// A set page whose file the reader refuses, or that nobody can parse, shows an error block and the report, and does not fail.
   /// </summary>
   [Theory]
   [InlineData( "{ not json" )]
   [InlineData( "{\"a\": 1}" )]
   public void ASetPage_WithAnUnreadableFile_ShowsAnErrorBlock( string json )
   {
      WriteFolder( "candidate-v8", json );
      File.WriteAllText( Path.Combine( _root, "candidate-v8", "consolidated.md" ), BenchResultsV8Fixture.REPORT_MD );

      string html = BenchResultsEndpoints.RunPageHtml( _root, "candidate-v8" )!;

      Assert.Contains( $"<p class=\"errors\">{BenchLegends.UNREADABLE}:", html );
      Assert.Contains( "Vector engine benchmark: consolidated", html );
   }

   /// <summary>
   /// A report over the size limit gives an error line and not an exception.
   /// </summary>
   [Fact]
   public void AReportOverTheLimit_GivesAnErrorLine()
   {
      WriteRun();
      File.WriteAllBytes( Path.Combine( _root, RUN, "results.md" ), new byte[BenchRunList.MAX_FILE_BYTES + 1] );

      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.Contains( "The report could not be read:", html );
      Assert.Contains( "<table class=\"bench-table\">", html );
   }

   /// <summary>
   /// A run used by a published set and by a blocked set is marked with the published set only; a run only a blocked set uses is marked
   /// blocked; a basis run is marked as such.
   /// </summary>
   [Fact]
   public void RunMarks_PublishedWinsOverBlocked_AndBasisRunsAreMarked()
   {
      WriteRun();
      WriteSet( PUBLISHED );
      WriteSet( "blocked-2026-10-06-v7" );
      WriteBlockedOnlyRun( "20261005-111111-eshoponweb" );
      Directory.CreateDirectory( Path.Combine( _root, "20261005-023459-eshoponweb" ) );
      File.WriteAllText( Path.Combine( _root, "20261005-023459-eshoponweb", "results.json" ), BenchResultsFixtures.RUN_RESULTS_V8_JSON );
      IReadOnlyList<BenchFolderFacts> facts = BenchRunUse.Scan( _root );

      Assert.Equal( new[] { ( PUBLISHED, "v7" ), ( PUBLISHED, "basis" ) }, BenchRunUse.MarksFor( facts, RUN ).Select( m => ( m.Folder.Folder, m.Role ) ) );
      Assert.Equal( new[] { ( "blocked-2026-10-06-v7", "runs" ) }, BenchRunUse.MarksFor( facts, "20261005-111111-eshoponweb" ).Select( m => ( m.Folder.Folder, m.Role ) ) );
      Assert.Equal( new[] { ( PUBLISHED, "basis v5" ) }, BenchRunUse.MarksFor( facts, "20261005-023459-eshoponweb" ).Select( m => ( m.Folder.Folder, m.Role ) ) );
      string list = BenchResultsEndpoints.ListPageHtml( _root );
      Assert.Contains( "<a href=\"/bench-results/published-2026-10-07\">published-2026-10-07</a> (v7, basis)", list );
      Assert.Contains( "<a href=\"/bench-results/published-2026-10-07\">published-2026-10-07</a> (basis v5)", list );
      Assert.Contains( "<a href=\"/bench-results/blocked-2026-10-06-v7\">blocked-2026-10-06-v7</a> (runs)", list );
   }

   /// <summary>
   /// The page of a run a published set uses says which folders use it, prints the published set's reuse sentence with its sources, and shows a
   /// banner for a clock warning the set dropped, with the recomputed median figures.
   /// </summary>
   [Fact]
   public void RunPage_OfAUsedRun_SaysWhoUsesIt_TheReuseNote_AndTheDroppedWarning()
   {
      WriteRun();
      WriteSet( PUBLISHED );
      WriteSet( "blocked-2026-10-06-v7" );
      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.Contains( $"{BenchLegends.USED_BY}: <a href=\"/bench-results/published-2026-10-07\">published-2026-10-07</a> (v7, basis)", html );
      Assert.DoesNotContain( "href=\"/bench-results/blocked-2026-10-06-v7\"", html );
      Assert.Contains( "The runs of the first session were measured before this set was built and are read where they were written.", html );
      Assert.Contains( $"<a href=\"/bench-results/published-2026-10-07\">published-2026-10-07</a> {System.Net.WebUtility.HtmlEncode( BenchLegends.DROPPED_WARNING )}", html );
      Assert.Contains( "<li>Warning as recorded: clock off its pinned value during oracle default@8: the engine CPUs averaged 3121 MHz</li>", html );
      Assert.Contains( "<li>Median MHz of the engine CPUs: 3492</li>", html );
      Assert.Contains( "<li>Median MHz of the client CPUs: 3492</li>", html );
   }

   /// <summary>
   /// The page of a run prints, under an engine's name, the note a set that uses the run gives that engine (for example that it holds its data in
   /// memory), and prints no note on the page of a run no set uses.
   /// </summary>
   [Fact]
   public void RunPage_PrintsTheNoteAUsingSetGivesAnEngine()
   {
      WriteRun();
      string before = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;
      WriteSet( PUBLISHED );
      string after = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.DoesNotContain( BenchResultsV8Fixture.REDIS_NOTE, before );
      Assert.Contains( $"<td>Redis<br><small class=\"muted bench-note\">{BenchResultsV8Fixture.REDIS_NOTE}</small><details class=\"bench-sources muted\"><summary>sources</summary><ul>", after );
      Assert.Contains( "<li>file <code>deploy/engines/redis.compose.yaml#--appendonly no</code> = --appendonly no</li>", after );
      Assert.Single( Regex.Matches( after, Regex.Escape( BenchResultsV8Fixture.REDIS_NOTE ) ) );
   }

   /// <summary>
   /// Two folders that dropped the same warning for a run give one banner that names both folders, not one banner each.
   /// </summary>
   [Fact]
   public void RunPage_GivesOneBannerForTheSameDroppedWarning_FromTwoFolders()
   {
      WriteRun();
      WriteSet( PUBLISHED );
      WriteSet( "candidate-v8" );
      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.Single( Regex.Matches( html, "Warning as recorded:" ) );
      Assert.Contains( $"<a href=\"/bench-results/candidate-v8\">candidate-v8</a>, <a href=\"/bench-results/published-2026-10-07\">published-2026-10-07</a> {System.Net.WebUtility.HtmlEncode( BenchLegends.DROPPED_WARNING )}", html );
   }

   /// <summary>
   /// A run no set uses carries no used-by line and no banner.
   /// </summary>
   [Fact]
   public void RunPage_OfAnUnusedRun_HasNoUsedByLine()
   {
      WriteRun();
      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.DoesNotContain( BenchLegends.USED_BY, html );
      Assert.DoesNotContain( BenchLegends.DROPPED_WARNING, html );
   }

   /// <summary>
   /// The sentences that tie the client CPU figure to the latency are left out of a run report's copy on its page, a note says so, and the raw file
   /// still holds them and is linked; the sentence before them stays. This holds for a run a published set uses and for one no set uses.
   /// </summary>
   [Theory]
   [InlineData( true )]
   [InlineData( false )]
   public void RunPage_LeavesOutTheLatencySplitSentences_AndSaysSo( bool used )
   {
      WriteRun();
      if( used )
      {
         WriteSet( PUBLISHED );
      }

      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.DoesNotContain( "Where it is close to the latency", html );
      Assert.DoesNotContain( "client library is a large part", html );
      Assert.DoesNotContain( "not client overhead", html );
      Assert.Contains( "measured in the same pass as the figure beside it.", html );
      Assert.Contains( BenchLegends.STRIPPED, html.Replace( "&#39;", "'" ) );
      Assert.Contains( "href=\"/bench-results/20261006-130619-eshoponweb/results.md\"", html );
      Assert.Contains( "Where it is close to the latency", File.ReadAllText( BenchResultsEndpoints.RawFilePath( _root, RUN, "results.md" )! ) );
   }

   /// <summary>
   /// A report that has no such sentence gets no note, and the strip finds both variants of the sentence as the reports of earlier runs carry them.
   /// </summary>
   [Fact]
   public void Strip_CountsEachSentence_AndLeavesOtherReportsAlone()
   {
      string shortForm = "A. Where it is close to the latency, the client library is a large part of what is measured.";
      string clean = BenchReportStrip.Apply( shortForm, out int one );
      string full = BenchReportStrip.Apply( REPORT, out int two );
      BenchReportStrip.Apply( "Nothing to strip here.", out int none );

      Assert.Equal( "A.", clean );
      Assert.Equal( 1, one );
      Assert.Equal( 2, two );
      Assert.Equal( 0, none );
      Assert.DoesNotContain( "latency", full.Split( '\n' )[3] );
   }

   /// <summary>
   /// The run page lists the files one folder below it, so a file such as observer/summary.json can be reached from the page.
   /// </summary>
   [Fact]
   public void RunPage_ListsFilesOneFolderDown()
   {
      WriteRun();
      Directory.CreateDirectory( Path.Combine( _root, RUN, "observer" ) );
      File.WriteAllText( Path.Combine( _root, RUN, "observer", "summary.json" ), "{}" );

      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.Contains( "<li><a href=\"/bench-results/20261006-130619-eshoponweb/observer/summary.json\">observer/summary.json</a></li>", html );
   }

   /// <summary>
   /// No page of any folder kind prints a claim of the earlier page: "was fastest", "Not final", a band, "about 2%", "tied" as a word.
   /// </summary>
   [Fact]
   public void NoPage_PrintsAnEarlierClaim()
   {
      WriteRun();
      WriteSet( PUBLISHED );
      WriteSet( "candidate-v8" );
      WriteSet( "withdrawn-2026-10-04" );
      WriteFolder( "blocked-2026-10-05-v6", BenchResultsFixtures.REPORT_SHAPE_JSON );
      var pages = new List<string> { BenchResultsEndpoints.ListPageHtml( _root ) };
      pages.AddRange( new[] { PUBLISHED, "candidate-v8", "withdrawn-2026-10-04", "blocked-2026-10-05-v6", RUN }.Select( n => BenchResultsEndpoints.RunPageHtml( _root, n )! ) );

      foreach( string html in pages )
      {
         string text = System.Net.WebUtility.HtmlDecode( Regex.Replace( html, "<[^>]+>", " " ) );
         Assert.DoesNotMatch( new Regex( @"was fastest|fastest together|Not final|about 2%|\bbands?\b|\btied\b|Read before quoting", RegexOptions.IgnoreCase ), text );
         Assert.DoesNotContain( "bench-chart", html );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Writes the first v7 run as a folder with results.json and results.md.
   /// </summary>
   private void WriteRun()
   {
      string folder = Path.Combine( _root, RUN );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "results.json" ), BenchResultsFixtures.RUN_RESULTS_V8_JSON );
      File.WriteAllText( Path.Combine( folder, "results.md" ), REPORT );
   }

   /// <summary>
   /// Writes a consolidated folder holding the v8 fixture and its report, with a headline of the test's choice.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <param name="headline">The headline sentence, or null for the fixture's own.</param>
   private void WriteSet( string name, string? headline = null )
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         if( headline != null )
         {
            root["sentences"]![0]!["text"] = headline;
         }
      } );
      WriteFolder( name, json );
      File.WriteAllText( Path.Combine( _root, name, "consolidated.md" ), BenchResultsV8Fixture.REPORT_MD );
   }

   /// <summary>
   /// Writes a folder holding only a consolidated.json.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <param name="json">File text.</param>
   private void WriteFolder( string name, string json )
   {
      string folder = Path.Combine( _root, name );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "consolidated.json" ), json );
   }

   /// <summary>
   /// Writes a run folder that only the blocked set (in the shape before v8) names.
   /// </summary>
   /// <param name="run">The run's folder name.</param>
   private void WriteBlockedOnlyRun( string run )
   {
      Directory.CreateDirectory( Path.Combine( _root, run ) );
      File.WriteAllText( Path.Combine( _root, run, "results.json" ), BenchResultsFixtures.RUN_RESULTS_V8_JSON );
      string json = new JsonObject
      {
         ["settings"] = new JsonObject { ["rows"] = 524 },
         ["runs"] = new JsonArray( new JsonObject { ["name"] = run } ),
         ["targetSummaries"] = new JsonArray(),
      }.ToJsonString();
      WriteFolder( "blocked-2026-10-06-v7", json );
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
