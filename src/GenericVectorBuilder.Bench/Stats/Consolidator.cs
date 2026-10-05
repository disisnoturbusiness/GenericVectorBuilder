using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Folds several benchmark runs into one <see cref="ConsolidatedReport"/>.
/// Rules, each from a hole found in the first published draft:
/// only runs where every listed target has a result are used, and every other run is listed
/// with its reason; runs whose settings differ (queries, machine, levels) are not mixed;
/// comparisons between two targets, and between a target's exact and default search, are
/// computed inside each run and only then summarized, never as a ratio of separate medians;
/// ranks are taken per run and their median reported, not the rank of the medians.
/// Rules added after the second review: runs measured under different build configurations,
/// CPU governors, CPU partitions, warm-up counts, exact-mode budgets or per-engine search
/// settings are never mixed (a number measured at 2.1 GHz and one at 3.5 GHz are not repeats of
/// one experiment); load rows/s is reported but never ranked; there is no default pair, because
/// the two targets of an obvious pair hold separate copies of the data; and the flags (spread,
/// p50 against mean, unsettled engine, busy box, governor, shared cores) are computed here once
/// so the markdown and the web page show the same warnings.
/// Rules added after the third review: targets whose min-max ranges overlap on a speed metric
/// share a tie band and no strict rank is shown; runs of different commands (run-all against
/// bench), different engine hosting (container against native) or different segment layouts are
/// refused, not merged; and each target carries notes a reader needs next to its speed (a CPU
/// cap, an exact-by-design search, a scan where an index was meant).
/// </summary>
public static class Consolidator
{
   #region Data Members

   /// <summary>
   /// Pairs compared run by run when --pairs is not given: none. Why none: the obvious pairs
   /// (sql-diskann against sql) write to different databases, so a difference between them mixes
   /// the search method with a different copy of the data; an exact scan against the default
   /// search inside one target is compared in the exact-versus-default tables instead.
   /// </summary>
   public static readonly IReadOnlyList<(string A, string B)> DEFAULT_PAIRS = Array.Empty<(string A, string B)>();

   private const int MAX_ERROR_TEXT = 120;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Consolidates the runs.
   /// </summary>
   /// <param name="runs">Runs that loaded, in any order.</param>
   /// <param name="targets">Targets every used run must have, in report order.</param>
   /// <param name="pairs">Target pairs to compare run by run (pairs naming an unlisted target are skipped).</param>
   /// <param name="alreadyDropped">Folders that could not be read, carried into the report's dropped list.</param>
   /// <returns>The consolidated report.</returns>
   /// <exception cref="ArgumentException">No targets, or a target listed twice.</exception>
   /// <exception cref="InvalidOperationException">No run qualifies; the message lists every run's reason.</exception>
   public static ConsolidatedReport Consolidate( IReadOnlyList<RunResult> runs, IReadOnlyList<string> targets, IReadOnlyList<(string A, string B)> pairs, IReadOnlyList<RunDropped> alreadyDropped )
   {
      if( targets.Count == 0 || targets.Distinct( StringComparer.Ordinal ).Count() != targets.Count )
      {
         throw new ArgumentException( "List each target once, e.g. --targets sql,sql-diskann,qdrant." );
      }

      var report = new ConsolidatedReport { Targets = targets.ToList() };
      report.Dropped.AddRange( alreadyDropped );
      List<RunResult> used = SelectRuns( runs, targets, report.Dropped );
      if( used.Count == 0 )
      {
         throw new InvalidOperationException( "No run has a result for every listed target. "
            + string.Join( " ", report.Dropped.Select( d => $"[{d.Name}: {d.Reason}]" ) ) );
      }

      string? refusal = ConsolidateIdentity.Refusal( used, targets );
      if( refusal != null )
      {
         throw new InvalidOperationException( refusal );
      }

      report.Settings = SettingsOf( used[0], targets );
      report.Runs = used.Select( ToRunUsed ).ToList();
      IReadOnlyList<int> levels = Levels( used, targets );
      report.TargetSummaries = targets.Select( t => Summarize( t, used, levels ) ).ToList();
      AddRanks( report.TargetSummaries, used, levels );
      report.Bands = ConsolidateBands.Build( report.TargetSummaries, levels, used.Count );
      report.TargetSummaries.ForEach( t => t.EngineNotes = ConsolidateEngineNotes.For( t, used ) );
      report.Pairs = pairs.Where( p => targets.Contains( p.A ) && targets.Contains( p.B ) && p.A != p.B )
         .Select( p => Pair( p.A, p.B, used, levels ) ).ToList();
      report.ExactVsDefault = targets.Select( t => Exact( t, used ) ).OfType<ExactVsDefault>().ToList();
      report.Flags = ConsolidateFlags.Build( report.TargetSummaries, used );
      report.Notes = Notes( report );
      return report;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Picks the runs to use: every listed target present, not failed and searched; then the
   /// largest group of runs with identical settings (ties go to the group with the newest run).
   /// Every other run is added to <paramref name="dropped"/> with its reason.
   /// </summary>
   /// <param name="runs">Candidate runs.</param>
   /// <param name="targets">Required targets.</param>
   /// <param name="dropped">Receives the runs left out.</param>
   /// <returns>Runs used, oldest first.</returns>
   private static List<RunResult> SelectRuns( IReadOnlyList<RunResult> runs, IReadOnlyList<string> targets, List<RunDropped> dropped )
   {
      var complete = new List<RunResult>();
      var seen = new HashSet<string>( StringComparer.Ordinal );
      foreach( RunResult run in runs.OrderBy( r => r.StartedUtc ?? string.Empty, StringComparer.Ordinal ).ThenBy( r => r.Name, StringComparer.Ordinal ) )
      {
         string? problem = !seen.Add( run.Folder ) ? "listed twice; used once" : TargetProblems( run, targets );
         if( problem != null )
         {
            dropped.Add( new RunDropped { Name = run.Name, Folder = run.Folder, Reason = problem } );
         }
         else
         {
            complete.Add( run );
         }
      }

      if( complete.Count == 0 )
      {
         return complete;
      }

      IGrouping<string, RunResult> chosen = complete.GroupBy( r => Signature( r, targets ) )
         .OrderByDescending( g => g.Count() )
         .ThenByDescending( g => g.Select( r => r.StartedUtc ?? string.Empty ).Max( StringComparer.Ordinal ), StringComparer.Ordinal )
         .First();
      foreach( RunResult run in complete.Where( r => Signature( r, targets ) != chosen.Key ) )
      {
         dropped.Add( new RunDropped { Name = run.Name, Folder = run.Folder, Reason = "settings differ from the runs used: " + SettingsDifference( run, chosen.First(), targets ) } );
      }

      return chosen.ToList();
   }

   /// <summary>
   /// Why a run cannot be used for these targets, or null when it can.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="targets">Required targets.</param>
   /// <returns>The reason, or null.</returns>
   private static string? TargetProblems( RunResult run, IReadOnlyList<string> targets )
   {
      var parts = new List<string>();
      string[] missing = targets.Where( t => run.Find( t ) == null ).ToArray();
      if( missing.Length > 0 )
      {
         parts.Add( $"no result for {string.Join( ", ", missing )}" );
      }

      TargetResult[] present = targets.Select( run.Find ).OfType<TargetResult>().ToArray();
      foreach( TargetResult failed in present.Where( t => t.Error != null ) )
      {
         string error = failed.Error!.Length > MAX_ERROR_TEXT ? failed.Error[..MAX_ERROR_TEXT] + "..." : failed.Error;
         parts.Add( $"{failed.Name} failed ({error.Replace( '\n', ' ' )})" );
      }

      string[] unsearched = present.Where( t => t.Error == null && ( !t.Searched || t.P50Ms == null ) ).Select( t => t.Name ).ToArray();
      if( unsearched.Length > 0 )
      {
         parts.Add( $"no search result for {string.Join( ", ", unsearched )}" );
      }

      return parts.Count == 0 ? null : string.Join( "; ", parts );
   }

   /// <summary>
   /// The settings that must match between runs, as one comparable string.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="targets">The listed targets (their search settings are part of the signature).</param>
   /// <returns>The signature.</returns>
   private static string Signature( RunResult run, IReadOnlyList<string> targets )
   {
      return string.Join( "|", SettingFields( run, targets ).Select( f => f.Value ) );
   }

   /// <summary>
   /// Names and values of the settings that must match.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="targets">The listed targets.</param>
   /// <returns>(name, value) pairs; "missing" for a field the run did not write.</returns>
   private static IEnumerable<(string Name, string Value)> SettingFields( RunResult run, IReadOnlyList<string> targets )
   {
      yield return ( "pipeline", run.Pipeline ?? "missing" );
      yield return ( "host", run.Host ?? "missing" );
      yield return ( "rows", run.Rows?.ToString() ?? "missing" );
      yield return ( "dimension", run.Dimension?.ToString() ?? "missing" );
      yield return ( "queryKind", run.QueryKind ?? "missing" );
      yield return ( "queryCount", run.QueryCount?.ToString() ?? "missing" );
      yield return ( "top", run.Top?.ToString() ?? "missing" );
      yield return ( "concurrency", string.Join( ",", run.Concurrency ) );
      yield return ( "secondsPerLevel", run.SecondsPerLevel?.ToString() ?? "missing" );
      RunConditions c = run.Conditions;
      yield return ( "buildConfiguration", c.BuildConfiguration ?? "missing" );
      yield return ( "machineControl", c.MachineControlState ?? "missing" );
      yield return ( "governor", c.Governor ?? "missing" );
      yield return ( "cpuPartition", c.Partition ?? "missing" );
      yield return ( "warmupSearches", c.WarmupSearches?.ToString() ?? "missing" );
      yield return ( "exactSeconds", c.ExactSeconds?.ToString() ?? "missing" );
      foreach( string target in targets )
      {
         yield return ( $"searchSettings[{target}]", run.Find( target )?.SearchSettings ?? "missing" );
         yield return ( $"index[{target}]", run.Find( target )?.Index ?? "missing" );
      }
   }

   /// <summary>
   /// The settings that differ between a run and the runs used, e.g. "queryCount 200 vs 20".
   /// </summary>
   /// <param name="run">The dropped run.</param>
   /// <param name="reference">A run that was used.</param>
   /// <param name="targets">The listed targets.</param>
   /// <returns>The differences.</returns>
   private static string SettingsDifference( RunResult run, RunResult reference, IReadOnlyList<string> targets )
   {
      return string.Join( ", ", SettingFields( run, targets ).Zip( SettingFields( reference, targets ) )
         .Where( p => p.First.Value != p.Second.Value )
         .Select( p => $"{p.First.Name} {p.First.Value} vs {p.Second.Value}" ) );
   }

   /// <summary>
   /// The shared settings, from one used run (all used runs match).
   /// </summary>
   /// <param name="run">A used run.</param>
   /// <param name="targets">The listed targets.</param>
   /// <returns>The settings.</returns>
   private static RunSettings SettingsOf( RunResult run, IReadOnlyList<string> targets )
   {
      RunConditions c = run.Conditions;
      return new RunSettings
      {
         Pipeline = run.Pipeline,
         Command = run.Command,
         Host = run.Host,
         Rows = run.Rows,
         Dimension = run.Dimension,
         QueryKind = run.QueryKind,
         QueryCount = run.QueryCount,
         Top = run.Top,
         Concurrency = run.Concurrency.ToList(),
         SecondsPerLevel = run.SecondsPerLevel,
         BuildConfiguration = c.BuildConfiguration,
         MachineControl = c.MachineControl,
         Governor = c.Governor,
         CpuPartition = c.Partition,
         WarmupSearches = c.WarmupSearches,
         ExactSeconds = c.ExactSeconds,
         SearchSettings = targets.ToDictionary( t => t, t => run.Find( t )?.SearchSettings, StringComparer.Ordinal ),
         Derived = c.Derived.ToDictionary( d => d.Key, d => d.Value, StringComparer.Ordinal ),
      };
   }

   /// <summary>
   /// The run-level facts carried into the report.
   /// </summary>
   /// <param name="run">A used run.</param>
   /// <returns>The facts.</returns>
   private static RunUsed ToRunUsed( RunResult run )
   {
      return new RunUsed
      {
         Name = run.Name,
         Folder = run.Folder,
         StartedUtc = run.StartedUtc,
         RunSeed = run.RunSeed,
         TargetOrder = run.TargetOrder?.ToList(),
         LoadAverage = run.LoadAverage,
      };
   }

   /// <summary>
   /// Concurrency levels, lowest first: the runs' setting, or every level any target measured
   /// when the setting is absent.
   /// </summary>
   /// <param name="used">Runs used.</param>
   /// <param name="targets">Targets.</param>
   /// <returns>The levels.</returns>
   private static IReadOnlyList<int> Levels( IReadOnlyList<RunResult> used, IReadOnlyList<string> targets )
   {
      IEnumerable<int> levels = used[0].Concurrency.Count > 0
         ? used[0].Concurrency
         : used.SelectMany( r => targets.Select( r.Find ).OfType<TargetResult>() ).SelectMany( t => t.Qps.Keys );
      return levels.Distinct().OrderBy( l => l ).ToList();
   }

   /// <summary>
   /// Summarizes one target over the runs used.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="used">Runs used (each has the target).</param>
   /// <param name="levels">Concurrency levels, lowest first.</param>
   /// <returns>The summary (ranks are added afterwards).</returns>
   private static TargetSummary Summarize( string name, IReadOnlyList<RunResult> used, IReadOnlyList<int> levels )
   {
      List<TargetResult> results = used.Select( r => r.Find( name )! ).ToList();
      var summary = new TargetSummary
      {
         Name = name,
         Engines = Distinct( results.Select( t => t.Engine ) ),
         Indexes = Distinct( results.Select( t => t.Index ) ),
         Hosting = Distinct( results.Select( t => t.Hosting ) ),
         P50Ms = Spread.Of( results.Select( t => t.P50Ms ) ),
         P95Ms = Spread.Of( results.Select( t => t.P95Ms ) ),
         LoadRowsPerSecond = Spread.Of( results.Select( t => t.LoadRowsPerSecond ) ),
         Recall = Spread.Of( results.Select( t => t.Recall ) ),
         Ndcg = Spread.Of( results.Select( t => t.Ndcg ) ),
         ExactP50Ms = Spread.Of( results.Select( t => t.ExactP50Ms ) ),
         Errors = SumOrNull( results.Select( t => t.Errors ) ),
         WarmupErrors = SumOrNull( results.Select( t => t.WarmupErrors ) ),
         PerRun = used.Zip( results ).Select( p => ToFacts( p.First, p.Second, levels ) ).ToList(),
      };

      foreach( int level in levels )
      {
         summary.Qps[Key( level )] = Spread.Of( results.Select( t => Qps( t, level ) ) );
         if( level != levels[0] )
         {
            summary.QpsRatio[$"{level}/{levels[0]}"] = Spread.Of( results.Select( t => ConsolidateMath.Ratio( Qps( t, level ), Qps( t, levels[0] ) ) ) );
         }
      }

      return summary;
   }

   /// <summary>
   /// The carry-through facts of one target in one run.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="target">The target's result in that run.</param>
   /// <param name="levels">Concurrency levels, lowest first.</param>
   /// <returns>The facts.</returns>
   private static TargetRunFacts ToFacts( RunResult run, TargetResult target, IReadOnlyList<int> levels )
   {
      ( double? mean, string? meanSource ) = MeanLatency( target, levels );
      return new TargetRunFacts
      {
         Run = run.Name,
         OrderInRun = OrderOf( run, target.Name ),
         PassOrder = target.PassOrder?.ToList(),
         WarmupErrors = target.WarmupErrors,
         Errors = target.Errors,
         ExactRecall = target.ExactRecall,
         AfterLoad = ToFacts( target.AfterLoad ),
         AfterSearch = ToFacts( target.AfterSearch ),
         SegmentLayoutAfterLoad = target.AfterLoad?.Layout,
         SegmentLayoutAfterSearch = target.AfterSearch?.Layout,
         Durability = target.Durability,
         LoadIndexNote = target.LoadIndexNote,
         SearchSettings = target.SearchSettings,
         Settled = target.Settled,
         SettleDetail = target.SettleDetail,
         MeanMs = mean,
         MeanSource = meanSource,
      };
   }

   /// <summary>
   /// The mean latency of one search at a time: the recorded mean, else 1000 divided by the QPS
   /// measured with one searcher (one searcher going back to back finishes one search per mean
   /// latency).
   /// </summary>
   /// <param name="target">The target's result.</param>
   /// <param name="levels">Concurrency levels, lowest first.</param>
   /// <returns>The mean in ms and where it came from, or nulls when neither exists.</returns>
   private static (double? Mean, string? Source) MeanLatency( TargetResult target, IReadOnlyList<int> levels )
   {
      if( ConsolidateMath.IsNumber( target.MeanMs ) )
      {
         return ( target.MeanMs, "recorded" );
      }

      double? one = levels.Count > 0 && levels[0] == 1 ? Qps( target, 1 ) : null;
      return ConsolidateMath.IsNumber( one ) && one > 0 ? ( 1000.0 / one, "1000 / QPS@1" ) : ( null, null );
   }

   /// <summary>
   /// Copies an index state into the report's shape.
   /// </summary>
   /// <param name="state">The state, or null.</param>
   /// <returns>The copy, or null.</returns>
   private static IndexStateFacts? ToFacts( IndexStateValue? state )
   {
      return state == null ? null : new IndexStateFacts { Ready = state.Ready, IndexedVectors = state.IndexedVectors, TotalVectors = state.TotalVectors, Detail = state.Detail };
   }

   /// <summary>
   /// Ranks every target inside each run, for each metric, then takes the median rank.
   /// Load rows/s is deliberately not ranked: the loads are a few hundred rows, so they time
   /// connection and first-call costs more than the engine.
   /// </summary>
   /// <param name="summaries">Target summaries, in target order.</param>
   /// <param name="used">Runs used.</param>
   /// <param name="levels">Concurrency levels.</param>
   private static void AddRanks( IReadOnlyList<TargetSummary> summaries, IReadOnlyList<RunResult> used, IReadOnlyList<int> levels )
   {
      var metrics = new List<(string Name, Func<TargetResult, double?> Value, bool HigherIsBetter)>
      {
         ( "p50Ms", t => t.P50Ms, false ),
         ( "p95Ms", t => t.P95Ms, false ),
      };
      metrics.AddRange( levels.Select( l => ( $"qps@{l}", (Func<TargetResult, double?>)( t => Qps( t, l ) ), true ) ) );
      metrics.Add( ( "recall", t => t.Recall, true ) );
      metrics.Add( ( "ndcg", t => t.Ndcg, true ) );
      foreach( (string name, Func<TargetResult, double?> value, bool higher) in metrics )
      {
         var perTarget = summaries.Select( _ => new RankSpread() ).ToList();
         foreach( RunResult run in used )
         {
            int?[] ranks = ConsolidateMath.CompetitionRanks( summaries.Select( s => value( run.Find( s.Name )! ) ).ToList(), higher );
            for( int i = 0; i < summaries.Count; i++ )
            {
               perTarget[i].PerRun.Add( ranks[i] );
            }
         }

         for( int i = 0; i < summaries.Count; i++ )
         {
            perTarget[i].Median = ConsolidateMath.Median( perTarget[i].PerRun.Select( r => (double?)r ) );
            summaries[i].Ranks[name] = perTarget[i];
         }
      }
   }

   /// <summary>
   /// Compares target A with target B inside each run, then summarizes the per-run values.
   /// </summary>
   /// <param name="a">Target A (numerator).</param>
   /// <param name="b">Target B (denominator).</param>
   /// <param name="used">Runs used (each has both).</param>
   /// <param name="levels">Concurrency levels.</param>
   /// <returns>The pair.</returns>
   private static PairSummary Pair( string a, string b, IReadOnlyList<RunResult> used, IReadOnlyList<int> levels )
   {
      var pair = new PairSummary { A = a, B = b };
      foreach( RunResult run in used )
      {
         TargetResult ta = run.Find( a )!;
         TargetResult tb = run.Find( b )!;
         var row = new PairRun
         {
            Run = run.Name,
            AP50Ms = ta.P50Ms,
            BP50Ms = tb.P50Ms,
            P50Ratio = ConsolidateMath.Ratio( ta.P50Ms, tb.P50Ms ),
            P95Ratio = ConsolidateMath.Ratio( ta.P95Ms, tb.P95Ms ),
            RecallDifference = ConsolidateMath.Difference( ta.Recall, tb.Recall ),
            NdcgDifference = ConsolidateMath.Difference( ta.Ndcg, tb.Ndcg ),
            AOrder = OrderOf( run, a ),
            BOrder = OrderOf( run, b ),
         };
         foreach( int level in levels )
         {
            row.AQps[Key( level )] = Qps( ta, level );
            row.BQps[Key( level )] = Qps( tb, level );
            row.QpsRatio[Key( level )] = ConsolidateMath.Ratio( Qps( ta, level ), Qps( tb, level ) );
         }

         pair.Runs.Add( row );
      }

      SummarizePair( pair, levels );
      return pair;
   }

   /// <summary>
   /// Fills a pair's summaries from its per-run rows.
   /// </summary>
   /// <param name="pair">The pair, rows filled.</param>
   /// <param name="levels">Concurrency levels.</param>
   private static void SummarizePair( PairSummary pair, IReadOnlyList<int> levels )
   {
      pair.P50Ratio = Spread.Of( pair.Runs.Select( r => r.P50Ratio ) );
      pair.P95Ratio = Spread.Of( pair.Runs.Select( r => r.P95Ratio ) );
      pair.RecallDifference = Spread.Of( pair.Runs.Select( r => r.RecallDifference ) );
      pair.NdcgDifference = Spread.Of( pair.Runs.Select( r => r.NdcgDifference ) );
      pair.RunsALowerP50 = pair.Runs.Count( r => r.AP50Ms < r.BP50Ms );
      foreach( string key in levels.Select( Key ) )
      {
         pair.QpsRatio[key] = Spread.Of( pair.Runs.Select( r => r.QpsRatio[key] ) );
         pair.RunsAHigherQps[key] = pair.Runs.Count( r => r.AQps[key] > r.BQps[key] );
      }

      bool ordered = pair.Runs.Any( r => r.AOrder.HasValue && r.BOrder.HasValue );
      pair.RunsARanFirst = ordered ? pair.Runs.Count( r => r.AOrder < r.BOrder ) : null;
   }

   /// <summary>
   /// A target's exact mode against its default search in each run that timed the exact mode.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="used">Runs used.</param>
   /// <returns>The comparison, or null when no run timed an exact mode.</returns>
   private static ExactVsDefault? Exact( string name, IReadOnlyList<RunResult> used )
   {
      var rows = new List<ExactRun>();
      foreach( RunResult run in used )
      {
         TargetResult t = run.Find( name )!;
         if( t.ExactP50Ms == null )
         {
            continue;
         }

         rows.Add( new ExactRun
         {
            Run = run.Name,
            DefaultP50Ms = t.P50Ms,
            ExactP50Ms = t.ExactP50Ms,
            DifferenceMs = ConsolidateMath.Difference( t.ExactP50Ms, t.P50Ms ),
            Ratio = ConsolidateMath.Ratio( t.ExactP50Ms, t.P50Ms ),
            ExactRecall = t.ExactRecall,
            ExactRanFirst = ExactRanFirst( t.PassOrder ),
         } );
      }

      PairHintValue? hint = used.Select( r => r.Find( name )!.PairHint ).FirstOrDefault( h => h != null );
      return rows.Count == 0 ? null : new ExactVsDefault
      {
         Target = name,
         Runs = rows,
         Ratio = Spread.Of( rows.Select( r => r.Ratio ) ),
         DifferenceMs = Spread.Of( rows.Select( r => r.DifferenceMs ) ),
         RunsExactSlower = rows.Count( r => r.ExactP50Ms > r.DefaultP50Ms ),
         DeclaredPair = hint == null ? null : $"{hint.PassA ?? "?"} vs {hint.PassB ?? "?"}",
         Note = hint?.Note,
      };
   }

   /// <summary>
   /// Whether the exact pass ran before the default latency pass, from the recorded pass order.
   /// </summary>
   /// <param name="passOrder">Pass names in run order, or null.</param>
   /// <returns>True or false, or null when either pass is not in the recorded order.</returns>
   private static bool? ExactRanFirst( IReadOnlyList<string>? passOrder )
   {
      if( passOrder == null )
      {
         return null;
      }

      int exact = IndexWhere( passOrder, p => p.StartsWith( "exact", StringComparison.OrdinalIgnoreCase ) );
      int latency = IndexWhere( passOrder, p => p.Equals( "default@1", StringComparison.OrdinalIgnoreCase ) );
      latency = latency >= 0 ? latency : IndexWhere( passOrder, p => p.StartsWith( "default", StringComparison.OrdinalIgnoreCase ) );
      return exact < 0 || latency < 0 ? null : exact < latency;
   }

   /// <summary>
   /// The rules the numbers follow, printed next to them.
   /// </summary>
   /// <param name="report">The consolidated report (settings and pairs filled).</param>
   /// <returns>One sentence per rule.</returns>
   private static List<string> Notes( ConsolidatedReport report )
   {
      string rows = report.Settings.Rows.HasValue ? $"{report.Settings.Rows.Value.ToString( "N0", System.Globalization.CultureInfo.InvariantCulture )} rows" : "a few hundred rows";
      var notes = new List<string>
      {
         $"Load rows/s is reported but not ranked and does not feed any comparison: each load wrote {rows}, which is too few to separate engines from connection set-up and first-call cost.",
         $"Runs were used together only when their build configuration, CPU governor, CPU partition, warm-up count, exact-mode seconds, seconds per level and every listed target's search settings matched; runs that differed are listed under Runs dropped. Spread is flagged when the largest value is more than {ConsolidateFlags.SPREAD_RATIO.ToString( "0.00", System.Globalization.CultureInfo.InvariantCulture )} times the smallest across runs.",
      };
      notes.Add( ConsolidateFraming.BANDS_LINE + " Bands are drawn only from two runs or more." );
      notes.Add( "Runs of different commands (run-all against bench), a different engine hosting (container against native) or a different segment layout reported by an engine are refused, never merged: the largest-group rule does not apply to them." );
      if( report.Pairs.Count > 0 )
      {
         notes.Add( "Each pair compares two targets that hold their own copy of the data (a separate database, table or collection), so a difference mixes the engine setting with the copy. The exact-versus-default tables compare the two search methods inside one target." );
      }

      return notes;
   }

   /// <summary>
   /// QPS at a level, or null when not measured.
   /// </summary>
   /// <param name="t">Target result.</param>
   /// <param name="level">Concurrency level.</param>
   /// <returns>QPS or null.</returns>
   private static double? Qps( TargetResult t, int level )
   {
      return t.Qps.TryGetValue( level, out double q ) ? q : null;
   }

   /// <summary>
   /// Position (1 = first) of a target in a run's recorded target order, or null.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="name">Target name.</param>
   /// <returns>The position, or null when the order was not recorded or lacks the target.</returns>
   private static int? OrderOf( RunResult run, string name )
   {
      if( run.TargetOrder == null )
      {
         return null;
      }

      int index = IndexWhere( run.TargetOrder, t => string.Equals( t, name, StringComparison.Ordinal ) );
      return index < 0 ? null : index + 1;
   }

   /// <summary>
   /// Index of the first item matching, or -1.
   /// </summary>
   /// <param name="items">Items.</param>
   /// <param name="match">Condition.</param>
   /// <returns>The index or -1.</returns>
   private static int IndexWhere( IReadOnlyList<string> items, Func<string, bool> match )
   {
      for( int i = 0; i < items.Count; i++ )
      {
         if( match( items[i] ) )
         {
            return i;
         }
      }

      return -1;
   }

   /// <summary>
   /// Sum of the values present, or null when none is present.
   /// </summary>
   /// <param name="values">Values.</param>
   /// <returns>The sum, or null.</returns>
   private static int? SumOrNull( IEnumerable<int?> values )
   {
      int[] present = values.OfType<int>().ToArray();
      return present.Length == 0 ? null : present.Sum();
   }

   /// <summary>
   /// Distinct non-null texts, in first-seen order.
   /// </summary>
   /// <param name="values">Texts.</param>
   /// <returns>The distinct texts.</returns>
   private static List<string> Distinct( IEnumerable<string?> values )
   {
      return values.OfType<string>().Distinct( StringComparer.Ordinal ).ToList();
   }

   /// <summary>
   /// Dictionary key for a concurrency level ("8").
   /// </summary>
   /// <param name="level">Level.</param>
   /// <returns>The key.</returns>
   private static string Key( int level )
   {
      return level.ToString( System.Globalization.CultureInfo.InvariantCulture );
   }

   #endregion Private Methods
}
