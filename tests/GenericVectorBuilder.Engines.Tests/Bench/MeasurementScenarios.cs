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
/// window, a rehearsal of every pass type, a settle that waits for falling latency, per-pass
/// records) are about what the runner measures, and only a sink with a known latency shape
/// shows whether the runner measured it right.
/// </summary>
public static class MeasurementScenarios
{
   #region Data Members

   /// <summary>Collection every scenario searches, as the benchmark names it.</summary>
   public const string COLLECTION = "gvbbench_m";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Runs every timed pass (no preparation) on a sink with the given latency script.
   /// </summary>
   /// <param name="args">Extra benchmark arguments (seconds, exact-seconds, concurrency, queries).</param>
   /// <param name="defaultScript">Latency script of default searches: "fixed:MS", "bimodal:MS" or "stall:MS:AT:STALLMS".</param>
   /// <param name="exactScript">Latency script of exact searches, or null for a sink without an exact mode.</param>
   /// <param name="withPreparation">True to pass a (empty) preparation so the first pass reads "after settle".</param>
   /// <returns>The trace, the report numbers, the pass records and the flags.</returns>
   public static async Task<PassScenario> PassesAsync( string[] args, string defaultScript, string? exactScript, bool withPreparation )
   {
      ( BenchOptions options, PipelineData data, QuerySet queries ) = Setup( args );
      var trace = new MeasureTrace();
      ScriptedSink sink = exactScript == null ? new ScriptedSink( trace, defaultScript ) : new ScriptedExactSink( trace, defaultScript, exactScript );
      await sink.UpsertAsync( COLLECTION, data.Records( 0, data.Count ), CancellationToken.None );
      var runner = new SearchRunner( data, queries, BruteForce.Compute( data, queries, options.Top ), options, 3 );
      SearchOutcome outcome = await runner.RunAsync( Target( sink ), COLLECTION, line => trace.Add( "LOG " + line ), CancellationToken.None, withPreparation ? new Preparation() : null );
      SearchReport r = outcome.Report;
      return new PassScenario( trace.Events(), outcome.PassOrder.ToArray(), outcome.Passes.Select( Pass ).ToArray(), outcome.Flags.ToArray(),
         r.LatencySamples, r.P50Ms, r.P95Ms, r.P99Ms, r.Qps.ToDictionary( p => p.Key, p => p.Value ), r.Recall, r.ExactQueries, r.ExactP50Ms, r.ExactRecall, r.Errors );
   }

   /// <summary>
   /// Runs only the preparation (rehearsal and settle) with a short rehearsal and cap.
   /// </summary>
   /// <param name="args">Extra benchmark arguments.</param>
   /// <param name="defaultScript">Latency script of default searches.</param>
   /// <param name="rehearsalSeconds">Rehearsal length per pass type.</param>
   /// <param name="capSeconds">Settle cap.</param>
   /// <returns>The trace and what the preparation reported, or the error it threw.</returns>
   public static async Task<PrepareScenario> PrepareAsync( string[] args, string defaultScript, double rehearsalSeconds, double capSeconds )
   {
      ( BenchOptions options, PipelineData data, QuerySet queries ) = Setup( args );
      var trace = new MeasureTrace();
      var sink = new ScriptedExactSink( trace, defaultScript, "fixed:1" );
      await sink.UpsertAsync( COLLECTION, data.Records( 0, data.Count ), CancellationToken.None );
      var runner = new SearchRunner( data, queries, BruteForce.Compute( data, queries, options.Top ), options, 3 )
      {
         RehearsalTime = TimeSpan.FromSeconds( rehearsalSeconds ),
         SettleCap = TimeSpan.FromSeconds( capSeconds ),
      };
      try
      {
         Preparation p = await runner.PrepareAsync( Target( sink ), COLLECTION, line => Log( trace, sink, line ), CancellationToken.None );
         SettleResult s = p.Settle!;
         return new PrepareScenario( trace.Events(), p.Rehearsals.Select( x => $"{x.Pass}|{x.Seconds}|{x.Searches}|{x.Errors}" ).ToArray(),
            s.Settled, s.Seconds, s.Searches, s.WindowP50s.ToArray(), s.StoppedBecause, TargetRunner.DescribeSettle( s ), null );
      }
      catch( InvalidOperationException ex )
      {
         return new PrepareScenario( trace.Events(), Array.Empty<string>(), false, 0, 0, Array.Empty<double>(), null, string.Empty, ex.Message );
      }
   }

   /// <summary>
   /// Measures one target the way run-all does, with the default rehearsal and settle cap, and
   /// writes results.json.
   /// </summary>
   /// <param name="defaultScript">Latency script of default searches.</param>
   /// <param name="folder">Folder for the results.</param>
   /// <returns>The trace, notes, error, pass order and results.json text.</returns>
   public static async Task<TargetRun> MeasureAsync( string defaultScript, string folder )
   {
      var trace = new MeasureTrace();
      BenchOptions options = BenchOptions.Parse( new[] { "run-all", "--pipeline", "m", "--warmup", "3", "--seconds", "1", "--exact-seconds", "1",
         "--concurrency", "1,2", "--queries", "random:5", "--top", "3", "--seed", "9" } );
      PipelineData data = Data( 30, 4 );
      QuerySet queries = QuerySet.Random( data, options.RandomCount );
      var runner = new SearchRunner( data, queries, BruteForce.Compute( data, queries, options.Top ), options, 9 );
      var measurer = new TargetRunner( options, new EngineLifecycle( new NoHost(), true, _ => { } ), line => trace.Add( "LOG " + line ) );
      TargetReport result = await measurer.MeasureAsync( Target( new ScriptedExactSink( trace, defaultScript, "fixed:1" ) ), data, runner, CancellationToken.None );
      var report = new BenchReport { RunSeed = 9, Command = "run-all", Pipeline = "m" };
      report.Targets.Add( result );
      ResultsWriter.Write( report, folder );
      return new TargetRun( trace.Events(), result.Notes.ToArray(), result.Error, result.PassOrder?.ToArray() ?? Array.Empty<string>(), result.WarmupErrors,
         File.ReadAllText( Path.Combine( folder, "results.json" ) ) );
   }

   /// <summary>
   /// The preparation and every timed pass of one target with short rehearsal, settle, trial and
   /// extension times, on a sink whose latency script restarts when the settle begins: shows what
   /// the settle check saw and did, the progress lines in order, the settle note TargetRunner
   /// writes, and whether the consolidation's reader (TargetResult) reads that note as settled.
   /// </summary>
   /// <param name="script">Latency script of default searches.</param>
   /// <param name="trialSeconds">Trial length.</param>
   /// <param name="extensionMinimum">Shortest extension, seconds.</param>
   /// <param name="extensionCap">Longest extension, seconds.</param>
   /// <param name="settleCap">Cap of the first settle, seconds.</param>
   /// <returns>What the check did.</returns>
   public static async Task<CheckScenario> SettleCheckAsync( string script, double trialSeconds, double extensionMinimum, double extensionCap, double settleCap )
   {
      ( BenchOptions options, PipelineData data, QuerySet queries ) = Setup( new[] { "--seconds", "1", "--concurrency", "1", "--queries", "random:5", "--exact-seconds", "0" } );
      var trace = new MeasureTrace();
      var sink = new ScriptedSink( trace, script );
      await sink.UpsertAsync( COLLECTION, data.Records( 0, data.Count ), CancellationToken.None );
      var runner = new SearchRunner( data, queries, BruteForce.Compute( data, queries, options.Top ), options, 3 )
      {
         RehearsalTime = TimeSpan.FromSeconds( 0.2 ),
         SettleCap = TimeSpan.FromSeconds( settleCap ),
         TrialTime = TimeSpan.FromSeconds( trialSeconds ),
         ExtensionMinimum = TimeSpan.FromSeconds( extensionMinimum ),
         ExtensionCap = TimeSpan.FromSeconds( extensionCap ),
      };
      Action<string> log = line => Log( trace, sink, line );
      Preparation p = await runner.PrepareAsync( Target( sink ), COLLECTION, log, CancellationToken.None );
      SearchOutcome outcome = await runner.RunAsync( Target( sink ), COLLECTION, log, CancellationToken.None, p );
      SettleCheck? c = p.Check;
      string note = TargetRunner.DescribeSettle( p.Settle, c );
      using var doc = System.Text.Json.JsonDocument.Parse( System.Text.Json.JsonSerializer.Serialize( new { name = "fake", notes = new[] { note } } ) );
      TargetResult read = TargetResult.Parse( doc.RootElement );
      return new CheckScenario( trace.Events().Where( e => e.StartsWith( "LOG ", StringComparison.Ordinal ) ).ToArray(), p.Settle!.Settled, SearchRunner.SettledP50( p.Settle.WindowP50s ),
         c?.Trial.P50, c?.Trial.Agrees ?? false, c?.Extension != null, c?.Extension?.Seconds ?? 0, c?.Retrial?.P50, c?.Retrial?.Agrees ?? false, c?.Confirmed ?? false,
         note, read.Settled, outcome.PassOrder.ToArray(), p.Searches, c?.Searches ?? 0 );
   }

   /// <summary>
   /// The settle note for hand-made settle checks, each with how the consolidation's reader
   /// (TargetResult) reads it: "agreed", "extended-ok", "extended-bad", "gave-up", "unchecked".
   /// </summary>
   /// <param name="mode">Which case.</param>
   /// <returns>The note and whether the reader calls it settled (null when it says nothing).</returns>
   public static NoteScenario SettleNote( string mode )
   {
      var settled = new SettleResult( true, 0.4, 300, 0, null, new[] { 2.91, 2.92, 2.82 }, null );
      var unsettled = new SettleResult( false, 120, 9000, 20, "x", new[] { 1.0, 4.0, 1.0 }, "20 searches in a row failed", true );
      var trialBad = new TrialRecord( 2.91, 1.34, 1490, 0, 2.0 );
      var trialOk = new TrialRecord( 1.33, 1.34, 1500, 0, 2.0 );
      var extension = new SettleResult( true, 30.1, 22000, 0, null, new[] { 1.33, 1.32, 1.34 }, null );
      ( SettleResult s, SettleCheck? c ) = mode switch
      {
         "agreed" => ( settled, new SettleCheck( trialOk with { SettledP50 = 1.35 }, null, null, true ) ),
         "extended-ok" => ( settled, new SettleCheck( trialBad, extension, trialOk, true ) ),
         "extended-bad" => ( settled, new SettleCheck( trialBad, extension, trialBad with { SettledP50 = 1.33, P50 = 0.9 }, false ) ),
         "gave-up" => ( unsettled, new SettleCheck( trialBad, null, null, false ) ),
         _ => ( settled, (SettleCheck?)null ),
      };
      string note = TargetRunner.DescribeSettle( s, c );
      using var doc = System.Text.Json.JsonDocument.Parse( System.Text.Json.JsonSerializer.Serialize( new { name = "fake", notes = new[] { note } } ) );
      return new NoteScenario( note, TargetResult.Parse( doc.RootElement ).Settled );
   }

   /// <summary>
   /// <see cref="SearchRunner.SettledP50"/> and <see cref="SearchRunner.TrialAgrees"/> on given numbers.
   /// </summary>
   /// <param name="p50s">Window p50s.</param>
   /// <param name="settled">Settled p50 for the agreement check.</param>
   /// <param name="trial">Trial p50 for the agreement check.</param>
   /// <returns>The settled p50 (NaN for none) and whether they agree.</returns>
   public static double[] TrialRules( double[] p50s, double settled, double trial )
   {
      return new[] { SearchRunner.SettledP50( p50s ) ?? double.NaN, SearchRunner.TrialAgrees( settled, trial ) ? 1 : 0 };
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
   /// <see cref="SearchRunner.IsSettled"/> for a list of window p50s.
   /// </summary>
   /// <param name="p50s">Window p50s, ms.</param>
   /// <returns>True when settled.</returns>
   public static bool IsSettled( double[] p50s )
   {
      return SearchRunner.IsSettled( p50s );
   }

   /// <summary>The benchmark's default rehearsal length, settle cap, trial length and tolerance, and extension minimum and cap.</summary>
   /// <returns>Rehearsal s, cap s, trial s, tolerance, extension minimum s, extension cap s.</returns>
   public static double[] Defaults()
   {
      return new[] { SearchRunner.REHEARSAL_TIME.TotalSeconds, SearchRunner.SETTLE_CAP.TotalSeconds, SearchRunner.TRIAL_TIME.TotalSeconds, SearchRunner.TRIAL_TOLERANCE,
         SearchRunner.EXTENSION_MINIMUM.TotalSeconds, SearchRunner.EXTENSION_CAP.TotalSeconds };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Records a progress line, restarts the sink's latency script when the settle begins, so a
   /// script like "decay" plays out inside the settle and not inside the rehearsal, and drops a
   /// "drop" script to its low latency as soon as the settle reports that it settled.
   /// </summary>
   /// <param name="trace">Trace.</param>
   /// <param name="sink">The sink.</param>
   /// <param name="line">The progress line.</param>
   private static void Log( MeasureTrace trace, ScriptedSink sink, string line )
   {
      trace.Add( "LOG " + line );
      if( line.Contains( ": settling,", StringComparison.Ordinal ) )
      {
         sink.Restart();
      }

      if( line.Contains( ": settled after", StringComparison.Ordinal ) )
      {
         sink.Drop();
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

/// <summary>What <see cref="MeasurementScenarios.PassesAsync"/> saw.</summary>
/// <param name="Events">"S" default search, "X" exact search, "LOG ..." lines, in order.</param>
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
public sealed record PassScenario( string[] Events, string[] PassOrder, string[] Passes, string[] Flags, int LatencySamples, double? P50, double? P95, double? P99,
   Dictionary<int, double> Qps, double? Recall, int ExactQueries, double? ExactP50, double? ExactRecall, int Errors );

/// <summary>What <see cref="MeasurementScenarios.PrepareAsync"/> saw.</summary>
/// <param name="Events">Trace.</param>
/// <param name="Rehearsals">pass|seconds|searches|errors per rehearsal.</param>
/// <param name="Settled">Settle result.</param>
/// <param name="SettleSeconds">Settle time.</param>
/// <param name="SettleSearches">Settle searches.</param>
/// <param name="WindowP50s">Settle window p50s.</param>
/// <param name="StoppedBecause">Why the settle stopped unsettled.</param>
/// <param name="SettleNote">The settle note TargetRunner writes.</param>
/// <param name="Error">The preparation's exception message, or null.</param>
public sealed record PrepareScenario( string[] Events, string[] Rehearsals, bool Settled, double SettleSeconds, int SettleSearches, double[] WindowP50s,
   string? StoppedBecause, string SettleNote, string? Error );

/// <summary>What <see cref="MeasurementScenarios.SettleCheckAsync"/> saw.</summary>
/// <param name="Log">Progress lines in order.</param>
/// <param name="FirstSettled">The first settle settled.</param>
/// <param name="SettledP50">Its settled p50.</param>
/// <param name="TrialP50">The trial's p50.</param>
/// <param name="TrialAgreed">The trial agreed.</param>
/// <param name="Extended">The settle was extended.</param>
/// <param name="ExtensionSeconds">How long the extension ran.</param>
/// <param name="RetrialP50">The second trial's p50.</param>
/// <param name="RetrialAgreed">The second trial agreed.</param>
/// <param name="Confirmed">The check's verdict.</param>
/// <param name="Note">The settle note.</param>
/// <param name="ReadSettled">How the consolidation's reader reads the note.</param>
/// <param name="PassOrder">Passes as run.</param>
/// <param name="PreparationSearches">Untimed searches of the preparation, check included.</param>
/// <param name="CheckSearches">Searches of the check alone.</param>
public sealed record CheckScenario( string[] Log, bool FirstSettled, double? SettledP50, double? TrialP50, bool TrialAgreed, bool Extended, double ExtensionSeconds,
   double? RetrialP50, bool RetrialAgreed, bool Confirmed, string Note, bool? ReadSettled, string[] PassOrder, int PreparationSearches, int CheckSearches );

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
/// An ordered, thread-safe event list with the time of each event.
/// </summary>
public sealed class MeasureTrace
{
   #region Data Members

   private readonly List<string> _events = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Appends an event.
   /// </summary>
   /// <param name="item">The event.</param>
   public void Add( string item )
   {
      lock( _events )
      {
         _events.Add( item );
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
         return _events.ToArray();
      }
   }

   #endregion Public Methods
}

/// <summary>
/// An in-memory sink with exact search and a scripted latency per default search:
/// "fixed:MS" waits MS every time; "bimodal:MS" answers 2 of 5 searches at once and waits MS for
/// the rest; "stall:MS:AT:STALLMS" waits MS except search number AT, which waits STALLMS;
/// "decay" waits 6, 5, 4 and 3 ms for 150 searches each and 2 ms after that; "alternate" waits
/// 1 ms and 4 ms in turns of 100 searches;
/// "drop:HIGH:LOW" waits HIGH ms until <see cref="ScriptedSink.Drop"/> and LOW after (an engine
/// that is still getting faster after it "settled"); "climb"
/// waits 1 ms more every 100 searches (it never settles);
/// "fail" throws every time.
/// </summary>
public class ScriptedSink : ISink
{
   #region Data Members

   private readonly Dictionary<Guid, float[]> _rows = new();
   private readonly string _script;
   private int _searches = -1;
   private volatile bool _dropped;

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
   /// Default search: records "S", waits as scripted, answers exactly.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      Trace.Add( "S" );
      string script = _dropped && _script.StartsWith( "drop:", StringComparison.Ordinal ) ? "fixed:" + _script.Split( ':' )[2] : _script;
      await WaitAsync( script, Interlocked.Increment( ref _searches ), ct );
      return Nearest( vector, top );
   }

   /// <summary>
   /// Starts the latency script again from search number 0.
   /// </summary>
   public void Restart()
   {
      Interlocked.Exchange( ref _searches, -1 );
   }

   /// <summary>
   /// Switches a "drop" script to its low latency from now on.
   /// </summary>
   public void Drop()
   {
      _dropped = true;
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
   /// Exact search: records "X", waits as scripted, answers exactly.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   public async Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      Trace.Add( "X" );
      await WaitAsync( _exactScript, Interlocked.Increment( ref _exactSearches ), ct );
      return Nearest( vector, top );
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
