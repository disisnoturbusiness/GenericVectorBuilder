using System.Text.Json;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// One engine's numbers as the summary shows them.
/// </summary>
/// <param name="Key">The target name the Bench tool used, e.g. "sql-diskann".</param>
/// <param name="Name">The friendly name a reader sees, e.g. "SQL Server 2025 + DiskANN".</param>
/// <param name="Qps8">Searches per second with 8 at once: the median across runs, or the one run's value.</param>
/// <param name="Qps8Min">Slowest run's searches per second with 8 at once, or null for a single run.</param>
/// <param name="Qps8Max">Fastest run's searches per second with 8 at once, or null for a single run.</param>
/// <param name="P50Ms">Median latency of one search at a time, in milliseconds, or null when missing.</param>
/// <param name="Recall">Share of the exact top 10 the engine returned (1 = same answers), or null when missing.</param>
/// <param name="InMemory">True when the engine holds everything in memory, which the chart marks.</param>
/// <param name="Flags">Warnings about this engine's numbers (spread, unsettled, ...); null or empty when there are none.</param>
/// <param name="Band">Tie band by searches per second with 8 at once (1 is the fastest band; engines whose min to max ranges overlap, or whose neighboring medians are less than 3% apart, share one); null when only one run was used, so no spread is known.</param>
/// <param name="Notes">What to know about this engine to read its speed (a CPU cap, an exact-by-design search); null or empty when there is none.</param>
/// <param name="ClientCpuMs">CPU time the test's .NET client itself used per search with one searcher, in milliseconds (the median across runs, or the one run's value); null when the results did not record it. Why shown: for the fastest engines it is about as large as the latency, so the speed order partly reflects each engine's client library.</param>
public sealed record BenchEngineRow( string Key, string Name, double Qps8, double? Qps8Min, double? Qps8Max, double? P50Ms, double? Recall, bool InMemory, IReadOnlyList<BenchFlag>? Flags = null, int? Band = null, IReadOnlyList<BenchEngineNote>? Notes = null, double? ClientCpuMs = null );

/// <summary>
/// One note about an engine, as the consolidate command wrote it.
/// </summary>
/// <param name="Kind">"cpu-cap", "exact-by-design" or "scans-all-vectors" (an unknown kind is shown as written).</param>
/// <param name="Text">The note, one or two sentences.</param>
/// <param name="Source">"results" when the result files say it, "known" when it is a documented limit the run did not record.</param>
public sealed record BenchEngineNote( string Kind, string Text, string Source );

/// <summary>
/// An engine the runs hold that the consolidated report leaves out of its tables, as the report
/// names it.
/// Why shown on the page: an engine missing from a published table with no word said reads as an
/// engine nobody measured.
/// </summary>
/// <param name="Key">The target name the Bench tool used, e.g. "mongodb".</param>
/// <param name="Name">The friendly name a reader sees, e.g. "MongoDB Atlas Local".</param>
/// <param name="Reason">Why it has no row, in the report's words (what the runs show).</param>
public sealed record BenchWithheld( string Key, string Name, string Reason );

/// <summary>
/// What the runs recorded about how one engine was set up, as the summary lists it (a text the runs
/// recorded is shown as written; null when no run recorded it).
/// Why shown on the page: an engine's non-default settings (its index parameters, server settings, the
/// files it was configured from, its durability) are part of what its numbers mean, and a setting nobody
/// can see cannot be questioned.
/// </summary>
/// <param name="Key">The target name the Bench tool used, e.g. "clickhouse".</param>
/// <param name="Name">The friendly name a reader sees.</param>
/// <param name="Engine">The engine description (name, version, how it was hosted), or null.</param>
/// <param name="Index">The index description with its build parameters, or null.</param>
/// <param name="SearchSettings">The settings that set how hard it searched, or null.</param>
/// <param name="EngineSettings">Non-default engine settings a run recorded as a structured field, or null.</param>
/// <param name="Durability">What a crash can lose with the engine's settings, or null.</param>
/// <param name="EngineFiles">The files the engine was configured from with a short SHA-256 each, or null.</param>
public sealed record BenchEngineConfig( string Key, string Name, string? Engine, string? Index, string? SearchSettings, string? EngineSettings, string? Durability, string? EngineFiles );

/// <summary>
/// The ranked numbers behind a summary block: the leader first.
/// Why one shape for both sources: the summary page (medians of several runs) and each run page
/// (one run) must read the same way, so the chart, headline and table are built from this alone.
/// </summary>
/// <param name="Ranked">Engines with a result, fastest first by searches per second with 8 at once (the median order; engines that share a band are not ranked against each other).</param>
/// <param name="NoResult">Friendly names of engines that produced no throughput number.</param>
/// <param name="Runs">How many runs each median covers (1 for a single run page).</param>
/// <param name="Conditions">The machine conditions the numbers were measured under; null or <see cref="BenchConditions.None"/> when the result files did not record them.</param>
/// <param name="Rows">Vectors in the collection that was searched; null when the result files did not record it.</param>
/// <param name="Withheld">Engines the runs hold that the consolidated report does not show, each with the reason the report gives; null when the file lists none (older files do not).</param>
/// <param name="Configs">How each engine was set up, as the runs recorded it; null for a file that does not carry it.</param>
/// <param name="Dropped">Runs the consolidated report left out of the medians, each as "name: reason"; null when none or when the file does not say.</param>
/// <param name="OldFormat">True for the first published shape (an object keyed by engine, no flags, settings or notes), which cannot say anything about itself.</param>
public sealed record BenchSummary( IReadOnlyList<BenchEngineRow> Ranked, IReadOnlyList<string> NoResult, int Runs, BenchConditions? Conditions = null, int? Rows = null, IReadOnlyList<BenchWithheld>? Withheld = null,
   IReadOnlyList<BenchEngineConfig>? Configs = null, IReadOnlyList<string>? Dropped = null, bool OldFormat = false )
{
   /// <summary>True when the numbers are medians across runs, so the chart draws min to max lines and the engines are put in tie bands.</summary>
   public bool HasRanges => Runs > 1;
}

/// <summary>
/// Reads the Bench tool's JSON (consolidated.json or one run's results.json) into a
/// <see cref="BenchSummary"/>. Malformed input throws, so the page can show the failure
/// instead of a quietly wrong chart.
/// </summary>
public static class BenchSummaryReader
{
   #region Data Members

   /// <summary>Most engines one file may hold; more means the file is not what we think it is.</summary>
   public const int MAX_ENGINES = 200;

   private const string EIGHT_AT_ONCE = "8";

   private static readonly string[] CLIENT_CPU_NAMES = { "clientCpuMsPerSearch", "clientCpuPerSearchMs" };

   private static readonly Dictionary<string, string> FRIENDLY = new( StringComparer.OrdinalIgnoreCase )
   {
      ["sql"] = "SQL Server 2025",
      ["sql-diskann"] = "SQL Server 2025 + DiskANN",
      ["sql-native"] = "SQL Server 2025 (native service)",
      ["qdrant"] = "Qdrant (exact)",
      ["qdrant-hnsw"] = "Qdrant (HNSW)",
      ["qdrant-native"] = "Qdrant (native service, exact)",
      ["pgvector"] = "pgvector",
      ["mariadb"] = "MariaDB",
      ["oracle"] = "Oracle 23ai Free",
      ["redis"] = "Redis",
      ["mongodb"] = "MongoDB Atlas Local",
      ["clickhouse"] = "ClickHouse",
      ["milvus"] = "Milvus",
      ["weaviate"] = "Weaviate",
      ["chroma"] = "Chroma",
      ["elasticsearch"] = "Elasticsearch",
      ["opensearch"] = "OpenSearch",
      ["typesense"] = "Typesense",
      ["vespa"] = "Vespa",
      ["duckdb"] = "DuckDB",
      ["sqlitevec"] = "sqlite-vec",
   };

   /// <summary>Endings a benchmark target name may carry to say it runs in the benchmark's own container ("mariadb-bench"); the engine's name is what precedes it.</summary>
   private static readonly string[] CONTAINER_SUFFIXES = { "-bench", "-container", "-compose" };

   private static readonly HashSet<string> IN_MEMORY = new( StringComparer.OrdinalIgnoreCase ) { "redis" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The name a reader sees for a Bench target, or the target name itself when unknown.
   /// The two native comparison targets say so ("sql-native" is "SQL Server 2025 (native service)"),
   /// and a target that runs in the benchmark's own container keeps its engine's name: "mariadb"
   /// and "mariadb-bench" are both "MariaDB", because the container is where it runs and not what
   /// the reader is comparing.
   /// </summary>
   /// <param name="key">Target name, e.g. "sqlitevec".</param>
   /// <returns>Friendly name, e.g. "sqlite-vec".</returns>
   public static string FriendlyName( string key )
   {
      if( FRIENDLY.TryGetValue( key, out string? name ) )
      {
         return name;
      }

      string? suffix = CONTAINER_SUFFIXES.FirstOrDefault( x => key.EndsWith( x, StringComparison.OrdinalIgnoreCase ) );
      return suffix != null && FRIENDLY.TryGetValue( key[..^suffix.Length], out string? engine ) ? engine : key;
   }

   /// <summary>
   /// Reads consolidated.json in either shape: the published one (an object keyed by target,
   /// each with qps8, p50 and recall as { median, min, max } and a run count; it carries no
   /// flags and no conditions), or the one the Bench tool's consolidate command writes
   /// (targetSummaries, settings, runs and flags).
   /// </summary>
   /// <param name="json">File text.</param>
   /// <returns>The ranked summary.</returns>
   public static BenchSummary FromConsolidated( string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement root = doc.RootElement;
      if( root.ValueKind != JsonValueKind.Object )
      {
         throw new JsonException( "consolidated.json is not an object keyed by engine." );
      }

      if( root.TryGetProperty( "targetSummaries", out JsonElement summaries ) && summaries.ValueKind == JsonValueKind.Array )
      {
         return FromConsolidatedReport( root, summaries );
      }

      var rows = new List<BenchEngineRow>();
      var missing = new List<string>();
      int runs = 0;
      foreach( JsonProperty engine in root.EnumerateObject() )
      {
         GuardCount( rows.Count + missing.Count );
         JsonElement e = engine.Value;
         runs = Math.Max( runs, e.ValueKind == JsonValueKind.Object && e.TryGetProperty( "runs", out JsonElement r ) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32( out int n ) ? n : 0 );
         double? qps = Stat( e, "qps8", "median" );
         if( qps == null )
         {
            missing.Add( FriendlyName( engine.Name ) );
            continue;
         }

         rows.Add( new BenchEngineRow( engine.Name, FriendlyName( engine.Name ), qps.Value, Stat( e, "qps8", "min" ), Stat( e, "qps8", "max" ),
            Stat( e, "p50", "median" ), Stat( e, "recall", "median" ), IN_MEMORY.Contains( engine.Name ) ) );
      }

      return new BenchSummary( WithBands( Rank( rows ), runs ), missing, Math.Max( runs, 1 ), OldFormat: true );
   }

   /// <summary>
   /// Reads one run's results.json: targets[] with name and search.qps["8"], search.p50Ms and
   /// search.recall.
   /// </summary>
   /// <param name="json">File text.</param>
   /// <returns>The ranked summary for that run.</returns>
   public static BenchSummary FromRunResults( string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      if( doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty( "targets", out JsonElement targets ) || targets.ValueKind != JsonValueKind.Array )
      {
         throw new JsonException( "results.json has no targets list." );
      }

      var rows = new List<BenchEngineRow>();
      var missing = new List<string>();
      foreach( JsonElement t in targets.EnumerateArray() )
      {
         GuardCount( rows.Count + missing.Count );
         string key = t.ValueKind == JsonValueKind.Object && t.TryGetProperty( "name", out JsonElement n ) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "?" : "?";
         JsonElement search = t.ValueKind == JsonValueKind.Object && t.TryGetProperty( "search", out JsonElement s ) ? s : default;
         double? qps = search.ValueKind == JsonValueKind.Object && search.TryGetProperty( "qps", out JsonElement q ) ? Number( q, EIGHT_AT_ONCE ) : null;
         if( qps == null )
         {
            missing.Add( FriendlyName( key ) );
            continue;
         }

         rows.Add( new BenchEngineRow( key, FriendlyName( key ), qps.Value, null, null, Number( search, "p50Ms" ), Number( search, "recall" ), IN_MEMORY.Contains( key ),
            t.ValueKind == JsonValueKind.Object ? BenchFlagInfo.FromTarget( t ) : null, ClientCpuMs: ClientCpuAtOne( search, false ) ) );
      }

      return new BenchSummary( Rank( rows ), missing, 1, BenchConditions.FromRun( doc.RootElement ), WholeNumber( doc.RootElement, "rows" ) );
   }

   /// <summary>
   /// The one line that says what the published numbers were measured on. An old published file
   /// records no settings and gets the fixed line written for it; a consolidate output builds the
   /// line from its own settings (pipeline, vectors, dimensions, questions, top), so the page never
   /// describes one folder's data with another folder's words.
   /// </summary>
   /// <param name="json">The file's text.</param>
   /// <returns>The line, or null when the file records nothing to build it from and is not the old shape.</returns>
   public static string? DataLine( string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement root = doc.RootElement;
      if( root.ValueKind != JsonValueKind.Object || !root.TryGetProperty( "targetSummaries", out JsonElement summaries ) || summaries.ValueKind != JsonValueKind.Array )
      {
         return BenchSummaryHtml.PUBLISHED_DATA;
      }

      if( !root.TryGetProperty( "settings", out JsonElement settings ) || settings.ValueKind != JsonValueKind.Object )
      {
         return null;
      }

      int? rows = WholeNumber( settings, "rows" );
      int? dimension = WholeNumber( settings, "dimension" );
      int? questions = WholeNumber( settings, "queryCount" );
      int? top = WholeNumber( settings, "top" );
      string? kind = Text( settings, "queryKind" );
      var parts = new List<string>();
      if( Text( settings, "pipeline" ) is string pipeline )
      {
         parts.Add( $"{pipeline} pipeline" );
      }

      if( rows != null )
      {
         parts.Add( dimension == null ? $"{rows:N0} vectors" : $"{rows:N0} vectors of {dimension} dimensions" );
      }

      if( questions != null )
      {
         parts.Add( kind == "golden" ? $"{questions} labelled questions" : $"{questions} {kind ?? "unlabelled"} queries" );
      }

      if( top != null )
      {
         parts.Add( $"top {top}" );
      }

      return parts.Count == 0 ? null : "Data: " + string.Join( ", ", parts ) + ".";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads the consolidated.json the consolidate command writes: one summary per target (qps by
   /// level, p50 and recall as median/min/max), the shared settings, the runs used and the flags.
   /// </summary>
   /// <param name="root">The file's root object.</param>
   /// <param name="summaries">Its targetSummaries array.</param>
   /// <returns>The ranked summary with flags and conditions.</returns>
   private static BenchSummary FromConsolidatedReport( JsonElement root, JsonElement summaries )
   {
      int runs = root.TryGetProperty( "runs", out JsonElement used ) && used.ValueKind == JsonValueKind.Array ? used.GetArrayLength() : 0;
      Dictionary<string, List<BenchFlag>> flags = ReportFlags( root, runs );
      var rows = new List<BenchEngineRow>();
      var missing = new List<string>();
      var configs = new List<BenchEngineConfig>();
      bool hasSettings = root.TryGetProperty( "settings", out JsonElement settings ) && settings.ValueKind == JsonValueKind.Object;
      foreach( JsonElement t in summaries.EnumerateArray() )
      {
         GuardCount( rows.Count + missing.Count );
         string key = t.ValueKind == JsonValueKind.Object && t.TryGetProperty( "name", out JsonElement n ) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "?" : "?";
         configs.Add( EngineConfig( t, key, hasSettings ? settings : default ) );
         JsonElement qps = t.ValueKind == JsonValueKind.Object && t.TryGetProperty( "qps", out JsonElement q ) ? q : default;
         double? median = qps.ValueKind == JsonValueKind.Object && qps.TryGetProperty( EIGHT_AT_ONCE, out JsonElement at8 ) ? Number( at8, "median" ) : null;
         if( median == null )
         {
            missing.Add( FriendlyName( key ) );
            continue;
         }

         JsonElement eight = qps.GetProperty( EIGHT_AT_ONCE );
         rows.Add( new BenchEngineRow( key, FriendlyName( key ), median.Value, Number( eight, "min" ), Number( eight, "max" ), Stat( t, "p50Ms", "median" ), Stat( t, "recall", "median" ),
            IN_MEMORY.Contains( key ), flags.GetValueOrDefault( key ), null, EngineNotes( t ), ClientCpuAtOne( t, true ) ) );
      }

      BenchConditions conditions = hasSettings ? BenchConditions.FromSettings( settings ) : BenchConditions.None;
      return new BenchSummary( WithBands( Rank( rows ), runs ), missing, Math.Max( runs, 1 ), conditions, hasSettings ? WholeNumber( settings, "rows" ) : null, WithheldTargets( root ),
         configs.Count == 0 ? null : configs, DroppedRuns( root ) );
   }

   /// <summary>
   /// How one engine was set up, from its targetSummaries entry and the shared settings: the engine
   /// and index descriptions, its search settings, any recorded engine settings and files, and its
   /// durability statement (the "durabilities" list, or the one each run carries).
   /// </summary>
   /// <param name="target">One targetSummaries entry.</param>
   /// <param name="key">The target name.</param>
   /// <param name="settings">The report's settings object, or default when it has none.</param>
   /// <returns>The configuration.</returns>
   private static BenchEngineConfig EngineConfig( JsonElement target, string key, JsonElement settings )
   {
      JsonElement searchSettings = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty( "searchSettings", out JsonElement all ) ? all : default;
      string? durability = TextList( target, "durabilities" ) ?? PerRunTexts( target, "durability" );
      return new BenchEngineConfig( key, FriendlyName( key ), TextList( target, "engines" ), TextList( target, "indexes" ), Text( searchSettings, key ), TextList( target, "engineSettings" ), durability, TextList( target, "engineFiles" ) );
   }

   /// <summary>
   /// The runs the report left out, each as "name: reason"; none when the file lists none.
   /// </summary>
   /// <param name="root">The file's root object.</param>
   /// <returns>The runs, or null.</returns>
   private static List<string>? DroppedRuns( JsonElement root )
   {
      if( !root.TryGetProperty( "dropped", out JsonElement dropped ) || dropped.ValueKind != JsonValueKind.Array )
      {
         return null;
      }

      List<string> list = dropped.EnumerateArray().Where( d => d.ValueKind == JsonValueKind.Object && Text( d, "name" ) != null )
         .Select( d => $"{Text( d, "name" )}: {Text( d, "reason" ) ?? "no reason given"}" ).Take( MAX_ENGINES ).ToList();
      return list.Count == 0 ? null : list;
   }

   /// <summary>
   /// The distinct strings of an array property joined with " / ", or null when it is absent or empty.
   /// </summary>
   /// <param name="parent">Object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The text, or null.</returns>
   private static string? TextList( JsonElement parent, string name )
   {
      if( parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty( name, out JsonElement list ) || list.ValueKind != JsonValueKind.Array )
      {
         return null;
      }

      string[] values = list.EnumerateArray().Where( v => v.ValueKind == JsonValueKind.String ).Select( v => v.GetString()! ).Distinct( StringComparer.Ordinal ).ToArray();
      return values.Length == 0 ? null : string.Join( " / ", values );
   }

   /// <summary>
   /// The distinct strings a property holds in each of the target's per-run facts, joined with " / ".
   /// </summary>
   /// <param name="target">One targetSummaries entry.</param>
   /// <param name="name">Property name inside each perRun entry.</param>
   /// <returns>The text, or null when no run carries it.</returns>
   private static string? PerRunTexts( JsonElement target, string name )
   {
      if( target.ValueKind != JsonValueKind.Object || !target.TryGetProperty( "perRun", out JsonElement runs ) || runs.ValueKind != JsonValueKind.Array )
      {
         return null;
      }

      string[] values = runs.EnumerateArray().Select( r => Text( r, name ) ).OfType<string>().Distinct( StringComparer.Ordinal ).ToArray();
      return values.Length == 0 ? null : string.Join( " / ", values );
   }

   /// <summary>
   /// The engines the report names as left out ("withheld"), each with its reason; none for an
   /// older file or an empty list.
   /// </summary>
   /// <param name="root">The file's root object.</param>
   /// <returns>The engines, or null when there are none.</returns>
   private static List<BenchWithheld>? WithheldTargets( JsonElement root )
   {
      if( !root.TryGetProperty( "withheld", out JsonElement withheld ) || withheld.ValueKind != JsonValueKind.Array )
      {
         return null;
      }

      List<BenchWithheld> list = withheld.EnumerateArray().Where( w => w.ValueKind == JsonValueKind.Object && Text( w, "target" ) != null )
         .Select( w => new BenchWithheld( Text( w, "target" )!, FriendlyName( Text( w, "target" )! ), Text( w, "reason" ) ?? "no reason given" ) ).Take( MAX_ENGINES ).ToList();
      return list.Count == 0 ? null : list;
   }

   /// <summary>
   /// Puts the engines in tie bands when the numbers come from two runs or more; one run has no
   /// spread, so its engines get no band.
   /// </summary>
   /// <param name="ranked">Engines in median order.</param>
   /// <param name="runs">Runs behind each median.</param>
   /// <returns>The engines, with their band when there is a spread.</returns>
   private static List<BenchEngineRow> WithBands( List<BenchEngineRow> ranked, int runs )
   {
      return runs > 1 ? BenchBands.Assign( ranked ) : ranked;
   }

   /// <summary>
   /// The client's CPU per search with one searcher, in milliseconds: "clientCpuMsPerSearch" keyed by
   /// level ("1" or "default@1"), read from one run's search section (a number per level) or from a
   /// consolidated target (a median, min and max per level, of which the median is shown).
   /// </summary>
   /// <param name="holder">One run's search section, or one targetSummaries entry.</param>
   /// <param name="spread">True when each level holds a median/min/max object, false when it holds a number.</param>
   /// <returns>The milliseconds, or null when the results did not record it.</returns>
   private static double? ClientCpuAtOne( JsonElement holder, bool spread )
   {
      foreach( string name in CLIENT_CPU_NAMES )
      {
         if( holder.ValueKind != JsonValueKind.Object || !holder.TryGetProperty( name, out JsonElement levels ) || levels.ValueKind != JsonValueKind.Object )
         {
            continue;
         }

         foreach( string level in new[] { "1", "default@1" } )
         {
            if( levels.TryGetProperty( level, out JsonElement one ) && ( spread ? Number( one, "median" ) : PlainNumber( one ) ) is double value )
            {
               return value;
            }
         }
      }

      return null;
   }

   /// <summary>
   /// A JSON number as a finite, non-negative double, or null.
   /// </summary>
   /// <param name="element">The element.</param>
   /// <returns>The value, or null when it is not such a number.</returns>
   private static double? PlainNumber( JsonElement element )
   {
      return element.ValueKind == JsonValueKind.Number && element.TryGetDouble( out double d ) && double.IsFinite( d ) && d >= 0 ? d : null;
   }

   /// <summary>
   /// The notes the consolidate command wrote for one target ("engineNotes"); none for an older file.
   /// </summary>
   /// <param name="target">One targetSummaries entry.</param>
   /// <returns>The notes, or null when there are none.</returns>
   private static List<BenchEngineNote>? EngineNotes( JsonElement target )
   {
      if( target.ValueKind != JsonValueKind.Object || !target.TryGetProperty( "engineNotes", out JsonElement notes ) || notes.ValueKind != JsonValueKind.Array )
      {
         return null;
      }

      List<BenchEngineNote> list = notes.EnumerateArray().Where( n => n.ValueKind == JsonValueKind.Object )
         .Select( n => new BenchEngineNote( Text( n, "kind" ) ?? "note", Text( n, "text" ) ?? string.Empty, Text( n, "source" ) ?? "results" ) )
         .Where( n => n.Text.Length > 0 ).ToList();
      return list.Count == 0 ? null : list;
   }

   /// <summary>
   /// A string property, or null.
   /// </summary>
   /// <param name="parent">Object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The text, or null.</returns>
   private static string? Text( JsonElement parent, string name )
   {
      return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty( name, out JsonElement v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
   }

   /// <summary>
   /// A positive whole-number property, or null.
   /// </summary>
   /// <param name="parent">Object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The number, or null when absent, not a whole number or not positive.</returns>
   private static int? WholeNumber( JsonElement parent, string name )
   {
      return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty( name, out JsonElement v ) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32( out int n ) && n > 0 ? n : null;
   }

   /// <summary>
   /// Groups the report's flags by target; each detail ends with how many of the runs it covers.
   /// </summary>
   /// <param name="root">The file's root object.</param>
   /// <param name="runs">Runs used.</param>
   /// <returns>Flags by target name.</returns>
   private static Dictionary<string, List<BenchFlag>> ReportFlags( JsonElement root, int runs )
   {
      var byTarget = new Dictionary<string, List<BenchFlag>>( StringComparer.Ordinal );
      if( !root.TryGetProperty( "flags", out JsonElement flags ) || flags.ValueKind != JsonValueKind.Array )
      {
         return byTarget;
      }

      foreach( JsonElement flag in flags.EnumerateArray().Where( f => f.ValueKind == JsonValueKind.Object ) )
      {
         string? target = flag.TryGetProperty( "target", out JsonElement t ) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
         string? kind = flag.TryGetProperty( "kind", out JsonElement k ) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
         if( target == null || kind == null )
         {
            continue;
         }

         string detail = flag.TryGetProperty( "detail", out JsonElement d ) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : string.Empty;
         int covered = flag.TryGetProperty( "runs", out JsonElement r ) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0;
         if( !byTarget.TryGetValue( target, out List<BenchFlag>? list ) )
         {
            byTarget[target] = list = new List<BenchFlag>();
         }

         list.Add( new BenchFlag( kind, runs > 0 && covered > 0 ? $"{detail} [{covered} of {runs} runs]" : detail ) );
      }

      return byTarget;
   }

   /// <summary>
   /// Fastest median first; equal numbers fall back to the name so the order never wobbles. This
   /// is the order inside a band too; it is not a claim that a faster median is a faster engine.
   /// </summary>
   /// <param name="rows">Unordered rows.</param>
   /// <returns>Ranked rows.</returns>
   private static List<BenchEngineRow> Rank( List<BenchEngineRow> rows )
   {
      return rows.OrderByDescending( r => r.Qps8 ).ThenBy( r => r.Name, StringComparer.Ordinal ).ToList();
   }

   /// <summary>
   /// Refuses a file with more engines than any real run has, so a wrong file fails loud
   /// instead of drawing a chart thousands of rows tall.
   /// </summary>
   /// <param name="seen">Engines read so far.</param>
   private static void GuardCount( int seen )
   {
      if( seen >= MAX_ENGINES )
      {
         throw new JsonException( $"More than {MAX_ENGINES} engines in one file; refusing to draw it." );
      }
   }

   /// <summary>
   /// Reads e.g. engine.qps8.median, or null when absent or not a finite number.
   /// </summary>
   /// <param name="engine">Engine object.</param>
   /// <param name="field">Stat name, e.g. "qps8".</param>
   /// <param name="part">"median", "min" or "max".</param>
   /// <returns>The value or null.</returns>
   private static double? Stat( JsonElement engine, string field, string part )
   {
      return engine.ValueKind == JsonValueKind.Object && engine.TryGetProperty( field, out JsonElement stat ) ? Number( stat, part ) : null;
   }

   /// <summary>
   /// Reads a finite number property, or null.
   /// Why finite only: NaN or infinity would put a bar off the chart.
   /// </summary>
   /// <param name="parent">Object holding the property.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The value or null.</returns>
   private static double? Number( JsonElement parent, string name )
   {
      return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty( name, out JsonElement v ) && v.ValueKind == JsonValueKind.Number
         && v.TryGetDouble( out double d ) && double.IsFinite( d ) && d >= 0 ? d : null;
   }

   #endregion Private Methods
}
