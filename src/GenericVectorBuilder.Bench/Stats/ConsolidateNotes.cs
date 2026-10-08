using System.Text.Json;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// One statement a run recorded that the repository's run-page notes list marks as false, misleading or unbacked, with the correction.
/// </summary>
/// <param name="Runs">The run folders whose report holds the statement.</param>
/// <param name="Target">The engine whose section of the report holds it; empty when the statement belongs to no engine.</param>
/// <param name="Statement">The statement as the run's report holds it.</param>
/// <param name="Kind">One of <see cref="RunPageNoteList.KINDS"/>.</param>
/// <param name="Text">The correction.</param>
/// <param name="Field">The recorded field the statement comes from (for example "disk.text" or "notes"); empty when the list gives none.</param>
/// <param name="Sources">What the correction is bound to: a recorded field of a run, a repository file; at least one for a note read from a list.</param>
public sealed record RunPageNote( IReadOnlyList<string> Runs, string Target, string Statement, string Kind, string Text, string Field = "", IReadOnlyList<RunPageNoteSource>? Sources = null );

/// <summary>
/// One source of a correction in the run-page notes list.
/// </summary>
/// <param name="Kind">What the source is ("file" or "results").</param>
/// <param name="Ref">Where it is, in the grammar of the list.</param>
/// <param name="Value">The words or figure the source holds.</param>
public sealed record RunPageNoteSource( string Kind, string Ref, string Value );

/// <summary>
/// The list of recorded statements the repository marks as false, misleading or unbacked (deploy/bench/run-page-notes.json), as the consolidation reads it.
/// Why the consolidation reads it: the web pages print each correction at the statement on the page of the run that holds it, but the summary is written from the same runs' fields, and a recorded
/// text it quotes may hold the same statement. A summary that printed a marked statement bare would contradict the run page that corrects it, so the list is an input of the report: a sentence that
/// holds a marked statement stops the report, and a recorded text the list marks is replaced by what the line records plus a pointer to the correction.
/// Why a missing list is a refusal: an absent list would read as "nothing is marked", and the report would then print every marked statement without saying so.
/// </summary>
public sealed class RunPageNoteList
{
   #region Data Members

   /// <summary>The repository path of the list.</summary>
   public const string FILE = "deploy/bench/run-page-notes.json";

   /// <summary>The kind of a statement the runs' own record contradicts.</summary>
   public const string KIND_FALSE = "false";

   /// <summary>The kind of a statement that is not contradicted but gives a reader a wrong picture.</summary>
   public const string KIND_MISLEADING = "misleading";

   /// <summary>The kind of a statement that cites a measurement or reading that no saved source backs.</summary>
   public const string KIND_UNBACKED = "unbacked";

   /// <summary>The kinds a note may have.</summary>
   public static readonly IReadOnlyList<string> KINDS = new[] { KIND_FALSE, KIND_MISLEADING, KIND_UNBACKED };

   /// <summary>Largest list read.</summary>
   public const long MAX_BYTES = 4L * 1024 * 1024;

   /// <summary>Most notes a list may hold; more means the file is not what the report thinks it is.</summary>
   public const int MAX_NOTES = 1000;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the list.
   /// </summary>
   /// <param name="notes">The notes, in file order.</param>
   /// <param name="sha256">SHA-256 of the file, lower-case hex; empty for a list that was not read from a file.</param>
   public RunPageNoteList( IReadOnlyList<RunPageNote> notes, string sha256 )
   {
      Notes = notes;
      Sha256 = sha256;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>A list with no notes, for an input built in code.</summary>
   public static RunPageNoteList Empty { get; } = new( Array.Empty<RunPageNote>(), string.Empty );

   /// <summary>The notes in file order.</summary>
   public IReadOnlyList<RunPageNote> Notes { get; }

   /// <summary>SHA-256 of the file.</summary>
   public string Sha256 { get; }

   /// <summary>
   /// Reads the list.
   /// </summary>
   /// <param name="path">The list file.</param>
   /// <returns>The list.</returns>
   /// <exception cref="ConsolidateRefusal">The file is too large, not JSON, not a list of notes, or a note lacks a statement, a kind, a correction or a run.</exception>
   public static RunPageNoteList Load( string path )
   {
      if( new FileInfo( path ).Length > MAX_BYTES )
      {
         throw new ConsolidateRefusal( $"the run-page notes list {path} is over {MAX_BYTES} bytes" );
      }

      try
      {
         using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( path ) );
         JsonElement root = doc.RootElement;
         JsonElement list = root.ValueKind == JsonValueKind.Array ? root : root.ValueKind == JsonValueKind.Object && root.TryGetProperty( "notes", out JsonElement n ) ? n
            : throw new ConsolidateRefusal( $"the run-page notes list {path} holds no \"notes\" array" );
         if( list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MAX_NOTES )
         {
            throw new ConsolidateRefusal( $"the run-page notes list {path}: \"notes\" must be an array of at most {MAX_NOTES} notes" );
         }

         var notes = new List<RunPageNote>();
         foreach( ( JsonElement e, int i ) in list.EnumerateArray().Select( ( e, i ) => ( e, i ) ) )
         {
            notes.Add( ReadOne( e, $"{path} notes[{i}]" ) );
         }

         return new RunPageNoteList( notes, EngineFactSheet.FileSha256( path ) );
      }
      catch( JsonException ex )
      {
         throw new ConsolidateRefusal( $"the run-page notes list {path} is not valid JSON: {ex.Message}" );
      }
   }

   /// <summary>
   /// The notes as the consolidated file carries them ("runPageNotes"), in list order, each with only the runs the set uses.
   /// Why in the file: the summary page draws a table of all corrections from the file, and the page of each run reads the entries of the folders that use it, so the corrections a set states about
   /// its runs travel with the set and a later change of the repository's list does not change what a published set said.
   /// Why only the runs the set uses: the page refuses a file whose correction names a run the set does not link to (the correction would be printed on no page a reader reaches from the set), and the
   /// repository's list also covers older runs the set leaves out; a note that names none of the set's runs is left out, and the run pages of the older runs keep reading the repository's list.
   /// </summary>
   /// <param name="usedRuns">The run folders of the set's sessions and basis.</param>
   /// <returns>One record per note that names at least one of the set's runs.</returns>
   public List<RunPageNoteRecord> ToRecords( IReadOnlyCollection<string> usedRuns )
   {
      var records = new List<RunPageNoteRecord>();
      foreach( RunPageNote n in Notes )
      {
         List<string> runs = n.Runs.Where( r => usedRuns.Contains( r, StringComparer.Ordinal ) ).ToList();
         if( runs.Count > 0 )
         {
            records.Add( new RunPageNoteRecord
            {
               Runs = runs, Target = n.Target, Field = n.Field, Statement = n.Statement, Kind = n.Kind, Note = n.Text,
               Sources = ( n.Sources ?? Array.Empty<RunPageNoteSource>() ).Select( s => new RunPageNoteSourceRecord { Kind = s.Kind, Ref = s.Ref, Value = s.Value } ).ToList(),
            } );
         }
      }

      return records;
   }

   /// <summary>
   /// The first note that marks a statement held by one run's field of one target.
   /// </summary>
   /// <param name="run">The run folder name.</param>
   /// <param name="target">The target whose field it is.</param>
   /// <param name="text">The field's text.</param>
   /// <returns>The note, or null when none marks a statement of the text for this run and target.</returns>
   public RunPageNote? Marking( string run, string target, string text )
   {
      return Notes.FirstOrDefault( n => n.Runs.Contains( run, StringComparer.Ordinal ) && ( n.Target.Length == 0 || string.Equals( n.Target, target, StringComparison.OrdinalIgnoreCase ) ) && Occurs( text, n.Statement ) );
   }

   /// <summary>
   /// The notes whose statement a text holds, whatever run or target the note names.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The notes in file order; none when the text holds no marked statement.</returns>
   public IEnumerable<RunPageNote> MarkedIn( string text )
   {
      return Notes.Where( n => Occurs( text, n.Statement ) );
   }

   /// <summary>
   /// Whether a text holds a statement as a whole phrase or figure: an occurrence counts when it is not part of a longer word or figure, so "1.9 GiB at the start" is not found inside
   /// "11.9 GiB at the start". The page marks a report with the same rule.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <param name="statement">The statement.</param>
   /// <returns>True when the text holds the statement.</returns>
   public static bool Occurs( string text, string statement )
   {
      if( statement.Length == 0 )
      {
         return false;
      }

      for( int at = text.IndexOf( statement, StringComparison.Ordinal ); at >= 0; at = text.IndexOf( statement, at + 1, StringComparison.Ordinal ) )
      {
         int end = at + statement.Length;
         bool before = at == 0 || !Continues( text, at - 1, -1 ) || !IsPart( statement[0] );
         bool after = end == text.Length || !Continues( text, end, 1 ) || !IsPart( statement[^1] );
         if( before && after )
         {
            return true;
         }
      }

      return false;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads one note.
   /// </summary>
   /// <param name="e">The note's object.</param>
   /// <param name="where">Where it sits in the file, for messages.</param>
   /// <returns>The note.</returns>
   /// <exception cref="ConsolidateRefusal">A field is missing or wrong.</exception>
   private static RunPageNote ReadOne( JsonElement e, string where )
   {
      if( e.ValueKind != JsonValueKind.Object )
      {
         throw new ConsolidateRefusal( $"the run-page notes list: {where} is not an object" );
      }

      var runs = new List<string>();
      if( e.TryGetProperty( "runs", out JsonElement list ) && list.ValueKind == JsonValueKind.Array )
      {
         runs.AddRange( list.EnumerateArray().Where( r => r.ValueKind == JsonValueKind.String ).Select( r => r.GetString()! ) );
      }

      if( Text( e, "run" ) is { Length: > 0 } single )
      {
         runs.Add( single );
      }

      if( runs.Count == 0 )
      {
         throw new ConsolidateRefusal( $"the run-page notes list: {where} names no run" );
      }

      string kind = Text( e, "kind" ) ?? string.Empty;
      if( !KINDS.Contains( kind ) )
      {
         throw new ConsolidateRefusal( $"the run-page notes list: {where}.kind is \"{kind}\"; the list knows {string.Join( ", ", KINDS.Select( k => $"\"{k}\"" ) )}" );
      }

      string statement = Text( e, "statement" ) is { Length: > 0 } s ? s : throw new ConsolidateRefusal( $"the run-page notes list: {where}.statement is missing" );
      string note = Text( e, "note" ) is { Length: > 0 } t ? t : throw new ConsolidateRefusal( $"the run-page notes list: {where}.note is missing" );
      return new RunPageNote( runs.Distinct( StringComparer.Ordinal ).ToList(), Text( e, "target" ) ?? string.Empty, statement, kind, note, Text( e, "field" ) ?? string.Empty, ReadSources( e, where ) );
   }

   /// <summary>
   /// Reads the sources of one note. Why at least one: the page draws a correction only with what it is bound to, so a note with none could not be shown, and the file this list goes into would be refused.
   /// </summary>
   /// <param name="e">The note's object.</param>
   /// <param name="where">Where the note sits in the file, for messages.</param>
   /// <returns>The sources in list order.</returns>
   /// <exception cref="ConsolidateRefusal">There is no source, or one lacks a kind, a ref or a value.</exception>
   private static List<RunPageNoteSource> ReadSources( JsonElement e, string where )
   {
      if( !e.TryGetProperty( "sources", out JsonElement list ) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0 )
      {
         throw new ConsolidateRefusal( $"the run-page notes list: {where}.sources is empty; a correction with no source is not printed" );
      }

      var sources = new List<RunPageNoteSource>();
      foreach( ( JsonElement source, int j ) in list.EnumerateArray().Select( ( x, j ) => ( x, j ) ) )
      {
         string kind = source.ValueKind == JsonValueKind.Object ? Text( source, "kind" ) ?? string.Empty : string.Empty;
         string reference = source.ValueKind == JsonValueKind.Object ? Text( source, "ref" ) ?? string.Empty : string.Empty;
         if( kind.Length == 0 || reference.Length == 0 )
         {
            throw new ConsolidateRefusal( $"the run-page notes list: {where}.sources[{j}] needs a kind and a ref" );
         }

         sources.Add( new RunPageNoteSource( kind, reference, Text( source, "value" ) ?? string.Empty ) );
      }

      return sources;
   }

   /// <summary>
   /// A string property, or null.
   /// </summary>
   /// <param name="element">The object.</param>
   /// <param name="name">The property.</param>
   /// <returns>The text, or null.</returns>
   private static string? Text( JsonElement element, string name )
   {
      return element.TryGetProperty( name, out JsonElement value ) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
   }

   /// <summary>
   /// Whether the character at a position continues a word or a figure on the side that faces a statement: a letter, a digit or an underscore always does; a dot does only when a letter or a digit
   /// sits beyond it (the dot of 1.9), so the full stop that ends a sentence does not hide a statement before it.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <param name="index">The position of the character next to the statement.</param>
   /// <param name="step">+1 when the character follows the statement, -1 when it precedes it.</param>
   /// <returns>True when the character is part of a longer word or figure.</returns>
   private static bool Continues( string text, int index, int step )
   {
      char c = text[index];
      if( IsPart( c ) )
      {
         return true;
      }

      int beyond = index + step;
      return c == '.' && beyond >= 0 && beyond < text.Length && char.IsLetterOrDigit( text[beyond] );
   }

   /// <summary>
   /// Whether a character continues a word or a figure without needing a neighbour: a letter, a digit or an underscore.
   /// </summary>
   /// <param name="c">The character.</param>
   /// <returns>True for a letter, a digit or an underscore.</returns>
   private static bool IsPart( char c )
   {
      return char.IsLetterOrDigit( c ) || c == '_';
   }

   #endregion Private Methods
}
