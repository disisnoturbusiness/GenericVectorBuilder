using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// A fact that is skipped, with the reason named, when a file of the repository checkout that holds the test output folder is not there: found by walking
/// up from the test output folder, so the test reads the checkout it was built in. A skipped test shows as skipped, never as a pass.
/// </summary>
public sealed class FactIfRepositoryFileExistsAttribute : FactAttribute
{
   #region Constructor

   /// <summary>
   /// Marks the test skipped when no folder above the test output folder holds the file.
   /// </summary>
   /// <param name="relative">The file's path relative to the repository root.</param>
   /// <param name="alsoPath">A second path the test reads (a real run folder, say), or null; the test is skipped when it does not exist either.</param>
   public FactIfRepositoryFileExistsAttribute( string relative, string? alsoPath = null )
   {
      if( BenchResultsV8hWebTests.FindRepositoryFile( relative ) == null )
      {
         Skip = $"{relative} is not in the checkout above {AppContext.BaseDirectory}";
      }
      else if( alsoPath != null && !File.Exists( alsoPath ) && !Directory.Exists( alsoPath ) )
      {
         Skip = $"{alsoPath} is not on this machine";
      }
   }

   #endregion Constructor
}

/// <summary>
/// The web side of the v8 report-layer fixes (verdict v8h, reason 5 and fix-with 4, 6, 9 and 20): corrections printed at render time on the run pages
/// of recorded statements that a set marks as false or misleading, a metric row the tool recorded as not held shown and not ranked, both builds named
/// on the summary, the results ahead of the threshold on the summary, the image reference column, and the recorded texts the summary used to drop.
/// Why a class of its own: each test names the verdict item it proves, and the fixture is the shared v8 file with one change per test.
/// </summary>
public class BenchResultsV8hWebTests : IDisposable
{
   #region Data Members

   private const string RUN = "20261006-130619-eshoponweb";
   private const string OTHER_RUN = "20261006-142724-eshoponweb";
   private const string BASIS_RUN = "20261005-023459-eshoponweb";
   private const string PUBLISHED = "published-2026-10-07";

   private const string WEAVIATE = "weaviate.compose.yaml sets no persistence variable";
   private const string VESPA_ADDED = "235.33 MiB added by this load";
   private const string VESPA_START = "1.9 GiB at the start";
   private const string REAL_RUNS = "/home/dan/ForClaude/GenericVectorBuilder/bench-results";

   private const string REPORT = "# Vector engine benchmark: eshoponweb\n\n- Machine: linus\n\n## Details per target\n\n### weaviate\n\n"
      + "- Index after the load: ready. Durability: Weaviate 1.39.8 defaults, weaviate.compose.yaml sets no persistence variable: every object is appended to the log.\n"
      + "- RAM: 100 MiB (docker stats); disk: 2.96 MiB added by this load (5.02 MiB (whole engine data folder), 2.06 MiB before)\n\n"
      + "### vespa\n\n- RAM: 2.95 GiB (docker stats); disk: 235.33 MiB added by this load (2.13 GiB (whole engine data folder), 1.9 GiB before)\n"
      + "- Data folder /data/vespa: 1.9 GiB at the start, 2.13 GiB at the end\n- Data folder /data/other: 11.9 GiB at the start, 2.13 GiB at the end\n\n"
      + "### redis\n\n- RAM: 1 GiB; disk: 235.33 MiB added by this load\n";

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public BenchResultsV8hWebTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-page-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Verdict reason 5: the list of corrections in the consolidated file is read into the model, every field of an entry is read, and none is reported
   /// as a field the page does not read.
   /// </summary>
   [Fact]
   public void TheRunPageNotes_OfAConsolidatedFile_AreRead()
   {
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( root => root[BenchRunNotes.FIELD] = new JsonArray( WeaviateNote( RUN ), VespaNote( RUN, OTHER_RUN ) ) ) );

      Assert.Equal( 2, model.RunNoteList.Count );
      BenchRunNote weaviate = model.RunNoteList[0];
      Assert.Equal( new[] { RUN }, weaviate.Runs );
      Assert.Equal( ( "weaviate", "durability", WEAVIATE, BenchRunNotes.KIND_FALSE ), ( weaviate.Target, weaviate.Field, weaviate.Statement, weaviate.Kind ) );
      Assert.Equal( 2, weaviate.Sources.Count );
      Assert.Equal( new[] { RUN, OTHER_RUN }, model.RunNoteList[1].Runs );
      Assert.Empty( model.UnreadFields ?? Array.Empty<string>() );
   }

   /// <summary>
   /// A list that is wrong is refused with the path of the field: an unknown kind, a correction with no source, a run the set does not use, an unsafe run
   /// name, a missing statement, a missing correction, and a value that is not a list. A mistyped run must fail and not vanish from every page.
   /// </summary>
   [Theory]
   [InlineData( "kind", "runPageNotes[0].kind is \"wrong\"" )]
   [InlineData( "sources", "runPageNotes[0].sources is empty" )]
   [InlineData( "run", "names run 20261001-000000-eshoponweb, which the sessions and the basis runs do not list" )]
   [InlineData( "unsafe", "runPageNotes[0].runs must name between 1 and" )]
   [InlineData( "statement", "runPageNotes[0].statement is missing" )]
   [InlineData( "note", "runPageNotes[0].note is missing" )]
   [InlineData( "list", "runPageNotes is not an array" )]
   public void AListOfCorrectionsThatIsWrong_IsRefused( string defect, string message )
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         JsonObject note = WeaviateNote( RUN );
         switch( defect )
         {
            case "kind": note["kind"] = "wrong"; break;
            case "sources": note["sources"] = new JsonArray(); break;
            case "run": note["runs"] = new JsonArray( "20261001-000000-eshoponweb" ); break;
            case "unsafe": note["runs"] = new JsonArray( "../x" ); break;
            case "statement": note.Remove( "statement" ); break;
            case "note": note.Remove( "note" ); break;
         }

         root[BenchRunNotes.FIELD] = defect == "list" ? new JsonObject() : new JsonArray( note );
      } );

      InvalidDataException ex = Assert.Throws<InvalidDataException>( () => BenchConsolidatedReader.Read( json ) );
      Assert.Contains( message, ex.Message );
   }

   /// <summary>
   /// The marker goes right after the statement, in the section of the engine the note names and nowhere else; a figure is not found inside a longer figure
   /// ("1.9 GiB at the start" is not in "11.9 GiB at the start"); each note counts the lines it was found in; the text outside the markers is unchanged.
   /// </summary>
   [Fact]
   public void TheMarker_GoesAfterTheStatement_InTheEnginesSectionOnly()
   {
      var notes = new List<BenchRunNote> { Note( "weaviate", WEAVIATE ), Note( "vespa", VESPA_ADDED ), Note( "vespa", VESPA_START ) };

      string marked = BenchRunNotes.Apply( REPORT, notes, out IReadOnlyList<int> found );

      Assert.Equal( new[] { 1, 1, 1 }, found );
      Assert.Contains( $"{WEAVIATE} **[Correction 1]**: every object", marked );
      Assert.Contains( $"disk: {VESPA_ADDED} **[Correction 2]** (2.13 GiB", marked );
      Assert.Contains( $"/data/vespa: {VESPA_START} **[Correction 3]**, 2.13 GiB", marked );
      Assert.Contains( "/data/other: 11.9 GiB at the start, 2.13 GiB", marked );
      Assert.Contains( "### redis\n\n- RAM: 1 GiB; disk: 235.33 MiB added by this load\n", marked );
      Assert.Equal( REPORT, Regex.Replace( marked, @" \*\*\[Correction \d+\]\*\*", string.Empty ) );
   }

   /// <summary>
   /// A statement inside a table cell is marked inside the cell, so the table keeps its cells; a note with no engine is found in any section; a statement that
   /// is not in the report is counted as found nowhere; with no notes the text is returned as it is.
   /// </summary>
   [Fact]
   public void TheMarker_KeepsATableWhole_AndCountsAStatementThatIsNotThere()
   {
      const string TABLE = "| engine | durability | next |\n|---|---|---|\n| weaviate | weaviate.compose.yaml sets no persistence variable | after |\n";
      var notes = new List<BenchRunNote> { Note( string.Empty, WEAVIATE ), Note( string.Empty, "a statement the report does not hold" ) };

      string marked = BenchRunNotes.Apply( TABLE, notes, out IReadOnlyList<int> found );

      Assert.Equal( new[] { 1, 0 }, found );
      string[] before = BenchMarkdown.SplitCells( TABLE.Split( '\n' )[2] );
      string[] after = BenchMarkdown.SplitCells( marked.Split( '\n' )[2] );
      Assert.Equal( before.Length, after.Length );
      Assert.Contains( $"{WEAVIATE} **[Correction 1]**", after[1] );
      Assert.Same( TABLE, BenchRunNotes.Apply( TABLE, Array.Empty<BenchRunNote>(), out IReadOnlyList<int> none ) );
      Assert.Empty( none );
   }

   /// <summary>
   /// Verdict reason 5 and fix-with 6: the page of a run a published set uses prints the set's corrections in a block above the table (the kind, the engine,
   /// the field, the statement as recorded, the correction, the set it is listed in, how many lines carry its marker, and its sources), marks each statement in
   /// the report text, says the raw files hold the statements as written, and leaves the file on disk as it was.
   /// </summary>
   [Fact]
   public void TheRunPage_OfAPublishedSet_PrintsTheCorrectionsAndMarksTheStatements()
   {
      WriteSet( PUBLISHED, WeaviateNote( RUN ), VespaNote( RUN ) );
      WriteRun( RUN );

      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.Contains( "data-section=\"run-notes\"", html );
      Assert.Contains( BenchLegends.H_RUN_NOTES, html );
      Assert.Contains( BenchFormat.Enc( BenchLegends.RUN_NOTES_INTRO ), html );
      Assert.Contains( $"<strong>{BenchLegends.L_NOTE_FALSE}</strong> Weaviate, durability.", html );
      Assert.Contains( $"<strong>{BenchLegends.L_NOTE_MISLEADING}</strong> Vespa, disk.text.", html );
      Assert.Contains( $"{BenchLegends.L_NOTE_RECORDED} <q>{WEAVIATE}</q> The compose file sets a persistence variable.", html );
      Assert.Contains( $"{BenchLegends.L_NOTE_LISTED_IN} <a href=\"/bench-results/{PUBLISHED}\">{PUBLISHED}</a>.", html );
      Assert.Contains( $"{BenchLegends.L_NOTE_PLACES} 1.", html );
      Assert.Contains( "deploy/engines/weaviate.compose.yaml#PERSISTENCE_DATA_PATH", html );
      Assert.Contains( $"{WEAVIATE} <strong>[Correction 1]</strong>: every object", html );
      Assert.Contains( $"{VESPA_ADDED} <strong>[Correction 2]</strong> (2.13 GiB", html );
      Assert.Contains( BenchLegends.RAW_NOTES_UNEDITED, html );
      Assert.True( html.IndexOf( "data-section=\"run-notes\"", StringComparison.Ordinal ) < html.IndexOf( "class=\"bench-table\"", StringComparison.Ordinal ), "the corrections sit above the table" );
      Assert.DoesNotContain( "Correction", File.ReadAllText( Path.Combine( _root, RUN, "results.md" ) ) );
   }

   /// <summary>
   /// A run the set uses but no correction names has no block, a run no set uses has no block, and a run named by a correction for another run gets none of it.
   /// </summary>
   [Fact]
   public void TheRunPage_OfARunNoCorrectionNames_HasNoBlock()
   {
      WriteSet( PUBLISHED, WeaviateNote( RUN ) );
      WriteRun( RUN );
      WriteRun( OTHER_RUN );
      WriteRun( "20261001-000000-eshoponweb" );

      Assert.Contains( "data-section=\"run-notes\"", BenchResultsEndpoints.RunPageHtml( _root, RUN )! );
      Assert.DoesNotContain( "run-notes", BenchResultsEndpoints.RunPageHtml( _root, OTHER_RUN )! );
      Assert.DoesNotContain( "run-notes", BenchResultsEndpoints.RunPageHtml( _root, "20261001-000000-eshoponweb" )! );
      Assert.DoesNotContain( "[Correction", BenchResultsEndpoints.RunPageHtml( _root, OTHER_RUN )! );
   }

   /// <summary>
   /// A correction whose statement is not in the run's report text is still listed, and says it was not found; nothing in the report is marked. The page
   /// never drops a correction because its statement moved.
   /// </summary>
   [Fact]
   public void ACorrection_WhoseStatementIsNotInTheReport_SaysSo()
   {
      WriteSet( PUBLISHED, VespaNote( RUN ) );
      WriteRun( RUN, "# Report\n\n## Details per target\n\n### vespa\n\n- disk: 3.59 MiB added by this load\n" );

      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.Contains( BenchLegends.L_NOTE_NOT_FOUND, html );
      Assert.Contains( "data-places=\"0\"", html );
      Assert.DoesNotContain( "[Correction", html );
   }

   /// <summary>
   /// The corrections of a run page come from a candidate set too, because the pages of a candidate are what a verifier reads before anything is published.
   /// </summary>
   [Fact]
   public void TheRunPage_OfACandidateSet_PrintsItsCorrections()
   {
      WriteSet( "candidate-v8", WeaviateNote( RUN ) );
      WriteRun( RUN );

      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN )!;

      Assert.Contains( "<a href=\"/bench-results/candidate-v8\">candidate-v8</a>", html );
      Assert.Contains( "[Correction 1]", html );
   }

   /// <summary>
   /// Verdict fix-with 6: a set that carries no list of its own gets the corrections of the list file the endpoint is given, with the file named as the place
   /// they are listed in (a path, not a link); an entry that both give is printed once, from the set.
   /// </summary>
   [Fact]
   public void WhenTheSetHasNoList_TheListFileIsRead_AndAnEntryBothGiveIsPrintedOnce()
   {
      WriteSet( PUBLISHED );
      WriteRun( RUN );
      string file = WriteNotesFile( WeaviateNote( RUN ), VespaNote( RUN ) );

      string fromFile = BenchResultsEndpoints.RunPageHtml( _root, RUN, file )!;

      Assert.Contains( $"{BenchLegends.L_NOTE_LISTED_IN} <code>{BenchRunNotes.DEFAULT_FILE}</code>.", fromFile );
      Assert.Contains( "[Correction 2]", fromFile );
      Assert.DoesNotContain( "run-notes", BenchResultsEndpoints.RunPageHtml( _root, RUN )! );

      WriteSet( PUBLISHED, WeaviateNote( RUN ) );
      string both = BenchResultsEndpoints.RunPageHtml( _root, RUN, file )!;
      Assert.Single( Regex.Matches( both, Regex.Escape( $"<q>{WEAVIATE}</q>" ) ) );
      Assert.Contains( $"<a href=\"/bench-results/{PUBLISHED}\">{PUBLISHED}</a>", both );
      Assert.Equal( 2, Regex.Matches( both, @"<li data-note=""\d+""" ).Count );
   }

   /// <summary>
   /// A list file that cannot be read is said so on the page, with the reason; it is never taken for an empty list. A file that is absent is an empty list.
   /// </summary>
   [Theory]
   [InlineData( "{ not json", "invalid" )]
   [InlineData( "{ \"notes\": [ { \"runs\": [\"" + RUN + "\"], \"kind\": \"false\" } ] }", "notes[0].sources is empty" )]
   [InlineData( "{ \"other\": [] }", "has no \"notes\" array" )]
   public void AListFileThatCannotBeRead_IsSaidOnThePage( string content, string reason )
   {
      WriteSet( PUBLISHED );
      WriteRun( RUN );
      string file = Path.Combine( _root, "notes.json" );
      File.WriteAllText( file, content );

      string html = BenchResultsEndpoints.RunPageHtml( _root, RUN, file )!;

      Assert.Contains( BenchLegends.RUN_NOTES_UNREADABLE, WebUtility.HtmlDecode( html ) );
      Assert.Contains( reason, WebUtility.HtmlDecode( html ) );

      string absent = BenchResultsEndpoints.RunPageHtml( _root, RUN, Path.Combine( _root, "no-such-file.json" ) )!;
      Assert.DoesNotContain( "run-notes", absent );
      Assert.DoesNotContain( BenchLegends.RUN_NOTES_UNREADABLE, absent );
   }

   /// <summary>
   /// A summary says so when its set lists no corrections and the list file is absent (the run pages then print every recorded statement as written); it
   /// says nothing when the set lists corrections (even an empty list), when the list file exists (even with none in it), or when the caller does not check.
   /// </summary>
   [Fact]
   public void TheSummary_SaysWhenNoListOfCorrectionsExists()
   {
      string missing = Path.Combine( _root, "deploy", "run-page-notes.json" );
      WriteSet( PUBLISHED );

      string notice = BenchResultsEndpoints.ListPageHtml( _root, missing );

      Assert.Contains( "data-notice=\"no-corrections-list\"", notice );
      Assert.Contains( BenchFormat.Enc( BenchLegends.NO_RUN_NOTES ), notice );
      Assert.DoesNotContain( "no-corrections-list", BenchResultsEndpoints.ListPageHtml( _root ) );
      Assert.DoesNotContain( "no-corrections-list", BenchResultsEndpoints.ListPageHtml( _root, WriteNotesFile() ) );
      WriteSet( PUBLISHED, WeaviateNote( RUN ) );
      Assert.DoesNotContain( "no-corrections-list", BenchResultsEndpoints.ListPageHtml( _root, missing ) );
      Directory.Delete( Path.Combine( _root, PUBLISHED ), true );
      WriteSet( PUBLISHED );
      File.WriteAllText( Path.Combine( _root, PUBLISHED, "consolidated.json" ), BenchResultsV8Fixture.Json( root => root[BenchRunNotes.FIELD] = new JsonArray() ) );
      Assert.DoesNotContain( "no-corrections-list", BenchResultsEndpoints.ListPageHtml( _root, missing ) );
   }

   /// <summary>
   /// The list file is looked for in the repository above the results root unless a path is configured.
   /// </summary>
   [Fact]
   public void TheListFilePath_IsAboveTheResultsRoot_UnlessConfigured()
   {
      Assert.Equal( "/srv/gvb/deploy/bench/run-page-notes.json", BenchResultsEndpoints.NotesFilePath( "/srv/gvb/bench-results", null ) );
      Assert.Equal( "/srv/gvb/deploy/bench/run-page-notes.json", BenchResultsEndpoints.NotesFilePath( "/srv/gvb/bench-results", "  " ) );
      Assert.Equal( "/etc/notes.json", BenchResultsEndpoints.NotesFilePath( "/srv/gvb/bench-results", "/etc/notes.json" ) );
   }

   /// <summary>
   /// The repository's own list file (deploy/bench/run-page-notes.json), when it is on this machine, reads without a refusal, and every statement in it is
   /// in the report of the real run it names, under the engine's heading, once or more. A statement the report does not hold would be a correction of nothing.
   /// </summary>
   [FactIfRepositoryFileExists( BenchRunNotes.DEFAULT_FILE )]
   public void TheRepositoryListFile_IsReadable_AndItsStatementsAreInTheRealReports()
   {
      string file = FindRepositoryFile( BenchRunNotes.DEFAULT_FILE )!;

      IReadOnlyList<BenchRunNote> notes = BenchRunNotes.ReadFile( file, out string? error );

      Assert.Null( error );
      Assert.NotEmpty( notes );
      Assert.All( notes, n => Assert.NotEmpty( n.Sources ) );
      string repo = Path.GetFullPath( Path.Combine( Path.GetDirectoryName( file )!, "..", ".." ) );
      foreach( BenchSource source in notes.SelectMany( n => n.Sources ).Where( s => s.Kind == "file" ) )
      {
         string[] parts = source.Ref.Split( '#', 2 );
         string path = Path.Combine( repo, parts[0] );
         Assert.True( File.Exists( path ), $"the source file {parts[0]} is not in the repository" );
         string text = File.ReadAllText( path );
         Assert.Contains( parts[1], text );
         Assert.Contains( source.Value, text );
      }

      string results = REAL_RUNS;
      foreach( BenchRunNote note in notes.Where( n => Directory.Exists( Path.Combine( results, n.Runs[0] ) ) ) )
      {
         string md = File.ReadAllText( Path.Combine( results, note.Runs[0], "results.md" ) );
         BenchRunNotes.Apply( md, new[] { note }, out IReadOnlyList<int> found );
         Assert.True( found[0] > 0, $"{note.Runs[0]}: \"{note.Statement}\" is not in the report under {note.Target}" );
      }
   }

   /// <summary>
   /// The summary lists the corrections the file carries, one row per run and statement, with the run as a link, the kind, the statement and the correction;
   /// a file with none has no such block.
   /// </summary>
   [Fact]
   public void TheSummary_ListsTheCorrectionsThePagesOfTheRunsPrint()
   {
      string html = Html( root => root[BenchRunNotes.FIELD] = new JsonArray( WeaviateNote( RUN ), VespaNote( RUN, OTHER_RUN ) ) );

      string table = Regex.Match( html, "<table class=\"bench-table\" data-table=\"run-notes\">.*?</table>", RegexOptions.Singleline ).Value;
      Assert.Equal( 3, Regex.Matches( table, "<tr data-target=" ).Count );
      Assert.Contains( $"<td><a href=\"/bench-results/{RUN}\">{RUN}</a></td><td>Weaviate</td><td>durability</td><td>false</td><td>{WEAVIATE}</td>", table );
      Assert.Contains( $"<a href=\"/bench-results/{OTHER_RUN}\">{OTHER_RUN}</a></td><td>Vespa</td><td>disk.text</td><td>misleading</td>", table );
      Assert.Contains( $"{BenchLegends.H_RUN_NOTES_SUMMARY} 3</summary>", html );
      Assert.DoesNotContain( "data-table=\"run-notes\"", Html( null ) );
   }

   /// <summary>
   /// Fix-with 4: a row whose status is "not-held" is drawn in a group of rows that are not ranked, after every ranked row, with its figures and the words
   /// "not held, not ranked"; its median is not required to fit the order; no ranked row names it as an engine it is not separated from; the legend is printed
   /// once; the other tables have no such group.
   /// </summary>
   [Theory]
   [InlineData( "status" )]
   [InlineData( "flag" )]
   public void ANotHeldRow_IsShownAndNotRanked( string how )
   {
      string html = Html( root =>
      {
         JsonObject row = Row( root, "qps8", "sqlitevec" );
         if( how == "status" )
         {
            row["status"] = BenchRow.NOT_HELD;
            row["median"] = 99999.0;
         }
         else
         {
            ( (JsonArray)row["flags"]! ).Add( new JsonObject { ["code"] = BenchRow.NOT_HELD, ["text"] = "default@8 timed pass NOT HELD" } );
         }
      } );

      string table = TableHtml( html, "qps8" );
      int group = table.IndexOf( "data-group=\"not-ranked\"", StringComparison.Ordinal );
      Assert.True( group > 0, "the group of rows that are not ranked is missing" );
      List<string> order = Regex.Matches( table, "<tr data-target=\"([^\"]+)\"" ).Select( m => m.Groups[1].Value ).ToList();
      Assert.Equal( "sqlitevec", order[^1] );
      Assert.True( table.IndexOf( "<tr data-target=\"sqlitevec\"", StringComparison.Ordinal ) > group );
      Assert.All( order.Take( order.Count - 1 ), t => Assert.True( table.IndexOf( $"<tr data-target=\"{t}\"", StringComparison.Ordinal ) < group ) );
      string notHeld = Regex.Match( table, "<tr data-target=\"sqlitevec\">.*?</tr>" ).Value;
      Assert.Contains( BenchLegends.L_NOT_HELD, notHeld );
      Assert.Contains( "data-state=\"not-held\"", notHeld );
      Assert.Contains( "sqlite-vec", notHeld );
      Assert.Matches( @"<td class=""n"">[0-9.,]+ to [0-9.,]+</td>", notHeld );
      foreach( string t in order.Take( order.Count - 1 ) )
      {
         Assert.DoesNotContain( "sqlite-vec", Regex.Match( table, $"<tr data-target=\"{t}\">.*?</tr>" ).Value.Split( "</td>" )[^3], StringComparison.Ordinal );
      }

      Assert.Single( Regex.Matches( html, Regex.Escape( BenchLegends.NOT_HELD ) ) );
      Assert.DoesNotContain( "data-group=\"not-ranked\"", TableHtml( html, "p50Ms" ) );
   }

   /// <summary>
   /// A run page flags each timed pass its run recorded as NOT HELD with the same flag code the summary uses, naming the pass the mark sits in (the passes
   /// are listed in any order in the warning) and quoting the clause; a target with no mark has no such flag; a mark that sits in no pass is flagged with
   /// the words around it and never dropped.
   /// </summary>
   [Fact]
   public void ARunPage_FlagsEachPassItsRunRecordedAsNotHeld()
   {
      const string TWO = "WARNING: latency had NOT settled when timing began for default@1 and default@8. default@1: warm-up 15.0 s and 5,000 searches; settled before the timed pass. "
         + "default@8: warm-up 15.0 s and 6,523 searches; the timed pass 336 QPS, 24% from the settled 418 QPS (limit 10%), NOT HELD: the engine was still changing when it was timed; "
         + "exact: warm-up 5.0 s and 100 searches; the timed pass p50 6.630 ms, 46% from the settled p50 4.520 ms (limit 10% or 0.1 ms; 2.1 ms), NOT HELD";
      string json = RunJson( ( "clickhouse", TWO ), ( "redis", "WARNING: latency had NOT settled when timing began for default@8. default@8: warm-up 15.0 s; settled before the timed pass." ),
         ( "oracle", "WARNING: latency had NOT settled; NOT HELD with no pass before it" ) );

      BenchRunSummary summary = BenchRunTable.Read( json );

      List<BenchFlagEntry> held = summary.Rows.Single( r => r.Key == "clickhouse" ).Flags.Where( f => f.Code == BenchRow.NOT_HELD ).ToList();
      Assert.Equal( 2, held.Count );
      Assert.Equal( "default@8: the timed pass 336 QPS, 24% from the settled 418 QPS (limit 10%), NOT HELD", held[0].Text );
      Assert.StartsWith( "exact: the timed pass p50 6.630 ms, 46% from the settled p50 4.520 ms", held[1].Text );
      Assert.DoesNotContain( summary.Rows.Single( r => r.Key == "redis" ).Flags, f => f.Code == BenchRow.NOT_HELD );
      Assert.StartsWith( "pass not named: ", summary.Rows.Single( r => r.Key == "oracle" ).Flags.Single( f => f.Code == BenchRow.NOT_HELD ).Text );
      Assert.Contains( BenchLegends.Legend( BenchRow.NOT_HELD ), BenchRunTable.Block( summary ).Replace( "&#39;", "'" ) );
   }

   /// <summary>
   /// The three real v8 runs, when they are on this machine, each flag ClickHouse's eight-searcher pass as not held, with the figures their notes record
   /// (336, 380 and 349 QPS); no other engine of those runs is flagged.
   /// </summary>
   [FactIfPathExists( REAL_RUNS + "/20261007-233551-eshoponweb/results.json" )]
   public void TheRealV8Runs_FlagClickHousesEightSearcherPassAsNotHeld()
   {
      var expected = new Dictionary<string, string>
      {
         ["20261007-205106-eshoponweb"] = "default@8: the timed pass 336 QPS, 24% from the settled 418 QPS (limit 10%), NOT HELD",
         ["20261007-221429-eshoponweb"] = "default@8: the timed pass 380 QPS, 13% from the settled 432 QPS (limit 10%), NOT HELD",
         ["20261007-233551-eshoponweb"] = "default@8: the timed pass 349 QPS, 19% from the settled 416 QPS (limit 10%), NOT HELD",
      };
      foreach( ( string run, string text ) in expected )
      {
         BenchRunSummary summary = BenchRunTable.Read( File.ReadAllText( $"{REAL_RUNS}/{run}/results.json" ) );

         Assert.Equal( new[] { "clickhouse" }, summary.Rows.Where( r => r.Flags.Any( f => f.Code == BenchRow.NOT_HELD ) ).Select( r => r.Key ) );
         Assert.Equal( text, summary.Rows.Single( r => r.Key == "clickhouse" ).Flags.Single( f => f.Code == BenchRow.NOT_HELD ).Text );
      }
   }

   /// <summary>
   /// A file with no row that is not held has no group of unranked rows and no legend for it.
   /// </summary>
   [Fact]
   public void AFileWithNoNotHeldRow_HasNoUnrankedGroup()
   {
      string html = Html( null );

      Assert.DoesNotContain( "data-group=\"not-ranked\"", html );
      Assert.DoesNotContain( BenchLegends.NOT_HELD, html );
   }

   /// <summary>
   /// The not held flag has its own marker and legend, so a row that carries it never shows the "??" marker of an unknown code, and the marker is unique.
   /// </summary>
   [Fact]
   public void TheNotHeldFlag_HasAMarkerAndALegend()
   {
      Assert.Equal( "Nh", BenchLegends.Marker( BenchRow.NOT_HELD ) );
      Assert.NotEqual( BenchLegends.FLAG_UNKNOWN, BenchLegends.Legend( BenchRow.NOT_HELD ) );
      Assert.Single( BenchLegends.KnownFlagCodes, c => BenchLegends.Marker( c ) == "Nh" );
   }

   /// <summary>
   /// Both builds are named in the lead of the summary: the sentences the report writes in the subtitle section ("subtitle.build.v8" for the build that measured
   /// the session, "subtitle.build.consolidated" for the build that made the report) are printed once each, after the headline and before the first table, and
   /// the structured record behind them (the file's "build") is left out on purpose and not listed as a field the page does not read.
   /// </summary>
   [Fact]
   public void TheSummary_PrintsTheSentencesThatNameBothBuilds_BeforeTheFirstTable()
   {
      const string MEASURED = "The runs of v8 were measured by build 9b924200abc3.";
      const string CONSOLIDATED = "This report was consolidated by build fedcba987654, not by the build that measured the runs of v8.";
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( root =>
      {
         var sentences = (JsonArray)root["sentences"]!;
         sentences.Add( BenchResultsV8Fixture.Sentence( "subtitle.build.v8", MEASURED ) );
         sentences.Add( BenchResultsV8Fixture.Sentence( "subtitle.build.consolidated", CONSOLIDATED ) );
         root["build"] = new JsonObject
         {
            ["measured"] = new JsonArray( new JsonObject { ["session"] = "v8", ["commit"] = "9b924200abc3f2c3e41c2a9520e5c7bb16748e6c" } ),
            ["consolidatedBy"] = new JsonObject { ["commit"] = "fedcba9876543210fedcba9876543210fedcba98", ["commitShort"] = "fedcba987654" },
         };
      } ) );

      string html = BenchConsolidatedHtml.Block( model );

      int first = html.IndexOf( "data-metric=\"p50Ms\"", StringComparison.Ordinal );
      int headline = html.IndexOf( "data-slot=\"headline\"", StringComparison.Ordinal );
      foreach( string text in new[] { MEASURED, CONSOLIDATED } )
      {
         Assert.Single( Regex.Matches( html, Regex.Escape( text ) ) );
         int at = html.IndexOf( text, StringComparison.Ordinal );
         Assert.True( at > headline && at < first, $"\"{text}\" is not between the headline and the first table" );
      }

      Assert.Empty( model.UnreadFields ?? Array.Empty<string>() );
   }

   /// <summary>
   /// Fix-with 19: the SHA-256 of each results.json the report read, which the file records for every run of a session and every basis run, is in the table
   /// of the runs used (cut to twelve characters, the whole hash as the hover text), a dash where the file records none, and the field is not listed as one
   /// the page does not read.
   /// </summary>
   [Fact]
   public void TheRunsTable_ShowsTheHashOfEachResultsFileTheReportRead()
   {
      string sha = new string( 'a', 8 ) + new string( 'b', 56 );
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( root =>
      {
         ( (JsonArray)root["sessions"]![0]!["runs"]! )[0]!["resultsSha256"] = sha;
         ( (JsonArray)root["basis"]!["runs"]! )[0]!["resultsSha256"] = new string( 'c', 64 );
      } ) );

      string html = BenchConsolidatedHtml.Block( model );

      string table = Regex.Match( html, "<table class=\"bench-table\" data-table=\"runs-used\">.*?</table>", RegexOptions.Singleline ).Value;
      Assert.Contains( "<th>results.json SHA-256</th>", table );
      Assert.Contains( $"<td title=\"{sha}\"><code>aaaaaaaabbbb</code></td>", table );
      Assert.Contains( $"<td title=\"{new string( 'c', 64 )}\"><code>cccccccccccc</code></td>", table );
      Assert.Contains( "<td>-</td></tr>", table );
      Assert.Empty( model.UnreadFields ?? Array.Empty<string>() );
   }

   /// <summary>
   /// Fix-with 9: on the summary page the headline and the four metric tables come before the threshold and its basis, and the threshold section comes after
   /// the recall and the facts and costs tables. The order is the page's own and does not follow the order of the report.
   /// </summary>
   [Fact]
   public void OnTheSummary_TheResultsComeBeforeTheThresholdAndItsBasis()
   {
      WriteSet( PUBLISHED );

      string html = BenchResultsEndpoints.ListPageHtml( _root );

      int headline = html.IndexOf( "data-slot=\"headline\"", StringComparison.Ordinal );
      int[] tables = new[] { "p50Ms", "qps1", "qps8", "exactP50Ms" }.Select( m => html.IndexOf( $"data-metric=\"{m}\"", StringComparison.Ordinal ) ).ToArray();
      int recall = html.IndexOf( $"<h2>{BenchLegends.H_RECALL}</h2>", StringComparison.Ordinal );
      int why = html.IndexOf( $"<h2>{BenchLegends.H_WHY}</h2>", StringComparison.Ordinal );
      int threshold = html.IndexOf( $"<h2>{BenchLegends.H_THRESHOLD}</h2>", StringComparison.Ordinal );
      Assert.True( headline > 0 && tables.All( t => t > headline ), "the headline comes first" );
      Assert.Equal( tables.OrderBy( t => t ), tables );
      Assert.True( threshold > tables.Max() && threshold > recall && threshold > why, "the threshold and basis come after every table" );
      Assert.True( html.IndexOf( $"<h2>{BenchLegends.H_DRIFT}</h2>", StringComparison.Ordinal ) > threshold );
   }

   /// <summary>
   /// Fix-with 20: the image reference column is on the summary (a floating tag is visible only there), a dash where the file gives none, and the field is
   /// not listed as one the page does not read.
   /// </summary>
   [Fact]
   public void TheImagesTable_ShowsTheImageReference()
   {
      string html = Html( root =>
      {
         ( (JsonArray)root["images"]! )[0]!["ref"] = "chromadb/chroma:latest";
      } );
      BenchConsolidated model = BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( root => ( (JsonArray)root["images"]! )[0]!["ref"] = "chromadb/chroma:latest" ) );

      Assert.Contains( "<th>Image reference</th>", html );
      Assert.Contains( "<code>chromadb/chroma:latest</code>", html );
      Assert.Equal( "chromadb/chroma:latest", model.Images[0].Ref );
      Assert.Null( model.Images[1].Ref );
      Assert.Empty( model.UnreadFields ?? Array.Empty<string>() );
      Assert.Contains( "<td>-</td><td title=", html );
   }

   /// <summary>
   /// Fix-with 20: the clauses of the recorded texts that no sentence prints (the index clauses and the note clauses) are on the summary in a table with the
   /// class and basis the report gives them; a clause that is already a sentence is not printed a second time; a clause the report did not print says why.
   /// </summary>
   [Fact]
   public void TheRecordedTexts_ThatNoSentencePrints_AreInTheClauseTable()
   {
      string html = Html( root => root["recordedTexts"] = new JsonArray(
         RecordedText( "redis", "index", null, ( "HNSW M=16 ef_construction=128,", "documented", true, null, "src/Redis.cs#M=16" ), ( "Redis: " + BenchResultsV8Fixture.REDIS_DURABILITY, "unverified", true, null, null ) ),
         RecordedText( string.Empty, "notes", 3, ( "the native comparison table", "unverified", false, "absent-targets", null ) ) ) );

      string table = Regex.Match( html, "<table class=\"bench-table\" data-table=\"recorded-texts\">.*?</table>", RegexOptions.Singleline ).Value;
      Assert.Contains( "HNSW M=16 ef_construction=128,", table );
      Assert.Contains( "<code>src/Redis.cs#M=16</code>", table );
      Assert.Contains( "<td>notes 3</td>", table );
      Assert.Contains( "no (absent-targets)", table );
      Assert.DoesNotContain( "save &quot;300 1&quot;", table );
      Assert.DoesNotContain( BenchLegends.H_UNPLACED, html );
      Assert.DoesNotContain( "recordedTexts", html );
   }

   /// <summary>
   /// The structured record of the timed passes recorded as not held (the file's "notHeld", and the basis' "maxMoveNotHeld" and "maxMoveWithout") and of the
   /// differences between the sessions (the drift's "sessionDifferences" and "outsideLoad"), the counts of engines and clients, the sessions whose recall hits are
   /// derived and the runs that held the clock is left out of the page on purpose, because the file's sentences state it; it is therefore not listed under the fields the page does not read, and a field that
   /// is not on the list still is.
   /// </summary>
   [Fact]
   public void TheNotHeldRecords_AreLeftOutOnPurpose_AndAnUnknownFieldIsStillListed()
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         root["notHeld"] = new JsonObject { ["cells"] = new JsonArray(), ["checks"] = new JsonArray( new JsonObject { ["metric"] = "qps8", ["orderedPairs"] = 132, ["changedPairs"] = 0 } ) };
         root["basis"]!["maxMoveNotHeld"] = new JsonArray( new JsonObject { ["run"] = RUN, ["target"] = "sql", ["pass"] = "default@8" } );
         root["basis"]!["maxMoveWithout"] = new JsonObject { ["targets"] = new JsonArray( "sql" ), ["maxBp"] = 2182, ["tBp"] = 3500 };
         root["recallDerivedSessions"] = new JsonArray( "v7" );
         root["targetCount"] = 19;
         root["httpClientCount"] = 8;
         ( (JsonObject)root["basis"]!["setupSplits"]![0]!["oneEngine"]! )["moveBpExact"] = 409.5;
         ( (JsonObject)root["basis"]!["setupSplits"]![0]!["pair"]! )["moveBpExact"] = 1785.2;
         root["basis"]!["clockHeld"] = new JsonArray( new JsonObject { ["session"] = "v7", ["runs"] = 3, ["held"] = 3 } );
         root["drift"]!["sessionDifferences"] = new JsonArray( "build", "day", "outside load" );
         root["drift"]!["outsideLoad"] = new JsonArray( new JsonObject { ["session"] = "v7", ["minCpus"] = 0.2095, ["maxCpus"] = 0.2135, ["runs"] = new JsonArray() } );
      } );
      string withUnknown = BenchResultsV8Fixture.Json( root => root["somethingNew"] = 1 );

      Assert.Empty( BenchConsolidatedReader.Read( json ).UnreadFields ?? Array.Empty<string>() );
      Assert.Equal( new[] { "somethingNew" }, BenchConsolidatedReader.Read( withUnknown ).UnreadFields );
   }

   /// <summary>
   /// The fixed strings added for these pages pass the allow-list walk that every fixed string passes (the legend tests walk the whole list), and none is
   /// missing from it.
   /// </summary>
   [Fact]
   public void TheNewFixedStrings_AreInTheAllowList()
   {
      foreach( string name in new[] { "H_RUN_NOTES", "RUN_NOTES_INTRO", "RUN_NOTES_UNREADABLE", "L_NOTE_FALSE", "L_NOTE_MISLEADING", "L_NOTE_RECORDED", "L_NOTE_LISTED_IN", "L_NOTE_PLACES",
         "L_NOTE_NOT_FOUND", "RAW_NOTES_UNEDITED", "H_RUN_NOTES_SUMMARY", "RUN_NOTES_SUMMARY", "NO_RUN_NOTES", "NOT_HELD", "H_NOT_RANKED", "L_NOT_HELD", "H_RECORDED_TEXTS", "RECORDED_TEXTS", "FLAG:not-held" } )
      {
         Assert.Contains( name, BenchLegends.All.Keys );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The summary block of the fixture after an optional change.
   /// </summary>
   private static string Html( Action<JsonObject>? change )
   {
      return BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( BenchResultsV8Fixture.Json( change ) ) );
   }

   /// <summary>
   /// A results.json holding only the targets given, each with its figures and one note.
   /// </summary>
   private static string RunJson( params (string Name, string Note)[] targets )
   {
      return new JsonObject
      {
         ["targets"] = new JsonArray( targets.Select( t => (JsonNode)new JsonObject
         {
            ["name"] = t.Name,
            ["search"] = new JsonObject { ["qps"] = new JsonObject { ["8"] = 400.0, ["1"] = 200.0 }, ["p50Ms"] = 4.8, ["recall"] = 1.0 },
            ["notes"] = new JsonArray( t.Note ),
         } ).ToArray() ),
      }.ToJsonString();
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
   /// One row of a metric of the fixture's JSON.
   /// </summary>
   private static JsonObject Row( JsonObject root, string metric, string target )
   {
      JsonNode table = ( (JsonArray)root["metrics"]! ).First( m => m!["metric"]!.GetValue<string>() == metric )!;
      return (JsonObject)( (JsonArray)table["rows"]! ).First( r => r!["target"]!.GetValue<string>() == target )!;
   }

   /// <summary>
   /// A note object for the list, with two sources.
   /// </summary>
   private static JsonObject WeaviateNote( string run )
   {
      return new JsonObject
      {
         ["runs"] = new JsonArray( run ),
         ["target"] = "weaviate",
         ["field"] = "durability",
         ["statement"] = WEAVIATE,
         ["kind"] = BenchRunNotes.KIND_FALSE,
         ["note"] = "The compose file sets a persistence variable.",
         ["sources"] = new JsonArray(
            new JsonObject { ["kind"] = "file", ["ref"] = "deploy/engines/weaviate.compose.yaml#PERSISTENCE_DATA_PATH", ["value"] = "PERSISTENCE_DATA_PATH: /var/lib/weaviate" },
            new JsonObject { ["kind"] = "results", ["ref"] = $"results@{run}:targets[weaviate].durability#sets no persistence variable", ["value"] = "sets no persistence variable" } ),
      };
   }

   /// <summary>
   /// A misleading note about Vespa's growth figure, for one run or several.
   /// </summary>
   private static JsonObject VespaNote( params string[] runs )
   {
      return new JsonObject
      {
         ["runs"] = new JsonArray( runs.Select( r => (JsonNode)JsonValue.Create( r )! ).ToArray() ),
         ["target"] = "vespa",
         ["field"] = "disk.text",
         ["statement"] = VESPA_ADDED,
         ["kind"] = BenchRunNotes.KIND_MISLEADING,
         ["note"] = "The figure rests on a start reading that a witness disputes.",
         ["sources"] = new JsonArray( new JsonObject { ["kind"] = "file", ["ref"] = "observer/analysis-803.txt#X12", ["value"] = "('vespa', 2042392842, 2280491799)" } ),
      };
   }

   /// <summary>
   /// A note for the direct tests of the marker.
   /// </summary>
   private static BenchRunNote Note( string target, string statement )
   {
      return new BenchRunNote( new[] { RUN }, target, "field", statement, BenchRunNotes.KIND_MISLEADING, "A correction.", new[] { new BenchSource( "file", "x#y", "y" ) }, "published-2026-10-07" );
   }

   /// <summary>
   /// One recorded text of the file: clauses given as text, class, printed, why not printed (or null) and a basis (or null).
   /// </summary>
   private static JsonObject RecordedText( string target, string field, int? note, params (string Text, string Class, bool Printed, string? NotPrinted, string? Basis)[] clauses )
   {
      return new JsonObject
      {
         ["target"] = target,
         ["field"] = field,
         ["run"] = BenchResultsV8Fixture.V7_RUNS[0],
         ["note"] = note,
         ["clauses"] = new JsonArray( clauses.Select( c => (JsonNode)new JsonObject
         {
            ["text"] = c.Text, ["length"] = c.Text.Length, ["class"] = c.Class, ["basis"] = c.Basis == null ? new JsonArray() : new JsonArray( c.Basis ), ["printed"] = c.Printed, ["notPrinted"] = c.NotPrinted, ["unlisted"] = false,
         } ).ToArray() ),
      };
   }

   /// <summary>
   /// Writes a published or candidate folder holding the fixture with the given corrections in its list.
   /// </summary>
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
   private void WriteRun( string run, string? report = null )
   {
      string folder = Path.Combine( _root, run );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "results.json" ), BenchResultsFixtures.RUN_RESULTS_V8_JSON );
      File.WriteAllText( Path.Combine( folder, "results.md" ), report ?? REPORT );
   }

   /// <summary>
   /// Writes a list file holding the given corrections, in the object form.
   /// </summary>
   private string WriteNotesFile( params JsonObject[] notes )
   {
      string file = Path.Combine( _root, "run-page-notes.json" );
      File.WriteAllText( file, new JsonObject { ["notes"] = new JsonArray( notes.Select( n => (JsonNode)n.DeepClone() ).ToArray() ) }.ToJsonString() );
      return file;
   }

   /// <summary>
   /// A repository file found by walking up from the test output folder, or null when this machine has no checkout above it.
   /// </summary>
   internal static string? FindRepositoryFile( string relative )
   {
      for( DirectoryInfo? dir = new( AppContext.BaseDirectory ); dir != null; dir = dir.Parent )
      {
         string candidate = Path.Combine( dir.FullName, relative );
         if( File.Exists( candidate ) )
         {
            return candidate;
         }
      }

      return null;
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
