using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Core.Pipeline;

/// <summary>
/// A mapped, chunked document waiting to be embedded, and the sinks that need it.
/// </summary>
/// <param name="Document">The document.</param>
/// <param name="Chunks">Its chunks.</param>
/// <param name="Sinks">Sinks that lack its current content.</param>
internal sealed record PendingDocument( Document Document, IReadOnlyList<Chunk> Chunks, IReadOnlyList<ISink> Sinks );

/// <summary>
/// An unchanged document whose stored state lists chunk ids the current chunking no longer
/// produces (left over from an interrupted write or a chunker change). The extra ids are
/// deleted from the sink, then the trimmed state is saved.
/// </summary>
/// <param name="Sink">The sink holding the extra chunks.</param>
/// <param name="State">The trimmed state to save once the extra chunks are gone.</param>
/// <param name="Extra">Chunk ids to delete.</param>
internal sealed record ChunkCleanup( ISink Sink, DocState State, IReadOnlyList<Guid> Extra );

/// <summary>
/// Mutable state for one run of <see cref="PipelineRunner"/>: which sinks are still healthy,
/// what each sink holds, which documents were seen, and the batch waiting for the embedder.
/// Why a separate class: it keeps the runner's methods small and makes the one piece of
/// shared mutable state (the active sink list, changed from parallel sink tasks) explicit and
/// locked in one place.
/// </summary>
internal sealed class RunContext
{
   #region Data Members

   private readonly object _sync = new();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the context.
   /// </summary>
   /// <param name="pipeline">Sanitized pipeline name.</param>
   public RunContext( string pipeline )
   {
      Pipeline = pipeline;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Pipeline name.</summary>
   public string Pipeline { get; }

   /// <summary>Sinks still taking writes. Change only through <see cref="Drop"/>.</summary>
   public List<ISink> ActiveSinks { get; } = new();

   /// <summary>Sinks dropped so far in this run.</summary>
   public int FailedSinks { get; set; }

   /// <summary>Per-sink document state, keyed by sink name then document key.</summary>
   public Dictionary<string, Dictionary<string, DocState>> States { get; } = new( StringComparer.Ordinal );

   /// <summary>
   /// Documents each sink's state recorded when the run started, before this run wrote
   /// anything, keyed by sink name. The mass-delete guard measures "more than half" against
   /// this, because counting this run's own writes would let a run over a different folder
   /// of similar size delete everything the pipeline held.
   /// </summary>
   public Dictionary<string, int> StoredBefore { get; } = new( StringComparer.Ordinal );

   /// <summary>Every document key produced this run.</summary>
   public HashSet<string> Seen { get; } = new( StringComparer.Ordinal );

   /// <summary>Origins where at least one row failed to map; protected from deletes.</summary>
   public HashSet<string> ErrorOrigins { get; } = new( StringComparer.Ordinal );

   /// <summary>Documents deleted so far, so a delete is counted once across sinks.</summary>
   public HashSet<string> DeletedKeys { get; } = new( StringComparer.Ordinal );

   /// <summary>Documents waiting for the embedder.</summary>
   public List<PendingDocument> Pending { get; } = new();

   /// <summary>Chunks across <see cref="Pending"/>.</summary>
   public int PendingChunks { get; set; }

   /// <summary>Unchanged documents whose origin moved, to be saved at the end.</summary>
   public List<(string Sink, DocState State)> MovedOrigins { get; } = new();

   /// <summary>Unchanged documents with leftover chunk ids in a sink, cleaned up at the end of the read.</summary>
   public List<ChunkCleanup> Cleanups { get; } = new();

   /// <summary>Records the source produced, used by the guard that refuses deletes after an empty read.</summary>
   public long RecordsRead { get; set; }

   /// <summary>Why deletes were refused this run, or null when they were allowed.</summary>
   public string? DeletesRefused { get; set; }

   /// <summary>
   /// Origins whose stored documents were kept although the origin was neither read nor
   /// rejected, so nothing else tells the user about them. Any makes the run Partial.
   /// </summary>
   public int UnconfirmedOrigins { get; set; }

   /// <summary>
   /// Copies the active sink list so callers can iterate while a parallel task drops a sink.
   /// </summary>
   /// <returns>The current active sinks.</returns>
   public List<ISink> ActiveSinksSnapshot()
   {
      lock( _sync )
      {
         return ActiveSinks.ToList();
      }
   }

   /// <summary>
   /// Removes a failed sink from the run.
   /// </summary>
   /// <param name="sink">The sink.</param>
   public void Drop( ISink sink )
   {
      lock( _sync )
      {
         if( ActiveSinks.Remove( sink ) )
         {
            FailedSinks++;
         }
      }
   }

   #endregion Public Methods
}
