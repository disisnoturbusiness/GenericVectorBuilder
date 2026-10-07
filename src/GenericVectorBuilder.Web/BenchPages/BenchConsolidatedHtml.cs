using System.Text;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// Prints a <see cref="BenchConsolidated"/> as the benchmark page: the headline and subtitle, the four metric
/// tables in the order the file gives, each row naming the engines it is not separated from, the flags, the
/// recall table, the facts and costs table, the threshold and its basis, the drift between sessions, the
/// disclosures, the runs used, and every other sentence of the file in its section.
/// Why this shape: the page states no fact of its own. Every sentence is one the file carries with its
/// sources, and each is printed once, in the section its slot names; the words the page adds are the fixed
/// headings and legends of <see cref="BenchLegends"/>, which hold no number, engine name, cause or purpose.
/// Why rows are never re-sorted: the file's order is the median order, and sorting by a figure would put the
/// latency tables upside down and turn "not separated" neighbours into a ranking.
/// A stopped set prints its stopped block and its sentences, and no headline and no tables.
/// Every name and number is HTML-encoded; the only markup is this class's own.
/// </summary>
public static class BenchConsolidatedHtml
{
   #region Data Members

   /// <summary>Longest source value printed whole; a longer one is cut and ends in an ellipsis (the whole value is in the raw file).</summary>
   public const int MAX_SOURCE_VALUE = 400;

   private const string MARK_STYLE = "display:inline-block;margin-left:4px;padding:0 5px;border:1px solid var(--warn);border-radius:8px;color:var(--warn);font-size:11px;font-weight:700;line-height:16px;cursor:help;text-decoration:none;vertical-align:1px";

   private static readonly Dictionary<string, string> MACHINE_LABELS = new( StringComparer.Ordinal )
   {
      ["cpu"] = "CPU",
      ["logicalCpus"] = "Logical CPUs",
      ["ramGiB"] = "RAM (GiB)",
      ["os"] = "OS",
      ["governor"] = "Governor",
      ["partition"] = "Partition",
      ["host"] = "Host",
      ["gpu"] = "GPU",
      ["dotNet"] = ".NET",
   };

   private static readonly Dictionary<string, string> METRIC_HEADINGS = new( StringComparer.Ordinal )
   {
      ["p50Ms"] = BenchLegends.H_P50,
      ["qps1"] = BenchLegends.H_QPS1,
      ["qps8"] = BenchLegends.H_QPS8,
      ["exactP50Ms"] = BenchLegends.H_EXACT,
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The whole block for one consolidated file.
   /// </summary>
   /// <param name="model">The file, read and checked.</param>
   /// <returns>HTML fragment.</returns>
   public static string Block( BenchConsolidated model )
   {
      var placed = new HashSet<BenchSentence>();
      var html = new StringBuilder( "<section class=\"bench-summary\">" );
      if( model.Stopped )
      {
         AppendStopped( html, model, placed );
         foreach( string hidden in new[] { BenchSlots.HEADLINE, BenchSlots.TABLE, BenchSlots.RECALL, BenchSlots.WHY } )
         {
            _ = Take( model, placed, hidden );
         }
      }
      else
      {
         AppendHeadline( html, model, placed );
         AppendTables( html, model, placed );
         AppendRecall( html, model, placed );
         AppendWhy( html, model, placed );
      }

      AppendThreshold( html, model, placed );
      AppendDrift( html, model, placed );
      AppendDisclosures( html, model, placed );
      AppendRuns( html, model, placed );
      AppendSection( html, model, placed, BenchSlots.METHOD, null );
      AppendUnplaced( html, model, placed );
      AppendUnread( html, model );
      AppendAudit( html, model );
      return html.Append( "</section>" ).ToString();
   }

   /// <summary>
   /// One sentence as the page prints it: the text, then its sources in a closed details so the page works
   /// without hover. A recorded text is printed as a quotation.
   /// </summary>
   /// <param name="sentence">The sentence.</param>
   /// <param name="css">Class of the paragraph (for a lead sentence).</param>
   /// <returns>HTML fragment.</returns>
   public static string Sentence( BenchSentence sentence, string css = "bench-sentence" )
   {
      string text = sentence.Quote ? $"<blockquote class=\"bench-quote\">{BenchFormat.Enc( sentence.Text )}</blockquote>" : $"<p class=\"{css}\">{BenchFormat.Enc( sentence.Text )}</p>";
      var html = new StringBuilder( $"<div class=\"bench-sentence-block\" data-slot=\"{BenchFormat.Enc( sentence.Slot )}\">{text}" );
      html.Append( sentence.Sources.Count == 0 ? "<p class=\"muted bench-sources\">no sources recorded</p>" : SourceList( sentence.Sources, sentence.Quote ? sentence.Text : null ) );
      return html.Append( "</div>" ).ToString();
   }

   /// <summary>
   /// A list of sources in a closed details, so the page works without hover: the kind, the place and the value each source gives.
   /// </summary>
   /// <param name="sources">The sources (at least one).</param>
   /// <param name="quoted">The text of a quotation the sources belong to, so a source that only repeats it is printed without the value; null for none.</param>
   /// <returns>HTML fragment.</returns>
   public static string SourceList( IReadOnlyList<BenchSource> sources, string? quoted = null )
   {
      var html = new StringBuilder( "<details class=\"bench-sources muted\"><summary>sources</summary><ul>" );
      foreach( BenchSource source in sources )
      {
         string value = source.Value.Length > MAX_SOURCE_VALUE ? source.Value[..MAX_SOURCE_VALUE] + "..." : source.Value;
         bool echoesTheText = quoted != null && source.Value == quoted;
         html.Append( echoesTheText
            ? $"<li>{BenchFormat.Enc( source.Kind )} <code>{BenchFormat.Enc( source.Ref )}</code></li>"
            : $"<li>{BenchFormat.Enc( source.Kind )} <code>{BenchFormat.Enc( source.Ref )}</code> = {BenchFormat.Enc( value )}</li>" );
      }

      return html.Append( "</ul></details>" ).ToString();
   }

   /// <summary>
   /// One metric table.
   /// </summary>
   /// <param name="metric">The metric.</param>
   /// <param name="sessions">The sessions, in file order, for the per-session columns.</param>
   /// <param name="why">The facts table, so the search column can print the label of the fact behind each mode; null for none.</param>
   /// <returns>HTML fragment.</returns>
   public static string MetricTable( BenchMetric metric, IReadOnlyList<BenchSession> sessions, IReadOnlyList<BenchWhy>? why = null )
   {
      bool search = metric.Rows.Any( r => r.SearchMode != null );
      var names = metric.Rows.ToDictionary( r => r.Target, r => r.Display, StringComparer.Ordinal );
      var html = new StringBuilder( $"<h2 class=\"bench-metric\" data-metric=\"{BenchFormat.Enc( metric.Metric )}\">{BenchFormat.Enc( Heading( metric.Metric ) )}</h2>" );
      html.Append( "<div class=\"preview\"><table class=\"bench-table\"><thead><tr><th>Engine</th>" );
      foreach( BenchSession session in sessions )
      {
         html.Append( $"<th class=\"n\">{BenchFormat.Enc( session.Name )} min to max</th>" );
      }

      html.Append( $"<th class=\"n\">Median of all runs</th>{( search ? $"<th data-column=\"search\">{BenchFormat.Enc( BenchLegends.H_SEARCH_MODE )}</th>" : string.Empty )}<th>Not separated from</th><th>Flags</th></tr></thead><tbody>" );
      foreach( BenchRow row in metric.Rows )
      {
         html.Append( $"<tr data-target=\"{BenchFormat.Enc( row.Target )}\"><td>{BenchFormat.Enc( row.Display )}{Note( row )}</td>" );
         foreach( BenchSession session in sessions )
         {
            html.Append( $"<td class=\"n\">{BenchFormat.Enc( Range( row, session.Name, metric.LowerIsBetter ) )}</td>" );
         }

         html.Append( $"<td class=\"n\">{BenchFormat.Enc( row.Median == null ? "-" : BenchFormat.Figure( row.Median.Value, metric.LowerIsBetter ) )}</td>" );
         html.Append( search ? $"<td data-column=\"search\">{SearchCell( row, why )}</td>" : string.Empty );
         html.Append( $"<td>{Separation( row, names )}</td><td>{Markers( row )}</td></tr>" );
      }

      return html.Append( "</tbody></table></div>" ).ToString();
   }

   /// <summary>
   /// The small markers after an engine's name, one per flag code, each with its evidence as hover text.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <returns>HTML fragment; empty when the row has no flag.</returns>
   public static string Markers( BenchRow row )
   {
      var html = new StringBuilder();
      foreach( IGrouping<string, BenchFlagEntry> group in row.Flags.GroupBy( f => f.Code ).OrderBy( g => BenchLegends.Order( g.Key ) ) )
      {
         string evidence = string.Join( "\n", group.Select( f => f.Text.Length == 0 ? f.Code : $"{f.Code}: {f.Text}" ) );
         html.Append( $"<abbr class=\"bench-mark\" style=\"{MARK_STYLE}\" title=\"{BenchFormat.Enc( evidence )}\" aria-label=\"{BenchFormat.Enc( evidence )}\">{BenchFormat.Enc( BenchLegends.Marker( group.Key ) )}</abbr>" );
      }

      return html.ToString();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The lead of the page: the headline, the subtitle and the machine line.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   private static void AppendHeadline( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed )
   {
      foreach( BenchSentence headline in Take( model, placed, BenchSlots.HEADLINE ) )
      {
         html.Append( Sentence( headline, "bench-lead" ) );
      }

      foreach( BenchSentence subtitle in Take( model, placed, BenchSlots.SUBTITLE ) )
      {
         html.Append( Sentence( subtitle, "bench-data muted" ) );
      }

      html.Append( MachineLine( model ) );
   }

   /// <summary>
   /// The machine as label and value pairs in one line, then one line of clock fields for each session; nothing when the file has no machine block.
   /// </summary>
   /// <param name="model">The file.</param>
   /// <returns>HTML fragment.</returns>
   private static string MachineLine( BenchConsolidated model )
   {
      if( model.Machine.Count == 0 )
      {
         return string.Empty;
      }

      string pairs = string.Join( "; ", model.Machine.Where( p => p.Value.Length > 0 ).Select( p => $"{BenchFormat.Enc( MACHINE_LABELS.GetValueOrDefault( p.Key, p.Key ) )} {BenchFormat.Enc( p.Value )}" ) );
      var html = new StringBuilder( $"<p class=\"bench-conditions muted\">Machine: {pairs}</p>" );
      foreach( ( string session, IReadOnlyList<BenchPair> fields ) in model.Clock?.PerSession ?? new List<( string, IReadOnlyList<BenchPair> )>() )
      {
         html.Append( $"<p class=\"bench-conditions muted\">Clock {BenchFormat.Enc( session )}: {string.Join( "; ", fields.Select( ClockField ) )}</p>" );
      }

      return html.ToString();
   }

   /// <summary>
   /// One clock field as the machine line prints it: the field's name and value as the file gives them, and, for the field that says the clock came from the
   /// runs' notes and not from a clock block, what that means.
   /// </summary>
   /// <param name="field">The field.</param>
   /// <returns>HTML fragment.</returns>
   private static string ClockField( BenchPair field )
   {
      string text = $"{BenchFormat.Enc( field.Key )} {BenchFormat.Enc( field.Value.Length == 0 ? "not recorded" : field.Value )}";
      return field.Key == "legacyParsed" && field.Value == "true" ? $"{text} {BenchFormat.Enc( BenchLegends.L_LEGACY_PARSED )}" : text;
   }

   /// <summary>
   /// The four metric tables with the sentences of each, the row-order legend, and the flags list.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   private static void AppendTables( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed )
   {
      foreach( BenchMetric metric in model.Metrics )
      {
         html.Append( MetricTable( metric, model.Sessions, model.Why ) );
         AppendSentences( html, Take( model, placed, BenchSlots.TABLE, metric.Metric ) );
      }

      html.Append( $"<p class=\"bench-legend muted\">{BenchFormat.Enc( BenchLegends.ROW_ORDER )} {BenchFormat.Enc( BenchLegends.ROW_SEPARATION )}</p>" );
      if( model.Metrics.Any( m => m.Rows.Any( r => r.SearchMode != null ) ) )
      {
         html.Append( $"<p class=\"bench-legend muted\">{BenchFormat.Enc( model.Why.Count > 0 ? BenchLegends.SEARCH_COLUMN : BenchLegends.SEARCH_COLUMN_BARE )}</p>" );
      }

      if( model.Metrics.Any( m => m.Rows.Any( r => r.Status == "one-session" ) ) )
      {
         html.Append( $"<p class=\"bench-legend muted\">{BenchFormat.Enc( BenchLegends.ONE_SESSION )}</p>" );
      }

      html.Append( $"<p class=\"bench-legend muted\">{BenchFormat.Enc( BenchLegends.DASH )}</p>" );
      AppendSentences( html, Take( model, placed, BenchSlots.TABLE ) );
      html.Append( FlagsList( model ) );
      if( model.Guards.G3Targets.Count > 0 )
      {
         html.Append( SetupDiffers( model ) );
      }
   }

   /// <summary>
   /// The flags section: what each marker code on any table means and the evidence of every flag, row by row.
   /// </summary>
   /// <param name="model">The file.</param>
   /// <returns>HTML fragment; empty when no row has a flag.</returns>
   private static string FlagsList( BenchConsolidated model )
   {
      var all = model.Metrics.SelectMany( m => m.Rows.SelectMany( r => r.Flags.Select( f => ( Metric: m.Metric, Row: r, Flag: f ) ) ) ).ToList();
      if( all.Count == 0 )
      {
         return string.Empty;
      }

      var html = new StringBuilder( $"<h2>{BenchLegends.H_FLAGS}</h2><p class=\"muted\">{BenchFormat.Enc( BenchLegends.FLAGS_INTRO )}</p><ul>" );
      foreach( IGrouping<string, ( string Metric, BenchRow Row, BenchFlagEntry Flag )> group in all.GroupBy( x => BenchLegends.Marker( x.Flag.Code ) + "|" + BenchLegends.Legend( x.Flag.Code ) )
         .OrderBy( g => g.Min( x => BenchLegends.Order( x.Flag.Code ) ) ) )
      {
         int engines = group.Select( x => x.Row.Target ).Distinct( StringComparer.Ordinal ).Count();
         string codes = string.Join( ", ", group.Select( x => x.Flag.Code ).Distinct( StringComparer.Ordinal ).OrderBy( BenchLegends.Order ).Select( c => $"<code>{BenchFormat.Enc( c )}</code>" ) );
         string first = group.First().Flag.Code;
         html.Append( $"<li><abbr class=\"bench-mark\" style=\"{MARK_STYLE}\">{BenchFormat.Enc( BenchLegends.Marker( first ) )}</abbr> {codes} {BenchFormat.Enc( BenchLegends.Legend( first ) )} <span class=\"muted\">({engines} of {AllTargets( model )} engines)</span></li>" );
      }

      html.Append( $"</ul><details class=\"bench-evidence\"><summary>{BenchFormat.Enc( BenchLegends.L_EVIDENCE )}</summary><ul>" );
      foreach( ( string metric, BenchRow row, BenchFlagEntry flag ) in all )
      {
         html.Append( $"<li><strong>{BenchFormat.Enc( row.Display )}</strong> <code>{BenchFormat.Enc( metric )}</code> <code>{BenchFormat.Enc( flag.Code )}</code> {BenchFormat.Enc( flag.Text )}</li>" );
      }

      return html.Append( "</ul></details>" ).ToString();
   }

   /// <summary>
   /// The recall table: hits per run for each session, and whether any two runs differ.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   private static void AppendRecall( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed )
   {
      if( model.Recall.Count == 0 )
      {
         AppendSection( html, model, placed, BenchSlots.RECALL, null );
         return;
      }

      html.Append( $"<h2>{BenchLegends.H_RECALL}</h2><div class=\"preview\"><table class=\"bench-table\"><thead><tr><th>Engine</th>" );
      foreach( BenchSession session in model.Sessions )
      {
         html.Append( $"<th>{BenchFormat.Enc( session.Name )} hits per run</th>" );
      }

      html.Append( "<th>Differs between runs</th></tr></thead><tbody>" );
      foreach( BenchRecall recall in model.Recall )
      {
         html.Append( $"<tr data-target=\"{BenchFormat.Enc( recall.Target )}\"><td>{BenchFormat.Enc( DisplayOf( model, recall.Target ) )}</td>" );
         foreach( BenchSession session in model.Sessions )
         {
            string cell = recall.Hits.TryGetValue( session.Name, out IReadOnlyList<long>? hits ) ? string.Join( ", ", hits ) + " of " + recall.Of : "-";
            html.Append( $"<td>{BenchFormat.Enc( cell )}</td>" );
         }

         html.Append( $"<td{( recall.Differs ? " class=\"bench-no\"" : string.Empty )}>{( recall.Differs ? "yes" : "no" )}</td></tr>" );
      }

      html.Append( "</tbody></table></div>" );
      AppendSection( html, model, placed, BenchSlots.RECALL, null );
   }

   /// <summary>
   /// The facts and costs table, one row per engine in the file's order, with the framing sentences under it.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   private static void AppendWhy( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed )
   {
      if( model.Why.Count == 0 )
      {
         AppendSection( html, model, placed, BenchSlots.WHY, null );
         return;
      }

      Dictionary<string, List<BenchSentence>> settings = TakeByTarget( model, placed, "settings" );
      Dictionary<string, List<BenchSentence>> caveats = TakeByTarget( model, placed, "effort" );
      html.Append( $"<h2>{BenchLegends.H_WHY}</h2>" );
      AppendSentences( html, Take( model, placed, BenchSlots.WHY ) );

      html.Append( $"<div class=\"preview\"><table class=\"bench-table\"><thead><tr><th>Engine</th><th>Facts</th><th>{BenchFormat.Enc( BenchLegends.H_SEARCH_EFFORT )}</th><th class=\"n\">{BenchFormat.Enc( BenchLegends.H_ENGINE_CPU_1 )}</th><th class=\"n\">{BenchFormat.Enc( BenchLegends.H_ENGINE_CPU_8 )}</th>" );
      html.Append( $"<th class=\"n\">{BenchFormat.Enc( BenchLegends.H_CLIENT_CPU_1 )}</th><th class=\"n\">{BenchFormat.Enc( BenchLegends.H_CLIENT_CPU_8 )}</th><th class=\"n\">{BenchFormat.Enc( BenchLegends.H_ENGINE_CPUS_BUSY )}</th></tr></thead><tbody>" );
      foreach( BenchWhy why in model.Why )
      {
         html.Append( $"<tr data-target=\"{BenchFormat.Enc( why.Target )}\"><td>{BenchFormat.Enc( DisplayOf( model, why.Target ) )}</td><td>{Facts( why )}</td><td data-column=\"effort\">{Effort( why, settings.GetValueOrDefault( why.Target ), caveats.GetValueOrDefault( why.Target ) )}</td>" );
         html.Append( $"<td class=\"n\">{BenchFormat.Enc( BenchFormat.Ms( why.Costs.EngineCpuMs1 ) )}</td><td class=\"n\">{BenchFormat.Enc( BenchFormat.Ms( why.Costs.EngineCpuMs8 ) )}</td>" );
         html.Append( $"<td class=\"n\">{BenchFormat.Enc( BenchFormat.Ms( why.Costs.ClientCpuMs1 ) )}</td><td class=\"n\">{BenchFormat.Enc( BenchFormat.Ms( why.Costs.ClientCpuMs8 ) )}</td>" );
         html.Append( $"<td class=\"n\">{BenchFormat.Enc( BenchFormat.Ms( why.Costs.EngineCpusBusyAt8 ) )}</td></tr>" );
      }

      html.Append( "</tbody></table></div>" );
      AppendEffortStatements( html, model, settings );
   }

   /// <summary>
   /// Takes the sentences of "why.KIND.TARGET" for each engine of the facts and costs table (KIND "settings" or "effort"), so the table can print them in the row
   /// of their engine and none is left for the framing sentences above it.
   /// </summary>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   /// <param name="kind">The second part of the slot.</param>
   /// <returns>The sentences by target; a target with none is absent.</returns>
   private static Dictionary<string, List<BenchSentence>> TakeByTarget( BenchConsolidated model, HashSet<BenchSentence> placed, string kind )
   {
      var byTarget = new Dictionary<string, List<BenchSentence>>( StringComparer.Ordinal );
      foreach( BenchWhy why in model.Why )
      {
         List<BenchSentence> taken = Take( model, placed, BenchSlots.WHY, $"{kind}.{why.Target}" ).ToList();
         if( taken.Count > 0 )
         {
            byTarget[why.Target] = taken;
         }
      }

      return byTarget;
   }

   /// <summary>
   /// The statements about each engine's search settings that the table's column does not print (those of an engine that has the settings as a list), in a
   /// closed block with their sources, so each is printed once and a reader can open the sources.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="settings">The settings statements by target.</param>
   private static void AppendEffortStatements( StringBuilder html, BenchConsolidated model, Dictionary<string, List<BenchSentence>> settings )
   {
      List<BenchSentence> behind = model.Why.Where( w => w.SearchEffort.Count > 0 ).SelectMany( w => settings.GetValueOrDefault( w.Target ) ?? new List<BenchSentence>() ).ToList();
      if( behind.Count == 0 )
      {
         return;
      }

      html.Append( $"<details class=\"bench-target\"><summary>{BenchFormat.Enc( BenchLegends.H_EFFORT_STATEMENTS )}</summary>" );
      foreach( BenchSentence sentence in behind )
      {
         html.Append( Sentence( sentence ) );
      }

      html.Append( "</details>" );
   }

   /// <summary>
   /// The settings that control the effort of each search of one engine, one per line with where the file says they come from, then the caveats the file states
   /// for them; for an engine with no list, the file's statement of its settings in place of the list; a dash when the file gives nothing.
   /// </summary>
   /// <param name="why">The row.</param>
   /// <param name="settings">The file's statements of the engine's settings, or null.</param>
   /// <param name="caveats">The file's caveats on the engine's search effort, or null.</param>
   /// <returns>HTML fragment.</returns>
   private static string Effort( BenchWhy why, List<BenchSentence>? settings, List<BenchSentence>? caveats )
   {
      var html = new StringBuilder();
      if( why.SearchEffort.Count > 0 )
      {
         html.Append( "<ul class=\"bench-facts\">" );
         foreach( BenchEffort setting in why.SearchEffort )
         {
            html.Append( $"<li><code>{BenchFormat.Enc( setting.Text )}</code>{( setting.Source == null ? string.Empty : $" <span class=\"muted\">(<code>{BenchFormat.Enc( setting.Source )}</code>)</span>" )}</li>" );
         }

         html.Append( "</ul>" );
      }
      else
      {
         settings?.ForEach( s => html.Append( Sentence( s ) ) );
      }

      caveats?.ForEach( s => html.Append( Sentence( s ) ) );
      return html.Length == 0 ? "-" : html.ToString();
   }

   /// <summary>
   /// The facts of one why row, each with its kind, confidence and source.
   /// </summary>
   /// <param name="why">The row.</param>
   /// <returns>HTML fragment.</returns>
   private static string Facts( BenchWhy why )
   {
      if( why.Facts.Count == 0 )
      {
         return "-";
      }

      var html = new StringBuilder( "<ul class=\"bench-facts\">" );
      foreach( BenchFact fact in why.Facts )
      {
         string kind = fact.Mode == null ? fact.Kind : $"{fact.Kind} ({fact.Mode})";
         string where = fact.Where == null ? string.Empty : $"; {BenchFormat.Enc( fact.Where )}";
         html.Append( $"<li data-kind=\"{BenchFormat.Enc( fact.Kind )}\"><strong>{BenchFormat.Enc( kind )}</strong> {BenchFormat.Enc( fact.Text )} <span class=\"muted\">(<span class=\"bench-label\">{BenchFormat.Enc( fact.Confidence )}</span>; <code>{BenchFormat.Enc( fact.Source )}</code>{where})</span></li>" );
      }

      return html.Append( "</ul>" ).ToString();
   }

   /// <summary>
   /// The stopped block: the fixed notice, the pairs the file names and the stopped sentences.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   private static void AppendStopped( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed )
   {
      html.Append( $"<aside class=\"bench-caveats\"><h2>{BenchLegends.H_STOPPED}</h2><p>{BenchFormat.Enc( BenchLegends.STOPPED )}</p>" );
      AppendSentences( html, Take( model, placed, BenchSlots.STOPPED ) );

      if( model.Guards.StoppedBy is { Count: > 0 } stoppedBy )
      {
         html.Append( $"<p class=\"muted\">{BenchFormat.Enc( BenchLegends.STOPPED_IN )} {string.Join( ", ", stoppedBy.Select( f => $"<code>{BenchFormat.Enc( f )}</code>" ) )}</p>" );
      }

      if( model.Guards.G2Pairs.Count > 0 )
      {
         html.Append( "<ul>" );
         foreach( string pair in model.Guards.G2Pairs )
         {
            html.Append( $"<li>{BenchFormat.Enc( pair )}</li>" );
         }

         html.Append( "</ul>" );
      }

      html.Append( "</aside>" );
      foreach( BenchSentence sentence in Take( model, placed, BenchSlots.SUBTITLE ) )
      {
         html.Append( Sentence( sentence, "bench-data muted" ) );
      }

      html.Append( MachineLine( model ) );
   }

   /// <summary>
   /// The threshold and its basis: the threshold figures, the rule and basis sentences, and the basis
   /// figures (largest moves, runs, exclusions, folders left out) in a closed details.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   private static void AppendThreshold( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed )
   {
      html.Append( $"<h2>{BenchLegends.H_THRESHOLD}</h2>" );
      html.Append( $"<p class=\"bench-conditions muted\">Threshold {BenchFormat.Enc( BenchFormat.Percent( model.TBp ) )}; basis points {BenchFormat.Enc( model.TBp.ToString( System.Globalization.CultureInfo.InvariantCulture ) )}; ratio {BenchFormat.Enc( model.Ratio.ToString( "0.####", System.Globalization.CultureInfo.InvariantCulture ) )}</p>" );
      foreach( string section in new[] { BenchSlots.RULE, BenchSlots.BASIS } )
      {
         AppendSentences( html, Take( model, placed, section ) );
      }

      if( model.Basis != null )
      {
         html.Append( SetupSplits( model, model.Basis ) );
         html.Append( BasisFigures( model.Basis ) );
      }
   }

   /// <summary>
   /// The changes of an engine's recorded setup that the basis does not compare across, as a table: the engine, each field that changed with the text before and
   /// after, the runs on each side, the largest one-engine and pair moves the change hides, and the threshold the basis would give with the change counted.
   /// Why drawn in the open: the threshold, and with it every ahead-of pair, depends on which changes the basis leaves out, so a reader must see them without a click.
   /// </summary>
   /// <param name="model">The file, for display names.</param>
   /// <param name="basis">The basis.</param>
   /// <returns>HTML fragment; empty when the file lists no change.</returns>
   private static string SetupSplits( BenchConsolidated model, BenchBasis basis )
   {
      if( basis.Splits.Count == 0 )
      {
         return string.Empty;
      }

      var html = new StringBuilder( $"<h3>{BenchFormat.Enc( BenchLegends.H_SETUP_SPLITS )}</h3><div class=\"preview\"><table class=\"bench-table\" data-table=\"setup-splits\"><thead><tr><th>Engine</th><th>What changed</th>" );
      html.Append( "<th>Runs before the change</th><th>Runs after the change</th><th>Largest one-engine move across the change</th><th>Largest pair move across the change</th><th class=\"n\">Threshold if counted</th></tr></thead><tbody>" );
      foreach( BenchSetupSplit split in basis.Splits )
      {
         html.Append( $"<tr data-target=\"{BenchFormat.Enc( split.Target )}\"><td>{BenchFormat.Enc( DisplayOf( model, split.Target ) )}</td><td>{SplitChanges( split )}</td>" );
         html.Append( $"<td>{SplitRuns( split.BeforeSessions, split.BeforeRuns )}</td><td>{SplitRuns( split.AfterSessions, split.AfterRuns )}</td>" );
         html.Append( $"<td>{SplitMove( split.OneEngine )}</td><td>{SplitMove( split.Pair )}</td>" );
         html.Append( $"<td class=\"n\">{BenchFormat.Enc( split.TBpIfCounted == null ? "-" : BenchFormat.Percent( split.TBpIfCounted.Value ) )}</td></tr>" );
      }

      return html.Append( "</tbody></table></div>" ).ToString();
   }

   /// <summary>
   /// The fields that changed in one setup split, each with the text the earlier runs recorded and the text the later runs recorded.
   /// </summary>
   /// <param name="split">The split.</param>
   /// <returns>HTML fragment.</returns>
   private static string SplitChanges( BenchSetupSplit split )
   {
      var html = new StringBuilder( "<ul class=\"bench-facts\">" );
      foreach( BenchSetupChange change in split.Changes )
      {
         html.Append( $"<li><code>{BenchFormat.Enc( change.Field )}</code><br><span class=\"muted\">{BenchFormat.Enc( BenchLegends.L_BEFORE )}</span> {BenchFormat.Enc( change.Before.Length == 0 ? "not recorded" : change.Before )}" );
         html.Append( $"<br><span class=\"muted\">{BenchFormat.Enc( BenchLegends.L_AFTER )}</span> {BenchFormat.Enc( change.After.Length == 0 ? "not recorded" : change.After )}</li>" );
      }

      return html.Append( "</ul>" ).ToString();
   }

   /// <summary>
   /// The sessions and runs on one side of a setup split: the sessions' names and the number of runs, with the runs themselves as links in a closed details.
   /// </summary>
   /// <param name="sessions">The sessions.</param>
   /// <param name="runs">The runs.</param>
   /// <returns>HTML fragment.</returns>
   private static string SplitRuns( IReadOnlyList<string> sessions, IReadOnlyList<string> runs )
   {
      string head = $"{BenchFormat.Enc( sessions.Count == 0 ? "-" : string.Join( ", ", sessions ) )}, {runs.Count} {( runs.Count == 1 ? "run" : "runs" )}";
      return runs.Count == 0 ? head : $"{head}<details class=\"bench-sources muted\"><summary>runs</summary>{string.Join( ", ", runs.Select( RunLink ) )}</details>";
   }

   /// <summary>
   /// One move a setup split hides: its metric, size and engine or pair, then the two runs, the two figures and the recorded differences between the runs.
   /// </summary>
   /// <param name="move">The move, or null.</param>
   /// <returns>HTML fragment; a dash when the split has none of that kind.</returns>
   private static string SplitMove( BenchSplitMove? move )
   {
      if( move == null )
      {
         return "-";
      }

      var html = new StringBuilder( $"{BenchFormat.Enc( Heading( move.Metric ) )}: {BenchFormat.Enc( BenchFormat.Percent( move.Move.MoveBp ) )}, {BenchFormat.Enc( move.Move.Who )}" );
      html.Append( $"<br><small class=\"muted\">runs {BenchFormat.Enc( string.Join( ", ", move.Move.Runs ) )}; values {BenchFormat.Enc( string.Join( ", ", move.Move.Values ) )}</small>" );
      if( move.Move.Differences.Count > 0 )
      {
         html.Append( $"<details class=\"bench-sources muted\"><summary>differences between the two runs</summary><ul>{string.Concat( move.Move.Differences.Select( d => $"<li>{BenchFormat.Enc( d )}</li>" ) )}</ul></details>" );
      }

      return html.ToString();
   }

   /// <summary>
   /// The basis figures as tables in a closed details.
   /// </summary>
   /// <param name="basis">The basis.</param>
   /// <returns>HTML fragment.</returns>
   private static string BasisFigures( BenchBasis basis )
   {
      var html = new StringBuilder( $"<details class=\"bench-target\"><summary>{BenchLegends.H_BASIS_FIGURES}</summary>" );
      html.Append( "<div class=\"preview\"><table class=\"bench-table\"><thead><tr><th>Kind</th><th>Metric</th><th class=\"n\">Move</th><th>Engines</th><th>Runs</th><th>Values</th><th>Differences</th></tr></thead><tbody>" );
      var printed = new HashSet<string>( StringComparer.Ordinal );
      foreach( BenchBasisMoves group in basis.Moves.Concat( basis.NoMachineControl.Select( g => g with { Kind = g.Kind + " without machine control" } ) ) )
      {
         foreach( ( string metric, BenchMove move ) in group.Moves )
         {
            if( !printed.Add( $"{group.Kind}|{metric}|{move.MoveBp}|{move.Who}|{string.Join( ",", move.Runs )}|{string.Join( ",", move.Values )}|{string.Join( ";", move.Differences )}" ) )
            {
               continue;
            }

            html.Append( $"<tr><td>{BenchFormat.Enc( group.Kind )}</td><td>{BenchFormat.Enc( metric.Length == 0 ? "-" : metric )}</td><td class=\"n\">{BenchFormat.Enc( BenchFormat.Percent( move.MoveBp ) )}</td><td>{BenchFormat.Enc( move.Who )}</td>" );
            html.Append( $"<td>{BenchFormat.Enc( string.Join( ", ", move.Runs ) )}</td><td>{BenchFormat.Enc( string.Join( ", ", move.Values ) )}</td><td>{BenchFormat.Enc( string.Join( "; ", move.Differences ) )}</td></tr>" );
         }
      }

      html.Append( "</tbody></table></div>" );
      html.Append( Records( BenchLegends.L_EXCLUSIONS, basis.Exclusions ) ).Append( Records( BenchLegends.L_LEFT_OUT, basis.LeftOut ) );
      html.Append( Records( BenchLegends.L_BASIS_RUNS, basis.Runs.Select( r => BasisRunFields( r ) ).ToList() ) );
      return html.Append( "</details>" ).ToString();
   }

   /// <summary>
   /// One basis run as its fields: the folder, the seed and the session when the file gives them, then the recorded conditions.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>The fields in the order printed.</returns>
   private static IReadOnlyList<BenchPair> BasisRunFields( BenchBasisRun run )
   {
      var fields = new List<BenchPair> { new( "folder", run.Folder ) };
      if( run.Seed != null )
      {
         fields.Add( new BenchPair( "seed", run.Seed.Value.ToString( System.Globalization.CultureInfo.InvariantCulture ) ) );
      }

      if( run.Session != null )
      {
         fields.Add( new BenchPair( "session", run.Session ) );
      }

      if( run.StartedUtc != null )
      {
         fields.Add( new BenchPair( "startedUtc", run.StartedUtc ) );
      }

      fields.AddRange( run.Conditions );
      return fields;
   }

   /// <summary>
   /// A list of records as a table whose columns are the union of their fields in first-seen order.
   /// </summary>
   /// <param name="title">Table title (a fixed label).</param>
   /// <param name="records">The records.</param>
   /// <returns>HTML fragment; empty when there are none.</returns>
   private static string Records( string title, IReadOnlyList<IReadOnlyList<BenchPair>> records )
   {
      if( records.Count == 0 )
      {
         return string.Empty;
      }

      List<string> columns = records.SelectMany( r => r.Select( p => p.Key ) ).Distinct( StringComparer.Ordinal ).ToList();
      var html = new StringBuilder( title.Length == 0 ? string.Empty : $"<h3>{BenchFormat.Enc( title )}</h3>" );
      html.Append( "<div class=\"preview\"><table class=\"bench-table\"><thead><tr>" );
      foreach( string column in columns )
      {
         html.Append( $"<th>{BenchFormat.Enc( column )}</th>" );
      }

      html.Append( "</tr></thead><tbody>" );
      foreach( IReadOnlyList<BenchPair> record in records )
      {
         html.Append( "<tr>" );
         foreach( string column in columns )
         {
            html.Append( $"<td>{BenchFormat.Enc( record.FirstOrDefault( p => p.Key == column )?.Value ?? "-" )}</td>" );
         }

         html.Append( "</tr>" );
      }

      return html.Append( "</tbody></table></div>" ).ToString();
   }

   /// <summary>
   /// The drift between sessions: its sentences, the key figures, the per-engine moves and the orders
   /// the file lists as unconfirmed or close to the line.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   private static void AppendDrift( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed )
   {
      if( model.Drift == null )
      {
         AppendSection( html, model, placed, BenchSlots.DRIFT, null );
         return;
      }

      BenchDrift drift = model.Drift;
      html.Append( $"<h2>{BenchLegends.H_DRIFT}</h2>" );
      AppendSentences( html, Take( model, placed, BenchSlots.DRIFT ) );

      var facts = new List<string>
      {
         $"First session {BenchFormat.Enc( drift.From ?? "-" )}",
         $"second session {BenchFormat.Enc( drift.To ?? "-" )}",
         $"median absolute move {( drift.MedianAbsMoveBp == null ? "-" : BenchFormat.Enc( BenchFormat.Percent( drift.MedianAbsMoveBp.Value ) ) )}",
         $"largest move {BenchFormat.Enc( drift.Largest ?? "-" )}",
      };
      html.Append( $"<p class=\"bench-conditions muted\">{string.Join( "; ", facts )}</p>" );
      html.Append( DriftMatrix( model, drift ) );
      html.Append( PairList( BenchLegends.L_UNCONFIRMED, drift.UnconfirmedOrders, model, true ) );
      html.Append( PairList( BenchLegends.L_CLOSE, drift.CloseToLine, model, false ) );
      html.Append( PairList( BenchLegends.L_ON_LINE, drift.OnLinePairs, model, true ) );
   }

   /// <summary>
   /// Each engine's move per metric.
   /// </summary>
   /// <param name="model">The file.</param>
   /// <param name="drift">The drift.</param>
   /// <returns>HTML fragment; empty when there are no moves.</returns>
   private static string DriftMatrix( BenchConsolidated model, BenchDrift drift )
   {
      if( drift.PerTarget.Count == 0 )
      {
         return string.Empty;
      }

      List<string> metrics = drift.PerTarget.Select( e => e.Metric ).Distinct( StringComparer.Ordinal ).ToList();
      var html = new StringBuilder( "<div class=\"preview\"><table class=\"bench-table\"><thead><tr><th>Engine</th>" );
      foreach( string metric in metrics )
      {
         html.Append( $"<th class=\"n\">{BenchFormat.Enc( Heading( metric ) )}</th>" );
      }

      html.Append( "</tr></thead><tbody>" );
      foreach( string target in drift.PerTarget.Select( e => e.Target ).Distinct( StringComparer.Ordinal ) )
      {
         html.Append( $"<tr><td>{BenchFormat.Enc( DisplayOf( model, target ) )}</td>" );
         foreach( string metric in metrics )
         {
            BenchDriftEntry? entry = drift.PerTarget.FirstOrDefault( e => e.Target == target && e.Metric == metric );
            html.Append( $"<td class=\"n\">{BenchFormat.Enc( entry == null ? "-" : BenchFormat.Percent( entry.MoveBp ) )}</td>" );
         }

         html.Append( "</tr>" );
      }

      return html.Append( "</tbody></table></div>" ).ToString();
   }

   /// <summary>
   /// A list of engine pairs with their metric, the smallest ratio when the file gives one, and the session an order held in when the file gives one.
   /// A list of orders (the unconfirmed orders and the pairs on the line) says which engine is ahead of which, as the file's two fields (a is ahead, b is
   /// behind) do; a list of unordered pairs (close to the line) names the two without saying one is ahead.
   /// </summary>
   /// <param name="title">Fixed label.</param>
   /// <param name="pairs">The pairs.</param>
   /// <param name="model">The file, for display names.</param>
   /// <param name="ordered">True for a list of orders, whose first engine is ahead of the second.</param>
   /// <returns>HTML fragment; empty when there are none.</returns>
   private static string PairList( string title, IReadOnlyList<BenchPairRef> pairs, BenchConsolidated model, bool ordered )
   {
      if( pairs.Count == 0 )
      {
         return string.Empty;
      }

      var html = new StringBuilder( $"<h3>{BenchFormat.Enc( title )}</h3><ul>" );
      foreach( BenchPairRef pair in pairs )
      {
         string ratio = pair.MinRatioBp == null ? string.Empty : $", {BenchFormat.Enc( BenchLegends.L_MIN_RATIO )} {BenchFormat.Enc( ( pair.MinRatioBp.Value / 10000.0 ).ToString( "0.0000", System.Globalization.CultureInfo.InvariantCulture ) )}";
         string held = pair.Session == null ? string.Empty : $", {BenchFormat.Enc( BenchLegends.L_HELD_IN )} {BenchFormat.Enc( pair.Session )}";
         string link = ordered ? BenchLegends.L_AHEAD_OF : "and";
         html.Append( $"<li><code>{BenchFormat.Enc( pair.Metric )}</code> {BenchFormat.Enc( DisplayOf( model, pair.A ) )} {BenchFormat.Enc( link )} {BenchFormat.Enc( DisplayOf( model, pair.B ) )}{ratio}{held}</li>" );
      }

      return html.Append( "</ul>" ).ToString();
   }

   /// <summary>
   /// The disclosures: their sentences, then the clock, the dropped warnings and the images.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   private static void AppendDisclosures( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed )
   {
      html.Append( $"<h2>{BenchLegends.H_DISCLOSURES}</h2>" );
      AppendSentences( html, Take( model, placed, BenchSlots.DISCLOSURE ) );

      if( model.Clock != null )
      {
         html.Append( DroppedWarnings( model.Clock ) );
      }

      if( model.Images.Count > 0 )
      {
         html.Append( ImagesTable( model, model.Images ) );
      }

      html.Append( EngineSettingsTable( model ) );

      AppendSection( html, model, placed, BenchSlots.NOT_IN_REPORT, BenchLegends.H_NOT_IN_REPORT );
   }

   /// <summary>
   /// The clock warnings the set dropped, with the recomputed median figures, in a closed details.
   /// </summary>
   /// <param name="clock">The clock block.</param>
   /// <returns>HTML fragment; empty when the set dropped none.</returns>
   private static string DroppedWarnings( BenchClock clock )
   {
      if( clock.Dropped.Count == 0 )
      {
         return string.Empty;
      }

      var html = new StringBuilder( $"<details class=\"bench-target\"><summary>{BenchLegends.H_DROPPED}</summary>" );
      html.Append( Records( string.Empty, clock.Dropped.Select( w => ( IReadOnlyList<BenchPair> )new[]
      {
         new BenchPair( "run", w.Run ), new BenchPair( "text", w.Text ),
         new BenchPair( "engine", w.Target ?? "-" ), new BenchPair( "pass", w.Pass ?? "-" ),
         new BenchPair( "engine median MHz", w.EngineMedianMhz?.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture ) ?? "-" ),
         new BenchPair( "client median MHz", w.ClientMedianMhz?.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture ) ?? "-" ),
      } ).ToList() ) );
      return html.Append( "</details>" ).ToString();
   }

   /// <summary>
   /// The engine settings table in a closed details: one row per setting, with the engine, the session, the value and how the run read or set it.
   /// The disclosure sentence about the table says it lists what the run read from each running engine or set from a repository file, so the table must be on the page.
   /// </summary>
   /// <param name="model">The file, for display names.</param>
   /// <returns>HTML fragment; empty when the runs recorded no engine settings.</returns>
   private static string EngineSettingsTable( BenchConsolidated model )
   {
      IReadOnlyList<BenchEngineSettings> rows = model.EngineSettings ?? Array.Empty<BenchEngineSettings>();
      if( rows.Count == 0 )
      {
         return string.Empty;
      }

      var html = new StringBuilder( $"<details class=\"bench-target\"><summary>{BenchFormat.Enc( BenchLegends.H_ENGINE_SETTINGS )}</summary><div class=\"preview\"><table class=\"bench-table\" data-table=\"engine-settings\"><thead><tr><th>Engine</th><th>Session</th><th>Setting</th><th>Value</th><th>How it was read or set</th></tr></thead><tbody>" );
      foreach( BenchEngineSettings row in rows )
      {
         foreach( BenchSetting setting in row.Settings )
         {
            html.Append( $"<tr data-target=\"{BenchFormat.Enc( row.Target )}\"><td>{BenchFormat.Enc( DisplayOf( model, row.Target ) )}</td><td>{BenchFormat.Enc( row.Session ?? "-" )}</td>" );
            html.Append( $"<td><code>{BenchFormat.Enc( setting.Key )}</code></td><td>{BenchFormat.Enc( setting.Value.Length == 0 ? "-" : setting.Value )}</td><td>{BenchFormat.Enc( setting.How.Length == 0 ? "-" : setting.How )}</td></tr>" );
         }
      }

      return html.Append( "</tbody></table></div></details>" ).ToString();
   }

   /// <summary>
   /// The container images used.
   /// </summary>
   /// <param name="model">The file, for display names.</param>
   /// <param name="images">The images.</param>
   /// <returns>HTML fragment.</returns>
   private static string ImagesTable( BenchConsolidated model, IReadOnlyList<BenchImage> images )
   {
      var html = new StringBuilder( $"<details class=\"bench-target\"><summary>{BenchLegends.H_IMAGES}</summary><div class=\"preview\"><table class=\"bench-table\"><thead><tr><th>Engine</th><th>Image id</th><th>Tag last set (UTC)</th><th>Before the first start</th></tr></thead><tbody>" );
      foreach( BenchImage image in images )
      {
         html.Append( $"<tr><td>{BenchFormat.Enc( DisplayOf( model, image.Target ) )}</td><td title=\"{BenchFormat.Enc( image.Id )}\"><code>{BenchFormat.Enc( image.Id.Length == 0 ? "-" : BenchFormat.ShortId( image.Id ) )}</code></td>" );
         html.Append( $"<td>{BenchFormat.Enc( image.LastTagTimeUtc ?? "-" )}</td><td>{( image.BeforeFirstStart == null ? "-" : image.BeforeFirstStart.Value ? "yes" : "no" )}</td></tr>" );
      }

      return html.Append( "</tbody></table></div></details>" ).ToString();
   }

   /// <summary>
   /// The runs of each session as links, with the sentences of the runs section.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   private static void AppendRuns( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed )
   {
      html.Append( $"<h2>{BenchLegends.H_RUNS}</h2>" );
      AppendSentences( html, Take( model, placed, BenchSlots.RUNS ) );

      html.Append( "<div class=\"preview\"><table class=\"bench-table\" data-table=\"runs-used\"><thead><tr><th>Session</th><th>Run</th><th class=\"n\">Seed</th><th>Started (UTC)</th></tr></thead><tbody>" );
      foreach( ( string label, string folder, long? seed, string? started ) in RunsUsed( model ) )
      {
         html.Append( $"<tr><td>{BenchFormat.Enc( label )}</td><td>{RunLink( folder )}</td><td class=\"n\">{BenchFormat.Enc( seed?.ToString( System.Globalization.CultureInfo.InvariantCulture ) ?? "-" )}</td><td>{BenchFormat.Enc( started ?? "-" )}</td></tr>" );
      }

      html.Append( "</tbody></table></div>" );
   }

   /// <summary>
   /// Every run the set uses: the runs of the compared sessions first, each under its session's name, then each run that only feeds the threshold, labelled
   /// with its session and "(basis)" (or "basis" when the file gives no session), with the seed and start the file gives it. A run that is both a compared
   /// run and a basis run is listed once, under its session. Why all of them: the report says the basis runs are listed with their seeds and start times, and a
   /// page that lists the compared runs only makes that sentence false.
   /// </summary>
   /// <param name="model">The file.</param>
   /// <returns>The rows: label, folder, seed, start.</returns>
   private static List<( string Label, string Folder, long? Seed, string? Started )> RunsUsed( BenchConsolidated model )
   {
      var rows = model.Sessions.SelectMany( s => s.Runs.Select( r => ( Label: s.Name, r.Folder, r.Seed, Started: r.StartedUtc ) ) ).ToList();
      foreach( BenchBasisRun run in model.Basis?.Runs ?? new List<BenchBasisRun>() )
      {
         if( !rows.Any( r => r.Folder == run.Folder && ( run.Session == null || r.Label == run.Session ) ) )
         {
            rows.Add( ( run.Session == null ? "basis" : $"{run.Session} (basis)", run.Folder, run.Seed, run.StartedUtc ) );
         }
      }

      return rows;
   }

   /// <summary>
   /// The audit block in a closed details; failures are printed as errors.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   private static void AppendAudit( StringBuilder html, BenchConsolidated model )
   {
      if( model.Audit == null )
      {
         return;
      }

      BenchAudit audit = model.Audit;
      html.Append( $"<details class=\"bench-target\"><summary>{BenchLegends.H_AUDIT}</summary><ul>" );
      html.Append( $"<li>Sentences checked <code>{audit.SentencesChecked}</code></li>" );
      html.Append( $"<li>Facts file SHA-256 <code>{BenchFormat.Enc( audit.FactsSha256 ?? "-" )}</code></li><li>Exclusions file SHA-256 <code>{BenchFormat.Enc( audit.ExclusionsSha256 ?? "-" )}</code></li>" );
      html.Append( audit.ObserverSha256 == null ? string.Empty : $"<li>Observer summary SHA-256 <code>{BenchFormat.Enc( audit.ObserverSha256 )}</code></li>" );
      html.Append( audit.FactsChecked == null ? string.Empty : $"<li>Facts checked <code>{audit.FactsChecked}</code></li>" );
      html.Append( audit.RowSentencesChecked == null ? string.Empty : $"<li>Row sentences checked <code>{audit.RowSentencesChecked}</code></li>" );
      foreach( string failure in audit.Failures )
      {
         html.Append( $"<li class=\"errors\">Failure: {BenchFormat.Enc( failure )}</li>" );
      }

      html.Append( "</ul></details>" );
   }

   /// <summary>
   /// The sentences of a section under an optional heading; nothing when it has none.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   /// <param name="section">Section name.</param>
   /// <param name="heading">Fixed heading, or null for none.</param>
   private static void AppendSection( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed, string section, string? heading )
   {
      List<BenchSentence> sentences = Take( model, placed, section ).ToList();
      if( sentences.Count == 0 )
      {
         return;
      }

      html.Append( heading == null ? string.Empty : $"<h2>{BenchFormat.Enc( heading )}</h2>" );
      AppendSentences( html, sentences );
   }

   /// <summary>
   /// Prints sentences in the order given, and the legend of a quotation once after them when any of them is one.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="sentences">The sentences.</param>
   private static void AppendSentences( StringBuilder html, IEnumerable<BenchSentence> sentences )
   {
      List<BenchSentence> list = sentences.ToList();
      foreach( BenchSentence sentence in list )
      {
         html.Append( Sentence( sentence ) );
      }

      html.Append( list.Any( s => s.Quote ) ? $"<p class=\"muted\">{BenchFormat.Enc( BenchLegends.QUOTE )}</p>" : string.Empty );
   }

   /// <summary>
   /// The sentences no section took, printed so none is lost.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   private static void AppendUnplaced( StringBuilder html, BenchConsolidated model, HashSet<BenchSentence> placed )
   {
      List<BenchSentence> rest = model.Sentences.Where( s => !placed.Contains( s ) ).ToList();
      if( rest.Count == 0 )
      {
         return;
      }

      html.Append( $"<aside class=\"bench-caveats\"><h2>{BenchLegends.H_UNPLACED}</h2><p class=\"errors\">{BenchFormat.Enc( BenchLegends.UNPLACED )}</p>" );
      foreach( BenchSentence sentence in rest )
      {
         placed.Add( sentence );
         html.Append( Sentence( sentence ) );
      }

      html.Append( "</aside>" );
   }

   /// <summary>
   /// The fields of the file that the page neither draws nor leaves out on purpose (see <see cref="BenchConsolidatedFields"/>), listed so that anything they
   /// say is known to be missing from the page; nothing when the page reads the file whole.
   /// </summary>
   /// <param name="html">Output.</param>
   /// <param name="model">The file.</param>
   private static void AppendUnread( StringBuilder html, BenchConsolidated model )
   {
      if( model.UnreadFields is not { Count: > 0 } unread )
      {
         return;
      }

      html.Append( $"<aside class=\"bench-caveats\" data-block=\"unread-fields\"><h2>{BenchFormat.Enc( BenchLegends.H_UNREAD )}</h2><p class=\"errors\">{BenchFormat.Enc( BenchLegends.UNREAD )}</p><ul>" );
      html.Append( string.Concat( unread.Select( f => $"<li><code>{BenchFormat.Enc( f )}</code></li>" ) ) );
      html.Append( "</ul></aside>" );
   }

   /// <summary>
   /// The targets whose recorded setup differs between sessions, with the differing fields.
   /// </summary>
   /// <param name="model">The file.</param>
   /// <returns>HTML fragment.</returns>
   private static string SetupDiffers( BenchConsolidated model )
   {
      var html = new StringBuilder( $"<h2>{BenchLegends.H_SETUP_DIFFERS}</h2><ul>" );
      foreach( ( string target, IReadOnlyList<string> fields ) in model.Guards.G3Targets )
      {
         html.Append( $"<li><strong>{BenchFormat.Enc( DisplayOf( model, target ) )}</strong> <code>{BenchFormat.Enc( string.Join( ", ", fields ) )}</code></li>" );
      }

      return html.Append( "</ul>" ).ToString();
   }

   /// <summary>
   /// Takes the sentences of a section (and, when a sub-slot is given, of that sub-slot) that no earlier
   /// call took, and marks them taken, so each sentence is printed once however the page asks.
   /// </summary>
   /// <param name="model">The file.</param>
   /// <param name="placed">Sentences already printed.</param>
   /// <param name="section">Section name.</param>
   /// <param name="sub">Sub-slot name, or null for every sentence of the section.</param>
   /// <returns>The sentences in file order.</returns>
   private static IEnumerable<BenchSentence> Take( BenchConsolidated model, HashSet<BenchSentence> placed, string section, string? sub = null )
   {
      List<BenchSentence> taken = model.Sentences.Where( s => !placed.Contains( s ) && BenchConsolidatedReader.SectionOf( s.Slot ) == section
         && ( sub == null || s.Slot == $"{section}.{sub}" || s.Slot.StartsWith( $"{section}.{sub}.", StringComparison.Ordinal ) ) ).ToList();
      placed.UnionWith( taken );
      return taken;
   }

   /// <summary>
   /// The note under an engine's name, if the row has one.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <returns>HTML fragment.</returns>
   private static string Note( BenchRow row )
   {
      if( string.IsNullOrWhiteSpace( row.Note ) )
      {
         return string.Empty;
      }

      return $"<br><small class=\"muted bench-note\">{BenchFormat.Enc( row.Note )}</small>{( row.NoteSourceList.Count > 0 ? SourceList( row.NoteSourceList ) : string.Empty )}";
   }

   /// <summary>
   /// The search cell of a row: the mode the report's search fact states for the engine, and under it the label that fact carries, as written. The label
   /// is the confidence the report gives the fact (every label of the engine's facts that state this mode, so a weaker one is never hidden behind a
   /// stronger one); the fact's text and source are the hover text.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="why">The facts table, or null.</param>
   /// <returns>HTML fragment.</returns>
   private static string SearchCell( BenchRow row, IReadOnlyList<BenchWhy>? why )
   {
      if( row.SearchMode == null )
      {
         return "-";
      }

      List<BenchFact> facts = ( why ?? Array.Empty<BenchWhy>() ).Where( w => w.Target == row.Target ).SelectMany( w => w.Facts )
         .Where( f => f.Kind == "search" && f.Mode != null && BenchFormat.SameMode( f.Mode, row.SearchMode ) ).ToList();
      if( facts.Count == 0 )
      {
         return BenchFormat.Enc( row.SearchMode );
      }

      string evidence = string.Join( "\n", facts.Select( f => $"{f.Confidence}: {f.Text} ({f.Source})" ) );
      string labels = string.Join( ", ", facts.Select( f => f.Confidence ).Distinct( StringComparer.Ordinal ) );
      return $"{BenchFormat.Enc( row.SearchMode )}<br><small class=\"muted bench-label\" title=\"{BenchFormat.Enc( evidence )}\">{BenchFormat.Enc( labels )}</small>";
   }

   /// <summary>
   /// The figures of one session for one row, as "min to max" (one figure when they agree).
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="session">The session's name.</param>
   /// <param name="lowerIsBetter">True for a latency metric.</param>
   /// <returns>The text, or "-" when the row has none for that session.</returns>
   private static string Range( BenchRow row, string session, bool lowerIsBetter )
   {
      if( !row.PerSession.TryGetValue( session, out BenchRange? range ) )
      {
         return "-";
      }

      string low = BenchFormat.Figure( range.Min, lowerIsBetter );
      string high = BenchFormat.Figure( range.Max, lowerIsBetter );
      return low == high ? low : $"{low} to {high}";
   }

   /// <summary>
   /// The "not separated from" cell: the engines' display names, "one session" for a row that is not
   /// ranked, or a dash when the row is separated from every engine.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="names">Display names by target.</param>
   /// <returns>HTML fragment.</returns>
   private static string Separation( BenchRow row, Dictionary<string, string> names )
   {
      if( row.Status == "one-session" )
      {
         return "<span class=\"bench-no\">one session</span>";
      }

      return row.NotSeparatedFrom.Count == 0 ? "-" : BenchFormat.Enc( string.Join( ", ", row.NotSeparatedFrom.Select( t => names.GetValueOrDefault( t, t ) ) ) );
   }

   /// <summary>
   /// A run folder as a link when its name is safe to link to, as plain text otherwise.
   /// </summary>
   /// <param name="folder">The folder name the file gives (untrusted).</param>
   /// <returns>HTML fragment.</returns>
   private static string RunLink( string folder )
   {
      string name = BenchFolderNames.LastSegment( folder );
      return BenchFolderNames.IsSafe( name ) ? $"<a href=\"/bench-results/{BenchFormat.Enc( name )}\">{BenchFormat.Enc( name )}</a>" : BenchFormat.Enc( folder );
   }

   /// <summary>
   /// The heading of a metric: its fixed heading, or the metric's name when the page has none for it.
   /// </summary>
   /// <param name="metric">The metric's name.</param>
   /// <returns>The heading.</returns>
   private static string Heading( string metric )
   {
      return METRIC_HEADINGS.GetValueOrDefault( metric, metric );
   }

   /// <summary>
   /// The display name of a target, from the first metric row that has it; the friendly name otherwise.
   /// </summary>
   /// <param name="model">The file.</param>
   /// <param name="target">The target.</param>
   /// <returns>The name.</returns>
   private static string DisplayOf( BenchConsolidated model, string target )
   {
      return model.Metrics.SelectMany( m => m.Rows ).FirstOrDefault( r => r.Target == target )?.Display ?? BenchNames.FriendlyName( target );
   }

   /// <summary>
   /// How many distinct engines the tables hold.
   /// </summary>
   /// <param name="model">The file.</param>
   /// <returns>The count.</returns>
   private static int AllTargets( BenchConsolidated model )
   {
      return model.Metrics.SelectMany( m => m.Rows.Select( r => r.Target ) ).Distinct( StringComparer.Ordinal ).Count();
   }

   #endregion Private Methods
}
