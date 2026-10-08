using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The web side of the v8j fixes: the clock clause is marked on every run page that prints it, including the clock line of a v7 page that the page draws from the run's notes and not from the report text;
/// every clause the summary drops as unverified is marked wherever a linked run page prints it; a ranked row that is separated from every engine says "none" and not a dash; the blocked pages point to the
/// corrections. Each test fails on the tree the v8j verdict judged.
/// </summary>
public class BenchResultsV8kFixTests : IDisposable
{
   #region Data Members

   private const string CLASSES_FILE = "deploy/bench/recorded-text-classes.json";
   private const string CLOCK_STATEMENT = "so every CPU's clock is held at its ceiling of 3500 MHz whatever the engine runs";
   private const string SAMPLE_RUN = "bench-results/20261007-233551-eshoponweb/results.md";

   private static readonly string[] V7_RUNS = { "20261006-130619-eshoponweb", "20261006-142724-eshoponweb", "20261006-154837-eshoponweb" };
   private static readonly string[] V8_RUNS = { "20261007-205106-eshoponweb", "20261007-221429-eshoponweb", "20261007-233551-eshoponweb" };

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public BenchResultsV8kFixTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-page-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The block reason on the real pages: the clause is in all six runs. The page of a v8 run prints it once (in the machine control note of the report text) and the page of a v7 run twice (also on the
   /// clock line the page draws from the run's notes); every place carries a correction marker, the block counts the places, and the label is False for v8 and Not backed by a saved source for v7.
   /// </summary>
   [FactIfRepositoryFileExists( SAMPLE_RUN )]
   public void TheClockClause_IsMarkedAtEveryPlaceOfTheSixRealRunPages()
   {
      string repo = Repo();
      string file = Path.Combine( repo, BenchRunNotes.DEFAULT_FILE );

      foreach( string run in V7_RUNS.Concat( V8_RUNS ) )
      {
         bool v7 = V7_RUNS.Contains( run );
         string html = RenderRun( repo, run, file );
         int places = v7 ? 2 : 1;
         string body = Regex.Replace( html, "<aside class=\"bench-caveats\" data-section=\"run-notes\">.*?</aside>", " ", RegexOptions.Singleline );
         Assert.Equal( places, Regex.Matches( body, Regex.Escape( "whatever the engine runs" ) ).Count );
         Assert.Equal( places, Regex.Matches( body, Regex.Escape( "whatever the engine runs" ) + @" <strong>\[Correction \d+\]</strong>" ).Count );
         Assert.Matches( $"<li data-note=\"\\d+\" data-kind=\"{( v7 ? "unbacked" : "false" )}\" data-places=\"{places}\"><strong>{( v7 ? "Not backed by a saved source:" : "False:" )}</strong>[^<]*<q>{Regex.Escape( WebUtility.HtmlEncode( CLOCK_STATEMENT ) )}</q>", html );
      }
   }

   /// <summary>
   /// The clock line of a run page is drawn from the run's notes, not from the report text, so the marking of the report text alone leaves it bare. A run whose notes state the clause gets the marker on that
   /// line too, and a note with an engine heading (which this line has none of) does not mark it.
   /// </summary>
   [Fact]
   public void TheClockLineOfARunPage_CarriesTheMarker_OfANoteWithNoEngine()
   {
      const string clause = "CPU clock pinned for the run: turbo off (intel_pstate/no_turbo 0 -> 1), so every CPU's clock is held at its ceiling of 3500 MHz whatever the engine runs";
      string results = ResultsWithClockNote( "Machine control on: governor performance on every CPU during the run. " + clause + "; the uncore clock is held at 3000 MHz." );
      string report = "# Report\n\n## Details per target\n\n### mariadb\n\n- Index: HNSW.\n\n## Run notes\n\n- Machine control on: governor performance on every CPU during the run. " + clause + "; the uncore clock is held.\n";
      string list = ListFile( CLOCK_STATEMENT, string.Empty );
      WriteRun( "20261006-130619-eshoponweb", results, report );

      string html = BenchResultsEndpoints.RunPageHtml( _root, "20261006-130619-eshoponweb", list )!;

      Assert.Equal( 2, Regex.Matches( html, @"whatever the engine runs <strong>\[Correction 1\]</strong>" ).Count );
      Assert.Contains( "data-places=\"2\"", html );
      Assert.Contains( "data-line=\"clock\">Clock, as this run&#39;s notes record it:", html );

      string engineBound = ListFile( CLOCK_STATEMENT, "mariadb" );
      string bound = BenchResultsEndpoints.RunPageHtml( _root, "20261006-130619-eshoponweb", engineBound )!;
      Assert.Empty( Regex.Matches( bound, @"data-line=""clock"">[^<]*whatever the engine runs <strong>" ) );
   }

   /// <summary>
   /// The general rule on the pages: every clause the summary drops as unverified (a clause with a drop code in the classes list, and a clause a repository file contradicts) that a linked run page prints in its
   /// report text or on a line drawn from its notes carries a correction marker inside or at the end of the clause. A new dropped clause with no note, or a place the page draws without marking, fails here.
   /// </summary>
   [FactIfRepositoryFileExists( SAMPLE_RUN )]
   public void EveryClauseTheSummaryDropsAsUnverified_IsMarkedWhereverALinkedRunPagePrintsIt()
   {
      string repo = Repo();
      string file = Path.Combine( repo, BenchRunNotes.DEFAULT_FILE );
      IReadOnlyList<BenchRunNote> notes = BenchRunNotes.ReadFile( file, out string? error );
      Assert.Null( error );
      List<( string Label, Regex Clause )> dropped = DroppedClauses( repo );
      Assert.Equal( new[] { "clock-held", "contradicted", "mariadb-recall" }, dropped.Select( d => d.Label ).Distinct().OrderBy( l => l, StringComparer.Ordinal ).ToArray() );
      int places = 0;

      foreach( string run in notes.SelectMany( n => n.Runs ).Distinct( StringComparer.Ordinal ).OrderBy( r => r, StringComparer.Ordinal ) )
      {
         string visible = Visible( RenderRun( repo, run, file ), out List<int> markers );
         foreach( ( string label, Regex clause ) in dropped )
         {
            foreach( Match place in clause.Matches( visible ) )
            {
               places++;
               Assert.True( markers.Any( m => m > place.Index && m <= place.Index + place.Length ),
                  $"{run}: the page prints the {label} clause \"{place.Value[..Math.Min( 80, place.Value.Length )]}\" that the summary drops, with no correction marker inside it" );
            }
         }
      }

      Assert.True( places >= 30, $"only {places} places of dropped clauses were found on the pages; the rule is not looking at them" );
   }

   /// <summary>
   /// Fix-with 1: a ranked row that is separated from every other engine says "none" in the "Not separated from" column, as the summary's Markdown does; the page legend gives a dash the meaning "no figure",
   /// so a dash there was read as missing. A row that is not held, and a row of one session, keep their words.
   /// </summary>
   [Fact]
   public void TheNotSeparatedCell_SaysNone_ForARankedRowSeparatedFromEveryEngine()
   {
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json() );
      int checkedRows = 0;

      foreach( BenchMetric metric in model.Metrics )
      {
         string html = BenchConsolidatedHtml.MetricTable( metric, model.Sessions );
         foreach( BenchRow row in metric.Rows.Where( r => !r.IsNotHeld && r.Status == "ranked" && r.NotSeparatedFrom.Count == 0 ) )
         {
            Match tr = Regex.Match( html, "<tr data-target=\"" + Regex.Escape( row.Target ) + "\">.*?</tr>", RegexOptions.Singleline );
            Assert.True( tr.Success );
            List<string> cells = Regex.Matches( tr.Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline ).Select( c => c.Groups[1].Value ).ToList();
            Assert.Equal( "none", cells[^2] );
            checkedRows++;
         }
      }

      Assert.True( checkedRows > 0, "the fixture holds no ranked row separated from every engine" );
   }

   /// <summary>
   /// Fix-with 3: the page of a blocked set prints its report as it was written, including statements the run pages correct, so it carries one line that points to the corrections. A candidate and a published
   /// set carry no such line.
   /// </summary>
   [Fact]
   public void ABlockedSetPage_PointsToTheCorrections_AndOtherSetsDoNot()
   {
      foreach( string name in new[] { "blocked-2026-10-05-v5", "blocked-2026-10-06-v7", "candidate-v8", "published-2026-10-07" } )
      {
         WriteSet( name );
      }

      string blocked = BenchResultsEndpoints.RunPageHtml( _root, "blocked-2026-10-06-v7" )!;

      Match line = Regex.Match( blocked, "<p class=\"muted\" data-notice=\"blocked-corrections\">(.*?)</p>", RegexOptions.Singleline );
      Assert.True( line.Success, "the blocked page has no pointer line" );
      Assert.Contains( BenchLegends.BLOCKED_CORRECTIONS, WebUtility.HtmlDecode( line.Groups[1].Value ) );
      Assert.Contains( "href=\"/bench-results\"", line.Groups[1].Value );
      Assert.Contains( "blocked-corrections", BenchResultsEndpoints.RunPageHtml( _root, "blocked-2026-10-05-v5" )! );
      Assert.DoesNotContain( "blocked-corrections", BenchResultsEndpoints.RunPageHtml( _root, "candidate-v8" )! );
      Assert.DoesNotContain( "blocked-corrections", BenchResultsEndpoints.RunPageHtml( _root, "published-2026-10-07" )! );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The root of the checkout above the test output folder.
   /// </summary>
   /// <returns>The path.</returns>
   private static string Repo()
   {
      return Path.GetFullPath( Path.Combine( Path.GetDirectoryName( BenchResultsV8hWebTests.FindRepositoryFile( BenchRunNotes.DEFAULT_FILE )! )!, "..", ".." ) );
   }

   /// <summary>
   /// The clauses the summary drops as unverified, each as a label and a regex that finds it on a page.
   /// </summary>
   /// <param name="repo">The checkout.</param>
   /// <returns>The clauses.</returns>
   private static List<( string Label, Regex Clause )> DroppedClauses( string repo )
   {
      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( Path.Combine( repo, CLASSES_FILE ) ) );
      var found = new List<( string, Regex )>();
      foreach( JsonElement span in doc.RootElement.GetProperty( "texts" ).EnumerateArray().Concat( doc.RootElement.GetProperty( "notes" ).EnumerateArray() ).SelectMany( t => t.GetProperty( "spans" ).EnumerateArray() ) )
      {
         string? label = span.GetProperty( "class" ).GetString() != "unverified" ? null : span.TryGetProperty( "drop", out JsonElement drop ) ? drop.GetString() : span.TryGetProperty( "contradictedBy", out _ ) ? "contradicted" : null;
         if( label != null )
         {
            string pattern = span.TryGetProperty( "pattern", out JsonElement p ) ? p.GetString()! : Regex.Escape( span.GetProperty( "text" ).GetString()! );
            found.Add( ( label, new Regex( pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) ) ) );
         }
      }

      return found;
   }

   /// <summary>
   /// Renders the page of a real run from copies of its files.
   /// </summary>
   /// <param name="repo">The checkout.</param>
   /// <param name="run">The run folder.</param>
   /// <param name="notesFile">The list file.</param>
   /// <returns>The page.</returns>
   private string RenderRun( string repo, string run, string notesFile )
   {
      string folder = Path.Combine( _root, run );
      Directory.CreateDirectory( folder );
      File.Copy( Path.Combine( repo, "bench-results", run, "results.json" ), Path.Combine( folder, "results.json" ), true );
      File.Copy( Path.Combine( repo, "bench-results", run, "results.md" ), Path.Combine( folder, "results.md" ), true );
      return BenchResultsEndpoints.RunPageHtml( _root, run, notesFile ) ?? throw new InvalidOperationException( $"{run} did not render" );
   }

   /// <summary>
   /// The visible text of a run page outside the block of corrections, with every correction marker taken out and the position each one stood at.
   /// </summary>
   /// <param name="html">The page.</param>
   /// <param name="markers">Receives the positions, in the returned text, where a marker stood.</param>
   /// <returns>The text.</returns>
   private static string Visible( string html, out List<int> markers )
   {
      string body = Regex.Replace( html, "<aside class=\"bench-caveats\" data-section=\"run-notes\">.*?</aside>", " ", RegexOptions.Singleline );
      string text = Regex.Replace( WebUtility.HtmlDecode( Regex.Replace( body, "<[^>]+>", string.Empty ) ), @"\s+", " " );
      markers = new List<int>();
      var plain = new StringBuilder();
      int last = 0;
      foreach( Match m in Regex.Matches( text, @" ?\[Correction \d+\]" ) )
      {
         plain.Append( text, last, m.Index - last );
         markers.Add( plain.Length );
         last = m.Index + m.Length;
      }

      return plain.Append( text, last, text.Length - last ).ToString();
   }

   /// <summary>
   /// A list file with one unbacked note for a statement.
   /// </summary>
   /// <param name="statement">The statement.</param>
   /// <param name="target">The engine the note is bound to, or empty.</param>
   /// <returns>The file's path.</returns>
   private string ListFile( string statement, string target )
   {
      string path = Path.Combine( _root, $"list-{Guid.NewGuid():N}.json" );
      var note = new Dictionary<string, object>
      {
         ["runs"] = new[] { "20261006-130619-eshoponweb" }, ["target"] = target, ["field"] = "notes", ["statement"] = statement, ["kind"] = "unbacked", ["note"] = "A test note.",
         ["sources"] = new[] { new Dictionary<string, string> { ["kind"] = "file", ["ref"] = "GenericVectorBuilder.slnx#Solution", ["value"] = "Solution" } },
      };
      File.WriteAllText( path, JsonSerializer.Serialize( new Dictionary<string, object> { ["format"] = "run-page-notes-1", ["notes"] = new[] { note } } ) );
      return path;
   }

   /// <summary>
   /// A results.json that carries one run note (the shared v8 run file with its notes replaced and its clock block taken out, so the page draws the clock line from the note, as it does for a v7 run).
   /// </summary>
   /// <param name="note">The note.</param>
   /// <returns>The JSON text.</returns>
   private static string ResultsWithClockNote( string note )
   {
      System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse( BenchResultsFixtures.RUN_RESULTS_V8_JSON )!;
      root["notes"] = new System.Text.Json.Nodes.JsonArray( note );
      root["conditions"]!.AsObject().Remove( "clock" );
      return root.ToJsonString();
   }

   /// <summary>
   /// Writes a run folder.
   /// </summary>
   /// <param name="run">The run folder.</param>
   /// <param name="results">The results.json text.</param>
   /// <param name="report">The report text.</param>
   private void WriteRun( string run, string results, string report )
   {
      string folder = Path.Combine( _root, run );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "results.json" ), results );
      File.WriteAllText( Path.Combine( folder, "results.md" ), report );
   }

   /// <summary>
   /// Writes a set folder holding the shared fixture.
   /// </summary>
   /// <param name="name">Folder name.</param>
   private void WriteSet( string name )
   {
      Directory.CreateDirectory( Path.Combine( _root, name ) );
      File.WriteAllText( Path.Combine( _root, name, "consolidated.json" ), BenchResultsV8Fixture.Json() );
      File.WriteAllText( Path.Combine( _root, name, "consolidated.md" ), BenchResultsV8Fixture.REPORT_MD );
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
