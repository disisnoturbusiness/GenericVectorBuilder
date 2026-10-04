using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// One results folder as the "All runs" list shows it.
/// </summary>
/// <param name="Folder">Folder name, used for the link.</param>
/// <param name="When">Readable start time (UTC) or date, e.g. "4 Oct 2026, 13:27".</param>
/// <param name="Data">What was searched, e.g. "eShopOnWeb, 524 vectors".</param>
/// <param name="Engines">How many engines, as text ("19"), or "-" when unknown.</param>
/// <param name="Queries">What was asked, e.g. "20 labelled questions", or "-".</param>
public sealed record BenchRunInfo( string Folder, string When, string Data, string Engines, string Queries );

/// <summary>
/// Builds the "All runs" list from the results folders.
/// Why a table and readable dates: folder names like 20261004-132735-eshoponweb were the whole
/// old list page, which nobody could scan.
/// A file that cannot be read shows its error in the row instead of dropping the run.
/// </summary>
public static class BenchRunList
{
   #region Data Members

   /// <summary>Largest file the pages read (JSON or Markdown).</summary>
   public const int MAX_FILE_BYTES = 5 * 1024 * 1024;

   private static readonly Regex RUN_NAME = new( "^(\\d{8})-(\\d{6})-", RegexOptions.Compiled );
   private static readonly Regex DATE_IN_NAME = new( "(\\d{4}-\\d{2}-\\d{2})", RegexOptions.Compiled );
   private static readonly Dictionary<string, string> PIPELINES = new( StringComparer.OrdinalIgnoreCase )
   {
      ["eshoponweb"] = "eShopOnWeb",
      ["adventureworks"] = "AdventureWorks",
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Describes one folder from its results.json, or its consolidated.json, or its file names.
   /// </summary>
   /// <param name="folder">The folder.</param>
   /// <returns>The row.</returns>
   public static BenchRunInfo Describe( DirectoryInfo folder )
   {
      string results = Path.Combine( folder.FullName, "results.json" );
      string consolidated = Path.Combine( folder.FullName, "consolidated.json" );
      try
      {
         if( File.Exists( results ) )
         {
            return FromResults( folder.Name, ReadCapped( results ) );
         }

         if( File.Exists( consolidated ) )
         {
            return FromConsolidated( folder.Name, ReadCapped( consolidated ) );
         }
      }
      catch( Exception ex ) when( ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException )
      {
         return new BenchRunInfo( folder.Name, WhenFromName( folder.Name ), $"unreadable: {ex.Message}", "-", "-" );
      }

      string[] files = folder.GetFiles().Select( f => f.Name ).OrderBy( f => f, StringComparer.Ordinal ).ToArray();
      return new BenchRunInfo( folder.Name, WhenFromName( folder.Name ), files.Length == 0 ? "empty folder" : string.Join( ", ", files ), "-", "-" );
   }

   /// <summary>
   /// Renders the rows as a table styled like the run pages' tables, each date linking to its run.
   /// </summary>
   /// <param name="runs">Rows, already in display order.</param>
   /// <returns>HTML fragment.</returns>
   public static string Html( IEnumerable<BenchRunInfo> runs )
   {
      var html = new StringBuilder( "<div class=\"preview\"><table class=\"bench-table bench-runs\"><thead><tr><th>When (UTC)</th><th>Data</th><th class=\"n\">Engines</th><th>Queries</th></tr></thead><tbody>" );
      foreach( BenchRunInfo run in runs )
      {
         html.Append( $"<tr><td><a href=\"/bench-results/{Enc( run.Folder )}\" title=\"{Enc( run.Folder )}\">{Enc( run.When )}</a></td>" );
         html.Append( $"<td>{Enc( run.Data )}</td><td class=\"n\">{Enc( run.Engines )}</td><td>{Enc( run.Queries )}</td></tr>" );
      }

      return html.Append( "</tbody></table></div>" ).ToString();
   }

   /// <summary>
   /// Reads a results file as text, refusing anything over <see cref="MAX_FILE_BYTES"/>.
   /// Why a cap: these pages read files on every request, so a huge file must fail loud, not stall the page.
   /// </summary>
   /// <param name="path">File path.</param>
   /// <returns>File text.</returns>
   public static string ReadCapped( string path )
   {
      long size = new FileInfo( path ).Length;
      if( size > MAX_FILE_BYTES )
      {
         throw new InvalidDataException( $"{Path.GetFileName( path )} is {size:N0} bytes, over the {MAX_FILE_BYTES:N0} byte limit." );
      }

      return File.ReadAllText( path );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A row from one run's results.json: start time, pipeline and row count, target count, queries.
   /// </summary>
   /// <param name="folder">Folder name.</param>
   /// <param name="json">results.json text.</param>
   /// <returns>The row.</returns>
   private static BenchRunInfo FromResults( string folder, string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement r = doc.RootElement;
      if( r.ValueKind != JsonValueKind.Object )
      {
         throw new JsonException( "results.json is not an object." );
      }

      string when = Text( r, "startedUtc" ) is string started && DateTime.TryParse( started, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime at )
         ? at.ToString( "d MMM yyyy, HH:mm", CultureInfo.InvariantCulture )
         : WhenFromName( folder );
      string pipeline = Text( r, "pipeline" ) is string p ? PIPELINES.GetValueOrDefault( p, p ) : "?";
      string data = r.TryGetProperty( "rows", out JsonElement rows ) && rows.ValueKind == JsonValueKind.Number && rows.TryGetInt64( out long n ) ? $"{pipeline}, {n.ToString( "N0", CultureInfo.InvariantCulture )} vectors" : pipeline;
      string engines = r.TryGetProperty( "targets", out JsonElement t ) && t.ValueKind == JsonValueKind.Array ? t.GetArrayLength().ToString( CultureInfo.InvariantCulture ) : "-";
      return new BenchRunInfo( folder, when, data, engines, Queries( r ) );
   }

   /// <summary>
   /// A row from consolidated.json: medians across runs, one engine per top-level key.
   /// </summary>
   /// <param name="folder">Folder name.</param>
   /// <param name="json">consolidated.json text.</param>
   /// <returns>The row.</returns>
   private static BenchRunInfo FromConsolidated( string folder, string json )
   {
      BenchSummary summary = BenchSummaryReader.FromConsolidated( json );
      int engines = summary.Ranked.Count + summary.NoResult.Count;
      return new BenchRunInfo( folder, WhenFromName( folder ), $"Published medians of {summary.Runs} runs", engines.ToString( CultureInfo.InvariantCulture ), "-" );
   }

   /// <summary>
   /// Plain words for the query set: "20 labelled questions" or "200 random stored vectors".
   /// </summary>
   /// <param name="r">results.json root.</param>
   /// <returns>Text.</returns>
   private static string Queries( JsonElement r )
   {
      string kind = ( Text( r, "queries" ) ?? "-" ).Split( ':', 2 )[0].Trim();
      string count = r.TryGetProperty( "queryCount", out JsonElement c ) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32( out int n ) ? n.ToString( CultureInfo.InvariantCulture ) : string.Empty;
      return kind switch
      {
         "golden" => $"{count} labelled questions".Trim(),
         "random" => $"{count} random stored vectors".Trim(),
         _ => kind,
      };
   }

   /// <summary>
   /// Readable time from a run folder name (20261004-132735-...), a date inside any other name
   /// (published-2026-10-04), or the name itself.
   /// </summary>
   /// <param name="folder">Folder name.</param>
   /// <returns>Text.</returns>
   private static string WhenFromName( string folder )
   {
      Match run = RUN_NAME.Match( folder );
      if( run.Success && DateTime.TryParseExact( run.Groups[1].Value + run.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at ) )
      {
         return at.ToString( "d MMM yyyy, HH:mm", CultureInfo.InvariantCulture );
      }

      Match date = DATE_IN_NAME.Match( folder );
      return date.Success && DateTime.TryParseExact( date.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day )
         ? day.ToString( "d MMM yyyy", CultureInfo.InvariantCulture )
         : folder;
   }

   /// <summary>
   /// Reads a string property, or null.
   /// </summary>
   /// <param name="parent">Object.</param>
   /// <param name="name">Property.</param>
   /// <returns>The string or null.</returns>
   private static string? Text( JsonElement parent, string name )
   {
      return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty( name, out JsonElement v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
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
