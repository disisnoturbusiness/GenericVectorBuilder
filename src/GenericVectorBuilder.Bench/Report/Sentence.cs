using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// One thing a sentence rests on: where it comes from and the value it contributes.
/// Why: no number or fact in generated report text may be typed by hand. Each is bound to a
/// recorded field, a token in a repository file, a quote from a saved documentation page or a
/// value of the consolidated result, and a check resolves every binding again from the raw files
/// before the report is written.
/// </summary>
/// <param name="Kind">One of <see cref="SourceKinds"/>.</param>
/// <param name="Ref">The reference, in the grammar of <see cref="SourceResolver"/>.</param>
/// <param name="Value">What the sentence prints for this source: a number as printed, or the token or quote.</param>
public sealed record SentenceSource( string Kind, string Ref, string Value );

/// <summary>
/// One sentence of generated report text with the sources every fact in it comes from.
/// Why a record of its own: the report prints prose only through these objects, so the audit sees
/// exactly what a reader sees and nothing is prose that the audit has not checked.
/// </summary>
/// <param name="Slot">Where the sentence sits in the report (for example "headline"); the headline has a shorter word limit.</param>
/// <param name="Text">The sentence as printed.</param>
/// <param name="Sources">The bindings for the facts and numbers in the text.</param>
public sealed record Sentence( string Slot, string Text, IReadOnlyList<SentenceSource> Sources );

/// <summary>
/// The kinds of source a sentence can name.
/// Why a closed list: each kind has its own way of being re-checked, so an unknown kind cannot be
/// checked at all and is refused.
/// </summary>
public static class SourceKinds
{
   #region Data Members

   /// <summary>A value or token in a raw results.json, read by JSON path.</summary>
   public const string RESULTS = "results";

   /// <summary>A token on a code line of a repository file.</summary>
   public const string FILE = "file";

   /// <summary>A quote from a saved documentation page.</summary>
   public const string DOC = "doc";

   /// <summary>A value of the consolidated result.</summary>
   public const string CONSOLIDATED = "consolidated";

   /// <summary>
   /// A verbatim quote of a text field the tool recorded in results.json (a durability note, an
   /// index text). A sentence made of one is checked against the raw field and is exempt from the
   /// number, banned-word and length rules, because recorded text is the tool's own wording.
   /// </summary>
   public const string QUOTE = "quote";

   /// <summary>A check that a word does not occur anywhere in a target's recorded text ("not recorded").</summary>
   public const string ABSENT = "absent";

   /// <summary>Every valid kind.</summary>
   public static readonly IReadOnlyList<string> ALL = new[] { RESULTS, FILE, DOC, CONSOLIDATED, QUOTE, ABSENT };

   #endregion Data Members
}

/// <summary>
/// What a source reference resolved to when it was read again.
/// </summary>
/// <param name="Found">True when the reference points at something that exists.</param>
/// <param name="Value">The value found (after any conversion), or null when not found.</param>
/// <param name="Detail">Where it was found ("path:line", "3 of 3 runs") or why it was not.</param>
/// <param name="Evidence">The text the value was found in: the token, the matched code line or the quote; null when not found.</param>
public sealed record SourceResolution( bool Found, string? Value, string Detail, string? Evidence = null )
{
   /// <summary>
   /// A reference that resolved.
   /// </summary>
   /// <param name="value">The value found.</param>
   /// <param name="detail">Where it was found.</param>
   /// <param name="evidence">The text it was found in.</param>
   /// <returns>The resolution.</returns>
   public static SourceResolution Ok( string value, string detail, string? evidence = null )
   {
      return new SourceResolution( true, value, detail, evidence ?? value );
   }

   /// <summary>
   /// A reference that did not resolve.
   /// </summary>
   /// <param name="detail">Why not.</param>
   /// <returns>The resolution.</returns>
   public static SourceResolution Missing( string detail )
   {
      return new SourceResolution( false, null, detail );
   }
}

/// <summary>
/// Reads a source reference again from the raw files.
/// Why an interface: the audit must not know where files live, and its tests plant stale values
/// without touching the real files.
/// </summary>
public interface ISourceResolver
{
   /// <summary>
   /// Resolves one source reference.
   /// </summary>
   /// <param name="source">The source to read again.</param>
   /// <returns>What it resolves to now; never throws for a reference that simply points at nothing.</returns>
   SourceResolution Resolve( SentenceSource source );
}

/// <summary>
/// One raw results.json of a run, parsed, with the name of its folder.
/// Why raw: facts and sentences are checked against what the run recorded, not against the
/// consolidator's model of it, so a bug in the model cannot hide a false sentence.
/// </summary>
public sealed class FactRun : IDisposable
{
   #region Data Members

   /// <summary>Largest results.json that is read; a bigger one is refused rather than loaded.</summary>
   public const long MAX_FILE_BYTES = 64L * 1024 * 1024;

   private readonly JsonDocument _document;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Wraps a parsed results.json.
   /// </summary>
   /// <param name="name">The run's folder name.</param>
   /// <param name="document">The parsed file; this object owns it from now on.</param>
   public FactRun( string name, JsonDocument document )
   {
      ArgumentException.ThrowIfNullOrWhiteSpace( name );
      Name = name;
      _document = document ?? throw new ArgumentNullException( nameof( document ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The run's folder name, used to pick one run in a reference ("results@name:...").</summary>
   public string Name { get; }

   /// <summary>The root object of the results.json.</summary>
   public JsonElement Root => _document.RootElement;

   /// <summary>
   /// Reads a run's results.json.
   /// </summary>
   /// <param name="resultsJsonPath">Path of the file; its folder name becomes the run's name.</param>
   /// <returns>The run; the caller disposes it.</returns>
   /// <exception cref="FileNotFoundException">The file does not exist.</exception>
   /// <exception cref="InvalidDataException">The file is too large or is not valid JSON; the message names the path.</exception>
   public static FactRun Load( string resultsJsonPath )
   {
      var info = new FileInfo( resultsJsonPath );
      if( !info.Exists )
      {
         throw new FileNotFoundException( $"results.json not found: {resultsJsonPath}", resultsJsonPath );
      }

      if( info.Length > MAX_FILE_BYTES )
      {
         throw new InvalidDataException( $"{resultsJsonPath} is {info.Length} bytes, over the {MAX_FILE_BYTES} byte limit" );
      }

      try
      {
         using FileStream stream = File.OpenRead( resultsJsonPath );
         string folder = info.Directory?.Name ?? throw new InvalidDataException( $"{resultsJsonPath} has no folder" );
         return new FactRun( folder, JsonDocument.Parse( stream ) );
      }
      catch( JsonException ex )
      {
         throw new InvalidDataException( $"{resultsJsonPath} is not valid JSON: {ex.Message}", ex );
      }
   }

   #endregion Public Methods

   #region IDisposable

   /// <summary>
   /// Releases the parsed document.
   /// </summary>
   public void Dispose()
   {
      _document.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// The real <see cref="ISourceResolver"/>: reads raw results.json files, repository files, saved
/// documentation pages and the consolidated result.
/// Reference grammar (a reference is the text after the kind):
///   results      "results[@folder]:path[#token][|conversion]". Path: dot-separated field names, each with
///                optional [selector]s, where a selector is an array index, key=value or a bare name
///                (meaning name=value), for example targets[oracle].index. With #token the value is
///                the token and it must occur in the field in every selected run; without it the
///                field is a single value that must be equal in every selected run. "@folder" selects
///                one run, otherwise all runs given are used.
///   quote        the same as results without a token; the value is the whole text of the field.
///   file         "repo/path#token": the token must occur on a code line (comment lines and trailing
///                comments are skipped). The detail is "path:line"; no line number is ever stored.
///   doc          "doc:repo/path#quote": the quote must lie inside one text node of the saved page
///                (tags, scripts and styles removed, entities decoded, white space collapsed).
///   consolidated "consolidated:path[|conversion]" read in the consolidated document.
///   absent       "absent:results:path#alt1|alt2": none of the alternatives occurs, as a whole word
///                ignoring case, anywhere in the text under the path in any selected run. The last field of the
///                path may be a list joined by '+', as in "targets[sqlitevec].index+engine+durability": the check
///                then covers those fields alone, each of which must be recorded in every run.
/// Conversions (closed list): bp-pct (basis points shown as a percent), bp-ratio (1 plus basis
/// points over 10000, shown as a ratio such as 1.35).
/// Why: one reader for every kind keeps the fact checks and the sentence audit from disagreeing
/// about what a reference means.
/// </summary>
public sealed class SourceResolver : ISourceResolver
{
   #region Data Members

   /// <summary>Largest repository file or saved page that is read.</summary>
   public const long MAX_REPO_FILE_BYTES = 16L * 1024 * 1024;

   private static readonly Regex TAG_PATTERN = new( "<[^>]*>", RegexOptions.Compiled, TimeSpan.FromSeconds( 10 ) );
   private static readonly Regex BLOCK_PATTERN = new( @"<(script|style)\b.*?</\1\s*>|<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds( 10 ) );
   private static readonly Regex SPACE_PATTERN = new( @"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds( 10 ) );

   private readonly string _repoRoot;
   private readonly IReadOnlyList<FactRun> _runs;
   private readonly JsonElement? _consolidated;
   private readonly Dictionary<string, string[]> _lineCache = new( StringComparer.Ordinal );
   private readonly Dictionary<string, List<string>> _nodeCache = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a resolver over a repository, the runs a reference may read and, optionally, the consolidated document.
   /// </summary>
   /// <param name="repoRoot">Repository root; file and doc references are relative to it and may not leave it.</param>
   /// <param name="runs">The raw runs results references read; may be empty when only other kinds are resolved.</param>
   /// <param name="consolidated">The consolidated document, or null when consolidated references are not used.</param>
   public SourceResolver( string repoRoot, IReadOnlyList<FactRun> runs, JsonElement? consolidated = null )
   {
      ArgumentException.ThrowIfNullOrWhiteSpace( repoRoot );
      _repoRoot = Path.GetFullPath( repoRoot );
      _runs = runs ?? throw new ArgumentNullException( nameof( runs ) );
      _consolidated = consolidated;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The runs a reference without "@folder" reads.</summary>
   public IReadOnlyList<FactRun> Runs => _runs;

   /// <summary>
   /// Builds the sentence source for a fact row's source string, so a sentence can cite a fact
   /// without retyping it.
   /// </summary>
   /// <param name="factSource">A source string as written in engine-facts.json.</param>
   /// <returns>The kind, the reference and the token or quote as the value.</returns>
   /// <exception cref="FormatException">The string has no token.</exception>
   public static SentenceSource FromFactSource( string factSource )
   {
      ArgumentException.ThrowIfNullOrWhiteSpace( factSource );
      string kind = KindOf( factSource );
      int hash = factSource.IndexOf( '#' );
      if( hash < 0 || hash == factSource.Length - 1 )
      {
         throw new FormatException( $"source '{factSource}' has no #token" );
      }

      return new SentenceSource( kind, factSource, factSource[( hash + 1 )..] );
   }

   /// <summary>
   /// The kind of a source string by its prefix: results:, doc:, absent:, otherwise a file.
   /// </summary>
   /// <param name="source">The source string.</param>
   /// <returns>One of <see cref="SourceKinds"/>.</returns>
   public static string KindOf( string source )
   {
      if( source.StartsWith( "results", StringComparison.Ordinal ) && ( source.Length > 7 && ( source[7] == ':' || source[7] == '@' ) ) )
      {
         return SourceKinds.RESULTS;
      }

      if( source.StartsWith( "doc:", StringComparison.Ordinal ) )
      {
         return SourceKinds.DOC;
      }

      if( source.StartsWith( "absent:", StringComparison.Ordinal ) )
      {
         return SourceKinds.ABSENT;
      }

      return SourceKinds.FILE;
   }

   /// <summary>
   /// Reads a source reference again from the raw files, by its kind.
   /// Why: a reference that is malformed or points at nothing is a finding for the audit to report,
   /// not an exception, so one bad sentence does not hide the others.
   /// </summary>
   /// <param name="source">The source to read.</param>
   /// <returns>What it resolves to now.</returns>
   public SourceResolution Resolve( SentenceSource source )
   {
      ArgumentNullException.ThrowIfNull( source );
      try
      {
         return source.Kind switch
         {
            SourceKinds.RESULTS => ResolveResults( ParseRef( source.Ref, "results" ), false ),
            SourceKinds.QUOTE => ResolveResults( ParseRef( source.Ref, "results" ), true ),
            SourceKinds.FILE => ResolveFile( source.Ref ),
            SourceKinds.DOC => ResolveDoc( source.Ref ),
            SourceKinds.CONSOLIDATED => ResolveConsolidated( source.Ref ),
            SourceKinds.ABSENT => ResolveAbsent( source.Ref ),
            _ => SourceResolution.Missing( $"unknown source kind '{source.Kind}'" ),
         };
      }
      catch( FormatException ex )
      {
         return SourceResolution.Missing( $"bad reference '{source.Ref}': {ex.Message}" );
      }
   }

   /// <summary>
   /// Follows a path in a JSON document.
   /// </summary>
   /// <param name="root">Where to start.</param>
   /// <param name="path">Dot-separated names with optional [selector]s.</param>
   /// <param name="element">The element found.</param>
   /// <param name="problem">Why nothing was found, or empty.</param>
   /// <returns>True when the path leads to a value that is not JSON null.</returns>
   /// <exception cref="FormatException">The path itself is malformed.</exception>
   public static bool TryPath( JsonElement root, string path, out JsonElement element, out string problem )
   {
      element = root;
      problem = string.Empty;
      foreach( (string name, List<string> selectors) in ParsePath( path ) )
      {
         if( name.Length > 0 )
         {
            if( element.ValueKind != JsonValueKind.Object || !element.TryGetProperty( name, out JsonElement child ) || child.ValueKind == JsonValueKind.Null )
            {
               problem = $"no value at '{name}'";
               return false;
            }

            element = child;
         }

         foreach( string selector in selectors )
         {
            if( !TrySelect( element, selector, out JsonElement picked ) )
            {
               problem = $"nothing matches [{selector}]";
               return false;
            }

            element = picked;
         }
      }

      return true;
   }

   /// <summary>
   /// All the text under an element: strings and numbers as written, joined by new lines, object
   /// property names left out.
   /// Why: a token may sit in a string field, in one note of a list, or anywhere under a target.
   /// </summary>
   /// <param name="element">The element.</param>
   /// <returns>The text, without a trailing new line.</returns>
   public static string Flatten( JsonElement element )
   {
      var builder = new StringBuilder();
      Append( element, builder );
      return builder.ToString().TrimEnd( '\n' );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>A reference split into its parts.</summary>
   /// <param name="Folder">The selected run, or null for all.</param>
   /// <param name="Path">The JSON path.</param>
   /// <param name="Token">The token after #, or null.</param>
   /// <param name="Conv">The conversion after |, or null.</param>
   private sealed record ParsedRef( string? Folder, string Path, string? Token, string? Conv );

   /// <summary>
   /// Splits "prefix[@folder]:path[#token][|conversion]".
   /// </summary>
   /// <param name="text">The reference.</param>
   /// <param name="prefix">The expected prefix.</param>
   /// <returns>The parts.</returns>
   /// <exception cref="FormatException">The text does not follow the grammar.</exception>
   private static ParsedRef ParseRef( string text, string prefix )
   {
      if( !text.StartsWith( prefix, StringComparison.Ordinal ) )
      {
         throw new FormatException( $"must start with '{prefix}'" );
      }

      int at = prefix.Length;
      string? folder = null;
      if( at < text.Length && text[at] == '@' )
      {
         int colon = text.IndexOf( ':', at );
         if( colon < 0 )
         {
            throw new FormatException( "'@folder' must be followed by ':'" );
         }

         folder = text[( at + 1 )..colon];
         at = colon;
      }

      if( at >= text.Length || text[at] != ':' )
      {
         throw new FormatException( $"'{prefix}' must be followed by ':' or '@folder:'" );
      }

      string rest = text[( at + 1 )..];
      int hash = rest.IndexOf( '#' );
      if( hash >= 0 )
      {
         return new ParsedRef( folder, rest[..hash], rest[( hash + 1 )..], null );
      }

      int bar = rest.LastIndexOf( '|' );
      return bar >= 0 ? new ParsedRef( folder, rest[..bar], null, rest[( bar + 1 )..] ) : new ParsedRef( folder, rest, null, null );
   }

   /// <summary>
   /// Splits a path into names with their selectors.
   /// </summary>
   /// <param name="path">The path.</param>
   /// <returns>The segments in order.</returns>
   /// <exception cref="FormatException">An empty segment, a missing ']' or a stray character.</exception>
   private static List<(string Name, List<string> Selectors)> ParsePath( string path )
   {
      if( string.IsNullOrWhiteSpace( path ) )
      {
         throw new FormatException( "empty path" );
      }

      var segments = new List<(string, List<string>)>();
      int i = 0;
      while( i < path.Length )
      {
         int start = i;
         while( i < path.Length && path[i] != '.' && path[i] != '[' )
         {
            i++;
         }

         string name = path[start..i];
         var selectors = new List<string>();
         while( i < path.Length && path[i] == '[' )
         {
            int close = path.IndexOf( ']', i );
            if( close < 0 )
            {
               throw new FormatException( "missing ']'" );
            }

            selectors.Add( path[( i + 1 )..close] );
            i = close + 1;
         }

         if( name.Length == 0 && selectors.Count == 0 )
         {
            throw new FormatException( "empty path segment" );
         }

         segments.Add( (name, selectors) );
         if( i < path.Length )
         {
            if( path[i] != '.' || i == path.Length - 1 )
            {
               throw new FormatException( $"unexpected '{path[i]}' at {i}" );
            }

            i++;
         }
      }

      return segments;
   }

   /// <summary>
   /// Applies one [selector] to an array: an index, key=value, or a bare name meaning name=value.
   /// </summary>
   /// <param name="element">The array.</param>
   /// <param name="selector">The selector text.</param>
   /// <param name="picked">The matching element.</param>
   /// <returns>True when one matched.</returns>
   private static bool TrySelect( JsonElement element, string selector, out JsonElement picked )
   {
      picked = default;
      if( element.ValueKind != JsonValueKind.Array )
      {
         return false;
      }

      if( int.TryParse( selector, NumberStyles.None, CultureInfo.InvariantCulture, out int index ) )
      {
         if( index >= element.GetArrayLength() )
         {
            return false;
         }

         picked = element[index];
         return true;
      }

      int eq = selector.IndexOf( '=' );
      string key = eq >= 0 ? selector[..eq] : "name";
      string wanted = eq >= 0 ? selector[( eq + 1 )..] : selector;
      foreach( JsonElement item in element.EnumerateArray() )
      {
         if( item.ValueKind == JsonValueKind.Object && item.TryGetProperty( key, out JsonElement value ) && string.Equals( ScalarText( value ), wanted, StringComparison.Ordinal ) )
         {
            picked = item;
            return true;
         }
      }

      return false;
   }

   /// <summary>
   /// A scalar's text as written, or null for an object, an array or JSON null.
   /// </summary>
   /// <param name="element">The element.</param>
   /// <returns>The text or null.</returns>
   private static string? ScalarText( JsonElement element )
   {
      return element.ValueKind switch
      {
         JsonValueKind.String => element.GetString(),
         JsonValueKind.Number => element.GetRawText(),
         JsonValueKind.True => "true",
         JsonValueKind.False => "false",
         _ => null,
      };
   }

   /// <summary>
   /// Adds the text of an element and of everything under it.
   /// </summary>
   /// <param name="element">The element.</param>
   /// <param name="builder">Where the text goes, one value per line.</param>
   private static void Append( JsonElement element, StringBuilder builder )
   {
      switch( element.ValueKind )
      {
         case JsonValueKind.Array:
            foreach( JsonElement item in element.EnumerateArray() )
            {
               Append( item, builder );
            }

            break;
         case JsonValueKind.Object:
            foreach( JsonProperty property in element.EnumerateObject() )
            {
               Append( property.Value, builder );
            }

            break;
         default:
            string? text = ScalarText( element );
            if( text is not null )
            {
               builder.Append( text ).Append( '\n' );
            }

            break;
      }
   }

   /// <summary>
   /// Applies a conversion from the closed list to a number.
   /// </summary>
   /// <param name="value">The value as read.</param>
   /// <param name="conv">The conversion name, or null for none.</param>
   /// <returns>The converted value in invariant format, or the input when there is no conversion.</returns>
   /// <exception cref="FormatException">The value is not a number or the conversion is unknown.</exception>
   private static string ApplyConversion( string value, string? conv )
   {
      if( conv is null )
      {
         return value;
      }

      if( !decimal.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number ) )
      {
         throw new FormatException( $"conversion '{conv}' needs a number, the value is '{value}'" );
      }

      decimal converted = conv switch
      {
         "bp-pct" => number / 100m,
         "bp-ratio" => 1m + ( number / 10000m ),
         _ => throw new FormatException( $"unknown conversion '{conv}'" ),
      };
      return converted.ToString( "0.############################", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// The runs a reference reads: the named one, or all.
   /// </summary>
   /// <param name="folder">The folder after "@", or null.</param>
   /// <returns>The runs; empty when the named one is not among them.</returns>
   private IReadOnlyList<FactRun> SelectRuns( string? folder )
   {
      return folder is null ? _runs : _runs.Where( r => string.Equals( r.Name, folder, StringComparison.Ordinal ) ).ToList();
   }

   /// <summary>
   /// Resolves a results or quote reference in every selected run.
   /// </summary>
   /// <param name="reference">The parsed reference.</param>
   /// <param name="quote">True for a quote reference (the whole text of a field).</param>
   /// <returns>The resolution.</returns>
   private SourceResolution ResolveResults( ParsedRef reference, bool quote )
   {
      IReadOnlyList<FactRun> selected = SelectRuns( reference.Folder );
      if( selected.Count == 0 )
      {
         return SourceResolution.Missing( reference.Folder is null ? "no runs were given" : $"run '{reference.Folder}' is not among the runs given" );
      }

      var values = new List<string>();
      foreach( FactRun run in selected )
      {
         if( !TryPath( run.Root, reference.Path, out JsonElement element, out string problem ) )
         {
            return SourceResolution.Missing( $"{run.Name}: {problem} in {reference.Path}" );
         }

         if( reference.Token is not null )
         {
            if( !Flatten( element ).Contains( reference.Token, StringComparison.Ordinal ) && !HasPair( element, reference.Token ) )
            {
               return SourceResolution.Missing( $"{run.Name}: token not in {reference.Path}" );
            }

            continue;
         }

         if( !quote && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array )
         {
            return SourceResolution.Missing( $"{run.Name}: {reference.Path} is not a single value" );
         }

         values.Add( Flatten( element ) );
      }

      if( reference.Token is not null )
      {
         return SourceResolution.Ok( reference.Token, $"in all {selected.Count} run(s) selected, at {reference.Path}" );
      }

      if( values.Distinct( StringComparer.Ordinal ).Count() != 1 )
      {
         return SourceResolution.Missing( $"{reference.Path} differs between the runs" );
      }

      return SourceResolution.Ok( ApplyConversion( values[0], reference.Conv ), $"{selected.Count} run(s) agree at {reference.Path}" );
   }

   /// <summary>
   /// Whether an object holds a property whose name and scalar value make the token "name=value".
   /// Why: <see cref="Flatten"/> leaves property names out, so a recorded setting such as mhnsw_ef_search=100 or hnsw.ef_search=100
   /// (a name with a dot, which a path cannot reach) is found by its pair.
   /// </summary>
   /// <param name="element">The element searched.</param>
   /// <param name="token">The token, "name=value".</param>
   /// <returns>True when the element is an object with that pair.</returns>
   private static bool HasPair( JsonElement element, string token )
   {
      int equals = token.IndexOf( '=' );
      return element.ValueKind == JsonValueKind.Object && equals > 0
         && element.TryGetProperty( token[..equals], out JsonElement value ) && string.Equals( ScalarText( value ), token[( equals + 1 )..], StringComparison.Ordinal );
   }

   /// <summary>
   /// Resolves a consolidated reference in the consolidated document.
   /// </summary>
   /// <param name="reference">The reference "consolidated:path[|conversion]".</param>
   /// <returns>The resolution.</returns>
   private SourceResolution ResolveConsolidated( string reference )
   {
      if( _consolidated is null )
      {
         return SourceResolution.Missing( "no consolidated document was given" );
      }

      ParsedRef parsed = ParseRef( reference, "consolidated" );
      if( !TryPath( _consolidated.Value, parsed.Path, out JsonElement element, out string problem ) )
      {
         return SourceResolution.Missing( $"{problem} in {parsed.Path}" );
      }

      if( element.ValueKind is JsonValueKind.Object or JsonValueKind.Array )
      {
         return SourceResolution.Missing( $"{parsed.Path} is not a single value" );
      }

      return SourceResolution.Ok( ApplyConversion( Flatten( element ), parsed.Conv ), $"consolidated {parsed.Path}" );
   }

   /// <summary>
   /// Resolves an absence check: none of the alternatives occurs in the text under the path.
   /// </summary>
   /// <param name="reference">The reference "absent:results:path#alt1|alt2".</param>
   /// <returns>Found (the check holds) with the alternatives as the value, or missing with the first occurrence.</returns>
   private SourceResolution ResolveAbsent( string reference )
   {
      const string PREFIX = "absent:";
      if( !reference.StartsWith( PREFIX, StringComparison.Ordinal ) )
      {
         throw new FormatException( "must start with 'absent:'" );
      }

      ParsedRef parsed = ParseRef( reference[PREFIX.Length..], "results" );
      if( parsed.Token is null || _runs.Count == 0 )
      {
         return SourceResolution.Missing( parsed.Token is null ? "an absence check needs #alt1|alt2" : "no runs were given" );
      }

      IReadOnlyList<FactRun> selected = SelectRuns( parsed.Folder );
      if( selected.Count == 0 )
      {
         return SourceResolution.Missing( $"run '{parsed.Folder}' is not among the runs given" );
      }

      string[] alternatives = parsed.Token.Split( '|', StringSplitOptions.RemoveEmptyEntries );
      List<string> scope = ScopePaths( parsed.Path );
      foreach( FactRun run in selected )
      {
         foreach( string path in scope )
         {
            if( !TryPath( run.Root, path, out JsonElement element, out string problem ) )
            {
               return SourceResolution.Missing( $"{run.Name}: {problem} in {path}" );
            }

            IReadOnlyList<string> hits = BannedWords.FindIn( Flatten( element ), alternatives );
            if( hits.Count > 0 )
            {
               return SourceResolution.Missing( $"{run.Name}: '{hits[0]}' occurs in {path}" );
            }
         }
      }

      return SourceResolution.Ok( parsed.Token, $"none of {alternatives.Length} words in {selected.Count} run(s) at {parsed.Path}", "not recorded" );
   }

   /// <summary>
   /// The paths an absence check reads: the path itself, or, when its last field is a list joined by '+', one path per field.
   /// Why a field list: a target's whole record also holds text that is about something else (the engine-settings rows say a version was read
   /// "on an in-memory connection"), so a check about where the vectors are kept reads the index, engine and durability text alone.
   /// </summary>
   /// <param name="path">The path of the reference, such as targets[sqlitevec].index+engine+durability.</param>
   /// <returns>The paths to read, in the order of the list.</returns>
   private static List<string> ScopePaths( string path )
   {
      int dot = path.LastIndexOf( '.' );
      if( dot < 0 || dot < path.LastIndexOf( ']' ) || !path[( dot + 1 )..].Contains( '+', StringComparison.Ordinal ) )
      {
         return new List<string> { path };
      }

      string prefix = path[..dot];
      return path[( dot + 1 )..].Split( '+', StringSplitOptions.RemoveEmptyEntries ).Select( field => $"{prefix}.{field}" ).ToList();
   }

   /// <summary>
   /// Resolves a repository path to a full path, refusing anything outside the repository.
   /// </summary>
   /// <param name="relative">Path relative to the repository root.</param>
   /// <param name="problem">Why the path was refused, or empty.</param>
   /// <returns>The full path, or null when refused.</returns>
   private string? SafePath( string relative, out string problem )
   {
      problem = string.Empty;
      if( Path.IsPathRooted( relative ) || relative.Contains( '\\' ) || relative.Split( '/' ).Contains( ".." ) )
      {
         problem = $"'{relative}' must be a relative path inside the repository";
         return null;
      }

      string full = Path.GetFullPath( Path.Combine( _repoRoot, relative ) );
      if( !full.StartsWith( _repoRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal ) )
      {
         problem = $"'{relative}' leaves the repository";
         return null;
      }

      if( !File.Exists( full ) )
      {
         problem = $"{relative} does not exist";
         return null;
      }

      long size = new FileInfo( full ).Length;
      if( size > MAX_REPO_FILE_BYTES )
      {
         throw new InvalidDataException( $"{relative} is {size} bytes, over the {MAX_REPO_FILE_BYTES} byte limit" );
      }

      return full;
   }

   /// <summary>
   /// Resolves "repo/path#token": the token on a code line of the file.
   /// </summary>
   /// <param name="reference">The reference.</param>
   /// <returns>The resolution; the detail is "path:line" and the evidence is the matched line.</returns>
   private SourceResolution ResolveFile( string reference )
   {
      int hash = reference.IndexOf( '#' );
      if( hash <= 0 || hash == reference.Length - 1 )
      {
         return SourceResolution.Missing( "a file reference is <repo path>#<token>" );
      }

      string relative = reference[..hash];
      string token = reference[( hash + 1 )..];
      string? full = SafePath( relative, out string problem );
      if( full is null )
      {
         return SourceResolution.Missing( problem );
      }

      if( !_lineCache.TryGetValue( full, out string[]? lines ) )
      {
         lines = File.ReadAllLines( full );
         _lineCache[full] = lines;
      }

      (int line, string text)? hit = CodeLines.Find( relative, lines, token );
      return hit is null
         ? SourceResolution.Missing( $"token not on a code line of {relative}" )
         : SourceResolution.Ok( token, $"{relative}:{hit.Value.line}", hit.Value.text );
   }

   /// <summary>
   /// Resolves "doc:repo/path#quote": the quote inside one text node of the saved page.
   /// </summary>
   /// <param name="reference">The reference.</param>
   /// <returns>The resolution; the value is the quote.</returns>
   private SourceResolution ResolveDoc( string reference )
   {
      const string PREFIX = "doc:";
      if( !reference.StartsWith( PREFIX, StringComparison.Ordinal ) )
      {
         throw new FormatException( "must start with 'doc:'" );
      }

      int hash = reference.IndexOf( '#' );
      if( hash <= PREFIX.Length || hash == reference.Length - 1 )
      {
         return SourceResolution.Missing( "a doc reference is doc:<repo path>#<quote>" );
      }

      string relative = reference[PREFIX.Length..hash];
      string quote = Normalize( reference[( hash + 1 )..] );
      string? full = SafePath( relative, out string problem );
      if( full is null )
      {
         return SourceResolution.Missing( problem );
      }

      if( !_nodeCache.TryGetValue( full, out List<string>? nodes ) )
      {
         nodes = TextNodes( File.ReadAllText( full ) );
         _nodeCache[full] = nodes;
      }

      return nodes.Any( n => n.Contains( quote, StringComparison.Ordinal ) )
         ? SourceResolution.Ok( reference[( hash + 1 )..], $"{relative}, inside one text node" )
         : SourceResolution.Missing( $"quote not inside one text node of {relative}" );
   }

   /// <summary>
   /// The visible text nodes of an HTML page, in order, as a quote of a saved page is matched against them.
   /// </summary>
   /// <param name="html">The page.</param>
   /// <returns>The non-empty text nodes.</returns>
   public static IReadOnlyList<string> PageNodes( string html )
   {
      return TextNodes( html );
   }

   /// <summary>
   /// The visible text nodes of an HTML page: scripts, styles and comments removed, tags
   /// removed, entities decoded, white space collapsed.
   /// </summary>
   /// <param name="html">The page.</param>
   /// <returns>The non-empty text nodes.</returns>
   private static List<string> TextNodes( string html )
   {
      string visible = BLOCK_PATTERN.Replace( html, " " );
      return TAG_PATTERN.Split( visible ).Select( n => Normalize( WebUtility.HtmlDecode( n ) ) ).Where( n => n.Length > 0 ).ToList();
   }

   /// <summary>
   /// Collapses every run of white space to one space and trims.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The normalised text.</returns>
   private static string Normalize( string text )
   {
      return SPACE_PATTERN.Replace( text, " " ).Trim();
   }

   #endregion Private Methods
}

/// <summary>
/// Finds a token on the code lines of a source file, skipping comments.
/// Why: a token that sits only in a comment is documentation of intent, not evidence that the
/// code or the compose file does it (the MariaDB buffer pool size and the Typesense image tag
/// both appear in comments that do not set anything).
/// </summary>
internal static class CodeLines
{
   #region Public Methods

   /// <summary>
   /// The first code line that holds the token.
   /// </summary>
   /// <param name="path">The file's path, used for its extension (which picks the comment style).</param>
   /// <param name="lines">The file's lines.</param>
   /// <param name="token">The token, matched exactly.</param>
   /// <returns>The 1-based line number and the trimmed line, or null when no code line holds it.</returns>
   public static (int Line, string Text)? Find( string path, string[] lines, string token )
   {
      string extension = Path.GetExtension( path ).ToLowerInvariant();
      bool csharp = extension == ".cs";
      bool hashComments = extension is ".yaml" or ".yml" or ".sh" or ".py";
      bool inBlock = false;
      for( int i = 0; i < lines.Length; i++ )
      {
         string code = csharp ? StripCSharp( lines[i], ref inBlock ) : hashComments ? StripHash( lines[i] ) : lines[i];
         if( code.Contains( token, StringComparison.Ordinal ) )
         {
            return (i + 1, lines[i].Trim());
         }
      }

      return null;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Removes // and /* */ comments from a C# line, keeping string and character literals intact.
   /// </summary>
   /// <param name="line">The line.</param>
   /// <param name="inBlock">True while inside a block comment that started on an earlier line.</param>
   /// <returns>The code part of the line.</returns>
   private static string StripCSharp( string line, ref bool inBlock )
   {
      var code = new StringBuilder();
      char quote = '\0';
      for( int i = 0; i < line.Length; i++ )
      {
         char c = line[i];
         char next = i + 1 < line.Length ? line[i + 1] : '\0';
         if( inBlock )
         {
            if( c == '*' && next == '/' )
            {
               inBlock = false;
               i++;
            }

            continue;
         }

         if( quote != '\0' )
         {
            code.Append( c );
            if( c == '\\' && next != '\0' )
            {
               code.Append( next );
               i++;
            }
            else if( c == quote )
            {
               quote = '\0';
            }

            continue;
         }

         if( c == '/' && next == '/' )
         {
            break;
         }

         if( c == '/' && next == '*' )
         {
            inBlock = true;
            i++;
            continue;
         }

         if( c == '"' || c == '\'' )
         {
            quote = c;
         }

         code.Append( c );
      }

      return code.ToString();
   }

   /// <summary>
   /// Removes a # comment from a YAML or shell line: a # at the start of the line or after
   /// white space, outside quotes.
   /// </summary>
   /// <param name="line">The line.</param>
   /// <returns>The code part of the line.</returns>
   private static string StripHash( string line )
   {
      char quote = '\0';
      for( int i = 0; i < line.Length; i++ )
      {
         char c = line[i];
         if( quote != '\0' )
         {
            if( c == '\\' && i + 1 < line.Length )
            {
               i++;
            }
            else if( c == quote )
            {
               quote = '\0';
            }

            continue;
         }

         if( c == '"' || c == '\'' )
         {
            quote = c;
         }
         else if( c == '#' && ( i == 0 || char.IsWhiteSpace( line[i - 1] ) ) )
         {
            return line[..i];
         }
      }

      return line;
   }

   #endregion Private Methods
}
