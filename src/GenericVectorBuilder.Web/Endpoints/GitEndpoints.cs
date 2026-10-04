using GenericVectorBuilder.Code.Git;
using GenericVectorBuilder.Web.Git;

namespace GenericVectorBuilder.Web.Endpoints;

/// <summary>
/// What the page needs before a "Git repo" run: Fetch (clone or update the copy and describe
/// what a run would read) and the progress of a fetch that is still going.
/// Why Fetch really fetches: the file count, the folders and the chunk estimate can only come
/// from the files themselves, and the copy is then ready, so Go only has to fetch what changed.
/// Why every failure comes back as JSON with a plain message: the page shows the text as is,
/// and a wrong address, a private repository and a network that does not answer are ordinary
/// events here.
/// </summary>
public static class GitEndpoints
{
   #region Data Members

   private const string PREVIEW_HOLDER = "a Fetch from the page";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Maps the endpoints.
   /// </summary>
   /// <param name="app">The app.</param>
   public static void Map( WebApplication app )
   {
      app.MapPost( "/api/git/preview", PreviewAsync );
      app.MapGet( "/api/git/progress", Progress );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Fetches a repository's latest commit into its copy and describes what a run would read.
   /// Refused with 409 while a run or another Fetch holds the same copy, because fetching would
   /// change the files under it.
   /// </summary>
   /// <param name="request">Address, branch and file types.</param>
   /// <param name="git">The git workspace.</param>
   /// <param name="loggers">Logger factory.</param>
   /// <param name="ct">Cancellation (the browser left).</param>
   /// <returns>The preview; 400 for a bad request; 409 when the copy is busy; 502 when git failed; 504 when the address did not answer; 507 when the disk is too full.</returns>
   private static async Task<IResult> PreviewAsync( GitPreviewRequest request, GitWorkspace git, ILoggerFactory loggers, CancellationToken ct )
   {
      GitRepository repo;
      IReadOnlyList<string> extensions;
      try
      {
         repo = git.Open( request.Url, request.Branch );
         extensions = GitWorkspace.CleanExtensions( request.Extensions );
      }
      catch( ArgumentException ex )
      {
         return Results.BadRequest( new { error = ex.Message } );
      }

      using IDisposable? claim = git.TryClaim( repo.LocalPath, PREVIEW_HOLDER );
      if( claim == null )
      {
         return Results.Json( new { error = $"{repo.Url} is in use by {git.HolderOf( repo.LocalPath ) ?? "another request"} right now. Fetch again when it finishes." },
            statusCode: StatusCodes.Status409Conflict );
      }

      ILogger logger = loggers.CreateLogger( "GenericVectorBuilder.Git" );
      try
      {
         string commit = await git.SyncAsync( repo, null, ct );
         GitPreview preview = await GitPreviewBuilder.BuildAsync( repo, commit, extensions, ct );
         logger.LogInformation( "Fetched {Url} at {Commit}: {Files} files, about {Chunks} chunks", repo.Url, commit, preview.FileCount, preview.Chunks.Chunks );
         return Results.Ok( preview );
      }
      catch( Exception ex ) when( ex is TimeoutException or InvalidOperationException or IOException or UnauthorizedAccessException )
      {
         // Anything else is a bug, not a git or disk problem; it reaches the app's error handler, which logs it in full.
         logger.LogWarning( "Fetch of {Url} failed: {Reason}", repo.Url, ex.Message );
         return Results.Json( new { error = ex.Message }, statusCode: StatusFor( ex ) );
      }
   }

   /// <summary>
   /// What the latest fetch of a repository's copy is doing, for the page to show while Fetch
   /// or a run waits on git. Contacts nothing.
   /// </summary>
   /// <param name="url">The repository address, as sent to Fetch.</param>
   /// <param name="branch">The branch, as sent to Fetch.</param>
   /// <param name="git">The git workspace.</param>
   /// <returns>The status; "active": false when this server has not fetched that repository since it started; 400 for a bad address.</returns>
   private static IResult Progress( string? url, string? branch, GitWorkspace git )
   {
      GitRepository repo;
      try
      {
         repo = git.Open( url, branch );
      }
      catch( ArgumentException ex )
      {
         return Results.BadRequest( new { error = ex.Message } );
      }

      GitFetchStatus? status = git.StatusOf( repo.LocalPath );
      if( status == null )
      {
         return Results.Ok( new { active = false, text = "not fetched yet" } );
      }

      double seconds = ( ( status.Finished ? status.UpdatedUtc : DateTime.UtcNow ) - status.StartedUtc ).TotalSeconds;
      double quietSeconds = ( DateTime.UtcNow - status.UpdatedUtc ).TotalSeconds;
      return Results.Ok( new { active = !status.Finished, text = status.Text, error = status.Error, seconds = Math.Round( seconds ), quietSeconds = Math.Round( quietSeconds ) } );
   }

   /// <summary>
   /// The HTTP status for a failed fetch.
   /// </summary>
   /// <param name="ex">The failure.</param>
   /// <returns>507 for a full disk, 504 for an address or command that ran out of time, 502 otherwise.</returns>
   private static int StatusFor( Exception ex )
   {
      return ex switch
      {
         GitDiskSpaceException => StatusCodes.Status507InsufficientStorage,
         TimeoutException => StatusCodes.Status504GatewayTimeout,
         _ => StatusCodes.Status502BadGateway,
      };
   }

   #endregion Private Methods
}

/// <summary>
/// Body of POST /api/git/preview.
/// </summary>
/// <param name="Url">An https or ssh address, or a local folder inside the allowed data folders.</param>
/// <param name="Branch">Branch, or null for the default branch.</param>
/// <param name="Extensions">File types to read; null or empty means ".cs".</param>
public sealed record GitPreviewRequest( string? Url, string? Branch, IReadOnlyList<string>? Extensions );
