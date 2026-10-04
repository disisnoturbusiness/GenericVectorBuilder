using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Engines.Common;

/// <summary>
/// Optional capability for a sink that can also search by brute force (no approximate index).
/// Why: the benchmark compares each engine's normal (usually approximate) search against the
/// exact answer. The benchmark computes its own exact ground truth in memory, and this lets it
/// also time each engine's own exact mode where the engine has one.
/// </summary>
public interface IExactSearchSink
{
   /// <summary>
   /// Exact nearest-neighbour search: every stored vector is compared, no index shortcuts.
   /// </summary>
   /// <param name="collection">Sanitized pipeline collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">How many hits to return.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity scores.</returns>
   Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct );
}

/// <summary>
/// Facts about an engine sink that the benchmark report prints next to its numbers.
/// Why: speed numbers mean nothing without the index and settings that produced them.
/// </summary>
public interface IEngineDescription
{
   /// <summary>Display name, e.g. "PostgreSQL 17 + pgvector 0.8.7".</summary>
   string Engine { get; }

   /// <summary>How the default search works, e.g. "HNSW m=16 ef_construction=64, ef_search=40, cosine".</summary>
   string IndexDescription { get; }

   /// <summary>Compose file under deploy/engines that starts this engine, e.g. "pgvector.compose.yaml".</summary>
   string ComposeFile { get; }

   /// <summary>
   /// What a crash can lose with this engine's settings as configured here, e.g. "fsync on every
   /// commit" or "snapshot every 5 minutes, no append-only log". The default says it was not
   /// stated, which the benchmark report flags, so a missing disclosure is visible.
   /// </summary>
   string Durability => "not stated";
}
