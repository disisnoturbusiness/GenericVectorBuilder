using System.Globalization;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Builds the v8 "big gaps only" report from its input: checks that the claim runs are one
/// experiment, computes the threshold basis, applies the claim rule per metric, runs guards G2 and
/// G3, the drift, the recall, the clock, the machine, image and settings blocks and the why table,
/// then has <see cref="ConsolidateText"/> write every sentence and <see cref="ConsolidateAudit"/>
/// check every sentence and fact against the raw files.
/// Refusals (<see cref="ConsolidateRefusal"/>), each naming the runs, targets and fields: a claim
/// session without exactly <see cref="RUNS_PER_SESSION"/> runs; more than <see cref="MAX_SESSIONS"/>
/// claim sessions; sessions out of time order; a folder in two sessions; a settings-only run; runs of
/// another pipeline; claim runs whose run-level or session-level identity differs; a listed target
/// missing, failed or without a value in a claim run; an exact pass in some runs of a target and not
/// in others; a basis run without machine control; an exclusion row that matches no cell; a fact or
/// a sentence that does not hold.
/// Why refuse instead of using "the largest group": the v7 code dropped mismatched runs and printed
/// "median of 6" over fewer; a reader could not tell. A mismatch now stops the report and says why.
/// </summary>
public static class Consolidator
{
   #region Data Members

   /// <summary>Runs every claim session must hold.</summary>
   public const int RUNS_PER_SESSION = 3;

   /// <summary>Most claim sessions.</summary>
   public const int MAX_SESSIONS = 2;

   /// <summary>Most targets whose setup may change between the sessions before G3 stops the report.</summary>
   public const int MAX_SETUP_CHANGES = 3;

   /// <summary>Status of a report no guard stopped.</summary>
   public const string COMPLETE = "complete";

   /// <summary>Status of a report a guard stopped.</summary>
   public const string STOPPED = "stopped";

   /// <summary>Repository path of the copied golden question file (P5 copies it byte for byte).</summary>
   public const string QUESTIONS_COPY = "design/bench-inputs/questions_golden.json";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Builds the whole report: figures, sentences and the audit.
   /// </summary>
   /// <param name="input">The loaded input.</param>
   /// <returns>The report, ready to write.</returns>
   /// <exception cref="ConsolidateRefusal">The input is not one experiment, a value is missing, or a fact or sentence does not hold; the message says which.</exception>
   public static ConsolidatedReport Build( ConsolidateInput input )
   {
      List<ClaimSession> sessions = CheckSessions( input );
      ConsolidatedReport report = Figures( input, sessions );
      using var raw = RawRuns.Load( sessions, input );
      IReadOnlyList<ResolvedFact> facts = ConsolidateAudit.ValidateFacts( input, raw.Claim, report.Targets );
      report.Audit = new AuditInfo { FactsSha256 = input.FactsSha256, ExclusionsSha256 = input.ExclusionsSha256, ObserverSha256 = report.Observer?.Sha256, ObserverSha256Others = report.ObserverOthers.ToDictionary( o => o.Session, o => o.Sha256, StringComparer.Ordinal ), ClassesSha256 = input.ClassesSha256, RunPageNotesSha256 = input.RunPageNotes.Sha256, FactsChecked = facts.Count };
      report.RunPageNotes = input.RunPageNotes.ToRecords( report.Sessions.SelectMany( s => s.Runs.Select( r => r.Folder ) ).Concat( report.Basis.Runs.Select( r => r.Folder ) ).ToHashSet( StringComparer.Ordinal ) );
      ConsolidateClasses.Validate( input, sessions, raw.Claim, report );
      report.Why = ConsolidateWhy.Build( sessions, report, facts, input );
      report.HttpClientCount = ConsolidateTextParts.HttpClientTargets( report ).Count;
      ConsolidateWhy.MarkSearchModes( report );
      List<Sentence> sentences = ConsolidateText.Write( report, sessions, input );
      ConsolidateSettings.WithholdMarked( report.SearchSettings, input.RunPageNotes );
      ConsolidateAudit.CheckSentences( report, sentences, raw, input.RepoRoot, input.RunPageNotes );
      return report;
   }

   /// <summary>
   /// The figures of the report, everything but the why table, the sentences and the audit.
   /// Why public: tests check the rule, the basis and the guards without the fact sheet.
   /// </summary>
   /// <param name="input">The loaded input.</param>
   /// <param name="sessions">The checked claim sessions, from <see cref="CheckSessions"/>.</param>
   /// <returns>The report without why rows and sentences.</returns>
   /// <exception cref="ConsolidateRefusal">A refusal.</exception>
   public static ConsolidatedReport Figures( ConsolidateInput input, List<ClaimSession> sessions )
   {
      List<RunResult> claimRuns = sessions.SelectMany( s => s.Runs ).ToList();
      var report = new ConsolidatedReport
      {
         CreatedUtc = input.CreatedUtc,
         CommandLine = input.CommandLine,
         Mode = sessions.Count == 2 ? "two-session" : "one-session",
         Sessions = sessions.Select( s => new SessionInfo { Name = s.Name, Runs = s.Runs.Select( r => Ref( r, input.ResultsRoot ) ).ToList() } ).ToList(),
      };
      CheckRunIdentity( claimRuns, report );
      List<string> targets = CheckTargets( input, sessions, report );
      List<string> changed = SetupChanges( sessions, targets, report );
      report.Basis = Basis( input, sessions );
      report.Threshold = new ThresholdInfo { TBp = report.Basis.TBp, Ratio = 1 + report.Basis.TBp / 10000.0, Rule = ClaimRule.RULE_TEXT };
      List<MetricOutcome> outcomes = Tables( sessions, targets, changed, report );
      report.Guards.G2 = ClaimDrift.G2( outcomes, sessions, report.Threshold.TBp );
      report.Drift = sessions.Count == 2 ? ClaimDrift.Drift( outcomes, sessions, report.Threshold.TBp ) : null;
      if( report.Drift != null )
      {
         report.Drift.OutsideLoad = ConsolidateSessions.OutsideLoad( sessions );
         report.Drift.SessionDifferences = ConsolidateSessions.Differences( sessions, report.Drift.OutsideLoad );
      }

      report.Recall = Recall( sessions, targets );
      report.RecallDerivedSessions = sessions.Where( s => s.Runs.All( r => targets.All( t => r.Find( t )!.RecallHits == null ) ) ).Select( s => s.Name ).ToList();
      report.Clock = Clock( sessions );
      report.Machine = Machine( claimRuns, report.Machine.Differences );
      report.Images = Images( sessions, targets );
      report.EngineSettings = Settings( sessions, targets );
      report.SearchSettings = ConsolidateSettings.Build( sessions, targets );
      ( report.Observer, report.ObserverOthers ) = ConsolidateObserver.Read( input.ObserverPaths, sessions );
      ConsolidateObserverPlacement.Fill( report.Observer, sessions[^1], input.RepoRoot );
      report.Queries = Queries( claimRuns, input.RepoRoot );
      report.Build = new BuildInfo { Measured = ConsolidateBuild.Measured( sessions ), ConsolidatedBy = input.Build };
      Counts( report, sessions );
      Status( report );
      return report;
   }

   /// <summary>
   /// Checks the claim and basis sessions: one or two claim sessions of exactly 3 runs, names unique,
   /// no folder twice, no settings-only run, one pipeline, the first session's runs all started before
   /// the second's, and every claim run's own runSeed and startedUtc recorded.
   /// </summary>
   /// <param name="input">Input.</param>
   /// <returns>The claim sessions, runs oldest first.</returns>
   /// <exception cref="ConsolidateRefusal">Any check fails.</exception>
   public static List<ClaimSession> CheckSessions( ConsolidateInput input )
   {
      if( input.Sessions.Count is 0 or > MAX_SESSIONS )
      {
         throw new ConsolidateRefusal( $"give one or two --session, not {input.Sessions.Count}" );
      }

      List<string> names = input.Sessions.Select( s => s.Name ).Concat( input.BasisSessions.Select( s => s.Name ) ).ToList();
      if( names.Distinct( StringComparer.Ordinal ).Count() != names.Count )
      {
         throw new ConsolidateRefusal( $"session names must be unique: {string.Join( ", ", names )}" );
      }

      foreach( (string name, List<RunResult> runs) in input.Sessions )
      {
         CheckClaimSession( name, runs );
      }

      CheckOverlaps( input );
      CheckPipeline( input );
      List<ClaimSession> sessions = input.Sessions.Select( s => new ClaimSession( s.Name, s.Runs.OrderBy( r => r.StartedUtc, StringComparer.Ordinal ).ThenBy( r => r.Name, StringComparer.Ordinal ).ToList() ) ).ToList();
      if( sessions.Count == 2 && string.CompareOrdinal( sessions[0].Runs[^1].StartedUtc, sessions[1].Runs[0].StartedUtc ) >= 0 )
      {
         throw new ConsolidateRefusal( $"session {sessions[0].Name} must be the earlier one: its last run {sessions[0].Runs[^1].Name} started at {sessions[0].Runs[^1].StartedUtc}, not before {sessions[1].Name}'s first run {sessions[1].Runs[0].Name} at {sessions[1].Runs[0].StartedUtc}" );
      }

      return sessions;
   }

   /// <summary>
   /// True when the first UTC time is before the second (both ISO text).
   /// </summary>
   /// <param name="a">First time, or null.</param>
   /// <param name="b">Second time, or null.</param>
   /// <returns>True, false, or null when either is missing or unreadable.</returns>
   public static bool? Before( string? a, string? b )
   {
      return DateTime.TryParse( a, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime x )
         && DateTime.TryParse( b, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime y )
         ? x < y : null;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// One claim session's own checks: exactly 3 runs, no folder twice, no settings-only run, a start time and a seed on each.
   /// </summary>
   /// <param name="name">Session name.</param>
   /// <param name="runs">Its runs.</param>
   /// <exception cref="ConsolidateRefusal">A check fails.</exception>
   private static void CheckClaimSession( string name, List<RunResult> runs )
   {
      if( runs.Count != RUNS_PER_SESSION )
      {
         throw new ConsolidateRefusal( $"session {name} has {runs.Count} run(s) ({string.Join( ", ", runs.Select( r => r.Name ) )}); a claim session needs exactly {RUNS_PER_SESSION}" );
      }

      if( runs.Select( r => r.Name ).Distinct( StringComparer.Ordinal ).Count() != runs.Count )
      {
         throw new ConsolidateRefusal( $"session {name} lists one folder twice: {string.Join( ", ", runs.Select( r => r.Name ) )}" );
      }

      foreach( RunResult run in runs )
      {
         if( run.SettingsOnly )
         {
            throw new ConsolidateRefusal( $"{run.Name} (session {name}) is a settings-only run (settingsOnly true): it searched nothing" );
         }

         if( run.StartedUtc == null || run.RunSeed == null )
         {
            throw new ConsolidateRefusal( $"{run.Name} (session {name}) has no {( run.StartedUtc == null ? "startedUtc" : "runSeed" )}" );
         }
      }
   }

   /// <summary>
   /// Refuses a folder that sits in two sessions of any kind.
   /// </summary>
   /// <param name="input">Input.</param>
   /// <exception cref="ConsolidateRefusal">An overlap.</exception>
   private static void CheckOverlaps( ConsolidateInput input )
   {
      var seen = new Dictionary<string, string>( StringComparer.Ordinal );
      foreach( (string name, List<RunResult> runs) in input.Sessions.Concat( input.BasisSessions ) )
      {
         foreach( RunResult run in runs )
         {
            if( seen.TryGetValue( run.Name, out string? other ) && other != name )
            {
               throw new ConsolidateRefusal( $"{run.Name} is in session {other} and in session {name}; a run belongs to one session (claim runs join the basis by themselves)" );
            }

            seen[run.Name] = name;
         }
      }
   }

   /// <summary>
   /// Refuses sessions of more than one pipeline.
   /// </summary>
   /// <param name="input">Input.</param>
   /// <exception cref="ConsolidateRefusal">Two pipelines.</exception>
   private static void CheckPipeline( ConsolidateInput input )
   {
      var pipelines = input.Sessions.Concat( input.BasisSessions ).SelectMany( s => s.Runs ).GroupBy( r => r.Pipeline ?? ConsolidateIdentity.NOT_RECORDED, StringComparer.Ordinal ).ToList();
      if( pipelines.Count > 1 )
      {
         throw new ConsolidateRefusal( "the sessions hold runs of more than one pipeline: " + string.Join( "; ", pipelines.Select( g => $"{g.Key} ({string.Join( ", ", g.Select( r => r.Name ) )})" ) ) );
      }
   }

   /// <summary>
   /// Refuses claim runs whose run-level or session-level identity differs; keeps the differences that do not refuse (os, .NET).
   /// </summary>
   /// <param name="claimRuns">Every claim run.</param>
   /// <param name="report">Receives the non-refusing differences.</param>
   /// <exception cref="ConsolidateRefusal">A difference that refuses.</exception>
   private static void CheckRunIdentity( List<RunResult> claimRuns, ConsolidatedReport report )
   {
      RunResult first = claimRuns[0];
      List<string> problems = claimRuns.Skip( 1 ).Where( r => ConsolidateIdentity.RunKey( r ) != ConsolidateIdentity.RunKey( first ) )
         .Select( r => $"{r.Name} against {first.Name}: {string.Join( "; ", ConsolidateIdentity.RunDifferences( r, first ) )}" ).ToList();
      IReadOnlyList<(string Field, bool Refuses, string Values)> session = ConsolidateIdentity.SessionDifferences( claimRuns );
      problems.AddRange( session.Where( d => d.Refuses ).Select( d => $"{d.Field} differs: {d.Values}" ) );
      if( problems.Count > 0 )
      {
         throw new ConsolidateRefusal( "the claim runs are not one experiment: " + string.Join( " | ", problems ) );
      }

      report.Machine.Differences = session.Where( d => !d.Refuses ).Select( d => $"{d.Field}: {d.Values}" ).ToList();
   }

   /// <summary>
   /// The targets with rows: the listed ones (or every target of the claim runs), each present, not
   /// failed and with p50, QPS@1 and QPS@8 in every claim run. A target whose setup differs inside a
   /// session is left out with the reason; every target the runs hold that has no row is listed.
   /// </summary>
   /// <param name="input">Input.</param>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="report">Receives the not-in-report list and the targets.</param>
   /// <returns>The targets with rows.</returns>
   /// <exception cref="ConsolidateRefusal">A listed target is missing, failed or lacks a value, or no target is left.</exception>
   private static List<string> CheckTargets( ConsolidateInput input, List<ClaimSession> sessions, ConsolidatedReport report )
   {
      List<RunResult> runs = sessions.SelectMany( s => s.Runs ).ToList();
      List<string> all = runs.SelectMany( r => r.Targets.Select( t => t.Name ) ).Distinct( StringComparer.Ordinal ).ToList();
      List<string> listed = input.Targets.Count > 0 ? input.Targets : all;
      if( listed.Distinct( StringComparer.Ordinal ).Count() != listed.Count )
      {
         throw new ConsolidateRefusal( $"--targets lists a target twice: {string.Join( ",", listed )}" );
      }

      List<string> problems = listed.SelectMany( t => runs.Select( r => TargetProblem( r, t ) ).OfType<string>() ).ToList();
      if( problems.Count > 0 )
      {
         throw new ConsolidateRefusal( "a listed target lacks a result: " + string.Join( "; ", problems ) + ". Leave it out with --targets to report the others." );
      }

      report.NotInReport.AddRange( all.Where( t => !listed.Contains( t ) ).Select( t => new NotInReport { Target = t, Code = "left out by --targets", Detail = "not in --targets" } ) );
      var kept = new List<string>();
      foreach( string target in listed )
      {
         (string Detail, List<string> Fields)? within = WithinSessionDifference( sessions, target );
         if( within == null )
         {
            kept.Add( target );
         }
         else
         {
            report.NotInReport.Add( new NotInReport { Target = target, Code = "setup differs within a session", Detail = within.Value.Detail, Fields = within.Value.Fields } );
         }
      }

      if( kept.Count == 0 )
      {
         throw new ConsolidateRefusal( "no listed target has one setup in every run of a session: " + string.Join( "; ", report.NotInReport.Select( n => $"{n.Target}: {n.Detail}" ) ) );
      }

      report.Targets = kept;
      return kept;
   }

   /// <summary>
   /// Why a run cannot serve a target, or null when it can.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="target">Target.</param>
   /// <returns>The problem, or null.</returns>
   private static string? TargetProblem( RunResult run, string target )
   {
      TargetResult? t = run.Find( target );
      if( t == null )
      {
         return $"{run.Name} has no result for {target}";
      }

      if( t.Error != null )
      {
         return $"{run.Name}: {target} failed ({t.Error.Replace( '\n', ' ' )})";
      }

      try
      {
         string[] missing = new[] { ClaimMetrics.P50, ClaimMetrics.QPS1, ClaimMetrics.QPS8 }.Where( m => ClaimMetrics.Value( t, m ) == null ).Select( ClaimMetrics.FieldOf ).ToArray();
         return missing.Length == 0 ? null : $"{run.Name}: {target} has no {string.Join( ", ", missing )}";
      }
      catch( InvalidDataException ex )
      {
         return $"{run.Name}: {ex.Message}";
      }
   }

   /// <summary>
   /// The differing fields when a target's setup is not the same in every run of one session.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="target">Target.</param>
   /// <returns>"session v7: field, field (run against run)" with the field names, or null when every session is uniform.</returns>
   private static (string Detail, List<string> Fields)? WithinSessionDifference( List<ClaimSession> sessions, string target )
   {
      foreach( ClaimSession session in sessions )
      {
         TargetResult first = session.Runs[0].Find( target )!;
         foreach( RunResult run in session.Runs.Skip( 1 ) )
         {
            IReadOnlyList<string> fields = ConsolidateIdentity.TargetDifferences( first, run.Find( target )! );
            if( fields.Count > 0 )
            {
               return ( $"session {session.Name}: {string.Join( ", ", fields )} ({run.Name} against {session.Runs[0].Name})", fields.ToList() );
            }
         }
      }

      return null;
   }

   /// <summary>
   /// Guard G3: targets whose setup differs between the two claim sessions (compared only where both
   /// recorded a field). Each becomes a one-session row; more than <see cref="MAX_SETUP_CHANGES"/> stops the report.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="targets">Targets with rows.</param>
   /// <param name="report">Receives G3.</param>
   /// <returns>The changed targets.</returns>
   private static List<string> SetupChanges( List<ClaimSession> sessions, List<string> targets, ConsolidatedReport report )
   {
      if( sessions.Count != 2 )
      {
         return new List<string>();
      }

      foreach( string target in targets )
      {
         List<string> fields = sessions[0].Runs.SelectMany( a => sessions[1].Runs.SelectMany( b => ConsolidateIdentity.TargetDifferences( a.Find( target )!, b.Find( target )! ) ) )
            .Distinct( StringComparer.Ordinal ).ToList();
         if( fields.Count > 0 )
         {
            report.Guards.G3.Targets.Add( new SetupChange { Target = target, Fields = fields } );
         }
      }

      report.Guards.G3.Stopped = report.Guards.G3.Targets.Count > MAX_SETUP_CHANGES;
      return report.Guards.G3.Targets.Select( t => t.Target ).ToList();
   }

   /// <summary>
   /// The threshold basis over the claim sessions and the basis sessions; every basis run must have
   /// machine control on. The other runs of the pipeline give the disclosed no-machine-control maxima.
   /// </summary>
   /// <param name="input">Input.</param>
   /// <param name="sessions">Claim sessions.</param>
   /// <returns>The basis.</returns>
   /// <exception cref="ConsolidateRefusal">A basis run without machine control, or the basis cannot be computed.</exception>
   private static BasisInfo Basis( ConsolidateInput input, List<ClaimSession> sessions )
   {
      var basisRuns = new List<BasisRun>();
      foreach( (string name, IReadOnlyList<RunResult> runs) in sessions.Select( s => ( s.Name, s.Runs ) ).Concat( input.BasisSessions.Select( b => ( b.Name, (IReadOnlyList<RunResult>)b.Runs ) ) ) )
      {
         foreach( RunResult run in runs )
         {
            if( run.Conditions.MachineControlState != "on" )
            {
               throw new ConsolidateRefusal( $"{run.Name} (session {name}) has machineControl {run.Conditions.MachineControl ?? ConsolidateIdentity.NOT_RECORDED}; the basis holds runs with machine control on" );
            }

            basisRuns.Add( new BasisRun( run, name ) );
         }
      }

      List<BasisRun> other = input.OtherRuns.Where( r => r.Conditions.MachineControlState != "on" ).Select( r => new BasisRun( r, "other" ) ).ToList();
      BasisInfo basis;
      try
      {
         basis = ThresholdBasis.Compute( basisRuns, other, input.Exclusions );
      }
      catch( InvalidDataException ex )
      {
         throw new ConsolidateRefusal( "the threshold basis cannot be computed: " + ex.Message );
      }

      basis.ClockHeld = basisRuns.GroupBy( b => b.Session, StringComparer.Ordinal ).Select( g => new SessionClockHeld { Session = g.Key, Runs = g.Count(), Held = g.Count( b => b.Run.Conditions.Clock.Pinned ) } ).OrderBy( c => c.Session, StringComparer.Ordinal ).ToList();
      basis.Runs = basisRuns.OrderBy( b => b.Folder, StringComparer.Ordinal ).Select( b => new BasisRunInfo
      {
         Folder = b.Folder,
         Seed = b.Seed,
         Session = b.Session,
         StartedUtc = b.Run.StartedUtc,
         ResultsSha256 = ResultsHash( b.Run ),
         Conditions = b.Conditions.ToInfo(),
         StaleLines = StaleLines( b.Run ),
      } ).ToList();
      basis.LeftOut = LeftOut( input );
      basis.NoMachineControlCount = other.Count;
      return basis;
   }

   /// <summary>
   /// Every other run of the pipeline with the reason it is not in the basis.
   /// </summary>
   /// <param name="input">Input.</param>
   /// <returns>The list, in folder order.</returns>
   private static List<LeftOutRun> LeftOut( ConsolidateInput input )
   {
      List<LeftOutRun> list = input.OtherRuns.Select( r => new LeftOutRun
      {
         Folder = r.Name,
         Reason = r.Conditions.MachineControlState == "on" ? "machineControl on, not in any session given" : $"machineControl {r.Conditions.MachineControl ?? ConsolidateIdentity.NOT_RECORDED}",
      } ).ToList();
      list.AddRange( input.Unreadable );
      return list.OrderBy( l => l.Folder, StringComparer.Ordinal ).ToList();
   }

   /// <summary>
   /// The four tables: each metric over the targets that hold it (exact: the targets with an exact pass in every claim run).
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="targets">Targets with rows.</param>
   /// <param name="changed">G3 targets.</param>
   /// <param name="report">Receives the tables.</param>
   /// <returns>The per-metric outcomes.</returns>
   /// <exception cref="ConsolidateRefusal">A target has a metric in some claim runs and not in others.</exception>
   private static List<MetricOutcome> Tables( List<ClaimSession> sessions, List<string> targets, List<string> changed, ConsolidatedReport report )
   {
      List<RunResult> runs = sessions.SelectMany( s => s.Runs ).ToList();
      var outcomes = new List<MetricOutcome>();
      foreach( string metric in ClaimMetrics.ALL )
      {
         List<string> holding = targets.Where( t => runs.All( r => ClaimMetrics.Value( r.Find( t )!, metric ) != null ) ).ToList();
         List<string> partial = targets.Where( t => !holding.Contains( t ) && runs.Any( r => ClaimMetrics.Value( r.Find( t )!, metric ) != null ) ).ToList();
         if( partial.Count > 0 )
         {
            throw new ConsolidateRefusal( $"{string.Join( ", ", partial )} has {ClaimMetrics.FieldOf( metric )} in some claim runs and not in others" );
         }

         List<NotHeldFigure> cells = NotHeldCells( sessions, targets, metric );
         List<string> notHeld = cells.Select( c => c.Target ).Distinct( StringComparer.Ordinal ).Where( t => holding.Contains( t ) && !changed.Contains( t ) ).ToList();
         MetricOutcome outcome = ClaimRule.Build( metric, sessions, holding.Where( t => !changed.Contains( t ) && !notHeld.Contains( t ) ).ToList(), holding.Where( changed.Contains ).ToList(), report.Basis.TBp, notHeld );
         NoteNotHeld( report, metric, cells.Where( c => notHeld.Contains( c.Target ) ).ToList() );
         foreach( MetricRow row in outcome.Table.Rows )
         {
            IReadOnlyList<RunResult> shown = row.Status == ClaimRule.ONE_SESSION && sessions.Count == 2 ? sessions[1].Runs : runs;
            row.Flags = ConsolidateFlags.For( row.Target, metric, shown ).Select( f => new RowFlag { Code = f.Code } ).ToList();
            if( row.Status == ClaimRule.ONE_SESSION && sessions.Count == 2 )
            {
               row.Flags.Insert( 0, new RowFlag { Code = ConsolidateFlags.ONE_SESSION } );
            }
         }

         if( metric == ClaimMetrics.EXACT )
         {
            outcome.Table.NoExactPass = targets.Where( t => !holding.Contains( t ) ).Select( t => new NoExactPass { Target = t } ).ToList();
         }

         report.Metrics.Add( outcome.Table );
         outcomes.Add( outcome );
      }

      return outcomes;
   }

   /// <summary>
   /// The cells the claim runs recorded as NOT HELD for a metric.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="targets">Targets with rows.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>The cells.</returns>
   /// <exception cref="ConsolidateRefusal">A NOT HELD pass has no metric to stand for.</exception>
   private static List<NotHeldFigure> NotHeldCells( List<ClaimSession> sessions, List<string> targets, string metric )
   {
      try
      {
         return ConsolidateNotHeld.Cells( sessions, targets, metric );
      }
      catch( InvalidDataException ex )
      {
         throw new ConsolidateRefusal( "NOT HELD pass: " + ex.Message );
      }
   }

   /// <summary>
   /// Records the cells of the rows a table leaves unranked, and the rows themselves.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="cells">The cells of the rows left unranked.</param>
   private static void NoteNotHeld( ConsolidatedReport report, string metric, List<NotHeldFigure> cells )
   {
      if( cells.Count == 0 )
      {
         return;
      }

      report.NotHeld ??= new NotHeldInfo();
      report.NotHeld.Cells.AddRange( cells );
      foreach( IGrouping<string, NotHeldFigure> target in cells.GroupBy( c => c.Target, StringComparer.Ordinal ) )
      {
         report.NotHeld.Unranked.Add( new NotHeldRow { Metric = metric, Target = target.Key, Pass = target.First().Pass, Runs = target.Select( c => c.Run ).Distinct( StringComparer.Ordinal ).Count() } );
      }
   }

   /// <summary>
   /// Recall per target as whole hits per run.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="targets">Targets.</param>
   /// <returns>The rows.</returns>
   /// <exception cref="ConsolidateRefusal">A recall that is not a whole number of hits, or runs that disagree on queryCount x top.</exception>
   private static List<RecallRow> Recall( List<ClaimSession> sessions, List<string> targets )
   {
      var rows = new List<RecallRow>();
      RunResult first = sessions[0].Runs[0];
      foreach( string target in targets )
      {
         var row = new RecallRow { Target = target, Of = ( first.QueryCount ?? 0 ) * ( first.Top ?? 0 ) };
         foreach( ClaimSession session in sessions )
         {
            row.Hits[session.Name] = session.Runs.Select( r => Hits( r, target ) ).ToList();
         }

         if( row.Hits.Values.SelectMany( h => h ).Any( h => h < 0 || h > row.Of ) )
         {
            throw new ConsolidateRefusal( $"{target} has a hit count outside 0 to {row.Of}: {string.Join( ", ", row.Hits.Values.SelectMany( h => h ) )}" );
         }

         row.Differs = row.Hits.Values.SelectMany( h => h ).Distinct().Count() > 1;
         rows.Add( row );
      }

      return rows;
   }

   /// <summary>
   /// A target's recall hits in a run: search.recallHits when recorded (it must then agree with
   /// recall x queryCount x top), else that product, which must be within 1e-6 of a whole number.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="target">Target.</param>
   /// <returns>The hits.</returns>
   /// <exception cref="ConsolidateRefusal">No recall, not a whole number of hits, or a recorded count that disagrees.</exception>
   private static int Hits( RunResult run, string target )
   {
      TargetResult t = run.Find( target )!;
      if( t.Recall is not double recall || run.QueryCount is not int queries || run.Top is not int top )
      {
         throw new ConsolidateRefusal( $"{run.Name}: {target} has no recall, queryCount or top, so no hit count" );
      }

      double raw = recall * queries * top;
      double rounded = Math.Round( raw );
      if( Math.Abs( raw - rounded ) > 1e-6 )
      {
         throw new ConsolidateRefusal( $"{run.Name}: {target} recall {recall.ToString( "R", CultureInfo.InvariantCulture )} x {queries} x {top} = {raw.ToString( "R", CultureInfo.InvariantCulture )} is not a whole number of hits" );
      }

      if( t.RecallHits is int hits && hits != (int)rounded )
      {
         throw new ConsolidateRefusal( $"{run.Name}: {target} recallHits {hits} disagrees with recall x queryCount x top = {(int)rounded}" );
      }

      return (int)rounded;
   }

   /// <summary>
   /// The clock per session and the dropped warnings.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <returns>The clock block.</returns>
   /// <exception cref="ConsolidateRefusal">A recorded clock disagrees with the recomputed one.</exception>
   private static ClockInfo Clock( List<ClaimSession> sessions )
   {
      try
      {
         var clock = new ClockInfo();
         foreach( ClaimSession session in sessions )
         {
            clock.PerSession[session.Name] = ConsolidateClock.ForSession( session.Runs );
         }

         clock.DroppedWarnings = ConsolidateClock.Dropped( sessions.SelectMany( s => s.Runs ) );
         return clock;
      }
      catch( InvalidDataException ex )
      {
         throw new ConsolidateRefusal( "clock check: " + ex.Message );
      }
   }

   /// <summary>
   /// The machine block: the refusing fields agree in every claim run by now, so they come from the newest run.
   /// </summary>
   /// <param name="claimRuns">Claim runs.</param>
   /// <param name="differences">The non-refusing differences found earlier.</param>
   /// <returns>The block.</returns>
   private static MachineInfo Machine( List<RunResult> claimRuns, List<string> differences )
   {
      RunResult last = claimRuns[^1];
      return new MachineInfo
      {
         Cpu = last.MachineCpu,
         LogicalCpus = last.MachineLogicalCpus,
         RamGiB = last.MachineRamGiB,
         Os = last.MachineOs,
         DotNet = last.MachineDotNet,
         Governor = last.Conditions.Governor,
         Partition = last.Conditions.Partition,
         Differences = differences,
      };
   }

   /// <summary>
   /// Image ids from the newest session that recorded them; they must agree over its runs and equal the recorded pin.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="targets">Targets with rows.</param>
   /// <returns>One entry per target with an image, in target order.</returns>
   /// <exception cref="ConsolidateRefusal">A target's image id differs between runs of the session, or from its pin.</exception>
   private static List<ImageInfo> Images( List<ClaimSession> sessions, List<string> targets )
   {
      ClaimSession newest = sessions[^1];
      string? firstStart = sessions[0].Runs[0].StartedUtc;
      var images = new List<ImageInfo>();
      foreach( string target in targets )
      {
         List<TargetResult> results = newest.Runs.Select( r => r.Find( target )! ).ToList();
         List<string> ids = results.Select( t => t.ImageId ).OfType<string>().Distinct( StringComparer.Ordinal ).ToList();
         if( ids.Count > 1 || ( ids.Count == 1 && results.Any( t => t.ImageId == null ) ) )
         {
            throw new ConsolidateRefusal( $"{target} recorded image ids {string.Join( ", ", results.Select( t => t.ImageId ?? ConsolidateIdentity.NOT_RECORDED ) )} in the runs of session {newest.Name}" );
         }

         if( results.FirstOrDefault( t => t.ImagePinnedId != null && t.ImagePinnedId != t.ImageId ) is TargetResult unpinned )
         {
            throw new ConsolidateRefusal( $"{target} ran image {unpinned.ImageId} against its pin {unpinned.ImagePinnedId} in session {newest.Name}" );
         }

         if( ids.Count == 1 )
         {
            string? tagged = results[0].ImageLastTagTimeUtc;
            images.Add( new ImageInfo { Target = target, Ref = results[0].ImageRef, Id = ids[0], LastTagTimeUtc = tagged, BeforeFirstV7Start = Before( tagged, firstStart ) } );
         }
      }

      return images;
   }

   /// <summary>
   /// Engine settings per target from the newest session's first run.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="targets">Targets.</param>
   /// <returns>The rows (targets that recorded none are left out).</returns>
   private static List<EngineSettingsRow> Settings( List<ClaimSession> sessions, List<string> targets )
   {
      ClaimSession newest = sessions[^1];
      return targets.Select( t => ( Target: t, List: newest.Runs[0].Find( t )!.EngineSettingsList ) ).Where( x => x.List.Count > 0 )
         .Select( x => new EngineSettingsRow { Target = x.Target, Session = newest.Name, Settings = x.List.Select( s => new EngineSettingEntry { Key = ConsolidateSettings.KeyShown( x.Target, s.Key ), Value = s.Value, How = s.How } ).ToList() } ).ToList();
   }

   /// <summary>
   /// The queries block: the first claim run's queries line, and the copied question file checked
   /// against every claim run that recorded queriesFileSha256.
   /// Why refuse on a mismatch: the page names the copied file as the list the runs read; a run that
   /// read another file would make that sentence false.
   /// </summary>
   /// <param name="claimRuns">Claim runs.</param>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>The block.</returns>
   /// <exception cref="ConsolidateRefusal">A run recorded a SHA-256 that differs from the copy's, or the copy is missing while a run recorded one.</exception>
   private static QueriesInfo Queries( List<RunResult> claimRuns, string repoRoot )
   {
      var info = new QueriesInfo { Recorded = claimRuns[0].Queries, CopyPath = QUESTIONS_COPY };
      string copy = Path.Combine( repoRoot, QUESTIONS_COPY );
      info.CopySha256 = File.Exists( copy ) ? EngineFactSheet.FileSha256( copy ) : null;
      foreach( RunResult run in claimRuns )
      {
         if( run.QueriesFileSha256 == null )
         {
            info.UnrecordedRuns.Add( run.Name );
         }
         else if( string.Equals( run.QueriesFileSha256, info.CopySha256, StringComparison.OrdinalIgnoreCase ) )
         {
            info.MatchingRuns.Add( run.Name );
         }
         else
         {
            throw new ConsolidateRefusal( $"{run.Name} recorded queriesFileSha256 {run.QueriesFileSha256}, but {QUESTIONS_COPY} in {repoRoot} has {info.CopySha256 ?? "no file"}" );
         }
      }

      return info;
   }

   /// <summary>
   /// Fills the counts that sentences cite, so every number a sentence prints is a field of the report.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <param name="sessions">Claim sessions.</param>
   private static void Counts( ConsolidatedReport report, List<ClaimSession> sessions )
   {
      report.RunsPerSession = RUNS_PER_SESSION;
      report.RunsShown = RUNS_PER_SESSION * sessions.Count;
      report.ClaimRunCount = sessions.Sum( s => s.Runs.Count );
      report.TargetCount = report.Targets.Count;
      report.ImageTargets = report.Images.Count;
      report.Basis.RunCount = report.Basis.Runs.Count;
      report.Basis.LeftOutCount = report.Basis.LeftOut.Count;
      report.Basis.SetupSplitCount = report.Basis.SetupSplits.Count;
      report.Guards.G3.ChangedCount = report.Guards.G3.Targets.Count;
      report.Guards.G3.Limit = MAX_SETUP_CHANGES;
      if( report.Drift != null )
      {
         report.Drift.LargestAbsMoveBp = Math.Abs( report.Drift.Largest?.MoveBp ?? 0 );
         report.Drift.CloseFromBp = report.Threshold.TBp - ClaimRule.CLOSE_BELOW_BP;
         report.Drift.CloseCount = report.Drift.CloseToLine.Count;
         report.Drift.OnLineBp = ClaimRule.ON_LINE_BP;
         report.Drift.OnLineCount = report.Drift.OnLine.Count;
      }
   }

   /// <summary>
   /// Sets the status and the stop reasons from the guards.
   /// </summary>
   /// <param name="report">The report.</param>
   private static void Status( ConsolidatedReport report )
   {
      if( report.Guards.G2.Stopped )
      {
         report.StopReasons.Add( "G2: " + string.Join( "; ", report.Guards.G2.Pairs.Select( p => $"{p.Metric} {p.A} ahead of {p.B} in {p.OrderedIn}, medians reversed in {p.ReversedIn}" ) ) );
      }

      if( report.Guards.G3.Stopped )
      {
         report.StopReasons.Add( $"G3: {report.Guards.G3.Targets.Count} targets changed setup between the sessions, more than {MAX_SETUP_CHANGES}: " + string.Join( "; ", report.Guards.G3.Targets.Select( t => $"{t.Target} ({string.Join( ", ", t.Fields )})" ) ) );
      }

      report.Status = report.StopReasons.Count > 0 ? STOPPED : COMPLETE;
   }

   /// <summary>
   /// A run's reference for the report.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="root">The results folder.</param>
   /// <returns>The reference.</returns>
   private static RunRef Ref( RunResult run, string root )
   {
      string relative = Path.GetRelativePath( root, run.Folder );
      if( relative.StartsWith( "..", StringComparison.Ordinal ) || Path.IsPathRooted( relative ) || relative.Contains( '.' ) )
      {
         throw new ConsolidateRefusal( $"claim run {run.Folder} must lie inside the results folder {root} (give --results-dir), and its folder name may not hold a dot" );
      }

      return new RunRef { Folder = Path.GetRelativePath( root, run.Folder ), Seed = run.RunSeed, StartedUtc = run.StartedUtc, ResultsSha256 = ResultsHash( run ), StaleLines = StaleLines( run ) };
   }

   /// <summary>
   /// The SHA-256 of a run's results.json.
   /// Why recorded: the report is made from these files, and a reader can then tell which bytes of each run it read.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>Lower-case hex, or null when the run was not read from a file.</returns>
   private static string? ResultsHash( RunResult run )
   {
      string path = Path.Combine( run.Folder, "results.json" );
      return File.Exists( path ) ? EngineFactSheet.FileSha256( path ) : null;
   }

   /// <summary>
   /// Lines of a run's results.md that hold a retired framing sentence (hole H4).
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>The lines; empty when there is no results.md or no such line.</returns>
   private static List<StaleLine> StaleLines( RunResult run )
   {
      string path = Path.Combine( run.Folder, "results.md" );
      if( !File.Exists( path ) )
      {
         return new List<StaleLine>();
      }

      string[] lines = File.ReadAllLines( path );
      var found = new List<StaleLine>();
      for( int i = 0; i < lines.Length; i++ )
      {
         found.AddRange( ConsolidateFraming.RETIRED.Where( r => lines[i].Contains( r, StringComparison.Ordinal ) ).Select( r => new StaleLine { Line = i + 1, Retired = r } ) );
      }

      return found;
   }

   #endregion Private Methods
}
