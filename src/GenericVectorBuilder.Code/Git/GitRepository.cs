using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Configuration;

namespace GenericVectorBuilder.Code.Git;

/// <summary>
/// A local, shallow copy of a git repository that the code source reads from. The first sync
/// clones the latest commit only (depth 1); later syncs fetch the latest commit and hard-reset
/// to it, so the folder always matches the remote exactly and nothing local survives.
/// Why a shallow clone: the builder only ever reads the current files, never history, and a
/// depth-1 clone of a large repository is a fraction of the full download.
/// Why the commit is exposed: it is stored with every chunk, so a search hit says exactly
/// which version of the code it came from.
/// Why addresses are checked before git sees them: only https, ssh and local folders are
/// accepted, a password inside the address is refused (git would save it in plain text in the
/// clone's config), and anything that could be read as a git option or a command transport is
/// refused outright.
/// </summary>
public sealed partial class GitRepository
{
   #region Data Members

   /// <summary>Where clones live unless the caller says otherwise.</summary>
   public const string DEFAULT_REPOS_ROOT = "~/gvb-data/repos";

   private const int MAX_NAME_CHARS = 80;
   private const int MAX_DETAIL_CHARS = 400;
   private static readonly TimeSpan GIT_TIMEOUT = TimeSpan.FromMinutes( 10 );
   private static readonly ConcurrentDictionary<string, SemaphoreSlim> LOCKS = new( StringComparer.Ordinal );

   private readonly string _reposRoot;
   private readonly bool _isLocal;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Describes a repository and where its local copy lives. Nothing is cloned until
   /// <see cref="SyncAsync"/> runs.
   /// </summary>
   /// <param name="url">An https or ssh address, a "user@host:owner/repo" address, or an absolute local folder.</param>
   /// <param name="reposRoot">Folder that holds every clone; defaults to <see cref="DEFAULT_REPOS_ROOT"/>.</param>
   /// <param name="branch">Branch to follow, or null for the remote's default branch.</param>
   /// <exception cref="ArgumentException">The address or branch is not acceptable; the message says why.</exception>
   public GitRepository( string url, string? reposRoot = null, string? branch = null )
   {
      string trimmed = ( url ?? string.Empty ).Trim();
      string? problem = ValidateUrl( trimmed ) ?? ValidateBranch( branch );
      if( problem != null )
      {
         throw new ArgumentException( problem, nameof( url ) );
      }

      _isLocal = IsLocalPath( trimmed );
      Url = _isLocal ? GvbSettings.Expand( trimmed ) : trimmed;
      Branch = string.IsNullOrWhiteSpace( branch ) ? null : branch.Trim();
      _reposRoot = GvbSettings.Expand( reposRoot ?? DEFAULT_REPOS_ROOT );
      LocalPath = Path.Combine( _reposRoot, SanitizeName( Url ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The repository address as given (a local folder is made absolute).</summary>
   public string Url { get; }

   /// <summary>The branch followed, or null for the remote's default branch.</summary>
   public string? Branch { get; }

   /// <summary>The folder holding the local copy, e.g. "/home/dan/gvb-data/repos/dotnet-architecture_eShopOnWeb".</summary>
   public string LocalPath { get; }

   /// <summary>The full commit id the local copy is at, known after <see cref="SyncAsync"/>.</summary>
   public string? CommitSha { get; private set; }

   /// <summary>
   /// Brings the local copy to the remote's latest commit: clones it when missing, otherwise
   /// fetches and hard-resets, removing every local change and untracked file. Two syncs of the
   /// same folder in this process wait for each other.
   /// </summary>
   /// <param name="ct">Cancellation; a running git command is stopped.</param>
   /// <returns>The commit id the copy is now at.</returns>
   public Task<string> SyncAsync( CancellationToken ct )
   {
      return SyncAsync( null, ct );
   }

   /// <summary>
   /// Same as <see cref="SyncAsync(CancellationToken)"/>, reporting what it is doing as it goes:
   /// a plain line when each step starts, then git's own progress lines ("Receiving objects:
   /// 45% (1203/2671)"). Why: a large clone can take minutes, and without these lines a slow
   /// clone and a stuck one look the same.
   /// </summary>
   /// <param name="onProgress">Receives each step and progress line, or null for none. Called from
   /// background tasks, so it must be quick and thread-safe.</param>
   /// <param name="ct">Cancellation; a running git command is stopped.</param>
   /// <returns>The commit id the copy is now at.</returns>
   public async Task<string> SyncAsync( Action<string>? onProgress, CancellationToken ct )
   {
      SemaphoreSlim gate = LOCKS.GetOrAdd( LocalPath, _ => new SemaphoreSlim( 1, 1 ) );
      await gate.WaitAsync( ct );
      try
      {
         Directory.CreateDirectory( _reposRoot );
         if( Directory.Exists( Path.Combine( LocalPath, ".git" ) ) )
         {
            await UpdateAsync( onProgress, ct );
         }
         else if( Directory.Exists( LocalPath ) || File.Exists( LocalPath ) )
         {
            throw new InvalidOperationException( $"{LocalPath} already exists but is not a git copy. Remove it or use a different repository." );
         }
         else
         {
            await CloneAsync( onProgress, ct );
         }

         CommitSha = await GitCommand.RunCheckedAsync( LocalPath, new[] { "rev-parse", "HEAD" }, GIT_TIMEOUT, ct );
         return CommitSha;
      }
      finally
      {
         gate.Release();
      }
   }

   /// <summary>
   /// Asks the remote for the commit the followed branch points at, with a short time limit,
   /// without downloading anything. Why: a clone of an address that does not answer can wait
   /// minutes on the network before it fails; this answers in seconds with a plain reason,
   /// before a clone is started.
   /// </summary>
   /// <param name="timeout">Longest to wait for the remote, e.g. 20 seconds.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The commit id the remote's branch (or default branch) points at.</returns>
   /// <exception cref="TimeoutException">The remote did not answer in time.</exception>
   /// <exception cref="InvalidOperationException">The remote answered with an error, has no such branch, or has no commits.</exception>
   public async Task<string> CheckReachableAsync( TimeSpan timeout, CancellationToken ct )
   {
      string reference = Branch == null ? "HEAD" : "refs/heads/" + Branch;
      GitResult result;
      try
      {
         result = await GitCommand.RunAsync( null, new[] { "ls-remote", "--", Url, reference }, timeout, ct );
      }
      catch( TimeoutException ex )
      {
         throw new TimeoutException( $"{Url} did not answer within {timeout.TotalSeconds:0} seconds. Check the address, and that this server can reach it.", ex );
      }

      if( result.ExitCode != 0 )
      {
         throw new InvalidOperationException( ExplainUnreachable( result.Error ) );
      }

      string? sha = result.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries ).Select( l => l.Split( '\t' )[0].Trim() ).FirstOrDefault( s => s.Length > 0 );
      if( sha == null )
      {
         throw new InvalidOperationException( Branch == null ? $"{Url} has no commits yet." : $"{Url} has no branch named '{Branch}'." );
      }

      return sha;
   }

   /// <summary>
   /// Checks a repository address without contacting anything.
   /// </summary>
   /// <param name="url">The address to check.</param>
   /// <returns>Null when acceptable, otherwise a plain-English reason.</returns>
   public static string? ValidateUrl( string? url )
   {
      string value = ( url ?? string.Empty ).Trim();
      if( value.Length == 0 )
      {
         return "Enter a repository address or a local folder.";
      }

      if( value.Any( char.IsControl ) || value.Contains( ' ' ) )
      {
         return "A repository address cannot contain spaces or control characters.";
      }

      if( value.StartsWith( '-' ) )
      {
         return "A repository address cannot start with '-'.";
      }

      if( value.Contains( "::", StringComparison.Ordinal ) )
      {
         return "Git transport helpers (an address containing '::') are not allowed.";
      }

      if( IsLocalPath( value ) )
      {
         return Directory.Exists( GvbSettings.Expand( value ) ) ? null : $"The folder {value} does not exist.";
      }

      return SCP_LIKE().IsMatch( value ) ? null : ValidateUri( value );
   }

   /// <summary>
   /// The folder name a repository's copy gets: owner and repository name joined by '_', using
   /// only letters, digits, '.', '_' and '-'. The same repository reached by https or ssh gets
   /// the same name. Never empty and never "." or "..", so it always stays inside the root.
   /// </summary>
   /// <param name="url">The repository address.</param>
   /// <returns>The folder name, e.g. "dotnet-architecture_eShopOnWeb".</returns>
   public static string SanitizeName( string url )
   {
      string trimmed = url.Trim().TrimEnd( '/', '\\' );
      if( trimmed.EndsWith( ".git", StringComparison.OrdinalIgnoreCase ) )
      {
         trimmed = trimmed[..^4];
      }

      List<string> parts = trimmed.Split( new[] { '/', '\\', ':' }, StringSplitOptions.RemoveEmptyEntries )
         .Where( p => !p.Equals( "_git", StringComparison.Ordinal ) )
         .ToList();
      string repo = parts.Count > 0 ? parts[^1] : "repo";
      string owner = parts.Count > 1 && !parts[^2].Contains( '@' ) ? parts[^2] : string.Empty;
      string name = UNSAFE_NAME_CHARS().Replace( owner.Length > 0 ? $"{owner}_{repo}" : repo, "_" ).Trim( '.' );
      if( name.Length > MAX_NAME_CHARS )
      {
         name = name[..MAX_NAME_CHARS];
      }

      return name.Length == 0 ? "repo" : name;
   }

   /// <summary>
   /// Checks an optional branch name without contacting anything: letters, digits, '.', '_', '-'
   /// and '/', not starting with '-' or '/', and without "..". Public so a caller can refuse a bad
   /// branch with this plain reason before building a repository.
   /// </summary>
   /// <param name="branch">Branch name, or null.</param>
   /// <returns>Null when acceptable or absent, otherwise the reason.</returns>
   public static string? ValidateBranch( string? branch )
   {
      if( string.IsNullOrWhiteSpace( branch ) )
      {
         return null;
      }

      string value = branch.Trim();
      bool ok = BRANCH().IsMatch( value ) && !value.Contains( "..", StringComparison.Ordinal ) && !value.EndsWith( '/' )
         && !value.EndsWith( ".lock", StringComparison.Ordinal );
      return ok ? null : $"'{value}' is not a valid branch name.";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Clones into a temporary folder beside the final one and renames it into place, so a clone
   /// that fails or is cancelled part way never leaves a half-filled copy behind.
   /// </summary>
   /// <param name="onProgress">Receives steps and git's progress lines, or null.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task CloneAsync( Action<string>? onProgress, CancellationToken ct )
   {
      string partial = $"{LocalPath}.partial-{Guid.NewGuid().ToString( "N" )[..8]}";
      onProgress?.Invoke( "cloning a new copy" );
      var arguments = new List<string> { "clone", "--depth", "1", "--single-branch", "--no-tags" };
      if( onProgress != null )
      {
         // git prints progress only to a terminal unless asked.
         arguments.Add( "--progress" );
      }

      if( _isLocal )
      {
         // A plain local clone ignores --depth; --no-local makes git copy only the latest commit.
         arguments.Add( "--no-local" );
      }

      if( Branch != null )
      {
         arguments.AddRange( new[] { "--branch", Branch } );
      }

      arguments.AddRange( new[] { "--", Url, partial } );
      try
      {
         await GitCommand.RunCheckedAsync( _reposRoot, arguments, GIT_TIMEOUT, ct, onProgress );
         Directory.Move( partial, LocalPath );
      }
      finally
      {
         DeleteQuietly( partial );
      }
   }

   /// <summary>
   /// Fetches the latest commit of the followed branch and makes the copy match it exactly:
   /// points the copy at the current address first (it may have moved from https to ssh), then
   /// hard-resets and removes every untracked or ignored file.
   /// </summary>
   /// <param name="onProgress">Receives steps and git's progress lines, or null.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task UpdateAsync( Action<string>? onProgress, CancellationToken ct )
   {
      GitResult current = await GitCommand.RunAsync( LocalPath, new[] { "config", "--get", "remote.origin.url" }, GIT_TIMEOUT, ct );
      if( current.ExitCode != 0 || !string.Equals( current.Output.Trim(), Url, StringComparison.Ordinal ) )
      {
         string verb = current.ExitCode == 0 ? "set-url" : "add";
         await GitCommand.RunCheckedAsync( LocalPath, new[] { "remote", verb, "origin", Url }, GIT_TIMEOUT, ct );
      }

      onProgress?.Invoke( "fetching the latest commit" );
      var fetch = new List<string> { "fetch", "--depth", "1", "--no-tags" };
      if( onProgress != null )
      {
         fetch.Add( "--progress" );
      }

      fetch.AddRange( new[] { "origin", Branch ?? "HEAD" } );
      await GitCommand.RunCheckedAsync( LocalPath, fetch, GIT_TIMEOUT, ct, onProgress );
      onProgress?.Invoke( "updating the copy to the latest commit" );
      await GitCommand.RunCheckedAsync( LocalPath, new[] { "reset", "--hard", "FETCH_HEAD" }, GIT_TIMEOUT, ct );
      await GitCommand.RunCheckedAsync( LocalPath, new[] { "clean", "-ffdx" }, GIT_TIMEOUT, ct );
   }

   /// <summary>
   /// True for an absolute local folder ("/..." or "~/..."), as opposed to a network address.
   /// </summary>
   /// <param name="value">The address.</param>
   /// <returns>True for a local folder.</returns>
   private static bool IsLocalPath( string value )
   {
      return value.StartsWith( '/' ) || value == "~" || value.StartsWith( "~/", StringComparison.Ordinal );
   }

   /// <summary>
   /// Checks a URL-style address: https or ssh with a host and a path, and no password.
   /// </summary>
   /// <param name="value">The address.</param>
   /// <returns>Null when acceptable, otherwise the reason.</returns>
   private static string? ValidateUri( string value )
   {
      if( !Uri.TryCreate( value, UriKind.Absolute, out Uri? uri ) )
      {
         return "Use an https or ssh address (for example https://github.com/owner/repo) or an absolute local folder.";
      }

      if( uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeSsh )
      {
         return $"Only https, ssh and local folders are supported, not '{uri.Scheme}'.";
      }

      if( uri.UserInfo.Contains( ':' ) )
      {
         return "Do not put a password or token in the address; git would store it in plain text. Use a git credential helper.";
      }

      // A host starting with '-' could be read by ssh as an option, so it must start with a letter or digit.
      bool hostOk = uri.Host.Length > 0 && char.IsAsciiLetterOrDigit( uri.Host[0] );
      bool hasPath = uri.AbsolutePath.Trim( '/' ).Length > 0;
      return hostOk && hasPath ? null : "The address needs a host and a repository path, for example https://github.com/owner/repo.";
   }

   /// <summary>
   /// Turns git's answer for an address it could not read into a plain reason. git asks for a
   /// login when a repository is private or does not exist, and with prompts turned off it says
   /// "terminal prompts disabled", which means nothing to a person; that case is reworded.
   /// </summary>
   /// <param name="error">git's standard error.</param>
   /// <returns>The reason, naming the address.</returns>
   private string ExplainUnreachable( string error )
   {
      string detail = string.Join( " ", error.Split( '\n', StringSplitOptions.RemoveEmptyEntries ).Select( l => l.Trim() ) );
      if( detail.Contains( "terminal prompts disabled", StringComparison.OrdinalIgnoreCase )
         || detail.Contains( "could not read Username", StringComparison.OrdinalIgnoreCase ) )
      {
         return $"{Url} was not found, or it needs a login this server does not have.";
      }

      if( detail.Length > MAX_DETAIL_CHARS )
      {
         detail = detail[..MAX_DETAIL_CHARS] + "...";
      }

      return $"Could not read {Url}: {detail}";
   }

   /// <summary>
   /// Deletes a temporary clone folder if it is still there. Failures are ignored; the folder
   /// has a unique name and is never read.
   /// </summary>
   /// <param name="path">The folder.</param>
   private static void DeleteQuietly( string path )
   {
      try
      {
         if( Directory.Exists( path ) )
         {
            Directory.Delete( path, recursive: true );
         }
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         // Best effort.
      }
   }

   /// <summary>"user@host:owner/repo" style ssh address.</summary>
   [GeneratedRegex( @"^[A-Za-z0-9_][A-Za-z0-9._-]*@[A-Za-z0-9][A-Za-z0-9.-]*:[A-Za-z0-9._~/][A-Za-z0-9._~/-]*$" )]
   private static partial Regex SCP_LIKE();

   /// <summary>Characters not allowed in a clone folder name.</summary>
   [GeneratedRegex( @"[^A-Za-z0-9._-]" )]
   private static partial Regex UNSAFE_NAME_CHARS();

   /// <summary>Characters and shape allowed in a branch name.</summary>
   [GeneratedRegex( @"^[A-Za-z0-9_][A-Za-z0-9._/-]*$" )]
   private static partial Regex BRANCH();

   #endregion Private Methods
}
