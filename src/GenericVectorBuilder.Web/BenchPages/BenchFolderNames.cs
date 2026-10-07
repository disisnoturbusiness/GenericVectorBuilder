using System.Globalization;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// What a results folder is, from its name alone.
/// </summary>
public enum BenchFolderKind
{
   /// <summary>One benchmark run (20261006-130619-eshoponweb) or any other folder that is none of the kinds below.</summary>
   Run,

   /// <summary>"candidate-...": a consolidated set built and waiting for review; not published.</summary>
   Candidate,

   /// <summary>"published-yyyy-mm-dd": a consolidated set that passed review; the newest one is the summary page.</summary>
   Published,

   /// <summary>"withdrawn-...": a set that was published and then taken back; kept for the record.</summary>
   Withdrawn,

   /// <summary>"blocked-...": a set an independent review blocked; kept as evidence.</summary>
   Blocked,
}

/// <summary>
/// What a results folder's name says about it: "published-yyyy-mm-dd" is a consolidated set that passed
/// review and may be the summary page; "candidate-..." waits for review; "withdrawn-..." was published and
/// taken back; "blocked-..." is a set an independent review blocked, kept as evidence; any other name is a
/// run or something unreviewed. None of the last four is ever the summary.
/// Why the name and nothing else: a set changes state by renaming its folder, so the one place the rule is
/// written is here, and the summary page, the run list and the run page all read it from here.
/// </summary>
public static class BenchFolderNames
{
   #region Data Members

   /// <summary>Start of the name of a published set.</summary>
   public const string PUBLISHED_PREFIX = "published-";

   /// <summary>Start of the name of a set an independent review blocked.</summary>
   public const string BLOCKED_PREFIX = "blocked-";

   /// <summary>Start of the name of a set that waits for review.</summary>
   public const string CANDIDATE_PREFIX = "candidate-";

   /// <summary>Start of the name of a set that was published and then withdrawn.</summary>
   public const string WITHDRAWN_PREFIX = "withdrawn-";

   private static readonly Regex SAFE_NAME = new( "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled );
   private static readonly Regex PUBLISHED = new( "^published-(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})$", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// True when a folder or file name is made of letters, digits, dot, dash and underscore, starts with a letter or digit,
   /// is at most 100 characters long and holds no "..": the only names the pages serve or link to.
   /// </summary>
   /// <param name="name">The name.</param>
   /// <returns>True when the name is safe.</returns>
   public static bool IsSafe( string name )
   {
      return SAFE_NAME.IsMatch( name ) && !name.Contains( ".." );
   }

   /// <summary>
   /// The last segment of a path-like text: the folder name of "a/b/c" or of "a/b/c/". Text from a results file is
   /// untrusted, so a page links to the last segment only after <see cref="IsSafe"/> accepts it.
   /// </summary>
   /// <param name="path">The text.</param>
   /// <returns>The last segment; the text itself when it has no slash.</returns>
   public static string LastSegment( string path )
   {
      string trimmed = path.TrimEnd( '/', '\\' );
      int cut = Math.Max( trimmed.LastIndexOf( '/' ), trimmed.LastIndexOf( '\\' ) );
      return cut < 0 ? trimmed : trimmed[( cut + 1 )..];
   }

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
   /// The kind of a folder from its name. "published-" counts only in the exact dated shape, so a suffixed
   /// or misdated name is a plain run folder and is never served as the summary.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <returns>The kind.</returns>
   public static BenchFolderKind KindOf( string name )
   {
      return PublishedDate( name ) != null ? BenchFolderKind.Published
         : name.StartsWith( BLOCKED_PREFIX, StringComparison.Ordinal ) ? BenchFolderKind.Blocked
         : name.StartsWith( CANDIDATE_PREFIX, StringComparison.Ordinal ) ? BenchFolderKind.Candidate
         : name.StartsWith( WITHDRAWN_PREFIX, StringComparison.Ordinal ) ? BenchFolderKind.Withdrawn
         : BenchFolderKind.Run;
   }

   /// <summary>
   /// Where a folder sits in the list of all folders: a published set first, then a candidate, then a withdrawn set, then a blocked set, then every other folder
   /// (the runs). Why not by name alone: "withdrawn-" sorts after "published-", so a withdrawn set led the list and read as the newest result.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <returns>The position, smallest first.</returns>
   public static int ListPriority( string name )
   {
      return KindOf( name ) switch
      {
         BenchFolderKind.Published => 0,
         BenchFolderKind.Candidate => 1,
         BenchFolderKind.Withdrawn => 2,
         BenchFolderKind.Blocked => 3,
         _ => 4,
      };
   }

   /// <summary>
   /// True for a folder an independent review blocked.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <returns>True when the name starts with "blocked-".</returns>
   public static bool IsBlocked( string name )
   {
      return KindOf( name ) == BenchFolderKind.Blocked;
   }

   /// <summary>
   /// True for a set that waits for review.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <returns>True when the name starts with "candidate-".</returns>
   public static bool IsCandidate( string name )
   {
      return KindOf( name ) == BenchFolderKind.Candidate;
   }

   /// <summary>
   /// True for a set that was published and then withdrawn.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <returns>True when the name starts with "withdrawn-".</returns>
   public static bool IsWithdrawn( string name )
   {
      return KindOf( name ) == BenchFolderKind.Withdrawn;
   }

   /// <summary>
   /// True when a folder name is one of the four kinds of consolidated set (published, candidate,
   /// withdrawn, blocked); a run folder is not.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <returns>True for a set.</returns>
   public static bool IsSet( string name )
   {
      return KindOf( name ) != BenchFolderKind.Run;
   }

   /// <summary>
   /// The banner text a folder of this kind carries at the top of its page, or null when it carries none.
   /// </summary>
   /// <param name="kind">The kind.</param>
   /// <returns>One of the fixed legends, or null.</returns>
   public static string? Banner( BenchFolderKind kind )
   {
      return kind switch
      {
         BenchFolderKind.Candidate => BenchLegends.BANNER_CANDIDATE,
         BenchFolderKind.Withdrawn => BenchLegends.BANNER_WITHDRAWN,
         BenchFolderKind.Blocked => BenchLegends.BANNER_BLOCKED,
         _ => null,
      };
   }

   #endregion Public Methods
}
