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
/// <param name="Marks">The consolidated folders that use this run, each as "folder (role)", best first; empty for a run no folder uses.</param>
public sealed record BenchRunInfo( string Folder, string When, string Data, string Engines, string Queries, IReadOnlyList<( string Folder, string Role )>? Marks = null );

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
   /// The name a reader sees for a pipeline (data set) name as a results file records it, for example "eShopOnWeb" for "eshoponweb"; the name itself
   /// when this table does not know it. One table for the run list and the header of the Vector search benchmark page, so the data set is spelled one way.
   /// </summary>
   /// <param name="pipeline">The pipeline name as recorded.</param>
   /// <returns>The name to print.</returns>
   public static string PipelineName( string pipeline )
   {
      return PIPELINES.GetValueOrDefault( pipeline, pipeline );
   }

   /// <summary>
   /// Describes one folder from its results.json, or its consolidated.json, or its file names, and marks it with
   /// the consolidated folders that use it.
   /// </summary>
   /// <param name="folder">The folder.</param>
   /// <param name="facts">Every consolidated folder's facts (see <see cref="BenchRunUse.Scan"/>); null for none.</param>
   /// <returns>The row.</returns>
   public static BenchRunInfo Describe( DirectoryInfo folder, IReadOnlyList<BenchFolderFacts>? facts = null )
   {
      string results = Path.Combine( folder.FullName, "results.json" );
      string consolidated = Path.Combine( folder.FullName, "consolidated.json" );
      IReadOnlyList<( string Folder, string Role )> marks = facts == null ? Array.Empty<( string, string )>() : BenchRunUse.MarksFor( facts, folder.Name ).Select( m => ( m.Folder.Folder, m.Role ) ).ToList();
      try
      {
         if( File.Exists( results ) )
         {
            return FromResults( folder.Name, ReadCapped( results ) ) with { Marks = marks };
         }

         if( File.Exists( consolidated ) )
         {
            BenchFolderFacts own = facts?.FirstOrDefault( f => f.Folder == folder.Name ) ?? throw new InvalidDataException( "consolidated.json was not scanned." );
            return own.Error != null ? throw new InvalidDataException( own.Error ) : FromConsolidated( own );
         }
      }
      catch( Exception ex ) when( ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException )
      {
         return new BenchRunInfo( folder.Name, WhenFromName( folder.Name ), $"unreadable: {ex.Message}", "-", "-", marks );
      }

      string[] files = folder.GetFiles().Select( f => f.Name ).OrderBy( f => f, StringComparer.Ordinal ).ToArray();
      return new BenchRunInfo( folder.Name, WhenFromName( folder.Name ), files.Length == 0 ? "empty folder" : string.Join( ", ", files ), "-", "-", marks );
   }

   /// <summary>
   /// Renders the rows as a table styled like the run pages' tables, each date linking to its run, with the
   /// folders that use a run in the last column.
   /// </summary>
   /// <param name="runs">Rows, already in display order.</param>
   /// <returns>HTML fragment.</returns>
   public static string Html( IEnumerable<BenchRunInfo> runs )
   {
      var html = new StringBuilder( "<div class=\"preview\"><table class=\"bench-table bench-runs\"><thead><tr><th>When (UTC)</th><th>Data</th><th class=\"n\">Engines</th><th>Queries</th><th>" + BenchLegends.USED_BY + "</th></tr></thead><tbody>" );
      foreach( BenchRunInfo run in runs )
      {
         html.Append( $"<tr><td><a href=\"/bench-results/{Enc( run.Folder )}\" title=\"{Enc( run.Folder )}\">{Enc( run.When )}</a></td>" );
         html.Append( $"<td>{Enc( run.Data )}</td><td class=\"n\">{Enc( run.Engines )}</td><td>{Enc( run.Queries )}</td><td>{MarkLinks( run.Marks )}</td></tr>" );
      }

      return html.Append( "</tbody></table></div>" ).ToString();
   }

   /// <summary>
   /// The folders that use a run as links, each folder once with the roles the run plays there ("v7, basis").
   /// </summary>
   /// <param name="marks">The marks, or null.</param>
   /// <returns>HTML fragment; "-" when no folder uses the run.</returns>
   public static string MarkLinks( IReadOnlyList<( string Folder, string Role )>? marks )
   {
      if( marks is not { Count: > 0 } )
      {
         return "-";
      }

      return string.Join( ", ", marks.GroupBy( m => m.Folder, StringComparer.Ordinal )
         .Select( g => $"<a href=\"/bench-results/{Enc( g.Key )}\">{Enc( g.Key )}</a> ({Enc( string.Join( ", ", g.Select( m => m.Role ).Distinct( StringComparer.Ordinal ) ) )})" ) );
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
      string pipeline = Text( r, "pipeline" ) is string p ? PipelineName( p ) : "?";
      string data = r.TryGetProperty( "rows", out JsonElement rows ) && rows.ValueKind == JsonValueKind.Number && rows.TryGetInt64( out long n ) ? $"{pipeline}, {n.ToString( "N0", CultureInfo.InvariantCulture )} vectors" : pipeline;
      string engines = r.TryGetProperty( "targets", out JsonElement t ) && t.ValueKind == JsonValueKind.Array ? t.GetArrayLength().ToString( CultureInfo.InvariantCulture ) : "-";
      return new BenchRunInfo( folder, when, data, engines, Queries( r ) );
   }

   /// <summary>
   /// A row from a consolidated folder's facts: its label by kind, how many runs it uses and how many engines.
   /// </summary>
   /// <param name="facts">The folder's facts.</param>
   /// <returns>The row.</returns>
   private static BenchRunInfo FromConsolidated( BenchFolderFacts facts )
   {
      return new BenchRunInfo( facts.Folder, WhenFromName( facts.Folder ), ConsolidatedLabel( facts ), facts.EngineCount.ToString( CultureInfo.InvariantCulture ), "-" );
   }

   /// <summary>
   /// What a folder of medians is, by its name and the shape of its file: only an exact "published-yyyy-mm-dd" is
   /// called published, and every other kind says what it is. A file in a shape older than v8 says so.
   /// Why not "Published" for all of them: a blocked set listed as published medians reads as the result. A folder named "published-" whose file is in an
   /// older shape is not called published either: the page draws no result from it, and the label says so.
   /// </summary>
   /// <param name="facts">The folder's facts.</param>
   /// <returns>The text for the Data column.</returns>
   private static string ConsolidatedLabel( BenchFolderFacts facts )
   {
      int runs = facts.Runs.Where( r => !r.IsBasis ).Select( r => r.Run ).Distinct( StringComparer.Ordinal ).Count();
      string what = facts.Shape == BenchShape.Consolidated ? $"medians of {runs} runs" : "medians, older format";
      return facts.Kind switch
      {
         BenchFolderKind.Published when facts.Shape != BenchShape.Consolidated => $"{char.ToUpperInvariant( what[0] )}{what[1..]} (not shown as a result)",
         BenchFolderKind.Published => $"Published {what}",
         BenchFolderKind.Candidate => $"Candidate {what} (not published)",
         BenchFolderKind.Withdrawn => $"Withdrawn {what} (not the current result)",
         BenchFolderKind.Blocked => $"Blocked {what} (marked blocked; not published)",
         _ => $"{char.ToUpperInvariant( what[0] )}{what[1..]} (not a published set)",
      };
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
