using GenericVectorBuilder.Code.Chunking;
using GenericVectorBuilder.Code.Git;
using GenericVectorBuilder.Code.Mapping;
using GenericVectorBuilder.Code.Sources;
using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Mapping;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sources.Files;
using GenericVectorBuilder.Core.Sources.Odbc;
using GenericVectorBuilder.Web.Git;

namespace GenericVectorBuilder.Web.Runs;

/// <summary>
/// Background worker that takes runs off the queue one at a time and executes them.
/// Each run re-scans its folder first, so it reads exactly what is on disk at that moment,
/// then hands a <see cref="FolderSource"/> to the <see cref="PipelineRunner"/>. A database run
/// hands an <see cref="OdbcSource"/> instead; its login lives in memory for that run only.
/// A git run fetches the repository's latest commit first and holds the copy until it ends, so
/// nothing can change the files under it; it reads them with a <see cref="CodeFolderSource"/>
/// and splits C# by type and member with the <see cref="RoslynCodeChunker"/>.
/// </summary>
public sealed class RunWorker : BackgroundService
{
   #region Data Members

   private readonly RunRegistry _registry;
   private readonly GvbServices _services;
   private readonly ILogger<RunWorker> _logger;
   private readonly OdbcCatalog _odbc;
   private readonly GitWorkspace _git;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the worker.
   /// </summary>
   /// <param name="registry">Run registry and queue.</param>
   /// <param name="services">Shared services.</param>
   /// <param name="logger">Logger.</param>
   /// <param name="odbc">ODBC catalog for database runs; one over the services' settings when omitted.</param>
   /// <param name="git">Git workspace for code runs; one over the services' settings when omitted. Last, so older callers are unaffected.</param>
   public RunWorker( RunRegistry registry, GvbServices services, ILogger<RunWorker> logger, OdbcCatalog? odbc = null, GitWorkspace? git = null )
   {
      _registry = registry;
      _services = services;
      _logger = logger;
      _odbc = odbc ?? new OdbcCatalog( services.Settings );
      _git = git ?? new GitWorkspace( services.Settings );
   }

   #endregion Constructor

   #region Overrides

   /// <summary>
   /// Drains the queue until the app stops.
   /// </summary>
   /// <param name="stoppingToken">Signalled on shutdown.</param>
   protected override async Task ExecuteAsync( CancellationToken stoppingToken )
   {
      await foreach( RunJob job in _registry.Reader.ReadAllAsync( stoppingToken ) )
      {
         if( !_registry.TryStart( job ) )
         {
            job.Login?.Clear();
            _logger.LogInformation( "Run {RunId} pipeline {Pipeline} was cancelled while queued; skipped", job.Progress.RunId, job.Progress.Pipeline );
            continue;
         }

         using var linked = CancellationTokenSource.CreateLinkedTokenSource( stoppingToken, job.Cancel.Token );
         await ExecuteJobAsync( job, linked.Token );
         RunSnapshot done = job.Progress.Snapshot();
         _logger.LogInformation( "Run {RunId} pipeline {Pipeline} ended {Status}: {Message}", done.RunId, done.Pipeline, done.Status, done.Message );
      }
   }

   #endregion Overrides

   #region Private Methods

   /// <summary>
   /// Prepares the source, builds the pipeline pieces and runs it. Setup failures (folder gone,
   /// bad model name, unknown sink, unknown connection, unreachable repository) end the run as
   /// Failed with the reason. The registry has already marked the run Scanning when it handed
   /// the job over. A database run takes its password out of the job here, so it is gone from the
   /// registry as soon as the run starts, and every failure message is masked before it is shown.
   /// A git run's hold on its copy is released here however the run ends.
   /// </summary>
   /// <param name="job">The job.</param>
   /// <param name="ct">Cancellation (user cancel or shutdown).</param>
   private async Task ExecuteJobAsync( RunJob job, CancellationToken ct )
   {
      RunProgress progress = job.Progress;
      var secrets = new List<string?>();
      PreparedRun? prepared = null;
      try
      {
         prepared = job.Request switch
         {
            { Git: not null } => await PrepareGitAsync( job, ct ),
            { Odbc: not null } => await PrepareDatabaseAsync( job, secrets, ct ),
            _ => await PrepareFolderAsync( job, ct ),
         };
         var runner = new PipelineRunner( _services.CreateEmbedder( job.Request.Model ), _services.GetSinks( job.Request.Sinks ), _services.State, prepared.Chunker )
         {
            AllowLargeDeletes = job.Request.AllowLargeDeletes,
         };
         await runner.RunAsync( job.Request.Pipeline, prepared.Source, prepared.Mapper, progress, ct );
      }
      catch( OperationCanceledException ) when( ct.IsCancellationRequested )
      {
         progress.Finish( RunStatus.Cancelled, "cancelled before writing" );
      }
      catch( Exception ex )
      {
         progress.Finish( RunStatus.Failed, SecretScrubber.Scrub( ex.Message, secrets ) );
      }
      finally
      {
         prepared?.Claim?.Dispose();
      }
   }

   /// <summary>
   /// Re-scans the folder so the run reads exactly what is on disk now, and builds the row mapper.
   /// </summary>
   /// <param name="job">The folder job.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The folder source, the row mapper and the text chunker.</returns>
   private static async Task<PreparedRun> PrepareFolderAsync( RunJob job, CancellationToken ct )
   {
      RunProgress progress = job.Progress;
      progress.Message = "scanning the folder";
      IReadOnlyCollection<string> keyColumns = ChosenKeyColumns( job.Request );
      FolderScanResult scan = await Task.Run( () => new FolderScanner().Scan( job.Request.Path, ct, keyColumns ), ct );
      progress.TotalRows = scan.TotalRows;

      var mapper = new RowDocumentMapper( job.Request.Tables ?? new Dictionary<string, TableMapping>() );
      ShareDuplicatesWithMapper( scan, mapper );
      return new PreparedRun( new FolderSource( scan ), mapper, new TextChunker() );
   }

   /// <summary>
   /// Claims the repository's copy for the whole run, fetches the latest commit into it (the
   /// git steps show as the run's message), counts the files for the progress bar, and builds
   /// the code source, mapper and chunker. The commit goes into the final message and into every
   /// chunk's metadata. On any failure the claim is released before the error goes up.
   /// </summary>
   /// <param name="job">The git job.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The code source, mapper and chunker, and the claim on the copy.</returns>
   private async Task<PreparedRun> PrepareGitAsync( RunJob job, CancellationToken ct )
   {
      GitRunRequest request = job.Request.Git!;
      RunProgress progress = job.Progress;
      GitRepository repo = _git.Open( request.Url, request.Branch );
      CodeFolderOptions options = CodeFolderOptions.Default with { Extensions = GitWorkspace.CleanExtensions( request.Extensions ) };
      progress.Message = $"waiting until nothing else is using {repo.Url}";
      IDisposable claim = await _git.ClaimForRunAsync( repo.LocalPath, $"the run of pipeline '{job.Request.Pipeline}'", ct );
      try
      {
         string commit = await _git.SyncAsync( repo, line => progress.Message = $"fetching {repo.Url}: {line}", ct );
         progress.FinishNote = $"git commit {commit}";
         progress.Message = "listing the files";
         IReadOnlyList<string> files = await Task.Run( () => new CodeFolderSource( repo.LocalPath, options ).ListFiles( ct ), ct );
         progress.TotalRows = files.Count;
         _logger.LogInformation( "Run {RunId} reads {Url} at commit {Commit}: {Files} files", progress.RunId, repo.Url, commit, files.Count );
         return new PreparedRun( new CodeFolderSource( repo.LocalPath, options ), new CodeDocumentMapper( commit ), new RoslynCodeChunker(), claim );
      }
      catch
      {
         claim.Dispose();
         throw;
      }
   }

   /// <summary>
   /// Builds the source for a database run from the connection, the login and the chosen
   /// tables, and the row mapper. The mapper takes mappings keyed by table name or by table id,
   /// as the preview reports them. Nothing is opened here except, best effort, one catalog
   /// listing to learn how many rows to expect.
   /// </summary>
   /// <param name="job">The database job.</param>
   /// <param name="secrets">Receives what must be masked in any failure message of this run.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The ODBC source, the row mapper and the text chunker.</returns>
   private async Task<PreparedRun> PrepareDatabaseAsync( RunJob job, List<string?> secrets, CancellationToken ct )
   {
      OdbcRunRequest odbc = job.Request.Odbc!;
      var login = new OdbcCredentials( odbc.User, job.Login?.Take() );
      secrets.AddRange( SecretScrubber.SecretsOf( odbc.Connection, login.Password ) );
      job.Progress.Message = "connecting to the database";

      string connectionString = _odbc.ResolveConnectionString( odbc.Connection, login );
      secrets.Add( connectionString );
      secrets.AddRange( SecretScrubber.PasswordsIn( connectionString ) );
      var source = new OdbcSource( OdbcCatalog.ConnectionName( odbc.Connection ), connectionString, odbc.Tables );
      job.Progress.TotalRows = await ExpectedRowsAsync( odbc, login, ct );
      return new PreparedRun( source, new RowDocumentMapper( job.Request.Tables ?? new Dictionary<string, TableMapping>() ), new TextChunker() );
   }

   /// <summary>
   /// How many rows the chosen tables hold, for the progress bar, from one catalog listing.
   /// Zero (unknown) when any chosen table has no cheap count (a view, or a listing that was
   /// too slow), because a partial total would show a bar that jumps past 100 percent. A
   /// failed listing is not an error here: the run reports the real problem table by table.
   /// </summary>
   /// <param name="odbc">The database request.</param>
   /// <param name="login">The login for this run.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The total, or 0 when it is not known.</returns>
   private async Task<long> ExpectedRowsAsync( OdbcRunRequest odbc, OdbcCredentials login, CancellationToken ct )
   {
      try
      {
         IReadOnlyList<OdbcTableInfo> listed = await _odbc.ListTablesAsync( odbc.Connection, login, ct );
         long total = 0;
         foreach( OdbcSelection chosen in odbc.Tables.Distinct() )
         {
            OdbcTableInfo? info = listed.FirstOrDefault( t => t.Schema == chosen.Schema && t.Name == chosen.Name );
            if( info?.RowCount == null )
            {
               return 0;
            }

            total += info.RowCount.Value;
         }

         return total;
      }
      catch( Exception ex ) when( ex is InvalidOperationException or ArgumentException )
      {
         return 0;
      }
   }

   /// <summary>
   /// The key columns the user picked for this run, without blanks or repeats. The scan needs
   /// them to find key values that appear in more than one file.
   /// </summary>
   /// <param name="request">The run request.</param>
   /// <returns>The chosen key column names; empty when every table is keyed by row content.</returns>
   private static IReadOnlyCollection<string> ChosenKeyColumns( RunRequest request )
   {
      return ( request.Tables?.Values ?? Enumerable.Empty<TableMapping>() )
         .Select( t => t.KeyColumn )
         .Where( k => !string.IsNullOrWhiteSpace( k ) )
         .Select( k => k! )
         .Distinct( StringComparer.OrdinalIgnoreCase )
         .ToList();
   }

   /// <summary>
   /// Tells the mapper, table by table, which key values the scan found in more than one file,
   /// so each file's copy of such a value gets a document key of its own. A table without any
   /// repeated value passes an empty set, so its keys stay exactly as they were.
   /// </summary>
   /// <param name="scan">The scan, carrying the repeated values.</param>
   /// <param name="mapper">The mapper for this run.</param>
   private static void ShareDuplicatesWithMapper( FolderScanResult scan, RowDocumentMapper mapper )
   {
      foreach( ScannedTable table in scan.Tables )
      {
         mapper.SetCrossOriginDuplicates( FolderSource.TableId( table ), table.Name, table.CrossOriginDuplicates );
      }
   }

   #endregion Private Methods

   /// <summary>
   /// Everything a run needs from its source side: what to read, how to turn a record into a
   /// document, how to split it, and for a git run the hold on its copy.
   /// </summary>
   /// <param name="Source">The source.</param>
   /// <param name="Mapper">The mapper.</param>
   /// <param name="Chunker">The chunker.</param>
   /// <param name="Claim">The hold on a git copy, released when the run ends, or null.</param>
   private sealed record PreparedRun( ISource Source, IDocumentMapper Mapper, IChunker Chunker, IDisposable? Claim = null );
}
