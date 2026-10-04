namespace GenericVectorBuilder.Engines.Common;

/// <summary>
/// Proof of what an engine's index holds at a moment, read from the engine itself.
/// Why: a benchmark that searches a half-built index, or no index at all, measures the wrong
/// thing. The report prints this proof next to every engine's numbers.
/// </summary>
/// <param name="Ready">True only when the engine itself says searches now use the finished index.</param>
/// <param name="IndexedVectors">Vectors covered by the index, as the engine reports it (null when the engine cannot say).</param>
/// <param name="TotalVectors">Vectors stored, as the engine reports it (null when the engine cannot say).</param>
/// <param name="Detail">Short plain-English evidence, e.g. "indexed_vectors_count 524 of 524, status green".</param>
public sealed record IndexState( bool Ready, long? IndexedVectors, long? TotalVectors, string Detail );

/// <summary>
/// Optional step for an engine whose index is built or finished after the rows are loaded
/// (SQL Server's DiskANN index can only be built on a full table; Qdrant, Milvus and others
/// build in the background after the writes return).
/// Why it is a separate, timed step: searching before the index is ready measures a
/// half-built index, and the build time is part of what loading costs.
/// </summary>
public interface IIndexFinisher
{
   /// <summary>
   /// Builds or waits for the index of a loaded collection and returns only when searches use
   /// it. Throws with a plain message when the index is not ready within the engine's limit.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A short note on what was built (shown in the report).</returns>
   Task<string> FinishLoadAsync( string collection, CancellationToken ct );

   /// <summary>
   /// Reads the index state from the engine for the report. The default says nothing was
   /// reported, which the benchmark flags as a warning, so an engine without proof is visible.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine's own account of its index.</returns>
   Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      return Task.FromResult( new IndexState( false, null, null, "not reported by this engine" ) );
   }
}
