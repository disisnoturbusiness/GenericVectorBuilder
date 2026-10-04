# Vector engine benchmark: eshoponweb

Run 2026-10-03T18:44:45Z (run-all). Command line:

```
/home/dan/ForClaude/GenericVectorBuilder/src/GenericVectorBuilder.Bench/bin/Release/net10.0/GenericVectorBuilder.Bench.dll run-all --pipeline eshoponweb --targets sql,qdrant,qdrant-hnsw,pgvector,mariadb,oracle,redis,mongodb,clickhouse,milvus,weaviate,chroma,elasticsearch,opensearch,typesense,vespa,duckdb,sqlitevec --queries golden
```

- Machine: linus7795, Intel(R) Xeon(R) CPU E5-1620 v3 @ 3.50GHz (8 logical CPUs), 62.7 GiB RAM, GPU NVIDIA GeForce RTX 3060, 12288 MiB, Ubuntu 24.04.5 LTS, kernel 6.8.0-142-generic, .NET 10.0.12, load average at start 5.55 4.28 3.37
- Data: 524 vectors x 1024 dims, collection `gvbbench_eshoponweb`. Read READ-ONLY from GenericVectorBuilder.dbo.gvb_eshoponweb in ChunkId order, vectors as native SqlVector<float>, 0.3 s.
- Queries: golden: 20 labelled questions from /home/dan/ForClaude/evalkit/questions_golden.json, embedded with qwen3-emb-0.6b (cached in /home/dan/gvb-data/bench-cache/golden-eshoponweb-68df42ca69efa079.json, no embedding calls). Top 10. Throughput at concurrency 1, 8 for 20 s each.
- Ground truth: brute force over every vector in memory, 0.0 s for all 20 queries.
- nDCG@10 of the exact answer itself (the ceiling for this embedder): 0.518

| engine | index | load rows/s | p50 ms | p95 ms | p99 ms | QPS@1 | QPS@8 | recall@10 | nDCG@10 | RAM | disk |
|---|---|---|---|---|---|---|---|---|---|---|---|
| sql | exact VECTOR_DISTANCE cosine, no vector index (full scan) | 547 | 6.52 | 9.98 | 11.7 | 196.0 | 806.2 | 1.000 | 0.518 | 4.13 GiB | 5.15 MiB |
| qdrant | exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used | 2,355 | 2.79 | 3.32 | 4.21 | 407.7 | 3443.8 | 1.000 | 0.518 | 4.21 GiB | 328.14 MiB |
| qdrant-hnsw | HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine | 6,627 | 2.42 | 2.72 | 2.98 | 412.1 | 3432.3 | 1.000 | 0.518 | 4.21 GiB | 328.14 MiB |
| pgvector | HNSW vector_cosine_ops m=16 ef_construction=128, hnsw.ef_search=100 per query, float32 vector(n), cosine; exact mode = same query with index scans off (sequential scan) | 528 | 2.87 | 4.11 | 4.90 | 403.1 | 3830.9 | 1.000 | 0.518 | 101.3 MiB | 7.62 MiB |
| mariadb | VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16 (no ef_construction setting exists), mhnsw_ef_search=3200, mhnsw_max_cache_size 4G; exact mode = IGNORE INDEX full scan | 668 | 2.34 | 2.72 | 2.91 | 435.3 | 4559.3 | 1.000 | 0.518 | 156.1 MiB | 22.01 MiB |
| oracle | HNSW in-memory neighbor graph NEIGHBORS=16 EFCONSTRUCTION=128, EFSEARCH=100 per query, cosine; exact mode = FETCH EXACT FIRST (full scan) | 1,621 | 5.60 | 6.85 | 7.56 | 269.3 | 695.1 | 1.000 | 0.518 | 2.09 GiB | 0 B |
| redis | HNSW TYPE FLOAT32 M=16 EF_CONSTRUCTION=128, EF_RUNTIME=100 per query, cosine; exact mode = FLAT index built on first exact query | 6,347 | 1.51 | 2.02 | 2.51 | 767.5 | 2011.0 | 1.000 | 0.518 | 21 MiB | 0 B |
| mongodb | vectorSearch index, HNSW maxEdges=16 numEdgeCandidates=128, float32 binData, cosine, numCandidates=20x hits (min 100); exact mode = $vectorSearch exact:true | 1,803 | 8.14 | 15.8 | 17.5 | 307.7 | 2807.4 | 1.000 | 0.518 | 687.6 MiB | 115.72 KiB |
| clickhouse | vector_similarity HNSW cosineDistance, quantization bf16, M=16 ef_construction=128, hnsw_candidate_list_size_for_search=256, rescoring off; exact mode = full scan with skip indexes off | 2,711 | 6.25 | 8.03 | 9.62 | 166.8 | 711.3 | 0.985 | 0.520 | 1.05 GiB | 210.16 MiB |
| milvus | HNSW M=16 efConstruction=128, ef=100, metric COSINE, Strong consistency searches; approximate only (no exact mode) | 853 | 7.42 | 8.23 | 9.96 | 150.8 | 1723.6 | 1.000 | 0.518 | 204.8 MiB | 8.78 MiB |
| weaviate | HNSW maxConnections(M)=16 efConstruction=128, ef=-1 (dynamic: limit x 8 clamped 100..500), cosine, no quantization; approximate only (no exact mode) | 575 | 15.4 | 23.3 | 26.3 | 63.8 | 541.1 | 1.000 | 0.518 | 107.4 MiB | 2.88 MiB |
| chroma | HNSW M=16 ef_construction=128, ef_search=100 (Chroma default), cosine; approximate only (no exact mode) | 431 | 6.01 | 6.80 | 7.34 | 167.6 | 330.9 | 1.000 | 0.518 | 51.85 MiB | 414.16 KiB |
| elasticsearch | HNSW float32, no quantization, m=16, ef_construction=128, cosine; search k=top, num_candidates=100; 1 shard, 0 replicas | 474 | 13.2 | 16.8 | 17.8 | 148.1 | 2631.9 | 1.000 | 0.518 | 2.6 GiB | 9.46 MiB |
| opensearch | faiss HNSW float32, no compression, m=16, ef_construction=128, cosinesimil; search k=top, ef_search=100; 1 shard, 0 replicas | 386 | 12.3 | 15.0 | 16.5 | 129.8 | 1874.6 | 1.000 | 0.518 | 2.63 GiB | 16.07 MiB |
| typesense | HNSW float32 (hnswlib), m=16, ef_construction=128, cosine; search k=top, ef=100; exact mode = filter ordinal:>=0 with flat_search_cutoff; index held in memory | 709 | 8.92 | 12.5 | 13.3 | 95.4 | 974.4 | 1.000 | 0.518 | 120.4 MiB | 389.1 MiB |
| vespa | HNSW float32 tensor, prenormalized-angular (cosine), max-links-per-node=16, neighbors-to-explore-at-insert=128; search targetHits=top, ef=100 via exploreAdditionalHits; exact mode = approximate:false; vectors held in memory | 518 | 6.53 | 8.24 | 9.07 | 174.2 | 1552.9 | 1.000 | 0.518 | 2.89 GiB | 414.97 MiB |
| duckdb | HNSW (vss extension) FLOAT[n] metric=cosine m=16 ef_construction=128, ef_search=100 per connection, persistent (hnsw_enable_experimental_persistence=true, checkpoint_threshold=256MB); exact mode = array_cosine_similarity sequential scan; score = 1 - cosine distance | 1,569 | 4.06 | 5.28 | 6.05 | 257.3 | 199.5 | 1.000 | 0.518 | - | 2.9 MiB |
| sqlitevec | vec0 brute-force scan, no ANN index (exact), float32, cosine distance, default chunk_size=1024; score = 1 - cosine distance | 4,027 | 2.16 | 2.48 | 2.73 | 545.2 | 528.1 | 1.000 | 0.518 | - | 9.72 MiB |

## Details per target

### sql

- Engine: Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) (always-on)
- Index: exact VECTOR_DISTANCE cosine, no vector index (full scan)
- Load: 524 rows in batches of 1000, 1.0 s of upserts (547 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 4.13 GiB (whole SQL Server process); disk: 5.15 MiB (table and its indexes, reserved pages)
- Load average 5.42 4.28 3.37 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### qdrant

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used
- Load: 524 rows in batches of 1000, 0.2 s of upserts (2,355 rows/s); count matched 0.0 s after the last upsert; index step 2.0 s (status Green, 0 of 524 vectors in HNSW segments (4 segments))
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 4.21 GiB (whole Qdrant process, every collection); disk: 328.14 MiB (collection folder)
- Load average 5.58 4.44 3.46 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### qdrant-hnsw

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine
- Load: 524 rows in batches of 1000, 0.1 s of upserts (6,627 rows/s); count matched 0.0 s after the last upsert; index step 2.0 s (status Green, 0 of 524 vectors in HNSW segments (4 segments))
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 0.85 ms, p95 0.97 ms, recall 1.000
- RAM: 4.21 GiB (whole Qdrant process, every collection); disk: 328.14 MiB (collection folder)
- Load average 6.21 4.69 3.59 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### pgvector

- Engine: PostgreSQL 17 + pgvector 0.8.7 (compose)
- Index: HNSW vector_cosine_ops m=16 ef_construction=128, hnsw.ef_search=100 per query, float32 vector(n), cosine; exact mode = same query with index scans off (sequential scan)
- Load: 524 rows in batches of 1000, 1.0 s of upserts (528 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 2.36 ms, p95 2.49 ms, recall 1.000
- RAM: 101.3 MiB (docker stats); disk: 7.62 MiB added by this load (1.04 GiB (whole engine data folder), 1.03 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 6.05 4.86 3.70 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### mariadb

- Engine: MariaDB 11.8.9 (InnoDB + VECTOR INDEX) (compose)
- Index: VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16 (no ef_construction setting exists), mhnsw_ef_search=3200, mhnsw_max_cache_size 4G; exact mode = IGNORE INDEX full scan
- Load: 524 rows in batches of 1000, 0.8 s of upserts (668 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.66 ms, p95 1.75 ms, recall 1.000
- RAM: 156.1 MiB (docker stats); disk: 22.01 MiB added by this load (193.79 MiB (whole engine data folder), 171.78 MiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.76 4.88 3.76 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### oracle

- Engine: Oracle AI Database Free 23.26 (23ai line, VECTOR FLOAT32) (compose)
- Index: HNSW in-memory neighbor graph NEIGHBORS=16 EFCONSTRUCTION=128, EFSEARCH=100 per query, cosine; exact mode = FETCH EXACT FIRST (full scan)
- Load: 524 rows in batches of 1000, 0.3 s of upserts (1,621 rows/s); count matched 0.1 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.19 ms, p95 7.61 ms, recall 1.000
- RAM: 2.09 GiB (docker stats); disk: 0 B added by this load (6.88 GiB (whole engine data folder), 6.88 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.32 4.93 3.85 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### redis

- Engine: Redis 8.10.2 (query engine, HASH + vector index) (compose)
- Index: HNSW TYPE FLOAT32 M=16 EF_CONSTRUCTION=128, EF_RUNTIME=100 per query, cosine; exact mode = FLAT index built on first exact query
- Load: 524 rows in batches of 1000, 0.1 s of upserts (6,347 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.34 ms, p95 12.2 ms, recall 1.000
- RAM: 21 MiB (docker stats); disk: 0 B added by this load (89 B (whole engine data folder), 89 B before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 3.11 4.40 3.72 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### mongodb

- Engine: MongoDB 8.0.32 Atlas Local (mongod + mongot Vector Search) (compose)
- Index: vectorSearch index, HNSW maxEdges=16 numEdgeCandidates=128, float32 binData, cosine, numCandidates=20x hits (min 100); exact mode = $vectorSearch exact:true
- Load: 524 rows in batches of 1000, 0.3 s of upserts (1,803 rows/s); count matched 1.4 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 2.66 ms, p95 4.90 ms, recall 1.000
- RAM: 687.6 MiB (docker stats); disk: 115.72 KiB added by this load (1.12 GiB (whole engine data folder), 1.12 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 3.03 4.14 3.67 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### clickhouse

- Engine: ClickHouse 26.3.39.7 (MergeTree + vector_similarity index) (compose)
- Index: vector_similarity HNSW cosineDistance, quantization bf16, M=16 ef_construction=128, hnsw_candidate_list_size_for_search=256, rescoring off; exact mode = full scan with skip indexes off
- Load: 524 rows in batches of 1000, 0.2 s of upserts (2,711 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 6.53 ms, p95 7.49 ms, recall 1.000
- RAM: 1.05 GiB (docker stats); disk: 210.16 MiB added by this load (430.66 MiB (whole engine data folder), 220.5 MiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.43 4.69 3.92 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### milvus

- Engine: Milvus 2.6.25 (standalone, embedded etcd, local storage, REST API v2) (compose)
- Index: HNSW M=16 efConstruction=128, ef=100, metric COSINE, Strong consistency searches; approximate only (no exact mode)
- Load: 524 rows in batches of 1000, 0.6 s of upserts (853 rows/s); count matched 1.6 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 204.8 MiB (docker stats); disk: 8.78 MiB added by this load (5.2 GiB (whole engine data folder), 5.19 GiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 5.64 4.87 4.03 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### weaviate

- Engine: Weaviate 1.39.8 (single node, REST + GraphQL) (compose)
- Index: HNSW maxConnections(M)=16 efConstruction=128, ef=-1 (dynamic: limit x 8 clamped 100..500), cosine, no quantization; approximate only (no exact mode)
- Load: 524 rows in batches of 1000, 0.9 s of upserts (575 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 107.4 MiB (docker stats); disk: 2.88 MiB added by this load (3.44 MiB (whole engine data folder), 576.14 KiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 4.13 4.65 4.02 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### chroma

- Engine: Chroma 1.4.4 (single node, REST API v2) (compose)
- Index: HNSW M=16 ef_construction=128, ef_search=100 (Chroma default), cosine; approximate only (no exact mode)
- Load: 524 rows in batches of 1000, 1.2 s of upserts (431 rows/s); count matched 0.1 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 51.85 MiB (docker stats); disk: 414.16 KiB added by this load (543.56 MiB (whole engine data folder), 543.16 MiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 4.18 4.57 4.03 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### elasticsearch

- Engine: Elasticsearch 9.5.3 (dense_vector) (compose)
- Index: HNSW float32, no quantization, m=16, ef_construction=128, cosine; search k=top, num_candidates=100; 1 shard, 0 replicas
- Load: 524 rows in batches of 1000, 1.1 s of upserts (474 rows/s); count matched 0.1 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.35 ms, p95 8.21 ms, recall 1.000
- RAM: 2.6 GiB (docker stats); disk: 9.46 MiB added by this load (9.58 MiB (whole engine data folder), 128.77 KiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 3.45 4.21 3.94 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### opensearch

- Engine: OpenSearch 3.9.0 (k-NN plugin, faiss) (compose)
- Index: faiss HNSW float32, no compression, m=16, ef_construction=128, cosinesimil; search k=top, ef_search=100; 1 shard, 0 replicas
- Load: 524 rows in batches of 1000, 1.4 s of upserts (386 rows/s); count matched 0.1 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 5.50 ms, p95 7.82 ms, recall 1.000
- RAM: 2.63 GiB (docker stats); disk: 16.07 MiB added by this load (16.92 MiB (whole engine data folder), 872.09 KiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 4.51 4.41 4.03 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### typesense

- Engine: Typesense 30.2 (vector search) (compose)
- Index: HNSW float32 (hnswlib), m=16, ef_construction=128, cosine; search k=top, ef=100; exact mode = filter ordinal:>=0 with flat_search_cutoff; index held in memory
- Load: 524 rows in batches of 1000, 0.7 s of upserts (709 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 9.26 ms, p95 16.7 ms, recall 1.000
- RAM: 120.4 MiB (docker stats); disk: 389.1 MiB (whole engine data folder)
- Started by run-all for this measurement and stopped afterwards.
- Load average 4.55 4.45 4.07 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### vespa

- Engine: Vespa 8.754.14 (tensor attribute + HNSW) (compose)
- Index: HNSW float32 tensor, prenormalized-angular (cosine), max-links-per-node=16, neighbors-to-explore-at-insert=128; search targetHits=top, ef=100 via exploreAdditionalHits; exact mode = approximate:false; vectors held in memory
- Load: 524 rows in batches of 1000, 1.0 s of upserts (518 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 2.22 ms, p95 2.91 ms, recall 1.000
- RAM: 2.89 GiB (docker stats); disk: 414.97 MiB added by this load (1.24 GiB (whole engine data folder), 853.39 MiB before)
- Started by run-all for this measurement and stopped afterwards.
- WARNING: load average 8.07 5.27 4.36 (1/5/15 min) on 8 logical CPUs when searching began; the box was busy, so latency and QPS are inflated by other work.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### duckdb

- Engine: DuckDB 1.5.6 + vss b833341 (HNSW, embedded, in process) (embedded)
- Index: HNSW (vss extension) FLOAT[n] metric=cosine m=16 ef_construction=128, ef_search=100 per connection, persistent (hnsw_enable_experimental_persistence=true, checkpoint_threshold=256MB); exact mode = array_cosine_similarity sequential scan; score = 1 - cosine distance
- Load: 524 rows in batches of 1000, 0.3 s of upserts (1,569 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 5.86 ms, p95 9.41 ms, recall 1.000
- RAM: in this process, not measured; disk: 2.9 MiB added by this load (2.9 MiB (engine data folder), 0 B before)
- Load average 7.24 5.47 4.48 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### sqlitevec

- Engine: SQLite 3.53.3 + sqlite-vec 0.1.7-alpha.2.1 (vec0, embedded, in process) (embedded)
- Index: vec0 brute-force scan, no ANN index (exact), float32, cosine distance, default chunk_size=1024; score = 1 - cosine distance
- Load: 524 rows in batches of 1000, 0.1 s of upserts (4,027 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.95 ms, p95 2.08 ms, recall 1.000
- RAM: in this process, not measured; disk: 9.72 MiB added by this load (9.72 MiB (engine data folder), 0 B before)
- Load average 4.58 5.01 4.37 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.


## Notes

- Load rows/s counts only time inside each target's upsert calls: one writer, batches of 1000, rows already in memory, the collection dropped and created fresh first.
- Latency is client-side wall time around each search (network and driver included), one query at a time, after 20 warm-up queries; at least 200 samples (small query sets are repeated).
- QPS: N workers searching back to back for 20 s per level; completed searches divided by elapsed time.
- Recall@10: share of the exact top 10 (brute force in memory) that the engine returned. A hit whose exact similarity ties the 10th best (within 1e-5) also counts, because duplicate rows embed to identical vectors.
- RAM of always-on servers (SQL Server, Qdrant) is the whole process, including every other database or collection it serves; for compose engines it is docker stats of the engine's containers. Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing).
- The empty benchmark database GvbBench was dropped at the end.
