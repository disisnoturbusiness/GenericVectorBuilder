using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The why table (design section 3): per target, in p50 table order, the facts of engine-facts.json
/// that validated against the claim runs, side by side with measured costs from the newest session:
/// engine CPU per search and client CPU per search at one and eight searchers, and engine CPUs busy
/// at eight. Facts and costs are shown side by side, never as causes.
/// Hole H1: exact or approximate comes from the search fact, which the fact sheet takes from
/// measured state and P5's validation holds against each run's indexState. This class checks it
/// once more, independently: a target with nothing indexed and vectors stored (indexState
/// indexedVectors 0 of totalVectors) searched by a scan, so a search fact that says approximate is
/// refused. Elasticsearch in v7 (no HNSW graph for 524 vectors) is that case.
/// Costs are medians over the newest session's runs and null unless every run recorded the value,
/// so one missing pass never turns into a figure from two runs.
/// </summary>
public static class ConsolidateWhy
{
   #region Data Members

   /// <summary>The searcher counts the costs are shown at.</summary>
   public static readonly IReadOnlyList<int> LEVELS = new[] { 1, 8 };

   /// <summary>The hosting text of an engine that runs inside the client process.</summary>
   public const string EMBEDDED = "embedded";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Builds the why rows and fills the exact table's facts for targets without an exact pass.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="report">The report, tables filled.</param>
   /// <param name="facts">The fact rows that resolved, from the validation.</param>
   /// <returns>One row per target, in p50 table order.</returns>
   /// <exception cref="ConsolidateRefusal">A target has no search fact, or its search fact contradicts the recorded index state.</exception>
   public static List<WhyRow> Build( IReadOnlyList<ClaimSession> sessions, ConsolidatedReport report, IReadOnlyList<ResolvedFact> facts )
   {
      List<string> order = report.Metrics.First( m => m.Metric == ClaimMetrics.P50 ).Rows.Select( r => r.Target ).ToList();
      var rows = new List<WhyRow>();
      foreach( string target in order )
      {
         List<WhyFact> mine = facts.Where( f => f.Row.Target == target ).Select( f => new WhyFact
         {
            Kind = f.Row.Kind,
            Text = f.Row.Text,
            Source = f.Row.Source,
            Confidence = f.Row.Confidence,
            Mode = f.Row.Mode,
            Where = f.Where,
         } ).ToList();
         CheckSearchMode( target, mine, sessions );
         rows.Add( new WhyRow { Target = target, Facts = mine, Costs = Costs( target, sessions[^1] ) } );
      }

      FillNoExactPass( report, facts );
      return rows;
   }

   /// <summary>
   /// Puts each target's search mode on its rows of the p50, QPS@1 and QPS@8 tables, from its search fact.
   /// </summary>
   /// <param name="report">The report with tables and why rows filled.</param>
   public static void MarkSearchModes( ConsolidatedReport report )
   {
      foreach( MetricTable table in report.Metrics.Where( m => m.Metric != ClaimMetrics.EXACT ) )
      {
         foreach( MetricRow row in table.Rows )
         {
            row.SearchMode = report.Why.FirstOrDefault( w => w.Target == row.Target )?.Facts.FirstOrDefault( f => f.Kind == "search" )?.Mode;
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Refuses a search fact that says approximate for a target whose recorded index held nothing.
   /// </summary>
   /// <param name="target">Target.</param>
   /// <param name="facts">Its facts.</param>
   /// <param name="sessions">Claim sessions.</param>
   /// <exception cref="ConsolidateRefusal">No search fact, or a contradiction.</exception>
   private static void CheckSearchMode( string target, List<WhyFact> facts, IReadOnlyList<ClaimSession> sessions )
   {
      List<WhyFact> search = facts.Where( f => f.Kind == "search" ).ToList();
      if( search.Count == 0 )
      {
         throw new ConsolidateRefusal( $"{target} has no search fact that validated, so the why table cannot say how it searched" );
      }

      foreach( RunResult run in sessions.SelectMany( s => s.Runs ) )
      {
         TargetResult t = run.Find( target )!;
         foreach( (string name, IndexStateValue? state) in new[] { ( "afterLoad", t.AfterLoad ), ( "afterSearch", t.AfterSearch ) } )
         {
            bool nothingIndexed = state is { IndexedVectors: 0, TotalVectors: > 0 };
            if( nothingIndexed && search.Any( f => f.Mode == "approximate" ) )
            {
               throw new ConsolidateRefusal( $"{target}'s search fact says approximate, but {run.Name} indexState.{name} records 0 of {state!.TotalVectors} vectors indexed, so its searches scanned every vector" );
            }
         }
      }
   }

   /// <summary>
   /// The measured costs of one target in a session: medians over its runs.
   /// </summary>
   /// <param name="target">Target.</param>
   /// <param name="session">The newest claim session.</param>
   /// <returns>The costs.</returns>
   private static WhyCosts Costs( string target, ClaimSession session )
   {
      bool embedded = string.Equals( session.Runs[0].Find( target )!.Hosting, EMBEDDED, StringComparison.Ordinal );
      var costs = new WhyCosts { Session = session.Name, ClientIncludesEngine = embedded };
      foreach( int level in LEVELS )
      {
         string pass = level == 1 ? ClaimMetrics.PassOf( ClaimMetrics.P50 ) : ClaimMetrics.PassOf( ClaimMetrics.QPS8 );
         List<PassFacts?> passes = session.Runs.Select( r => PassOf( r, target, pass ) ).ToList();
         string key = level.ToString( System.Globalization.CultureInfo.InvariantCulture );
         costs.EngineCpuMsPerSearch[key] = MedianOfAll( passes.Select( p => p?.EngineCpuMsPerSearch ) );
         costs.ClientCpuMsPerSearch[key] = MedianOfAll( session.Runs.Select( ( r, i ) => passes[i]?.ClientCpuMsPerSearch ?? ( r.Find( target )!.ClientCpuMsPerSearch.TryGetValue( level, out double v ) ? v : null ) ) );
         costs.EngineCpuNullReason ??= passes.Select( p => p?.EngineCpuNullReason ).FirstOrDefault( r => r != null );
         if( level == 8 )
         {
            costs.EngineCpusBusyAt8 = MedianOfAll( passes.Select( p => p?.EngineCpusBusy ) );
         }
      }

      return costs;
   }

   /// <summary>
   /// A target's last recorded pass of a name in a run.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="target">Target.</param>
   /// <param name="pass">Pass name.</param>
   /// <returns>The pass, or null.</returns>
   private static PassFacts? PassOf( RunResult run, string target, string pass )
   {
      int index = ConsolidateFlags.LastIndex( run.Conditions.Passes, target, pass );
      return index < 0 ? null : run.Conditions.Passes[index];
   }

   /// <summary>
   /// The median when every value is a finite number, else null.
   /// </summary>
   /// <param name="values">One value per run.</param>
   /// <returns>The median or null.</returns>
   private static double? MedianOfAll( IEnumerable<double?> values )
   {
      List<double?> list = values.ToList();
      return list.Count > 0 && list.All( v => v is double d && double.IsFinite( d ) ) ? ClaimRule.Median( list.Select( v => v!.Value ).ToList() ) : null;
   }

   /// <summary>
   /// Fills each target without an exact pass with its search fact.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <param name="facts">Resolved facts.</param>
   private static void FillNoExactPass( ConsolidatedReport report, IReadOnlyList<ResolvedFact> facts )
   {
      foreach( NoExactPass entry in report.Metrics.First( m => m.Metric == ClaimMetrics.EXACT ).NoExactPass )
      {
         ResolvedFact? search = facts.FirstOrDefault( f => f.Row.Target == entry.Target && f.Row.Kind == "search" );
         entry.Mode = search?.Row.Mode;
         entry.FactText = search?.Row.Text;
         entry.FactSource = search?.Row.Source;
      }
   }

   #endregion Private Methods
}
