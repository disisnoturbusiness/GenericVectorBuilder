using System.Globalization;
using System.Net;
using System.Text;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The short summary block that tops the benchmark pages: a one-sentence headline, the bar
/// chart, a compact table, and the list of known problems with the numbers.
/// Why: the full report is a wide table plus a page of per-engine detail that nobody reads, so
/// the answer has to be readable in about ten seconds before any of that starts.
/// Every piece of data is HTML-escaped; only the fixed text in this class is trusted markup.
/// </summary>
public static class BenchSummaryHtml
{
   #region Data Members

   /// <summary>Recall at or above this counts as "same answers as exact search" (allows float noise under 1.0).</summary>
   public const double SAME_ANSWERS = 0.9995;

   /// <summary>What the published medians were measured on, from that folder's consolidated.md.</summary>
   public const string PUBLISHED_DATA = "Data: eShopOnWeb, 254 C# files cut into 524 chunks, 1024-dimension vectors, 20 labelled questions, top 10.";

   /// <summary>The known problems with the published runs, one plain sentence each.</summary>
   public static readonly IReadOnlyList<string> CAVEATS = new[]
   {
      "These runs are not final. Checks on 4 Oct 2026 found problems in how they were run.",
      "Run order changed the timings.",
      "Qdrant (HNSW) never built its HNSW index at 524 points, so both Qdrant rows are plain scans.",
      "Milvus and Oracle were searched without waiting for their index builds to finish.",
      "SQL Server 2025 + DiskANN and SQL Server 2025 exact were compared across different runs.",
      "524 vectors measures the cost of each call more than how an engine scales.",
      "The test client and every engine shared one 8-thread box.",
      "Redis holds everything in memory. As set up here it snapshots every 5 minutes with no append-only log, so a crash can lose recent writes.",
   };

   /// <summary>Inline style of a flag marker (the page's stylesheet is shared and not owned here); colors are its tokens, so light and dark both work.</summary>
   private const string MARK_STYLE = "display:inline-block;margin-left:4px;padding:0 5px;border:1px solid var(--warn);border-radius:8px;color:var(--warn);font-size:11px;font-weight:700;line-height:16px;cursor:help;text-decoration:none;vertical-align:1px";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The one-sentence headline: the leader and its number, plus a warning when the leader did
   /// not return the same answers as exact search.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>Plain text, not yet escaped.</returns>
   public static string Headline( BenchSummary summary )
   {
      if( summary.Ranked.Count == 0 )
      {
         return "No engine finished a search.";
      }

      BenchEngineRow leader = summary.Ranked[0];
      string where = summary.HasRanges ? string.Empty : " in this run";
      string text = $"{leader.Name} was fastest{where}: {BenchChart.Count( leader.Qps8 )} searches per second with 8 at once";
      if( summary.HasRanges )
      {
         text += $" (median of {summary.Runs} runs)";
      }

      if( leader.Recall != null && leader.Recall.Value < SAME_ANSWERS )
      {
         text += $", returning {Percent( leader.Recall.Value )} of the exact top 10";
      }

      return text + ".";
   }

   /// <summary>
   /// The whole block: headline, a "not final" line, an optional data line, the chart with its
   /// caption, and the compact table.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <param name="noteHtml">Trusted fixed markup for the "not final" line (never data).</param>
   /// <param name="dataLine">Plain text describing the data, or null for none.</param>
   /// <returns>HTML fragment.</returns>
   public static string Block( BenchSummary summary, string noteHtml, string? dataLine )
   {
      var html = new StringBuilder( "<section class=\"bench-summary\">" );
      html.Append( $"<p class=\"bench-lead\">{Enc( Headline( summary ) )}</p>" );
      html.Append( $"<p class=\"bench-sub\"><span class=\"bench-flag\">Not final</span> {noteHtml}</p>" );
      if( !string.IsNullOrWhiteSpace( dataLine ) )
      {
         html.Append( $"<p class=\"bench-data muted\">{Enc( dataLine )}</p>" );
      }

      html.Append( ConditionsLine( summary.Conditions ) );

      string svg = BenchChart.Svg( summary );
      if( svg.Length > 0 )
      {
         html.Append( $"<figure class=\"bench-figure\">{svg}<figcaption>{Enc( Caption( summary ) )}</figcaption></figure>" );
      }

      html.Append( Table( summary ) ).Append( Legend( summary ) ).Append( "</section>" );
      return html.ToString();
   }

   /// <summary>
   /// The compact table: rank, engine, searches per second with 8 at once, single search p50,
   /// and whether the answers matched exact search. Same order as the chart.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>HTML fragment.</returns>
   public static string Table( BenchSummary summary )
   {
      var html = new StringBuilder( "<div class=\"preview\"><table class=\"bench-table\"><thead><tr><th class=\"n\">Rank</th><th>Engine</th>" );
      html.Append( "<th class=\"n\">Searches/s, 8 at once</th><th class=\"n\">Single search p50 ms</th><th>Same answers as exact search</th></tr></thead><tbody>" );
      for( int i = 0; i < summary.Ranked.Count; i++ )
      {
         BenchEngineRow row = summary.Ranked[i];
         string memory = row.InMemory ? " <span class=\"muted\">(in memory)</span>" : string.Empty;
         string same = SameAnswers( row.Recall );
         string sameClass = same.StartsWith( "no", StringComparison.Ordinal ) ? " class=\"bench-no\"" : string.Empty;
         html.Append( $"<tr><td class=\"n\">{i + 1}</td><td>{Enc( row.Name )}{memory}{Markers( row )}</td><td class=\"n\">{Enc( BenchChart.Count( row.Qps8 ) )}</td>" );
         html.Append( $"<td class=\"n\">{Enc( Ms( row.P50Ms ) )}</td><td{sameClass}>{Enc( same )}</td></tr>" );
      }

      foreach( string name in summary.NoResult )
      {
         html.Append( $"<tr><td class=\"n\">-</td><td>{Enc( name )}</td><td colspan=\"3\" class=\"muted\">no result</td></tr>" );
      }

      return html.Append( "</tbody></table></div>" ).ToString();
   }

   /// <summary>
   /// The one line that states the machine conditions the numbers were measured under, or says
   /// they were not recorded (old result files). A governor other than performance and a
   /// non-Release build are marked like the "no" answers in the table.
   /// </summary>
   /// <param name="conditions">The conditions, or null when the result file had none.</param>
   /// <returns>HTML fragment.</returns>
   public static string ConditionsLine( BenchConditions? conditions )
   {
      if( conditions == null || conditions.NoneRecorded )
      {
         return "<p class=\"bench-conditions muted\">Machine conditions (CPU governor, CPU partition, build) were not recorded in these results.</p>";
      }

      var parts = new List<string>
      {
         $"CPU governor {Value( conditions.Governor, conditions.GovernorIsWrong )}",
         $"CPU partition {Value( conditions.Partition, false )}",
         conditions.Build == null ? "build not recorded" : conditions.BuildIsWrong ? $"<span class=\"bench-no\">{Enc( conditions.Build )} build</span>" : $"{Enc( conditions.Build )} build",
      };
      if( conditions.WarmupSearches != null )
      {
         parts.Add( $"{Enc( conditions.WarmupSearches )} warm-up searches before each timed pass" );
      }

      if( conditions.ExactSeconds != null )
      {
         parts.Add( $"exact mode up to {Enc( conditions.ExactSeconds )} s" );
      }

      return $"<p class=\"bench-conditions muted\">Machine: {string.Join( "; ", parts )}.</p>";
   }

   /// <summary>
   /// The small markers after an engine's name, one per kind of warning, each with its evidence
   /// as hover text. Empty when the engine has no warning.
   /// </summary>
   /// <param name="row">The engine.</param>
   /// <returns>HTML fragment.</returns>
   public static string Markers( BenchEngineRow row )
   {
      if( row.Flags is not { Count: > 0 } flags )
      {
         return string.Empty;
      }

      var html = new StringBuilder();
      foreach( IGrouping<string, BenchFlag> group in flags.GroupBy( f => BenchFlagInfo.Code( f.Kind ) ).OrderBy( g => g.Min( f => BenchFlagInfo.Order( f.Kind ) ) ) )
      {
         string evidence = string.Join( "\n", group.Select( f => $"{f.Kind}: {f.Detail}" ) );
         html.Append( $"<abbr class=\"bench-mark\" style=\"{MARK_STYLE}\" title=\"{Enc( evidence )}\" aria-label=\"{Enc( evidence )}\">{Enc( group.Key )}</abbr>" );
      }

      return html.ToString();
   }

   /// <summary>
   /// The legend under the table: what each marker used on the page means, and (closed by
   /// default, so it works without hover) the evidence behind every marker, engine by engine.
   /// Empty when no engine has a warning.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>HTML fragment.</returns>
   public static string Legend( BenchSummary summary )
   {
      var flagged = summary.Ranked.Where( r => r.Flags is { Count: > 0 } ).ToList();
      if( flagged.Count == 0 )
      {
         return string.Empty;
      }

      var html = new StringBuilder( "<div class=\"bench-legend\"><p class=\"muted\">The small markers after an engine name warn about its numbers. Hover a marker for the evidence.</p><ul>" );
      foreach( IGrouping<string, ( BenchEngineRow Row, BenchFlag Flag )> group in flagged.SelectMany( r => r.Flags!.Select( f => ( Row: r, Flag: f ) ) )
         .GroupBy( x => BenchFlagInfo.Code( x.Flag.Kind ) ).OrderBy( g => g.Min( x => BenchFlagInfo.Order( x.Flag.Kind ) ) ) )
      {
         int engines = group.Select( x => x.Row.Key ).Distinct().Count();
         string meaning = BenchFlagInfo.Meaning( group.OrderBy( x => BenchFlagInfo.Order( x.Flag.Kind ) ).First().Flag.Kind );
         html.Append( $"<li><abbr class=\"bench-mark\" style=\"{MARK_STYLE}\">{Enc( group.Key )}</abbr> {Enc( meaning )} <span class=\"muted\">({engines} of {summary.Ranked.Count} engines)</span></li>" );
      }

      html.Append( "</ul><details class=\"bench-evidence\"><summary>Evidence behind the markers</summary><ul>" );
      foreach( BenchEngineRow row in flagged )
      {
         foreach( BenchFlag flag in row.Flags! )
         {
            html.Append( $"<li><strong>{Enc( row.Name )}</strong> {Enc( BenchFlagInfo.Code( flag.Kind ) )} {Enc( flag.Kind )}: {Enc( flag.Detail )}</li>" );
         }
      }

      return html.Append( "</ul></details></div>" ).ToString();
   }

   /// <summary>
   /// The boxed list of known problems, shown under the summary table.
   /// </summary>
   /// <returns>HTML fragment.</returns>
   public static string Caveats()
   {
      var html = new StringBuilder( "<aside class=\"bench-caveats\"><h2>Read before quoting these numbers</h2><ul>" );
      foreach( string caveat in CAVEATS )
      {
         html.Append( $"<li>{Enc( caveat )}</li>" );
      }

      return html.Append( "</ul></aside>" ).ToString();
   }

   /// <summary>
   /// "yes" when recall says the engine returned the exact top 10, otherwise "no" with the share.
   /// </summary>
   /// <param name="recall">Recall at 10, or null.</param>
   /// <returns>E.g. "yes", "no (96.5%)" or "not measured".</returns>
   public static string SameAnswers( double? recall )
   {
      if( recall == null )
      {
         return "not measured";
      }

      return recall.Value >= SAME_ANSWERS ? "yes" : $"no ({Percent( recall.Value )})";
   }

   /// <summary>
   /// Formats a latency in milliseconds with one decimal, or "-" when missing.
   /// </summary>
   /// <param name="ms">Milliseconds or null.</param>
   /// <returns>E.g. "2.0".</returns>
   public static string Ms( double? ms )
   {
      return ms == null ? "-" : ms.Value.ToString( "0.0", CultureInfo.InvariantCulture );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The chart caption: what the bars, lines and hatching mean.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>Plain text.</returns>
   private static string Caption( BenchSummary summary )
   {
      string text = summary.HasRanges
         ? $"Searches per second with 8 at once; longer is faster. Bar: median of {summary.Runs} runs. Thin line: slowest to fastest run."
         : "Searches per second with 8 at once in this run; longer is faster.";
      return summary.Ranked.Any( r => r.InMemory ) ? text + " Hatched: holds everything in memory." : text;
   }

   /// <summary>
   /// Formats a 0 to 1 share as a percentage with at most one decimal.
   /// </summary>
   /// <param name="share">0 to 1.</param>
   /// <returns>E.g. "96.5%".</returns>
   private static string Percent( double share )
   {
      return ( share * 100 ).ToString( "0.#", CultureInfo.InvariantCulture ) + "%";
   }

   /// <summary>
   /// A condition value for the conditions line: "not recorded" for null, marked when wrong.
   /// </summary>
   /// <param name="value">The value, or null.</param>
   /// <param name="wrong">True to mark it like a "no" answer.</param>
   /// <returns>HTML fragment.</returns>
   private static string Value( string? value, bool wrong )
   {
      if( value == null )
      {
         return "not recorded";
      }

      return wrong ? $"<span class=\"bench-no\">{Enc( value )}</span>" : Enc( value );
   }

   /// <summary>
   /// HTML-encodes text.
   /// </summary>
   /// <param name="text">Text.</param>
   /// <returns>Encoded text.</returns>
   private static string Enc( string text )
   {
      return WebUtility.HtmlEncode( text );
   }

   #endregion Private Methods
}
