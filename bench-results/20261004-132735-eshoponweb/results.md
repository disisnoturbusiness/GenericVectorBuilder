# Vector engine benchmark: eshoponweb

Run 2026-10-04T13:27:35Z (run-all). Command line:

```
/home/dan/ForClaude/GenericVectorBuilder/src/GenericVectorBuilder.Bench/bin/Release/net10.0/GenericVectorBuilder.Bench.dll run-all --pipeline eshoponweb --targets sql,sql-diskann,qdrant,qdrant-hnsw,pgvector,mariadb,oracle,redis,mongodb,clickhouse,milvus,weaviate,chroma,elasticsearch,opensearch,typesense,vespa,duckdb,sqlitevec --queries golden
```

- Machine: linus7795, Intel(R) Xeon(R) CPU E5-1620 v3 @ 3.50GHz (8 logical CPUs), 62.7 GiB RAM, GPU NVIDIA GeForce RTX 3060, 12288 MiB, Ubuntu 24.04.5 LTS, kernel 6.8.0-142-generic, .NET 10.0.12, load average at start 3.97 4.78 3.48
- Data: 524 vectors x 1024 dims, collection `gvbbench_eshoponweb`. Read READ-ONLY from GenericVectorBuilder.dbo.gvb_eshoponweb in ChunkId order, vectors as native SqlVector<float>, 0.3 s.
- Queries: golden: 20 labelled questions from /home/dan/ForClaude/evalkit/questions_golden.json, embedded with qwen3-emb-0.6b (cached in /home/dan/gvb-data/bench-cache/golden-eshoponweb-68df42ca69efa079.json, no embedding calls). Top 10. Throughput at concurrency 1, 8 for 20 s each.
- Ground truth: brute force over every vector in memory, 0.0 s for all 20 queries.
- nDCG@10 of the exact answer itself (the ceiling for this embedder): 0.518

| engine | index | load rows/s | p50 ms | p95 ms | p99 ms | QPS@1 | QPS@8 | recall@10 | nDCG@10 | RAM | disk |
|---|---|---|---|---|---|---|---|---|---|---|---|
| sql | exact VECTOR_DISTANCE cosine, no vector index (full scan) | 550 | 6.81 | 9.18 | 12.6 | 177.7 | 764.7 | 1.000 | 0.518 | 4.13 GiB | 5.15 MiB |
| sql-diskann | DiskANN (preview) via VECTOR_SEARCH, cosine, build {"StartId":"306", "L":"48", "M":"8", "R":"48"} | 584 | 4.50 | 5.84 | 6.27 | 205.0 | 890.2 | 0.965 | 0.526 | 4.13 GiB | 4.52 MiB |
| qdrant | exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used | 2,742 | 1.56 | 2.10 | 2.24 | 436.2 | 3051.4 | 1.000 | 0.518 | 4.21 GiB | 328.14 MiB |
| qdrant-hnsw | HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine | 5,965 | 2.57 | 3.49 | 3.96 | 428.7 | 3040.5 | 1.000 | 0.518 | 4.21 GiB | 328.14 MiB |
| pgvector | HNSW vector_cosine_ops m=16 ef_construction=128, hnsw.ef_search=100 per query, float32 vector(n), cosine; exact mode = same query with index scans off (sequential scan) | 471 | 1.97 | 3.15 | 3.63 | 468.3 | 3312.1 | 1.000 | 0.518 | 99.93 MiB | 7.72 MiB |
| mariadb | VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16 (no ef_construction setting exists), mhnsw_ef_search=3200, mhnsw_max_cache_size 4G; exact mode = IGNORE INDEX full scan | 715 | 1.98 | 2.83 | 3.09 | 501.7 | 4131.7 | 1.000 | 0.518 | 160.9 MiB | 22.01 MiB |
| oracle | HNSW in-memory neighbor graph NEIGHBORS=16 EFCONSTRUCTION=128, EFSEARCH=100 per query, cosine; exact mode = FETCH EXACT FIRST (full scan) | 1,390 | 5.56 | 7.60 | 7.99 | 245.4 | 670.6 | 1.000 | 0.518 | 2.1 GiB | 0 B |
| redis | HNSW TYPE FLOAT32 M=16 EF_CONSTRUCTION=128, EF_RUNTIME=100 per query, cosine; exact mode = FLAT index built on first exact query | 6,874 | 1.22 | 1.84 | 2.17 | 820.3 | 3154.2 | 1.000 | 0.518 | 21.49 MiB | 0 B |
| mongodb | vectorSearch index, HNSW maxEdges=16 numEdgeCandidates=128, float32 binData, cosine, numCandidates=20x hits (min 100); exact mode = $vectorSearch exact:true | 2,322 | 4.96 | 7.73 | 9.95 | 225.3 | 2426.7 | 0.965 | 0.535 | 683.4 MiB | 115.44 KiB |
| clickhouse | vector_similarity HNSW cosineDistance, quantization bf16, M=16 ef_construction=128, hnsw_candidate_list_size_for_search=256, rescoring off; exact mode = full scan with skip indexes off | 3,103 | 6.88 | 9.66 | 10.4 | 151.0 | 625.3 | 0.995 | 0.528 | 927.4 MiB | 0 B |
| milvus | HNSW M=16 efConstruction=128, ef=100, metric COSINE, Strong consistency searches; approximate only (no exact mode) | 745 | 8.27 | 9.47 | 11.0 | 138.3 | 1493.2 | 1.000 | 0.518 | 198.7 MiB | 8.78 MiB |
| weaviate | HNSW maxConnections(M)=16 efConstruction=128, ef=-1 (dynamic: limit x 8 clamped 100..500), cosine, no quantization; approximate only (no exact mode) | 735 | 16.1 | 25.1 | 28.9 | 62.8 | 484.6 | 1.000 | 0.518 | 92.98 MiB | 2.88 MiB |
| chroma | HNSW M=16 ef_construction=128, ef_search=100 (Chroma default), cosine; approximate only (no exact mode) | 430 | 6.63 | 7.55 | 8.30 | 156.6 | 609.8 | 1.000 | 0.518 | 46.35 MiB | 414.16 KiB |
| elasticsearch | HNSW float32, no quantization, m=16, ef_construction=128, cosine; search k=top, num_candidates=100; 1 shard, 0 replicas | 462 | 11.0 | 14.0 | 15.8 | 166.8 | 2543.4 | 1.000 | 0.518 | 2.6 GiB | 9.46 MiB |
| opensearch | faiss HNSW float32, no compression, m=16, ef_construction=128, cosinesimil; search k=top, ef_search=100; 1 shard, 0 replicas | 414 | 9.83 | 14.1 | 16.2 | 150.5 | 1702.4 | 1.000 | 0.518 | 2.58 GiB | 16.33 MiB |
| typesense | HNSW float32 (hnswlib), m=16, ef_construction=128, cosine; search k=top, ef=100; exact mode = filter ordinal:>=0 with flat_search_cutoff; index held in memory | 814 | 5.59 | 10.3 | 11.9 | 135.9 | 896.5 | 1.000 | 0.518 | 122.6 MiB | 410.09 MiB |
| vespa | HNSW float32 tensor, prenormalized-angular (cosine), max-links-per-node=16, neighbors-to-explore-at-insert=128; search targetHits=top, ef=100 via exploreAdditionalHits; exact mode = approximate:false; vectors held in memory | 498 | 6.38 | 8.95 | 11.0 | 218.9 | 1489.0 | 1.000 | 0.518 | 2.85 GiB | 5.21 MiB |
| duckdb | HNSW (vss extension) FLOAT[n] metric=cosine m=16 ef_construction=128, ef_search=100 per connection, persistent (hnsw_enable_experimental_persistence=true, checkpoint_threshold=256MB); exact mode = array_cosine_similarity sequential scan; score = 1 - cosine distance | 1,530 | 4.00 | 7.59 | 9.47 | 247.1 | 181.5 | 1.000 | 0.518 | - | 2.9 MiB |
| sqlitevec | vec0 brute-force scan, no ANN index (exact), float32, cosine distance, default chunk_size=1024; score = 1 - cosine distance | 4,183 | 2.16 | 2.40 | 2.81 | 502.6 | 457.3 | 1.000 | 0.518 | - | 9.72 MiB |

## Details per target

### sql

- Engine: Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) (always-on)
- Index: exact VECTOR_DISTANCE cosine, no vector index (full scan)
- Load: 524 rows in batches of 1000, 1.0 s of upserts (550 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 4.13 GiB (whole SQL Server process); disk: 5.15 MiB (table and its indexes, reserved pages)
- Load average 3.89 4.75 3.48 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### sql-diskann

- Engine: Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) (always-on)
- Index: DiskANN (preview) via VECTOR_SEARCH, cosine, build {"StartId":"306", "L":"48", "M":"8", "R":"48"}
- Load: 524 rows in batches of 1000, 0.9 s of upserts (584 rows/s); count matched 0.0 s after the last upsert; index step 3.7 s (copied to dbo.gvb_gvbbench_eshoponweb_ann (INT key) and built DiskANN, parameters {"StartId":"306", "L":"48", "M":"8", "R":"48"})
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.77 ms, p95 5.84 ms, recall 1.000
- RAM: 4.13 GiB (whole SQL Server process); disk: 4.52 MiB (table and its indexes, reserved pages)
- Load average 4.84 4.85 3.56 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### qdrant

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used
- Load: 524 rows in batches of 1000, 0.2 s of upserts (2,742 rows/s); count matched 0.0 s after the last upsert; index step 2.0 s (status Green, 0 of 524 vectors in HNSW segments (4 segments))
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 4.21 GiB (whole Qdrant process, every collection); disk: 328.14 MiB (collection folder)
- Load average 4.97 4.82 3.61 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### qdrant-hnsw

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine
- Load: 524 rows in batches of 1000, 0.1 s of upserts (5,965 rows/s); count matched 0.0 s after the last upsert; index step 2.0 s (status Green, 0 of 524 vectors in HNSW segments (4 segments))
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.05 ms, p95 1.45 ms, recall 1.000
- RAM: 4.21 GiB (whole Qdrant process, every collection); disk: 328.14 MiB (collection folder)
- Load average 5.16 4.87 3.68 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### pgvector

- Engine: PostgreSQL 17 + pgvector 0.8.7 (compose)
- Index: HNSW vector_cosine_ops m=16 ef_construction=128, hnsw.ef_search=100 per query, float32 vector(n), cosine; exact mode = same query with index scans off (sequential scan)
- Load: 524 rows in batches of 1000, 1.1 s of upserts (471 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 3.16 ms, p95 4.27 ms, recall 1.000
- RAM: 99.93 MiB (docker stats); disk: 7.72 MiB added by this load (1.02 GiB (whole engine data folder), 1.01 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 6.11 5.13 3.83 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### mariadb

- Engine: MariaDB 11.8.9 (InnoDB + VECTOR INDEX) (compose)
- Index: VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16 (no ef_construction setting exists), mhnsw_ef_search=3200, mhnsw_max_cache_size 4G; exact mode = IGNORE INDEX full scan
- Load: 524 rows in batches of 1000, 0.7 s of upserts (715 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 2.08 ms, p95 3.29 ms, recall 1.000
- RAM: 160.9 MiB (docker stats); disk: 22.01 MiB added by this load (193.79 MiB (whole engine data folder), 171.78 MiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 6.41 5.32 3.96 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### oracle

- Engine: Oracle AI Database Free 23.26 (23ai line, VECTOR FLOAT32) (compose)
- Index: HNSW in-memory neighbor graph NEIGHBORS=16 EFCONSTRUCTION=128, EFSEARCH=100 per query, cosine; exact mode = FETCH EXACT FIRST (full scan)
- Load: 524 rows in batches of 1000, 0.4 s of upserts (1,390 rows/s); count matched 0.1 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.71 ms, p95 7.69 ms, recall 1.000
- RAM: 2.1 GiB (docker stats); disk: 0 B added by this load (6.88 GiB (whole engine data folder), 6.88 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 6.41 5.52 4.11 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### redis

- Engine: Redis 8.10.2 (query engine, HASH + vector index) (compose)
- Index: HNSW TYPE FLOAT32 M=16 EF_CONSTRUCTION=128, EF_RUNTIME=100 per query, cosine; exact mode = FLAT index built on first exact query
- Load: 524 rows in batches of 1000, 0.1 s of upserts (6,874 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.13 ms, p95 12.1 ms, recall 1.000
- RAM: 21.49 MiB (docker stats); disk: 0 B added by this load (89 B (whole engine data folder), 89 B before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 3.80 4.96 3.99 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### mongodb

- Engine: MongoDB 8.0.32 Atlas Local (mongod + mongot Vector Search) (compose)
- Index: vectorSearch index, HNSW maxEdges=16 numEdgeCandidates=128, float32 binData, cosine, numCandidates=20x hits (min 100); exact mode = $vectorSearch exact:true
- Load: 524 rows in batches of 1000, 0.2 s of upserts (2,322 rows/s); count matched 0.7 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 2.35 ms, p95 3.07 ms, recall 1.000
- RAM: 683.4 MiB (docker stats); disk: 115.44 KiB added by this load (1.12 GiB (whole engine data folder), 1.12 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 4.45 4.94 4.04 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### clickhouse

- Engine: ClickHouse 26.3.39.7 (MergeTree + vector_similarity index) (compose)
- Index: vector_similarity HNSW cosineDistance, quantization bf16, M=16 ef_construction=128, hnsw_candidate_list_size_for_search=256, rescoring off; exact mode = full scan with skip indexes off
- Load: 524 rows in batches of 1000, 0.2 s of upserts (3,103 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 7.18 ms, p95 9.45 ms, recall 1.000
- RAM: 927.4 MiB (docker stats); disk: 0 B added by this load (1.22 GiB (whole engine data folder), 1.63 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 3.24 4.46 3.97 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### milvus

- Engine: Milvus 2.6.25 (standalone, embedded etcd, local storage, REST API v2) (compose)
- Index: HNSW M=16 efConstruction=128, ef=100, metric COSINE, Strong consistency searches; approximate only (no exact mode)
- Load: 524 rows in batches of 1000, 0.7 s of upserts (745 rows/s); count matched 1.6 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 198.7 MiB (docker stats); disk: 8.78 MiB added by this load (5.22 GiB (whole engine data folder), 5.21 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.39 4.88 4.15 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### weaviate

- Engine: Weaviate 1.39.8 (single node, REST + GraphQL) (compose)
- Index: HNSW maxConnections(M)=16 efConstruction=128, ef=-1 (dynamic: limit x 8 clamped 100..500), cosine, no quantization; approximate only (no exact mode)
- Load: 524 rows in batches of 1000, 0.7 s of upserts (735 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 92.98 MiB (docker stats); disk: 2.88 MiB added by this load (3.44 MiB (whole engine data folder), 576.14 KiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.52 5.11 4.28 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### chroma

- Engine: Chroma 1.4.4 (single node, REST API v2) (compose)
- Index: HNSW M=16 ef_construction=128, ef_search=100 (Chroma default), cosine; approximate only (no exact mode)
- Load: 524 rows in batches of 1000, 1.2 s of upserts (430 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 46.35 MiB (docker stats); disk: 414.16 KiB added by this load (544.37 MiB (whole engine data folder), 543.97 MiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 6.12 5.28 4.38 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### elasticsearch

- Engine: Elasticsearch 9.5.3 (dense_vector) (compose)
- Index: HNSW float32, no quantization, m=16, ef_construction=128, cosine; search k=top, num_candidates=100; 1 shard, 0 replicas
- Load: 524 rows in batches of 1000, 1.1 s of upserts (462 rows/s); count matched 0.1 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.51 ms, p95 10.1 ms, recall 1.000
- RAM: 2.6 GiB (docker stats); disk: 9.46 MiB added by this load (9.58 MiB (whole engine data folder), 129.22 KiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 4.45 4.89 4.31 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### opensearch

- Engine: OpenSearch 3.9.0 (k-NN plugin, faiss) (compose)
- Index: faiss HNSW float32, no compression, m=16, ef_construction=128, cosinesimil; search k=top, ef_search=100; 1 shard, 0 replicas
- Load: 524 rows in batches of 1000, 1.3 s of upserts (414 rows/s); count matched 0.1 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.18 ms, p95 9.55 ms, recall 1.000
- RAM: 2.58 GiB (docker stats); disk: 16.33 MiB added by this load (16.8 MiB (whole engine data folder), 476.87 KiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.50 5.17 4.45 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### typesense

- Engine: Typesense 30.2 (vector search) (compose)
- Index: HNSW float32 (hnswlib), m=16, ef_construction=128, cosine; search k=top, ef=100; exact mode = filter ordinal:>=0 with flat_search_cutoff; index held in memory
- Load: 524 rows in batches of 1000, 0.6 s of upserts (814 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 7.36 ms, p95 17.2 ms, recall 1.000
- RAM: 122.6 MiB (docker stats); disk: 410.09 MiB (whole engine data folder)
- Started by run-all for this measurement and stopped afterwards.
- Load average 6.65 5.58 4.64 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### vespa

- Engine: Vespa 8.754.14 (tensor attribute + HNSW) (compose)
- Index: HNSW float32 tensor, prenormalized-angular (cosine), max-links-per-node=16, neighbors-to-explore-at-insert=128; search targetHits=top, ef=100 via exploreAdditionalHits; exact mode = approximate:false; vectors held in memory
- Load: 524 rows in batches of 1000, 1.1 s of upserts (498 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 2.36 ms, p95 2.84 ms, recall 1.000
- RAM: 2.85 GiB (docker stats); disk: 5.21 MiB added by this load (1.36 GiB (whole engine data folder), 1.36 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 7.24 5.96 4.85 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### duckdb

- Engine: DuckDB 1.5.6 + vss b833341 (HNSW, embedded, in process) (embedded)
- Index: HNSW (vss extension) FLOAT[n] metric=cosine m=16 ef_construction=128, ef_search=100 per connection, persistent (hnsw_enable_experimental_persistence=true, checkpoint_threshold=256MB); exact mode = array_cosine_similarity sequential scan; score = 1 - cosine distance
- Load: 524 rows in batches of 1000, 0.3 s of upserts (1,530 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 5.91 ms, p95 8.39 ms, recall 1.000
- RAM: in this process, not measured; disk: 2.9 MiB added by this load (2.9 MiB (engine data folder), 0 B before)
- WARNING: load average 8.25 6.41 5.06 (1/5/15 min) on 8 logical CPUs when searching began; the box was busy, so latency and QPS are inflated by other work.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### sqlitevec

- Engine: SQLite 3.53.3 + sqlite-vec 0.1.7-alpha.2.1 (vec0, embedded, in process) (embedded)
- Index: vec0 brute-force scan, no ANN index (exact), float32, cosine distance, default chunk_size=1024; score = 1 - cosine distance
- Load: 524 rows in batches of 1000, 0.1 s of upserts (4,183 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 2.04 ms, p95 2.36 ms, recall 1.000
- RAM: in this process, not measured; disk: 9.72 MiB added by this load (9.72 MiB (engine data folder), 0 B before)
- Load average 5.00 5.82 4.93 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.


## Notes

- Load rows/s counts only time inside each target's upsert calls: one writer, batches of 1000, rows already in memory, the collection dropped and created fresh first.
- Latency is client-side wall time around each search (network and driver included), one query at a time, after 20 warm-up queries; at least 200 samples (small query sets are repeated).
- QPS: N workers searching back to back for 20 s per level; completed searches divided by elapsed time.
- Recall@10: share of the exact top 10 (brute force in memory) that the engine returned. A hit whose exact similarity ties the 10th best (within 1e-5) also counts, because duplicate rows embed to identical vectors.
- RAM of always-on servers (SQL Server, Qdrant) is the whole process, including every other database or collection it serves; for compose engines it is docker stats of the engine's containers. Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing).
- The empty benchmark database GvbBench was dropped at the end.
