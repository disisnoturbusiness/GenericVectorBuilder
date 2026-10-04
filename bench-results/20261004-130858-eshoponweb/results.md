# Vector engine benchmark: eshoponweb

Run 2026-10-04T13:08:58Z (run-all). Command line:

```
/home/dan/ForClaude/GenericVectorBuilder/src/GenericVectorBuilder.Bench/bin/Release/net10.0/GenericVectorBuilder.Bench.dll run-all --pipeline eshoponweb --targets sql,sql-diskann,qdrant,qdrant-hnsw,pgvector,mariadb,oracle,redis,mongodb,clickhouse,milvus,weaviate,chroma,elasticsearch,opensearch,typesense,vespa,duckdb,sqlitevec --queries golden
```

- Machine: linus7795, Intel(R) Xeon(R) CPU E5-1620 v3 @ 3.50GHz (8 logical CPUs), 62.7 GiB RAM, GPU NVIDIA GeForce RTX 3060, 12288 MiB, Ubuntu 24.04.5 LTS, kernel 6.8.0-142-generic, .NET 10.0.12, load average at start 1.60 0.95 0.46
- Data: 524 vectors x 1024 dims, collection `gvbbench_eshoponweb`. Read READ-ONLY from GenericVectorBuilder.dbo.gvb_eshoponweb in ChunkId order, vectors as native SqlVector<float>, 0.3 s.
- Queries: golden: 20 labelled questions from /home/dan/ForClaude/evalkit/questions_golden.json, embedded with qwen3-emb-0.6b (cached in /home/dan/gvb-data/bench-cache/golden-eshoponweb-68df42ca69efa079.json, no embedding calls). Top 10. Throughput at concurrency 1, 8 for 20 s each.
- Ground truth: brute force over every vector in memory, 0.0 s for all 20 queries.
- nDCG@10 of the exact answer itself (the ceiling for this embedder): 0.518

| engine | index | load rows/s | p50 ms | p95 ms | p99 ms | QPS@1 | QPS@8 | recall@10 | nDCG@10 | RAM | disk |
|---|---|---|---|---|---|---|---|---|---|---|---|
| sql | exact VECTOR_DISTANCE cosine, no vector index (full scan) | 565 | 6.72 | 9.42 | 10.9 | 191.6 | 914.0 | 1.000 | 0.518 | 4.13 GiB | 5.15 MiB |
| sql-diskann | DiskANN (preview) via VECTOR_SEARCH, cosine, build {"StartId":"306", "L":"48", "M":"8", "R":"48"} | 713 | 4.57 | 5.50 | 6.41 | 226.9 | 1025.4 | 0.965 | 0.526 | 4.13 GiB | 4.52 MiB |
| qdrant | exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used | 2,721 | 2.30 | 3.11 | 3.65 | 425.1 | 3213.1 | 1.000 | 0.518 | 4.21 GiB | 328.14 MiB |
| qdrant-hnsw | HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine | 4,968 | 2.26 | 2.86 | 3.36 | 407.9 | 3355.3 | 1.000 | 0.518 | 4.21 GiB | 328.14 MiB |
| pgvector | HNSW vector_cosine_ops m=16 ef_construction=128, hnsw.ef_search=100 per query, float32 vector(n), cosine; exact mode = same query with index scans off (sequential scan) | 537 | 3.03 | 3.68 | 3.87 | 399.1 | 3822.7 | 1.000 | 0.518 | 100.1 MiB | 7.72 MiB |
| mariadb | VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16 (no ef_construction setting exists), mhnsw_ef_search=3200, mhnsw_max_cache_size 4G; exact mode = IGNORE INDEX full scan | 681 | 1.69 | 2.43 | 2.65 | 457.8 | 4558.4 | 1.000 | 0.518 | 170.3 MiB | 22.01 MiB |
| oracle | HNSW in-memory neighbor graph NEIGHBORS=16 EFCONSTRUCTION=128, EFSEARCH=100 per query, cosine; exact mode = FETCH EXACT FIRST (full scan) | 1,486 | 4.85 | 6.68 | 7.28 | 288.5 | 508.7 | 1.000 | 0.518 | 2.14 GiB | 0 B |
| redis | HNSW TYPE FLOAT32 M=16 EF_CONSTRUCTION=128, EF_RUNTIME=100 per query, cosine; exact mode = FLAT index built on first exact query | 10,766 | 1.86 | 2.19 | 3.01 | 783.3 | 2106.8 | 1.000 | 0.518 | 20.88 MiB | 0 B |
| mongodb | vectorSearch index, HNSW maxEdges=16 numEdgeCandidates=128, float32 binData, cosine, numCandidates=20x hits (min 100); exact mode = $vectorSearch exact:true | 2,343 | 5.51 | 9.32 | 10.1 | 253.4 | 2927.7 | 1.000 | 0.518 | 666.6 MiB | 115.24 KiB |
| clickhouse | vector_similarity HNSW cosineDistance, quantization bf16, M=16 ef_construction=128, hnsw_candidate_list_size_for_search=256, rescoring off; exact mode = full scan with skip indexes off | 2,663 | 6.22 | 8.52 | 9.41 | 168.1 | 665.0 | 0.995 | 0.528 | 1019 MiB | 1018 MiB |
| milvus | HNSW M=16 efConstruction=128, ef=100, metric COSINE, Strong consistency searches; approximate only (no exact mode) | 739 | 7.12 | 7.93 | 10.4 | 154.4 | 1690.9 | 1.000 | 0.518 | 194.1 MiB | 9.29 MiB |
| weaviate | HNSW maxConnections(M)=16 efConstruction=128, ef=-1 (dynamic: limit x 8 clamped 100..500), cosine, no quantization; approximate only (no exact mode) | 836 | 15.3 | 23.0 | 25.0 | 81.5 | 523.4 | 1.000 | 0.518 | 107.8 MiB | 2.88 MiB |
| chroma | HNSW M=16 ef_construction=128, ef_search=100 (Chroma default), cosine; approximate only (no exact mode) | 461 | 5.98 | 6.73 | 7.50 | 193.9 | 600.3 | 1.000 | 0.518 | 49.23 MiB | 414.16 KiB |
| elasticsearch | HNSW float32, no quantization, m=16, ef_construction=128, cosine; search k=top, num_candidates=100; 1 shard, 0 replicas | 507 | 11.8 | 15.3 | 17.2 | 159.9 | 2544.2 | 1.000 | 0.518 | 2.6 GiB | 9.45 MiB |
| opensearch | faiss HNSW float32, no compression, m=16, ef_construction=128, cosinesimil; search k=top, ef_search=100; 1 shard, 0 replicas | 551 | 10.6 | 14.1 | 15.2 | 144.8 | 1966.6 | 1.000 | 0.518 | 2.59 GiB | 16.07 MiB |
| typesense | HNSW float32 (hnswlib), m=16, ef_construction=128, cosine; search k=top, ef=100; exact mode = filter ordinal:>=0 with flat_search_cutoff; index held in memory | 764 | 11.9 | 12.9 | 13.3 | 106.1 | 944.0 | 1.000 | 0.518 | 119.7 MiB | 402.53 MiB |
| vespa | HNSW float32 tensor, prenormalized-angular (cosine), max-links-per-node=16, neighbors-to-explore-at-insert=128; search targetHits=top, ef=100 via exploreAdditionalHits; exact mode = approximate:false; vectors held in memory | 494 | 6.41 | 9.58 | 14.1 | 201.4 | 1563.8 | 1.000 | 0.518 | 2.85 GiB | 5.51 MiB |
| duckdb | HNSW (vss extension) FLOAT[n] metric=cosine m=16 ef_construction=128, ef_search=100 per connection, persistent (hnsw_enable_experimental_persistence=true, checkpoint_threshold=256MB); exact mode = array_cosine_similarity sequential scan; score = 1 - cosine distance | 1,483 | 4.05 | 5.39 | 6.49 | 255.1 | 171.2 | 1.000 | 0.518 | - | 2.9 MiB |
| sqlitevec | vec0 brute-force scan, no ANN index (exact), float32, cosine distance, default chunk_size=1024; score = 1 - cosine distance | 2,718 | 2.07 | 2.35 | 2.55 | 526.6 | 457.8 | 1.000 | 0.518 | - | 9.72 MiB |

## Details per target

### sql

- Engine: Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) (always-on)
- Index: exact VECTOR_DISTANCE cosine, no vector index (full scan)
- Load: 524 rows in batches of 1000, 0.9 s of upserts (565 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 4.13 GiB (whole SQL Server process); disk: 5.15 MiB (table and its indexes, reserved pages)
- Load average 1.79 1.00 0.48 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### sql-diskann

- Engine: Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) (always-on)
- Index: DiskANN (preview) via VECTOR_SEARCH, cosine, build {"StartId":"306", "L":"48", "M":"8", "R":"48"}
- Load: 524 rows in batches of 1000, 0.7 s of upserts (713 rows/s); count matched 0.0 s after the last upsert; index step 3.7 s (copied to dbo.gvb_gvbbench_eshoponweb_ann (INT key) and built DiskANN, parameters {"StartId":"306", "L":"48", "M":"8", "R":"48"})
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.28 ms, p95 5.81 ms, recall 1.000
- RAM: 4.13 GiB (whole SQL Server process); disk: 4.52 MiB (table and its indexes, reserved pages)
- Load average 4.01 1.65 0.72 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### qdrant

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used
- Load: 524 rows in batches of 1000, 0.2 s of upserts (2,721 rows/s); count matched 0.0 s after the last upsert; index step 2.0 s (status Green, 0 of 524 vectors in HNSW segments (4 segments))
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 4.21 GiB (whole Qdrant process, every collection); disk: 328.14 MiB (collection folder)
- Load average 5.17 2.26 0.97 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### qdrant-hnsw

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine
- Load: 524 rows in batches of 1000, 0.1 s of upserts (4,968 rows/s); count matched 0.0 s after the last upsert; index step 2.0 s (status Green, 0 of 524 vectors in HNSW segments (4 segments))
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 0.87 ms, p95 0.96 ms, recall 1.000
- RAM: 4.21 GiB (whole Qdrant process, every collection); disk: 328.14 MiB (collection folder)
- Load average 6.31 2.85 1.22 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### pgvector

- Engine: PostgreSQL 17 + pgvector 0.8.7 (compose)
- Index: HNSW vector_cosine_ops m=16 ef_construction=128, hnsw.ef_search=100 per query, float32 vector(n), cosine; exact mode = same query with index scans off (sequential scan)
- Load: 524 rows in batches of 1000, 1.0 s of upserts (537 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 2.59 ms, p95 2.76 ms, recall 1.000
- RAM: 100.1 MiB (docker stats); disk: 7.72 MiB added by this load (1.04 GiB (whole engine data folder), 1.03 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.45 3.19 1.43 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### mariadb

- Engine: MariaDB 11.8.9 (InnoDB + VECTOR INDEX) (compose)
- Index: VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16 (no ef_construction setting exists), mhnsw_ef_search=3200, mhnsw_max_cache_size 4G; exact mode = IGNORE INDEX full scan
- Load: 524 rows in batches of 1000, 0.8 s of upserts (681 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.66 ms, p95 1.75 ms, recall 1.000
- RAM: 170.3 MiB (docker stats); disk: 22.01 MiB added by this load (193.79 MiB (whole engine data folder), 171.78 MiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 6.33 3.77 1.72 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### oracle

- Engine: Oracle AI Database Free 23.26 (23ai line, VECTOR FLOAT32) (compose)
- Index: HNSW in-memory neighbor graph NEIGHBORS=16 EFCONSTRUCTION=128, EFSEARCH=100 per query, cosine; exact mode = FETCH EXACT FIRST (full scan)
- Load: 524 rows in batches of 1000, 0.4 s of upserts (1,486 rows/s); count matched 0.1 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 5.63 ms, p95 17.6 ms, recall 1.000
- RAM: 2.14 GiB (docker stats); disk: 0 B added by this load (6.88 GiB (whole engine data folder), 6.88 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.58 3.98 1.91 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### redis

- Engine: Redis 8.10.2 (query engine, HASH + vector index) (compose)
- Index: HNSW TYPE FLOAT32 M=16 EF_CONSTRUCTION=128, EF_RUNTIME=100 per query, cosine; exact mode = FLAT index built on first exact query
- Load: 524 rows in batches of 1000, 0.0 s of upserts (10,766 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.48 ms, p95 12.5 ms, recall 1.000
- RAM: 20.88 MiB (docker stats); disk: 0 B added by this load (89 B (whole engine data folder), 89 B before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 3.83 3.79 1.97 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### mongodb

- Engine: MongoDB 8.0.32 Atlas Local (mongod + mongot Vector Search) (compose)
- Index: vectorSearch index, HNSW maxEdges=16 numEdgeCandidates=128, float32 binData, cosine, numCandidates=20x hits (min 100); exact mode = $vectorSearch exact:true
- Load: 524 rows in batches of 1000, 0.2 s of upserts (2,343 rows/s); count matched 1.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.92 ms, p95 3.05 ms, recall 1.000
- RAM: 666.6 MiB (docker stats); disk: 115.24 KiB added by this load (1.11 GiB (whole engine data folder), 1.11 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 3.27 3.63 2.01 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### clickhouse

- Engine: ClickHouse 26.3.39.7 (MergeTree + vector_similarity index) (compose)
- Index: vector_similarity HNSW cosineDistance, quantization bf16, M=16 ef_construction=128, hnsw_candidate_list_size_for_search=256, rescoring off; exact mode = full scan with skip indexes off
- Load: 524 rows in batches of 1000, 0.2 s of upserts (2,663 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 6.77 ms, p95 9.03 ms, recall 1.000
- RAM: 1019 MiB (docker stats); disk: 1018 MiB added by this load (1.53 GiB (whole engine data folder), 550.51 MiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 1.87 3.19 2.04 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### milvus

- Engine: Milvus 2.6.25 (standalone, embedded etcd, local storage, REST API v2) (compose)
- Index: HNSW M=16 efConstruction=128, ef=100, metric COSINE, Strong consistency searches; approximate only (no exact mode)
- Load: 524 rows in batches of 1000, 0.7 s of upserts (739 rows/s); count matched 1.6 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 194.1 MiB (docker stats); disk: 9.29 MiB added by this load (5.21 GiB (whole engine data folder), 5.2 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 3.67 3.54 2.23 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### weaviate

- Engine: Weaviate 1.39.8 (single node, REST + GraphQL) (compose)
- Index: HNSW maxConnections(M)=16 efConstruction=128, ef=-1 (dynamic: limit x 8 clamped 100..500), cosine, no quantization; approximate only (no exact mode)
- Load: 524 rows in batches of 1000, 0.6 s of upserts (836 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 107.8 MiB (docker stats); disk: 2.88 MiB added by this load (3.44 MiB (whole engine data folder), 576.14 KiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 4.03 3.69 2.38 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### chroma

- Engine: Chroma 1.4.4 (single node, REST API v2) (compose)
- Index: HNSW M=16 ef_construction=128, ef_search=100 (Chroma default), cosine; approximate only (no exact mode)
- Load: 524 rows in batches of 1000, 1.1 s of upserts (461 rows/s); count matched 0.1 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 49.23 MiB (docker stats); disk: 414.16 KiB added by this load (543.97 MiB (whole engine data folder), 543.56 MiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.35 4.09 2.59 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### elasticsearch

- Engine: Elasticsearch 9.5.3 (dense_vector) (compose)
- Index: HNSW float32, no quantization, m=16, ef_construction=128, cosine; search k=top, num_candidates=100; 1 shard, 0 replicas
- Load: 524 rows in batches of 1000, 1.0 s of upserts (507 rows/s); count matched 0.1 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.41 ms, p95 8.32 ms, recall 1.000
- RAM: 2.6 GiB (docker stats); disk: 9.45 MiB added by this load (9.58 MiB (whole engine data folder), 129.42 KiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 3.56 3.81 2.60 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### opensearch

- Engine: OpenSearch 3.9.0 (k-NN plugin, faiss) (compose)
- Index: faiss HNSW float32, no compression, m=16, ef_construction=128, cosinesimil; search k=top, ef_search=100; 1 shard, 0 replicas
- Load: 524 rows in batches of 1000, 1.0 s of upserts (551 rows/s); count matched 0.2 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.29 ms, p95 11.7 ms, recall 1.000
- RAM: 2.59 GiB (docker stats); disk: 16.07 MiB added by this load (16.79 MiB (whole engine data folder), 738.32 KiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 4.87 4.14 2.80 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### typesense

- Engine: Typesense 30.2 (vector search) (compose)
- Index: HNSW float32 (hnswlib), m=16, ef_construction=128, cosine; search k=top, ef=100; exact mode = filter ordinal:>=0 with flat_search_cutoff; index held in memory
- Load: 524 rows in batches of 1000, 0.7 s of upserts (764 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 12.5 ms, p95 16.6 ms, recall 1.000
- RAM: 119.7 MiB (docker stats); disk: 402.53 MiB (whole engine data folder)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.43 4.46 3.00 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### vespa

- Engine: Vespa 8.754.14 (tensor attribute + HNSW) (compose)
- Index: HNSW float32 tensor, prenormalized-angular (cosine), max-links-per-node=16, neighbors-to-explore-at-insert=128; search targetHits=top, ef=100 via exploreAdditionalHits; exact mode = approximate:false; vectors held in memory
- Load: 524 rows in batches of 1000, 1.1 s of upserts (494 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.95 ms, p95 2.71 ms, recall 1.000
- RAM: 2.85 GiB (docker stats); disk: 5.51 MiB added by this load (1.3 GiB (whole engine data folder), 1.3 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.74 4.74 3.23 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### duckdb

- Engine: DuckDB 1.5.6 + vss b833341 (HNSW, embedded, in process) (embedded)
- Index: HNSW (vss extension) FLOAT[n] metric=cosine m=16 ef_construction=128, ef_search=100 per connection, persistent (hnsw_enable_experimental_persistence=true, checkpoint_threshold=256MB); exact mode = array_cosine_similarity sequential scan; score = 1 - cosine distance
- Load: 524 rows in batches of 1000, 0.4 s of upserts (1,483 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.47 ms, p95 7.44 ms, recall 1.000
- RAM: in this process, not measured; disk: 2.9 MiB added by this load (2.9 MiB (engine data folder), 0 B before)
- Load average 7.57 5.42 3.55 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### sqlitevec

- Engine: SQLite 3.53.3 + sqlite-vec 0.1.7-alpha.2.1 (vec0, embedded, in process) (embedded)
- Index: vec0 brute-force scan, no ANN index (exact), float32, cosine distance, default chunk_size=1024; score = 1 - cosine distance
- Load: 524 rows in batches of 1000, 0.2 s of upserts (2,718 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.80 ms, p95 1.89 ms, recall 1.000
- RAM: in this process, not measured; disk: 9.72 MiB added by this load (9.72 MiB (engine data folder), 0 B before)
- Load average 5.31 5.10 3.52 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.


## Notes

- Load rows/s counts only time inside each target's upsert calls: one writer, batches of 1000, rows already in memory, the collection dropped and created fresh first.
- Latency is client-side wall time around each search (network and driver included), one query at a time, after 20 warm-up queries; at least 200 samples (small query sets are repeated).
- QPS: N workers searching back to back for 20 s per level; completed searches divided by elapsed time.
- Recall@10: share of the exact top 10 (brute force in memory) that the engine returned. A hit whose exact similarity ties the 10th best (within 1e-5) also counts, because duplicate rows embed to identical vectors.
- RAM of always-on servers (SQL Server, Qdrant) is the whole process, including every other database or collection it serves; for compose engines it is docker stats of the engine's containers. Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing).
- The empty benchmark database GvbBench was dropped at the end.
