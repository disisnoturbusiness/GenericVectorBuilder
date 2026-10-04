namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Where Weaviate is and how its HNSW index is built. The defaults match the local container
/// started by deploy/engines/weaviate.compose.yaml (HTTP on host port 8085).
/// Why these index numbers: M=16 and ef_construction=128 are the values every engine in the
/// benchmark uses where it can be tuned. Weaviate's own defaults are maxConnections 32 and
/// efConstruction 128. Query ef stays at Weaviate's dynamic default (-1).
/// </summary>
/// <param name="BaseUrl">Server address, e.g. http://127.0.0.1:8085.</param>
/// <param name="MaxConnections">HNSW M, the links kept per node.</param>
/// <param name="EfConstruction">HNSW candidate list size while building.</param>
/// <param name="Ef">HNSW candidate list size while searching; -1 lets Weaviate pick from the result limit (limit x 8, clamped to 100..500).</param>
public sealed record WeaviateSinkOptions(
   string BaseUrl = "http://127.0.0.1:8085",
   int MaxConnections = 16,
   int EfConstruction = 128,
   int Ef = -1 );
