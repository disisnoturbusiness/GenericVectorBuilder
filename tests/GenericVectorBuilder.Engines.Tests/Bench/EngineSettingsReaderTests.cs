namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The engine settings reader (Targets/EngineSettingsReader.cs): what it asks each engine, which
/// command lines it runs, what it records, and that a failed read is an error that names the key.
/// Why these checks: the v7 sessions recorded no engine settings at all, so a heap, a buffer pool
/// or a worker count could differ between sessions unseen; the readers talk to nineteen engines and
/// cannot be run against them from a unit test, so each is run here against the exact text the
/// engine's client prints (scripted), and the pilot's settings-only run is the live proof.
/// Every secret the container's environment holds (all ten values of secrets.env are planted in each
/// fake container) must stay out of every recorded line, command line and error.
/// </summary>
public sealed class EngineSettingsReaderTests
{
   #region Data Members

   private const string REDIS = "redis";
   private const string CONTAINER_MOUNT = "/home/dan/gvb-data/engines/redis:/data:rw";
   private const string HEAP_JSON = """{"_nodes":{"total":1,"successful":1,"failed":0},"cluster_name":"docker-cluster","nodes":{"vJk1x0QeSIaH7ZxEqTr8Fw":{"name":"a1b2c3","jvm":{"pid":1,"version":"24.0.1","mem":{"heap_init_in_bytes":2147483648,"heap_max_in_bytes":2147483648,"non_heap_init_in_bytes":7667712,"non_heap_max_in_bytes":0,"direct_max_in_bytes":0}}}}}""";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Every container gets its three Docker limits first, read from "docker inspect": the memory in bytes, the CPU set and
   /// the CPU quota, each saying so in its how; an engine with nothing else to read (Milvus here) records only those.
   /// </summary>
   [Fact]
   public void ContainerLimits_AreReadFromDockerInspectAndAreAllThereIsForAnEngineWithNothingElse()
   {
      string[] lines = Read( "milvus", "gvb-milvus", Inspect( "gvb-milvus" ), new[] { "19530:19530" } );
      Assert.Equal( new[]
      {
         "setting|HostConfig.Memory|8589934592|read: docker inspect HostConfig.Memory (0 = no limit)",
         "setting|HostConfig.CpusetCpus|2-3,6-7|read: docker inspect HostConfig.CpusetCpus (empty = every CPU)",
         "setting|HostConfig.NanoCpus|0|read: docker inspect HostConfig.NanoCpus (0 = no limit)",
         "leaks|0",
      }, lines );
   }

   /// <summary>
   /// Redis: four CONFIG GET reads, one docker exec each, run through "sh -c" inside the container with the container's
   /// own REDIS_PASSWORD variable (the host never holds the password), search-workers first. The answers are the
   /// two-line form redis-cli prints (the name, then the value; an empty value is an empty line).
   /// </summary>
   [Fact]
   public void Redis_ReadsFourSettingsWithTheContainersOwnPasswordVariable()
   {
      string[] docker =
      {
         "CONFIG GET search-workers=>0|search-workers\\n8\\n|", "CONFIG GET save=>0|save\\n300 1\\n|", "CONFIG GET appendonly=>0|appendonly\\nno\\n|", "CONFIG GET maxmemory=>0|maxmemory\\n0\\n|",
      };
      string[] lines = Read( REDIS, "gvb-redis", Inspect( "gvb-redis", CONTAINER_MOUNT ), new[] { "6379:6379" }, docker );
      Assert.Contains( "setting|search-workers|8|read: docker exec gvb-redis redis-cli CONFIG GET search-workers", lines );
      Assert.Contains( "setting|save|300 1|read: docker exec gvb-redis redis-cli CONFIG GET save", lines );
      Assert.Contains( "setting|appendonly|no|read: docker exec gvb-redis redis-cli CONFIG GET appendonly", lines );
      Assert.Contains( "setting|maxmemory|0|read: docker exec gvb-redis redis-cli CONFIG GET maxmemory", lines );
      Assert.Equal( new[] { "HostConfig.Memory", "HostConfig.CpusetCpus", "HostConfig.NanoCpus", "search-workers", "save", "appendonly", "maxmemory" }, lines.Where( l => l.StartsWith( "setting|" ) ).Select( l => l.Split( '|' )[1] ).ToArray() );
      Assert.Equal( "call|docker exec gvb-redis sh -c REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli CONFIG GET search-workers", lines.First( l => l.StartsWith( "call|" ) ) );
      Assert.Equal( 4, lines.Count( l => l.StartsWith( "call|docker exec gvb-redis sh -c " ) ) );
      Assert.Equal( "leaks|0", lines[^1] );
   }

   /// <summary>
   /// A CONFIG GET that prints nothing (a name this Redis does not know) or an error line is the target's error naming the
   /// key, the target and how it was read, and the command's own error text is kept short.
   /// </summary>
   [Fact]
   public void Redis_AnUnknownNameOrAnErrorIsAnErrorThatNamesTheKey()
   {
      string[] empty = Read( REDIS, "gvb-redis", Inspect( "gvb-redis", CONTAINER_MOUNT ), new[] { "6379:6379" }, new[] { "CONFIG GET search-workers=>0||" } );
      Assert.Contains( empty, l => l.StartsWith( "error|Could not read the setting search-workers of redis (read: docker exec gvb-redis redis-cli CONFIG GET search-workers): redis-cli CONFIG GET search-workers did not answer with the name and its value" ) );
      string[] failed = Read( REDIS, "gvb-redis", Inspect( "gvb-redis", CONTAINER_MOUNT ), new[] { "6379:6379" }, new[] { "CONFIG GET search-workers=>1||Could not connect to Redis at 127.0.0.1:6379: Connection refused" } );
      Assert.Contains( "error|Could not read the setting search-workers of redis (read: docker exec gvb-redis redis-cli CONFIG GET search-workers): docker exec gvb-redis exited with code 1: Could not connect to Redis at 127.0.0.1:6379: Connection refused", failed );
   }

   /// <summary>
   /// Elasticsearch and OpenSearch: the two heap figures from GET _nodes/jvm at the container's own address and port
   /// (OpenSearch's published port 9201 is 9200 inside the container, and the read goes to 9200), and a two-node answer is refused.
   /// </summary>
   [Fact]
   public void Heap_IsReadFromNodesJvmAtTheContainersOwnAddress()
   {
      string[] elastic = Read( "elasticsearch", "gvb-elasticsearch", Inspect( "gvb-elasticsearch" ), new[] { "9200:9200" }, Array.Empty<string>(), new[] { "/_nodes/jvm=>" + HEAP_JSON } );
      Assert.Contains( "setting|heap_init_in_bytes|2147483648|read: GET /_nodes/jvm (nodes.<id>.jvm.mem.heap_init_in_bytes and heap_max_in_bytes)", elastic );
      Assert.Contains( "setting|heap_max_in_bytes|2147483648|read: GET /_nodes/jvm (nodes.<id>.jvm.mem.heap_init_in_bytes and heap_max_in_bytes)", elastic );
      Assert.Contains( "call|get http://172.24.0.2:9200/_nodes/jvm", elastic );
      string[] open = Read( "opensearch", "gvb-opensearch", Inspect( "gvb-opensearch" ), new[] { "9201:9200" }, Array.Empty<string>(), new[] { "/_nodes/jvm=>" + HEAP_JSON } );
      Assert.Contains( "call|get http://172.24.0.2:9200/_nodes/jvm", open );
      string two = HEAP_JSON.Replace( "\"nodes\":{", "\"nodes\":{\"second\":{\"jvm\":{\"mem\":{}}}," );
      string[] refused = Read( "elasticsearch", "gvb-elasticsearch", Inspect( "gvb-elasticsearch" ), new[] { "9200:9200" }, Array.Empty<string>(), new[] { "/_nodes/jvm=>" + two } );
      Assert.Contains( refused, l => l.StartsWith( "error|Could not read the setting heap_init_in_bytes and heap_max_in_bytes of elasticsearch" ) && l.Contains( "lists 2 nodes" ) );
   }

   /// <summary>
   /// MariaDB: three variables from SHOW GLOBAL VARIABLES with the client's tab-separated output, the password passed to the
   /// client as MYSQL_PWD from the container's own MARIADB_ROOT_PASSWORD, only for the benchmark's own container: the daily
   /// gvb-mariadb is refused before any command runs.
   /// </summary>
   [Fact]
   public void MariaDb_ReadsThreeVariablesAndNeverAsksTheDailyContainer()
   {
      string answer = "0|innodb_buffer_pool_size\\t2147483648\\ninnodb_flush_log_at_trx_commit\\t2\\nmhnsw_max_cache_size\\t4294967296\\n|";
      string[] lines = Read( "mariadb", "gvbbench-mariadb", Inspect( "gvbbench-mariadb" ), new[] { "13306:3306" }, new[] { "SHOW GLOBAL VARIABLES=>" + answer } );
      Assert.Contains( "setting|innodb_buffer_pool_size|2147483648|read: docker exec gvbbench-mariadb mariadb SHOW GLOBAL VARIABLES", lines );
      Assert.Contains( "setting|innodb_flush_log_at_trx_commit|2|read: docker exec gvbbench-mariadb mariadb SHOW GLOBAL VARIABLES", lines );
      Assert.Contains( "setting|mhnsw_max_cache_size|4294967296|read: docker exec gvbbench-mariadb mariadb SHOW GLOBAL VARIABLES", lines );
      string call = lines.Single( l => l.StartsWith( "call|docker exec gvbbench-mariadb sh -c " ) );
      Assert.Contains( "MYSQL_PWD=\"$MARIADB_ROOT_PASSWORD\" mariadb -uroot -N -B -e", call );
      Assert.Equal( "leaks|0", lines[^1] );

      string[] daily = Read( "mariadb", "gvb-mariadb", Inspect( "gvb-mariadb" ), new[] { "3306:3306" }, new[] { "SHOW GLOBAL VARIABLES=>" + answer } );
      Assert.Contains( "error|The mariadb target's settings are read only from gvbbench-mariadb, not from 'gvb-mariadb' (the daily gvb-mariadb is never asked).", daily );
      Assert.DoesNotContain( daily, l => l.StartsWith( "call|docker exec" ) );
   }

   /// <summary>
   /// PostgreSQL: shared_buffers is a live read; enable_seqscan and jit come back from the same transaction the sink's own SET LOCAL
   /// statements ran in, so they are labelled "set:" with the sink's file, and the statements the reader runs are the sink's. Command tags
   /// (BEGIN, SET) in the client's output are ignored, and the client runs with the container's POSTGRES_PASSWORD.
   /// </summary>
   [Fact]
   public void PgVector_ReadsSharedBuffersLiveAndLabelsTheSinksOwnPlannerSettingsAsSet()
   {
      string[] lines = Read( "pgvector", "gvb-pgvector", Inspect( "gvb-pgvector" ), new[] { "5432:5432" }, new[] { "psql=>0|BEGIN\\nSET\\nSET\\nenable_seqscan=off\\njit=off\\nshared_buffers=2GB\\nCOMMIT\\n|" } );
      string set = "set: src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs#SET LOCAL enable_seqscan = off; SET LOCAL jit = off;";
      Assert.Contains( "setting|shared_buffers|2GB|read: docker exec gvb-pgvector psql current_setting", lines );
      Assert.Contains( $"setting|enable_seqscan|off|{set}", lines );
      Assert.Contains( $"setting|jit|off|{set}", lines );
      string call = lines.Single( l => l.StartsWith( "call|docker exec gvb-pgvector sh -c " ) );
      Assert.Contains( "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -d gvb -X -q -At -c \"BEGIN; SET LOCAL enable_seqscan = off; SET LOCAL jit = off;", call );
      Assert.Equal( "leaks|0", lines[^1] );
   }

   /// <summary>
   /// Chroma: the server's version from GET /api/v2/version at the container's own address (the sink states its version as a fixed text and
   /// the image tag floats), a JSON string or bare text; anything that is not a version number is an error that quotes the start of the answer.
   /// </summary>
   [Fact]
   public void Chroma_ReadsItsVersionAndRefusesAnAnswerThatIsNotOne()
   {
      string[] lines = Read( "chroma", "gvb-chroma", Inspect( "gvb-chroma" ), new[] { "8000:8000" }, Array.Empty<string>(), new[] { "/api/v2/version=>\"1.4.4\"" } );
      Assert.Contains( "setting|chroma_version|1.4.4|read: GET /api/v2/version", lines );
      Assert.Contains( "call|get http://172.24.0.2:8000/api/v2/version", lines );
      Assert.Equal( 4, lines.Count( l => l.StartsWith( "setting|" ) ) );
      string[] bare = Read( "chroma", "gvb-chroma", Inspect( "gvb-chroma" ), new[] { "8000:8000" }, Array.Empty<string>(), new[] { "/api/v2/version=>1.4.4" } );
      Assert.Contains( "setting|chroma_version|1.4.4|read: GET /api/v2/version", bare );
      string[] odd = Read( "chroma", "gvb-chroma", Inspect( "gvb-chroma" ), new[] { "8000:8000" }, Array.Empty<string>(), new[] { "/api/v2/version=><html>moved</html>" } );
      Assert.Contains( odd, l => l.StartsWith( "error|Could not read the setting chroma_version of chroma (read: GET /api/v2/version): GET /api/v2/version did not answer with a version number: <html>moved</html>" ) );
      string[] down = Read( "chroma", "gvb-chroma", Inspect( "gvb-chroma" ), new[] { "8000:8000" } );
      Assert.Contains( down, l => l.StartsWith( "error|Could not read the setting chroma_version of chroma (read: GET /api/v2/version): no scripted answer" ) );
   }

   /// <summary>
   /// Oracle: cpu_count and sga_target from V$PARAMETER and the full version from V$INSTANCE as SYSDBA inside the container (no password
   /// involved), the SQL passed as a quoted here-document so the shell inside the container expands nothing in it, blank lines around the
   /// answer ignored. A missing figure is an error that names it.
   /// </summary>
   [Fact]
   public void Oracle_ReadsTwoParametersAndTheVersionAsSysdbaInsideTheContainer()
   {
      string[] lines = Read( "oracle", "gvb-oracle", Inspect( "gvb-oracle" ), new[] { "1521:1521" }, new[] { "sqlplus=>0|\\ncpu_count=2\\nsga_target=1610612736\\noracle_version=23.26.0.0.0\\n\\n|" } );
      Assert.Contains( "setting|cpu_count|2|read: docker exec gvb-oracle sqlplus / as sysdba, V$PARAMETER and V$INSTANCE at the instance", lines );
      Assert.Contains( "setting|sga_target|1610612736|read: docker exec gvb-oracle sqlplus / as sysdba, V$PARAMETER and V$INSTANCE at the instance", lines );
      Assert.Contains( "setting|oracle_version|23.26.0.0.0|read: docker exec gvb-oracle sqlplus / as sysdba, V$PARAMETER and V$INSTANCE at the instance", lines );
      string[] noVersion = Read( "oracle", "gvb-oracle", Inspect( "gvb-oracle" ), new[] { "1521:1521" }, new[] { "sqlplus=>0|cpu_count=2\\nsga_target=1610612736\\n|" } );
      Assert.Contains( "error|Could not read the setting oracle_version of oracle: the engine's answer had no oracle_version.", noVersion );
      string call = lines.Single( l => l.StartsWith( "call|docker exec gvb-oracle sh -c " ) );
      Assert.Contains( "sqlplus -S / as sysdba <<'GVB_EOF'", call );
      Assert.Contains( "FROM v$parameter WHERE name IN ('cpu_count', 'sga_target')", call );
      Assert.Equal( "leaks|0", lines[^1] );
   }

   /// <summary>
   /// ClickHouse: max_threads from system.settings over HTTP at the container's own address; the user and password travel in headers
   /// (never in the address), the connection is asked to close, and the value is recorded as ClickHouse prints it.
   /// </summary>
   [Fact]
   public void ClickHouse_ReadsMaxThreadsOverHttpWithCredentialsInHeaders()
   {
      string[] lines = Read( "clickhouse", "gvb-clickhouse", Inspect( "gvb-clickhouse" ), new[] { "8123:8123" }, Array.Empty<string>(), Array.Empty<string>(), new[] { "max_threads=>200|'auto(4)'\\n" } );
      Assert.Contains( "setting|max_threads|'auto(4)'|read: ClickHouse system.settings, name max_threads, over HTTP", lines );
      string request = lines.Single( l => l.StartsWith( "call|http POST " ) );
      Assert.StartsWith( "call|http POST http://172.24.0.2:8123/ [", request );
      Assert.Contains( "X-ClickHouse-User=gvb", request );
      Assert.Contains( "X-ClickHouse-Key=", request );
      Assert.Contains( "closeConnection=True", request );
      Assert.Equal( "http://172.24.0.2:8123/", request.Split( ' ' )[2] );
      Assert.Equal( "leaks|0", lines[^1] );
   }

   /// <summary>
   /// ClickHouse errors keep the status and the start of its text but never the password, and an answer that is not one row is refused.
   /// </summary>
   [Fact]
   public void ClickHouse_AFailedOrOddAnswerIsAnErrorThatNamesTheKey()
   {
      string[] denied = Read( "clickhouse", "gvb-clickhouse", Inspect( "gvb-clickhouse" ), new[] { "8123:8123" }, Array.Empty<string>(), Array.Empty<string>(), new[] { "max_threads=>516|Code: 516. DB::Exception: gvb: Authentication failed" } );
      Assert.Contains( denied, l => l.StartsWith( "error|Could not read the setting max_threads of clickhouse (read: ClickHouse system.settings, name max_threads, over HTTP): ClickHouse at 172.24.0.2:8123 answered HTTP 516" ) );
      Assert.Contains( "leaks|0", denied );
      string[] two = Read( "clickhouse", "gvb-clickhouse", Inspect( "gvb-clickhouse" ), new[] { "8123:8123" }, Array.Empty<string>(), Array.Empty<string>(), new[] { "max_threads=>200|1\\n2\\n" } );
      Assert.Contains( two, l => l.StartsWith( "error|" ) && l.Contains( "max_threads is not one row" ) );
   }

   /// <summary>
   /// Qdrant: the number of threads named search-* of its process, counted from /proc (a number the engine text already carried, now an
   /// entry); no process or no thread names is an error, never a zero.
   /// </summary>
   [Fact]
   public void Qdrant_RecordsItsSearchThreadsAndRefusesToGuess()
   {
      string[] lines = Read( "qdrant", "gvb-qdrant", Inspect( "gvb-qdrant" ), new[] { "16333:6333" }, searchThreads: 3 );
      Assert.Contains( "setting|search_threads|3|read: threads named search-* of the qdrant process in container gvb-qdrant (/proc)", lines );
      string[] none = Read( "qdrant-hnsw", "gvb-qdrant", Inspect( "gvb-qdrant" ), new[] { "16333:6333" }, searchThreads: -1 );
      Assert.Contains( none, l => l.StartsWith( "error|Could not read the setting search_threads of qdrant-hnsw" ) );
   }

   /// <summary>
   /// Weaviate: GOMEMLIMIT from the container's environment, that one name only, so the ten secrets next to it in the same
   /// environment stay out of every line; a container without it is an error.
   /// </summary>
   [Fact]
   public void Weaviate_ReadsGoMemLimitAndNothingElseFromTheEnvironment()
   {
      string[] lines = Read( "weaviate", "gvb-weaviate", Inspect( "gvb-weaviate", "GOMEMLIMIT=6GiB" ), new[] { "8085:8080" } );
      Assert.Contains( "setting|GOMEMLIMIT|6GiB|read: docker inspect Config.Env, the name GOMEMLIMIT only", lines );
      Assert.Equal( "leaks|0", lines[^1] );
      string[] missing = Read( "weaviate", "gvb-weaviate", Inspect( "gvb-weaviate" ), new[] { "8085:8080" } );
      Assert.Contains( missing, l => l.StartsWith( "error|Could not read the setting GOMEMLIMIT of weaviate" ) );
   }

   /// <summary>
   /// The environment parser keeps only the names it is told to keep. A container with all ten secrets.env values in its environment
   /// (eight of the compose files load the whole file) yields none of them: not in the description, not in any setting, not in any
   /// command line, not in any error. Its error for a text that is not an inspect result never repeats the text.
   /// </summary>
   [Fact]
   public void Secrets_NeverLeaveTheEnvironmentOfTheContainer()
   {
      string json = Inspect( "gvb-redis", CONTAINER_MOUNT, "GOMEMLIMIT=6GiB" );
      string[] described = Describe( json );
      Assert.Equal( new[] { "name|gvb-redis", "image|sha256:6f81e8915c60b065a524e6967e0ad1c639ba6efa84d669f823683ea04d9150ee|redis:8.10.2", "limits|8589934592|2-3,6-7|0", "env|GOMEMLIMIT|6GiB", "mount|bind|/home/dan/gvb-data/engines/redis|/data|True" }, described );
      string everything = string.Join( "\n", described );
      foreach( string secret in new[] { "pg-secret-7f3a", "maria-secret-91bc", "ora-secret-22de", "gvbapp", "app-secret-5c10", "redis-secret-ab42", "mongo-secret-0e77", "ch-secret-d8e1", "ts-secret-33aa", "mssql-secret-c4f9" } )
      {
         Assert.DoesNotContain( secret, everything );
      }

      string[] broken = Describe( "[{\"Id\":\"x\",\"Config\":{\"Env\":[\"REDIS_PASSWORD=redis-secret-ab42\"" );
      Assert.Single( broken );
      Assert.StartsWith( "error|docker inspect did not return a container description (", broken[0] );
      Assert.DoesNotContain( "redis-secret-ab42", broken[0] );
   }

   /// <summary>
   /// A read that does not answer within its time limit is an error that says which key, which target, how, and how long, not "The
   /// operation was canceled."; the limit here is 250 ms, so the test takes a quarter of a second instead of the real 15 s.
   /// </summary>
   [Fact]
   public void ATimedOutReadNamesTheKeyAndTheLimit()
   {
      string[] lines = Read( REDIS, "gvb-redis", Inspect( "gvb-redis", CONTAINER_MOUNT ), new[] { "6379:6379" }, new[] { "CONFIG GET search-workers=>hang" }, timeoutMs: 250 );
      Assert.Contains( "error|Could not read the setting search-workers of redis (read: docker exec gvb-redis redis-cli CONFIG GET search-workers): no answer within 0.25 s.", lines );
   }

   /// <summary>
   /// A target with no container (a fake in a test, the native servers) gets one entry that says nothing was read, never an empty
   /// list; an embedded engine without a reader says so in its own words.
   /// </summary>
   [Fact]
   public void ATargetWithNothingToReadStillGetsOneEntry()
   {
      Assert.Equal( new[] { "setting|none|not read|embedded engine without a settings reader" }, ReadEmbedded( "fake", false ) );
   }

   /// <summary>
   /// The allowed keys: every key the readers record is on the list, "none" is, and a key made up by a bug is not.
   /// </summary>
   [Fact]
   public void OnlyListedKeysAreAllowed()
   {
      Assert.True( (bool)TargetsHarness.CallSettings( "KeyAllowed", "search-workers" ) );
      Assert.True( (bool)TargetsHarness.CallSettings( "KeyAllowed", "none" ) );
      Assert.False( (bool)TargetsHarness.CallSettings( "KeyAllowed", "REDIS_PASSWORD" ) );
      Assert.False( (bool)TargetsHarness.CallSettings( "KeyAllowed", "Config.Env" ) );
   }

   /// <summary>
   /// DuckDB, live and in process: the version and thread count come from a real throwaway in-memory database, the settings the
   /// sink sets are cited to its code, and after a real bench file exists (made by the real sink in a temporary folder and still
   /// open, as during a run) the memory limit and checkpoint threshold are read back from a second connection to the file.
   /// Without a file (a settings-only run) the same two entries are the sink's own defaults, labelled "set".
   /// </summary>
   [Fact]
   public void DuckDb_ReadsItsVersionAndThreadsAndThenItsFileSettingsFromARealBenchFile()
   {
      string[] noFile = ReadEmbedded( "duckdb", false );
      Assert.Contains( noFile, l => l.StartsWith( "setting|duckdb_version|v" ) );
      Assert.Contains( noFile, l => System.Text.RegularExpressions.Regex.IsMatch( l, @"^setting\|threads\|\d+\|read: SELECT current_setting\('threads'\) on an in-memory DuckDB connection; DuckDB's own thread count, not a CPU cap" ) );
      Assert.Contains( "setting|hnsw_ef_search|100|set: src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET hnsw_ef_search", noFile );
      Assert.Contains( "setting|hnsw_enable_experimental_persistence|true|set: src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET hnsw_enable_experimental_persistence = true", noFile );
      Assert.Contains( "setting|memory_limit|8GB|set: src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET memory_limit", noFile );
      Assert.Contains( "setting|checkpoint_threshold|256MB|set: src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET checkpoint_threshold", noFile );

      string[] withFile = ReadEmbedded( "duckdb", true );
      string limit = withFile.Single( l => l.StartsWith( "setting|memory_limit|" ) );
      Assert.EndsWith( "|read: SELECT current_setting('memory_limit') on a second connection to the bench file opened with the sink's own connection string", limit );
      Assert.Matches( @"^setting\|memory_limit\|7\.\d+ GiB\|read:", limit );
      Assert.Matches( @"^setting\|checkpoint_threshold\|(256\.0 MiB|256MB|244\.\d+ MiB)\|read: SELECT current_setting\('checkpoint_threshold'\)", withFile.Single( l => l.StartsWith( "setting|checkpoint_threshold|" ) ) );
   }

   /// <summary>
   /// sqlite-vec, live and in process: the SQLite version from a throwaway in-memory database, the sink's PRAGMA synchronous cited to its
   /// code, and the journal mode read through a second, read-only connection to a real bench file made by the real sink ("wal"), or the
   /// sink's own statement when there is no file.
   /// </summary>
   [Fact]
   public void SqliteVec_ReadsItsVersionAndTheJournalModeOfARealBenchFile()
   {
      string[] noFile = ReadEmbedded( "sqlitevec", false );
      Assert.Contains( noFile, l => System.Text.RegularExpressions.Regex.IsMatch( l, @"^setting\|sqlite_version\|3\.\d+\.\d+\|read: SELECT sqlite_version\(\) on an in-memory SQLite connection$" ) );
      Assert.Contains( "setting|synchronous|NORMAL|set: src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs#PRAGMA synchronous=NORMAL", noFile );
      Assert.Contains( "setting|journal_mode|WAL|set: src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs#PRAGMA journal_mode=WAL", noFile );

      string[] withFile = ReadEmbedded( "sqlitevec", true );
      Assert.Contains( "setting|journal_mode|wal|read: PRAGMA journal_mode on a second, read-only connection to the bench file", withFile );
   }

   /// <summary>
   /// Every "set:" entry cites a file and a token that are really there: the token must be found in that file of the repository, so a
   /// renamed statement in a sink breaks this test instead of leaving a citation to nothing.
   /// </summary>
   [Fact]
   public void EverySetCitationIsFoundInItsFile()
   {
      string root = TargetsHarness.RepoRoot();
      string pg = "set: src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs#SET LOCAL enable_seqscan = off; SET LOCAL jit = off;";
      Assert.True( (bool)TargetsHarness.CallSettings( "SetTokenFound", root, pg ) );
      foreach( string token in new[] { "SET memory_limit", "SET checkpoint_threshold", "SET hnsw_ef_search", "SET hnsw_enable_experimental_persistence = true" } )
      {
         Assert.True( (bool)TargetsHarness.CallSettings( "SetTokenFound", root, $"set: src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#{token}" ), token );
      }

      foreach( string token in new[] { "PRAGMA journal_mode=WAL", "PRAGMA synchronous=NORMAL" } )
      {
         Assert.True( (bool)TargetsHarness.CallSettings( "SetTokenFound", root, $"set: src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs#{token}" ), token );
      }

      Assert.False( (bool)TargetsHarness.CallSettings( "SetTokenFound", root, "set: src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs#SET LOCAL enable_hashjoin = off" ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// "docker inspect" text for a fake container carrying all ten secrets.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="extra">Extra "source:destination:rw" mount (the first extra that holds a colon) or "NAME=value" environment entries.</param>
   /// <returns>The JSON text.</returns>
   private static string Inspect( string container, params string[] extra )
   {
      string[] mounts = extra.Where( e => e.Contains( ':' ) && e.Contains( '/' ) ).ToArray();
      string[] env = extra.Except( mounts ).ToArray();
      return (string)TargetsHarness.CallSettings( "Inspect", container, env, mounts, new[] { "1:1" }, "sha256:6f81e8915c60b065a524e6967e0ad1c639ba6efa84d669f823683ea04d9150ee" );
   }

   /// <summary>
   /// Reads a container target's settings through the scripted probes.
   /// </summary>
   /// <param name="target">Target name.</param>
   /// <param name="container">Container name.</param>
   /// <param name="inspect">Inspect text.</param>
   /// <param name="ports">Ports "host:container" the route asks for.</param>
   /// <param name="docker">Docker script.</param>
   /// <param name="http">Web script.</param>
   /// <param name="clickHouse">ClickHouse script.</param>
   /// <param name="searchThreads">Qdrant thread count, -1 for none.</param>
   /// <param name="timeoutMs">Short read limit, 0 for the real one.</param>
   /// <returns>The scenario's lines.</returns>
   private static string[] Read( string target, string container, string inspect, string[] ports, string[]? docker = null, string[]? http = null, string[]? clickHouse = null, int searchThreads = -1, int timeoutMs = 0 )
   {
      return (string[])TargetsHarness.CallSettings( "Read", target, container, inspect, ports, docker ?? Array.Empty<string>(), http ?? Array.Empty<string>(), clickHouse ?? Array.Empty<string>(), searchThreads, timeoutMs );
   }

   /// <summary>
   /// Runs the description parser scenario.
   /// </summary>
   /// <param name="json">The inspect text.</param>
   /// <returns>The scenario's lines.</returns>
   private static string[] Describe( string json )
   {
      return (string[])TargetsHarness.CallSettings( "Describe", json );
   }

   /// <summary>
   /// Reads an embedded engine's settings.
   /// </summary>
   /// <param name="target">Target name.</param>
   /// <param name="withFile">True to use a real bench file.</param>
   /// <returns>The scenario's lines.</returns>
   private static string[] ReadEmbedded( string target, bool withFile )
   {
      return (string[])TargetsHarness.CallSettings( "ReadEmbedded", target, withFile );
   }

   #endregion Private Methods
}
