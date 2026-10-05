using System.Globalization;
using System.Text;
using GenericVectorBuilder.Bench.Stats;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// Renders a <see cref="ConsolidatedReport"/> as consolidated.md: tables of numbers and
/// recorded facts, plus the fixed framing sentences and the per-engine notes (each tagged with
/// where it came from); no causes.
/// Why numbers and tagged facts only: the dead draft mixed measured gaps with guessed reasons; this
/// file is the evidence a writeup cites, and any explanation belongs in the writeup, tagged as such.
/// A field a run did not record prints as "missing"; a metric that does not apply prints "-".
/// The speed ranking is printed as tie bands under a title that says it is request speed on a
/// small collection (see <see cref="ConsolidateFraming"/>); no ranking table shows a strict rank
/// across runs. The rank-per-run tables show each run's own order, with the band beside them.
/// </summary>
public static class ConsolidatedMarkdown
{
   #region Data Members

   /// <summary>Text for a field the result files did not record.</summary>
   public const string MISSING = "missing";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Renders the report.
   /// </summary>
   /// <param name="report">The consolidated report.</param>
   /// <returns>Markdown text.</returns>
   public static string Render( ConsolidatedReport report )
   {
      var md = new StringBuilder();
      md.AppendLine( "# Consolidated benchmark" ).AppendLine();
      AppendSettings( md, report );
      AppendRuns( md, report );
      AppendTargets( md, report );
      AppendRanking( md, report );
      AppendEngineNotes( md, report );
      AppendSummary( md, report );
      AppendPerRun( md, report );
      AppendRanks( md, report );
      AppendPairs( md, report );
      AppendExact( md, report );
      AppendFacts( md, report );
      AppendFlags( md, report );
      AppendNotes( md, report );
      return md.ToString();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The shared settings and run counts.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendSettings( StringBuilder md, ConsolidatedReport report )
   {
      RunSettings s = report.Settings;
      Table( md, new[] { "item", "value" }, new List<string[]>
      {
         new[] { "created (UTC)", report.CreatedUtc },
         new[] { "command", "`" + report.CommandLine + "`" },
         new[] { "targets", report.Targets.Count.ToString( CultureInfo.InvariantCulture ) },
         new[] { "runs used", report.Runs.Count.ToString( CultureInfo.InvariantCulture ) },
         new[] { "runs dropped", report.Dropped.Count.ToString( CultureInfo.InvariantCulture ) },
         new[] { "pipeline", s.Pipeline ?? MISSING },
         new[] { "benchmark command (every run)", s.Command ?? MISSING },
         new[] { "host", s.Host ?? MISSING },
         new[] { "rows x dimension", $"{Int( s.Rows )} x {Int( s.Dimension )}" },
         new[] { "queries", $"{s.QueryKind ?? MISSING}, {Int( s.QueryCount )}" },
         new[] { "top", Int( s.Top ) },
         new[] { "concurrency", s.Concurrency.Count == 0 ? MISSING : string.Join( ", ", s.Concurrency ) },
         new[] { "seconds per level", Int( s.SecondsPerLevel ) },
         new[] { "build configuration", Recorded( s.BuildConfiguration, s, "buildConfiguration" ) },
         new[] { "machine control", Recorded( s.MachineControl, s, "machineControl" ) },
         new[] { "CPU governor", Recorded( s.Governor, s, "governor" ) },
         new[] { "CPU partition", Recorded( s.CpuPartition, s, "cpuPartition" ) },
         new[] { "warm-up searches before each timed pass", Recorded( s.WarmupSearches?.ToString( CultureInfo.InvariantCulture ), s, "warmupSearches" ) },
         new[] { "exact mode seconds", Recorded( s.ExactSeconds?.ToString( CultureInfo.InvariantCulture ), s, "exactSeconds" ) },
      } );
   }

   /// <summary>
   /// Runs used (with seed and target order) and runs dropped (with reason).
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendRuns( StringBuilder md, ConsolidatedReport report )
   {
      md.AppendLine( "## Runs used" ).AppendLine();
      Table( md, new[] { "run", "started (UTC)", "runSeed", "load average", "targetOrder" }, report.Runs.Select( r => new[]
      {
         r.Name, r.StartedUtc ?? MISSING, Int( r.RunSeed ), r.LoadAverage ?? MISSING,
         r.TargetOrder == null ? MISSING : string.Join( ", ", r.TargetOrder ),
      } ) );
      md.AppendLine( "## Runs dropped" ).AppendLine();
      Table( md, new[] { "run", "reason" }, report.Dropped.Select( d => new[] { d.Name, d.Reason } ) );
   }

   /// <summary>
   /// Engine, hosting and index text of each target (every distinct value seen).
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendTargets( StringBuilder md, ConsolidatedReport report )
   {
      md.AppendLine( "## Targets" ).AppendLine();
      Table( md, new[] { "target", "hosting", "engine", "index", "search settings" }, report.TargetSummaries.Select( t => new[]
      {
         t.Name, Join( t.Hosting ), Join( t.Engines ), Join( t.Indexes ),
         report.Settings.SearchSettings.TryGetValue( t.Name, out string? settings ) && settings != null ? settings : MISSING,
      } ) );
   }

   /// <summary>
   /// Median [min, max] of every metric per target.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendSummary( StringBuilder md, ConsolidatedReport report )
   {
      List<string> levels = Levels( report );
      List<string> ratios = report.TargetSummaries.SelectMany( t => t.QpsRatio.Keys ).Distinct().ToList();
      int? top = report.Settings.Top;
      var header = new List<string> { "target", "n", "p50 ms", "p95 ms" };
      header.AddRange( levels.Select( l => $"QPS@{l}" ) );
      header.AddRange( ratios.Select( r => $"QPS ratio {r}" ) );
      header.AddRange( new[] { "load rows/s (not ranked)", $"recall@{Int( top )}", $"nDCG@{Int( top )}", "exact p50 ms", "errors", "warm-up errors" } );
      md.AppendLine( "## Per target: median [min, max]" ).AppendLine();
      Table( md, header, report.TargetSummaries.Select( t =>
      {
         var cells = new List<string> { t.Name, t.P50Ms.N.ToString( CultureInfo.InvariantCulture ), Range( t.P50Ms, MsFormat ), Range( t.P95Ms, MsFormat ) };
         cells.AddRange( levels.Select( l => t.Qps.TryGetValue( l, out Spread? q ) ? Range( q, _ => "0.0" ) : "-" ) );
         cells.AddRange( ratios.Select( r => t.QpsRatio.TryGetValue( r, out Spread? q ) ? Range( q, _ => "0.00" ) : "-" ) );
         cells.AddRange( new[] { Range( t.LoadRowsPerSecond, _ => "0" ), Range( t.Recall, _ => "0.000" ), Range( t.Ndcg, _ => "0.000" ), Range( t.ExactP50Ms, MsFormat ), Int( t.Errors ), Int( t.WarmupErrors ) } );
         return cells.ToArray();
      } ) );
   }

   /// <summary>
   /// Per-run p50, QPS at each level and QPS ratio (higher level over the lowest), one column
   /// per run, so every median can be checked against the values it came from.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendPerRun( StringBuilder md, ConsolidatedReport report )
   {
      md.AppendLine( "## Per run: p50 ms" ).AppendLine();
      PerRunTable( md, report, "median", t => t.P50Ms, "0.00" );
      foreach( string level in Levels( report ) )
      {
         md.AppendLine( $"## Per run: QPS@{level}" ).AppendLine();
         PerRunTable( md, report, "median", t => t.Qps.TryGetValue( level, out Spread? s ) ? s : null, "0.0" );
      }

      foreach( string ratio in report.TargetSummaries.SelectMany( t => t.QpsRatio.Keys ).Distinct() )
      {
         md.AppendLine( $"## Per run: QPS ratio {ratio}" ).AppendLine();
         PerRunTable( md, report, "median", t => t.QpsRatio.TryGetValue( ratio, out Spread? s ) ? s : null, "0.00" );
      }
   }

   /// <summary>
   /// Rank per run and, in place of a median rank, the tie band for latency and each QPS level.
   /// Why the band and not the median rank: the median of three per-run ranks reads as a strict
   /// rank, and targets whose ranges overlap cannot be ranked from these runs.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendRanks( StringBuilder md, ConsolidatedReport report )
   {
      var metrics = new List<(string Key, string Title)> { ( "p50Ms", "p50 (1 = lowest)" ) };
      metrics.AddRange( Levels( report ).AsEnumerable().Reverse().Select( l => ( $"qps@{l}", $"QPS@{l} (1 = highest)" ) ) );
      foreach( (string key, string title) in metrics )
      {
         md.AppendLine( $"## Rank per run: {title}" ).AppendLine();
         var header = new List<string> { "target" };
         header.AddRange( report.Runs.Select( r => r.Name ) );
         header.Add( "band" );
         Table( md, header, report.TargetSummaries.Select( t =>
         {
            RankSpread? ranks = t.Ranks.TryGetValue( key, out RankSpread? r ) ? r : null;
            var cells = new List<string> { t.Name };
            cells.AddRange( report.Runs.Select( ( _, i ) => ranks != null && i < ranks.PerRun.Count ? Int( ranks.PerRun[i] ) : "-" ) );
            cells.Add( BandOf( report, key, t.Name ) );
            return cells.ToArray();
         } ) );
      }
   }

   /// <summary>
   /// Each pair, run by run, then the summary of the per-run ratios.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendPairs( StringBuilder md, ConsolidatedReport report )
   {
      List<string> levels = Levels( report );
      foreach( PairSummary pair in report.Pairs )
      {
         md.AppendLine( $"## Paired per run: {pair.A} / {pair.B}" ).AppendLine();
         var header = new List<string> { "run", $"{pair.A} p50 ms", $"{pair.B} p50 ms", "p50 ratio", "p95 ratio" };
         header.AddRange( levels.SelectMany( l => new[] { $"{pair.A} QPS@{l}", $"{pair.B} QPS@{l}", $"QPS@{l} ratio" } ) );
         header.AddRange( new[] { "recall diff", "nDCG diff", $"{pair.A} order", $"{pair.B} order" } );
         Table( md, header, pair.Runs.Select( r =>
         {
            var cells = new List<string> { r.Run, Ms( r.AP50Ms ), Ms( r.BP50Ms ), Number( r.P50Ratio, "0.000" ), Number( r.P95Ratio, "0.000" ) };
            cells.AddRange( levels.SelectMany( l => new[] { Number( r.AQps[l], "0.0" ), Number( r.BQps[l], "0.0" ), Number( r.QpsRatio[l], "0.000" ) } ) );
            cells.AddRange( new[] { Number( r.RecallDifference, "0.000" ), Number( r.NdcgDifference, "0.000" ), Int( r.AOrder ), Int( r.BOrder ) } );
            return cells.ToArray();
         } ) );
         var rows = new List<string[]>
         {
            SpreadRow( "p50 ratio", pair.P50Ratio, "0.000", $"{pair.A} lower p50", pair.RunsALowerP50 ),
            SpreadRow( "p95 ratio", pair.P95Ratio, "0.000", "-", null ),
         };
         rows.AddRange( levels.Select( l => SpreadRow( $"QPS@{l} ratio", pair.QpsRatio[l], "0.000", $"{pair.A} higher QPS@{l}", pair.RunsAHigherQps[l] ) ) );
         rows.Add( SpreadRow( "recall diff", pair.RecallDifference, "0.000", "-", null ) );
         rows.Add( SpreadRow( "nDCG diff", pair.NdcgDifference, "0.000", "-", null ) );
         rows.Add( new[] { "target order", "-", "-", "-", "-", $"{pair.A} ran before {pair.B}", pair.RunsARanFirst.HasValue ? pair.RunsARanFirst.Value.ToString( CultureInfo.InvariantCulture ) : MISSING } );
         Table( md, new[] { "per-run value", "median", "min", "max", "n", "runs counted", "count" }, rows );
      }
   }

   /// <summary>
   /// Exact against default p50, run by run, then the summary per target.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendExact( StringBuilder md, ConsolidatedReport report )
   {
      if( report.ExactVsDefault.Count == 0 )
      {
         return;
      }

      md.AppendLine( "## Paired per run: exact p50 / default p50" ).AppendLine();
      foreach( ExactVsDefault hinted in report.ExactVsDefault.Where( e => e.DeclaredPair != null ) )
      {
         md.AppendLine( $"- {hinted.Target}, default pair {hinted.DeclaredPair}: {hinted.Note ?? "no note"}" );
      }

      if( report.ExactVsDefault.Any( e => e.DeclaredPair != null ) )
      {
         md.AppendLine();
      }

      Table( md, new[] { "target", "run", "default p50 ms", "exact p50 ms", "exact - default ms", "exact / default", "exact recall", "exact pass before default@1" },
         report.ExactVsDefault.SelectMany( e => e.Runs.Select( r => new[]
         {
            e.Target, r.Run, Ms( r.DefaultP50Ms ), Ms( r.ExactP50Ms ), Number( r.DifferenceMs, "0.00" ), Number( r.Ratio, "0.000" ),
            Number( r.ExactRecall, "0.000" ), r.ExactRanFirst.HasValue ? ( r.ExactRanFirst.Value ? "yes" : "no" ) : MISSING,
         } ) ) );
      md.AppendLine( "## Exact / default summary" ).AppendLine();
      Table( md, new[] { "target", "n", "median exact/default", "min", "max", "median exact - default ms", "runs exact slower" },
         report.ExactVsDefault.Select( e => new[]
         {
            e.Target, e.Ratio.N.ToString( CultureInfo.InvariantCulture ), Number( e.Ratio.Median, "0.000" ), Number( e.Ratio.Min, "0.000" ), Number( e.Ratio.Max, "0.000" ),
            Number( e.DifferenceMs.Median, "0.00" ), $"{e.RunsExactSlower} of {e.Runs.Count}",
         } ) );
   }

   /// <summary>
   /// Carry-through facts per target and run: order, passes, warm-up errors, index proof, durability.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendFacts( StringBuilder md, ConsolidatedReport report )
   {
      md.AppendLine( "## Recorded per run: order, passes, index state, durability" ).AppendLine();
      Table( md, new[] { "target", "run", "order", "passOrder", "warm-up errors", "errors", "afterLoad ready", "afterLoad indexed/total", "afterLoad detail", "afterSearch ready", "afterSearch indexed/total", "segments after load / after search", "durability", "load indexNote", "settled", "mean ms" },
         report.TargetSummaries.SelectMany( t => t.PerRun.Select( r => new[]
         {
            t.Name, r.Run, Int( r.OrderInRun ), r.PassOrder == null ? MISSING : string.Join( ", ", r.PassOrder ), Int( r.WarmupErrors ), Int( r.Errors ),
            Ready( r.AfterLoad ), Counts( r.AfterLoad ), r.AfterLoad == null ? MISSING : r.AfterLoad.Detail ?? MISSING,
            Ready( r.AfterSearch ), Counts( r.AfterSearch ), Layouts( r ), r.Durability ?? MISSING, r.LoadIndexNote ?? "-",
            r.Settled.HasValue ? ( r.Settled.Value ? "yes" : "no" + ( r.SettleDetail == null ? string.Empty : ": " + r.SettleDetail ) ) : MISSING,
            r.MeanMs.HasValue ? $"{Number( r.MeanMs, MsFormat( r.MeanMs.Value ) )} ({r.MeanSource})" : "-",
         } ) ) );
   }

   /// <summary>
   /// The speed ranking: a title that says what was ranked and on how big a collection, the one
   /// line that says what the numbers do not measure, what a band is, then one table per metric
   /// in band order. A table never shows a strict rank.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendRanking( StringBuilder md, ConsolidatedReport report )
   {
      int? rows = report.Settings.Rows;
      md.AppendLine( $"## {ConsolidateFraming.Title( rows )}" ).AppendLine();
      md.AppendLine( ConsolidateFraming.Line( rows ) ).AppendLine();
      md.AppendLine( report.Runs.Count >= 2 ? ConsolidateFraming.BANDS_LINE : ConsolidateFraming.ONE_RUN_LINE ).AppendLine();
      foreach( MetricBands metric in report.Bands )
      {
         md.AppendLine( $"### {metric.Title}" ).AppendLine();
         string[] header = { metric.Banded ? "band" : "order in this run", "target", metric.Banded ? "median [min, max]" : "value", "runs", "engine notes" };
         Table( md, header, metric.Entries.Select( e => RankingRow( report, metric, e ) ) );
      }
   }

   /// <summary>
   /// One row of a ranking table: band (or order in a one-run report), target, value, runs, note kinds.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <param name="metric">The metric's bands.</param>
   /// <param name="entry">The target's place.</param>
   /// <returns>The cells.</returns>
   private static string[] RankingRow( ConsolidatedReport report, MetricBands metric, BandEntry entry )
   {
      bool higher = metric.HigherIsBetter;
      string value = entry.Median.HasValue ? ( metric.Banded ? $"{Speed( entry.Median, higher )} [{Speed( entry.Min, higher )}, {Speed( entry.Max, higher )}]" : Speed( entry.Median, higher ) ) : "-";
      return new[] { metric.Banded ? Int( entry.Band, "-" ) : Int( entry.OrderInBand, "-" ), entry.Target, value, entry.N.ToString( CultureInfo.InvariantCulture ), NoteKinds( report, entry.Target ) };
   }

   /// <summary>
   /// A speed value: one decimal for QPS, the millisecond format for latency, "-" when missing.
   /// </summary>
   /// <param name="value">Value.</param>
   /// <param name="higherIsBetter">True for QPS.</param>
   /// <returns>Text.</returns>
   private static string Speed( double? value, bool higherIsBetter )
   {
      return value.HasValue ? Number( value, higherIsBetter ? "0.0" : MsFormat( value.Value ) ) : "-";
   }

   /// <summary>
   /// The per-engine notes: what a reader needs to know about an engine to read its speed, each
   /// with where it came from, and the flags the engine carries (their evidence is under Flags).
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendEngineNotes( StringBuilder md, ConsolidatedReport report )
   {
      md.AppendLine( "## Per-engine notes" ).AppendLine();
      var rows = new List<string[]>();
      foreach( TargetSummary t in report.TargetSummaries )
      {
         rows.AddRange( t.EngineNotes.Select( n => new[] { t.Name, n.Kind, n.Text, n.Source } ) );
         string[] kinds = report.Flags.Where( f => f.Target == t.Name ).Select( f => f.Kind ).ToArray();
         if( kinds.Length > 0 )
         {
            rows.Add( new[] { t.Name, "flags", string.Join( ", ", kinds ) + " (evidence under Flags)", ConsolidateEngineNotes.SOURCE_RESULTS } );
         }
      }

      Table( md, new[] { "target", "kind", "note", "source" }, rows );
   }

   /// <summary>
   /// The band of a target for a metric, "-" when the metric has no bands (one run) or no value.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <param name="metric">Metric key ("p50Ms", "qps@8").</param>
   /// <param name="target">Target name.</param>
   /// <returns>The band number as text, or "-".</returns>
   private static string BandOf( ConsolidatedReport report, string metric, string target )
   {
      BandEntry? entry = report.Bands.FirstOrDefault( b => b.Metric == metric )?.Entries.FirstOrDefault( e => e.Target == target );
      return Int( entry?.Band, "-" );
   }

   /// <summary>
   /// The kinds of a target's engine notes, joined, or "-" when it has none.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <param name="target">Target name.</param>
   /// <returns>E.g. "cpu-cap, exact-by-design".</returns>
   private static string NoteKinds( ConsolidatedReport report, string target )
   {
      string[] kinds = report.TargetSummaries.FirstOrDefault( t => t.Name == target )?.EngineNotes.Select( n => n.Kind ).ToArray() ?? Array.Empty<string>();
      return kinds.Length == 0 ? "-" : string.Join( ", ", kinds );
   }

   /// <summary>
   /// The segment layouts a run reported for a target, "after load / after search", "-" for one not reported.
   /// </summary>
   /// <param name="facts">The target's facts in one run.</param>
   /// <returns>Text such as "2 segments / 2 segments".</returns>
   private static string Layouts( TargetRunFacts facts )
   {
      return facts.SegmentLayoutAfterLoad == null && facts.SegmentLayoutAfterSearch == null ? "-" : $"{facts.SegmentLayoutAfterLoad ?? "-"} / {facts.SegmentLayoutAfterSearch ?? "-"}";
   }

   /// <summary>
   /// Every flag, one row per target and kind.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendFlags( StringBuilder md, ConsolidatedReport report )
   {
      md.AppendLine( "## Flags" ).AppendLine();
      Table( md, new[] { "target", "flag", "runs", "detail" }, report.Flags.Select( f => new[]
      {
         f.Target, f.Kind, $"{f.Runs.Count} of {report.Runs.Count}", f.Detail,
      } ) );
   }

   /// <summary>
   /// The rules the numbers follow, as bullets.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void AppendNotes( StringBuilder md, ConsolidatedReport report )
   {
      if( report.Notes.Count == 0 )
      {
         return;
      }

      md.AppendLine( "## Notes" ).AppendLine();
      report.Notes.ForEach( n => md.AppendLine( $"- {n}" ) );
      md.AppendLine();
   }

   /// <summary>
   /// A recorded setting with the source when it was derived, "missing" when it was not recorded.
   /// </summary>
   /// <param name="value">The value, or null.</param>
   /// <param name="settings">The report's settings (holds the derived sources).</param>
   /// <param name="field">Field name in the derived map.</param>
   /// <returns>Text such as "Debug (from the path of the binary in the command line)".</returns>
   private static string Recorded( string? value, RunSettings settings, string field )
   {
      return value == null ? MISSING : settings.Derived.TryGetValue( field, out string? how ) ? $"{value} ({how})" : value;
   }

   /// <summary>
   /// A table with one column per run plus a summary column, from a per-target spread.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   /// <param name="summaryTitle">Title of the summary column.</param>
   /// <param name="spread">The spread of a target, or null.</param>
   /// <param name="format">Number format.</param>
   private static void PerRunTable( StringBuilder md, ConsolidatedReport report, string summaryTitle, Func<TargetSummary, Spread?> spread, string format )
   {
      var header = new List<string> { "target" };
      header.AddRange( report.Runs.Select( r => r.Name ) );
      header.Add( summaryTitle );
      Table( md, header, report.TargetSummaries.Select( t =>
      {
         Spread? s = spread( t );
         var cells = new List<string> { t.Name };
         cells.AddRange( report.Runs.Select( ( _, i ) => s != null && i < s.PerRun.Count ? Number( s.PerRun[i], format ) : "-" ) );
         cells.Add( Number( s?.Median, format ) );
         return cells.ToArray();
      } ) );
   }

   /// <summary>
   /// One summary row: median, min, max, n, and an optional count of runs.
   /// </summary>
   /// <param name="label">Row label.</param>
   /// <param name="s">The spread.</param>
   /// <param name="format">Number format.</param>
   /// <param name="countLabel">What the count counts, or "-".</param>
   /// <param name="count">The count, or null.</param>
   /// <returns>The cells.</returns>
   private static string[] SpreadRow( string label, Spread s, string format, string countLabel, int? count )
   {
      return new[]
      {
         label, Number( s.Median, format ), Number( s.Min, format ), Number( s.Max, format ), s.N.ToString( CultureInfo.InvariantCulture ),
         countLabel, count.HasValue ? count.Value.ToString( CultureInfo.InvariantCulture ) : "-",
      };
   }

   /// <summary>
   /// Writes a Markdown table followed by a blank line; "(none)" when there are no rows.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="header">Column titles.</param>
   /// <param name="rows">Rows of cells.</param>
   private static void Table( StringBuilder md, IReadOnlyList<string> header, IEnumerable<string[]> rows )
   {
      List<string[]> list = rows.ToList();
      if( list.Count == 0 )
      {
         md.AppendLine( "(none)" ).AppendLine();
         return;
      }

      md.AppendLine( "| " + string.Join( " | ", header.Select( Cell ) ) + " |" );
      md.AppendLine( "|" + string.Concat( header.Select( _ => "---|" ) ) );
      foreach( string[] row in list )
      {
         md.AppendLine( "| " + string.Join( " | ", row.Select( Cell ) ) + " |" );
      }

      md.AppendLine();
   }

   /// <summary>
   /// Concurrency levels as dictionary keys, lowest first.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <returns>The level keys.</returns>
   private static List<string> Levels( ConsolidatedReport report )
   {
      return report.TargetSummaries.SelectMany( t => t.Qps.Keys ).Distinct()
         .OrderBy( k => int.TryParse( k, NumberStyles.Integer, CultureInfo.InvariantCulture, out int l ) ? l : int.MaxValue ).ToList();
   }

   /// <summary>
   /// "median [min, max]", or "-" when no run had the value.
   /// </summary>
   /// <param name="s">The spread.</param>
   /// <param name="format">Picks the number format from the value.</param>
   /// <returns>Text.</returns>
   private static string Range( Spread s, Func<double, string> format )
   {
      if( s.Median is not double median )
      {
         return "-";
      }

      return $"{Number( median, format( median ) )} [{Number( s.Min, format( s.Min!.Value ) )}, {Number( s.Max, format( s.Max!.Value ) )}]";
   }

   /// <summary>
   /// Millisecond format: two decimals below 10 ms, one above (same rule as results.md).
   /// </summary>
   /// <param name="ms">Value.</param>
   /// <returns>Format string.</returns>
   private static string MsFormat( double ms )
   {
      return ms < 10 ? "0.00" : "0.0";
   }

   /// <summary>
   /// Milliseconds with <see cref="MsFormat"/>, "-" when missing.
   /// </summary>
   /// <param name="ms">Value.</param>
   /// <returns>Text.</returns>
   private static string Ms( double? ms )
   {
      return ms.HasValue ? Number( ms, MsFormat( ms.Value ) ) : "-";
   }

   /// <summary>
   /// A number in invariant culture, "-" when missing.
   /// </summary>
   /// <param name="value">Value.</param>
   /// <param name="format">Format string.</param>
   /// <returns>Text.</returns>
   private static string Number( double? value, string format )
   {
      return ConsolidateMath.IsNumber( value ) ? value!.Value.ToString( format, CultureInfo.InvariantCulture ) : "-";
   }

   /// <summary>
   /// A whole number, or "missing" when the result files did not record it.
   /// </summary>
   /// <param name="value">Value.</param>
   /// <returns>Text.</returns>
   private static string Int( int? value )
   {
      return value.HasValue ? value.Value.ToString( CultureInfo.InvariantCulture ) : MISSING;
   }

   /// <summary>
   /// A whole number, or the given text when it is missing.
   /// </summary>
   /// <param name="value">Value.</param>
   /// <param name="missing">Text for a missing value.</param>
   /// <returns>Text.</returns>
   private static string Int( int? value, string missing )
   {
      return value.HasValue ? value.Value.ToString( CultureInfo.InvariantCulture ) : missing;
   }

   /// <summary>
   /// Ready flag of an index state: "true", "false", "not written" or "missing".
   /// </summary>
   /// <param name="state">The state.</param>
   /// <returns>Text.</returns>
   private static string Ready( IndexStateFacts? state )
   {
      return state == null ? MISSING : state.Ready.HasValue ? ( state.Ready.Value ? "true" : "false" ) : "not written";
   }

   /// <summary>
   /// "indexed/total" of an index state, "?" for a count the engine did not give.
   /// </summary>
   /// <param name="state">The state.</param>
   /// <returns>Text.</returns>
   private static string Counts( IndexStateFacts? state )
   {
      return state == null ? MISSING : $"{state.IndexedVectors?.ToString( CultureInfo.InvariantCulture ) ?? "?"}/{state.TotalVectors?.ToString( CultureInfo.InvariantCulture ) ?? "?"}";
   }

   /// <summary>
   /// Distinct values joined, "missing" when there are none.
   /// </summary>
   /// <param name="values">Values.</param>
   /// <returns>Text.</returns>
   private static string Join( IReadOnlyList<string> values )
   {
      return values.Count == 0 ? MISSING : string.Join( " / ", values );
   }

   /// <summary>
   /// Makes text safe inside a Markdown table cell.
   /// </summary>
   /// <param name="text">Text.</param>
   /// <returns>Escaped text.</returns>
   private static string Cell( string text )
   {
      return text.Replace( "|", "\\|" ).Replace( "\r", " " ).Replace( "\n", " " );
   }

   #endregion Private Methods
}
