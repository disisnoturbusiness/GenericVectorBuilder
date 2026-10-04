namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Where Milvus is and how its HNSW index is built. The defaults match the local container
/// started by deploy/engines/milvus.compose.yaml (REST API on host port 19530).
/// Why these index numbers: M=16 and ef_construction=128 are the values every engine in the
/// benchmark uses where it can be tuned. Query ef is left to Milvus unless set here.
/// </summary>
/// <param name="BaseUrl">Server address, e.g. http://127.0.0.1:19530.</param>
/// <param name="M">HNSW M, the links kept per node.</param>
/// <param name="EfConstruction">HNSW candidate list size while building.</param>
/// <param name="Ef">HNSW candidate list size while searching, or null to let Milvus choose.</param>
/// <param name="SearchConsistency">Milvus consistency level for searches: Strong, Bounded, Session or Eventually.</param>
public sealed record MilvusSinkOptions(
   string BaseUrl = "http://127.0.0.1:19530",
   int M = 16,
   int EfConstruction = 128,
   int? Ef = 100,
   string SearchConsistency = "Strong" );
