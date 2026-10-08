# eShopOnWeb: medians of 3 runs

**Not publishable as is.** On 2026-10-04 independent checks found method problems in these runs: run order changed timings, the qdrant-hnsw target never built its HNSW index at 524 points (so both Qdrant rows are scans), Milvus and Oracle were searched without waiting for their index builds, and SQL DiskANN was compared against SQL exact across different runs. The fix was stopped partway; see `design/dead-drafts/`.

- Data: eShopOnWeb, 254 C# files, 524 chunks, qwen3-emb-0.6b, 1024 dims; 20 golden questions.
- Runs: 20261003-184445, 20261004-130858, 20261004-132735 (sql-diskann: 20261003-234838, 20261004-130858, 20261004-132735).
- Each cell: median, then min to max in brackets.

| rank | engine | searches/s, 8 at once | searches/s, 1 at a time | p50 ms | load rows/s | recall@10 | nDCG@10 | errors |
|---|---|---|---|---|---|---|---|---|
| 1 | mariadb | 4,558 (4,132 to 4,559) | 458 (435 to 502) | 1.98 (1.69 to 2.34) | 681 | 1.000 | 0.518 | 0 |
| 2 | pgvector | 3,823 (3,312 to 3,831) | 403 (399 to 468) | 2.87 (1.97 to 3.03) | 528 | 1.000 | 0.518 | 0 |
| 3 | qdrant-hnsw | 3,355 (3,041 to 3,432) | 412 (408 to 429) | 2.42 (2.26 to 2.57) | 5,965 | 1.000 | 0.518 | 0 |
| 4 | qdrant | 3,213 (3,051 to 3,444) | 425 (408 to 436) | 2.30 (1.56 to 2.79) | 2,721 | 1.000 | 0.518 | 0 |
| 5 | mongodb | 2,807 (2,427 to 2,928) | 253 (225 to 308) | 5.51 (4.96 to 8.14) | 2,322 | 1.000 | 0.518 | 0 |
| 6 | elasticsearch | 2,544 (2,543 to 2,632) | 160 (148 to 167) | 11.76 (11.03 to 13.19) | 474 | 1.000 | 0.518 | 0 |
| 7 | redis | 2,107 (2,011 to 3,154) | 783 (768 to 820) | 1.51 (1.22 to 1.86) | 6,874 | 1.000 | 0.518 | 0 |
| 8 | opensearch | 1,875 (1,702 to 1,967) | 145 (130 to 150) | 10.62 (9.83 to 12.29) | 414 | 1.000 | 0.518 | 0 |
| 9 | milvus | 1,691 (1,493 to 1,724) | 151 (138 to 154) | 7.42 (7.12 to 8.27) | 745 | 1.000 | 0.518 | 0 |
| 10 | vespa | 1,553 (1,489 to 1,564) | 201 (174 to 219) | 6.41 (6.38 to 6.53) | 498 | 1.000 | 0.518 | 0 |
| 11 | sql-diskann | 1,003 (890 to 1,025) | 227 (205 to 248) | 4.50 (4.24 to 4.56) | 668 | 0.965 | 0.526 | 0 |
| 12 | typesense | 944 (896 to 974) | 106 (95 to 136) | 8.92 (5.59 to 11.89) | 764 | 1.000 | 0.518 | 0 |
| 13 | sql | 806 (765 to 914) | 192 (178 to 196) | 6.72 (6.52 to 6.81) | 550 | 1.000 | 0.518 | 0 |
| 14 | oracle | 671 (509 to 695) | 269 (245 to 289) | 5.56 (4.85 to 5.60) | 1,486 | 1.000 | 0.518 | 0 |
| 15 | clickhouse | 665 (625 to 711) | 167 (151 to 168) | 6.25 (6.22 to 6.88) | 2,711 | 0.995 | 0.528 | 0 |
| 16 | chroma | 600 (331 to 610) | 168 (157 to 194) | 6.01 (5.98 to 6.63) | 431 | 1.000 | 0.518 | 0 |
| 17 | weaviate | 523 (485 to 541) | 64 (63 to 82) | 15.36 (15.33 to 16.11) | 735 | 1.000 | 0.518 | 0 |
| 18 | sqlitevec | 458 (457 to 528) | 527 (503 to 545) | 2.16 (2.07 to 2.16) | 4,027 | 1.000 | 0.518 | 0 |
| 19 | duckdb | 181 (171 to 200) | 255 (247 to 257) | 4.05 (4.00 to 4.06) | 1,530 | 1.000 | 0.518 | 0 |

Redis holds everything in memory; as configured here it snapshots every 5 minutes with no append-only log, so a crash can lose recent writes.
