using System.Globalization;
using System.Net;
using System.Text;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// Draws the summary's horizontal bar chart as inline SVG on the server: one bar per engine in
/// rank order, the value at each bar's tip, a thin min to max line when the numbers are medians,
/// and a hatched bar for an engine that holds everything in memory.
/// Why server-side SVG with no script: the page must load with nothing from outside the box.
/// Why horizontal positions are percentages and text sizes are pixels: the chart then stretches
/// to any width, 375 px phones included, while the names stay readable instead of shrinking
/// with a viewBox. Names sit above their bar for the same reason: a left label column would eat
/// a phone screen. Colors come from style.css tokens, so light and dark both work.
/// When the numbers are medians of several runs each name carries its tie band ("band 2"):
/// engines whose min to max lines overlap share a band, and the bars are in median order, which
/// is not a ranking inside a band.
/// </summary>
public static class BenchChart
{
   #region Data Members

   /// <summary>Share of the width the longest bar or range line reaches; the rest holds its value label.</summary>
   public const double PLOT_PERCENT = 78.0;

   /// <summary>Height of one engine's row (name line plus bar), in pixels.</summary>
   public const int ROW_HEIGHT = 38;

   private const int TOP_PAD = 2;
   private const int NAME_BASELINE = 14;
   private const int BAR_TOP = 20;
   private const int BAR_HEIGHT = 12;
   private const int TICK_HALF = 5;
   private const int CORNER = 4;
   private const double MIN_SQUARE_PERCENT = 2.0;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Builds the chart for a summary. Every name and number is HTML-escaped.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>An inline &lt;svg&gt; element, or an empty string when no engine has a result.</returns>
   public static string Svg( BenchSummary summary )
   {
      IReadOnlyList<BenchEngineRow> rows = summary.Ranked;
      if( rows.Count == 0 )
      {
         return string.Empty;
      }

      double top = rows.Max( r => Math.Max( r.Qps8, summary.HasRanges ? r.Qps8Max ?? 0 : 0 ) );
      double scale = top > 0 ? PLOT_PERCENT / top : 0;
      int height = TOP_PAD * 2 + rows.Count * ROW_HEIGHT;
      var svg = new StringBuilder();
      svg.Append( $"<svg class=\"bench-chart\" xmlns=\"http://www.w3.org/2000/svg\" width=\"100%\" height=\"{height}\" role=\"img\" aria-label=\"{Enc( AriaLabel( summary ) )}\">" );
      svg.Append( "<defs><pattern id=\"bc-hatch\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\">" );
      svg.Append( "<rect class=\"bc-hatch-bg\" width=\"6\" height=\"6\"/><line class=\"bc-hatch-line\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\"/></pattern></defs>" );
      for( int i = 0; i < rows.Count; i++ )
      {
         AppendRow( svg, rows[i], TOP_PAD + i * ROW_HEIGHT, scale, summary.HasRanges );
      }

      svg.Append( "</svg>" );
      return svg.ToString();
   }

   /// <summary>
   /// The hover text for one engine's row, e.g. "MariaDB: 4,558 searches/s with 8 at once,
   /// median (4,132 to 4,559 across runs), band 1".
   /// </summary>
   /// <param name="row">Engine.</param>
   /// <param name="ranges">True when the numbers are medians with min and max.</param>
   /// <returns>Plain text, not yet escaped.</returns>
   public static string Tooltip( BenchEngineRow row, bool ranges )
   {
      string text = $"{row.Name}: {Count( row.Qps8 )} searches/s with 8 at once";
      if( ranges && row.Qps8Min != null && row.Qps8Max != null )
      {
         text += $", median ({Count( row.Qps8Min.Value )} to {Count( row.Qps8Max.Value )} across runs)";
      }

      if( ranges && row.Band != null )
      {
         text += $", band {row.Band}";
      }

      return row.InMemory ? text + ". Holds everything in memory." : text;
   }

   /// <summary>
   /// Formats a searches-per-second number the way every summary shows it: whole, with commas.
   /// </summary>
   /// <param name="value">Searches per second.</param>
   /// <returns>E.g. "4,558".</returns>
   public static string Count( double value )
   {
      return Math.Round( value, MidpointRounding.AwayFromZero ).ToString( "N0", CultureInfo.InvariantCulture );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Draws one engine: name (and an "in memory" note), the bar, the optional range line with
   /// end ticks, and the value just past whichever reaches further, the bar or the range.
   /// </summary>
   /// <param name="svg">Output.</param>
   /// <param name="row">Engine.</param>
   /// <param name="y">Top of the row in pixels.</param>
   /// <param name="scale">Percent of width per search per second.</param>
   /// <param name="ranges">True to draw min to max.</param>
   private static void AppendRow( StringBuilder svg, BenchEngineRow row, int y, double scale, bool ranges )
   {
      string bar = row.InMemory ? "bc-bar bc-mem" : "bc-bar";
      double width = row.Qps8 * scale;
      double labelAt = width;
      int barMid = y + BAR_TOP + BAR_HEIGHT / 2;
      svg.Append( $"<g class=\"bc-row\" data-engine=\"{Enc( row.Key )}\"><title>{Enc( Tooltip( row, ranges ) )}</title>" );
      svg.Append( $"<text class=\"bc-name\" x=\"0\" y=\"{y + NAME_BASELINE}\">{Enc( row.Name )}" );
      svg.Append( row.InMemory ? "<tspan class=\"bc-note\" dx=\"8\">in memory</tspan>" : string.Empty );
      svg.Append( ranges && row.Band != null ? $"<tspan class=\"bc-note\" dx=\"8\">band {row.Band}</tspan></text>" : "</text>" );
      svg.Append( $"<rect class=\"{bar}\" x=\"0\" y=\"{y + BAR_TOP}\" width=\"{Pct( width )}\" height=\"{BAR_HEIGHT}\" rx=\"{CORNER}\"/>" );
      if( width >= MIN_SQUARE_PERCENT )
      {
         svg.Append( $"<rect class=\"{bar}\" x=\"0\" y=\"{y + BAR_TOP}\" width=\"{CORNER}\" height=\"{BAR_HEIGHT}\"/>" );
      }

      if( ranges && row.Qps8Min != null && row.Qps8Max != null )
      {
         string lo = Pct( row.Qps8Min.Value * scale );
         string hi = Pct( row.Qps8Max.Value * scale );
         svg.Append( $"<line class=\"bc-range\" x1=\"{lo}\" y1=\"{barMid}\" x2=\"{hi}\" y2=\"{barMid}\"/>" );
         svg.Append( $"<line class=\"bc-range\" x1=\"{lo}\" y1=\"{barMid - TICK_HALF}\" x2=\"{lo}\" y2=\"{barMid + TICK_HALF}\"/>" );
         svg.Append( $"<line class=\"bc-range\" x1=\"{hi}\" y1=\"{barMid - TICK_HALF}\" x2=\"{hi}\" y2=\"{barMid + TICK_HALF}\"/>" );
         labelAt = Math.Max( labelAt, row.Qps8Max.Value * scale );
      }

      svg.Append( $"<text class=\"bc-value\" x=\"{Pct( labelAt )}\" dx=\"8\" y=\"{barMid + 4}\">{Enc( Count( row.Qps8 ) )}</text></g>" );
   }

   /// <summary>
   /// The chart's screen reader label: what it plots and where the same numbers are as text.
   /// </summary>
   /// <param name="summary">Ranked engines.</param>
   /// <returns>Plain text.</returns>
   private static string AriaLabel( BenchSummary summary )
   {
      string what = summary.HasRanges ? $"median of {summary.Runs} runs" : "one run";
      string bands = summary.HasRanges ? " Engines with the same band number have overlapping ranges, so their order is not a ranking." : string.Empty;
      return $"Bar chart of searches per second with 8 at once for {summary.Ranked.Count} engines, {what}, in median order.{bands} The same numbers are in the table below.";
   }

   /// <summary>
   /// Formats a percentage for an SVG attribute with a dot decimal whatever the server culture.
   /// </summary>
   /// <param name="percent">0 to 100.</param>
   /// <returns>E.g. "56.25%".</returns>
   private static string Pct( double percent )
   {
      return Math.Clamp( percent, 0, 100 ).ToString( "0.##", CultureInfo.InvariantCulture ) + "%";
   }

   /// <summary>
   /// HTML-encodes text for element content and attribute values.
   /// </summary>
   /// <param name="text">Text.</param>
   /// <returns>Encoded text.</returns>
   private static string Enc( string text )
   {
      return WebUtility.HtmlEncode( text );
   }

   #endregion Private Methods
}
