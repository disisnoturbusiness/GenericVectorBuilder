using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;

namespace GenericVectorBuilder.Web.Endpoints;

/// <summary>
/// Read-only pages for the benchmark results the Bench tool writes to disk: a summary at
/// /bench-results (the newest published consolidated set, then every run), each folder at
/// /bench-results/{folder} (a consolidated set drawn from its consolidated.json, a run drawn from its
/// results.json, then the folder's report with the per-engine detail folded), and the raw .md and .json
/// files under /bench-results/{folder}/{file} (and one folder deeper, /bench-results/{folder}/{dir}/{file}).
/// Why here: the numbers lived only as files on linus, so nobody could look at them from a
/// browser. Serving them from the app that produced the data keeps one place to look.
/// Only folder and file names made of letters, digits, dot, dash and underscore are accepted,
/// and every resolved path must stay inside the results folder, so a request can never read
/// anything else on the box.
/// What the folder name decides (see <see cref="BenchFolderNames"/>): only "published-yyyy-mm-dd" can be the
/// summary page; candidate, withdrawn and blocked folders are served at their own address with a banner, and a
/// file in a shape older than v8 is shown as an older-format notice, never as a result.
/// </summary>
public static class BenchResultsEndpoints
{
   #region Data Members

   /// <summary>
   /// Start of the name of every folder a consolidate output is published in: "published-" and the
   /// date, e.g. "published-2026-10-05", and nothing after the date. The summary page is built from the
   /// newest of them; a folder with any other name (blocked-2026-10-05-v6, published-2026-10-05b, a run
   /// folder) is never the summary, so a set that was blocked stops being served by renaming it.
   /// </summary>
   public const string PUBLISHED_PREFIX = BenchFolderNames.PUBLISHED_PREFIX;

   private const string DEFAULT_ROOT = "/home/dan/ForClaude/GenericVectorBuilder/bench-results";
   private const string CONFIG_KEY = "Gvb:BenchResultsPath";
   private const string NOTES_CONFIG_KEY = "Gvb:BenchRunNotesPath";
   private const string HTML = "text/html; charset=utf-8";
   private const string RESULTS_JSON = "results.json";
   private const string CONSOLIDATED_JSON = "consolidated.json";
   private const int MAX_RUNS_LISTED = 500;
   private static readonly HashSet<string> RAW_EXTENSIONS = new( StringComparer.OrdinalIgnoreCase ) { ".md", ".json" };
   private static readonly string[] REPORTS = { "results.md", "consolidated.md" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Maps the read-only routes.
   /// </summary>
   /// <param name="app">The app.</param>
   public static void Map( WebApplication app )
   {
      string root = Path.GetFullPath( app.Configuration[CONFIG_KEY] ?? DEFAULT_ROOT );
      string notesFile = NotesFilePath( root, app.Configuration[NOTES_CONFIG_KEY] );
      app.MapGet( "/bench-results", () => Results.Content( ListPageHtml( root, notesFile ), HTML ) );
      app.MapGet( "/bench-results/{run}", ( string run ) => RunPageHtml( root, run, notesFile ) is string page ? Results.Content( page, HTML ) : Results.NotFound( new { error = "No such benchmark run." } ) );
      app.MapGet( "/bench-results/{run}/{file}", ( string run, string file ) => RawFile( root, run, file ) );
      app.MapGet( "/bench-results/{run}/{dir}/{file}", ( string run, string dir, string file ) => RawFile( root, run, $"{dir}/{file}" ) );
   }

   /// <summary>
   /// Resolves a run folder or a file inside it, refusing anything unsafe or outside the root.
   /// Public so the path rules can be tested directly.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="run">Run folder name.</param>
   /// <param name="file">File name inside it, or null for the folder itself.</param>
   /// <returns>The full path, or null when the request is refused.</returns>
   public static string? Resolve( string root, string run, string? file )
   {
      if( !BenchFolderNames.IsSafe( run ) || ( file != null && !BenchFolderNames.IsSafe( file ) ) )
      {
         return null;
      }

      string full = Path.GetFullPath( file == null ? Path.Combine( root, run ) : Path.Combine( root, run, file ) );
      return full.StartsWith( root.TrimEnd( '/' ) + "/", StringComparison.Ordinal ) ? full : null;
   }

   /// <summary>
   /// The path of the list file of corrections the run pages read for a set that carries no list of its own: the configured path, or
   /// <see cref="BenchRunNotes.DEFAULT_FILE"/> in the repository that holds the results root (the folder above it).
   /// Public so the rule can be tested.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="configured">The configured path, or null.</param>
   /// <returns>The full path; the file may not exist.</returns>
   public static string NotesFilePath( string root, string? configured )
   {
      return Path.GetFullPath( string.IsNullOrWhiteSpace( configured ) ? Path.Combine( root, "..", BenchRunNotes.DEFAULT_FILE ) : configured );
   }

   /// <summary>
   /// Renders a Bench report as HTML (see <see cref="BenchMarkdown.Render"/>). Kept here so
   /// existing callers and tests keep working.
   /// </summary>
   /// <param name="markdown">Report text.</param>
   /// <returns>HTML fragment.</returns>
   public static string RenderMarkdown( string markdown )
   {
      return BenchMarkdown.Render( markdown );
   }

   /// <summary>
   /// The published folders under the results root, newest first: names that read "published-" and a
   /// valid date (yyyy-mm-dd) and nothing else, that hold a consolidated.json. Anything else in the
   /// root (a run folder, candidate-*, withdrawn-*, blocked-*, a published folder with a suffix, no date or no
   /// file) is not a candidate for the summary.
   /// Why by the date in the name and not by file times: a copy or a checkout changes file times,
   /// and the name is what the person who published it wrote. Why no suffix: a second publish on
   /// the same day (published-2026-10-05b) was the blocked set of 5 Oct, and a name the page serves
   /// must be one a reviewer can decide on; a blocked set is renamed out of the pattern.
   /// Internal so tests can list a folder of their own.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <returns>Folder names, newest first; empty when there are none or the root cannot be read.</returns>
   internal static IReadOnlyList<string> PublishedFolders( string root )
   {
      try
      {
         return new DirectoryInfo( root ).GetDirectories()
            .Select( d => ( d.Name, Date: BenchFolderNames.PublishedDate( d.Name ) ) )
            .Where( x => x.Date != null && BenchFolderNames.IsSafe( x.Name ) && File.Exists( Path.Combine( root, x.Name, CONSOLIDATED_JSON ) ) )
            .OrderByDescending( x => x.Date, StringComparer.Ordinal )
            .Select( x => x.Name ).ToList();
      }
      catch( Exception ex ) when( IsReadError( ex ) )
      {
         return Array.Empty<string>();
      }
   }

   /// <summary>
   /// Builds the /bench-results page: the summary from the newest published consolidated.json that
   /// can be read (see <see cref="PublishedFolders"/>), then every run. With no published folder it
   /// falls back to the run list alone.
   /// Internal so tests can render it against a folder of their own.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="notesFile">The list file of corrections the run pages read, or null when the caller does not check for it.</param>
   /// <returns>Full HTML page.</returns>
   internal static string ListPageHtml( string root, string? notesFile = null )
   {
      if( !Directory.Exists( root ) )
      {
         return Page( "Benchmark results", "<h1>Benchmark results</h1><p class=\"muted\">No results folder yet.</p>" );
      }

      var body = new StringBuilder();
      IReadOnlyList<string> published = PublishedFolders( root );
      if( published.Count > 0 )
      {
         body.Append( "<h1>Vector search benchmark</h1>" ).Append( PublishedSummary( root, published, notesFile ) ).Append( "<h2>All runs</h2>" );
      }
      else
      {
         body.Append( "<h1>Benchmark results</h1>" );
      }

      body.Append( RunTable( root ) );
      return Page( "Vector search benchmark", body.ToString() );
   }

   /// <summary>
   /// Builds one folder's page: its title and banner, the block for that folder (a consolidated set or a run),
   /// then its report with the per-engine detail folded, then links to its raw files.
   /// Internal so tests can render it against a folder of their own.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="run">Folder name.</param>
   /// <param name="notesFile">The list file of corrections to read for a run whose sets carry none, or null for no list file.</param>
   /// <returns>Full HTML page, or null when the name is refused or the folder does not exist.</returns>
   internal static string? RunPageHtml( string root, string run, string? notesFile = null )
   {
      string? folder = Resolve( root, run, null );
      if( folder == null || !Directory.Exists( folder ) )
      {
         return null;
      }

      string? md = REPORTS.Select( f => Path.Combine( folder, f ) ).FirstOrDefault( File.Exists );
      ( string? title, string rest, string? reportError ) = ReadReport( md );
      var body = new StringBuilder( "<p><a href=\"/bench-results\">Summary and all runs</a></p>" );
      body.Append( "<h1>" ).Append( BenchMarkdown.Inline( title ?? run ) ).Append( "</h1>" );
      bool isSet = File.Exists( Path.Combine( folder, CONSOLIDATED_JSON ) ) && !File.Exists( Path.Combine( folder, RESULTS_JSON ) );
      bool recordOnly = isSet && IsRecordOnly( folder, run );
      if( BenchFolderNames.Banner( BenchFolderNames.KindOf( run ), recordOnly ) is string banner )
      {
         body.Append( $"<p class=\"errors bench-banner\">{Enc( banner )}</p>" );
      }

      string text = BenchReportStrip.Apply( rest, out int stripped );
      IReadOnlyList<BenchFolderFacts> facts = isSet ? Array.Empty<BenchFolderFacts>() : BenchRunUse.Scan( root );
      string? listError = null;
      IReadOnlyList<BenchRunNote> notes = isSet ? Array.Empty<BenchRunNote>() : BenchRunUse.RunNotesFor( facts, run, BenchRunNotes.ReadFile( notesFile, out listError ) );
      text = BenchRunNotes.Apply( text, notes, out IReadOnlyList<int> found );
      body.Append( isSet ? SetBlock( folder ) : RunBlock( folder, run, facts, BenchRunNotes.Block( notes, found, listError ) ) );
      string heading = recordOnly ? BenchLegends.H_REPORT_RECORD : BenchLegends.H_REPORT_TEXT;
      body.Append( reportError != null ? $"<p class=\"errors\">The report could not be read: {Enc( reportError )}</p>" : ReportHtml( md, text, stripped, isSet, heading ) );
      body.Append( RawFiles( folder, run, md == null ? null : Path.GetFileName( md ), stripped, notes.Count > 0 ) );
      return Page( title ?? run, body.ToString() );
   }

   /// <summary>
   /// The path of a raw .md or .json file a request may download, or null when refused. The file name may carry
   /// one folder level ("observer/summary.json"); each segment must be a safe name.
   /// Internal so the refusal rules can be tested.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="run">Run folder name.</param>
   /// <param name="file">File name, or "folder/file".</param>
   /// <returns>The path, or null.</returns>
   internal static string? RawFilePath( string root, string run, string file )
   {
      string[] parts = file.Split( '/' );
      if( parts.Length is < 1 or > 2 || parts.Any( p => !BenchFolderNames.IsSafe( p ) ) )
      {
         return null;
      }

      string? path = parts.Length == 1 ? Resolve( root, run, parts[0] ) : Resolve( root, run, null ) == null ? null : ResolveNested( root, run, parts[0], parts[1] );
      return path == null || !RAW_EXTENSIONS.Contains( Path.GetExtension( path ) ) || !File.Exists( path ) || new FileInfo( path ).Length > BenchRunList.MAX_FILE_BYTES ? null : path;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The "All runs" table: every safe folder name under the root, a published set first, then a candidate, a withdrawn set and a blocked set, then the runs, each group newest name first, each marked with the folders that use it; the error
   /// when the root cannot be listed.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <returns>HTML fragment.</returns>
   private static string RunTable( string root )
   {
      try
      {
         IReadOnlyList<BenchFolderFacts> facts = BenchRunUse.Scan( root );
         IEnumerable<BenchRunInfo> runs = new DirectoryInfo( root ).GetDirectories().Where( d => BenchFolderNames.IsSafe( d.Name ) )
            .OrderBy( d => BenchFolderNames.ListPriority( d.Name ) ).ThenByDescending( d => d.Name, StringComparer.Ordinal ).Take( MAX_RUNS_LISTED ).Select( d => BenchRunList.Describe( d, facts ) ).ToList();
         return BenchRunList.Html( runs );
      }
      catch( Exception ex ) when( IsReadError( ex ) )
      {
         return $"<p class=\"errors\">The list of runs could not be read: {Enc( ex.Message )}</p>";
      }
   }

   /// <summary>
   /// True for the exceptions a malformed or unreadable results file raises; one list for every page, so a bad
   /// folder shows an error block and never a 500.
   /// </summary>
   /// <param name="ex">The exception.</param>
   /// <returns>True when the page should print it as an error block.</returns>
   private static bool IsReadError( Exception ex )
   {
      return ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException;
   }

   /// <summary>
   /// A file one folder below a run folder, resolved inside the root.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="run">Run folder name.</param>
   /// <param name="dir">Folder below it.</param>
   /// <param name="file">File name.</param>
   /// <returns>The full path, or null when it falls outside the root.</returns>
   private static string? ResolveNested( string root, string run, string dir, string file )
   {
      string full = Path.GetFullPath( Path.Combine( root, run, dir, file ) );
      return full.StartsWith( root.TrimEnd( '/' ) + "/", StringComparison.Ordinal ) ? full : null;
   }

   /// <summary>
   /// Reads a folder's Markdown report and splits off its title.
   /// </summary>
   /// <param name="md">The report path, or null when the folder has none.</param>
   /// <returns>The title (null when none), the rest of the text, and the error when the file could not be read.</returns>
   private static ( string? Title, string Body, string? Error ) ReadReport( string? md )
   {
      if( md == null )
      {
         return ( null, string.Empty, null );
      }

      try
      {
         ( string? title, string rest ) = BenchMarkdown.SplitTitle( BenchRunList.ReadCapped( md ) );
         return ( title, rest, null );
      }
      catch( Exception ex ) when( IsReadError( ex ) )
      {
         return ( null, string.Empty, ex.Message );
      }
   }

   /// <summary>
   /// The report of a folder as HTML: for a run, under "Full results" with the sentences that predate the v8
   /// framing left out and a note saying so; for a consolidated set, inside a closed details.
   /// </summary>
   /// <param name="md">The report path, or null.</param>
   /// <param name="text">The report text after its title, with the retired sentences already left out (see <see cref="BenchReportStrip"/>).</param>
   /// <param name="removed">How many sentences were left out.</param>
   /// <param name="isSet">True for a consolidated set.</param>
   /// <param name="heading">The summary line of a consolidated set's details block.</param>
   /// <returns>HTML fragment.</returns>
   private static string ReportHtml( string? md, string text, int removed, bool isSet, string heading )
   {
      if( md == null )
      {
         return "<p class=\"muted\">This folder has no Markdown report.</p>";
      }

      string note = removed > 0 ? $"<aside class=\"bench-caveats\"><p>{Enc( BenchLegends.STRIPPED )} {removed}</p></aside>" : string.Empty;
      string asWritten = isSet ? string.Empty : $"<p class=\"bench-legend muted\">{Enc( BenchLegends.RUN_REPORT_AS_WRITTEN )} {Enc( BenchLegends.RUN_REPORT_UNCHECKED )}</p>";
      return isSet
         ? $"<details class=\"bench-target\"><summary>{Enc( heading )}</summary>{note}{BenchMarkdown.Render( text )}</details>"
         : $"<h2>Full results</h2>{asWritten}{note}{BenchMarkdown.Render( text )}";
   }

   /// <summary>
   /// True when a set's Markdown report is kept as a record and is not the report of a current result: the set is withdrawn or blocked, or its file is
   /// not in the v8 shape, or cannot be read. Why: such a report can hold a ranked table, and a table on this page must not read as the page's result.
   /// </summary>
   /// <param name="folder">Folder path (already resolved).</param>
   /// <param name="name">Folder name.</param>
   /// <returns>True when the report is a record.</returns>
   private static bool IsRecordOnly( string folder, string name )
   {
      if( BenchFolderNames.KindOf( name ) is BenchFolderKind.Withdrawn or BenchFolderKind.Blocked )
      {
         return true;
      }

      try
      {
         return BenchConsolidatedReader.Detect( BenchRunList.ReadCapped( Path.Combine( folder, CONSOLIDATED_JSON ) ) ) != BenchShape.Consolidated;
      }
      catch( Exception ex ) when( IsReadError( ex ) )
      {
         return true;
      }
   }

   /// <summary>
   /// The links to a folder's raw files, including the files one folder below it.
   /// </summary>
   /// <param name="folder">Folder path (already resolved).</param>
   /// <param name="run">Folder name.</param>
   /// <param name="report">The file name of the report the page printed a copy of, or null when the folder has none.</param>
   /// <param name="stripped">How many sentences the page left out of its copy of the report; when it is above zero the raw file is said to hold them.</param>
   /// <param name="corrected">True when the page printed corrections of the run's recorded statements; the raw files are then said to hold the statements as written.</param>
   /// <returns>HTML fragment.</returns>
   private static string RawFiles( string folder, string run, string? report, int stripped, bool corrected = false )
   {
      var files = new List<string>();
      try
      {
         files.AddRange( Directory.GetFiles( folder ).Select( Path.GetFileName ).OfType<string>().Where( f => RAW_EXTENSIONS.Contains( Path.GetExtension( f ) ) && BenchFolderNames.IsSafe( f ) ) );
         foreach( string dir in Directory.GetDirectories( folder ).Select( Path.GetFileName ).OfType<string>().Where( BenchFolderNames.IsSafe ) )
         {
            files.AddRange( Directory.GetFiles( Path.Combine( folder, dir ) ).Select( Path.GetFileName ).OfType<string>()
               .Where( f => RAW_EXTENSIONS.Contains( Path.GetExtension( f ) ) && BenchFolderNames.IsSafe( f ) ).Select( f => $"{dir}/{f}" ) );
         }
      }
      catch( Exception ex ) when( IsReadError( ex ) )
      {
         return $"<h2>Raw files</h2><p class=\"errors\">The file list could not be read: {Enc( ex.Message )}</p>";
      }

      var html = new StringBuilder( "<h2>Raw files</h2>" );
      if( report != null && stripped > 0 )
      {
         html.Append( $"<p class=\"muted bench-raw-note\"><code>{Enc( report )}</code> {Enc( BenchLegends.RAW_UNEDITED )} {stripped}</p>" );
      }

      if( corrected )
      {
         html.Append( $"<p class=\"muted bench-raw-note\" data-note=\"corrections\">{Enc( BenchLegends.RAW_NOTES_UNEDITED )}</p>" );
      }

      html.Append( "<ul>" );
      foreach( string file in files.OrderBy( f => f, StringComparer.Ordinal ) )
      {
         html.Append( $"<li><a href=\"/bench-results/{Enc( run )}/{Enc( file )}\">{Enc( file )}</a></li>" );
      }

      return html.Append( "</ul>" ).ToString();
   }

   /// <summary>
   /// The block for a consolidated set's own page: the whole page for a v8 file, an older-format notice for any
   /// other shape, or the error when the file cannot be read.
   /// </summary>
   /// <param name="folder">Folder path (already resolved).</param>
   /// <returns>HTML fragment.</returns>
   private static string SetBlock( string folder )
   {
      try
      {
         return ConsolidatedHtml( BenchRunList.ReadCapped( Path.Combine( folder, CONSOLIDATED_JSON ) ), Path.GetFileName( folder ) );
      }
      catch( Exception ex ) when( IsReadError( ex ) )
      {
         return $"<p class=\"errors\">{Enc( BenchLegends.UNREADABLE )}: {Enc( ex.Message )}</p>";
      }
   }

   /// <summary>
   /// The block for one run folder: the run's table, then which consolidated folders use it, the note on why they
   /// reuse it and the clock warnings they dropped.
   /// </summary>
   /// <param name="folder">Folder path (already resolved).</param>
   /// <param name="run">Folder name.</param>
   /// <param name="facts">Every consolidated folder's facts.</param>
   /// <param name="notesHtml">The block of corrections to this run's recorded statements (see <see cref="BenchRunNotes.Block"/>); empty for none.</param>
   /// <returns>HTML fragment.</returns>
   private static string RunBlock( string folder, string run, IReadOnlyList<BenchFolderFacts> facts, string notesHtml )
   {
      string results = Path.Combine( folder, RESULTS_JSON );
      if( !File.Exists( results ) )
      {
         return notesHtml;
      }

      var html = new StringBuilder();
      try
      {
         BenchRunInfo info = BenchRunList.Describe( new DirectoryInfo( folder ) );
         html.Append( $"<p class=\"bench-data muted\">Run started {Enc( info.When )} UTC. Data: {Enc( info.Data )}. Queries: {Enc( info.Queries )}.</p>" );
         html.Append( UsedBy( facts, run ) );
         html.Append( notesHtml );
         html.Append( BenchRunTable.Block( BenchRunTable.Read( BenchRunList.ReadCapped( results ) ), BenchRunUse.NotesFor( facts, run ) ) );
      }
      catch( Exception ex ) when( IsReadError( ex ) )
      {
         html.Append( $"<p class=\"errors\">{Enc( BenchLegends.UNREADABLE )}: {Enc( ex.Message )}</p>" );
      }

      return html.ToString();
   }

   /// <summary>
   /// The paragraph that says which consolidated folders use a run, the reuse sentences that belong to this run (see <see cref="BenchRunUse.ReuseFor"/>:
   /// the sentences of the session the run belongs to, in the part it plays), and a banner for each clock warning a folder dropped for this run.
   /// </summary>
   /// <param name="facts">Every consolidated folder's facts.</param>
   /// <param name="run">Run folder name.</param>
   /// <returns>HTML fragment; empty when no folder uses the run.</returns>
   private static string UsedBy( IReadOnlyList<BenchFolderFacts> facts, string run )
   {
      IReadOnlyList<( BenchFolderFacts Folder, string Role )> marks = BenchRunUse.MarksFor( facts, run );
      var html = new StringBuilder();
      if( marks.Count > 0 )
      {
         html.Append( $"<p class=\"bench-sub\">{Enc( BenchLegends.USED_BY )}: {BenchRunList.MarkLinks( marks.Select( m => ( m.Folder.Folder, m.Role ) ).ToList() )}</p>" );
         foreach( BenchSentence reuse in BenchRunUse.ReuseFor( facts, run ).Select( r => r.Sentence ).DistinctBy( r => r.Text ) )
         {
            html.Append( BenchConsolidatedHtml.Sentence( reuse ) );
         }
      }

      foreach( IGrouping<BenchDroppedWarning, ( string Folder, BenchDroppedWarning Warning )> group in BenchRunUse.DroppedFor( facts, run ).GroupBy( d => d.Warning with { Run = string.Empty } ) )
      {
         string droppers = string.Join( ", ", group.Select( d => d.Folder ).Distinct( StringComparer.Ordinal ).Select( d => $"<a href=\"/bench-results/{Enc( d )}\">{Enc( d )}</a>" ) );
         BenchDroppedWarning warning = group.Key;
         html.Append( $"<aside class=\"bench-caveats\"><p>{droppers} {Enc( BenchLegends.DROPPED_WARNING )}</p><ul>" );
         html.Append( $"<li>{Enc( BenchLegends.L_WARNING_AS_RECORDED )} {Enc( warning.Text )}</li>" );
         html.Append( $"<li>{Enc( BenchLegends.L_ENGINE_MHZ )} {Enc( warning.EngineMedianMhz?.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture ) ?? "not recorded" )}</li>" );
         html.Append( $"<li>{Enc( BenchLegends.L_CLIENT_MHZ )} {Enc( warning.ClientMedianMhz?.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture ) ?? "not recorded" )}</li></ul></aside>" );
      }

      return html.ToString();
   }

   /// <summary>
   /// The page block for a consolidated.json of any shape: the whole page for a v8 file, the older-format
   /// notice for any other.
   /// </summary>
   /// <param name="json">File text.</param>
   /// <param name="folder">Folder name, for the links of the older-format notice.</param>
   /// <returns>HTML fragment.</returns>
   private static string ConsolidatedHtml( string json, string folder )
   {
      return BenchConsolidatedReader.Detect( json ) == BenchShape.Consolidated
         ? BenchConsolidatedHtml.Block( BenchConsolidatedReader.Read( json ) )
         : $"<aside class=\"bench-caveats\"><p>{Enc( BenchLegends.OLDER_FORMAT )}</p><p><a href=\"/bench-results/{Enc( folder )}/{CONSOLIDATED_JSON}\">{CONSOLIDATED_JSON}</a></p></aside>";
   }

   /// <summary>
   /// The summary of the newest published folder that can be read. When a newer folder cannot be
   /// read, an older one is shown under a visible note that says which folder failed and why; when
   /// none can be read, the error stands where the tables would be.
   /// Why fall back at all: a publish that is half written (or one bad file) must not take the
   /// whole page's numbers away; why say so: the older numbers are not the newest ones.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="folders">Published folder names, newest first (not empty).</param>
   /// <param name="notesFile">The list file of corrections the run pages read, or null when the caller does not check for it.</param>
   /// <returns>HTML fragment.</returns>
   private static string PublishedSummary( string root, IReadOnlyList<string> folders, string? notesFile )
   {
      string? failedFolder = null;
      string? failure = null;
      foreach( string folder in folders )
      {
         try
         {
            string json = BenchRunList.ReadCapped( Path.Combine( root, folder, CONSOLIDATED_JSON ) );
            string block = ConsolidatedHtml( json, folder );
            string source = $"<p class=\"bench-source muted\">Numbers from <a href=\"/bench-results/{Enc( folder )}\">{Enc( folder )}</a>.</p>";
            string fallback = failedFolder == null ? string.Empty : $"<p class=\"errors\">The newest published results, {Enc( failedFolder )}, could not be read ({Enc( failure ?? "no reason given" )}), so the page shows the older {Enc( folder )}.</p>";
            return fallback + source + NoListNotice( json, notesFile ) + block;
         }
         catch( Exception ex ) when( IsReadError( ex ) )
         {
            failedFolder ??= folder;
            failure ??= ex.Message;
         }
      }

      return $"<p class=\"errors\">{Enc( BenchLegends.UNREADABLE )}: {Enc( failure ?? "no reason given" )}</p>";
   }

   /// <summary>
   /// The notice for a summary whose set lists no corrections to recorded statements while the list file is absent: the run pages then print every recorded
   /// statement as written, and a reader is told so. Why: a list that is missing must be seen, and not taken for a list with nothing in it (an empty list in
   /// either place prints no notice).
   /// </summary>
   /// <param name="json">The set's consolidated.json text.</param>
   /// <param name="notesFile">The list file's path, or null when the caller does not check for it.</param>
   /// <returns>HTML fragment; empty when the set lists corrections, the file exists, the caller does not check, or the set is not in the v8 shape.</returns>
   private static string NoListNotice( string json, string? notesFile )
   {
      if( notesFile == null || File.Exists( notesFile ) || BenchConsolidatedReader.Detect( json ) != BenchShape.Consolidated )
      {
         return string.Empty;
      }

      using JsonDocument doc = JsonDocument.Parse( json );
      return doc.RootElement.TryGetProperty( BenchRunNotes.FIELD, out _ ) ? string.Empty : $"<p class=\"errors\" data-notice=\"no-corrections-list\">{Enc( BenchLegends.NO_RUN_NOTES )}</p>";
   }

   /// <summary>
   /// Serves a raw .md or .json file from a run folder as text.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="run">Run folder name.</param>
   /// <param name="file">File name, or "folder/file".</param>
   /// <returns>The file, or 404.</returns>
   private static IResult RawFile( string root, string run, string file )
   {
      string? path = RawFilePath( root, run, file );
      if( path == null )
      {
         return Results.NotFound( new { error = "No such file." } );
      }

      string type = Path.GetExtension( path ).Equals( ".json", StringComparison.OrdinalIgnoreCase ) ? "application/json" : "text/plain";
      return Results.File( path, $"{type}; charset=utf-8" );
   }

   /// <summary>
   /// HTML-encodes text.
   /// </summary>
   /// <param name="text">Text.</param>
   /// <returns>Encoded text.</returns>
   private static string Enc( string text )
   {
      return WebUtility.HtmlEncode( text );
   }

   /// <summary>
   /// Wraps a body in a page that uses the app's own stylesheet.
   /// </summary>
   /// <param name="title">Page title.</param>
   /// <param name="body">Body HTML.</param>
   /// <returns>Full HTML page.</returns>
   private static string Page( string title, string body )
   {
      return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
         + $"<meta name=\"color-scheme\" content=\"light dark\"><title>{Enc( title )}</title><link rel=\"stylesheet\" href=\"/style.css\"></head><body>"
         + "<header class=\"topbar\"><h1><a href=\"/\">GenericVectorBuilder</a></h1><a href=\"/bench-results\">Benchmark results</a></header>"
         + $"<main class=\"card bench\">{body}</main></body></html>";
   }

   #endregion Private Methods
}
