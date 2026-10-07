using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The four speed metrics the v8 report ranks, with their ids, direction, the timed pass each one
/// comes from and how to read it from a run. Load rows/s, p95, recall and nDCG are never ranked.
/// Why one table: the threshold basis, the claim rule, the drift and the flags must name and read a
/// metric the same way, and the contract fixes the ids ("p50Ms", "qps1", "qps8", "exactP50Ms").
/// </summary>
public static class ClaimMetrics
{
   #region Data Members

   /// <summary>Median latency at one searcher, ms.</summary>
   public const string P50 = "p50Ms";

   /// <summary>Searches per second at one searcher (the same pass as p50).</summary>
   public const string QPS1 = "qps1";

   /// <summary>Searches per second at eight searchers.</summary>
   public const string QPS8 = "qps8";

   /// <summary>Median latency of the engine's exact mode, ms.</summary>
   public const string EXACT = "exactP50Ms";

   /// <summary>Every ranked metric, in table order.</summary>
   public static readonly IReadOnlyList<string> ALL = new[] { P50, QPS1, QPS8, EXACT };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// True when a lower value is better (latency), false for searches per second.
   /// </summary>
   /// <param name="metric">Metric id.</param>
   /// <returns>The direction.</returns>
   /// <exception cref="ArgumentException">Unknown metric.</exception>
   public static bool LowerIsBetter( string metric )
   {
      return metric switch
      {
         P50 or EXACT => true,
         QPS1 or QPS8 => false,
         _ => throw new ArgumentException( $"unknown metric '{metric}'" ),
      };
   }

   /// <summary>
   /// The timed pass a metric is measured in, as conditions.passes[].pass names it.
   /// </summary>
   /// <param name="metric">Metric id.</param>
   /// <returns>"default@1", "default@8" or "exact".</returns>
   /// <exception cref="ArgumentException">Unknown metric.</exception>
   public static string PassOf( string metric )
   {
      return metric switch
      {
         P50 or QPS1 => "default@1",
         QPS8 => "default@8",
         EXACT => "exact",
         _ => throw new ArgumentException( $"unknown metric '{metric}'" ),
      };
   }

   /// <summary>
   /// The results.json field path of a metric inside a target (for sources).
   /// </summary>
   /// <param name="metric">Metric id.</param>
   /// <returns>E.g. "search.qps.8".</returns>
   /// <exception cref="ArgumentException">Unknown metric.</exception>
   public static string FieldOf( string metric )
   {
      return metric switch
      {
         P50 => "search.p50Ms",
         QPS1 => "search.qps.1",
         QPS8 => "search.qps.8",
         EXACT => "search.exactP50Ms",
         _ => throw new ArgumentException( $"unknown metric '{metric}'" ),
      };
   }

   /// <summary>
   /// The metric's label in report text ("p50", "QPS@1", "QPS@8", "exact p50").
   /// </summary>
   /// <param name="metric">Metric id.</param>
   /// <returns>The label.</returns>
   /// <exception cref="ArgumentException">Unknown metric.</exception>
   public static string Label( string metric )
   {
      return metric switch
      {
         P50 => "p50",
         QPS1 => "QPS@1",
         QPS8 => "QPS@8",
         EXACT => "exact p50",
         _ => throw new ArgumentException( $"unknown metric '{metric}'" ),
      };
   }

   /// <summary>
   /// Maps a metric name as the exclusions file or the basis prototype writes it ("p50", "qps1",
   /// "qps8", "exact", or a contract id) to the contract id.
   /// </summary>
   /// <param name="name">The name.</param>
   /// <returns>The contract id.</returns>
   /// <exception cref="InvalidDataException">The name is none of them.</exception>
   public static string FromAnyName( string name )
   {
      return name switch
      {
         "p50" or P50 => P50,
         "qps1" => QPS1,
         "qps8" => QPS8,
         "exact" or EXACT => EXACT,
         _ => throw new InvalidDataException( $"'{name}' is not a ranked metric (p50, qps1, qps8, exact)" ),
      };
   }

   /// <summary>
   /// A metric's value for one target in one run: a finite number above zero, or null when the run
   /// did not record it.
   /// </summary>
   /// <param name="target">The target's result.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>The value, or null.</returns>
   /// <exception cref="InvalidDataException">The value is recorded but is zero, negative or not finite.</exception>
   public static double? Value( TargetResult target, string metric )
   {
      double? value = metric switch
      {
         P50 => target.P50Ms,
         QPS1 => target.Qps.TryGetValue( 1, out double one ) ? one : null,
         QPS8 => target.Qps.TryGetValue( 8, out double eight ) ? eight : null,
         EXACT => target.ExactP50Ms,
         _ => throw new ArgumentException( $"unknown metric '{metric}'" ),
      };
      if( value is double v && ( !double.IsFinite( v ) || v <= 0 ) )
      {
         throw new InvalidDataException( $"{target.Name} {FieldOf( metric )} is {v}, not a finite number above zero" );
      }

      return value;
   }

   #endregion Public Methods
}
