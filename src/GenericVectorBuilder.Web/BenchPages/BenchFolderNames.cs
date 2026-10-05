using System.Globalization;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// What a results folder's name says about it: "published-yyyy-mm-dd" is a consolidated set that passed
/// review and may be the summary page; "blocked-..." is a set an independent review blocked, kept as
/// evidence and never the summary; any other name is a run or something unreviewed.
/// Why the name and nothing else: a set is blocked by renaming its folder, so the one place the rule is
/// written is here, and the summary page, the run list and the run page all read it from here.
/// </summary>
public static class BenchFolderNames
{
   #region Data Members

   /// <summary>Start of the name of a published set.</summary>
   public const string PUBLISHED_PREFIX = "published-";

   /// <summary>Start of the name of a set an independent review blocked.</summary>
   public const string BLOCKED_PREFIX = "blocked-";

   private static readonly Regex PUBLISHED = new( "^published-(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})$", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The date of a published folder: only the exact shape "published-" and a real calendar date, nothing after it.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <returns>The date as yyyy-mm-dd, or null for any other name (a suffix, a blocked set, a run, an impossible date).</returns>
   public static string? PublishedDate( string name )
   {
      Match match = PUBLISHED.Match( name );
      string date = match.Groups["date"].Value;
      return match.Success && DateOnly.TryParseExact( date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _ ) ? date : null;
   }

   /// <summary>
   /// True for a folder an independent review blocked.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <returns>True when the name starts with "blocked-".</returns>
   public static bool IsBlocked( string name )
   {
      return name.StartsWith( BLOCKED_PREFIX, StringComparison.Ordinal );
   }

   #endregion Public Methods
}
