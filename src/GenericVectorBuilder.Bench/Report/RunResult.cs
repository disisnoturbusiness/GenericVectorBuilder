using System.Text.Json;
using GenericVectorBuilder.Bench.Stats;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// One run's results.json, read for the consolidate command. Every field is optional: a run
/// written before a field existed loads with that field null, and the report shows it as
/// missing instead of guessing a value.
/// Why its own reader instead of deserializing <see cref="BenchReport"/>: old result folders
/// must keep loading after BenchReport grows, and a field that is absent must stay
/// distinguishable from a field that is zero.
/// </summary>
public sealed class RunResult
{
   #region Public Methods

   /// <summary>Full path of the result folder.</summary>
   public string Folder { get; init; } = string.Empty;

   /// <summary>Folder name, used as the run's label (e.g. "20261004-130858-eshoponweb").</summary>
   public string Name => Path.GetFileName( Folder.TrimEnd( '/', '\\' ) );

   /// <summary>When the run started (UTC text as written).</summary>
   public string? StartedUtc { get; init; }

   /// <summary>The exact command line of the run.</summary>
   public string? CommandLine { get; init; }

   /// <summary>The benchmark command the run was made with ("run-all", "bench" or "replicate"); null when the run did not record it. Why: a bench run searches a copy an earlier replicate loaded, a run-all loads its own, and the two end with different segment layouts.</summary>
   public string? Command { get; init; }

   /// <summary>Source pipeline.</summary>
   public string? Pipeline { get; init; }

   /// <summary>Host the run happened on.</summary>
   public string? Host { get; init; }

   /// <summary>Load average when the run started.</summary>
   public string? LoadAverage { get; init; }

   /// <summary>Build, governor, CPU partition, warm-up and exact-mode settings the run was measured under; every field null when the run did not record it.</summary>
   public RunConditions Conditions { get; init; } = new();

   /// <summary>Rows used.</summary>
   public int? Rows { get; init; }

   /// <summary>Vector length.</summary>
   public int? Dimension { get; init; }

   /// <summary>"golden" or "random": the text before the first colon of the queries line.</summary>
   public string? QueryKind { get; init; }

   /// <summary>Number of queries.</summary>
   public int? QueryCount { get; init; }

   /// <summary>Hits per query.</summary>
   public int? Top { get; init; }

   /// <summary>Concurrency levels of the throughput test.</summary>
   public IReadOnlyList<int> Concurrency { get; init; } = Array.Empty<int>();

   /// <summary>Seconds per concurrency level.</summary>
   public int? SecondsPerLevel { get; init; }

   /// <summary>Seed that shuffled target and pass order, or null when the run did not record one.</summary>
   public int? RunSeed { get; init; }

   /// <summary>Target order actually run, or null when the run did not record it.</summary>
   public IReadOnlyList<string>? TargetOrder { get; init; }

   /// <summary>Targets in file order.</summary>
   public IReadOnlyList<TargetResult> Targets { get; init; } = Array.Empty<TargetResult>();

   /// <summary>
   /// Reads results.json from a folder.
   /// </summary>
   /// <param name="folder">The run's folder.</param>
   /// <returns>The run.</returns>
   /// <exception cref="InvalidDataException">The file is missing or is not a results.json; the message says which.</exception>
   public static RunResult Load( string folder )
   {
      string file = Path.Combine( folder, "results.json" );
      if( !File.Exists( file ) )
      {
         throw new InvalidDataException( "no results.json in the folder" );
      }

      try
      {
         return Parse( Path.GetFullPath( folder ), File.ReadAllText( file ) );
      }
      catch( JsonException ex )
      {
         throw new InvalidDataException( $"results.json is not valid JSON ({ex.Message})" );
      }
   }

   /// <summary>
   /// Parses results.json text.
   /// </summary>
   /// <param name="folder">Folder the text came from.</param>
   /// <param name="json">The file's text.</param>
   /// <returns>The run.</returns>
   /// <exception cref="InvalidDataException">The text has no targets array.</exception>
   public static RunResult Parse( string folder, string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement root = doc.RootElement;
      if( root.ValueKind != JsonValueKind.Object || ResultJson.Child( root, "targets" ) is not { ValueKind: JsonValueKind.Array } targets )
      {
         throw new InvalidDataException( "results.json has no targets array" );
      }

      JsonElement? machine = ResultJson.Child( root, "machine" );
      string? queries = ResultJson.Text( root, "queries" );
      string? commandLine = ResultJson.Text( root, "commandLine" );
      return new RunResult
      {
         Folder = folder,
         StartedUtc = ResultJson.Text( root, "startedUtc" ),
         CommandLine = commandLine,
         Command = ResultJson.Text( root, "command" ),
         Pipeline = ResultJson.Text( root, "pipeline" ),
         Host = machine is JsonElement m ? ResultJson.Text( m, "host" ) : null,
         LoadAverage = machine is JsonElement l ? ResultJson.Text( l, "loadAverage" ) : null,
         Conditions = RunConditions.Parse( root, commandLine, ResultJson.Texts( root, "notes" ) ),
         Rows = ResultJson.Int( root, "rows" ),
         Dimension = ResultJson.Int( root, "dimension" ),
         QueryKind = queries == null ? null : queries.Split( ':' )[0].Trim(),
         QueryCount = ResultJson.Int( root, "queryCount" ),
         Top = ResultJson.Int( root, "top" ),
         Concurrency = ResultJson.Ints( root, "concurrency" ) ?? Array.Empty<int>(),
         SecondsPerLevel = ResultJson.Int( root, "secondsPerLevel" ),
         RunSeed = ResultJson.Int( root, "runSeed" ),
         TargetOrder = ResultJson.Texts( root, "targetOrder" ),
         Targets = targets.EnumerateArray().Where( t => t.ValueKind == JsonValueKind.Object ).Select( TargetResult.Parse ).ToList(),
      };
   }

   /// <summary>
   /// The target with this name, or null.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <returns>The first target with the name.</returns>
   public TargetResult? Find( string name )
   {
      return Targets.FirstOrDefault( t => string.Equals( t.Name, name, StringComparison.Ordinal ) );
   }

   #endregion Public Methods
}

/// <summary>
/// One target's entry in a results.json. Null means the run did not write the field.
/// Why every number is nullable: "not measured" and "measured as zero" must never look the same
/// in a consolidated report.
/// </summary>
public sealed class TargetResult
{
   #region Data Members

   private static readonly string[] SETTING_NAMES = { "searchSettings", "searchSetting", "searchParams", "searchEffort", "effort", "settings" };
   private static readonly string[] SETTLED_NAMES = { "settled", "isSettled", "indexSettled" };
   private static readonly string[] CPU_CAP_NAMES = { "cpuCap", "cpuLimit", "engineCpuCap" };
   private static readonly string[] CLIENT_CPU_NAMES = { "clientCpuMsPerSearch", "clientCpuPerSearchMs" };

   #endregion Data Members

   #region Public Methods

   /// <summary>Target name.</summary>
   public string Name { get; init; } = string.Empty;

   /// <summary>Engine name and version.</summary>
   public string? Engine { get; init; }

   /// <summary>Index used by the default search.</summary>
   public string? Index { get; init; }

   /// <summary>compose, always-on or embedded.</summary>
   public string? Hosting { get; init; }

   /// <summary>Why the target failed, if it did.</summary>
   public string? Error { get; init; }

   /// <summary>True when the run wrote a search section for this target.</summary>
   public bool Searched { get; init; }

   /// <summary>Load rows per second.</summary>
   public double? LoadRowsPerSecond { get; init; }

   /// <summary>What the load's index step reported (load.indexNote), e.g. how many vectors an index covered.</summary>
   public string? LoadIndexNote { get; init; }

   /// <summary>Median latency, ms.</summary>
   public double? P50Ms { get; init; }

   /// <summary>95th percentile latency, ms.</summary>
   public double? P95Ms { get; init; }

   /// <summary>Queries per second by concurrency level.</summary>
   public IReadOnlyDictionary<int, double> Qps { get; init; } = new Dictionary<int, double>();

   /// <summary>Recall@k.</summary>
   public double? Recall { get; init; }

   /// <summary>nDCG@k (golden queries only).</summary>
   public double? Ndcg { get; init; }

   /// <summary>Timed searches that failed.</summary>
   public int? Errors { get; init; }

   /// <summary>Exact-mode queries timed.</summary>
   public int? ExactQueries { get; init; }

   /// <summary>Exact-mode median latency, ms.</summary>
   public double? ExactP50Ms { get; init; }

   /// <summary>Exact-mode recall.</summary>
   public double? ExactRecall { get; init; }

   /// <summary>Pass names in the order run, e.g. "default@1", "exact", "default@8"; null when not recorded.</summary>
   public IReadOnlyList<string>? PassOrder { get; init; }

   /// <summary>Warm-up searches that failed; null when not recorded.</summary>
   public int? WarmupErrors { get; init; }

   /// <summary>Index state read from the engine after the load; null when not recorded.</summary>
   public IndexStateValue? AfterLoad { get; init; }

   /// <summary>Index state read from the engine after the searches; null when not recorded.</summary>
   public IndexStateValue? AfterSearch { get; init; }

   /// <summary>What a crash can lose with this engine's settings; null when not recorded.</summary>
   public string? Durability { get; init; }

   /// <summary>
   /// The settings that set how hard the engine searched (search beam, probes, ef), as sorted
   /// "key=value" text; null when not recorded. Why: two engines at different effort are not
   /// comparable, and a run at another effort must not be averaged with this one.
   /// </summary>
   public string? SearchSettings { get; init; }

   /// <summary>True when the engine was idle and warm before it was timed, false when it was still starting, building or compacting; null when not recorded.</summary>
   public bool? Settled { get; init; }

   /// <summary>The run's own words on why the target was or was not settled; null when not recorded.</summary>
   public string? SettleDetail { get; init; }

   /// <summary>The two passes of this target a consolidated report should compare by default, as the target declared them; null when it declared none.</summary>
   public PairHintValue? PairHint { get; init; }

   /// <summary>Mean latency of one search at a time, ms, as recorded; null when not recorded (the consolidator then derives it from QPS at one searcher).</summary>
   public double? MeanMs { get; init; }

   /// <summary>A CPU limit the engine puts on itself (e.g. "cpu_count 2"), as the run recorded it on the target; null when not recorded.</summary>
   public string? CpuCap { get; init; }

   /// <summary>
   /// CPU time the benchmark client itself used per search, in milliseconds, by concurrency level
   /// (the pass with that many searchers); empty when the run did not record it. Read from the
   /// search section's "clientCpuMsPerSearch", an object keyed by level ("1", "8" or "default@8").
   /// Why carried: for the fastest engines it is about as large as the latency, so the speed order
   /// partly reflects each engine's .NET client library, and a reader needs both numbers side by side.
   /// </summary>
   public IReadOnlyDictionary<int, double> ClientCpuMsPerSearch { get; init; } = new Dictionary<int, double>();

   /// <summary>
   /// Reads one target object.
   /// Why passOrder and warmupErrors are also looked for inside "search": the contract puts them
   /// on the target, and a writer that put them on the search section must not read as missing.
   /// </summary>
   /// <param name="t">The target's JSON object.</param>
   /// <returns>The target.</returns>
   public static TargetResult Parse( JsonElement t )
   {
      ( bool? notedSettled, string? notedDetail ) = SettleFromNotes( t );
      JsonElement? search = ResultJson.Child( t, "search" ) is { ValueKind: JsonValueKind.Object } s ? s : null;
      JsonElement? load = ResultJson.Child( t, "load" ) is { ValueKind: JsonValueKind.Object } l ? l : null;
      JsonElement? state = ResultJson.Child( t, "indexState" ) is { ValueKind: JsonValueKind.Object } i ? i : null;
      return new TargetResult
      {
         Name = ResultJson.Text( t, "name" ) ?? string.Empty,
         Engine = ResultJson.Text( t, "engine" ),
         Index = ResultJson.Text( t, "index" ),
         Hosting = ResultJson.Text( t, "hosting" ),
         Error = ResultJson.Text( t, "error" ),
         Searched = search.HasValue,
         LoadRowsPerSecond = load is JsonElement ld ? ResultJson.Number( ld, "rowsPerSecond" ) : null,
         LoadIndexNote = load is JsonElement ln ? ResultJson.Text( ln, "indexNote" ) : null,
         P50Ms = Search( search, "p50Ms" ),
         P95Ms = Search( search, "p95Ms" ),
         Qps = search is JsonElement q ? ResultJson.LevelMap( q, "qps" ) : new Dictionary<int, double>(),
         Recall = Search( search, "recall" ),
         Ndcg = Search( search, "ndcg" ),
         Errors = search is JsonElement e ? ResultJson.Int( e, "errors" ) : null,
         ExactQueries = search is JsonElement x ? ResultJson.Int( x, "exactQueries" ) : null,
         ExactP50Ms = Search( search, "exactP50Ms" ),
         ExactRecall = Search( search, "exactRecall" ),
         PassOrder = ResultJson.Texts( t, "passOrder" ) ?? ( search is JsonElement p ? ResultJson.Texts( p, "passOrder" ) : null ),
         WarmupErrors = ResultJson.Int( t, "warmupErrors" ) ?? ( search is JsonElement w ? ResultJson.Int( w, "warmupErrors" ) : null ),
         AfterLoad = state is JsonElement a ? IndexStateValue.Parse( ResultJson.Child( a, "afterLoad" ) ) : null,
         AfterSearch = state is JsonElement b ? IndexStateValue.Parse( ResultJson.Child( b, "afterSearch" ) ) : null,
         Durability = ResultJson.Text( t, "durability" ),
         SearchSettings = SettingsText( t, search ),
         Settled = ReadSettled( t, search ) ?? notedSettled,
         SettleDetail = ReadSettleDetail( t, search ) ?? notedDetail,
         PairHint = PairHintValue.Parse( ResultJson.Child( t, "pairHint" ) ),
         MeanMs = Search( search, "meanMs" ) ?? Search( search, "latencyMeanMs" ),
         CpuCap = CpuCapText( t ),
         ClientCpuMsPerSearch = ClientCpu( t, search ),
      };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The client's CPU time per search by concurrency level, from the search section or the target
   /// object; levels whose value is negative are skipped.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <param name="search">The search section, if any.</param>
   /// <returns>Milliseconds by level; empty when the run recorded none.</returns>
   private static IReadOnlyDictionary<int, double> ClientCpu( JsonElement target, JsonElement? search )
   {
      foreach( JsonElement holder in search.HasValue ? new[] { search.Value, target } : new[] { target } )
      {
         foreach( string name in CLIENT_CPU_NAMES )
         {
            Dictionary<int, double> found = ResultJson.LevelMap( holder, name ).Where( p => p.Value >= 0 ).ToDictionary( p => p.Key, p => p.Value );
            if( found.Count > 0 )
            {
               return found;
            }
         }
      }

      return new Dictionary<int, double>();
   }

   /// <summary>
   /// The engine's own CPU limit as text, from the accepted spellings on the target object.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <returns>The text, or null when none is recorded.</returns>
   private static string? CpuCapText( JsonElement target )
   {
      return CPU_CAP_NAMES.Select( name => ResultJson.Child( target, name ) is JsonElement found ? RunConditions.AsText( found ) : null ).FirstOrDefault( text => text != null );
   }

   /// <summary>
   /// The search-effort settings as sorted text, looked for on the target and in its search section.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <param name="search">The search section, if any.</param>
   /// <returns>The text, or null when neither carries any.</returns>
   private static string? SettingsText( JsonElement target, JsonElement? search )
   {
      foreach( JsonElement holder in search.HasValue ? new[] { target, search.Value } : new[] { target } )
      {
         foreach( string name in SETTING_NAMES )
         {
            if( ResultJson.Child( holder, name ) is JsonElement found && RunConditions.AsText( found ) is string text )
            {
               return text;
            }
         }
      }

      return null;
   }

   /// <summary>
   /// Whether the target was settled before it was timed: "settled" as a true/false on the
   /// target or its search section, or inside a "settle" object.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <param name="search">The search section, if any.</param>
   /// <returns>True, false, or null when not recorded.</returns>
   private static bool? ReadSettled( JsonElement target, JsonElement? search )
   {
      foreach( JsonElement holder in search.HasValue ? new[] { target, search.Value } : new[] { target } )
      {
         foreach( string name in SETTLED_NAMES )
         {
            if( ResultJson.Bool( holder, name ) is bool flag )
            {
               return flag;
            }
         }

         if( ResultJson.Child( holder, "settle" ) is { ValueKind: JsonValueKind.Object } settle )
         {
            foreach( string name in SETTLED_NAMES.Append( "ok" ) )
            {
               if( ResultJson.Bool( settle, name ) is bool flag )
               {
                  return flag;
               }
            }
         }
      }

      return null;
   }

   /// <summary>
   /// Whether the target settled, read from its notes when no structured field says: the
   /// measurement step writes "Settle: settled after ..." when it settled and "WARNING: latency had
   /// NOT settled ..." (or "no settle ran") when it did not.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <returns>Settled (null when the notes say nothing) and the warning text without its "WARNING: " prefix.</returns>
   private static (bool? Settled, string? Detail) SettleFromNotes( JsonElement target )
   {
      IReadOnlyList<string> notes = ResultJson.Texts( target, "notes" ) ?? Array.Empty<string>();
      string? warning = notes.FirstOrDefault( n => n.Contains( "NOT settled", StringComparison.Ordinal ) || n.Contains( "no settle ran", StringComparison.Ordinal ) );
      if( warning != null )
      {
         return ( false, warning.StartsWith( "WARNING: ", StringComparison.Ordinal ) ? warning["WARNING: ".Length..] : warning );
      }

      return notes.Any( n => n.StartsWith( "Settle: settled", StringComparison.Ordinal ) ) ? ( true, null ) : ( null, null );
   }

   /// <summary>
   /// The run's note about settling: "settleNote" or "settleDetail" on the target or search
   /// section, or the "detail" / "note" of a "settle" object.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <param name="search">The search section, if any.</param>
   /// <returns>The text, or null.</returns>
   private static string? ReadSettleDetail( JsonElement target, JsonElement? search )
   {
      foreach( JsonElement holder in search.HasValue ? new[] { target, search.Value } : new[] { target } )
      {
         string? direct = ResultJson.Text( holder, "settleNote" ) ?? ResultJson.Text( holder, "settleDetail" );
         if( direct != null )
         {
            return direct;
         }

         if( ResultJson.Child( holder, "settle" ) is { ValueKind: JsonValueKind.Object } settle )
         {
            return ResultJson.Text( settle, "detail" ) ?? ResultJson.Text( settle, "note" );
         }
      }

      return null;
   }

   /// <summary>
   /// A number from the search section, or null when there is no search section.
   /// </summary>
   /// <param name="search">The search section, if any.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The number, or null.</returns>
   private static double? Search( JsonElement? search, string name )
   {
      return search is JsonElement s ? ResultJson.Number( s, name ) : null;
   }

   #endregion Private Methods
}

/// <summary>
/// An engine's own account of its index, as written in results.json.
/// Why a local copy of the shape instead of the engines' IndexState record: the consolidate
/// code compiles and is tested with no reference to the engines, and an old or partial object
/// (Ready missing) must load as "not known" rather than "not ready".
/// </summary>
/// <param name="Ready">True when the engine said searches use the finished index; null when not written.</param>
/// <param name="IndexedVectors">Vectors covered by the index, when the engine said.</param>
/// <param name="TotalVectors">Vectors stored, when the engine said.</param>
/// <param name="Detail">The engine's evidence text.</param>
/// <param name="Layout">The segment layout the engine reported ("2 segments"), or null when it reported none.</param>
public sealed record IndexStateValue( bool? Ready, long? IndexedVectors, long? TotalVectors, string? Detail, string? Layout = null )
{
   #region Public Methods

   /// <summary>
   /// Reads an index state object.
   /// </summary>
   /// <param name="element">The object, or null.</param>
   /// <returns>The state, or null when the object is absent.</returns>
   public static IndexStateValue? Parse( JsonElement? element )
   {
      if( element is not { ValueKind: JsonValueKind.Object } e )
      {
         return null;
      }

      string? detail = ResultJson.Text( e, "detail" );
      return new IndexStateValue( ResultJson.Bool( e, "ready" ), ResultJson.Long( e, "indexedVectors" ), ResultJson.Long( e, "totalVectors" ), detail, SegmentLayout.Read( e, detail ) );
   }

   #endregion Public Methods
}

/// <summary>
/// The pair of passes of one target that a consolidated report compares by default, as the
/// target declared it in results.json ("pairHint").
/// Why it comes from the target: only the target knows what a fair pair is (SQL Server's DiskANN
/// default search against its own exact mode on the same table, not against another database).
/// </summary>
/// <param name="PassA">First pass, as named in the run's passOrder, e.g. "default@1".</param>
/// <param name="PassB">Second pass, e.g. "exact".</param>
/// <param name="Note">What makes the pair fair, in the target's words.</param>
public sealed record PairHintValue( string? PassA, string? PassB, string? Note )
{
   #region Public Methods

   /// <summary>
   /// Reads a pairHint object.
   /// </summary>
   /// <param name="element">The object, or null.</param>
   /// <returns>The hint, or null when absent.</returns>
   public static PairHintValue? Parse( JsonElement? element )
   {
      return element is { ValueKind: JsonValueKind.Object } e ? new PairHintValue( ResultJson.Text( e, "passA" ), ResultJson.Text( e, "passB" ), ResultJson.Text( e, "note" ) ) : null;
   }

   #endregion Public Methods
}
