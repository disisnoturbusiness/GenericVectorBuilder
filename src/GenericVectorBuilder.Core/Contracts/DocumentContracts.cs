namespace GenericVectorBuilder.Core.Contracts;

/// <summary>
/// A source record after mapping: the text that gets embedded, a stable key, and metadata
/// that travels with every chunk into every sink.
/// Why: the content hash lets a re-run skip anything unchanged, which is what makes a nightly
/// rebuild of a big folder cost minutes instead of hours.
/// </summary>
/// <param name="DocKey">Stable identity of the record across runs, e.g. "t3f9a2c41d0e7b6a5|10042": the table's stable
/// id plus the key value. At most about 200 characters, so it fits every sink's key column.</param>
/// <param name="Table">Logical table name.</param>
/// <param name="Origin">Physical origin (file or file#sheet), used to scope deletes.</param>
/// <param name="Text">The text that will be chunked and embedded.</param>
/// <param name="ContentHash">SHA-256 of <paramref name="Text"/>, hex encoded.</param>
/// <param name="Metadata">Extra fields stored beside the vector (row number, key column, ...).</param>
public sealed record Document( string DocKey, string Table, string Origin, string Text, string ContentHash, IReadOnlyDictionary<string, string> Metadata );

/// <summary>
/// One embeddable piece of a document. Most table rows are a single chunk; long text fields
/// are split so the embedder never truncates them.
/// </summary>
/// <param name="ChunkId">Deterministic id derived from the pipeline, doc key and chunk text, so an
/// unchanged chunk keeps its id across runs and upserts stay idempotent.</param>
/// <param name="DocKey">Key of the owning document.</param>
/// <param name="Ordinal">0-based position of the chunk inside the document.</param>
/// <param name="Text">Chunk text sent to the embedder.</param>
public sealed record Chunk( Guid ChunkId, string DocKey, int Ordinal, string Text );

/// <summary>
/// A chunk with its embedding, ready to be written to a sink.
/// </summary>
/// <param name="Document">The owning document (for table, origin and metadata).</param>
/// <param name="Chunk">The chunk that was embedded.</param>
/// <param name="Vector">The L2-normalized embedding.</param>
public sealed record VectorRecord( Document Document, Chunk Chunk, float[] Vector );

/// <summary>
/// One search result from a sink, used by the UI's "try a search" box and the quality gate.
/// </summary>
/// <param name="ChunkId">Id of the matching chunk.</param>
/// <param name="DocKey">Key of the owning document.</param>
/// <param name="Table">Logical table name.</param>
/// <param name="Text">The chunk text that matched.</param>
/// <param name="Score">Cosine similarity, higher is better (1 - cosine distance for SQL).</param>
public sealed record SearchHit( Guid ChunkId, string DocKey, string Table, string Text, double Score );
