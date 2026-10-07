using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// A v8 consolidated.json built in code: six engines, two sessions of three runs, the four metric tables with
/// their separations computed by the claim rule at a 35% threshold, and one sentence for every section the page
/// knows. Built from numbers here, not copied from a results folder, so that no test depends on bench-results
/// and so that the separations, the median order and the per-session ranges in the file agree by construction
/// (the reader refuses a file where they do not).
/// Why built and not stored: Tests.csproj copies no data files to the output, and a fixture the test can change
/// one field of (a stopped set, a bad order, an unknown slot) is how each refusal is proven.
/// </summary>
internal static class BenchResultsV8Fixture
{
   #region Data Members

   /// <summary>The threshold in basis points the fixture's separations use.</summary>
   public const int T_BP = 3500;

   /// <summary>The two session names.</summary>
   public static readonly string[] SESSIONS = { "v7", "v8" };

   /// <summary>The run folders of the first session.</summary>
   public static readonly string[] V7_RUNS = { "20261006-130619-eshoponweb", "20261006-142724-eshoponweb", "20261006-154837-eshoponweb" };

   /// <summary>The run folders of the second session.</summary>
   public static readonly string[] V8_RUNS = { "20261008-130000-eshoponweb", "20261008-143000-eshoponweb", "20261008-160000-eshoponweb" };

   /// <summary>A recorded durability text, quoted on the page.</summary>
   public const string REDIS_DURABILITY = "save \"300 1\" and appendonly no: a crash can lose the writes since the last snapshot, plus the time a snapshot takes.";

   /// <summary>A note an engine's row carries.</summary>
   public const string REDIS_NOTE = "Holds its data in memory.";

   /// <summary>The label the fixture gives one engine's search fact, one the page must print as it is and never as a stronger one.</summary>
   public const string OWN_REPORT_LABEL = "recorded (engine's own report)";

   /// <summary>The engine whose search fact carries <see cref="OWN_REPORT_LABEL"/>.</summary>
   public const string OWN_REPORT_TARGET = "pgvector";

   /// <summary>Targets whose search ran as an exact scan.</summary>
   public static readonly string[] EXACT_TARGETS = { "qdrant", "sqlitevec", "sql" };

   /// <summary>Target, display name, p50 (ms), QPS at 1, QPS at 8, exact p50 (ms), row note; the figures are medians the runs scatter around.</summary>
   private static readonly ( string Target, string Display, double P50, double Qps1, double Qps8, double? Exact, string? Note )[] ENGINES =
   {
      ( "redis", "Redis", 0.40, 2527, 5120, 1.2, REDIS_NOTE ),
      ( "mariadb", "MariaDB", 0.64, 1546, 5911, 2.0, null ),
      ( "pgvector", "pgvector", 0.88, 1107, 3934, 3.0, null ),
      ( "qdrant", "Qdrant (exact)", 0.89, 1111, 3274, null, null ),
      ( "sqlitevec", "sqlite-vec", 1.92, 510, 1142, 1.9, null ),
      ( "sql", "SQL Server 2025", 4.06, 241, 612, 4.0, null ),
   };

   /// <summary>The Markdown report a fixture folder carries: a title, a bullet and a table whose cell holds an escaped pipe.</summary>
   public const string REPORT_MD = "# Vector engine benchmark: consolidated\n\n- A line of the report.\n\n| engine | durability | next |\n|---|---|---|\n| sqlitevec | PRAGMA journal_mode=WAL \\| USE TEMP B-TREE | after |\n";

   private static readonly double[] V7_SCATTER = { 0.99, 1.00, 1.01 };
   private static readonly double[] V8_SCATTER = { 1.00, 1.02, 1.01 };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The fixture as JSON text, after an optional change to its object.
   /// </summary>
   /// <param name="change">Edits the root before it is written, or null for the file as built.</param>
   /// <returns>The JSON text.</returns>
   public static string Json( Action<JsonObject>? change = null )
   {
      JsonObject root = Build();
      change?.Invoke( root );
      return root.ToJsonString();
   }

   /// <summary>
   /// A sentence object for a slot, with one results source.
   /// </summary>
   /// <param name="slot">The slot.</param>
   /// <param name="text">The text.</param>
   /// <param name="quote">True for a quoted recorded text.</param>
   /// <returns>The object.</returns>
   public static JsonObject Sentence( string slot, string text, bool quote = false )
   {
      var obj = new JsonObject
      {
         ["slot"] = slot,
         ["text"] = text,
         ["sources"] = new JsonArray( new JsonObject { ["kind"] = "results", ["ref"] = "targets[redis].index#HNSW", ["value"] = "HNSW" } ),
      };
      if( quote )
      {
         obj["quote"] = true;
      }

      return obj;
   }

   /// <summary>
   /// The target names of the fixture's engines, in p50 order.
   /// </summary>
   /// <returns>The names.</returns>
   public static string[] Targets()
   {
      return ENGINES.OrderBy( e => e.P50 ).Select( e => e.Target ).ToArray();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds the whole file.
   /// </summary>
   /// <returns>The root object.</returns>
   private static JsonObject Build()
   {
      return new JsonObject
      {
         ["sessions"] = new JsonArray( Session( "v7", V7_RUNS, 701 ), Session( "v8", V8_RUNS, 801 ) ),
         ["threshold"] = new JsonObject { ["tBp"] = T_BP, ["ratio"] = 1.35 },
         ["basis"] = Basis(),
         ["metrics"] = new JsonArray( Metric( "p50Ms", true, e => e.P50 ), Metric( "qps1", false, e => e.Qps1 ), Metric( "qps8", false, e => e.Qps8 ), Metric( "exactP50Ms", true, e => e.Exact ) ),
         ["guards"] = new JsonObject { ["g2"] = new JsonObject { ["stopped"] = false, ["pairs"] = new JsonArray() }, ["g3"] = new JsonObject { ["targets"] = new JsonArray() } },
         ["drift"] = Drift(),
         ["recall"] = new JsonArray( ENGINES.Select( e => (JsonNode)Recall( e.Target, e.Target == "qdrant" ? 198 : 200 ) ).ToArray() ),
         ["clock"] = Clock(),
         ["machine"] = new JsonObject { ["cpu"] = "Intel(R) Xeon(R) CPU E5-1620 v3 @ 3.50GHz", ["logicalCpus"] = 8, ["ramGiB"] = 62.7, ["os"] = "Ubuntu 24.04.5 LTS", ["governor"] = "performance", ["partition"] = "client 0-1,4-5; engines 2-3,6-7" },
         ["images"] = new JsonArray( ENGINES.Select( e => (JsonNode)new JsonObject { ["target"] = e.Target, ["id"] = "sha256:" + new string( 'a', 64 ), ["lastTagTimeUtc"] = "2026-09-30T10:00:00Z", ["beforeFirstV7Start"] = true } ).ToArray() ),
         ["why"] = new JsonArray( Targets().Select( t => (JsonNode)Why( t ) ).ToArray() ),
         ["sentences"] = Sentences(),
         ["audit"] = new JsonObject { ["sentencesChecked"] = 20, ["failures"] = new JsonArray(), ["factsSha256"] = new string( 'b', 64 ), ["exclusionsSha256"] = new string( 'c', 64 ) },
      };
   }

   /// <summary>
   /// One session object.
   /// </summary>
   /// <param name="name">Its name.</param>
   /// <param name="runs">Its run folders.</param>
   /// <param name="firstSeed">The first run's seed.</param>
   /// <returns>The object.</returns>
   private static JsonObject Session( string name, string[] runs, int firstSeed )
   {
      return new JsonObject
      {
         ["name"] = name,
         ["runs"] = new JsonArray( runs.Select( ( r, i ) => (JsonNode)new JsonObject { ["folder"] = r, ["seed"] = firstSeed + i, ["startedUtc"] = r[..8] + "T" + r.Substring( 9, 6 ) + "Z" } ).ToArray() ),
      };
   }

   /// <summary>
   /// One metric table with the engines in median order and the separations by the claim rule.
   /// </summary>
   /// <param name="metric">The metric name.</param>
   /// <param name="lowerIsBetter">True for latency.</param>
   /// <param name="pick">The engine's median figure for this metric, or null when it has none.</param>
   /// <returns>The object.</returns>
   private static JsonObject Metric( string metric, bool lowerIsBetter, Func<( string Target, string Display, double P50, double Qps1, double Qps8, double? Exact, string? Note ), double?> pick )
   {
      var engines = ENGINES.Where( e => pick( e ) != null ).ToList();
      var figures = engines.ToDictionary( e => e.Target, e => Runs( pick( e )!.Value ) );
      var median = figures.ToDictionary( f => f.Key, f => Median( f.Value.V7.Concat( f.Value.V8 ).ToArray() ) );
      List<string> order = engines.Select( e => e.Target ).OrderBy( t => median[t] * ( lowerIsBetter ? 1 : -1 ) ).ToList();
      var rows = new JsonArray();
      foreach( string target in order )
      {
         var engine = engines.First( e => e.Target == target );
         string[] notSeparated = order.Where( o => o != target && !Ordered( figures, order, target, o, lowerIsBetter ) ).ToArray();
         var row = new JsonObject
         {
            ["target"] = target,
            ["display"] = engine.Display,
            ["status"] = "ranked",
            ["perSession"] = new JsonObject
            {
               ["v7"] = new JsonObject { ["min"] = figures[target].V7.Min(), ["max"] = figures[target].V7.Max(), ["runs"] = new JsonArray( figures[target].V7.Select( v => (JsonNode)JsonValue.Create( v )! ).ToArray() ) },
               ["v8"] = new JsonObject { ["min"] = figures[target].V8.Min(), ["max"] = figures[target].V8.Max(), ["runs"] = new JsonArray( figures[target].V8.Select( v => (JsonNode)JsonValue.Create( v )! ).ToArray() ) },
            },
            ["median"] = median[target],
            ["notSeparatedFrom"] = new JsonArray( notSeparated.Select( n => (JsonNode)JsonValue.Create( n )! ).ToArray() ),
            ["flags"] = new JsonArray(),
         };
         if( metric != "exactP50Ms" )
         {
            row["searchMode"] = SearchMode( target );
         }

         if( engine.Note != null )
         {
            row["note"] = engine.Note;
            row["noteSources"] = new JsonArray( new JsonObject { ["kind"] = "doc", ["ref"] = "doc:design/engine-docs/redis-faq.html#in-memory", ["value"] = "in-memory" },
               new JsonObject { ["kind"] = "file", ["ref"] = "deploy/engines/redis.compose.yaml#--appendonly no", ["value"] = "--appendonly no" } );
         }

         rows.Add( row );
      }

      return new JsonObject { ["metric"] = metric, ["lowerIsBetter"] = lowerIsBetter, ["rows"] = rows };
   }

   /// <summary>
   /// True when the first engine is ordered over the second by the claim rule in both sessions (the order list is median order, so
   /// the first is the better one when it is listed first).
   /// </summary>
   private static bool Ordered( Dictionary<string, ( double[] V7, double[] V8 )> figures, List<string> order, string a, string b, bool lowerIsBetter )
   {
      string better = order.IndexOf( a ) < order.IndexOf( b ) ? a : b;
      string worse = better == a ? b : a;
      bool Separated( double[] good, double[] bad ) => lowerIsBetter
         ? Math.Floor( 10000 * bad.Min() / good.Max() ) >= 10000 + T_BP
         : Math.Floor( 10000 * good.Min() / bad.Max() ) >= 10000 + T_BP;
      return Separated( figures[better].V7, figures[worse].V7 ) && Separated( figures[better].V8, figures[worse].V8 );
   }

   /// <summary>
   /// The two sessions' run figures around a median.
   /// </summary>
   private static ( double[] V7, double[] V8 ) Runs( double median )
   {
      return ( V7_SCATTER.Select( s => Math.Round( median * s, 4 ) ).ToArray(), V8_SCATTER.Select( s => Math.Round( median * s, 4 ) ).ToArray() );
   }

   /// <summary>
   /// The median of a list.
   /// </summary>
   private static double Median( double[] values )
   {
      double[] sorted = values.OrderBy( v => v ).ToArray();
      return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : ( sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2] ) / 2;
   }

   /// <summary>
   /// One recall row.
   /// </summary>
   private static JsonObject Recall( string target, int hits )
   {
      return new JsonObject
      {
         ["target"] = target,
         ["hits"] = new JsonObject { ["v7"] = new JsonArray( hits, hits, hits ), ["v8"] = new JsonArray( hits, hits, hits == 200 ? 200 : 197 ) },
         ["of"] = 200,
         ["differs"] = hits != 200,
      };
   }

   /// <summary>
   /// A number as a JSON node, or a JSON null.
   /// </summary>
   private static JsonNode? Num( double? value )
   {
      return value == null ? null : JsonValue.Create( value.Value );
   }

   /// <summary>
   /// One row of the facts and costs table.
   /// </summary>
   private static JsonObject Why( string target )
   {
      string mode = SearchMode( target );
      return new JsonObject
      {
         ["target"] = target,
         ["facts"] = new JsonArray( new JsonObject { ["kind"] = "index", ["text"] = "HNSW graph, approximate search", ["source"] = $"results:targets[{target}].index#HNSW", ["confidence"] = "recorded" },
            new JsonObject
            {
               ["kind"] = "search",
               ["text"] = mode == "exact" ? "no index used, exact scan by design" : "the search walked the graph",
               ["source"] = $"results:targets[{target}].indexState.afterLoad.detail#graph",
               ["confidence"] = target == OWN_REPORT_TARGET ? OWN_REPORT_LABEL : "measured",
               ["mode"] = mode,
               ["where"] = $"in all 6 run(s) selected, at targets[{target}].indexState.afterLoad.detail",
            } ),
         ["costs"] = new JsonObject
         {
            ["engineCpuMsPerSearch"] = new JsonObject { ["1"] = Num( target == "sqlitevec" ? null : 0.31 ), ["8"] = Num( target == "sqlitevec" ? null : 0.29 ) },
            ["clientCpuMsPerSearch"] = new JsonObject { ["1"] = 0.57, ["8"] = 0.36 },
            ["engineCpusBusyAt8"] = 1.7,
         },
      };
   }

   /// <summary>
   /// The search mode of a target in the fixture: exact for the three exact-scan engines, approximate for the rest.
   /// </summary>
   private static string SearchMode( string target )
   {
      return EXACT_TARGETS.Contains( target ) ? "exact" : "approximate";
   }

   /// <summary>
   /// One setup split in the shape the consolidate command writes it: the changed field with its text before and after, the runs on each side, the largest
   /// one-engine and pair moves across the change, and the threshold the change would give.
   /// </summary>
   private static JsonObject SetupSplit( string target, string field, string before, string after, int oneBp, int pairBp, int tBp )
   {
      JsonObject Move( string metric, int bp, string who, bool pair ) => new()
      {
         ["metric"] = metric, ["moveBp"] = bp, ["move"] = bp / 10000.0, ["target"] = pair ? null : who, ["pair"] = pair ? who : null,
         ["runs"] = new JsonArray( "20261005-073329-eshoponweb", "20261006-130619-eshoponweb" ), ["values"] = new JsonArray( 495.78, 374.79 ),
         ["differences"] = new JsonArray( "turbo: turbo not pinned vs turbo off", "build: v6-final vs v7-final" ),
      };

      return new JsonObject
      {
         ["target"] = target,
         ["fields"] = new JsonArray( field ),
         ["changes"] = new JsonArray( new JsonObject { ["field"] = field, ["before"] = before, ["after"] = after } ),
         ["before"] = new JsonObject { ["sessions"] = new JsonArray( "v6" ), ["runs"] = new JsonArray( "20261005-073329-eshoponweb", "20261005-085448-eshoponweb", "20261005-101815-eshoponweb" ) },
         ["after"] = new JsonObject { ["sessions"] = new JsonArray( "v7", "v8" ), ["runs"] = new JsonArray( V7_RUNS.Concat( V8_RUNS ).Select( r => (JsonNode)JsonValue.Create( r )! ).ToArray() ) },
         ["oneEngine"] = Move( "qps8", oneBp, target, false ),
         ["pair"] = Move( "qps8", pairBp, target + "/mongodb", true ),
         ["perMetric"] = new JsonObject(),
         ["tBpIfCounted"] = tBp,
      };
   }

   /// <summary>
   /// The threshold basis block.
   /// </summary>
   private static JsonObject Basis()
   {
      var move = new JsonObject { ["moveBp"] = 2908, ["target"] = "mongodb", ["runs"] = new JsonArray( "20261005-023459-eshoponweb", "20261006-130619-eshoponweb" ), ["values"] = new JsonArray( 2066.0, 2668.9 ), ["differences"] = new JsonArray( "turbo: on, off", "uncore: not pinned, 0x1e1e" ) };
      return new JsonObject
      {
         ["runs"] = new JsonArray( new JsonObject { ["folder"] = "20261005-023459-eshoponweb", ["seed"] = 501, ["session"] = "v5", ["conditions"] = new JsonObject { ["turbo"] = "on", ["uncore"] = "not pinned" } },
            new JsonObject { ["folder"] = V7_RUNS[0], ["seed"] = 701, ["session"] = "v7", ["conditions"] = new JsonObject { ["turbo"] = "off", ["uncore"] = "0x1e1e" } } ),
         ["leftOut"] = new JsonArray( new JsonObject { ["folder"] = "20261003-183955-eshoponweb", ["reason"] = "machine control off" } ),
         ["exclusions"] = new JsonArray( new JsonObject { ["target"] = "clickhouse", ["seeds"] = new JsonArray( 501, 502, 503 ), ["metric"] = "*", ["kind"] = "unrecorded config change", ["why"] = "log tables switched off", ["source"] = "design/verdicts/v6-verdict.md" } ),
         ["perMetric"] = new JsonObject { ["p50Ms"] = new JsonObject { ["oneEngine"] = move.DeepClone(), ["pair"] = move.DeepClone() } },
         ["noMachineControl"] = new JsonObject { ["oneEngine"] = new JsonObject { ["moveBp"] = 11280, ["target"] = "redis", ["runs"] = new JsonArray( "a", "b" ), ["values"] = new JsonArray( 1.0, 2.0 ), ["differences"] = new JsonArray() } },
         ["setupSplitCount"] = 2,
         ["setupSplits"] = new JsonArray(
            SetupSplit( "mariadb", "durability", "innodb_flush_log_at_trx_commit=2 (mariadb.compose.yaml)", "innodb_flush_log_at_trx_commit=2 (set in mariadb-bench.compose.yaml)", 409, 1785, 3500 ),
            SetupSplit( "sqlitevec", "index", "vec0 brute-force scan, default chunk", "vec0 brute-force scan, chunk_size=1024", 11936, 15508, 16500 ) ),
         ["maxBp"] = 2908,
         ["tBp"] = T_BP,
      };
   }

   /// <summary>
   /// The drift block.
   /// </summary>
   private static JsonObject Drift()
   {
      return new JsonObject
      {
         ["from"] = "v7",
         ["to"] = "v8",
         ["perTarget"] = new JsonArray( ENGINES.SelectMany( e => new[] { "p50Ms", "qps1", "qps8" }.Select( m => (JsonNode)new JsonObject { ["target"] = e.Target, ["metric"] = m, ["moveBp"] = 150 } ) ).ToArray() ),
         ["medianAbsMoveBp"] = 150,
         ["largest"] = new JsonObject { ["target"] = "sqlitevec", ["metric"] = "qps8", ["moveBp"] = 390 },
         ["unconfirmedOrders"] = new JsonArray( new JsonObject { ["metric"] = "p50Ms", ["a"] = "mariadb", ["b"] = "pgvector", ["session"] = "v7" } ),
         ["closeToLine"] = new JsonArray( new JsonObject { ["metric"] = "qps8", ["a"] = "redis", ["b"] = "mariadb", ["minRatioBp"] = 13100 } ),
         ["onLine"] = new JsonArray( new JsonObject { ["metric"] = "p50Ms", ["a"] = "mariadb", ["b"] = "sqlitevec", ["minRatioBp"] = 13513 } ),
      };
   }

   /// <summary>
   /// The clock block with one dropped warning.
   /// </summary>
   private static JsonObject Clock()
   {
      return new JsonObject
      {
         ["perSession"] = new JsonObject
         {
            ["v7"] = new JsonObject { ["pinned"] = true, ["pinnedMhz"] = 3500, ["noTurbo"] = 1, ["uncore"] = "0x1e1e", ["clockOffPasses"] = 0, ["clockUnreadPasses"] = 0, ["legacyParsed"] = true },
            ["v8"] = new JsonObject { ["pinned"] = true, ["pinnedMhz"] = 3500, ["noTurbo"] = 1, ["uncore"] = "0x1e1e", ["clockOffPasses"] = 0, ["clockUnreadPasses"] = 0, ["legacyParsed"] = false },
         },
         ["droppedWarnings"] = new JsonArray( new JsonObject { ["run"] = V7_RUNS[0], ["text"] = "clock off its pinned value during oracle default@8: the engine CPUs averaged 3121 MHz", ["target"] = "oracle", ["pass"] = "default@8", ["engineMedianMhz"] = 3492, ["clientMedianMhz"] = 3492 } ),
      };
   }

   /// <summary>
   /// One sentence for every section the page places.
   /// </summary>
   private static JsonArray Sentences()
   {
      return new JsonArray(
         Sentence( "headline", "Every engine below Redis, which holds its data in memory, took at least 1.35 times as long per search in each session." ),
         Sentence( "subtitle", "Six engines on 524 vectors of 1024 dimensions, 20 questions, one machine." ),
         Sentence( "table.p50Ms", "p50 is the median over every search of one pass." ),
         Sentence( "table.qps1", "Same one-searcher pass as p50." ),
         Sentence( "table.exactP50Ms", "Exact mode differs per engine." ),
         Sentence( "rule.threshold", "A is ahead of B when every run of A beats every run of B by the threshold in each session." ),
         Sentence( "basis.largest", "The largest move seen between two runs of one engine was 29.08 percent." ),
         Sentence( "recall.note", "Recall is counted in hits." ),
         Sentence( "why.framing", "CPU per search is cost summed over every thread. It can exceed the time per search. This test did not isolate causes." ),
         Sentence( "drift.summary", "Figures moved by a median of 1.5 percent between the sessions." ),
         Sentence( "disclosure.clock", "The clock was held at the pinned value." ),
         Sentence( "disclosure.durability.redis", "Redis: " + REDIS_DURABILITY, quote: true ),
         Sentence( "notinreport.targets", "Qdrant (HNSW) is not in this report." ),
         Sentence( "runs.reuse.v7", "The runs of the first session were measured before this set was built and are read where they were written." ),
         Sentence( "method.notes", "Order: targets ran one at a time in a random order.", quote: true ) );
   }

   #endregion Private Methods
}
