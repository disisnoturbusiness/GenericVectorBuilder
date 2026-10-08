using System.Globalization;
using System.Text;
using GenericVectorBuilder.Bench.Stats;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// consolidated.md of the v8 report: headline, subtitle, the rule that says what "not separated" means, the four tables, recall, why, then the threshold and basis, drift,
/// disclosures, method, targets not in this report and runs used. The results come first and the argument for the threshold after them.
/// Every sentence comes from the report's audited sentences[] by slot; everything else is a heading,
/// a table header or a table cell holding a value of consolidated.json. No prose is written here.
/// Why: the reader lens re-derives every sentence of this file from the raw files, and a sentence
/// that does not travel in sentences[] would escape the audit.
/// </summary>
public static class ConsolidatedMarkdown
{
   #region Public Methods

   /// <summary>
   /// Renders the report.
   /// </summary>
   /// <param name="report">The report, sentences filled.</param>
   /// <returns>Markdown text.</returns>
   public static string Render( ConsolidatedReport report )
   {
      var md = new StringBuilder();
      md.AppendLine( "# Vector engine benchmark" ).AppendLine();
      Paragraph( md, report, "headline", "stopped" );
      Paragraph( md, report, "subtitle" );
      Paragraph( md, report, "rule.claim" );
      var printed = new HashSet<string>( StringComparer.Ordinal );
      foreach( MetricTable table in report.Metrics )
      {
         Table( md, report, table, printed );
      }

      Section( md, "Recall" );
      Paragraph( md, report, "recall" );
      RecallTable( md, report );
      Section( md, "Why some engines beat others: facts and measured costs" );
      Paragraph( md, report, "why" );
      WhyTable( md, report );
      Section( md, "Threshold and basis" );
      Paragraph( md, report, "rule.threshold", "basis" );
      BasisTables( md, report );
      Drift( md, report );
      Section( md, "Disclosures" );
      Paragraph( md, report, "disclosure" );
      DroppedTable( md, report );
      SettingsTable( md, report );
      ClausesTable( md, report );
      Section( md, "Method" );
      Paragraph( md, report, "method" );
      if( HasSentences( report, "notinreport" ) )
      {
         Section( md, "Targets not in this report" );
         Paragraph( md, report, "notinreport" );
      }

      Section( md, "Runs used" );
      Paragraph( md, report, "runs" );
      RunsTable( md, report );
      return md.ToString();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Whether the report holds a sentence whose slot is the prefix or starts with it and a dot.
   /// Why: a heading with nothing under it is an empty section on the page, and a reader looks for what is missing.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <param name="prefix">The slot prefix.</param>
   /// <returns>True when there is one.</returns>
   private static bool HasSentences( ConsolidatedReport report, string prefix )
   {
      return report.Sentences.Any( s => s.Slot == prefix || s.Slot.StartsWith( prefix + ".", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A second-level heading.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="title">Heading text.</param>
   private static void Section( StringBuilder md, string title )
   {
      md.AppendLine( $"## {title}" ).AppendLine();
   }

   /// <summary>
   /// Every sentence whose slot is one of the prefixes (or starts with it and a dot), in report order, one line each.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   /// <param name="prefixes">Slot prefixes.</param>
   private static void Paragraph( StringBuilder md, ConsolidatedReport report, params string[] prefixes )
   {
      List<SentenceRecord> found = report.Sentences.Where( s => prefixes.Any( p => s.Slot == p || s.Slot.StartsWith( p + ".", StringComparison.Ordinal ) ) ).ToList();
      foreach( SentenceRecord s in found )
      {
         string? label = Quoted( s ) ? Label( s.Slot ) : null;
         if( label != null )
         {
            md.AppendLine( $"**{label}**" ).AppendLine();
         }

         md.AppendLine( Quoted( s ) ? $"> {s.Text}" : s.Text ).AppendLine();
      }
   }

   /// <summary>
   /// The label shown above a quoted durability or disk text: the target (and run) its slot names; null for other quotes.
   /// </summary>
   /// <param name="slot">The sentence's slot.</param>
   /// <returns>The label, or null.</returns>
   private static string? Label( string slot )
   {
      foreach( string prefix in new[] { "disclosure.disk." } )
      {
         if( slot.StartsWith( prefix, StringComparison.Ordinal ) )
         {
            return slot[prefix.Length..].Replace( '.', ' ' );
         }
      }

      return null;
   }

   /// <summary>
   /// True when the sentence is a verbatim quote of a recorded field.
   /// </summary>
   /// <param name="s">The sentence.</param>
   /// <returns>True for a quote.</returns>
   private static bool Quoted( SentenceRecord s )
   {
      return s.Sources.Count == 1 && s.Sources[0].Kind == SourceKinds.QUOTE;
   }

   /// <summary>
   /// The basis runs, the per-metric moves and the left-out runs.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void BasisTables( StringBuilder md, ConsolidatedReport report )
   {
      BasisInfo b = report.Basis;
      md.AppendLine( "| basis run | session | seed | turbo | uncore | warm-up | rehearsal | build |" ).AppendLine( "|---|---|---|---|---|---|---|---|" );
      foreach( BasisRunInfo r in b.Runs )
      {
         md.AppendLine( Row( r.Folder, r.Session, r.Seed?.ToString( CultureInfo.InvariantCulture ) ?? string.Empty, r.Conditions.Turbo, r.Conditions.Uncore, r.Conditions.Warmup, r.Conditions.Rehearsal, r.Conditions.Build ) );
      }

      md.AppendLine().AppendLine( "| metric | one engine, same recorded setup | pair, same recorded setup | one engine, also same run conditions | pair, also same run conditions |" ).AppendLine( "|---|---|---|---|---|" );
      foreach( (string metric, MetricBasis m) in b.PerMetric )
      {
         md.AppendLine( Row( ClaimMetrics.Label( metric ), Move( m.OneEngine ), Move( m.Pair ), Move( m.OneEngineSameSettings ), Move( m.PairSameSettings ) ) );
      }

      md.AppendLine();
      if( b.MaxMove != null )
      {
         md.AppendLine( "| differences between the two runs of the largest move |" ).AppendLine( "|---|" );
         b.MaxMove.Differences.ForEach( d => md.AppendLine( Row( d ) ) );
         md.AppendLine();
      }

      SplitsTable( md, b );
      md.AppendLine( "| run not in the basis | reason |" ).AppendLine( "|---|---|" );
      b.LeftOut.ForEach( l => md.AppendLine( Row( l.Folder, l.Reason ) ) );
      md.AppendLine();
   }

   /// <summary>
   /// The setup splits: each change of an engine's recorded setup between basis runs, the field that changed, the largest moves it hides and the threshold they would give.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="b">The basis.</param>
   private static void SplitsTable( StringBuilder md, BasisInfo b )
   {
      if( b.SetupSplits.Count == 0 )
      {
         return;
      }

      md.AppendLine( "| setup split | fields changed | between | one-engine move hidden | pair move hidden | threshold if counted |" ).AppendLine( "|---|---|---|---|---|---|" );
      foreach( SetupSplit x in b.SetupSplits )
      {
         string one = x.OneEngine == null ? string.Empty : $"{ConsolidateSources.Percent( x.OneEngine.MoveBp )}% {ClaimMetrics.Label( x.OneEngine.Metric )}";
         string pair = x.Pair == null ? string.Empty : $"{ConsolidateSources.Percent( x.Pair.MoveBp )}% {x.Pair.Pair} {ClaimMetrics.Label( x.Pair.Metric )}";
         md.AppendLine( Row( x.Target, string.Join( ", ", x.Fields ), $"{ConsolidateSources.List( x.Before.Sessions )} vs {ConsolidateSources.List( x.After.Sessions )}", one, pair, $"{ConsolidateSources.Percent( x.TBpIfCounted )}%" ) );
      }

      md.AppendLine();
   }

   /// <summary>
   /// One metric table with its captions, row notes and flags.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   /// <param name="table">The table.</param>
   /// <param name="printed">Flag texts printed so far: a target-level flag is the same evidence under every metric, and later tables show its code alone.</param>
   private static void Table( StringBuilder md, ConsolidatedReport report, MetricTable table, HashSet<string> printed )
   {
      Section( md, ClaimMetrics.Label( table.Metric ) );
      Paragraph( md, report, $"table.{table.Metric}.caption", $"table.{table.Metric}.oneSession" );
      List<string> sessions = report.Sessions.Select( s => s.Name ).ToList();
      int dec = ConsolidateText.Decimals( table.Metric );
      bool mode = table.Metric != ClaimMetrics.EXACT;
      md.AppendLine( "| engine | " + string.Join( " | ", sessions.Select( s => $"{s} min-max" ) ) + $" | median of {report.RunsShown} |{( mode ? " search |" : string.Empty )} not separated from | flags |" );
      md.AppendLine( "|---|" + string.Concat( sessions.Select( _ => "---|" ) ) + "---|" + ( mode ? "---|" : string.Empty ) + "---|---|" );
      foreach( MetricRow row in table.Rows )
      {
         IEnumerable<string> ranges = sessions.Select( s => row.PerSession.TryGetValue( s, out SessionFigures? f ) ? $"{ConsolidateSources.Number( f.Min, dec )}-{ConsolidateSources.Number( f.Max, dec )}" : "one session" );
         IEnumerable<string> cells = new[] { row.Display }.Concat( ranges ).Append( ConsolidateSources.Number( row.Median, dec ) );
         if( mode )
         {
            cells = cells.Append( row.SearchMode == null ? string.Empty : $"{row.SearchMode}; {row.SearchConfidence}" );
         }

         md.AppendLine( Row( cells.Append( string.Join( ", ", row.NotSeparatedFrom ) ).Append( string.Join( ", ", row.Flags.Select( f => f.Code ).Distinct( StringComparer.Ordinal ) ) ).ToArray() ) );
      }

      md.AppendLine();
      Paragraph( md, report, $"table.{table.Metric}.absent" );
      Paragraph( md, report, $"table.{table.Metric}.notHeld" );
      foreach( MetricRow row in table.Rows.Where( r => r.Note != null ) )
      {
         md.AppendLine( row.Note ).AppendLine();
      }

      foreach( RowFlag flag in table.Rows.SelectMany( r => r.Flags ) )
      {
         if( printed.Add( flag.Text ) )
         {
            md.AppendLine( flag.Text ).AppendLine();
         }
      }
   }

   /// <summary>
   /// The recall table.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void RecallTable( StringBuilder md, ConsolidatedReport report )
   {
      md.AppendLine( "| engine | hits per run | of | differs |" ).AppendLine( "|---|---|---|---|" );
      foreach( RecallRow r in report.Recall )
      {
         md.AppendLine( Row( r.Target, string.Join( "; ", r.Hits.Select( h => $"{h.Key}: {string.Join( ", ", h.Value )}" ) ), r.Of.ToString( CultureInfo.InvariantCulture ), r.Differs ? "yes" : "no" ) );
      }

      md.AppendLine();
   }

   /// <summary>
   /// The why table: facts, then costs.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void WhyTable( StringBuilder md, ConsolidatedReport report )
   {
      md.AppendLine( "| engine | kind | fact | confidence | class | source |" ).AppendLine( "|---|---|---|---|---|---|" );
      foreach( WhyRow row in report.Why )
      {
         row.Facts.ForEach( f => md.AppendLine( Row( row.Target, f.Kind + ( f.Mode == null ? string.Empty : $" ({f.Mode})" ), f.Text, f.Confidence, f.Class, f.Where ) ) );
      }

      md.AppendLine().AppendLine( "| engine | engine CPU ms/search @1 | @8 | client CPU ms/search @1 | @8 | engine CPUs busy @8 |" ).AppendLine( "|---|---|---|---|---|---|" );
      foreach( WhyRow row in report.Why )
      {
         WhyCosts c = row.Costs;
         md.AppendLine( Row( row.Target, Cost( c.EngineCpuMsPerSearch, "1" ), Cost( c.EngineCpuMsPerSearch, "8" ), Cost( c.ClientCpuMsPerSearch, "1" ), Cost( c.ClientCpuMsPerSearch, "8" ), c.EngineCpusBusyAt8 is double busy ? ConsolidateSources.Number( busy, 2 ) : string.Empty ) );
      }

      md.AppendLine();
   }

   /// <summary>
   /// The drift section with its lists (two-session mode).
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void Drift( StringBuilder md, ConsolidatedReport report )
   {
      if( report.Drift == null )
      {
         return;
      }

      Section( md, "Drift between the sessions" );
      Paragraph( md, report, "drift" );
      md.AppendLine( "| unconfirmed order | ahead | behind | held in |" ).AppendLine( "|---|---|---|---|" );
      report.Drift.UnconfirmedOrders.ForEach( u => md.AppendLine( Row( ClaimMetrics.Label( u.Metric ), u.A, u.B, u.Session ) ) );
      md.AppendLine().AppendLine( "| close to the line | ahead | behind | lowest ratio, bp |" ).AppendLine( "|---|---|---|---|" );
      report.Drift.CloseToLine.ForEach( c => md.AppendLine( Row( ClaimMetrics.Label( c.Metric ), c.A, c.B, c.MinRatioBp.ToString( CultureInfo.InvariantCulture ) ) ) );
      md.AppendLine().AppendLine( "| on the line | ahead | behind | lowest ratio, bp |" ).AppendLine( "|---|---|---|---|" );
      report.Drift.OnLine.ForEach( c => md.AppendLine( Row( ClaimMetrics.Label( c.Metric ), c.A, c.B, c.MinRatioBp.ToString( CultureInfo.InvariantCulture ) ) ) );
      md.AppendLine();
   }

   /// <summary>
   /// The dropped mean-based clock warnings, quoted as the runs recorded them, with the recomputed medians.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void DroppedTable( StringBuilder md, ConsolidatedReport report )
   {
      if( report.Clock.DroppedWarnings.Count == 0 )
      {
         return;
      }

      md.AppendLine( "| run | recorded warning | engine median MHz | client median MHz |" ).AppendLine( "|---|---|---|---|" );
      report.Clock.DroppedWarnings.ForEach( d => md.AppendLine( Row( d.Run, d.Text, d.EngineMedianMhz?.ToString( CultureInfo.InvariantCulture ) ?? string.Empty, d.ClientMedianMhz?.ToString( CultureInfo.InvariantCulture ) ?? string.Empty ) ) );
      md.AppendLine();
   }

   /// <summary>
   /// The images and engine settings tables.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void SettingsTable( StringBuilder md, ConsolidatedReport report )
   {
      if( report.Images.Count > 0 )
      {
         md.AppendLine( "| engine | image | id | last tagged (UTC) |" ).AppendLine( "|---|---|---|---|" );
         report.Images.ForEach( i => md.AppendLine( Row( i.Target, i.Ref ?? string.Empty, i.Id, i.LastTagTimeUtc ?? string.Empty ) ) );
         md.AppendLine();
      }

      if( report.EngineSettings.Count > 0 )
      {
         md.AppendLine( "| engine | setting | value | how |" ).AppendLine( "|---|---|---|---|" );
         report.EngineSettings.ForEach( r => r.Settings.ForEach( s => md.AppendLine( Row( r.Target, s.Key, s.Value, s.How ) ) ) );
         md.AppendLine();
      }
   }

   /// <summary>
   /// The recorded texts the report prints, clause by clause: how each clause is backed and whether the report prints it.
   /// Why a table as well as the sentences: it lists every clause in one place, with the sources that back it, so a reader can check the classes without reading the sentences.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void ClausesTable( StringBuilder md, ConsolidatedReport report )
   {
      if( report.RecordedTexts.Count == 0 )
      {
         return;
      }

      md.AppendLine( "| text | clause | how it is backed | printed | basis |" ).AppendLine( "|---|---|---|---|---|" );
      foreach( RecordedTextRecord record in report.RecordedTexts )
      {
         string name = record.Target.Length == 0 ? $"run note {record.Note?.ToString( CultureInfo.InvariantCulture )}" : $"{record.Target} {record.Field}";
         foreach( RecordedClause clause in record.Clauses )
         {
            md.AppendLine( Row( name, clause.Text, clause.Unlisted ? clause.Class + " (not in the list)" : clause.Class, clause.Printed ? "yes" : "no: " + clause.NotPrinted, string.Join( "; ", clause.Basis ) ) );
         }
      }

      md.AppendLine();
   }

   /// <summary>
   /// The runs-used table.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The report.</param>
   private static void RunsTable( StringBuilder md, ConsolidatedReport report )
   {
      md.AppendLine( "| run | session | seed | started (UTC) |" ).AppendLine( "|---|---|---|---|" );
      foreach( SessionInfo s in report.Sessions )
      {
         s.Runs.ForEach( r => md.AppendLine( Row( r.Folder, s.Name, r.Seed?.ToString( CultureInfo.InvariantCulture ) ?? string.Empty, r.StartedUtc ?? string.Empty ) ) );
      }

      report.Basis.Runs.Where( b => report.Sessions.All( s => s.Name != b.Session ) ).ToList()
         .ForEach( b => md.AppendLine( Row( b.Folder, b.Session + " (basis)", b.Seed?.ToString( CultureInfo.InvariantCulture ) ?? string.Empty, b.StartedUtc ?? string.Empty ) ) );
      md.AppendLine();
   }

   /// <summary>
   /// A move cell.
   /// </summary>
   /// <param name="move">The move, or null.</param>
   /// <returns>"29.08% mongodb/oracle" or empty.</returns>
   private static string Move( BasisMove? move )
   {
      return move == null ? string.Empty : $"{ConsolidateSources.Percent( move.MoveBp )}% {move.Target ?? move.Pair}";
   }

   /// <summary>
   /// A cost cell.
   /// </summary>
   /// <param name="costs">Costs by level.</param>
   /// <param name="level">Level key.</param>
   /// <returns>The figure with three decimals, or empty.</returns>
   private static string Cost( Dictionary<string, double?> costs, string level )
   {
      return costs.TryGetValue( level, out double? v ) && v is double d ? ConsolidateSources.Number( d, 3 ) : string.Empty;
   }

   /// <summary>
   /// One table row; a '|' inside a cell becomes '/', so no cell can split.
   /// </summary>
   /// <param name="cells">Cells.</param>
   /// <returns>The row.</returns>
   private static string Row( params string[] cells )
   {
      return "| " + string.Join( " | ", cells.Select( c => c.Replace( '|', '/' ).Replace( '\n', ' ' ) ) ) + " |";
   }

   #endregion Private Methods
}
