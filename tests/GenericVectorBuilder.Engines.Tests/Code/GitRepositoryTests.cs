using GenericVectorBuilder.Code.Git;

namespace GenericVectorBuilder.Engines.Tests.Code;

/// <summary>
/// Address checks, clone folder names, and clone-then-update against a local repository (no
/// network needed).
/// </summary>
public class GitRepositoryTests : IDisposable
{
   #region Data Members

   private readonly CodeTempFolder _origin = new();
   private readonly CodeTempFolder _repos = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// https, ssh and scp-style addresses and existing local folders are accepted.
   /// </summary>
   [Theory]
   [InlineData( "https://github.com/dotnet-architecture/eShopOnWeb" )]
   [InlineData( "https://github.com/dotnet-architecture/eShopOnWeb.git" )]
   [InlineData( "https://org@dev.azure.com/org/project/_git/repo" )]
   [InlineData( "ssh://git@github.com/owner/repo.git" )]
   [InlineData( "git@github.com:owner/repo.git" )]
   public void ValidateUrl_AcceptsSupportedAddresses( string url )
   {
      Assert.Null( GitRepository.ValidateUrl( url ) );
   }

   /// <summary>
   /// Anything that could carry a password, run a command, act as a git option or use another
   /// transport is refused with a reason.
   /// </summary>
   [Theory]
   [InlineData( "" )]
   [InlineData( "http://github.com/owner/repo" )]
   [InlineData( "git://github.com/owner/repo" )]
   [InlineData( "ftp://example.com/repo" )]
   [InlineData( "ext::sh -c touch% /tmp/pwned" )]
   [InlineData( "ext::sh" )]
   [InlineData( "fd::17" )]
   [InlineData( "--upload-pack=touch /tmp/pwned" )]
   [InlineData( "-oProxyCommand=evil" )]
   [InlineData( "https://user:secret@github.com/owner/repo" )]
   [InlineData( "ssh://-oProxyCommand=evil/repo" )]
   [InlineData( "git@-oProxyCommand=evil:repo" )]
   [InlineData( "https://github.com/" )]
   [InlineData( "https://github.com/owner/repo\nrm -rf" )]
   [InlineData( "/no/such/folder/anywhere" )]
   [InlineData( "relative/path" )]
   public void ValidateUrl_RefusesEverythingElse( string url )
   {
      string? problem = GitRepository.ValidateUrl( url );

      Assert.False( string.IsNullOrWhiteSpace( problem ), $"'{url}' should be refused" );
   }

   /// <summary>
   /// A local folder that exists is accepted.
   /// </summary>
   [Fact]
   public void ValidateUrl_AcceptsAnExistingLocalFolder()
   {
      Assert.Null( GitRepository.ValidateUrl( _origin.Path ) );
   }

   /// <summary>
   /// Folder names keep owner and repository, agree across https and ssh, and can never leave
   /// the repos folder.
   /// </summary>
   [Fact]
   public void SanitizeName_IsReadableStableAndSafe()
   {
      Assert.Equal( "dotnet-architecture_eShopOnWeb", GitRepository.SanitizeName( "https://github.com/dotnet-architecture/eShopOnWeb" ) );
      Assert.Equal( "dotnet-architecture_eShopOnWeb", GitRepository.SanitizeName( "git@github.com:dotnet-architecture/eShopOnWeb.git" ) );
      Assert.Equal( "project_repo", GitRepository.SanitizeName( "https://org@dev.azure.com/org/project/_git/repo" ) );
      string hostile = GitRepository.SanitizeName( "https://example.com/../.." );
      Assert.NotEmpty( hostile );
      Assert.DoesNotContain( '/', hostile );
      Assert.NotEqual( "..", hostile );
   }

   /// <summary>
   /// The first sync clones only the latest commit; the next sync fetches a new commit and
   /// hard-resets, wiping local edits and untracked files; the commit id matches the origin.
   /// </summary>
   [Fact]
   public async Task Sync_ClonesThenUpdatesToTheLatestCommit()
   {
      await Git( _origin.Path, "init", "--quiet", "--initial-branch=main" );
      _origin.Write( "src/A.cs", "class A { }" );
      await Commit( "first" );
      _origin.Write( "src/A.cs", "class A { int x; }" );
      string second = await Commit( "second" );
      var repo = new GitRepository( _origin.Path, _repos.Path );

      string cloned = await repo.SyncAsync( CancellationToken.None );

      Assert.Equal( second, cloned );
      Assert.Equal( "1", await Git( repo.LocalPath, "rev-list", "--count", "HEAD" ) );
      File.WriteAllText( Path.Combine( repo.LocalPath, "src", "A.cs" ), "local edit" );
      File.WriteAllText( Path.Combine( repo.LocalPath, "untracked.cs" ), "junk" );
      _origin.Write( "src/B.cs", "class B { }" );
      string third = await Commit( "third" );

      string updated = await repo.SyncAsync( CancellationToken.None );

      Assert.Equal( third, updated );
      Assert.Equal( third, repo.CommitSha );
      Assert.Equal( "class A { int x; }", File.ReadAllText( Path.Combine( repo.LocalPath, "src", "A.cs" ) ) );
      Assert.True( File.Exists( Path.Combine( repo.LocalPath, "src", "B.cs" ) ) );
      Assert.False( File.Exists( Path.Combine( repo.LocalPath, "untracked.cs" ) ) );
      Assert.Empty( Directory.GetDirectories( _repos.Path, "*.partial-*" ) );
   }

   /// <summary>
   /// A folder in the clone's place that is not a git copy is never overwritten.
   /// </summary>
   [Fact]
   public async Task Sync_RefusesAFolderThatIsNotAGitCopy()
   {
      var repo = new GitRepository( "https://github.com/owner/repo", _repos.Path );
      Directory.CreateDirectory( repo.LocalPath );
      File.WriteAllText( Path.Combine( repo.LocalPath, "keep.txt" ), "mine" );

      await Assert.ThrowsAsync<InvalidOperationException>( () => repo.SyncAsync( CancellationToken.None ) );
      Assert.True( File.Exists( Path.Combine( repo.LocalPath, "keep.txt" ) ) );
   }

   /// <summary>
   /// A clone that fails leaves no folder behind.
   /// </summary>
   [Fact]
   public async Task Sync_FailedClone_LeavesNothingBehind()
   {
      var repo = new GitRepository( _origin.Path, _repos.Path );

      await Assert.ThrowsAsync<InvalidOperationException>( () => repo.SyncAsync( CancellationToken.None ) );
      Assert.Empty( Directory.GetFileSystemEntries( _repos.Path ) );
   }

   /// <summary>
   /// With a progress callback, a clone and an update each say which step they are on, and the
   /// steps arrive while git runs (git's own progress lines may come too).
   /// </summary>
   [Fact]
   public async Task Sync_WithProgress_ReportsEachStep()
   {
      await Git( _origin.Path, "init", "--quiet", "--initial-branch=main" );
      _origin.Write( "src/A.cs", "class A { }" );
      await Commit( "first" );
      var repo = new GitRepository( _origin.Path, _repos.Path );
      var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();

      await repo.SyncAsync( lines.Enqueue, CancellationToken.None );
      List<string> cloneLines = lines.ToList();
      lines.Clear();
      _origin.Write( "src/B.cs", "class B { }" );
      string second = await Commit( "second" );
      string updated = await repo.SyncAsync( lines.Enqueue, CancellationToken.None );

      Assert.Contains( "cloning a new copy", cloneLines );
      Assert.Contains( "fetching the latest commit", lines );
      Assert.Contains( "updating the copy to the latest commit", lines );
      Assert.Equal( second, updated );
   }

   /// <summary>
   /// The reachability check answers with the remote's commit and clones nothing.
   /// </summary>
   [Fact]
   public async Task CheckReachable_ReturnsTheRemoteCommit_WithoutCloning()
   {
      await Git( _origin.Path, "init", "--quiet", "--initial-branch=main" );
      _origin.Write( "src/A.cs", "class A { }" );
      string head = await Commit( "first" );
      var repo = new GitRepository( _origin.Path, _repos.Path );
      var onBranch = new GitRepository( _origin.Path, _repos.Path, "main" );

      string seen = await repo.CheckReachableAsync( TimeSpan.FromSeconds( 10 ), CancellationToken.None );
      string seenOnBranch = await onBranch.CheckReachableAsync( TimeSpan.FromSeconds( 10 ), CancellationToken.None );

      Assert.Equal( head, seen );
      Assert.Equal( head, seenOnBranch );
      Assert.Empty( Directory.GetFileSystemEntries( _repos.Path ) );
   }

   /// <summary>
   /// A branch the remote does not have is named in a plain message, before any clone.
   /// </summary>
   [Fact]
   public async Task CheckReachable_MissingBranch_SaysSo()
   {
      await Git( _origin.Path, "init", "--quiet", "--initial-branch=main" );
      _origin.Write( "src/A.cs", "class A { }" );
      await Commit( "first" );
      var repo = new GitRepository( _origin.Path, _repos.Path, "no-such-branch" );

      var ex = await Assert.ThrowsAsync<InvalidOperationException>( () => repo.CheckReachableAsync( TimeSpan.FromSeconds( 10 ), CancellationToken.None ) );

      Assert.Equal( $"{_origin.Path} has no branch named 'no-such-branch'.", ex.Message );
   }

   /// <summary>
   /// An address nothing answers on fails within seconds with a message that names it and
   /// carries git's reason, not a stack trace or "terminal prompts disabled".
   /// </summary>
   [Fact]
   public async Task CheckReachable_RefusedPort_FailsFastWithAPlainReason()
   {
      var repo = new GitRepository( "https://127.0.0.1:1/owner/repo", _repos.Path );
      var clock = System.Diagnostics.Stopwatch.StartNew();

      var ex = await Assert.ThrowsAsync<InvalidOperationException>( () => repo.CheckReachableAsync( TimeSpan.FromSeconds( 20 ), CancellationToken.None ) );

      Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 10 ), $"took {clock.Elapsed.TotalSeconds:0.0}s" );
      Assert.StartsWith( "Could not read https://127.0.0.1:1/owner/repo: ", ex.Message );
      Assert.DoesNotContain( "   at ", ex.Message );
   }

   /// <summary>
   /// The branch check is public so a caller can refuse a bad branch before building anything.
   /// </summary>
   [Theory]
   [InlineData( "main", true )]
   [InlineData( "feature/x-1", true )]
   [InlineData( null, true )]
   [InlineData( "-delete", false )]
   [InlineData( "a..b", false )]
   [InlineData( "x.lock", false )]
   public void ValidateBranch_IsPublic_AndPlain( string? branch, bool ok )
   {
      string? problem = GitRepository.ValidateBranch( branch );

      Assert.Equal( ok, problem == null );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Commits everything in the origin repository.
   /// </summary>
   /// <param name="message">Commit message.</param>
   /// <returns>The new commit id.</returns>
   private async Task<string> Commit( string message )
   {
      await Git( _origin.Path, "add", "-A" );
      await Git( _origin.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--quiet", "-m", message );
      return await Git( _origin.Path, "rev-parse", "HEAD" );
   }

   /// <summary>
   /// Runs git in a folder and returns its trimmed output, failing the test on a non-zero exit.
   /// </summary>
   /// <param name="folder">Working folder.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>Standard output, trimmed.</returns>
   private static async Task<string> Git( string folder, params string[] arguments )
   {
      var info = new System.Diagnostics.ProcessStartInfo( "git" ) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
      foreach( string argument in arguments )
      {
         info.ArgumentList.Add( argument );
      }

      using var process = System.Diagnostics.Process.Start( info )!;
      string output = await process.StandardOutput.ReadToEndAsync();
      string error = await process.StandardError.ReadToEndAsync();
      await process.WaitForExitAsync();
      Assert.True( process.ExitCode == 0, $"git {string.Join( ' ', arguments )} failed: {error}" );
      return output.Trim();
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Releases the temp folders.
   /// </summary>
   public void Dispose()
   {
      _origin.Dispose();
      _repos.Dispose();
   }

   #endregion IDisposable
}
