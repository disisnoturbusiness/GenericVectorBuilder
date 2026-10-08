using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The web side of the v8i report-layer fixes: a correction of kind "unbacked" (a recorded clause the set drops because no saved source backs it) is read and labelled on its own terms, and the real
/// run pages carry the framing correction (B2) and the MariaDB correction (B3) at the statement, for every run that holds them.
/// Why a class of its own: each test names the verdict item it proves, and the fixture is the shared v8 file with one change per test.
/// </summary>
public class BenchResultsV8iFixTests : IDisposable
{
   #region Data Members

   private const string RUN = "20261006-130619-eshoponweb";
   private const string PUBLISHED = "published-2026-10-07";
   private const string REAL_RUNS = "/home/dan/ForClaude/GenericVectorBuilder/bench-results";
   private const string CLIENT_STATEMENT = "Measured end to end through each engine's .NET client";
   private const string MARIADB_STATEMENT = "an earlier run gave about 0.09 at 100,000";

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public BenchResultsV8iFixTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-page-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// B3: a correction of kind "unbacked" is read from a set's list (the page knew "false" and "misleading" and refused the third), and the run page labels it "Not backed by a saved source:" and not
   /// as a false or misleading statement.
   /// </summary>
   [Fact]
   public void AnUnbackedCorrection_IsRead_AndLabelledOnItsOwnTerms()
   {
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( root => root[BenchRunNotes.FIELD] = new JsonArray( UnbackedNote( RUN ) ) ) );
      Assert.Equal( "unbacked", Assert.Single( model.RunNoteList ).Kind );

      WriteSet( PUBLISHED, UnbackedNote( RUN ) );
      WriteRun( RUN, "# Report\n\n## Details per target\n\n### mariadb\n\n- Index: HNSW (measured 2026-10-04: 0.99 at 524; an earlier run gave about 0.09 at 100,000)), cache 4G\n" );

      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.Contains( "<strong>Not backed by a saved source:</strong>", html );
      Assert.DoesNotContain( "<strong>False:</strong>", html );
      Assert.DoesNotContain( "<strong>Misleading:</strong>", html );
      Assert.Contains( $"{MARIADB_STATEMENT} <strong>[Correction 1]</strong>", html );
      Assert.Contains( "data-kind=\"unbacked\"", html );
   }

   /// <summary>
   /// B3, the summary's table of corrections: a correction of kind "unbacked" is listed with its kind.
   /// </summary>
   [Fact]
   public void TheSummaryTable_ListsAnUnbackedCorrectionWithItsKind()
   {
      string html = BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( root => root[BenchRunNotes.FIELD] = new JsonArray( UnbackedNote( RUN ) ) ) ) );

      string table = Regex.Match( html, "<table class=\"bench-table\" data-table=\"run-notes\">.*?</table>", RegexOptions.Singleline ).Value;
      Assert.Contains( "<td>unbacked</td>", table );
      Assert.Contains( MARIADB_STATEMENT, table );
   }

   /// <summary>
   /// B2 and B3 on the real run pages: for every run of the sets, the repository's run-page notes list marks the framing line (false, once) and the MariaDB clause (unbacked, at both places the report
   /// text holds it), and the page of a run built from the real files carries both labels and a marker after each of the three places.
   /// </summary>
   [FactIfRepositoryFileExists( BenchRunNotes.DEFAULT_FILE, REAL_RUNS )]
   public void TheRealRunPages_CarryTheFramingCorrection_AndTheMariaDbCorrection()
   {
      string file = BenchResultsV8hWebTests.FindRepositoryFile( BenchRunNotes.DEFAULT_FILE )!;
      IReadOnlyList<BenchRunNote> all = BenchRunNotes.ReadFile( file, out string? error );
      Assert.Null( error );
      string[] runs = Directory.GetDirectories( REAL_RUNS, "2026*-eshoponweb" ).Select( Path.GetFileName ).Cast<string>().Where( r => string.CompareOrdinal( r, "20261005-023459-eshoponweb" ) >= 0 ).ToArray();
      Assert.Equal( 12, runs.Length );
      WriteSet( PUBLISHED );

      foreach( string run in runs )
      {
         List<BenchRunNote> mine = all.Where( n => n.Runs.Contains( run ) ).ToList();
         BenchRunNote framing = Assert.Single( mine, n => n.Statement == CLIENT_STATEMENT );
         BenchRunNote mariadb = Assert.Single( mine, n => n.Statement == MARIADB_STATEMENT );
         Assert.Equal( ( "false", "unbacked" ), ( framing.Kind, mariadb.Kind ) );
         string md = File.ReadAllText( Path.Combine( REAL_RUNS, run, "results.md" ) );
         BenchRunNotes.Apply( md, mine, out IReadOnlyList<int> found );
         Assert.Equal( 1, found[mine.IndexOf( framing )] );
         Assert.Equal( 2, found[mine.IndexOf( mariadb )] );

         Directory.CreateDirectory( Path.Combine( _root, run ) );
         File.Copy( Path.Combine( REAL_RUNS, run, "results.json" ), Path.Combine( _root, run, "results.json" ) );
         File.WriteAllText( Path.Combine( _root, run, "results.md" ), md );
         string html = BenchResultsEndpoints.RunPageHtml( _root, run, file )!;
         Assert.Contains( "<strong>False:</strong>", html );
         Assert.Contains( "<strong>Not backed by a saved source:</strong>", html );
         Assert.Single( Regex.Matches( html, @"\.NET client <strong>\[Correction \d+\]</strong>" ) );
         Assert.Equal( 2, Regex.Matches( html, @"at 100,000 <strong>\[Correction \d+\]</strong>" ).Count );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A note of kind "unbacked" about MariaDB's recall clause.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>The note object.</returns>
   private static JsonObject UnbackedNote( string run )
   {
      return new JsonObject
      {
         ["runs"] = new JsonArray( run ),
         ["target"] = string.Empty,
         ["field"] = "index",
         ["statement"] = MARIADB_STATEMENT,
         ["kind"] = "unbacked",
         ["note"] = "No log was saved for this figure. A saved re-run read 0.988 to 0.996 at 524 vectors; the 100,000-row figure is dropped.",
         ["sources"] = new JsonArray( new JsonObject { ["kind"] = "file", ["ref"] = "design/bench-inputs/mariadb-effort-2026-10-07/SOURCES.txt#no log was saved", ["value"] = "no log was saved" } ),
      };
   }

   /// <summary>
   /// Writes a published folder holding the fixture with the given corrections in its list.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <param name="notes">The corrections.</param>
   private void WriteSet( string name, params JsonObject[] notes )
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         if( notes.Length > 0 )
         {
            root[BenchRunNotes.FIELD] = new JsonArray( notes.Select( n => (JsonNode)n.DeepClone() ).ToArray() );
         }
      } );
      Directory.CreateDirectory( Path.Combine( _root, name ) );
      File.WriteAllText( Path.Combine( _root, name, "consolidated.json" ), json );
      File.WriteAllText( Path.Combine( _root, name, "consolidated.md" ), BenchResultsV8Fixture.REPORT_MD );
   }

   /// <summary>
   /// Writes a run folder with the shared results.json and a report.
   /// </summary>
   /// <param name="run">The run folder.</param>
   /// <param name="report">The report text.</param>
   private void WriteRun( string run, string report )
   {
      string folder = Path.Combine( _root, run );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "results.json" ), BenchResultsFixtures.RUN_RESULTS_V8_JSON );
      File.WriteAllText( Path.Combine( folder, "results.md" ), report );
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
