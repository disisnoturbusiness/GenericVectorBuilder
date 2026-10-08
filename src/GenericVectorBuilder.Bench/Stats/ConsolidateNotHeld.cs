using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The timed passes the claim runs recorded as NOT HELD, found by metric: the engine was still changing when the pass was timed, and the pass landed outside the margin around the figure the
/// warm-up had settled at. A target with such a cell in a metric is shown in that metric's table with every run's figure, flagged, and not ranked: it takes part in no pair, so no published order rests on it.
/// Which metric a pass stands for: a pass timed in QPS stands for the QPS metric of that pass (the eight-searcher pass: QPS@8); a pass timed in p50 stands for that p50 (the one-searcher pass: p50, the exact
/// pass: exact p50). A one-searcher pass has a settled p50 and no settled QPS, so its QPS@1 is flagged with it (the flag follows the pass) but the row of QPS@1 is ranked as recorded.
/// Why not rank it: the run's own warning says its numbers may still include warm-up or a change the engine was going through, and that they should be rerun before they are quoted.
/// </summary>
public static class ConsolidateNotHeld
{
   #region Public Methods

   /// <summary>
   /// The cells the claim runs record as NOT HELD for a metric.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="targets">The targets of the report.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>The cells in run order; empty when no claim run recorded a pass behind this metric as NOT HELD.</returns>
   /// <exception cref="InvalidDataException">A NOT HELD pass is timed in a unit that its pass has no metric for.</exception>
   public static List<NotHeldFigure> Cells( IReadOnlyList<ClaimSession> sessions, IReadOnlyList<string> targets, string metric )
   {
      var cells = new List<NotHeldFigure>();
      foreach( RunResult run in sessions.SelectMany( s => s.Runs ) )
      {
         foreach( string target in targets )
         {
            foreach( NotHeldPass held in run.Find( target )?.NotHeldPasses ?? Array.Empty<NotHeldPass>() )
            {
               if( MetricOf( run.Name, target, held ) == metric )
               {
                  cells.Add( new NotHeldFigure
                  {
                     Run = run.Name, Seed = run.RunSeed, Target = target, Pass = held.Pass, Metric = metric, Token = held.Token, Timed = held.Timed, Settled = held.Settled,
                     OffPercent = held.OffPercent, LimitPercent = held.LimitPercent,
                  } );
               }
            }
         }
      }

      return cells;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The metric a NOT HELD pass stands for.
   /// </summary>
   /// <param name="run">The run, for the message.</param>
   /// <param name="target">The target, for the message.</param>
   /// <param name="held">The pass.</param>
   /// <returns>The metric id.</returns>
   /// <exception cref="InvalidDataException">The pass and its unit do not name a metric.</exception>
   private static string MetricOf( string run, string target, NotHeldPass held )
   {
      return ( held.Pass, held.Metric ) switch
      {
         ("default@8", "qps") => ClaimMetrics.QPS8,
         ("default@1", "p50") => ClaimMetrics.P50,
         ("exact", "p50") => ClaimMetrics.EXACT,
         _ => throw new InvalidDataException( $"{run}: {target}'s {held.Pass} pass is recorded NOT HELD with a figure in {held.Metric}, which stands for none of the ranked metrics: {held.Token}" ),
      };
   }

   #endregion Private Methods
}
