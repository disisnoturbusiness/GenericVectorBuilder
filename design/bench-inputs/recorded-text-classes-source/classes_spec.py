# Classification spec for deploy/bench/recorded-text-classes.json.
# Each (target, field) lists spans in text order: (start marker, class, [basis sources], options).
# A span runs from its marker to the next marker (or the end). class: RB read back, ME measured, DO documented, UN unverified.
# basis sources use the engine-facts grammar: results:targets[T].path#token (every claim run), "doc:path#quote", or a repository path#token.
# options: {"drop": "..."} for spans the report does not print; {"contradictedBy": {...}} for the weaviate clause; {"dropWhenAbsent": [...]}.

S = 'src/GenericVectorBuilder.Engines/Sinks/'
B = 'src/GenericVectorBuilder.Bench/'
D = 'deploy/engines/'
LOGS = 'design/engine-docs/durability-logs-2026-10-04/'
MARIA = 'design/bench-inputs/mariadb-effort-2026-10-07/run.log'
T = 'tests/GenericVectorBuilder.Engines.Tests/'

def note(t, token):
    return f'results:targets[{t}].load.indexNote#{token}'

SPEC = {}

# ------------------------------------------------------------------ durability
SPEC[('sqlitevec', 'durability')] = [
    ('PRAGMA journal_mode=WAL and PRAGMA synchronous=NORMAL (set by this sink when it opens the file)', 'DO',
     [S + 'SqliteVecSink.cs#Run( connection, "PRAGMA journal_mode=WAL;" )', S + 'SqliteVecSink.cs#Run( connection, "PRAGMA synchronous=NORMAL;" )']),
    (': each commit is appended to the -wal file', 'UN', [], {'cites': 'measurement'}),
]

SPEC[('mariadb', 'durability')] = [
    ('innodb_flush_log_at_trx_commit=2 (set in mariadb-bench.compose.yaml', 'DO',
     [D + 'mariadb-bench.compose.yaml#--innodb-flush-log-at-trx-commit=2', D + 'mariadb.compose.yaml#--innodb-flush-log-at-trx-commit=2', MARIA + '#SETTING innodb_flush_log_at_trx_commit = 2']),
    (': the InnoDB redo log is written', 'UN', []),
    ('; innodb_doublewrite is on', 'DO', [MARIA + '#SETTING innodb_doublewrite = ON']),
    (' (default),', 'UN', []),
    (' the binary log is off', 'DO', [MARIA + '#SETTING log_bin = OFF']),
    (', and the vector graph is an InnoDB table under the same log', 'UN', []),
    (' (settings read from the running server with SHOW VARIABLES;', 'DO', [MARIA + '#SETTING innodb_flush_log_at_trx_commit = 2']),
    (' the crash behaviour is InnoDB', 'UN', []),
]

SPEC[('duckdb', 'durability')] = [
    ("DuckDB's write-ahead log is fsynced at every commit", 'UN', []),
    (' (checkpoint_threshold=256MB', 'DO', [S + 'DuckDbSinkOptions.cs#CheckpointThreshold { get; set; } = "256MB"', S + 'DuckDbSink.cs#SET checkpoint_threshold']),
    (' only spaces out the checkpoints', 'UN', [], {'cites': 'measurement'}),
    (' which this sink turns on,', 'DO', [S + 'DuckDbSink.cs#SET hnsw_enable_experimental_persistence = true;']),
    (' and WAL recovery for such custom indexes', 'UN', []),
]

_QDRANT = [
    ('What was measured (strace -f on the Qdrant server process', 'DO',
     [LOGS + 'qdrant-wait-true.strace#MS_SYNC', LOGS + 'qdrant-wait-true.marks.json#"mode": "true"']),
    (' with wait=false the 30 upserts were acknowledged within 0.26 s', 'DO',
     [LOGS + 'qdrant-wait-false.strace#msync', LOGS + 'qdrant-wait-false.marks.json#"mode": "false"']),
    (' So the log is flushed to disk under both settings', 'UN', []),
    (' Every upsert here is sent with wait=true', 'DO', ['src/GenericVectorBuilder.Core/Sinks/QdrantSink.cs#_client.UpsertAsync( name, points, wait: true', 'src/GenericVectorBuilder.Core/Sinks/QdrantSink.cs#private const int UPSERT_BATCH = 256;']),
    (' the strace used one point per request', 'UN', []),
    (' Segment files are flushed every 5 s;', 'RB', [B + 'Targets/QdrantServer.cs#ConfigFiles.ReadContainerFileAsync( container, configPath, ct )']),
    (' Not tested by cutting power;', 'UN', []),
]
SPEC[('qdrant', 'durability')] = _QDRANT
SPEC[('qdrant-hnsw', 'durability')] = _QDRANT

_ES = [
    ('Every acknowledged bulk request is fsynced to the translog', 'UN', []),
    (' (ElasticsearchReadinessTests reads it back from the live index).', 'DO', [T + 'ElasticsearchReadinessTests.cs#index.translog.durability']),
    (' By that setting a process crash or power loss', 'UN', []),
    (' One node and no replicas,', 'DO', [S + 'ElasticsearchSink.cs#settings = new { number_of_shards = 1, number_of_replicas = 0 }']),
    (' so a lost disk loses the data.', 'UN', []),
]
SPEC[('elasticsearch', 'durability')] = _ES
SPEC[('opensearch', 'durability')] = [
    ('Every acknowledged bulk request is fsynced to the translog', 'UN', []),
    (' (OpenSearchReadinessTests reads it back from the live index).', 'DO', [T + 'OpenSearchReadinessTests.cs#index.translog.durability']),
    (' By that setting a process crash or power loss', 'UN', []),
    (' One node and no replicas,', 'DO', [S + 'OpenSearchSink.cs#["number_of_shards"] = 1, ["number_of_replicas"] = 0']),
    (' so a lost disk loses the data.', 'UN', []),
]

SPEC[('vespa', 'durability')] = [
    ("Vespa's transaction log server fsyncs after each commit", 'UN', []),
    (' VespaReadinessTests reads both from the live config server.', 'DO', [T + 'VespaReadinessTests.cs#usefsync']),
    (' After a crash the node replays the transaction log', 'UN', []),
    (' One node and min-redundancy 1,', 'DO', [S + 'VespaApplicationPackage.cs#<min-redundancy>1</min-redundancy>']),
    (' so a lost disk loses the data.', 'UN', []),
]

SPEC[('oracle', 'durability')] = [
    ('By these settings a committed row should survive', 'UN', []),
    (' Measured 2026-10-04: 50 separate client commits', 'UN', [], {'cites': 'measurement'}),
    (' The database runs NOARCHIVELOG', 'UN', []),
    (' The HNSW graph lives in the 768 MB vector memory pool (oracle-init/01-vector-memory.sh)', 'DO', [D + 'oracle-init/01-vector-memory.sh#POOL_SIZE=768M']),
    (' and is not the durable copy; the table is.', 'UN', []),
    (' Not tested by cutting power;', 'UN', []),
    (' CPU: Oracle Free caps itself at 2 CPUs (cpu_count 2 in V$PARAMETER, edition FREE in V$INSTANCE, 8 host CPUs in V$OSSTAT NUM_CPUS', 'RB',
     [S + 'OracleCpuCap.cs#cpu_count {CpuCount.ToString( CultureInfo.InvariantCulture )} in V$PARAMETER']),
    ("; the 2 CPU thread limit is Oracle's documented Free edition limit).", 'UN', []),
]

SPEC[('clickhouse', 'durability')] = [
    ('Acknowledged inserts are not fsynced.', 'UN', []),
    (' Measured 2026-10-04 with strace over 30 acknowledged single-row inserts (30 Ok rows in system.asynchronous_insert_log):', 'UN', [], {'cites': 'measurement'}),
    (' zero fsync, fdatasync or sync_file_range calls,', 'DO', [LOGS + 'clickhouse-acknowledged-inserts.strace#SIGUSR1']),
    (' while the control, a table created with fsync_after_insert=1, made 61 fdatasync calls', 'DO', [LOGS + 'clickhouse-control-fsync-after-insert.strace#fdatasync']),
    (' for 5 inserts.', 'UN', [], {'cites': 'measurement'}),
    (' A host power loss or kernel crash', 'UN', []),
]

SPEC[('milvus', 'durability')] = [
    ('Writes are not fsynced.', 'UN', []),
    (' (COMMON_STORAGETYPE=local in milvus.compose.yaml).', 'DO', [D + 'milvus.compose.yaml#COMMON_STORAGETYPE: local']),
    (' Measured 2026-10-04 with strace over 30 acknowledged single-row upserts and one flush:', 'DO', [LOGS + 'milvus-acknowledged-upserts.strace#member/wal']),
    (' A host power loss or kernel crash', 'UN', []),
]

SPEC[('mongodb', 'durability')] = [
    ('Acknowledged writes are journaled before the acknowledgement.', 'UN', []),
    (" Measured 2026-10-04: 30 acknowledged single-document inserts raised WiredTiger 'log sync operations' by 30 and", 'UN', [], {'cites': 'measurement'}),
    (' strace showed fdatasync on /data/db/journal/WiredTigerLog files.', 'DO', [LOGS + 'mongodb-acknowledged-inserts.strace#WiredTigerLog']),
    (' A crash loses no acknowledged write.', 'UN', []),
]

SPEC[('typesense', 'durability')] = [
    ('Every acknowledged write is appended to', 'UN', [], {'cites': 'measurement'}),
    (' (typesense.compose.yaml sets TYPESENSE_SNAPSHOT_INTERVAL_SECONDS=300)', 'DO', [D + 'typesense.compose.yaml#TYPESENSE_SNAPSHOT_INTERVAL_SECONDS: "300"']),
    (' and rebuilds the in-memory HNSW graph,', 'UN', []),
]

SPEC[('redis', 'durability')] = [
    ('save "300 1" and appendonly no (redis.compose.yaml)', 'DO', [D + 'redis.compose.yaml#--save "300 1"', D + 'redis.compose.yaml#--appendonly no']),
    (': an RDB snapshot is written every 5 minutes', 'UN', [], {'cites': 'measurement'}),
]

SPEC[('chroma', 'durability')] = [
    ('SQLite rollback journal with its default synchronous=FULL under the data directory', 'UN', []),
    (' (chroma.compose.yaml sets IS_PERSISTENT=1 and PERSIST_DIRECTORY=/data and no sync setting)', 'DO',
     [D + 'chroma.compose.yaml#IS_PERSISTENT: "1"', D + 'chroma.compose.yaml#PERSIST_DIRECTORY: /data']),
    (': every write is committed to chroma.sqlite3', 'UN', [], {'cites': 'measurement'}),
]

SPEC[('pgvector', 'durability')] = [
    ('fsync on, synchronous_commit on, full_page_writes on, wal_sync_method fdatasync (PostgreSQL defaults;', 'UN', []),
    (' pgvector.compose.yaml sets only shared_buffers, maintenance_work_mem and max_wal_size)', 'DO',
     [D + 'pgvector.compose.yaml#shared_buffers=2GB', D + 'pgvector.compose.yaml#maintenance_work_mem=1GB', D + 'pgvector.compose.yaml#max_wal_size=4GB']),
    (': every commit is flushed to the write-ahead log', 'UN', [], {'cites': 'measurement'}),
]

SPEC[('weaviate', 'durability')] = [
    ('Weaviate 1.39.8 defaults, weaviate.compose.yaml sets no persistence variable:', 'UN', [],
     {'contradictedBy': {'file': D + 'weaviate.compose.yaml', 'prefix': 'PERSISTENCE_', 'words': 'sets no persistence variable'}}),
    (' every object is appended to the LSM write-ahead log', 'UN', [], {'cites': 'measurement'}),
]

_SQL = [
    ('A commit returns after its transaction-log records are written to disk (SQL Server write-ahead logging;', 'UN', []),
    (' delayed durability is DISABLED);', 'RB', [B + 'Targets/SqlDurability.cs#SELECT name, recovery_model_desc, delayed_durability_desc, page_verify_option_desc FROM sys.databases']),
    (' a database created here copies the model database:', 'UN', []),
    (' recovery model FULL, page_verify CHECKSUM; no global trace flags are enabled;', 'RB',
     [B + 'Targets/SqlDurability.cs#SELECT name, recovery_model_desc, delayed_durability_desc, page_verify_option_desc FROM sys.databases', B + 'Targets/SqlDurability.cs#DBCC TRACESTATUS( -1 ) WITH NO_INFOMSGS;']),
    (' mssql.conf of container gvb-mssql', 'RB', [B + 'Targets/SqlDurability.cs#ConfigFiles.ExistsWithSudoAsync( confPath, ct )']),
    (" so SQL Server's own Linux defaults for flushing writes apply", 'UN', []),
    (' Not tested by cutting power;', 'UN', []),
    (' Container settings from its environment (names only):', 'RB', [B + 'Targets/SqlServerContainer.cs#n.StartsWith( "MSSQL_"']),
]
SPEC[('sql', 'durability')] = _SQL
SPEC[('sql-diskann', 'durability')] = _SQL

# ------------------------------------------------------------------ index
O = S  # option files sit beside the sinks
W = 'design/engine-docs/'

SPEC[('sqlitevec', 'index')] = [
    ('vec0 brute-force scan, no ANN index (exact)', 'RB', [note('sqlitevec', 'EXPLAIN QUERY PLAN: SCAN')]),
    (', float32, cosine distance', 'DO', [S + 'SqliteVecSink.cs#embedding float[{dimension}] distance_metric=cosine,']),
    (', default chunk_size=1024; score = 1 - cosine distance;', 'UN', []),
    (' searches run concurrently,', 'DO', [S + 'SqliteVecSinkOptions.cs#public int MaxSearchConnections { get; set; } = 32;', S + 'SqliteVecSink.cs#return Math.Max( MIN_SEARCH_CONNECTIONS, _options.MaxSearchConnections );']),
]

SPEC[('mariadb', 'index')] = [
    ('VECTOR INDEX', 'DO', [S + 'MariaDbSink.cs#VECTOR INDEX {INDEX_NAME} ( embedding ) M={_options.HnswM} DISTANCE=cosine']),
    (' (HNSW variant)', 'UN', []),
    (' DISTANCE=cosine, M=16', 'DO', [S + 'MariaDbSink.cs#VECTOR INDEX {INDEX_NAME} ( embedding ) M={_options.HnswM} DISTANCE=cosine', S + 'MariaDbSinkOptions.cs#public int HnswM { get; set; } = 16;']),
    (' (no ef_construction setting exists),', 'UN', []),
    (' mhnsw_ef_search=100 per statement', 'RB', [note('mariadb', 'the server applied mhnsw_ef_search 100')]),
    (' (the ef 100 most engines here use, so the search effort matches;', 'UN', []),
    (" MariaDB's own default is 20;", 'DO', [MARIA + '#SETTING mhnsw_ef_search = 20']),
    (' recall@10 at ef 100 falls as the set grows (random 1024-dimension vectors, measured 2026-10-04: 0.99 at 524, 0.89 to 0.92 at 2,000; an earlier run gave about 0.09 at 100,000)),', 'UN', [],
     {'drop': 'mariadb-recall', 'evidence': 'mariadbRecallLog'}),
    (' mhnsw_max_cache_size 4G;', 'DO', [D + 'mariadb-bench.compose.yaml#--mhnsw-max-cache-size=4G', MARIA + '#SETTING mhnsw_max_cache_size = 4294967296']),
    (' exact mode = IGNORE INDEX full scan', 'DO', [S + 'MariaDbSink.cs#SearchSql( collection, top, $"IGNORE INDEX ( {INDEX_NAME} )" )']),
]

SPEC[('duckdb', 'index')] = [
    ('HNSW (vss extension) FLOAT[n] metric=cosine m=16 ef_construction=128,', 'DO',
     [S + "DuckDbSink.cs#WITH ( metric = 'cosine', m =", S + 'DuckDbSink.cs#Run( connection, "LOAD vss;" )', S + 'DuckDbSink.cs#{VECTOR_COLUMN} FLOAT[{dimension.ToString( CultureInfo.InvariantCulture )}] NOT NULL );', S + 'DuckDbSinkOptions.cs#public int HnswM { get; set; } = 16;', S + 'DuckDbSinkOptions.cs#public int HnswEfConstruction { get; set; } = 128;']),
    (' ef_search=100 per connection,', 'DO', [S + 'DuckDbSink.cs#SET hnsw_ef_search', S + 'DuckDbSinkOptions.cs#public int HnswEfSearch { get; set; } = 100;']),
    (' persistent (hnsw_enable_experimental_persistence=true, checkpoint_threshold=256MB);', 'DO',
     [S + 'DuckDbSink.cs#SET hnsw_enable_experimental_persistence = true;', S + 'DuckDbSink.cs#SET checkpoint_threshold', S + 'DuckDbSinkOptions.cs#public string CheckpointThreshold { get; set; } = "256MB";']),
    (' exact mode = array_cosine_similarity sequential scan;', 'UN', []),
    (' score = 1 - cosine distance;', 'UN', []),
    (' searches run concurrently, one connection per searcher', 'UN', []),
]

SPEC[('qdrant', 'index')] = [
    ("exact scan: the builder's sink sends exact=true on every search", 'DO', [B + 'Targets/TargetFactory.cs#private const string QDRANT_EXACT_INDEX = "exact scan: the builder\'s sink sends exact=true on every search']),
    (', so no HNSW graph is used whether or not Qdrant has built one (see the index state)', 'RB', [note('qdrant', '0 of 524 vectors in HNSW segments')]),
]

SPEC[('elasticsearch', 'index')] = [
    ('HNSW float32, no quantization, m=16, ef_construction=128,', 'RB', [note('elasticsearch', 'index_options hnsw m=16 ef_construction=128')]),
    (' cosine;', 'DO', [S + 'ElasticsearchSink.cs#similarity = "cosine",']),
    (' search k=top, num_candidates=100;', 'DO', [S + 'ElasticsearchSink.cs#knn = new { field = "vector", query_vector = vector, k = top, num_candidates = candidates }', S + 'ElasticsearchSinkOptions.cs#int NumCandidates = 100']),
    (' 1 shard, 0 replicas;', 'DO', [S + 'ElasticsearchSink.cs#settings = new { number_of_shards = 1, number_of_replicas = 0 }']),
    (' force-merged to one segment after the load', 'RB', [note('elasticsearch', '1 segment(s), 524 vectors')]),
    (' (at 1,024 dimensions a segment under 1,043 vectors gets no graph)', 'UN', []),
]

SPEC[('vespa', 'index')] = [
    ('HNSW float32 tensor, prenormalized-angular (cosine), max-links-per-node=16, neighbors-to-explore-at-insert=128;', 'DO',
     [S + 'VespaApplicationPackage.cs#type tensor<float>(x[{{dimension}}])', S + 'VespaApplicationPackage.cs#distance-metric: prenormalized-angular', S + 'VespaApplicationPackage.cs#max-links-per-node: 16', S + 'VespaApplicationPackage.cs#neighbors-to-explore-at-insert: 128']),
    (' search targetHits=top, ef=100 via exploreAdditionalHits;', 'DO', [S + 'VespaSink.cs#targetHits:{top},hnsw.exploreAdditionalHits:{extra}', S + 'VespaSinkOptions.cs#int EfSearch = 100']),
    (' exact mode = approximate:false;', 'DO', [S + 'VespaSink.cs#targetHits:{top},approximate:false']),
    (' vectors held in memory', 'UN', []),
]

SPEC[('qdrant-hnsw', 'index')] = [
    ('HNSW m=16 ef_construct=100,', 'DO', [B + 'Targets/QdrantHnswSink.cs#var hnsw = new HnswConfigDiff { M = HNSW_M, EfConstruct = HNSW_EF_CONSTRUCT, FullScanThreshold = FULL_SCAN_THRESHOLD_KB };']),
    (' hnsw_ef=server default,', 'DO', [B + 'Targets/QdrantHnswSink.cs#search.HnswEf = _ef.Value;']),
    (' cosine;', 'DO', [B + 'Targets/QdrantHnswSink.cs#new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine }']),
    (' indexing_threshold_kb 1 and full_scan_threshold_kb 10', 'DO',
     [B + 'Targets/QdrantHnswSink.cs#var optimizers = new OptimizersConfigDiff { IndexingThreshold = INDEXING_THRESHOLD_KB };', B + 'Targets/QdrantHnswSink.cs#var hnsw = new HnswConfigDiff { M = HNSW_M, EfConstruct = HNSW_EF_CONSTRUCT, FullScanThreshold = FULL_SCAN_THRESHOLD_KB };']),
    (' (server defaults are 10,000 each)', 'RB',
     ['results:targets[qdrant-hnsw].durability#storage.optimizers.indexing_threshold_kb = 10000', 'results:targets[qdrant-hnsw].durability#storage.hnsw_index.full_scan_threshold_kb = 10000']),
    (' so a small collection builds and walks its graph', 'RB', [note('qdrant-hnsw', '524 of 524 vectors in HNSW segments')]),
]

SPEC[('sql-diskann', 'index')] = [
    ('DiskANN (preview) via VECTOR_SEARCH, cosine,', 'UN', []),
    (' build {"StartId":"306", "L":"48", "M":"8", "R":"48"};', 'RB', [note('sql-diskann', 'built DiskANN, parameters {"StartId":"306", "L":"48", "M":"8", "R":"48"}'), B + 'Targets/SqlDiskAnnSink.cs#_buildParameters = index?.Build ?? "unknown";']),
    (' the exact mode scans the same table', 'UN', []),
]

SPEC[('pgvector', 'index')] = [
    ('HNSW vector_cosine_ops m=16 ef_construction=128,', 'RB', [note('pgvector', "USING hnsw (embedding vector_cosine_ops) WITH (m='16', ef_construction='128')")]),
    (' hnsw.ef_search=100 per query,', 'DO', [S + 'PgVectorSink.cs#SET LOCAL hnsw.ef_search = ', S + 'PgVectorSinkOptions.cs#public int HnswEfSearch { get; set; } = 100;']),
    (' float32 vector(n), cosine;', 'UN', []),
    (' exact mode = same query with index scans off (sequential scan)', 'DO', [S + 'PgVectorSink.cs#EXACT_SETTINGS = "SET LOCAL enable_indexscan = off; SET LOCAL jit = off"']),
]

SPEC[('weaviate', 'index')] = [
    ('HNSW maxConnections(M)=16 efConstruction=128,', 'DO',
     [S + 'WeaviateSink.cs#["maxConnections"] = _options.MaxConnections,', S + 'WeaviateSink.cs#["efConstruction"] = _options.EfConstruction,', S + 'WeaviateSinkOptions.cs#int MaxConnections = 16,', S + 'WeaviateSinkOptions.cs#int EfConstruction = 128,']),
    (' ef=-1', 'DO', [S + 'WeaviateSink.cs#["ef"] = _options.Ef,', S + 'WeaviateSinkOptions.cs#int Ef = -1 );', W + 'weaviate-vector-index-reference-2026-10-07.mdx#`ef`'],
     {'docRows': [['ef', '-1']]}),
    (' (dynamic: limit x 8 clamped 100..500),', 'DO',
     [W + 'weaviate-vector-index-reference-2026-10-07.mdx#`dynamicEfFactor`', W + 'weaviate-vector-index-reference-2026-10-07.mdx#`dynamicEfMin`', W + 'weaviate-vector-index-reference-2026-10-07.mdx#`dynamicEfMax`',
      W + 'weaviate-vector-index-concepts-2026-10-07.md#The dynamic list size will be set as the query limit multiplied by `dynamicEfFactor`, modified by a minimum of `dynamicEfMin` and a maximum of `dynamicEfMax`.'],
     {'docRows': [['dynamicEfFactor', '8'], ['dynamicEfMin', '100'], ['dynamicEfMax', '500']]}),
    (' cosine, no quantization;', 'UN', []),
    (' approximate only (no exact mode)', 'UN', []),
]

SPEC[('clickhouse', 'index')] = [
    ('vector_similarity HNSW cosineDistance, quantization bf16, M=16 ef_construction=128,', 'DO',
     [S + "ClickHouseSink.cs#INDEX vec_idx embedding TYPE vector_similarity( 'hnsw', 'cosineDistance', {dimension}, '{_options.Quantization}', {_options.HnswM}, {_options.HnswEfConstruction} )",
      S + 'ClickHouseSinkOptions.cs#public string Quantization { get; set; } = "bf16";', S + 'ClickHouseSinkOptions.cs#public int HnswM { get; set; } = 16;', S + 'ClickHouseSinkOptions.cs#public int HnswEfConstruction { get; set; } = 128;']),
    (' hnsw_candidate_list_size_for_search=256, rescoring off;', 'DO',
     [S + 'ClickHouseSink.cs#hnsw_candidate_list_size_for_search = {_options.HnswEfSearch}, vector_search_with_rescoring = {_options.Rescoring}', S + 'ClickHouseSinkOptions.cs#public int HnswEfSearch { get; set; } = 256;', S + 'ClickHouseSinkOptions.cs#public int Rescoring { get; set; }']),
    (' exact mode = full scan with skip indexes off', 'UN', []),
]

SPEC[('oracle', 'index')] = [
    ('HNSW in-memory neighbor graph NEIGHBORS=16 EFCONSTRUCTION=128,', 'DO',
     [S + 'OracleSink.cs#ORGANIZATION INMEMORY NEIGHBOR GRAPH DISTANCE COSINE', S + 'OracleSink.cs#PARAMETERS ( TYPE HNSW, NEIGHBORS {_options.HnswNeighbors}, EFCONSTRUCTION {_options.HnswEfConstruction} )', S + 'OracleSinkOptions.cs#public int HnswNeighbors { get; set; } = 16;', S + 'OracleSinkOptions.cs#public int HnswEfConstruction { get; set; } = 128;']),
    (' EFSEARCH=100 per query, cosine;', 'DO',
     [S + 'OracleSink.cs#APPROX FIRST :top ROWS ONLY WITH TARGET ACCURACY PARAMETERS ( EFSEARCH {Math.Max( _options.HnswEfSearch, top )} )', S + 'OracleSink.cs#VECTOR_DISTANCE( embedding, :query, COSINE )', S + 'OracleSinkOptions.cs#public int HnswEfSearch { get; set; } = 100;']),
    (' exact mode = FETCH EXACT FIRST (full scan);', 'DO', [S + 'OracleSink.cs#"EXACT FIRST :top ROWS ONLY"']),
    (' Oracle Free caps itself at 2 CPUs (cpu_count 2 in V$PARAMETER, edition FREE in V$INSTANCE, 8 host CPUs in V$OSSTAT NUM_CPUS', 'RB',
     [S + 'OracleCpuCap.cs#cpu_count {CpuCount.ToString( CultureInfo.InvariantCulture )} in V$PARAMETER']),
    ("; the 2 CPU thread limit is Oracle's documented Free edition limit)", 'UN', []),
]

SPEC[('sql', 'index')] = [
    ('exact VECTOR_DISTANCE cosine, no vector index (full scan)', 'DO', [B + 'Targets/TargetFactory.cs#private const string SQL_EXACT_INDEX = "exact VECTOR_DISTANCE cosine, no vector index (full scan)"']),
]

SPEC[('opensearch', 'index')] = [
    ('faiss HNSW float32, no compression, m=16, ef_construction=128, cosinesimil;', 'DO',
     [S + 'OpenSearchSink.cs#parameters = new { m = HNSW_M, ef_construction = HNSW_EF_CONSTRUCTION }', S + 'OpenSearchSink.cs#private const int HNSW_M = 16;', S + 'OpenSearchSink.cs#private const int HNSW_EF_CONSTRUCTION = 128;']),
    (' search k=top, ef_search=100;', 'DO', [S + 'OpenSearchSink.cs#method_parameters = new { ef_search = Math.Max( _options.EfSearch, top ) }', S + 'OpenSearchSinkOptions.cs#int EfSearch = 100']),
    (' 1 shard, 0 replicas;', 'DO', [S + 'OpenSearchSink.cs#["number_of_shards"] = 1, ["number_of_replicas"] = 0']),
    (' graph built at any segment size (approximate_threshold=0);', 'RB', [note('opensearch', 'approximate_threshold 0')]),
    (' force-merged to one segment after the load', 'RB', [note('opensearch', 'force-merged to one segment')]),
]

SPEC[('redis', 'index')] = [
    ('HNSW TYPE FLOAT32 M=16 EF_CONSTRUCTION=128,', 'DO',
     [S + 'RedisSink.cs#["M"] = _options.HnswM,', S + 'RedisSink.cs#["EF_CONSTRUCTION"] = _options.HnswEfConstruction,', S + 'RedisSinkOptions.cs#public int HnswM { get; set; } = 16;', S + 'RedisSinkOptions.cs#public int HnswEfConstruction { get; set; } = 128;']),
    (' EF_RUNTIME=100 per query,', 'DO', [S + 'RedisSink.cs#$"EF_RUNTIME {Math.Max( _options.HnswEfRuntime, top )}"', S + 'RedisSinkOptions.cs#public int HnswEfRuntime { get; set; } = 100;']),
    (' cosine;', 'DO', [S + 'RedisSink.cs#["DISTANCE_METRIC"] = "COSINE",']),
    (' exact mode = FLAT index built on first exact query', 'UN', []),
]

SPEC[('chroma', 'index')] = [
    ('HNSW M=16 ef_construction=128, ef_search=100', 'DO',
     [S + 'ChromaSink.cs#["max_neighbors"] = _options.MaxNeighbors,', S + 'ChromaSink.cs#["ef_construction"] = _options.EfConstruction,', S + 'ChromaSink.cs#["ef_search"] = _options.EfSearch,',
      S + 'ChromaSinkOptions.cs#int MaxNeighbors = 16,', S + 'ChromaSinkOptions.cs#int EfConstruction = 128,', S + 'ChromaSinkOptions.cs#int EfSearch = 100 );']),
    (' (Chroma default),', 'UN', []),
    (' cosine;', 'DO', [S + 'ChromaSink.cs#["space"] = "cosine",']),
    (' approximate only (no exact mode)', 'UN', []),
]

SPEC[('typesense', 'index')] = [
    ('HNSW float32 (hnswlib), m=16, ef_construction=128, cosine;', 'DO',
     [S + 'TypesenseSink.cs#hnsw_params = new { M = HNSW_M, ef_construction = HNSW_EF_CONSTRUCTION }', S + 'TypesenseSink.cs#private const int HNSW_M = 16;', S + 'TypesenseSink.cs#private const int HNSW_EF_CONSTRUCTION = 128;']),
    (' search k=top, ef=100;', 'DO', [S + 'TypesenseSink.cs#string options = $"k:{top}, ef:{Math.Max( _options.Ef, top )}";', S + 'TypesenseSinkOptions.cs#int Ef = 100']),
    (' exact mode = filter ordinal:>=0 with flat_search_cutoff;', 'UN', []),
    (' index held in memory', 'UN', []),
]

SPEC[('mongodb', 'index')] = [
    ('vectorSearch index, HNSW maxEdges=16 numEdgeCandidates=128,', 'DO', [S + 'MongoDbSinkOptions.cs#public int HnswMaxEdges { get; set; } = 16;', S + 'MongoDbSinkOptions.cs#public int HnswNumEdgeCandidates { get; set; } = 128;']),
    (' float32 binData, cosine,', 'DO', [S + 'MongoDbSink.cs#VectorSimilarity.Cosine']),
    (' numCandidates=20x hits (min 100);', 'DO', [S + 'MongoDbSinkOptions.cs#public int NumCandidatesFactor { get; set; } = 20;', S + 'MongoDbSinkOptions.cs#public int MinNumCandidates { get; set; } = 100;']),
    (' exact mode = $vectorSearch exact:true', 'UN', []),
]

SPEC[('milvus', 'index')] = [
    ('HNSW M=16 efConstruction=128,', 'RB', [note('milvus', 'type HNSW (COSINE, {"M":16,"efConstruction":128})')]),
    (' ef=100, metric COSINE,', 'DO', [S + 'MilvusSink.cs#writer.WriteNumber( "ef", _options.Ef.Value );', S + 'MilvusSinkOptions.cs#int? Ef = 100,', S + 'MilvusSink.cs#["metricType"] = "COSINE",']),
    (' Strong consistency searches;', 'UN', []),
    (' approximate only (no exact mode)', 'UN', []),
]

# ------------------------------------------------------------------ run notes (the Method section)
# Each entry: match = regex the note must start with; spans = (pattern, class, basis, options), matched in order from the start of the note.
# Patterns are regular expressions (.NET syntax, singleline); a span that must end at the end of the note uses (?s:.*) .
BENCH_MATH = 'src/GenericVectorBuilder.Bench/Stats/BenchMath.cs'
RESULTS_CONN = 'results:conditions.connections#this target'
NATIVE = ['sql-native', 'qdrant-native']

REST = r'(?s:.*)'

NOTES = [
    {'match': r'^Load average at start: ', 'spans': [
        (r'Load average at start: \S+ \S+ \S+ on \d+ logical CPUs \(1/5/15 min\)\.', 'RB', [B + 'Report/MachineFacts.cs#File.ReadAllText( "/proc/loadavg" )'], {}),
        (r' It counts this benchmark\'s own earlier work and the engines it started, so it is recorded here and never judged; each timed pass is judged by the outside load machine control measures \(conditions\.passes\)\.', 'UN', [], {}),
    ]},
    {'match': r'^Order: ', 'spans': [
        (r'Order: targets ran one at a time in a random order from seed \d+ \(runSeed; --seed \d+ repeats it, targetOrder lists it\)\.', 'DO', [B + 'Running/RunOrder.cs#return Shuffle( names, Mix( (ulong)(uint)runSeed ^ TARGET_SALT ) );'], {}),
        (r' Inside each target the timed passes also ran in a random order from the same seed and the target\'s name \(passOrder\):', 'DO', [B + 'Running/RunOrder.cs#return Shuffle( passes, Mix( (ulong)(uint)runSeed ^ PASS_SALT ^ StableHash( targetName.ToLowerInvariant() ) ) );'], {}),
        (REST, 'UN', [], {}),
    ]},
    {'match': r'^Preparation, untimed', 'spans': [(REST, 'UN', [], {})]},
    {'match': r'^Warm-up and settle check', 'spans': [(REST, 'UN', [], {})]},
    {'match': r'^Load rows/s counts only time', 'spans': [(REST, 'UN', [], {})]},
    {'match': r'^Index proof: ', 'spans': [(REST, 'UN', [], {})]},
    {'match': r'^Search settings: ', 'spans': [(REST, 'UN', [], {})]},
    {'match': r'^Durability: each target', 'spans': [(REST, 'UN', [], {})]},
    {'match': r'^Engine record: ', 'spans': [(REST, 'UN', [], {})]},
    {'match': r'^Latency is client-side wall time', 'spans': [(REST, 'UN', [], {})]},
    {'match': r'^QPS: N searchers', 'spans': [(REST, 'UN', [], {})]},
    {'match': r'^Recall@\d+: ', 'spans': [
        (r'Recall@\d+: share of the exact top \d+ \(brute force in memory\) that the engine returned\.', 'UN', [], {}),
        (r' A hit whose exact similarity ties the \d+th best \(within 1e-5\) also counts', 'DO', [BENCH_MATH + '#public const double TIE_TOLERANCE = 1e-5;'], {'evidence': 'tieCheck'}),
        (r', because duplicate rows embed to identical vectors\.', 'UN', [], {}),
    ]},
    {'match': r'^Routes: ', 'spans': [
        (r'Routes: a container engine \(the benchmark\'s own SQL Server and Qdrant containers included\) is reached at its container\'s own address on its Docker network, never through the published 127\.0\.0\.1 port \(docker-proxy\);', 'RB', [RESULTS_CONN], {}),
        (r' the native comparison targets \(sql-native, qdrant-native\) are native services reached directly over loopback\.', 'UN', [], {'dropWhenAbsent': NATIVE}),
        (REST, 'UN', [], {}),
    ]},
    {'match': r'^RAM of compose engines', 'spans': [
        (r'RAM of compose engines, the benchmark\'s own SQL Server and Qdrant containers \(sql, sql-diskann, qdrant, qdrant-hnsw\) included, is docker stats of the engine\'s containers;', 'UN', [], {}),
        (r' for the native comparison targets \(sql-native, qdrant-native\) it is the whole native process, including every other database or collection it serves\.', 'UN', [], {'dropWhenAbsent': NATIVE}),
        (REST, 'UN', [], {}),
    ]},
    {'match': r'^Engines: ', 'spans': [(REST, 'UN', [], {})]},
    {'match': r'^Machine control on: ', 'spans': [
        (r'Machine control on: .*?(?= Busy box: )', 'RB', ['results:conditions.governor#performance', 'results:conditions.clientCpus#0-1,4-5', 'results:conditions.engineCpus#2-3,6-7'], {}),
        (REST, 'UN', [], {}),
    ]},
    {'match': r'^\S+ is embedded: it ran inside the client process on the client CPUs ', 'spans': [
        (r'\S+ is embedded: it ran inside the client process on the client CPUs \S+, sharing them with the client\.', 'RB', ['results:conditions.engines#embedded'], {}),
    ]},
]

POLICY = {'durability': 'refuse', 'index': 'refuse', 'notes': 'label'}

EVIDENCE = {
    'mariadbRecallLog': 'design/bench-inputs/mariadb-effort-2026-10-07/run.log',
    'tieCheck': 'design/bench-inputs/tie-check-eshoponweb-2026-10-07.json',
}


def extra(repo):
    return {'policy': POLICY, 'evidence': EVIDENCE, 'notes': [
        {'match': n['match'], 'spans': [dict(pattern=sp[0].lstrip(' '), **{'class': {'RB': 'read-back', 'ME': 'measured', 'DO': 'documented', 'UN': 'unverified'}[sp[1]], 'basis': sp[2]}, **sp[3]) for sp in n['spans']]}
        for n in NOTES]}
