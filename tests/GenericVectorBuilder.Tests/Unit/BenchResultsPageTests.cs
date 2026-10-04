using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The /bench-results pages read files from disk on request, so the path rules and the
/// HTML escaping are pinned here: nothing outside the results folder, no injected markup.
/// </summary>
public class BenchResultsPageTests
{
   #region Data Members

   private const string ROOT = "/srv/bench-results";

   #endregion Data Members

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

   #endregion Public Methods
}
