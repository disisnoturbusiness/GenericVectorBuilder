using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The segment layout an engine reported for its collection, as one comparable text such as
/// "2 segments" or "1 sealed segment covering 524 rows".
/// Why: Qdrant ended a replicate load with 3 or 4 segments and a run-all load with 2, and Milvus
/// compacted two sealed segments covering 1,048 rows for 524 stored during the timed passes; runs
/// searched against different layouts are different experiments and must not be averaged.
/// Where it is read from: a structured "segmentLayout" text or "segments" number on the index
/// state when a writer gives one, else the engine's own evidence text ("2 segments",
/// "1 segment(s)", "1 sealed segment(s) with the index loaded covering 524 rows"). Counts of
/// searches that merely contain the word ("98,871 segment searches") are not layouts.
/// </summary>
public static class SegmentLayout
{
   #region Data Members

   private static readonly Regex IN_TEXT = new( @"(?<n>\d[\d,]*)\s+(?<kind>sealed\s+|growing\s+)?segment(?:s|\(s\))?(?!\w)(?!\s+search)(?:\s+with the index loaded)?(?:\s+covering\s+(?<rows>\d[\d,]*)\s+rows)?", RegexOptions.Compiled | RegexOptions.IgnoreCase );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the layout from an index state object, structured fields first.
   /// </summary>
   /// <param name="state">The state object (afterLoad or afterSearch).</param>
   /// <param name="detail">The state's evidence text, or null.</param>
   /// <returns>The layout text, or null when the engine reported none.</returns>
   public static string? Read( JsonElement state, string? detail )
   {
      if( ResultJson.Text( state, "segmentLayout" ) is string text && text.Trim().Length > 0 )
      {
         return text.Trim();
      }

      if( ResultJson.Int( state, "segments" ) is int count && count >= 0 )
      {
         return Describe( count, null, null );
      }

      return FromText( detail );
   }

   /// <summary>
   /// Finds the first segment count in an evidence text.
   /// </summary>
   /// <param name="detail">The engine's evidence text, or null.</param>
   /// <returns>The layout text, or null when the text names no segment count.</returns>
   public static string? FromText( string? detail )
   {
      if( detail == null || IN_TEXT.Match( detail ) is not { Success: true } m || !TryCount( m.Groups["n"].Value, out long count ) )
      {
         return null;
      }

      string kind = m.Groups["kind"].Success ? m.Groups["kind"].Value.Trim().ToLowerInvariant() : string.Empty;
      long? rows = m.Groups["rows"].Success && TryCount( m.Groups["rows"].Value, out long covered ) ? covered : null;
      return Describe( count, kind.Length == 0 ? null : kind, rows );
   }

   /// <summary>
   /// The engine's own words for its layout: the part of the evidence text that names the segment
   /// count, exactly as written (for example "4 segment(s)").
   /// Why: a sentence that prints the layout must print text that is in the run's record, so the
   /// audit can find it there.
   /// </summary>
   /// <param name="detail">The engine's evidence text, or null.</param>
   /// <returns>The matched text, or null when the text names no segment count.</returns>
   public static string? RawMatch( string? detail )
   {
      return detail != null && IN_TEXT.Match( detail ) is { Success: true } m ? m.Value : null;
   }

   /// <summary>
   /// Writes a layout the same way whatever spelling the engine used.
   /// </summary>
   /// <param name="count">Segments.</param>
   /// <param name="kind">"sealed" or "growing", or null.</param>
   /// <param name="rows">Rows the segments cover, or null.</param>
   /// <returns>E.g. "1 sealed segment covering 524 rows".</returns>
   public static string Describe( long count, string? kind, long? rows )
   {
      string noun = count == 1 ? "segment" : "segments";
      string covering = rows.HasValue ? $" covering {rows.Value.ToString( "N0", CultureInfo.InvariantCulture )} rows" : string.Empty;
      return $"{count.ToString( "N0", CultureInfo.InvariantCulture )} {( kind == null ? string.Empty : kind + " " )}{noun}{covering}";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Parses a count written with thousands commas.
   /// </summary>
   /// <param name="text">E.g. "1,048".</param>
   /// <param name="value">The number.</param>
   /// <returns>True when it parsed.</returns>
   private static bool TryCount( string text, out long value )
   {
      return long.TryParse( text.Replace( ",", string.Empty ), NumberStyles.Integer, CultureInfo.InvariantCulture, out value );
   }

   #endregion Private Methods
}
