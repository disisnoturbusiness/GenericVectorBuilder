namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Where Chroma is and how its HNSW index is built. The defaults match the local container
/// started by deploy/engines/chroma.compose.yaml.
/// Why these index numbers: M=16 and ef_construction=128 are the values every engine in the
/// benchmark uses where it can be tuned, so the speed comparison is not decided by index settings.
/// ef_search stays at Chroma's own default of 100, which is the setting a normal user gets.
/// </summary>
/// <param name="BaseUrl">Server address, e.g. http://127.0.0.1:8000.</param>
/// <param name="Tenant">Chroma tenant. The server creates "default_tenant" itself.</param>
/// <param name="Database">Chroma database. The server creates "default_database" itself.</param>
/// <param name="MaxNeighbors">HNSW M, the links kept per node.</param>
/// <param name="EfConstruction">HNSW candidate list size while building.</param>
/// <param name="EfSearch">HNSW candidate list size while searching.</param>
public sealed record ChromaSinkOptions(
   string BaseUrl = "http://127.0.0.1:8000",
   string Tenant = "default_tenant",
   string Database = "default_database",
   int MaxNeighbors = 16,
   int EfConstruction = 128,
   int EfSearch = 100 );
