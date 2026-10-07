using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The headline of a ranked report (design section 4): it names the engines at the top of the p50
/// table whose rows name no one in their not-separated list, each ordered above every engine below
/// it, says "1.35 times as long" from the threshold and "in each session", and adds that Redis holds
/// its data in memory when Redis is named. Templates for none, one and several named engines; when
/// the names would push the headline past 34 words, it counts them instead of naming them.
/// Why p50 alone: QPS@1 comes from the same one-searcher pass, so it is never a second confirmation.
/// </summary>
public static class ConsolidateHeadline
{
   #region Data Members

   /// <summary>The target the design requires the in-memory clause for whenever the headline names it.</summary>
   public const string REDIS = "redis";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes the headline of a two-session report that no guard stopped.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <exception cref="ConsolidateRefusal">A named engine's in-memory fact is needed and missing.</exception>
   public static void Write( Writer w )
   {
      List<MetricRow> named = Named( w.Report );
      string ratio = S.Ratio( w.Report.Threshold.TBp );
      var sources = new List<SentenceSource> { S.Consolidated( "threshold.tBp", ratio, "bp-ratio" ) };
      if( named.Count == 0 )
      {
         w.Add( "headline", T.Fill( T.HEADLINE_NONE, ratio ), sources );
         return;
      }

      sources.AddRange( named.Select( ( r, i ) => S.Consolidated( $"metrics[metric=p50Ms].rows[{i}].target", r.Target ) ) );
      string memory = Memory( w, named, sources );
      if( named.Any( r => r.Target == REDIS ) && InMemoryFact( w, REDIS ) == null )
      {
         throw new ConsolidateRefusal( $"the headline names {REDIS}, but its storage fact is not a saved docs page saying in-memory, so the headline cannot say it holds its data in memory" );
      }

      string list = S.List( named.Select( r => r.Target ).ToList() );
      string text = named.Count == 1 ? T.Fill( T.HEADLINE_ONE, list, ratio, memory ) : T.Fill( T.HEADLINE_MANY, list, ratio, memory );
      if( SentenceAudit.WordCount( text ) > SentenceAudit.HEADLINE_MAX_WORDS )
      {
         string count = S.Whole( named.Count );
         w.Add( "headline", T.Fill( T.HEADLINE_COUNT, count, ratio ), S.Consolidated( "threshold.tBp", ratio, "bp-ratio" ), S.Consolidated( "headlineNamedCount", count ) );
         w.Report.HeadlineNamedCount = named.Count;
         return;
      }

      w.Report.HeadlineNamedCount = named.Count;
      w.Add( "headline", text, sources );
   }

   /// <summary>
   /// The rows the headline names: from the top of the p50 table, ranked rows whose not-separated list is empty, as long as they run unbroken.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <returns>The rows, in table order.</returns>
   public static List<MetricRow> Named( ConsolidatedReport report )
   {
      MetricTable p50 = report.Metrics.First( m => m.Metric == ClaimMetrics.P50 );
      return p50.Rows.TakeWhile( r => r.Status == ClaimRule.RANKED && r.NotSeparatedFrom.Count == 0 ).ToList();
   }

   /// <summary>
   /// The storage fact of a target when it is a documented in-memory fact (a saved docs page whose quote says in-memory), else null.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="target">Target.</param>
   /// <returns>The fact, or null.</returns>
   public static WhyFact? InMemoryFact( Writer w, string target )
   {
      WhyFact? storage = w.Fact( target, "storage" );
      return storage != null && storage.Confidence == "documented" && storage.Source.StartsWith( "doc:", StringComparison.Ordinal )
         && storage.Source.Contains( "in-memory", StringComparison.OrdinalIgnoreCase ) ? storage : null;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The in-memory clause for the named engines whose storage fact documents it, with its source.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="named">The named rows.</param>
   /// <param name="sources">Receives the sources.</param>
   /// <returns>The clause, or empty.</returns>
   private static string Memory( Writer w, List<MetricRow> named, List<SentenceSource> sources )
   {
      List<(string Target, WhyFact Fact)> inMemory = named.Select( r => ( r.Target, Fact: InMemoryFact( w, r.Target ) ) ).Where( x => x.Fact != null ).Select( x => ( x.Target, x.Fact! ) ).ToList();
      sources.AddRange( inMemory.Select( x => S.Fact( x.Fact ) ) );
      return inMemory.Count == 0 ? string.Empty : T.Fill( T.HEADLINE_MEMORY, S.List( inMemory.Select( x => x.Target ).ToList() ) );
   }

   #endregion Private Methods
}
