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
/// Preparation (<see cref="PrepareAsync"/>), untimed: a rehearsal of every pass type, each at its
/// own concurrency, for at least <see cref="REHEARSAL_TIME"/>. Timed passes (<see cref="RunAsync"/>),
/// in a seeded random order (<see cref="RunOrder"/>): "default@1" (one searcher for the set seconds;
/// every search's latency gives p50/p95/p99 and the completed count gives QPS@1, its first round
/// gives recall and nDCG), "default@N" (N searchers for the set seconds) and "exact" (the engine's
/// exact mode, one searcher for the exact seconds, cycling the queries).
/// Right before every timed pass, untimed: its warm-up, which is also its settle: the pass's own
/// search at the pass's own concurrency for at least <see cref="WarmupTime"/> (and at least
/// --warmup searches), its completions cut into windows of at least <see cref="WindowTime"/> and
/// <see cref="SETTLE_WINDOW"/> searches. After a preparation the settle check follows: a short
/// trial of that same pass (<see cref="TrialTime"/>, same search, same searchers). Its figure (p50
/// with one searcher, QPS with several, the number the pass reports) must lie within
/// <see cref="TRIAL_TOLERANCE"/> of the warm-up's settled figure (the median of its last
/// <see cref="SETTLE_WINDOWS"/> windows, which must agree within <see cref="SETTLE_TOLERANCE"/>).
/// When it does not, the warm-up is extended once (at least <see cref="ExtensionMinimum"/>, at
/// most <see cref="ExtensionCap"/>, until its level test passes: the older and the newer half of
/// its latest windows, each half read as the pass reads it, within <see cref="SETTLE_TOLERANCE"/>,
/// see <see cref="LevelOf"/>), a second trial is taken (it agrees inside the range of the newer
/// half's windows or within <see cref="TRIAL_TOLERANCE"/> of the settled figure, see
/// <see cref="TrialRecord"/>), both are recorded, the warm-up is announced and run again, and a
/// pass that still disagrees flags its target as not settled. After every timed pass its own
/// figure is held against the settled figure; more than <see cref="TRIAL_TOLERANCE"/> away flags
/// the target too (see <see cref="SettleCheck"/>).
/// Why the extension is judged by halves and not by its last three windows (v7): an engine whose
/// speed swings from one 2 s window to the next with no trend never shows three windows within 5%.
/// In the three v6 runs Oracle Free at 8 searchers ran its extension to the 120 s cap every time
/// (its engine CPUs stop for 0.25 to 0.4 s in every 1.25 s, its own 2-CPU cap, so a 2 s window
/// holds one stop or two), while its timed QPS@8 agreed run to run within 2.5%.
/// Why time-based and at the pass's own concurrency: the v5 runs timed Vespa at 914 and 977 QPS@8
/// when default@8 ran first and at 1550 after a 60 s exact pass (engine CPU per search 4.3 against
/// 2.5 ms). Its 20 warm-up searches at 8 searchers lasted milliseconds, and its settle ran one
/// searcher at a time, so nothing before a first default@8 pass had loaded the engine the way the
/// pass does. The settle also declared Vespa, Elasticsearch and OpenSearch settled at a p50 13 to
/// 51% above the timed one: windows of 100 searches last a fraction of a second on a fast engine.
/// Why the rehearsal: a per-pass warm-up alone does not get the client's own code for every pass
/// compiled or its connections pooled before the first timed pass, so whichever pass ran first
/// paid for it.
/// Latency is wall-clock time around the sink's SearchAsync, measured by the client: it includes
/// the network hop and the driver, which is what an application sees.
/// </summary>
public sealed class SearchRunner
{
   #region Data Members

   /// <summary>Fewest timed searches a latency window should give; fewer is flagged, because a p99 of 20 samples is just the slowest one.</summary>
   public const int MIN_LATENCY_SAMPLES = 200;

   /// <summary>Fewest searches a settle window holds; a window shorter in searches is merged with the next.</summary>
   public const int SETTLE_WINDOW = 100;

   /// <summary>Consecutive settle windows whose figures must agree.</summary>
   public const int SETTLE_WINDOWS = 3;

   /// <summary>Most the figures of those windows may differ, as a share of the lowest.</summary>
   public const double SETTLE_TOLERANCE = 0.05;

   /// <summary>
   /// Fewest windows in each half of the extension's level test (see <see cref="LevelOf"/>).
   /// Why 5 (10 s or more a half): a half must span several cycles of an engine whose speed cycles
   /// every second or two (Oracle Free at 8 searchers, about 1.25 s), or the halves differ only by
   /// where the cycle fell.
   /// </summary>
   public const int LEVEL_HALF_WINDOWS = 5;

   /// <summary>
   /// Most windows in each half of the extension's level test: it reads only the latest windows.
   /// Why: a disturbance early in a long extension must not keep it unsettled once it has passed;
   /// halves of the whole 120 s would still carry it.
   /// </summary>
   public const int LEVEL_MAX_HALF_WINDOWS = 10;

   /// <summary>
   /// Smallest difference, in ms, between two one-searcher p50 figures that counts as a change in
   /// the checks that flag a target (the extension's level test, the second trial and the hold of
   /// the timed pass); smaller differences agree whatever their percentage. Why: at a p50 under a
   /// millisecond a relative tolerance is finer than this box can resolve one searcher's latency.
   /// Measured 2026-10-06 (v7 diagnosis runs): Redis's one-searcher p50 sits on one of two steady
   /// levels about 0.05 ms apart (0.32 and 0.37 ms), in either order, for 10 to 40 s at a time, with
   /// the searches on all four client CPUs moving together and no trend, while its timed pass and its
   /// settled figure can land on different levels (13 to 15% apart). 0.1 ms covers that gap with room
   /// and stays below the 0.133 ms this box's CPUs take to wake from their deepest idle state (C6),
   /// which a single searcher's request can pay on either side. Above 1 ms the 10% limit is already
   /// wider than the floor, so only sub-millisecond engines are affected.
   /// </summary>
   public const double P50_FLOOR_MS = 0.1;

   /// <summary>A p50 above this many times the mean of the same window is flagged.</summary>
   public const double SKEW_LIMIT = 1.25;

   /// <summary>Name of the step before the first timed pass, recorded as what ran before it.</summary>
   public const string REHEARSAL_STEP = "rehearsal";

   /// <summary>Name of the settle check, in the log and the notes.</summary>
   public const string CHECK_STEP = "settle check";

   /// <summary>
   /// Most the warm-up's settled figure may differ from the trial's figure, as a share of the
   /// trial's. Why 10%: the v5 settle passed figures 13% to 51% off the timed ones at 15%.
   /// </summary>
   public const double TRIAL_TOLERANCE = 0.10;

   /// <summary>Default rehearsal length per pass type.</summary>
   public static readonly TimeSpan REHEARSAL_TIME = TimeSpan.FromSeconds( 30 );

   /// <summary>Default shortest warm-up before every timed pass.</summary>
   public static readonly TimeSpan WARMUP_TIME = TimeSpan.FromSeconds( 15 );

   /// <summary>
   /// Default shortest settle window. Why 2 s: a QPS read over one second at 8 searchers moves a
   /// few percent with a single garbage collection, which is noise, not warm-up.
   /// </summary>
   public static readonly TimeSpan WINDOW_TIME = TimeSpan.FromSeconds( 2 );

   /// <summary>
   /// Default longest one warm-up may run: it ends at its time once it has also sent the --warmup
   /// minimum of searches, or here when an engine is too slow to send them.
   /// </summary>
   public static readonly TimeSpan SETTLE_CAP = TimeSpan.FromSeconds( 120 );

   /// <summary>Default length of the trial right before a timed pass.</summary>
   public static readonly TimeSpan TRIAL_TIME = TimeSpan.FromSeconds( 3 );

   /// <summary>Default longest the one extension of a warm-up may run.</summary>
   public static readonly TimeSpan EXTENSION_CAP = TimeSpan.FromSeconds( 120 );

   /// <summary>
   /// Default shortest the extension runs. Why: the windows can agree within seconds on a fast
   /// engine, and an extension that stops as soon as they do gives a JVM engine no more time.
   /// </summary>
   public static readonly TimeSpan EXTENSION_MINIMUM = TimeSpan.FromSeconds( 30 );

   private const int GIVE_UP_FAILURES = 20;
   private static readonly TimeSpan SETTLE_POLL = TimeSpan.FromMilliseconds( 100 );

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
   /// <param name="options">Options (top, concurrency, seconds, exact seconds, warm-up searches, timeouts).</param>
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

   /// <summary>Rehearsal length per pass type. Why settable: tests shorten it; the benchmark uses the default.</summary>
   public TimeSpan RehearsalTime { get; init; } = REHEARSAL_TIME;

   /// <summary>
   /// Shortest warm-up before every timed pass. Why settable: the benchmark can be told another
   /// length, and tests shorten it; zero leaves only the --warmup count of searches (no windows,
   /// so a settle check after it extends).
   /// </summary>
   public TimeSpan WarmupTime { get; init; } = WARMUP_TIME;

   /// <summary>Shortest settle window. Why settable: tests shorten it; the benchmark uses the default.</summary>
   public TimeSpan WindowTime { get; init; } = WINDOW_TIME;

   /// <summary>Longest one warm-up may run. Why settable: tests shorten it; the benchmark uses the default.</summary>
   public TimeSpan SettleCap { get; init; } = SETTLE_CAP;

   /// <summary>Trial length. Why settable: tests shorten it; the benchmark uses the default.</summary>
   public TimeSpan TrialTime { get; init; } = TRIAL_TIME;

   /// <summary>Longest the extension may run. Why settable: tests shorten it; the benchmark uses the default.</summary>
   public TimeSpan ExtensionCap { get; init; } = EXTENSION_CAP;

   /// <summary>Shortest the extension runs. Why settable: tests shorten it; the benchmark uses the default.</summary>
   public TimeSpan ExtensionMinimum { get; init; } = EXTENSION_MINIMUM;

   /// <summary>
   /// The untimed preparation of a target whose index is ready: a rehearsal of every pass type
   /// (default at each concurrency, and exact), each at its own concurrency for
   /// <see cref="RehearsalTime"/>, through the same window code the timed passes use. A rehearsal
   /// whose window ended before any of its searchers sent a search is run once more.
   /// Why: with the short rehearsals of the tests, a starved thread pool can start the searchers
   /// after the window has already closed (RunnerTests.Finisher_CalledAndStateRecorded failed that
   /// way on 2026-10-06 with "every rehearsal search failed (first: none sent)"); nothing had failed,
   /// so throwing as if the engine had refused every search was wrong. The benchmark's 30 s
   /// rehearsal never ends before its searchers start.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="collection">Benchmark collection name.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What the rehearsal did; the settle checks are added to it as the passes run.</returns>
   /// <exception cref="InvalidOperationException">Every default-search rehearsal search failed (or none was sent, twice); the target cannot be timed.</exception>
   public async Task<Preparation> PrepareAsync( BenchTarget target, string collection, Action<string> log, CancellationToken ct )
   {
      ( SearchCall search, SearchCall? exact ) = Calls( target.Sink, collection );
      var preparation = new Preparation();
      foreach( string pass in PassesFor( target.Name, exact != null ) )
      {
         int concurrency = ConcurrencyOf( pass );
         log( $"  {target.Name}: rehearsal of {pass}, {RehearsalTime.TotalSeconds:0.#} s untimed at {Searchers( concurrency )}" );
         SearchCall call = pass == RunOrder.EXACT_PASS ? exact! : search;
         SearchWindow window = await WindowAsync( call, concurrency, RehearsalTime, false, ct );
         if( window.Searches + window.Errors == 0 )
         {
            log( $"  {target.Name}: rehearsal of {pass} sent no search in its {window.Seconds:0.0#} s (its searchers had not started yet); running it once more" );
            window = await WindowAsync( call, concurrency, RehearsalTime, false, ct );
         }

         preparation.AddRehearsal( new RehearsalRecord( pass, window.Seconds, window.Searches, window.Errors ), window.FirstError );
      }

      if( preparation.Rehearsals.Where( r => r.Pass != RunOrder.EXACT_PASS ).All( r => r.Searches == 0 ) )
      {
         throw new InvalidOperationException( $"every rehearsal search failed (first: {preparation.FirstError ?? "none sent"}), so the target was not timed" );
      }

      return preparation;
   }

   /// <summary>
   /// Measures a target: every timed pass, in this target's seeded order, each right after its own
   /// warm-up and, when a preparation ran, its own settle check (see the class summary). Without
   /// a preparation each pass gets only its warm-up.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="collection">Benchmark collection name.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="preparation">The preparation that ran just before (the checks are added to it), or null when none did.</param>
   /// <returns>What was measured, the pass order and pass records, the warm-up tally and the flags.</returns>
   public async Task<SearchOutcome> RunAsync( BenchTarget target, string collection, Action<string> log, CancellationToken ct, Preparation? preparation = null )
   {
      ISink sink = target.Sink;
      var outcome = new SearchOutcome( new SearchReport { CountInTarget = await CountAsync( sink, collection, ct ) }, preparation );
      ( SearchCall search, SearchCall? exact ) = Calls( sink, collection );
      string? previous = preparation == null ? null : REHEARSAL_STEP;
      foreach( string pass in PassesFor( target.Name, exact != null ) )
      {
         SearchCall call = pass == RunOrder.EXACT_PASS ? exact! : search;
         int concurrency = ConcurrencyOf( pass );
         await ReadyAsync( pass, call, concurrency, outcome, log, target.Name, ct );
         log( $"  {target.Name}: timing {pass}" );
         outcome.PassOrder.Add( pass );
         await RunPassAsync( pass, call, concurrency, outcome, previous, log, target.Name, ct );
         HoldPass( pass, concurrency, outcome, log, target.Name );
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
   /// True when the figures of the last <see cref="SETTLE_WINDOWS"/> settle windows lie within
   /// <see cref="SETTLE_TOLERANCE"/> of each other (highest minus lowest, over the lowest).
   /// Why the spread of all three and not each step: three steps of 4% in one direction are a
   /// 12% drift, which is still warming up.
   /// </summary>
   /// <param name="windowFigures">Figure (p50 or QPS) of each settle window so far, oldest first.</param>
   /// <returns>True when settled.</returns>
   public static bool IsSettled( IReadOnlyList<double> windowFigures )
   {
      if( windowFigures.Count < SETTLE_WINDOWS )
      {
         return false;
      }

      double[] last = windowFigures.Skip( windowFigures.Count - SETTLE_WINDOWS ).ToArray();
      double low = last.Min();
      return low > 0 && ( last.Max() - low ) / low < SETTLE_TOLERANCE;
   }

   /// <summary>
   /// The settled figure: the median of the figures of the last <see cref="SETTLE_WINDOWS"/>
   /// windows (of fewer when fewer ran).
   /// </summary>
   /// <param name="windowFigures">Figure of each settle window, oldest first.</param>
   /// <returns>The figure, or null when no window completed.</returns>
   public static double? SettledFigure( IReadOnlyList<double> windowFigures )
   {
      if( windowFigures.Count == 0 )
      {
         return null;
      }

      List<double> last = windowFigures.Skip( Math.Max( 0, windowFigures.Count - SETTLE_WINDOWS ) ).OrderBy( p => p ).ToList();
      int middle = last.Count / 2;
      return last.Count % 2 == 1 ? last[middle] : ( last[middle - 1] + last[middle] ) / 2;
   }

   /// <summary>
   /// The extension's level test: its latest windows (at most twice <see cref="LEVEL_MAX_HALF_WINDOWS"/>)
   /// split into an older and a newer half of equal count (the oldest window left out when the
   /// count is odd), each half read as the pass reads it: with one searcher the p50 of all the
   /// half's searches, with several its searches per second. Null with fewer than
   /// <see cref="LEVEL_HALF_WINDOWS"/> windows a half.
   /// Why halves: warm-up is a level that keeps moving, and two halves of 10 s or more show it,
   /// while they average out a swing from one 2 s window to the next that has no trend.
   /// Why each half is read as the pass reads it and not as the mean of its window figures: a timed
   /// pass's p50 is the p50 of all its searches, and the mean of the p50s of windows from two
   /// speeds is not that.
   /// </summary>
   /// <param name="windows">Every window, oldest first.</param>
   /// <param name="concurrency">Searchers (one: halves read as a p50; several: as QPS).</param>
   /// <returns>The two halves, or null.</returns>
   public static SettleLevel? LevelOf( IReadOnlyList<SettleWindow> windows, int concurrency )
   {
      int half = Math.Min( windows.Count / 2, LEVEL_MAX_HALF_WINDOWS );
      if( half < LEVEL_HALF_WINDOWS )
      {
         return null;
      }

      List<SettleWindow> older = windows.Skip( windows.Count - 2 * half ).Take( half ).ToList();
      List<SettleWindow> newer = windows.Skip( windows.Count - half ).ToList();
      return new SettleLevel( HalfFigure( older, concurrency ), HalfFigure( newer, concurrency ), older.Select( w => w.Figure ).ToArray(), newer.Select( w => w.Figure ).ToArray(), concurrency );
   }

   /// <summary>
   /// True when two figures are one-searcher p50s (ms) no more than <see cref="P50_FLOOR_MS"/>
   /// apart: a difference this box cannot resolve at one searcher (see the constant).
   /// </summary>
   /// <param name="a">One figure.</param>
   /// <param name="b">The other.</param>
   /// <param name="concurrency">Searchers of the pass they belong to (the floor is for one searcher only).</param>
   /// <returns>True when inside the floor.</returns>
   public static bool WithinFloor( double a, double b, int concurrency )
   {
      return concurrency == 1 && Math.Abs( a - b ) <= P50_FLOOR_MS;
   }

   /// <summary>
   /// True when the settled figure lies within <see cref="TRIAL_TOLERANCE"/> of the trial's
   /// figure (the difference over the trial's figure). False when either is missing.
   /// </summary>
   /// <param name="settled">Settled figure.</param>
   /// <param name="trial">Trial figure.</param>
   /// <returns>True when they agree.</returns>
   public static bool TrialAgrees( double? settled, double? trial )
   {
      return TrialRecord.ApartOf( settled, trial ) is double apart && apart <= TRIAL_TOLERANCE;
   }

   /// <summary>
   /// The figure a pass at this concurrency reports, as text: "p50 1.871 ms" for one searcher,
   /// "1,524 QPS" for several.
   /// </summary>
   /// <param name="value">The figure.</param>
   /// <param name="concurrency">Searchers.</param>
   /// <returns>The text.</returns>
   public static string FigureText( double value, int concurrency )
   {
      return concurrency == 1 ? $"p50 {value:0.000} ms" : $"{value:N0} QPS";
   }

   /// <summary>
   /// "1 searcher" or "8 searchers".
   /// </summary>
   /// <param name="concurrency">Searchers.</param>
   /// <returns>The text.</returns>
   public static string Searchers( int concurrency )
   {
      return concurrency == 1 ? "1 searcher" : $"{concurrency} searchers";
   }

   /// <summary>
   /// The method notes for the run's results: the rehearsal, and the warm-up and settle check
   /// before every timed pass, with the lengths and limits the runner uses.
   /// Why here: the review of 2026-10-04 found the run's method notes describing an older
   /// method; kept next to the constants they quote, they change with the code. The phrases
   /// RunConditions and the web's BenchConditions read the lengths from (for example "extended
   /// once (at least 30 s, until its windows agree, at most 120 s)") are kept word for word.
   /// </summary>
   /// <param name="warmupSearches">The --warmup minimum of searches.</param>
   /// <param name="warmupTime">Shortest warm-up, or null for <see cref="WARMUP_TIME"/>.</param>
   /// <returns>The notes.</returns>
   public static IReadOnlyList<string> DescribeMethod( int warmupSearches, TimeSpan? warmupTime = null )
   {
      double warm = ( warmupTime ?? WARMUP_TIME ).TotalSeconds;
      return new[]
      {
         $"Preparation, untimed, before any timed pass: a rehearsal of every pass type at its own concurrency for {REHEARSAL_TIME.TotalSeconds:0} s each, through the same code the passes use.",
         $"Warm-up and settle check, untimed, right before every timed pass: the pass's own search at the pass's own number of searchers for at least {warm:0.#} s and at least {warmupSearches} searches (at most {SETTLE_CAP.TotalSeconds:0} s), "
            + $"read in windows of at least {WINDOW_TIME.TotalSeconds:0} s and {SETTLE_WINDOW} searches; then a {TRIAL_TIME.TotalSeconds:0} s trial of the same pass. The trial's figure (p50 with one searcher, QPS with several) must lie within {TRIAL_TOLERANCE:0%} "
            + $"of the warm-up's settled figure (the median of its last {SETTLE_WINDOWS} windows, which must agree within {SETTLE_TOLERANCE:0%}); if not, the warm-up is extended once (at least {EXTENSION_MINIMUM.TotalSeconds:0} s, until its windows agree, at most {EXTENSION_CAP.TotalSeconds:0} s), "
            + $"where an extension's windows agree when its level test passes: the older and the newer half of its latest windows, {LEVEL_HALF_WINDOWS} to {LEVEL_MAX_HALF_WINDOWS} windows a half, each half read as the pass reads it (the p50 of all its searches with one searcher, its searches per second with several), agree within {SETTLE_TOLERANCE:0%}, "
            + $"judged on the windows that stopped it (an extension whose cap runs out with fewer than {2 * LEVEL_HALF_WINDOWS} windows is judged by its last {SETTLE_WINDOWS} instead); "
            + $"then a second trial is taken, which must lie inside the range of the newer half's windows or within {TRIAL_TOLERANCE:0%} of the newer half's figure, and the warm-up runs again before the pass. "
            + $"After the pass its own figure is held against the settled figure, within {TRIAL_TOLERANCE:0%}. "
            + $"In the level test, the second trial and the hold, two one-searcher p50s no more than {P50_FLOOR_MS:0.0#} ms apart agree whatever their percentage (below what this box resolves at one searcher). "
            + "Each target's notes give every check, and a pass that still disagrees, or whose own figure did not hold, flags its target as unsettled. "
            + "With machine control on, the check for a quiet box is made when the warm-up is announced, before it starts (a wait between the warm-up and the timed pass let the engine go cold), "
            + $"so by the time the clock opens that check is as old as the warm-up and the trial (about {warm + TRIAL_TIME.TotalSeconds:0} s, more after an extension); each pass's conditions record that lead (quietCheckLeadSeconds). "
            + "Failed untimed searches are counted per target (warmupErrors) and are not in the timed error counts.",
      };
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
   /// One half of the level test read as the pass reads it: with one searcher the p50 of all its
   /// searches, with several its completed searches over its seconds.
   /// </summary>
   /// <param name="half">The half's windows.</param>
   /// <param name="concurrency">Searchers.</param>
   /// <returns>The figure.</returns>
   /// <exception cref="InvalidOperationException">A one-searcher window came without its latencies.</exception>
   private static double HalfFigure( IReadOnlyList<SettleWindow> half, int concurrency )
   {
      if( concurrency == 1 )
      {
         return BenchMath.Percentile( half.SelectMany( w => w.Latencies ?? throw new InvalidOperationException( "a one-searcher settle window has no latencies" ) ).ToList(), 50 );
      }

      double seconds = half.Sum( w => w.Seconds );
      return seconds > 0 ? half.Sum( w => w.Searches ) / seconds : 0;
   }

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
   /// Brings the engine to a timed pass: the pass's warm-up and, after a preparation, its settle
   /// check. After an extension the warm-up is announced and run again, so the quiet-box check
   /// (made at the warm-up line) and the warm-up still come right before the timed pass.
   /// </summary>
   /// <param name="pass">Pass name.</param>
   /// <param name="call">The search the pass times.</param>
   /// <param name="concurrency">Searchers the pass uses.</param>
   /// <param name="outcome">Outcome (warm-up tally; the check goes into its preparation).</param>
   /// <param name="log">Progress output.</param>
   /// <param name="name">Target name, for the log.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task ReadyAsync( string pass, SearchCall call, int concurrency, SearchOutcome outcome, Action<string> log, string name, CancellationToken ct )
   {
      SettleResult warm = await WarmUpAsync( pass, call, concurrency, outcome, log, name, false, ct );
      if( outcome.Preparation == null )
      {
         return;
      }

      SettleCheck check = await CheckAsync( pass, call, concurrency, warm, log, name, ct );
      outcome.Preparation.Checks.Add( check );
      if( check.Extension != null )
      {
         await WarmUpAsync( pass, call, concurrency, outcome, log, name, true, ct );
      }
   }

   /// <summary>
   /// One warm-up: announced (machine control holds the run at this line until the box is
   /// quiet), then the pass's own search at the pass's own concurrency for at least
   /// <see cref="WarmupTime"/> and at least --warmup searches, stopped by <see cref="SettleCap"/>
   /// or when the searches keep failing.
   /// </summary>
   /// <param name="pass">Pass name.</param>
   /// <param name="call">The search the pass times.</param>
   /// <param name="concurrency">Searchers the pass uses.</param>
   /// <param name="outcome">Outcome (warm-up tally).</param>
   /// <param name="log">Progress output.</param>
   /// <param name="name">Target name, for the log.</param>
   /// <param name="again">True for the warm-up run again after a settle extension.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The warm-up read as a settle (its windows and figures).</returns>
   private async Task<SettleResult> WarmUpAsync( string pass, SearchCall call, int concurrency, SearchOutcome outcome, Action<string> log, string name, bool again, CancellationToken ct )
   {
      string time = WarmupTime > TimeSpan.Zero ? $" and {WarmupTime.TotalSeconds:0.#} s at least, {Searchers( concurrency )}" : string.Empty;
      log( $"  {name}: warm-up before {pass}, {_options.Warmup} searches{time}{( again ? " (again, after the settle extension)" : string.Empty )}" );
      var plan = new SustainPlan( _options.Warmup, WarmupTime, SettleCap, false );
      SettleSampler run = await SustainAsync( call, concurrency, plan, ct );
      outcome.Add( pass, new WarmupResult( run.Sent, run.Errors, run.FirstError, run.Capped && run.Sent < _options.Warmup ), log, name );
      return run.Result( plan );
   }

   /// <summary>
   /// The settle check before one timed pass: a trial of that pass against the warm-up's settled
   /// figure; when they disagree (or the warm-up's windows did not agree), the one extension of
   /// the warm-up and a second trial. A warm-up that stopped because the searches kept failing is
   /// not extended: more time does not fix a failing engine.
   /// </summary>
   /// <param name="pass">Pass name.</param>
   /// <param name="call">The search the pass times.</param>
   /// <param name="concurrency">Searchers the pass uses.</param>
   /// <param name="settle">The warm-up, read as a settle.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="name">Target name, for the log.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The check.</returns>
   private async Task<SettleCheck> CheckAsync( string pass, SearchCall call, int concurrency, SettleResult settle, Action<string> log, string name, CancellationToken ct )
   {
      double? settled = SettledFigure( settle.WindowFigures );
      log( $"  {name}: {CHECK_STEP} before {pass}: trial, {TrialTime.TotalSeconds:0.#} s at {Searchers( concurrency )}, against the warm-up's settled {( settled is double s ? FigureText( s, concurrency ) : "none (no full window)" )}" );
      TrialRecord trial = await TrialAsync( call, concurrency, settled, ct );
      log( $"  {name}: {CHECK_STEP} before {pass}: warm-up {( settle.Settled ? "windows agreed" : $"windows did not settle ({settle.StoppedBecause})" )}; {trial.Describe( "trial" )}" );
      if( ( settle.Settled && trial.Agrees ) || settle.GaveUp )
      {
         return new SettleCheck( pass, settle, trial, null, null, settle.Settled && trial.Agrees );
      }

      log( $"  {name}: {CHECK_STEP} before {pass}: extending the warm-up once, at least {ExtensionMinimum.TotalSeconds:0.#} s and at most {ExtensionCap.TotalSeconds:0.#} s, until its level test passes" );
      var plan = new SustainPlan( 0, ExtensionMinimum, ExtensionCap, true );
      SettleResult extension = ( await SustainAsync( call, concurrency, plan, ct ) ).Result( plan );
      TrialRecord retrial = await TrialAsync( call, concurrency, extension.Level?.Newer ?? SettledFigure( extension.WindowFigures ), ct, extension.Level );
      var check = new SettleCheck( pass, settle, trial, extension, retrial, extension.Settled && retrial.Agrees );
      log( $"  {name}: {CHECK_STEP} before {pass}: extension ran {extension.Seconds:0.0} s and {extension.Searches:N0} searches; {retrial.Describe( "second trial" )}; {( check.Confirmed ? "confirmed" : "STILL NOT CONFIRMED" )}" );
      return check;
   }

   /// <summary>
   /// The trial: the pass's own search at the pass's own concurrency for <see cref="TrialTime"/>,
   /// untimed for the results, through the same window code the timed passes use, read as the
   /// pass reads it (p50 with one searcher, completed searches per second with several).
   /// </summary>
   /// <param name="call">The search the pass times.</param>
   /// <param name="concurrency">Searchers the pass uses.</param>
   /// <param name="settled">The settled figure it is compared with, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="level">The extension's level test, whose newer half's window range the trial may also land in; null after the warm-up.</param>
   /// <returns>The trial.</returns>
   private async Task<TrialRecord> TrialAsync( SearchCall call, int concurrency, double? settled, CancellationToken ct, SettleLevel? level = null )
   {
      SearchWindow window = await WindowAsync( call, concurrency, TrialTime, false, ct );
      List<double> latencies = window.Latencies();
      double? figure = latencies.Count == 0 || window.Seconds <= 0 ? null
         : concurrency == 1 ? BenchMath.Percentile( latencies, 50 ) : latencies.Count / window.Seconds;
      return new TrialRecord( concurrency, settled, figure, latencies.Count, window.Errors, window.Seconds, level?.Low, level?.High );
   }

   /// <summary>
   /// Keeps <paramref name="concurrency"/> searchers busy with one search until the plan says
   /// stop: at least its minimum of searches and its minimum time, and, for an extension, until
   /// the windows agree; never past its cap, and not once <see cref="GIVE_UP_FAILURES"/> searches
   /// in a row failed. An extension looks at its windows every <see cref="SETTLE_POLL"/>.
   /// Why searchers claim a search number before sending it: with no minimum time the run sends
   /// exactly the minimum of searches, so a count-only warm-up stays a count.
   /// </summary>
   /// <param name="call">The search.</param>
   /// <param name="concurrency">Searchers.</param>
   /// <param name="plan">When to stop.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The searches, read into windows.</returns>
   private async Task<SettleSampler> SustainAsync( SearchCall call, int concurrency, SustainPlan plan, CancellationToken ct )
   {
      var sampler = new SettleSampler( concurrency, WindowTime, GIVE_UP_FAILURES, plan.UntilSettled );
      long claims = -1;
      Task all = Task.WhenAll( Enumerable.Range( 0, concurrency ).Select( worker => Task.Run( async () =>
      {
         for( long i = Interlocked.Increment( ref claims ); sampler.Continues( i, plan ); i = Interlocked.Increment( ref claims ) )
         {
            ( _, double ms, string? error ) = await TimedAsync( call, (int)( i % _queries.Count ), ct );
            sampler.Record( worker, ms, error );
         }
      }, ct ) ) );
      while( plan.UntilSettled && !all.IsCompleted )
      {
         await Task.WhenAny( all, Task.Delay( SETTLE_POLL, ct ) );
         sampler.Evaluate( false );
      }

      await all;
      sampler.Evaluate( true );
      return sampler;
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
   /// Records the timed pass's own figure on its settle check, and logs it when it lies more than
   /// <see cref="TRIAL_TOLERANCE"/> from the settled figure (the engine was still changing).
   /// Why: a check can confirm an engine on a plateau of a slow drift, and only the pass itself,
   /// timed for far longer than the trial, shows that the level it settled on held.
   /// </summary>
   /// <param name="pass">Pass name.</param>
   /// <param name="concurrency">Searchers it used.</param>
   /// <param name="outcome">Outcome (its last pass record; the check is in its preparation).</param>
   /// <param name="log">Progress output.</param>
   /// <param name="name">Target name, for the log.</param>
   private static void HoldPass( string pass, int concurrency, SearchOutcome outcome, Action<string> log, string name )
   {
      List<SettleCheck>? checks = outcome.Preparation?.Checks;
      if( checks is not { Count: > 0 } || checks[^1].Pass != pass || outcome.Passes.Count == 0 )
      {
         return;
      }

      PassRecord record = outcome.Passes[^1];
      SettleCheck check = checks[^1] with { PassFigure = concurrency == 1 ? record.P50Ms : record.Qps };
      checks[^1] = check;
      if( !check.Held )
      {
         log( $"  {name}: {CHECK_STEP} after {pass}: the timed pass gave {FigureText( check.PassFigure!.Value, concurrency )} against the settled {FigureText( check.SettledFigure!.Value, concurrency )}, {check.PassApart:0%} apart (limit {TRIAL_TOLERANCE:0%}); NOT HELD, the engine was still changing" );
      }
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
   /// Runs <paramref name="concurrency"/> searchers back to back for <paramref name="duration"/>,
   /// cycling the queries, and keeps every search's latency. Every timed pass, every rehearsal
   /// and every trial goes through here, so the rehearsal compiles exactly the code that is timed
   /// and the trial reads a pass the way the pass is read.
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
/// When a sustained run of searches (a warm-up or an extension) stops.
/// </summary>
/// <param name="MinimumSearches">Searches it sends at least (unless the cap or the failures stop it).</param>
/// <param name="MinimumTime">Time it runs at least.</param>
/// <param name="Cap">Time it never runs past.</param>
/// <param name="UntilSettled">True for an extension: keep going after both minimums until its level test passes (<see cref="SearchRunner.LevelOf"/>).</param>
public sealed record SustainPlan( int MinimumSearches, TimeSpan MinimumTime, TimeSpan Cap, bool UntilSettled );

/// <summary>
/// The searches of one warm-up or extension, sorted by completion time into buckets of the
/// window length per searcher, so the window figures can be read while the searchers run.
/// Why buckets by time and not windows of a fixed count: on a fast engine 100 searches last a
/// fraction of a second, and three such windows agreed while the engine was still speeding up.
/// Why a window's figure is the pass's own number: a settle at 8 searchers is judged by the
/// QPS@8 it would report, a settle at one searcher by its p50.
/// A warm-up is judged by its last windows (<see cref="SearchRunner.IsSettled"/>), an extension
/// by its level test (<see cref="SearchRunner.LevelOf"/>). An extension that stops because it
/// settled is judged on exactly the windows that stopped it.
/// Why (v7): the v6 runs read the windows once more after the stop, and that read takes in the
/// bucket that was held back while the searchers ran. In 7 of their 10 unsettled checks the
/// extension had stopped at 30 to 38 s because its windows agreed, and that one extra window undid
/// it; in 4 of the 7 the second trial agreed, so the extra window alone flagged the pass.
/// </summary>
public sealed class SettleSampler
{
   #region Data Members

   private readonly int _concurrency;
   private readonly TimeSpan _windowTime;
   private readonly int _giveUp;
   private readonly bool _byLevel;
   private readonly object _judge = new();
   private readonly long _start;
   private readonly List<List<double>>[] _buckets;
   private readonly List<SettleWindow> _windows = new();
   private readonly List<double> _figures = new();
   private readonly List<double> _pending = new();
   private int _pendingBuckets;
   private int _merged;
   private int _sent;
   private int _errors;
   private int _failedInRow;
   private string? _firstError;
   private long _stopTicks;
   private int _capped;
   private volatile bool _settled;
   private bool _stoppedSettled;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Starts the clock of a run.
   /// </summary>
   /// <param name="concurrency">Searchers.</param>
   /// <param name="windowTime">Shortest window (the bucket length).</param>
   /// <param name="giveUp">Failures in a row after which the run stops.</param>
   /// <param name="byLevel">True for an extension (judged by its level test), false for a warm-up (judged by its last windows).</param>
   public SettleSampler( int concurrency, TimeSpan windowTime, int giveUp, bool byLevel = false )
   {
      _concurrency = concurrency;
      _windowTime = windowTime > TimeSpan.Zero ? windowTime : TimeSpan.FromMilliseconds( 1 );
      _giveUp = giveUp;
      _byLevel = byLevel;
      _buckets = Enumerable.Range( 0, concurrency ).Select( _ => new List<List<double>>() ).ToArray();
      _start = Stopwatch.GetTimestamp();
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Searches sent (completed and failed).</summary>
   public int Sent => Volatile.Read( ref _sent );

   /// <summary>Searches that failed.</summary>
   public int Errors => Volatile.Read( ref _errors );

   /// <summary>The first failure, or null.</summary>
   public string? FirstError => Volatile.Read( ref _firstError );

   /// <summary>True when the run stopped because the searches kept failing.</summary>
   public bool GaveUp => Volatile.Read( ref _failedInRow ) >= _giveUp;

   /// <summary>True when the cap stopped the run.</summary>
   public bool Capped => Volatile.Read( ref _capped ) == 1;

   /// <summary>Figure of each full window so far, oldest first.</summary>
   public IReadOnlyList<double> Figures => _figures;

   /// <summary>
   /// Whether a searcher sends the search it claimed. The first "no" fixes the moment the run
   /// stopped; only windows before it count, so the searchers trailing off do not lower a QPS.
   /// </summary>
   /// <param name="claim">The search number claimed, from 0.</param>
   /// <param name="plan">When to stop.</param>
   /// <returns>True to send it.</returns>
   public bool Continues( long claim, SustainPlan plan )
   {
      TimeSpan elapsed = Stopwatch.GetElapsedTime( _start );
      bool capped = elapsed >= plan.Cap;
      bool more = !capped && !GaveUp && ( claim < plan.MinimumSearches || elapsed < plan.MinimumTime || ( plan.UntilSettled && !StopsSettled() ) );
      if( !more && Interlocked.CompareExchange( ref _stopTicks, Math.Max( 1, elapsed.Ticks ), 0 ) == 0 && capped )
      {
         Volatile.Write( ref _capped, 1 );
      }

      return more;
   }

   /// <summary>
   /// Records one search: a failure counts toward giving up, a success goes into the bucket of
   /// its completion time.
   /// </summary>
   /// <param name="worker">The searcher.</param>
   /// <param name="ms">Its latency.</param>
   /// <param name="error">Its failure, or null.</param>
   public void Record( int worker, double ms, string? error )
   {
      Interlocked.Increment( ref _sent );
      if( error != null )
      {
         Interlocked.Increment( ref _errors );
         Interlocked.Increment( ref _failedInRow );
         Interlocked.CompareExchange( ref _firstError, error, null );
         return;
      }

      Volatile.Write( ref _failedInRow, 0 );
      int bucket = (int)( Stopwatch.GetElapsedTime( _start ).Ticks / _windowTime.Ticks );
      List<List<double>> mine = _buckets[worker];
      lock( mine )
      {
         while( mine.Count <= bucket )
         {
            mine.Add( new List<double>() );
         }

         mine[bucket].Add( ms );
      }
   }

   /// <summary>
   /// Turns the buckets that are complete into windows of at least <see cref="SearchRunner.SETTLE_WINDOW"/>
   /// searches and refreshes whether the run is settled (its last windows for a warm-up, its level
   /// test for an extension). While searchers run, a bucket is read one bucket after it ended (a
   /// search that completed at its end may still be on its way into the list); at the end every
   /// bucket before the stop is read, except after an extension stopped because it settled: its
   /// windows stay the ones that stopped it. Never called twice at once.
   /// </summary>
   /// <param name="final">True once every searcher has finished.</param>
   public void Evaluate( bool final )
   {
      lock( _judge )
      {
         if( !_stoppedSettled )
         {
            Merge( final );
         }
      }
   }

   /// <summary>
   /// The run read as a settle: whether its last windows agreed (a warm-up) or its level test
   /// passed (an extension), how long it ran, its searches, its window figures, the level test
   /// for an extension, and why it did not settle. An extension whose cap ran out with fewer
   /// windows than its level test needs is judged by its last windows, as a warm-up is. Why: an
   /// engine slow enough that 100 searches take longer than the window would otherwise be called
   /// unsettled for want of windows, not for anything it did.
   /// </summary>
   /// <param name="plan">The plan it ran under.</param>
   /// <returns>The settle.</returns>
   public SettleResult Result( SustainPlan plan )
   {
      lock( _judge )
      {
         SettleLevel? level = _byLevel ? SearchRunner.LevelOf( _windows, _concurrency ) : null;
         bool settled = ( level != null ? level.Agrees : SearchRunner.IsSettled( _figures ) ) && !GaveUp;
         string windows = $"only {_figures.Count} full window{( _figures.Count == 1 ? string.Empty : "s" )}";
         string? why = settled ? null
            : GaveUp ? $"{_giveUp} searches in a row failed"
            : level != null ? $"the {plan.Cap.TotalSeconds:0} s cap ran out with its older and newer halves {level.Apart:0.0%} apart (limit {SearchRunner.SETTLE_TOLERANCE:0%}{( _concurrency == 1 ? $", or {SearchRunner.P50_FLOOR_MS:0.0#} ms" : string.Empty )})"
            : _figures.Count < SearchRunner.SETTLE_WINDOWS ? $"{windows}, fewer than {SearchRunner.SETTLE_WINDOWS}"
            : _byLevel ? $"{windows} when the {plan.Cap.TotalSeconds:0} s cap ran out, fewer than the {2 * SearchRunner.LEVEL_HALF_WINDOWS} its level test needs, and its last {SearchRunner.SETTLE_WINDOWS} differed by more than {SearchRunner.SETTLE_TOLERANCE:0%}"
            : $"its last {SearchRunner.SETTLE_WINDOWS} windows differed by more than {SearchRunner.SETTLE_TOLERANCE:0%}";
         double seconds = Stopwatch.GetElapsedTime( _start ).TotalSeconds;
         return new SettleResult( settled, seconds, Sent, Errors, FirstError, _figures.ToArray(), why, GaveUp, _concurrency, _windowTime.TotalSeconds, level );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// True when the run may stop because it settled; the first such answer freezes the windows,
   /// so the verdict is made on exactly the windows that stopped it. Why the lock only here: it
   /// is taken only once the windows already agree, so the searchers never wait on it before then.
   /// </summary>
   /// <returns>True when settled.</returns>
   private bool StopsSettled()
   {
      if( !_settled )
      {
         return false;
      }

      lock( _judge )
      {
         _stoppedSettled |= _settled;
         return _settled;
      }
   }

   /// <summary>
   /// Turns the complete buckets into windows and refreshes whether the run is settled (see
   /// <see cref="Evaluate"/>). Called under the judge lock. A window keeps its latencies only for
   /// an extension at one searcher, whose level test reads a half's p50 from all its searches.
   /// </summary>
   /// <param name="final">True once every searcher has finished.</param>
   private void Merge( bool final )
   {
      long stop = Volatile.Read( ref _stopTicks );
      long now = Stopwatch.GetElapsedTime( _start ).Ticks;
      int limit = stop > 0 ? (int)( stop / _windowTime.Ticks ) : (int)( now / _windowTime.Ticks ) - 1;
      if( !final && stop > 0 )
      {
         limit = Math.Min( limit, (int)( now / _windowTime.Ticks ) - 1 );
      }

      bool added = false;
      for( int bucket = _merged; bucket < limit; bucket++ )
      {
         foreach( List<List<double>> worker in _buckets )
         {
            lock( worker )
            {
               _pending.AddRange( worker.Count > bucket ? worker[bucket] : Enumerable.Empty<double>() );
            }
         }

         _pendingBuckets++;
         if( _pending.Count >= SearchRunner.SETTLE_WINDOW )
         {
            double seconds = _pendingBuckets * _windowTime.TotalSeconds;
            double figure = _concurrency == 1 ? BenchMath.Percentile( _pending, 50 ) : _pending.Count / seconds;
            _windows.Add( new SettleWindow( figure, _pending.Count, seconds, _byLevel && _concurrency == 1 ? _pending.ToArray() : null ) );
            _figures.Add( figure );
            _pending.Clear();
            _pendingBuckets = 0;
            added = true;
         }
      }

      _merged = Math.Max( _merged, limit );
      if( added )
      {
         _settled = _byLevel ? SearchRunner.LevelOf( _windows, _concurrency ) is { Agrees: true } : SearchRunner.IsSettled( _figures );
      }
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
/// <param name="Previous">The timed pass that ran just before ("rehearsal" for the first after a preparation), or null.</param>
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
/// A warm-up or an extension read as a settle.
/// </summary>
/// <param name="Settled">True when the figures of its last windows agreed.</param>
/// <param name="Seconds">How long it ran.</param>
/// <param name="Searches">Searches sent.</param>
/// <param name="Errors">Searches that failed.</param>
/// <param name="FirstError">The first failure, or null.</param>
/// <param name="WindowFigures">Figure of each full window (p50 ms with one searcher, QPS with several), oldest first.</param>
/// <param name="StoppedBecause">Why it did not settle, or null.</param>
/// <param name="GaveUp">True when it stopped because the searches kept failing.</param>
/// <param name="Concurrency">Searchers it ran with (says which figure the windows hold).</param>
/// <param name="WindowSeconds">Shortest window, seconds.</param>
/// <param name="Level">An extension's level test (null for a warm-up, or with too few windows for it).</param>
public sealed record SettleResult( bool Settled, double Seconds, int Searches, int Errors, string? FirstError, IReadOnlyList<double> WindowFigures, string? StoppedBecause,
   bool GaveUp, int Concurrency, double WindowSeconds, SettleLevel? Level = null );

/// <summary>
/// One settle window: its figure, its completed searches and its length, and for an extension at
/// one searcher its latencies, so the level test can read a half of windows as the pass reads it.
/// </summary>
/// <param name="Figure">p50 ms with one searcher, QPS with several.</param>
/// <param name="Searches">Completed searches in it.</param>
/// <param name="Seconds">Its length (whole buckets).</param>
/// <param name="Latencies">Every latency in it (ms) for an extension at one searcher; otherwise null.</param>
public sealed record SettleWindow( double Figure, int Searches, double Seconds, IReadOnlyList<double>? Latencies );

/// <summary>
/// An extension's level test (see <see cref="SearchRunner.LevelOf"/>): the figure of its older and
/// of its newer half, each read as the pass reads it, and the window figures of each half.
/// </summary>
/// <param name="Older">Figure of the older half.</param>
/// <param name="Newer">Figure of the newer half: the settled figure.</param>
/// <param name="OlderWindows">Window figures of the older half, oldest first.</param>
/// <param name="NewerWindows">Window figures of the newer half, oldest first.</param>
/// <param name="Concurrency">Searchers (one: the figures are p50s in ms, and <see cref="SearchRunner.P50_FLOOR_MS"/> applies).</param>
public sealed record SettleLevel( double Older, double Newer, IReadOnlyList<double> OlderWindows, IReadOnlyList<double> NewerWindows, int Concurrency )
{
   #region Public Methods

   /// <summary>Windows in each half.</summary>
   public int HalfWindows => NewerWindows.Count;

   /// <summary>Lowest window figure of the newer half.</summary>
   public double Low => NewerWindows.Min();

   /// <summary>Highest window figure of the newer half.</summary>
   public double High => NewerWindows.Max();

   /// <summary>How far apart the halves are, as a share of the lower.</summary>
   public double Apart => Math.Min( Older, Newer ) > 0 ? Math.Abs( Newer - Older ) / Math.Min( Older, Newer ) : double.PositiveInfinity;

   /// <summary>True when the halves differ by less than <see cref="SearchRunner.SETTLE_TOLERANCE"/>, or, as p50s, by no more than <see cref="SearchRunner.P50_FLOOR_MS"/>.</summary>
   public bool Agrees => Apart < SearchRunner.SETTLE_TOLERANCE || SearchRunner.WithinFloor( Older, Newer, Concurrency );

   #endregion Public Methods
}

/// <summary>
/// One trial of a settle check: the pass's own search at the pass's own concurrency right
/// before the pass, read as the pass reads it, compared with the warm-up's settled figure.
/// After an extension it also agrees when it lands inside the range of the extension's newer
/// half's windows. Why: an engine that swings between two speeds every few seconds gives a 3 s
/// trial from either side of the swing, and one settled figure then calls the swing a change
/// (v6, ClickHouse with its system logs off at 8 searchers: second trials of 432, 435 and 526 QPS
/// while its 2 s windows ran from 434 to 530). A trial outside both is a change: the newer half
/// is the engine's own settled behaviour, so the range is no wider than what the engine itself
/// just did.
/// </summary>
/// <param name="Concurrency">Searchers (one: the figure is a p50 in ms; several: QPS).</param>
/// <param name="Settled">The settled figure it was compared with, or null when no window completed.</param>
/// <param name="Figure">The trial's figure, or null when no search completed.</param>
/// <param name="Searches">Completed searches.</param>
/// <param name="Errors">Failed searches.</param>
/// <param name="Seconds">How long it ran.</param>
/// <param name="Low">Lowest window figure of the extension's newer half, or null after a warm-up.</param>
/// <param name="High">Highest window figure of the extension's newer half, or null after a warm-up.</param>
public sealed record TrialRecord( int Concurrency, double? Settled, double? Figure, int Searches, int Errors, double Seconds, double? Low = null, double? High = null )
{
   #region Public Methods

   /// <summary>How far apart the two figures are, as a share of the trial's; null when either is missing.</summary>
   public double? Apart => ApartOf( Settled, Figure );

   /// <summary>True when a range was given and the trial's figure lies inside it.</summary>
   public bool InRange => Low is double low && High is double high && Figure is double f && f >= low && f <= high;

   /// <summary>True when a range was given and the trial's p50 lies within <see cref="SearchRunner.P50_FLOOR_MS"/> of the settled figure (the floor applies to the second trial only).</summary>
   public bool InFloor => Low.HasValue && Settled is double s && Figure is double f && SearchRunner.WithinFloor( s, f, Concurrency );

   /// <summary>True when the figures are within <see cref="SearchRunner.TRIAL_TOLERANCE"/>, or (a second trial) the trial lies inside the given range or within the p50 floor.</summary>
   public bool Agrees => InRange || InFloor || ( Apart is double apart && apart <= SearchRunner.TRIAL_TOLERANCE );

   /// <summary>
   /// The difference of a settled figure and a trial figure over the trial figure.
   /// </summary>
   /// <param name="settled">Settled figure.</param>
   /// <param name="trial">Trial figure.</param>
   /// <returns>The share, or null when either is missing or the trial is not positive.</returns>
   public static double? ApartOf( double? settled, double? trial )
   {
      return settled is double s && trial is double t && t > 0 && !double.IsNaN( s ) ? Math.Abs( s - t ) / t : null;
   }

   /// <summary>
   /// One line: "trial of 4,560 searches in 3.0 s at 8 searchers: 1,520 QPS against the settled 1,524 QPS, 0% apart (limit 10%)";
   /// after an extension also whether it lies inside its newer windows' range.
   /// </summary>
   /// <param name="what">"trial" or "second trial".</param>
   /// <returns>The text.</returns>
   public string Describe( string what )
   {
      string trial = Figure is double f ? SearchRunner.FigureText( f, Concurrency ) : "none (no search completed)";
      string settled = Settled is double s ? SearchRunner.FigureText( s, Concurrency ) : "none (no full window)";
      string apart = Apart is double a ? $"{a:0%} apart" : "not comparable";
      string range = Low is double low && High is double high
         ? $", {( InRange ? "inside" : "outside" )} its newer windows' range {( Concurrency == 1 ? $"{low:0.000} to {high:0.000} ms" : $"{low:N0} to {high:N0} QPS" )}"
            + ( InFloor && !InRange && Apart > SearchRunner.TRIAL_TOLERANCE ? $", within the {SearchRunner.P50_FLOOR_MS:0.0#} ms floor" : string.Empty )
         : string.Empty;
      return $"{what} of {Searches:N0} searches in {Seconds:0.0} s at {SearchRunner.Searchers( Concurrency )}{( Errors > 0 ? $" ({Errors} failed)" : string.Empty )}: {trial} against the settled {settled}, {apart} (limit {SearchRunner.TRIAL_TOLERANCE:0%}){range}";
   }

   #endregion Public Methods
}

/// <summary>
/// The settle check before one timed pass: the warm-up read as a settle, the trial, and when it
/// was needed the one extension and the second trial; once the pass has run, its own figure,
/// held against the settled figure.
/// Why the pass is held too: a check can confirm an engine on a plateau of a slow drift, and the
/// pass, far longer than the trial, shows whether the level held. Why the same 10% as the trial:
/// over the 156 timed passes of the three v6 runs the pass's figure lay a median 0.4% from its
/// settled figure and more than 7.3% only once (Oracle at 8 searchers, 15%, against the median of
/// three 2 s windows of its 1.25 s stop-and-go cycle, which the level test now reads over halves of
/// 10 s or more).
/// </summary>
/// <param name="Pass">The timed pass it preceded.</param>
/// <param name="Settle">The pass's warm-up, read as a settle.</param>
/// <param name="Trial">The first trial.</param>
/// <param name="Extension">The extension, or null when none ran.</param>
/// <param name="Retrial">The trial after the extension, or null.</param>
/// <param name="Confirmed">True when the last settle settled and its trial agreed.</param>
/// <param name="PassFigure">The timed pass's own figure (p50 ms with one searcher, QPS with several), or null before it ran.</param>
public sealed record SettleCheck( string Pass, SettleResult Settle, TrialRecord Trial, SettleResult? Extension, TrialRecord? Retrial, bool Confirmed, double? PassFigure = null )
{
   #region Public Methods

   /// <summary>The settled figure the last trial was compared with, which the timed pass is held against.</summary>
   public double? SettledFigure => ( Retrial ?? Trial ).Settled;

   /// <summary>How far the timed pass's figure lies from the settled figure, as a share of the pass's; null before the pass ran or without a settled figure.</summary>
   public double? PassApart => TrialRecord.ApartOf( SettledFigure, PassFigure );

   /// <summary>True when the timed pass's p50 lies within <see cref="SearchRunner.P50_FLOOR_MS"/> of the settled figure.</summary>
   public bool HeldByFloor => PassFigure is double p && SettledFigure is double s && SearchRunner.WithinFloor( s, p, Trial.Concurrency );

   /// <summary>False when the timed pass's figure lies more than <see cref="SearchRunner.TRIAL_TOLERANCE"/> from the settled figure (and, as a p50, more than <see cref="SearchRunner.P50_FLOOR_MS"/>).</summary>
   public bool Held => PassApart is not double apart || apart <= SearchRunner.TRIAL_TOLERANCE || HeldByFloor;

   /// <summary>True when the check confirmed the warm-up and the timed pass held to it.</summary>
   public bool Settled => Confirmed && Held;

   /// <summary>Searches the check sent (trials and extension, completed and failed); the warm-up is counted with the warm-ups.</summary>
   public int Searches => Trial.Searches + Trial.Errors + ( Extension?.Searches ?? 0 ) + ( Retrial is TrialRecord r ? r.Searches + r.Errors : 0 );

   /// <summary>Searches of the check that failed.</summary>
   public int Errors => Trial.Errors + ( Extension?.Errors ?? 0 ) + ( Retrial?.Errors ?? 0 );

   #endregion Public Methods
}

/// <summary>
/// One warm-up's tally.
/// </summary>
/// <param name="Searches">Warm-up searches sent.</param>
/// <param name="Errors">How many of them failed or timed out.</param>
/// <param name="FirstError">The first failure message, or null.</param>
/// <param name="CutShort">True when the warm-up cap ran out before the --warmup minimum of searches was sent.</param>
public sealed record WarmupResult( int Searches, int Errors, string? FirstError, bool CutShort );

/// <summary>
/// The untimed preparation of one target: its rehearsals, and the settle check made before each
/// timed pass.
/// </summary>
public sealed class Preparation
{
   #region Public Methods

   /// <summary>Each pass type's rehearsal, in the order run.</summary>
   public List<RehearsalRecord> Rehearsals { get; } = new();

   /// <summary>The settle check made before each timed pass, in the order run.</summary>
   public List<SettleCheck> Checks { get; } = new();

   /// <summary>The first rehearsal failure, with its pass, or null.</summary>
   public string? FirstError { get; private set; }

   /// <summary>Searches sent by the rehearsals and the settle checks (completed and failed).</summary>
   public int Searches => Rehearsals.Sum( r => r.Searches + r.Errors ) + Checks.Sum( c => c.Searches );

   /// <summary>Searches of the rehearsals and the settle checks that failed.</summary>
   public int Errors => Rehearsals.Sum( r => r.Errors ) + Checks.Sum( c => c.Errors );

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

   /// <summary>The rehearsal that ran before the passes and the settle checks made before each, or null.</summary>
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

   /// <summary>Every untimed search sent: per-pass warm-ups, rehearsals and settle checks.</summary>
   public int UntimedSearches => WarmupSearches + ( Preparation?.Searches ?? 0 );

   /// <summary>Every untimed search that failed: per-pass warm-ups, rehearsals and settle checks.</summary>
   public int UntimedErrors => WarmupErrors + ( Preparation?.Errors ?? 0 );

   /// <summary>The first warm-up failure, with the pass it preceded.</summary>
   public string? FirstWarmupError { get; private set; }

   /// <summary>Passes whose warm-up ran out of time before sending the --warmup minimum of searches.</summary>
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
         log( $"  {name}: warm-up before {pass} stopped after {warmup.Searches} searches (time cap)" );
      }
   }

   #endregion Public Methods
}
