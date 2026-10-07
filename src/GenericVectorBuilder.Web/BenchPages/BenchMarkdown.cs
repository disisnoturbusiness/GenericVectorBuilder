using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// Renders the small Markdown subset the Bench report uses (headings, pipe tables, fenced code,
/// bullets, paragraphs, inline code, bold) as HTML, folding each engine's section under
/// "Details per target" into its own closed &lt;details&gt;.
/// Why fold: that section is a page of per-engine detail that nobody reads end to end; closed,
/// it is one line per engine and still a click away.
/// Everything is HTML-escaped first, so file content can never inject markup.
/// </summary>
public static class BenchMarkdown
{
   #region Data Members

   /// <summary>The level 2 heading whose level 3 sections are folded.</summary>
   public const string FOLD_SECTION = "Details per target";

   private static readonly Regex INLINE_CODE = new( "`([^`]+)`", RegexOptions.Compiled );
   private static readonly Regex BOLD = new( "\\*\\*([^*]+)\\*\\*", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Renders a report.
   /// </summary>
   /// <param name="markdown">Report text.</param>
   /// <returns>HTML fragment.</returns>
   public static string Render( string markdown )
   {
      var html = new StringBuilder();
      string[] lines = markdown.Replace( "\r", string.Empty ).Split( '\n' );
      bool folding = false;
      bool open = false;
      for( int i = 0; i < lines.Length; )
      {
         int level = HeadingLevel( lines[i] );
         if( level is 1 or 2 )
         {
            open = Close( html, open );
            folding = level == 2 && lines[i][( level + 1 )..].Trim().Equals( FOLD_SECTION, StringComparison.OrdinalIgnoreCase );
         }
         else if( level == 3 && folding )
         {
            open = Close( html, open );
            html.Append( "<details class=\"bench-target\"><summary>" ).Append( FoldTitle( lines[i][4..].Trim() ) ).Append( "</summary>" );
            open = true;
            i++;
            continue;
         }

         int next = RenderBlock( lines, i, html );
         if( next <= i )
         {
            throw new InvalidOperationException( $"Markdown renderer did not advance at line {i + 1}." );
         }

         i = next;
      }

      Close( html, open );
      return html.ToString();
   }

   /// <summary>
   /// Splits off the report's first line when it is a level 1 heading, so a page can put the
   /// summary block between the title and the rest of the report.
   /// </summary>
   /// <param name="markdown">Report text.</param>
   /// <returns>The title (null when the report does not start with one) and the remaining text as Body.</returns>
   public static (string? Title, string Body) SplitTitle( string markdown )
   {
      string text = markdown.Replace( "\r", string.Empty ).TrimStart( '\n' );
      int end = text.IndexOf( '\n' );
      string first = end < 0 ? text : text[..end];
      return HeadingLevel( first ) == 1 ? ( first[2..].Trim(), end < 0 ? string.Empty : text[( end + 1 )..] ) : ( null, text );
   }

   /// <summary>
   /// Escapes a line, then applies inline code and bold.
   /// </summary>
   /// <param name="text">Raw text.</param>
   /// <returns>Safe HTML.</returns>
   public static string Inline( string text )
   {
      string safe = WebUtility.HtmlEncode( text );
      safe = INLINE_CODE.Replace( safe, "<code>$1</code>" );
      return BOLD.Replace( safe, "<strong>$1</strong>" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The line a folded engine section shows: its friendly name, then the target name the full
   /// table uses, so the two can be matched without opening anything.
   /// </summary>
   /// <param name="key">Heading text, normally a Bench target name such as "sqlitevec".</param>
   /// <returns>Safe HTML, e.g. "sqlite-vec &lt;span class="muted"&gt;sqlitevec&lt;/span&gt;".</returns>
   private static string FoldTitle( string key )
   {
      string name = BenchNames.FriendlyName( key );
      return name == key ? Inline( key ) : $"{Inline( name )} <span class=\"muted\">{Inline( key )}</span>";
   }

   /// <summary>
   /// Closes an open &lt;details&gt; when there is one.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="open">True when one is open.</param>
   /// <returns>Always false: nothing is open afterwards.</returns>
   private static bool Close( StringBuilder html, bool open )
   {
      if( open )
      {
         html.Append( "</details>" );
      }

      return false;
   }

   /// <summary>
   /// The heading level of a line ("## x" is 2), or 0 when it is not a heading.
   /// </summary>
   /// <param name="line">Line.</param>
   /// <returns>1 to 4, or 0.</returns>
   private static int HeadingLevel( string line )
   {
      int level = line.TakeWhile( c => c == '#' ).Count();
      return level is >= 1 and <= 4 && line.Length > level && line[level] == ' ' ? level : 0;
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

         html.Append( "<pre>" ).Append( WebUtility.HtmlEncode( string.Join( "\n", code ) ) ).Append( "</pre>" );
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

      int level = HeadingLevel( line );
      if( level > 0 )
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
   /// Splits a table line into its cells on every pipe that is not escaped; an escaped pipe (backslash, pipe) is a
   /// pipe inside the cell.
   /// Why: the consolidated report escapes a pipe inside a recorded text this way, and splitting on every pipe put
   /// the rest of that text under the wrong column.
   /// </summary>
   /// <param name="line">One table line.</param>
   /// <returns>The trimmed cells.</returns>
   internal static string[] SplitCells( string line )
   {
      const char ESCAPED = '\u0001';
      return line.Trim().Replace( "\\|", ESCAPED.ToString() ).Trim( '|' ).Split( '|' ).Select( c => c.Replace( ESCAPED, '|' ).Trim() ).ToArray();
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
         string[] cells = SplitCells( lines[i] );
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

   #endregion Private Methods
}
