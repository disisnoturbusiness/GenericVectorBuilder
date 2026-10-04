# Vector engine benchmark: eshoponweb

Run 2026-10-03T18:42:07Z (run-all). Command line:

```
/home/dan/ForClaude/GenericVectorBuilder/src/GenericVectorBuilder.Bench/bin/Release/net10.0/GenericVectorBuilder.Bench.dll run-all --pipeline eshoponweb --targets sql,qdrant,qdrant-hnsw --queries random:200
```

- Machine: linus7795, Intel(R) Xeon(R) CPU E5-1620 v3 @ 3.50GHz (8 logical CPUs), 62.7 GiB RAM, GPU NVIDIA GeForce RTX 3060, 12288 MiB, Ubuntu 24.04.5 LTS, kernel 6.8.0-142-generic, .NET 10.0.12, load average at start 4.67 3.33 2.95
- Data: 524 vectors x 1024 dims, collection `gvbbench_eshoponweb`. Read READ-ONLY from GenericVectorBuilder.dbo.gvb_eshoponweb in ChunkId order, vectors as native SqlVector<float>, 0.3 s.
- Queries: random:200 (stored vectors picked with seed 20261003; each query's own row is left out of the exact answer and removed from every engine's hits). Top 10. Throughput at concurrency 1, 8 for 20 s each.
- Ground truth: brute force over every vector in memory, 0.1 s for all 200 queries.

| engine | index | load rows/s | p50 ms | p95 ms | p99 ms | QPS@1 | QPS@8 | recall@10 | RAM | disk |
|---|---|---|---|---|---|---|---|---|---|---|
| sql | exact VECTOR_DISTANCE cosine, no vector index (full scan) | 528 | 6.62 | 10.2 | 11.9 | 172.2 | 831.9 | 1.000 | 4.13 GiB | 5.15 MiB |
| qdrant | exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used | 2,539 | 3.08 | 3.76 | 7.52 | 377.8 | 3035.5 | 1.000 | 4.21 GiB | 328.14 MiB |
| qdrant-hnsw | HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine | 5,058 | 2.63 | 3.48 | 4.41 | 379.8 | 3021.2 | 1.000 | 4.21 GiB | 328.14 MiB |

## Details per target

### sql

- Engine: Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) (always-on)
- Index: exact VECTOR_DISTANCE cosine, no vector index (full scan)
- Load: 524 rows in batches of 1000, 1.0 s of upserts (528 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 4.13 GiB (whole SQL Server process); disk: 5.15 MiB (table and its indexes, reserved pages)
- Load average 4.67 3.33 2.95 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### qdrant

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used
- Load: 524 rows in batches of 1000, 0.2 s of upserts (2,539 rows/s); count matched 0.0 s after the last upsert; index step 2.0 s (status Green, 0 of 524 vectors in HNSW segments (4 segments))
- Search: 200 latency samples, target held 524 rows, 0 errors
- RAM: 4.21 GiB (whole Qdrant process, every collection); disk: 328.14 MiB (collection folder)
- Load average 5.05 3.59 3.06 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.

### qdrant-hnsw

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine
- Load: 524 rows in batches of 1000, 0.1 s of upserts (5,058 rows/s); count matched 0.0 s after the last upsert; index step 2.0 s (status Green, 0 of 524 vectors in HNSW segments (4 segments))
- Search: 200 latency samples, target held 524 rows, 0 errors
- Exact mode: 200 queries, p50 1.47 ms, p95 3.03 ms, recall 1.000
- RAM: 4.21 GiB (whole Qdrant process, every collection); disk: 328.14 MiB (collection folder)
- Load average 6.33 4.02 3.22 (1/5/15 min) when searching began.
- Benchmark copy gvbbench_eshoponweb dropped afterwards.


## Notes

- Load rows/s counts only time inside each target's upsert calls: one writer, batches of 1000, rows already in memory, the collection dropped and created fresh first.
- Latency is client-side wall time around each search (network and driver included), one query at a time, after 20 warm-up queries; at least 200 samples (small query sets are repeated).
- QPS: N workers searching back to back for 20 s per level; completed searches divided by elapsed time.
- Recall@10: share of the exact top 10 (brute force in memory) that the engine returned. A hit whose exact similarity ties the 10th best (within 1e-5) also counts, because duplicate rows embed to identical vectors.
- RAM of always-on servers (SQL Server, Qdrant) is the whole process, including every other database or collection it serves; for compose engines it is docker stats of the engine's containers. Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing).
- The empty benchmark database GvbBench was dropped at the end.
