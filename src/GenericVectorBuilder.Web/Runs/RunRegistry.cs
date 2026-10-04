using System.Collections.Concurrent;
using System.Threading.Channels;
using GenericVectorBuilder.Core.Mapping;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sources.Odbc;

namespace GenericVectorBuilder.Web.Runs;

/// <summary>
/// What the browser asks for when the user presses Go.
/// Exactly one of <paramref name="Path"/>, <paramref name="Odbc"/> and <paramref name="Git"/> is
/// set: a folder run names a folder, a database run names an ODBC connection and its tables, a
/// code run names a git repository.
/// </summary>
/// <param name="Pipeline">Pipeline name (sanitized on arrival).</param>
/// <param name="Path">Folder to read, inside an allowed data root. Empty for a database run.</param>
/// <param name="Sinks">Destinations, e.g. ["sql", "qdrant"].</param>
/// <param name="Model">Embedding model key, or null for the default.</param>
/// <param name="Tables">Per-table key column and template choices, keyed by table name (folder runs) or by table name or table id (database runs).</param>
/// <param name="AllowLargeDeletes">True when the user ticked "Allow large deletes", so this run may remove more than half of what the pipeline holds. Off unless asked for, and after <paramref name="Tables"/> so older callers that omit it are unaffected.</param>
/// <param name="Odbc">The database to read instead of a folder, or null for a folder run. After the members above, so older callers that omit it are unaffected.</param>
/// <param name="Git">The git repository to read instead of a folder, or null. Last, so older callers that omit it are unaffected.</param>
public sealed record RunRequest( string Pipeline, string Path, IReadOnlyList<string> Sinks, string? Model, Dictionary<string, TableMapping>? Tables, bool AllowLargeDeletes = false,
   OdbcRunRequest? Odbc = null, GitRunRequest? Git = null );

/// <summary>
/// The code half of a run request: which git repository to read, which branch, and which file
/// types. The run fetches the repository's latest commit into the server's repos folder, reads
/// the matching files and splits C# by type and member.
/// Why the address cannot carry a password: git would save it in plain text in the copy's
/// settings, so <see cref="GenericVectorBuilder.Code.Git.GitRepository"/> refuses one.
/// </summary>
/// <param name="Url">An https or ssh address, or a local folder inside the allowed data folders.</param>
/// <param name="Branch">Branch to read, or null for the repository's default branch.</param>
/// <param name="Extensions">File types to read, e.g. [".cs"]; null or empty means ".cs".</param>
public sealed record GitRunRequest( string Url, string? Branch, IReadOnlyList<string>? Extensions );

/// <summary>
/// The database half of a run request: which ODBC connection to read, the login typed on the page
/// for it, and the tables and views to read.
/// Why ToString is overridden: a record prints every member by default, so one stray log line or
/// interpolated string would write the password into the log. Why the registry strips the
/// password from the copy it keeps: see <see cref="RunSecret"/>.
/// </summary>
/// <param name="Connection">Configured connection name, DSN name, or raw ODBC connection string.</param>
/// <param name="User">User name typed on the page, or null to use the connection's own.</param>
/// <param name="Password">Password typed on the page, or null to use the connection's own. Held in memory for the run only.</param>
/// <param name="Tables">Tables and views to read.</param>
public sealed record OdbcRunRequest( string Connection, string? User, string? Password, IReadOnlyList<OdbcSelection> Tables )
{
   /// <summary>
   /// Describes the request without the password and without a raw connection string, which
   /// can carry one of its own.
   /// </summary>
   /// <returns>The user, a masked password and the table count.</returns>
   public override string ToString()
   {
      return $"OdbcRunRequest {{ User = {User ?? "(none)"}, Password = {( Password == null ? "(none)" : "***" )}, Tables = {Tables?.Count ?? 0} }}";
   }
}

/// <summary>
/// A queued or running job: the request, its live progress, and its cancel switch.
/// </summary>
/// <param name="Request">The request. For a database run its password has been moved to <paramref name="Login"/>.</param>
/// <param name="Progress">Live progress.</param>
/// <param name="Cancel">Cancels the run.</param>
/// <param name="Login">The password for a database run, or null. Last, so older callers that omit it are unaffected.</param>
public sealed record RunJob( RunRequest Request, RunProgress Progress, CancellationTokenSource Cancel, RunSecret? Login = null );

/// <summary>
/// Why a run was not queued.
/// </summary>
public enum EnqueueRefusal
{
   /// <summary>Nothing wrong; the run was queued.</summary>
   None,

   /// <summary>Too many runs are already waiting.</summary>
   QueueFull,

   /// <summary>The pipeline is being reset right now.</summary>
   PipelineResetting,
}

/// <summary>
/// Outcome of trying to queue a run.
/// </summary>
/// <param name="Job">The queued job, or null when it was refused.</param>
/// <param name="Refusal">Why it was refused, or <see cref="EnqueueRefusal.None"/>.</param>
public sealed record EnqueueResult( RunJob? Job, EnqueueRefusal Refusal );

/// <summary>
/// Holds every run of this process and the queue feeding the background worker.
/// Why a queue with one worker: there is one GPU, and two runs embedding at once would only
/// fight over it (and over which model the service keeps loaded).
/// Runs are kept in memory; the durable record of what was written lives in the state store.
/// One lock covers every status change that decides who owns a job (queued to started,
/// queued to cancelled, reset gate), so a cancel and the worker picking the job up can never
/// both win.
/// </summary>
public sealed class RunRegistry
{
   #region Data Members

   private const int QUEUE_CAPACITY = 20;

   private readonly object _gate = new();
   private readonly ConcurrentDictionary<string, RunJob> _jobs = new( StringComparer.Ordinal );
   private readonly HashSet<string> _resetting = new( StringComparer.Ordinal );
   private readonly Channel<RunJob> _queue = Channel.CreateUnbounded<RunJob>( new UnboundedChannelOptions { SingleReader = true } );

   #endregion Data Members

   #region Public Methods

   /// <summary>The queue the background worker reads.</summary>
   public ChannelReader<RunJob> Reader => _queue.Reader;

   /// <summary>
   /// Queues a run. The limit counts runs still waiting, so a run cancelled while queued
   /// gives its place back at once.
   /// </summary>
   /// <param name="request">The request (pipeline name already sanitized).</param>
   /// <returns>The job, or the reason it was refused.</returns>
   public EnqueueResult Enqueue( RunRequest request )
   {
      lock( _gate )
      {
         if( _resetting.Contains( request.Pipeline ) )
         {
            return new EnqueueResult( null, EnqueueRefusal.PipelineResetting );
         }

         if( _jobs.Values.Count( j => j.Progress.Status == RunStatus.Queued && !j.Progress.IsFinished ) >= QUEUE_CAPACITY )
         {
            return new EnqueueResult( null, EnqueueRefusal.QueueFull );
         }

         string runId = Guid.NewGuid().ToString( "N" )[..12];
         ( RunRequest kept, RunSecret? login ) = SeparatePassword( request );
         var job = new RunJob( kept, new RunProgress( runId, request.Pipeline ), new CancellationTokenSource(), login );
         _jobs[runId] = job;
         _queue.Writer.TryWrite( job );
         return new EnqueueResult( job, EnqueueRefusal.None );
      }
   }

   /// <summary>
   /// Finds a run by id.
   /// </summary>
   /// <param name="runId">Run id.</param>
   /// <returns>The job, or null.</returns>
   public RunJob? Find( string runId )
   {
      return _jobs.TryGetValue( runId, out RunJob? job ) ? job : null;
   }

   /// <summary>
   /// Takes a snapshot that cannot show a finished run as still going. Finish() writes the
   /// status before the finish time, so a snapshot taken while a run ends can carry a finish
   /// time next to the old status; the page would then close its stream on "Running". Once
   /// the finish time is seen the status is already final, so reading again fixes it.
   /// </summary>
   /// <param name="progress">The run's progress.</param>
   /// <returns>A snapshot whose status agrees with its finish time.</returns>
   public static RunSnapshot ConsistentSnapshot( RunProgress progress )
   {
      RunSnapshot snapshot = progress.Snapshot();
      bool stillGoing = snapshot.Status is RunStatus.Queued or RunStatus.Scanning or RunStatus.Running;
      return snapshot.FinishedUtc.HasValue && stillGoing ? progress.Snapshot() : snapshot;
   }

   /// <summary>
   /// Lists recent runs, newest first, so a reloaded page can find a run still going.
   /// </summary>
   /// <returns>Snapshots.</returns>
   public IReadOnlyList<RunSnapshot> Recent()
   {
      return _jobs.Values.Select( j => ConsistentSnapshot( j.Progress ) ).OrderByDescending( s => s.StartedUtc ).Take( 20 ).ToList();
   }

   /// <summary>
   /// Cancels a run. A run still waiting in the queue is finished as Cancelled right here, so
   /// the page sees it at once and the queue slot is free again; a run that already started
   /// is told to stop and finishes as Cancelled when the pipeline notices.
   /// </summary>
   /// <param name="runId">Run id.</param>
   /// <returns>False when no such run exists.</returns>
   public bool Cancel( string runId )
   {
      RunJob? job = Find( runId );
      if( job == null )
      {
         return false;
      }

      lock( _gate )
      {
         if( job.Progress.Status == RunStatus.Queued && !job.Progress.IsFinished )
         {
            job.Progress.Finish( RunStatus.Cancelled, "cancelled before it started" );
            job.Login?.Clear();
         }
      }

      job.Cancel.Cancel();
      return true;
   }

   /// <summary>
   /// Called by the worker when it takes a job off the queue. A job that was cancelled while
   /// it waited is skipped; any other job becomes Scanning.
   /// </summary>
   /// <param name="job">The job the worker just dequeued.</param>
   /// <returns>True when the worker should run it.</returns>
   public bool TryStart( RunJob job )
   {
      lock( _gate )
      {
         if( job.Progress.IsFinished )
         {
            return false;
         }

         job.Progress.Status = RunStatus.Scanning;
         return true;
      }
   }

   /// <summary>
   /// True while a pipeline has a run that is queued or running.
   /// </summary>
   /// <param name="pipeline">Sanitized pipeline name.</param>
   /// <returns>True when a run for it has not finished.</returns>
   public bool IsPipelineActive( string pipeline )
   {
      return _jobs.Values.Any( j => j.Request.Pipeline == pipeline && !j.Progress.IsFinished );
   }

   /// <summary>
   /// Claims a pipeline for a reset. Refused while the pipeline has a queued or running run;
   /// once claimed, new runs for it are refused until <see cref="EndReset"/>, so a run can
   /// never slip in between the check and the drop.
   /// </summary>
   /// <param name="pipeline">Sanitized pipeline name.</param>
   /// <returns>False when a run for it is still active or another reset is under way.</returns>
   public bool TryBeginReset( string pipeline )
   {
      lock( _gate )
      {
         return !IsPipelineActive( pipeline ) && _resetting.Add( pipeline );
      }
   }

   /// <summary>
   /// Releases a pipeline claimed by <see cref="TryBeginReset"/>.
   /// </summary>
   /// <param name="pipeline">Sanitized pipeline name.</param>
   public void EndReset( string pipeline )
   {
      lock( _gate )
      {
         _resetting.Remove( pipeline );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Splits the password off a database request. The registry keeps every run for the life of
   /// the process, so a password left in the request would stay in memory long after its run
   /// ended; it goes into a <see cref="RunSecret"/> that the worker empties instead.
   /// </summary>
   /// <param name="request">The request as received.</param>
   /// <returns>The request to keep (no password), and the secret holding the password, or null when there is none.</returns>
   private static ( RunRequest Kept, RunSecret? Login ) SeparatePassword( RunRequest request )
   {
      if( request.Odbc?.Password == null )
      {
         return ( request, null );
      }

      return ( request with { Odbc = request.Odbc with { Password = null } }, new RunSecret( request.Odbc.Password ) );
   }

   #endregion Private Methods
}
