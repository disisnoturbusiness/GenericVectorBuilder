using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Code.Chunking;

/// <summary>
/// Builds the one-line context header that starts every code chunk, e.g.
/// "// File: src/Web/Services/BasketService.cs | Namespace: Web.Services | Type: BasketService | Member: AddItem(int)".
/// Why: a chunk is embedded and returned on its own. Without the header a method body says
/// nothing about where it lives, and a search for "basket service add item" has only the body
/// to match. Each part is capped so the header can never crowd out the code it describes.
/// </summary>
internal static partial class ChunkHeader
{
   #region Data Members

   /// <summary>Longest a header can get with every part at its cap.</summary>
   public const int MAX_HEADER_CHARS = 9 + MAX_PATH_CHARS + 14 + MAX_NAMESPACE_CHARS + 9 + MAX_TYPE_CHARS + 11 + MAX_MEMBER_CHARS;

   /// <summary>Room kept for " (part 999/999)" on a member that had to be split.</summary>
   public const int PART_SUFFIX_RESERVE = 16;

   private const int MAX_PATH_CHARS = 120;
   private const int MAX_NAMESPACE_CHARS = 80;
   private const int MAX_TYPE_CHARS = 80;
   private const int MAX_MEMBER_CHARS = 100;
   private const string ELLIPSIS = "...";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Builds the header line. Empty parts are left out.
   /// </summary>
   /// <param name="path">File path; a long one keeps its end, which holds the file name.</param>
   /// <param name="ns">Namespace, or empty.</param>
   /// <param name="type">Type path, or empty.</param>
   /// <param name="member">Member label, or empty.</param>
   /// <returns>One line, no line breaks, at most <see cref="MAX_HEADER_CHARS"/> characters.</returns>
   public static string Build( string path, string ns, string type, string member )
   {
      var parts = new List<string> { "// File: " + Cap( path, MAX_PATH_CHARS, keepEnd: true ) };
      AddPart( parts, "Namespace: ", ns, MAX_NAMESPACE_CHARS );
      AddPart( parts, "Type: ", type, MAX_TYPE_CHARS );
      AddPart( parts, "Member: ", member, MAX_MEMBER_CHARS );
      return string.Join( " | ", parts );
   }

   /// <summary>
   /// The marker appended to the header of each part of a split member.
   /// </summary>
   /// <param name="part">1-based part number.</param>
   /// <param name="parts">Number of parts.</param>
   /// <returns>E.g. " (part 2/3)".</returns>
   public static string PartSuffix( int part, int parts )
   {
      return $" (part {part}/{parts})";
   }

   /// <summary>
   /// Joins labels for a group of pieces, keeping as many whole labels as fit and saying how
   /// many more there were, so the header stays short but honest.
   /// </summary>
   /// <param name="labels">Labels in order; empty and repeated ones are skipped.</param>
   /// <returns>The joined label.</returns>
   public static string JoinLabels( IEnumerable<string> labels )
   {
      List<string> distinct = labels.Where( l => l.Length > 0 ).Distinct( StringComparer.Ordinal ).ToList();
      string joined = string.Join( ", ", distinct );
      if( joined.Length <= MAX_MEMBER_CHARS )
      {
         return joined;
      }

      int kept = 0;
      int length = 0;
      while( kept < distinct.Count && length + distinct[kept].Length + 2 <= MAX_MEMBER_CHARS - 16 )
      {
         length += distinct[kept].Length + 2;
         kept++;
      }

      return kept == 0 ? joined : $"{string.Join( ", ", distinct.Take( kept ) )} (+{distinct.Count - kept} more)";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Adds "Name: value" when the value is not empty.
   /// </summary>
   /// <param name="parts">Header parts so far.</param>
   /// <param name="name">Part name with its colon and space.</param>
   /// <param name="value">Value.</param>
   /// <param name="max">Longest value kept.</param>
   private static void AddPart( List<string> parts, string name, string value, int max )
   {
      if( !string.IsNullOrWhiteSpace( value ) )
      {
         parts.Add( name + Cap( value, max, keepEnd: false ) );
      }
   }

   /// <summary>
   /// Flattens whitespace (including line breaks) to single spaces and cuts the value to a
   /// maximum length, marking the cut with "...".
   /// </summary>
   /// <param name="value">Value.</param>
   /// <param name="max">Longest result.</param>
   /// <param name="keepEnd">Keep the end instead of the start.</param>
   /// <returns>The capped value.</returns>
   private static string Cap( string value, int max, bool keepEnd )
   {
      string flat = WHITESPACE().Replace( value, " " ).Trim();
      if( flat.Length <= max )
      {
         return flat;
      }

      int keep = max - ELLIPSIS.Length;
      return keepEnd ? ELLIPSIS + flat[^keep..] : flat[..keep] + ELLIPSIS;
   }

   /// <summary>Any run of whitespace.</summary>
   [GeneratedRegex( @"\s+" )]
   private static partial Regex WHITESPACE();

   #endregion Private Methods
}
