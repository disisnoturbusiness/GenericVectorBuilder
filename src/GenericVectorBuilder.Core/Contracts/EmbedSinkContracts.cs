namespace GenericVectorBuilder.Core.Contracts;

/// <summary>
/// Turns text into vectors. Documents and queries are separate calls because most modern
/// embedders format them differently (instruction prefixes, end-of-sequence tokens).
/// Why: getting the document/query convention wrong produces plausible-looking but bad
/// search results, so the embedder owns that convention and nothing else touches it.
/// </summary>
public interface IEmbedder
{
   /// <summary>
   /// Identity of the model plus every formatting convention. Stored per pipeline so a run
   /// with a different embedder is refused instead of silently mixing incompatible vectors.
   /// </summary>
   string Fingerprint { get; }

   /// <summary>
   /// Vector length. Known only after <see cref="PreflightAsync"/> has run.
   /// </summary>
   int Dimension { get; }

   /// <summary>
   /// Embeds one probe string to prove the service is up and to learn the real dimension.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   Task PreflightAsync( CancellationToken ct );

   /// <summary>
   /// Embeds document chunks.
   /// </summary>
   /// <param name="texts">Chunk texts.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>One vector per input, same order.</returns>
   Task<float[][]> EmbedDocumentsAsync( IReadOnlyList<string> texts, CancellationToken ct );

   /// <summary>
   /// Embeds a search query using the model's query convention.
   /// </summary>
   /// <param name="query">The user's question.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The query vector.</returns>
   Task<float[]> EmbedQueryAsync( string query, CancellationToken ct );
}

/// <summary>
/// A vector store the builder writes to. Every sink keeps one collection (or table) per pipeline.
/// Why: one interface lets a single run fan out the same vectors to SQL Server, Qdrant and
/// later Azure AI Search, while each sink handles its own batching and retry.
/// </summary>
public interface ISink
{
   /// <summary>
   /// Short stable name ("sql", "qdrant") used in state tracking and the UI.
   /// </summary>
   string Name { get; }

   /// <summary>
   /// Creates the collection if missing, or verifies an existing one has the same dimension.
   /// Throws with a plain-English message on a mismatch rather than writing bad data.
   /// Why it reports creation: the pipeline's state says what the sink holds. If the collection
   /// had to be created (a wiped volume, a different database, a reset), that state is stale and
   /// must be discarded so every document is written again instead of being skipped as unchanged.
   /// </summary>
   /// <param name="collection">Sanitized pipeline collection name.</param>
   /// <param name="dimension">Embedding dimension.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the collection did not exist and was created by this call.</returns>
   Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct );

   /// <summary>
   /// Inserts or replaces the given vectors. Must be idempotent so a retried batch is harmless.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="records">Vectors to write.</param>
   /// <param name="ct">Cancellation.</param>
   Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct );

   /// <summary>
   /// Removes chunks by id. Missing ids are ignored.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="chunkIds">Ids to remove.</param>
   /// <param name="ct">Cancellation.</param>
   Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct );

   /// <summary>
   /// Counts the chunks the collection holds right now, exactly.
   /// Why: the pipeline's state says what a sink holds, but a collection can be emptied behind
   /// its back (a truncated table, points deleted by hand) while it still exists, so
   /// <see cref="EnsureCollectionAsync"/> cannot notice. Comparing this count with state lets a
   /// run rewrite an emptied sink instead of skipping every row as unchanged.
   /// The default throws <see cref="NotSupportedException"/>, which the pipeline treats as
   /// "cannot count" and simply skips the check, so a sink written before this member existed
   /// keeps working.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The number of chunks stored.</returns>
   Task<long> CountAsync( string collection, CancellationToken ct )
   {
      throw new NotSupportedException( $"The {Name} destination cannot count its chunks." );
   }

   /// <summary>
   /// Nearest-neighbour search, used by the UI and the retrieval quality gate.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">How many hits to return.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct );

   /// <summary>
   /// Drops the whole collection. Used by "reset pipeline" and test cleanup.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   Task DropCollectionAsync( string collection, CancellationToken ct );
}
