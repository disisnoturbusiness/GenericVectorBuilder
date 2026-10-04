using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.Endpoints;

/// <summary>
/// Read-only pages for the benchmark results the Bench tool writes to disk: a list of runs at
/// /bench-results, each run's results.md rendered as HTML at /bench-results/{run}, and the raw
/// .md and .json files under /bench-results/{run}/{file}.
/// Why here: the numbers lived only as files on linus, so nobody could look at them from a
/// browser. Serving them from the app that produced the data keeps one place to look.
/// Only folder and file names made of letters, digits, dot, dash and underscore are accepted,
/// and every resolved path must stay inside the results folder, so a request can never read
/// anything else on the box.
/// </summary>
public static class BenchResultsEndpoints
{
   #region Data Members

   private const string DEFAULT_ROOT = "/home/dan/ForClaude/GenericVectorBuilder/bench-results";
   private const string CONFIG_KEY = "Gvb:BenchResultsPath";
   private const int MAX_FILE_BYTES = 5 * 1024 * 1024;
   private static readonly Regex SAFE_NAME = new( "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled );
   private static readonly Regex INLINE_CODE = new( "`([^`]+)`", RegexOptions.Compiled );
   private static readonly Regex BOLD = new( "\\*\\*([^*]+)\\*\\*", RegexOptions.Compiled );
   private static readonly HashSet<string> RAW_EXTENSIONS = new( StringComparer.OrdinalIgnoreCase ) { ".md", ".json" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Maps the three read-only routes.
   /// </summary>
   /// <param name="app">The app.</param>
   public static void Map( WebApplication app )
   {
      string root = Path.GetFullPath( app.Configuration[CONFIG_KEY] ?? DEFAULT_ROOT );
      app.MapGet( "/bench-results", () => Results.Content( ListPage( root ), "text/html; charset=utf-8" ) );
      app.MapGet( "/bench-results/{run}", ( string run ) => RunPage( root, run ) );
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
   /// Renders the small Markdown subset the Bench report uses (headings, pipe tables, fenced
   /// code, bullets, paragraphs, inline code, bold) as HTML. Everything is HTML-escaped first,
   /// so file content can never inject markup. Public so it can be tested directly.
   /// </summary>
   /// <param name="markdown">Report text.</param>
   /// <returns>HTML fragment.</returns>
   public static string RenderMarkdown( string markdown )
   {
      var html = new StringBuilder();
      string[] lines = markdown.Replace( "\r", string.Empty ).Split( '\n' );
      for( int i = 0; i < lines.Length; )
      {
         i = RenderBlock( lines, i, html );
      }

      return html.ToString();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds the list page: every run folder, newest first, with its pipeline and target count
   /// when its results.json can be read.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <returns>Full HTML page.</returns>
   private static string ListPage( string root )
   {
      var body = new StringBuilder( "<h1>Benchmark results</h1>" );
      if( !Directory.Exists( root ) )
      {
         body.Append( "<p class=\"muted\">No results folder yet.</p>" );
         return Page( "Benchmark results", body.ToString() );
      }

      body.Append( "<p class=\"muted\">Every run the benchmark wrote, newest first. Each opens as a table.</p><ul class=\"runs-list\">" );
      foreach( DirectoryInfo dir in new DirectoryInfo( root ).GetDirectories().Where( d => SAFE_NAME.IsMatch( d.Name ) ).OrderByDescending( d => d.Name, StringComparer.Ordinal ) )
      {
         string summary = Summarize( dir.FullName );
         body.Append( $"<li><a href=\"/bench-results/{Enc( dir.Name )}\">{Enc( dir.Name )}</a> <span class=\"muted\">{Enc( summary )}</span></li>" );
      }

      body.Append( "</ul>" );
      return Page( "Benchmark results", body.ToString() );
   }

   /// <summary>
   /// One-line summary of a run folder from its results.json (pipeline, targets, queries), or a
   /// note when the folder holds something else (e.g. consolidated numbers).
   /// </summary>
   /// <param name="folder">Run folder.</param>
   /// <returns>Summary text.</returns>
   private static string Summarize( string folder )
   {
      string json = Path.Combine( folder, "results.json" );
      if( !File.Exists( json ) )
      {
         string[] files = Directory.GetFiles( folder ).Select( Path.GetFileName ).OfType<string>().ToArray();
         return files.Length == 0 ? "empty" : string.Join( ", ", files );
      }

      try
      {
         using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( json ) );
         JsonElement r = doc.RootElement;
         int targets = r.TryGetProperty( "targets", out JsonElement t ) ? t.GetArrayLength() : 0;
         string pipeline = r.TryGetProperty( "pipeline", out JsonElement p ) ? p.GetString() ?? "?" : "?";
         string queries = r.TryGetProperty( "queries", out JsonElement q ) ? q.ToString() : "?";
         return $"pipeline {pipeline}, {targets} targets, queries {queries}";
      }
      catch( Exception ex ) when( ex is JsonException or IOException )
      {
         return $"results.json unreadable: {ex.Message}";
      }
   }

   /// <summary>
   /// Renders one run's results.md (or consolidated.md) as a page, with links to its raw files.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="run">Run folder name.</param>
   /// <returns>The page, or 404.</returns>
   private static IResult RunPage( string root, string run )
   {
      string? folder = Resolve( root, run, null );
      if( folder == null || !Directory.Exists( folder ) )
      {
         return Results.NotFound( new { error = "No such benchmark run." } );
      }

      string? md = new[] { "results.md", "consolidated.md" }.Select( f => Path.Combine( folder, f ) ).FirstOrDefault( File.Exists );
      var body = new StringBuilder( $"<p><a href=\"/bench-results\">All runs</a></p>" );
      body.Append( md == null ? $"<h1>{Enc( run )}</h1><p class=\"muted\">This run has no Markdown report.</p>" : RenderMarkdown( File.ReadAllText( md ) ) );
      body.Append( "<h2>Raw files</h2><ul>" );
      foreach( string file in Directory.GetFiles( folder ).Select( Path.GetFileName ).OfType<string>().Where( f => RAW_EXTENSIONS.Contains( Path.GetExtension( f ) ) ).OrderBy( f => f, StringComparer.Ordinal ) )
      {
         body.Append( $"<li><a href=\"/bench-results/{Enc( run )}/{Enc( file )}\">{Enc( file )}</a></li>" );
      }

      body.Append( "</ul>" );
      return Results.Content( Page( run, body.ToString() ), "text/html; charset=utf-8" );
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
      string? path = Resolve( root, run, file );
      if( path == null || !RAW_EXTENSIONS.Contains( Path.GetExtension( path ) ) || !File.Exists( path ) || new FileInfo( path ).Length > MAX_FILE_BYTES )
      {
         return Results.NotFound( new { error = "No such file." } );
      }

      string type = Path.GetExtension( path ).Equals( ".json", StringComparison.OrdinalIgnoreCase ) ? "application/json" : "text/plain";
      return Results.File( path, $"{type}; charset=utf-8" );
   }

   /// <summary>
   /// Renders the block starting at a line and returns the index of the next unread line.
   /// </summary>
   /// <param name="lines">All lines.</param>
   /// <param name="i">Current line.</param>
   /// <param name="html">Output.</param>
   /// <returns>Next line index.</returns>
   private static int RenderBlock( string[] lines, int i, StringBuilder html )
   {
      string line = lines[i];
      if( line.StartsWith( "```", StringComparison.Ordinal ) )
      {
         var code = new List<string>();
         for( i++; i < lines.Length && !lines[i].StartsWith( "```", StringComparison.Ordinal ); i++ )
         {
            code.Add( lines[i] );
         }

         html.Append( "<pre>" ).Append( Enc( string.Join( "\n", code ) ) ).Append( "</pre>" );
         return i + 1;
      }

      if( line.StartsWith( '|' ) )
      {
         return RenderTable( lines, i, html );
      }

      if( line.StartsWith( "- ", StringComparison.Ordinal ) )
      {
         html.Append( "<ul>" );
         for( ; i < lines.Length && lines[i].StartsWith( "- ", StringComparison.Ordinal ); i++ )
         {
            html.Append( "<li>" ).Append( Inline( lines[i][2..] ) ).Append( "</li>" );
         }

         html.Append( "</ul>" );
         return i;
      }

      int level = line.TakeWhile( c => c == '#' ).Count();
      if( level is >= 1 and <= 4 && line.Length > level && line[level] == ' ' )
      {
         html.Append( $"<h{level}>" ).Append( Inline( line[( level + 1 )..] ) ).Append( $"</h{level}>" );
      }
      else if( !string.IsNullOrWhiteSpace( line ) )
      {
         html.Append( "<p>" ).Append( Inline( line ) ).Append( "</p>" );
      }

      return i + 1;
   }

   /// <summary>
   /// Renders a pipe table (header, separator, rows) inside a horizontally scrolling box, since
   /// the benchmark tables are wider than a phone screen.
   /// </summary>
   /// <param name="lines">All lines.</param>
   /// <param name="i">First table line.</param>
   /// <param name="html">Output.</param>
   /// <returns>Next line index after the table.</returns>
   private static int RenderTable( string[] lines, int i, StringBuilder html )
   {
      html.Append( "<div class=\"preview\"><table>" );
      bool header = true;
      for( ; i < lines.Length && lines[i].StartsWith( '|' ); i++ )
      {
         string[] cells = lines[i].Trim().Trim( '|' ).Split( '|' ).Select( c => c.Trim() ).ToArray();
         if( cells.All( c => c.Length > 0 && c.All( ch => ch is '-' or ':' ) ) )
         {
            continue;
         }

         string tag = header ? "th" : "td";
         html.Append( "<tr>" );
         foreach( string cell in cells )
         {
            html.Append( $"<{tag}>" ).Append( Inline( cell ) ).Append( $"</{tag}>" );
         }

         html.Append( "</tr>" );
         header = false;
      }

      html.Append( "</table></div>" );
      return i;
   }

   /// <summary>
   /// Escapes a line, then applies inline code and bold.
   /// </summary>
   /// <param name="text">Raw text.</param>
   /// <returns>Safe HTML.</returns>
   private static string Inline( string text )
   {
      string safe = Enc( text );
      safe = INLINE_CODE.Replace( safe, "<code>$1</code>" );
      return BOLD.Replace( safe, "<strong>$1</strong>" );
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
         + $"<title>{Enc( title )}</title><link rel=\"stylesheet\" href=\"/style.css\"></head><body>"
         + "<header class=\"topbar\"><h1><a href=\"/\">GenericVectorBuilder</a></h1><a href=\"/bench-results\">Benchmark results</a></header>"
         + $"<main class=\"card bench\">{body}</main></body></html>";
   }

   #endregion Private Methods
}
