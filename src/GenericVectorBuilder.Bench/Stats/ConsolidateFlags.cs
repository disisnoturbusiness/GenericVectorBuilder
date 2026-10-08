using System.Globalization;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The evidence behind every flag on a row of a v8 table: what the runs recorded about one target's
/// pass for one metric. A flag states evidence (the run, the pass, the recorded value and where it
/// sits in results.json), never a cause; <see cref="ConsolidateText"/> turns each item into a
/// sentence whose sources are exactly these paths.
/// Codes: busy-box (the run flagged the pass busy from its own outside load during the pass),
/// clock-off (the median rule puts the pass off its pinned clock), clock-not-read, governor,
/// throttle-rise (the thermal throttle counters rose during the target),
/// unsettled, not-held (the timed pass itself was recorded NOT HELD), index-not-ready, segment-layout-differs, search-errors, warmup-errors and one-session
/// (set by the consolidator for a target whose setup changed between sessions).
/// What is gone and why: the busy flag from the 1-minute load average (it counted the engine's own
/// start-up as outside work), the spread and p50-against-mean flags (the claim rule reads every run's
/// value, and p50 and QPS@1 come from one pass), and the bands.
/// </summary>
public static class ConsolidateFlags
{
   #region Data Members

   /// <summary>The run flagged the pass busy (outside load during the pass above its limit).</summary>
   public const string BUSY_BOX = "busy-box";

   /// <summary>The recomputed clock of the pass was off the pinned clock.</summary>
   public const string CLOCK_OFF = "clock-off";

   /// <summary>A CPU group had no clock reading during the pass.</summary>
   public const string CLOCK_NOT_READ = "clock-not-read";

   /// <summary>The pass ran under another governor than performance.</summary>
   public const string GOVERNOR = "governor-not-performance";

   /// <summary>The thermal throttle counters rose during the target.</summary>
   public const string THROTTLE = "throttle-rise";

   /// <summary>The run recorded the target as not settled before it was timed.</summary>
   public const string UNSETTLED = "unsettled";

   /// <summary>The run recorded the timed pass itself as NOT HELD: the engine was still changing when it was timed, and the figure lies outside the margin around the settled one.</summary>
   public const string NOT_HELD = "not-held";

   /// <summary>The engine reported its index not ready after the load or after the searches.</summary>
   public const string INDEX_NOT_READY = "index-not-ready";

   /// <summary>The engine's segment layout differed between runs.</summary>
   public const string SEGMENT_LAYOUT = "segment-layout-differs";

   /// <summary>Timed searches failed.</summary>
   public const string SEARCH_ERRORS = "search-errors";

   /// <summary>Untimed warm-up searches failed.</summary>
   public const string WARMUP_ERRORS = "warmup-errors";

   /// <summary>The target's setup differed between the sessions; one session shown, not ranked.</summary>
   public const string ONE_SESSION = "one-session";

   /// <summary>Every code, in the order rows list them; the page's legend allow-list.</summary>
   public static readonly IReadOnlyList<string> CODES = new[] { BUSY_BOX, CLOCK_OFF, CLOCK_NOT_READ, GOVERNOR, THROTTLE, UNSETTLED, NOT_HELD, INDEX_NOT_READY, SEGMENT_LAYOUT, SEARCH_ERRORS, WARMUP_ERRORS, ONE_SESSION };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The flag evidence of one target on one metric over the runs shown.
   /// </summary>
   /// <param name="target">Target name.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="runs">The runs shown for the row, oldest first.</param>
   /// <returns>Evidence items in <see cref="CODES"/> order, then run order.</returns>
   public static List<FlagEvidence> For( string target, string metric, IReadOnlyList<RunResult> runs )
   {
      var items = new List<FlagEvidence>();
      string passName = ClaimMetrics.PassOf( metric );
      foreach( RunResult run in runs )
      {
         int index = LastIndex( run.Conditions.Passes, target, passName );
         if( index >= 0 )
         {
            PassPart( items, run, run.Conditions.Passes[index], index );
         }

         TargetPart( items, run, run.Find( target )!, passName );
      }

      Layout( items, target, runs );
      return items.Select( ( item, order ) => ( item, order ) ).OrderBy( x => CodeIndex( x.item.Code ) ).ThenBy( x => x.order ).Select( x => x.item ).ToList();
   }

   /// <summary>
   /// The index of the last recorded pass of a target with a name (a pass name can repeat after a settle extension; the last one is the timed one).
   /// </summary>
   /// <param name="passes">The run's passes.</param>
   /// <param name="target">Target.</param>
   /// <param name="pass">Pass name.</param>
   /// <returns>The index, or -1.</returns>
   public static int LastIndex( IReadOnlyList<PassFacts> passes, string target, string pass )
   {
      for( int i = passes.Count - 1; i >= 0; i-- )
      {
         if( passes[i].Target == target && passes[i].Pass == pass )
         {
            return i;
         }
      }

      return -1;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Flags read from one pass: busy box, clock off, clock not read, governor.
   /// </summary>
   /// <param name="items">Receives the evidence.</param>
   /// <param name="run">The run.</param>
   /// <param name="pass">The pass.</param>
   /// <param name="index">Its index in conditions.passes.</param>
   private static void PassPart( List<FlagEvidence> items, RunResult run, PassFacts pass, int index )
   {
      string at = $"conditions.passes[{index}]";
      if( pass.BusyBox )
      {
         items.Add( new FlagEvidence( BUSY_BOX, run.Name, pass.Target, pass.Pass, index, new[] { ( $"{at}.outsideLoadDuring", Num( pass.OutsideLoadDuring, "0.000" ) ) } ) );
      }

      PassClock clock = ConsolidateClock.Recompute( run, pass );
      if( clock.Off )
      {
         items.Add( new FlagEvidence( CLOCK_OFF, run.Name, pass.Target, pass.Pass, index, new[] { ( $"{at}.engineMhzMedian", Num( clock.EngineMedian ) ), ( $"{at}.clientMhzMedian", Num( clock.ClientMedian ) ) } ) );
      }

      if( !clock.Read )
      {
         items.Add( new FlagEvidence( CLOCK_NOT_READ, run.Name, pass.Target, pass.Pass, index, Array.Empty<(string, string)>() ) );
      }

      if( pass.Governor != null && !RunConditions.IsPerformance( pass.Governor ) )
      {
         items.Add( new FlagEvidence( GOVERNOR, run.Name, pass.Target, pass.Pass, index, new[] { ( $"{at}.governor", pass.Governor ) } ) );
      }

      foreach( NotHeldPass held in run.Find( pass.Target )?.NotHeldPasses.Where( n => n.Pass == pass.Pass ) ?? Enumerable.Empty<NotHeldPass>() )
      {
         items.Add( new FlagEvidence( NOT_HELD, run.Name, pass.Target, pass.Pass, index, new[] { ( $"targets[{pass.Target}].notes", held.Token ), ( "timed", held.Timed ), ( "offPercent", held.OffPercent ), ( "settled", held.Settled ), ( "limitPercent", held.LimitPercent ) } ) );
      }
   }

   /// <summary>
   /// Flags read from the target's own record: unsettled, index not ready, errors.
   /// </summary>
   /// <param name="items">Receives the evidence.</param>
   /// <param name="run">The run.</param>
   /// <param name="t">The target's result.</param>
   /// <param name="passName">The pass of the metric the flags are for: the unsettled flag belongs to the passes the run's warning names, and to every pass when it names none.</param>
   private static void TargetPart( List<FlagEvidence> items, RunResult run, TargetResult t, string passName )
   {
      string at = $"targets[{t.Name}]";
      if( run.Conditions.ThrottleByTarget.TryGetValue( t.Name, out RecordedThrottle? rise ) && ( rise.CoreRise > 0 || rise.PackageRise > 0 ) )
      {
         string by = $"conditions.throttleByTarget.{t.Name}";
         items.Add( new FlagEvidence( THROTTLE, run.Name, t.Name, null, null, new[] { ( by + ".coreRise", Num( rise.CoreRise ) ), ( by + ".packageRise", Num( rise.PackageRise ) ) } ) );
      }

      if( t.Settled == false && ( t.UnsettledPasses.Count == 0 || t.UnsettledPasses.Contains( passName ) ) )
      {
         items.Add( new FlagEvidence( UNSETTLED, run.Name, t.Name, t.UnsettledPasses.Count == 0 ? null : passName, null, Array.Empty<(string, string)>() ) );
      }

      foreach( (string name, IndexStateValue? state) in new[] { ( "afterLoad", t.AfterLoad ), ( "afterSearch", t.AfterSearch ) } )
      {
         if( state?.Ready == false )
         {
            items.Add( new FlagEvidence( INDEX_NOT_READY, run.Name, t.Name, name, null, new[] { ( $"{at}.indexState.{name}.indexedVectors", Num( state.IndexedVectors ) ), ( $"{at}.indexState.{name}.totalVectors", Num( state.TotalVectors ) ) } ) );
         }
      }

      if( t.Errors > 0 )
      {
         items.Add( new FlagEvidence( SEARCH_ERRORS, run.Name, t.Name, null, null, new[] { ( $"{at}.search.errors", Num( t.Errors ) ) } ) );
      }

      if( t.WarmupErrors > 0 )
      {
         items.Add( new FlagEvidence( WARMUP_ERRORS, run.Name, t.Name, null, null, new[] { ( $"{at}.warmupErrors", Num( t.WarmupErrors ) ) } ) );
      }
   }

   /// <summary>
   /// The segment-layout flag: the layouts after the searches differ between runs (one item per run, value = the engine's own words for its layout).
   /// </summary>
   /// <param name="items">Receives the evidence.</param>
   /// <param name="target">Target.</param>
   /// <param name="runs">Runs shown.</param>
   private static void Layout( List<FlagEvidence> items, string target, IReadOnlyList<RunResult> runs )
   {
      var layouts = runs.Select( r => ( Run: r.Name, Layout: r.Find( target )!.AfterSearch?.Layout, Raw: SegmentLayout.RawMatch( r.Find( target )!.AfterSearch?.Detail ) ) ).ToList();
      if( layouts.All( l => l.Layout != null && l.Raw != null ) && layouts.Select( l => l.Layout ).Distinct( StringComparer.Ordinal ).Count() > 1 )
      {
         items.AddRange( layouts.Select( l => new FlagEvidence( SEGMENT_LAYOUT, l.Run, target, "afterSearch", null, new[] { ( $"targets[{target}].indexState.afterSearch.detail", l.Raw! ) } ) ) );
      }
   }

   /// <summary>
   /// Position of a code in <see cref="CODES"/>.
   /// </summary>
   /// <param name="code">Code.</param>
   /// <returns>Index.</returns>
   private static int CodeIndex( string code )
   {
      int index = CODES.ToList().IndexOf( code );
      return index < 0 ? int.MaxValue : index;
   }

   /// <summary>
   /// A recorded number as printed, or "not recorded".
   /// </summary>
   /// <param name="value">The number.</param>
   /// <param name="format">Format string.</param>
   /// <returns>The text.</returns>
   private static string Num( double? value, string format )
   {
      return value?.ToString( format, CultureInfo.InvariantCulture ) ?? ConsolidateIdentity.NOT_RECORDED;
   }

   /// <summary>
   /// A recorded whole number as printed, or "not recorded".
   /// </summary>
   /// <param name="value">The number.</param>
   /// <returns>The text.</returns>
   private static string Num( long? value )
   {
      return value?.ToString( CultureInfo.InvariantCulture ) ?? ConsolidateIdentity.NOT_RECORDED;
   }

   #endregion Private Methods
}

/// <summary>
/// One piece of flag evidence: the code, where it was recorded and the recorded values a sentence prints.
/// </summary>
/// <param name="Code">Flag code.</param>
/// <param name="Run">Run folder name.</param>
/// <param name="Target">Target.</param>
/// <param name="Pass">Pass name for a pass flag, the index-state snapshot for an index flag, or null.</param>
/// <param name="PassIndex">The pass's index in conditions.passes, or null for a target-level flag.</param>
/// <param name="Values">The recorded values: results.json path and the value as printed.</param>
public sealed record FlagEvidence( string Code, string Run, string Target, string? Pass, int? PassIndex, IReadOnlyList<(string Path, string Value)> Values );
