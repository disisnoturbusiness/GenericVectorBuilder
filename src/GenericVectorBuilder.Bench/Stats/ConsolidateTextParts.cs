using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Fills the templates of <see cref="ConsolidateText"/> for the headline, subtitle, threshold,
/// tables, recall, why, drift, clock and tail sections. Every number goes in through a source;
/// no string here is prose of its own (the templates hold all fixed words).
/// </summary>
public static class ConsolidateTextParts
{
   #region Public Methods

   /// <summary>
   /// Headline (or the stop sentences) and subtitle.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Head( Writer w )
   {
      if( w.Report.Status == Consolidator.STOPPED )
      {
         Stopped( w );
      }
      else if( !w.TwoSessions )
      {
         w.Add( "headline", T.Fill( T.HEADLINE_ONE_SESSION, S.Whole( w.Report.RunsPerSession ) ), S.Consolidated( "runsPerSession", S.Whole( w.Report.RunsPerSession ) ) );
      }
      else
      {
         ConsolidateHeadline.Write( w );
      }

      Subtitle( w );
   }

   /// <summary>
   /// The threshold and basis sentences.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Threshold( Writer w )
   {
      BasisInfo b = w.Report.Basis;
      string ratio = S.Ratio( b.TBp );
      w.Add( "rule.claim", T.Fill( T.THRESHOLD_RULE, ratio ), S.Consolidated( "threshold.tBp", ratio, "bp-ratio" ) );
      w.Add( "rule.threshold", T.Fill( T.THRESHOLD_TBP, S.Percent( b.TBp ), S.Percent( b.StepBp ), S.Percent( b.MarginBp ), S.Percent( b.MaxBp ), S.Percent( b.FloorBp ) ),
         S.Consolidated( "threshold.tBp", S.Percent( b.TBp ), "bp-pct" ), S.Consolidated( "basis.stepBp", S.Percent( b.StepBp ), "bp-pct" ),
         S.Consolidated( "basis.marginBp", S.Percent( b.MarginBp ), "bp-pct" ), S.Consolidated( "basis.maxBp", S.Percent( b.MaxBp ), "bp-pct" ),
         S.Consolidated( "basis.floorBp", S.Percent( b.FloorBp ), "bp-pct" ) );
      List<string> names = b.Runs.Select( r => r.Session ).Distinct( StringComparer.Ordinal ).ToList();
      w.Add( "basis.runs", T.Fill( T.THRESHOLD_BASIS, S.Whole( b.RunCount ), S.List( names ) ), S.Consolidated( "basis.runCount", S.Whole( b.RunCount ) ) );
      w.Add( "basis.identity", T.THRESHOLD_IDENTITY );
      w.Add( "basis.identity.missing", T.THRESHOLD_IDENTITY_MISSING );
      MaxMove( w, b );
      foreach( string metric in ClaimMetrics.ALL )
      {
         PerMetric( w, metric, b.PerMetric[metric] );
      }

      Splits( w, b );
      Exclusions( w, b );
      NoControl( w, b );
   }

   /// <summary>
   /// The table captions, the rows' notes and flags, and the targets without an exact pass.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Tables( Writer w )
   {
      foreach( MetricTable table in w.Report.Metrics )
      {
         Caption( w, table );
         foreach( MetricRow row in table.Rows )
         {
            Note( w, table.Metric, row );
            ConsolidateTextFlags.Write( w, table.Metric, row );
         }

         foreach( NoExactPass entry in table.NoExactPass )
         {
            NoExact( w, entry );
         }
      }
   }

   /// <summary>
   /// The recall sentences.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Recall( Writer w )
   {
      if( w.Report.Recall.Count == 0 )
      {
         return;
      }

      RunResult first = w.Sessions[0].Runs[0];
      string top = S.Whole( first.Top ?? 0 );
      string queries = S.Whole( first.QueryCount ?? 0 );
      string of = S.Whole( w.Report.Recall[0].Of );
      w.Add( "recall.caption", T.Fill( T.RECALL, top, queries, of ), S.ResultAll( "top", top ), S.ResultAll( "queryCount", queries ), S.Consolidated( "recall[0].of", of ) );
      foreach( RecallRow row in w.Report.Recall.Where( r => r.Differs ) )
      {
         var sources = new List<SentenceSource>();
         var parts = new List<string>();
         foreach( (string session, List<int> hits) in row.Hits )
         {
            for( int i = 0; i < hits.Count; i++ )
            {
               sources.Add( S.Consolidated( $"recall[target={row.Target}].hits.{session}[{i}]", S.Whole( hits[i] ) ) );
            }

            parts.Add( $"{session} {string.Join( ", ", hits.Select( h => S.Whole( h ) ) )}" );
         }

         w.Add( $"recall.differs.{row.Target}", T.Fill( T.RECALL_DIFFERS, row.Target, string.Join( "; ", parts ) ), sources );
      }
   }

   /// <summary>
   /// The why sentences: the fixed framing, where the costs come from, the ranges and the embedded engines.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Why( Writer w )
   {
      for( int i = 0; i < T.WHY_FRAMING.Count; i++ )
      {
         w.Add( $"why.framing.{i + 1}", T.WHY_FRAMING[i] );
      }

      string runs = S.Whole( w.Report.RunsPerSession );
      w.Add( "why.costs", T.Fill( T.WHY_COSTS, runs, w.Newest.Name ), S.Consolidated( "runsPerSession", runs ) );
      Range( w, "clientCpuMsPerSearch", T.WHY_RANGE, "why.range.client" );
      if( !Range( w, "engineCpuMsPerSearch", T.WHY_ENGINE_RANGE, "why.range.engine" ) )
      {
         w.Add( "why.range.engine", T.Fill( T.WHY_ENGINE_NONE, w.Newest.Name ) );
      }

      List<string> embedded = w.Report.Why.Where( r => r.Costs.ClientIncludesEngine ).Select( r => r.Target ).ToList();
      if( embedded.Count > 0 )
      {
         w.Add( "why.embedded", T.Fill( T.WHY_EMBEDDED, S.List( embedded ), ConsolidateWhy.EMBEDDED ), embedded.Select( e => S.TokenAll( $"targets[{e}].hosting", ConsolidateWhy.EMBEDDED ) ) );
      }

      ConsolidateSettings.Write( w );
   }

   /// <summary>
   /// The drift sentences (two-session mode).
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Drift( Writer w )
   {
      DriftInfo? d = w.Report.Drift;
      if( d == null )
      {
         return;
      }

      if( d.Largest != null )
      {
         w.Add( "drift.median", T.Fill( T.DRIFT_MEDIAN, d.From, d.To, S.Percent( d.MedianAbsMoveBp ), S.Percent( d.LargestAbsMoveBp ), d.Largest.Target, T.Label( d.Largest.Metric ) ),
            S.Consolidated( "drift.medianAbsMoveBp", S.Percent( d.MedianAbsMoveBp ), "bp-pct" ), S.Consolidated( "drift.largestAbsMoveBp", S.Percent( d.LargestAbsMoveBp ), "bp-pct" ),
            S.Consolidated( "drift.largest.target", d.Largest.Target ) );
      }

      w.Add( "drift.unconfirmed", T.Fill( T.DRIFT_UNCONFIRMED, S.Whole( d.UnconfirmedFromFirst ), d.From, d.To, S.Whole( d.UnconfirmedFromSecond ) ),
         S.Consolidated( "drift.unconfirmedFromFirst", S.Whole( d.UnconfirmedFromFirst ) ), S.Consolidated( "drift.unconfirmedFromSecond", S.Whole( d.UnconfirmedFromSecond ) ) );
      string from = S.Ratio( d.CloseFromBp );
      string to = S.Ratio( w.Report.Threshold.TBp );
      w.Add( "drift.close", T.Fill( T.DRIFT_CLOSE, S.Whole( d.CloseCount ), from, to ),
         S.Consolidated( "drift.closeCount", S.Whole( d.CloseCount ) ), S.Consolidated( "drift.closeFromBp", from, "bp-ratio" ), S.Consolidated( "threshold.tBp", to, "bp-ratio" ) );
      string line = S.Ratio( w.Report.Threshold.TBp );
      string margin = S.Whole( d.OnLineBp );
      if( d.OnLineCount == 0 )
      {
         w.Add( "drift.onLine", T.Fill( T.DRIFT_ONLINE_NONE, line, margin ), S.Consolidated( "threshold.tBp", line, "bp-ratio" ), S.Consolidated( "drift.onLineBp", margin ) );
      }
      else
      {
         string count = S.Whole( d.OnLineCount );
         w.Add( "drift.onLine", T.Fill( T.DRIFT_ONLINE, count, line, margin ), S.Consolidated( "drift.onLineCount", count ), S.Consolidated( "threshold.tBp", line, "bp-ratio" ), S.Consolidated( "drift.onLineBp", margin ) );
      }

      w.Add( "drift.scope", T.DRIFT_SCOPE );
   }

   /// <summary>
   /// The clock sentences per session and the dropped warnings.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Clock( Writer w )
   {
      var stated = new HashSet<string>( StringComparer.Ordinal );
      foreach( (string name, SessionClock c) in w.Report.Clock.PerSession )
      {
         string at = $"clock.perSession.{name}";
         if( c.Pinned && c.Uncore != null && c.PinnedMhz != null && c.CeilingBeforeMhz != null )
         {
            w.Add( $"disclosure.clock.held.{name}", T.Fill( T.CLOCK_HELD, name, T.Msr(), c.Uncore, S.Whole( c.PinnedMhz.Value ), S.Whole( c.CeilingBeforeMhz.Value ) ),
               S.File( T.MSR_FILE, T.MSR_TOKEN ), S.Consolidated( at + ".uncore", c.Uncore ), S.Consolidated( at + ".pinnedMhz", S.Whole( c.PinnedMhz.Value ) ),
               S.Consolidated( at + ".ceilingBeforeMhz", S.Whole( c.CeilingBeforeMhz.Value ) ), S.Consolidated( at + ".noTurbo", "1" ) );
         }
         else
         {
            w.Add( $"disclosure.clock.held.{name}", T.Fill( T.CLOCK_NOT_HELD, name ) );
         }

         ClockRule( w, name, c, stated );
         w.Add( $"disclosure.clock.checked.{name}", T.Fill( T.CLOCK_CHECKED, name, S.Whole( c.ClockOffPasses ), S.Whole( c.PassesEvaluated ), S.Whole( c.ClockUnreadPasses ) ),
            S.Consolidated( at + ".clockOffPasses", S.Whole( c.ClockOffPasses ) ), S.Consolidated( at + ".passesEvaluated", S.Whole( c.PassesEvaluated ) ),
            S.Consolidated( at + ".clockUnreadPasses", S.Whole( c.ClockUnreadPasses ) ) );
      }

      for( int i = 0; i < w.Report.Clock.DroppedWarnings.Count; i++ )
      {
         Dropped( w, i );
      }
   }

   /// <summary>
   /// The method quotes, the targets not in the report and the runs used.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Tail( Writer w )
   {
      RunResult first = w.Newest.Runs[0];
      w.Add( "method.intro", T.Fill( T.METHOD, first.Name ), w.RunSource( first.Name ) );
      var clauses = new ClauseWriter( w, "method" );
      for( int i = 0; i < first.Notes.Count; i++ )
      {
         if( !first.Notes[i].StartsWith( "WARNING", StringComparison.Ordinal ) )
         {
            clauses.Add( first.Name, $"notes[{i}]", w.Input.Classes.Split( string.Empty, RecordedTextClasses.NOTES_FIELD, first.Notes[i] ) );
         }
      }

      foreach( NotInReport entry in w.Report.NotInReport )
      {
         string text = entry.Code == "left out by --targets" ? T.Fill( T.NOT_IN_REPORT_TARGETS, entry.Target )
            : T.Fill( T.NOT_IN_REPORT_SETUP, entry.Target, S.List( entry.Fields ) );
         w.Add( $"notinreport.{entry.Target}", text );
      }

      string claim = S.Whole( w.Report.ClaimRunCount );
      string basis = S.Whole( w.Report.Basis.RunCount );
      w.Add( "runs.caption", T.Fill( T.RUNS, claim, basis ), S.Consolidated( "claimRunCount", claim ), S.Consolidated( "basis.runCount", basis ) );
      Reuse( w );
      Stale( w );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The stop sentences.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Stopped( Writer w )
   {
      if( w.Report.Guards.G2.Stopped )
      {
         w.Add( "stopped.g2", T.Fill( T.STOPPED_G2, w.Sessions[0].Name, w.Sessions[1].Name ) );
         for( int i = 0; i < w.Report.Guards.G2.Pairs.Count; i++ )
         {
            ReversedPair p = w.Report.Guards.G2.Pairs[i];
            int dec = T.Decimals( p.Metric );
            string a = S.Number( p.MediansReversed[0], dec );
            string b = S.Number( p.MediansReversed[1], dec );
            w.Add( $"stopped.g2.{i}", T.Fill( T.STOPPED_G2_PAIR, T.Label( p.Metric ), p.A, p.B, p.OrderedIn, p.ReversedIn, a, b ),
               S.Consolidated( $"guards.g2.pairs[{i}].mediansReversed[0]", a ), S.Consolidated( $"guards.g2.pairs[{i}].mediansReversed[1]", b ) );
         }
      }

      if( w.Report.Guards.G3.Stopped )
      {
         GuardG3 g = w.Report.Guards.G3;
         w.Add( "stopped.g3", T.Fill( T.STOPPED_G3, S.Whole( g.ChangedCount ), w.Sessions[0].Name, w.Sessions[1].Name, S.Whole( g.Limit ) ),
            S.Consolidated( "guards.g3.changedCount", S.Whole( g.ChangedCount ) ), S.Consolidated( "guards.g3.limit", S.Whole( g.Limit ) ) );
      }
   }

   /// <summary>
   /// The subtitle sentences: data, machine, sessions and the question file.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Subtitle( Writer w )
   {
      RunResult first = w.Sessions[0].Runs[0];
      string rows = S.Whole( first.Rows ?? 0 );
      string dims = S.Whole( first.Dimension ?? 0 );
      string queries = S.Whole( first.QueryCount ?? 0 );
      string top = S.Whole( first.Top ?? 0 );
      w.Add( "subtitle.data", T.Fill( T.SUBTITLE_DATA, first.Pipeline ?? ConsolidateIdentity.NOT_RECORDED, rows, dims, queries, top ),
         S.ResultAll( "pipeline", first.Pipeline ?? string.Empty ), S.ResultAll( "rows", rows ), S.ResultAll( "dimension", dims ), S.ResultAll( "queryCount", queries ), S.ResultAll( "top", top ) );
      MachineInfo m = w.Report.Machine;
      string cpus = S.Whole( m.LogicalCpus ?? 0 );
      string ram = S.Number( m.RamGiB ?? 0, 1 );
      w.Add( "subtitle.machine", T.Fill( T.SUBTITLE_MACHINE, m.Cpu ?? ConsolidateIdentity.NOT_RECORDED, cpus, ram ),
         S.ResultAll( "machine.cpu", m.Cpu ?? string.Empty ), S.ResultAll( "machine.logicalCpus", cpus ), S.ResultAll( "machine.ramGiB", ram ) );
      string template = ( first.Rows ?? 0 ) <= ConsolidateFraming.SMALL_COLLECTION_MAX_ROWS ? T.SUBTITLE_SCOPE_SMALL : T.SUBTITLE_SCOPE_LARGE;
      w.Add( "subtitle.scope", T.Fill( template, rows ), S.ResultAll( "rows", rows ) );
      foreach( ClaimSession session in w.Sessions )
      {
         var sources = session.Runs.Select( r => S.Result( r.Name, "startedUtc", r.StartedUtc! ) ).ToList();
         w.Add( $"subtitle.session.{session.Name}", T.Fill( T.SUBTITLE_SESSION, session.Name, S.List( session.Runs.Select( r => r.Name ).ToList() ), session.Runs[0].StartedUtc!, session.Runs[^1].StartedUtc! ),
            sources.Concat( session.Runs.Select( r => w.RunSource( r.Name ) ) ) );
      }

      QueriesInfo q = w.Report.Queries;
      List<string> matching = q.MatchingRuns;
      List<string> sessionsMatching = w.Sessions.Where( s => s.Runs.All( r => matching.Contains( r.Name ) ) ).Select( s => s.Name ).ToList();
      if( sessionsMatching.Count > 0 )
      {
         w.Add( "subtitle.questions", T.Fill( T.SUBTITLE_QUESTIONS, S.List( sessionsMatching ), q.CopyPath ),
            matching.Select( r => S.Result( r, "queriesFileSha256", q.CopySha256! ) ).Append( S.Consolidated( "queries.copySha256", q.CopySha256! ) ).Append( S.File( T.HASH_FILE, T.HASH_TOKEN ) ) );
      }

      List<ClaimSession> unmatched = w.Sessions.Where( s => !sessionsMatching.Contains( s.Name ) ).ToList();
      if( unmatched.Count > 1 && unmatched.All( s => string.Equals( s.Runs[0].Queries, unmatched[0].Runs[0].Queries, StringComparison.Ordinal ) ) )
      {
         RunResult same = unmatched[0].Runs[0];
         w.Add( "subtitle.queries", same.Queries ?? string.Empty, S.Quote( same.Name, "queries", same.Queries ?? string.Empty ) );
         return;
      }

      foreach( ClaimSession session in unmatched )
      {
         RunResult run = session.Runs[0];
         w.Add( $"subtitle.queries.{session.Name}", run.Queries ?? string.Empty, S.Quote( run.Name, "queries", run.Queries ?? string.Empty ) );
      }
   }

   /// <summary>
   /// The largest move and its differences.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="b">The basis.</param>
   private static void MaxMove( Writer w, BasisInfo b )
   {
      BasisMove? m = b.MaxMove;
      if( m == null )
      {
         return;
      }

      string what = T.Fill( m.Target != null ? T.MOVE_VALUE : T.MOVE_RATIO, T.Label( m.Metric ) );
      string who = m.Target ?? m.Pair!;
      string seedA = m.Seeds[0]?.ToString( System.Globalization.CultureInfo.InvariantCulture ) ?? ConsolidateIdentity.NOT_RECORDED;
      string seedB = m.Seeds[1]?.ToString( System.Globalization.CultureInfo.InvariantCulture ) ?? ConsolidateIdentity.NOT_RECORDED;
      w.Add( "basis.max", T.Fill( T.THRESHOLD_MAX, S.Percent( m.MoveBp ), what, who, m.Runs[0], seedA, m.Runs[1], seedB ),
         S.Consolidated( "basis.maxMove.moveBp", S.Percent( m.MoveBp ), "bp-pct" ), S.Consolidated( m.Target != null ? "basis.maxMove.target" : "basis.maxMove.pair", who ),
         S.Consolidated( "basis.maxMove.runs[0]", m.Runs[0] ), S.Consolidated( "basis.maxMove.runs[1]", m.Runs[1] ),
         S.Consolidated( "basis.maxMove.seeds[0]", seedA ), S.Consolidated( "basis.maxMove.seeds[1]", seedB ) );
      if( m.DifferenceNames.Count == 0 )
      {
         w.Add( "basis.max.differences", T.THRESHOLD_MAX_SAME );
         return;
      }

      w.Add( "basis.max.differences", T.Fill( T.THRESHOLD_MAX_DIFFERENCES, S.List( m.DifferenceNames ) ),
         m.DifferenceNames.Select( ( n, i ) => S.Consolidated( $"basis.maxMove.differenceNames[{i}]", n ) ) );
   }

   /// <summary>
   /// The all-runs and same-settings maxima of one metric.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="m">Its moves.</param>
   private static void PerMetric( Writer w, string metric, MetricBasis m )
   {
      string at = $"basis.perMetric.{metric}";
      var all = new List<SentenceSource>();
      w.Add( $"basis.all.{metric}", T.Fill( T.THRESHOLD_ALL, T.Label( metric ), Writer.Move( m.OneEngine, at + ".oneEngine", all ), Writer.Move( m.Pair, at + ".pair", all ) ), all );
      var same = new List<SentenceSource>();
      w.Add( $"basis.same.{metric}", T.Fill( T.THRESHOLD_SAME, T.Label( metric ), Writer.Move( m.OneEngineSameSettings, at + ".oneEngineSameSettings", same ), Writer.Move( m.PairSameSettings, at + ".pairSameSettings", same ) ), same );
   }

   /// <summary>
   /// The setup splits: how many and in which engines, then for each the fields that changed, the moves the change hides and the
   /// threshold they would give.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="b">The basis.</param>
   private static void Splits( Writer w, BasisInfo b )
   {
      if( b.SetupSplits.Count == 0 )
      {
         w.Add( "basis.splits", T.THRESHOLD_SPLITS_NONE );
         return;
      }

      string count = S.Whole( b.SetupSplitCount );
      List<string> engines = b.SetupSplits.Select( x => x.Target ).Distinct( StringComparer.Ordinal ).ToList();
      var sources = new List<SentenceSource> { S.Consolidated( "basis.setupSplitCount", count ) };
      sources.AddRange( b.SetupSplits.Select( ( x, i ) => S.Consolidated( $"basis.setupSplits[{i}].target", x.Target ) ).GroupBy( x => x.Value, StringComparer.Ordinal ).Select( g => g.First() ) );
      w.Add( "basis.splits", T.Fill( T.THRESHOLD_SPLITS, count, S.List( engines ) ), sources );
      w.Add( "basis.splits.each", T.THRESHOLD_SPLITS_EACH );
      for( int i = 0; i < b.SetupSplits.Count; i++ )
      {
         Split( w, b.SetupSplits[i], i );
      }
   }

   /// <summary>
   /// The sentences of one split: the fields that changed, the moves it hides with the threshold they would give, and the conditions the runs of its one-engine move also differ in.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="split">The split.</param>
   /// <param name="i">Its index in the basis.</param>
   private static void Split( Writer w, SetupSplit split, int i )
   {
      string at = $"basis.setupSplits[{i}]";
      var fieldSources = new List<SentenceSource> { S.Consolidated( at + ".target", split.Target ) };
      fieldSources.AddRange( split.Before.Sessions.Select( ( n, j ) => S.Consolidated( $"{at}.before.sessions[{j}]", n ) ) );
      fieldSources.AddRange( split.After.Sessions.Select( ( n, j ) => S.Consolidated( $"{at}.after.sessions[{j}]", n ) ) );
      fieldSources.AddRange( split.Fields.Select( ( n, j ) => S.Consolidated( $"{at}.fields[{j}]", n ) ) );
      w.Add( $"basis.split.{i}.fields", T.Fill( T.SPLIT_FIELDS, split.Target, S.List( split.Before.Sessions ), S.List( split.After.Sessions ), S.List( split.Fields ) ), fieldSources );
      var moveSources = new List<SentenceSource>();
      string one = SplitMove( split.OneEngine, at + ".oneEngine", moveSources, pair: false );
      string pair = SplitMove( split.Pair, at + ".pair", moveSources, pair: true );
      string tbp = S.Percent( split.TBpIfCounted );
      moveSources.Add( S.Consolidated( at + ".tBpIfCounted", tbp, "bp-pct" ) );
      w.Add( $"basis.split.{i}.moves", T.Fill( T.SPLIT_MOVES, one, pair, tbp ), moveSources );
      if( split.OneEngine is { DifferenceNames.Count: > 0 } move )
      {
         w.Add( $"basis.split.{i}.conditions", T.Fill( T.SPLIT_CONDITIONS, S.List( move.DifferenceNames ) ), move.DifferenceNames.Select( ( n, j ) => S.Consolidated( $"{at}.oneEngine.differenceNames[{j}]", n ) ) );
      }
   }

   /// <summary>
   /// A hidden move as "119.36% (QPS@8)" or "11.5% (sqlitevec/oracle QPS@8)", with its sources; "none" for no move.
   /// </summary>
   /// <param name="move">The move, or null.</param>
   /// <param name="path">Its path in consolidated.json.</param>
   /// <param name="sources">Receives the sources.</param>
   /// <param name="pair">True for a pair move, which names the pair.</param>
   /// <returns>The text.</returns>
   private static string SplitMove( BasisMove? move, string path, List<SentenceSource> sources, bool pair )
   {
      if( move == null )
      {
         return T.MOVE_NONE;
      }

      string pct = S.Percent( move.MoveBp );
      sources.Add( S.Consolidated( path + ".moveBp", pct, "bp-pct" ) );
      if( pair )
      {
         sources.Add( S.Consolidated( path + ".pair", move.Pair! ) );
      }

      return pair ? $"{pct}% ({move.Pair} {T.Label( move.Metric )})" : $"{pct}% ({T.Label( move.Metric )})";
   }

   /// <summary>
   /// The exclusion rows and the maxima with each kind kept in.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="b">The basis.</param>
   private static void Exclusions( Writer w, BasisInfo b )
   {
      for( int i = 0; i < b.Exclusions.Count; i++ )
      {
         ExclusionRow row = b.Exclusions[i];
         string metric = row.Metric == "*" ? T.EVERY_METRIC : T.Label( ClaimMetrics.FromAnyName( row.Metric ) );
         var sources = row.Seeds.Select( ( s, j ) => S.Consolidated( $"basis.exclusions[{i}].seeds[{j}]", S.Whole( s ) ) ).ToList();
         string item = row.Item?.ToString( System.Globalization.CultureInfo.InvariantCulture ) ?? ConsolidateIdentity.NOT_RECORDED;
         sources.Add( S.Consolidated( $"basis.exclusions[{i}].item", item ) );
         string evidence = ( row.SeedEvidence.Length > 0 ? row.Evidence.FirstOrDefault( e => e.Contains( row.SeedEvidence, StringComparison.Ordinal ) ) : null ) ?? row.Evidence.FirstOrDefault()
            ?? throw new ConsolidateRefusal( $"exclusion row {i} ({row.Target}) lists no evidence words to cite from {row.Source}" );
         sources.Add( S.File( row.Source, evidence ) );
         sources.Add( S.Consolidated( $"basis.exclusions[{i}].source", row.Source ) );
         w.Add( $"basis.exclusion.{i}", T.Fill( T.THRESHOLD_EXCLUSION, row.Target, metric, S.List( row.Seeds.Select( s => S.Whole( s ) ).ToList() ), row.Kind, row.Source, item, evidence ), sources );
      }

      foreach( KeptIn kept in b.ExclusionsKept )
      {
         string at = $"basis.exclusionsKept[kind={kept.Kind}]";
         string one = S.Percent( kept.OneEngine?.MoveBp ?? 0 );
         string pair = S.Percent( kept.Pair?.MoveBp ?? 0 );
         string tbp = S.Percent( kept.TBpIfKept ?? 0 );
         w.Add( $"basis.kept.{kept.Kind.Replace( ' ', '-' )}", T.Fill( T.THRESHOLD_KEPT, kept.Kind, one, pair, tbp ),
            S.Consolidated( at + ".oneEngine.moveBp", one, "bp-pct" ), S.Consolidated( at + ".pair.moveBp", pair, "bp-pct" ), S.Consolidated( at + ".tBpIfKept", tbp, "bp-pct" ) );
      }
   }

   /// <summary>
   /// The no-machine-control maxima and the left-out count.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="b">The basis.</param>
   private static void NoControl( Writer w, BasisInfo b )
   {
      if( b.NoMachineControl.OneEngine != null && b.NoMachineControl.Pair != null )
      {
         string n = S.Whole( b.NoMachineControlCount );
         string one = S.Percent( b.NoMachineControl.OneEngine.MoveBp );
         string pair = S.Percent( b.NoMachineControl.Pair.MoveBp );
         w.Add( "basis.noMachineControl", T.Fill( T.THRESHOLD_NO_CONTROL, n, one, pair ), S.Consolidated( "basis.noMachineControlCount", n ),
            S.Consolidated( "basis.noMachineControl.oneEngine.moveBp", one, "bp-pct" ), S.Consolidated( "basis.noMachineControl.pair.moveBp", pair, "bp-pct" ) );
      }

      string left = S.Whole( b.LeftOutCount );
      w.Add( "basis.leftOut", T.Fill( T.THRESHOLD_LEFT_OUT, left ), S.Consolidated( "basis.leftOutCount", left ) );
   }

   /// <summary>
   /// A table's caption sentences.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="table">The table.</param>
   private static void Caption( Writer w, MetricTable table )
   {
      string slot = $"table.{table.Metric}";
      switch( table.Metric )
      {
         case ClaimMetrics.P50:
            w.Add( slot + ".caption", T.Fill( T.TABLE_P50, S.Whole( w.Report.RunsShown ) ), S.Consolidated( "runsShown", S.Whole( w.Report.RunsShown ) ) );
            break;
         case ClaimMetrics.QPS1:
            w.Add( slot + ".caption", T.TABLE_QPS1 );
            break;
         case ClaimMetrics.QPS8:
            w.Add( slot + ".caption", T.Fill( T.TABLE_QPS8, "8" ), S.ResultAll( "concurrency[1]", "8" ) );
            break;
         default:
            w.Add( slot + ".caption", T.TABLE_EXACT );
            break;
      }

      if( !w.TwoSessions )
      {
         w.Add( slot + ".oneSession", T.Fill( T.TABLE_ONE_SESSION_MODE, w.Newest.Name ) );
      }
      else if( table.Rows.Any( r => r.Status == ClaimRule.ONE_SESSION ) )
      {
         w.Add( slot + ".oneSession", T.Fill( T.TABLE_ONE_SESSION_ROWS, S.Whole( w.Report.RunsPerSession ), w.Newest.Name ), S.Consolidated( "runsPerSession", S.Whole( w.Report.RunsPerSession ) ) );
      }
   }

   /// <summary>
   /// The in-memory note on a row whose storage fact is documented as in-memory (Redis).
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="row">The row.</param>
   private static void Note( Writer w, string metric, MetricRow row )
   {
      WhyFact? storage = ConsolidateHeadline.InMemoryFact( w, row.Target );
      if( storage != null )
      {
         SentenceSource doc = S.Fact( storage );
         List<WhyFact> setup = ConsolidateTextDisclosures.ComposeSettings( w, row.Target );
         row.Note = setup.Count == 2
            ? w.Add( $"row.{metric}.{row.Target}.note", T.Fill( T.ROW_MEMORY, row.Target, doc.Value, setup[0].Text, setup[1].Text ), new[] { doc }.Concat( setup.Select( S.Fact ) ) )
            : w.Add( $"row.{metric}.{row.Target}.note", T.Fill( T.ROW_MEMORY_DOCS, row.Target, doc.Value ), doc );
      }
   }

   /// <summary>
   /// The sentence for a target without an exact pass.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="entry">The entry.</param>
   private static void NoExact( Writer w, NoExactPass entry )
   {
      string slot = $"table.{ClaimMetrics.EXACT}.absent.{entry.Target}";
      if( entry.Mode == "exact" && entry.FactSource != null && entry.FactText != null )
      {
         w.Add( slot, T.Fill( T.TABLE_EXACT_ABSENT_EXACT, entry.Target, entry.FactText ), SourceResolver.FromFactSource( entry.FactSource ) );
      }
      else
      {
         w.Add( slot, T.Fill( T.TABLE_EXACT_ABSENT, entry.Target ) );
      }
   }

   /// <summary>
   /// A range sentence of the why costs at one searcher.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="field">The costs field.</param>
   /// <param name="template">The template.</param>
   /// <param name="slot">The slot.</param>
   /// <returns>False when no target has the figure.</returns>
   private static bool Range( Writer w, string field, string template, string slot )
   {
      var values = w.Report.Why.Select( r => ( r.Target, Value: ( field == "clientCpuMsPerSearch" ? r.Costs.ClientCpuMsPerSearch : r.Costs.EngineCpuMsPerSearch ).GetValueOrDefault( "1" ) ) )
         .Where( x => x.Value != null ).OrderBy( x => x.Value ).ThenBy( x => x.Target, StringComparer.Ordinal ).ToList();
      if( values.Count == 0 )
      {
         return false;
      }

      string low = S.Number( values[0].Value!.Value, 3 );
      string high = S.Number( values[^1].Value!.Value, 3 );
      w.Add( slot, T.Fill( template, low, values[0].Target, high, values[^1].Target ),
         S.Consolidated( $"why[target={values[0].Target}].costs.{field}.1", low ), S.Consolidated( $"why[target={values[^1].Target}].costs.{field}.1", high ) );
      return true;
   }

   /// <summary>
   /// The sentence that defines the median rule, written the first time a session's clock check uses it and again only when a later session's tolerance, pinned clock or CPU grouping differs.
   /// Why here: "by the median rule" in the next sentence is meaningless to a reader unless the rule is stated first, with the tolerance and the clock it is held against.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="name">The session.</param>
   /// <param name="c">Its clock.</param>
   /// <param name="stated">The rules already stated, by their key.</param>
   private static void ClockRule( Writer w, string name, SessionClock c, HashSet<string> stated )
   {
      RunConditions conditions = w.Sessions.First( s => s.Name == name ).Runs[0].Conditions;
      bool split = CpuSet.TryParse( conditions.EngineCpus ) != null && CpuSet.TryParse( conditions.ClientCpus ) != null;
      if( c.ToleranceBp == null || c.PinnedMhz == null || !stated.Add( $"{c.ToleranceBp}|{c.PinnedMhz}|{split}" ) )
      {
         return;
      }

      string at = $"clock.perSession.{name}";
      string tolerance = S.Percent( c.ToleranceBp.Value );
      string pinned = S.Whole( c.PinnedMhz.Value );
      w.Add( $"disclosure.clock.rule.{name}", T.Fill( split ? T.CLOCK_RULE : T.CLOCK_RULE_ALL, tolerance, pinned ),
         S.Consolidated( at + ".toleranceBp", tolerance, "bp-pct" ), S.Consolidated( at + ".pinnedMhz", pinned ) );
   }

   /// <summary>
   /// One dropped warning's sentence; the warning's own words travel as data in clock.droppedWarnings[].text, which the page and
   /// consolidated.md print in their dropped-warnings table.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="i">Its index.</param>
   private static void Dropped( Writer w, int i )
   {
      DroppedWarning d = w.Report.Clock.DroppedWarnings[i];
      string session = w.Sessions.First( s => s.Runs.Any( r => r.Name == d.Run ) ).Name;
      SessionClock clock = w.Report.Clock.PerSession[session];
      string e = S.Whole( d.EngineMedianMhz ?? 0 );
      string c = S.Whole( d.ClientMedianMhz ?? 0 );
      string tolerance = S.Percent( clock.ToleranceBp ?? 0 );
      w.Add( $"disclosure.clock.dropped.{i}", T.Fill( T.CLOCK_DROPPED, d.Run, d.Target, T.PassLabel( d.Pass ), e, c, tolerance ),
         S.Consolidated( $"clock.droppedWarnings[{i}].run", d.Run ), w.RunSource( d.Run ), S.Consolidated( $"clock.droppedWarnings[{i}].engineMedianMhz", e ),
         S.Consolidated( $"clock.droppedWarnings[{i}].clientMedianMhz", c ), S.Consolidated( $"clock.perSession.{session}.toleranceBp", tolerance, "bp-pct" ) );
   }

   /// <summary>
   /// The reuse note (slots runs.reuse.NAME) for each session, claim or basis, whose verdict file in the repository holds the word that blocked
   /// the report it judged, naming the blocked report's folder when one sits beside the runs.
   /// Why every session and not the claim sessions alone: the v5 and v6 runs feed the threshold basis and were blocked in their own sets
   /// (blocked-2026-10-05-v5 and blocked-2026-10-05-v6), and a reader who opens one of their run pages must be told that, not left to
   /// read a page that says only that a v7 report was blocked.
   /// Why only then: the note quotes the verdict; a session without one has nothing to quote.
   /// Order: the claim sessions first, then the basis sessions, each note in its own slot (runs.reuse.NAME) and with its runs in the report's reuse list, so a page
   /// that shows one note on a run page can pick the note of that run's session.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Reuse( Writer w )
   {
      IEnumerable<(string Name, List<string> Runs)> sessions = w.Sessions.Select( s => ( s.Name, s.Runs.Select( r => r.Name ).ToList() ) )
         .Concat( w.Input.BasisSessions.Select( b => ( b.Name, b.Runs.Select( r => r.Name ).ToList() ) ) );
      foreach( ( string name, List<string> runs ) in sessions )
      {
         string? verdict = BlockingVerdict( w.RepoRoot, name );
         if( verdict != null )
         {
            w.Report.Reuse.Add( new ReuseNote { Session = name, Verdict = verdict, Runs = runs, BlockedFolders = BlockedFolders( w.Input.ResultsRoot, name ) } );
         }
      }

      foreach( ReuseNote note in w.Report.Reuse )
      {
         string at = $"reuse[session={note.Session}]";
         var sources = new List<SentenceSource> { S.File( note.Verdict, T.REUSE_TOKEN ), S.Consolidated( at + ".verdict", note.Verdict ) };
         if( note.BlockedFolders.Count == 0 )
         {
            w.Add( $"runs.reuse.{note.Session}", T.Fill( T.RUNS_REUSE, note.Session, note.Verdict, T.REUSE_TOKEN ), sources );
            continue;
         }

         sources.AddRange( note.BlockedFolders.Select( ( f, i ) => S.Consolidated( $"{at}.blockedFolders[{i}]", f ) ) );
         w.Add( $"runs.reuse.{note.Session}", T.Fill( T.RUNS_REUSE_SET, note.Session, note.Verdict, T.REUSE_TOKEN, S.List( note.BlockedFolders ) ), sources );
      }
   }

   /// <summary>
   /// The verdict file of a session in the repository (design/verdicts/NAME-verdict.md or .txt) when it holds the word that blocked its report.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="session">Session name.</param>
   /// <returns>The repository path, or null when there is no such file or it does not hold the word.</returns>
   private static string? BlockingVerdict( string repoRoot, string session )
   {
      string folder = Path.Combine( repoRoot, "design", "verdicts" );
      if( !Directory.Exists( folder ) )
      {
         return null;
      }

      foreach( string file in Directory.GetFiles( folder, $"{session}-verdict.*" ).OrderBy( f => f, StringComparer.Ordinal ) )
      {
         if( File.ReadAllText( file ).Contains( T.REUSE_TOKEN, StringComparison.Ordinal ) )
         {
            return $"design/verdicts/{Path.GetFileName( file )}";
         }
      }

      return null;
   }

   /// <summary>
   /// The blocked reports of a session found beside the runs: folders named blocked-DATE-NAME in the results folder.
   /// </summary>
   /// <param name="resultsRoot">The results folder.</param>
   /// <param name="session">Session name.</param>
   /// <returns>The folder names in order; empty when none.</returns>
   private static List<string> BlockedFolders( string resultsRoot, string session )
   {
      if( !Directory.Exists( resultsRoot ) )
      {
         return new List<string>();
      }

      return Directory.GetDirectories( resultsRoot ).Select( d => Path.GetFileName( d ) )
         .Where( n => n.StartsWith( "blocked-", StringComparison.Ordinal ) && n.EndsWith( "-" + session, StringComparison.Ordinal ) ).OrderBy( n => n, StringComparer.Ordinal ).ToList();
   }

   /// <summary>
   /// The run pages that carry a retired framing sentence (hole H4).
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Stale( Writer w )
   {
      foreach( SessionInfo session in w.Report.Sessions )
      {
         foreach( RunRef run in session.Runs.Where( r => r.StaleLines.Count > 0 ) )
         {
            string line = S.Whole( run.StaleLines[0].Line );
            w.Add( $"runs.stale.{run.Folder}", T.Fill( T.RUNS_STALE, run.Folder, line ),
               S.Consolidated( $"sessions[name={session.Name}].runs[folder={run.Folder}].staleLines[0].line", line ), w.RunSource( Path.GetFileName( run.Folder ) ) );
         }
      }
   }

   #endregion Private Methods
}
