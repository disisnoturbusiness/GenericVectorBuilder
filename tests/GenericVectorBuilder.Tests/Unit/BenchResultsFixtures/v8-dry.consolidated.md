# Vector engine benchmark

redis and mariadb had the lowest p50 latencies, in that order: each engine further down took at least 1.35 times as long, in each session; redis holds its data in memory.

Data: eshoponweb, 524 vectors of 1024 dimensions; 20 queries, top 10 hits each.

Machine: Intel(R) Xeon(R) CPU E5-1620 v3 @ 3.50GHz, 8 logical CPUs, 62.7 GiB of RAM.

At 524 vectors each figure is the cost of one request through the engine's .NET client, and does not show how an index scales.

Session v7: runs 20261006-130619-eshoponweb, 20261006-142724-eshoponweb and 20261006-154837-eshoponweb, started from 2026-10-06T13:06:19Z to 2026-10-06T15:48:37Z.

Session v8: runs 20261007-130619-eshoponweb, 20261007-142724-eshoponweb and 20261007-154837-eshoponweb, started from 2026-10-07T13:06:19Z to 2026-10-07T15:48:37Z.

> golden: 20 labelled questions from /home/dan/ForClaude/evalkit/questions_golden.json, embedded with qwen3-emb-0.6b (cached in /home/dan/gvb-data/bench-cache/golden-eshoponweb-68df42ca69efa079.json, no embedding calls)

## Threshold and basis

An engine is shown ahead of another when, in each session separately, its slowest run beat the other's fastest run by at least 1.35 times; every other pair is not separated by this test.

The threshold is 35%: the smallest multiple of 5% at least 5% above the largest move in the basis, 29.08%, and never below 35%.

The basis holds the 12 runs of sessions v5, v6, v7 and v8, all with machine control on.

Basis moves are taken between runs in which the engine recorded the same setup: its engine text, index text, search settings, durability text, engine files, engine settings, image id and hosting.

A field other than the engine text and the index text that one of two runs did not record is not compared.

The largest move was 29.08%, the QPS@8 ratio of mongodb/oracle between runs 20261005-035711-eshoponweb (seed 503) and 20261006-154837-eshoponweb (seed 703).

Those two runs differ in turbo, uncore, warmup, rehearsal, build and whether searchSettings was recorded.

p50, between basis runs of one recorded setup: largest moves 15.28% (redis) for one engine and 26.34% (mongodb/redis) for a pair.

p50, between those runs that also share turbo, uncore, warm-up and rehearsal: largest moves 12.02% (mongodb) for one engine and 14.78% (mongodb/sql-diskann) for a pair.

QPS@1, between basis runs of one recorded setup: largest moves 11.49% (mongodb) for one engine and 19.33% (mongodb/pgvector) for a pair.

QPS@1, between those runs that also share turbo, uncore, warm-up and rehearsal: largest moves 11.49% (mongodb) for one engine and 12.99% (mariadb/mongodb) for a pair.

QPS@8, between basis runs of one recorded setup: largest moves 18.84% (oracle) for one engine and 29.08% (mongodb/oracle) for a pair.

QPS@8, between those runs that also share turbo, uncore, warm-up and rehearsal: largest moves 14.33% (clickhouse) for one engine and 21.83% (mongodb/sql-diskann) for a pair.

exact p50, between basis runs of one recorded setup: largest moves 20.23% (redis) for one engine and 19.66% (qdrant-hnsw/redis) for a pair.

exact p50, between those runs that also share turbo, uncore, warm-up and rehearsal: largest moves 5.30% (redis) for one engine and 7.31% (pgvector/redis) for a pair.

Changes of recorded setup left out of the basis moves: 4, in clickhouse, duckdb, mariadb and sqlitevec.

Each is listed with the field that changed, the move it hides and the threshold it would give.

Between its v6 runs and its v7 and v8 runs, clickhouse recorded a different engine.

Counted across that change, the largest one-engine move is 32.29% (QPS@8) and the largest pair move 37.94% (clickhouse/mongodb QPS@8), and the threshold would be 45%.

The two runs of that one-engine move also differ in turbo, uncore, warmup, build and median MHz.

Between its v5 runs and its v6, v7 and v8 runs, duckdb recorded a different index.

Counted across that change, the largest one-engine move is 115.56% (QPS@8) and the largest pair move 144.95% (duckdb/oracle QPS@8), and the threshold would be 150%.

The two runs of that one-engine move also differ in warmup, rehearsal, build, median MHz and whether searchSettings was recorded.

Between its v5 runs and its v6, v7 and v8 runs, mariadb recorded a different durability.

Counted across that change, the largest one-engine move is 4.09% (exact p50) and the largest pair move 17.85% (mariadb/oracle QPS@8), and the threshold would be 35%.

The two runs of that one-engine move also differ in turbo, uncore, warmup, rehearsal, build, median MHz and whether searchSettings was recorded.

Between its v5 runs and its v6, v7 and v8 runs, sqlitevec recorded a different index.

Counted across that change, the largest one-engine move is 119.36% (QPS@8) and the largest pair move 155.08% (oracle/sqlitevec QPS@8), and the threshold would be 165%.

The two runs of that one-engine move also differ in warmup, rehearsal, build, median MHz and whether searchSettings was recorded.

vespa QPS@8, seed list 502 and 503, is left out of the basis (kind: method defect); design/verdicts/v5-verdict.txt, item 2, holds the words: The @8 pass ran first in runs 502 and 503.

elasticsearch QPS@8, seed list 503, is left out of the basis (kind: method defect); design/verdicts/v5-verdict.txt, item 2, holds the words: run 503, the one run where @8 was its first pass.

clickhouse every metric, seed list 501, 502 and 503, is left out of the basis (kind: unrecorded config change); design/verdicts/v6-verdict.md, item 1, holds the words: after the v5 runs ended at 04:38.

With the method defect rows kept in, the largest moves would be 74.42% for one engine and 95.64% for a pair, and the threshold 105%.

With the unrecorded config change rows kept in, the largest moves would be 21.81% for one engine and 30.71% for a pair, and the threshold 40%.

In the 6 runs without machine control, one engine moved up to 112.82% and a pair up to 148.82%; the threshold rests on runs with machine control on.

6 other runs of this pipeline are not in the basis; each is listed with its reason.

| basis run | session | seed | turbo | uncore | warm-up | rehearsal | build |
|---|---|---|---|---|---|---|---|
| 20261005-023459-eshoponweb | v5 | 501 | turbo not pinned (no no_turbo change recorded) | uncore not pinned | 20-search warm-up | rehearsal 5 s | /home/dan/gvb-work/lanes/v5-final |
| 20261005-031556-eshoponweb | v5 | 502 | turbo not pinned (no no_turbo change recorded) | uncore not pinned | 20-search warm-up | rehearsal 5 s | /home/dan/gvb-work/lanes/v5-final |
| 20261005-035711-eshoponweb | v5 | 503 | turbo not pinned (no no_turbo change recorded) | uncore not pinned | 20-search warm-up | rehearsal 5 s | /home/dan/gvb-work/lanes/v5-final |
| 20261005-073329-eshoponweb | v6 | 601 | turbo not pinned (no no_turbo change recorded) | uncore not pinned | settle check | rehearsal 30 s | /home/dan/gvb-work/lanes/v6-final |
| 20261005-085448-eshoponweb | v6 | 602 | turbo not pinned (no no_turbo change recorded) | uncore not pinned | settle check | rehearsal 30 s | /home/dan/gvb-work/lanes/v6-final |
| 20261005-101815-eshoponweb | v6 | 603 | turbo not pinned (no no_turbo change recorded) | uncore not pinned | settle check | rehearsal 30 s | /home/dan/gvb-work/lanes/v6-final |
| 20261006-130619-eshoponweb | v7 | 701 | turbo off (no_turbo 0 -> 1) | uncore pinned (MSR 0x620 = 0x1e1e) | settle check with level test | rehearsal 30 s | /home/dan/gvb-work/lanes/v7-final |
| 20261006-142724-eshoponweb | v7 | 702 | turbo off (no_turbo 0 -> 1) | uncore pinned (MSR 0x620 = 0x1e1e) | settle check with level test | rehearsal 30 s | /home/dan/gvb-work/lanes/v7-final |
| 20261006-154837-eshoponweb | v7 | 703 | turbo off (no_turbo 0 -> 1) | uncore pinned (MSR 0x620 = 0x1e1e) | settle check with level test | rehearsal 30 s | /home/dan/gvb-work/lanes/v7-final |
| 20261007-130619-eshoponweb | v8 | 701 | turbo off (no_turbo 0 -> 1) | uncore pinned (MSR 0x620 = 0x1e1e) | settle check with level test | rehearsal 30 s | /home/dan/gvb-work/lanes/v7-final |
| 20261007-142724-eshoponweb | v8 | 702 | turbo off (no_turbo 0 -> 1) | uncore pinned (MSR 0x620 = 0x1e1e) | settle check with level test | rehearsal 30 s | /home/dan/gvb-work/lanes/v7-final |
| 20261007-154837-eshoponweb | v8 | 703 | turbo off (no_turbo 0 -> 1) | uncore pinned (MSR 0x620 = 0x1e1e) | settle check with level test | rehearsal 30 s | /home/dan/gvb-work/lanes/v7-final |

| metric | one engine, same recorded setup | pair, same recorded setup | one engine, also same run conditions | pair, also same run conditions |
|---|---|---|---|---|
| p50 | 15.28% redis | 26.34% mongodb/redis | 12.02% mongodb | 14.78% mongodb/sql-diskann |
| QPS@1 | 11.49% mongodb | 19.33% mongodb/pgvector | 11.49% mongodb | 12.99% mariadb/mongodb |
| QPS@8 | 18.84% oracle | 29.08% mongodb/oracle | 14.33% clickhouse | 21.83% mongodb/sql-diskann |
| exact p50 | 20.23% redis | 19.66% qdrant-hnsw/redis | 5.30% redis | 7.31% pgvector/redis |

| differences between the two runs of the largest move |
|---|
| turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1) |
| uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e) |
| warmup: 20-search warm-up vs settle check with level test |
| rehearsal: rehearsal 5 s vs rehearsal 30 s |
| build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final |
| searchSettings recorded in one run only |

| setup split | fields changed | between | one-engine move hidden | pair move hidden | threshold if counted |
|---|---|---|---|---|---|
| clickhouse | engine | v6 vs v7 and v8 | 32.29% QPS@8 | 37.94% clickhouse/mongodb QPS@8 | 45% |
| duckdb | index | v5 vs v6, v7 and v8 | 115.56% QPS@8 | 144.95% duckdb/oracle QPS@8 | 150% |
| mariadb | durability | v5 vs v6, v7 and v8 | 4.09% exact p50 | 17.85% mariadb/oracle QPS@8 | 35% |
| sqlitevec | index | v5 vs v6, v7 and v8 | 119.36% QPS@8 | 155.08% oracle/sqlitevec QPS@8 | 165% |

| run not in the basis | reason |
|---|---|
| 20261003-183955-eshoponweb | machineControl not recorded |
| 20261003-184207-eshoponweb | machineControl not recorded |
| 20261003-184445-eshoponweb | machineControl not recorded |
| 20261003-234838-eshoponweb | machineControl not recorded |
| 20261004-130858-eshoponweb | machineControl not recorded |
| 20261004-132735-eshoponweb | machineControl not recorded |

## p50

p50 is the median latency of one searcher's searches, in ms; rows are in order of the median of 6 runs.

| engine | v7 min-max | v8 min-max | median of 6 | search | not separated from | flags |
|---|---|---|---|---|---|---|
| redis | 0.393-0.399 | 0.393-0.399 | 0.398 | approximate; recorded, documented |  |  |
| mariadb | 0.623-0.636 | 0.623-0.636 | 0.633 | approximate; measured |  |  |
| pgvector | 0.870-0.885 | 0.870-0.885 | 0.877 | approximate; measured | qdrant, qdrant-hnsw, oracle |  |
| qdrant | 0.881-0.886 | 0.881-0.886 | 0.883 | exact; measured | pgvector, qdrant-hnsw, oracle |  |
| qdrant-hnsw | 0.916-0.920 | 0.916-0.920 | 0.916 | approximate; measured | pgvector, qdrant, oracle |  |
| oracle | 0.923-0.934 | 0.923-0.934 | 0.927 | approximate; measured | pgvector, qdrant, qdrant-hnsw |  |
| elasticsearch | 1.310-1.329 | 1.310-1.329 | 1.328 | exact; measured | mongodb | index-not-ready |
| mongodb | 1.262-1.414 | 1.262-1.414 | 1.408 | approximate; recorded (engine's own report) | elasticsearch | busy-box, segment-layout-differs |
| sqlitevec | 1.910-1.933 | 1.910-1.933 | 1.925 | exact; measured | vespa, opensearch, chroma, milvus |  |
| vespa | 1.947-1.970 | 1.947-1.970 | 1.948 | approximate; measured | sqlitevec, opensearch, chroma, milvus |  |
| opensearch | 1.995-2.157 | 1.995-2.157 | 2.027 | approximate; measured | sqlitevec, vespa, chroma, milvus |  |
| chroma | 2.105-2.106 | 2.105-2.106 | 2.105 | approximate; recorded, unverified | sqlitevec, vespa, opensearch, milvus |  |
| milvus | 2.316-2.339 | 2.316-2.339 | 2.323 | approximate; recorded, unverified | sqlitevec, vespa, opensearch, chroma |  |
| duckdb | 3.666-3.678 | 3.666-3.678 | 3.667 | approximate; measured | sql-diskann, sql, typesense, clickhouse |  |
| sql-diskann | 3.596-3.694 | 3.596-3.694 | 3.685 | approximate; measured | duckdb, sql, typesense, clickhouse |  |
| sql | 4.016-4.060 | 4.016-4.060 | 4.050 | exact; measured | duckdb, sql-diskann, typesense, clickhouse, weaviate |  |
| typesense | 4.182-4.203 | 4.182-4.203 | 4.186 | approximate; measured | duckdb, sql-diskann, sql, clickhouse, weaviate |  |
| clickhouse | 4.941-4.973 | 4.941-4.973 | 4.957 | approximate; measured | duckdb, sql-diskann, sql, typesense, weaviate |  |
| weaviate | 5.360-5.372 | 5.360-5.372 | 5.361 | approximate; recorded, unverified | sql, typesense, clickhouse |  |

redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database', and its compose file sets save "300 1" and appendonly no.

Run 20261006-130619-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261006-130619-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261006-142724-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261006-142724-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261006-154837-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261006-154837-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261007-130619-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261007-130619-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261007-142724-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261007-142724-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261007-154837-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261007-154837-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261006-154837-eshoponweb: busy box during the one-searcher pass, with 0.350 CPUs of outside load.

Run 20261007-154837-eshoponweb: busy box during the one-searcher pass, with 0.350 CPUs of outside load.

Run 20261006-130619-eshoponweb: mongodb's index state after the searches reads 4 segment(s).

Run 20261006-142724-eshoponweb: mongodb's index state after the searches reads 2 segment(s).

Run 20261006-154837-eshoponweb: mongodb's index state after the searches reads 4 segment(s).

Run 20261007-130619-eshoponweb: mongodb's index state after the searches reads 4 segment(s).

Run 20261007-142724-eshoponweb: mongodb's index state after the searches reads 2 segment(s).

Run 20261007-154837-eshoponweb: mongodb's index state after the searches reads 4 segment(s).

## QPS@1

QPS@1 is searches per second of the same one-searcher pass as p50, so it is no second confirmation of the p50 order.

| engine | v7 min-max | v8 min-max | median of 6 | search | not separated from | flags |
|---|---|---|---|---|---|---|
| redis | 2519-2537 | 2519-2537 | 2527 | approximate; recorded, documented |  |  |
| mariadb | 1546-1578 | 1546-1578 | 1552 | approximate; measured |  |  |
| pgvector | 1107-1126 | 1107-1126 | 1117 | approximate; measured | qdrant, qdrant-hnsw, oracle |  |
| qdrant | 1111-1118 | 1111-1118 | 1114 | exact; measured | pgvector, qdrant-hnsw, oracle |  |
| qdrant-hnsw | 1071-1077 | 1071-1077 | 1077 | approximate; measured | pgvector, qdrant, oracle |  |
| oracle | 1042-1055 | 1042-1055 | 1051 | approximate; measured | pgvector, qdrant, qdrant-hnsw |  |
| elasticsearch | 739-748 | 739-748 | 739 | exact; measured | mongodb | index-not-ready |
| mongodb | 691-770 | 691-770 | 693 | approximate; recorded (engine's own report) | elasticsearch, sqlitevec | busy-box, segment-layout-differs |
| sqlitevec | 506-514 | 506-514 | 510 | exact; measured | mongodb, vespa, opensearch, chroma, milvus |  |
| vespa | 492-496 | 492-496 | 496 | approximate; measured | sqlitevec, opensearch, chroma, milvus |  |
| opensearch | 451-491 | 451-491 | 484 | approximate; measured | sqlitevec, vespa, chroma, milvus |  |
| chroma | 469-469 | 469-469 | 469 | approximate; recorded, unverified | sqlitevec, vespa, opensearch, milvus |  |
| milvus | 405-409 | 405-409 | 407 | approximate; recorded, unverified | sqlitevec, vespa, opensearch, chroma |  |
| duckdb | 269-270 | 269-270 | 270 | approximate; measured | sql-diskann, sql, typesense |  |
| sql-diskann | 263-271 | 263-271 | 268 | approximate; measured | duckdb, sql, typesense, clickhouse |  |
| sql | 241-243 | 241-243 | 241 | exact; measured | duckdb, sql-diskann, typesense, clickhouse |  |
| typesense | 235-236 | 235-236 | 235 | approximate; measured | duckdb, sql-diskann, sql, clickhouse |  |
| clickhouse | 192-196 | 192-196 | 193 | approximate; measured | sql-diskann, sql, typesense, weaviate |  |
| weaviate | 165-167 | 165-167 | 166 | approximate; recorded, unverified | clickhouse |  |

redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database', and its compose file sets save "300 1" and appendonly no.

## QPS@8

QPS@8 is searches per second with 8 searchers at once.

| engine | v7 min-max | v8 min-max | median of 6 | search | not separated from | flags |
|---|---|---|---|---|---|---|
| mariadb | 5911-5920 | 5911-5920 | 5918 | approximate; measured | redis |  |
| redis | 5038-5120 | 5038-5120 | 5067 | approximate; recorded, documented | mariadb, pgvector |  |
| pgvector | 3910-4001 | 3910-4001 | 3934 | approximate; measured | redis, qdrant-hnsw, qdrant |  |
| qdrant-hnsw | 3440-3452 | 3440-3452 | 3448 | approximate; measured | pgvector, qdrant, oracle, elasticsearch |  |
| qdrant | 3274-3287 | 3274-3287 | 3280 | exact; measured | pgvector, qdrant-hnsw, oracle, elasticsearch |  |
| oracle | 2655-2803 | 2655-2803 | 2802 | approximate; measured | qdrant-hnsw, qdrant, elasticsearch, mongodb |  |
| elasticsearch | 2669-2708 | 2669-2708 | 2671 | exact; measured | qdrant-hnsw, qdrant, oracle, mongodb | index-not-ready |
| mongodb | 2066-2352 | 2066-2352 | 2115 | approximate; recorded (engine's own report) | oracle, elasticsearch, vespa | segment-layout-differs |
| vespa | 1506-1565 | 1506-1565 | 1526 | approximate; measured | mongodb, opensearch, milvus, sqlitevec |  |
| opensearch | 1346-1516 | 1346-1516 | 1477 | approximate; measured | vespa, milvus, sqlitevec |  |
| milvus | 1255-1263 | 1255-1263 | 1258 | approximate; recorded, unverified | vespa, opensearch, sqlitevec |  |
| sqlitevec | 1136-1142 | 1136-1142 | 1138 | exact; measured | vespa, opensearch, milvus |  |
| chroma | 777-777 | 777-777 | 777 | approximate; recorded, unverified | sql-diskann, sql, duckdb, typesense |  |
| sql-diskann | 645-691 | 645-691 | 682 | approximate; measured | chroma, sql, duckdb, typesense |  |
| sql | 602-614 | 602-614 | 612 | exact; measured | chroma, sql-diskann, duckdb, typesense |  |
| duckdb | 576-576 | 576-576 | 576 | approximate; measured | chroma, sql-diskann, sql, typesense, clickhouse |  |
| typesense | 574-576 | 574-576 | 575 | approximate; measured | chroma, sql-diskann, sql, duckdb, clickhouse |  |
| clickhouse | 375-428 | 375-428 | 402 | approximate; measured | duckdb, typesense, weaviate |  |
| weaviate | 305-305 | 305-305 | 305 | approximate; recorded, unverified | clickhouse |  |

redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database', and its compose file sets save "300 1" and appendonly no.

## exact p50

Exact p50 is the median latency of each engine's own exact mode, which is a different operation per engine; the why table's CPU figures come from the other passes.

| engine | v7 min-max | v8 min-max | median of 6 | not separated from | flags |
|---|---|---|---|---|---|
| redis | 0.377-0.382 | 0.377-0.382 | 0.378 |  |  |
| qdrant-hnsw | 0.891-0.895 | 0.891-0.895 | 0.893 |  |  |
| mongodb | 1.242-1.263 | 1.242-1.263 | 1.242 | elasticsearch, mariadb | busy-box, segment-layout-differs |
| elasticsearch | 1.261-1.283 | 1.261-1.283 | 1.278 | mongodb, mariadb | index-not-ready |
| mariadb | 1.635-1.645 | 1.635-1.645 | 1.639 | mongodb, elasticsearch, sqlitevec, vespa, pgvector |  |
| sqlitevec | 1.906-1.936 | 1.906-1.936 | 1.914 | mariadb, vespa, pgvector, oracle |  |
| vespa | 1.935-1.963 | 1.935-1.963 | 1.941 | mariadb, sqlitevec, pgvector, oracle, opensearch |  |
| pgvector | 2.210-2.257 | 2.210-2.257 | 2.252 | mariadb, sqlitevec, vespa, oracle, opensearch |  |
| oracle | 2.484-2.503 | 2.484-2.503 | 2.490 | sqlitevec, vespa, pgvector, opensearch |  |
| opensearch | 2.615-2.750 | 2.615-2.750 | 2.688 | vespa, pgvector, oracle |  |
| sql-diskann | 3.740-3.790 | 3.740-3.790 | 3.749 | duckdb |  |
| duckdb | 4.195-4.214 | 4.195-4.214 | 4.200 | sql-diskann, typesense |  |
| typesense | 5.660-5.682 | 5.660-5.682 | 5.668 | duckdb, clickhouse |  |
| clickhouse | 6.657-6.677 | 6.657-6.677 | 6.674 | typesense |  |

sql has no exact pass; its search fact says: no index used, exact scan by design.

qdrant has no exact pass; its search fact says: no index used, exact scan by design.

milvus has no exact pass in these runs.

weaviate has no exact pass in these runs.

chroma has no exact pass in these runs.

redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database', and its compose file sets save "300 1" and appendonly no.

Run 20261006-130619-eshoponweb: busy box during the exact pass, with 0.307 CPUs of outside load.

Run 20261006-142724-eshoponweb: busy box during the exact pass, with 0.317 CPUs of outside load.

Run 20261006-154837-eshoponweb: busy box during the exact pass, with 0.301 CPUs of outside load.

Run 20261007-130619-eshoponweb: busy box during the exact pass, with 0.307 CPUs of outside load.

Run 20261007-142724-eshoponweb: busy box during the exact pass, with 0.317 CPUs of outside load.

Run 20261007-154837-eshoponweb: busy box during the exact pass, with 0.301 CPUs of outside load.

## Recall

Recall counts hits of the exact top 10 over 20 queries, 200 per run; it is printed, never ranked.

clickhouse's recall hits differ between runs: v7 199, 193, 198; v8 199, 193, 198.

| engine | hits per run | of | differs |
|---|---|---|---|
| clickhouse | v7: 199, 193, 198; v8: 199, 193, 198 | 200 | yes |
| vespa | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| oracle | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| elasticsearch | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| sql | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| pgvector | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| sqlitevec | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| qdrant | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| opensearch | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| qdrant-hnsw | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| redis | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| milvus | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| mariadb | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| weaviate | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| mongodb | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| chroma | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| typesense | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |
| sql-diskann | v7: 193, 193, 193; v8: 193, 193, 193 | 200 | no |
| duckdb | v7: 200, 200, 200; v8: 200, 200, 200 | 200 | no |

## Why some engines beat others: facts and measured costs

CPU per search is cost summed over every thread.

It can exceed the time per search, so it is not a split of the latency.

This test did not isolate causes.

Costs are medians over the 3 runs of v8, and a cost is left blank unless every one of them recorded it.

At one searcher, client CPU per search ran from 0.311 ms (mongodb) to 5.023 ms (duckdb).

The runs of v8 recorded no engine CPU per search.

Hosting embedded is recorded for sqlitevec and duckdb: each runs inside the test's own process, so its client CPU per search includes the engine's own work.

redis recorded these search and index settings: EF_CONSTRUCTION=128, EF_RUNTIME=100, M=16.

mariadb recorded these search and index settings: DISTANCE=cosine, M=16, mhnsw_ef_search=100.

mariadb: the clause of its recorded index text that states how hard a query searches, with how each part is backed.

Read back in these runs from the engine or the machine:

> mhnsw_ef_search=100 per statement

Typed in the program's own text; not read back, measured or backed by a saved source:

> (the ef 100 most engines here use, so the search effort matches;

Backed by a saved log or measurement, not read back from the engine in these runs:

> MariaDB's own default is 20;

A clause of the mariadb text is not printed: it cites figures that no saved source backs.

In the saved re-run of the mariadb effort test, recall@10 at ef 100 on 524 random 1024-dim vectors read 0.988 to 0.996.

In the saved re-run of the mariadb effort test, recall@10 at ef 100 on 2000 random 1024-dim vectors read 0.894 to 0.932.

pgvector recorded these search and index settings: ef_construction=128, hnsw.ef_search=100, m=16.

qdrant recorded these search and index settings: exact=true.

qdrant-hnsw recorded these search and index settings: ef_construct=100, hnsw_ef=server, m=16.

qdrant-hnsw: the clause of its recorded index text that states how hard a query searches, with how each part is backed.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> hnsw_ef=server default

oracle recorded these search and index settings: EFCONSTRUCTION=128, EFSEARCH=100, NEIGHBORS=16.

elasticsearch recorded these search and index settings: ef_construction=128, k=top, m=16, num_candidates=100.

mongodb recorded these search and index settings: maxEdges=16, numCandidates=20x, numEdgeCandidates=128.

mongodb: the clause of its recorded index text that states how hard a query searches, with how each part is backed.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> numCandidates=20x hits (min 100)

sqlitevec recorded these search and index settings: chunk_size=1024.

vespa recorded these search and index settings: ef=100, max-links-per-node=16, neighbors-to-explore-at-insert=128, targetHits=top.

opensearch recorded these search and index settings: approximate_threshold=0, ef_construction=128, ef_search=100, k=top, m=16.

chroma recorded these search and index settings: M=16, ef_construction=128, ef_search=100.

chroma: the clause of its recorded index text that states how hard a query searches, with how each part is backed.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> ef_search=100

Typed in the program's own text; not read back, measured or backed by a saved source:

> (Chroma default)

milvus recorded these search and index settings: M=16, ef=100, efConstruction=128.

duckdb recorded these search and index settings: checkpoint_threshold=256MB, ef_construction=128, ef_search=100, hnsw_enable_experimental_persistence=true, m=16, metric=cosine.

sql-diskann recorded these search and index settings: L=48, M=8, R=48, StartId=306.

sql-diskann recorded no setting for how hard a query searches.

sql recorded these search and index settings: description=exact VECTOR_DISTANCE cosine, no vector index (full scan).

typesense recorded these search and index settings: ef=100, ef_construction=128, k=top, m=16.

clickhouse recorded these search and index settings: M=16, ef_construction=128, hnsw_candidate_list_size_for_search=256.

weaviate recorded these search and index settings: ef=-1, efConstruction=128.

weaviate: the clause of its recorded index text that states how hard a query searches, with how each part is backed.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> ef=-1

Documented on a saved page, not read back from the engine:

> (dynamic: limit x 8 clamped 100..500)

| engine | kind | fact | confidence | class | source |
|---|---|---|---|---|---|
| redis | index | HNSW TYPE FLOAT32 M=16 EF_CONSTRUCTION=128, EF_RUNTIME=100 | recorded, documented | documented | in all 6 run(s) selected, at targets[redis].index |
| redis | search (approximate) | HNSW, EF_RUNTIME=100 per query | recorded, documented | documented | in all 6 run(s) selected, at targets[redis].index |
| redis | storage | Redis is an in-memory but persistent on disk database | documented | documented | design/engine-docs/redis-faq-2026-10-07.html, inside one text node |
| redis | protocol | StackExchange.Redis | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/RedisSink.cs:12 |
| redis | set-by-setup | save "300 1" | set-by-code | documented | deploy/engines/redis.compose.yaml:18 |
| redis | set-by-setup | appendonly no | set-by-code | documented | deploy/engines/redis.compose.yaml:18 |
| redis | set-by-setup | mem_limit: 12g | set-by-code | documented | deploy/engines/redis.compose.yaml:23 |
| mariadb | index | VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16 | recorded, unverified | unverified | in all 6 run(s) selected, at targets[mariadb].index |
| mariadb | search (approximate) | EXPLAIN of the search uses key vec_idx | measured | read-back | in all 6 run(s) selected, at targets[mariadb].load.indexNote |
| mariadb | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[mariadb] |
| mariadb | protocol | MySqlConnector | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/MariaDbSink.cs:10 |
| mariadb | set-by-setup | innodb-buffer-pool-size=2G | set-by-code | documented | deploy/engines/mariadb-bench.compose.yaml:47 |
| mariadb | set-by-setup | mhnsw-max-cache-size=4G | set-by-code | documented | deploy/engines/mariadb-bench.compose.yaml:48 |
| mariadb | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/mariadb-bench.compose.yaml:56 |
| pgvector | index | HNSW vector_cosine_ops m=16 ef_construction=128 | recorded, read-back | read-back | in all 6 run(s) selected, at targets[pgvector].index |
| pgvector | search (approximate) | EXPLAIN of the search uses Index Scan | measured | read-back | in all 6 run(s) selected, at targets[pgvector].load.indexNote |
| pgvector | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[pgvector] |
| pgvector | protocol | Npgsql | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs:9 |
| pgvector | set-by-setup | shared_buffers=2GB | set-by-code | documented | deploy/engines/pgvector.compose.yaml:23 |
| pgvector | set-by-setup | SET LOCAL enable_seqscan = off; SET LOCAL jit = off | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs:84 |
| pgvector | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/pgvector.compose.yaml:33 |
| qdrant | index | exact scan, sink sends exact=true on every search | recorded, documented | documented | in all 6 run(s) selected, at targets[qdrant].index |
| qdrant | search (exact) | no index used, exact scan by design | measured | read-back | in all 6 run(s) selected, at targets[qdrant].indexState.afterLoad.detail |
| qdrant | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[qdrant] |
| qdrant | protocol | Qdrant.Client.Grpc | set-by-code | documented | src/GenericVectorBuilder.Core/Sinks/QdrantSink.cs:3 |
| qdrant | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/qdrant.compose.yaml:35 |
| qdrant-hnsw | index | HNSW m=16 ef_construct=100 | recorded, documented | documented | in all 6 run(s) selected, at targets[qdrant-hnsw].index |
| qdrant-hnsw | search (approximate) | segment searches walked the graph and none scanned | measured | read-back | in all 6 run(s) selected, at targets[qdrant-hnsw].indexState.afterSearch.detail |
| qdrant-hnsw | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[qdrant-hnsw] |
| qdrant-hnsw | protocol | Qdrant.Client.Grpc | set-by-code | documented | src/GenericVectorBuilder.Bench/Targets/QdrantHnswSink.cs:5 |
| qdrant-hnsw | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/qdrant.compose.yaml:35 |
| oracle | index | HNSW in-memory neighbor graph NEIGHBORS=16 EFCONSTRUCTION=128 | recorded, documented | documented | in all 6 run(s) selected, at targets[oracle].index |
| oracle | search (approximate) | plan of the search: VECTOR INDEX HNSW SCAN | measured | read-back | in all 6 run(s) selected, at targets[oracle].load.indexNote |
| oracle | storage | HNSW in-memory neighbor graph | recorded, documented | documented | in all 6 run(s) selected, at targets[oracle].index |
| oracle | protocol | Oracle.ManagedDataAccess.Client | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/OracleSink.cs:8 |
| oracle | cap | Oracle Free caps itself at 2 CPUs | recorded, read-back | read-back | in all 6 run(s) selected, at targets[oracle].index |
| oracle | set-by-setup | POOL_SIZE=768M | set-by-code | documented | deploy/engines/oracle-init/01-vector-memory.sh:13 |
| oracle | set-by-setup | mem_limit: 6g | set-by-code | documented | deploy/engines/oracle.compose.yaml:45 |
| elasticsearch | index | total_vex_size_bytes 0, index_options hnsw m=16 ef_construction=128 | measured | read-back | in all 6 run(s) selected, at targets[elasticsearch].load.indexNote |
| elasticsearch | search (exact) | no HNSW graph, searches scan all 524 vectors | measured | read-back | in all 6 run(s) selected, at targets[elasticsearch].load.indexNote |
| elasticsearch | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[elasticsearch] |
| elasticsearch | protocol | HttpClient | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/ElasticsearchRest.cs:70 |
| elasticsearch | set-by-setup | -Xms2g -Xmx2g | set-by-code | documented | deploy/engines/elasticsearch.compose.yaml:14 |
| elasticsearch | set-by-setup | mem_limit: 6g | set-by-code | documented | deploy/engines/elasticsearch.compose.yaml:19 |
| mongodb | index | vectorSearch index, HNSW maxEdges=16 numEdgeCandidates=128 | recorded, documented | documented | in all 6 run(s) selected, at targets[mongodb].index |
| mongodb | search (approximate) | searched through the HNSW graph (Approximate) | recorded (engine's own report) | read-back | in all 6 run(s) selected, at targets[mongodb].load.indexNote |
| mongodb | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[mongodb] |
| mongodb | protocol | MongoDB.Driver | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/MongoDbSink.cs:7 |
| mongodb | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/mongodb.compose.yaml:21 |
| sqlitevec | index | vec0 brute-force scan, no ANN index (exact) | recorded, read-back | read-back | in all 6 run(s) selected, at targets[sqlitevec].index |
| sqlitevec | search (exact) | no index, exact scan by design | measured | read-back | in all 6 run(s) selected, at targets[sqlitevec].indexState.afterLoad.detail |
| sqlitevec | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[sqlitevec] |
| sqlitevec | protocol | Microsoft.Data.Sqlite | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs:9 |
| sqlitevec | set-by-setup | PRAGMA journal_mode=WAL | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs:528 |
| sqlitevec | set-by-setup | PRAGMA synchronous=NORMAL | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs:529 |
| vespa | index | HNSW float32 tensor, prenormalized-angular, max-links-per-node=16 | recorded, documented | documented | in all 6 run(s) selected, at targets[vespa].index |
| vespa | search (approximate) | nearestNeighbor approximate:true | measured | read-back | in all 6 run(s) selected, at targets[vespa].load.indexNote |
| vespa | storage | vectors held in memory | recorded, unverified | unverified | in all 6 run(s) selected, at targets[vespa].index |
| vespa | protocol | HttpClient | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/VespaRest.cs:44 |
| vespa | set-by-setup | mem_limit: 6g | set-by-code | documented | deploy/engines/vespa.compose.yaml:20 |
| opensearch | index | faiss HNSW float32, no compression, m=16, ef_construction=128, cosinesimil | recorded, documented | documented | in all 6 run(s) selected, at targets[opensearch].index |
| opensearch | search (approximate) | every segment searched through its HNSW graph | measured | read-back | in all 6 run(s) selected, at targets[opensearch].load.indexNote |
| opensearch | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[opensearch] |
| opensearch | protocol | HttpClient | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/OpenSearchRest.cs:70 |
| opensearch | set-by-setup | -Xms2g -Xmx2g | set-by-code | documented | deploy/engines/opensearch.compose.yaml:16 |
| opensearch | set-by-setup | mem_limit: 6g | set-by-code | documented | deploy/engines/opensearch.compose.yaml:21 |
| chroma | index | HNSW M=16 ef_construction=128, ef_search=100 | recorded, documented | documented | in all 6 run(s) selected, at targets[chroma].index |
| chroma | search (approximate) | approximate, no exact mode | recorded, unverified | unverified | in all 6 run(s) selected, at targets[chroma].index |
| chroma | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[chroma] |
| chroma | protocol | HttpClient | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/ChromaSink.cs:73 |
| chroma | set-by-setup | mem_limit: 6g | set-by-code | documented | deploy/engines/chroma.compose.yaml:20 |
| milvus | index | HNSW M=16 efConstruction=128, ef=100, metric COSINE | recorded, documented | documented | in all 6 run(s) selected, at targets[milvus].index |
| milvus | search (approximate) | approximate, no exact mode | recorded, unverified | unverified | in all 6 run(s) selected, at targets[milvus].index |
| milvus | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[milvus] |
| milvus | protocol | HttpClient | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/MilvusSink.cs:108 |
| milvus | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/milvus.compose.yaml:68 |
| duckdb | index | HNSW (vss extension) FLOAT[n] metric=cosine m=16 ef_construction=128 | recorded, documented | documented | in all 6 run(s) selected, at targets[duckdb].index |
| duckdb | search (approximate) | EXPLAIN of the search shows HNSW_INDEX_SCAN | measured | read-back | in all 6 run(s) selected, at targets[duckdb].load.indexNote |
| duckdb | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[duckdb] |
| duckdb | protocol | DuckDB.NET.Data | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs:7 |
| duckdb | set-by-setup | SET hnsw_enable_experimental_persistence = true | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs:750 |
| duckdb | set-by-setup | CheckpointThreshold 256MB | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/DuckDbSinkOptions.cs:49 |
| duckdb | set-by-setup | MemoryLimit 8GB | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/DuckDbSinkOptions.cs:38 |
| sql-diskann | index | DiskANN (preview) via VECTOR_SEARCH, cosine | recorded, unverified | unverified | in all 6 run(s) selected, at targets[sql-diskann].index |
| sql-diskann | search (approximate) | DiskANN index built and used | measured | read-back | in all 6 run(s) selected, at targets[sql-diskann].indexState.afterLoad.detail |
| sql-diskann | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[sql-diskann] |
| sql-diskann | protocol | Microsoft.Data.SqlClient | set-by-code | documented | src/GenericVectorBuilder.Bench/Targets/SqlDiskAnnSink.cs:5 |
| sql-diskann | protocol | vector as NVarChar value from ToJson | set-by-code | documented | src/GenericVectorBuilder.Bench/Targets/SqlDiskAnnSink.cs:305 |
| sql-diskann | set-by-setup | MSSQL_MEMORY_LIMIT_MB: "6144" | set-by-code | documented | deploy/engines/mssql.compose.yaml:49 |
| sql-diskann | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/mssql.compose.yaml:56 |
| sql | index | exact VECTOR_DISTANCE cosine, no vector index (full scan) | recorded, documented | documented | in all 6 run(s) selected, at targets[sql].index |
| sql | search (exact) | no index used, exact scan by design | measured | read-back | in all 6 run(s) selected, at targets[sql].indexState.afterLoad.detail |
| sql | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[sql] |
| sql | protocol | Microsoft.Data.SqlClient | set-by-code | documented | src/GenericVectorBuilder.Core/Sinks/SqlVectorSink.cs:7 |
| sql | protocol | vector as NVarChar value from ToJson | set-by-code | documented | src/GenericVectorBuilder.Core/Sinks/SqlVectorSink.cs:155 |
| sql | protocol | CAST( @q AS VECTOR(dimension) ) | set-by-code | documented | src/GenericVectorBuilder.Core/Sinks/SqlVectorSink.cs:149 |
| sql | set-by-setup | MSSQL_MEMORY_LIMIT_MB: "6144" | set-by-code | documented | deploy/engines/mssql.compose.yaml:49 |
| sql | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/mssql.compose.yaml:56 |
| typesense | index | HNSW float32 (hnswlib), m=16, ef_construction=128 | recorded, documented | documented | in all 6 run(s) selected, at targets[typesense].index |
| typesense | search (approximate) | HNSW graph returns 524 of 524 vectors | measured | read-back | in all 6 run(s) selected, at targets[typesense].load.indexNote |
| typesense | storage | index held in memory | recorded, unverified | unverified | in all 6 run(s) selected, at targets[typesense].index |
| typesense | protocol | HttpClient | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/TypesenseRest.cs:69 |
| typesense | protocol | vector as string vectorQuery | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/TypesenseSink.cs:249 |
| typesense | set-by-setup | mem_limit: 6g | set-by-code | documented | deploy/engines/typesense.compose.yaml:24 |
| clickhouse | index | vector_similarity HNSW cosineDistance, quantization bf16, M=16 | recorded, documented | documented | in all 6 run(s) selected, at targets[clickhouse].index |
| clickhouse | search (approximate) | plan of the search uses the Skip index vec_idx | measured | read-back | in all 6 run(s) selected, at targets[clickhouse].load.indexNote |
| clickhouse | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[clickhouse] |
| clickhouse | protocol | HttpClient over http | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/ClickHouseSink.cs:117 |
| clickhouse | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/clickhouse.compose.yaml:36 |
| weaviate | index | HNSW maxConnections(M)=16 efConstruction=128 | recorded, documented | documented | in all 6 run(s) selected, at targets[weaviate].index |
| weaviate | search (approximate) | approximate, no exact mode | recorded, unverified | unverified | in all 6 run(s) selected, at targets[weaviate].index |
| weaviate | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[weaviate] |
| weaviate | protocol | HttpClient | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/WeaviateSink.cs:86 |
| weaviate | protocol | HttpMethod Post, v1 graphql | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/WeaviateSink.cs:542 |
| weaviate | set-by-setup | GOMEMLIMIT: 6GiB | set-by-code | documented | deploy/engines/weaviate.compose.yaml:23 |
| weaviate | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/weaviate.compose.yaml:29 |

| engine | engine CPU ms/search @1 | @8 | client CPU ms/search @1 | @8 | engine CPUs busy @8 |
|---|---|---|---|---|---|
| redis |  |  | 0.569 | 0.366 |  |
| mariadb |  |  | 0.482 | 0.376 |  |
| pgvector |  |  | 0.687 | 0.538 |  |
| qdrant |  |  | 0.729 | 0.527 |  |
| qdrant-hnsw |  |  | 0.730 | 0.512 |  |
| oracle |  |  | 0.886 | 0.643 |  |
| elasticsearch |  |  | 0.458 | 0.491 |  |
| mongodb |  |  | 0.311 | 0.288 |  |
| sqlitevec |  |  | 2.067 | 3.502 |  |
| vespa |  |  | 0.938 | 0.876 |  |
| opensearch |  |  | 0.465 | 0.510 |  |
| chroma |  |  | 0.476 | 0.472 |  |
| milvus |  |  | 0.569 | 0.636 |  |
| duckdb |  |  | 5.023 | 6.902 |  |
| sql-diskann |  |  | 1.024 | 1.095 |  |
| sql |  |  | 1.219 | 1.192 |  |
| typesense |  |  | 0.521 | 0.592 |  |
| clickhouse |  |  | 0.878 | 1.057 |  |
| weaviate |  |  | 0.595 | 0.621 |  |

## Drift between the sessions

From v7 to v8, a ranked engine's session median moved 0% in the middle case and 0% at most (clickhouse, exact p50).

0 orders held in v7 and not in v8, and 0 the other way; they are listed and not published.

22 unordered pairs sat between 1.25 and 1.35 times in each session, always in one direction; they are listed as close to the line.

4 ordered pairs cleared 1.35 times by 25 bp or less in their lowest session; they are listed as on the line.

Both sessions ran with the clock held; drift under other conditions is not measured here.

| unconfirmed order | ahead | behind | held in |
|---|---|---|---|

| close to the line | ahead | behind | lowest ratio, bp |
|---|---|---|---|
| p50 | sql-diskann | clickhouse | 13376 |
| p50 | duckdb | clickhouse | 13435 |
| p50 | sql | weaviate | 13199 |
| p50 | typesense | weaviate | 12751 |
| QPS@1 | sql-diskann | clickhouse | 13451 |
| QPS@1 | mongodb | sqlitevec | 13437 |
| QPS@8 | typesense | clickhouse | 13397 |
| QPS@8 | duckdb | clickhouse | 13442 |
| QPS@8 | vespa | sqlitevec | 13185 |
| QPS@8 | mongodb | vespa | 13203 |
| QPS@8 | qdrant-hnsw | elasticsearch | 12703 |
| QPS@8 | chroma | sql | 12663 |
| QPS@8 | redis | pgvector | 12590 |
| QPS@8 | chroma | typesense | 13487 |
| QPS@8 | chroma | duckdb | 13488 |
| exact p50 | vespa | oracle | 12652 |
| exact p50 | vespa | opensearch | 13320 |
| exact p50 | sqlitevec | oracle | 12828 |
| exact p50 | elasticsearch | mariadb | 12738 |
| exact p50 | mariadb | pgvector | 13436 |
| exact p50 | mongodb | mariadb | 12942 |
| exact p50 | duckdb | typesense | 13430 |

| on the line | ahead | behind | lowest ratio, bp |
|---|---|---|---|
| p50 | oracle | mongodb | 13508 |
| p50 | mongodb | sqlitevec | 13513 |
| QPS@1 | oracle | mongodb | 13525 |
| exact p50 | sqlitevec | opensearch | 13504 |

## Disclosures

In v7, every run held the clock: turbo off, MSR 0x620 at 0x1e1e, every CPU at 3500 MHz; the ceiling before was 3600 MHz, so absolute figures are for the held clock.

The median rule: a pass is off when the median, over its engine CPUs or over its client CPUs, of each CPU's median MHz is more than 1% from the pinned 3500 MHz.

In v7, by the median rule, 0 of 156 passes were off the pinned clock and 0 had a CPU group not read.

In v8, every run held the clock: turbo off, MSR 0x620 at 0x1e1e, every CPU at 3500 MHz; the ceiling before was 3600 MHz, so absolute figures are for the held clock.

In v8, by the median rule, 0 of 156 passes were off the pinned clock and 0 had a CPU group not read.

Run 20261006-130619-eshoponweb flagged oracle's eight-searcher pass as off its clock by a mean; the medians read 3492 MHz on engine CPUs and 3492 MHz on client CPUs, within 1%.

Run 20261006-142724-eshoponweb flagged oracle's eight-searcher pass as off its clock by a mean; the medians read 3492 MHz on engine CPUs and 3492 MHz on client CPUs, within 1%.

Run 20261006-154837-eshoponweb flagged oracle's eight-searcher pass as off its clock by a mean; the medians read 3492 MHz on engine CPUs and 3492 MHz on client CPUs, within 1%.

Run 20261007-130619-eshoponweb flagged oracle's eight-searcher pass as off its clock by a mean; the medians read 3492 MHz on engine CPUs and 3492 MHz on client CPUs, within 1%.

Run 20261007-142724-eshoponweb flagged oracle's eight-searcher pass as off its clock by a mean; the medians read 3492 MHz on engine CPUs and 3492 MHz on client CPUs, within 1%.

Run 20261007-154837-eshoponweb flagged oracle's eight-searcher pass as off its clock by a mean; the medians read 3492 MHz on engine CPUs and 3492 MHz on client CPUs, within 1%.

Every claim run used the performance governor, with the client on CPUs 0-1,4-5.

The container engines clickhouse, vespa, oracle, elasticsearch, sql, pgvector, qdrant, opensearch, qdrant-hnsw, redis, milvus, mariadb, weaviate, mongodb, chroma, typesense and sql-diskann ran on CPUs 2-3,6-7.

The embedded engines sqlitevec and duckdb ran inside the client process on CPUs 0-1,4-5.

redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database'; its compose file sets save "300 1" and appendonly no.

Each engine's durability text, as run 20261007-130619-eshoponweb recorded it, follows clause by clause with how each clause is backed; the program wrote the text, and its figures were not re-derived for this report.

Strace logs of measurements cited in those texts are saved for clickhouse, milvus, mongodb, qdrant and qdrant-hnsw, in design/engine-docs/durability-logs-2026-10-04, each listed with its hash in SHA256SUMS.

No log is saved for the measurements cited in the durability texts of oracle, pgvector, sqlitevec, redis, weaviate, chroma, typesense and duckdb.

clickhouse: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Acknowledged inserts are not fsynced. The sink creates plain MergeTree tables, so the MergeTree settings are the defaults: fsync_after_insert 0 and fsync_part_directory 0 (system.merge_tree_settings), and async_insert 1 with wait_for_async_insert 1 is the server default for the gvb login (system.settings), so an insert is acknowledged once its part is written, not once it is on disk.

> Measured 2026-10-04 with strace over 30 acknowledged single-row inserts (30 Ok rows in system.asynchronous_insert_log):

Backed by a saved log or measurement, not read back from the engine in these runs:

> zero fsync, fdatasync or sync_file_range calls,

> while the control, a table created with fsync_after_insert=1, made 61 fdatasync calls

Typed in the program's own text; not read back, measured or backed by a saved source:

> for 5 inserts.

> A host power loss or kernel crash can lose acknowledged rows that are still in the OS page cache; a ClickHouse process crash alone should not (inferred, not tested).

vespa: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Vespa's transaction log server fsyncs after each commit: searchlib.translogserver usefsync=true, and an operation is searchable when it is acknowledged: proton documentdb visibilitydelay=0. Neither is overridden by this sink or by the services.xml it deploys;

Described by a test saved in this repository, which these runs did not run:

> VespaReadinessTests reads both from the live config server.

Typed in the program's own text; not read back, measured or backed by a saved source:

> After a crash the node replays the transaction log over its last flushed data and rebuilds the in-memory HNSW graph. By those settings an acknowledged write survives a crash; this is read from the settings, not shown by pulling power.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> One node and min-redundancy 1,

Typed in the program's own text; not read back, measured or backed by a saved source:

> so a lost disk loses the data.

oracle: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> By these settings a committed row should survive a crash or power loss. The sink uses a plain COMMIT and commit_logging, commit_wait and commit_write are unset in the database, so every commit waits for its redo to be written.

> Measured 2026-10-04: 50 separate client commits raised V$SYSSTAT 'redo synch writes' by 55 (the 50 commits plus the CREATE and DROP of the probe table), and the log writer and the datafile writer hold their files open with O_DSYNC (open flags 02110002, filesystemio_options none).

> The database runs NOARCHIVELOG (V$DATABASE.LOG_MODE), so redo serves crash recovery only and there is no point-in-time restore.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> The HNSW graph lives in the 768 MB vector memory pool (oracle-init/01-vector-memory.sh)

Typed in the program's own text; not read back, measured or backed by a saved source:

> and is not the durable copy; the table is.

> Not tested by cutting power; whether the disk's own write cache reaches the media was not checked.

Read from the running engine or the machine by the benchmark's own code, as the cited line shows:

> CPU: Oracle Free caps itself at 2 CPUs (cpu_count 2 in V$PARAMETER, edition FREE in V$INSTANCE, 8 host CPUs in V$OSSTAT NUM_CPUS;

Typed in the program's own text; not read back, measured or backed by a saved source:

> the 2 CPU thread limit is Oracle's documented Free edition limit).

elasticsearch: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Every acknowledged bulk request is fsynced to the translog before the answer: index.translog.durability=request, the Elasticsearch default, which neither this sink nor elasticsearch.compose.yaml overrides

Described by a test saved in this repository, which these runs did not run:

> (ElasticsearchReadinessTests reads it back from the live index).

Typed in the program's own text; not read back, measured or backed by a saved source:

> By that setting a process crash or power loss loses no acknowledged write; this is read from the setting, not shown by pulling power.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> One node and no replicas,

Typed in the program's own text; not read back, measured or backed by a saved source:

> so a lost disk loses the data.

sql: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> A commit returns after its transaction-log records are written to disk (SQL Server write-ahead logging;

Read from the running engine or the machine by the benchmark's own code, as the cited line shows:

> delayed durability is DISABLED);

Typed in the program's own text; not read back, measured or backed by a saved source:

> a database created here copies the model database:

Read from the running engine or the machine by the benchmark's own code, as the cited line shows:

> recovery model FULL, page_verify CHECKSUM; no global trace flags are enabled;

> mssql.conf of container gvb-mssql (/var/opt/mssql/mssql.conf, on the host at /home/dan/gvb-data/engines/mssql/mssql.conf) does not exist, so it sets: nothing; it has no [control] or [traceflag] entry,

Typed in the program's own text; not read back, measured or backed by a saved source:

> so SQL Server's own Linux defaults for flushing writes apply (Microsoft's Linux performance guide names trace flag 3982 as that default; read from the guide, not tested here).

> Not tested by cutting power; whether the disk's own write cache reaches the media was not checked.

Read from the running engine or the machine by the benchmark's own code, as the cited line shows:

> Container settings from its environment (names only): MSSQL_AGENT_ENABLED, MSSQL_MEMORY_LIMIT_MB, MSSQL_PID, MSSQL_RPC_PORT, MSSQL_SA_PASSWORD.

pgvector: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> fsync on, synchronous_commit on, full_page_writes on, wal_sync_method fdatasync (PostgreSQL defaults;

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> pgvector.compose.yaml sets only shared_buffers, maintenance_work_mem and max_wal_size):

Typed in the program's own text; not read back, measured or backed by a saved source:

> every commit is flushed to the write-ahead log before it returns and HNSW index changes are WAL-logged, so a crash loses no committed row (max_wal_size 4GB only spaces out checkpoints; measured with docker kill, which is SIGKILL, right after a 2,000-vector load and a restart: all 2,000 rows were there and the HNSW index was valid and used by the default search)

sqlitevec: its recorded durability text, clause by clause, with how each clause is backed.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> PRAGMA journal_mode=WAL and PRAGMA synchronous=NORMAL (set by this sink when it opens the file):

Typed in the program's own text; not read back, measured or backed by a saved source:

> each commit is appended to the -wal file and handed to the operating system, but the WAL is fsynced only when SQLite checkpoints it (measured with strace: 40 single-record commits caused 3 WAL fsyncs), so a crash of the process loses nothing (measured with kill -9 and a reopen: all 50, 300 and 2,000 rows were there), while an operating-system crash or power cut can lose the newest commits since the last checkpoint and leaves the database consistent

qdrant: its recorded durability text, clause by clause, with how each clause is backed.

Backed by a saved log or measurement, not read back from the engine in these runs:

> What was measured (strace -f on the Qdrant server process, 30 single-point REST upserts per setting, every sync call timed against the request that caused it): with wait=true each upsert had exactly one msync(MS_SYNC) of the write-ahead-log segment inside the request, before the reply (30 of 30);

> with wait=false the 30 upserts were acknowledged within 0.26 s with no sync call, and the first WAL msync (one call covering all 30 records) came 2.7 s after the last reply, followed by the segment-file flushes.

Typed in the program's own text; not read back, measured or backed by a saved source:

> So the log is flushed to disk under both settings, but with wait=false the flush comes after the acknowledgement: an operating-system crash or power cut in that gap loses acknowledged writes, while a crash of the Qdrant process alone should not, because the bytes are already in the kernel's page cache (inferred, not tested).

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> Every upsert here is sent with wait=true in batches of 256 over gRPC;

Typed in the program's own text; not read back, measured or backed by a saved source:

> the strace used one point per request, so one flush per batch is inferred, not measured.

Read from the running engine or the machine by the benchmark's own code, as the cited line shows:

> Segment files are flushed every 5 s; log segments 32 MB, 0 created ahead. /qdrant/config/config.yaml in container gvb-qdrant (the image's own file; its storage keys are listed) sets: storage.collection.quantization = null, storage.collection.replication_factor = 1, storage.collection.vectors.on_disk = null, storage.collection.write_consistency_factor = 1, storage.hnsw_index.ef_construct = 100, storage.hnsw_index.full_scan_threshold_kb = 10000, storage.hnsw_index.m = 16, storage.hnsw_index.max_indexing_threads = 0, storage.hnsw_index.on_disk = false, storage.hnsw_index.payload_m = null, storage.max_collections = null, storage.node_type = Normal, storage.on_disk_payload = true, storage.optimizers.default_segment_number = 0, storage.optimizers.deleted_threshold = 0.2, storage.optimizers.flush_interval_sec = 5, storage.optimizers.indexing_threshold_kb = 10000, storage.optimizers.max_optimization_threads = null, storage.optimizers.max_segment_size_kb = null, storage.optimizers.vacuum_min_vector_number = 1000, storage.performance.max_search_threads = 0, storage.performance.optimizer_cpu_budget = 0, storage.performance.update_rate_limit = null, storage.shard_transfer_method = null, storage.snapshots_config.snapshots_storage = local, storage.snapshots_path = ./snapshots, storage.storage_path = ./storage, storage.temp_path = null, storage.update_concurrency = null, storage.wal.wal_capacity_mb = 32, storage.wal.wal_segments_ahead = 0; no QDRANT__ environment overrides in the server process.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Not tested by cutting power; whether the disk's own write cache reaches the media was not checked.

opensearch: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Every acknowledged bulk request is fsynced to the translog before the answer: index.translog.durability=request, the OpenSearch default, which neither this sink nor opensearch.compose.yaml overrides

Described by a test saved in this repository, which these runs did not run:

> (OpenSearchReadinessTests reads it back from the live index).

Typed in the program's own text; not read back, measured or backed by a saved source:

> By that setting a process crash or power loss loses no acknowledged write; this is read from the setting, not shown by pulling power.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> One node and no replicas,

Typed in the program's own text; not read back, measured or backed by a saved source:

> so a lost disk loses the data.

qdrant-hnsw: its recorded durability text, clause by clause, with how each clause is backed.

Backed by a saved log or measurement, not read back from the engine in these runs:

> What was measured (strace -f on the Qdrant server process, 30 single-point REST upserts per setting, every sync call timed against the request that caused it): with wait=true each upsert had exactly one msync(MS_SYNC) of the write-ahead-log segment inside the request, before the reply (30 of 30);

> with wait=false the 30 upserts were acknowledged within 0.26 s with no sync call, and the first WAL msync (one call covering all 30 records) came 2.7 s after the last reply, followed by the segment-file flushes.

Typed in the program's own text; not read back, measured or backed by a saved source:

> So the log is flushed to disk under both settings, but with wait=false the flush comes after the acknowledgement: an operating-system crash or power cut in that gap loses acknowledged writes, while a crash of the Qdrant process alone should not, because the bytes are already in the kernel's page cache (inferred, not tested).

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> Every upsert here is sent with wait=true in batches of 256 over gRPC;

Typed in the program's own text; not read back, measured or backed by a saved source:

> the strace used one point per request, so one flush per batch is inferred, not measured.

Read from the running engine or the machine by the benchmark's own code, as the cited line shows:

> Segment files are flushed every 5 s; log segments 32 MB, 0 created ahead. /qdrant/config/config.yaml in container gvb-qdrant (the image's own file; its storage keys are listed) sets: storage.collection.quantization = null, storage.collection.replication_factor = 1, storage.collection.vectors.on_disk = null, storage.collection.write_consistency_factor = 1, storage.hnsw_index.ef_construct = 100, storage.hnsw_index.full_scan_threshold_kb = 10000, storage.hnsw_index.m = 16, storage.hnsw_index.max_indexing_threads = 0, storage.hnsw_index.on_disk = false, storage.hnsw_index.payload_m = null, storage.max_collections = null, storage.node_type = Normal, storage.on_disk_payload = true, storage.optimizers.default_segment_number = 0, storage.optimizers.deleted_threshold = 0.2, storage.optimizers.flush_interval_sec = 5, storage.optimizers.indexing_threshold_kb = 10000, storage.optimizers.max_optimization_threads = null, storage.optimizers.max_segment_size_kb = null, storage.optimizers.vacuum_min_vector_number = 1000, storage.performance.max_search_threads = 0, storage.performance.optimizer_cpu_budget = 0, storage.performance.update_rate_limit = null, storage.shard_transfer_method = null, storage.snapshots_config.snapshots_storage = local, storage.snapshots_path = ./snapshots, storage.storage_path = ./storage, storage.temp_path = null, storage.update_concurrency = null, storage.wal.wal_capacity_mb = 32, storage.wal.wal_segments_ahead = 0; no QDRANT__ environment overrides in the server process.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Not tested by cutting power; whether the disk's own write cache reaches the media was not checked.

redis: its recorded durability text, clause by clause, with how each clause is backed.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> save "300 1" and appendonly no (redis.compose.yaml):

Typed in the program's own text; not read back, measured or backed by a saved source:

> an RDB snapshot is written every 5 minutes if at least one key changed, and on a clean stop; there is no append-only log, so a crash loses every write since the last snapshot (up to 5 minutes plus the time a snapshot takes; measured with docker kill, which is SIGKILL, right after a 2,000-vector load: none of the 2,000 hashes were there after the restart, and the restart loaded an older snapshot that still held keys of a collection that had been dropped since)

milvus: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Writes are not fsynced. Standalone uses the default message queue rocksmq (RocksDB, /var/lib/milvus/rdb_data, mq.type default) and flushed segments go to local-disk object storage

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> (COMMON_STORAGETYPE=local in milvus.compose.yaml).

Backed by a saved log or measurement, not read back from the engine in these runs:

> Measured 2026-10-04 with strace over 30 acknowledged single-row upserts and one flush: fdatasync ran only on the embedded etcd files (member/wal, member/snap/db), never on rdb_data or the segment files.

Typed in the program's own text; not read back, measured or backed by a saved source:

> A host power loss or kernel crash can lose acknowledged rows that are still in the OS page cache; a Milvus process crash alone should not (inferred, not tested). Metadata in embedded etcd is fdatasynced on every commit.

mariadb: its recorded durability text, clause by clause, with how each clause is backed.

Backed by a saved log or measurement, not read back from the engine in these runs:

> innodb_flush_log_at_trx_commit=2 (set in mariadb-bench.compose.yaml for the benchmark's own container gvbbench-mariadb, and in mariadb.compose.yaml for the daily gvb-mariadb, which has the same server settings):

Typed in the program's own text; not read back, measured or backed by a saved source:

> the InnoDB redo log is written to the operating system at every commit but fsynced about once a second, so a crash of the mariadbd process loses nothing, while an operating-system crash or power cut can lose the last second of commits;

Backed by a saved log or measurement, not read back from the engine in these runs:

> innodb_doublewrite is on

Typed in the program's own text; not read back, measured or backed by a saved source:

> (default),

Backed by a saved log or measurement, not read back from the engine in these runs:

> the binary log is off,

Typed in the program's own text; not read back, measured or backed by a saved source:

> and the vector graph is an InnoDB table under the same log

Backed by a saved log or measurement, not read back from the engine in these runs:

> (settings read from the running server with SHOW VARIABLES;

Typed in the program's own text; not read back, measured or backed by a saved source:

> the crash behaviour is InnoDB's documented behaviour for this setting and was not tested on either container)

weaviate: its recorded durability text, clause by clause, with how each clause is backed.

The recorded weaviate text says 'sets no persistence variable'; deploy/engines/weaviate.compose.yaml sets PERSISTENCE_DATA_PATH. That clause is not printed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> every object is appended to the LSM write-ahead log with a plain write and no fsync, and the log is fsynced only when its memtable is flushed, 60 seconds after the last write (PERSISTENCE_MEMTABLES_FLUSH_IDLE_AFTER_SECONDS default; measured with strace: 2,080 writes into the objects log during a 2,000-object load, the first fsync 60 s after the last write), so a power cut can lose the last minute of writes; the HNSW commit log is buffered inside the process (77 writes for 2,000 vectors), so a killed process loses the newest vectors from the vector index while their objects survive (measured with docker kill, which is SIGKILL, one second after the load and a restart, two runs each: 0 to 1 of 50, 264 to 267 of 300 and 1,992 to 1,997 of 2,000 stored objects were still reachable through a vector search)

mongodb: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Acknowledged writes are journaled before the acknowledgement. The sink sets no write concern, so the server default applies: getDefaultRWConcern gives w majority and the one-member replica set (--replSet gvbmongo in the image) has writeConcernMajorityJournalDefault true, with WiredTiger journaling on (journalCommitInterval 100 ms is only the interval for unacknowledged work).

> Measured 2026-10-04: 30 acknowledged single-document inserts raised WiredTiger 'log sync operations' by 30 and

Backed by a saved log or measurement, not read back from the engine in these runs:

> strace showed fdatasync on /data/db/journal/WiredTigerLog files.

Typed in the program's own text; not read back, measured or backed by a saved source:

> A crash loses no acknowledged write. mongot's search index is not part of that promise: it follows the collection asynchronously and is rebuilt from it.

chroma: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> SQLite rollback journal with its default synchronous=FULL under the data directory

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> (chroma.compose.yaml sets IS_PERSISTENT=1 and PERSIST_DIRECTORY=/data and no sync setting):

Typed in the program's own text; not read back, measured or backed by a saved source:

> every write is committed to chroma.sqlite3 with fsync of the journal, the directory and the database file before the call returns (strace: 15 database fsyncs and 38 journal fsyncs for one create, four 500-vector upserts and one delete), and the HNSW files are written every sync_threshold=1000 vectors and rebuilt from the SQLite log after a crash; measured with kill -9 and a restart: all 50, 300 and 2,000 vectors were still stored and searchable

typesense: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Every acknowledged write is appended to Typesense's raft log and fsynced before the answer: braft raft_sync=true with raft_sync_policy=0 (sync immediately), read from the running container's brpc /flags page on 2026-10-04. A restart replays the log from the last snapshot

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> (typesense.compose.yaml sets TYPESENSE_SNAPSHOT_INTERVAL_SECONDS=300)

Typed in the program's own text; not read back, measured or backed by a saved source:

> and rebuilds the in-memory HNSW graph, so by those settings a crash loses no acknowledged write, but the restart is slow after a big load. This is read from the settings, not shown by pulling power. One node and no replicas, so a lost disk loses the data.

sql-diskann: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> A commit returns after its transaction-log records are written to disk (SQL Server write-ahead logging;

Read from the running engine or the machine by the benchmark's own code, as the cited line shows:

> delayed durability is DISABLED);

Typed in the program's own text; not read back, measured or backed by a saved source:

> a database created here copies the model database:

Read from the running engine or the machine by the benchmark's own code, as the cited line shows:

> recovery model FULL, page_verify CHECKSUM; no global trace flags are enabled;

> mssql.conf of container gvb-mssql (/var/opt/mssql/mssql.conf, on the host at /home/dan/gvb-data/engines/mssql/mssql.conf) does not exist, so it sets: nothing; it has no [control] or [traceflag] entry,

Typed in the program's own text; not read back, measured or backed by a saved source:

> so SQL Server's own Linux defaults for flushing writes apply (Microsoft's Linux performance guide names trace flag 3982 as that default; read from the guide, not tested here).

> Not tested by cutting power; whether the disk's own write cache reaches the media was not checked.

Read from the running engine or the machine by the benchmark's own code, as the cited line shows:

> Container settings from its environment (names only): MSSQL_AGENT_ENABLED, MSSQL_MEMORY_LIMIT_MB, MSSQL_PID, MSSQL_RPC_PORT, MSSQL_SA_PASSWORD.

duckdb: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> DuckDB's write-ahead log is fsynced at every commit and no DuckDbSink setting changes that

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> (checkpoint_threshold=256MB

Typed in the program's own text; not read back, measured or backed by a saved source:

> only spaces out the checkpoints that write the database file; measured with strace: 42 WAL fsyncs for 40 single-record commits plus 2 setup statements), so an operating-system crash or power cut loses no committed row; measured with kill -9 right after the last commit and a reopen: all 50, 300 and 2,000 rows and an HNSW index that counted the same number were recovered from the log; not tested and documented by DuckDB: the HNSW index is file-backed only through hnsw_enable_experimental_persistence = true,

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> which this sink turns on,

Typed in the program's own text; not read back, measured or backed by a saved source:

> and WAL recovery for such custom indexes is not complete, so a crash during a checkpoint or a later commit can damage the index while the rows survive

qdrant's measured search fact says: no index used, exact scan by design.

oracle's recorded index text says: Oracle Free caps itself at 2 CPUs.

elasticsearch's measured search fact says: no HNSW graph, searches scan all 524 vectors.

sqlitevec's measured search fact says: no index, exact scan by design.

sql's measured search fact says: no index used, exact scan by design.

mongodb's search fact is the engine's own report; its segments held at most 278 vectors, below the 1,043 at which elasticsearch's recorded text says a segment gets a graph, and no graph was checked.

Segment layouts differed between runs for mongodb; each run's layout is in its row flags.

An observer process ran beside the v8 runs; its own CPU, at most 0.123 CPUs in a pass, counts as outside load, and its summary is observer/summary.json beside this report.

By APERF and MPERF, the observer's largest clock deviation in a pass was 0.63%, and it read MSR 0x620 as 0x1e1e.

The observer summary does not cover systemd timers for every v8 run.

An observer process ran beside the v7 runs; its own CPU, at most 0.123 CPUs in a pass, counts as outside load, and its summary is observer/summary-v7.json beside this report.

By APERF and MPERF, the observer's largest clock deviation in a pass was 0.63%, and it read MSR 0x620 as 0x1e1e.

The observer summary does not cover systemd timers for every v7 run.

CPU idle states were recorded and left as found: driver intel_idle, governor menu.

Outside load is busy CPU less this client's and the followed engine cgroups'; dockerd's cgroup is one of them for a compose engine, so its CPU counts as benchmark work, not outside load.

The runs of v7 did not record how dockerd's CPU was counted.

The runs of v8 did not record how dockerd's CPU was counted.

Every order here was measured at the search settings each run recorded for the engine, listed in the why section; this report makes no claim at other settings.

A route each run recorded in conditions.connections reads: container address on its Docker network, not the published port (no docker-proxy).

A route each run recorded in conditions.connections reads: in this process (embedded), no network.

| run | recorded warning | engine median MHz | client median MHz |
|---|---|---|---|
| 20261006-130619-eshoponweb | WARNING: clock off its pinned value during oracle default@8: the engine CPUs averaged 3121 MHz, -10.8% against the pinned 3500 MHz; the client CPUs averaged 3141 MHz, -10.3% against the pinned 3500 MHz (limit 1%); this pass is not comparable with passes at the pinned clock. | 3492 | 3492 |
| 20261006-142724-eshoponweb | WARNING: clock off its pinned value during oracle default@8: the engine CPUs averaged 3129 MHz, -10.6% against the pinned 3500 MHz; the client CPUs averaged 3230 MHz, -7.7% against the pinned 3500 MHz (limit 1%); this pass is not comparable with passes at the pinned clock. | 3492 | 3492 |
| 20261006-154837-eshoponweb | WARNING: clock off its pinned value during oracle default@8: the engine CPUs averaged 3168 MHz, -9.5% against the pinned 3500 MHz; the client CPUs averaged 3214 MHz, -8.2% against the pinned 3500 MHz (limit 1%); this pass is not comparable with passes at the pinned clock. | 3492 | 3492 |
| 20261007-130619-eshoponweb | WARNING: clock off its pinned value during oracle default@8: the engine CPUs averaged 3121 MHz, -10.8% against the pinned 3500 MHz; the client CPUs averaged 3141 MHz, -10.3% against the pinned 3500 MHz (limit 1%); this pass is not comparable with passes at the pinned clock. | 3492 | 3492 |
| 20261007-142724-eshoponweb | WARNING: clock off its pinned value during oracle default@8: the engine CPUs averaged 3129 MHz, -10.6% against the pinned 3500 MHz; the client CPUs averaged 3230 MHz, -7.7% against the pinned 3500 MHz (limit 1%); this pass is not comparable with passes at the pinned clock. | 3492 | 3492 |
| 20261007-154837-eshoponweb | WARNING: clock off its pinned value during oracle default@8: the engine CPUs averaged 3168 MHz, -9.5% against the pinned 3500 MHz; the client CPUs averaged 3214 MHz, -8.2% against the pinned 3500 MHz (limit 1%); this pass is not comparable with passes at the pinned clock. | 3492 | 3492 |

| text | clause | how it is backed | printed | basis |
|---|---|---|---|---|
| clickhouse durability | Acknowledged inserts are not fsynced. The sink creates plain MergeTree tables, so the MergeTree settings are the defaults: fsync_after_insert 0 and fsync_part_directory 0 (system.merge_tree_settings), and async_insert 1 with wait_for_async_insert 1 is the server default for the gvb login (system.settings), so an insert is acknowledged once its part is written, not once it is on disk. | unverified | yes |  |
| clickhouse durability | Measured 2026-10-04 with strace over 30 acknowledged single-row inserts (30 Ok rows in system.asynchronous_insert_log): | unverified | yes |  |
| clickhouse durability | zero fsync, fdatasync or sync_file_range calls, | documented | yes | design/engine-docs/durability-logs-2026-10-04/clickhouse-acknowledged-inserts.strace#SIGUSR1 |
| clickhouse durability | while the control, a table created with fsync_after_insert=1, made 61 fdatasync calls | documented | yes | design/engine-docs/durability-logs-2026-10-04/clickhouse-control-fsync-after-insert.strace#fdatasync |
| clickhouse durability | for 5 inserts. | unverified | yes |  |
| clickhouse durability | A host power loss or kernel crash can lose acknowledged rows that are still in the OS page cache; a ClickHouse process crash alone should not (inferred, not tested). | unverified | yes |  |
| clickhouse index | vector_similarity HNSW cosineDistance, quantization bf16, M=16 ef_construction=128, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/ClickHouseSink.cs#INDEX vec_idx embedding TYPE vector_similarity( 'hnsw', 'cosineDistance', {dimension}, '{_options.Quantization}', {_options.HnswM}, {_options.HnswEfConstruction} ); src/GenericVectorBuilder.Engines/Sinks/ClickHouseSinkOptions.cs#public string Quantization { get; set; } = "bf16";; src/GenericVectorBuilder.Engines/Sinks/ClickHouseSinkOptions.cs#public int HnswM { get; set; } = 16;; src/GenericVectorBuilder.Engines/Sinks/ClickHouseSinkOptions.cs#public int HnswEfConstruction { get; set; } = 128; |
| clickhouse index | hnsw_candidate_list_size_for_search=256, rescoring off; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/ClickHouseSink.cs#hnsw_candidate_list_size_for_search = {_options.HnswEfSearch}, vector_search_with_rescoring = {_options.Rescoring}; src/GenericVectorBuilder.Engines/Sinks/ClickHouseSinkOptions.cs#public int HnswEfSearch { get; set; } = 256;; src/GenericVectorBuilder.Engines/Sinks/ClickHouseSinkOptions.cs#public int Rescoring { get; set; } |
| clickhouse index | exact mode = full scan with skip indexes off | unverified | yes |  |
| vespa durability | Vespa's transaction log server fsyncs after each commit: searchlib.translogserver usefsync=true, and an operation is searchable when it is acknowledged: proton documentdb visibilitydelay=0. Neither is overridden by this sink or by the services.xml it deploys; | unverified | yes |  |
| vespa durability | VespaReadinessTests reads both from the live config server. | documented | yes | tests/GenericVectorBuilder.Engines.Tests/VespaReadinessTests.cs#usefsync |
| vespa durability | After a crash the node replays the transaction log over its last flushed data and rebuilds the in-memory HNSW graph. By those settings an acknowledged write survives a crash; this is read from the settings, not shown by pulling power. | unverified | yes |  |
| vespa durability | One node and min-redundancy 1, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/VespaApplicationPackage.cs#<min-redundancy>1</min-redundancy> |
| vespa durability | so a lost disk loses the data. | unverified | yes |  |
| vespa index | HNSW float32 tensor, prenormalized-angular (cosine), max-links-per-node=16, neighbors-to-explore-at-insert=128; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/VespaApplicationPackage.cs#type tensor<float>(x[{{dimension}}]); src/GenericVectorBuilder.Engines/Sinks/VespaApplicationPackage.cs#distance-metric: prenormalized-angular; src/GenericVectorBuilder.Engines/Sinks/VespaApplicationPackage.cs#max-links-per-node: 16; src/GenericVectorBuilder.Engines/Sinks/VespaApplicationPackage.cs#neighbors-to-explore-at-insert: 128 |
| vespa index | search targetHits=top, ef=100 via exploreAdditionalHits; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/VespaSink.cs#targetHits:{top},hnsw.exploreAdditionalHits:{extra}; src/GenericVectorBuilder.Engines/Sinks/VespaSinkOptions.cs#int EfSearch = 100 |
| vespa index | exact mode = approximate:false; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/VespaSink.cs#targetHits:{top},approximate:false |
| vespa index | vectors held in memory | unverified | yes |  |
| oracle durability | By these settings a committed row should survive a crash or power loss. The sink uses a plain COMMIT and commit_logging, commit_wait and commit_write are unset in the database, so every commit waits for its redo to be written. | unverified | yes |  |
| oracle durability | Measured 2026-10-04: 50 separate client commits raised V$SYSSTAT 'redo synch writes' by 55 (the 50 commits plus the CREATE and DROP of the probe table), and the log writer and the datafile writer hold their files open with O_DSYNC (open flags 02110002, filesystemio_options none). | unverified | yes |  |
| oracle durability | The database runs NOARCHIVELOG (V$DATABASE.LOG_MODE), so redo serves crash recovery only and there is no point-in-time restore. | unverified | yes |  |
| oracle durability | The HNSW graph lives in the 768 MB vector memory pool (oracle-init/01-vector-memory.sh) | documented | yes | deploy/engines/oracle-init/01-vector-memory.sh#POOL_SIZE=768M |
| oracle durability | and is not the durable copy; the table is. | unverified | yes |  |
| oracle durability | Not tested by cutting power; whether the disk's own write cache reaches the media was not checked. | unverified | yes |  |
| oracle durability | CPU: Oracle Free caps itself at 2 CPUs (cpu_count 2 in V$PARAMETER, edition FREE in V$INSTANCE, 8 host CPUs in V$OSSTAT NUM_CPUS; | read-back | yes | src/GenericVectorBuilder.Engines/Sinks/OracleCpuCap.cs#cpu_count {CpuCount.ToString( CultureInfo.InvariantCulture )} in V$PARAMETER |
| oracle durability | the 2 CPU thread limit is Oracle's documented Free edition limit). | unverified | yes |  |
| oracle index | HNSW in-memory neighbor graph NEIGHBORS=16 EFCONSTRUCTION=128, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/OracleSink.cs#ORGANIZATION INMEMORY NEIGHBOR GRAPH DISTANCE COSINE; src/GenericVectorBuilder.Engines/Sinks/OracleSink.cs#PARAMETERS ( TYPE HNSW, NEIGHBORS {_options.HnswNeighbors}, EFCONSTRUCTION {_options.HnswEfConstruction} ); src/GenericVectorBuilder.Engines/Sinks/OracleSinkOptions.cs#public int HnswNeighbors { get; set; } = 16;; src/GenericVectorBuilder.Engines/Sinks/OracleSinkOptions.cs#public int HnswEfConstruction { get; set; } = 128; |
| oracle index | EFSEARCH=100 per query, cosine; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/OracleSink.cs#APPROX FIRST :top ROWS ONLY WITH TARGET ACCURACY PARAMETERS ( EFSEARCH {Math.Max( _options.HnswEfSearch, top )} ); src/GenericVectorBuilder.Engines/Sinks/OracleSink.cs#VECTOR_DISTANCE( embedding, :query, COSINE ); src/GenericVectorBuilder.Engines/Sinks/OracleSinkOptions.cs#public int HnswEfSearch { get; set; } = 100; |
| oracle index | exact mode = FETCH EXACT FIRST (full scan); | documented | yes | src/GenericVectorBuilder.Engines/Sinks/OracleSink.cs#"EXACT FIRST :top ROWS ONLY" |
| oracle index | Oracle Free caps itself at 2 CPUs (cpu_count 2 in V$PARAMETER, edition FREE in V$INSTANCE, 8 host CPUs in V$OSSTAT NUM_CPUS; | read-back | yes | src/GenericVectorBuilder.Engines/Sinks/OracleCpuCap.cs#cpu_count {CpuCount.ToString( CultureInfo.InvariantCulture )} in V$PARAMETER |
| oracle index | the 2 CPU thread limit is Oracle's documented Free edition limit) | unverified | yes |  |
| elasticsearch durability | Every acknowledged bulk request is fsynced to the translog before the answer: index.translog.durability=request, the Elasticsearch default, which neither this sink nor elasticsearch.compose.yaml overrides | unverified | yes |  |
| elasticsearch durability | (ElasticsearchReadinessTests reads it back from the live index). | documented | yes | tests/GenericVectorBuilder.Engines.Tests/ElasticsearchReadinessTests.cs#index.translog.durability |
| elasticsearch durability | By that setting a process crash or power loss loses no acknowledged write; this is read from the setting, not shown by pulling power. | unverified | yes |  |
| elasticsearch durability | One node and no replicas, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/ElasticsearchSink.cs#settings = new { number_of_shards = 1, number_of_replicas = 0 } |
| elasticsearch durability | so a lost disk loses the data. | unverified | yes |  |
| elasticsearch index | HNSW float32, no quantization, m=16, ef_construction=128, | read-back | yes | results:targets[elasticsearch].load.indexNote#index_options hnsw m=16 ef_construction=128 |
| elasticsearch index | cosine; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/ElasticsearchSink.cs#similarity = "cosine", |
| elasticsearch index | search k=top, num_candidates=100; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/ElasticsearchSink.cs#knn = new { field = "vector", query_vector = vector, k = top, num_candidates = candidates }; src/GenericVectorBuilder.Engines/Sinks/ElasticsearchSinkOptions.cs#int NumCandidates = 100 |
| elasticsearch index | 1 shard, 0 replicas; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/ElasticsearchSink.cs#settings = new { number_of_shards = 1, number_of_replicas = 0 } |
| elasticsearch index | force-merged to one segment after the load | read-back | yes | results:targets[elasticsearch].load.indexNote#1 segment(s), 524 vectors |
| elasticsearch index | (at 1,024 dimensions a segment under 1,043 vectors gets no graph) | unverified | yes |  |
| sql durability | A commit returns after its transaction-log records are written to disk (SQL Server write-ahead logging; | unverified | yes |  |
| sql durability | delayed durability is DISABLED); | read-back | yes | src/GenericVectorBuilder.Bench/Targets/SqlDurability.cs#SELECT name, recovery_model_desc, delayed_durability_desc, page_verify_option_desc FROM sys.databases |
| sql durability | a database created here copies the model database: | unverified | yes |  |
| sql durability | recovery model FULL, page_verify CHECKSUM; no global trace flags are enabled; | read-back | yes | src/GenericVectorBuilder.Bench/Targets/SqlDurability.cs#SELECT name, recovery_model_desc, delayed_durability_desc, page_verify_option_desc FROM sys.databases; src/GenericVectorBuilder.Bench/Targets/SqlDurability.cs#DBCC TRACESTATUS( -1 ) WITH NO_INFOMSGS; |
| sql durability | mssql.conf of container gvb-mssql (/var/opt/mssql/mssql.conf, on the host at /home/dan/gvb-data/engines/mssql/mssql.conf) does not exist, so it sets: nothing; it has no [control] or [traceflag] entry, | read-back | yes | src/GenericVectorBuilder.Bench/Targets/SqlDurability.cs#ConfigFiles.ExistsWithSudoAsync( confPath, ct ) |
| sql durability | so SQL Server's own Linux defaults for flushing writes apply (Microsoft's Linux performance guide names trace flag 3982 as that default; read from the guide, not tested here). | unverified | yes |  |
| sql durability | Not tested by cutting power; whether the disk's own write cache reaches the media was not checked. | unverified | yes |  |
| sql durability | Container settings from its environment (names only): MSSQL_AGENT_ENABLED, MSSQL_MEMORY_LIMIT_MB, MSSQL_PID, MSSQL_RPC_PORT, MSSQL_SA_PASSWORD. | read-back | yes | src/GenericVectorBuilder.Bench/Targets/SqlServerContainer.cs#n.StartsWith( "MSSQL_" |
| sql index | exact VECTOR_DISTANCE cosine, no vector index (full scan) | documented | yes | src/GenericVectorBuilder.Bench/Targets/TargetFactory.cs#private const string SQL_EXACT_INDEX = "exact VECTOR_DISTANCE cosine, no vector index (full scan)" |
| pgvector durability | fsync on, synchronous_commit on, full_page_writes on, wal_sync_method fdatasync (PostgreSQL defaults; | unverified | yes |  |
| pgvector durability | pgvector.compose.yaml sets only shared_buffers, maintenance_work_mem and max_wal_size): | documented | yes | deploy/engines/pgvector.compose.yaml#shared_buffers=2GB; deploy/engines/pgvector.compose.yaml#maintenance_work_mem=1GB; deploy/engines/pgvector.compose.yaml#max_wal_size=4GB |
| pgvector durability | every commit is flushed to the write-ahead log before it returns and HNSW index changes are WAL-logged, so a crash loses no committed row (max_wal_size 4GB only spaces out checkpoints; measured with docker kill, which is SIGKILL, right after a 2,000-vector load and a restart: all 2,000 rows were there and the HNSW index was valid and used by the default search) | unverified | yes |  |
| pgvector index | HNSW vector_cosine_ops m=16 ef_construction=128, | read-back | yes | results:targets[pgvector].load.indexNote#USING hnsw (embedding vector_cosine_ops) WITH (m='16', ef_construction='128') |
| pgvector index | hnsw.ef_search=100 per query, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs#SET LOCAL hnsw.ef_search = ; src/GenericVectorBuilder.Engines/Sinks/PgVectorSinkOptions.cs#public int HnswEfSearch { get; set; } = 100; |
| pgvector index | float32 vector(n), cosine; | unverified | yes |  |
| pgvector index | exact mode = same query with index scans off (sequential scan) | documented | yes | src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs#EXACT_SETTINGS = "SET LOCAL enable_indexscan = off; SET LOCAL jit = off" |
| sqlitevec durability | PRAGMA journal_mode=WAL and PRAGMA synchronous=NORMAL (set by this sink when it opens the file): | documented | yes | src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs#Run( connection, "PRAGMA journal_mode=WAL;" ); src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs#Run( connection, "PRAGMA synchronous=NORMAL;" ) |
| sqlitevec durability | each commit is appended to the -wal file and handed to the operating system, but the WAL is fsynced only when SQLite checkpoints it (measured with strace: 40 single-record commits caused 3 WAL fsyncs), so a crash of the process loses nothing (measured with kill -9 and a reopen: all 50, 300 and 2,000 rows were there), while an operating-system crash or power cut can lose the newest commits since the last checkpoint and leaves the database consistent | unverified | yes |  |
| sqlitevec index | vec0 brute-force scan, no ANN index (exact), | read-back | yes | results:targets[sqlitevec].load.indexNote#EXPLAIN QUERY PLAN: SCAN |
| sqlitevec index | float32, cosine distance, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs#embedding float[{dimension}] distance_metric=cosine, |
| sqlitevec index | default chunk_size=1024; score = 1 - cosine distance; | unverified | yes |  |
| sqlitevec index | searches run concurrently, one WAL reader connection per searcher (opened as searchers arrive, at most 32), writes run one at a time and may overlap searches | documented | yes | src/GenericVectorBuilder.Engines/Sinks/SqliteVecSinkOptions.cs#public int MaxSearchConnections { get; set; } = 32;; src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs#return Math.Max( MIN_SEARCH_CONNECTIONS, _options.MaxSearchConnections ); |
| qdrant durability | What was measured (strace -f on the Qdrant server process, 30 single-point REST upserts per setting, every sync call timed against the request that caused it): with wait=true each upsert had exactly one msync(MS_SYNC) of the write-ahead-log segment inside the request, before the reply (30 of 30); | documented | yes | design/engine-docs/durability-logs-2026-10-04/qdrant-wait-true.strace#MS_SYNC; design/engine-docs/durability-logs-2026-10-04/qdrant-wait-true.marks.json#"mode": "true" |
| qdrant durability | with wait=false the 30 upserts were acknowledged within 0.26 s with no sync call, and the first WAL msync (one call covering all 30 records) came 2.7 s after the last reply, followed by the segment-file flushes. | documented | yes | design/engine-docs/durability-logs-2026-10-04/qdrant-wait-false.strace#msync; design/engine-docs/durability-logs-2026-10-04/qdrant-wait-false.marks.json#"mode": "false" |
| qdrant durability | So the log is flushed to disk under both settings, but with wait=false the flush comes after the acknowledgement: an operating-system crash or power cut in that gap loses acknowledged writes, while a crash of the Qdrant process alone should not, because the bytes are already in the kernel's page cache (inferred, not tested). | unverified | yes |  |
| qdrant durability | Every upsert here is sent with wait=true in batches of 256 over gRPC; | documented | yes | src/GenericVectorBuilder.Core/Sinks/QdrantSink.cs#_client.UpsertAsync( name, points, wait: true; src/GenericVectorBuilder.Core/Sinks/QdrantSink.cs#private const int UPSERT_BATCH = 256; |
| qdrant durability | the strace used one point per request, so one flush per batch is inferred, not measured. | unverified | yes |  |
| qdrant durability | Segment files are flushed every 5 s; log segments 32 MB, 0 created ahead. /qdrant/config/config.yaml in container gvb-qdrant (the image's own file; its storage keys are listed) sets: storage.collection.quantization = null, storage.collection.replication_factor = 1, storage.collection.vectors.on_disk = null, storage.collection.write_consistency_factor = 1, storage.hnsw_index.ef_construct = 100, storage.hnsw_index.full_scan_threshold_kb = 10000, storage.hnsw_index.m = 16, storage.hnsw_index.max_indexing_threads = 0, storage.hnsw_index.on_disk = false, storage.hnsw_index.payload_m = null, storage.max_collections = null, storage.node_type = Normal, storage.on_disk_payload = true, storage.optimizers.default_segment_number = 0, storage.optimizers.deleted_threshold = 0.2, storage.optimizers.flush_interval_sec = 5, storage.optimizers.indexing_threshold_kb = 10000, storage.optimizers.max_optimization_threads = null, storage.optimizers.max_segment_size_kb = null, storage.optimizers.vacuum_min_vector_number = 1000, storage.performance.max_search_threads = 0, storage.performance.optimizer_cpu_budget = 0, storage.performance.update_rate_limit = null, storage.shard_transfer_method = null, storage.snapshots_config.snapshots_storage = local, storage.snapshots_path = ./snapshots, storage.storage_path = ./storage, storage.temp_path = null, storage.update_concurrency = null, storage.wal.wal_capacity_mb = 32, storage.wal.wal_segments_ahead = 0; no QDRANT__ environment overrides in the server process. | read-back | yes | src/GenericVectorBuilder.Bench/Targets/QdrantServer.cs#ConfigFiles.ReadContainerFileAsync( container, configPath, ct ) |
| qdrant durability | Not tested by cutting power; whether the disk's own write cache reaches the media was not checked. | unverified | yes |  |
| qdrant index | exact scan: the builder's sink sends exact=true on every search, | documented | yes | src/GenericVectorBuilder.Bench/Targets/TargetFactory.cs#private const string QDRANT_EXACT_INDEX = "exact scan: the builder's sink sends exact=true on every search |
| qdrant index | so no HNSW graph is used whether or not Qdrant has built one (see the index state) | read-back | yes | results:targets[qdrant].load.indexNote#0 of 524 vectors in HNSW segments |
| opensearch durability | Every acknowledged bulk request is fsynced to the translog before the answer: index.translog.durability=request, the OpenSearch default, which neither this sink nor opensearch.compose.yaml overrides | unverified | yes |  |
| opensearch durability | (OpenSearchReadinessTests reads it back from the live index). | documented | yes | tests/GenericVectorBuilder.Engines.Tests/OpenSearchReadinessTests.cs#index.translog.durability |
| opensearch durability | By that setting a process crash or power loss loses no acknowledged write; this is read from the setting, not shown by pulling power. | unverified | yes |  |
| opensearch durability | One node and no replicas, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/OpenSearchSink.cs#["number_of_shards"] = 1, ["number_of_replicas"] = 0 |
| opensearch durability | so a lost disk loses the data. | unverified | yes |  |
| opensearch index | faiss HNSW float32, no compression, m=16, ef_construction=128, cosinesimil; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/OpenSearchSink.cs#parameters = new { m = HNSW_M, ef_construction = HNSW_EF_CONSTRUCTION }; src/GenericVectorBuilder.Engines/Sinks/OpenSearchSink.cs#private const int HNSW_M = 16;; src/GenericVectorBuilder.Engines/Sinks/OpenSearchSink.cs#private const int HNSW_EF_CONSTRUCTION = 128; |
| opensearch index | search k=top, ef_search=100; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/OpenSearchSink.cs#method_parameters = new { ef_search = Math.Max( _options.EfSearch, top ) }; src/GenericVectorBuilder.Engines/Sinks/OpenSearchSinkOptions.cs#int EfSearch = 100 |
| opensearch index | 1 shard, 0 replicas; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/OpenSearchSink.cs#["number_of_shards"] = 1, ["number_of_replicas"] = 0 |
| opensearch index | graph built at any segment size (approximate_threshold=0); | read-back | yes | results:targets[opensearch].load.indexNote#approximate_threshold 0 |
| opensearch index | force-merged to one segment after the load | read-back | yes | results:targets[opensearch].load.indexNote#force-merged to one segment |
| qdrant-hnsw durability | What was measured (strace -f on the Qdrant server process, 30 single-point REST upserts per setting, every sync call timed against the request that caused it): with wait=true each upsert had exactly one msync(MS_SYNC) of the write-ahead-log segment inside the request, before the reply (30 of 30); | documented | yes | design/engine-docs/durability-logs-2026-10-04/qdrant-wait-true.strace#MS_SYNC; design/engine-docs/durability-logs-2026-10-04/qdrant-wait-true.marks.json#"mode": "true" |
| qdrant-hnsw durability | with wait=false the 30 upserts were acknowledged within 0.26 s with no sync call, and the first WAL msync (one call covering all 30 records) came 2.7 s after the last reply, followed by the segment-file flushes. | documented | yes | design/engine-docs/durability-logs-2026-10-04/qdrant-wait-false.strace#msync; design/engine-docs/durability-logs-2026-10-04/qdrant-wait-false.marks.json#"mode": "false" |
| qdrant-hnsw durability | So the log is flushed to disk under both settings, but with wait=false the flush comes after the acknowledgement: an operating-system crash or power cut in that gap loses acknowledged writes, while a crash of the Qdrant process alone should not, because the bytes are already in the kernel's page cache (inferred, not tested). | unverified | yes |  |
| qdrant-hnsw durability | Every upsert here is sent with wait=true in batches of 256 over gRPC; | documented | yes | src/GenericVectorBuilder.Core/Sinks/QdrantSink.cs#_client.UpsertAsync( name, points, wait: true; src/GenericVectorBuilder.Core/Sinks/QdrantSink.cs#private const int UPSERT_BATCH = 256; |
| qdrant-hnsw durability | the strace used one point per request, so one flush per batch is inferred, not measured. | unverified | yes |  |
| qdrant-hnsw durability | Segment files are flushed every 5 s; log segments 32 MB, 0 created ahead. /qdrant/config/config.yaml in container gvb-qdrant (the image's own file; its storage keys are listed) sets: storage.collection.quantization = null, storage.collection.replication_factor = 1, storage.collection.vectors.on_disk = null, storage.collection.write_consistency_factor = 1, storage.hnsw_index.ef_construct = 100, storage.hnsw_index.full_scan_threshold_kb = 10000, storage.hnsw_index.m = 16, storage.hnsw_index.max_indexing_threads = 0, storage.hnsw_index.on_disk = false, storage.hnsw_index.payload_m = null, storage.max_collections = null, storage.node_type = Normal, storage.on_disk_payload = true, storage.optimizers.default_segment_number = 0, storage.optimizers.deleted_threshold = 0.2, storage.optimizers.flush_interval_sec = 5, storage.optimizers.indexing_threshold_kb = 10000, storage.optimizers.max_optimization_threads = null, storage.optimizers.max_segment_size_kb = null, storage.optimizers.vacuum_min_vector_number = 1000, storage.performance.max_search_threads = 0, storage.performance.optimizer_cpu_budget = 0, storage.performance.update_rate_limit = null, storage.shard_transfer_method = null, storage.snapshots_config.snapshots_storage = local, storage.snapshots_path = ./snapshots, storage.storage_path = ./storage, storage.temp_path = null, storage.update_concurrency = null, storage.wal.wal_capacity_mb = 32, storage.wal.wal_segments_ahead = 0; no QDRANT__ environment overrides in the server process. | read-back | yes | src/GenericVectorBuilder.Bench/Targets/QdrantServer.cs#ConfigFiles.ReadContainerFileAsync( container, configPath, ct ) |
| qdrant-hnsw durability | Not tested by cutting power; whether the disk's own write cache reaches the media was not checked. | unverified | yes |  |
| qdrant-hnsw index | HNSW m=16 ef_construct=100, | documented | yes | src/GenericVectorBuilder.Bench/Targets/QdrantHnswSink.cs#var hnsw = new HnswConfigDiff { M = HNSW_M, EfConstruct = HNSW_EF_CONSTRUCT, FullScanThreshold = FULL_SCAN_THRESHOLD_KB }; |
| qdrant-hnsw index | hnsw_ef=server default, | documented | yes | src/GenericVectorBuilder.Bench/Targets/QdrantHnswSink.cs#search.HnswEf = _ef.Value; |
| qdrant-hnsw index | cosine; | documented | yes | src/GenericVectorBuilder.Bench/Targets/QdrantHnswSink.cs#new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine } |
| qdrant-hnsw index | indexing_threshold_kb 1 and full_scan_threshold_kb 10 | documented | yes | src/GenericVectorBuilder.Bench/Targets/QdrantHnswSink.cs#var optimizers = new OptimizersConfigDiff { IndexingThreshold = INDEXING_THRESHOLD_KB };; src/GenericVectorBuilder.Bench/Targets/QdrantHnswSink.cs#var hnsw = new HnswConfigDiff { M = HNSW_M, EfConstruct = HNSW_EF_CONSTRUCT, FullScanThreshold = FULL_SCAN_THRESHOLD_KB }; |
| qdrant-hnsw index | (server defaults are 10,000 each) | read-back | yes | results:targets[qdrant-hnsw].durability#storage.optimizers.indexing_threshold_kb = 10000; results:targets[qdrant-hnsw].durability#storage.hnsw_index.full_scan_threshold_kb = 10000 |
| qdrant-hnsw index | so a small collection builds and walks its graph | read-back | yes | results:targets[qdrant-hnsw].load.indexNote#524 of 524 vectors in HNSW segments |
| redis durability | save "300 1" and appendonly no (redis.compose.yaml): | documented | yes | deploy/engines/redis.compose.yaml#--save "300 1"; deploy/engines/redis.compose.yaml#--appendonly no |
| redis durability | an RDB snapshot is written every 5 minutes if at least one key changed, and on a clean stop; there is no append-only log, so a crash loses every write since the last snapshot (up to 5 minutes plus the time a snapshot takes; measured with docker kill, which is SIGKILL, right after a 2,000-vector load: none of the 2,000 hashes were there after the restart, and the restart loaded an older snapshot that still held keys of a collection that had been dropped since) | unverified | yes |  |
| redis index | HNSW TYPE FLOAT32 M=16 EF_CONSTRUCTION=128, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/RedisSink.cs#["M"] = _options.HnswM,; src/GenericVectorBuilder.Engines/Sinks/RedisSink.cs#["EF_CONSTRUCTION"] = _options.HnswEfConstruction,; src/GenericVectorBuilder.Engines/Sinks/RedisSinkOptions.cs#public int HnswM { get; set; } = 16;; src/GenericVectorBuilder.Engines/Sinks/RedisSinkOptions.cs#public int HnswEfConstruction { get; set; } = 128; |
| redis index | EF_RUNTIME=100 per query, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/RedisSink.cs#$"EF_RUNTIME {Math.Max( _options.HnswEfRuntime, top )}"; src/GenericVectorBuilder.Engines/Sinks/RedisSinkOptions.cs#public int HnswEfRuntime { get; set; } = 100; |
| redis index | cosine; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/RedisSink.cs#["DISTANCE_METRIC"] = "COSINE", |
| redis index | exact mode = FLAT index built on first exact query | unverified | yes |  |
| milvus durability | Writes are not fsynced. Standalone uses the default message queue rocksmq (RocksDB, /var/lib/milvus/rdb_data, mq.type default) and flushed segments go to local-disk object storage | unverified | yes |  |
| milvus durability | (COMMON_STORAGETYPE=local in milvus.compose.yaml). | documented | yes | deploy/engines/milvus.compose.yaml#COMMON_STORAGETYPE: local |
| milvus durability | Measured 2026-10-04 with strace over 30 acknowledged single-row upserts and one flush: fdatasync ran only on the embedded etcd files (member/wal, member/snap/db), never on rdb_data or the segment files. | documented | yes | design/engine-docs/durability-logs-2026-10-04/milvus-acknowledged-upserts.strace#member/wal |
| milvus durability | A host power loss or kernel crash can lose acknowledged rows that are still in the OS page cache; a Milvus process crash alone should not (inferred, not tested). Metadata in embedded etcd is fdatasynced on every commit. | unverified | yes |  |
| milvus index | HNSW M=16 efConstruction=128, | read-back | yes | results:targets[milvus].load.indexNote#type HNSW (COSINE, {"M":16,"efConstruction":128}) |
| milvus index | ef=100, metric COSINE, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/MilvusSink.cs#writer.WriteNumber( "ef", _options.Ef.Value );; src/GenericVectorBuilder.Engines/Sinks/MilvusSinkOptions.cs#int? Ef = 100,; src/GenericVectorBuilder.Engines/Sinks/MilvusSink.cs#["metricType"] = "COSINE", |
| milvus index | Strong consistency searches; | unverified | yes |  |
| milvus index | approximate only (no exact mode) | unverified | yes |  |
| mariadb durability | innodb_flush_log_at_trx_commit=2 (set in mariadb-bench.compose.yaml for the benchmark's own container gvbbench-mariadb, and in mariadb.compose.yaml for the daily gvb-mariadb, which has the same server settings): | documented | yes | deploy/engines/mariadb-bench.compose.yaml#--innodb-flush-log-at-trx-commit=2; deploy/engines/mariadb.compose.yaml#--innodb-flush-log-at-trx-commit=2; design/bench-inputs/mariadb-effort-2026-10-07/run.log#SETTING innodb_flush_log_at_trx_commit = 2 |
| mariadb durability | the InnoDB redo log is written to the operating system at every commit but fsynced about once a second, so a crash of the mariadbd process loses nothing, while an operating-system crash or power cut can lose the last second of commits; | unverified | yes |  |
| mariadb durability | innodb_doublewrite is on | documented | yes | design/bench-inputs/mariadb-effort-2026-10-07/run.log#SETTING innodb_doublewrite = ON |
| mariadb durability | (default), | unverified | yes |  |
| mariadb durability | the binary log is off, | documented | yes | design/bench-inputs/mariadb-effort-2026-10-07/run.log#SETTING log_bin = OFF |
| mariadb durability | and the vector graph is an InnoDB table under the same log | unverified | yes |  |
| mariadb durability | (settings read from the running server with SHOW VARIABLES; | documented | yes | design/bench-inputs/mariadb-effort-2026-10-07/run.log#SETTING innodb_flush_log_at_trx_commit = 2 |
| mariadb durability | the crash behaviour is InnoDB's documented behaviour for this setting and was not tested on either container) | unverified | yes |  |
| mariadb index | VECTOR INDEX | documented | yes | src/GenericVectorBuilder.Engines/Sinks/MariaDbSink.cs#VECTOR INDEX {INDEX_NAME} ( embedding ) M={_options.HnswM} DISTANCE=cosine |
| mariadb index | (HNSW variant) | unverified | yes |  |
| mariadb index | DISTANCE=cosine, M=16 | documented | yes | src/GenericVectorBuilder.Engines/Sinks/MariaDbSink.cs#VECTOR INDEX {INDEX_NAME} ( embedding ) M={_options.HnswM} DISTANCE=cosine; src/GenericVectorBuilder.Engines/Sinks/MariaDbSinkOptions.cs#public int HnswM { get; set; } = 16; |
| mariadb index | (no ef_construction setting exists), | unverified | yes |  |
| mariadb index | mhnsw_ef_search=100 per statement | read-back | yes | results:targets[mariadb].load.indexNote#the server applied mhnsw_ef_search 100 |
| mariadb index | (the ef 100 most engines here use, so the search effort matches; | unverified | yes |  |
| mariadb index | MariaDB's own default is 20; | documented | yes | design/bench-inputs/mariadb-effort-2026-10-07/run.log#SETTING mhnsw_ef_search = 20 |
| mariadb index | recall@10 at ef 100 fall... | unverified | no: mariadb-recall |  |
| mariadb index | mhnsw_max_cache_size 4G; | documented | yes | deploy/engines/mariadb-bench.compose.yaml#--mhnsw-max-cache-size=4G; design/bench-inputs/mariadb-effort-2026-10-07/run.log#SETTING mhnsw_max_cache_size = 4294967296 |
| mariadb index | exact mode = IGNORE INDEX full scan | documented | yes | src/GenericVectorBuilder.Engines/Sinks/MariaDbSink.cs#SearchSql( collection, top, $"IGNORE INDEX ( {INDEX_NAME} )" ) |
| weaviate durability | Weaviate 1.39.8 defaults... | unverified | no: contradicted |  |
| weaviate durability | every object is appended to the LSM write-ahead log with a plain write and no fsync, and the log is fsynced only when its memtable is flushed, 60 seconds after the last write (PERSISTENCE_MEMTABLES_FLUSH_IDLE_AFTER_SECONDS default; measured with strace: 2,080 writes into the objects log during a 2,000-object load, the first fsync 60 s after the last write), so a power cut can lose the last minute of writes; the HNSW commit log is buffered inside the process (77 writes for 2,000 vectors), so a killed process loses the newest vectors from the vector index while their objects survive (measured with docker kill, which is SIGKILL, one second after the load and a restart, two runs each: 0 to 1 of 50, 264 to 267 of 300 and 1,992 to 1,997 of 2,000 stored objects were still reachable through a vector search) | unverified | yes |  |
| weaviate index | HNSW maxConnections(M)=16 efConstruction=128, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/WeaviateSink.cs#["maxConnections"] = _options.MaxConnections,; src/GenericVectorBuilder.Engines/Sinks/WeaviateSink.cs#["efConstruction"] = _options.EfConstruction,; src/GenericVectorBuilder.Engines/Sinks/WeaviateSinkOptions.cs#int MaxConnections = 16,; src/GenericVectorBuilder.Engines/Sinks/WeaviateSinkOptions.cs#int EfConstruction = 128, |
| weaviate index | ef=-1 | documented | yes | src/GenericVectorBuilder.Engines/Sinks/WeaviateSink.cs#["ef"] = _options.Ef,; src/GenericVectorBuilder.Engines/Sinks/WeaviateSinkOptions.cs#int Ef = -1 ); |
| weaviate index | (dynamic: limit x 8 clamped 100..500), | documented | yes | design/engine-docs/weaviate-vector-index-reference-2026-10-07.mdx#`dynamicEfFactor`; design/engine-docs/weaviate-vector-index-reference-2026-10-07.mdx#`dynamicEfMin`; design/engine-docs/weaviate-vector-index-reference-2026-10-07.mdx#`dynamicEfMax`; design/engine-docs/weaviate-vector-index-concepts-2026-10-07.md#The dynamic list size will be set as the query limit multiplied by `dynamicEfFactor`, modified by a minimum of `dynamicEfMin` and a maximum of `dynamicEfMax`. |
| weaviate index | cosine, no quantization; | unverified | yes |  |
| weaviate index | approximate only (no exact mode) | unverified | yes |  |
| mongodb durability | Acknowledged writes are journaled before the acknowledgement. The sink sets no write concern, so the server default applies: getDefaultRWConcern gives w majority and the one-member replica set (--replSet gvbmongo in the image) has writeConcernMajorityJournalDefault true, with WiredTiger journaling on (journalCommitInterval 100 ms is only the interval for unacknowledged work). | unverified | yes |  |
| mongodb durability | Measured 2026-10-04: 30 acknowledged single-document inserts raised WiredTiger 'log sync operations' by 30 and | unverified | yes |  |
| mongodb durability | strace showed fdatasync on /data/db/journal/WiredTigerLog files. | documented | yes | design/engine-docs/durability-logs-2026-10-04/mongodb-acknowledged-inserts.strace#WiredTigerLog |
| mongodb durability | A crash loses no acknowledged write. mongot's search index is not part of that promise: it follows the collection asynchronously and is rebuilt from it. | unverified | yes |  |
| mongodb index | vectorSearch index, HNSW maxEdges=16 numEdgeCandidates=128, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/MongoDbSinkOptions.cs#public int HnswMaxEdges { get; set; } = 16;; src/GenericVectorBuilder.Engines/Sinks/MongoDbSinkOptions.cs#public int HnswNumEdgeCandidates { get; set; } = 128; |
| mongodb index | float32 binData, cosine, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/MongoDbSink.cs#VectorSimilarity.Cosine |
| mongodb index | numCandidates=20x hits (min 100); | documented | yes | src/GenericVectorBuilder.Engines/Sinks/MongoDbSinkOptions.cs#public int NumCandidatesFactor { get; set; } = 20;; src/GenericVectorBuilder.Engines/Sinks/MongoDbSinkOptions.cs#public int MinNumCandidates { get; set; } = 100; |
| mongodb index | exact mode = $vectorSearch exact:true | unverified | yes |  |
| chroma durability | SQLite rollback journal with its default synchronous=FULL under the data directory | unverified | yes |  |
| chroma durability | (chroma.compose.yaml sets IS_PERSISTENT=1 and PERSIST_DIRECTORY=/data and no sync setting): | documented | yes | deploy/engines/chroma.compose.yaml#IS_PERSISTENT: "1"; deploy/engines/chroma.compose.yaml#PERSIST_DIRECTORY: /data |
| chroma durability | every write is committed to chroma.sqlite3 with fsync of the journal, the directory and the database file before the call returns (strace: 15 database fsyncs and 38 journal fsyncs for one create, four 500-vector upserts and one delete), and the HNSW files are written every sync_threshold=1000 vectors and rebuilt from the SQLite log after a crash; measured with kill -9 and a restart: all 50, 300 and 2,000 vectors were still stored and searchable | unverified | yes |  |
| chroma index | HNSW M=16 ef_construction=128, ef_search=100 | documented | yes | src/GenericVectorBuilder.Engines/Sinks/ChromaSink.cs#["max_neighbors"] = _options.MaxNeighbors,; src/GenericVectorBuilder.Engines/Sinks/ChromaSink.cs#["ef_construction"] = _options.EfConstruction,; src/GenericVectorBuilder.Engines/Sinks/ChromaSink.cs#["ef_search"] = _options.EfSearch,; src/GenericVectorBuilder.Engines/Sinks/ChromaSinkOptions.cs#int MaxNeighbors = 16,; src/GenericVectorBuilder.Engines/Sinks/ChromaSinkOptions.cs#int EfConstruction = 128,; src/GenericVectorBuilder.Engines/Sinks/ChromaSinkOptions.cs#int EfSearch = 100 ); |
| chroma index | (Chroma default), | unverified | yes |  |
| chroma index | cosine; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/ChromaSink.cs#["space"] = "cosine", |
| chroma index | approximate only (no exact mode) | unverified | yes |  |
| typesense durability | Every acknowledged write is appended to Typesense's raft log and fsynced before the answer: braft raft_sync=true with raft_sync_policy=0 (sync immediately), read from the running container's brpc /flags page on 2026-10-04. A restart replays the log from the last snapshot | unverified | yes |  |
| typesense durability | (typesense.compose.yaml sets TYPESENSE_SNAPSHOT_INTERVAL_SECONDS=300) | documented | yes | deploy/engines/typesense.compose.yaml#TYPESENSE_SNAPSHOT_INTERVAL_SECONDS: "300" |
| typesense durability | and rebuilds the in-memory HNSW graph, so by those settings a crash loses no acknowledged write, but the restart is slow after a big load. This is read from the settings, not shown by pulling power. One node and no replicas, so a lost disk loses the data. | unverified | yes |  |
| typesense index | HNSW float32 (hnswlib), m=16, ef_construction=128, cosine; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/TypesenseSink.cs#hnsw_params = new { M = HNSW_M, ef_construction = HNSW_EF_CONSTRUCTION }; src/GenericVectorBuilder.Engines/Sinks/TypesenseSink.cs#private const int HNSW_M = 16;; src/GenericVectorBuilder.Engines/Sinks/TypesenseSink.cs#private const int HNSW_EF_CONSTRUCTION = 128; |
| typesense index | search k=top, ef=100; | documented | yes | src/GenericVectorBuilder.Engines/Sinks/TypesenseSink.cs#string options = $"k:{top}, ef:{Math.Max( _options.Ef, top )}";; src/GenericVectorBuilder.Engines/Sinks/TypesenseSinkOptions.cs#int Ef = 100 |
| typesense index | exact mode = filter ordinal:>=0 with flat_search_cutoff; | unverified | yes |  |
| typesense index | index held in memory | unverified | yes |  |
| sql-diskann durability | A commit returns after its transaction-log records are written to disk (SQL Server write-ahead logging; | unverified | yes |  |
| sql-diskann durability | delayed durability is DISABLED); | read-back | yes | src/GenericVectorBuilder.Bench/Targets/SqlDurability.cs#SELECT name, recovery_model_desc, delayed_durability_desc, page_verify_option_desc FROM sys.databases |
| sql-diskann durability | a database created here copies the model database: | unverified | yes |  |
| sql-diskann durability | recovery model FULL, page_verify CHECKSUM; no global trace flags are enabled; | read-back | yes | src/GenericVectorBuilder.Bench/Targets/SqlDurability.cs#SELECT name, recovery_model_desc, delayed_durability_desc, page_verify_option_desc FROM sys.databases; src/GenericVectorBuilder.Bench/Targets/SqlDurability.cs#DBCC TRACESTATUS( -1 ) WITH NO_INFOMSGS; |
| sql-diskann durability | mssql.conf of container gvb-mssql (/var/opt/mssql/mssql.conf, on the host at /home/dan/gvb-data/engines/mssql/mssql.conf) does not exist, so it sets: nothing; it has no [control] or [traceflag] entry, | read-back | yes | src/GenericVectorBuilder.Bench/Targets/SqlDurability.cs#ConfigFiles.ExistsWithSudoAsync( confPath, ct ) |
| sql-diskann durability | so SQL Server's own Linux defaults for flushing writes apply (Microsoft's Linux performance guide names trace flag 3982 as that default; read from the guide, not tested here). | unverified | yes |  |
| sql-diskann durability | Not tested by cutting power; whether the disk's own write cache reaches the media was not checked. | unverified | yes |  |
| sql-diskann durability | Container settings from its environment (names only): MSSQL_AGENT_ENABLED, MSSQL_MEMORY_LIMIT_MB, MSSQL_PID, MSSQL_RPC_PORT, MSSQL_SA_PASSWORD. | read-back | yes | src/GenericVectorBuilder.Bench/Targets/SqlServerContainer.cs#n.StartsWith( "MSSQL_" |
| sql-diskann index | DiskANN (preview) via VECTOR_SEARCH, cosine, | unverified | yes |  |
| sql-diskann index | build {"StartId":"306", "L":"48", "M":"8", "R":"48"}; | read-back | yes | results:targets[sql-diskann].load.indexNote#built DiskANN, parameters {"StartId":"306", "L":"48", "M":"8", "R":"48"}; src/GenericVectorBuilder.Bench/Targets/SqlDiskAnnSink.cs#_buildParameters = index?.Build ?? "unknown"; |
| sql-diskann index | the exact mode scans the same table | unverified | yes |  |
| duckdb durability | DuckDB's write-ahead log is fsynced at every commit and no DuckDbSink setting changes that | unverified | yes |  |
| duckdb durability | (checkpoint_threshold=256MB | documented | yes | src/GenericVectorBuilder.Engines/Sinks/DuckDbSinkOptions.cs#CheckpointThreshold { get; set; } = "256MB"; src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET checkpoint_threshold |
| duckdb durability | only spaces out the checkpoints that write the database file; measured with strace: 42 WAL fsyncs for 40 single-record commits plus 2 setup statements), so an operating-system crash or power cut loses no committed row; measured with kill -9 right after the last commit and a reopen: all 50, 300 and 2,000 rows and an HNSW index that counted the same number were recovered from the log; not tested and documented by DuckDB: the HNSW index is file-backed only through hnsw_enable_experimental_persistence = true, | unverified | yes |  |
| duckdb durability | which this sink turns on, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET hnsw_enable_experimental_persistence = true; |
| duckdb durability | and WAL recovery for such custom indexes is not complete, so a crash during a checkpoint or a later commit can damage the index while the rows survive | unverified | yes |  |
| duckdb index | HNSW (vss extension) FLOAT[n] metric=cosine m=16 ef_construction=128, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#WITH ( metric = 'cosine', m =; src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#Run( connection, "LOAD vss;" ); src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#{VECTOR_COLUMN} FLOAT[{dimension.ToString( CultureInfo.InvariantCulture )}] NOT NULL );; src/GenericVectorBuilder.Engines/Sinks/DuckDbSinkOptions.cs#public int HnswM { get; set; } = 16;; src/GenericVectorBuilder.Engines/Sinks/DuckDbSinkOptions.cs#public int HnswEfConstruction { get; set; } = 128; |
| duckdb index | ef_search=100 per connection, | documented | yes | src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET hnsw_ef_search; src/GenericVectorBuilder.Engines/Sinks/DuckDbSinkOptions.cs#public int HnswEfSearch { get; set; } = 100; |
| duckdb index | persistent (hnsw_enable_experimental_persistence=true, checkpoint_threshold=256MB); | documented | yes | src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET hnsw_enable_experimental_persistence = true;; src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET checkpoint_threshold; src/GenericVectorBuilder.Engines/Sinks/DuckDbSinkOptions.cs#public string CheckpointThreshold { get; set; } = "256MB"; |
| duckdb index | exact mode = array_cosine_similarity sequential scan; | unverified | yes |  |
| duckdb index | score = 1 - cosine distance; | unverified | yes |  |
| duckdb index | searches run concurrently, one connection per searcher (opened as searchers arrive, at most 32), writes run one at a time and never overlap a search | unverified | yes |  |
| run note 0 | Order: targets ran one at a time in a random order from seed 701 (runSeed; --seed 701 repeats it, targetOrder lists it). | documented | yes | src/GenericVectorBuilder.Bench/Running/RunOrder.cs#return Shuffle( names, Mix( (ulong)(uint)runSeed ^ TARGET_SALT ) ); |
| run note 0 | Inside each target the timed passes also ran in a random order from the same seed and the target's name (passOrder): | documented | yes | src/GenericVectorBuilder.Bench/Running/RunOrder.cs#return Shuffle( passes, Mix( (ulong)(uint)runSeed ^ PASS_SALT ^ StableHash( targetName.ToLowerInvariant() ) ) ); |
| run note 0 | default@1 is one searcher for 20 s (every search's latency gives p50/p95/p99, the completed searches give QPS@1, its first answer to each query gives recall and nDCG), default@N is N searchers for 20 s (QPS@N), exact is the engine's exact mode, one searcher for 60 s cycling the queries. | unverified | yes |  |
| run note 1 | Preparation, untimed, before any timed pass: a rehearsal of every pass type at its own concurrency for 30 s each, through the same code the passes use. | unverified | yes |  |
| run note 2 | Warm-up and settle check, untimed, right before every timed pass: the pass's own search at the pass's own number of searchers for at least 15 s and at least 20 searches (at most 120 s), read in windows of at least 2 s and 100 searches; then a 3 s trial of the same pass. The trial's figure (p50 with one searcher, QPS with several) must lie within 10% of the warm-up's settled figure (the median of its last 3 windows, which must agree within 5%); if not, the warm-up is extended once (at least 30 s, until its windows agree, at most 120 s), where an extension's windows agree when its level test passes: the older and the newer half of its latest windows, 5 to 10 windows a half, each half read as the pass reads it (the p50 of all its searches with one searcher, its searches per second with several), agree within 5%, judged on the windows that stopped it (an extension whose cap runs out with fewer than 10 windows is judged by its last 3 instead); then a second trial is taken, which must lie inside the range of the newer half's windows or within 10% of the newer half's figure, and the warm-up runs again before the pass. After the pass its own figure is held against the settled figure, within 10%. In the level test, the second trial and the hold, two one-searcher p50s no more than 0.1 ms apart agree whatever their percentage (below what this box resolves at one searcher). Each target's notes give every check, and a pass that still disagrees, or whose own figure did not hold, flags its target as unsettled. With machine control on, the check for a quiet box is made when the warm-up is announced, before it starts (a wait between the warm-up and the timed pass let the engine go cold), so by the time the clock opens that check is as old as the warm-up and the trial (about 18 s, more after an extension); each pass's conditions record that lead (quietCheckLeadSeconds). Failed untimed searches are counted per target (warmupErrors) and are not in the timed error counts. | unverified | yes |  |
| run note 3 | Load rows/s counts only time inside each target's upsert calls: one writer, batches of 1000, rows already in memory, the collection dropped and created fresh first. Engines that build or finish their index after the writes do it in a separate timed index step (the load's index seconds), and the run waits for it before searching. | unverified | yes |  |
| run note 4 | Index proof: each engine's own report of its index (indexState) is read after the load and again after the last pass. A target whose index was not ready after the load was still measured and carries a WARNING; an engine that reports nothing counts as not ready. | unverified | yes |  |
| run note 5 | Search settings: each searched target records the settings its own index description states (searchSettings: the build parameters and the effort per query, such as m, ef_construction and ef_search), read after the load; consolidating never averages runs whose settings differ. | unverified | yes |  |
| run note 6 | Durability: each target's crash-safety setting as configured here (durability). Engines that do not force writes to disk on every commit load faster for that reason. | unverified | yes |  |
| run note 7 | Latency is client-side wall time around each search (network and driver included), every search of the default@1 window, one searcher, the queries cycled; a window with fewer than 200 searches, a p50 above 1.25 x its mean, or a mean above its p99 is flagged. | unverified | yes |  |
| run note 8 | QPS: N searchers back to back for 20 s per level; completed searches divided by the window's elapsed time. | unverified | yes |  |
| run note 9 | Recall@10: share of the exact top 10 (brute force in memory) that the engine returned. | unverified | yes |  |
| run note 9 | A hit whose exact similarity ties the 10th best (within 1e-5) also counts | documented | yes | src/GenericVectorBuilder.Bench/Stats/BenchMath.cs#public const double TIE_TOLERANCE = 1e-5; |
| run note 9 | , because duplicate rows embed to identical vectors. | unverified | yes |  |
| run note 10 | Routes: a container engine (the benchmark's own SQL Server and Qdrant containers included) is reached at its container's own address on its Docker network, never through the published 127.0.0.1 port (docker-proxy); | read-back | yes | results:conditions.connections#this target |
| run note 10 | the native comparison ta... | unverified | no: absent-targets |  |
| run note 10 | Each target's addresses and the connections the client held open after its passes are in its notes and in conditions.connections. | unverified | yes |  |
| run note 11 | RAM of compose engines, the benchmark's own SQL Server and Qdrant containers (sql, sql-diskann, qdrant, qdrant-hnsw) included, is docker stats of the engine's containers; | unverified | yes |  |
| run note 11 | for the native compariso... | unverified | no: absent-targets |  |
| run note 11 | Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing). | unverified | yes |  |
| run note 12 | Engines: run-all starts an engine that is down and stops it afterwards only if it was not running when the run began; an engine that was already running is left running. | unverified | yes |  |
| run note 14 | Machine control on: governor performance on every CPU during the run (before: schedutil; at the end: performance; after putting it back: schedutil). CPU clock pinned for the run: turbo off (intel_pstate/no_turbo 0 -> 1), so every CPU's clock is held at its ceiling of 3500 MHz whatever the engine runs; the uncore (L3 and memory) clock is held at 3000 MHz (min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); was min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)) (before: turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; while pinned: turbo off (intel_pstate/no_turbo 1), ceiling 3500 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; at the end: turbo off (intel_pstate/no_turbo 1), ceiling 3500 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; after putting it back: turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7). Uncore limit at the end: min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); after putting it back: min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e). Each target's clock note gives every pass's average MHz on the engine CPUs and on the client CPUs; a pass whose average on either is more than 1% off the pinned 3500 MHz is flagged. Figures from runs made with turbo on are not comparable with these in absolute terms. engine CPUs 2-3,6-7 (cores 2,6 and 3,7), client CPUs 0-1,4-5 (cores 0,4 and 1,5); the client process was pinned; each engine was pinned to the engine CPUs for its turn and put back after (conditions.engines); an engine run-all started (and stopped) was asked to be created on them, and its notes say whether the host did so or it was moved there after the start. | read-back | yes | results:conditions.governor#performance; results:conditions.clientCpus#0-1,4-5; results:conditions.engineCpus#2-3,6-7 |
| run note 14 | Busy box: right before each pass's warm-up the run waits, up to 10 min, while processes outside the benchmark (everything but this client and the engine under test's cgroups) use more than 0.3 CPUs on average over the last 60 s or the last 5 s, neither window reaching back past the start of the target or of its engine (so the engine's own start-up is not outside work); if the box does not clear the pass runs anyway, flagged 'busy box', as is a pass whose own outside load is above the limit. Outside load counts busy = user + nice + system + irq + softirq + steal (guest time is already in user); CONFIG_IRQ_TIME_ACCOUNTING is not set, so task and cgroup run time include the interrupt and softirq time that hit them and it is added; CONFIG_PARAVIRT_TIME_ACCOUNTING is not set, so steal is added (/boot/config-6.8.0-142-generic) (conditions.cpuAccounting); each pass also records this client's own CPU time per search (conditions.passes[].clientCpuMsPerSearch). CPU clocks were sampled every 250 ms; each pass's min/median/max per CPU is in conditions.passes. CPU idle states, recorded and left as found: driver intel_idle, governor menu, intel_idle max_cstate 9; POLL on, C1 on, C1E on, C3 OFF on CPUs 0-7 (default disabled), C6 on (conditions.cpuIdle). How each target was reached: conditions.connections. Client build Release, .NET 10.0.12. | unverified | yes |  |
| run note 15 | sqlitevec is embedded: it ran inside the client process on the client CPUs 0-1,4-5, sharing them with the client. | read-back | yes | results:conditions.engines#embedded |
| run note 16 | duckdb is embedded: it ran inside the client process on the client CPUs 0-1,4-5, sharing them with the client. | read-back | yes | results:conditions.engines#embedded |

## Method

The method, quoted from the notes of run 20261007-130619-eshoponweb:

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> Order: targets ran one at a time in a random order from seed 701 (runSeed; --seed 701 repeats it, targetOrder lists it).

> Inside each target the timed passes also ran in a random order from the same seed and the target's name (passOrder):

Typed in the program's own text; not read back, measured or backed by a saved source:

> default@1 is one searcher for 20 s (every search's latency gives p50/p95/p99, the completed searches give QPS@1, its first answer to each query gives recall and nDCG), default@N is N searchers for 20 s (QPS@N), exact is the engine's exact mode, one searcher for 60 s cycling the queries.

> Preparation, untimed, before any timed pass: a rehearsal of every pass type at its own concurrency for 30 s each, through the same code the passes use.

> Warm-up and settle check, untimed, right before every timed pass: the pass's own search at the pass's own number of searchers for at least 15 s and at least 20 searches (at most 120 s), read in windows of at least 2 s and 100 searches; then a 3 s trial of the same pass. The trial's figure (p50 with one searcher, QPS with several) must lie within 10% of the warm-up's settled figure (the median of its last 3 windows, which must agree within 5%); if not, the warm-up is extended once (at least 30 s, until its windows agree, at most 120 s), where an extension's windows agree when its level test passes: the older and the newer half of its latest windows, 5 to 10 windows a half, each half read as the pass reads it (the p50 of all its searches with one searcher, its searches per second with several), agree within 5%, judged on the windows that stopped it (an extension whose cap runs out with fewer than 10 windows is judged by its last 3 instead); then a second trial is taken, which must lie inside the range of the newer half's windows or within 10% of the newer half's figure, and the warm-up runs again before the pass. After the pass its own figure is held against the settled figure, within 10%. In the level test, the second trial and the hold, two one-searcher p50s no more than 0.1 ms apart agree whatever their percentage (below what this box resolves at one searcher). Each target's notes give every check, and a pass that still disagrees, or whose own figure did not hold, flags its target as unsettled. With machine control on, the check for a quiet box is made when the warm-up is announced, before it starts (a wait between the warm-up and the timed pass let the engine go cold), so by the time the clock opens that check is as old as the warm-up and the trial (about 18 s, more after an extension); each pass's conditions record that lead (quietCheckLeadSeconds). Failed untimed searches are counted per target (warmupErrors) and are not in the timed error counts.

> Load rows/s counts only time inside each target's upsert calls: one writer, batches of 1000, rows already in memory, the collection dropped and created fresh first. Engines that build or finish their index after the writes do it in a separate timed index step (the load's index seconds), and the run waits for it before searching.

> Index proof: each engine's own report of its index (indexState) is read after the load and again after the last pass. A target whose index was not ready after the load was still measured and carries a WARNING; an engine that reports nothing counts as not ready.

> Search settings: each searched target records the settings its own index description states (searchSettings: the build parameters and the effort per query, such as m, ef_construction and ef_search), read after the load; consolidating never averages runs whose settings differ.

> Durability: each target's crash-safety setting as configured here (durability). Engines that do not force writes to disk on every commit load faster for that reason.

> Latency is client-side wall time around each search (network and driver included), every search of the default@1 window, one searcher, the queries cycled; a window with fewer than 200 searches, a p50 above 1.25 x its mean, or a mean above its p99 is flagged.

> QPS: N searchers back to back for 20 s per level; completed searches divided by the window's elapsed time.

> Recall@10: share of the exact top 10 (brute force in memory) that the engine returned.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> A hit whose exact similarity ties the 10th best (within 1e-5) also counts

A saved check of this data found 0 pairs of rows within 1e-5 of identical, and 0 of 20 queries with a row outside the exact top 10 that the tie rule would count.

Typed in the program's own text; not read back, measured or backed by a saved source:

> , because duplicate rows embed to identical vectors.

Read back in these runs from the engine or the machine:

> Routes: a container engine (the benchmark's own SQL Server and Qdrant containers included) is reached at its container's own address on its Docker network, never through the published 127.0.0.1 port (docker-proxy);

A clause of this note names sql-native and qdrant-native, which are not targets of this report, so it is not printed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Each target's addresses and the connections the client held open after its passes are in its notes and in conditions.connections.

> RAM of compose engines, the benchmark's own SQL Server and Qdrant containers (sql, sql-diskann, qdrant, qdrant-hnsw) included, is docker stats of the engine's containers;

A clause of this note names sql-native and qdrant-native, which are not targets of this report, so it is not printed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing).

> Engines: run-all starts an engine that is down and stops it afterwards only if it was not running when the run began; an engine that was already running is left running.

Read back in these runs from the engine or the machine:

> Machine control on: governor performance on every CPU during the run (before: schedutil; at the end: performance; after putting it back: schedutil). CPU clock pinned for the run: turbo off (intel_pstate/no_turbo 0 -> 1), so every CPU's clock is held at its ceiling of 3500 MHz whatever the engine runs; the uncore (L3 and memory) clock is held at 3000 MHz (min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); was min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)) (before: turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; while pinned: turbo off (intel_pstate/no_turbo 1), ceiling 3500 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; at the end: turbo off (intel_pstate/no_turbo 1), ceiling 3500 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; after putting it back: turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7). Uncore limit at the end: min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); after putting it back: min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e). Each target's clock note gives every pass's average MHz on the engine CPUs and on the client CPUs; a pass whose average on either is more than 1% off the pinned 3500 MHz is flagged. Figures from runs made with turbo on are not comparable with these in absolute terms. engine CPUs 2-3,6-7 (cores 2,6 and 3,7), client CPUs 0-1,4-5 (cores 0,4 and 1,5); the client process was pinned; each engine was pinned to the engine CPUs for its turn and put back after (conditions.engines); an engine run-all started (and stopped) was asked to be created on them, and its notes say whether the host did so or it was moved there after the start.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Busy box: right before each pass's warm-up the run waits, up to 10 min, while processes outside the benchmark (everything but this client and the engine under test's cgroups) use more than 0.3 CPUs on average over the last 60 s or the last 5 s, neither window reaching back past the start of the target or of its engine (so the engine's own start-up is not outside work); if the box does not clear the pass runs anyway, flagged 'busy box', as is a pass whose own outside load is above the limit. Outside load counts busy = user + nice + system + irq + softirq + steal (guest time is already in user); CONFIG_IRQ_TIME_ACCOUNTING is not set, so task and cgroup run time include the interrupt and softirq time that hit them and it is added; CONFIG_PARAVIRT_TIME_ACCOUNTING is not set, so steal is added (/boot/config-6.8.0-142-generic) (conditions.cpuAccounting); each pass also records this client's own CPU time per search (conditions.passes[].clientCpuMsPerSearch). CPU clocks were sampled every 250 ms; each pass's min/median/max per CPU is in conditions.passes. CPU idle states, recorded and left as found: driver intel_idle, governor menu, intel_idle max_cstate 9; POLL on, C1 on, C1E on, C3 OFF on CPUs 0-7 (default disabled), C6 on (conditions.cpuIdle). How each target was reached: conditions.connections. Client build Release, .NET 10.0.12.

Read back in these runs from the engine or the machine:

> sqlitevec is embedded: it ran inside the client process on the client CPUs 0-1,4-5, sharing them with the client.

Read back in these runs from the engine or the machine:

> duckdb is embedded: it ran inside the client process on the client CPUs 0-1,4-5, sharing them with the client.

## Runs used

Runs used: 6 claim runs, which are among the 12 runs of the basis; each is listed below with its seed and start time.

The runs of v7 were also used by the set blocked-2026-10-06-v7, which was not published; its verdict, design/verdicts/v7-verdict.md, holds the word BLOCK.

The runs of v5 were also used by the set blocked-2026-10-05-v5, which was not published; its verdict, design/verdicts/v5-verdict.txt, holds the word BLOCK.

The runs of v6 were also used by the set blocked-2026-10-05-v6, which was not published; its verdict, design/verdicts/v6-verdict.md, holds the word BLOCK.

The results.md of run 20261006-130619-eshoponweb holds a framing sentence this report retired, on line 13.

The results.md of run 20261006-142724-eshoponweb holds a framing sentence this report retired, on line 13.

The results.md of run 20261006-154837-eshoponweb holds a framing sentence this report retired, on line 13.

The results.md of run 20261007-130619-eshoponweb holds a framing sentence this report retired, on line 13.

The results.md of run 20261007-142724-eshoponweb holds a framing sentence this report retired, on line 13.

The results.md of run 20261007-154837-eshoponweb holds a framing sentence this report retired, on line 13.

| run | session | seed | started (UTC) |
|---|---|---|---|
| 20261006-130619-eshoponweb | v7 | 701 | 2026-10-06T13:06:19Z |
| 20261006-142724-eshoponweb | v7 | 702 | 2026-10-06T14:27:24Z |
| 20261006-154837-eshoponweb | v7 | 703 | 2026-10-06T15:48:37Z |
| 20261007-130619-eshoponweb | v8 | 701 | 2026-10-07T13:06:19Z |
| 20261007-142724-eshoponweb | v8 | 702 | 2026-10-07T14:27:24Z |
| 20261007-154837-eshoponweb | v8 | 703 | 2026-10-07T15:48:37Z |
| 20261005-023459-eshoponweb | v5 (basis) | 501 | 2026-10-05T02:34:59Z |
| 20261005-031556-eshoponweb | v5 (basis) | 502 | 2026-10-05T03:15:56Z |
| 20261005-035711-eshoponweb | v5 (basis) | 503 | 2026-10-05T03:57:11Z |
| 20261005-073329-eshoponweb | v6 (basis) | 601 | 2026-10-05T07:33:29Z |
| 20261005-085448-eshoponweb | v6 (basis) | 602 | 2026-10-05T08:54:48Z |
| 20261005-101815-eshoponweb | v6 (basis) | 603 | 2026-10-05T10:18:15Z |

