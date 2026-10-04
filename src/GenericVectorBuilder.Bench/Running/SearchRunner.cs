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
/// Searches one loaded target with the shared queries and measures it, in this order:
/// warm-up (untimed), latency at concurrency 1 (the first pass also gives recall and nDCG),
/// throughput at each concurrency level for a fixed time, then the engine's exact mode if it
/// has one.
/// Latency is wall-clock time around the sink's SearchAsync, measured by the client: it
/// includes the network hop and the driver, which is what an application sees.
/// Why at least <see cref="MIN_LATENCY_SAMPLES"/> samples: a p99 of 20 samples is just the
/// slowest one, so a small query set (the 20 golden questions) is repeated for latency;
/// recall and nDCG come from the first pass only.
/// </summary>
public sealed class SearchRunner
{
   #region Data Members

   private const int MIN_LATENCY_SAMPLES = 200;
   private const int MAX_PASSES = 20;

   private readonly PipelineData _data;
   private readonly QuerySet _queries;
   private readonly TruthSet _truth;
   private readonly BenchOptions _options;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the runner.
   /// </summary>
   /// <param name="data">Loaded rows (for exact similarities and file paths).</param>
   /// <param name="queries">Queries.</param>
   /// <param name="truth">Exact answers.</param>
   /// <param name="options">Options (top, concurrency, seconds, timeouts).</param>
   public SearchRunner( PipelineData data, QuerySet queries, TruthSet truth, BenchOptions options )
   {
      _data = data;
      _queries = queries;
      _truth = truth;
      _options = options;
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
   /// Measures a target.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="collection">Benchmark collection name.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was measured.</returns>
   public async Task<SearchReport> RunAsync( BenchTarget target, string collection, Action<string> log, CancellationToken ct )
   {
      ISink sink = target.Sink;
      var report = new SearchReport();
      try
      {
         report.CountInTarget = await sink.CountAsync( collection, ct );
      }
      catch( NotSupportedException )
      {
         report.CountInTarget = null;
      }

      SearchCall search = ( v, top, c ) => sink.SearchAsync( collection, v, top, c );
      for( int i = 0; i < _options.Warmup; i++ )
      {
         await TimedAsync( search, i % _queries.Count, ct );
      }

      await LatencyAsync( search, report, ct );
      log( $"  {target.Name}: p50 {report.P50Ms:0.00} ms, p95 {report.P95Ms:0.00} ms, recall@{_options.Top} {report.Recall:0.000}{( report.Ndcg.HasValue ? $", nDCG {report.Ndcg:0.000}" : string.Empty )}" );
      foreach( int concurrency in _options.Concurrency )
      {
         report.Qps[concurrency] = await ThroughputAsync( search, concurrency, report, ct );
         log( $"  {target.Name}: {report.Qps[concurrency]:0.0} QPS at concurrency {concurrency}" );
      }

      if( sink is IExactSearchSink exact && _options.ExactSeconds > 0 )
      {
         await ExactAsync( ( v, top, c ) => exact.SearchExactAsync( collection, v, top, c ), report, ct );
         log( $"  {target.Name}: exact mode p50 {report.ExactP50Ms:0.00} ms over {report.ExactQueries} queries, recall {report.ExactRecall:0.000}" );
      }

      return report;
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

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Times queries one at a time: one pass over every query (kept for scoring), repeated
   /// for latency until there are enough samples.
   /// </summary>
   /// <param name="search">The search.</param>
   /// <param name="report">Report to fill.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task LatencyAsync( SearchCall search, SearchReport report, CancellationToken ct )
   {
      var latencies = new List<double>();
      var answers = new IReadOnlyList<SearchHit>?[_queries.Count];
      for( int pass = 0; pass == 0 || ( latencies.Count < MIN_LATENCY_SAMPLES && pass < MAX_PASSES && latencies.Count > 0 ); pass++ )
      {
         for( int q = 0; q < _queries.Count; q++ )
         {
            (IReadOnlyList<SearchHit>? hits, double ms, string? error) = await TimedAsync( search, q, ct );
            if( error != null )
            {
               report.Errors++;
               report.FirstError ??= error;
               continue;
            }

            latencies.Add( ms );
            answers[q] = pass == 0 ? hits : answers[q];
         }
      }

      report.LatencySamples = latencies.Count;
      report.P50Ms = Nullable( BenchMath.Percentile( latencies, 50 ) );
      report.P95Ms = Nullable( BenchMath.Percentile( latencies, 95 ) );
      report.P99Ms = Nullable( BenchMath.Percentile( latencies, 99 ) );
      ( double recall, double? ndcg ) = Score( answers );
      report.Recall = Nullable( recall );
      report.Ndcg = ndcg.HasValue ? Nullable( ndcg.Value ) : null;
   }

   /// <summary>
   /// Runs <paramref name="concurrency"/> workers, each searching back to back, for the set
   /// number of seconds, and counts completed searches.
   /// </summary>
   /// <param name="search">The search.</param>
   /// <param name="concurrency">Workers.</param>
   /// <param name="report">Report (errors are added to it).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Completed searches per second.</returns>
   private async Task<double> ThroughputAsync( SearchCall search, int concurrency, SearchReport report, CancellationToken ct )
   {
      long done = 0;
      int errors = 0;
      var duration = TimeSpan.FromSeconds( _options.Seconds );
      long start = Stopwatch.GetTimestamp();
      Task[] workers = Enumerable.Range( 0, concurrency ).Select( worker => Task.Run( async () =>
      {
         for( int i = worker; Stopwatch.GetElapsedTime( start ) < duration; i += concurrency )
         {
            ( _, _, string? error ) = await TimedAsync( search, i % _queries.Count, ct );
            if( error == null )
            {
               Interlocked.Increment( ref done );
            }
            else
            {
               Interlocked.Increment( ref errors );
            }
         }
      }, ct ) ).ToArray();
      await Task.WhenAll( workers );
      report.Errors += errors;
      return done / Stopwatch.GetElapsedTime( start ).TotalSeconds;
   }

   /// <summary>
   /// Times the engine's exact mode one query at a time within the exact-mode time budget,
   /// and scores it (it must reach recall 1.0).
   /// </summary>
   /// <param name="exact">The exact search.</param>
   /// <param name="report">Report to fill.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task ExactAsync( SearchCall exact, SearchReport report, CancellationToken ct )
   {
      var latencies = new List<double>();
      var answers = new IReadOnlyList<SearchHit>?[_queries.Count];
      long start = Stopwatch.GetTimestamp();
      for( int q = 0; q < _queries.Count && Stopwatch.GetElapsedTime( start ).TotalSeconds < _options.ExactSeconds; q++ )
      {
         (IReadOnlyList<SearchHit>? hits, double ms, string? error) = await TimedAsync( exact, q, ct );
         if( error != null )
         {
            report.Errors++;
            report.FirstError ??= $"exact mode: {error}";
            continue;
         }

         latencies.Add( ms );
         answers[q] = hits;
      }

      report.ExactQueries = latencies.Count;
      report.ExactP50Ms = Nullable( BenchMath.Percentile( latencies, 50 ) );
      report.ExactP95Ms = Nullable( BenchMath.Percentile( latencies, 95 ) );
      report.ExactRecall = Nullable( Score( answers ).Recall );
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
