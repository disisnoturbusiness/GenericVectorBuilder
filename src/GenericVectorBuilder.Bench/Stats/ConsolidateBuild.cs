using System.Reflection;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The build that makes the report, and the builds that made the runs it reports on.
/// Why both: a run is made by the frozen build named in its own record, and the report is made by whichever build holds the report code; when a defect in the report code is
/// fixed after the runs, the two differ, and a page that names only the first says the runs and the report came from one program. The report names both.
/// How the consolidating build is named: by the commit its assembly carries after the "+" of its informational version, the way a run names its own build, and by the SHA-256 of
/// the assembly file, so a reader can match it to the freeze record. A build with no commit is named as such, never guessed at.
/// </summary>
public static class ConsolidateBuild
{
   #region Data Members

   /// <summary>How many characters of a commit the page prints.</summary>
   public const int SHORT_COMMIT = 12;

   private static readonly Regex COMMIT = new( @"^[0-9a-f]{7,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The build an assembly is.
   /// </summary>
   /// <param name="assembly">The assembly that holds the report code.</param>
   /// <returns>Its version, its commit when it carries one, and the SHA-256 of its file when it has one.</returns>
   public static ConsolidatingBuild Of( Assembly assembly )
   {
      string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
      string? commit = CommitOf( version );
      string location = assembly.Location;
      return new ConsolidatingBuild
      {
         InformationalVersion = version, Commit = commit, CommitShort = commit?[..Math.Min( SHORT_COMMIT, commit.Length )],
         AssemblySha256 = location.Length > 0 && File.Exists( location ) ? EngineFactSheet.FileSha256( location ) : null,
      };
   }

   /// <summary>
   /// The commit an informational version carries after its "+".
   /// </summary>
   /// <param name="version">The version text, or null.</param>
   /// <returns>The lower-case hash of 7 to 40 hex digits, or null when the version has none.</returns>
   public static string? CommitOf( string? version )
   {
      int plus = version?.IndexOf( '+' ) ?? -1;
      string tail = plus < 0 ? string.Empty : version![( plus + 1 )..].Trim().ToLowerInvariant();
      return COMMIT.IsMatch( tail ) ? tail : null;
   }

   /// <summary>
   /// The builds that measured each claim session: the commit its runs record, when they all record the same one.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <returns>One entry per session; its commit is null when its runs record none or record different ones.</returns>
   public static List<SessionBuild> Measured( IReadOnlyList<ClaimSession> sessions )
   {
      return sessions.Select( s =>
      {
         List<string?> commits = s.Runs.Select( r => r.Conditions.BuildCommit?.ToLowerInvariant() ).Distinct( StringComparer.Ordinal ).ToList();
         string? commit = commits.Count == 1 ? commits[0] : null;
         return new SessionBuild { Session = s.Name, Commit = commit, CommitShort = commit?[..Math.Min( SHORT_COMMIT, commit.Length )] };
      } ).ToList();
   }

   #endregion Public Methods
}
