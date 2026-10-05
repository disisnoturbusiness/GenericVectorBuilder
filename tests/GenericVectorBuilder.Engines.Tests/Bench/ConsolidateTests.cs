using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The benchmark's consolidate command, run end to end on synthetic result folders: which runs
/// are used and which are dropped (and why), the paired per-run arithmetic, ranks, the flags,
/// and loading result folders written before the method-fix fields existed.
/// Why the command's source is compiled here with Roslyn: the benchmark is a console project
/// this test project does not reference (its project file is not this lane's to change), so
/// the real files under src/GenericVectorBuilder.Bench are compiled and run, not copies.
/// Why assertions read consolidated.json: it is the contract writeups consume, so the tests
/// check the numbers exactly where a reader will find them.
/// </summary>
public sealed partial class ConsolidateTests : IDisposable
{
   #region Data Members

   private const string COMMAND_TYPE = "GenericVectorBuilder.Bench.Report.ConsolidateCommand";
   private static readonly Lazy<Assembly> COMPILED = new( Compile );
   private static readonly string[] SOURCES =
   {
      "Report/RunResult.cs", "Report/RunConditions.cs", "Report/ResultJson.cs", "Report/ConsolidatedMarkdown.cs", "Report/ConsolidateCommand.cs",
      "Stats/ConsolidateMath.cs", "Stats/ConsolidatedReport.cs", "Stats/Consolidator.cs", "Stats/ConsolidateFlags.cs", "Stats/CpuSet.cs", "Stats/BenchMath.cs",
      "Stats/ConsolidateBands.cs", "Stats/ConsolidateFraming.cs", "Stats/ConsolidateIdentity.cs", "Stats/ConsolidateEngineNotes.cs", "Stats/SegmentLayout.cs",
   };

   private readonly string _root;
   private readonly List<string> _log = new();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a private folder for this test's synthetic result folders, under the test
   /// binaries (not the system temp folder), removed again in <see cref="Dispose"/>.
   /// </summary>
   public ConsolidateTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "consolidate-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// A run missing a listed target, a run where a listed target failed, a folder with no
   /// results.json and a folder with broken JSON are all dropped, each with its reason in the
   /// log and in consolidated.json; only the complete run is used.
   /// </summary>
   [Fact]
   public void DropsIncompleteRuns_AndSaysWhy()
   {
      WriteRun( "20261005-100000-x", "2026-10-05T10:00:00Z", Target( "sql", 6, 900, 180 ), Target( "qdrant", 2, 3000, 400 ) );
      WriteRun( "20261005-110000-x", "2026-10-05T11:00:00Z", Target( "sql", 6, 900, 180 ) );
      JsonObject failed = Target( "qdrant", 2, 3000, 400 );
      failed["error"] = "container did not start";
      failed.Remove( "search" );
      WriteRun( "20261005-120000-x", "2026-10-05T12:00:00Z", Target( "sql", 6, 900, 180 ), failed );
      Directory.CreateDirectory( Path.Combine( _root, "20261005-130000-x" ) );
      Directory.CreateDirectory( Path.Combine( _root, "20261005-140000-x" ) );
      File.WriteAllText( Path.Combine( _root, "20261005-140000-x", "results.json" ), "{ not json" );

      JsonElement json = Consolidate( "--targets", "sql,qdrant", Path.Combine( _root, "20261005-*-x" ) );

      Assert.Equal( new[] { "20261005-100000-x" }, Names( json.GetProperty( "runs" ) ) );
      Dictionary<string, string> reasons = json.GetProperty( "dropped" ).EnumerateArray().ToDictionary( d => d.GetProperty( "name" ).GetString()!, d => d.GetProperty( "reason" ).GetString()! );
      Assert.Equal( 4, reasons.Count );
      Assert.Equal( "no result for qdrant", reasons["20261005-110000-x"] );
      Assert.Equal( "qdrant failed (container did not start)", reasons["20261005-120000-x"] );
      Assert.Equal( "no results.json in the folder", reasons["20261005-130000-x"] );
      Assert.StartsWith( "results.json is not valid JSON", reasons["20261005-140000-x"] );
      Assert.Contains( _log, l => l == "Dropped 20261005-110000-x: no result for qdrant" );
   }

   /// <summary>
   /// Runs with different settings (here random queries against golden ones) are never mixed:
   /// the largest matching group is used and the odd run is dropped with the differing fields.
   /// </summary>
   [Fact]
   public void DropsRunsWithDifferentSettings()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Target( "sql", 6, 900, 180 ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Target( "sql", 6, 900, 180 ) );
      WriteRun( "r3", "2026-10-05T12:00:00Z", run => { run["queries"] = "random:200 (stored vectors)"; run["queryCount"] = 200; }, Target( "sql", 6, 900, 180 ) );

      JsonElement json = Consolidate( "--targets", "sql", "--runs", Path.Combine( _root, "r{1,2,3}" ) );

      Assert.Equal( new[] { "r1", "r2" }, Names( json.GetProperty( "runs" ) ) );
      string reason = json.GetProperty( "dropped" )[0].GetProperty( "reason" ).GetString()!;
      Assert.Equal( "settings differ from the runs used: queryKind random vs golden, queryCount 200 vs 20", reason );
   }

   /// <summary>
   /// sql-diskann against sql is a ratio taken inside each run and then summarized. The per-run
   /// p50 ratios 0.5, 2.0 and 1.0 have median 1.0, while the ratio of the two separate medians
   /// (3 / 4) would be 0.75; the report must give 1.0.
   /// </summary>
   [Fact]
   public void PairedComparison_IsPerRun_NotRatioOfMedians()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Target( "sql", 4, 400, 100 ), Target( "sql-diskann", 2, 800, 200 ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Target( "sql", 4, 400, 100 ), Target( "sql-diskann", 8, 200, 50 ) );
      WriteRun( "r3", "2026-10-05T12:00:00Z", Target( "sql", 3, 300, 100 ), Target( "sql-diskann", 3, 300, 100 ) );

      JsonElement json = Consolidate( "--targets", "sql,sql-diskann", "--pairs", "sql-diskann:sql", Path.Combine( _root, "r*" ) );

      JsonElement pair = Assert.Single( json.GetProperty( "pairs" ).EnumerateArray() );
      Assert.Equal( "sql-diskann", pair.GetProperty( "a" ).GetString() );
      Assert.Equal( new double?[] { 0.5, 2.0, 1.0 }, PerRun( pair.GetProperty( "p50Ratio" ) ) );
      Assert.Equal( 1.0, pair.GetProperty( "p50Ratio" ).GetProperty( "median" ).GetDouble(), 9 );
      Assert.Equal( 0.5, pair.GetProperty( "p50Ratio" ).GetProperty( "min" ).GetDouble(), 9 );
      Assert.Equal( 2.0, pair.GetProperty( "p50Ratio" ).GetProperty( "max" ).GetDouble(), 9 );
      Assert.Equal( 1, pair.GetProperty( "runsALowerP50" ).GetInt32() );
      Assert.Equal( new double?[] { 2.0, 0.5, 1.0 }, PerRun( pair.GetProperty( "qpsRatio" ).GetProperty( "8" ) ) );
      Assert.Equal( 1, pair.GetProperty( "runsAHigherQps" ).GetProperty( "8" ).GetInt32() );
      Assert.Equal( JsonValueKind.Null, pair.GetProperty( "runsARanFirst" ).ValueKind );
   }

   /// <summary>
   /// The QPS@8/QPS@1 ratio is taken in each run: per-run ratios 8, 2 and 6 have median 6,
   /// while the ratio of the medians (400 / 100) would be 4. Median, min and max of the raw
   /// metrics come from the same per-run values.
   /// </summary>
   [Fact]
   public void QpsRatio_IsPerRun_AndSpreadsAreRight()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Target( "pg", 2.0, 800, 100 ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Target( "pg", 4.0, 400, 200 ) );
      WriteRun( "r3", "2026-10-05T12:00:00Z", Target( "pg", 3.0, 300, 50 ) );
      WriteRun( "r4", "2026-10-05T13:00:00Z", Target( "pg", 5.0, 200, 100 ) );

      JsonElement json = Consolidate( "--targets", "pg", "--runs", $"{Path.Combine( _root, "r1" )},{Path.Combine( _root, "r{2,3}" )}" );

      JsonElement pg = json.GetProperty( "targetSummaries" )[0];
      Assert.Equal( new double?[] { 8, 2, 6 }, PerRun( pg.GetProperty( "qpsRatio" ).GetProperty( "8/1" ) ) );
      Assert.Equal( 6, pg.GetProperty( "qpsRatio" ).GetProperty( "8/1" ).GetProperty( "median" ).GetDouble(), 9 );
      Assert.Equal( 3, pg.GetProperty( "p50Ms" ).GetProperty( "median" ).GetDouble(), 9 );
      Assert.Equal( 2, pg.GetProperty( "p50Ms" ).GetProperty( "min" ).GetDouble(), 9 );
      Assert.Equal( 4, pg.GetProperty( "p50Ms" ).GetProperty( "max" ).GetDouble(), 9 );
      Assert.Equal( 3, pg.GetProperty( "p50Ms" ).GetProperty( "n" ).GetInt32() );
      Assert.Equal( 3, json.GetProperty( "runs" ).GetArrayLength() );
   }

   /// <summary>
   /// Exact against default p50 is paired per run, counts the runs where exact was slower,
   /// and reads from the recorded pass order whether the exact pass ran first (missing when the
   /// run did not record the order).
   /// </summary>
   [Fact]
   public void ExactVsDefault_IsPerRun_WithPassOrder()
   {
      JsonObject a = Target( "maria", 2.0, 4000, 500, exactP50: 3.0 );
      a["passOrder"] = new JsonArray( "exact", "default@1", "default@8" );
      JsonObject b = Target( "maria", 2.0, 4000, 500, exactP50: 1.0 );
      b["passOrder"] = new JsonArray( "default@1", "exact", "default@8" );
      WriteRun( "r1", "2026-10-05T10:00:00Z", a );
      WriteRun( "r2", "2026-10-05T11:00:00Z", b );
      WriteRun( "r3", "2026-10-05T12:00:00Z", Target( "maria", 4.0, 4000, 500, exactP50: 5.0 ) );

      JsonElement json = Consolidate( "--targets", "maria", Path.Combine( _root, "r?" ) );

      JsonElement exact = Assert.Single( json.GetProperty( "exactVsDefault" ).EnumerateArray() );
      Assert.Equal( new double?[] { 1.5, 0.5, 1.25 }, PerRun( exact.GetProperty( "ratio" ) ) );
      Assert.Equal( new double?[] { 1.0, -1.0, 1.0 }, PerRun( exact.GetProperty( "differenceMs" ) ) );
      Assert.Equal( 1.25, exact.GetProperty( "ratio" ).GetProperty( "median" ).GetDouble(), 9 );
      Assert.Equal( 2, exact.GetProperty( "runsExactSlower" ).GetInt32() );
      JsonElement[] runs = exact.GetProperty( "runs" ).EnumerateArray().ToArray();
      Assert.True( runs[0].GetProperty( "exactRanFirst" ).GetBoolean() );
      Assert.False( runs[1].GetProperty( "exactRanFirst" ).GetBoolean() );
      Assert.Equal( JsonValueKind.Null, runs[2].GetProperty( "exactRanFirst" ).ValueKind );
   }

   /// <summary>
   /// Ranks are taken inside each run among the listed targets (ties share the best rank and
   /// skip the next), and the median of the per-run ranks is reported.
   /// </summary>
   [Fact]
   public void Ranks_ArePerRun_WithTiesAndMedian()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Target( "a", 1, 900, 100 ), Target( "b", 2, 900, 100 ), Target( "c", 3, 500, 100 ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Target( "a", 3, 100, 100 ), Target( "b", 2, 900, 100 ), Target( "c", 1, 500, 100 ) );

      JsonElement json = Consolidate( "--targets", "a,b,c", Path.Combine( _root, "r*" ) );

      JsonElement[] t = json.GetProperty( "targetSummaries" ).EnumerateArray().ToArray();
      Assert.Equal( new int?[] { 1, 3 }, Ranks( t[0], "qps@8" ) );
      Assert.Equal( new int?[] { 1, 1 }, Ranks( t[1], "qps@8" ) );
      Assert.Equal( new int?[] { 3, 2 }, Ranks( t[2], "qps@8" ) );
      Assert.Equal( 2.0, t[0].GetProperty( "ranks" ).GetProperty( "qps@8" ).GetProperty( "median" ).GetDouble(), 9 );
      Assert.Equal( new int?[] { 1, 3 }, Ranks( t[0], "p50Ms" ) );
      Assert.Equal( new int?[] { 3, 1 }, Ranks( t[2], "p50Ms" ) );
      Assert.Equal( new int?[] { 1, 1 }, Ranks( t[2], "recall" ) );
   }

   /// <summary>
   /// A target whose index was not ready after the load, whose durability is "not stated", or
   /// that had warm-up errors is flagged with the engine's own evidence; a target with ready
   /// state, stated durability and no errors gets no flag. Run seed, target order and pass order
   /// are carried into the report.
   /// </summary>
   [Fact]
   public void Flags_IndexNotReady_DurabilityNotStated_WarmupErrors()
   {
      JsonObject qdrant = Target( "qdrant", 2, 3000, 400 );
      qdrant["indexState"] = new JsonObject { ["afterLoad"] = State( false, 0, 524, "0 of 524 vectors in HNSW segments" ), ["afterSearch"] = State( false, 0, 524, "still 0" ) };
      qdrant["durability"] = "not stated";
      qdrant["warmupErrors"] = 2;
      qdrant["passOrder"] = new JsonArray( "default@1", "exact", "default@8" );
      JsonObject sql = Target( "sql", 6, 900, 180 );
      sql["indexState"] = new JsonObject { ["afterLoad"] = State( true, 524, 524, "full scan" ), ["afterSearch"] = State( true, 524, 524, "full scan" ) };
      sql["durability"] = "full recovery model, log flushed on commit";
      sql["warmupErrors"] = 0;
      sql["passOrder"] = new JsonArray( "default@8", "default@1" );
      WriteRun( "r1", "2026-10-05T10:00:00Z", run => { run["runSeed"] = 42; run["targetOrder"] = new JsonArray( "qdrant", "sql" ); }, sql, qdrant );

      JsonElement json = Consolidate( "--targets", "sql,qdrant", Path.Combine( _root, "r1" ) );

      JsonElement[] flags = json.GetProperty( "flags" ).EnumerateArray().ToArray();
      Assert.All( flags, f => Assert.Equal( "qdrant", f.GetProperty( "target" ).GetString() ) );
      Dictionary<string, string> byKind = flags.ToDictionary( f => f.GetProperty( "kind" ).GetString()!, f => f.GetProperty( "detail" ).GetString()! );
      Assert.Equal( "ready false, 0 of 524: 0 of 524 vectors in HNSW segments", byKind["index-not-ready-after-load"] );
      Assert.Equal( "not stated", byKind["durability-not-stated"] );
      Assert.Equal( "2 warm-up errors", byKind["warmup-errors"] );
      Assert.True( byKind.ContainsKey( "index-not-ready-after-search" ) );
      Assert.Equal( 4, flags.Length );
      JsonElement run = json.GetProperty( "runs" )[0];
      Assert.Equal( 42, run.GetProperty( "runSeed" ).GetInt32() );
      Assert.Equal( new[] { "qdrant", "sql" }, run.GetProperty( "targetOrder" ).EnumerateArray().Select( e => e.GetString() ).ToArray() );
      JsonElement facts = json.GetProperty( "targetSummaries" )[1].GetProperty( "perRun" )[0];
      Assert.Equal( 1, facts.GetProperty( "orderInRun" ).GetInt32() );
      Assert.Equal( "exact", facts.GetProperty( "passOrder" )[1].GetString() );
      Assert.Equal( 2, json.GetProperty( "targetSummaries" )[1].GetProperty( "warmupErrors" ).GetInt32() );
   }

   /// <summary>
   /// A result folder written before the method-fix fields existed still loads: the fields read
   /// as null in consolidated.json, print as "missing" in consolidated.md, and the target gets
   /// one fields-missing flag. Its numbers are still used.
   /// </summary>
   [Fact]
   public void OldResultFolders_LoadWithFieldsMissing()
   {
      WriteRun( "old", "2026-10-03T18:44:45Z", Legacy, Old( Target( "sql", 5.5, 800, 190 ) ) );

      JsonElement json = Consolidate( "--targets", "sql", "--out", Path.Combine( _root, "out" ), Path.Combine( _root, "old" ) );

      Assert.Equal( JsonValueKind.Null, json.GetProperty( "runs" )[0].GetProperty( "runSeed" ).ValueKind );
      Assert.Equal( JsonValueKind.Null, json.GetProperty( "runs" )[0].GetProperty( "targetOrder" ).ValueKind );
      JsonElement facts = json.GetProperty( "targetSummaries" )[0].GetProperty( "perRun" )[0];
      foreach( string field in new[] { "passOrder", "warmupErrors", "afterLoad", "afterSearch", "durability", "orderInRun" } )
      {
         Assert.Equal( JsonValueKind.Null, facts.GetProperty( field ).ValueKind );
      }

      JsonElement flag = Assert.Single( json.GetProperty( "flags" ).EnumerateArray() );
      Assert.Equal( "fields-missing", flag.GetProperty( "kind" ).GetString() );
      Assert.Equal( "targetOrder, passOrder, warmupErrors, indexState.afterLoad, indexState.afterSearch, durability, settled, "
         + "conditions.buildConfiguration, conditions.governor, conditions.clientCpus, conditions.engineCpus, conditions.warmupSearches, conditions.exactSeconds", flag.GetProperty( "detail" ).GetString() );
      Assert.Equal( 5.5, json.GetProperty( "targetSummaries" )[0].GetProperty( "p50Ms" ).GetProperty( "median" ).GetDouble(), 9 );
      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "| sql | old | missing | missing | missing | 0 | missing | missing | missing | missing | missing | - | missing | - | missing | 5.26 (1000 / QPS@1) |", md );
      Assert.Contains( "| CPU governor | missing |", md );
      Assert.DoesNotContain( "\u2014", md );
   }

   /// <summary>
   /// A bad command line exits 2 without writing; a set of folders where no run has every
   /// target exits 1 without writing and names each run's reason.
   /// </summary>
   [Fact]
   public void Failures_ExitNonZero_AndWriteNothing()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Target( "sql", 6, 900, 180 ) );
      string output = Path.Combine( _root, "out" );

      Assert.Equal( 2, Run( "--runs", Path.Combine( _root, "r1" ) ) );
      Assert.Equal( 2, Run( "--targets", "sql", Path.Combine( _root, "nothing-*" ) ) );
      Assert.Equal( 2, Run( "--targets", "sql,sql", Path.Combine( _root, "r1" ) ) );
      Assert.Equal( 2, Run( "--targets", "sql", "--pairs", "sql", Path.Combine( _root, "r1" ) ) );
      Assert.Equal( 1, Run( "--targets", "sql,qdrant", "--out", output, Path.Combine( _root, "r1" ) ) );
      Assert.Contains( _log, l => l.Contains( "[r1: no result for qdrant]", StringComparison.Ordinal ) );
      Assert.False( Directory.Exists( output ) );
   }

   /// <summary>
   /// Brace alternatives expand shell style, nested ones too, and commas inside braces do not
   /// split a --runs list.
   /// </summary>
   [Fact]
   public void BraceExpansion_MatchesTheShell()
   {
      Type command = COMPILED.Value.GetType( COMMAND_TYPE )!;
      var expand = (IReadOnlyList<string>)command.GetMethod( "ExpandBraces" )!.Invoke( null, new object[] { "2026100{3,4}-*-{a,b{1,2}}" } )!;
      Assert.Equal( new[] { "20261003-*-a", "20261003-*-b1", "20261003-*-b2", "20261004-*-a", "20261004-*-b1", "20261004-*-b2" }, expand );
      var split = (List<string>)command.GetMethod( "SplitTopLevel", BindingFlags.NonPublic | BindingFlags.Static )!.Invoke( null, new object[] { "x{1,2},y" } )!;
      Assert.Equal( new[] { "x{1,2}", "y" }, split );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs the command and returns consolidated.json, asserting it succeeded.
   /// </summary>
   /// <param name="args">Arguments after "consolidate" (an --out is added when absent).</param>
   /// <returns>The root of consolidated.json.</returns>
   private JsonElement Consolidate( params string[] args )
   {
      string output = Path.Combine( _root, "out" );
      string[] full = args.Contains( "--out" ) ? args : args.Concat( new[] { "--out", output } ).ToArray();
      int exit = Run( full );
      Assert.True( exit == 0, $"exit {exit}: {string.Join( Environment.NewLine, _log )}" );
      string folder = full[Array.IndexOf( full, "--out" ) + 1];
      Assert.True( File.Exists( Path.Combine( folder, "consolidated.md" ) ) );
      return JsonDocument.Parse( File.ReadAllText( Path.Combine( folder, "consolidated.json" ) ) ).RootElement.Clone();
   }

   /// <summary>
   /// Calls ConsolidateCommand.Run.
   /// </summary>
   /// <param name="args">Arguments after "consolidate".</param>
   /// <returns>The exit code.</returns>
   private int Run( params string[] args )
   {
      MethodInfo run = COMPILED.Value.GetType( COMMAND_TYPE )!.GetMethod( "Run" )!;
      Action<string> log = _log.Add;
      return (int)run.Invoke( null, new object[] { args, log, CancellationToken.None } )!;
   }

   /// <summary>
   /// Writes a synthetic results.json with the run-level fields every harness version wrote.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <param name="started">startedUtc.</param>
   /// <param name="targets">Target objects.</param>
   private void WriteRun( string name, string started, params JsonObject[] targets )
   {
      WriteRun( name, started, _ => { }, targets );
   }

   /// <summary>
   /// Writes a synthetic results.json, letting the caller change run-level fields.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <param name="started">startedUtc.</param>
   /// <param name="change">Changes to the run object.</param>
   /// <param name="targets">Target objects.</param>
   private void WriteRun( string name, string started, Action<JsonObject> change, params JsonObject[] targets )
   {
      var run = new JsonObject
      {
         ["startedUtc"] = started,
         ["command"] = "run-all",
         ["machine"] = new JsonObject { ["host"] = "testbox", ["loadAverage"] = "0.10 0.20 0.30" },
         ["pipeline"] = "eshoponweb",
         ["rows"] = 524,
         ["dimension"] = 1024,
         ["queries"] = "golden: 20 labelled questions",
         ["queryCount"] = 20,
         ["top"] = 10,
         ["concurrency"] = new JsonArray( 1, 8 ),
         ["secondsPerLevel"] = 20,
         ["targets"] = new JsonArray( targets.Select( t => (JsonNode)t ).ToArray() ),
      };
      Conditioned( run );
      change( run );
      string folder = Path.Combine( _root, name );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "results.json" ), run.ToJsonString() );
   }

   /// <summary>
   /// A synthetic target with every field the current harness writes (settled, search settings).
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="p50">p50 ms (p95 is twice it).</param>
   /// <param name="qps8">QPS at 8.</param>
   /// <param name="qps1">QPS at 1.</param>
   /// <param name="exactP50">Exact-mode p50, or null for no exact mode.</param>
   /// <returns>The target object.</returns>
   private static JsonObject Target( string name, double p50, double qps8, double qps1, double? exactP50 = null )
   {
      var search = new JsonObject
      {
         ["p50Ms"] = p50,
         ["p95Ms"] = p50 * 2,
         ["qps"] = new JsonObject { ["1"] = qps1, ["8"] = qps8 },
         ["recall"] = 1.0,
         ["ndcg"] = 0.5,
         ["errors"] = 0,
         ["exactQueries"] = exactP50.HasValue ? 20 : 0,
      };
      if( exactP50.HasValue )
      {
         search["exactP50Ms"] = exactP50.Value;
         search["exactRecall"] = 1.0;
      }

      return new JsonObject
      {
         ["name"] = name,
         ["engine"] = name + " 1.0",
         ["index"] = "test index",
         ["hosting"] = "compose",
         ["load"] = new JsonObject { ["rows"] = 524, ["rowsPerSecond"] = 1000.0 },
         ["search"] = search,
         ["settled"] = true,
         ["searchSettings"] = new JsonObject { ["ef"] = 100 },
      };
   }

   /// <summary>
   /// An index state object as the method-fix harness writes it.
   /// </summary>
   /// <param name="ready">Ready.</param>
   /// <param name="indexed">Indexed vectors.</param>
   /// <param name="total">Total vectors.</param>
   /// <param name="detail">Evidence text.</param>
   /// <returns>The object.</returns>
   private static JsonObject State( bool ready, long indexed, long total, string detail )
   {
      return new JsonObject { ["ready"] = ready, ["indexedVectors"] = indexed, ["totalVectors"] = total, ["detail"] = detail };
   }

   /// <summary>
   /// The "name" of each item of an array.
   /// </summary>
   /// <param name="array">Array of objects.</param>
   /// <returns>The names.</returns>
   private static string[] Names( JsonElement array )
   {
      return array.EnumerateArray().Select( e => e.GetProperty( "name" ).GetString()! ).ToArray();
   }

   /// <summary>
   /// A spread's per-run values.
   /// </summary>
   /// <param name="spread">The spread object.</param>
   /// <returns>Values, null where missing.</returns>
   private static double?[] PerRun( JsonElement spread )
   {
      return spread.GetProperty( "perRun" ).EnumerateArray().Select( e => e.ValueKind == JsonValueKind.Null ? (double?)null : Math.Round( e.GetDouble(), 9 ) ).ToArray();
   }

   /// <summary>
   /// A target's per-run ranks for a metric.
   /// </summary>
   /// <param name="target">Target summary.</param>
   /// <param name="metric">Metric key.</param>
   /// <returns>Ranks.</returns>
   private static int?[] Ranks( JsonElement target, string metric )
   {
      return target.GetProperty( "ranks" ).GetProperty( metric ).GetProperty( "perRun" ).EnumerateArray().Select( e => e.ValueKind == JsonValueKind.Null ? (int?)null : e.GetInt32() ).ToArray();
   }

   /// <summary>
   /// Compiles the consolidate sources from the repository into an in-memory assembly.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string bench = Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench" );
      var trees = SOURCES.Select( s => CSharpSyntaxTree.ParseText( File.ReadAllText( Path.Combine( bench, s ) ), path: s ) ).ToList();
      trees.Add( CSharpSyntaxTree.ParseText( "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;" ) );
      IEnumerable<MetadataReference> references = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! )
         .Split( Path.PathSeparator ).Select( p => MetadataReference.CreateFromFile( p ) );
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "ConsolidateUnderTest", trees, references, options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file.
   /// </summary>
   /// <returns>The repository root.</returns>
   private static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found." );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes this test's synthetic folders.
   /// </summary>
   public void Dispose()
   {
      try
      {
         Directory.Delete( _root, recursive: true );
      }
      catch( IOException )
      {
         // A leftover folder under the test binaries is harmless; the next build's clean removes it.
      }
   }

   #endregion IDisposable
}
