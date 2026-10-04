using System.Diagnostics;
using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Data;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Stats;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Bench.Truth;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Searches one loaded target with the shared queries and measures it in timed windows.
/// Preparation (<see cref="PrepareAsync"/>), untimed: a rehearsal of every pass type for at least
/// <see cref="REHEARSAL_TIME"/> each, then a settle that repeats default searches until their
/// latency stops moving. Timed passes (<see cref="RunAsync"/>), in a seeded random order
/// (<see cref="RunOrder"/>), each after its own warm-up: "default@1" (one searcher for the set
/// seconds; every search's latency gives p50/p95/p99 and the completed count gives QPS@1, its
/// first round gives recall and nDCG), "default@N" (N searchers for the set seconds) and "exact"
/// (the engine's exact mode, one searcher for the exact seconds, cycling the queries).
/// Why one window for latency and QPS@1: when p50 came from a separate 200-search burst, the
/// burst and the throughput window ran at different moments (and clock speeds), so p50 and
/// 1000/QPS@1 described different seconds and the pass order moved them.
/// Why the rehearsal: a per-pass warm-up of 20 searches does not get the client's own code for
/// that pass compiled or its connections pooled, so whichever pass ran first paid for it.
/// Why the settle: engines that compile at run time (the JVM ones) keep getting faster for a while
/// after they start; timing them seconds after start measured the warm-up, not the engine.
/// Latency is wall-clock time around the sink's SearchAsync, measured by the client: it includes
/// the network hop and the driver, which is what an application sees.
/// </summary>
public sealed class SearchRunner
{
   #region Data Members

   /// <summary>Fewest timed searches a latency window should give; fewer is flagged, because a p99 of 20 samples is just the slowest one.</summary>
   public const int MIN_LATENCY_SAMPLES = 200;

   /// <summary>Searches in one settle window (the p50 is taken over each window).</summary>
   public const int SETTLE_WINDOW = 100;

   /// <summary>Consecutive settle windows whose p50s must agree.</summary>
   public const int SETTLE_WINDOWS = 3;

   /// <summary>Most the p50s of those windows may differ, as a share of the lowest.</summary>
   public const double SETTLE_TOLERANCE = 0.05;

   /// <summary>A p50 above this many times the mean of the same window is flagged.</summary>
   public const double SKEW_LIMIT = 1.25;

   /// <summary>Name of the settle step, recorded as what ran before the first timed pass.</summary>
   public const string SETTLE_STEP = "settle";

   /// <summary>Default rehearsal length per pass type.</summary>
   public static readonly TimeSpan REHEARSAL_TIME = TimeSpan.FromSeconds( 5 );

   /// <summary>Default longest the settle may run.</summary>
   public static readonly TimeSpan SETTLE_CAP = TimeSpan.FromSeconds( 120 );

   private const int GIVE_UP_FAILURES = 20;
   private static readonly TimeSpan WARMUP_BUDGET = TimeSpan.FromSeconds( 60 );

   private readonly PipelineData _data;
   private readonly QuerySet _queries;
   private readonly TruthSet _truth;
   private readonly BenchOptions _options;
   private readonly int _runSeed;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the runner.
   /// </summary>
   /// <param name="data">Loaded rows (for exact similarities and file paths).</param>
   /// <param name="queries">Queries.</param>
   /// <param name="truth">Exact answers.</param>
   /// <param name="options">Options (top, concurrency, seconds, exact seconds, warm-up, timeouts).</param>
   /// <param name="runSeed">The run's seed; with the target's name it fixes the pass order.</param>
   public SearchRunner( PipelineData data, QuerySet queries, TruthSet truth, BenchOptions options, int runSeed )
   {
      _data = data;
      _queries = queries;
      _truth = truth;
      _options = options;
      _runSeed = runSeed;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// One search call: query vector, hits wanted, cancellation.
   /// </summary>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">Hits wanted.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   public delegate Task<IReadOnlyList<SearchHit>> SearchCall( float[] vector, int top, CancellationToken ct );

   /// <summary>
   /// Rehearsal length per pass type. Why settable: tests shorten it; the benchmark never does.
   /// </summary>
   public TimeSpan RehearsalTime { get; init; } = REHEARSAL_TIME;

   /// <summary>
   /// Longest the settle may run. Why settable: tests shorten it; the benchmark never does.
   /// </summary>
   public TimeSpan SettleCap { get; init; } = SETTLE_CAP;

   /// <summary>
   /// The untimed preparation of a target whose index is ready: a rehearsal of every pass type
   /// (default at each concurrency, and exact) for <see cref="RehearsalTime"/> each, through the
   /// same window code the timed passes use, then the settle.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="collection">Benchmark collection name.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What the rehearsal and the settle did.</returns>
   /// <exception cref="InvalidOperationException">Every default-search rehearsal search failed; the target cannot be timed.</exception>
   public async Task<Preparation> PrepareAsync( BenchTarget target, string collection, Action<string> log, CancellationToken ct )
   {
      ( SearchCall search, SearchCall? exact ) = Calls( target.Sink, collection );
      var preparation = new Preparation();
      foreach( string pass in PassesFor( target.Name, exact != null ) )
      {
         log( $"  {target.Name}: rehearsal of {pass}, {RehearsalTime.TotalSeconds:0.#} s untimed" );
         SearchWindow window = await WindowAsync( pass == RunOrder.EXACT_PASS ? exact! : search, ConcurrencyOf( pass ), RehearsalTime, false, ct );
         preparation.AddRehearsal( new RehearsalRecord( pass, window.Seconds, window.Searches, window.Errors ), window.FirstError );
      }

      if( preparation.Rehearsals.Where( r => r.Pass != RunOrder.EXACT_PASS ).All( r => r.Searches == 0 ) )
      {
         throw new InvalidOperationException( $"every rehearsal search failed (first: {preparation.FirstError ?? "none sent"}), so the target was not timed" );
      }

      log( $"  {target.Name}: settling, one search at a time until the p50s of {SETTLE_WINDOWS} windows of {SETTLE_WINDOW} searches agree within {SETTLE_TOLERANCE:0%}, at most {SettleCap.TotalSeconds:0} s" );
      preparation.Settle = await SettleAsync( search, ct );
      log( $"  {target.Name}: {( preparation.Settle.Settled ? "settled" : "NOT settled" )} after {preparation.Settle.Seconds:0.0} s and {preparation.Settle.Searches:N0} searches" );
      return preparation;
   }

   /// <summary>
   /// Measures a target: every timed pass, in this target's seeded order, each after its own
   /// warm-up.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="collection">Benchmark collection name.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="preparation">The preparation that ran just before, or null when none did.</param>
   /// <returns>What was measured, the pass order and pass records, the warm-up tally and the flags.</returns>
   public async Task<SearchOutcome> RunAsync( BenchTarget target, string collection, Action<string> log, CancellationToken ct, Preparation? preparation = null )
   {
      ISink sink = target.Sink;
      var outcome = new SearchOutcome( new SearchReport { CountInTarget = await CountAsync( sink, collection, ct ) }, preparation );
      ( SearchCall search, SearchCall? exact ) = Calls( sink, collection );
      string? previous = preparation == null ? null : SETTLE_STEP;
      foreach( string pass in PassesFor( target.Name, exact != null ) )
      {
         SearchCall call = pass == RunOrder.EXACT_PASS ? exact! : search;
         int concurrency = ConcurrencyOf( pass );
         log( $"  {target.Name}: warm-up before {pass}, {_options.Warmup} searches" );
         outcome.Add( pass, await WarmUpAsync( call, concurrency, ct ), log, target.Name );
         log( $"  {target.Name}: timing {pass}" );
         outcome.PassOrder.Add( pass );
         await RunPassAsync( pass, call, concurrency, outcome, previous, log, target.Name, ct );
         previous = pass;
      }

      return outcome;
   }

   /// <summary>
   /// Scores a set of answers: mean recall@k, and mean nDCG@k for golden queries. Queries
   /// without an answer (failed searches) are left out of the means; they are counted as
   /// errors instead.
   /// </summary>
   /// <param name="answers">Per query, the hits returned, or null.</param>
   /// <returns>Mean recall and mean nDCG (null for random queries).</returns>
   public (double Recall, double? Ndcg) Score( IReadOnlyList<SearchHit>?[] answers )
   {
      var recalls = new List<double>();
      var ndcgs = new List<double>();
      for( int q = 0; q < answers.Length; q++ )
      {
         if( answers[q] is not IReadOnlyList<SearchHit> raw )
         {
            continue;
         }

         IReadOnlyList<SearchHit> hits = Clean( raw, q );
         float[] query = _queries.Vectors[q];
         List<Guid> truthIds = _truth.Rows[q].Select( _data.Id ).ToList();
         recalls.Add( BenchMath.RecallAtK( truthIds, _truth.KthScore( q ), hits.Select( h => h.ChunkId ).ToList(), id => _data.Similarity( query, id ), _options.Top ) );
         if( _queries.Grades != null )
         {
            IEnumerable<string> files = hits.Select( h => _data.TryGetRow( h.ChunkId, out int row ) ? _data.FilePath( row ) : h.DocKey );
            ndcgs.Add( BenchMath.NdcgAtK( BenchMath.FirstPerFile( files ), _queries.Grades[q], _options.Top ) );
         }
      }

      return ( BenchMath.MeanOfNumbers( recalls ), _queries.IsGolden ? BenchMath.MeanOfNumbers( ndcgs ) : null );
   }

   /// <summary>
   /// nDCG@k of the exact answer itself: what a perfect engine would score with this embedder.
   /// </summary>
   /// <returns>Mean nDCG, or null for random queries.</returns>
   public double? TruthNdcg()
   {
      if( _queries.Grades == null )
      {
         return null;
      }

      IEnumerable<double> scores = _truth.Rows.Select( ( rows, q ) =>
         BenchMath.NdcgAtK( BenchMath.FirstPerFile( rows.Select( _data.FilePath ) ), _queries.Grades[q], _options.Top ) );
      return BenchMath.MeanOfNumbers( scores );
   }

   /// <summary>
   /// True when the p50s of the last <see cref="SETTLE_WINDOWS"/> settle windows lie within
   /// <see cref="SETTLE_TOLERANCE"/> of each other (highest minus lowest, over the lowest).
   /// Why the spread of all three and not each step: three steps of 4% in one direction are a
   /// 12% drift, which is still warming up.
   /// </summary>
   /// <param name="windowP50s">p50 of each settle window so far, oldest first.</param>
   /// <returns>True when settled.</returns>
   public static bool IsSettled( IReadOnlyList<double> windowP50s )
   {
      if( windowP50s.Count < SETTLE_WINDOWS )
      {
         return false;
      }

      double[] last = windowP50s.Skip( windowP50s.Count - SETTLE_WINDOWS ).ToArray();
      double low = last.Min();
      return low > 0 && ( last.Max() - low ) / low < SETTLE_TOLERANCE;
   }

   /// <summary>
   /// The flags for one latency window: too few samples, a p50 above
   /// <see cref="SKEW_LIMIT"/> times the mean, or a mean above the p99.
   /// Why: with every sample from one window a p50 well above the mean means the window held two
   /// speeds (a fast minority pulled the mean down), and a mean above the p99 means a handful of
   /// stalls outweigh everything else; either way the single numbers mislead on their own.
   /// </summary>
   /// <param name="pass">Pass name, for the text.</param>
   /// <param name="latencies">Every timed latency of the window, ms.</param>
   /// <returns>The WARNING lines (none when the window looks sound).</returns>
   public static IEnumerable<string> ShapeFlags( string pass, IReadOnlyList<double> latencies )
   {
      if( latencies.Count < MIN_LATENCY_SAMPLES )
      {
         yield return $"WARNING: {pass} timed only {latencies.Count} searches (fewer than {MIN_LATENCY_SAMPLES}), so its p95 and p99 rest on the few slowest searches.";
      }

      if( latencies.Count == 0 )
      {
         yield break;
      }

      double p50 = BenchMath.Percentile( latencies, 50 );
      double p99 = BenchMath.Percentile( latencies, 99 );
      double mean = latencies.Average();
      if( p50 > SKEW_LIMIT * mean )
      {
         yield return $"WARNING: {pass} p50 {p50:0.00} ms is more than {SKEW_LIMIT} x its mean {mean:0.00} ms in the same window, so the window mixed two speeds; do not quote its p50 alone.";
      }

      if( mean > p99 )
      {
         yield return $"WARNING: {pass} mean {mean:0.00} ms is above its p99 {p99:0.00} ms, so a few stalls outweigh every other search; do not quote its mean or QPS without the p99.";
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The target's row count as it reports it, or null when it cannot count.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Rows held, or null.</returns>
   private static async Task<long?> CountAsync( ISink sink, string collection, CancellationToken ct )
   {
      try
      {
         return await sink.CountAsync( collection, ct );
      }
      catch( NotSupportedException )
      {
         return null;
      }
   }

   /// <summary>
   /// The default search and, when the sink has one and it is not switched off, the exact search.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="collection">Collection name.</param>
   /// <returns>The two calls (exact null when not measured).</returns>
   private (SearchCall Search, SearchCall? Exact) Calls( ISink sink, string collection )
   {
      SearchCall search = ( v, top, c ) => sink.SearchAsync( collection, v, top, c );
      SearchCall? exact = sink is IExactSearchSink exactSink && _options.ExactSeconds > 0
         ? ( v, top, c ) => exactSink.SearchExactAsync( collection, v, top, c )
         : null;
      return ( search, exact );
   }

   /// <summary>
   /// This target's passes in its seeded order.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="hasExact">True when the exact mode is measured.</param>
   /// <returns>Pass names in run order.</returns>
   private IReadOnlyList<string> PassesFor( string name, bool hasExact )
   {
      return RunOrder.ShufflePasses( RunOrder.Passes( _options.Concurrency, hasExact ), _runSeed, name );
   }

   /// <summary>
   /// Searchers a pass uses: N for "default@N", 1 for the exact mode.
   /// </summary>
   /// <param name="pass">Pass name.</param>
   /// <returns>Searchers.</returns>
   private static int ConcurrencyOf( string pass )
   {
      return pass != RunOrder.EXACT_PASS && RunOrder.TryParseDefault( pass, out int level ) ? level : 1;
   }

   /// <summary>
   /// Runs one timed pass, fills the report, adds the pass record and its flags, and logs it.
   /// </summary>
   /// <param name="pass">Pass name.</param>
   /// <param name="call">The search the pass times (default or exact).</param>
   /// <param name="concurrency">Searchers in flight.</param>
   /// <param name="outcome">Outcome to fill.</param>
   /// <param name="previous">The timed pass (or step) that ran before this one, or null.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="name">Target name, for the log.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task RunPassAsync( string pass, SearchCall call, int concurrency, SearchOutcome outcome, string? previous, Action<string> log, string name, CancellationToken ct )
   {
      bool isExact = pass == RunOrder.EXACT_PASS;
      SearchWindow window = await WindowAsync( call, concurrency, TimeSpan.FromSeconds( isExact ? _options.ExactSeconds : _options.Seconds ), concurrency == 1, ct );
      SearchReport report = outcome.Report;
      report.Errors += window.Errors;
      report.FirstError ??= window.FirstError == null ? null : $"{pass}: {window.FirstError}";
      List<double> latencies = window.Latencies();
      PassRecord record = Record( pass, previous, window, latencies );
      outcome.Passes.Add( record );
      if( concurrency > 1 )
      {
         report.Qps[concurrency] = record.Qps;
         log( $"  {name}: {record.Qps:0.0} QPS at concurrency {concurrency}" );
         return;
      }

      outcome.Flags.AddRange( ShapeFlags( pass, latencies ) );
      outcome.Flags.AddRange( CoverageFlags( pass, window ) );
      if( isExact )
      {
         FillExact( report, window, latencies );
         log( $"  {name}: exact mode p50 {report.ExactP50Ms:0.00} ms over {report.ExactQueries} searches, recall {report.ExactRecall:0.000}" );
         return;
      }

      FillLatency( report, window, latencies, record.Qps );
      log( $"  {name}: p50 {report.P50Ms:0.00} ms, p95 {report.P95Ms:0.00} ms over {report.LatencySamples} searches, {record.Qps:0.0} QPS with one searcher, recall@{_options.Top} {report.Recall:0.000}{( report.Ndcg.HasValue ? $", nDCG {report.Ndcg:0.000}" : string.Empty )}" );
   }

   /// <summary>
   /// Fills the default@1 numbers from its one window: percentiles of every search, QPS@1 from
   /// the same searches (when 1 is a concurrency level), recall and nDCG from the first answer
   /// to each query.
   /// </summary>
   /// <param name="report">Report to fill.</param>
   /// <param name="window">The default@1 window.</param>
   /// <param name="latencies">Its latencies, ms.</param>
   /// <param name="qps">Completed searches per second in the window.</param>
   private void FillLatency( SearchReport report, SearchWindow window, IReadOnlyList<double> latencies, double qps )
   {
      report.LatencySamples = latencies.Count;
      report.P50Ms = Nullable( BenchMath.Percentile( latencies, 50 ) );
      report.P95Ms = Nullable( BenchMath.Percentile( latencies, 95 ) );
      report.P99Ms = Nullable( BenchMath.Percentile( latencies, 99 ) );
      if( _options.Concurrency.Contains( 1 ) )
      {
         report.Qps[1] = qps;
      }

      ( double recall, double? ndcg ) = Score( window.Answers );
      report.Recall = Nullable( recall );
      report.Ndcg = ndcg.HasValue ? Nullable( ndcg.Value ) : null;
   }

   /// <summary>
   /// Fills the exact-mode numbers from its window: searches timed, p50, p95 and recall (which
   /// must be 1.0).
   /// </summary>
   /// <param name="report">Report to fill.</param>
   /// <param name="window">The exact window.</param>
   /// <param name="latencies">Its latencies, ms.</param>
   private void FillExact( SearchReport report, SearchWindow window, IReadOnlyList<double> latencies )
   {
      report.ExactQueries = latencies.Count;
      report.ExactP50Ms = Nullable( BenchMath.Percentile( latencies, 50 ) );
      report.ExactP95Ms = Nullable( BenchMath.Percentile( latencies, 95 ) );
      report.ExactRecall = Nullable( Score( window.Answers ).Recall );
   }

   /// <summary>
   /// Flags a scored window that did not answer every query once: its recall covers only part
   /// of the query set.
   /// </summary>
   /// <param name="pass">Pass name.</param>
   /// <param name="window">The window.</param>
   /// <returns>The WARNING line, if any.</returns>
   private IEnumerable<string> CoverageFlags( string pass, SearchWindow window )
   {
      int answered = window.Answers.Count( a => a != null );
      if( answered < _queries.Count )
      {
         yield return $"WARNING: {pass} answered {answered} of {_queries.Count} queries within its window (twice the set seconds at most), so its recall covers only those.";
      }
   }

   /// <summary>
   /// The record of one timed pass.
   /// </summary>
   /// <param name="pass">Pass name.</param>
   /// <param name="previous">What ran before it.</param>
   /// <param name="window">Its window.</param>
   /// <param name="latencies">Its latencies, ms.</param>
   /// <returns>The record.</returns>
   private static PassRecord Record( string pass, string? previous, SearchWindow window, IReadOnlyList<double> latencies )
   {
      double? Stat( Func<double> f ) => latencies.Count == 0 ? null : f();
      return new PassRecord( pass, previous, window.StartUtc, window.EndUtc, window.Seconds, latencies.Count, window.Errors,
         Stat( () => BenchMath.Percentile( latencies, 50 ) ), Stat( () => latencies.Average() ), Stat( () => BenchMath.Percentile( latencies, 99 ) ),
         window.Seconds > 0 ? latencies.Count / window.Seconds : 0 );
   }

   /// <summary>
   /// Repeats default searches one at a time, in windows of <see cref="SETTLE_WINDOW"/>, until
   /// <see cref="IsSettled"/>, <see cref="SettleCap"/> runs out, or
   /// <see cref="GIVE_UP_FAILURES"/> searches in a row fail.
   /// </summary>
   /// <param name="search">The default search.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Whether it settled, how long it took, and the window p50s.</returns>
   private async Task<SettleResult> SettleAsync( SearchCall search, CancellationToken ct )
   {
      var p50s = new List<double>();
      var block = new List<double>( SETTLE_WINDOW );
      int searches = 0;
      int errors = 0;
      int failedInRow = 0;
      string? first = null;
      long start = Stopwatch.GetTimestamp();
      while( !IsSettled( p50s ) && Stopwatch.GetElapsedTime( start ) < SettleCap && failedInRow < GIVE_UP_FAILURES )
      {
         ( _, double ms, string? error ) = await TimedAsync( search, searches++ % _queries.Count, ct );
         failedInRow = error == null ? 0 : failedInRow + 1;
         if( error != null )
         {
            errors++;
            first ??= error;
            continue;
         }

         block.Add( ms );
         if( block.Count == SETTLE_WINDOW )
         {
            p50s.Add( BenchMath.Percentile( block, 50 ) );
            block.Clear();
         }
      }

      string? stopped = IsSettled( p50s ) ? null : failedInRow >= GIVE_UP_FAILURES ? $"{GIVE_UP_FAILURES} searches in a row failed" : $"the {SettleCap.TotalSeconds:0} s cap ran out";
      return new SettleResult( stopped == null, Stopwatch.GetElapsedTime( start ).TotalSeconds, searches, errors, first, p50s, stopped );
   }

   /// <summary>
   /// Untimed searches right before a timed pass, sent by as many searchers as the pass uses
   /// (so a pass with 8 searchers starts with 8 warm connections), with the pass's own search.
   /// Stops early after <see cref="WARMUP_BUDGET"/> so a slow engine cannot stall the run here.
   /// </summary>
   /// <param name="search">The search the pass will time.</param>
   /// <param name="concurrency">Searchers the pass uses.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Searches sent, how many failed, the first failure, and whether time ran out.</returns>
   private async Task<WarmupResult> WarmUpAsync( SearchCall search, int concurrency, CancellationToken ct )
   {
      int total = _options.Warmup;
      int next = -1;
      int sent = 0;
      int errors = 0;
      string? first = null;
      long start = Stopwatch.GetTimestamp();
      Task[] workers = Enumerable.Range( 0, Math.Min( concurrency, total ) ).Select( worker => Task.Run( async () =>
      {
         for( int i = Interlocked.Increment( ref next ); i < total && Stopwatch.GetElapsedTime( start ) < WARMUP_BUDGET; i = Interlocked.Increment( ref next ) )
         {
            ( _, _, string? error ) = await TimedAsync( search, i % _queries.Count, ct );
            Interlocked.Increment( ref sent );
            if( error != null )
            {
               Interlocked.Increment( ref errors );
               Interlocked.CompareExchange( ref first, error, null );
            }
         }
      }, ct ) ).ToArray();
      await Task.WhenAll( workers );
      return new WarmupResult( sent, errors, first, sent < total );
   }

   /// <summary>
   /// Runs <paramref name="concurrency"/> searchers back to back for <paramref name="duration"/>,
   /// cycling the queries, and keeps every search's latency. Every timed pass and every
   /// rehearsal goes through here, so the rehearsal compiles exactly the code that is timed.
   /// A scored window also keeps the first answer to each query and, if the duration ends before
   /// every query was sent once, runs on until it was, but never past twice the duration.
   /// A searcher that has not completed one search stops once <see cref="GIVE_UP_FAILURES"/>
   /// searches of the window failed. Why: an engine that refuses every search fails in
   /// microseconds, and spinning on it for the whole window only heats the box; the errors are
   /// still counted, so the target still shows as failed.
   /// </summary>
   /// <param name="search">The search.</param>
   /// <param name="concurrency">Searchers.</param>
   /// <param name="duration">Window length.</param>
   /// <param name="scored">True to keep answers for recall (one-searcher windows).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The window's samples, errors, answers and times.</returns>
   private async Task<SearchWindow> WindowAsync( SearchCall search, int concurrency, TimeSpan duration, bool scored, CancellationToken ct )
   {
      var window = new SearchWindow( concurrency, _queries.Count );
      long next = -1;
      long start = Stopwatch.GetTimestamp();
      window.StartUtc = DateTime.UtcNow;
      Task[] workers = Enumerable.Range( 0, concurrency ).Select( worker => Task.Run( async () =>
      {
         List<double> mine = window.PerWorker[worker];
         for( long i = Interlocked.Increment( ref next ); InWindow( Stopwatch.GetElapsedTime( start ), duration, scored && i < _queries.Count ); i = Interlocked.Increment( ref next ) )
         {
            if( mine.Count == 0 && window.Errors >= GIVE_UP_FAILURES )
            {
               break;
            }

            int query = (int)( i % _queries.Count );
            ( IReadOnlyList<SearchHit>? hits, double ms, string? error ) = await TimedAsync( search, query, ct );
            if( error != null )
            {
               window.Fail( error );
               continue;
            }

            mine.Add( ms );
            if( scored )
            {
               Interlocked.CompareExchange( ref window.Answers[query], hits, null );
            }
         }
      }, ct ) ).ToArray();
      await Task.WhenAll( workers );
      window.Seconds = Stopwatch.GetElapsedTime( start ).TotalSeconds;
      window.EndUtc = DateTime.UtcNow;
      return window;
   }

   /// <summary>
   /// Whether a window sends another search: while its duration lasts, or past it (up to twice
   /// the duration) while a query of the first round has not been sent yet.
   /// </summary>
   /// <param name="elapsed">Time since the window started.</param>
   /// <param name="duration">Window length.</param>
   /// <param name="roundUnfinished">True when the next search is still part of the first round of a scored window.</param>
   /// <returns>True to send it.</returns>
   private static bool InWindow( TimeSpan elapsed, TimeSpan duration, bool roundUnfinished )
   {
      return elapsed < duration || ( roundUnfinished && elapsed < duration + duration );
   }

   /// <summary>
   /// Runs one search with the per-search time limit.
   /// </summary>
   /// <param name="search">The search.</param>
   /// <param name="query">Query index.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The hits (null on failure), milliseconds, and the failure message.</returns>
   private async Task<(IReadOnlyList<SearchHit>? Hits, double Ms, string? Error)> TimedAsync( SearchCall search, int query, CancellationToken ct )
   {
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( TimeSpan.FromSeconds( _options.SearchTimeoutSeconds ) );
      long start = Stopwatch.GetTimestamp();
      try
      {
         IReadOnlyList<SearchHit> hits = await search( _queries.Vectors[query], _queries.RequestSize( _options.Top ), limit.Token );
         return ( hits, Stopwatch.GetElapsedTime( start ).TotalMilliseconds, null );
      }
      catch( Exception ex ) when( !ct.IsCancellationRequested )
      {
         string error = ex is OperationCanceledException ? $"no answer within {_options.SearchTimeoutSeconds} s" : ex.Message;
         return ( null, Stopwatch.GetElapsedTime( start ).TotalMilliseconds, error );
      }
   }

   /// <summary>
   /// Removes the query's own row (random queries) and keeps the top k.
   /// </summary>
   /// <param name="hits">Hits as returned.</param>
   /// <param name="query">Query index.</param>
   /// <returns>The hits that are scored.</returns>
   private IReadOnlyList<SearchHit> Clean( IReadOnlyList<SearchHit> hits, int query )
   {
      int self = _queries.SelfRows[query];
      Guid selfId = self >= 0 ? _data.Id( self ) : Guid.Empty;
      return hits.Where( h => self < 0 || h.ChunkId != selfId ).Take( _options.Top ).ToList();
   }

   /// <summary>
   /// NaN (nothing measured) becomes null, so the JSON says "missing" instead of a fake number.
   /// </summary>
   /// <param name="value">Value.</param>
   /// <returns>The value or null.</returns>
   private static double? Nullable( double value )
   {
      return double.IsNaN( value ) ? null : value;
   }

   #endregion Private Methods
}

/// <summary>
/// One window of searches: every searcher's latencies, the errors, the first answer to each
/// query, and when it ran.
/// Why per-searcher lists: the searchers add a sample after every search, and a shared list
/// would need a lock inside the timed loop.
/// </summary>
public sealed class SearchWindow
{
   #region Data Members

   private int _errors;
   private string? _firstError;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty window.
   /// </summary>
   /// <param name="searchers">Searchers in flight.</param>
   /// <param name="queries">Queries in the set.</param>
   public SearchWindow( int searchers, int queries )
   {
      PerWorker = Enumerable.Range( 0, searchers ).Select( _ => new List<double>() ).ToArray();
      Answers = new IReadOnlyList<SearchHit>?[queries];
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Latencies (ms) of the completed searches, one list per searcher.</summary>
   public List<double>[] PerWorker { get; }

   /// <summary>First answer to each query (scored windows only), or null.</summary>
   public IReadOnlyList<SearchHit>?[] Answers { get; }

   /// <summary>When the window started (UTC).</summary>
   public DateTime StartUtc { get; set; }

   /// <summary>When the last searcher finished (UTC).</summary>
   public DateTime EndUtc { get; set; }

   /// <summary>Seconds from the start until the last searcher finished.</summary>
   public double Seconds { get; set; }

   /// <summary>Searches that failed or timed out.</summary>
   public int Errors => Volatile.Read( ref _errors );

   /// <summary>The first failure message, or null.</summary>
   public string? FirstError => Volatile.Read( ref _firstError );

   /// <summary>Completed searches.</summary>
   public int Searches => PerWorker.Sum( w => w.Count );

   /// <summary>
   /// Counts a failed search.
   /// </summary>
   /// <param name="error">Its message.</param>
   public void Fail( string error )
   {
      Interlocked.Increment( ref _errors );
      Interlocked.CompareExchange( ref _firstError, error, null );
   }

   /// <summary>
   /// Every latency of the window, all searchers together.
   /// </summary>
   /// <returns>The latencies, ms.</returns>
   public List<double> Latencies()
   {
      return PerWorker.SelectMany( w => w ).ToList();
   }

   #endregion Public Methods
}

/// <summary>
/// One timed pass as it ran.
/// Why: a reader can only judge an order effect, a slow start or a clock change if each number
/// says when it was taken, from how many searches, and what ran just before it.
/// </summary>
/// <param name="Name">Pass name, e.g. "default@1".</param>
/// <param name="Previous">The timed pass that ran just before ("settle" for the first after a settle), or null.</param>
/// <param name="StartUtc">When its window started.</param>
/// <param name="EndUtc">When its window ended.</param>
/// <param name="Seconds">Window length in seconds.</param>
/// <param name="Searches">Completed (timed) searches.</param>
/// <param name="Errors">Searches that failed or timed out.</param>
/// <param name="P50Ms">Median latency, ms, or null.</param>
/// <param name="MeanMs">Mean latency, ms, or null.</param>
/// <param name="P99Ms">99th percentile latency, ms, or null.</param>
/// <param name="Qps">Completed searches per second.</param>
public sealed record PassRecord( string Name, string? Previous, DateTime StartUtc, DateTime EndUtc, double Seconds, int Searches, int Errors,
   double? P50Ms, double? MeanMs, double? P99Ms, double Qps );

/// <summary>
/// One pass type's rehearsal.
/// </summary>
/// <param name="Pass">Pass name.</param>
/// <param name="Seconds">How long it ran.</param>
/// <param name="Searches">Completed searches.</param>
/// <param name="Errors">Failed searches.</param>
public sealed record RehearsalRecord( string Pass, double Seconds, int Searches, int Errors );

/// <summary>
/// How the settle went.
/// </summary>
/// <param name="Settled">True when the window p50s agreed before the cap.</param>
/// <param name="Seconds">How long it ran.</param>
/// <param name="Searches">Searches sent.</param>
/// <param name="Errors">Searches that failed.</param>
/// <param name="FirstError">The first failure, or null.</param>
/// <param name="WindowP50s">p50 of each full window, ms, oldest first.</param>
/// <param name="StoppedBecause">Why it stopped without settling, or null.</param>
public sealed record SettleResult( bool Settled, double Seconds, int Searches, int Errors, string? FirstError, IReadOnlyList<double> WindowP50s, string? StoppedBecause );

/// <summary>
/// One warm-up's tally.
/// </summary>
/// <param name="Searches">Warm-up searches sent.</param>
/// <param name="Errors">How many of them failed or timed out.</param>
/// <param name="FirstError">The first failure message, or null.</param>
/// <param name="CutShort">True when the warm-up time budget ran out before every search was sent.</param>
public sealed record WarmupResult( int Searches, int Errors, string? FirstError, bool CutShort );

/// <summary>
/// The untimed preparation of one target: its rehearsals and its settle.
/// </summary>
public sealed class Preparation
{
   #region Public Methods

   /// <summary>Each pass type's rehearsal, in the order run.</summary>
   public List<RehearsalRecord> Rehearsals { get; } = new();

   /// <summary>The settle, or null when it did not run.</summary>
   public SettleResult? Settle { get; set; }

   /// <summary>The first rehearsal failure, with its pass, or null.</summary>
   public string? FirstError { get; private set; }

   /// <summary>Searches sent by the rehearsals and the settle (completed and failed).</summary>
   public int Searches => Rehearsals.Sum( r => r.Searches + r.Errors ) + ( Settle?.Searches ?? 0 );

   /// <summary>Searches of the rehearsals and the settle that failed.</summary>
   public int Errors => Rehearsals.Sum( r => r.Errors ) + ( Settle?.Errors ?? 0 );

   /// <summary>
   /// Adds one pass type's rehearsal.
   /// </summary>
   /// <param name="record">The rehearsal.</param>
   /// <param name="firstError">Its first failure, or null.</param>
   public void AddRehearsal( RehearsalRecord record, string? firstError )
   {
      Rehearsals.Add( record );
      FirstError ??= firstError == null ? null : $"rehearsal of {record.Pass}: {firstError}";
   }

   #endregion Public Methods
}

/// <summary>
/// What searching one target produced: the measurements, the passes in the order they ran with
/// a record of each, the warm-up tally, the preparation and the flags.
/// Why the order, the records and the warm-ups are kept beside the numbers: a reader can only
/// judge an order effect or a failing warm-up if the results say what happened.
/// </summary>
public sealed class SearchOutcome
{
   #region Constructor

   /// <summary>
   /// Creates the outcome around a report.
   /// </summary>
   /// <param name="report">The search report the passes fill.</param>
   /// <param name="preparation">The preparation that ran before the passes, or null.</param>
   public SearchOutcome( SearchReport report, Preparation? preparation = null )
   {
      Report = report;
      Preparation = preparation;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The search measurements.</summary>
   public SearchReport Report { get; }

   /// <summary>The rehearsal and settle that ran before the passes, or null.</summary>
   public Preparation? Preparation { get; }

   /// <summary>Timed passes in the order they ran.</summary>
   public List<string> PassOrder { get; } = new();

   /// <summary>One record per timed pass, in the order run.</summary>
   public List<PassRecord> Passes { get; } = new();

   /// <summary>WARNING lines about the measurements themselves (too few samples, skew, partial recall).</summary>
   public List<string> Flags { get; } = new();

   /// <summary>Warm-up searches sent, over every per-pass warm-up.</summary>
   public int WarmupSearches { get; private set; }

   /// <summary>Warm-up searches that failed, over every per-pass warm-up.</summary>
   public int WarmupErrors { get; private set; }

   /// <summary>Every untimed search sent: per-pass warm-ups, rehearsals and settle.</summary>
   public int UntimedSearches => WarmupSearches + ( Preparation?.Searches ?? 0 );

   /// <summary>Every untimed search that failed: per-pass warm-ups, rehearsals and settle.</summary>
   public int UntimedErrors => WarmupErrors + ( Preparation?.Errors ?? 0 );

   /// <summary>The first warm-up failure, with the pass it preceded.</summary>
   public string? FirstWarmupError { get; private set; }

   /// <summary>Passes whose warm-up ran out of time before sending every search.</summary>
   public List<string> WarmupsCutShort { get; } = new();

   /// <summary>
   /// Adds one warm-up's tally and logs any trouble with it.
   /// </summary>
   /// <param name="pass">The pass the warm-up preceded.</param>
   /// <param name="warmup">The tally.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="name">Target name, for the log.</param>
   public void Add( string pass, WarmupResult warmup, Action<string> log, string name )
   {
      WarmupSearches += warmup.Searches;
      WarmupErrors += warmup.Errors;
      if( warmup.FirstError != null )
      {
         FirstWarmupError ??= $"before {pass}: {warmup.FirstError}";
         log( $"  {name}: {warmup.Errors} of {warmup.Searches} warm-up searches before {pass} FAILED (first: {warmup.FirstError})" );
      }

      if( warmup.CutShort )
      {
         WarmupsCutShort.Add( pass );
         log( $"  {name}: warm-up before {pass} stopped after {warmup.Searches} searches (time budget)" );
      }
   }

   #endregion Public Methods
}
