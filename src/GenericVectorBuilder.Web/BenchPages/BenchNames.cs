namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The name a reader sees for a Bench target, for the pages that have only the target name (a run
/// page, the folded engine sections of a report, a row of the why table).
/// Why a table here: the consolidated file carries its own display name for each row, and this
/// table is only the fallback for a page that has none.
/// </summary>
public static class BenchNames
{
   #region Data Members

   private static readonly Dictionary<string, string> FRIENDLY = new( StringComparer.OrdinalIgnoreCase )
   {
      ["sql"] = "SQL Server 2025",
      ["sql-diskann"] = "SQL Server 2025 + DiskANN",
      ["sql-native"] = "SQL Server 2025 (native service)",
      ["qdrant"] = "Qdrant (exact)",
      ["qdrant-hnsw"] = "Qdrant (HNSW)",
      ["qdrant-native"] = "Qdrant (native service, exact)",
      ["pgvector"] = "pgvector",
      ["mariadb"] = "MariaDB",
      ["oracle"] = "Oracle 23ai Free",
      ["redis"] = "Redis",
      ["mongodb"] = "MongoDB Atlas Local",
      ["clickhouse"] = "ClickHouse",
      ["milvus"] = "Milvus",
      ["weaviate"] = "Weaviate",
      ["chroma"] = "Chroma",
      ["elasticsearch"] = "Elasticsearch",
      ["opensearch"] = "OpenSearch",
      ["typesense"] = "Typesense",
      ["vespa"] = "Vespa",
      ["duckdb"] = "DuckDB",
      ["sqlitevec"] = "sqlite-vec",
   };

   /// <summary>Endings a benchmark target name may carry to say it runs in the benchmark's own container ("mariadb-bench"); the engine's name is what precedes it.</summary>
   private static readonly string[] CONTAINER_SUFFIXES = { "-bench", "-container", "-compose" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Every target name this table knows, so a test can check that no fixed page text names an engine.
   /// </summary>
   public static IReadOnlyCollection<string> KnownTargets => FRIENDLY.Keys;

   /// <summary>
   /// The name a reader sees for a Bench target, or the target name itself when unknown.
   /// The two native comparison targets say so ("sql-native" is "SQL Server 2025 (native service)"),
   /// and a target that runs in the benchmark's own container keeps its engine's name: "mariadb"
   /// and "mariadb-bench" are both "MariaDB", because the container is where it runs and not what
   /// the reader is comparing.
   /// </summary>
   /// <param name="key">Target name, e.g. "sqlitevec".</param>
   /// <returns>Friendly name, e.g. "sqlite-vec".</returns>
   public static string FriendlyName( string key )
   {
      if( FRIENDLY.TryGetValue( key, out string? name ) )
      {
         return name;
      }

      string? suffix = CONTAINER_SUFFIXES.FirstOrDefault( x => key.EndsWith( x, StringComparison.OrdinalIgnoreCase ) );
      return suffix != null && FRIENDLY.TryGetValue( key[..^suffix.Length], out string? engine ) ? engine : key;
   }

   /// <summary>
   /// Every friendly name this table gives, so a test can check that no fixed page text names an engine.
   /// </summary>
   public static IReadOnlyCollection<string> KnownNames => FRIENDLY.Values;

   #endregion Public Methods
}
