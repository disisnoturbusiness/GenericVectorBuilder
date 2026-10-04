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
/// <param name="ManagementUrl">
/// Milvus's health and management port (9091 in milvus.compose.yaml). Why it is here: the REST API on
/// the main port says when the index is built, but only the management port says which segments the
/// query node serves and whether each has its index loaded, and a search uses the index only once
/// the query node serves the sealed, indexed segment instead of the raw growing one.
/// </param>
/// <param name="IndexWaitMinutes">Longest <see cref="MilvusSink.FinishLoadAsync"/> waits for the index before it fails with a plain message.</param>
public sealed record MilvusSinkOptions(
   string BaseUrl = "http://127.0.0.1:19530",
   int M = 16,
   int EfConstruction = 128,
   int? Ef = 100,
   string SearchConsistency = "Strong",
   string ManagementUrl = "http://127.0.0.1:9091",
   int IndexWaitMinutes = 30 );
