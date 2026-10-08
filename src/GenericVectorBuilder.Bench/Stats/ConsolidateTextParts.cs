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
   /// The targets whose protocol fact names the benchmark's own HttpClient REST code, each with that fact.
   /// </summary>
   /// <param name="report">The report, whose why rows are filled.</param>
   /// <returns>The targets in why-table order.</returns>
   public static List<(string Target, WhyFact Fact)> HttpClientTargets( ConsolidatedReport report )
   {
      return report.Why.Select( r => ( r.Target, Fact: r.Facts.FirstOrDefault( f => f.Kind == "protocol" && f.Text.StartsWith( T.HTTP_CLIENT, StringComparison.Ordinal ) ) ) )
         .Where( x => x.Fact != null ).Select( x => ( x.Target, x.Fact! ) ).ToList();
   }

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
      BasisClock( w, b );
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

         NotHeld( w, table.Metric );
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
      for( int i = 0; i < w.Report.RecallDerivedSessions.Count; i++ )
      {
         w.Add( $"recall.derived.{w.Report.RecallDerivedSessions[i]}", T.Fill( T.RECALL_DERIVED, w.Report.RecallDerivedSessions[i], queries, top ),
            S.Consolidated( $"recallDerivedSessions[{i}]", w.Report.RecallDerivedSessions[i] ), S.ResultAll( "queryCount", queries ), S.ResultAll( "top", top ) );
      }
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

      for( int i = 0; i < d.Excluded.Count; i++ )
      {
         DriftExcluded x = d.Excluded[i];
         string at = $"drift.excluded[{i}]";
         string move = S.Percent( x.AbsMoveBp );
         string[] figures = { S.Number( x.FromMin, 0 ), S.Number( x.FromMax, 0 ), S.Number( x.ToMin, 0 ), S.Number( x.ToMax, 0 ) };
         w.Add( $"drift.excluded.{x.Target}.{x.Metric}", T.Fill( T.DRIFT_EXCLUDED, x.Target, T.Label( x.Metric ), move, d.From, figures[0], figures[1], d.To, figures[2], figures[3] ),
            S.Consolidated( at + ".target", x.Target ), S.Consolidated( at + ".absMoveBp", move, "bp-pct" ), S.Consolidated( at + ".fromMin", figures[0] ), S.Consolidated( at + ".fromMax", figures[1] ),
            S.Consolidated( at + ".toMin", figures[2] ), S.Consolidated( at + ".toMax", figures[3] ) );
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
         w.Add( "drift.onLine", T.Fill( d.OnLineCount == 1 ? T.DRIFT_ONLINE_ONE : T.DRIFT_ONLINE, count, line, margin ), S.Consolidated( "drift.onLineCount", count ), S.Consolidated( "threshold.tBp", line, "bp-ratio" ), S.Consolidated( "drift.onLineBp", margin ) );
      }

      if( d.SessionDifferences.Count > 0 )
      {
         w.Add( "drift.sessions", T.Fill( T.DRIFT_SESSIONS, S.List( d.SessionDifferences ) ), d.SessionDifferences.Select( ( n, i ) => S.Consolidated( $"drift.sessionDifferences[{i}]", n ) ) );
      }

      if( d.OutsideLoad.Count == 2 )
      {
         OutsideLoad( w, d.OutsideLoad );
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
         if( c.Pinned && c.Uncore != null && c.PinnedMhz != null && c.CeilingBeforeMhz != null && c.KernelMedianMhz != null )
         {
            ClockHeld( w, name, c, at );
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
   /// The outside load each session's passes saw, run medians, from the passes' own record.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="load">The two sessions' outside load.</param>
   private static void OutsideLoad( Writer w, IReadOnlyList<SessionOutsideLoad> load )
   {
      string a = S.Exact( load[0].MinCpus );
      string b = S.Exact( load[0].MaxCpus );
      string c = S.Exact( load[1].MinCpus );
      string e = S.Exact( load[1].MaxCpus );
      w.Add( "drift.outsideLoad", T.Fill( T.DRIFT_OUTSIDE_LOAD, a, b, load[0].Session, c, e, load[1].Session ),
         S.Consolidated( "drift.outsideLoad[0].minCpus", a ), S.Consolidated( "drift.outsideLoad[0].maxCpus", b ), S.Consolidated( "drift.outsideLoad[0].session", load[0].Session ),
         S.Consolidated( "drift.outsideLoad[1].minCpus", c ), S.Consolidated( "drift.outsideLoad[1].maxCpus", e ), S.Consolidated( "drift.outsideLoad[1].session", load[1].Session ) );
   }

   /// <summary>
   /// The sentence under a table for each row it shows and does not rank because the runs recorded its timed pass NOT HELD.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="metric">Metric id.</param>
   private static void NotHeld( Writer w, string metric )
   {
      if( w.Report.NotHeld == null )
      {
         return;
      }

      for( int i = 0; i < w.Report.NotHeld.Unranked.Count; i++ )
      {
         NotHeldRow row = w.Report.NotHeld.Unranked[i];
         if( row.Metric != metric )
         {
            continue;
         }

         string at = $"notHeld.unranked[{i}]";
         string runs = S.Whole( row.Runs );
         string total = S.Whole( w.Report.RunsShown );
         w.Add( $"table.{metric}.notHeld.{row.Target}", T.Fill( T.TABLE_NOT_HELD, row.Target, T.PassLabel( row.Pass ), runs, total ),
            S.Consolidated( at + ".target", row.Target ), S.Consolidated( at + ".runs", runs ), S.Consolidated( "runsShown", total ) );
      }
   }

   /// <summary>
   /// The sentence that says a session held the clock: turbo off, the uncore MSR, the ratio the pin is and what the kernel reads at it.
   /// Why the ratio and the kernel's figure: "every CPU at 3500 MHz" is a label; the CPU is held at its top ratio, and the kernel's median reading for that ratio is 3492 MHz.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="name">Session name.</param>
   /// <param name="c">The session's clock.</param>
   /// <param name="at">Its path in consolidated.json.</param>
   private static void ClockHeld( Writer w, string name, SessionClock c, string at )
   {
      string mhz = S.Whole( c.PinnedMhz!.Value );
      string kernel = S.Whole( c.KernelMedianMhz!.Value );
      string ceiling = S.Whole( c.CeilingBeforeMhz!.Value );
      var sources = new List<SentenceSource>
      {
         S.File( T.MSR_FILE, T.MSR_TOKEN ), S.Consolidated( at + ".uncore", c.Uncore! ), S.Consolidated( at + ".pinnedMhz", mhz ), S.Consolidated( at + ".kernelMedianMhz", kernel ),
         S.Consolidated( at + ".ceilingBeforeMhz", ceiling ), S.Consolidated( at + ".noTurbo", "1" ),
      };
      if( c.PinnedRatio is int ratio )
      {
         sources.Add( S.Consolidated( at + ".pinnedRatio", S.Whole( ratio ) ) );
         sources.Add( S.File( T.RATIO_STEP_FILE, T.RATIO_STEP_TOKEN ) );
         w.Add( $"disclosure.clock.held.{name}", T.Fill( T.CLOCK_HELD, name, T.Msr(), c.Uncore!, S.Whole( ratio ), mhz, kernel, ceiling ), sources );
         return;
      }

      w.Add( $"disclosure.clock.held.{name}", T.Fill( T.CLOCK_HELD_NO_RATIO, name, T.Msr(), c.Uncore!, mhz, kernel, ceiling ), sources );
   }

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
      Clients( w );
      foreach( ClaimSession session in w.Sessions )
      {
         var sources = session.Runs.Select( r => S.Result( r.Name, "startedUtc", r.StartedUtc! ) ).ToList();
         w.Add( $"subtitle.session.{session.Name}", T.Fill( T.SUBTITLE_SESSION, session.Name, S.List( session.Runs.Select( r => r.Name ).ToList() ), session.Runs[0].StartedUtc!, session.Runs[^1].StartedUtc! ),
            sources.Concat( session.Runs.Select( r => w.RunSource( r.Name ) ) ) );
      }

      Builds( w );
      QueriesInfo q = w.Report.Queries;
      List<string> matching = q.MatchingRuns;
      List<string> sessionsMatching = w.Sessions.Where( s => s.Runs.All( r => matching.Contains( r.Name ) ) ).Select( s => s.Name ).ToList();
      if( sessionsMatching.Count > 0 )
      {
         w.Add( "subtitle.questions", T.Fill( T.SUBTITLE_QUESTIONS, S.List( sessionsMatching ), q.CopyPath ),
            matching.Select( r => S.Result( r, "queriesFileSha256", q.CopySha256! ) ).Append( S.Consolidated( "queries.copySha256", q.CopySha256! ) ).Append( S.File( T.HASH_FILE, T.HASH_TOKEN ) ) );
      }

      List<ClaimSession> unmatched = w.Sessions.Where( s => !sessionsMatching.Contains( s.Name ) ).ToList();
      Truth( w, q );
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
   /// The builds behind the report: the commit that measured each session whose runs record one, and the commit that consolidated them.
   /// Why both: when the report code is fixed after the runs, the build that makes the report is not the build that made the runs, and the page says so.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Builds( Writer w )
   {
      List<SessionBuild> measured = w.Report.Build.Measured.Where( b => b.CommitShort != null ).ToList();
      foreach( SessionBuild b in measured )
      {
         int index = w.Report.Build.Measured.IndexOf( b );
         ClaimSession session = w.Sessions.First( s => s.Name == b.Session );
         w.Add( $"subtitle.build.{b.Session}", T.Fill( T.SUBTITLE_BUILD_MEASURED, b.Session, b.CommitShort! ),
            session.Runs.Select( r => S.Token( r.Name, "conditions.build.commit", b.CommitShort! ) ).Append( S.Consolidated( $"build.measured[{index}].session", b.Session ) ) );
      }

      ConsolidatingBuild c = w.Report.Build.ConsolidatedBy;
      if( c.CommitShort == null )
      {
         w.Add( "subtitle.build.consolidated", T.SUBTITLE_BUILD_NONE );
         return;
      }

      List<string> same = measured.Where( b => b.Commit == c.Commit ).Select( b => b.Session ).ToList();
      var sources = new List<SentenceSource> { S.Consolidated( "build.consolidatedBy.commitShort", c.CommitShort ) };
      if( measured.Count == 0 )
      {
         w.Add( "subtitle.build.consolidated", T.Fill( T.SUBTITLE_BUILD_ONLY, c.CommitShort ), sources );
         return;
      }

      if( same.Count > 0 )
      {
         w.Add( "subtitle.build.consolidated", T.Fill( T.SUBTITLE_BUILD_SAME, c.CommitShort, S.List( same ) ), sources.Concat( same.Select( ( n, i ) => S.Consolidated( $"build.measured[session={n}].session", n ) ) ) );
         return;
      }

      List<string> others = measured.Select( b => b.Session ).ToList();
      w.Add( "subtitle.build.consolidated", T.Fill( T.SUBTITLE_BUILD_OTHER, c.CommitShort, S.List( others ) ), sources.Concat( others.Select( n => S.Consolidated( $"build.measured[session={n}].session", n ) ) ) );
   }

   /// <summary>
   /// The engines the benchmark reaches with its own HttpClient REST code, from their protocol facts.
   /// Why: the figure of such an engine is the cost of the benchmark's own request code, and of another engine the cost of that engine's vendor library; a sentence that says
   /// "the engine's .NET client" is true of the second kind and not of the first.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Clients( Writer w )
   {
      List<(string Target, WhyFact Fact)> http = HttpClientTargets( w.Report );
      if( http.Count == 0 )
      {
         return;
      }

      string count = S.Whole( http.Count );
      string total = S.Whole( w.Report.TargetCount );
      w.Add( "subtitle.clients", T.Fill( T.SUBTITLE_CLIENTS, total, count, S.List( http.Select( x => x.Target ).ToList() ) ),
         new[] { S.Consolidated( "targetCount", total ), S.Consolidated( "httpClientCount", count ) }.Concat( http.Select( x => S.Fact( x.Fact ) ) ) );
   }

   /// <summary>
   /// The truthNdcg of the claim runs, for the sessions whose runs record no question-file hash: the same figure in every run shows the runs read the same questions.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="q">The queries block.</param>
   private static void Truth( Writer w, QueriesInfo q )
   {
      List<ClaimSession> unrecorded = w.Sessions.Where( s => s.Runs.Any( r => q.UnrecordedRuns.Contains( r.Name ) ) ).ToList();
      List<double> values = w.Sessions.SelectMany( s => s.Runs ).Select( r => r.TruthNdcg ).OfType<double>().Distinct().ToList();
      if( unrecorded.Count == 0 || values.Count != 1 || w.Sessions.SelectMany( s => s.Runs ).Any( r => r.TruthNdcg == null ) )
      {
         return;
      }

      string value = values[0].ToString( "R", System.Globalization.CultureInfo.InvariantCulture );
      string runs = S.Whole( w.Report.ClaimRunCount );
      w.Add( "subtitle.truth", T.Fill( T.SUBTITLE_TRUTH, S.List( unrecorded.Select( s => s.Name ).ToList() ), value, runs ),
         S.ResultAll( "truthNdcg", value ), S.Consolidated( "claimRunCount", runs ) );
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
      w.Add( "basis.max.rounding", T.Fill( T.THRESHOLD_ROUNDING, S.Exact( m.MoveBpExact / 100.0 ), S.Percent( m.MoveBp ) ),
         S.Consolidated( "basis.maxMove.moveBpExact", S.Exact( m.MoveBpExact / 100.0 ), "bp-pct" ), S.Consolidated( "basis.maxMove.moveBp", S.Percent( m.MoveBp ), "bp-pct" ), S.Consolidated( "basis.moveRule", b.MoveRule ) );
      if( m.DifferenceNames.Count == 0 )
      {
         w.Add( "basis.max.differences", T.THRESHOLD_MAX_SAME );
      }
      else
      {
         w.Add( "basis.max.differences", T.Fill( T.THRESHOLD_MAX_DIFFERENCES, S.List( m.DifferenceNames ) ),
            m.DifferenceNames.Select( ( n, i ) => S.Consolidated( $"basis.maxMove.differenceNames[{i}]", n ) ) );
      }

      MaxMoveNotHeld( w, b );
   }

   /// <summary>
   /// Which basis runs held the clock, by session: the sessions whose every run held it, the sessions none of whose runs did, and any session in between.
   /// Why: "all with machine control on" is true of the v5 and v6 runs as well as of v7 and v8, but only v7 and v8 held the clock, and the threshold rests on all four.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="b">The basis.</param>
   private static void BasisClock( Writer w, BasisInfo b )
   {
      if( b.ClockHeld.Count == 0 )
      {
         return;
      }

      List<SessionClockHeld> all = b.ClockHeld.Where( c => c.Held == c.Runs ).ToList();
      List<SessionClockHeld> none = b.ClockHeld.Where( c => c.Held == 0 ).ToList();
      List<SessionClockHeld> some = b.ClockHeld.Where( c => c.Held != 0 && c.Held != c.Runs ).ToList();
      IEnumerable<SentenceSource> Sources( IEnumerable<SessionClockHeld> sessions ) => sessions.SelectMany( c => new[]
      {
         S.Consolidated( $"basis.clockHeld[session={c.Session}].session", c.Session ), S.Consolidated( $"basis.clockHeld[session={c.Session}].held", S.Whole( c.Held ) ),
         S.Consolidated( $"basis.clockHeld[session={c.Session}].runs", S.Whole( c.Runs ) ),
      } );
      if( none.Count == 0 && some.Count == 0 )
      {
         w.Add( "basis.clock", T.THRESHOLD_CLOCK_ALL, Sources( all ) );
      }
      else if( all.Count == 0 && some.Count == 0 )
      {
         w.Add( "basis.clock", T.THRESHOLD_CLOCK_NONE, Sources( none ) );
      }
      else if( all.Count > 0 && none.Count > 0 )
      {
         w.Add( "basis.clock", T.Fill( T.THRESHOLD_CLOCK_BOTH, S.List( all.Select( c => c.Session ).ToList() ), S.List( none.Select( c => c.Session ).ToList() ) ), Sources( all ).Concat( Sources( none ) ) );
      }
      else if( all.Count > 0 )
      {
         w.Add( "basis.clock", T.Fill( T.THRESHOLD_CLOCK_EVERY_OF, S.List( all.Select( c => c.Session ).ToList() ) ), Sources( all ) );
      }
      else
      {
         w.Add( "basis.clock", T.Fill( T.THRESHOLD_CLOCK_NONE_OF, S.List( none.Select( c => c.Session ).ToList() ) ), Sources( none ) );
      }

      foreach( SessionClockHeld c in some )
      {
         w.Add( $"basis.clock.{c.Session}", T.Fill( T.THRESHOLD_CLOCK_SOME, S.Whole( c.Held ), S.Whole( c.Runs ), c.Session ), Sources( new[] { c } ) );
      }
   }

   /// <summary>
   /// The passes behind the largest move that their run recorded as NOT HELD, and what the basis gives without their engines.
   /// Why: the threshold follows the largest move, so a page that does not say the move rests on a pass its own run says not to quote hides what sets the line.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="b">The basis.</param>
   private static void MaxMoveNotHeld( Writer w, BasisInfo b )
   {
      for( int i = 0; i < b.MaxMoveNotHeld.Count; i++ )
      {
         NotHeldCell c = b.MaxMoveNotHeld[i];
         string at = $"basis.maxMoveNotHeld[{i}]";
         w.Add( $"basis.max.notHeld.{i}", T.Fill( T.FLAG_NOT_HELD, c.Run, T.PassLabel( c.Pass ), c.Target, c.Timed, c.OffPercent, c.Settled, c.LimitPercent ),
            S.Consolidated( at + ".run", c.Run ), S.Consolidated( at + ".target", c.Target ), S.Consolidated( at + ".timed", c.Timed ), S.Consolidated( at + ".offPercent", c.OffPercent ),
            S.Consolidated( at + ".settled", c.Settled ), S.Consolidated( at + ".limitPercent", c.LimitPercent ) );
      }

      if( b.MaxMoveWithout is not BasisWithout without || without.MaxMove == null )
      {
         return;
      }

      BasisMove m = without.MaxMove;
      string what = T.Fill( m.Target != null ? T.MOVE_VALUE : T.MOVE_RATIO, T.Label( m.Metric ) );
      string who = m.Target ?? m.Pair!;
      var sources = new List<SentenceSource>
      {
         S.Consolidated( "basis.maxMoveWithout.maxBp", S.Percent( without.MaxBp ), "bp-pct" ), S.Consolidated( "basis.maxMoveWithout.tBp", S.Percent( without.TBp ), "bp-pct" ),
         S.Consolidated( m.Target != null ? "basis.maxMoveWithout.maxMove.target" : "basis.maxMoveWithout.maxMove.pair", who ),
      };
      sources.AddRange( without.Targets.Select( ( t, i ) => S.Consolidated( $"basis.maxMoveWithout.targets[{i}]", t ) ) );
      w.Add( "basis.max.without", T.Fill( T.THRESHOLD_MAX_WITHOUT, S.List( without.Targets ), S.Percent( without.MaxBp ), what, who, S.Percent( without.TBp ) ), sources );
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
