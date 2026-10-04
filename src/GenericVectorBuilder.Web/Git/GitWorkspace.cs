using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Code.Git;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Pipeline;

namespace GenericVectorBuilder.Web.Git;

/// <summary>
/// What a fetch of one repository is doing, for the page to show while it waits.
/// </summary>
/// <param name="Text">The latest step or git progress line, e.g. "Receiving objects: 45% (1203/2671)".</param>
/// <param name="StartedUtc">When the fetch started.</param>
/// <param name="UpdatedUtc">When <paramref name="Text"/> last changed; a value that stops moving means git is waiting on the network.</param>
/// <param name="Finished">True once the fetch ended, either way.</param>
/// <param name="Error">Why the fetch failed, or null.</param>
public sealed record GitFetchStatus( string Text, DateTime StartedUtc, DateTime UpdatedUtc, bool Finished, string? Error );

/// <summary>
/// The repos folder is too full to fetch into safely.
/// </summary>
public sealed class GitDiskSpaceException : IOException
{
   #region Constructor

   /// <summary>
   /// Creates the exception.
   /// </summary>
   /// <param name="message">Plain-English reason naming the folder and the space left.</param>
   public GitDiskSpaceException( string message ) : base( message )
   {
   }

   #endregion Constructor
}

/// <summary>
/// The server side of the "Git repo" mode: checks what the page sends, keeps one local copy per
/// repository under the repos folder, fetches it with a short first-contact time limit, and makes
/// sure only one thing touches a copy at a time.
/// Why one holder per copy: a fetch resets the copy to the remote's latest commit and deletes
/// anything else in it, so a fetch in the middle of a run would change files under the run and
/// the run would mix two commits. A run holds the copy from its fetch to its last write; a
/// preview that finds the copy busy is refused with a plain reason instead of waiting.
/// Why a local folder must be inside the data folders: a folder address is read by git on this
/// server, and the page must not be a way to read any repository the service account can see.
/// </summary>
public sealed partial class GitWorkspace
{
   #region Data Members

   /// <summary>Least free space the repos folder must have before a clone or fetch starts: 2 GB.</summary>
   public const long DEFAULT_MIN_FREE_BYTES = 2L * 1024 * 1024 * 1024;

   /// <summary>File types read when the request names none.</summary>
   public static readonly IReadOnlyList<string> DEFAULT_EXTENSIONS = new[] { ".cs" };

   private const int MAX_EXTENSIONS = 20;
   private const int MAX_TEXT_CHARS = 200;
   private static readonly TimeSpan RUN_WAIT_LIMIT = TimeSpan.FromMinutes( 30 );

   private readonly GvbSettings _settings;
   private readonly long _minFreeBytes;
   private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new( StringComparer.Ordinal );
   private readonly ConcurrentDictionary<string, string> _holders = new( StringComparer.Ordinal );
   private readonly ConcurrentDictionary<string, GitFetchStatus> _status = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the workspace over the configured repos folder.
   /// </summary>
   /// <param name="settings">Settings: repos folder, first-contact time limit, data folders.</param>
   public GitWorkspace( GvbSettings settings ) : this( settings, DEFAULT_MIN_FREE_BYTES )
   {
   }

   /// <summary>
   /// Creates the workspace with a different free-space floor, so a test can make the floor
   /// impossible to meet without filling a disk.
   /// </summary>
   /// <param name="settings">Settings.</param>
   /// <param name="minFreeBytes">Least free space needed before a fetch.</param>
   internal GitWorkspace( GvbSettings settings, long minFreeBytes )
   {
      _settings = settings;
      _minFreeBytes = minFreeBytes;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The folder that holds every copy, absolute.</summary>
   public string ReposRoot => GvbSettings.Expand( _settings.ReposRoot );

   /// <summary>How long a repository address gets to answer before the fetch gives up.</summary>
   public TimeSpan ReachTimeout => TimeSpan.FromSeconds( Math.Clamp( _settings.GitReachTimeoutSeconds, 1, 300 ) );

   /// <summary>
   /// Checks an address and branch and describes the repository, contacting nothing.
   /// </summary>
   /// <param name="url">An https or ssh address, or a local folder inside the data folders.</param>
   /// <param name="branch">Branch, or null for the default branch.</param>
   /// <returns>The repository, with its local copy under <see cref="ReposRoot"/>.</returns>
   /// <exception cref="ArgumentException">The address or branch is not acceptable; the message says why.</exception>
   public GitRepository Open( string? url, string? branch )
   {
      string trimmed = ( url ?? string.Empty ).Trim();
      if( IsLocalFolder( trimmed ) && !_settings.IsAllowedDataPath( trimmed ) )
      {
         // Checked before anything looks at the folder, so the answer never says whether a folder outside the data folders exists.
         throw new ArgumentException( "That folder is outside the allowed data folders. A repository on this server must be inside one of them." );
      }

      string? problem = GitRepository.ValidateUrl( trimmed ) ?? GitRepository.ValidateBranch( branch );
      if( problem != null )
      {
         throw new ArgumentException( problem );
      }

      return new GitRepository( trimmed, ReposRoot, string.IsNullOrWhiteSpace( branch ) ? null : branch.Trim() );
   }

   /// <summary>
   /// Cleans the file types a request names: trimmed, lowercased, with a leading dot, without
   /// repeats. None at all means C# only.
   /// </summary>
   /// <param name="extensions">File types as sent, e.g. ["cs", ".TS"].</param>
   /// <returns>The clean list, e.g. [".cs", ".ts"].</returns>
   /// <exception cref="ArgumentException">Too many types, or one that is not a file type.</exception>
   public static IReadOnlyList<string> CleanExtensions( IReadOnlyList<string>? extensions )
   {
      List<string> clean = ( extensions ?? Array.Empty<string>() )
         .Where( e => !string.IsNullOrWhiteSpace( e ) )
         .Select( e => e.Trim().ToLowerInvariant() )
         .Select( e => e.StartsWith( '.' ) ? e : "." + e )
         .Distinct( StringComparer.Ordinal )
         .ToList();
      if( clean.Count == 0 )
      {
         return DEFAULT_EXTENSIONS;
      }

      if( clean.Count > MAX_EXTENSIONS )
      {
         throw new ArgumentException( $"Name at most {MAX_EXTENSIONS} file types." );
      }

      string? bad = clean.FirstOrDefault( e => !EXTENSION().IsMatch( e ) );
      return bad == null ? clean : throw new ArgumentException( $"'{bad}' is not a file type. Use something like .cs or .ts." );
   }

   /// <summary>
   /// The pipeline name offered for a repository: its own name, lowercased and made safe, without
   /// the owner, e.g. "eshoponweb" for https://github.com/dotnet-architecture/eShopOnWeb.
   /// </summary>
   /// <param name="url">The repository address or local folder.</param>
   /// <returns>A safe pipeline name.</returns>
   public static string SuggestPipeline( string url )
   {
      string trimmed = url.Trim().TrimEnd( '/', '\\' );
      if( trimmed.EndsWith( ".git", StringComparison.OrdinalIgnoreCase ) )
      {
         trimmed = trimmed[..^4];
      }

      string? last = trimmed.Split( new[] { '/', '\\', ':' }, StringSplitOptions.RemoveEmptyEntries ).LastOrDefault();
      return PipelineNames.Sanitize( last ?? "repo" );
   }

   /// <summary>
   /// Claims a copy for a preview, without waiting.
   /// </summary>
   /// <param name="localPath">The copy's folder.</param>
   /// <param name="holder">Who holds it, for the message another caller gets, e.g. "a preview".</param>
   /// <returns>The claim to dispose when done, or null when something else holds the copy.</returns>
   public IDisposable? TryClaim( string localPath, string holder )
   {
      SemaphoreSlim gate = _locks.GetOrAdd( localPath, _ => new SemaphoreSlim( 1, 1 ) );
      return gate.Wait( TimeSpan.Zero ) ? Hold( localPath, holder, gate ) : null;
   }

   /// <summary>
   /// Claims a copy for a run, waiting for a preview or another run that holds it. The wait is
   /// capped at 30 minutes; what holds a copy is itself bounded by git's time limits, so hitting
   /// the cap means something is stuck.
   /// </summary>
   /// <param name="localPath">The copy's folder.</param>
   /// <param name="holder">Who holds it, e.g. "the run of pipeline 'eshoponweb'".</param>
   /// <param name="ct">Cancellation (the run was cancelled or the service is stopping).</param>
   /// <returns>The claim to dispose when the run is done.</returns>
   /// <exception cref="TimeoutException">The copy stayed busy for the whole wait.</exception>
   public async Task<IDisposable> ClaimForRunAsync( string localPath, string holder, CancellationToken ct )
   {
      SemaphoreSlim gate = _locks.GetOrAdd( localPath, _ => new SemaphoreSlim( 1, 1 ) );
      if( !await gate.WaitAsync( RUN_WAIT_LIMIT, ct ) )
      {
         throw new TimeoutException( $"Waited {RUN_WAIT_LIMIT.TotalMinutes:0} minutes for {HolderOf( localPath ) ?? "another request"} to finish with {localPath}, and it did not." );
      }

      return Hold( localPath, holder, gate );
   }

   /// <summary>
   /// Who holds a copy right now.
   /// </summary>
   /// <param name="localPath">The copy's folder.</param>
   /// <returns>The holder's description, or null when it is free.</returns>
   public string? HolderOf( string localPath )
   {
      return _holders.TryGetValue( localPath, out string? holder ) ? holder : null;
   }

   /// <summary>
   /// Fetches a repository's latest commit into its copy: checks free space, gives the address a
   /// short time to answer, then clones or updates, recording every step and git progress line
   /// for <see cref="StatusOf"/>. The caller must hold the copy (see <see cref="TryClaim"/>).
   /// </summary>
   /// <param name="repo">The repository.</param>
   /// <param name="onProgress">Also receives every step and progress line, or null.</param>
   /// <param name="ct">Cancellation; a running git command is stopped.</param>
   /// <returns>The commit id the copy is now at.</returns>
   /// <exception cref="GitDiskSpaceException">The repos folder has too little free space.</exception>
   /// <exception cref="TimeoutException">The address did not answer in time, or git ran past its limit.</exception>
   /// <exception cref="InvalidOperationException">git failed; the message says why.</exception>
   public async Task<string> SyncAsync( GitRepository repo, Action<string>? onProgress, CancellationToken ct )
   {
      DateTime started = DateTime.UtcNow;
      void Report( string text )
      {
         string shown = text.Length > MAX_TEXT_CHARS ? text[..MAX_TEXT_CHARS] + "..." : text;
         _status[repo.LocalPath] = new GitFetchStatus( shown, started, DateTime.UtcNow, false, null );
         onProgress?.Invoke( shown );
      }

      try
      {
         Report( "checking free disk space" );
         CheckFreeSpace();
         Report( $"contacting {repo.Url}" );
         await repo.CheckReachableAsync( ReachTimeout, ct );
         string commit = await repo.SyncAsync( Report, ct );
         _status[repo.LocalPath] = new GitFetchStatus( $"at commit {commit}", started, DateTime.UtcNow, true, null );
         return commit;
      }
      catch( Exception ex )
      {
         string error = ex is OperationCanceledException ? "stopped before it finished" : ex.Message;
         _status[repo.LocalPath] = new GitFetchStatus( "failed", started, DateTime.UtcNow, true, error );
         throw;
      }
   }

   /// <summary>
   /// What the latest fetch of a copy is doing or did.
   /// </summary>
   /// <param name="localPath">The copy's folder.</param>
   /// <returns>The status, or null when this process never fetched it.</returns>
   public GitFetchStatus? StatusOf( string localPath )
   {
      return _status.TryGetValue( localPath, out GitFetchStatus? status ) ? status : null;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// True for an address that is a folder on this server ("/..." or "~..."), the same rule
   /// <see cref="GitRepository"/> uses.
   /// </summary>
   /// <param name="value">The trimmed address.</param>
   /// <returns>True for a local folder.</returns>
   private static bool IsLocalFolder( string value )
   {
      return value.StartsWith( '/' ) || value == "~" || value.StartsWith( "~/", StringComparison.Ordinal );
   }

   /// <summary>
   /// Records who holds a copy and returns the claim that frees it.
   /// </summary>
   /// <param name="localPath">The copy's folder.</param>
   /// <param name="holder">Who holds it.</param>
   /// <param name="gate">The copy's gate, already entered.</param>
   /// <returns>The claim.</returns>
   private IDisposable Hold( string localPath, string holder, SemaphoreSlim gate )
   {
      _holders[localPath] = holder;
      return new Claim( () =>
      {
         _holders.TryRemove( localPath, out _ );
         gate.Release();
      } );
   }

   /// <summary>
   /// Refuses to fetch when the repos folder's disk has less than the floor free, so a clone
   /// never fills the disk the database and the vector store also live on.
   /// </summary>
   /// <exception cref="GitDiskSpaceException">Not enough space.</exception>
   private void CheckFreeSpace()
   {
      Directory.CreateDirectory( ReposRoot );
      long free = new DriveInfo( ReposRoot ).AvailableFreeSpace;
      if( free < _minFreeBytes )
      {
         throw new GitDiskSpaceException( $"Only {free / 1e9:0.0} GB is free where the repository copies live ({ReposRoot}). At least {_minFreeBytes / 1e9:0.0} GB is needed before fetching. Free some space and try again." );
      }
   }

   /// <summary>A file type: a dot, then up to 16 letters, digits, '_', '+' or '-'.</summary>
   [GeneratedRegex( @"^\.[a-z0-9][a-z0-9_+-]{0,15}$" )]
   private static partial Regex EXTENSION();

   #endregion Private Methods

   /// <summary>
   /// Frees a held copy exactly once, however many times it is disposed.
   /// </summary>
   private sealed class Claim : IDisposable
   {
      #region Data Members

      private Action? _release;

      #endregion Data Members

      #region Constructor

      /// <summary>
      /// Creates the claim.
      /// </summary>
      /// <param name="release">Frees the copy.</param>
      public Claim( Action release )
      {
         _release = release;
      }

      #endregion Constructor

      #region IDisposable

      /// <summary>
      /// Frees the copy the first time; later calls do nothing.
      /// </summary>
      public void Dispose()
      {
         Interlocked.Exchange( ref _release, null )?.Invoke();
      }

      #endregion IDisposable
   }
}
