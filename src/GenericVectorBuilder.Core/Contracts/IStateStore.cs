namespace GenericVectorBuilder.Core.Contracts;

/// <summary>
/// What a sink currently holds for one document: the hash of the text that produced it, the
/// origin it came from, and the chunk ids written.
/// </summary>
/// <param name="DocKey">Document key.</param>
/// <param name="ContentHash">Hash of the text last written to the sink.</param>
/// <param name="Origin">Origin the document was last read from.</param>
/// <param name="ChunkIds">Chunk ids currently in the sink for this document.</param>
public sealed record DocState( string DocKey, string ContentHash, string Origin, IReadOnlyList<Guid> ChunkIds );

/// <summary>
/// Remembers, per pipeline and per sink, what has been written. Kept outside every sink so
/// sinks can be added or dropped without migrating anything.
/// Why: incremental runs need to know what is already in each sink. Asking the sinks would
/// mean a different query per sink type; one local store answers it the same way for all.
/// </summary>
public interface IStateStore
{
   /// <summary>
   /// Loads every document state for a pipeline and sink.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="sink">Sink name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>States keyed by document key.</returns>
   Task<Dictionary<string, DocState>> LoadAsync( string pipeline, string sink, CancellationToken ct );

   /// <summary>
   /// Inserts or replaces document states. The runner calls it twice per write: before the
   /// upsert with every chunk id the sink may end up holding (old and new), and after the sink
   /// acknowledged the write with the final ids, so no chunk is ever in a sink without state
   /// knowing its id.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="sink">Sink name.</param>
   /// <param name="states">States to insert or replace.</param>
   /// <param name="ct">Cancellation.</param>
   Task SaveAsync( string pipeline, string sink, IReadOnlyList<DocState> states, CancellationToken ct );

   /// <summary>
   /// Forgets documents after their chunks were deleted from the sink.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="sink">Sink name.</param>
   /// <param name="docKeys">Keys to forget.</param>
   /// <param name="ct">Cancellation.</param>
   Task RemoveAsync( string pipeline, string sink, IReadOnlyList<string> docKeys, CancellationToken ct );

   /// <summary>
   /// Reads the embedder fingerprint a pipeline was built with, or null for a new pipeline.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The stored fingerprint, or null.</returns>
   Task<string?> GetFingerprintAsync( string pipeline, CancellationToken ct );

   /// <summary>
   /// Records the embedder fingerprint for a pipeline.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="fingerprint">Embedder fingerprint.</param>
   /// <param name="ct">Cancellation.</param>
   Task SetFingerprintAsync( string pipeline, string fingerprint, CancellationToken ct );

   /// <summary>
   /// Forgets every document state of one sink for a pipeline, leaving the other sinks and the
   /// fingerprint alone. Used when a sink's collection had to be recreated, so its old state no
   /// longer describes what the sink holds.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="sink">Sink name.</param>
   /// <param name="ct">Cancellation.</param>
   Task ForgetSinkAsync( string pipeline, string sink, CancellationToken ct );

   /// <summary>
   /// Removes everything known about a pipeline. Used by "reset pipeline".
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="ct">Cancellation.</param>
   Task ForgetPipelineAsync( string pipeline, CancellationToken ct );

   /// <summary>
   /// Lists pipeline names that have state, for the UI's pipeline picker.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Pipeline names.</returns>
   Task<IReadOnlyList<string>> ListPipelinesAsync( CancellationToken ct );
}
