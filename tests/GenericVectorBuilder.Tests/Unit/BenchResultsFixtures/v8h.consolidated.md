# Vector engine benchmark

redis had the lowest p50 latency: every other engine took at least 1.45 times as long, in each session; redis holds its data in memory.

Data: eshoponweb, 524 vectors of 1024 dimensions; 20 queries, top 10 hits each.

Machine: Intel(R) Xeon(R) CPU E5-1620 v3 @ 3.50GHz, 8 logical CPUs, 62.7 GiB of RAM.

At 524 vectors each figure is the cost of one request through the benchmark's client for that engine, and does not show how an index scales.

Of the 19 engines, 8 are reached through the benchmark's own HttpClient REST code: elasticsearch, vespa, opensearch, chroma, milvus, typesense, clickhouse and weaviate.

Session v7: runs 20261006-130619-eshoponweb, 20261006-142724-eshoponweb and 20261006-154837-eshoponweb, started from 2026-10-06T13:06:19Z to 2026-10-06T15:48:37Z.

Session v8: runs 20261007-205106-eshoponweb, 20261007-221429-eshoponweb and 20261007-233551-eshoponweb, started from 2026-10-07T20:51:06Z to 2026-10-07T23:35:51Z.

The runs of v8 were measured by build 9b924200abc3.

This report was consolidated by build 9b924200abc3, which also measured the runs of v8.

The runs of v8 read a question file with the same SHA256 hash as design/bench-inputs/questions_golden.json.

The runs of v7 record no question-file hash, and truthNdcg reads 0.5181699774768911 in all 6 claim runs.

> golden: 20 labelled questions from /home/dan/ForClaude/evalkit/questions_golden.json, embedded with qwen3-emb-0.6b (cached in /home/dan/gvb-data/bench-cache/golden-eshoponweb-68df42ca69efa079.json, no embedding calls)

An engine is shown ahead of another when, in each session separately, its slowest run beat the other's fastest run by at least 1.45 times; every other pair is not separated by this test.

## p50

p50 is the median latency of one searcher's searches, in ms; rows are in order of the median of 6 runs.

| engine | v7 min-max | v8 min-max | median of 6 | search | not separated from | flags |
|---|---|---|---|---|---|---|
| redis | 0.393-0.399 | 0.367-0.392 | 0.392 | approximate; recorded, documented |  |  |
| mariadb | 0.623-0.636 | 0.623-0.635 | 0.630 | approximate; measured | pgvector, qdrant, qdrant-hnsw, oracle |  |
| pgvector | 0.870-0.885 | 0.845-0.869 | 0.869 | approximate; measured | mariadb, qdrant, qdrant-hnsw, oracle, mongodb |  |
| qdrant | 0.881-0.886 | 0.887-0.892 | 0.886 | exact; measured | mariadb, pgvector, qdrant-hnsw, oracle, mongodb |  |
| qdrant-hnsw | 0.916-0.920 | 0.920-0.922 | 0.920 | approximate; measured | mariadb, pgvector, qdrant, oracle, elasticsearch, mongodb |  |
| oracle | 0.923-0.934 | 0.919-0.944 | 0.927 | approximate; measured | mariadb, pgvector, qdrant, qdrant-hnsw, elasticsearch, mongodb |  |
| elasticsearch | 1.310-1.329 | 1.312-1.325 | 1.319 | exact; measured | qdrant-hnsw, oracle, mongodb, sqlitevec, vespa | index-not-ready |
| mongodb | 1.262-1.414 | 1.368-1.389 | 1.386 | approximate; recorded (engine's own report) | pgvector, qdrant, qdrant-hnsw, oracle, elasticsearch, sqlitevec, vespa, opensearch | busy-box, segment-layout-differs |
| sqlitevec | 1.910-1.933 | 1.863-1.915 | 1.913 | exact; measured | elasticsearch, mongodb, vespa, opensearch, chroma, milvus |  |
| vespa | 1.947-1.970 | 1.871-1.922 | 1.934 | approximate; measured | elasticsearch, mongodb, sqlitevec, opensearch, chroma, milvus |  |
| opensearch | 1.995-2.157 | 1.983-2.065 | 2.011 | approximate; measured | mongodb, sqlitevec, vespa, chroma, milvus |  |
| chroma | 2.105-2.106 | 2.067-2.117 | 2.105 | approximate; recorded, unverified | sqlitevec, vespa, opensearch, milvus |  |
| milvus | 2.316-2.339 | 2.230-2.251 | 2.283 | approximate; recorded, unverified | sqlitevec, vespa, opensearch, chroma |  |
| sql-diskann | 3.596-3.694 | 3.564-3.583 | 3.589 | approximate; measured | duckdb, sql, typesense, clickhouse |  |
| duckdb | 3.666-3.678 | 3.616-3.641 | 3.654 | approximate; measured | sql-diskann, sql, typesense, clickhouse |  |
| sql | 4.016-4.060 | 3.926-3.942 | 3.979 | exact; measured | sql-diskann, duckdb, typesense, clickhouse, weaviate |  |
| typesense | 4.182-4.203 | 4.160-4.177 | 4.179 | approximate; measured | sql-diskann, duckdb, sql, clickhouse, weaviate |  |
| clickhouse | 4.941-4.973 | 4.840-4.869 | 4.905 | approximate; measured | sql-diskann, duckdb, sql, typesense, weaviate | unsettled |
| weaviate | 5.360-5.372 | 5.311-5.327 | 5.343 | approximate; recorded, unverified | sql, typesense, clickhouse |  |

redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database', and its compose file sets save "300 1" and appendonly no.

Run 20261006-130619-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261006-130619-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261006-142724-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261006-142724-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261006-154837-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261006-154837-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261007-205106-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261007-205106-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261007-221429-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261007-221429-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261007-233551-eshoponweb: elasticsearch's index state after the load read not ready, 0 of 524 vectors indexed.

Run 20261007-233551-eshoponweb: elasticsearch's index state after the searches read not ready, 0 of 524 vectors indexed.

Run 20261006-154837-eshoponweb: busy box during the one-searcher pass, with 0.350 CPUs of outside load.

Run 20261006-130619-eshoponweb: mongodb's index state after the searches reads 4 segment(s).

Run 20261006-142724-eshoponweb: mongodb's index state after the searches reads 2 segment(s).

Run 20261006-154837-eshoponweb: mongodb's index state after the searches reads 4 segment(s).

Run 20261007-205106-eshoponweb: mongodb's index state after the searches reads 3 segment(s).

Run 20261007-221429-eshoponweb: mongodb's index state after the searches reads 2 segment(s).

Run 20261007-233551-eshoponweb: mongodb's index state after the searches reads 4 segment(s).

Run 20261007-205106-eshoponweb: clickhouse was recorded as not settled before its timed passes.

Run 20261007-221429-eshoponweb: clickhouse was recorded as not settled before its timed passes.

Run 20261007-233551-eshoponweb: clickhouse was recorded as not settled before its timed passes.

## QPS@1

QPS@1 is searches per second of the same one-searcher pass as p50, so it is no second confirmation of the p50 order.

| engine | v7 min-max | v8 min-max | median of 6 | search | not separated from | flags |
|---|---|---|---|---|---|---|
| redis | 2519-2537 | 2559-2618 | 2548 | approximate; recorded, documented |  |  |
| mariadb | 1546-1578 | 1549-1576 | 1558 | approximate; measured | pgvector, qdrant, qdrant-hnsw |  |
| pgvector | 1107-1126 | 1127-1158 | 1126 | approximate; measured | mariadb, qdrant, qdrant-hnsw, oracle, mongodb |  |
| qdrant | 1111-1118 | 1103-1113 | 1112 | exact; measured | mariadb, pgvector, qdrant-hnsw, oracle, mongodb |  |
| qdrant-hnsw | 1071-1077 | 1072-1075 | 1075 | approximate; measured | mariadb, pgvector, qdrant, oracle, elasticsearch, mongodb |  |
| oracle | 1042-1055 | 1029-1060 | 1050 | approximate; measured | pgvector, qdrant, qdrant-hnsw, elasticsearch, mongodb |  |
| elasticsearch | 739-748 | 742-749 | 745 | exact; measured | qdrant-hnsw, oracle, mongodb, sqlitevec, vespa | index-not-ready |
| mongodb | 691-770 | 705-717 | 707 | approximate; recorded (engine's own report) | pgvector, qdrant, qdrant-hnsw, oracle, elasticsearch, sqlitevec, vespa, opensearch | busy-box, segment-layout-differs |
| sqlitevec | 506-514 | 514-529 | 514 | exact; measured | elasticsearch, mongodb, vespa, opensearch, chroma, milvus |  |
| vespa | 492-496 | 503-517 | 500 | approximate; measured | elasticsearch, mongodb, sqlitevec, opensearch, chroma, milvus |  |
| opensearch | 451-491 | 480-495 | 487 | approximate; measured | mongodb, sqlitevec, vespa, chroma, milvus |  |
| chroma | 469-469 | 468-478 | 469 | approximate; recorded, unverified | sqlitevec, vespa, opensearch, milvus |  |
| milvus | 405-409 | 423-427 | 416 | approximate; recorded, unverified | sqlitevec, vespa, opensearch, chroma |  |
| sql-diskann | 263-271 | 275-278 | 273 | approximate; measured | duckdb, sql, typesense, clickhouse |  |
| duckdb | 269-270 | 272-274 | 271 | approximate; measured | sql-diskann, sql, typesense, clickhouse |  |
| sql | 241-243 | 245-250 | 244 | exact; measured | sql-diskann, duckdb, typesense, clickhouse, weaviate |  |
| typesense | 235-236 | 236-237 | 236 | approximate; measured | sql-diskann, duckdb, sql, clickhouse, weaviate |  |
| clickhouse | 192-196 | 194-200 | 195 | approximate; measured | sql-diskann, duckdb, sql, typesense, weaviate | unsettled |
| weaviate | 165-167 | 167-167 | 167 | approximate; recorded, unverified | sql, typesense, clickhouse |  |

redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database', and its compose file sets save "300 1" and appendonly no.

## QPS@8

QPS@8 is searches per second with 8 searchers at once.

| engine | v7 min-max | v8 min-max | median of 6 | search | not separated from | flags |
|---|---|---|---|---|---|---|
| mariadb | 5911-5920 | 5926-5956 | 5923 | approximate; measured | redis, pgvector |  |
| redis | 5038-5120 | 5237-5258 | 5179 | approximate; recorded, documented | mariadb, pgvector |  |
| pgvector | 3910-4001 | 4006-4095 | 4004 | approximate; measured | mariadb, redis, qdrant-hnsw, qdrant, oracle, elasticsearch |  |
| qdrant-hnsw | 3440-3452 | 3468-3477 | 3460 | approximate; measured | pgvector, qdrant, oracle, elasticsearch |  |
| qdrant | 3274-3287 | 3280-3296 | 3284 | exact; measured | pgvector, qdrant-hnsw, oracle, elasticsearch, mongodb |  |
| oracle | 2655-2803 | 2792-2832 | 2803 | approximate; measured | pgvector, qdrant-hnsw, qdrant, elasticsearch, mongodb |  |
| elasticsearch | 2669-2708 | 2694-2732 | 2701 | exact; measured | pgvector, qdrant-hnsw, qdrant, oracle, mongodb | index-not-ready |
| mongodb | 2066-2352 | 2098-2150 | 2123 | approximate; recorded (engine's own report) | qdrant, oracle, elasticsearch, vespa, opensearch | segment-layout-differs |
| vespa | 1506-1565 | 1556-1582 | 1560 | approximate; measured | mongodb, opensearch, milvus, sqlitevec |  |
| opensearch | 1346-1516 | 1462-1516 | 1492 | approximate; measured | mongodb, vespa, milvus, sqlitevec |  |
| milvus | 1255-1263 | 1266-1274 | 1264 | approximate; recorded, unverified | vespa, opensearch, sqlitevec |  |
| sqlitevec | 1136-1142 | 1131-1140 | 1137 | exact; measured | vespa, opensearch, milvus, chroma |  |
| chroma | 777-777 | 779-791 | 778 | approximate; recorded, unverified | sqlitevec, sql-diskann, sql, duckdb, typesense |  |
| sql-diskann | 645-691 | 668-695 | 687 | approximate; measured | chroma, sql, duckdb, typesense |  |
| sql | 602-614 | 580-607 | 604 | exact; measured | chroma, sql-diskann, duckdb, typesense |  |
| duckdb | 576-576 | 578-583 | 577 | approximate; measured | chroma, sql-diskann, sql, typesense |  |
| typesense | 574-576 | 572-576 | 574 | approximate; measured | chroma, sql-diskann, sql, duckdb |  |
| weaviate | 305-305 | 306-306 | 306 | approximate; recorded, unverified |  |  |
| clickhouse | 375-428 | 336-380 | 378 | approximate; measured |  | unsettled, not-held |

clickhouse is shown and not ranked in this table: its timed eight-searcher pass was recorded NOT HELD in 3 of the 6 runs.

redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database', and its compose file sets save "300 1" and appendonly no.

Run 20261007-205106-eshoponweb: the timed eight-searcher pass of clickhouse read 336 QPS, 24% from the settled 418 QPS (limit 10%), and the run recorded it as NOT HELD.

Run 20261007-221429-eshoponweb: the timed eight-searcher pass of clickhouse read 380 QPS, 13% from the settled 432 QPS (limit 10%), and the run recorded it as NOT HELD.

Run 20261007-233551-eshoponweb: the timed eight-searcher pass of clickhouse read 349 QPS, 19% from the settled 416 QPS (limit 10%), and the run recorded it as NOT HELD.

## exact p50

Exact p50 is the median latency of each engine's own exact mode, which is a different operation per engine; the why table's CPU figures come from the other passes.

| engine | v7 min-max | v8 min-max | median of 6 | not separated from | flags |
|---|---|---|---|---|---|
| redis | 0.377-0.382 | 0.365-0.368 | 0.372 |  |  |
| qdrant-hnsw | 0.891-0.895 | 0.897-0.899 | 0.896 | mongodb, elasticsearch |  |
| mongodb | 1.242-1.263 | 1.211-1.235 | 1.239 | qdrant-hnsw, elasticsearch, mariadb | busy-box, segment-layout-differs |
| elasticsearch | 1.261-1.283 | 1.247-1.258 | 1.259 | qdrant-hnsw, mongodb, mariadb | index-not-ready |
| mariadb | 1.635-1.645 | 1.615-1.631 | 1.633 | mongodb, elasticsearch, sqlitevec, vespa, pgvector |  |
| sqlitevec | 1.906-1.936 | 1.874-1.917 | 1.910 | mariadb, vespa, pgvector, oracle, opensearch |  |
| vespa | 1.935-1.963 | 1.868-1.916 | 1.925 | mariadb, sqlitevec, pgvector, oracle, opensearch |  |
| pgvector | 2.210-2.257 | 2.183-2.193 | 2.202 | mariadb, sqlitevec, vespa, oracle, opensearch |  |
| oracle | 2.484-2.503 | 2.422-2.486 | 2.485 | sqlitevec, vespa, pgvector, opensearch, sql-diskann |  |
| opensearch | 2.615-2.750 | 2.553-2.658 | 2.636 | sqlitevec, vespa, pgvector, oracle, sql-diskann |  |
| sql-diskann | 3.740-3.790 | 3.573-3.631 | 3.685 | oracle, opensearch, duckdb |  |
| duckdb | 4.195-4.214 | 4.101-4.163 | 4.179 | sql-diskann, typesense |  |
| typesense | 5.660-5.682 | 5.630-5.640 | 5.650 | duckdb, clickhouse |  |
| clickhouse | 6.657-6.677 | 6.603-6.630 | 6.643 | typesense | unsettled |

sql has no exact pass; its search fact says: no index used, exact scan by design.

qdrant has no exact pass; its search fact says: no index used, exact scan by design.

milvus has no exact pass in these runs.

weaviate has no exact pass in these runs.

chroma has no exact pass in these runs.

redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database', and its compose file sets save "300 1" and appendonly no.

Run 20261006-130619-eshoponweb: busy box during the exact pass, with 0.307 CPUs of outside load.

Run 20261006-142724-eshoponweb: busy box during the exact pass, with 0.317 CPUs of outside load.

Run 20261006-154837-eshoponweb: busy box during the exact pass, with 0.301 CPUs of outside load.

## Recall

Recall counts hits of the exact top 10 over 20 queries, 200 per run; it is printed, never ranked.

The recall hits of v7 are the recall of each run times 20 queries times 10 hits, rounded; those runs record no hit count.

clickhouse's recall hits differ between runs: v7 199, 193, 198; v8 199, 199, 199.

| engine | hits per run | of | differs |
|---|---|---|---|
| clickhouse | v7: 199, 193, 198; v8: 199, 199, 199 | 200 | yes |
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

At one searcher, client CPU per search ran from 0.303 ms (mongodb) to 4.985 ms (duckdb).

At one searcher, engine CPU per search ran from 0.258 ms (redis) to 8.246 ms (weaviate) where measured.

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

sql-diskann recorded these search and index settings: L=48, M=8, R=48, StartId=306.

sql-diskann recorded no setting for how hard a query searches.

duckdb recorded these search and index settings: checkpoint_threshold=256MB, ef_construction=128, ef_search=100, hnsw_enable_experimental_persistence=true, m=16, metric=cosine.

sql recorded these search and index settings: description=exact VECTOR_DISTANCE cosine, no vector index (full scan).

typesense recorded these search and index settings: ef=100, ef_construction=128, k=top, m=16.

clickhouse recorded these search and index settings: M=16, ef_construction=128, hnsw_candidate_list_size_for_search=256.

weaviate recorded these search and index settings: ef=-1, efConstruction=128.

weaviate: the clause of its recorded index text that states how hard a query searches, with how each part is backed.

Documented on a saved page, not read back from the engine:

> ef=-1

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
| sqlitevec | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[sqlitevec].index+engine+durability |
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
| sql-diskann | index | DiskANN (preview) via VECTOR_SEARCH, cosine | recorded, unverified | unverified | in all 6 run(s) selected, at targets[sql-diskann].index |
| sql-diskann | search (approximate) | DiskANN index built and used | measured | read-back | in all 6 run(s) selected, at targets[sql-diskann].indexState.afterLoad.detail |
| sql-diskann | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[sql-diskann] |
| sql-diskann | protocol | Microsoft.Data.SqlClient | set-by-code | documented | src/GenericVectorBuilder.Bench/Targets/SqlDiskAnnSink.cs:5 |
| sql-diskann | protocol | vector as NVarChar value from ToJson | set-by-code | documented | src/GenericVectorBuilder.Bench/Targets/SqlDiskAnnSink.cs:305 |
| sql-diskann | set-by-setup | MSSQL_MEMORY_LIMIT_MB: "6144" | set-by-code | documented | deploy/engines/mssql.compose.yaml:49 |
| sql-diskann | set-by-setup | mem_limit: 8g | set-by-code | documented | deploy/engines/mssql.compose.yaml:56 |
| duckdb | index | HNSW (vss extension) FLOAT[n] metric=cosine m=16 ef_construction=128 | recorded, documented | documented | in all 6 run(s) selected, at targets[duckdb].index |
| duckdb | search (approximate) | EXPLAIN of the search shows HNSW_INDEX_SCAN | measured | read-back | in all 6 run(s) selected, at targets[duckdb].load.indexNote |
| duckdb | storage | not recorded | checked | measured | none of 4 words in 6 run(s) at targets[duckdb].index+engine+durability |
| duckdb | protocol | DuckDB.NET.Data | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs:7 |
| duckdb | set-by-setup | SET hnsw_enable_experimental_persistence = true | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs:750 |
| duckdb | set-by-setup | CheckpointThreshold 256MB | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/DuckDbSinkOptions.cs:49 |
| duckdb | set-by-setup | MemoryLimit 8GB | set-by-code | documented | src/GenericVectorBuilder.Engines/Sinks/DuckDbSinkOptions.cs:38 |
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
| redis | 0.258 | 0.231 | 0.555 | 0.358 | 1.21 |
| mariadb | 0.454 | 0.621 | 0.485 | 0.377 | 3.68 |
| pgvector | 0.716 | 0.972 | 0.678 | 0.539 | 3.91 |
| qdrant | 0.793 | 1.004 | 0.737 | 0.535 | 3.30 |
| qdrant-hnsw | 0.731 | 0.933 | 0.733 | 0.522 | 3.24 |
| oracle | 0.577 | 0.720 | 0.885 | 0.643 | 2.03 |
| elasticsearch | 1.045 | 1.379 | 0.455 | 0.493 | 3.74 |
| mongodb | 1.322 | 1.781 | 0.303 | 0.285 | 3.80 |
| sqlitevec |  |  | 2.017 | 3.507 |  |
| vespa | 2.282 | 2.434 | 0.916 | 0.873 | 3.82 |
| opensearch | 1.750 | 2.554 | 0.466 | 0.505 | 3.87 |
| chroma | 2.464 | 2.720 | 0.475 | 0.472 | 2.15 |
| milvus | 2.743 | 2.953 | 0.546 | 0.630 | 3.74 |
| sql-diskann | 3.481 | 5.631 | 1.010 | 1.083 | 3.91 |
| duckdb |  |  | 4.985 | 6.833 |  |
| sql | 3.876 | 5.933 | 1.211 | 1.195 | 3.50 |
| typesense | 3.875 | 6.704 | 0.523 | 0.582 | 3.86 |
| clickhouse | 7.168 | 11.374 | 0.858 | 1.054 | 3.97 |
| weaviate | 8.246 | 12.959 | 0.563 | 0.609 | 3.97 |

## Threshold and basis

The threshold is 45%: the smallest multiple of 5% at least 5% above the largest move in the basis, 35.82%, and never below 35%.

The basis holds the 12 runs of sessions v5, v6, v7 and v8, all with machine control on.

The clock was held in the runs of v7 and v8 and not in those of v5 and v6.

Basis moves are taken between runs in which the engine recorded the same setup: its engine text, index text, search settings, durability text, engine files, engine settings, image id and hosting.

A field other than the engine text and the index text that one of two runs did not record is not compared.

The largest move was 35.82%, the QPS@8 ratio of clickhouse/oracle between runs 20261006-142724-eshoponweb (seed 702) and 20261007-205106-eshoponweb (seed 801).

Each move is rounded up to the next basis point before it is printed: the largest, 35.815% unrounded, prints as 35.82%.

Those two runs differ in build.

Run 20261007-205106-eshoponweb: the timed eight-searcher pass of clickhouse read 336 QPS, 24% from the settled 418 QPS (limit 10%), and the run recorded it as NOT HELD.

The largest move includes a NOT HELD pass; with clickhouse left out of the basis, the largest move is 29.08%, the QPS@8 ratio of mongodb/oracle, and the threshold would be 35%.

p50, between basis runs of one recorded setup: largest moves 15.28% (redis) for one engine and 26.34% (mongodb/redis) for a pair.

p50, between those runs that also share turbo, uncore, warm-up and rehearsal: largest moves 12.02% (mongodb) for one engine and 18.88% (mongodb/redis) for a pair.

QPS@1, between basis runs of one recorded setup: largest moves 11.49% (mongodb) for one engine and 19.33% (mongodb/pgvector) for a pair.

QPS@1, between those runs that also share turbo, uncore, warm-up and rehearsal: largest moves 11.49% (mongodb) for one engine and 14.01% (mongodb/sqlitevec) for a pair.

QPS@8, between basis runs of one recorded setup: largest moves 27.36% (clickhouse) for one engine and 35.82% (clickhouse/oracle) for a pair.

QPS@8, between those runs that also share turbo, uncore, warm-up and rehearsal: largest moves 27.36% (clickhouse) for one engine and 35.82% (clickhouse/oracle) for a pair.

exact p50, between basis runs of one recorded setup: largest moves 20.23% (redis) for one engine and 19.66% (qdrant-hnsw/redis) for a pair.

exact p50, between those runs that also share turbo, uncore, warm-up and rehearsal: largest moves 7.74% (opensearch) for one engine and 8.41% (opensearch/sqlitevec) for a pair.

Changes of recorded setup left out of the basis moves: 4, in clickhouse, duckdb, mariadb and sqlitevec.

Each is listed with the field that changed, the move it hides and the threshold it would give.

Between its v6 runs and its v7 and v8 runs, clickhouse recorded a different engine.

Counted across that change, the largest one-engine move is 47.36% (QPS@8) and the largest pair move 52.08% (clickhouse/redis QPS@8), and the threshold would be 60%.

The two runs of that one-engine move also differ in turbo, uncore, warmup, build and median MHz.

Between its v5 runs and its v6, v7 and v8 runs, duckdb recorded a different index.

Counted across that change, the largest one-engine move is 115.56% (QPS@8) and the largest pair move 144.95% (duckdb/oracle QPS@8), and the threshold would be 150%.

The two runs of that one-engine move also differ in warmup, rehearsal, build, median MHz and whether searchSettings was recorded.

Between its v5 runs and its v6, v7 and v8 runs, mariadb recorded a different durability.

Counted across that change, the largest one-engine move is 4.09% (exact p50) and the largest pair move 17.85% (mariadb/oracle QPS@8), and the threshold would be 45%.

The two runs of that one-engine move also differ in turbo, uncore, warmup, rehearsal, build, median MHz and whether searchSettings was recorded.

Between its v5 runs and its v6, v7 and v8 runs, sqlitevec recorded a different index.

Counted across that change, the largest one-engine move is 119.36% (QPS@8) and the largest pair move 155.08% (oracle/sqlitevec QPS@8), and the threshold would be 165%.

The two runs of that one-engine move also differ in warmup, rehearsal, build, median MHz and whether searchSettings was recorded.

vespa QPS@8, seed list 502 and 503, is left out of the basis (kind: method defect); design/verdicts/v5-verdict.txt, item 2, holds the words: The @8 pass ran first in runs 502 and 503.

elasticsearch QPS@8, seed list 503, is left out of the basis (kind: method defect); design/verdicts/v5-verdict.txt, item 2, holds the words: run 503, the one run where @8 was its first pass.

clickhouse every metric, seed list 501, 502 and 503, is left out of the basis (kind: unrecorded config change); design/verdicts/v6-verdict.md, item 1, holds the words: after the v5 runs ended at 04:38.

With the method defect rows kept in, the largest moves would be 74.42% for one engine and 95.64% for a pair, and the threshold 105%.

With the unrecorded config change rows kept in, the largest moves would be 27.36% for one engine and 35.82% for a pair, and the threshold 45%.

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
| 20261007-205106-eshoponweb | v8 | 801 | turbo off (no_turbo 0 -> 1) | uncore pinned (MSR 0x620 = 0x1e1e) | settle check with level test | rehearsal 30 s | /home/dan/gvb-work/v8-run/bin/GenericVectorBuilder.Bench.dll (commit 9b924200abc3f2c3e41c2a9520e5c7bb16748e6c) |
| 20261007-221429-eshoponweb | v8 | 802 | turbo off (no_turbo 0 -> 1) | uncore pinned (MSR 0x620 = 0x1e1e) | settle check with level test | rehearsal 30 s | /home/dan/gvb-work/v8-run/bin/GenericVectorBuilder.Bench.dll (commit 9b924200abc3f2c3e41c2a9520e5c7bb16748e6c) |
| 20261007-233551-eshoponweb | v8 | 803 | turbo off (no_turbo 0 -> 1) | uncore pinned (MSR 0x620 = 0x1e1e) | settle check with level test | rehearsal 30 s | /home/dan/gvb-work/v8-run/bin/GenericVectorBuilder.Bench.dll (commit 9b924200abc3f2c3e41c2a9520e5c7bb16748e6c) |

| metric | one engine, same recorded setup | pair, same recorded setup | one engine, also same run conditions | pair, also same run conditions |
|---|---|---|---|---|
| p50 | 15.28% redis | 26.34% mongodb/redis | 12.02% mongodb | 18.88% mongodb/redis |
| QPS@1 | 11.49% mongodb | 19.33% mongodb/pgvector | 11.49% mongodb | 14.01% mongodb/sqlitevec |
| QPS@8 | 27.36% clickhouse | 35.82% clickhouse/oracle | 27.36% clickhouse | 35.82% clickhouse/oracle |
| exact p50 | 20.23% redis | 19.66% qdrant-hnsw/redis | 7.74% opensearch | 8.41% opensearch/sqlitevec |

| differences between the two runs of the largest move |
|---|
| build: /home/dan/gvb-work/lanes/v7-final vs /home/dan/gvb-work/v8-run/bin/GenericVectorBuilder.Bench.dll (commit 9b924200abc3f2c3e41c2a9520e5c7bb16748e6c) |

| setup split | fields changed | between | one-engine move hidden | pair move hidden | threshold if counted |
|---|---|---|---|---|---|
| clickhouse | engine | v6 vs v7 and v8 | 47.36% QPS@8 | 52.08% clickhouse/redis QPS@8 | 60% |
| duckdb | index | v5 vs v6, v7 and v8 | 115.56% QPS@8 | 144.95% duckdb/oracle QPS@8 | 150% |
| mariadb | durability | v5 vs v6, v7 and v8 | 4.09% exact p50 | 17.85% mariadb/oracle QPS@8 | 45% |
| sqlitevec | index | v5 vs v6, v7 and v8 | 119.36% QPS@8 | 155.08% oracle/sqlitevec QPS@8 | 165% |

| run not in the basis | reason |
|---|---|
| 20261003-183955-eshoponweb | machineControl not recorded |
| 20261003-184207-eshoponweb | machineControl not recorded |
| 20261003-184445-eshoponweb | machineControl not recorded |
| 20261003-234838-eshoponweb | machineControl not recorded |
| 20261004-130858-eshoponweb | machineControl not recorded |
| 20261004-132735-eshoponweb | machineControl not recorded |

## Drift between the sessions

From v7 to v8, a ranked engine's session median moved 1.44% in the middle case and 4.66% at most (opensearch, exact p50).

6 orders held in v7 and not in v8, and 9 the other way; they are listed and not published.

20 unordered pairs sat between 1.35 and 1.45 times in each session, always in one direction; they are listed as close to the line.

1 ordered pair cleared 1.45 times by 25 bp or less in its lowest session; it is listed as on the line.

The sessions differ in build, day and outside load, and this test does not separate those from the engines' own drift.

The median outside load of a pass, per run, was 0.2095 to 0.2135 CPUs in v7 and 0.14 to 0.1435 in v8.

Both sessions ran with the clock held; drift under other conditions is not measured here.

| unconfirmed order | ahead | behind | held in |
|---|---|---|---|
| p50 | elasticsearch | vespa | v7 |
| p50 | mariadb | oracle | v7 |
| p50 | pgvector | mongodb | v8 |
| p50 | qdrant | mongodb | v8 |
| p50 | qdrant-hnsw | mongodb | v8 |
| QPS@1 | elasticsearch | vespa | v7 |
| QPS@1 | sql | weaviate | v8 |
| QPS@1 | pgvector | mongodb | v8 |
| QPS@1 | qdrant | mongodb | v8 |
| QPS@1 | qdrant-hnsw | mongodb | v8 |
| QPS@8 | pgvector | elasticsearch | v8 |
| QPS@8 | mariadb | pgvector | v7 |
| QPS@8 | sqlitevec | chroma | v7 |
| QPS@8 | qdrant | mongodb | v8 |
| exact p50 | oracle | sql-diskann | v7 |

| close to the line | ahead | behind | lowest ratio, bp |
|---|---|---|---|
| p50 | oracle | elasticsearch | 13892 |
| p50 | oracle | mongodb | 13508 |
| p50 | elasticsearch | sqlitevec | 14064 |
| p50 | qdrant-hnsw | elasticsearch | 14231 |
| p50 | mariadb | qdrant | 13846 |
| p50 | mongodb | opensearch | 14111 |
| p50 | mariadb | qdrant-hnsw | 14393 |
| QPS@1 | duckdb | clickhouse | 13573 |
| QPS@1 | mongodb | vespa | 13639 |
| QPS@1 | oracle | elasticsearch | 13725 |
| QPS@1 | oracle | mongodb | 13525 |
| QPS@1 | elasticsearch | sqlitevec | 14041 |
| QPS@1 | qdrant-hnsw | elasticsearch | 14306 |
| QPS@1 | mariadb | qdrant | 13826 |
| QPS@1 | mongodb | opensearch | 14078 |
| QPS@1 | mariadb | qdrant-hnsw | 14354 |
| QPS@1 | typesense | weaviate | 14065 |
| QPS@8 | pgvector | oracle | 13947 |
| QPS@8 | mongodb | opensearch | 13628 |
| exact p50 | qdrant-hnsw | elasticsearch | 13867 |

| on the line | ahead | behind | lowest ratio, bp |
|---|---|---|---|
| p50 | sql-diskann | weaviate | 14508 |

## Disclosures

In v7, every run held the clock: turbo off, MSR 0x620 at 0x1e1e, top ratio 35 (nominally 3500 MHz); the median of the kernel's pass medians is 3492 MHz; the ceiling before was 3600 MHz.

The median rule: a pass is off when the median, over its engine CPUs or over its client CPUs, of each CPU's median MHz is more than 1% from the pinned 3500 MHz.

In v7, by the median rule, 0 of 156 passes were off the pinned clock and 0 had a CPU group not read.

In v8, every run held the clock: turbo off, MSR 0x620 at 0x1e1e, top ratio 35 (nominally 3500 MHz); the median of the kernel's pass medians is 3492 MHz; the ceiling before was 3600 MHz.

In v8, by the median rule, 0 of 156 passes were off the pinned clock and 0 had a CPU group not read.

Run 20261006-130619-eshoponweb flagged oracle's eight-searcher pass as off its clock by a mean; the medians read 3492 MHz on engine CPUs and 3492 MHz on client CPUs, within 1%.

Run 20261006-142724-eshoponweb flagged oracle's eight-searcher pass as off its clock by a mean; the medians read 3492 MHz on engine CPUs and 3492 MHz on client CPUs, within 1%.

Run 20261006-154837-eshoponweb flagged oracle's eight-searcher pass as off its clock by a mean; the medians read 3492 MHz on engine CPUs and 3492 MHz on client CPUs, within 1%.

Every claim run used the performance governor, with the client on CPUs 0-1,4-5.

The container engines clickhouse, vespa, oracle, elasticsearch, sql, pgvector, qdrant, opensearch, qdrant-hnsw, redis, milvus, mariadb, weaviate, mongodb, chroma, typesense and sql-diskann ran on CPUs 2-3,6-7.

The embedded engines sqlitevec and duckdb ran inside the client process on CPUs 0-1,4-5.

Every v8 run recorded boot a2f94313-eeda-460c-8474-8a9ee898f123 at 2026-10-03T15:53:39Z, before the first v7 run started at 2026-10-06T13:06:19Z, so v7 ran on that boot too.

Each of the 17 container targets ran one image id, equal to its pin, in every v8 run; the ids are in the images table.

Each of those images was last tagged on this box before the first v7 run started at 2026-10-06T13:06:19Z.

redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database'; its compose file sets save "300 1" and appendonly no.

Each engine's durability text, as run 20261007-205106-eshoponweb recorded it, follows clause by clause with how each clause is backed; the program wrote the text, and its figures were not re-derived for this report.

Strace logs of measurements cited in those texts are saved for clickhouse, milvus, mongodb, qdrant and qdrant-hnsw, in design/engine-docs/durability-logs-2026-10-04, each listed with its hash in SHA256SUMS.

No log is saved for the measurements cited in the durability texts of oracle, pgvector, sqlitevec, redis, weaviate, chroma, typesense and duckdb.

clickhouse: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Acknowledged inserts are not fsynced. The sink creates plain MergeTree tables, so the MergeTree settings are the defaults: fsync_after_insert 0 and fsync_part_directory 0 (system.merge_tree_settings), and async_insert 1 with wait_for_async_insert 1 is the server default for the gvb login (system.settings), so an insert is acknowledged once its part is written, not once it is on disk.

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

> Measured 2026-10-04 with strace over 30 acknowledged single-row inserts (30 Ok rows in system.asynchronous_insert_log):

Backed by a saved log or measurement, not read back from the engine in these runs:

> zero fsync, fdatasync or sync_file_range calls,

> while the control, a table created with fsync_after_insert=1, made 61 fdatasync calls

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

> for 5 inserts.

Typed in the program's own text; not read back, measured or backed by a saved source:

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

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

> Measured 2026-10-04: 50 separate client commits raised V$SYSSTAT 'redo synch writes' by 55 (the 50 commits plus the CREATE and DROP of the probe table), and the log writer and the datafile writer hold their files open with O_DSYNC (open flags 02110002, filesystemio_options none).

Typed in the program's own text; not read back, measured or backed by a saved source:

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

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

> every commit is flushed to the write-ahead log before it returns and HNSW index changes are WAL-logged, so a crash loses no committed row (max_wal_size 4GB only spaces out checkpoints; measured with docker kill, which is SIGKILL, right after a 2,000-vector load and a restart: all 2,000 rows were there and the HNSW index was valid and used by the default search)

sqlitevec: its recorded durability text, clause by clause, with how each clause is backed.

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> PRAGMA journal_mode=WAL and PRAGMA synchronous=NORMAL (set by this sink when it opens the file):

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

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

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

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

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

> every object is appended to the LSM write-ahead log with a plain write and no fsync, and the log is fsynced only when its memtable is flushed, 60 seconds after the last write (PERSISTENCE_MEMTABLES_FLUSH_IDLE_AFTER_SECONDS default; measured with strace: 2,080 writes into the objects log during a 2,000-object load, the first fsync 60 s after the last write), so a power cut can lose the last minute of writes; the HNSW commit log is buffered inside the process (77 writes for 2,000 vectors), so a killed process loses the newest vectors from the vector index while their objects survive (measured with docker kill, which is SIGKILL, one second after the load and a restart, two runs each: 0 to 1 of 50, 264 to 267 of 300 and 1,992 to 1,997 of 2,000 stored objects were still reachable through a vector search)

mongodb: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Acknowledged writes are journaled before the acknowledgement. The sink sets no write concern, so the server default applies: getDefaultRWConcern gives w majority and the one-member replica set (--replSet gvbmongo in the image) has writeConcernMajorityJournalDefault true, with WiredTiger journaling on (journalCommitInterval 100 ms is only the interval for unacknowledged work).

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

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

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

> every write is committed to chroma.sqlite3 with fsync of the journal, the directory and the database file before the call returns (strace: 15 database fsyncs and 38 journal fsyncs for one create, four 500-vector upserts and one delete), and the HNSW files are written every sync_threshold=1000 vectors and rebuilt from the SQLite log after a crash; measured with kill -9 and a restart: all 50, 300 and 2,000 vectors were still stored and searchable

typesense: its recorded durability text, clause by clause, with how each clause is backed.

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

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

Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:

> only spaces out the checkpoints that write the database file; measured with strace: 42 WAL fsyncs for 40 single-record commits plus 2 setup statements), so an operating-system crash or power cut loses no committed row; measured with kill -9 right after the last commit and a reopen: all 50, 300 and 2,000 rows and an HNSW index that counted the same number were recovered from the log; not tested and documented by DuckDB: the HNSW index is file-backed only through hnsw_enable_experimental_persistence = true,

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> which this sink turns on,

Typed in the program's own text; not read back, measured or backed by a saved source:

> and WAL recovery for such custom indexes is not complete, so a crash during a checkpoint or a later commit can damage the index while the rows survive

The engine settings table lists what run 20261007-205106-eshoponweb read from each running engine or set from a repository file, as each row's how column says.

qdrant's measured search fact says: no index used, exact scan by design.

oracle's recorded index text says: Oracle Free caps itself at 2 CPUs.

elasticsearch's measured search fact says: no HNSW graph, searches scan all 524 vectors.

sqlitevec's measured search fact says: no index, exact scan by design.

sql's measured search fact says: no index used, exact scan by design.

mongodb's search fact is the engine's own report; its segments held at most 278 vectors, below the 1,043 at which elasticsearch's recorded text says a segment gets a graph, and no graph was checked.

**clickhouse 20261006-130619-eshoponweb**

> 4.21 GiB added by this load (4.21 GiB (whole engine data folder), 99.17 KiB before)

**clickhouse 20261006-142724-eshoponweb**

> 2.57 GiB added by this load (6.79 GiB (whole engine data folder), 4.22 GiB before)

**clickhouse 20261006-154837-eshoponweb**

> 0 B added by this load (6.79 GiB (whole engine data folder), 6.79 GiB before)

Run 20261007-205106-eshoponweb: clickhouse's data folder held 4559215474 bytes at its start, 14691380 bytes after its system log tables were truncated and it stopped changing, and 5699801237 bytes at its end.

Run 20261007-205106-eshoponweb: the reset truncated 9 MergeTree log tables, whose active bytes by clickhouse's own count were 2.09 GiB before and 0 B after.

Run 20261007-221429-eshoponweb: clickhouse's data folder held 5707585756 bytes at its start, 2917655627 bytes after its system log tables were truncated and it stopped changing, and 7408848771 bytes at its end.

Run 20261007-221429-eshoponweb: the reset truncated 9 MergeTree log tables, whose active bytes by clickhouse's own count were 183.97 KiB before and 0 B after.

Run 20261007-233551-eshoponweb: clickhouse's data folder held 7426117183 bytes at its start, 2246745378 bytes after its system log tables were truncated and it stopped changing, and 7261702692 bytes at its end.

Run 20261007-233551-eshoponweb: the reset truncated 9 MergeTree log tables, whose active bytes by clickhouse's own count were 185.06 KiB before and 0 B after.

Segment layouts differed between runs for mongodb; each run's layout is in its row flags.

An observer process ran beside the v8 runs; its own CPU, at most 0.138 CPUs in a pass, counts as outside load, and its summary is observer/summary.json beside this report.

By APERF and MPERF, the largest deviation of one CPU in a pass was 0.66%: oracle's eight-searcher pass on CPU 7, at 3477.0 MHz against the pin of 3500 MHz.

The observer read MSR 0x620 as 0x1e1e.

oracle's eight-searcher pass ran 0.27% to 0.66% under the pin on every CPU in all 3 runs of v8; no other pass was more than 0.04% from it, and why is not known.

Run 20261007-205106-eshoponweb: oracle's eight-searcher pass averaged 3085.5 MHz on the engine CPUs and 3189.8 MHz on the client CPUs, as in v7; a mean rule flags it and the median rule does not.

Run 20261007-221429-eshoponweb: oracle's eight-searcher pass averaged 3206.2 MHz on the engine CPUs and 3299.0 MHz on the client CPUs, as in v7; a mean rule flags it and the median rule does not.

Run 20261007-233551-eshoponweb: oracle's eight-searcher pass averaged 3063.0 MHz on the engine CPUs and 3189.2 MHz on the client CPUs, as in v7; a mean rule flags it and the median rule does not.

The observer was not pinned: in the v8 runs 34.8, 33.8 and 33.5 percent of its resident-thread ticks were seen on engine CPUs 2-3,6-7, and it used at most 0.0755 CPUs by cgroup.

The observer saw a systemd timer fire during 14 timed passes of the v8 runs.

An observer process ran beside the v7 runs; its own CPU, at most 0.123 CPUs in a pass, counts as outside load, and its summary is observer/summary-v7.json beside this report.

By APERF and MPERF, the largest deviation of one CPU in a pass was 0.73%: oracle's eight-searcher pass on CPU 2, at 3474.3 MHz against the pin of 3500 MHz.

The observer read MSR 0x620 as 0x1e1e.

oracle's eight-searcher pass ran 0.41% to 0.73% under the pin on every CPU in all 3 runs of v7; no other pass was more than 0.05% from it, and why is not known.

The observer summary does not cover systemd timers for every v7 run.

CPU idle states were recorded and left as found: driver intel_idle, governor menu.

Outside load is busy CPU less this client's and the followed engine cgroups'; dockerd's cgroup is one of them for a compose engine, so its CPU counts as benchmark work, not outside load.

The runs of v7 did not record how dockerd's CPU was counted.

> engine CPU per search is the CPU time (cpu.stat usage_usec) of the cgroups each target's turn followed (conditions.engines[].cgroups, dockerd's left out), read with every 250 ms sample, interpolated to the pass's timed window and divided by its completed searches; engineCpusBusy is that CPU time over the window's length. A followed cgroup counts every process in it. dockerd's cgroup (/sys/fs/cgroup/system.slice/docker.service, holding docker-proxy, dockerd, for every container on the box) is left out of engine CPU per search and given as dockerdCpuMsPerSearch; it is subtracted from outside load as benchmark work during the turns of oracle, mongodb, redis, typesense, clickhouse, sql, opensearch, milvus, sql-diskann, qdrant-hnsw, weaviate, pgvector, vespa, elasticsearch, mariadb, chroma, qdrant, and counts as outside load during the turns of duckdb, sqlitevec. containerd-shim runs in /sys/fs/cgroup/system.slice/containerd.service, which is in neither engine figure and counts as outside load. An embedded engine runs in this client process, so its CPU is in clientCpuMsPerSearch and its engine figure is null with the reason; a target whose engine has no cgroup followed, or whose containers could not all be pinned, also gets null with the reason, never 0.

The test OutsideLoad_IsTheV7AccountingBitForBit holds the formula that turns CPU counters into outside load equal to the formula of v7, on fixed counters; it does not make the measured load of two sessions equal.

Every order here was measured at the search settings each run recorded for the engine, listed in the why section; this report makes no claim at other settings.

A route each run recorded in conditions.connections reads: container address on its Docker network, not the published port (no docker-proxy).

A route each run recorded in conditions.connections reads: in this process (embedded), no network.

| run | recorded warning | engine median MHz | client median MHz |
|---|---|---|---|
| 20261006-130619-eshoponweb | WARNING: clock off its pinned value during oracle default@8: the engine CPUs averaged 3121 MHz, -10.8% against the pinned 3500 MHz; the client CPUs averaged 3141 MHz, -10.3% against the pinned 3500 MHz (limit 1%); this pass is not comparable with passes at the pinned clock. | 3492 | 3492 |
| 20261006-142724-eshoponweb | WARNING: clock off its pinned value during oracle default@8: the engine CPUs averaged 3129 MHz, -10.6% against the pinned 3500 MHz; the client CPUs averaged 3230 MHz, -7.7% against the pinned 3500 MHz (limit 1%); this pass is not comparable with passes at the pinned clock. | 3492 | 3492 |
| 20261006-154837-eshoponweb | WARNING: clock off its pinned value during oracle default@8: the engine CPUs averaged 3168 MHz, -9.5% against the pinned 3500 MHz; the client CPUs averaged 3214 MHz, -8.2% against the pinned 3500 MHz (limit 1%); this pass is not comparable with passes at the pinned clock. | 3492 | 3492 |

| engine | image | id | last tagged (UTC) |
|---|---|---|---|
| clickhouse | clickhouse/clickhouse-server:26.3.39.7 | sha256:3a91276f066905da0edbbd622d3fc2a2df632c87ea7fe32ef4e74fdf8c9567b0 | 2026-10-03T14:12:01.743437616Z |
| vespa | vespaengine/vespa:8.754.14 | sha256:5c30f5c41e7563498c4f925db6a837a3848f04726a3ed26aed4a7c8ab69f18fd | 2026-10-03T14:13:52.169232039Z |
| oracle | gvenzl/oracle-free:23-slim-faststart | sha256:f5ff19033860d662c821cb04eb10483fa94f14f78eae252d054291ea07028093 | 2026-10-03T14:17:05.132133694Z |
| elasticsearch | elasticsearch:9.5.3 | sha256:e23d4758358a4e356cc2ef3259a5a6f345cacc1722c10dffe6d7150a1a78e52f | 2026-10-03T14:07:11.337389556Z |
| sql | mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04@sha256:2b5b581621126574f3d1f75e78d3eebe8d05aedb59ad0cfdf9aa42cb0634d726 | sha256:2b5b581621126574f3d1f75e78d3eebe8d05aedb59ad0cfdf9aa42cb0634d726 | 2026-10-04T22:02:11.293843649Z |
| pgvector | pgvector/pgvector:0.8.7-pg17-trixie | sha256:7a7e9f22015b67edb4bef5c59daeebcd7e74bfa570df6ce60ae01237c8648a84 | 2026-10-03T14:04:52.665253039Z |
| qdrant | qdrant/qdrant:v1.17.0@sha256:f1c7272cdac52b38c1a0e89313922d940ba50afd90d593a1605dbbc214e66ffb | sha256:f1c7272cdac52b38c1a0e89313922d940ba50afd90d593a1605dbbc214e66ffb | 2026-10-04T22:03:16.973657523Z |
| opensearch | opensearchproject/opensearch:3.9.0 | sha256:adfa61f85025d06b4aeb562e7e74fde7e31c437039c93c3862c17e9acebd6c7c | 2026-10-03T14:09:29.512875356Z |
| qdrant-hnsw | qdrant/qdrant:v1.17.0@sha256:f1c7272cdac52b38c1a0e89313922d940ba50afd90d593a1605dbbc214e66ffb | sha256:f1c7272cdac52b38c1a0e89313922d940ba50afd90d593a1605dbbc214e66ffb | 2026-10-04T22:03:16.973657523Z |
| redis | redis:8.10.2 | sha256:6f81e8915c60b065a524e6967e0ad1c639ba6efa84d669f823683ea04d9150ee | 2026-10-03T14:09:34.185138197Z |
| milvus | milvusdb/milvus:v2.6.25 | sha256:29f7668e64df1c6d5cdadbfbee99f38c72e4001054f149a298725acae57230ad | 2026-10-03T14:05:28.412151323Z |
| mariadb | mariadb:11.8.9 | sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15 | 2026-10-03T14:10:11.675870329Z |
| weaviate | semitechnologies/weaviate:1.39.8 | sha256:f6f4a5961f99e8718a02c822ca6a0d65ed3f9c567734419a7b2144933014f13a | 2026-10-03T14:05:40.948499656Z |
| mongodb | mongodb/mongodb-atlas-local:8.0.32 | sha256:1985314b0ded756ba965e0f400d17406b7a89bec659d4763a490f42f006e4fbd | 2026-10-03T14:11:20.531944474Z |
| chroma | chromadb/chroma:latest | sha256:1e0b73a187a28757c572acba508c46f48c9e8b0acaf5c20e6d95cdedce1acdf6 | 2026-10-03T14:09:56.392230738Z |
| typesense | typesense/typesense:30.2 | sha256:610f2d34b1f93d00762869da2c67736775e5798d19a2c8b91b014b8a0cc1e110 | 2026-10-03T14:12:37.929033132Z |
| sql-diskann | mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04@sha256:2b5b581621126574f3d1f75e78d3eebe8d05aedb59ad0cfdf9aa42cb0634d726 | sha256:2b5b581621126574f3d1f75e78d3eebe8d05aedb59ad0cfdf9aa42cb0634d726 | 2026-10-04T22:02:11.293843649Z |

| engine | setting | value | how |
|---|---|---|---|
| clickhouse | HostConfig.Memory | 8589934592 | read: docker inspect HostConfig.Memory (0 = no limit) |
| clickhouse | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| clickhouse | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| clickhouse | max_threads | \'auto(4)\' | read: ClickHouse system.settings, name max_threads, over HTTP |
| vespa | HostConfig.Memory | 6442450944 | read: docker inspect HostConfig.Memory (0 = no limit) |
| vespa | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| vespa | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| oracle | HostConfig.Memory | 6442450944 | read: docker inspect HostConfig.Memory (0 = no limit) |
| oracle | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| oracle | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| oracle | cpu_count | 2 | read: docker exec gvb-oracle sqlplus / as sysdba, V$PARAMETER and V$INSTANCE at the instance |
| oracle | sga_target | 1610612736 | read: docker exec gvb-oracle sqlplus / as sysdba, V$PARAMETER and V$INSTANCE at the instance |
| oracle | oracle_version | 23.26.3.0.0 | read: docker exec gvb-oracle sqlplus / as sysdba, V$PARAMETER and V$INSTANCE at the instance |
| elasticsearch | HostConfig.Memory | 6442450944 | read: docker inspect HostConfig.Memory (0 = no limit) |
| elasticsearch | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| elasticsearch | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| elasticsearch | heap_init_in_bytes | 2147483648 | read: GET /_nodes/jvm (nodes.<id>.jvm.mem.heap_init_in_bytes and heap_max_in_bytes) |
| elasticsearch | heap_max_in_bytes | 2147483648 | read: GET /_nodes/jvm (nodes.<id>.jvm.mem.heap_init_in_bytes and heap_max_in_bytes) |
| sql | HostConfig.Memory | 8589934592 | read: docker inspect HostConfig.Memory (0 = no limit) |
| sql | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| sql | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| pgvector | HostConfig.Memory | 8589934592 | read: docker inspect HostConfig.Memory (0 = no limit) |
| pgvector | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| pgvector | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| pgvector | shared_buffers | 2GB | read: docker exec gvb-pgvector psql current_setting |
| pgvector | enable_seqscan | off | set: src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs#SET LOCAL enable_seqscan = off; SET LOCAL jit = off; |
| pgvector | jit | off | set: src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs#SET LOCAL enable_seqscan = off; SET LOCAL jit = off; |
| sqlitevec | sqlite_version | 3.53.3 | read: SELECT sqlite_version() on an in-memory SQLite connection |
| sqlitevec | synchronous | NORMAL | set: src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs#PRAGMA synchronous=NORMAL |
| sqlitevec | journal_mode | wal | read: PRAGMA journal_mode on a second, read-only connection to the bench file |
| qdrant | HostConfig.Memory | 8589934592 | read: docker inspect HostConfig.Memory (0 = no limit) |
| qdrant | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| qdrant | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| qdrant | search_threads | 3 | read: threads named search-* of the qdrant process in container gvb-qdrant (/proc) |
| opensearch | HostConfig.Memory | 6442450944 | read: docker inspect HostConfig.Memory (0 = no limit) |
| opensearch | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| opensearch | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| opensearch | heap_init_in_bytes | 2147483648 | read: GET /_nodes/jvm (nodes.<id>.jvm.mem.heap_init_in_bytes and heap_max_in_bytes) |
| opensearch | heap_max_in_bytes | 2147483648 | read: GET /_nodes/jvm (nodes.<id>.jvm.mem.heap_init_in_bytes and heap_max_in_bytes) |
| qdrant-hnsw | HostConfig.Memory | 8589934592 | read: docker inspect HostConfig.Memory (0 = no limit) |
| qdrant-hnsw | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| qdrant-hnsw | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| qdrant-hnsw | search_threads | 3 | read: threads named search-* of the qdrant process in container gvb-qdrant (/proc) |
| redis | HostConfig.Memory | 12884901888 | read: docker inspect HostConfig.Memory (0 = no limit) |
| redis | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| redis | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| redis | search-workers | 8 | read: docker exec gvb-redis redis-cli CONFIG GET search-workers |
| redis | save | 300 1 | read: docker exec gvb-redis redis-cli CONFIG GET save |
| redis | appendonly | no | read: docker exec gvb-redis redis-cli CONFIG GET appendonly |
| redis | maxmemory | 0 | read: docker exec gvb-redis redis-cli CONFIG GET maxmemory |
| milvus | HostConfig.Memory | 8589934592 | read: docker inspect HostConfig.Memory (0 = no limit) |
| milvus | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| milvus | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| mariadb | HostConfig.Memory | 8589934592 | read: docker inspect HostConfig.Memory (0 = no limit) |
| mariadb | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| mariadb | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| mariadb | innodb_buffer_pool_size | 2147483648 | read: docker exec gvbbench-mariadb mariadb SHOW GLOBAL VARIABLES |
| mariadb | innodb_flush_log_at_trx_commit | 2 | read: docker exec gvbbench-mariadb mariadb SHOW GLOBAL VARIABLES |
| mariadb | mhnsw_max_cache_size | 4294967296 | read: docker exec gvbbench-mariadb mariadb SHOW GLOBAL VARIABLES |
| weaviate | HostConfig.Memory | 8589934592 | read: docker inspect HostConfig.Memory (0 = no limit) |
| weaviate | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| weaviate | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| weaviate | GOMEMLIMIT | 6GiB | read: docker inspect Config.Env, the name GOMEMLIMIT only |
| mongodb | HostConfig.Memory | 8589934592 | read: docker inspect HostConfig.Memory (0 = no limit) |
| mongodb | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| mongodb | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| chroma | HostConfig.Memory | 6442450944 | read: docker inspect HostConfig.Memory (0 = no limit) |
| chroma | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| chroma | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| chroma | chroma_version (API version reported) | 1.0.0 | read: GET /api/v2/version |
| typesense | HostConfig.Memory | 6442450944 | read: docker inspect HostConfig.Memory (0 = no limit) |
| typesense | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| typesense | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| sql-diskann | HostConfig.Memory | 8589934592 | read: docker inspect HostConfig.Memory (0 = no limit) |
| sql-diskann | HostConfig.CpusetCpus | 2-3,6-7 | read: docker inspect HostConfig.CpusetCpus (empty = every CPU) |
| sql-diskann | HostConfig.NanoCpus | 0 | read: docker inspect HostConfig.NanoCpus (0 = no limit) |
| duckdb | duckdb_version | v1.5.6 | read: SELECT version() on an in-memory DuckDB connection |
| duckdb | threads | 8 | read: SELECT current_setting('threads') on an in-memory DuckDB connection; DuckDB's own thread count, not a CPU cap (it counts all CPUs and ignores affinity) |
| duckdb | hnsw_ef_search | 100 | set: src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET hnsw_ef_search |
| duckdb | hnsw_enable_experimental_persistence | true | set: src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#SET hnsw_enable_experimental_persistence = true |
| duckdb | memory_limit | 7.4 GiB | read: SELECT current_setting('memory_limit') on a second connection to the bench file opened with the sink's own connection string |
| duckdb | checkpoint_threshold | 244.1 MiB | read: SELECT current_setting('checkpoint_threshold') on a second connection to the bench file opened with the sink's own connection string |

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
| weaviate index | ef=-1 | documented | yes | src/GenericVectorBuilder.Engines/Sinks/WeaviateSink.cs#["ef"] = _options.Ef,; src/GenericVectorBuilder.Engines/Sinks/WeaviateSinkOptions.cs#int Ef = -1 );; design/engine-docs/weaviate-vector-index-reference-2026-10-07.mdx#`ef` |
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
| run note 0 | Load average at start: 0.38 0.64 1.22 on 8 logical CPUs (1/5/15 min). | read-back | yes | src/GenericVectorBuilder.Bench/Report/MachineFacts.cs#File.ReadAllText( "/proc/loadavg" ) |
| run note 0 | It counts this benchmark's own earlier work and the engines it started, so it is recorded here and never judged; each timed pass is judged by the outside load machine control measures (conditions.passes). | unverified | yes |  |
| run note 1 | Order: targets ran one at a time in a random order from seed 801 (runSeed; --seed 801 repeats it, targetOrder lists it). | documented | yes | src/GenericVectorBuilder.Bench/Running/RunOrder.cs#return Shuffle( names, Mix( (ulong)(uint)runSeed ^ TARGET_SALT ) ); |
| run note 1 | Inside each target the timed passes also ran in a random order from the same seed and the target's name (passOrder): | documented | yes | src/GenericVectorBuilder.Bench/Running/RunOrder.cs#return Shuffle( passes, Mix( (ulong)(uint)runSeed ^ PASS_SALT ^ StableHash( targetName.ToLowerInvariant() ) ) ); |
| run note 1 | default@1 is one searcher for 20 s (every search's latency gives p50/p95/p99, the completed searches give QPS@1, its first answer to each query gives recall and nDCG), default@N is N searchers for 20 s (QPS@N), exact is the engine's exact mode, one searcher for 60 s cycling the queries. | unverified | yes |  |
| run note 2 | Preparation, untimed, before any timed pass: a rehearsal of every pass type at its own concurrency for 30 s each, through the same code the passes use. | unverified | yes |  |
| run note 3 | Warm-up and settle check, untimed, right before every timed pass: the pass's own search at the pass's own number of searchers for at least 15 s and at least 20 searches (at most 120 s), read in windows of at least 2 s and 100 searches; then a 3 s trial of the same pass. The trial's figure (p50 with one searcher, QPS with several) must lie within 10% of the warm-up's settled figure (the median of its last 3 windows, which must agree within 5%); if not, the warm-up is extended once (at least 30 s, until its windows agree, at most 120 s), where an extension's windows agree when its level test passes: the older and the newer half of its latest windows, 5 to 10 windows a half, each half read as the pass reads it (the p50 of all its searches with one searcher, its searches per second with several), agree within 5%, judged on the windows that stopped it (an extension whose cap runs out with fewer than 10 windows is judged by its last 3 instead); then a second trial is taken, which must lie inside the range of the newer half's windows or within 10% of the newer half's figure, and the warm-up runs again before the pass. After the pass its own figure is held against the settled figure, within 10%. In the level test, the second trial and the hold, two one-searcher p50s no more than 0.1 ms apart agree whatever their percentage (below what this box resolves at one searcher). Each target's notes give every check, and a pass that still disagrees, or whose own figure did not hold, flags its target as unsettled. With machine control on, the check for a quiet box is made when the warm-up is announced, before it starts (a wait between the warm-up and the timed pass let the engine go cold), so by the time the clock opens that check is as old as the warm-up and the trial (about 18 s, more after an extension); each pass's conditions record that lead (quietCheckLeadSeconds). Failed untimed searches are counted per target (warmupErrors) and are not in the timed error counts. | unverified | yes |  |
| run note 4 | Load rows/s counts only time inside each target's upsert calls: one writer, batches of 1000, rows already in memory, the collection dropped and created fresh first. Engines that build or finish their index after the writes do it in a separate timed index step (the load's index seconds), and the run waits for it before searching. | unverified | yes |  |
| run note 5 | Index proof: each engine's own report of its index (indexState) is read after the load and again after the last pass. A target whose index was not ready after the load was still measured and carries a WARNING; an engine that reports nothing counts as not ready. | unverified | yes |  |
| run note 6 | Search settings: each searched target records the settings its own index description states (searchSettings: the build parameters and the effort per query, such as m, ef_construction and ef_search), read after the load; consolidating never averages runs whose settings differ. | unverified | yes |  |
| run note 7 | Durability: each target's crash-safety setting as configured here (durability). Engines that do not force writes to disk on every commit load faster for that reason. | unverified | yes |  |
| run note 8 | Engine record: right after each engine was bound, its settings (engineSettings: the container's limits and what the engine itself reports, each entry saying how it was obtained), its image against the id pinned in deploy/bench/image-pins.json (image; another id ends that target with an error) and the size of its data folder (dataFolder: at the start, after any reset, and at the end) were recorded. In run-all, ClickHouse's system log tables are truncated before its load, and the reset is written into its dataFolder. | unverified | yes |  |
| run note 9 | Latency is client-side wall time around each search (network and driver included), every search of the default@1 window, one searcher, the queries cycled; a window with fewer than 200 searches, a p50 above 1.25 x its mean, or a mean above its p99 is flagged. | unverified | yes |  |
| run note 10 | QPS: N searchers back to back for 20 s per level; completed searches divided by the window's elapsed time. | unverified | yes |  |
| run note 11 | Recall@10: share of the exact top 10 (brute force in memory) that the engine returned. | unverified | yes |  |
| run note 11 | A hit whose exact similarity ties the 10th best (within 1e-5) also counts | documented | yes | src/GenericVectorBuilder.Bench/Stats/BenchMath.cs#public const double TIE_TOLERANCE = 1e-5; |
| run note 11 | , because duplicate rows embed to identical vectors. | unverified | yes |  |
| run note 12 | Routes: a container engine (the benchmark's own SQL Server and Qdrant containers included) is reached at its container's own address on its Docker network, never through the published 127.0.0.1 port (docker-proxy); | read-back | yes | results:conditions.connections#this target |
| run note 12 | the native comparison ta... | unverified | no: absent-targets |  |
| run note 12 | Each target's addresses and the connections the client held open after its passes are in its notes and in conditions.connections. | unverified | yes |  |
| run note 13 | RAM of compose engines, the benchmark's own SQL Server and Qdrant containers (sql, sql-diskann, qdrant, qdrant-hnsw) included, is docker stats of the engine's containers; | unverified | yes |  |
| run note 13 | for the native compariso... | unverified | no: absent-targets |  |
| run note 13 | Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing); when the folder was smaller after the load than before it, no figure is given. | unverified | yes |  |
| run note 14 | Engines: run-all starts an engine that is down and stops it afterwards only if it was not running when the run began; an engine that was already running is left running. | unverified | yes |  |
| run note 16 | Machine control on: governor performance on every CPU during the run (before: schedutil; at the end: performance; after putting it back: schedutil). CPU clock pinned for the run: turbo off (intel_pstate/no_turbo 0 -> 1), so every CPU's clock is held at its ceiling of 3500 MHz whatever the engine runs; the uncore (L3 and memory) clock is held at 3000 MHz (min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); was min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)) (before: turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; while pinned: turbo off (intel_pstate/no_turbo 1), ceiling 3500 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; at the end: turbo off (intel_pstate/no_turbo 1), ceiling 3500 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; after putting it back: turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7). Uncore limit at the end: min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); after putting it back: min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e). Each target's clock note gives every pass's median MHz on the engine CPUs and on the client CPUs (the median of each CPU's median); a pass whose median on either is more than 100 bp (1%) off the pinned 3500 MHz, or that has no reading on one, is flagged (conditions.clock.rule). Figures from runs made with turbo on are not comparable with these in absolute terms. engine CPUs 2-3,6-7 (cores 2,6 and 3,7), client CPUs 0-1,4-5 (cores 0,4 and 1,5); the client process was pinned; each engine was pinned to the engine CPUs for its turn and put back after (conditions.engines); an engine run-all started (and stopped) was asked to be created on them, and its notes say whether the host did so or it was moved there after the start. | read-back | yes | results:conditions.governor#performance; results:conditions.clientCpus#0-1,4-5; results:conditions.engineCpus#2-3,6-7 |
| run note 16 | Busy box: right before each pass's warm-up the run waits, up to 10 min, while processes outside the benchmark (everything but this client and the engine under test's cgroups) use more than 0.3 CPUs on average over the last 60 s or the last 5 s, neither window reaching back past the start of the target or of its engine (so the engine's own start-up is not outside work); if the box does not clear the pass runs anyway, flagged 'busy box', as is a pass whose own outside load is above the limit. Outside load counts busy = user + nice + system + irq + softirq + steal (guest time is already in user); CONFIG_IRQ_TIME_ACCOUNTING is not set, so task and cgroup run time include the interrupt and softirq time that hit them and it is added; CONFIG_PARAVIRT_TIME_ACCOUNTING is not set, so steal is added (/boot/config-6.8.0-142-generic) (conditions.cpuAccounting); each pass also records this client's own CPU time per search and the engine's (conditions.passes[].clientCpuMsPerSearch and engineCpuMsPerSearch; how in conditions.engineCpu.rule). CPU clocks were sampled every 250 ms; each pass's min/median/max per CPU is in conditions.passes. CPU idle states, recorded and left as found: driver intel_idle, governor menu, intel_idle max_cstate 9; POLL on, C1 on, C1E on, C3 OFF on CPUs 0-7 (default disabled), C6 on (conditions.cpuIdle). How each target was reached: conditions.connections. Client build Release, .NET 10.0.12. | unverified | yes |  |
| run note 17 | duckdb is embedded: it ran inside the client process on the client CPUs 0-1,4-5, sharing them with the client. | read-back | yes | results:conditions.engines#embedded |
| run note 18 | sqlitevec is embedded: it ran inside the client process on the client CPUs 0-1,4-5, sharing them with the client. | read-back | yes | results:conditions.engines#embedded |

## Method

The method, quoted from the notes of run 20261007-205106-eshoponweb:

Read from the running engine or the machine by the benchmark's own code, as the cited line shows:

> Load average at start: 0.38 0.64 1.22 on 8 logical CPUs (1/5/15 min).

Typed in the program's own text; not read back, measured or backed by a saved source:

> It counts this benchmark's own earlier work and the engines it started, so it is recorded here and never judged; each timed pass is judged by the outside load machine control measures (conditions.passes).

Set by a line of code or a compose file saved in this repository, not read back from the engine:

> Order: targets ran one at a time in a random order from seed 801 (runSeed; --seed 801 repeats it, targetOrder lists it).

> Inside each target the timed passes also ran in a random order from the same seed and the target's name (passOrder):

Typed in the program's own text; not read back, measured or backed by a saved source:

> default@1 is one searcher for 20 s (every search's latency gives p50/p95/p99, the completed searches give QPS@1, its first answer to each query gives recall and nDCG), default@N is N searchers for 20 s (QPS@N), exact is the engine's exact mode, one searcher for 60 s cycling the queries.

> Preparation, untimed, before any timed pass: a rehearsal of every pass type at its own concurrency for 30 s each, through the same code the passes use.

> Warm-up and settle check, untimed, right before every timed pass: the pass's own search at the pass's own number of searchers for at least 15 s and at least 20 searches (at most 120 s), read in windows of at least 2 s and 100 searches; then a 3 s trial of the same pass. The trial's figure (p50 with one searcher, QPS with several) must lie within 10% of the warm-up's settled figure (the median of its last 3 windows, which must agree within 5%); if not, the warm-up is extended once (at least 30 s, until its windows agree, at most 120 s), where an extension's windows agree when its level test passes: the older and the newer half of its latest windows, 5 to 10 windows a half, each half read as the pass reads it (the p50 of all its searches with one searcher, its searches per second with several), agree within 5%, judged on the windows that stopped it (an extension whose cap runs out with fewer than 10 windows is judged by its last 3 instead); then a second trial is taken, which must lie inside the range of the newer half's windows or within 10% of the newer half's figure, and the warm-up runs again before the pass. After the pass its own figure is held against the settled figure, within 10%. In the level test, the second trial and the hold, two one-searcher p50s no more than 0.1 ms apart agree whatever their percentage (below what this box resolves at one searcher). Each target's notes give every check, and a pass that still disagrees, or whose own figure did not hold, flags its target as unsettled. With machine control on, the check for a quiet box is made when the warm-up is announced, before it starts (a wait between the warm-up and the timed pass let the engine go cold), so by the time the clock opens that check is as old as the warm-up and the trial (about 18 s, more after an extension); each pass's conditions record that lead (quietCheckLeadSeconds). Failed untimed searches are counted per target (warmupErrors) and are not in the timed error counts.

> Load rows/s counts only time inside each target's upsert calls: one writer, batches of 1000, rows already in memory, the collection dropped and created fresh first. Engines that build or finish their index after the writes do it in a separate timed index step (the load's index seconds), and the run waits for it before searching.

> Index proof: each engine's own report of its index (indexState) is read after the load and again after the last pass. A target whose index was not ready after the load was still measured and carries a WARNING; an engine that reports nothing counts as not ready.

> Search settings: each searched target records the settings its own index description states (searchSettings: the build parameters and the effort per query, such as m, ef_construction and ef_search), read after the load; consolidating never averages runs whose settings differ.

> Durability: each target's crash-safety setting as configured here (durability). Engines that do not force writes to disk on every commit load faster for that reason.

> Engine record: right after each engine was bound, its settings (engineSettings: the container's limits and what the engine itself reports, each entry saying how it was obtained), its image against the id pinned in deploy/bench/image-pins.json (image; another id ends that target with an error) and the size of its data folder (dataFolder: at the start, after any reset, and at the end) were recorded. In run-all, ClickHouse's system log tables are truncated before its load, and the reset is written into its dataFolder.

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

> Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing); when the folder was smaller after the load than before it, no figure is given.

> Engines: run-all starts an engine that is down and stops it afterwards only if it was not running when the run began; an engine that was already running is left running.

Read back in these runs from the engine or the machine:

> Machine control on: governor performance on every CPU during the run (before: schedutil; at the end: performance; after putting it back: schedutil). CPU clock pinned for the run: turbo off (intel_pstate/no_turbo 0 -> 1), so every CPU's clock is held at its ceiling of 3500 MHz whatever the engine runs; the uncore (L3 and memory) clock is held at 3000 MHz (min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); was min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)) (before: turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; while pinned: turbo off (intel_pstate/no_turbo 1), ceiling 3500 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; at the end: turbo off (intel_pstate/no_turbo 1), ceiling 3500 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7; after putting it back: turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7). Uncore limit at the end: min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); after putting it back: min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e). Each target's clock note gives every pass's median MHz on the engine CPUs and on the client CPUs (the median of each CPU's median); a pass whose median on either is more than 100 bp (1%) off the pinned 3500 MHz, or that has no reading on one, is flagged (conditions.clock.rule). Figures from runs made with turbo on are not comparable with these in absolute terms. engine CPUs 2-3,6-7 (cores 2,6 and 3,7), client CPUs 0-1,4-5 (cores 0,4 and 1,5); the client process was pinned; each engine was pinned to the engine CPUs for its turn and put back after (conditions.engines); an engine run-all started (and stopped) was asked to be created on them, and its notes say whether the host did so or it was moved there after the start.

Typed in the program's own text; not read back, measured or backed by a saved source:

> Busy box: right before each pass's warm-up the run waits, up to 10 min, while processes outside the benchmark (everything but this client and the engine under test's cgroups) use more than 0.3 CPUs on average over the last 60 s or the last 5 s, neither window reaching back past the start of the target or of its engine (so the engine's own start-up is not outside work); if the box does not clear the pass runs anyway, flagged 'busy box', as is a pass whose own outside load is above the limit. Outside load counts busy = user + nice + system + irq + softirq + steal (guest time is already in user); CONFIG_IRQ_TIME_ACCOUNTING is not set, so task and cgroup run time include the interrupt and softirq time that hit them and it is added; CONFIG_PARAVIRT_TIME_ACCOUNTING is not set, so steal is added (/boot/config-6.8.0-142-generic) (conditions.cpuAccounting); each pass also records this client's own CPU time per search and the engine's (conditions.passes[].clientCpuMsPerSearch and engineCpuMsPerSearch; how in conditions.engineCpu.rule). CPU clocks were sampled every 250 ms; each pass's min/median/max per CPU is in conditions.passes. CPU idle states, recorded and left as found: driver intel_idle, governor menu, intel_idle max_cstate 9; POLL on, C1 on, C1E on, C3 OFF on CPUs 0-7 (default disabled), C6 on (conditions.cpuIdle). How each target was reached: conditions.connections. Client build Release, .NET 10.0.12.

Read back in these runs from the engine or the machine:

> duckdb is embedded: it ran inside the client process on the client CPUs 0-1,4-5, sharing them with the client.

Read back in these runs from the engine or the machine:

> sqlitevec is embedded: it ran inside the client process on the client CPUs 0-1,4-5, sharing them with the client.

## Runs used

Runs used: 6 claim runs, which are among the 12 runs of the basis; each is listed below with its seed and start time.

The runs of v7 were also used by the set blocked-2026-10-06-v7, which was not published; its verdict, design/verdicts/v7-verdict.md, holds the word BLOCK.

The runs of v5 were also used by the set blocked-2026-10-05-v5, which was not published; its verdict, design/verdicts/v5-verdict.txt, holds the word BLOCK.

The runs of v6 were also used by the set blocked-2026-10-05-v6, which was not published; its verdict, design/verdicts/v6-verdict.md, holds the word BLOCK.

The results.md of run 20261006-130619-eshoponweb holds a framing sentence this report retired, on line 13.

The results.md of run 20261006-142724-eshoponweb holds a framing sentence this report retired, on line 13.

The results.md of run 20261006-154837-eshoponweb holds a framing sentence this report retired, on line 13.

| run | session | seed | started (UTC) |
|---|---|---|---|
| 20261006-130619-eshoponweb | v7 | 701 | 2026-10-06T13:06:19Z |
| 20261006-142724-eshoponweb | v7 | 702 | 2026-10-06T14:27:24Z |
| 20261006-154837-eshoponweb | v7 | 703 | 2026-10-06T15:48:37Z |
| 20261007-205106-eshoponweb | v8 | 801 | 2026-10-07T20:51:06Z |
| 20261007-221429-eshoponweb | v8 | 802 | 2026-10-07T22:14:29Z |
| 20261007-233551-eshoponweb | v8 | 803 | 2026-10-07T23:35:51Z |
| 20261005-023459-eshoponweb | v5 (basis) | 501 | 2026-10-05T02:34:59Z |
| 20261005-031556-eshoponweb | v5 (basis) | 502 | 2026-10-05T03:15:56Z |
| 20261005-035711-eshoponweb | v5 (basis) | 503 | 2026-10-05T03:57:11Z |
| 20261005-073329-eshoponweb | v6 (basis) | 601 | 2026-10-05T07:33:29Z |
| 20261005-085448-eshoponweb | v6 (basis) | 602 | 2026-10-05T08:54:48Z |
| 20261005-101815-eshoponweb | v6 (basis) | 603 | 2026-10-05T10:18:15Z |

