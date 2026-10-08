using System.Globalization;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// What the two claim sessions say about each other: guard G2 (a pair ordered in the first session
/// alone whose medians reverse in the second stops the report), the per-target movement from the
/// first session to the second, the orders that held in one session only, and the pairs close below
/// the line.
/// Why "unconfirmed" and not "unstable": an order that held in one session and not the other was not
/// confirmed by the second session; the test does not show it reversed.
/// </summary>
public static class ClaimDrift
{
   #region Public Methods

   /// <summary>
   /// Guard G2 over every metric, as the design states it: a pair ordered by the rule in the first
   /// claim session and not in the second, whose medians in the second session are the other way
   /// round, stops the report.
   /// Why one-way: the first session (v7) is the one whose orders the design projected before the
   /// second was measured; an order the second session shows alone is listed as unconfirmed, never
   /// published.
   /// </summary>
   /// <param name="outcomes">Per-metric pair results.</param>
   /// <param name="sessions">The two claim sessions, oldest first.</param>
   /// <param name="tBp">Threshold.</param>
   /// <returns>The guard result; never fired in one-session mode.</returns>
   public static GuardG2 G2( IReadOnlyList<MetricOutcome> outcomes, IReadOnlyList<ClaimSession> sessions, int tBp )
   {
      var guard = new GuardG2();
      if( sessions.Count != 2 )
      {
         return guard;
      }

      int line = 10000 + tBp;
      foreach( MetricOutcome outcome in outcomes )
      {
         bool lower = ClaimMetrics.LowerIsBetter( outcome.Metric );
         foreach( PairOutcome p in outcome.Pairs )
         {
            if( p.AOverBBp[0] >= line && p.AOverBBp[1] < line && Worse( p.MedianA[1], p.MedianB[1], lower ) )
            {
               guard.Pairs.Add( Reversed( outcome.Metric, p.A, p.B, sessions, p.MedianA[1], p.MedianB[1] ) );
            }

            if( p.BOverABp[0] >= line && p.BOverABp[1] < line && Worse( p.MedianB[1], p.MedianA[1], lower ) )
            {
               guard.Pairs.Add( Reversed( outcome.Metric, p.B, p.A, sessions, p.MedianB[1], p.MedianA[1] ) );
            }
         }
      }

      guard.Stopped = guard.Pairs.Count > 0;
      return guard;
   }

   /// <summary>
   /// The drift section: per target and metric the move of the median from the first session to the
   /// second, the median and largest absolute move, unconfirmed orders and pairs close to the line.
   /// </summary>
   /// <param name="outcomes">Per-metric results (tables and pairs).</param>
   /// <param name="sessions">The two claim sessions.</param>
   /// <param name="tBp">Threshold.</param>
   /// <returns>The drift.</returns>
   public static DriftInfo Drift( IReadOnlyList<MetricOutcome> outcomes, IReadOnlyList<ClaimSession> sessions, int tBp )
   {
      var drift = new DriftInfo { From = sessions[0].Name, To = sessions[1].Name };
      foreach( MetricOutcome outcome in outcomes )
      {
         foreach( MetricRow row in outcome.Table.Rows.Where( r => r.Status == ClaimRule.RANKED ) )
         {
            double from = ClaimRule.Median( row.PerSession[drift.From].Runs );
            double to = ClaimRule.Median( row.PerSession[drift.To].Runs );
            drift.PerTarget.Add( new DriftMove { Target = row.Target, Metric = outcome.Metric, MoveBp = (int)Math.Round( ( to / from - 1 ) * 10000, MidpointRounding.AwayFromZero ) } );
         }

         foreach( MetricRow row in outcome.Table.Rows.Where( r => r.Status == ClaimRule.NOT_HELD && r.PerSession.ContainsKey( drift.From ) && r.PerSession.ContainsKey( drift.To ) ) )
         {
            drift.Excluded.Add( Excluded( row, outcome.Metric, drift.From, drift.To ) );
         }

         drift.UnconfirmedOrders.AddRange( Unconfirmed( outcome, sessions, tBp ) );
         drift.CloseToLine.AddRange( Close( outcome, tBp ) );
         drift.OnLine.AddRange( OnTheLine( outcome, tBp ) );
      }

      drift.UnconfirmedFromFirst = drift.UnconfirmedOrders.Count( u => u.Session == drift.From );
      drift.UnconfirmedFromSecond = drift.UnconfirmedOrders.Count( u => u.Session == drift.To );

      if( drift.PerTarget.Count > 0 )
      {
         drift.MedianAbsMoveBp = (int)Math.Round( ClaimRule.Median( drift.PerTarget.Select( d => (double)Math.Abs( d.MoveBp ) ).ToList() ), MidpointRounding.AwayFromZero );
         drift.Largest = drift.PerTarget.OrderByDescending( d => Math.Abs( d.MoveBp ) ).ThenBy( d => d.Metric, StringComparer.Ordinal ).ThenBy( d => d.Target, StringComparer.Ordinal ).First();
      }

      return drift;
   }

   /// <summary>
   /// The pairs close below the line: not ordered, but one target's worst run beat the other's best
   /// run in every session by a ratio from line - <see cref="ClaimRule.CLOSE_BELOW_BP"/> to line - 1
   /// (12500 to 13499 bp at the 35% threshold), always in the same direction.
   /// Why listed: they are the pairs a slightly lower threshold would order, so a reader sees how
   /// near the line the unordered list is.
   /// </summary>
   /// <param name="outcome">One metric's results.</param>
   /// <param name="tBp">Threshold.</param>
   /// <returns>The close pairs, ahead target first.</returns>
   public static IEnumerable<CloseToLine> Close( MetricOutcome outcome, int tBp )
   {
      int line = 10000 + tBp;
      int low = line - ClaimRule.CLOSE_BELOW_BP;
      foreach( PairOutcome p in outcome.Pairs )
      {
         if( p.AOverBBp.All( r => r >= low && r < line ) )
         {
            yield return new CloseToLine { Metric = outcome.Metric, A = p.A, B = p.B, MinRatioBp = p.AOverBBp.Min() };
         }
         else if( p.BOverABp.All( r => r >= low && r < line ) )
         {
            yield return new CloseToLine { Metric = outcome.Metric, A = p.B, B = p.A, MinRatioBp = p.BOverABp.Min() };
         }
      }
   }

   /// <summary>
   /// The ordered pairs on the line: one target's worst run beat the other's best run in every session by at least the line and at most
   /// <see cref="ClaimRule.ON_LINE_BP"/> more (13500 to 13525 bp at the 35% threshold).
   /// Why listed: these orders are published, but they clear the line by less than a quarter of a percent, so a reader sees which
   /// published orders rest on a hair.
   /// </summary>
   /// <param name="outcome">One metric's results.</param>
   /// <param name="tBp">Threshold.</param>
   /// <returns>The pairs, ahead target first.</returns>
   public static IEnumerable<CloseToLine> OnTheLine( MetricOutcome outcome, int tBp )
   {
      int line = 10000 + tBp;
      int high = line + ClaimRule.ON_LINE_BP;
      foreach( PairOutcome p in outcome.Pairs )
      {
         if( p.AOverBBp.All( r => r >= line ) && p.AOverBBp.Min() <= high )
         {
            yield return new CloseToLine { Metric = outcome.Metric, A = p.A, B = p.B, MinRatioBp = p.AOverBBp.Min() };
         }
         else if( p.BOverABp.All( r => r >= line ) && p.BOverABp.Min() <= high )
         {
            yield return new CloseToLine { Metric = outcome.Metric, A = p.B, B = p.A, MinRatioBp = p.BOverABp.Min() };
         }
      }
   }

   /// <summary>
   /// A bp value as a percentage text with two decimals ("29.08%"), as every sentence prints one.
   /// </summary>
   /// <param name="bp">Basis points.</param>
   /// <returns>The text.</returns>
   public static string Percent( int bp )
   {
      return ( bp / 100.0 ).ToString( "0.00", CultureInfo.InvariantCulture ) + "%";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A cell the drift figures leave out because its pass was recorded NOT HELD, with the move of its session median.
   /// Why computed anyway: the cell is shown in the table and not ranked, so the move figures over ranked cells do not include it, and a reader must see how far it moved.
   /// </summary>
   /// <param name="row">The not-held row.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="from">First session.</param>
   /// <param name="to">Second session.</param>
   /// <returns>The cell.</returns>
   private static DriftExcluded Excluded( MetricRow row, string metric, string from, string to )
   {
      SessionFigures a = row.PerSession[from];
      SessionFigures b = row.PerSession[to];
      double before = ClaimRule.Median( a.Runs );
      double after = ClaimRule.Median( b.Runs );
      int move = (int)Math.Round( ( after / before - 1 ) * 10000, MidpointRounding.AwayFromZero );
      return new DriftExcluded { Target = row.Target, Metric = metric, MoveBp = move, AbsMoveBp = Math.Abs( move ), FromMin = a.Min, FromMax = a.Max, ToMin = b.Min, ToMax = b.Max };
   }

   /// <summary>
   /// Orders held in exactly one session.
   /// </summary>
   /// <param name="outcome">One metric's results.</param>
   /// <param name="sessions">Sessions.</param>
   /// <param name="tBp">Threshold.</param>
   /// <returns>The orders.</returns>
   private static IEnumerable<UnconfirmedOrder> Unconfirmed( MetricOutcome outcome, IReadOnlyList<ClaimSession> sessions, int tBp )
   {
      int line = 10000 + tBp;
      foreach( PairOutcome p in outcome.Pairs )
      {
         for( int s = 0; s < sessions.Count; s++ )
         {
            bool elsewhereA = Enumerable.Range( 0, sessions.Count ).Where( o => o != s ).All( o => p.AOverBBp[o] >= line );
            bool elsewhereB = Enumerable.Range( 0, sessions.Count ).Where( o => o != s ).All( o => p.BOverABp[o] >= line );
            if( p.AOverBBp[s] >= line && !elsewhereA )
            {
               yield return new UnconfirmedOrder { Metric = outcome.Metric, A = p.A, B = p.B, Session = sessions[s].Name };
            }

            if( p.BOverABp[s] >= line && !elsewhereB )
            {
               yield return new UnconfirmedOrder { Metric = outcome.Metric, A = p.B, B = p.A, Session = sessions[s].Name };
            }
         }
      }
   }

   /// <summary>
   /// A G2 entry.
   /// </summary>
   /// <param name="metric">Metric id.</param>
   /// <param name="a">The target ordered ahead in the first session.</param>
   /// <param name="b">The target behind it.</param>
   /// <param name="sessions">The two sessions.</param>
   /// <param name="medianA">A's median in the second session.</param>
   /// <param name="medianB">B's median in the second session.</param>
   /// <returns>The entry.</returns>
   private static ReversedPair Reversed( string metric, string a, string b, IReadOnlyList<ClaimSession> sessions, double medianA, double medianB )
   {
      return new ReversedPair { Metric = metric, A = a, B = b, OrderedIn = sessions[0].Name, ReversedIn = sessions[1].Name, MediansReversed = new List<double> { medianA, medianB } };
   }

   /// <summary>
   /// True when value x is worse than value y in the metric's direction.
   /// </summary>
   /// <param name="x">One median.</param>
   /// <param name="y">The other.</param>
   /// <param name="lower">True for latency.</param>
   /// <returns>True when x is worse.</returns>
   private static bool Worse( double x, double y, bool lower )
   {
      return lower ? x > y : x < y;
   }

   #endregion Private Methods
}
