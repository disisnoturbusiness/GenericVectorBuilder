using System.Globalization;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The flags of a consolidated report: everything a reader must see before trusting a target's
/// numbers. Some come from what an engine said about itself (index not ready, durability not
/// stated), some from the spread between runs, some from how the box was set up (governor, busy
/// box, client and engines on the same cores, debug build).
/// Why computed here and not in the page: the markdown and the web page must show the same
/// warnings from the same numbers, and a rule that lives in one place can be tested once.
/// A flag states evidence (the values and the runs), never a cause: why a spread is wide is for
/// the writeup to argue.
/// </summary>
public static class ConsolidateFlags
{
   #region Data Members

   /// <summary>A target is flagged when its largest p50 or QPS across runs is more than this many times its smallest.</summary>
   public const double SPREAD_RATIO = 1.15;

   /// <summary>
   /// p50 above the mean latency by more than this ratio is flagged: latency has a long tail
   /// to the slow side, so the median should sit at or under the mean, and a median above it
   /// means the two numbers were taken under different conditions.
   /// </summary>
   public const double P50_ABOVE_MEAN_RATIO = 1.15;

   /// <summary>Mean above p50 by more than this ratio is flagged (a heavy tail, or two passes under different conditions).</summary>
   public const double MEAN_ABOVE_P50_RATIO = 1.5;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Builds every flag for the listed targets.
   /// </summary>
   /// <param name="summaries">Target summaries, in target order; each one's PerRun follows <paramref name="used"/>.</param>
   /// <param name="used">Runs used, oldest first.</param>
   /// <returns>The flags, grouped by target in target order.</returns>
   public static List<Flag> Build( IReadOnlyList<TargetSummary> summaries, IReadOnlyList<RunResult> used )
   {
      return summaries.SelectMany( s => For( s, used ) ).ToList();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The flags for one target.
   /// </summary>
   /// <param name="s">The target's summary.</param>
   /// <param name="used">Runs used.</param>
   /// <returns>The flags.</returns>
   private static IEnumerable<Flag> For( TargetSummary s, IReadOnlyList<RunResult> used )
   {
      var flags = new List<Flag>();
      List<TargetRunFacts> runs = s.PerRun;
      AddFlag( flags, s.Name, "index-not-ready-after-load", runs.Where( r => r.AfterLoad != null && r.AfterLoad.Ready != true ).Select( r => ( r.Run, StateText( r.AfterLoad! ) ) ) );
      AddFlag( flags, s.Name, "index-not-ready-after-search", runs.Where( r => r.AfterSearch != null && r.AfterSearch.Ready != true ).Select( r => ( r.Run, StateText( r.AfterSearch! ) ) ) );
      AddFlag( flags, s.Name, "durability-not-stated", runs.Where( r => r.Durability != null && IsNotStated( r.Durability ) ).Select( r => ( r.Run, r.Durability!.Trim().Length == 0 ? "(empty)" : r.Durability! ) ) );
      AddFlag( flags, s.Name, "warmup-errors", runs.Where( r => r.WarmupErrors > 0 ).Select( r => ( r.Run, $"{r.WarmupErrors} warm-up errors" ) ) );
      AddFlag( flags, s.Name, "search-errors", runs.Where( r => r.Errors > 0 ).Select( r => ( r.Run, $"{r.Errors} search errors" ) ) );
      AddFlag( flags, s.Name, "segment-layout-changed", runs.Where( r => r.SegmentLayoutAfterLoad != null && r.SegmentLayoutAfterSearch != null && r.SegmentLayoutAfterLoad != r.SegmentLayoutAfterSearch )
         .Select( r => ( r.Run, $"{r.SegmentLayoutAfterLoad} after the load, {r.SegmentLayoutAfterSearch} after the searches" ) ) );
      AddFlag( flags, s.Name, "exact-recall-below-1", runs.Where( r => r.ExactRecall < 1.0 ).Select( r => ( r.Run, $"exactRecall {r.ExactRecall:0.000}" ) ) );
      AddFlag( flags, s.Name, "fields-missing", runs.Select( ( r, i ) => ( r.Run, string.Join( ", ", MissingFields( r, used[i] ) ) ) ).Where( x => x.Item2.Length > 0 ) );
      if( s.Engines.Count > 1 || s.Indexes.Count > 1 )
      {
         flags.Add( new Flag { Target = s.Name, Kind = "engine-or-index-text-differs", Runs = runs.Select( r => r.Run ).ToList(), Detail = $"{s.Engines.Count} engine texts, {s.Indexes.Count} index texts" } );
      }

      AddSpread( flags, s );
      AddMeanFlag( flags, s );
      AddFlag( flags, s.Name, "unsettled-target", runs.Where( r => r.Settled == false ).Select( r => ( r.Run, r.SettleDetail == null ? "not settled" : "not settled: " + r.SettleDetail ) ) );
      AddConditionFlags( flags, s.Name, used );
      return flags;
   }

   /// <summary>
   /// Adds one flag covering the given runs, with each distinct detail and its run count.
   /// Nothing is added when no run qualifies.
   /// </summary>
   /// <param name="flags">Receives the flag.</param>
   /// <param name="target">Target name.</param>
   /// <param name="kind">Flag kind.</param>
   /// <param name="items">One (run, evidence text) pair per run it applies to.</param>
   private static void AddFlag( List<Flag> flags, string target, string kind, IEnumerable<(string Run, string Detail)> items )
   {
      List<(string Run, string Detail)> list = items.ToList();
      if( list.Count == 0 )
      {
         return;
      }

      string text = string.Join( "; ", list.GroupBy( i => i.Detail ).Select( g => g.Count() == list.Count ? g.Key : $"{g.Key} ({g.Count()} run{( g.Count() == 1 ? string.Empty : "s" )})" ) );
      flags.Add( new Flag { Target = target, Kind = kind, Runs = list.Select( i => i.Run ).ToList(), Detail = text } );
   }

   /// <summary>
   /// Flags a target whose p50 or QPS at any level varies by more than <see cref="SPREAD_RATIO"/>
   /// between runs (two runs at least).
   /// </summary>
   /// <param name="flags">Receives the flag.</param>
   /// <param name="s">The target's summary.</param>
   private static void AddSpread( List<Flag> flags, TargetSummary s )
   {
      var parts = new List<string>();
      SpreadPart( parts, "p50 ms", s.P50Ms, "0.00" );
      foreach( KeyValuePair<string, Spread> level in s.Qps )
      {
         SpreadPart( parts, $"QPS@{level.Key}", level.Value, "0.0" );
      }

      if( parts.Count > 0 )
      {
         flags.Add( new Flag { Target = s.Name, Kind = "spread", Runs = s.PerRun.Select( r => r.Run ).ToList(), Detail = string.Join( "; ", parts ) } );
      }
   }

   /// <summary>
   /// Adds a "label min to max (xratio over n runs)" entry when the metric's spread is wide.
   /// </summary>
   /// <param name="parts">Receives the entry.</param>
   /// <param name="label">Metric label.</param>
   /// <param name="spread">The metric over the runs.</param>
   /// <param name="format">Number format.</param>
   private static void SpreadPart( List<string> parts, string label, Spread spread, string format )
   {
      if( spread.N < 2 || spread.Min is not double min || spread.Max is not double max || min <= 0 || max / min <= SPREAD_RATIO )
      {
         return;
      }

      parts.Add( $"{label} {min.ToString( format, CultureInfo.InvariantCulture )} to {max.ToString( format, CultureInfo.InvariantCulture )} (x{( max / min ).ToString( "0.00#", CultureInfo.InvariantCulture )} over {spread.N} runs)" );
   }

   /// <summary>
   /// Flags runs whose p50 and mean latency disagree: p50 well above the mean, or the mean far
   /// above the p50. The two come from separate passes, so a gap shows the passes ran under
   /// different conditions (a CPU still ramping up, a warm-up that did not warm).
   /// </summary>
   /// <param name="flags">Receives the flag.</param>
   /// <param name="s">The target's summary.</param>
   private static void AddMeanFlag( List<Flag> flags, TargetSummary s )
   {
      var items = new List<(string Run, string Detail)>();
      for( int i = 0; i < s.PerRun.Count && i < s.P50Ms.PerRun.Count; i++ )
      {
         if( s.P50Ms.PerRun[i] is not double p50 || s.PerRun[i].MeanMs is not double mean || !ConsolidateMath.IsNumber( p50 ) || !ConsolidateMath.IsNumber( mean ) || p50 <= 0 || mean <= 0 )
         {
            continue;
         }

         if( p50 / mean > P50_ABOVE_MEAN_RATIO || mean / p50 > MEAN_ABOVE_P50_RATIO )
         {
            items.Add( ( s.PerRun[i].Run, $"p50 {p50.ToString( "0.00", CultureInfo.InvariantCulture )} ms, mean {mean.ToString( "0.00", CultureInfo.InvariantCulture )} ms ({s.PerRun[i].MeanSource}), p50/mean {( p50 / mean ).ToString( "0.00", CultureInfo.InvariantCulture )}" ) );
         }
      }

      AddFlag( flags, s.Name, "p50-mean-inconsistent", items );
   }

   /// <summary>
   /// The flags about how the box was set up: busy box, CPU governor, client and engine on the
   /// same cores, and a build that is not Release. The run-level facts apply to every target of
   /// a run; the per-pass and per-engine records, when the run has them, speak for one target.
   /// </summary>
   /// <param name="flags">Receives the flags.</param>
   /// <param name="target">Target name.</param>
   /// <param name="used">Runs used.</param>
   private static void AddConditionFlags( List<Flag> flags, string target, IReadOnlyList<RunResult> used )
   {
      AddFlag( flags, target, "busy-box", Join( used, c => BusyText( c, target ) ) );
      AddFlag( flags, target, "governor-not-performance", Join( used, c => GovernorText( c, target ) ) );
      AddFlag( flags, target, "client-engine-share-cores", Join( used, c => SharedText( c, target ) ) );
      AddFlag( flags, target, "not-release-build", Join( used, BuildText ) );
   }

   /// <summary>
   /// One (run, evidence) pair per run where the check gives evidence.
   /// </summary>
   /// <param name="used">Runs used.</param>
   /// <param name="check">Gives the evidence text for a run's conditions, or null when the run is fine.</param>
   /// <returns>The pairs.</returns>
   private static IEnumerable<(string Run, string Detail)> Join( IReadOnlyList<RunResult> used, Func<RunConditions, string?> check )
   {
      return used.Select( r => ( r.Name, Detail: check( r.Conditions ) ) ).Where( x => x.Detail != null ).Select( x => ( x.Name, x.Detail! ) );
   }

   /// <summary>
   /// Why the box counts as busy for a target: a 1-minute load average above the run's logical
   /// CPU count at the start, and any timed pass of the target recorded as busy.
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <param name="target">Target name.</param>
   /// <returns>Evidence text, or null when the box was not busy.</returns>
   private static string? BusyText( RunConditions c, string target )
   {
      var parts = new List<string>();
      if( RunConditions.IsBusy( c.LoadAverage, c.LogicalCpus ) )
      {
         parts.Add( $"load average {c.LoadAverage} at the start on {c.LogicalCpus} logical CPUs" );
      }

      parts.AddRange( c.Passes.Where( p => p.Target == target && p.BusyBox ).Select( p => $"busy during {p.Pass}: {p.BusyReason ?? "no reason recorded"}" ) );
      return parts.Count == 0 ? null : string.Join( "; ", parts );
   }

   /// <summary>
   /// Why the CPU governor was not performance for a target: the run's governor, and any timed
   /// pass of the target that ran under another one.
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <param name="target">Target name.</param>
   /// <returns>Evidence text, or null when every recorded governor was performance.</returns>
   private static string? GovernorText( RunConditions c, string target )
   {
      var parts = new List<string>();
      if( c.Governor != null && !RunConditions.IsPerformance( c.Governor ) )
      {
         parts.Add( $"governor {c.Governor}" );
      }

      parts.AddRange( c.Passes.Where( p => p.Target == target && p.Governor != null && !RunConditions.IsPerformance( p.Governor ) ).Select( p => $"governor {p.Governor} during {p.Pass}" ) );
      return parts.Count == 0 ? null : string.Join( "; ", parts );
   }

   /// <summary>
   /// Why the client and a target's engine shared cores: an embedded engine runs inside the
   /// client, an engine that could not be fully pinned may roam, and CPU lists that overlap (per
   /// logical CPU or per physical core) are shared. The engine's own CPU list is used when the
   /// run recorded one, else the run's engine CPUs.
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <param name="target">Target name.</param>
   /// <returns>Evidence text, or null when they did not share cores or it cannot be told.</returns>
   private static string? SharedText( RunConditions c, string target )
   {
      var parts = new List<string>();
      EngineFacts? engine = c.Engines.FirstOrDefault( e => e.Target == target );
      if( engine?.Hosting == "embedded" )
      {
         parts.Add( $"embedded engine runs inside the client process on the client CPUs {c.ClientCpus ?? "(all)"}" );
      }

      if( engine is { Problems.Count: > 0 } )
      {
         parts.Add( $"engine not fully pinned to its CPUs: {string.Join( "; ", engine.Problems )}" );
      }

      string? overlap = OverlapText( c, engine?.Cpus ?? c.EngineCpus );
      if( overlap != null && engine?.Hosting != "embedded" )
      {
         parts.Add( overlap );
      }

      return parts.Count == 0 ? null : string.Join( "; ", parts );
   }

   /// <summary>
   /// Which CPUs or physical cores the client and an engine CPU list have in common.
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <param name="engineCpus">The engine's CPUs, or null when not recorded.</param>
   /// <returns>Evidence text, or null when they share nothing or a list is missing.</returns>
   private static string? OverlapText( RunConditions c, string? engineCpus )
   {
      ( string? sameCpus, string? sameCores ) = CpuSet.Overlap( c.ClientCpus, engineCpus, c.ThreadSiblings );
      if( sameCores == null )
      {
         return null;
      }

      string cpus = sameCpus == null ? string.Empty : $"CPUs {sameCpus}";
      string cores = sameCores == sameCpus ? string.Empty : $"physical cores of CPUs {sameCores} (hyperthread siblings)";
      return $"client CPUs {c.ClientCpus} and engine CPUs {engineCpus} share {string.Join( " and ", new[] { cpus, cores }.Where( t => t.Length > 0 ) )}";
   }

   /// <summary>
   /// "Debug build" with where the value came from when it was derived; null for a Release build
   /// or an unknown one.
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <returns>Evidence text, or null.</returns>
   private static string? BuildText( RunConditions c )
   {
      if( c.BuildConfiguration == null || c.BuildConfiguration.Equals( "Release", StringComparison.OrdinalIgnoreCase ) )
      {
         return null;
      }

      string source = c.Derived.TryGetValue( "buildConfiguration", out string? how ) ? $" ({how})" : string.Empty;
      return $"{c.BuildConfiguration} build{source}";
   }

   /// <summary>
   /// Index state as short evidence text: "ready false, 0 of 524: detail".
   /// </summary>
   /// <param name="state">The state.</param>
   /// <returns>The text.</returns>
   private static string StateText( IndexStateFacts state )
   {
      string ready = state.Ready?.ToString().ToLowerInvariant() ?? "not written";
      return $"ready {ready}, {state.IndexedVectors?.ToString() ?? "?"} of {state.TotalVectors?.ToString() ?? "?"}: {state.Detail ?? "no detail"}";
   }

   /// <summary>
   /// True for a durability statement that says nothing: "not stated" or empty.
   /// </summary>
   /// <param name="durability">The statement.</param>
   /// <returns>True when nothing was disclosed.</returns>
   private static bool IsNotStated( string durability )
   {
      return durability.Trim().Length == 0 || durability.Trim().Equals( "not stated", StringComparison.OrdinalIgnoreCase );
   }

   /// <summary>
   /// Contract fields a run did not record for a target: the target's own and the run's conditions.
   /// Explicit search settings are not required: the target's index description carries its
   /// search effort and is compared between runs.
   /// </summary>
   /// <param name="facts">The target's facts in one run.</param>
   /// <param name="run">The run.</param>
   /// <returns>Names of the missing fields.</returns>
   private static string[] MissingFields( TargetRunFacts facts, RunResult run )
   {
      var missing = new List<string>();
      Missing( missing, facts.OrderInRun == null, "targetOrder" );
      Missing( missing, facts.PassOrder == null, "passOrder" );
      Missing( missing, facts.WarmupErrors == null, "warmupErrors" );
      Missing( missing, facts.AfterLoad == null, "indexState.afterLoad" );
      Missing( missing, facts.AfterSearch == null, "indexState.afterSearch" );
      Missing( missing, facts.Durability == null, "durability" );
      Missing( missing, facts.Settled == null, "settled" );
      missing.AddRange( run.Conditions.MissingNames() );
      return missing.ToArray();
   }

   /// <summary>
   /// Adds a name when the condition holds.
   /// </summary>
   /// <param name="missing">Receives the name.</param>
   /// <param name="isMissing">True when the field was not recorded.</param>
   /// <param name="name">The field name.</param>
   private static void Missing( List<string> missing, bool isMissing, string name )
   {
      if( isMissing )
      {
         missing.Add( name );
      }
   }

   #endregion Private Methods
}
