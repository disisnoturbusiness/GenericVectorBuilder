using System.Globalization;
using System.Text;
using GenericVectorBuilder.Bench.Stats;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// Renders a <see cref="ConsolidatedReport"/> as consolidated.md: tables of numbers and
/// recorded facts only, no prose and no causes.
/// Why numbers only: the dead draft mixed measured gaps with guessed reasons; this file is the
/// evidence a writeup cites, and any explanation belongs in the writeup, tagged as such.
/// A field a run did not record prints as "missing"; a metric that does not apply prints "-".
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
      AppendSummary( md, report );
      AppendPerRun( md, report );
      AppendRanks( md, report );
      AppendPairs( md, report );
      AppendExact( md, report );
      AppendFacts( md, report );
      AppendFlags( md, report );
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
         new[] { "host", s.Host ?? MISSING },
         new[] { "rows x dimension", $"{Int( s.Rows )} x {Int( s.Dimension )}" },
         new[] { "queries", $"{s.QueryKind ?? MISSING}, {Int( s.QueryCount )}" },
         new[] { "top", Int( s.Top ) },
         new[] { "concurrency", s.Concurrency.Count == 0 ? MISSING : string.Join( ", ", s.Concurrency ) },
         new[] { "seconds per level", Int( s.SecondsPerLevel ) },
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
      Table( md, new[] { "target", "hosting", "engine", "index" }, report.TargetSummaries.Select( t => new[]
      {
         t.Name, Join( t.Hosting ), Join( t.Engines ), Join( t.Indexes ),
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
      header.AddRange( new[] { "load rows/s", $"recall@{Int( top )}", $"nDCG@{Int( top )}", "exact p50 ms", "errors", "warm-up errors" } );
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
   /// Rank per run and median rank for latency and each QPS level.
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
         header.Add( "median rank" );
         Table( md, header, report.TargetSummaries.Select( t =>
         {
            RankSpread? ranks = t.Ranks.TryGetValue( key, out RankSpread? r ) ? r : null;
            var cells = new List<string> { t.Name };
            cells.AddRange( report.Runs.Select( ( _, i ) => ranks != null && i < ranks.PerRun.Count ? Int( ranks.PerRun[i] ) : "-" ) );
            cells.Add( Number( ranks?.Median, "0.#" ) );
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
      Table( md, new[] { "target", "run", "order", "passOrder", "warm-up errors", "errors", "afterLoad ready", "afterLoad indexed/total", "afterLoad detail", "afterSearch ready", "afterSearch indexed/total", "durability", "load indexNote" },
         report.TargetSummaries.SelectMany( t => t.PerRun.Select( r => new[]
         {
            t.Name, r.Run, Int( r.OrderInRun ), r.PassOrder == null ? MISSING : string.Join( ", ", r.PassOrder ), Int( r.WarmupErrors ), Int( r.Errors ),
            Ready( r.AfterLoad ), Counts( r.AfterLoad ), r.AfterLoad == null ? MISSING : r.AfterLoad.Detail ?? MISSING,
            Ready( r.AfterSearch ), Counts( r.AfterSearch ), r.Durability ?? MISSING, r.LoadIndexNote ?? "-",
         } ) ) );
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
