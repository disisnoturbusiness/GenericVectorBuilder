using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;

namespace GenericVectorBuilder.Web.Endpoints;

/// <summary>
/// Read-only pages for the benchmark results the Bench tool writes to disk: a summary at
/// /bench-results (headline, chart, compact table, known problems, then every run), each run at
/// /bench-results/{run} (the same summary for that run, then its full report with per-engine
/// detail folded), and the raw .md and .json files under /bench-results/{run}/{file}.
/// Why here: the numbers lived only as files on linus, so nobody could look at them from a
/// browser. Serving them from the app that produced the data keeps one place to look.
/// Only folder and file names made of letters, digits, dot, dash and underscore are accepted,
/// and every resolved path must stay inside the results folder, so a request can never read
/// anything else on the box.
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
   private const string HTML = "text/html; charset=utf-8";
   private const string RESULTS_JSON = "results.json";
   private const string CONSOLIDATED_JSON = "consolidated.json";
   private const int MAX_RUNS_LISTED = 500;
   private const string SUMMARY_NOTE = "Method problems are listed below the table.";
   private const string RUN_NOTE = "Known method problems are listed on <a href=\"/bench-results\">the summary page</a>.";
   private const string BLOCKED_NOTE = "<p class=\"errors\">An independent review blocked this set of results. It is kept as evidence of what was measured and what was wrong with it; the summary page does not use it and its numbers are not the published ones.</p>";
   private static readonly Regex SAFE_NAME = new( "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled );
   private static readonly HashSet<string> RAW_EXTENSIONS = new( StringComparer.OrdinalIgnoreCase ) { ".md", ".json" };
   private static readonly string[] REPORTS = { "results.md", "consolidated.md" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Maps the three read-only routes.
   /// </summary>
   /// <param name="app">The app.</param>
   public static void Map( WebApplication app )
   {
      string root = Path.GetFullPath( app.Configuration[CONFIG_KEY] ?? DEFAULT_ROOT );
      app.MapGet( "/bench-results", () => Results.Content( ListPageHtml( root ), HTML ) );
      app.MapGet( "/bench-results/{run}", ( string run ) => RunPageHtml( root, run ) is string page ? Results.Content( page, HTML ) : Results.NotFound( new { error = "No such benchmark run." } ) );
      app.MapGet( "/bench-results/{run}/{file}", ( string run, string file ) => RawFile( root, run, file ) );
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
      if( !SAFE_NAME.IsMatch( run ) || run.Contains( ".." ) || ( file != null && ( !SAFE_NAME.IsMatch( file ) || file.Contains( ".." ) ) ) )
      {
         return null;
      }

      string full = Path.GetFullPath( file == null ? Path.Combine( root, run ) : Path.Combine( root, run, file ) );
      return full.StartsWith( root.TrimEnd( '/' ) + "/", StringComparison.Ordinal ) ? full : null;
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
   /// root (a run folder, blocked-*, a published folder with a suffix, no date or no file) is not a
   /// candidate.
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
            .Where( x => x.Date != null && SAFE_NAME.IsMatch( x.Name ) && File.Exists( Path.Combine( root, x.Name, CONSOLIDATED_JSON ) ) )
            .OrderByDescending( x => x.Date, StringComparer.Ordinal )
            .Select( x => x.Name ).ToList();
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
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
   /// <returns>Full HTML page.</returns>
   internal static string ListPageHtml( string root )
   {
      if( !Directory.Exists( root ) )
      {
         return Page( "Benchmark results", "<h1>Benchmark results</h1><p class=\"muted\">No results folder yet.</p>" );
      }

      var body = new StringBuilder();
      IReadOnlyList<string> published = PublishedFolders( root );
      if( published.Count > 0 )
      {
         body.Append( "<h1>Vector search benchmark</h1>" );
         body.Append( PublishedSummary( root, published, out string caveats ) );
         body.Append( caveats ).Append( "<h2>All runs</h2>" );
      }
      else
      {
         body.Append( "<h1>Benchmark results</h1>" );
      }

      IEnumerable<BenchRunInfo> runs = new DirectoryInfo( root ).GetDirectories().Where( d => SAFE_NAME.IsMatch( d.Name ) )
         .OrderByDescending( d => d.Name, StringComparer.Ordinal ).Take( MAX_RUNS_LISTED ).Select( BenchRunList.Describe );
      body.Append( BenchRunList.Html( runs ) );
      return Page( "Vector search benchmark", body.ToString() );
   }

   /// <summary>
   /// Builds one run's page: its title, the summary block for that run, then its report with the
   /// per-engine detail folded, then links to its raw files.
   /// Internal so tests can render it against a folder of their own.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="run">Run folder name.</param>
   /// <returns>Full HTML page, or null when the name is refused or the folder does not exist.</returns>
   internal static string? RunPageHtml( string root, string run )
   {
      string? folder = Resolve( root, run, null );
      if( folder == null || !Directory.Exists( folder ) )
      {
         return null;
      }

      string? md = REPORTS.Select( f => Path.Combine( folder, f ) ).FirstOrDefault( File.Exists );
      ( string? title, string rest ) = md == null ? ( null, string.Empty ) : BenchMarkdown.SplitTitle( BenchRunList.ReadCapped( md ) );
      var body = new StringBuilder( "<p><a href=\"/bench-results\">Summary and all runs</a></p>" );
      body.Append( "<h1>" ).Append( BenchMarkdown.Inline( title ?? run ) ).Append( "</h1>" );
      body.Append( BenchFolderNames.IsBlocked( run ) ? BLOCKED_NOTE : string.Empty );
      body.Append( RunSummary( folder, run ) );
      body.Append( md == null ? "<p class=\"muted\">This run has no Markdown report.</p>" : "<h2>Full results</h2>" + BenchMarkdown.Render( rest ) );
      body.Append( "<h2>Raw files</h2><ul>" );
      foreach( string file in Directory.GetFiles( folder ).Select( Path.GetFileName ).OfType<string>().Where( f => RAW_EXTENSIONS.Contains( Path.GetExtension( f ) ) && SAFE_NAME.IsMatch( f ) ).OrderBy( f => f, StringComparer.Ordinal ) )
      {
         body.Append( $"<li><a href=\"/bench-results/{Enc( run )}/{Enc( file )}\">{Enc( file )}</a></li>" );
      }

      body.Append( "</ul>" );
      return Page( title ?? run, body.ToString() );
   }

   /// <summary>
   /// The path of a raw .md or .json file a request may download, or null when refused.
   /// Internal so the refusal rules can be tested.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="run">Run folder name.</param>
   /// <param name="file">File name.</param>
   /// <returns>The path, or null.</returns>
   internal static string? RawFilePath( string root, string run, string file )
   {
      string? path = Resolve( root, run, file );
      return path == null || !RAW_EXTENSIONS.Contains( Path.GetExtension( path ) ) || !File.Exists( path ) || new FileInfo( path ).Length > BenchRunList.MAX_FILE_BYTES ? null : path;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The summary block for a run folder: from its results.json, or from consolidated.json for a
   /// folder of medians, or nothing when it has neither.
   /// </summary>
   /// <param name="folder">Run folder path (already resolved).</param>
   /// <param name="run">Run folder name.</param>
   /// <returns>HTML fragment.</returns>
   private static string RunSummary( string folder, string run )
   {
      string results = Path.Combine( folder, RESULTS_JSON );
      if( File.Exists( results ) )
      {
         BenchRunInfo info = BenchRunList.Describe( new DirectoryInfo( folder ) );
         string data = $"Run started {info.When} UTC. Data: {info.Data}. Queries: {info.Queries}.";
         return SummaryOrError( () => BenchSummaryReader.FromRunResults( BenchRunList.ReadCapped( results ) ), RUN_NOTE, data );
      }

      string consolidated = Path.Combine( folder, CONSOLIDATED_JSON );
      return File.Exists( consolidated )
         ? SummaryOrError( () => BenchSummaryReader.FromConsolidated( BenchRunList.ReadCapped( consolidated ) ), RUN_NOTE, DataLineOrNull( consolidated ) )
         : string.Empty;
   }

   /// <summary>
   /// The summary of the newest published folder that can be read. When a newer folder cannot be
   /// read, an older one is shown under a visible note that says which folder failed and why; when
   /// none can be read, the error stands where the chart would be.
   /// Why fall back at all: a publish that is half written (or one bad file) must not take the
   /// whole page's numbers away; why say so: the older numbers are not the newest ones.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="folders">Published folder names, newest first (not empty).</param>
   /// <param name="caveats">The known-problems box built from the data of the folder that was shown (or saying that none could be derived when no folder could be read).</param>
   /// <returns>HTML fragment.</returns>
   private static string PublishedSummary( string root, IReadOnlyList<string> folders, out string caveats )
   {
      string? failedFolder = null;
      string? failure = null;
      foreach( string folder in folders )
      {
         string path = Path.Combine( root, folder, CONSOLIDATED_JSON );
         try
         {
            string json = BenchRunList.ReadCapped( path );
            BenchSummary summary = BenchSummaryReader.FromConsolidated( json );
            string block = BenchSummaryHtml.Block( summary, SUMMARY_NOTE, BenchSummaryReader.DataLine( json ) );
            string source = $"<p class=\"bench-source muted\">Numbers from <a href=\"/bench-results/{Enc( folder )}\">{Enc( folder )}</a>.</p>";
            string fallback = failedFolder == null ? string.Empty : $"<p class=\"errors\">The newest published results, {Enc( failedFolder )}, could not be read ({Enc( failure ?? "no reason given" )}), so the page shows the older {Enc( folder )}.</p>";
            caveats = BenchSummaryHtml.Caveats( summary );
            return fallback + source + block;
         }
         catch( Exception ex ) when( ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException )
         {
            failedFolder ??= folder;
            failure ??= ex.Message;
         }
      }

      caveats = BenchSummaryHtml.Caveats( null );
      return $"<p class=\"errors\">The summary numbers could not be read: {Enc( failure ?? "no reason given" )}</p>";
   }

   /// <summary>
   /// The line that says what a consolidated.json was measured on, or null when it cannot be built
   /// (the summary then reports the unreadable file itself).
   /// </summary>
   /// <param name="path">Full path of the consolidated.json.</param>
   /// <returns>The line, or null.</returns>
   private static string? DataLineOrNull( string path )
   {
      try
      {
         return BenchSummaryReader.DataLine( BenchRunList.ReadCapped( path ) );
      }
      catch( Exception ex ) when( ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException )
      {
         return null;
      }
   }

   /// <summary>
   /// Renders the summary block, or a visible error when the numbers cannot be read.
   /// Why not throw: the rest of the page (the run list, the full report) is still worth showing,
   /// and the error is printed where the chart would be, so nobody mistakes it for no data.
   /// </summary>
   /// <param name="read">Reads the numbers.</param>
   /// <param name="noteHtml">Trusted fixed markup for the "not final" line.</param>
   /// <param name="dataLine">Plain text data description, or null.</param>
   /// <returns>HTML fragment.</returns>
   private static string SummaryOrError( Func<BenchSummary> read, string noteHtml, string? dataLine )
   {
      try
      {
         return BenchSummaryHtml.Block( read(), noteHtml, dataLine );
      }
      catch( Exception ex ) when( ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException )
      {
         return $"<p class=\"errors\">The summary numbers could not be read: {Enc( ex.Message )}</p>";
      }
   }

   /// <summary>
   /// Serves a raw .md or .json file from a run folder as text.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="run">Run folder name.</param>
   /// <param name="file">File name.</param>
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
