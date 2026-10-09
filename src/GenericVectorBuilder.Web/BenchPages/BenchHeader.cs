using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// One engine's line of the header's order.
/// </summary>
/// <param name="Row">The engine's row of the table the header orders by.</param>
/// <param name="Qps">The row's median searches per second.</param>
/// <param name="Ms">The milliseconds per search worked out from <paramref name="Qps"/>: one second over it.</param>
/// <param name="DifferenceMs">The row's <paramref name="Ms"/> less the row above's, or null for the first row.</param>
/// <param name="WithinMargin">True when the row's notSeparatedFrom names the row above it, so the report's test does not separate the two.</param>
internal sealed record BenchHeaderLine( BenchRow Row, double Qps, double Ms, double? DifferenceMs, bool WithinMargin );

/// <summary>
/// The header of the Vector search benchmark page: what the numbers are of, then the database engines in order of searches per second with eight searchers
/// at once, each with the ms per search worked out from that figure and its difference from the row above, a bold label on a row the file gives a note
/// (Redis holds its data in memory), a marker on a row the report's test does not separate from the row above, and the rows the tool recorded as not held
/// last and without a figure.
/// Why a page of its own and not a block on the full results page: the full results (four tables, the flags, the drift, the runs) stay on the /bench-results
/// page exactly as they were; the header is the answer a reader wants first, drawn from the same consolidated.json, and it links to the rest.
/// Why every figure is the file's or worked out from it: the page states no fact of its own. The data set, its size and the query count are the values the
/// file's data line is bound to, the query source is the file's recorded one, the order and the figures are the qps8 table's medians, the ms per search is one
/// second over the median (and the header says it is worked out and is not a timed latency), and the marker is the file's notSeparatedFrom. The words the
/// header adds are fixed legends in <see cref="BenchLegends"/>, which the legend tests walk.
/// Why the engines are not ranked by position alone: the order is the order of the medians, and a row the report's test does not separate from the row above
/// says so on its own line, so the order is never read as a finding the report does not make.
/// </summary>
public static class BenchHeader
{
   #region Data Members

   /// <summary>The table the header orders the engines by: searches per second with eight searchers at once.</summary>
   public const string METRIC = "qps8";

   private const string SEARCHERS_PREFIX = "qps";
   private const string RANKED = "ranked";
   /// <summary>The slot of the sentence that records the data line, which the header and the Golden Questions page read the query count from.</summary>
   internal const string DATA_SLOT = "subtitle.data";

   /// <summary>The ref of the source that holds the number of queries the runs ran.</summary>
   internal const string REF_QUERY_COUNT = "results:queryCount";

   private const string GOLDEN_KIND = "golden";
   private const string REF_PIPELINE = "results:pipeline";
   private const string REF_ROWS = "results:rows";
   private const string REF_DIMENSION = "results:dimension";
   private const string REF_RECORDED = "consolidated:queries.recorded";
   private const double MS_PER_SECOND = 1000.0;
   private const int COLUMNS = 4;
   private const int SHORT_COMMIT = 12;

   private static readonly Regex QUERY_FILE = new( @"\bfrom (\S+?\.json)\b", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The header for a consolidated.json, drawn from the file's own text. Nothing for a file that is not in the v8 shape (the page prints its older-format
   /// notice); a notice, and no order, for a set marked stopped; and, for a file that is in the shape and cannot give an order (a median that is not a
   /// positive number, a wrong type in a field the header reads), a visible error that says what and where.
   /// </summary>
   /// <param name="json">The file's text.</param>
   /// <param name="folder">The published folder the file is in, named in the small text of the header.</param>
   /// <returns>An HTML fragment: one section, or an empty string.</returns>
   public static string Html( string json, string folder )
   {
      try
      {
         if( BenchConsolidatedReader.Detect( json ) != BenchShape.Consolidated )
         {
            return string.Empty;
         }

         BenchConsolidated model = BenchConsolidatedReader.Read( json );
         using JsonDocument doc = JsonDocument.Parse( json );
         return model.Stopped ? Section( "stopped", $"<p class=\"errors\">{BenchFormat.Enc( BenchLegends.L_HEADER_STOPPED )}</p>{FullResultsLink()}" ) : Build( model, doc.RootElement, folder );
      }
      catch( Exception ex ) when( ex is InvalidDataException or JsonException )
      {
         return Section( "error", $"<p class=\"errors\">{BenchFormat.Enc( BenchLegends.L_HEADER_ERROR )}: {BenchFormat.Enc( ex.Message )}</p>{FullResultsLink()}" );
      }
   }

   /// <summary>
   /// The line that links to the full results page: every table, the flags and every run. Part of the header, and printed by the Vector search benchmark page
   /// by itself when it has no header to print.
   /// </summary>
   /// <returns>HTML fragment: one paragraph.</returns>
   public static string FullResultsLink()
   {
      return $"<p class=\"bench-header-link\"><a href=\"{BenchRoutes.FULL_RESULTS}\">{BenchFormat.Enc( BenchLegends.L_HEADER_FULL )}</a></p>";
   }

   /// <summary>
   /// True when the recorded query source starts with the kind "golden": the queries are the questions of a questions file.
   /// </summary>
   /// <param name="recorded">The recorded query source, or null.</param>
   /// <returns>True for golden questions.</returns>
   internal static bool IsGolden( string? recorded )
   {
      return string.Equals( ( recorded ?? string.Empty ).Split( ':', 2 )[0].Trim(), GOLDEN_KIND, StringComparison.Ordinal );
   }

   /// <summary>
   /// The path of the file the queries were read from, as the recorded query source names it ("... from /path/questions_golden.json, ...").
   /// Why one reader: the header prints this path and the Golden Questions page reads the questions from it, and they must be the same file.
   /// </summary>
   /// <param name="recorded">The recorded query source, or null.</param>
   /// <returns>The path, or null when the source is absent or names no .json file.</returns>
   internal static string? QueryFileOf( string? recorded )
   {
      return recorded != null && QUERY_FILE.Match( recorded ) is { Success: true } file ? file.Groups[1].Value : null;
   }

   /// <summary>
   /// The recorded query source: the text of the file's queries.recorded, or null when the file has none.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The text, or null.</returns>
   internal static string? RecordedQueries( JsonElement root )
   {
      return BenchJson.Get( root, "queries" ) is { ValueKind: JsonValueKind.Object } queries && BenchJson.Text( queries, "recorded", "queries" ) is { Length: > 0 } recorded ? recorded : null;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The whole section for a set that is not stopped: the first sentence, the order of the engines, the link to the full results, and the small text.
   /// </summary>
   /// <param name="model">The file, read and checked.</param>
   /// <param name="root">The file's root, for the fields the model does not keep (the recorded queries, the builds).</param>
   /// <param name="folder">The published folder.</param>
   /// <returns>HTML fragment.</returns>
   private static string Build( BenchConsolidated model, JsonElement root, string folder )
   {
      var html = new StringBuilder( Lead( model, root ) );
      BenchMetric? metric = model.Metrics.FirstOrDefault( m => m.Metric == METRIC );
      if( metric == null )
      {
         html.Append( $"<p class=\"errors\">{BenchFormat.Enc( BenchLegends.L_HEADER_NO_TABLE_A )} <code>{BenchFormat.Enc( METRIC )}</code> {BenchFormat.Enc( BenchLegends.L_HEADER_NO_TABLE_B )}</p>" );
      }
      else
      {
         html.Append( Order( metric, model.Sessions.Sum( s => s.Runs.Count ) ) );
      }

      html.Append( FullResultsLink() ).Append( BuildLine( root, folder ) );
      return Section( null, html.ToString() );
   }

   /// <summary>
   /// The first sentence: the data set, its size, and the number and kind of the queries, each as the file's data line records it; the file the queries were
   /// read from, as the file's recorded query source gives it; and the sources of these in a closed block. A file with no data line gets a plain statement
   /// that it names no data set.
   /// </summary>
   /// <param name="model">The file, read and checked.</param>
   /// <param name="root">The file's root.</param>
   /// <returns>HTML fragment.</returns>
   private static string Lead( BenchConsolidated model, JsonElement root )
   {
      BenchSentence? data = model.Sentences.FirstOrDefault( s => s.Slot == DATA_SLOT );
      string? pipeline = SourceValue( data, REF_PIPELINE );
      string? rows = SourceValue( data, REF_ROWS );
      string? dimension = SourceValue( data, REF_DIMENSION );
      string? count = SourceValue( data, REF_QUERY_COUNT );
      if( data == null || pipeline == null || rows == null || dimension == null || count == null )
      {
         return $"<p class=\"bench-header-nodata muted\">{BenchFormat.Enc( BenchLegends.L_HEADER_NO_DATA )}</p>";
      }

      string? recorded = RecordedQueries( root );
      var html = new StringBuilder( $"<p class=\"bench-header-lead\">{BenchFormat.Enc( BenchLegends.L_HEADER_FROM )} <strong class=\"bench-header-data\">{BenchFormat.Enc( BenchRunList.PipelineName( pipeline ) )}</strong> {BenchFormat.Enc( BenchLegends.L_HEADER_DATASET )} " );
      html.Append( $"(<span class=\"bench-header-vectors\">{BenchFormat.Enc( rows )}</span> {BenchFormat.Enc( BenchLegends.L_HEADER_VECTORS_OF )} <span class=\"bench-header-dimensions\">{BenchFormat.Enc( dimension )}</span> {BenchFormat.Enc( BenchLegends.L_HEADER_DIMENSIONS )}) " );
      html.Append( $"{BenchFormat.Enc( BenchLegends.L_HEADER_AND_FROM )} <span class=\"bench-header-count\">{BenchFormat.Enc( count )}</span> <strong>{QueryNameHtml( recorded )}</strong>, {BenchFormat.Enc( BenchLegends.L_HEADER_RAN )}</p>" );
      if( QueryFileOf( recorded ) is { } file )
      {
         html.Append( $"<p class=\"bench-header-from muted\">{BenchFormat.Enc( BenchLegends.L_HEADER_QUESTIONS_FROM )} <code>{BenchFormat.Enc( file )}</code>.</p>" );
      }

      var sources = data.Sources.Where( s => s.Ref is REF_PIPELINE or REF_ROWS or REF_DIMENSION or REF_QUERY_COUNT ).ToList();
      if( recorded != null )
      {
         sources.Add( new BenchSource( "consolidated", REF_RECORDED, recorded ) );
      }

      return html.Append( BenchConsolidatedHtml.SourceList( sources ) ).ToString();
   }

   /// <summary>
   /// The heading over the order, the line that says what the figures are, the table, and the legends that belong to what the table shows.
   /// </summary>
   /// <param name="metric">The table the order is drawn from.</param>
   /// <param name="runs">How many runs the medians are of.</param>
   /// <returns>HTML fragment.</returns>
   private static string Order( BenchMetric metric, int runs )
   {
      IReadOnlyList<BenchHeaderLine> lines = Lines( metric );
      List<BenchRow> unranked = metric.Rows.Where( r => r.Status != RANKED || r.IsNotHeld ).ToList();
      var html = new StringBuilder( $"<h2 class=\"bench-header-sub\">{BenchFormat.Enc( BenchLegends.L_HEADER_MULTI_A )} <span class=\"bench-header-searchers\">{BenchFormat.Enc( metric.Metric[SEARCHERS_PREFIX.Length..] )}</span> {BenchFormat.Enc( BenchLegends.L_HEADER_MULTI_B )}</h2>" );
      html.Append( $"<p class=\"bench-header-note muted\">{BenchFormat.Enc( BenchLegends.L_HEADER_ORDER )} {BenchFormat.Enc( BenchLegends.L_HEADER_MEDIAN_A )} <span class=\"bench-header-runs\">{runs.ToString( CultureInfo.InvariantCulture )}</span> " );
      html.Append( $"{BenchFormat.Enc( BenchLegends.L_HEADER_MEDIAN_B )} {BenchFormat.Enc( BenchLegends.L_HEADER_MS )} {BenchFormat.Enc( BenchLegends.L_HEADER_DIFF )}</p>" );
      if( lines.Count == 0 )
      {
         html.Append( $"<p class=\"errors\">{BenchFormat.Enc( BenchLegends.L_HEADER_NO_RANKED )}</p>" );
      }

      html.Append( "<div class=\"preview\"><table class=\"bench-header-table\"><thead><tr>" );
      html.Append( $"<th>{BenchFormat.Enc( BenchLegends.L_HEADER_COL_ENGINE )}</th><th class=\"n\">{BenchFormat.Enc( BenchLegends.L_HEADER_COL_QPS )}</th>" );
      html.Append( $"<th class=\"n\">{BenchFormat.Enc( BenchLegends.L_HEADER_COL_MS )}</th><th class=\"n\">{BenchFormat.Enc( BenchLegends.L_HEADER_COL_DIFF )}</th></tr></thead><tbody>" );
      foreach( BenchHeaderLine line in lines )
      {
         html.Append( RankedRow( line ) );
      }

      if( unranked.Count > 0 )
      {
         html.Append( $"<tr class=\"bench-header-unranked\"><th colspan=\"{COLUMNS}\">{BenchFormat.Enc( BenchLegends.H_NOT_RANKED )}</th></tr>" );
         unranked.ForEach( row => html.Append( UnrankedRow( row ) ) );
      }

      return html.Append( "</tbody></table></div>" ).Append( Legends( lines, unranked ) ).ToString();
   }

   /// <summary>
   /// The ranked rows of the table, most searches per second first, each with the ms per search worked out from its median, its difference from the row above,
   /// and whether the file's notSeparatedFrom for the row names the row above. A median that is not a positive number gives no ms per search and is refused.
   /// </summary>
   /// <param name="metric">The table.</param>
   /// <returns>The lines in page order; none when the table holds no ranked row.</returns>
   private static IReadOnlyList<BenchHeaderLine> Lines( BenchMetric metric )
   {
      if( metric.LowerIsBetter )
      {
         throw new InvalidDataException( $"metrics[{metric.Metric}] is a lower-is-better table; the header orders searches per second, the most first." );
      }

      List<BenchRow> ranked = metric.Rows.Where( r => r.Status == RANKED && !r.IsNotHeld ).ToList();
      if( ranked.FirstOrDefault( r => r.Median is not > 0 || !double.IsFinite( r.Median.Value ) ) is { } bad )
      {
         throw new InvalidDataException( $"metrics[{metric.Metric}].{bad.Target} has a median of {bad.Median?.ToString( "R", CultureInfo.InvariantCulture ) ?? "nothing"}, which is not a positive number of searches per second, so no ms per search can be worked out." );
      }

      ranked = ranked.OrderByDescending( r => r.Median!.Value ).ToList();
      var lines = new List<BenchHeaderLine>();
      for( int i = 0; i < ranked.Count; i++ )
      {
         double qps = ranked[i].Median!.Value;
         double ms = MS_PER_SECOND / qps;
         bool within = i > 0 && ranked[i].NotSeparatedFrom.Contains( ranked[i - 1].Target, StringComparer.Ordinal );
         lines.Add( new BenchHeaderLine( ranked[i], qps, ms, i == 0 ? null : ms - lines[i - 1].Ms, within ) );
      }

      return lines;
   }

   /// <summary>
   /// One ranked row: the engine's name, the marker when the report's test does not separate it from the row above, then the searches per second, the ms
   /// per search and the difference from the row above; and, under it, the engine's note as a bold label when the file gives it one.
   /// </summary>
   /// <param name="line">The line.</param>
   /// <returns>HTML fragment.</returns>
   private static string RankedRow( BenchHeaderLine line )
   {
      var html = new StringBuilder( $"<tr class=\"bench-header-row\" data-engine=\"{BenchFormat.Enc( line.Row.Target )}\"><td class=\"bench-header-engine\"><span class=\"bench-header-name\">{BenchFormat.Enc( line.Row.Display )}</span>" );
      if( line.WithinMargin )
      {
         html.Append( $" <small class=\"bench-header-mark\" data-mark=\"within\">{BenchFormat.Enc( BenchLegends.L_HEADER_WITHIN )}</small>" );
      }

      string difference = line.DifferenceMs == null ? BenchLegends.L_HEADER_FIRST : "+" + Ms( line.DifferenceMs.Value );
      html.Append( $"</td><td class=\"n\">{BenchFormat.Count( line.Qps )}</td><td class=\"n\">{Ms( line.Ms )}</td><td class=\"n\">{BenchFormat.Enc( difference )}</td></tr>" );
      return html.Append( NoteRow( line.Row ) ).ToString();
   }

   /// <summary>
   /// One row that is not ranked: the engine's name and, in place of the figures, the label the report gives it (and its note under it, when it has one). No
   /// searches per second, no ms per search and no difference: the tool recorded the pass as not held, or the row has one session, and its figure is not one to
   /// quote or to order by.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <returns>HTML fragment.</returns>
   private static string UnrankedRow( BenchRow row )
   {
      string state = row.IsNotHeld ? BenchRow.NOT_HELD : row.Status;
      string label = row.IsNotHeld ? BenchLegends.L_NOT_HELD : BenchLegends.L_HEADER_ONE_SESSION;
      return $"<tr class=\"bench-header-row\" data-engine=\"{BenchFormat.Enc( row.Target )}\" data-state=\"{BenchFormat.Enc( state )}\"><td class=\"bench-header-engine\"><span class=\"bench-header-name\">{BenchFormat.Enc( row.Display )}</span></td>"
         + $"<td colspan=\"{COLUMNS - 1}\" data-state=\"{BenchFormat.Enc( state )}\">{BenchFormat.Enc( label )}</td></tr>{NoteRow( row )}";
   }

   /// <summary>
   /// The row under an engine's row that carries its note, across the whole width of the table so the label has room: the note as <see cref="NoteBlock"/>
   /// draws it.
   /// </summary>
   /// <param name="row">The engine's row.</param>
   /// <returns>HTML fragment; empty when the row has no note.</returns>
   private static string NoteRow( BenchRow row )
   {
      return string.IsNullOrWhiteSpace( row.Note ) ? string.Empty : $"<tr class=\"bench-header-note-row\" data-engine=\"{BenchFormat.Enc( row.Target )}\"><td colspan=\"{COLUMNS}\">{NoteBlock( row )}</td></tr>";
   }

   /// <summary>
   /// The note the file gives a row (for example that the engine holds its data in memory) as a bold label in the report's own words, with the evidence the
   /// note carries after its first colon in small text and the sources the file binds the note to in a closed block.
   /// Why split at the first colon: the real note reads "redis holds its data in memory: its saved docs page says ...", a short claim and then the evidence for
   /// it, and the claim is what must stand out. The two parts together are the note, word for word.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <returns>HTML fragment; empty when the row has no note.</returns>
   private static string NoteBlock( BenchRow row )
   {
      if( string.IsNullOrWhiteSpace( row.Note ) )
      {
         return string.Empty;
      }

      int colon = row.Note.IndexOf( ": ", StringComparison.Ordinal );
      string label = colon > 0 ? row.Note[..colon] : row.Note;
      string evidence = colon > 0 ? row.Note[( colon + 2 )..] : string.Empty;
      var html = new StringBuilder( $"<div class=\"bench-header-note-block\"><strong class=\"bench-header-memory\">{BenchFormat.Enc( label )}</strong>" );
      html.Append( evidence.Length == 0 ? string.Empty : $" <small class=\"muted bench-header-evidence\">{BenchFormat.Enc( evidence )}</small>" );
      html.Append( $" <small class=\"bench-header-keep\">{BenchFormat.Enc( BenchLegends.L_HEADER_NOTE_KEEP )}</small>" );
      html.Append( row.NoteSourceList.Count > 0 ? BenchConsolidatedHtml.SourceList( row.NoteSourceList ) : string.Empty );
      return html.Append( "</div>" ).ToString();
   }

   /// <summary>
   /// The legends under the table that belong to what the table shows: what the marker means, what a row that is not held is, and where the figures of the rows
   /// that are not ranked are.
   /// </summary>
   /// <param name="lines">The ranked lines.</param>
   /// <param name="unranked">The rows that are not ranked.</param>
   /// <returns>HTML fragment; empty when the table shows nothing that needs a legend.</returns>
   private static string Legends( IReadOnlyList<BenchHeaderLine> lines, IReadOnlyList<BenchRow> unranked )
   {
      var html = new StringBuilder();
      if( lines.Any( l => l.WithinMargin ) )
      {
         html.Append( $"<p class=\"bench-header-legend muted\">{BenchFormat.Enc( BenchLegends.L_HEADER_WITHIN_LEGEND )}</p>" );
      }

      if( unranked.Any( r => r.IsNotHeld ) )
      {
         html.Append( $"<p class=\"bench-header-legend muted\">{BenchFormat.Enc( BenchLegends.NOT_HELD )}</p>" );
      }

      if( unranked.Count > 0 )
      {
         html.Append( $"<p class=\"bench-header-legend muted\">{BenchFormat.Enc( BenchLegends.L_HEADER_FIGURES_IN_FULL )}</p>" );
      }

      return html.ToString();
   }

   /// <summary>
   /// The small text: the build that measured each session's runs as the file records it ("not recorded" for a session whose runs record none) and the
   /// published folder the numbers come from.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <param name="folder">The published folder.</param>
   /// <returns>HTML fragment.</returns>
   private static string BuildLine( JsonElement root, string folder )
   {
      IReadOnlyList<JsonElement> measured = BenchJson.Get( root, "build" ) is { ValueKind: JsonValueKind.Object } build ? BenchJson.Items( build, "measured", "build" ) : Array.Empty<JsonElement>();
      string builds = measured.Count == 0
         ? BenchFormat.Enc( BenchLegends.L_HEADER_NO_BUILD )
         : BenchFormat.Enc( BenchLegends.L_HEADER_BUILD ) + " " + string.Join( ", ", measured.Select( ( m, i ) => BuildOf( m, $"build.measured[{i}]" ) ) ) + ".";
      return $"<p class=\"bench-header-build muted\"><small>{builds} {BenchFormat.Enc( BenchLegends.L_HEADER_SET )} {BenchFormat.Enc( folder )}.</small></p>";
   }

   /// <summary>
   /// One session's build: the session's name and the short commit the file records, or "not recorded".
   /// </summary>
   /// <param name="measured">One entry of the file's build.measured list.</param>
   /// <param name="path">Where it sits in the file, for messages.</param>
   /// <returns>HTML fragment.</returns>
   private static string BuildOf( JsonElement measured, string path )
   {
      string session = BenchJson.RequireText( measured, "session", path );
      string? commit = BenchJson.Text( measured, "commitShort", path ) is { Length: > 0 } shortId ? shortId : BenchJson.Text( measured, "commit", path ) is { Length: > 0 } full ? full[..Math.Min( full.Length, SHORT_COMMIT )] : null;
      return $"{BenchFormat.Enc( session )} " + ( commit == null ? BenchFormat.Enc( BenchLegends.L_HEADER_NOT_RECORDED ) : $"<code>{BenchFormat.Enc( commit )}</code>" );
   }

   /// <summary>
   /// The name of the queries as the first sentence prints it: the words of <see cref="QueryName"/>, and, when the file records the queries as golden, a link
   /// from those words to the Golden Questions page, where the questions can be seen. Any other kind of queries has no questions to show and is not linked.
   /// </summary>
   /// <param name="recorded">The recorded query source, or null.</param>
   /// <returns>HTML fragment.</returns>
   private static string QueryNameHtml( string? recorded )
   {
      string name = BenchFormat.Enc( QueryName( recorded ) );
      return IsGolden( recorded ) ? $"<a href=\"{BenchRoutes.GOLDEN_QUESTIONS}\">{name}</a>" : name;
   }

   /// <summary>
   /// The name of the queries for the first sentence: by the kind the file's recorded query source starts with ("golden:" or "random:"), as the run list
   /// names them; any other kind, and a file with no recorded source, is just "queries".
   /// </summary>
   /// <param name="recorded">The recorded query source, or null.</param>
   /// <returns>The name.</returns>
   private static string QueryName( string? recorded )
   {
      return ( recorded ?? string.Empty ).Split( ':', 2 )[0].Trim() switch
      {
         "golden" => BenchLegends.L_HEADER_GOLDEN,
         "random" => BenchLegends.L_HEADER_RANDOM,
         _ => BenchLegends.L_HEADER_QUERIES,
      };
   }

   /// <summary>
   /// The value a sentence's source gives at a place, or null when the sentence is absent or has no source there.
   /// </summary>
   /// <param name="sentence">The sentence, or null.</param>
   /// <param name="place">The source's ref.</param>
   /// <returns>The value as text, or null.</returns>
   private static string? SourceValue( BenchSentence? sentence, string place )
   {
      return sentence?.Sources.FirstOrDefault( s => s.Ref == place ) is { Value.Length: > 0 } source ? source.Value : null;
   }

   /// <summary>
   /// Milliseconds as the header prints them: three decimals, invariant.
   /// </summary>
   /// <param name="ms">Milliseconds.</param>
   /// <returns>E.g. "0.169".</returns>
   private static string Ms( double ms )
   {
      return ms.ToString( "0.000", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Wraps a body in the header's section.
   /// </summary>
   /// <param name="state">The state the section is in ("stopped", "error"), or null for the normal header.</param>
   /// <param name="body">Body HTML.</param>
   /// <returns>HTML fragment.</returns>
   private static string Section( string? state, string body )
   {
      return $"<section class=\"bench-header\"{( state == null ? string.Empty : $" data-state=\"{state}\"" )}>{body}</section>";
   }

   #endregion Private Methods
}
