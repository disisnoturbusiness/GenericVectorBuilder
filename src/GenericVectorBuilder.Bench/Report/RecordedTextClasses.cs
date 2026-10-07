using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// The names of the four classes a clause of recorded text can have, and their order of strength.
/// Why four: a clause is either read back from the engine (or the machine) in the runs, measured by this tool in the runs,
/// documented by a source saved in the repository (a code or compose line, a saved page, a saved log), or unverified, which
/// means it was typed in the program's own text and nothing here backs it. The report prints every clause with its class.
/// </summary>
public static class TextClass
{
   #region Data Members

   /// <summary>The engine or the machine reported it in these runs.</summary>
   public const string READ_BACK = "read-back";

   /// <summary>This tool counted or timed it in these runs.</summary>
   public const string MEASURED = "measured";

   /// <summary>A source saved in the repository backs it; the engine did not report it in these runs.</summary>
   public const string DOCUMENTED = "documented";

   /// <summary>Typed in the program's own text; not read back, measured or backed by a saved source.</summary>
   public const string UNVERIFIED = "unverified";

   /// <summary>Every class, weakest first.</summary>
   public static readonly IReadOnlyList<string> ALL = new[] { UNVERIFIED, DOCUMENTED, MEASURED, READ_BACK };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The strength of a class: 0 for unverified up to 3 for read-back.
   /// </summary>
   /// <param name="name">A class name.</param>
   /// <returns>The strength.</returns>
   /// <exception cref="ArgumentException">The name is not a class.</exception>
   public static int Strength( string name )
   {
      int at = ALL.ToList().IndexOf( name );
      return at >= 0 ? at : throw new ArgumentException( $"'{name}' is not one of {string.Join( ", ", ALL )}", nameof( name ) );
   }

   #endregion Public Methods
}

/// <summary>
/// A repository file that contradicts a recorded clause when it sets a variable whose name starts with a prefix.
/// </summary>
/// <param name="File">Repository path of the file.</param>
/// <param name="Prefix">The variable-name prefix whose presence contradicts the clause.</param>
/// <param name="Words">The recorded words the file contradicts.</param>
public sealed record ContradictedBy( string File, string Prefix, string Words );

/// <summary>
/// One row of a saved documentation table: a name followed, within a few text nodes, by its value.
/// </summary>
/// <param name="Name">The row's name node, such as dynamicEfFactor.</param>
/// <param name="Value">The value node that follows it, such as 8.</param>
public sealed record DocRow( string Name, string Value );

/// <summary>
/// One span of a recorded text: where it starts is the end of the span before it, what it is is a verbatim text or a pattern, and how it
/// is backed is its class and its basis.
/// </summary>
public sealed class ClassSpan
{
   #region Public Methods

   /// <summary>The span's verbatim text (white space collapsed), or null when it is matched by <see cref="Pattern"/>.</summary>
   public string? Text { get; init; }

   /// <summary>A regular expression the span matches at its start, or null when it is a verbatim <see cref="Text"/>.</summary>
   public string? Pattern { get; init; }

   /// <summary>One of <see cref="TextClass"/>.</summary>
   public string Class { get; init; } = TextClass.UNVERIFIED;

   /// <summary>The sources that back the span, in the grammar of the fact sheet: results:path#token, doc:path#quote or repo/path#token. Empty for an unverified span.</summary>
   public IReadOnlyList<string> Basis { get; init; } = Array.Empty<string>();

   /// <summary>A code naming why the span is not printed and what replaces it, or null when it is printed.</summary>
   public string? Drop { get; init; }

   /// <summary>The targets that must be reported for the span to be printed: when none of them is, the span is dropped.</summary>
   public IReadOnlyList<string> DropWhenAbsent { get; init; } = Array.Empty<string>();

   /// <summary>The repository file that contradicts the span, or null.</summary>
   public ContradictedBy? Contradicted { get; init; }

   /// <summary>Documentation table rows the span's numbers come from, each of which must hold in the saved page of the basis.</summary>
   public IReadOnlyList<DocRow> DocRows { get; init; } = Array.Empty<DocRow>();

   /// <summary>The name of a saved piece of evidence a generated sentence is written from after this span, or null.</summary>
   public string? Evidence { get; init; }

   /// <summary>What an unverified span cites, "measurement" when it quotes a measurement or a reading that no saved source backs; null otherwise.</summary>
   public string? Cites { get; init; }

   #endregion Public Methods
}

/// <summary>
/// The spans of one recorded text, in text order.
/// </summary>
public sealed class ClassEntry
{
   #region Public Methods

   /// <summary>The target the text belongs to; empty for a run note.</summary>
   public string Target { get; init; } = string.Empty;

   /// <summary>The field: durability or index for a target, notes for a run note.</summary>
   public string Field { get; init; } = string.Empty;

   /// <summary>For a run note, a regular expression the note must start with to belong to this entry; null for a target's text.</summary>
   public string? Match { get; init; }

   /// <summary>The spans, which together must cover the whole text.</summary>
   public IReadOnlyList<ClassSpan> Spans { get; init; } = Array.Empty<ClassSpan>();

   #endregion Public Methods
}

/// <summary>
/// A piece of a recorded text: the part one span matched.
/// </summary>
/// <param name="Start">Where the piece starts in the white-space-collapsed text.</param>
/// <param name="Text">The piece, verbatim from the collapsed text.</param>
/// <param name="Span">The span that matched it.</param>
/// <param name="Unlisted">True when no entry covered the text and the piece stands for all of it, as unverified.</param>
public sealed record TextPiece( int Start, string Text, ClassSpan Span, bool Unlisted = false );

/// <summary>
/// The sheet is wrong, or a recorded text has a clause the sheet does not classify; the message says which and where.
/// </summary>
public sealed class RecordedTextException : Exception
{
   /// <summary>
   /// Creates the exception.
   /// </summary>
   /// <param name="message">What is wrong.</param>
   public RecordedTextException( string message ) : base( message )
   {
   }
}

/// <summary>
/// The report-layer list that classifies every clause of the recorded engine texts the report prints (durability, index, effort) and of
/// the run notes it quotes as its method, loaded from deploy/bench/recorded-text-classes.json.
/// Why a list and not a change to the sinks: the recorded index and durability texts are part of an engine's identity between sessions, so
/// the sinks stay as they are and the report says, clause by clause, how each is backed.
/// Why spans that must cover the whole text: a clause added to a sink's text is then not classified, and the report refuses (or, for run
/// notes, prints it as unverified), so no clause can reach the report without a class.
/// Policy per field: refuse (a text with an uncovered clause stops the report) or label (it is printed whole as unverified and counted).
/// </summary>
public static class RecordedTextClasses
{
   #region Data Members

   /// <summary>The repository path of the sheet.</summary>
   public const string FILE = "deploy/bench/recorded-text-classes.json";

   /// <summary>Policy: an uncovered clause stops the report.</summary>
   public const string REFUSE = "refuse";

   /// <summary>Policy: an uncovered text is printed whole as unverified and counted.</summary>
   public const string LABEL = "label";

   /// <summary>The fields a sheet may classify for a target.</summary>
   public static readonly IReadOnlyList<string> TARGET_FIELDS = new[] { "durability", "index" };

   /// <summary>The field of a run note.</summary>
   public const string NOTES_FIELD = "notes";

   /// <summary>Largest sheet read.</summary>
   public const long MAX_BYTES = 4L * 1024 * 1024;

   private static readonly TimeSpan MATCH_TIMEOUT = TimeSpan.FromSeconds( 5 );
   private static readonly Regex SPACE = new( @"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant, MATCH_TIMEOUT );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the sheet.
   /// </summary>
   /// <param name="path">The sheet's path.</param>
   /// <returns>The sheet.</returns>
   /// <exception cref="RecordedTextException">The file is missing, too large, not valid JSON, or a field is wrong.</exception>
   public static ClassSheet Load( string path )
   {
      var info = new FileInfo( path );
      if( !info.Exists )
      {
         throw new RecordedTextException( $"the recorded-text classes are missing: {path}" );
      }

      if( info.Length > MAX_BYTES )
      {
         throw new RecordedTextException( $"{path} is {info.Length} bytes, over the {MAX_BYTES} byte limit" );
      }

      try
      {
         using JsonDocument document = JsonDocument.Parse( File.ReadAllText( path ) );
         return ReadSheet( document.RootElement, path );
      }
      catch( JsonException ex )
      {
         throw new RecordedTextException( $"{path} is not valid JSON: {ex.Message}" );
      }
   }

   /// <summary>
   /// Collapses every run of white space to one space and trims, the way the audit compares a quote with its field.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The collapsed text.</returns>
   public static string Collapse( string text )
   {
      return SPACE.Replace( text, " " ).Trim();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads the sheet's object.
   /// </summary>
   /// <param name="root">The root element.</param>
   /// <param name="path">The file, for messages.</param>
   /// <returns>The sheet.</returns>
   private static ClassSheet ReadSheet( JsonElement root, string path )
   {
      if( root.ValueKind != JsonValueKind.Object )
      {
         throw new RecordedTextException( $"{path} must hold a JSON object" );
      }

      var entries = new List<ClassEntry>();
      foreach( JsonElement element in Items( root, "texts", path ) )
      {
         entries.Add( ReadEntry( element, path, notes: false ) );
      }

      foreach( JsonElement element in Items( root, "notes", path ) )
      {
         entries.Add( ReadEntry( element, path, notes: true ) );
      }

      var policy = new Dictionary<string, string>( StringComparer.Ordinal );
      if( root.TryGetProperty( "policy", out JsonElement p ) && p.ValueKind == JsonValueKind.Object )
      {
         foreach( JsonProperty property in p.EnumerateObject() )
         {
            string value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : string.Empty;
            policy[property.Name] = value is REFUSE or LABEL ? value : throw new RecordedTextException( $"{path}: policy for {property.Name} must be {REFUSE} or {LABEL}, not '{value}'" );
         }
      }

      var evidence = new Dictionary<string, string>( StringComparer.Ordinal );
      if( root.TryGetProperty( "evidence", out JsonElement e ) && e.ValueKind == JsonValueKind.Object )
      {
         foreach( JsonProperty property in e.EnumerateObject() )
         {
            evidence[property.Name] = property.Value.GetString() ?? throw new RecordedTextException( $"{path}: evidence {property.Name} must be a path" );
         }
      }

      return new ClassSheet( entries, policy, evidence );
   }

   /// <summary>
   /// The elements of an array property, or none when it is absent.
   /// </summary>
   /// <param name="element">The object.</param>
   /// <param name="name">The property.</param>
   /// <param name="path">The file, for messages.</param>
   /// <returns>The elements.</returns>
   private static IEnumerable<JsonElement> Items( JsonElement element, string name, string path )
   {
      if( !element.TryGetProperty( name, out JsonElement list ) )
      {
         return Array.Empty<JsonElement>();
      }

      return list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : throw new RecordedTextException( $"{path}: {name} must be an array" );
   }

   /// <summary>
   /// Reads one entry and its spans.
   /// </summary>
   /// <param name="element">The entry's object.</param>
   /// <param name="path">The file, for messages.</param>
   /// <param name="notes">True for a run-note entry.</param>
   /// <returns>The entry.</returns>
   private static ClassEntry ReadEntry( JsonElement element, string path, bool notes )
   {
      string target = Text( element, "target" ) ?? string.Empty;
      string field = notes ? NOTES_FIELD : Text( element, "field" ) ?? throw new RecordedTextException( $"{path}: an entry has no field" );
      string? match = notes ? Text( element, "match" ) ?? throw new RecordedTextException( $"{path}: a note entry has no match pattern" ) : null;
      if( !notes && ( target.Length == 0 || !TARGET_FIELDS.Contains( field ) ) )
      {
         throw new RecordedTextException( $"{path}: an entry needs a target and a field of {string.Join( " or ", TARGET_FIELDS )} (got '{target}', '{field}')" );
      }

      var spans = Items( element, "spans", path ).Select( s => ReadSpan( s, path, $"{target}.{field}" ) ).ToList();
      if( spans.Count == 0 )
      {
         throw new RecordedTextException( $"{path}: {target}.{field} has no spans" );
      }

      return new ClassEntry { Target = target, Field = field, Match = match, Spans = spans };
   }

   /// <summary>
   /// Reads one span, checking its fields.
   /// </summary>
   /// <param name="element">The span's object.</param>
   /// <param name="path">The file, for messages.</param>
   /// <param name="where">The entry, for messages.</param>
   /// <returns>The span.</returns>
   private static ClassSpan ReadSpan( JsonElement element, string path, string where )
   {
      string[] allowed = { "text", "pattern", "class", "basis", "drop", "dropWhenAbsent", "contradictedBy", "docRows", "evidence", "cites" };
      string? unknown = element.EnumerateObject().Select( p => p.Name ).FirstOrDefault( n => !allowed.Contains( n ) );
      if( unknown != null )
      {
         throw new RecordedTextException( $"{path}: {where} has a span with an unknown field '{unknown}'" );
      }

      string? text = Text( element, "text" );
      string? pattern = Text( element, "pattern" );
      string cls = Text( element, "class" ) ?? string.Empty;
      List<string> basis = Strings( element, "basis" );
      if( ( text == null ) == ( pattern == null ) )
      {
         throw new RecordedTextException( $"{path}: a span of {where} needs exactly one of text and pattern" );
      }

      if( !TextClass.ALL.Contains( cls ) )
      {
         throw new RecordedTextException( $"{path}: a span of {where} has class '{cls}', not one of {string.Join( ", ", TextClass.ALL )}" );
      }

      if( ( cls == TextClass.UNVERIFIED ) != ( basis.Count == 0 ) )
      {
         throw new RecordedTextException( $"{path}: a span of {where} is {cls} with {basis.Count} basis source(s); only an unverified span has none" );
      }

      return new ClassSpan
      {
         Text = text == null ? null : Collapse( text ),
         Pattern = pattern,
         Class = cls,
         Basis = basis,
         Drop = Text( element, "drop" ),
         DropWhenAbsent = Strings( element, "dropWhenAbsent" ),
         Contradicted = ReadContradicted( element ),
         DocRows = ReadDocRows( element ),
         Evidence = Text( element, "evidence" ),
         Cites = Text( element, "cites" ),
      };
   }

   /// <summary>
   /// Reads a span's contradiction, when it has one.
   /// </summary>
   /// <param name="element">The span's object.</param>
   /// <returns>The contradiction, or null.</returns>
   private static ContradictedBy? ReadContradicted( JsonElement element )
   {
      if( !element.TryGetProperty( "contradictedBy", out JsonElement c ) || c.ValueKind != JsonValueKind.Object )
      {
         return null;
      }

      return new ContradictedBy( Text( c, "file" ) ?? string.Empty, Text( c, "prefix" ) ?? string.Empty, Text( c, "words" ) ?? string.Empty );
   }

   /// <summary>
   /// Reads a span's documentation rows.
   /// </summary>
   /// <param name="element">The span's object.</param>
   /// <returns>The rows.</returns>
   private static List<DocRow> ReadDocRows( JsonElement element )
   {
      var rows = new List<DocRow>();
      if( element.TryGetProperty( "docRows", out JsonElement list ) && list.ValueKind == JsonValueKind.Array )
      {
         foreach( JsonElement pair in list.EnumerateArray().Where( p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 ) )
         {
            rows.Add( new DocRow( pair[0].GetString() ?? string.Empty, pair[1].GetString() ?? string.Empty ) );
         }
      }

      return rows;
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
   /// A property that is an array of strings.
   /// </summary>
   /// <param name="element">The object.</param>
   /// <param name="name">The property.</param>
   /// <returns>The strings; empty when absent.</returns>
   private static List<string> Strings( JsonElement element, string name )
   {
      return element.TryGetProperty( name, out JsonElement list ) && list.ValueKind == JsonValueKind.Array
         ? list.EnumerateArray().Where( v => v.ValueKind == JsonValueKind.String ).Select( v => v.GetString()! ).ToList()
         : new List<string>();
   }

   #endregion Private Methods
}

/// <summary>
/// The loaded sheet: the entries, the policy per field and the paths of the saved evidence.
/// </summary>
public sealed class ClassSheet
{
   #region Data Members

   private static readonly TimeSpan MATCH_TIMEOUT = TimeSpan.FromSeconds( 5 );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sheet.
   /// </summary>
   /// <param name="entries">Every entry, target texts and note entries.</param>
   /// <param name="policy">The policy of each field, by field name.</param>
   /// <param name="evidence">The repository path of each named piece of evidence.</param>
   public ClassSheet( IReadOnlyList<ClassEntry> entries, IReadOnlyDictionary<string, string> policy, IReadOnlyDictionary<string, string> evidence )
   {
      Entries = entries;
      Policy = policy;
      Evidence = evidence;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Every entry.</summary>
   public IReadOnlyList<ClassEntry> Entries { get; }

   /// <summary>The policy of each field; a field without one is refused.</summary>
   public IReadOnlyDictionary<string, string> Policy { get; }

   /// <summary>The repository path of each named piece of evidence.</summary>
   public IReadOnlyDictionary<string, string> Evidence { get; }

   /// <summary>
   /// The entry of a target's text.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="field">The field.</param>
   /// <returns>The entry, or null when the sheet has none.</returns>
   public ClassEntry? Find( string target, string field )
   {
      return Entries.FirstOrDefault( e => e.Match == null && e.Target == target && e.Field == field );
   }

   /// <summary>
   /// Splits a recorded text into the pieces its spans match, so that every clause of it has a class.
   /// </summary>
   /// <param name="target">The target, or empty for a run note.</param>
   /// <param name="field">The field.</param>
   /// <param name="text">The recorded text.</param>
   /// <returns>The pieces in text order.</returns>
   /// <exception cref="RecordedTextException">No entry covers the text and the policy of the field is refuse; the message names the clause.</exception>
   public IReadOnlyList<TextPiece> Split( string target, string field, string text )
   {
      string collapsed = RecordedTextClasses.Collapse( text );
      ClassEntry? entry = target.Length == 0 ? NoteEntry( collapsed ) : Find( target, field );
      string problem;
      if( entry == null )
      {
         problem = target.Length == 0 ? $"no entry classifies the run note that starts '{Shorten( collapsed )}'" : $"no entry classifies the {field} text of {target}";
      }
      else
      {
         var pieces = new List<TextPiece>();
         problem = Cover( entry, collapsed, pieces );
         if( problem.Length == 0 )
         {
            return pieces;
         }
      }

      if( !Policy.TryGetValue( field, out string? policy ) || policy == RecordedTextClasses.REFUSE )
      {
         throw new RecordedTextException( $"{problem}; add it to {RecordedTextClasses.FILE}" );
      }

      return new[] { new TextPiece( 0, collapsed, new ClassSpan { Text = collapsed, Class = TextClass.UNVERIFIED }, Unlisted: true ) };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The run-note entry whose pattern the note starts with.
   /// </summary>
   /// <param name="note">The collapsed note.</param>
   /// <returns>The entry, or null.</returns>
   /// <exception cref="RecordedTextException">Two entries claim the note.</exception>
   private ClassEntry? NoteEntry( string note )
   {
      List<ClassEntry> found = Entries.Where( e => e.Match != null && Regex.IsMatch( note, e.Match, RegexOptions.CultureInvariant, MATCH_TIMEOUT ) ).ToList();
      return found.Count <= 1 ? found.FirstOrDefault() : throw new RecordedTextException( $"{found.Count} note entries claim the run note that starts '{Shorten( note )}'" );
   }

   /// <summary>
   /// Matches the spans of an entry one after another from the start of the text.
   /// </summary>
   /// <param name="entry">The entry.</param>
   /// <param name="text">The collapsed text.</param>
   /// <param name="pieces">Receives the pieces.</param>
   /// <returns>Empty when the spans cover the text, else what does not match.</returns>
   private static string Cover( ClassEntry entry, string text, List<TextPiece> pieces )
   {
      string where = entry.Match == null ? $"{entry.Target}.{entry.Field}" : "a run note";
      int pos = 0;
      for( int i = 0; i < entry.Spans.Count; i++ )
      {
         ClassSpan span = entry.Spans[i];
         while( pos < text.Length && text[pos] == ' ' )
         {
            pos++;
         }

         int length = Match( span, text, pos );
         if( length <= 0 )
         {
            string expected = span.Text ?? span.Pattern ?? string.Empty;
            return $"the {where} text has a clause the classes do not match at character {pos} (clause {i + 1} of {entry.Spans.Count} expects '{Shorten( expected )}'; the text reads '{Shorten( text[pos..] )}')";
         }

         pieces.Add( new TextPiece( pos, text.Substring( pos, length ), span ) );
         pos += length;
      }

      string tail = text[pos..].Trim();
      return tail.Length == 0 ? string.Empty : $"the {where} text has a clause no span classifies: '{Shorten( tail )}'";
   }

   /// <summary>
   /// How many characters a span matches at a position.
   /// </summary>
   /// <param name="span">The span.</param>
   /// <param name="text">The collapsed text.</param>
   /// <param name="pos">The position.</param>
   /// <returns>The length, or 0 when it does not match there.</returns>
   private static int Match( ClassSpan span, string text, int pos )
   {
      if( span.Text != null )
      {
         return string.CompareOrdinal( text, pos, span.Text, 0, span.Text.Length ) == 0 && pos + span.Text.Length <= text.Length ? span.Text.Length : 0;
      }

      Match m = new Regex( span.Pattern!, RegexOptions.CultureInvariant | RegexOptions.Singleline, MATCH_TIMEOUT ).Match( text, pos );
      return m.Success && m.Index == pos ? m.Length : 0;
   }

   /// <summary>
   /// A text cut to 80 characters for a message.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The shortened text.</returns>
   private static string Shorten( string text )
   {
      return text.Length <= 80 ? text : text[..80] + "...";
   }

   #endregion Private Methods
}
