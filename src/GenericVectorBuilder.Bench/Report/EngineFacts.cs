using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// One fact about one engine, with the place it comes from and how sure the report can be.
/// </summary>
/// <param name="Target">The benchmark target the fact is about (a name in the runs' targets list).</param>
/// <param name="Kind">index, search, storage, protocol, cap or set-by-setup.</param>
/// <param name="Text">The fact in under 12 words, built only from words of its source and a few joining words.</param>
/// <param name="Source">"results:targets[name].field#token", "repo/path#token", "doc:repo/path#quote" or "absent:results:targets[name]#alt1|alt2".</param>
/// <param name="Confidence">recorded (the run's static text), measured (state read from the running engine), documented (a saved page), set-by-code (a repository file) or checked (an absence check: none of the words occur in the recorded text).</param>
/// <param name="Mode">For search rows: exact or approximate, derived from the source and checked against the run's own index state; null otherwise.</param>
public sealed record EngineFactRow( string Target, string Kind, string Text, string Source, string Confidence, string? Mode = null );

/// <summary>
/// A fact row whose source was found, with where it was found.
/// </summary>
/// <param name="Row">The row.</param>
/// <param name="Where">Where the source resolved: "path:line", "in all 3 run(s) selected, at ..." or the saved page.</param>
public sealed record ResolvedFact( EngineFactRow Row, string Where );

/// <summary>
/// The outcome of checking the fact sheet: every problem found and every row that resolved.
/// </summary>
public sealed class FactValidation
{
   #region Constructor

   /// <summary>
   /// Creates the outcome.
   /// </summary>
   /// <param name="problems">What is wrong, one line each; empty when the sheet is sound.</param>
   /// <param name="resolved">The rows whose source resolved, in sheet order.</param>
   public FactValidation( IReadOnlyList<string> problems, IReadOnlyList<ResolvedFact> resolved )
   {
      Problems = problems;
      Resolved = resolved;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>What is wrong with the sheet, one line per problem.</summary>
   public IReadOnlyList<string> Problems { get; }

   /// <summary>The rows whose source resolved, with where.</summary>
   public IReadOnlyList<ResolvedFact> Resolved { get; }

   /// <summary>True when there is no problem.</summary>
   public bool Ok => Problems.Count == 0;

   /// <summary>
   /// Every resolved row on one line: target, kind, mode, confidence, text and where its source resolved.
   /// Why: the owner and the lenses read the facts together with their sources, not the sheet alone.
   /// </summary>
   /// <returns>The lines, tab separated.</returns>
   public string Listing()
   {
      return string.Join( Environment.NewLine, Resolved.Select( r => string.Join( '\t', r.Row.Target, r.Row.Kind, r.Row.Mode ?? "-", r.Row.Confidence, r.Row.Text, r.Row.Source, r.Where ) ) );
   }

   #endregion Public Methods
}

/// <summary>
/// The fact sheet is wrong or missing; the message lists every problem.
/// </summary>
public sealed class EngineFactsException : Exception
{
   /// <summary>
   /// Creates the exception.
   /// </summary>
   /// <param name="message">The problems.</param>
   public EngineFactsException( string message ) : base( message )
   {
   }
}

/// <summary>
/// Loads engine-facts.json and checks every row against what the runs recorded, the repository
/// and the saved documentation pages.
/// Why: the page that says why some engines beat others must not state anything nobody can
/// trace. A row is accepted only when its source is found again in every run given (so a
/// changed recorded text fails loud), its text uses only words of its own source, its confidence
/// label matches the kind of its source, and, for the exact-or-approximate row of the
/// Elasticsearch kind of hole, its mode agrees with the index state each run measured.
/// The class is named EngineFactSheet because Report/RunConditions.cs already declares a record EngineFacts.
/// </summary>
public static class EngineFactSheet
{
   #region Data Members

   /// <summary>Largest engine-facts.json that is read.</summary>
   public const long MAX_FILE_BYTES = 4L * 1024 * 1024;

   /// <summary>Most words in a fact's text (the design says under 12).</summary>
   public const int MAX_TEXT_WORDS = 11;

   /// <summary>The text of a storage row whose source is an absence check.</summary>
   public const string NOT_RECORDED = "not recorded";

   /// <summary>The kinds of fact row.</summary>
   public static readonly IReadOnlyList<string> KINDS = new[] { "index", "search", "storage", "protocol", "cap", "set-by-setup" };

   /// <summary>The kinds every target must have at least one row of.</summary>
   public static readonly IReadOnlyList<string> REQUIRED_KINDS = new[] { "index", "search", "storage", "protocol" };

   /// <summary>The label of an absence check: the row says "not recorded", and "checked" says the tool looked for the words and found none. Why not "recorded": a page that read "not recorded" beside "recorded" would contradict itself.</summary>
   public const string CHECKED = "checked";

   /// <summary>The confidence labels.</summary>
   public static readonly IReadOnlyList<string> CONFIDENCES = new[] { "recorded", "measured", "documented", "set-by-code", CHECKED };

   /// <summary>The search modes.</summary>
   public static readonly IReadOnlyList<string> MODES = new[] { "exact", "approximate" };

   /// <summary>Words a fact's text may not use even when its source does: a fact states what was found, never a default, an exclusive claim or a cause.</summary>
   public static readonly IReadOnlyList<string> FORBIDDEN_IN_TEXT = new[] { "only", "default", "defaults", "defaulted", "cause", "causes", "caused" };

   /// <summary>Joining words a fact's text may use without finding them in its source. Negations and verbs are deliberately not here.</summary>
   private static readonly HashSet<string> GLUE = new( StringComparer.Ordinal )
   {
      "a", "an", "the", "of", "is", "are", "in", "on", "at", "by", "for", "with", "and", "or", "to", "its", "it", "through", "via", "as", "from", "per", "over",
   };

   /// <summary>Phrases in a source text that say the search scans every vector.</summary>
   private static readonly string[] EXACT_MARKERS =
   {
      "exact scan", "no index used", "no index,", "no hnsw graph", "no graph was built", "no ann index", "brute-force", "scan all", "full scan", "sequential scan",
   };

   /// <summary>Phrases in a source text that say the search goes through an index.</summary>
   private static readonly string[] APPROXIMATE_MARKERS =
   {
      "approximate", "hnsw", "diskann", "index scan", "skip index", "key vec_idx", "vector index", "walked the graph",
   };

   private static readonly string[] STATIC_FIELDS = { "index", "engine", "durability", "searchSettings", "hosting", "notes" };
   private static readonly string[] MEASURED_FIELDS = { "load", "indexState", "search", "ram", "disk" };
   private static readonly TimeSpan MATCH_TIMEOUT = TimeSpan.FromSeconds( 5 );
   private static readonly Regex WORD_PATTERN = new( "[a-z0-9]+", RegexOptions.Compiled, MATCH_TIMEOUT );
   private static readonly Regex TARGET_FIELD_PATTERN = new( @"^targets\[([^\]]+)\](?:\.([A-Za-z]+))?", RegexOptions.Compiled, MATCH_TIMEOUT );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads engine-facts.json.
   /// </summary>
   /// <param name="path">The file; it is tracked in the repository, so a missing file is an error and never a skip.</param>
   /// <returns>The rows in file order.</returns>
   /// <exception cref="FileNotFoundException">The file is missing.</exception>
   /// <exception cref="EngineFactsException">The file is not valid JSON, is too large, or a row has a missing, misspelt or wrongly typed field.</exception>
   public static IReadOnlyList<EngineFactRow> Load( string path )
   {
      var info = new FileInfo( path );
      if( !info.Exists )
      {
         throw new FileNotFoundException( $"The engine fact sheet is missing: {path}", path );
      }

      if( info.Length > MAX_FILE_BYTES )
      {
         throw new EngineFactsException( $"{path} is {info.Length} bytes, over the {MAX_FILE_BYTES} byte limit" );
      }

      JsonDocument document;
      try
      {
         document = JsonDocument.Parse( File.ReadAllText( path ) );
      }
      catch( JsonException ex )
      {
         throw new EngineFactsException( $"{path} is not valid JSON: {ex.Message}" );
      }

      using( document )
      {
         if( document.RootElement.ValueKind != JsonValueKind.Array )
         {
            throw new EngineFactsException( $"{path} must hold a JSON array of fact rows" );
         }

         var rows = new List<EngineFactRow>();
         var problems = new List<string>();
         int index = 0;
         foreach( JsonElement element in document.RootElement.EnumerateArray() )
         {
            EngineFactRow? row = ReadRow( element, index++, problems );
            if( row is not null )
            {
               rows.Add( row );
            }
         }

         if( problems.Count > 0 || rows.Count == 0 )
         {
            throw new EngineFactsException( $"{path}: " + ( rows.Count == 0 && problems.Count == 0 ? "no fact rows" : string.Join( "; ", problems ) ) );
         }

         return rows;
      }
   }

   /// <summary>
   /// Checks the sheet: shape, coverage, every source, every text against its source, every
   /// confidence against its source kind, every search mode against the recorded index state, and
   /// every compose file against the run's own list of engine files.
   /// </summary>
   /// <param name="facts">The rows.</param>
   /// <param name="runs">The raw runs every token must be found in (the v7 runs, or all six); at least one.</param>
   /// <param name="repoRoot">Repository root, where the source files, compose files and saved pages are.</param>
   /// <returns>Every problem found and every row that resolved.</returns>
   public static FactValidation Validate( IReadOnlyList<EngineFactRow> facts, IReadOnlyList<FactRun> runs, string repoRoot )
   {
      ArgumentNullException.ThrowIfNull( facts );
      ArgumentNullException.ThrowIfNull( runs );
      var problems = new List<string>();
      var resolved = new List<ResolvedFact>();
      if( runs.Count == 0 )
      {
         problems.Add( "no runs were given, so no source can be checked" );
         return new FactValidation( problems, resolved );
      }

      var resolver = new SourceResolver( repoRoot, runs );
      var seen = new HashSet<string>( StringComparer.Ordinal );
      foreach( EngineFactRow row in facts )
      {
         string label = $"{row.Target}/{row.Kind}";
         if( !seen.Add( $"{row.Target}|{row.Kind}|{row.Source}" ) )
         {
            problems.Add( $"{label}: the same source is used twice for this target and kind: {row.Source}" );
         }

         if( CheckShape( row, label, problems ) )
         {
            CheckRow( row, label, runs, resolver, problems, resolved );
         }
      }

      CheckCoverage( facts, runs, problems );
      return new FactValidation( problems, resolved );
   }

   /// <summary>
   /// Checks the sheet and throws when anything is wrong.
   /// </summary>
   /// <param name="facts">The rows.</param>
   /// <param name="runs">The raw runs.</param>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>The outcome, when sound.</returns>
   /// <exception cref="EngineFactsException">Any problem; the message lists them all.</exception>
   public static FactValidation ValidateOrThrow( IReadOnlyList<EngineFactRow> facts, IReadOnlyList<FactRun> runs, string repoRoot )
   {
      FactValidation outcome = Validate( facts, runs, repoRoot );
      if( !outcome.Ok )
      {
         throw new EngineFactsException( $"{outcome.Problems.Count} problem(s) in the engine fact sheet:{Environment.NewLine}" + string.Join( Environment.NewLine, outcome.Problems ) );
      }

      return outcome;
   }

   /// <summary>
   /// The SHA-256 of a file, for the consolidated result to record which fact sheet and exclusions it used.
   /// </summary>
   /// <param name="path">The file.</param>
   /// <returns>Lower-case hex.</returns>
   /// <exception cref="FileNotFoundException">The file is missing.</exception>
   public static string FileSha256( string path )
   {
      if( !File.Exists( path ) )
      {
         throw new FileNotFoundException( $"Cannot hash a missing file: {path}", path );
      }

      using FileStream stream = File.OpenRead( path );
      return Convert.ToHexString( SHA256.HashData( stream ) ).ToLowerInvariant();
   }

   /// <summary>
   /// What an index-state snapshot says about how the search runs, read from the run's own record.
   /// </summary>
   /// <param name="snapshot">An indexState.afterLoad or afterSearch object.</param>
   /// <returns>"exact" (not ready, nothing indexed, or its detail says every vector is scanned), "graph" (ready with vectors indexed) or "unknown".</returns>
   public static string StateSays( JsonElement snapshot )
   {
      if( snapshot.ValueKind != JsonValueKind.Object )
      {
         return "unknown";
      }

      bool? ready = snapshot.TryGetProperty( "ready", out JsonElement r ) && r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : null;
      long? indexed = CountOf( snapshot, "indexedVectors" );
      long? total = CountOf( snapshot, "totalVectors" );
      string detail = snapshot.TryGetProperty( "detail", out JsonElement d ) && d.ValueKind == JsonValueKind.String ? d.GetString()! : string.Empty;
      if( ready == false || ( indexed == 0 && total > 0 ) || ContainsAny( detail, EXACT_MARKERS ) )
      {
         return "exact";
      }

      return ready == true && indexed > 0 ? "graph" : "unknown";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads one row of the file, naming every unknown, missing or wrongly typed field.
   /// </summary>
   /// <param name="element">The row's JSON.</param>
   /// <param name="index">Its position, for messages.</param>
   /// <param name="problems">Where problems are added.</param>
   /// <returns>The row, or null when it is unusable.</returns>
   private static EngineFactRow? ReadRow( JsonElement element, int index, List<string> problems )
   {
      string[] allowed = { "target", "kind", "text", "source", "confidence", "mode" };
      if( element.ValueKind != JsonValueKind.Object )
      {
         problems.Add( $"row {index} is not an object" );
         return null;
      }

      foreach( JsonProperty property in element.EnumerateObject() )
      {
         if( !allowed.Contains( property.Name ) )
         {
            problems.Add( $"row {index} has an unknown field '{property.Name}'" );
         }
      }

      string?[] values = allowed.Select( name => element.TryGetProperty( name, out JsonElement v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null ).ToArray();
      for( int field = 0; field < 5; field++ )
      {
         if( string.IsNullOrWhiteSpace( values[field] ) )
         {
            problems.Add( $"row {index} has no text for '{allowed[field]}'" );
            return null;
         }
      }

      return new EngineFactRow( values[0]!, values[1]!, values[2]!, values[3]!, values[4]!, values[5] );
   }

   /// <summary>
   /// Checks what needs no source: allowed values, text length and forbidden words.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="label">Target and kind, for messages.</param>
   /// <param name="problems">Where problems are added.</param>
   /// <returns>True when the row is sound enough to resolve its source.</returns>
   private static bool CheckShape( EngineFactRow row, string label, List<string> problems )
   {
      int before = problems.Count;
      int hash = row.Source.IndexOf( '#' );
      if( hash <= 0 || hash == row.Source.Length - 1 )
      {
         problems.Add( $"{label}: the source must end in #token or #quote: {row.Source}" );
      }

      if( !KINDS.Contains( row.Kind ) )
      {
         problems.Add( $"{label}: kind must be one of {string.Join( ", ", KINDS )}" );
      }

      if( !CONFIDENCES.Contains( row.Confidence ) )
      {
         problems.Add( $"{label}: confidence must be one of {string.Join( ", ", CONFIDENCES )}" );
      }

      if( row.Kind == "search" ? row.Mode is null || !MODES.Contains( row.Mode ) : row.Mode is not null )
      {
         problems.Add( $"{label}: a search row needs a mode of {string.Join( " or ", MODES )}, and no other row may have one" );
      }

      int words = SentenceAudit.WordCount( row.Text );
      if( words > MAX_TEXT_WORDS )
      {
         problems.Add( $"{label}: the text has {words} words, the limit is {MAX_TEXT_WORDS}: {row.Text}" );
      }

      IReadOnlyList<string> banned = BannedWords.Find( row.Text ).Concat( BannedWords.FindIn( row.Text, FORBIDDEN_IN_TEXT ) ).ToList();
      if( banned.Count > 0 )
      {
         problems.Add( $"{label}: the text uses a banned word ({string.Join( ", ", banned )}): {row.Text}" );
      }

      if( row.Text.Contains( '\u2014' ) || row.Text.Contains( '\u2013' ) )
      {
         problems.Add( $"{label}: the text holds an em or en dash: {row.Text}" );
      }

      if( !BracketsBalance( row.Text ) )
      {
         problems.Add( $"{label}: the text has an unbalanced bracket or quote, so it was cut short of what its source says: {row.Text}" );
      }

      return problems.Count == before;
   }

   /// <summary>
   /// Resolves a row's source and runs every check that needs it.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="label">Target and kind, for messages.</param>
   /// <param name="runs">The raw runs.</param>
   /// <param name="resolver">Reads the sources.</param>
   /// <param name="problems">Where problems are added.</param>
   /// <param name="resolved">Where resolved rows are added.</param>
   private static void CheckRow( EngineFactRow row, string label, IReadOnlyList<FactRun> runs, SourceResolver resolver, List<string> problems, List<ResolvedFact> resolved )
   {
      string kind = SourceResolver.KindOf( row.Source );
      if( !CheckSourceKind( row, kind, label, problems ) )
      {
         return;
      }

      SourceResolution resolution;
      try
      {
         resolution = resolver.Resolve( SourceResolver.FromFactSource( row.Source ) );
      }
      catch( FormatException ex )
      {
         problems.Add( $"{label}: {ex.Message}" );
         return;
      }

      if( !resolution.Found )
      {
         problems.Add( $"{label}: the source does not resolve ({resolution.Detail}): {row.Source}" );
         return;
      }

      int before = problems.Count;
      CheckConfidence( row, kind, label, problems );
      CheckTargetOwnsSource( row, kind, label, runs, problems );
      CheckText( row, resolution, kind, label, problems );
      CheckValueComplete( row, resolution, kind, label, problems );
      if( row.Kind == "search" )
      {
         CheckMode( row, resolution, label, runs, problems );
      }

      if( problems.Count == before )
      {
         resolved.Add( new ResolvedFact( row, resolution.Detail ) );
      }
   }

   /// <summary>
   /// Checks that the kind of source suits the kind of fact: an index, search or cap fact comes from a
   /// recorded field, a protocol or set-by-setup fact from a repository file, a storage fact from a
   /// recorded field, a saved page or an absence check.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="sourceKind">The source's kind.</param>
   /// <param name="label">Target and kind, for messages.</param>
   /// <param name="problems">Where problems are added.</param>
   /// <returns>True when it suits.</returns>
   private static bool CheckSourceKind( EngineFactRow row, string sourceKind, string label, List<string> problems )
   {
      bool ok = row.Kind switch
      {
         "index" or "search" or "cap" => sourceKind == SourceKinds.RESULTS,
         "storage" => sourceKind is SourceKinds.RESULTS or SourceKinds.DOC or SourceKinds.ABSENT,
         "protocol" or "set-by-setup" => sourceKind == SourceKinds.FILE,
         _ => false,
      };
      if( !ok )
      {
         problems.Add( $"{label}: a {row.Kind} fact cannot rest on a {sourceKind} source: {row.Source}" );
      }

      if( ok && sourceKind == SourceKinds.FILE && !FileAllowed( row, out string why ) )
      {
         problems.Add( $"{label}: {why}: {row.Source}" );
         ok = false;
      }

      return ok;
   }

   /// <summary>
   /// Checks where a file source may sit: protocol rows in a sink file of their own target, set-by-setup
   /// rows in a compose file or a sink file of their own target.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="why">What is wrong, or empty.</param>
   /// <returns>True when the file suits.</returns>
   private static bool FileAllowed( EngineFactRow row, out string why )
   {
      why = string.Empty;
      string path = row.Source[..row.Source.IndexOf( '#' )];
      if( path.StartsWith( "deploy/engines/", StringComparison.Ordinal ) && row.Kind == "set-by-setup" )
      {
         return true;
      }

      if( !path.StartsWith( "src/", StringComparison.Ordinal ) )
      {
         why = row.Kind == "protocol" ? "a protocol fact rests on a sink file under src/" : "a set-by-setup fact rests on a compose file under deploy/engines/ or a sink file under src/";
         return false;
      }

      string stem = Regex.Replace( Path.GetFileNameWithoutExtension( path ).ToLowerInvariant(), "[^a-z0-9]", string.Empty );
      string expected = SinkStem( row.Target );
      if( !stem.StartsWith( expected, StringComparison.Ordinal ) )
      {
         why = $"the file is not a sink of target '{row.Target}' (its name should start with '{expected}')";
         return false;
      }

      return true;
   }

   /// <summary>
   /// The start of the file name of a target's sink: the target's name, except for the four built-in targets that wrap the builder's own sinks.
   /// </summary>
   /// <param name="target">The target name.</param>
   /// <returns>Lower-case letters and digits.</returns>
   private static string SinkStem( string target )
   {
      return target switch
      {
         "sql" => "sqlvectorsink",
         "sql-diskann" => "sqldiskannsink",
         "qdrant" => "qdrantsink",
         "qdrant-hnsw" => "qdranthnswsink",
         _ => Regex.Replace( target.ToLowerInvariant(), "[^a-z0-9]", string.Empty ),
      };
   }

   /// <summary>
   /// Checks that the confidence label says what kind of source the row has.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="sourceKind">The source's kind.</param>
   /// <param name="label">Target and kind, for messages.</param>
   /// <param name="problems">Where problems are added.</param>
   private static void CheckConfidence( EngineFactRow row, string sourceKind, string label, List<string> problems )
   {
      string expected = sourceKind switch
      {
         SourceKinds.DOC => "documented",
         SourceKinds.FILE => "set-by-code",
         SourceKinds.ABSENT => CHECKED,
         _ => ResultsConfidence( row.Source ),
      };
      if( expected != row.Confidence )
      {
         problems.Add( $"{label}: the confidence is '{row.Confidence}' but a source of this kind is '{expected}': {row.Source}" );
      }
   }

   /// <summary>
   /// The confidence of a results source by the field it reads: the run's static texts are recorded,
   /// the state read from the running engine is measured.
   /// </summary>
   /// <param name="source">The results source string.</param>
   /// <returns>"recorded", "measured", or "unknown field" when the field is in neither list.</returns>
   private static string ResultsConfidence( string source )
   {
      Match match = TARGET_FIELD_PATTERN.Match( ResultsPath( source ) );
      string field = match.Success ? match.Groups[2].Value : string.Empty;
      return STATIC_FIELDS.Contains( field ) ? "recorded" : MEASURED_FIELDS.Contains( field ) ? "measured" : "unknown field";
   }

   /// <summary>
   /// Checks that a results source reads the row's own target, and that a compose file is one the
   /// runs recorded for that target.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="sourceKind">The source's kind.</param>
   /// <param name="label">Target and kind, for messages.</param>
   /// <param name="runs">The raw runs.</param>
   /// <param name="problems">Where problems are added.</param>
   private static void CheckTargetOwnsSource( EngineFactRow row, string sourceKind, string label, IReadOnlyList<FactRun> runs, List<string> problems )
   {
      if( sourceKind is SourceKinds.RESULTS or SourceKinds.ABSENT )
      {
         Match match = TARGET_FIELD_PATTERN.Match( ResultsPath( row.Source ) );
         if( !match.Success || match.Groups[1].Value != row.Target )
         {
            problems.Add( $"{label}: the source must read targets[{row.Target}]: {row.Source}" );
         }

         return;
      }

      string path = row.Source[..row.Source.IndexOf( '#' )];
      const string COMPOSE_ROOT = "deploy/engines/";
      if( sourceKind != SourceKinds.FILE || !path.StartsWith( COMPOSE_ROOT, StringComparison.Ordinal ) )
      {
         return;
      }

      string engineFile = path[COMPOSE_ROOT.Length..];
      foreach( FactRun run in runs )
      {
         string notes = EngineFilesNote( run, row.Target );
         if( notes.Length == 0 )
         {
            problems.Add( $"{label}: run {run.Name} records no 'Engine files' note for {row.Target}, so {engineFile} cannot be tied to it" );
         }
         else if( !Regex.IsMatch( notes, @"(?<![A-Za-z0-9_./-])" + Regex.Escape( engineFile ) + @"\s", RegexOptions.CultureInvariant, MATCH_TIMEOUT ) )
         {
            problems.Add( $"{label}: run {run.Name} did not create {row.Target} from {engineFile}" );
         }
      }
   }

   /// <summary>
   /// The "Engine files" note a run recorded for a target, which lists the compose files it created the engine from.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="target">The target.</param>
   /// <returns>The note, or empty when the run recorded none.</returns>
   private static string EngineFilesNote( FactRun run, string target )
   {
      if( !SourceResolver.TryPath( run.Root, $"targets[{target}].notes", out JsonElement notes, out _ ) || notes.ValueKind != JsonValueKind.Array )
      {
         return string.Empty;
      }

      return notes.EnumerateArray().Where( n => n.ValueKind == JsonValueKind.String ).Select( n => n.GetString()! ).FirstOrDefault( n => n.StartsWith( "Engine files", StringComparison.Ordinal ) ) ?? string.Empty;
   }

   /// <summary>
   /// Checks that a row's text is built from its own source: every word is a word of the evidence
   /// (the token or the quote) or a joining word or the target's name, and a
   /// storage row from an absence check says exactly "not recorded".
   /// Why: the text must not say more than the token does, for example "approximate" or "m=16"
   /// where the token has neither.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="resolution">What the source resolved to; its value is the token or quote the text must be built from.</param>
   /// <param name="sourceKind">The source's kind.</param>
   /// <param name="label">Target and kind, for messages.</param>
   /// <param name="problems">Where problems are added.</param>
   private static void CheckText( EngineFactRow row, SourceResolution resolution, string sourceKind, string label, List<string> problems )
   {
      if( sourceKind == SourceKinds.ABSENT )
      {
         if( row.Text != NOT_RECORDED || row.Kind != "storage" )
         {
            problems.Add( $"{label}: an absence check supports only a storage row whose text is '{NOT_RECORDED}'" );
         }

         return;
      }

      HashSet<string> allowed = Words( resolution.Value ?? string.Empty ).Concat( Words( row.Target ) ).Concat( GLUE ).ToHashSet( StringComparer.Ordinal );
      string[] extra = Words( row.Text ).Where( w => !allowed.Contains( w ) ).Distinct().ToArray();
      if( extra.Length > 0 )
      {
         problems.Add( $"{label}: the text uses words its source does not have ({string.Join( ", ", extra )}); the source token reads: {Shorten( resolution.Value )}" );
      }
   }

   /// <summary>
   /// Checks that a repository-file row's token does not stop where the code computes a value: a token such as
   /// "SET memory_limit = '" followed on its line by an interpolated limit proves the setting is set, and a row built from it
   /// prints the setting name with no value. The value must come from a line that holds it.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="resolution">What the source resolved to; its evidence is the matched code line.</param>
   /// <param name="sourceKind">The source's kind.</param>
   /// <param name="label">Target and kind, for messages.</param>
   /// <param name="problems">Where problems are added.</param>
   private static void CheckValueComplete( EngineFactRow row, SourceResolution resolution, string sourceKind, string label, List<string> problems )
   {
      if( sourceKind != SourceKinds.FILE || resolution.Evidence is null || resolution.Value is null )
      {
         return;
      }

      int at = resolution.Evidence.IndexOf( resolution.Value, StringComparison.Ordinal );
      if( at >= 0 && at + resolution.Value.Length < resolution.Evidence.Length && resolution.Evidence[at + resolution.Value.Length] == '{' )
      {
         problems.Add( $"{label}: the source token stops where a value begins ('{{' follows it on the line), so the text cannot hold the value; take the token from a line that holds it: {row.Source}" );
      }
   }

   /// <summary>
   /// Whether every round, square and curly bracket in a text is closed in order and the double quotes come in pairs.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>True when balanced.</returns>
   private static bool BracketsBalance( string text )
   {
      var open = new Stack<char>();
      foreach( char c in text )
      {
         switch( c )
         {
            case '(' or '[' or '{':
               open.Push( c );
               break;
            case ')' or ']' or '}':
               if( open.Count == 0 || open.Pop() != ( c == ')' ? '(' : c == ']' ? '[' : '{' ) )
               {
                  return false;
               }

               break;
         }
      }

      return open.Count == 0 && text.Count( c => c == '"' ) % 2 == 0;
   }

   /// <summary>
   /// Checks a search row's mode: it must be what the row's own source says, and it must not
   /// contradict the index state any run measured.
   /// Why: the Elasticsearch index text says HNSW with k and num_candidates while its measured state
   /// says no graph exists and every search scans all 524 vectors. A row that took its mode from the
   /// static text would say approximate; the measured state decides.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="resolution">What the source resolved to.</param>
   /// <param name="label">Target and kind, for messages.</param>
   /// <param name="runs">The raw runs.</param>
   /// <param name="problems">Where problems are added.</param>
   private static void CheckMode( EngineFactRow row, SourceResolution resolution, string label, IReadOnlyList<FactRun> runs, List<string> problems )
   {
      string? derived = DeriveMode( resolution.Value ?? string.Empty );
      if( derived is null )
      {
         problems.Add( $"{label}: the mode cannot be derived from the source text, which names neither an index nor a full scan: {Shorten( resolution.Value )}" );
      }
      else if( derived != row.Mode )
      {
         problems.Add( $"{label}: the row says {row.Mode} but its source text says {derived}: {Shorten( resolution.Value )}" );
      }

      foreach( FactRun run in runs )
      {
         foreach( string snapshotName in new[] { "afterLoad", "afterSearch" } )
         {
            if( !SourceResolver.TryPath( run.Root, $"targets[{row.Target}].indexState.{snapshotName}", out JsonElement snapshot, out _ ) )
            {
               continue;
            }

            string says = StateSays( snapshot );
            if( ( says == "exact" && row.Mode == "approximate" ) || ( says == "graph" && row.Mode == "exact" ) )
            {
               problems.Add( $"{label}: the row says {row.Mode} but {run.Name} indexState.{snapshotName} says {says}: {Shorten( SourceResolver.Flatten( snapshot ) )}" );
            }
         }
      }
   }

   /// <summary>
   /// The JSON path of a results or absence source: the text after "results:" (or "results@folder:") and before "#".
   /// </summary>
   /// <param name="source">The source string.</param>
   /// <returns>The path, or empty when the source has none.</returns>
   private static string ResultsPath( string source )
   {
      int start = source.IndexOf( "results", StringComparison.Ordinal );
      int colon = start < 0 ? -1 : source.IndexOf( ':', start );
      int hash = source.IndexOf( '#' );
      return colon < 0 ? string.Empty : source[( colon + 1 )..( hash < 0 ? source.Length : hash )];
   }

   /// <summary>
   /// A whole-number property of a snapshot, or null when it is absent or not a whole number.
   /// </summary>
   /// <param name="snapshot">The snapshot object.</param>
   /// <param name="name">The property.</param>
   /// <returns>The count or null.</returns>
   private static long? CountOf( JsonElement snapshot, string name )
   {
      return snapshot.TryGetProperty( name, out JsonElement value ) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64( out long count ) ? count : null;
   }

   /// <summary>
   /// Reads exact or approximate from a source text: a phrase about scanning every vector wins over a phrase about an index.
   /// </summary>
   /// <param name="text">The evidence text.</param>
   /// <returns>"exact", "approximate" or null when the text says neither.</returns>
   private static string? DeriveMode( string text )
   {
      if( ContainsAny( text, EXACT_MARKERS ) )
      {
         return "exact";
      }

      return ContainsAny( text, APPROXIMATE_MARKERS ) ? "approximate" : null;
   }

   /// <summary>
   /// Checks that every target of every run has a row of each required kind, that no row names a target
   /// no run has, and that a target's search rows agree on exact or approximate.
   /// </summary>
   /// <param name="facts">The rows.</param>
   /// <param name="runs">The raw runs.</param>
   /// <param name="problems">Where problems are added.</param>
   private static void CheckCoverage( IReadOnlyList<EngineFactRow> facts, IReadOnlyList<FactRun> runs, List<string> problems )
   {
      var targets = new SortedSet<string>( StringComparer.Ordinal );
      foreach( FactRun run in runs )
      {
         if( !run.Root.TryGetProperty( "targets", out JsonElement list ) || list.ValueKind != JsonValueKind.Array )
         {
            problems.Add( $"run {run.Name} has no targets list" );
            continue;
         }

         foreach( JsonElement target in list.EnumerateArray() )
         {
            if( target.TryGetProperty( "name", out JsonElement name ) && name.ValueKind == JsonValueKind.String )
            {
               targets.Add( name.GetString()! );
            }
         }
      }

      foreach( string target in targets )
      {
         foreach( string kind in REQUIRED_KINDS.Where( k => !facts.Any( f => f.Target == target && f.Kind == k ) ) )
         {
            problems.Add( $"{target} has no {kind} fact" );
         }
      }

      foreach( string unknown in facts.Select( f => f.Target ).Distinct().Where( t => !targets.Contains( t ) ) )
      {
         problems.Add( $"facts name target '{unknown}', which no run has" );
      }

      foreach( IGrouping<string, EngineFactRow> group in facts.Where( f => f.Kind == "search" ).GroupBy( f => f.Target ) )
      {
         if( group.Select( f => f.Mode ).Distinct().Count() > 1 )
         {
            problems.Add( $"{group.Key} has search rows that disagree on exact or approximate" );
         }
      }
   }

   /// <summary>
   /// The lower-case words of a text, plural endings dropped, so "scans" matches "scan".
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The words.</returns>
   private static IEnumerable<string> Words( string text )
   {
      return WORD_PATTERN.Matches( text.ToLowerInvariant() ).Select( m => m.Value.Length > 3 && m.Value.EndsWith( 's' ) && !m.Value.EndsWith( "ss", StringComparison.Ordinal ) ? m.Value[..^1] : m.Value );
   }

   /// <summary>
   /// Whether a text contains any of the phrases, ignoring case.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <param name="phrases">The phrases.</param>
   /// <returns>True when one is found.</returns>
   private static bool ContainsAny( string text, string[] phrases )
   {
      return phrases.Any( p => text.Contains( p, StringComparison.OrdinalIgnoreCase ) );
   }

   /// <summary>
   /// A text cut to 160 characters for a message.
   /// </summary>
   /// <param name="text">The text, or null.</param>
   /// <returns>The shortened text.</returns>
   private static string Shorten( string? text )
   {
      string flat = ( text ?? string.Empty ).Replace( '\n', ' ' );
      return flat.Length <= 160 ? flat : flat[..160] + "...";
   }

   #endregion Private Methods
}
