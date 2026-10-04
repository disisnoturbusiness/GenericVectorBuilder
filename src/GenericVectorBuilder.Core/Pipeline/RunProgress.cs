using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Core.Pipeline;

/// <summary>
/// Lifecycle of a run as shown in the UI.
/// </summary>
public enum RunStatus
{
   /// <summary>Waiting for the GPU (one run at a time).</summary>
   Queued,

   /// <summary>Re-scanning the folder so the run reads exactly what is there now.</summary>
   Scanning,

   /// <summary>Reading, embedding and writing.</summary>
   Running,

   /// <summary>Finished; every sink took every changed document.</summary>
   Completed,

   /// <summary>Finished, but a sink failed or some rows could not be mapped.</summary>
   Partial,

   /// <summary>Stopped by an error before finishing.</summary>
   Failed,

   /// <summary>Stopped by the user.</summary>
   Cancelled,
}

/// <summary>
/// Point-in-time copy of a run's progress, serialized to the browser.
/// The last three counters were added later and sit at the end so existing readers are unaffected:
/// SkippedRows (rows with more cells than the header, left out; their file keeps its old vectors),
/// MissingKeyRows (rows whose key column was empty, keyed by content instead), and DocsKept
/// (stored documents the run did not see but kept, because their file was not read completely
/// and could not be confirmed as removed).
/// </summary>
public sealed record RunSnapshot(
   string RunId, string Pipeline, RunStatus Status, string? Message, long TotalRows, long RecordsRead, long DocsUnchanged,
   long DocsEmbedded, long ChunksEmbedded, long DocsDeleted, long EmptyRows, long DocErrors, IReadOnlyDictionary<string, long> ChunksWritten,
   IReadOnlyDictionary<string, string> SinkErrors, IReadOnlyList<string> Errors, IReadOnlyList<RejectedOrigin> Rejected,
   DateTime StartedUtc, DateTime? FinishedUtc, long SkippedRows = 0, long MissingKeyRows = 0, long DocsKept = 0 );

/// <summary>
/// Live, thread-safe progress for one run. The runner bumps counters from several tasks at
/// once (one per sink), and the web layer reads snapshots for the progress stream.
/// Why Finish and Snapshot share a lock: the progress stream closes as soon as it sends a
/// finished snapshot, so that last snapshot must never mix a final timestamp with a running
/// status or an empty reject list.
/// </summary>
public sealed class RunProgress
{
   #region Data Members

   private const int MAX_ERRORS_KEPT = 20;

   private readonly object _finishSync = new();
   private readonly ConcurrentDictionary<string, long> _written = new();
   private readonly ConcurrentDictionary<string, string> _sinkErrors = new();
   private readonly ConcurrentQueue<string> _errors = new();
   private long _recordsRead;
   private long _docsUnchanged;
   private long _docsEmbedded;
   private long _chunksEmbedded;
   private long _docsDeleted;
   private long _emptyRows;
   private long _docErrors;
   private long _docsKept;
   private long _skippedRows;
   private long _missingKeyRows;
   private DateTime? _finishedUtc;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates progress for a queued run.
   /// </summary>
   /// <param name="runId">Run id.</param>
   /// <param name="pipeline">Pipeline name.</param>
   public RunProgress( string runId, string pipeline )
   {
      RunId = runId;
      Pipeline = pipeline;
      StartedUtc = DateTime.UtcNow;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Run id.</summary>
   public string RunId { get; }

   /// <summary>Pipeline name.</summary>
   public string Pipeline { get; }

   /// <summary>Current status.</summary>
   public RunStatus Status { get; set; } = RunStatus.Queued;

   /// <summary>What the run is doing right now, in plain English.</summary>
   public string? Message { get; set; }

   /// <summary>Rows the scan found, the denominator for the progress bar.</summary>
   public long TotalRows { get; set; }

   /// <summary>
   /// Text added in brackets to the final message, or null for none, e.g. "git commit 1a2b..."
   /// for a run that read a git repository. Why: the runner writes the final message, but only
   /// the caller knows facts like which commit was read, and the final message is what stays
   /// on the page and in the log.
   /// </summary>
   public string? FinishNote { get; set; }

   /// <summary>Files or sheets the source refused, copied from its report at the end.</summary>
   public IReadOnlyList<RejectedOrigin> Rejected { get; set; } = Array.Empty<RejectedOrigin>();

   /// <summary>When the run started.</summary>
   public DateTime StartedUtc { get; }

   /// <summary>When the run finished, or null while it is going.</summary>
   public DateTime? FinishedUtc
   {
      get
      {
         lock( _finishSync )
         {
            return _finishedUtc;
         }
      }
   }

   /// <summary>Errors seen so far (document-level, capped).</summary>
   public long DocErrors => Interlocked.Read( ref _docErrors );

   /// <summary>True once the run reached a final status.</summary>
   public bool IsFinished => FinishedUtc.HasValue;

   /// <summary>Rows the source skipped because they had more cells than the header.</summary>
   public long SkippedRows
   {
      get => Interlocked.Read( ref _skippedRows );
      set => Interlocked.Exchange( ref _skippedRows, value );
   }

   /// <summary>Rows whose key column was empty, so they were keyed by content.</summary>
   public long MissingKeyRows
   {
      get => Interlocked.Read( ref _missingKeyRows );
      set => Interlocked.Exchange( ref _missingKeyRows, value );
   }

   /// <summary>Stored documents not seen this run that were kept because their file could not be confirmed as removed.</summary>
   public long DocsKept => Interlocked.Read( ref _docsKept );

   /// <summary>Counts a record read from the source.</summary>
   public void RecordRead() => Interlocked.Increment( ref _recordsRead );

   /// <summary>Counts a document skipped because every sink already holds it.</summary>
   public void Unchanged() => Interlocked.Increment( ref _docsUnchanged );

   /// <summary>Counts a row with no text at all.</summary>
   public void EmptyRow() => Interlocked.Increment( ref _emptyRows );

   /// <summary>
   /// Counts embedded documents and chunks.
   /// </summary>
   /// <param name="docs">Documents embedded.</param>
   /// <param name="chunks">Chunks embedded.</param>
   public void Embedded( int docs, int chunks )
   {
      Interlocked.Add( ref _docsEmbedded, docs );
      Interlocked.Add( ref _chunksEmbedded, chunks );
   }

   /// <summary>
   /// Counts chunks a sink acknowledged.
   /// </summary>
   /// <param name="sink">Sink name.</param>
   /// <param name="chunks">Chunks written.</param>
   public void Written( string sink, int chunks ) => _written.AddOrUpdate( sink, chunks, ( _, v ) => v + chunks );

   /// <summary>
   /// Counts documents deleted because they left the source.
   /// </summary>
   /// <param name="docs">Documents deleted.</param>
   public void Deleted( int docs ) => Interlocked.Add( ref _docsDeleted, docs );

   /// <summary>
   /// Counts stored documents that were not seen but kept, because their file was not read
   /// completely and could not be confirmed as removed.
   /// </summary>
   /// <param name="docs">Documents kept.</param>
   public void Kept( int docs ) => Interlocked.Add( ref _docsKept, docs );

   /// <summary>
   /// Adds a plain-English note to the run's message list without counting it as a row error,
   /// for things the user should know (rows kept, settings that did not apply).
   /// </summary>
   /// <param name="message">The note.</param>
   public void Note( string message ) => AddError( message );

   /// <summary>
   /// Records a document-level error, keeping the first few messages for the UI.
   /// </summary>
   /// <param name="message">What went wrong.</param>
   public void DocError( string message )
   {
      Interlocked.Increment( ref _docErrors );
      AddError( message );
   }

   /// <summary>
   /// Records that a sink was dropped from the run.
   /// </summary>
   /// <param name="sink">Sink name.</param>
   /// <param name="message">Why.</param>
   public void SinkFailed( string sink, string message )
   {
      _sinkErrors[sink] = message;
      AddError( $"{sink}: {message}" );
   }

   /// <summary>
   /// Marks the run finished. <see cref="FinishNote"/>, when set, is added in brackets.
   /// </summary>
   /// <param name="status">Final status.</param>
   /// <param name="message">Final message.</param>
   public void Finish( RunStatus status, string message )
   {
      lock( _finishSync )
      {
         Status = status;
         Message = string.IsNullOrWhiteSpace( FinishNote ) ? message : $"{message} ({FinishNote})";
         _finishedUtc = DateTime.UtcNow;
      }
   }

   /// <summary>
   /// Takes a copy of the counters for serialization. Status, message, reject list and finish
   /// time are read together under the same lock as <see cref="Finish"/>, so a finished
   /// snapshot is always internally consistent.
   /// </summary>
   /// <returns>The snapshot.</returns>
   public RunSnapshot Snapshot()
   {
      lock( _finishSync )
      {
         return new RunSnapshot( RunId, Pipeline, Status, Message, TotalRows, Interlocked.Read( ref _recordsRead ), Interlocked.Read( ref _docsUnchanged ),
            Interlocked.Read( ref _docsEmbedded ), Interlocked.Read( ref _chunksEmbedded ), Interlocked.Read( ref _docsDeleted ), Interlocked.Read( ref _emptyRows ),
            DocErrors, new Dictionary<string, long>( _written ), new Dictionary<string, string>( _sinkErrors ), _errors.ToArray(), Rejected, StartedUtc, _finishedUtc,
            SkippedRows, MissingKeyRows, DocsKept );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Keeps at most <see cref="MAX_ERRORS_KEPT"/> error messages.
   /// </summary>
   /// <param name="message">Message to keep.</param>
   private void AddError( string message )
   {
      if( _errors.Count < MAX_ERRORS_KEPT )
      {
         _errors.Enqueue( message );
      }
   }

   #endregion Private Methods
}

/// <summary>
/// Pipeline name rules. Names become SQL table and Qdrant collection names, so they are
/// reduced to lowercase letters, digits and underscores, which are safe everywhere.
/// </summary>
public static class PipelineNames
{
   #region Data Members

   private const int MAX_LENGTH = 60;
   private static readonly Regex UNSAFE = new( "[^a-z0-9_]+", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Sanitizes a user-entered pipeline name.
   /// </summary>
   /// <param name="name">Raw name, e.g. a folder name.</param>
   /// <returns>A safe name; "pipeline" when nothing usable is left.</returns>
   public static string Sanitize( string? name )
   {
      string clean = UNSAFE.Replace( ( name ?? string.Empty ).Trim().ToLowerInvariant(), "_" ).Trim( '_' );
      clean = clean.Length > MAX_LENGTH ? clean[..MAX_LENGTH].TrimEnd( '_' ) : clean;
      return clean.Length == 0 ? "pipeline" : clean;
   }

   #endregion Public Methods
}
