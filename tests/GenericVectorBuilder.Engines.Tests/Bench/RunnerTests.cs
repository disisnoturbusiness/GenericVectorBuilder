using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The benchmark runner's method rules, checked against the real runner code with fake targets:
/// the seeded shuffle of target and pass order is repeatable and really shuffles, every timed
/// pass gets its own warm-up right before it, warm-up failures are counted, the index step is
/// called and the index state read on both sides of the searches, a not-ready index is measured
/// but flagged, and run-all stops only the engines it started.
/// Why these rules get tests: each one closes a hole that killed a published comparison (fixed
/// order, uncounted warm-up errors, unfinished indexes), and each is easy to undo by accident.
/// Why the sources are compiled here with Roslyn: the benchmark is a console project this test
/// project does not reference. Compiling the repository's Bench sources together with
/// RunnerScenarios.cs (fakes, built only in this compilation) tests the code the benchmark runs.
/// What it also checks, for the engine record: the order of the reads and the ClickHouse reset around the load, the
/// settings-only path, an image that is not the pinned one, the placeholders for targets with nothing to read, the disk
/// growth rule, the results.json field names, the writer's temporary files, and the build and boot stamp.
/// </summary>
public class RunnerTests
{
   #region Data Members

   private const string SCENARIOS_TYPE = "GenericVectorBuilder.Bench.UnderTest.RunnerScenarios";
   private const string OPTIONS_TYPE = "GenericVectorBuilder.Bench.Cli.BenchOptions";
   private const string IMPLICIT_USINGS = "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; "
      + "global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;";
   private static readonly Regex WARMUP_START = new( @"warm-up before (\S+), \d+ searches$", RegexOptions.Compiled );
   private static readonly Lazy<Assembly> COMPILED = new( Compile );
   private static readonly string[] ENGINES = { "sql", "sql-diskann", "qdrant", "qdrant-hnsw", "pgvector", "milvus", "redis" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The same seed always gives the same target order, the order holds every target once, and
   /// the requested list is not changed.
   /// </summary>
   [Fact]
   public void TargetOrder_SameSeedSameOrder()
   {
      foreach( int seed in new[] { 0, 1, 42, -5, int.MaxValue, int.MinValue } )
      {
         string[] names = ENGINES.ToArray();
         string[] first = TargetOrder( names, seed );
         Assert.Equal( first, TargetOrder( names, seed ) );
         Assert.Equal( ENGINES.OrderBy( n => n ), first.OrderBy( n => n ) );
         Assert.Equal( ENGINES, names );
      }
   }

   /// <summary>
   /// Different seeds really shuffle: over 200 seeds all six orders of three targets appear, and
   /// each target goes first a fair share of the time (about 67 of 200).
   /// </summary>
   [Fact]
   public void TargetOrder_ShufflesAcrossSeeds()
   {
      string[] names = { "a", "b", "c" };
      List<string> orders = Enumerable.Range( 0, 200 ).Select( seed => string.Join( ",", TargetOrder( names, seed ) ) ).ToList();
      Assert.Equal( 6, orders.Distinct().Count() );
      foreach( string name in names )
      {
         Assert.InRange( orders.Count( o => o.StartsWith( name, StringComparison.Ordinal ) ), 40, 100 );
      }
   }

   /// <summary>
   /// A recorded seed repeats its order on any later build. Why pinned values: results.json keeps
   /// runSeed so an order can be replayed, which only works if the shuffle never changes. The
   /// values were also worked out by a separate Python copy of SplitMix64 and Fisher-Yates.
   /// </summary>
   [Fact]
   public void Orders_ArePinnedForARecordedSeed()
   {
      Assert.Equal( new[] { "sql-diskann", "sql", "milvus", "redis", "qdrant", "pgvector", "qdrant-hnsw" }, TargetOrder( ENGINES, 20261004 ) );
      Assert.Equal( new[] { "exact", "default@1", "default@8" }, PassOrder( new[] { 1, 8 }, true, 20261004, "qdrant" ) );
   }

   /// <summary>
   /// Pass order depends only on the seed and the target's name, covers default search at
   /// concurrency 1 (even when 1 is not a level), every other level and the exact mode, and
   /// across seeds each pass comes first sometimes, so no pass is always timed warm.
   /// </summary>
   [Fact]
   public void PassOrder_ShufflesPerTargetAndSeed()
   {
      Assert.Equal( PassOrder( new[] { 1, 8 }, true, 7, "qdrant" ), PassOrder( new[] { 1, 8 }, true, 7, "QDRANT" ) );
      Assert.Equal( new[] { "default@1", "default@4", "default@8" }, PassOrder( new[] { 4, 8 }, false, 7, "x" ).OrderBy( p => p ) );
      List<string> firsts = Enumerable.Range( 0, 100 ).Select( seed => PassOrder( new[] { 1, 8 }, true, seed, "qdrant" )[0] ).ToList();
      Assert.Equal( new[] { "default@1", "default@8", "exact" }, firsts.Distinct().OrderBy( p => p ) );
      bool differsByTarget = Enumerable.Range( 0, 20 ).Any( seed => !PassOrder( new[] { 1, 8 }, true, seed, "qdrant" ).SequenceEqual( PassOrder( new[] { 1, 8 }, true, seed, "pgvector" ) ) );
      Assert.True( differsByTarget, "every target got the same pass order for 20 seeds" );
   }

   /// <summary>
   /// --seed is read as any whole number and is absent unless given.
   /// </summary>
   [Fact]
   public void Options_ReadSeed()
   {
      Assert.Equal( -7, Seed( "bench", "--pipeline", "p", "--targets", "sql", "--seed", "-7" ) );
      Assert.Null( Seed( "bench", "--pipeline", "p", "--targets", "sql" ) );
      var ex = Assert.Throws<TargetInvocationException>( () => Seed( "bench", "--pipeline", "p", "--targets", "sql", "--seed", "x" ) );
      Assert.IsType<ArgumentException>( ex.InnerException );
   }

   /// <summary>
   /// Every timed pass is preceded immediately by its own warm-up in the same search mode, in a
   /// run where the exact mode goes first (the order the old runner never produced).
   /// </summary>
   [Fact]
   public async Task Search_WarmsUpBeforeEveryPass()
   {
      int seed = Enumerable.Range( 0, 1000 ).First( s => PassOrder( new[] { 1, 4 }, true, s, "fake" )[0] == "exact" );
      dynamic result = await ScenarioAsync( "SearchAsync", 5, seed, 0 );
      string[] passes = result.PassOrder;
      Assert.Equal( PassOrder( new[] { 1, 4 }, true, seed, "fake" ), passes );
      Assert.Equal( "exact", passes[0] );
      AssertWarmupBeforeEveryPass( (string[])result.Events, passes, 5 );
      Assert.Equal( 0, (int)result.WarmupErrors );
      Assert.Equal( 0, (int)result.TimedErrors );
   }

   /// <summary>
   /// Failed warm-up searches are counted as warm-up errors and kept out of the timed errors.
   /// </summary>
   [Fact]
   public async Task Search_CountsWarmupErrors()
   {
      dynamic result = await ScenarioAsync( "SearchAsync", 5, 3, 3 );
      AssertWarmupBeforeEveryPass( (string[])result.Events, (string[])result.PassOrder, 5 );
      Assert.Equal( 3, (int)result.WarmupErrors );
      Assert.Equal( 0, (int)result.TimedErrors );
      Assert.Contains( ( (string[])result.Events ), e => e.Contains( "3 of 5 warm-up searches before" ) && e.Contains( "FAILED" ) );
   }

   /// <summary>
   /// run-all leaves an engine that was running before the run running (even one that went down
   /// mid-run and had to be started), and stops each engine it started, once per target, also
   /// when two targets share one compose file.
   /// </summary>
   [Fact]
   public async Task RunAll_StopsOnlyWhatItStarted()
   {
      var targets = new (string, string)[] { ( "a", "a.yaml" ), ( "b", "b.yaml" ), ( "c", "b.yaml" ), ( "d", "d.yaml" ) };
      dynamic result = await ScenarioAsync( "RunAllAsync", "run-all", targets, new[] { "a.yaml", "d.yaml" }, new[] { "d.yaml" }, Array.Empty<string>() );
      Assert.Equal( new[] { "up b.yaml", "down b.yaml", "up b.yaml", "down b.yaml", "up d.yaml" }, (string[])result.Calls );
      Assert.Equal( new[] { "a.yaml", "d.yaml" }, (string[])result.RunningAfter );
      Assert.All( (string?[])result.Errors, e => Assert.Null( e ) );
   }

   /// <summary>
   /// With the engine CPUs from machine control, run-all asks for an engine it starts (and stops)
   /// to be created on those CPUs and notes the host's read-back, and starts an engine that was
   /// running before the run but went down before its turn unrestricted, as it found it.
   /// </summary>
   [Fact]
   public async Task RunAll_CreatesEnginesItStartsOnTheEngineCpus()
   {
      var targets = new (string, string)[] { ( "b", "b.yaml" ), ( "d", "d.yaml" ), ( "a", "a.yaml" ) };
      dynamic result = await ScenarioAsync( "RunAllPinnedAsync", targets, new[] { "a.yaml", "d.yaml" }, new[] { "d.yaml" }, true );
      Assert.Equal( new[] { "up b.yaml on 2-3,6-7", "down b.yaml", "up d.yaml" }, (string[])result.Calls );
      string[] notes = result.Notes;
      Assert.Contains( "with every container created on CPUs 2-3,6-7 (b.yaml: 1 container started on CPUs 2-3,6-7 (read back from Docker))", notes[0] );
      Assert.Contains( "started it unrestricted (as it was)", notes[1] );
      Assert.Contains( "already running before the run", notes[2] );
   }

   /// <summary>
   /// A host that cannot create containers on a CPU set (it returns no read-back) is not taken at
   /// its word: the note says the engine was moved there after the start instead.
   /// </summary>
   [Fact]
   public async Task RunAll_SaysWhenTheHostDidNotApplyTheCpus()
   {
      var targets = new (string, string)[] { ( "b", "b.yaml" ) };
      dynamic result = await ScenarioAsync( "RunAllPinnedAsync", targets, Array.Empty<string>(), Array.Empty<string>(), false );
      Assert.Equal( new[] { "up b.yaml on 2-3,6-7", "down b.yaml" }, (string[])result.Calls );
      string note = ( (string[])result.Notes )[0];
      Assert.Contains( "the host did not apply a CPU set at creation, so machine control moved it there after the start", note );
      Assert.DoesNotContain( "with every container created", note );
   }

   /// <summary>
   /// An engine whose start fails half-way is still stopped, and only that target fails.
   /// </summary>
   [Fact]
   public async Task RunAll_StopsAHalfStartedEngine()
   {
      var targets = new (string, string)[] { ( "e", "e.yaml" ), ( "a", "a.yaml" ) };
      dynamic result = await ScenarioAsync( "RunAllAsync", "run-all", targets, new[] { "a.yaml" }, Array.Empty<string>(), new[] { "e.yaml" } );
      Assert.Equal( new[] { "up e.yaml", "down e.yaml" }, (string[])result.Calls );
      Assert.Equal( new[] { "a.yaml" }, (string[])result.RunningAfter );
      string?[] errors = result.Errors;
      Assert.Contains( "Could not start", errors[0] );
      Assert.Null( errors[1] );
   }

   /// <summary>
   /// bench starts and stops nothing: a stopped engine is that target's error, a running one is measured.
   /// </summary>
   [Fact]
   public async Task Bench_StartsNothing()
   {
      var targets = new (string, string)[] { ( "b", "b.yaml" ), ( "a", "a.yaml" ) };
      dynamic result = await ScenarioAsync( "RunAllAsync", "bench", targets, new[] { "a.yaml" }, Array.Empty<string>(), Array.Empty<string>() );
      Assert.Empty( (string[])result.Calls );
      Assert.Equal( new[] { "a.yaml" }, (string[])result.RunningAfter );
      string?[] errors = result.Errors;
      Assert.Contains( "is not running", errors[0] );
      Assert.Null( errors[1] );
   }

   /// <summary>
   /// The index step runs after the last write and before the first search, the index state is
   /// read before the first search and after the last, and results.json carries every field of
   /// the shared contract.
   /// </summary>
   [Fact]
   public async Task Finisher_CalledAndStateRecorded()
   {
      dynamic result = await MeasureOneAsync( true, true );
      string[] events = result.Events;
      int finish = Array.IndexOf( events, "finish" );
      int firstSearch = Array.FindIndex( events, e => e is "S" or "X" );
      int lastSearch = Array.FindLastIndex( events, e => e is "S" or "X" );
      Assert.True( finish > Array.LastIndexOf( events, "upsert" ) && finish < firstSearch, "the index step must run after the load and before searching" );
      Assert.Null( (string?)result.Error );
      Assert.True( Array.IndexOf( events, "state", finish ) < firstSearch && Array.LastIndexOf( events, "state" ) > lastSearch,
         $"state must be read before and after the searches (first search at {firstSearch}, last at {lastSearch} of {events.Length}; events after the last search: {string.Join( ",", events.Skip( lastSearch + 1 ) )}; notes: {string.Join( " | ", (string[])result.Notes )})" );
      using JsonDocument json = JsonDocument.Parse( (string)result.Json );
      JsonElement root = json.RootElement;
      Assert.Equal( 42, root.GetProperty( "runSeed" ).GetInt32() );
      Assert.Equal( "fake", root.GetProperty( "targetOrder" )[0].GetString() );
      JsonElement target = root.GetProperty( "targets" )[0];
      Assert.Equal( new[] { "default@1", "exact" }, target.GetProperty( "passOrder" ).EnumerateArray().Select( p => p.GetString() ).OrderBy( p => p ) );
      Assert.Equal( 0, target.GetProperty( "warmupErrors" ).GetInt32() );
      Assert.Equal( "fsync on every commit (fake)", target.GetProperty( "durability" ).GetString() );
      Assert.True( target.GetProperty( "indexState" ).GetProperty( "afterLoad" ).GetProperty( "ready" ).GetBoolean() );
      Assert.Equal( 20, target.GetProperty( "indexState" ).GetProperty( "afterSearch" ).GetProperty( "indexedVectors" ).GetInt64() );
      Assert.DoesNotContain( (string[])result.Notes, n => n.StartsWith( "WARNING: index", StringComparison.Ordinal ) || n.StartsWith( "WARNING: the index", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A target whose index is not ready after the load is still searched, and flagged.
   /// </summary>
   [Fact]
   public async Task NotReadyIndex_MeasuredAndFlagged()
   {
      dynamic result = await MeasureOneAsync( true, false );
      using JsonDocument json = JsonDocument.Parse( (string)result.Json );
      JsonElement target = json.RootElement.GetProperty( "targets" )[0];
      Assert.Null( (string?)result.Error );
      Assert.True( target.GetProperty( "search" ).GetProperty( "latencySamples" ).GetInt32() > 0 );
      Assert.False( target.GetProperty( "indexState" ).GetProperty( "afterLoad" ).GetProperty( "ready" ).GetBoolean() );
      Assert.Contains( (string[])result.Notes, n => n.StartsWith( "WARNING: index not ready after the load", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A sink that reports no index state counts as not ready, and its unstated durability shows.
   /// </summary>
   [Fact]
   public async Task NoIndexProof_FlaggedNotReady()
   {
      dynamic result = await MeasureOneAsync( false, true );
      using JsonDocument json = JsonDocument.Parse( (string)result.Json );
      JsonElement target = json.RootElement.GetProperty( "targets" )[0];
      JsonElement afterLoad = target.GetProperty( "indexState" ).GetProperty( "afterLoad" );
      Assert.False( afterLoad.GetProperty( "ready" ).GetBoolean() );
      Assert.Contains( "not reported", afterLoad.GetProperty( "detail" ).GetString() );
      Assert.Equal( "not stated", target.GetProperty( "durability" ).GetString() );
      Assert.Equal( new[] { "default@1" }, target.GetProperty( "passOrder" ).EnumerateArray().Select( p => p.GetString() ) );
      Assert.Contains( (string[])result.Notes, n => n.StartsWith( "WARNING: index not ready", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// run-all on ClickHouse: the engine record is made in the order the design needs. The settings are read first, then the data folder's start
   /// size (before any reset), then the log tables are listed, truncated one by one and listed again, then the folder is read until it holds
   /// steady, and only then does the load begin (the first sink call is the drop of the old copy); after the last search the folder is read again
   /// and only then is the copy dropped. results.json carries what each step found.
   /// </summary>
   [Fact]
   public async Task EngineRecord_ClickHouseIsReadAndResetBeforeTheLoadAndMeasuredAgainBeforeTheDrop()
   {
      dynamic run = await RecordsAsync( "clickhouse", false );
      string[] events = run.Events;
      Assert.Null( (string?)run.Error );
      int settings = Array.IndexOf( events, "http settings" );
      int start = Array.IndexOf( events, "du" );
      int listFirst = Array.IndexOf( events, "http list" );
      int truncate = Array.IndexOf( events, "http truncate" );
      int listLast = Array.LastIndexOf( events, "http list" );
      int firstSink = Array.FindIndex( events, e => e is "drop" or "ensure" or "upsert" );
      int lastSearch = Array.FindLastIndex( events, e => e is "S" or "X" );
      int endDu = Array.LastIndexOf( events, "du" );
      int lastDrop = Array.LastIndexOf( events, "drop" );
      Assert.True( settings >= 0 && settings < start && start < listFirst && listFirst < truncate && truncate < listLast, $"order of the reads and the reset: {string.Join( ",", events.Take( 12 ) )}" );
      Assert.Equal( 2, events.Count( e => e == "http truncate" ) );
      Assert.Equal( 2, events.Count( e => e == "http list" ) );
      Assert.True( listLast < Array.IndexOf( events, "du", listLast ) && Array.IndexOf( events, "du", listLast ) < firstSink, "the folder is read after the reset and before the load" );
      Assert.Equal( "drop", events[firstSink] );
      Assert.True( lastSearch < endDu && endDu < lastDrop, "the data folder is measured again after the last search and before the copy is dropped" );

      using JsonDocument json = JsonDocument.Parse( (string)run.Json );
      JsonElement target = json.RootElement.GetProperty( "targets" )[0];
      Assert.Equal( new[] { "HostConfig.Memory", "HostConfig.CpusetCpus", "HostConfig.NanoCpus", "max_threads" }, target.GetProperty( "engineSettings" ).EnumerateArray().Select( e => e.GetProperty( "key" ).GetString() ).ToArray() );
      JsonElement image = target.GetProperty( "image" );
      Assert.Equal( "sha256:6f81e8915c60b065a524e6967e0ad1c639ba6efa84d669f823683ea04d9150ee", image.GetProperty( "id" ).GetString() );
      Assert.Equal( image.GetProperty( "id" ).GetString(), image.GetProperty( "pinnedId" ).GetString() );
      Assert.Equal( "2026-10-03T14:09:34.185138197Z", image.GetProperty( "lastTagTimeUtc" ).GetString() );
      JsonElement folder = target.GetProperty( "dataFolder" );
      Assert.EndsWith( "/gvb-data/engines/clickhouse", folder.GetProperty( "path" ).GetString() );
      Assert.Equal( 7_000_000_000, folder.GetProperty( "bytesAtStart" ).GetInt64() );
      Assert.StartsWith( "truncated 2 MergeTree log tables in database system (metric_log, query_log); ", folder.GetProperty( "reset" ).GetString() );
      Assert.Equal( 150_000_000, folder.GetProperty( "bytesAfterReset" ).GetInt64() );
      Assert.Equal( 150_000_000, folder.GetProperty( "bytesAtEnd" ).GetInt64() );
      Assert.False( folder.TryGetProperty( "benchFileBytes", out _ ) );
      Assert.True( target.TryGetProperty( "search", out _ ) );
      string markdown = run.Markdown;
      Assert.Contains( "- Engine settings: HostConfig.Memory = 8589934592 (read: docker inspect HostConfig.Memory (0 = no limit)); ", markdown );
      Assert.Contains( "max_threads = 'auto(4)' (read: ClickHouse system.settings, name max_threads, over HTTP)", markdown );
      Assert.Contains( "- Image: redis:8.10.2, id sha256:6f81e8915c60b065a524e6967e0ad1c639ba6efa84d669f823683ea04d9150ee (the pinned id), tagged on this machine at 2026-10-03T14:09:34.185138197Z", markdown );
      Assert.Contains( "/gvb-data/engines/clickhouse: 6.52 GiB at the start, 143.05 MiB after the reset, 143.05 MiB at the end; reset at the start: truncated 2 MergeTree log tables", markdown );
   }

   /// <summary>
   /// settings-only: the engine is bound and read, and nothing is loaded, searched, truncated or dropped: no sink call at all, no ClickHouse statement
   /// but the settings read, a data folder with a start and an end size and no reset, no search and no load in results.json, the report marked
   /// settingsOnly, and the target not an error.
   /// </summary>
   [Fact]
   public async Task EngineRecord_SettingsOnlyLoadsSearchesAndResetsNothing()
   {
      dynamic run = await RecordsAsync( "clickhouse", true );
      string[] events = run.Events;
      Assert.Null( (string?)run.Error );
      Assert.Equal( new[] { "http settings", "du", "du" }, events );
      using JsonDocument json = JsonDocument.Parse( (string)run.Json );
      Assert.True( json.RootElement.GetProperty( "settingsOnly" ).GetBoolean() );
      JsonElement target = json.RootElement.GetProperty( "targets" )[0];
      Assert.False( target.TryGetProperty( "search", out _ ) );
      Assert.False( target.TryGetProperty( "load", out _ ) );
      JsonElement folder = target.GetProperty( "dataFolder" );
      Assert.Equal( 7_000_000_000, folder.GetProperty( "bytesAtStart" ).GetInt64() );
      Assert.Equal( 7_000_000_000, folder.GetProperty( "bytesAtEnd" ).GetInt64() );
      Assert.False( folder.TryGetProperty( "reset", out _ ) );
      Assert.False( folder.TryGetProperty( "bytesAfterReset", out _ ) );
      Assert.Contains( (string[])run.Notes, n => n.StartsWith( "Settings-only run: the engine's settings, image and data folder were read; nothing was loaded, searched or reset." ) );
      Assert.Equal( 4, target.GetProperty( "engineSettings" ).GetArrayLength() );
   }

   /// <summary>
   /// Redaction: a redis container shaped like the real one (the compose file's entrypoint is one sh -c string holding --requirepass "$REDIS_PASSWORD",
   /// and all ten values of secrets.env sit in its environment) is read, recorded and written, for both a measured run and a settings-only run. Not one
   /// secret value, and none of the words of the entrypoint, reaches results.json, results.md, a note, the target's error or the trace of calls; the
   /// settings recorded are the three limits and the four Redis reads, no more.
   /// </summary>
   [Fact]
   public async Task EngineRecord_NoSecretAndNoEntrypointTextReachesTheResults()
   {
      foreach( bool settingsOnly in new[] { false, true } )
      {
         dynamic run = await RecordsAsync( "redis", settingsOnly );
         Assert.Null( (string?)run.Error );
         Assert.Equal( 0, (int)run.Leaks );
         using JsonDocument json = JsonDocument.Parse( (string)run.Json );
         JsonElement settings = json.RootElement.GetProperty( "targets" )[0].GetProperty( "engineSettings" );
         Assert.Equal( new[] { "HostConfig.Memory", "HostConfig.CpusetCpus", "HostConfig.NanoCpus", "search-workers", "save", "appendonly", "maxmemory" }, settings.EnumerateArray().Select( e => e.GetProperty( "key" ).GetString() ).ToArray() );
         Assert.Equal( "300 1", settings[4].GetProperty( "value" ).GetString() );
      }
   }

   /// <summary>
   /// A container on another image than the pinned one is that target's error, with the settings and the image (both ids) recorded, nothing loaded
   /// and nothing searched, so a run on a different build is never taken for the pinned one.
   /// </summary>
   [Fact]
   public async Task EngineRecord_AnotherImageThanThePinnedOneEndsTheTargetBeforeTheLoad()
   {
      dynamic run = await RecordsAsync( "mismatch", false );
      string[] events = run.Events;
      Assert.StartsWith( "the container gvb-redis runs image sha256:1e0b73a187a2", (string)run.Error );
      Assert.Contains( "but image-pins.json pins sha256:6f81e8915c60", (string)run.Error );
      Assert.DoesNotContain( events, e => e is "upsert" or "ensure" or "S" or "X" or "du" );
      using JsonDocument json = JsonDocument.Parse( (string)run.Json );
      JsonElement target = json.RootElement.GetProperty( "targets" )[0];
      Assert.Equal( 7, target.GetProperty( "engineSettings" ).GetArrayLength() );
      Assert.Equal( "sha256:1e0b73a187a28757c572acba508c46f48c9e8b0acaf5c20e6d95cdedce1acdf6", target.GetProperty( "image" ).GetProperty( "id" ).GetString() );
      Assert.Equal( "sha256:6f81e8915c60b065a524e6967e0ad1c639ba6efa84d669f823683ea04d9150ee", target.GetProperty( "image" ).GetProperty( "pinnedId" ).GetString() );
      Assert.False( target.TryGetProperty( "dataFolder", out _ ) );
   }

   /// <summary>
   /// An embedded engine with a reader (sqlitevec here, the fake wearing its name) gets its in-process settings, no image, and the sink's default
   /// folder as its data folder with no bench file (none exists), and the fake without a container gets one placeholder entry and neither an image nor a data folder.
   /// </summary>
   [Fact]
   public async Task EngineRecord_EmbeddedEnginesAndTargetsWithNothingToReadAreRecordedAsSuch()
   {
      dynamic embedded = await RecordsAsync( "embedded", false );
      Assert.Null( (string?)embedded.Error );
      using JsonDocument json = JsonDocument.Parse( (string)embedded.Json );
      JsonElement target = json.RootElement.GetProperty( "targets" )[0];
      JsonElement[] settings = target.GetProperty( "engineSettings" ).EnumerateArray().ToArray();
      Assert.Equal( new[] { "sqlite_version", "synchronous", "journal_mode" }, settings.Select( s => s.GetProperty( "key" ).GetString() ).ToArray() );
      Assert.Equal( "WAL", settings[2].GetProperty( "value" ).GetString() );
      Assert.False( target.TryGetProperty( "image", out _ ) );
      Assert.EndsWith( "/gvb-data/engines/sqlitevec", target.GetProperty( "dataFolder" ).GetProperty( "path" ).GetString() );
      Assert.False( target.GetProperty( "dataFolder" ).TryGetProperty( "benchFileBytes", out _ ) );

      dynamic plain = await RecordsAsync( "plain", false );
      using JsonDocument plainJson = JsonDocument.Parse( (string)plain.Json );
      JsonElement fake = plainJson.RootElement.GetProperty( "targets" )[0];
      JsonElement[] only = fake.GetProperty( "engineSettings" ).EnumerateArray().ToArray();
      Assert.Single( only );
      Assert.Equal( "none", only[0].GetProperty( "key" ).GetString() );
      Assert.Equal( "not read", only[0].GetProperty( "value" ).GetString() );
      Assert.Equal( "embedded engine without a settings reader", only[0].GetProperty( "how" ).GetString() );
      Assert.False( fake.TryGetProperty( "image", out _ ) );
      Assert.False( fake.TryGetProperty( "dataFolder", out _ ) );
   }

   /// <summary>
   /// The disk growth rule: growth is the folder's size after the load minus before; a folder that did not change grew by 0; a folder that SHRANK has
   /// no growth that can be told (no bytes, and a text that says by how much it shrank), where the older code printed "0 B added"; no "before" reading
   /// or one without bytes keeps the plain reading.
   /// </summary>
   [Fact]
   public void Growth_ASmallerFolderIsNotMeasuredAndNeverAZero()
   {
      string[] lines = (string[])COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "GrowthCases" )!.Invoke( null, null )!;
      Assert.Equal( "4000000|3.81 MiB added by this load (whole engine data folder, 976.56 KiB before)", lines[0] );
      Assert.Equal( "0|0 B added by this load (whole engine data folder, 4.77 MiB before)", lines[1] );
      Assert.Equal( "null|not measured: the folder was 1.91 MiB smaller after the load than before it, so the load's own growth cannot be told (whole engine data folder, 6.68 MiB before)", lines[2] );
      Assert.Equal( "5000000|whole engine data folder", lines[3] );
      Assert.Equal( "5000000|whole engine data folder", lines[4] );
   }

   /// <summary>
   /// Recall is also written as a whole number of hits: recall x queries x top when that is a whole number (within 1e-6), so two runs differ by one
   /// hit or not at all; the mean over fewer queries than the set, a recall outside 0 to 1, no recall, NaN and an empty query set or top give none.
   /// </summary>
   [Fact]
   public void RecallHits_AreAWholeNumberOrNothing()
   {
      string[] lines = (string[])COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "RecallHitsCases" )!.Invoke( null, null )!;
      Assert.Equal( new[]
      {
         "0.935|20|10|187", "1|20|10|200", "0|20|10|0", "0.925|4|10|37",
         "0.9333|20|10|null", "0.98421|20|10|null", "1.5|20|10|null", "-0.1|20|10|null", "NaN|20|10|null", "null|20|10|null", "0.5|0|10|null", "0.5|20|0|null",
      }, lines );
   }

   /// <summary>
   /// results.json has the contract's field names: engineSettings as { key, value, how }, image as { ref, id, pinnedId, lastTagTimeUtc }, dataFolder
   /// as { path, bytesAtStart, reset, bytesAfterReset, bytesAtEnd, benchFileBytes }, search.recallHits, queriesFileSha256 and settingsOnly at the run level; a record
   /// that was not made is absent (the writer omits nulls), never an empty object, and settingsOnly is absent unless true.
   /// </summary>
   [Fact]
   public void ResultsJson_HasTheContractFieldNamesAndOmitsWhatWasNotRecorded()
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "runner-tests", Guid.NewGuid().ToString( "N" ) );
      try
      {
         using JsonDocument full = JsonDocument.Parse( (string)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "WriteContract" )!.Invoke( null, new object[] { folder, true } )! );
         JsonElement root = full.RootElement;
         Assert.Equal( new string( 'a', 64 ), root.GetProperty( "queriesFileSha256" ).GetString() );
         Assert.True( root.GetProperty( "settingsOnly" ).GetBoolean() );
         JsonElement target = root.GetProperty( "targets" )[0];
         Assert.Equal( new[] { "key", "value", "how" }, target.GetProperty( "engineSettings" )[0].EnumerateObject().Select( p => p.Name ).ToArray() );
         Assert.Equal( new[] { "ref", "id", "pinnedId", "lastTagTimeUtc" }, target.GetProperty( "image" ).EnumerateObject().Select( p => p.Name ).ToArray() );
         Assert.Equal( new[] { "path", "bytesAtStart", "reset", "bytesAfterReset", "bytesAtEnd", "benchFileBytes" }, target.GetProperty( "dataFolder" ).EnumerateObject().Select( p => p.Name ).ToArray() );
         Assert.Equal( 187, target.GetProperty( "search" ).GetProperty( "recallHits" ).GetInt32() );

         using JsonDocument empty = JsonDocument.Parse( (string)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "WriteContract" )!.Invoke( null, new object[] { folder + "-e", false } )! );
         Assert.False( empty.RootElement.TryGetProperty( "queriesFileSha256", out _ ) );
         Assert.False( empty.RootElement.TryGetProperty( "settingsOnly", out _ ) );
         Assert.All( new[] { "engineSettings", "image", "dataFolder" }, name => Assert.False( empty.RootElement.GetProperty( "targets" )[0].TryGetProperty( name, out _ ) ) );
      }
      finally
      {
         foreach( string path in new[] { folder, folder + "-e" }.Where( Directory.Exists ) )
         {
            Directory.Delete( path, true );
         }
      }
   }

   /// <summary>
   /// The writer builds both texts first and moves each file into place from a temporary file: a second write replaces the first, no temporary file
   /// stays in the folder, and a report that cannot be rendered writes nothing and leaves the earlier results.json and results.md whole.
   /// </summary>
   [Fact]
   public void Writer_ReplacesWholeFilesAndLeavesNothingBehind()
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "runner-tests", Guid.NewGuid().ToString( "N" ) );
      try
      {
         string[] lines = (string[])COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "WriterIsAtomic" )!.Invoke( null, new object[] { folder } )!;
         Assert.Equal( new[] { "first|first", "second|second", "files|results.json,results.md", "error|NullReferenceException", "after|second", "files|results.json,results.md" }, lines );
      }
      finally
      {
         if( Directory.Exists( folder ) )
         {
            Directory.Delete( folder, true );
         }
      }
   }

   /// <summary>
   /// Build and boot are added to "conditions" after machine control wrote that object (which it replaces whole on every write): both entries sit
   /// beside what machine control wrote, and a results.json with no conditions is an error, not a silent skip.
   /// </summary>
   [Fact]
   public void Conditions_GetTheBuildAndBootAfterMachineControlWrote()
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "runner-tests", Guid.NewGuid().ToString( "N" ) );
      try
      {
         string[] lines = (string[])COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "AddConditions" )!.Invoke( null, new object[] { folder } )!;
         Assert.Equal( "{\"machineControl\":true,\"governor\":\"performance\",\"build\":{\"informationalVersion\":\"1.0.0+f662f82f1993d2268dc85a55c872f06e556c987d\",\"commit\":\"f662f82f1993d2268dc85a55c872f06e556c987d\"},"
            + "\"boot\":{\"bootId\":\"7bdd3a0a-0c2e-4a2a-8f0b-4a0e0f0a1b2c\",\"bootTimeUtc\":\"2026-10-03T15:53:39Z\"}}", lines[0] );
         Assert.Equal( "error|<folder>/results.json has no \"conditions\" object to add \"build\" to.", lines[1] );
      }
      finally
      {
         if( Directory.Exists( folder ) )
         {
            Directory.Delete( folder, true );
         }
      }
   }

   /// <summary>
   /// The stamp: the commit is what follows the "+" of the informational version when it is a hex hash; a build without one is an error for a real run
   /// and a stamp with no commit otherwise; an empty boot id or a /proc/stat without btime is an error; btime becomes a UTC time.
   /// </summary>
   [Fact]
   public void Stamp_ReadsTheCommitTheBootIdAndTheBootTime()
   {
      const string STAT = "cpu  1 2 3\nbtime 1791042819\nprocesses 5\n";
      Assert.Equal( new[] { "build|{\"informationalVersion\":\"1.0.0+F662F82\",\"commit\":\"f662f82\"}", "boot|{\"bootId\":\"abc\",\"bootTimeUtc\":\"2026-10-03T15:53:39Z\"}" }, Stamp( "1.0.0+F662F82", "abc\n", STAT, true ) );
      Assert.Equal( new[] { "build|{\"informationalVersion\":\"1.0.0\"}", "boot|{\"bootId\":\"abc\",\"bootTimeUtc\":\"2026-10-03T15:53:39Z\"}" }, Stamp( "1.0.0", "abc", STAT, false ) );
      Assert.Equal( new[] { "build|{\"informationalVersion\":\"1.0.0+local build\"}", "boot|{\"bootId\":\"abc\",\"bootTimeUtc\":\"2026-10-03T15:53:39Z\"}" }, Stamp( "1.0.0+local build", "abc", STAT, false ) );
      Assert.Equal( new[] { "error|This build carries no commit (its informational version is '1.0.0+local build'), so a run could not say which code measured it. Build with: dotnet build -c Release -p:SourceRevisionId=$(git rev-parse HEAD)" }, Stamp( "1.0.0+local build", "abc", STAT, true ) );
      Assert.Equal( new[] { "error|/proc/sys/kernel/random/boot_id is empty, so the boot cannot be recorded." }, Stamp( "1.0.0+f662f82", " \n", STAT, true ) );
      Assert.Equal( new[] { "error|/proc/stat has no btime line with a number, so the boot time cannot be recorded." }, Stamp( "1.0.0+f662f82", "abc", "cpu 1\nprocesses 5\n", true ) );
   }

   /// <summary>
   /// "--settings-only" is for run-all and nothing else: on, it is true; off, false; with bench, replicate or clean it is a plain error that says why.
   /// </summary>
   [Fact]
   public void Options_SettingsOnlyIsForRunAllOnly()
   {
      Assert.Equal( "settingsOnly|True", SettingsOnlyOption( new[] { "run-all", "--pipeline", "p", "--settings-only" } ) );
      Assert.Equal( "settingsOnly|False", SettingsOnlyOption( new[] { "run-all", "--pipeline", "p" } ) );
      Assert.Equal( "error|--settings-only is only for run-all, which is the one command that starts engines; bench does not.", SettingsOnlyOption( new[] { "bench", "--pipeline", "p", "--targets", "a", "--settings-only" } ) );
   }

   /// <summary>
   /// The golden file's hash is the SHA-256 of its bytes in lower-case hex (the value of "abc" is the standard test vector).
   /// </summary>
   [Fact]
   public void GoldenHash_IsTheSha256OfTheBytes()
   {
      Assert.Equal( "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", (string)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "GoldenHash" )!.Invoke( null, new object[] { "abc" } )! );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Walks a search trace pass by pass: a warm-up start line naming the pass, exactly the
   /// warm-up searches in the pass's own mode (and possibly a line about failed warm-ups), the
   /// pass's start line, then only that pass's searches until the next warm-up begins.
   /// </summary>
   /// <param name="events">Search calls ("S" default, "X" exact) and "LOG ..." lines, in order.</param>
   /// <param name="passes">Passes in the order the runner reported.</param>
   /// <param name="warmup">Warm-up searches per pass.</param>
   private static void AssertWarmupBeforeEveryPass( string[] events, string[] passes, int warmup )
   {
      Assert.Equal( passes.Length, events.Count( e => WARMUP_START.IsMatch( e ) ) );
      int i = 0;
      foreach( string pass in passes )
      {
         string kind = pass == "exact" ? "X" : "S";
         Assert.Equal( pass, WARMUP_START.Match( events[i] ).Groups[1].Value );
         int warm = 0;
         for( i++; !events[i].StartsWith( "LOG ", StringComparison.Ordinal ); i++, warm++ )
         {
            Assert.Equal( kind, events[i] );
         }

         Assert.Equal( warmup, warm );
         i += events[i].Contains( "warm-up searches before", StringComparison.Ordinal ) ? 1 : 0;
         Assert.EndsWith( $"timing {pass}", events[i] );
         int timed = 0;
         for( i++; i < events.Length && !WARMUP_START.IsMatch( events[i] ); i++ )
         {
            timed += events[i].StartsWith( "LOG ", StringComparison.Ordinal ) ? 0 : 1;
            Assert.True( events[i].StartsWith( "LOG ", StringComparison.Ordinal ) || events[i] == kind, $"{pass} timed a '{events[i]}' search" );
         }

         Assert.True( timed > 0, $"{pass} timed no searches" );
      }

      Assert.Equal( events.Length, i );
   }

   /// <summary>
   /// Runs MeasureOneAsync in a fresh folder under the test output and removes it afterwards.
   /// </summary>
   /// <param name="finisher">True for a sink with an index step.</param>
   /// <param name="ready">Whether its index reports ready after the step.</param>
   /// <returns>The scenario result.</returns>
   private static async Task<dynamic> MeasureOneAsync( bool finisher, bool ready )
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "runner-tests", Guid.NewGuid().ToString( "N" ) );
      try
      {
         return await ScenarioAsync( "MeasureOneAsync", finisher, ready, folder );
      }
      finally
      {
         if( Directory.Exists( folder ) )
         {
            Directory.Delete( folder, true );
         }
      }
   }

   /// <summary>
   /// Measures one fake target with the engine record wired to fakes.
   /// </summary>
   /// <param name="kind">clickhouse, mismatch, embedded or plain.</param>
   /// <param name="settingsOnly">True for the settings-only path.</param>
   /// <returns>The scenario record (Events, Notes, Error, Json).</returns>
   private static async Task<dynamic> RecordsAsync( string kind, bool settingsOnly )
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "runner-tests", Guid.NewGuid().ToString( "N" ) );
      try
      {
         return await ScenarioAsync( "RecordsAsync", kind, settingsOnly, folder );
      }
      finally
      {
         if( Directory.Exists( folder ) )
         {
            Directory.Delete( folder, true );
         }
      }
   }

   /// <summary>RunnerScenarios.Stamp.</summary>
   /// <param name="version">Informational version.</param>
   /// <param name="bootId">boot_id text.</param>
   /// <param name="stat">/proc/stat text.</param>
   /// <param name="requireCommit">Whether a missing commit is an error.</param>
   /// <returns>The lines.</returns>
   private static string[] Stamp( string version, string bootId, string stat, bool requireCommit )
   {
      return (string[])COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "Stamp" )!.Invoke( null, new object[] { version, bootId, stat, requireCommit } )!;
   }

   /// <summary>RunnerScenarios.SettingsOnlyOption.</summary>
   /// <param name="args">Arguments.</param>
   /// <returns>The line.</returns>
   private static string SettingsOnlyOption( string[] args )
   {
      return (string)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "SettingsOnlyOption" )!.Invoke( null, new object[] { args } )!;
   }

   /// <summary>
   /// Calls an async scenario and returns its result.
   /// </summary>
   /// <param name="method">Scenario method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result record.</returns>
   private static async Task<dynamic> ScenarioAsync( string method, params object[] arguments )
   {
      var task = (Task)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
      Task finished = await Task.WhenAny( task, Task.Delay( TimeSpan.FromMinutes( 2 ) ) );
      Assert.True( finished == task, $"{method} did not finish within 2 minutes" );
      await task;
      return task.GetType().GetProperty( "Result" )!.GetValue( task )!;
   }

   /// <summary>RunnerScenarios.TargetOrder.</summary>
   /// <param name="names">Targets.</param>
   /// <param name="seed">Seed.</param>
   /// <returns>Run order.</returns>
   private static string[] TargetOrder( string[] names, int seed )
   {
      return (string[])COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "TargetOrder" )!.Invoke( null, new object[] { names, seed } )!;
   }

   /// <summary>RunnerScenarios.PassOrder.</summary>
   /// <param name="concurrency">Levels.</param>
   /// <param name="exact">Has an exact mode.</param>
   /// <param name="seed">Seed.</param>
   /// <param name="target">Target name.</param>
   /// <returns>Run order.</returns>
   private static string[] PassOrder( int[] concurrency, bool exact, int seed, string target )
   {
      return (string[])COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "PassOrder" )!.Invoke( null, new object[] { concurrency, exact, seed, target } )!;
   }

   /// <summary>
   /// Parses a command line with the real BenchOptions and returns its Seed.
   /// </summary>
   /// <param name="args">Arguments.</param>
   /// <returns>The seed, or null.</returns>
   private static int? Seed( params string[] args )
   {
      Type type = COMPILED.Value.GetType( OPTIONS_TYPE )!;
      object options = type.GetMethod( "Parse" )!.Invoke( null, new object[] { args } )!;
      return (int?)type.GetProperty( "Seed" )!.GetValue( options );
   }

   /// <summary>
   /// Compiles every benchmark source file plus RunnerScenarios.cs into one in-memory assembly.
   /// Packages only the benchmark uses (System.Numerics.Tensors) are found through the
   /// benchmark's own restore output and loaded from the NuGet folder when first needed.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string root = RepoRoot();
      string bench = Path.Combine( root, "src", "GenericVectorBuilder.Bench" );
      var parse = new CSharpParseOptions( LanguageVersion.Latest );
      List<SyntaxTree> trees = Directory.EnumerateFiles( bench, "*.cs", SearchOption.AllDirectories )
         .Where( f => Path.GetRelativePath( bench, f ).Split( Path.DirectorySeparatorChar )[0] is not ( "bin" or "obj" ) )
         .Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), parse, f ) ).ToList();
      foreach( string file in new[] { "RunnerScenarios.cs", "TargetsSettingsScenarios.cs" } )
      {
         string scenarios = Path.Combine( root, "tests", "GenericVectorBuilder.Engines.Tests", "Bench", file );
         trees.Add( CSharpSyntaxTree.ParseText( File.ReadAllText( scenarios ), parse.WithPreprocessorSymbols( "BENCH_UNDER_TEST" ), scenarios ) );
      }

      trees.Add( CSharpSyntaxTree.ParseText( IMPLICIT_USINGS, parse ) );
      List<string> platform = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! ).Split( Path.PathSeparator ).ToList();
      List<string> extra = BenchOnlyPackages( bench, platform.Select( Path.GetFileName ).ToHashSet( StringComparer.OrdinalIgnoreCase ) );
      AssemblyLoadContext.Default.Resolving += ( context, name ) =>
         extra.FirstOrDefault( p => Path.GetFileNameWithoutExtension( p ).Equals( name.Name, StringComparison.OrdinalIgnoreCase ) ) is string path ? context.LoadFromAssemblyPath( path ) : null;
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "BenchRunnerUnderTest", trees, platform.Concat( extra ).Select( p => MetadataReference.CreateFromFile( p ) ), options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   /// <summary>
   /// Runtime files of the packages the benchmark restored that this test host does not
   /// already have, read from the benchmark's project.assets.json.
   /// </summary>
   /// <param name="bench">Benchmark project folder.</param>
   /// <param name="loaded">File names already on the test host's platform list.</param>
   /// <returns>Full paths of the extra assemblies.</returns>
   private static List<string> BenchOnlyPackages( string bench, HashSet<string?> loaded )
   {
      string assets = Path.Combine( bench, "obj", "project.assets.json" );
      Assert.True( File.Exists( assets ), $"Restore the benchmark project first (no {assets})." );
      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( assets ) );
      string packages = doc.RootElement.GetProperty( "packageFolders" ).EnumerateObject().First().Name;
      JsonElement libraries = doc.RootElement.GetProperty( "libraries" );
      var extra = new List<string>();
      foreach( JsonProperty library in doc.RootElement.GetProperty( "targets" ).EnumerateObject().First().Value.EnumerateObject() )
      {
         if( !library.Value.TryGetProperty( "runtime", out JsonElement runtime ) || library.Value.GetProperty( "type" ).GetString() != "package" )
         {
            continue;
         }

         string folder = libraries.GetProperty( library.Name ).GetProperty( "path" ).GetString()!;
         extra.AddRange( runtime.EnumerateObject().Select( r => r.Name ).Where( r => r.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) && !loaded.Contains( Path.GetFileName( r ) ) )
            .Select( r => Path.Combine( packages, folder, r ) ) );
      }

      return extra;
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
}
