# Vector engine benchmark: adventureworks

Run 2026-10-03T15:04:28Z (run-all). Command line:

```
/tmp/claude-1000/gvblane-bench/src/GenericVectorBuilder.Bench/bin/Debug/net10.0/GenericVectorBuilder.Bench.dll run-all --pipeline adventureworks --limit 50000 --targets sql,qdrant,qdrant-hnsw --queries random:200 --repo /home/dan/ForClaude/GenericVectorBuilder
```

- Machine: linus7795, Intel(R) Xeon(R) CPU E5-1620 v3 @ 3.50GHz (8 logical CPUs), 62.7 GiB RAM, GPU NVIDIA GeForce RTX 3060, 12288 MiB, Ubuntu 24.04.5 LTS, kernel 6.8.0-139-generic, .NET 10.0.12, load average at start 16.30 27.87 28.42
- Data: 50,000 vectors x 1024 dims, collection `gvbbench_adventureworks`. Read READ-ONLY from GenericVectorBuilder.dbo.gvb_adventureworks in ChunkId order, vectors as native SqlVector<float>, 2.6 s.
- Queries: random:200 (stored vectors picked with seed 20261003; each query's own row is left out of the exact answer and removed from every engine's hits). Top 10. Throughput at concurrency 1, 8 for 20 s each.
- Ground truth: brute force over every vector in memory, 1.1 s for all 200 queries.

| engine | index | load rows/s | p50 ms | p95 ms | p99 ms | QPS@1 | QPS@8 | recall@10 | RAM | disk |
|---|---|---|---|---|---|---|---|---|---|---|
| sql | exact VECTOR_DISTANCE cosine, no vector index (full scan) | 343 | 176.3 | 492.1 | 1117.2 | 2.9 | 6.0 | 1.000 | 4.13 GiB | 398.52 MiB |
| qdrant | exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used | 337 | 23.7 | 45.7 | 59.8 | 80.0 | 110.3 | 1.000 | 6.22 GiB | 491.1 MiB |
| qdrant-hnsw | HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine | 530 | 2.04 | 2.81 | 3.73 | 399.1 | 1230.6 | 1.000 | 6.2 GiB | 491.06 MiB |

## Details per target

### sql

- Engine: Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) (always-on)
- Index: exact VECTOR_DISTANCE cosine, no vector index (full scan)
- Load: 50,000 rows in batches of 1000, 145.9 s of upserts (343 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 50,000 rows, 0 errors
- RAM: 4.13 GiB (whole SQL Server process); disk: 398.52 MiB (table and its indexes, reserved pages)
- WARNING: load average 17.34 23.19 26.53 (1/5/15 min) on 8 logical CPUs when searching began; the box was busy, so latency and QPS are inflated by other work.
- Benchmark copy gvbbench_adventureworks dropped afterwards.

### qdrant

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used
- Load: 50,000 rows in batches of 1000, 148.2 s of upserts (337 rows/s); count matched 0.0 s after the last upsert; index step n/a
- Search: 200 latency samples, target held 50,000 rows, 0 errors
- RAM: 6.22 GiB (whole Qdrant process, every collection); disk: 491.1 MiB (collection folder)
- WARNING: load average 22.83 21.87 25.09 (1/5/15 min) on 8 logical CPUs when searching began; the box was busy, so latency and QPS are inflated by other work.
- Benchmark copy gvbbench_adventureworks dropped afterwards.

### qdrant-hnsw

- Engine: Qdrant 1.17.0 (systemd, local) (always-on)
- Index: HNSW m=16 ef_construct=100, hnsw_ef=server default, cosine
- Load: 50,000 rows in batches of 1000, 94.4 s of upserts (530 rows/s); count matched 0.0 s after the last upsert; index step 3.0 s (status Green, 48,256 of 50,000 vectors in HNSW segments (4 segments))
- Search: 200 latency samples, target held 50,000 rows, 0 errors
- Exact mode: 200 queries, p50 12.5 ms, p95 18.9 ms, recall 1.000
- RAM: 6.2 GiB (whole Qdrant process, every collection); disk: 491.06 MiB (collection folder)
- WARNING: load average 18.67 20.99 24.32 (1/5/15 min) on 8 logical CPUs when searching began; the box was busy, so latency and QPS are inflated by other work.
- Benchmark copy gvbbench_adventureworks dropped afterwards.


## Notes

- WARNING: load average 16.30 27.87 28.42 on 8 logical CPUs when the run started. Other work was competing for the CPU, so absolute latency and QPS are worse than this box can do; compare engines only within one run, and rerun on a quiet box before quoting numbers.
- Load rows/s counts only time inside each target's upsert calls: one writer, batches of 1000, rows already in memory, the collection dropped and created fresh first.
- Latency is client-side wall time around each search (network and driver included), one query at a time, after 20 warm-up queries; at least 200 samples (small query sets are repeated).
- QPS: N workers searching back to back for 20 s per level; completed searches divided by elapsed time.
- Recall@10: share of the exact top 10 (brute force in memory) that the engine returned. A hit whose exact similarity ties the 10th best (within 1e-5) also counts, because duplicate rows embed to identical vectors.
- RAM of always-on servers (SQL Server, Qdrant) is the whole process, including every other database or collection it serves; for compose engines it is docker stats of the engine's containers. Disk of compose engines is the whole engine data folder.
- The empty benchmark database GvbBench was dropped at the end.
