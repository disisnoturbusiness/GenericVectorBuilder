using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The short summary block that tops the benchmark pages: a one-sentence headline, the bar
/// chart, a compact table, and the list of known problems with the numbers.
/// Why: the full report is a wide table plus a page of per-engine detail that nobody reads, so
/// the answer has to be readable in about ten seconds before any of that starts.
/// Every piece of data is HTML-escaped; only the fixed text in this class is trusted markup.
/// The ranking is titled as request speed on a small collection, measured through each engine's
/// .NET client, and put in tie bands instead of strict ranks: engines whose min to max ranges over
/// the runs overlap, or whose neighboring medians are less than 3% apart, share a band, because at
/// 524 vectors the numbers measure the cost of each request (the client library included) and
/// engines a few percent apart swap places from run to run. Where the results carry the client's
/// CPU per search it is shown beside the single-search p50.
/// </summary>
public static class BenchSummaryHtml
{
   #region Data Members

   /// <summary>Recall at or above this counts as "same answers as exact search" (allows float noise under 1.0).</summary>
   public const double SAME_ANSWERS = 0.9995;

   /// <summary>What the published medians were measured on, from that folder's consolidated.md.</summary>
   public const string PUBLISHED_DATA = "Data: eShopOnWeb, 254 C# files cut into 524 chunks, 1024-dimension vectors, 20 labelled questions, top 10.";

   /// <summary>Largest collection, in vectors, that is called small (same value as the Bench tool's ConsolidateFraming).</summary>
   public const int SMALL_COLLECTION_MAX_ROWS = 10000;

   /// <summary>Title for a small collection; {0} is the vector count (same text as the Bench tool's ConsolidateFraming).</summary>
   public const string TITLE_SMALL = "Request speed on a small collection ({0} vectors)";

   /// <summary>Title for a larger collection; {0} is the vector count.</summary>
   public const string TITLE_LARGE = "Request speed at {0} vectors";

   /// <summary>Title when the results did not record the collection size.</summary>
   public const string TITLE_UNKNOWN = "Request speed (collection size not recorded)";

   /// <summary>The one line under a small-collection title.</summary>
   public const string LINE_SMALL = "Measured end to end through each engine's .NET client; at this size it reflects per-request cost including the client library, not index scaling.";

   /// <summary>The one line under a larger-collection title; {0} is the vector count.</summary>
   public const string LINE_LARGE = "Measured end to end through each engine's .NET client at {0} vectors; the order applies to this size only.";

   /// <summary>The one line when the collection size is unknown.</summary>
   public const string LINE_UNKNOWN = "The collection size was not recorded in these results. Measured end to end through each engine's .NET client.";

   /// <summary>What a band is, printed under the table when the numbers are medians of several runs.</summary>
   public const string BANDS_LINE = "Engines in different bands never overlap: every run of an engine in a faster band beat every run of an engine in a slower band, and the medians on either side of a band boundary are at least 3% apart. Engines in one band are linked by overlapping slowest-to-fastest ranges or by neighboring medians less than 3% apart (an engine varies about 2% from run to run), so these runs do not separate them cleanly. Inside a band they are listed by median, and that order is not a ranking.";

   /// <summary>What the client CPU per search column is and why it sits beside the latency; printed under a table that shows it.</summary>
   public const string CLIENT_CPU_LINE = "Client CPU per search is the CPU time the test's .NET client itself used for each search, measured in the same pass as the figure beside it. Where it is close to the latency, the client library is a large part of what is measured.";

   /// <summary>Printed under the table of a single run, which has no spread and so no bands.</summary>
   public const string ONE_RUN_LINE = "One run: no spread is known, so no band can be drawn. The order shows this run only.";

   /// <summary>The known problems with the published runs, one plain sentence each.</summary>
   public static readonly IReadOnlyList<string> CAVEATS = new[]
   {
      "These runs are not final. Checks on 4 Oct 2026 found problems in how they were run.",
      "Run order changed the timings.",
      "Qdrant (HNSW) never built its HNSW index at 524 points, so both Qdrant rows are plain scans.",
      "Milvus and Oracle were searched without waiting for their index builds to finish.",
      "SQL Server 2025 + DiskANN and SQL Server 2025 exact were compared across different runs.",
      "524 vectors measures the cost of each call, through each engine's .NET client library, more than how an engine scales.",
      "The test client and every engine shared one 8-thread box.",
      "Redis holds everything in memory. As set up here it snapshots every 5 minutes with no append-only log, so a crash can lose recent writes.",
   };

   /// <summary>Inline style of a flag marker (the page's stylesheet is shared and not owned here); colors are its tokens, so light and dark both work.</summary>
   private static readonly Regex ROWS_IN_TEXT = new( @"(\d[\d,]*)\s+(?:chunks|vectors|rows)\b", RegexOptions.Compiled );

   private const string MARK_STYLE = "display:inline-block;margin-left:4px;padding:0 5px;border:1px solid var(--warn);border-radius:8px;color:var(--warn);font-size:11px;font-weight:700;line-height:16px;cursor:help;text-decoration:none;vertical-align:1px";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The one-sentence headline. A fastest band of one engine names it and its number; a fastest
   /// band of several names them all and says they were fastest together, with each one's median.
   /// A warning is added for any of them that did not return the same answers as exact search.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>Plain text, not yet escaped.</returns>
   public static string Headline( BenchSummary summary )
   {
      if( summary.Ranked.Count == 0 )
      {
         return "No engine finished a search.";
      }

      List<BenchEngineRow> top = summary.HasRanges ? summary.Ranked.Where( r => r.Band == summary.Ranked[0].Band ).ToList() : new List<BenchEngineRow> { summary.Ranked[0] };
      if( top.Count > 1 )
      {
         return TiedHeadline( summary, top );
      }

      BenchEngineRow leader = top[0];
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
   /// The title of the ranking: request speed on a small collection, with the vector count; for a
   /// larger collection the count only; for an unknown size, that it was not recorded.
   /// </summary>
   /// <param name="rows">Vectors in the collection, or null when not recorded.</param>
   /// <returns>Plain text, not yet escaped.</returns>
   public static string FramingTitle( int? rows )
   {
      if( rows is not > 0 )
      {
         return TITLE_UNKNOWN;
      }

      return string.Format( CultureInfo.InvariantCulture, rows.Value <= SMALL_COLLECTION_MAX_ROWS ? TITLE_SMALL : TITLE_LARGE, rows.Value.ToString( "N0", CultureInfo.InvariantCulture ) );
   }

   /// <summary>
   /// The one line under the title: what the numbers measure at this size.
   /// </summary>
   /// <param name="rows">Vectors in the collection, or null when not recorded.</param>
   /// <returns>Plain text, not yet escaped.</returns>
   public static string FramingLine( int? rows )
   {
      if( rows is not > 0 )
      {
         return LINE_UNKNOWN;
      }

      return rows.Value <= SMALL_COLLECTION_MAX_ROWS ? LINE_SMALL : string.Format( CultureInfo.InvariantCulture, LINE_LARGE, rows.Value.ToString( "N0", CultureInfo.InvariantCulture ) );
   }

   /// <summary>
   /// The whole block: the title that says what is ranked and on how big a collection, the one
   /// line about what the numbers measure at this size, the headline, a "not final" line, an
   /// optional data line, the machine conditions, the chart with its caption, the compact table,
   /// what a band is, the notes on individual engines and the legend of the warning markers.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <param name="noteHtml">Trusted fixed markup for the "not final" line (never data).</param>
   /// <param name="dataLine">Plain text describing the data, or null for none.</param>
   /// <returns>HTML fragment.</returns>
   public static string Block( BenchSummary summary, string noteHtml, string? dataLine )
   {
      int? rows = summary.Rows ?? RowsIn( dataLine );
      var html = new StringBuilder( "<section class=\"bench-summary\">" );
      html.Append( $"<h2 class=\"bench-title\">{Enc( FramingTitle( rows ) )}</h2><p class=\"bench-framing muted\">{Enc( FramingLine( rows ) )}</p>" );
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

      html.Append( Table( summary ) ).Append( BandsNote( summary ) ).Append( ClientCpuNote( summary ) ).Append( EngineNotes( summary ) ).Append( Legend( summary ) ).Append( "</section>" );
      return html.ToString();
   }

   /// <summary>
   /// The compact table: band (or the order in this run when only one run was used), engine,
   /// searches per second with 8 at once, single search p50, the client's CPU per search when the
   /// results carry it for any engine (the p50 then shows two decimals, to compare at the same
   /// precision), and whether the answers matched exact search. Same order as
   /// the chart. A band is shared by engines whose ranges overlap or whose neighboring medians are
   /// close, so the first column is never a strict rank when there are several runs.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>HTML fragment.</returns>
   public static string Table( BenchSummary summary )
   {
      bool cpu = HasClientCpu( summary );
      var html = new StringBuilder( $"<div class=\"preview\"><table class=\"bench-table\"><thead><tr><th class=\"n\">{( summary.HasRanges ? "Band" : "Order" )}</th><th>Engine</th>" );
      html.Append( "<th class=\"n\">Searches/s, 8 at once</th><th class=\"n\">Single search p50 ms</th>" );
      html.Append( cpu ? "<th class=\"n\">Client CPU per search ms</th>" : string.Empty );
      html.Append( "<th>Same answers as exact search</th></tr></thead><tbody>" );
      for( int i = 0; i < summary.Ranked.Count; i++ )
      {
         BenchEngineRow row = summary.Ranked[i];
         string memory = row.InMemory ? " <span class=\"muted\">(in memory)</span>" : string.Empty;
         string same = SameAnswers( row.Recall );
         string sameClass = same.StartsWith( "no", StringComparison.Ordinal ) ? " class=\"bench-no\"" : string.Empty;
         int place = summary.HasRanges ? row.Band ?? i + 1 : i + 1;
         html.Append( $"<tr><td class=\"n\">{place}</td><td>{Enc( row.Name )}{memory}{Markers( row )}</td><td class=\"n\">{Enc( BenchChart.Count( row.Qps8 ) )}</td>" );
         html.Append( $"<td class=\"n\">{Enc( cpu ? MsTwoDecimals( row.P50Ms ) : Ms( row.P50Ms ) )}</td>" );
         html.Append( cpu ? $"<td class=\"n\">{Enc( MsTwoDecimals( row.ClientCpuMs ) )}</td>" : string.Empty );
         html.Append( $"<td{sameClass}>{Enc( same )}</td></tr>" );
      }

      foreach( string name in summary.NoResult )
      {
         html.Append( $"<tr><td class=\"n\">-</td><td>{Enc( name )}</td><td colspan=\"{( cpu ? 4 : 3 )}\" class=\"muted\">no result</td></tr>" );
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
   /// The line under the table that says what the first column means: what a band is and that the
   /// order inside it is not a ranking, or that one run has no spread and so no bands.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>HTML fragment; empty when no engine has a result.</returns>
   public static string BandsNote( BenchSummary summary )
   {
      if( summary.Ranked.Count == 0 )
      {
         return string.Empty;
      }

      return $"<p class=\"bench-bands muted\">{Enc( summary.HasRanges ? BANDS_LINE : ONE_RUN_LINE )}</p>";
   }

   /// <summary>
   /// The line under the table that says what the client CPU column is: shown only when the table
   /// has it.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>HTML fragment; empty when no engine carries the figure.</returns>
   public static string ClientCpuNote( BenchSummary summary )
   {
      return HasClientCpu( summary ) ? $"<p class=\"bench-client-cpu muted\">{Enc( CLIENT_CPU_LINE )}</p>" : string.Empty;
   }

   /// <summary>
   /// The notes on individual engines that the consolidate command wrote: a CPU cap, an
   /// exact-by-design search, a scan where an index was meant. Each says where it came from: the
   /// results, or a known limit the run did not record. Empty when no engine has a note (older
   /// files carry none).
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>HTML fragment.</returns>
   public static string EngineNotes( BenchSummary summary )
   {
      List<BenchEngineRow> withNotes = summary.Ranked.Where( r => r.Notes is { Count: > 0 } ).ToList();
      if( withNotes.Count == 0 )
      {
         return string.Empty;
      }

      var html = new StringBuilder( "<div class=\"bench-engine-notes\"><p class=\"muted\">Notes on individual engines:</p><ul>" );
      foreach( BenchEngineRow row in withNotes )
      {
         foreach( BenchEngineNote note in row.Notes! )
         {
            string source = note.Source == "known" ? "known limit, not recorded in these results" : "from the results";
            html.Append( $"<li><strong>{Enc( row.Name )}</strong> {Enc( note.Text )} <span class=\"muted\">({Enc( source )})</span></li>" );
         }
      }

      return html.Append( "</ul></div>" ).ToString();
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
   /// Formats milliseconds with two decimals, or "-" when missing. Used for the client's CPU per
   /// search (a fraction of a millisecond for the fast engines) and, when that column is shown, for
   /// the p50 beside it, so the two can be compared at the same precision.
   /// </summary>
   /// <param name="ms">Milliseconds or null.</param>
   /// <returns>E.g. "0.49".</returns>
   public static string MsTwoDecimals( double? ms )
   {
      return ms == null ? "-" : ms.Value.ToString( "0.00", CultureInfo.InvariantCulture );
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
   /// True when at least one engine carries the client's CPU per search.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>True when the table shows the column.</returns>
   private static bool HasClientCpu( BenchSummary summary )
   {
      return summary.Ranked.Any( r => r.ClientCpuMs != null );
   }

   /// <summary>
   /// The chart caption: what the bars, lines and hatching mean.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>Plain text.</returns>
   private static string Caption( BenchSummary summary )
   {
      string text = summary.HasRanges
         ? $"Searches per second with 8 at once; longer is faster. Bar: median of {summary.Runs} runs. Thin line: slowest to fastest run. Engines with the same band number have overlapping lines or medians less than 3% apart."
         : "Searches per second with 8 at once in this run; longer is faster.";
      return summary.Ranked.Any( r => r.InMemory ) ? text + " Hatched: holds everything in memory." : text;
   }

   /// <summary>
   /// The headline when several engines share the fastest band: all of them, their medians, and
   /// that these runs do not tell them apart; plus a warning for each one that missed exact answers.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <param name="top">The engines of the fastest band, in median order.</param>
   /// <returns>Plain text, not yet escaped.</returns>
   private static string TiedHeadline( BenchSummary summary, List<BenchEngineRow> top )
   {
      string names = JoinWords( top.Select( r => r.Name ).ToList() );
      string medians = JoinWords( top.Select( r => BenchChart.Count( r.Qps8 ) ).ToList() );
      string text = $"{names} were fastest together, too close to tell apart in these runs: {medians} searches per second with 8 at once (median of {summary.Runs} runs)";
      foreach( BenchEngineRow row in top.Where( r => r.Recall != null && r.Recall.Value < SAME_ANSWERS ) )
      {
         text += $", {row.Name} returned {Percent( row.Recall!.Value )} of the exact top 10";
      }

      return text + ".";
   }

   /// <summary>
   /// "a", "a and b" or "a, b and c".
   /// </summary>
   /// <param name="items">Items.</param>
   /// <returns>The joined text.</returns>
   private static string JoinWords( List<string> items )
   {
      return items.Count <= 1 ? string.Join( string.Empty, items ) : string.Join( ", ", items.Take( items.Count - 1 ) ) + " and " + items[^1];
   }

   /// <summary>
   /// The vector count in a data line such as "254 C# files cut into 524 chunks" or "524 vectors":
   /// the first number followed by chunks, vectors or rows.
   /// Why from the text: the old published file records no row count, and the title must agree
   /// with the data line printed next to it.
   /// </summary>
   /// <param name="dataLine">The data line, or null.</param>
   /// <returns>The count, or null when the line names none.</returns>
   private static int? RowsIn( string? dataLine )
   {
      Match m = dataLine == null ? Match.Empty : ROWS_IN_TEXT.Match( dataLine );
      return m.Success && int.TryParse( m.Groups[1].Value.Replace( ",", string.Empty ), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows ) && rows > 0 ? rows : null;
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
