using System.Text;
using System.Text.Json;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// One statement a run recorded that the set using the run states is false, misleading or not backed by a saved source, with the correction and what the correction is bound to.
/// </summary>
/// <param name="Runs">The run folders whose report holds the statement (each checked safe by the reader).</param>
/// <param name="Target">The engine whose section of the report holds the statement; empty for a statement that belongs to no engine.</param>
/// <param name="Field">The recorded field the statement comes from (for example "disk.text" or "durability"); empty when the list gives none.</param>
/// <param name="Statement">The statement exactly as the run's report holds it. The page finds it in the report text by these words.</param>
/// <param name="Kind">"false", "misleading" or "unbacked".</param>
/// <param name="Text">The correction, as the list holds it.</param>
/// <param name="Sources">What the correction is bound to (a recorded field of a run, a repository file); at least one.</param>
/// <param name="Origin">Where the list came from: a consolidated folder's name, or the repository path of the list file.</param>
public sealed record BenchRunNote( IReadOnlyList<string> Runs, string Target, string Field, string Statement, string Kind, string Text, IReadOnlyList<BenchSource> Sources, string Origin );

/// <summary>
/// Reads the list of recorded statements a set marks as false or misleading, and puts the corrections on the page of each run that holds one.
/// Why at render time and not in the run's files: a run's report and results are a record and stay as written; a reader who follows a published set to
/// its runs meets the statement the set's own report withholds, so the run page prints the correction next to the statement, and says it did.
/// Why a list and not a fixed table of statements in the page: the page states no fact of its own. The list is data (the consolidated file's
/// "runPageNotes", or the repository file named in <see cref="DEFAULT_FILE"/> when the set has none), each entry bound to sources.
/// An entry is an object with: "runs" (the run folders whose report holds the statement; "run" names one), "target" (the engine whose section of the
/// report holds it; optional), "field" (the recorded field it comes from; optional), "statement" (its words exactly as the report holds them),
/// "kind" ("false", "misleading" or "unbacked"), "note" (the correction) and "sources" (a list of { kind, ref, value } objects, at least one). The list file is an
/// object with a "notes" array of such entries, or the array itself.
/// </summary>
public static class BenchRunNotes
{
   #region Data Members

   /// <summary>The kind of a statement the set's own record contradicts.</summary>
   public const string KIND_FALSE = "false";

   /// <summary>The kind of a statement that is not contradicted but gives a reader a wrong picture.</summary>
   public const string KIND_MISLEADING = "misleading";

   /// <summary>The kind of a statement that cites a figure or a reading that no saved source backs: it is not shown wrong, and the set that uses the run does not print it.</summary>
   public const string KIND_UNBACKED = "unbacked";

   /// <summary>The repository path of the list file the page reads when a set carries no list of its own (relative to the repository root).</summary>
   public const string DEFAULT_FILE = "deploy/bench/run-page-notes.json";

   /// <summary>The name of the list in a consolidated file.</summary>
   public const string FIELD = "runPageNotes";

   /// <summary>Most entries one list may hold; more means the file is not what the page thinks it is.</summary>
   public const int MAX_NOTES = 1000;

   /// <summary>Most runs one entry may name.</summary>
   public const int MAX_RUNS_PER_NOTE = 100;

   /// <summary>The word after the marker's opening bracket in the report text; the correction's number follows it.</summary>
   public const string MARKER_WORD = "Correction";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads a list of entries (the consolidated file's "runPageNotes", or the "notes" of the list file).
   /// </summary>
   /// <param name="list">The array.</param>
   /// <param name="path">Where the array sits in the file, for messages.</param>
   /// <param name="origin">Where the list came from, printed with each correction.</param>
   /// <returns>The entries in file order.</returns>
   public static IReadOnlyList<BenchRunNote> Read( JsonElement list, string path, string origin )
   {
      if( list.ValueKind != JsonValueKind.Array )
      {
         throw new InvalidDataException( $"{path} is not an array." );
      }

      if( list.GetArrayLength() > MAX_NOTES )
      {
         throw new InvalidDataException( $"{path} holds more than {MAX_NOTES} entries; refusing to draw it." );
      }

      var notes = new List<BenchRunNote>();
      foreach( ( JsonElement e, int i ) in list.EnumerateArray().Select( ( e, i ) => ( e, i ) ) )
      {
         notes.Add( ReadOne( e, $"{path}[{i}]", origin ) );
      }

      return notes;
   }

   /// <summary>
   /// Reads the list file: an object with a "notes" array, or the array itself. A file that is absent is an empty list; a file that cannot be
   /// read is reported, never taken for an empty list.
   /// </summary>
   /// <param name="file">The file's full path, or null for no list file.</param>
   /// <param name="error">Why the file could not be read, or null.</param>
   /// <returns>The entries; none when the file is absent or unreadable.</returns>
   public static IReadOnlyList<BenchRunNote> ReadFile( string? file, out string? error )
   {
      error = null;
      if( file == null || !File.Exists( file ) )
      {
         return Array.Empty<BenchRunNote>();
      }

      try
      {
         using JsonDocument doc = JsonDocument.Parse( BenchRunList.ReadCapped( file ) );
         JsonElement root = doc.RootElement;
         JsonElement? list = root.ValueKind == JsonValueKind.Array ? root : BenchJson.Get( root, "notes" );
         return list == null ? throw new InvalidDataException( $"{DEFAULT_FILE} has no \"notes\" array." ) : Read( list.Value, "notes", DEFAULT_FILE );
      }
      catch( Exception ex ) when( ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException )
      {
         error = $"{DEFAULT_FILE}: {ex.Message}";
         return Array.Empty<BenchRunNote>();
      }
   }

   /// <summary>
   /// Puts a marker after each place a report holds a statement of the list, for the notes that name the run's engine sections.
   /// A statement counts in a line when the line holds its words and, for a note with an engine, the line sits under that engine's heading.
   /// The marker is bold text read "[Correction N]", N being the note's position in the list given.
   /// </summary>
   /// <param name="markdown">The report text.</param>
   /// <param name="notes">The notes of this run, in the order the page lists them.</param>
   /// <param name="found">For each note, in the same order, how many lines it was found in.</param>
   /// <returns>The text with the markers.</returns>
   public static string Apply( string markdown, IReadOnlyList<BenchRunNote> notes, out IReadOnlyList<int> found )
   {
      var counts = new int[notes.Count];
      if( notes.Count == 0 )
      {
         found = counts;
         return markdown;
      }

      string[] lines = markdown.Split( '\n' );
      string section = string.Empty;
      for( int i = 0; i < lines.Length; i++ )
      {
         string heading = HeadingText( lines[i] );
         section = heading.Length > 0 ? heading : section;
         for( int n = 0; n < notes.Count; n++ )
         {
            BenchRunNote note = notes[n];
            bool inScope = note.Target.Length == 0 || string.Equals( section, note.Target, StringComparison.OrdinalIgnoreCase );
            if( inScope && Mark( lines[i], note.Statement, $" **[{MARKER_WORD} {n + 1}]**" ) is { } marked )
            {
               lines[i] = marked;
               counts[n]++;
            }
         }
      }

      found = counts;
      return string.Join( '\n', lines );
   }

   /// <summary>
   /// The block that opens a run page's corrections: one entry per note with its kind, the statement as recorded, the correction, its sources, where the list
   /// came from and how many places in the report text carry the marker.
   /// </summary>
   /// <param name="notes">The notes of this run, in the order the markers number them.</param>
   /// <param name="found">For each note, how many lines of the report text it was found in.</param>
   /// <param name="error">Why the list file could not be read, or null.</param>
   /// <returns>HTML fragment; empty when there is nothing to print.</returns>
   public static string Block( IReadOnlyList<BenchRunNote> notes, IReadOnlyList<int> found, string? error )
   {
      if( notes.Count == 0 && error == null )
      {
         return string.Empty;
      }

      var html = new StringBuilder( "<aside class=\"bench-caveats\" data-section=\"run-notes\">" );
      if( error != null )
      {
         html.Append( $"<p class=\"errors\">{BenchFormat.Enc( BenchLegends.RUN_NOTES_UNREADABLE )} {BenchFormat.Enc( error )}</p>" );
      }

      if( notes.Count > 0 )
      {
         html.Append( $"<h2>{BenchFormat.Enc( BenchLegends.H_RUN_NOTES )}</h2><p>{BenchFormat.Enc( BenchLegends.RUN_NOTES_INTRO )}</p><ol>" );
         for( int n = 0; n < notes.Count; n++ )
         {
            html.Append( Item( notes[n], n + 1, n < found.Count ? found[n] : 0 ) );
         }

         html.Append( "</ol>" );
      }

      return html.Append( "</aside>" ).ToString();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// One entry of the block.
   /// </summary>
   /// <param name="note">The note.</param>
   /// <param name="number">Its number in the block.</param>
   /// <param name="places">How many lines of the report text carry its marker.</param>
   /// <returns>HTML fragment.</returns>
   private static string Item( BenchRunNote note, int number, int places )
   {
      string label = note.Kind switch
      {
         KIND_FALSE => BenchLegends.L_NOTE_FALSE,
         KIND_UNBACKED => BenchLegends.L_NOTE_UNBACKED,
         _ => BenchLegends.L_NOTE_MISLEADING,
      };
      string where = string.Join( ", ", new[] { note.Target.Length == 0 ? string.Empty : BenchNames.FriendlyName( note.Target ), note.Field }.Where( s => s.Length > 0 ).Select( BenchFormat.Enc ) );
      string origin = BenchFolderNames.IsSafe( note.Origin ) && BenchFolderNames.KindOf( note.Origin ) != BenchFolderKind.Run
         ? $"<a href=\"/bench-results/{BenchFormat.Enc( note.Origin )}\">{BenchFormat.Enc( note.Origin )}</a>"
         : $"<code>{BenchFormat.Enc( note.Origin )}</code>";
      string seen = places > 0
         ? $"{BenchFormat.Enc( BenchLegends.L_NOTE_PLACES )} {places}."
         : BenchFormat.Enc( BenchLegends.L_NOTE_NOT_FOUND );
      var html = new StringBuilder( $"<li data-note=\"{number}\" data-kind=\"{BenchFormat.Enc( note.Kind )}\" data-places=\"{places}\">" );
      html.Append( $"<strong>{BenchFormat.Enc( label )}</strong> {where}{( where.Length > 0 ? ". " : string.Empty )}{BenchFormat.Enc( BenchLegends.L_NOTE_RECORDED )} <q>{BenchFormat.Enc( note.Statement )}</q> " );
      html.Append( $"{BenchFormat.Enc( note.Text )} <span class=\"muted\">{BenchFormat.Enc( BenchLegends.L_NOTE_LISTED_IN )} {origin}. {seen}</span>" );
      html.Append( BenchConsolidatedHtml.SourceList( note.Sources ) );
      return html.Append( "</li>" ).ToString();
   }

   /// <summary>
   /// Reads one entry.
   /// </summary>
   /// <param name="e">The entry's object.</param>
   /// <param name="path">Where it sits in the file.</param>
   /// <param name="origin">Where the list came from.</param>
   /// <returns>The note.</returns>
   private static BenchRunNote ReadOne( JsonElement e, string path, string origin )
   {
      if( e.ValueKind != JsonValueKind.Object )
      {
         throw new InvalidDataException( $"{path} is not an object." );
      }

      List<string> runs = BenchJson.Strings( e, "runs", path ).ToList();
      if( BenchJson.Text( e, "run", path ) is { Length: > 0 } single )
      {
         runs.Add( single );
      }

      if( runs.Count is 0 or > MAX_RUNS_PER_NOTE || runs.Any( r => !BenchFolderNames.IsSafe( r ) ) )
      {
         throw new InvalidDataException( $"{path}.runs must name between 1 and {MAX_RUNS_PER_NOTE} run folders, each a safe folder name." );
      }

      string kind = BenchJson.RequireText( e, "kind", path );
      if( kind is not ( KIND_FALSE or KIND_MISLEADING or KIND_UNBACKED ) )
      {
         throw new InvalidDataException( $"{path}.kind is \"{kind}\"; the page knows \"{KIND_FALSE}\", \"{KIND_MISLEADING}\" and \"{KIND_UNBACKED}\"." );
      }

      var sources = BenchJson.Items( e, "sources", path ).Select( ( s, j ) => new BenchSource( BenchJson.RequireText( s, "kind", $"{path}.sources[{j}]" ),
         BenchJson.RequireText( s, "ref", $"{path}.sources[{j}]" ), BenchJson.Get( s, "value" ) is { } v ? v.ToString() : string.Empty ) ).ToList();
      if( sources.Count == 0 )
      {
         throw new InvalidDataException( $"{path}.sources is empty; a correction with no source is not printed." );
      }

      return new BenchRunNote( runs.Distinct( StringComparer.Ordinal ).ToList(), BenchJson.Text( e, "target", path ) ?? string.Empty, BenchJson.Text( e, "field", path ) ?? string.Empty,
         BenchJson.RequireText( e, "statement", path ), kind, BenchJson.RequireText( e, "note", path ), sources, origin );
   }

   /// <summary>
   /// The line with the marker put after every whole occurrence of the statement: an occurrence counts when it is not part of a longer word or figure
   /// (the statement "1.9 GiB at the start" is not found inside "11.9 GiB at the start").
   /// </summary>
   /// <param name="line">The report line.</param>
   /// <param name="statement">The statement.</param>
   /// <param name="marker">The marker text.</param>
   /// <returns>The marked line, or null when the line holds no whole occurrence.</returns>
   private static string? Mark( string line, string statement, string marker )
   {
      var result = new StringBuilder();
      int from = 0;
      bool any = false;
      for( int at = line.IndexOf( statement, StringComparison.Ordinal ); at >= 0; at = line.IndexOf( statement, from, StringComparison.Ordinal ) )
      {
         int end = at + statement.Length;
         bool whole = ( at == 0 || !Continues( line, at - 1, -1 ) || !IsPart( statement[0] ) ) && ( end == line.Length || !Continues( line, end, 1 ) || !IsPart( statement[^1] ) );
         result.Append( line, from, end - from );
         if( whole )
         {
            result.Append( marker );
            any = true;
         }

         from = end;
      }

      return any ? result.Append( line, from, line.Length - from ).ToString() : null;
   }

   /// <summary>
   /// True for a character that continues a word or a figure on its own: a letter, a digit or an underscore.
   /// </summary>
   /// <param name="c">The character.</param>
   /// <returns>True for a letter, a digit or an underscore.</returns>
   private static bool IsPart( char c )
   {
      return char.IsLetterOrDigit( c ) || c == '_';
   }

   /// <summary>
   /// Whether the character at a position continues a word or a figure on the side that faces a statement: a letter, a digit or an underscore always does; a dot does only when a letter or a
   /// digit sits beyond it (the dot of 1.9). A full stop that ends a sentence does not hide the statement before it.
   /// </summary>
   /// <param name="line">The line.</param>
   /// <param name="index">The position of the character next to the statement.</param>
   /// <param name="step">+1 when the character follows the statement, -1 when it precedes it.</param>
   /// <returns>True when the character is part of a longer word or figure.</returns>
   private static bool Continues( string line, int index, int step )
   {
      int beyond = index + step;
      return IsPart( line[index] ) || ( line[index] == '.' && beyond >= 0 && beyond < line.Length && char.IsLetterOrDigit( line[beyond] ) );
   }

   /// <summary>
   /// The text of a Markdown heading line (levels 2 to 4), or an empty string for any other line.
   /// </summary>
   /// <param name="line">The line.</param>
   /// <returns>The heading's text, trimmed.</returns>
   private static string HeadingText( string line )
   {
      int level = line.TakeWhile( c => c == '#' ).Count();
      return level is >= 2 and <= 4 && line.Length > level && line[level] == ' ' ? line[( level + 1 )..].Trim() : string.Empty;
   }

   #endregion Private Methods
}
