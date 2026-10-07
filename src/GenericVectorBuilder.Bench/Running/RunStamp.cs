using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Which build of the benchmark made a run and which boot of the machine it ran on, written into
/// results.json as conditions.build and conditions.boot.
/// Why: two sessions are only comparable when the code that measured them is known to be the same
/// code, and the v7 sessions could say neither which commit built them nor whether the machine had
/// been restarted between them. The commit comes from the assembly's informational version, which
/// the build fills from -p:SourceRevisionId=COMMIT (the frozen build is made that way); a build
/// without it has no commit to record, and a real run refuses to start from it, because a run whose
/// code cannot be named could not be told apart from another.
/// The boot comes from /proc/sys/kernel/random/boot_id (new on every boot) and the btime line of
/// /proc/stat (when the kernel started, in seconds since 1970), so a machine restarted between two
/// sessions shows as two boot ids.
/// </summary>
public sealed record RunStamp( string InformationalVersion, string? Commit, string BootId, string BootTimeUtc )
{
   #region Data Members

   private static readonly Regex COMMIT = new( "^[0-9a-f]{7,40}$", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the stamp of the running program and machine.
   /// </summary>
   /// <param name="requireCommit">True to fail when the build carries no commit (a real run).</param>
   /// <returns>The stamp.</returns>
   /// <exception cref="InvalidOperationException">The build has no commit and one was required, or the boot files cannot be read.</exception>
   public static RunStamp Read( bool requireCommit )
   {
      string version = typeof( RunStamp ).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
      return Parse( version, ReadBoot( "/proc/sys/kernel/random/boot_id" ), ReadBoot( "/proc/stat" ), requireCommit );
   }

   /// <summary>
   /// Builds a stamp from the text of the three sources; the testable part of <see cref="Read"/>.
   /// </summary>
   /// <param name="informationalVersion">The assembly's informational version, e.g. "1.0.0+f662f82...".</param>
   /// <param name="bootId">The text of /proc/sys/kernel/random/boot_id.</param>
   /// <param name="stat">The text of /proc/stat.</param>
   /// <param name="requireCommit">True to fail when the version carries no commit.</param>
   /// <returns>The stamp.</returns>
   /// <exception cref="InvalidOperationException">A required commit is missing, or the boot id or btime cannot be read.</exception>
   public static RunStamp Parse( string informationalVersion, string bootId, string stat, bool requireCommit )
   {
      string? commit = CommitOf( informationalVersion );
      if( commit == null && requireCommit )
      {
         throw new InvalidOperationException( $"This build carries no commit (its informational version is '{informationalVersion}'), so a run could not say which code measured it. Build with: dotnet build -c Release -p:SourceRevisionId=$(git rev-parse HEAD)" );
      }

      string id = bootId.Trim();
      if( id.Length == 0 )
      {
         throw new InvalidOperationException( "/proc/sys/kernel/random/boot_id is empty, so the boot cannot be recorded." );
      }

      string? line = stat.Split( '\n' ).Select( l => l.Trim() ).FirstOrDefault( l => l.StartsWith( "btime ", StringComparison.Ordinal ) );
      if( line == null || !long.TryParse( line[6..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds ) || seconds <= 0 )
      {
         throw new InvalidOperationException( "/proc/stat has no btime line with a number, so the boot time cannot be recorded." );
      }

      string time = DateTimeOffset.FromUnixTimeSeconds( seconds ).UtcDateTime.ToString( "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture );
      return new RunStamp( informationalVersion, commit, id, time );
   }

   /// <summary>
   /// The commit hash an informational version carries after its "+", or null when it has none
   /// or what follows is not a hex hash of 7 to 40 digits.
   /// </summary>
   /// <param name="informationalVersion">The version text.</param>
   /// <returns>The lower-case hash, or null.</returns>
   public static string? CommitOf( string informationalVersion )
   {
      int plus = informationalVersion.IndexOf( '+' );
      string tail = plus < 0 ? string.Empty : informationalVersion[( plus + 1 )..].Trim().ToLowerInvariant();
      return COMMIT.IsMatch( tail ) ? tail : null;
   }

   /// <summary>
   /// The build part as the "conditions.build" JSON object.
   /// </summary>
   /// <returns>The object.</returns>
   public JsonObject BuildJson()
   {
      var build = new JsonObject { ["informationalVersion"] = InformationalVersion };
      if( Commit != null )
      {
         build["commit"] = Commit;
      }

      return build;
   }

   /// <summary>
   /// The boot part as the "conditions.boot" JSON object.
   /// </summary>
   /// <returns>The object.</returns>
   public JsonObject BootJson()
   {
      return new JsonObject { ["bootId"] = BootId, ["bootTimeUtc"] = BootTimeUtc };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads a small /proc file; a file that cannot be read is an error that names it.
   /// </summary>
   /// <param name="path">The file.</param>
   /// <returns>Its text.</returns>
   /// <exception cref="InvalidOperationException">The file cannot be read.</exception>
   private static string ReadBoot( string path )
   {
      try
      {
         return File.ReadAllText( path );
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         throw new InvalidOperationException( $"Could not read {path} to record the boot: {ex.Message}" );
      }
   }

   #endregion Private Methods
}
