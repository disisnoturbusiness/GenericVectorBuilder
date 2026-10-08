using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// What the two claim sessions differ in besides the engines: the build that ran them, the day they started on, and the outside load their passes saw.
/// Why: the drift between sessions is read as the engines' own movement only when nothing else changed, and the v7 and v8 sessions changed all three: v8 ran on a
/// frozen build on the next day, with about two thirds of v7's outside load. The tool's outside load per pass is a recorded field, so the figure is read from it,
/// per run, and not argued from the accounting code (a test holds the accounting equal, which says nothing about the load itself).
/// </summary>
public static class ConsolidateSessions
{
   #region Data Members

   /// <summary>The name of a difference in the build.</summary>
   public const string BUILD = "build";

   /// <summary>The name of a difference in the day.</summary>
   public const string DAY = "day";

   /// <summary>The name of a difference in the outside load: the two sessions' run medians do not overlap.</summary>
   public const string OUTSIDE_LOAD = "outside load";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The outside load of each session: per run the median of its passes' outside load during the pass, and the lowest and highest run median.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <returns>One entry per session; a session none of whose passes recorded the load has no entry.</returns>
   public static List<SessionOutsideLoad> OutsideLoad( IReadOnlyList<ClaimSession> sessions )
   {
      var result = new List<SessionOutsideLoad>();
      foreach( ClaimSession session in sessions )
      {
         var load = new SessionOutsideLoad { Session = session.Name };
         foreach( RunResult run in session.Runs )
         {
            List<double> during = run.Conditions.Passes.Select( p => p.OutsideLoadDuring ).OfType<double>().ToList();
            if( during.Count > 0 )
            {
               load.Runs.Add( new RunOutsideLoad { Run = run.Name, MedianCpus = ClaimRule.Median( during ), Passes = during.Count } );
            }
         }

         if( load.Runs.Count == session.Runs.Count )
         {
            load.MinCpus = load.Runs.Min( r => r.MedianCpus );
            load.MaxCpus = load.Runs.Max( r => r.MedianCpus );
            result.Add( load );
         }
      }

      return result;
   }

   /// <summary>
   /// What the two sessions differ in, in the order build, day, outside load.
   /// </summary>
   /// <param name="sessions">The two claim sessions.</param>
   /// <param name="outsideLoad">Their outside load, from <see cref="OutsideLoad"/>.</param>
   /// <returns>The names of the differences; empty when none.</returns>
   public static List<string> Differences( IReadOnlyList<ClaimSession> sessions, IReadOnlyList<SessionOutsideLoad> outsideLoad )
   {
      var found = new List<string>();
      if( sessions.Count != 2 )
      {
         return found;
      }

      if( !Same( sessions[0].Runs.Select( BuildOf ), sessions[1].Runs.Select( BuildOf ) ) )
      {
         found.Add( BUILD );
      }

      if( !Same( sessions[0].Runs.Select( DayOf ), sessions[1].Runs.Select( DayOf ) ) )
      {
         found.Add( DAY );
      }

      if( outsideLoad.Count == 2 && ( outsideLoad[0].MaxCpus < outsideLoad[1].MinCpus || outsideLoad[1].MaxCpus < outsideLoad[0].MinCpus ) )
      {
         found.Add( OUTSIDE_LOAD );
      }

      return found;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The build that ran a run: its binary's folder and, when recorded, its commit.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>The text.</returns>
   private static string BuildOf( RunResult run )
   {
      return ( run.BinaryRoot ?? ConsolidateIdentity.NOT_RECORDED ) + "|" + ( run.Conditions.BuildCommit ?? ConsolidateIdentity.NOT_RECORDED );
   }

   /// <summary>
   /// The UTC day a run started on.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>"2026-10-06", or "not recorded".</returns>
   private static string DayOf( RunResult run )
   {
      return run.StartedUtc is { Length: >= 10 } started ? started[..10] : ConsolidateIdentity.NOT_RECORDED;
   }

   /// <summary>
   /// Whether two lists hold the same set of values.
   /// </summary>
   /// <param name="a">One list.</param>
   /// <param name="b">The other.</param>
   /// <returns>True when the sets are equal.</returns>
   private static bool Same( IEnumerable<string> a, IEnumerable<string> b )
   {
      return a.ToHashSet( StringComparer.Ordinal ).SetEquals( b );
   }

   #endregion Private Methods
}
