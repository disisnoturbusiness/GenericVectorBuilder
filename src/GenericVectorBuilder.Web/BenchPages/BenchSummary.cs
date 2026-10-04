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
public sealed record BenchEngineRow( string Key, string Name, double Qps8, double? Qps8Min, double? Qps8Max, double? P50Ms, double? Recall, bool InMemory, IReadOnlyList<BenchFlag>? Flags = null );

/// <summary>
/// The ranked numbers behind a summary block: the leader first.
/// Why one shape for both sources: the summary page (medians of several runs) and each run page
/// (one run) must read the same way, so the chart, headline and table are built from this alone.
/// </summary>
/// <param name="Ranked">Engines with a result, fastest first by searches per second with 8 at once.</param>
/// <param name="NoResult">Friendly names of engines that produced no throughput number.</param>
/// <param name="Runs">How many runs each median covers (1 for a single run page).</param>
/// <param name="Conditions">The machine conditions the numbers were measured under; null or <see cref="BenchConditions.None"/> when the result files did not record them.</param>
public sealed record BenchSummary( IReadOnlyList<BenchEngineRow> Ranked, IReadOnlyList<string> NoResult, int Runs, BenchConditions? Conditions = null )
{
   /// <summary>True when the numbers are medians across runs, so the chart draws min to max lines.</summary>
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

   private static readonly Dictionary<string, string> FRIENDLY = new( StringComparer.OrdinalIgnoreCase )
   {
      ["sql"] = "SQL Server 2025",
      ["sql-diskann"] = "SQL Server 2025 + DiskANN",
      ["qdrant"] = "Qdrant (exact)",
      ["qdrant-hnsw"] = "Qdrant (HNSW)",
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

   private static readonly HashSet<string> IN_MEMORY = new( StringComparer.OrdinalIgnoreCase ) { "redis" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The name a reader sees for a Bench target, or the target name itself when unknown.
   /// </summary>
   /// <param name="key">Target name, e.g. "sqlitevec".</param>
   /// <returns>Friendly name, e.g. "sqlite-vec".</returns>
   public static string FriendlyName( string key )
   {
      return FRIENDLY.TryGetValue( key, out string? name ) ? name : key;
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

      return new BenchSummary( Rank( rows ), missing, Math.Max( runs, 1 ) );
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
            t.ValueKind == JsonValueKind.Object ? BenchFlagInfo.FromTarget( t ) : null ) );
      }

      return new BenchSummary( Rank( rows ), missing, 1, BenchConditions.FromRun( doc.RootElement ) );
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
      foreach( JsonElement t in summaries.EnumerateArray() )
      {
         GuardCount( rows.Count + missing.Count );
         string key = t.ValueKind == JsonValueKind.Object && t.TryGetProperty( "name", out JsonElement n ) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "?" : "?";
         JsonElement qps = t.ValueKind == JsonValueKind.Object && t.TryGetProperty( "qps", out JsonElement q ) ? q : default;
         double? median = qps.ValueKind == JsonValueKind.Object && qps.TryGetProperty( EIGHT_AT_ONCE, out JsonElement at8 ) ? Number( at8, "median" ) : null;
         if( median == null )
         {
            missing.Add( FriendlyName( key ) );
            continue;
         }

         JsonElement eight = qps.GetProperty( EIGHT_AT_ONCE );
         rows.Add( new BenchEngineRow( key, FriendlyName( key ), median.Value, Number( eight, "min" ), Number( eight, "max" ), Stat( t, "p50Ms", "median" ), Stat( t, "recall", "median" ),
            IN_MEMORY.Contains( key ), flags.GetValueOrDefault( key ) ) );
      }

      BenchConditions conditions = root.TryGetProperty( "settings", out JsonElement settings ) && settings.ValueKind == JsonValueKind.Object ? BenchConditions.FromSettings( settings ) : BenchConditions.None;
      return new BenchSummary( Rank( rows ), missing, Math.Max( runs, 1 ), conditions );
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
   /// Fastest first; equal numbers fall back to the name so the order never wobbles.
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
