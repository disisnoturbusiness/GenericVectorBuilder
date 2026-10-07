using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The v8 claim rule (design section 1): "A is ahead of B" on a metric only when, in every claim
/// session separately, every run of A beats every run of B by the threshold. For latency that is
/// floor(10000 x B_best / A_worst) &gt;= 10000 + TBp; for searches per second
/// floor(10000 x A_worst / B_best) &gt;= 10000 + TBp. Everything else is "not separated by this test",
/// never called a tie or said to be equal.
/// Also here, because they read the same per-session ratios: the table rows (in order of the median
/// of every run shown), each row's not-separated list, guard G2 (a pair ordered in the first session
/// whose medians reverse in the second, never the other way round), the orders held in one session
/// only, the pairs close below the line and the ordered pairs that clear it by a hair.
/// Why floor: a ratio exactly at the line in exact arithmetic that lands a hair below it in doubles
/// stays unordered, so rounding never creates a claim.
/// </summary>
public static class ClaimRule
{
   #region Data Members

   /// <summary>The rule as the report prints it.</summary>
   public const string RULE_TEXT = "ordered when floor(10000 x B_best / A_worst) >= 10000 + TBp for latency, or floor(10000 x A_worst / B_best) >= 10000 + TBp for searches per second, in every claim session separately";

   /// <summary>How far below the line, bp, an unordered pair still counts as close to it (12500 to 13499 bp at the 35% threshold).</summary>
   public const int CLOSE_BELOW_BP = 1000;

   /// <summary>How far above the line, bp, an ordered pair still counts as on it (13500 to 13525 bp at the 35% threshold). Why: such an order is published, and a drift of a quarter of a percent would remove it.</summary>
   public const int ON_LINE_BP = 25;

   /// <summary>Row status of a ranked target.</summary>
   public const string RANKED = "ranked";

   /// <summary>Row status of a target shown with one session's figures and not ranked.</summary>
   public const string ONE_SESSION = "one-session";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// floor(10000 x num / den), the ratio in bp.
   /// </summary>
   /// <param name="num">Numerator (above zero).</param>
   /// <param name="den">Denominator (above zero).</param>
   /// <returns>The ratio, bp, rounded down.</returns>
   public static int RatioBp( double num, double den )
   {
      return checked( (int)Math.Floor( 10000.0 * num / den ) );
   }

   /// <summary>
   /// How far A's worst run is ahead of B's best run in one session, bp (10000 = level).
   /// </summary>
   /// <param name="a">A's values in the session.</param>
   /// <param name="b">B's values in the session.</param>
   /// <param name="lowerIsBetter">True for latency.</param>
   /// <returns>The ratio, bp.</returns>
   public static int AheadBp( IReadOnlyList<double> a, IReadOnlyList<double> b, bool lowerIsBetter )
   {
      return lowerIsBetter ? RatioBp( b.Min(), a.Max() ) : RatioBp( a.Min(), b.Max() );
   }

   /// <summary>
   /// True when A is ahead of B in one session by the threshold.
   /// </summary>
   /// <param name="a">A's values.</param>
   /// <param name="b">B's values.</param>
   /// <param name="lowerIsBetter">True for latency.</param>
   /// <param name="tBp">Threshold, bp.</param>
   /// <returns>True when ordered.</returns>
   public static bool Ahead( IReadOnlyList<double> a, IReadOnlyList<double> b, bool lowerIsBetter, int tBp )
   {
      return AheadBp( a, b, lowerIsBetter ) >= 10000 + tBp;
   }

   /// <summary>
   /// The median of a list (the mean of the middle two for an even count).
   /// </summary>
   /// <param name="values">Values; at least one, all finite.</param>
   /// <returns>The median.</returns>
   /// <exception cref="InvalidDataException">The list is empty or holds a value that is not finite.</exception>
   public static double Median( IReadOnlyList<double> values )
   {
      if( values.Count == 0 || values.Any( v => !double.IsFinite( v ) ) )
      {
         throw new InvalidDataException( $"a median needs finite values, got [{string.Join( ", ", values )}]" );
      }

      List<double> sorted = values.OrderBy( v => v ).ToList();
      int middle = sorted.Count / 2;
      return sorted.Count % 2 == 1 ? sorted[middle] : ( sorted[middle - 1] + sorted[middle] ) / 2;
   }

   /// <summary>
   /// Builds one metric's table and its pair results.
   /// </summary>
   /// <param name="metric">Metric id.</param>
   /// <param name="sessions">Claim sessions, oldest first; every run holds every target with a value for the metric.</param>
   /// <param name="ranked">Targets ranked across the sessions.</param>
   /// <param name="oneSession">Targets shown with the last session's figures only (G3).</param>
   /// <param name="tBp">Threshold, bp.</param>
   /// <returns>The table and the per-pair results.</returns>
   public static MetricOutcome Build( string metric, IReadOnlyList<ClaimSession> sessions, IReadOnlyList<string> ranked, IReadOnlyList<string> oneSession, int tBp )
   {
      bool lower = ClaimMetrics.LowerIsBetter( metric );
      var values = new Dictionary<string, List<List<double>>>( StringComparer.Ordinal );
      foreach( string target in ranked.Concat( oneSession ) )
      {
         values[target] = sessions.Select( s => s.Values( target, metric ) ).ToList();
      }

      List<PairOutcome> pairs = Pairs( ranked, values, lower, tBp, sessions );
      var outcome = new MetricOutcome { Metric = metric, Pairs = pairs };
      outcome.Table = Table( metric, sessions, ranked, oneSession, values, pairs );
      return outcome;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Every pair of ranked targets, each with its per-session ratios in both directions.
   /// </summary>
   /// <param name="ranked">Ranked targets.</param>
   /// <param name="values">Values per target per session.</param>
   /// <param name="lower">True for latency.</param>
   /// <param name="tBp">Threshold.</param>
   /// <param name="sessions">Sessions.</param>
   /// <returns>The pairs.</returns>
   private static List<PairOutcome> Pairs( IReadOnlyList<string> ranked, Dictionary<string, List<List<double>>> values, bool lower, int tBp, IReadOnlyList<ClaimSession> sessions )
   {
      var pairs = new List<PairOutcome>();
      for( int i = 0; i < ranked.Count; i++ )
      {
         for( int j = i + 1; j < ranked.Count; j++ )
         {
            string a = ranked[i];
            string b = ranked[j];
            var pair = new PairOutcome { A = a, B = b };
            for( int s = 0; s < sessions.Count; s++ )
            {
               pair.AOverBBp.Add( AheadBp( values[a][s], values[b][s], lower ) );
               pair.BOverABp.Add( AheadBp( values[b][s], values[a][s], lower ) );
               pair.MedianA.Add( Median( values[a][s] ) );
               pair.MedianB.Add( Median( values[b][s] ) );
            }

            pair.AAhead = pair.AOverBBp.All( r => r >= 10000 + tBp );
            pair.BAhead = pair.BOverABp.All( r => r >= 10000 + tBp );
            pairs.Add( pair );
         }
      }

      return pairs;
   }

   /// <summary>
   /// The table: rows in order of the median of every run shown, each with its not-separated list.
   /// </summary>
   /// <param name="metric">Metric id.</param>
   /// <param name="sessions">Sessions.</param>
   /// <param name="ranked">Ranked targets.</param>
   /// <param name="oneSession">One-session targets.</param>
   /// <param name="values">Values.</param>
   /// <param name="pairs">Pair results.</param>
   /// <returns>The table.</returns>
   private static MetricTable Table( string metric, IReadOnlyList<ClaimSession> sessions, IReadOnlyList<string> ranked, IReadOnlyList<string> oneSession,
      Dictionary<string, List<List<double>>> values, List<PairOutcome> pairs )
   {
      bool lower = ClaimMetrics.LowerIsBetter( metric );
      bool oneSessionMode = sessions.Count == 1;
      var rows = new List<MetricRow>();
      foreach( string target in ranked.Concat( oneSession ) )
      {
         bool isOne = oneSession.Contains( target );
         var row = new MetricRow { Target = target, Display = target, Status = isOne || oneSessionMode ? ONE_SESSION : RANKED };
         IEnumerable<int> shown = isOne ? new[] { sessions.Count - 1 } : Enumerable.Range( 0, sessions.Count );
         foreach( int s in shown )
         {
            List<double> v = values[target][s];
            row.PerSession[sessions[s].Name] = new SessionFigures { Min = v.Min(), Max = v.Max(), Runs = v.ToList() };
         }

         row.Median = Median( shown.SelectMany( s => values[target][s] ).ToList() );
         row.NotSeparatedFrom = NotSeparated( target, isOne, ranked, oneSession, pairs );
         rows.Add( row );
      }

      List<MetricRow> ordered = ( lower ? rows.OrderBy( r => r.Median ) : rows.OrderByDescending( r => r.Median ) ).ThenBy( r => r.Target, StringComparer.Ordinal ).ToList();
      List<string> order = ordered.Select( r => r.Target ).ToList();
      ordered.ForEach( r => r.NotSeparatedFrom = r.NotSeparatedFrom.OrderBy( t => order.IndexOf( t ) ).ToList() );
      return new MetricTable
      {
         Metric = metric,
         LowerIsBetter = lower,
         Rows = ordered,
         OrderedPairs = pairs.Count( p => p.AAhead || p.BAhead ),
         ComparedPairs = pairs.Count,
      };
   }

   /// <summary>
   /// The targets a row is not separated from: every ranked target with no order against it, and
   /// every one-session target (a one-session row is not separated from anyone).
   /// </summary>
   /// <param name="target">The row's target.</param>
   /// <param name="isOne">True when the row is a one-session row.</param>
   /// <param name="ranked">Ranked targets.</param>
   /// <param name="oneSession">One-session targets.</param>
   /// <param name="pairs">Pair results.</param>
   /// <returns>The list (order fixed later).</returns>
   private static List<string> NotSeparated( string target, bool isOne, IReadOnlyList<string> ranked, IReadOnlyList<string> oneSession, List<PairOutcome> pairs )
   {
      if( isOne )
      {
         return ranked.Concat( oneSession ).Where( t => t != target ).ToList();
      }

      List<string> list = pairs.Where( p => ( p.A == target || p.B == target ) && !p.AAhead && !p.BAhead ).Select( p => p.A == target ? p.B : p.A ).ToList();
      list.AddRange( oneSession );
      return list;
   }

   #endregion Private Methods
}

/// <summary>A claim session: its name and its runs, oldest first.</summary>
public sealed class ClaimSession
{
   #region Constructor

   /// <summary>
   /// Creates a session.
   /// </summary>
   /// <param name="name">Session name.</param>
   /// <param name="runs">Its runs, oldest first.</param>
   public ClaimSession( string name, IReadOnlyList<RunResult> runs )
   {
      Name = name;
      Runs = runs;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Session name.</summary>
   public string Name { get; }

   /// <summary>Runs, oldest first.</summary>
   public IReadOnlyList<RunResult> Runs { get; }

   /// <summary>
   /// A target's values for a metric in run order.
   /// </summary>
   /// <param name="target">Target.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>One value per run.</returns>
   /// <exception cref="InvalidDataException">A run lacks the target or the value (the caller validated, so this is a bug guard).</exception>
   public List<double> Values( string target, string metric )
   {
      return Runs.Select( r => ( r.Find( target ) is TargetResult t ? ClaimMetrics.Value( t, metric ) : null )
         ?? throw new InvalidDataException( $"{r.Name} has no {ClaimMetrics.FieldOf( metric )} for {target}" ) ).ToList();
   }

   #endregion Public Methods
}

/// <summary>One metric's table and pair results.</summary>
public sealed class MetricOutcome
{
   #region Public Methods

   /// <summary>Metric id.</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>The table.</summary>
   public MetricTable Table { get; set; } = new();

   /// <summary>Every pair of ranked targets.</summary>
   public List<PairOutcome> Pairs { get; set; } = new();

   #endregion Public Methods
}

/// <summary>One pair of ranked targets under the rule.</summary>
public sealed class PairOutcome
{
   #region Public Methods

   /// <summary>First target.</summary>
   public string A { get; set; } = string.Empty;

   /// <summary>Second target.</summary>
   public string B { get; set; } = string.Empty;

   /// <summary>Per session: how far A's worst run is ahead of B's best, bp.</summary>
   public List<int> AOverBBp { get; set; } = new();

   /// <summary>Per session: how far B's worst run is ahead of A's best, bp.</summary>
   public List<int> BOverABp { get; set; } = new();

   /// <summary>Per session: A's median.</summary>
   public List<double> MedianA { get; set; } = new();

   /// <summary>Per session: B's median.</summary>
   public List<double> MedianB { get; set; } = new();

   /// <summary>True when A is ahead in every session.</summary>
   public bool AAhead { get; set; }

   /// <summary>True when B is ahead in every session.</summary>
   public bool BAhead { get; set; }

   #endregion Public Methods
}
