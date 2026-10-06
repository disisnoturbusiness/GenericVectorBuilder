// Compiled only by MeasurementTests, together with the benchmark's own sources (the test project
// does not reference the benchmark console project). BENCH_UNDER_TEST is defined only in that
// compilation, so the test project itself sees an empty file.
#if BENCH_UNDER_TEST
using System.Diagnostics;
using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Data;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Running;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Bench.Truth;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Bench.MeasurementUnderTest;

/// <summary>
/// Runs the benchmark's real <see cref="SearchRunner"/> and <see cref="TargetRunner"/> against a
/// fake sink whose latency follows a script, and hands back plain results for the tests.
/// Why a scripted latency: the rules under test (one window for p50 and QPS@1, a timed exact
/// window, a rehearsal of every pass type at its own concurrency, a time-based warm-up at the
/// pass's own concurrency, a settle check against a trial of the same pass, per-pass records)
/// are about what the runner measures, and only a sink with a known latency shape shows whether
/// the runner measured it right.
/// </summary>
public static class MeasurementScenarios
{
   #region Data Members

   /// <summary>Collection every scenario searches, as the benchmark names it.</summary>
   public const string COLLECTION = "gvbbench_m";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Runs every timed pass on a sink with the given latency script, with a warm-up of the given
   /// length (zero: only the --warmup count of 3 searches).
   /// </summary>
   /// <param name="args">Extra benchmark arguments (seconds, exact-seconds, concurrency, queries).</param>
   /// <param name="defaultScript">Latency script of default searches (see <see cref="ScriptedSink"/>).</param>
   /// <param name="exactScript">Latency script of exact searches, or null for a sink without an exact mode.</param>
   /// <param name="withPreparation">True to pass a (fresh) preparation, so every pass also gets its settle check.</param>
   /// <param name="warmupSeconds">Shortest warm-up before every pass.</param>
   /// <returns>The trace, the report numbers, the pass records and the flags.</returns>
   public static async Task<PassScenario> PassesAsync( string[] args, string defaultScript, string? exactScript, bool withPreparation, double warmupSeconds )
   {
      ( BenchOptions options, PipelineData data, QuerySet queries ) = Setup( args );
      var trace = new MeasureTrace();
      ScriptedSink sink = exactScript == null ? new ScriptedSink( trace, defaultScript ) : new ScriptedExactSink( trace, defaultScript, exactScript );
      await sink.UpsertAsync( COLLECTION, data.Records( 0, data.Count ), CancellationToken.None );
      SearchRunner runner = Runner( data, queries, options, 3, Quick( 0.2, warmupSeconds ) );
      SearchOutcome outcome = await runner.RunAsync( Target( sink ), COLLECTION, line => trace.Add( "LOG " + line, 0 ), CancellationToken.None, withPreparation ? new Preparation() : null );
      SearchReport r = outcome.Report;
      return new PassScenario( trace.Events(), trace.Ticks(), trace.InFlight(), Stopwatch.Frequency, outcome.PassOrder.ToArray(), outcome.Passes.Select( Pass ).ToArray(), outcome.Flags.ToArray(),
         r.LatencySamples, r.P50Ms, r.P95Ms, r.P99Ms, r.Qps.ToDictionary( p => p.Key, p => p.Value ), r.Recall, r.ExactQueries, r.ExactP50Ms, r.ExactRecall, r.Errors );
   }

   /// <summary>
   /// Runs only the preparation (the rehearsal) with a short rehearsal.
   /// </summary>
   /// <param name="args">Extra benchmark arguments.</param>
   /// <param name="defaultScript">Latency script of default searches.</param>
   /// <param name="rehearsalSeconds">Rehearsal length per pass type.</param>
   /// <returns>The trace and what the preparation reported, or the error it threw.</returns>
   public static async Task<PrepareScenario> PrepareAsync( string[] args, string defaultScript, double rehearsalSeconds )
   {
      ( BenchOptions options, PipelineData data, QuerySet queries ) = Setup( args );
      var trace = new MeasureTrace();
      var sink = new ScriptedExactSink( trace, defaultScript, "fixed:1" );
      await sink.UpsertAsync( COLLECTION, data.Records( 0, data.Count ), CancellationToken.None );
      SearchRunner runner = Runner( data, queries, options, 3, Quick( rehearsalSeconds, 0.5 ) );
      try
      {
         Preparation p = await runner.PrepareAsync( Target( sink ), COLLECTION, line => trace.Add( "LOG " + line, 0 ), CancellationToken.None );
         return new PrepareScenario( trace.Events(), trace.InFlight(), p.Rehearsals.Select( x => $"{x.Pass}|{x.Seconds}|{x.Searches}|{x.Errors}" ).ToArray(), null );
      }
      catch( InvalidOperationException ex )
      {
         return new PrepareScenario( trace.Events(), trace.InFlight(), Array.Empty<string>(), ex.Message );
      }
   }

   /// <summary>
   /// Measures one target the way run-all does (short rehearsal, warm-up, windows, trial and
   /// extension), and writes results.json.
   /// </summary>
   /// <param name="defaultScript">Latency script of default searches.</param>
   /// <param name="folder">Folder for the results.</param>
   /// <param name="outside">Outside load the runner is handed as machine control's reading ("window,recent,limit"), "none" for a reader that has nothing, or null for no reader at all.</param>
   /// <returns>The trace, notes, error, pass order and results.json text.</returns>
   public static async Task<TargetRun> MeasureAsync( string defaultScript, string folder, string? outside = null )
   {
      var trace = new MeasureTrace();
      BenchOptions options = BenchOptions.Parse( new[] { "run-all", "--pipeline", "m", "--warmup", "3", "--seconds", "1", "--exact-seconds", "1",
         "--concurrency", "1,2", "--queries", "random:5", "--top", "3", "--seed", "9" } );
      PipelineData data = Data( 30, 4 );
      QuerySet queries = QuerySet.Random( data, options.RandomCount );
      SearchRunner runner = Runner( data, queries, options, 9, Quick( 0.4, 1.5 ) );
      var measurer = new TargetRunner( options, new EngineLifecycle( new NoHost(), true, _ => { } ), line => trace.Add( "LOG " + line, 0 ) ) { OutsideLoad = OutsideReader( outside ) };
      TargetReport result = await measurer.MeasureAsync( Target( new ScriptedExactSink( trace, defaultScript, "fixed:1" ) ), data, runner, CancellationToken.None );
      var report = new BenchReport { RunSeed = 9, Command = "run-all", Pipeline = "m" };
      report.Targets.Add( result );
      ResultsWriter.Write( report, folder );
      return new TargetRun( trace.Events(), result.Notes.ToArray(), result.Error, result.PassOrder?.ToArray() ?? Array.Empty<string>(), result.WarmupErrors,
         File.ReadAllText( Path.Combine( folder, "results.json" ) ) );
   }

   /// <summary>
   /// The preparation and every timed pass of one target (default@1 and default@4) with short
   /// warm-up, window, trial and extension times. The sink's latency script restarts at the first
   /// warm-up line, and a "drop" script drops to its low latency when the first trial starts.
   /// Shows what each settle check saw and did, the progress lines in order, the settle note
   /// TargetRunner writes, whether the consolidation's reader (TargetResult) reads that note as
   /// settled, and how many searches were in flight during each trial.
   /// </summary>
   /// <param name="script">Latency script of default searches.</param>
   /// <param name="seed">Run seed (fixes which pass runs first).</param>
   /// <param name="extensionMinimum">Shortest extension, seconds.</param>
   /// <param name="extensionCap">Longest extension, seconds.</param>
   /// <returns>What the checks did.</returns>
   public static async Task<CheckScenario> CheckAsync( string script, int seed, double extensionMinimum, double extensionCap )
   {
      ( BenchOptions options, PipelineData data, QuerySet queries ) = Setup( new[] { "--seconds", "1", "--concurrency", "1,4", "--queries", "random:5", "--exact-seconds", "0" } );
      var trace = new MeasureTrace();
      var sink = new ScriptedSink( trace, script );
      await sink.UpsertAsync( COLLECTION, data.Records( 0, data.Count ), CancellationToken.None );
      SearchRunner runner = Runner( data, queries, options, seed, Quick( 0.2, 1.5 ) with { ExtensionMinimum = extensionMinimum, ExtensionCap = extensionCap } );
      Action<string> log = line => Log( trace, sink, line );
      Preparation p = await runner.PrepareAsync( Target( sink ), COLLECTION, log, CancellationToken.None );
      SearchOutcome outcome = await runner.RunAsync( Target( sink ), COLLECTION, log, CancellationToken.None, p );
      string note = TargetRunner.DescribeSettle( p.Checks );
      using var doc = System.Text.Json.JsonDocument.Parse( System.Text.Json.JsonSerializer.Serialize( new { name = "fake", notes = new[] { note } } ) );
      TargetResult read = TargetResult.Parse( doc.RootElement );
      string[] checks = p.Checks.Select( c => string.Join( "|", c.Pass, c.Trial.Concurrency, c.Settle.Settled, SearchRunner.SettledFigure( c.Settle.WindowFigures ) ?? -1, c.Trial.Figure ?? -1, c.Trial.Agrees,
         c.Extension != null, c.Extension?.Seconds ?? 0, c.Retrial?.Figure ?? -1, c.Retrial?.Agrees ?? false, c.Confirmed, c.Settle.Concurrency, c.Held, c.PassFigure ?? -1, c.SettledFigure ?? -1 ) ).ToArray();
      return new CheckScenario( trace.Events(), trace.InFlight(), checks, note, read.Settled, outcome.PassOrder.ToArray(), p.Searches, outcome.Report.Qps.ToDictionary( q => q.Key, q => q.Value ) );
   }

   /// <summary>
   /// A prepared run of one target (default@1 and default@4) on a sink that speeds up only after
   /// it has served many searches while others were in flight, the way a JVM engine compiles its
   /// concurrent paths only under concurrent load. Returns the pass order and QPS@4.
   /// </summary>
   /// <param name="seed">Run seed (fixes whether default@4 runs first or last).</param>
   /// <param name="script">"jit:HIGH:LOW:N".</param>
   /// <returns>Pass order, QPS by level, the settle note, and the concurrent searches served when default@4's timing began.</returns>
   public static async Task<OrderScenario> OrderAsync( int seed, string script )
   {
      ( BenchOptions options, PipelineData data, QuerySet queries ) = Setup( new[] { "--seconds", "2", "--concurrency", "1,4", "--queries", "random:5", "--exact-seconds", "0" } );
      var trace = new MeasureTrace();
      var sink = new ScriptedSink( trace, script );
      await sink.UpsertAsync( COLLECTION, data.Records( 0, data.Count ), CancellationToken.None );
      SearchRunner runner = Runner( data, queries, options, seed, Quick( 0.05, 1.5 ) );
      int servedAtTiming = -1;
      Action<string> log = line =>
      {
         trace.Add( "LOG " + line, 0 );
         servedAtTiming = line.EndsWith( "timing default@4", StringComparison.Ordinal ) ? sink.ConcurrentServed : servedAtTiming;
      };
      Preparation p = await runner.PrepareAsync( Target( sink ), COLLECTION, log, CancellationToken.None );
      SearchOutcome outcome = await runner.RunAsync( Target( sink ), COLLECTION, log, CancellationToken.None, p );
      return new OrderScenario( outcome.PassOrder.ToArray(), outcome.Report.Qps.ToDictionary( q => q.Key, q => q.Value ), TargetRunner.DescribeSettle( p.Checks ), servedAtTiming );
   }

   /// <summary>
   /// The settle note for hand-made settle checks, each with how the consolidation's reader
   /// (TargetResult) reads it: "agreed", "extended-ok", "extended-bad", "gave-up", "none".
   /// </summary>
   /// <param name="mode">Which case.</param>
   /// <returns>The note and whether the reader calls it settled (null when it says nothing).</returns>
   public static NoteScenario SettleNote( string mode )
   {
      var settled = new SettleResult( true, 15.0, 30000, 0, null, new[] { 1512.0, 1530, 1524 }, null, false, 8, 2 );
      var unsettled = new SettleResult( false, 15.0, 9000, 20, "x", new[] { 900.0, 1400, 1100 }, "20 searches in a row failed", true, 8, 2 );
      var trialBad = new TrialRecord( 8, 1524, 2900, 8700, 0, 3.0 );
      var trialOk = new TrialRecord( 8, 2880, 2900, 8700, 0, 3.0 );
      var extension = new SettleResult( true, 30.1, 87000, 0, null, new[] { 2870.0, 2890, 2880 }, null, false, 8, 2 );
      var one = new SettleCheck( "default@1", new SettleResult( true, 15.0, 8000, 0, null, new[] { 1.87, 1.88, 1.86 }, null, false, 1, 2 ), new TrialRecord( 1, 1.87, 1.871, 1600, 0, 3.0 ), null, null, true );
      SettleCheck[] checks = mode switch
      {
         "agreed" => new[] { new SettleCheck( "default@8", settled, trialOk with { Settled = 2899 }, null, null, true ), one },
         "extended-ok" => new[] { new SettleCheck( "default@8", settled, trialBad, extension, trialOk, true ), one },
         "extended-bad" => new[] { new SettleCheck( "default@8", settled, trialBad, extension, trialBad with { Settled = 2880, Figure = 3600 }, false ), one },
         "gave-up" => new[] { new SettleCheck( "default@8", unsettled, trialBad, null, null, false ), one },
         "held" => new[] { new SettleCheck( "default@8", settled, trialOk with { Settled = 2899 }, null, null, true, 2950 ), one with { PassFigure = 1.90 } },
         "not-held" => new[] { new SettleCheck( "default@8", settled, trialOk with { Settled = 2899 }, null, null, true, 2400 ), one with { PassFigure = 1.90 } },
         "held-floor" => new[] { new SettleCheck( "default@8", settled, trialOk with { Settled = 2899 }, null, null, true, 2950 ), one with { Trial = new TrialRecord( 1, 0.366, 0.36, 8000, 0, 3.0 ), PassFigure = 0.325 } },
         "not-held-p50" => new[] { new SettleCheck( "default@8", settled, trialOk with { Settled = 2899 }, null, null, true, 2950 ), one with { Trial = new TrialRecord( 1, 0.366, 0.36, 8000, 0, 3.0 ), PassFigure = 0.25 } },
         _ => Array.Empty<SettleCheck>(),
      };
      string note = TargetRunner.DescribeSettle( checks );
      using var doc = System.Text.Json.JsonDocument.Parse( System.Text.Json.JsonSerializer.Serialize( new { name = "fake", notes = new[] { note } } ) );
      return new NoteScenario( note, TargetResult.Parse( doc.RootElement ).Settled );
   }

   /// <summary>
   /// <see cref="SearchRunner.SettledFigure"/> and <see cref="SearchRunner.TrialAgrees"/> on given numbers.
   /// </summary>
   /// <param name="figures">Window figures.</param>
   /// <param name="settled">Settled figure for the agreement check.</param>
   /// <param name="trial">Trial figure for the agreement check.</param>
   /// <returns>The settled figure (NaN for none) and whether they agree.</returns>
   public static double[] TrialRules( double[] figures, double settled, double trial )
   {
      return new[] { SearchRunner.SettledFigure( figures ) ?? double.NaN, SearchRunner.TrialAgrees( settled, trial ) ? 1 : 0 };
   }

   /// <summary>
   /// The flags <see cref="SearchRunner.ShapeFlags"/> raises for a set of latencies.
   /// </summary>
   /// <param name="latencies">Latencies, ms.</param>
   /// <returns>The flags.</returns>
   public static string[] ShapeFlags( double[] latencies )
   {
      return SearchRunner.ShapeFlags( "default@1", latencies ).ToArray();
   }

   /// <summary>
   /// <see cref="SearchRunner.IsSettled"/> for a list of window figures.
   /// </summary>
   /// <param name="figures">Window figures.</param>
   /// <returns>True when settled.</returns>
   public static bool IsSettled( double[] figures )
   {
      return SearchRunner.IsSettled( figures );
   }

   /// <summary>
   /// The benchmark's default lengths and limits, as constants and as a fresh runner carries them:
   /// rehearsal s, warm-up s, window s, warm-up cap s, trial s, tolerance, extension minimum s,
   /// extension cap s, then the same seven lengths read from a runner built with no settings.
   /// </summary>
   /// <returns>The numbers.</returns>
   public static double[] Defaults()
   {
      ( BenchOptions options, PipelineData data, QuerySet queries ) = Setup( Array.Empty<string>() );
      var runner = new SearchRunner( data, queries, BruteForce.Compute( data, queries, options.Top ), options, 1 );
      return new[] { SearchRunner.REHEARSAL_TIME.TotalSeconds, SearchRunner.WARMUP_TIME.TotalSeconds, SearchRunner.WINDOW_TIME.TotalSeconds, SearchRunner.SETTLE_CAP.TotalSeconds,
         SearchRunner.TRIAL_TIME.TotalSeconds, SearchRunner.TRIAL_TOLERANCE, SearchRunner.EXTENSION_MINIMUM.TotalSeconds, SearchRunner.EXTENSION_CAP.TotalSeconds,
         runner.RehearsalTime.TotalSeconds, runner.WarmupTime.TotalSeconds, runner.WindowTime.TotalSeconds, runner.SettleCap.TotalSeconds, runner.TrialTime.TotalSeconds,
         runner.ExtensionMinimum.TotalSeconds, runner.ExtensionCap.TotalSeconds };
   }

   /// <summary>
   /// <see cref="SearchRunner.LevelOf"/> on hand-made windows: windows of the given figures, each
   /// of the given searches and seconds; with one searcher each window's latencies are its figure
   /// repeated, except where <paramref name="latencies"/> gives them.
   /// </summary>
   /// <param name="figures">Window figures, oldest first.</param>
   /// <param name="searches">Searches of each window.</param>
   /// <param name="seconds">Seconds of each window.</param>
   /// <param name="concurrency">Searchers.</param>
   /// <param name="latencies">Latencies of each window (one searcher), or null for the figure repeated.</param>
   /// <returns>Older, newer, windows a half, 1 when they agree, apart; or an empty array when there is no level test.</returns>
   public static double[] LevelRules( double[] figures, int[] searches, double[] seconds, int concurrency, double[][]? latencies )
   {
      List<SettleWindow> windows = figures.Select( ( f, i ) => new SettleWindow( f, searches[i], seconds[i],
         concurrency == 1 ? ( latencies?[i] ?? Enumerable.Repeat( f, searches[i] ).ToArray() ) : null ) ).ToList();
      SettleLevel? level = SearchRunner.LevelOf( windows, concurrency );
      return level == null ? Array.Empty<double>() : new[] { level.Older, level.Newer, level.HalfWindows, level.Agrees ? 1 : 0, level.Apart, level.Low, level.High };
   }

   /// <summary>
   /// How a second trial is judged: its distance from the settled figure, whether it lies inside
   /// the given range, and whether it agrees.
   /// </summary>
   /// <param name="settled">Settled figure.</param>
   /// <param name="figure">Trial figure.</param>
   /// <param name="low">Range low, or NaN for none.</param>
   /// <param name="high">Range high, or NaN for none.</param>
   /// <param name="concurrency">Searchers (one: the figures are p50s in ms).</param>
   /// <returns>Apart (NaN for none), 1 when inside the range, 1 when it agrees, and its text.</returns>
   public static object[] TrialJudged( double settled, double figure, double low, double high, int concurrency )
   {
      var trial = new TrialRecord( concurrency, settled, figure, 3000, 0, 3.0, double.IsNaN( low ) ? null : low, double.IsNaN( high ) ? null : high );
      return new object[] { trial.Apart ?? double.NaN, trial.InRange ? 1 : 0, trial.Agrees ? 1 : 0, trial.Describe( "second trial" ) };
   }

   /// <summary>
   /// An extension's verdict when one more full window arrives between the moment its windows
   /// agreed (and it stopped) and the end of its searchers. Drives a real <see cref="SettleSampler"/>
   /// at one searcher in windows of <paramref name="windowSeconds"/>: <paramref name="steady"/>
   /// windows of 150 searches at 1 ms, then one window of 150 at 5 ms, then, once that window has
   /// ended but before it is read (windows are read one window after they end), the poll, the
   /// stop decision, and the final read. Why: the v6 runs judged an extension on that extra
   /// window, and 7 of their 10 unsettled checks came from it.
   /// </summary>
   /// <param name="windowSeconds">Window length (keep it well above the box's timer jitter).</param>
   /// <param name="steady">Steady windows before the slow one.</param>
   /// <returns>Settled (1/0), stopped by settling (1/0), windows judged, the last figure judged, and whether the timing held (1/0: every step landed in its window).</returns>
   public static async Task<double[]> FrozenVerdictAsync( double windowSeconds, int steady )
   {
      var plan = new SustainPlan( 0, TimeSpan.Zero, TimeSpan.FromMinutes( 5 ), true );
      var sampler = new SettleSampler( 1, TimeSpan.FromSeconds( windowSeconds ), 20, true );
      var clock = Stopwatch.StartNew();
      bool onTime = true;
      for( int window = 0; window <= steady; window++ )
      {
         onTime &= await UntilAsync( clock, ( window + 0.2 ) * windowSeconds, ( window + 0.6 ) * windowSeconds );
         Enumerable.Range( 0, 150 ).ToList().ForEach( _ => sampler.Record( 0, window < steady ? 1.0 : 5.0, null ) );
         onTime &= clock.Elapsed.TotalSeconds < ( window + 0.8 ) * windowSeconds;
      }

      onTime &= await UntilAsync( clock, ( steady + 1.2 ) * windowSeconds, ( steady + 1.6 ) * windowSeconds );
      sampler.Evaluate( false );
      bool stopped = !sampler.Continues( 1, plan );
      onTime &= clock.Elapsed.TotalSeconds < ( steady + 1.8 ) * windowSeconds;
      sampler.Evaluate( true );
      SettleResult result = sampler.Result( plan );
      return new[] { result.Settled ? 1.0 : 0, stopped ? 1 : 0, result.WindowFigures.Count, result.WindowFigures.Count > 0 ? result.WindowFigures[^1] : -1, onTime ? 1 : 0 };
   }

   /// <summary>
   /// The load note <see cref="TargetRunner.DescribeLoad"/> writes for a reading.
   /// </summary>
   /// <param name="loadAverage">Load average text.</param>
   /// <param name="window">Outside load over the busy window, or NaN for none.</param>
   /// <param name="recent">Outside load over the recent window, or NaN for none.</param>
   /// <param name="limit">Busy limit.</param>
   /// <param name="hasReading">False for no reading at all (machine control off).</param>
   /// <param name="wired">False for no reader handed to the runner.</param>
   /// <returns>The note.</returns>
   public static string LoadNote( string loadAverage, double window, double recent, double limit, bool hasReading, bool wired )
   {
      OutsideLoadSample? sample = hasReading ? new OutsideLoadSample( Reading( window, recent ), limit ) : null;
      return TargetRunner.DescribeLoad( loadAverage, sample, wired );
   }

   /// <summary>
   /// The run's warm-up method as the consolidation reads it back from the runner's own method
   /// notes (<see cref="WarmupMethod.From"/>), as numbers: shortest warm-up s, fewest searches, cap
   /// s, window s, window searches, trial s, trial %, settle windows, settle %, extension minimum s,
   /// extension cap s, rehearsal s, and 1 when it reads that an unsettled pass flags its target.
   /// </summary>
   /// <param name="warmupSearches">The --warmup count.</param>
   /// <returns>The numbers (NaN for a part it could not read).</returns>
   public static double[] MethodReadBack( int warmupSearches )
   {
      WarmupMethod m = WarmupMethod.From( SearchRunner.DescribeMethod( warmupSearches ).ToList() ) ?? throw new InvalidOperationException( "the method notes were not read at all" );
      double N( double? v ) => v ?? double.NaN;
      return new[] { N( m.MinSeconds ), N( m.MinSearches ), N( m.CapSeconds ), N( m.WindowSeconds ), N( m.WindowSearches ), N( m.TrialSeconds ), N( m.TrialPercent ), N( m.SettleWindows ),
         N( m.SettlePercent ), N( m.ExtensionMinSeconds ), N( m.ExtensionCapSeconds ), N( m.RehearsalSeconds ), m.FlagsUnsettled ? 1 : 0 };
   }

   /// <summary>
   /// The run's method notes as the runner writes them for a --warmup count.
   /// </summary>
   /// <param name="warmupSearches">The --warmup count.</param>
   /// <returns>The notes.</returns>
   public static string[] MethodNotes( int warmupSearches )
   {
      return SearchRunner.DescribeMethod( warmupSearches ).ToArray();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The outside-load reader a scenario hands the target runner: null for none, one that has
   /// nothing for "none", else a fixed reading from "window,recent,limit".
   /// </summary>
   /// <param name="outside">The reading as text, "none", or null.</param>
   /// <returns>The reader, or null.</returns>
   private static Func<OutsideLoadSample?>? OutsideReader( string? outside )
   {
      if( outside == null )
      {
         return null;
      }

      if( outside == "none" )
      {
         return () => null;
      }

      double[] v = outside.Split( ',' ).Select( x => double.Parse( x, System.Globalization.CultureInfo.InvariantCulture ) ).ToArray();
      return () => new OutsideLoadSample( Reading( v[0], v[1] ), v[2] );
   }

   /// <summary>
   /// An outside load reading over a 60 s window ending now.
   /// </summary>
   /// <param name="window">CPUs over the busy window, or NaN for none.</param>
   /// <param name="recent">CPUs over the recent window, or NaN for none.</param>
   /// <returns>The reading.</returns>
   private static OutsideReading Reading( double window, double recent )
   {
      DateTime now = DateTime.UtcNow;
      return new OutsideReading( double.IsNaN( window ) ? null : window, double.IsNaN( recent ) ? null : recent, now.AddSeconds( -60 ), now );
   }

   /// <summary>
   /// Waits until the clock reads <paramref name="at"/> seconds; false when it was already past
   /// <paramref name="latest"/> by then (the box was too slow for the step to land in its window).
   /// </summary>
   /// <param name="clock">The clock.</param>
   /// <param name="at">Seconds to wait for.</param>
   /// <param name="latest">Latest acceptable seconds.</param>
   /// <returns>True when on time.</returns>
   private static async Task<bool> UntilAsync( Stopwatch clock, double at, double latest )
   {
      double wait = at - clock.Elapsed.TotalSeconds;
      if( wait > 0 )
      {
         await Task.Delay( TimeSpan.FromSeconds( wait ) );
      }

      return clock.Elapsed.TotalSeconds <= latest;
   }

   /// <summary>
   /// Short test lengths: the given rehearsal and warm-up, windows of 0.25 s, a 0.3 s trial, an
   /// extension of 0.5 s to 3 s and a warm-up cap of 10 s. Why windows of 0.25 s: on a loaded
   /// test box a 1 ms timer wait wanders by tens of percent over 0.1 s.
   /// </summary>
   /// <param name="rehearsal">Rehearsal per pass type, seconds.</param>
   /// <param name="warmup">Shortest warm-up, seconds.</param>
   /// <returns>The lengths.</returns>
   private static Timing Quick( double rehearsal, double warmup )
   {
      return new Timing( rehearsal, warmup, 0.25, 10, 0.3, 0.5, 3 );
   }

   /// <summary>
   /// A runner with the given lengths.
   /// </summary>
   /// <param name="data">Rows.</param>
   /// <param name="queries">Queries.</param>
   /// <param name="options">Options.</param>
   /// <param name="seed">Run seed.</param>
   /// <param name="t">Lengths.</param>
   /// <returns>The runner.</returns>
   private static SearchRunner Runner( PipelineData data, QuerySet queries, BenchOptions options, int seed, Timing t )
   {
      return new SearchRunner( data, queries, BruteForce.Compute( data, queries, options.Top ), options, seed )
      {
         RehearsalTime = TimeSpan.FromSeconds( t.Rehearsal ),
         WarmupTime = TimeSpan.FromSeconds( t.Warmup ),
         WindowTime = TimeSpan.FromSeconds( t.Window ),
         SettleCap = TimeSpan.FromSeconds( t.SettleCap ),
         TrialTime = TimeSpan.FromSeconds( t.Trial ),
         ExtensionMinimum = TimeSpan.FromSeconds( t.ExtensionMinimum ),
         ExtensionCap = TimeSpan.FromSeconds( t.ExtensionCap ),
      };
   }

   /// <summary>
   /// Records a progress line; restarts the sink's latency script at the first warm-up line, so a
   /// script like "decay" plays out inside the first warm-up and not inside the rehearsal; drops a
   /// "drop" script to its low latency when the first trial starts (an engine that gets faster
   /// right after its warm-up looked settled); and moves a "jump" script to its second latency when
   /// the first timed pass begins (an engine that changes once the clock is running).
   /// </summary>
   /// <param name="trace">Trace.</param>
   /// <param name="sink">The sink.</param>
   /// <param name="line">The progress line.</param>
   private static void Log( MeasureTrace trace, ScriptedSink sink, string line )
   {
      trace.Add( "LOG " + line, 0 );
      if( line.Contains( ": warm-up before ", StringComparison.Ordinal ) )
      {
         sink.RestartOnce();
      }

      if( line.Contains( $": {SearchRunner.CHECK_STEP} before ", StringComparison.Ordinal ) && line.Contains( ": trial, ", StringComparison.Ordinal ) )
      {
         sink.Drop();
      }

      if( line.Contains( ": timing ", StringComparison.Ordinal ) )
      {
         sink.Jump();
      }
   }

   /// <summary>
   /// Options, rows and random queries for a pass scenario.
   /// </summary>
   /// <param name="args">Extra arguments.</param>
   /// <returns>Options, data and queries.</returns>
   private static (BenchOptions Options, PipelineData Data, QuerySet Queries) Setup( string[] args )
   {
      BenchOptions options = BenchOptions.Parse( new[] { "bench", "--pipeline", "m", "--targets", "fake", "--warmup", "3", "--top", "3" }.Concat( args ).ToArray() );
      PipelineData data = Data( 40, 4 );
      return ( options, data, QuerySet.Random( data, options.RandomCount ) );
   }

   /// <summary>
   /// A pass record as one plain line: name|previous|startTicks|endTicks|seconds|searches|errors|p50|mean|p99|qps.
   /// </summary>
   /// <param name="p">The record.</param>
   /// <returns>The line.</returns>
   private static string Pass( PassRecord p )
   {
      return string.Join( "|", p.Name, p.Previous ?? "-", p.StartUtc.Ticks, p.EndUtc.Ticks, p.Seconds, p.Searches, p.Errors, p.P50Ms ?? -1, p.MeanMs ?? -1, p.P99Ms ?? -1, p.Qps );
   }

   /// <summary>
   /// A small deterministic data set: unit-length vectors from a fixed generator.
   /// </summary>
   /// <param name="rows">Rows.</param>
   /// <param name="dimension">Vector length.</param>
   /// <returns>The rows.</returns>
   private static PipelineData Data( int rows, int dimension )
   {
      var data = new PipelineData( "m", rows, dimension ) { HasText = true };
      var random = new Random( 11 );
      for( int row = 0; row < rows; row++ )
      {
         float[] vector = Enumerable.Range( 0, dimension ).Select( _ => (float)( random.NextDouble() - 0.5 ) ).ToArray();
         float norm = MathF.Sqrt( vector.Sum( v => v * v ) );
         data.Add( new Guid( row + 1, 0, 0, new byte[8] ), $"doc{row}.cs", "m", "fake", 0, $"text {row}", null, vector.Select( v => v / norm ).ToArray() );
      }

      return data;
   }

   /// <summary>
   /// Wraps a sink as an embedded benchmark target (nothing to start or stop).
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <returns>The target.</returns>
   private static BenchTarget Target( ISink sink )
   {
      Func<string, CancellationToken, Task<Measurement>> none = ( _, _ ) => Task.FromResult( Measurement.None( "fake" ) );
      return new BenchTarget( sink, "Fake 1.0", "fake flat index", "embedded", null, none, none );
   }

   #endregion Private Methods
}

/// <summary>Test lengths, seconds.</summary>
/// <param name="Rehearsal">Rehearsal per pass type.</param>
/// <param name="Warmup">Shortest warm-up.</param>
/// <param name="Window">Shortest settle window.</param>
/// <param name="SettleCap">Longest warm-up.</param>
/// <param name="Trial">Trial.</param>
/// <param name="ExtensionMinimum">Shortest extension.</param>
/// <param name="ExtensionCap">Longest extension.</param>
public sealed record Timing( double Rehearsal, double Warmup, double Window, double SettleCap, double Trial, double ExtensionMinimum, double ExtensionCap );

/// <summary>What <see cref="MeasurementScenarios.PassesAsync"/> saw.</summary>
/// <param name="Events">"S" default search, "X" exact search, "LOG ..." lines, in order.</param>
/// <param name="Ticks">Stopwatch ticks of each event.</param>
/// <param name="InFlight">Searches in flight when each search started (0 for log lines).</param>
/// <param name="Frequency">Stopwatch ticks per second.</param>
/// <param name="PassOrder">Passes as run.</param>
/// <param name="Passes">Pass records as plain lines.</param>
/// <param name="Flags">WARNING lines.</param>
/// <param name="LatencySamples">default@1 samples.</param>
/// <param name="P50">default@1 p50.</param>
/// <param name="P95">default@1 p95.</param>
/// <param name="P99">default@1 p99.</param>
/// <param name="Qps">QPS by level.</param>
/// <param name="Recall">default@1 recall.</param>
/// <param name="ExactQueries">Exact samples.</param>
/// <param name="ExactP50">Exact p50.</param>
/// <param name="ExactRecall">Exact recall.</param>
/// <param name="Errors">Timed errors.</param>
public sealed record PassScenario( string[] Events, long[] Ticks, int[] InFlight, long Frequency, string[] PassOrder, string[] Passes, string[] Flags, int LatencySamples, double? P50, double? P95, double? P99,
   Dictionary<int, double> Qps, double? Recall, int ExactQueries, double? ExactP50, double? ExactRecall, int Errors );

/// <summary>What <see cref="MeasurementScenarios.PrepareAsync"/> saw.</summary>
/// <param name="Events">Trace.</param>
/// <param name="InFlight">Searches in flight when each search started (0 for log lines).</param>
/// <param name="Rehearsals">pass|seconds|searches|errors per rehearsal.</param>
/// <param name="Error">The preparation's exception message, or null.</param>
public sealed record PrepareScenario( string[] Events, int[] InFlight, string[] Rehearsals, string? Error );

/// <summary>What <see cref="MeasurementScenarios.CheckAsync"/> saw.</summary>
/// <param name="Events">Trace (searches and progress lines).</param>
/// <param name="InFlight">Searches in flight when each search started (0 for log lines).</param>
/// <param name="Checks">Per check: pass|trial searchers|warm-up settled|settled figure|trial figure|trial agreed|extended|extension s|retrial figure|retrial agreed|confirmed|warm-up searchers|held|timed pass figure|figure it was held against.</param>
/// <param name="Note">The settle note.</param>
/// <param name="ReadSettled">How the consolidation's reader reads the note.</param>
/// <param name="PassOrder">Passes as run.</param>
/// <param name="PreparationSearches">Untimed searches of the rehearsal and the checks.</param>
/// <param name="Qps">QPS by level.</param>
public sealed record CheckScenario( string[] Events, int[] InFlight, string[] Checks, string Note, bool? ReadSettled, string[] PassOrder, int PreparationSearches, Dictionary<int, double> Qps );

/// <summary>What <see cref="MeasurementScenarios.OrderAsync"/> saw.</summary>
/// <param name="PassOrder">Passes as run.</param>
/// <param name="Qps">QPS by level.</param>
/// <param name="Note">The settle note.</param>
/// <param name="ServedAtTiming">Searches the sink had served with another in flight when default@4's timing began.</param>
public sealed record OrderScenario( string[] PassOrder, Dictionary<int, double> Qps, string Note, int ServedAtTiming );

/// <summary>A settle note and how the consolidation reads it.</summary>
/// <param name="Note">The note.</param>
/// <param name="ReadSettled">Settled as TargetResult reads it.</param>
public sealed record NoteScenario( string Note, bool? ReadSettled );

/// <summary>What <see cref="MeasurementScenarios.MeasureAsync"/> saw.</summary>
/// <param name="Events">Trace.</param>
/// <param name="Notes">Target notes.</param>
/// <param name="Error">Target error.</param>
/// <param name="PassOrder">Passes as run.</param>
/// <param name="WarmupErrors">Untimed errors recorded.</param>
/// <param name="Json">results.json text.</param>
public sealed record TargetRun( string[] Events, string[] Notes, string? Error, string[] PassOrder, int? WarmupErrors, string Json );

/// <summary>
/// An ordered, thread-safe event list with the time of each event and, for searches, how many
/// searches were in flight when it started.
/// </summary>
public sealed class MeasureTrace
{
   #region Data Members

   private readonly List<(string Item, long Ticks, int InFlight)> _events = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Appends an event.
   /// </summary>
   /// <param name="item">The event.</param>
   /// <param name="inFlight">Searches in flight when it started (0 for a log line).</param>
   public void Add( string item, int inFlight )
   {
      long now = Stopwatch.GetTimestamp();
      lock( _events )
      {
         _events.Add( ( item, now, inFlight ) );
      }
   }

   /// <summary>
   /// A copy of the events so far.
   /// </summary>
   /// <returns>The events.</returns>
   public string[] Events()
   {
      lock( _events )
      {
         return _events.Select( e => e.Item ).ToArray();
      }
   }

   /// <summary>
   /// The Stopwatch time of each event so far.
   /// </summary>
   /// <returns>The ticks.</returns>
   public long[] Ticks()
   {
      lock( _events )
      {
         return _events.Select( e => e.Ticks ).ToArray();
      }
   }

   /// <summary>
   /// Searches in flight as each event started (0 for log lines).
   /// </summary>
   /// <returns>The counts.</returns>
   public int[] InFlight()
   {
      lock( _events )
      {
         return _events.Select( e => e.InFlight ).ToArray();
      }
   }

   #endregion Public Methods
}

/// <summary>
/// An in-memory sink with a scripted latency per default search:
/// "fixed:MS" waits MS every time; "bimodal:MS" answers 2 of 5 searches at once and waits MS for
/// the rest; "stall:MS:AT:STALLMS" waits MS except search number AT, which waits STALLMS;
/// "decay" waits 6, 5, 4 and 3 ms for 150 searches each and 2 ms after that; "alternate" waits
/// 1 ms and 4 ms in turns of 100 searches; "drop:HIGH:LOW" waits HIGH ms until
/// <see cref="Drop"/> and LOW after (an engine that is still getting faster after it looked
/// settled); "climb" waits 1 ms more every 100 searches (it never settles); "jit:HIGH:LOW:N"
/// waits HIGH ms until it has served N searches that started while another was in flight, and
/// LOW after (an engine whose concurrent paths warm up only under concurrent load); "jump:A:B"
/// waits A ms until <see cref="Jump"/> and B after (an engine that changes once the first timed
/// pass has begun); "fail" throws every time.
/// </summary>
public class ScriptedSink : ISink
{
   #region Data Members

   private readonly Dictionary<Guid, float[]> _rows = new();
   private readonly string _script;
   private int _searches = -1;
   private int _inFlight;
   private int _concurrentServed;
   private int _restarted;
   private volatile bool _dropped;
   private volatile bool _jumped;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="trace">Where calls are recorded.</param>
   /// <param name="script">Latency script of default searches.</param>
   public ScriptedSink( MeasureTrace trace, string script )
   {
      Trace = trace;
      _script = script;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Sink name.</summary>
   public string Name => "fake";

   /// <summary>Where calls are recorded.</summary>
   public MeasureTrace Trace { get; }

   /// <summary>Searches served that started while another search was in flight.</summary>
   public int ConcurrentServed => Volatile.Read( ref _concurrentServed );

   /// <summary>
   /// The collection always exists.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="dimension">Dimension.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True.</returns>
   public Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      return Task.FromResult( true );
   }

   /// <summary>
   /// Stores the records.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="records">Records.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A completed task.</returns>
   public Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      lock( _rows )
      {
         records.ToList().ForEach( r => _rows[r.Chunk.ChunkId] = r.Vector );
      }

      return Task.CompletedTask;
   }

   /// <summary>
   /// Removes rows.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="chunkIds">Ids.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A completed task.</returns>
   public Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      lock( _rows )
      {
         chunkIds.ToList().ForEach( id => _rows.Remove( id ) );
      }

      return Task.CompletedTask;
   }

   /// <summary>
   /// Rows held.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The count.</returns>
   public Task<long> CountAsync( string collection, CancellationToken ct )
   {
      lock( _rows )
      {
         return Task.FromResult( (long)_rows.Count );
      }
   }

   /// <summary>
   /// Default search: records "S" with the searches in flight, waits as scripted, answers exactly.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      int inFlight = Interlocked.Increment( ref _inFlight );
      try
      {
         Trace.Add( "S", inFlight );
         await WaitAsync( Current( inFlight ), Interlocked.Increment( ref _searches ), ct );
         return Nearest( vector, top );
      }
      finally
      {
         Interlocked.Decrement( ref _inFlight );
      }
   }

   /// <summary>
   /// Starts the latency script again from search number 0, the first time only.
   /// </summary>
   public void RestartOnce()
   {
      if( Interlocked.Exchange( ref _restarted, 1 ) == 0 )
      {
         Interlocked.Exchange( ref _searches, -1 );
      }
   }

   /// <summary>
   /// Switches a "drop" script to its low latency from now on.
   /// </summary>
   public void Drop()
   {
      _dropped = true;
   }

   /// <summary>
   /// Switches a "jump" script to its second latency from now on.
   /// </summary>
   public void Jump()
   {
      _jumped = true;
   }

   /// <summary>
   /// Drops every row.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A completed task.</returns>
   public Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      lock( _rows )
      {
         _rows.Clear();
      }

      return Task.CompletedTask;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Waits as the script says for search number <paramref name="n"/>.
   /// </summary>
   /// <param name="script">The script.</param>
   /// <param name="n">Search number, from 0.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A task that ends after the wait.</returns>
   protected static async Task WaitAsync( string script, int n, CancellationToken ct )
   {
      string[] parts = script.Split( ':' );
      double ms = parts[0] switch
      {
         "fixed" => double.Parse( parts[1] ),
         "bimodal" => n % 5 < 2 ? 0 : double.Parse( parts[1] ),
         "stall" => n == int.Parse( parts[2] ) ? double.Parse( parts[3] ) : double.Parse( parts[1] ),
         "decay" => n < 600 ? 6 - n / 150 : 2,
         "alternate" => n / 100 % 2 == 0 ? 1 : 4,
         "drop" => double.Parse( parts[1] ),
         "jump" => double.Parse( parts[1] ),
         "climb" => 1 + n / 100,
         "fail" => throw new InvalidOperationException( "scripted failure" ),
         _ => throw new ArgumentException( $"unknown script {script}" ),
      };
      if( ms > 0 )
      {
         await Task.Delay( TimeSpan.FromMilliseconds( ms ), ct );
      }
   }

   /// <summary>
   /// Exact nearest rows by dot product (the vectors are unit length).
   /// </summary>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <returns>Hits, best first.</returns>
   protected IReadOnlyList<SearchHit> Nearest( float[] vector, int top )
   {
      List<KeyValuePair<Guid, float[]>> rows;
      lock( _rows )
      {
         rows = _rows.ToList();
      }

      return rows.Select( r => new SearchHit( r.Key, "doc", "m", string.Empty, r.Value.Zip( vector, ( a, b ) => (double)a * b ).Sum() ) )
         .OrderByDescending( h => h.Score ).Take( top ).ToList();
   }

   /// <summary>
   /// The script for this search: a dropped "drop" becomes its low latency, and "jit" becomes
   /// its high or low latency by how many concurrent searches it has served.
   /// </summary>
   /// <param name="inFlight">Searches in flight, this one included.</param>
   /// <returns>The script to wait by.</returns>
   private string Current( int inFlight )
   {
      string[] parts = _script.Split( ':' );
      if( parts[0] == "drop" && _dropped )
      {
         return "fixed:" + parts[2];
      }

      if( parts[0] == "jump" )
      {
         return "fixed:" + ( _jumped ? parts[2] : parts[1] );
      }

      if( parts[0] != "jit" )
      {
         return _script;
      }

      int served = inFlight > 1 ? Interlocked.Increment( ref _concurrentServed ) : Volatile.Read( ref _concurrentServed );
      return "fixed:" + ( served > int.Parse( parts[3] ) ? parts[2] : parts[1] );
   }

   #endregion Private Methods
}

/// <summary>
/// A scripted sink with an exact mode (records "X") whose latency has its own script.
/// </summary>
public sealed class ScriptedExactSink : ScriptedSink, IExactSearchSink
{
   #region Data Members

   private readonly string _exactScript;
   private int _exactSearches = -1;
   private int _exactInFlight;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="trace">Where calls are recorded.</param>
   /// <param name="script">Latency script of default searches.</param>
   /// <param name="exactScript">Latency script of exact searches.</param>
   public ScriptedExactSink( MeasureTrace trace, string script, string exactScript ) : base( trace, script )
   {
      _exactScript = exactScript;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Exact search: records "X" with the exact searches in flight, waits as scripted, answers exactly.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   public async Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      int inFlight = Interlocked.Increment( ref _exactInFlight );
      try
      {
         Trace.Add( "X", inFlight );
         await WaitAsync( _exactScript, Interlocked.Increment( ref _exactSearches ), ct );
         return Nearest( vector, top );
      }
      finally
      {
         Interlocked.Decrement( ref _exactInFlight );
      }
   }

   #endregion Public Methods
}

/// <summary>
/// An engine host for targets that need no starting: every call is an error, because an
/// embedded target must never reach it.
/// </summary>
public sealed class NoHost : IEngineHost
{
   #region Public Methods

   /// <summary>
   /// Never called for an embedded target.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Never returns.</returns>
   public Task<bool> IsRunningAsync( string composePath, CancellationToken ct )
   {
      throw new InvalidOperationException( "embedded target reached the engine host" );
   }

   /// <summary>
   /// Never called for an embedded target.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="cpuset">CPU set.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Never returns.</returns>
   public Task<string?> UpAsync( string composePath, string? cpuset, CancellationToken ct )
   {
      throw new InvalidOperationException( "embedded target reached the engine host" );
   }

   /// <summary>
   /// Never called for an embedded target.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Never returns.</returns>
   public Task<string?> DownAsync( string composePath, CancellationToken ct )
   {
      throw new InvalidOperationException( "embedded target reached the engine host" );
   }

   #endregion Public Methods
}
#endif
