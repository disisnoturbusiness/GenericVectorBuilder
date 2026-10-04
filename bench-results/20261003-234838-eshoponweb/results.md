# Vector engine benchmark: eshoponweb

Run 2026-10-03T23:48:37Z (run-all). Command line:

```
/home/dan/ForClaude/GenericVectorBuilder/src/GenericVectorBuilder.Bench/bin/Release/net10.0/GenericVectorBuilder.Bench.dll run-all --pipeline eshoponweb --targets sql,sql-diskann,mariadb,qdrant-hnsw --queries golden
```

- Machine: linus7795, Intel(R) Xeon(R) CPU E5-1620 v3 @ 3.50GHz (8 logical CPUs), 62.7 GiB RAM, GPU NVIDIA GeForce RTX 3060, 12288 MiB, Ubuntu 24.04.5 LTS, kernel 6.8.0-142-generic, .NET 10.0.12, load average at start 0.13 0.13 0.10
- Data: 524 vectors x 1024 dims, collection `gvbbench_eshoponweb`. Read READ-ONLY from GenericVectorBuilder.dbo.gvb_eshoponweb in ChunkId order, vectors as native SqlVector<float>, 0.2 s.
- Queries: golden: 20 labelled questions from /home/dan/ForClaude/evalkit/questions_golden.json, embedded with qwen3-emb-0.6b (cached in /home/dan/gvb-data/bench-cache/golden-eshoponweb-68df42ca69efa079.json, no embedding calls). Top 10. Throughput at concurrency 1, 8 for 20 s each.
- Ground truth: brute force over every vector in memory, 0.0 s for all 20 queries.
- nDCG@10 of the exact answer itself (the ceiling for this embedder): 0.518

| engine | index | load rows/s | p50 ms | p95 ms | p99 ms | QPS@1 | QPS@8 | recall@10 | nDCG@10 | RAM | disk |
|---|---|---|---|---|---|---|---|---|---|---|---|
| sql | exact VECTOR_DISTANCE cosine, no vector index (full scan) | 570 | 6.18 | 10.7 | 11.5 | 216.1 | 987.9 | 1.000 | 0.518 | 4.13 GiB | 5.15 MiB |
| sql-diskann | DiskANN (preview) via VECTOR_SEARCH, cosine, build {"StartId":"306", "L":"48", "M":"8", "R":"48"} | 668 | 4.24 | 5.58 | 5.77 | 247.9 | 1002.5 | 0.965 | 0.526 | 4.13 GiB | 4.52 MiB |
| mariadb | VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16 (no ef_construction setting exists), mhnsw_ef_search=3200, mhnsw_max_cache_size 4G; exact mode = IGNORE INDEX full scan | 700 | 2.63 | 2.99 | 3.11 | 434.6 | 4574.1 | 1.000 | 0.518 | 169.5 MiB | 22.01 MiB |
| qdrant-hnsw | HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine | 2,705 | 2.73 | 3.25 | 3.69 | 410.0 | 3448.7 | 1.000 | 0.518 | 4.21 GiB | 328.14 MiB |

## Details per target

### sql

- Engine: Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) (always-on)
- Index: exact VECTOR_DISTANCE cosine, no vector index (full scan)
- Load: 524 rows in batches of 1000, 0.9 s of upserts (570 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 4.13 GiB (whole SQL Server process); disk: 5.15 MiB (table and its indexes, reserved pages)
- Load average 0.13 0.13 0.10 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### sql-diskann

- Engine: Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) (always-on)
- Index: DiskANN (preview) via VECTOR_SEARCH, cosine, build {"StartId":"306", "L":"48", "M":"8", "R":"48"}
- Load: 524 rows in batches of 1000, 0.8 s of upserts (668 rows/s); count matched 0.0 s after the last upsert; index step 3.7 s (copied to dbo.gvb_gvbbench_eshoponweb_ann (INT key) and built DiskANN, parameters {"StartId":"306", "L":"48", "M":"8", "R":"48"})
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 4.30 ms, p95 5.49 ms, recall 1.000
- RAM: 4.13 GiB (whole SQL Server process); disk: 4.52 MiB (table and its indexes, reserved pages)
- Load average 2.48 0.72 0.30 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### mariadb

- Engine: MariaDB 11.8.9 (InnoDB + VECTOR INDEX) (compose)
- Index: VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16 (no ef_construction setting exists), mhnsw_ef_search=3200, mhnsw_max_cache_size 4G; exact mode = IGNORE INDEX full scan
- Load: 524 rows in batches of 1000, 0.7 s of upserts (700 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 1.67 ms, p95 1.76 ms, recall 1.000
- RAM: 169.5 MiB (docker stats); disk: 22.01 MiB added by this load (193.79 MiB (whole engine data folder), 171.78 MiB before)
- Started by run-all for this measurement and stopped afterwards.
- Load average 3.47 1.20 0.48 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### qdrant-hnsw

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine
- Load: 524 rows in batches of 1000, 0.2 s of upserts (2,705 rows/s); count matched 0.0 s after the last upsert; index step 2.0 s (status Green, 0 of 524 vectors in HNSW segments (4 segments))
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 20 queries, p50 0.85 ms, p95 1.31 ms, recall 1.000
- RAM: 4.21 GiB (whole Qdrant process, every collection); disk: 328.14 MiB (collection folder)
- Load average 6.27 2.18 0.84 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.


## Notes

- Load rows/s counts only time inside each target's upsert calls: one writer, batches of 1000, rows already in memory, the collection dropped and created fresh first.
- Latency is client-side wall time around each search (network and driver included), one query at a time, after 20 warm-up queries; at least 200 samples (small query sets are repeated).
- QPS: N workers searching back to back for 20 s per level; completed searches divided by elapsed time.
- Recall@10: share of the exact top 10 (brute force in memory) that the engine returned. A hit whose exact similarity ties the 10th best (within 1e-5) also counts, because duplicate rows embed to identical vectors.
- RAM of always-on servers (SQL Server, Qdrant) is the whole process, including every other database or collection it serves; for compose engines it is docker stats of the engine's containers. Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing).
- The empty benchmark database GvbBench was dropped at the end.
