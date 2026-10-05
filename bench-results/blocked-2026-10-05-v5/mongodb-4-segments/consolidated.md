# Consolidated benchmark

| item | value |
|---|---|
| created (UTC) | 2026-10-05T04:39:54Z |
| command | `consolidate --targets mongodb --runs /home/dan/ForClaude/GenericVectorBuilder/bench-results/20261005-023459-eshoponweb,/home/dan/ForClaude/GenericVectorBuilder/bench-results/20261005-031556-eshoponweb --out /home/dan/ForClaude/GenericVectorBuilder/bench-results/published-2026-10-05/mongodb-4-segments` |
| targets | 1 |
| runs used | 2 |
| runs dropped | 0 |
| pipeline | eshoponweb |
| benchmark command (every run) | run-all |
| host | linus7795 |
| rows x dimension | 524 x 1024 |
| queries | golden, 20 |
| top | 10 |
| concurrency | 1, 8 |
| seconds per level | 20 |
| build configuration | Release |
| machine control | on |
| CPU governor | performance |
| CPU partition | client 0-1,4-5; engines 2-3,6-7 |
| warm-up searches before each timed pass | 20 |
| exact mode seconds | 60 |

## Runs used

| run | started (UTC) | runSeed | load average | targetOrder |
|---|---|---|---|---|
| 20261005-023459-eshoponweb | 2026-10-05T02:34:59Z | 501 | 0.68 1.55 2.18 | clickhouse, oracle, milvus, sqlitevec, redis, weaviate, sql-diskann, mongodb, duckdb, qdrant, sql, opensearch, pgvector, chroma, vespa, qdrant-hnsw, elasticsearch, typesense, mariadb |
| 20261005-031556-eshoponweb | 2026-10-05T03:15:56Z | 502 | 3.47 2.99 2.95 | mariadb, weaviate, redis, sqlitevec, qdrant, oracle, mongodb, vespa, qdrant-hnsw, typesense, chroma, duckdb, elasticsearch, sql-diskann, pgvector, sql, milvus, clickhouse, opensearch |

## Runs dropped

(none)

## Targets

| target | hosting | engine | index | search settings |
|---|---|---|---|---|
| mongodb | compose | MongoDB 8.0.32 Atlas Local (mongod + mongot Vector Search) | vectorSearch index, HNSW maxEdges=16 numEdgeCandidates=128, float32 binData, cosine, numCandidates=20x hits (min 100); exact mode = $vectorSearch exact:true | missing |

## Request speed on a small collection (524 vectors)

Measured end to end through each engine's .NET client; at this size it reflects per-request cost including the client library, not index scaling.

Engines in different bands never overlap: every run of an engine in a faster band beat every run of an engine in a slower band, and the medians on either side of a band boundary are at least 3% apart. Engines in one band are linked by overlapping slowest-to-fastest ranges or by neighboring medians less than 3% apart (an engine varies about 2% from run to run), so these runs do not separate them cleanly. Inside a band they are listed by median, and that order is not a ranking.

### p50 latency, one search at a time (lower is faster)

| band | target | median [min, max] | client CPU ms/search | runs | engine notes |
|---|---|---|---|---|---|
| 1 | mongodb | 1.36 [1.35, 1.38] | 0.31 [0.31, 0.31] | 2 | - |

### QPS with 8 searchers at once (higher is faster)

| band | target | median [min, max] | client CPU ms/search | runs | engine notes |
|---|---|---|---|---|---|
| 1 | mongodb | 2079.4 [2063.4, 2095.5] | 0.29 [0.28, 0.30] | 2 | - |

### QPS with one searcher (higher is faster)

| band | target | median [min, max] | client CPU ms/search | runs | engine notes |
|---|---|---|---|---|---|
| 1 | mongodb | 710.7 [699.1, 722.3] | 0.31 [0.31, 0.31] | 2 | - |

Client CPU per search is the CPU time the test's .NET client itself used for each search, measured in the same pass as the figure beside it. Where it is close to the latency, the client library is a large part of what is measured.

## Per-engine notes

(none)

## Per target: median [min, max]

| target | n | p50 ms | p95 ms | QPS@1 | QPS@8 | QPS ratio 8/1 | client CPU ms/search@1 | client CPU ms/search@8 | load rows/s (not ranked) | recall@10 | nDCG@10 | exact p50 ms | errors | warm-up errors |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| mongodb | 2 | 1.36 [1.35, 1.38] | 1.67 [1.63, 1.71] | 710.7 [699.1, 722.3] | 2079.4 [2063.4, 2095.5] | 2.93 [2.90, 2.95] | 0.31 [0.31, 0.31] | 0.29 [0.28, 0.30] | 3334 [3275, 3394] | 1.000 [1.000, 1.000] | 0.518 [0.518, 0.518] | 1.23 [1.23, 1.24] | 0 | 0 |

## Per run: p50 ms

| target | 20261005-023459-eshoponweb | 20261005-031556-eshoponweb | median |
|---|---|---|---|
| mongodb | 1.35 | 1.38 | 1.36 |

## Per run: QPS@1

| target | 20261005-023459-eshoponweb | 20261005-031556-eshoponweb | median |
|---|---|---|---|
| mongodb | 722.3 | 699.1 | 710.7 |

## Per run: QPS@8

| target | 20261005-023459-eshoponweb | 20261005-031556-eshoponweb | median |
|---|---|---|---|
| mongodb | 2095.5 | 2063.4 | 2079.4 |

## Per run: QPS ratio 8/1

| target | 20261005-023459-eshoponweb | 20261005-031556-eshoponweb | median |
|---|---|---|---|
| mongodb | 2.90 | 2.95 | 2.93 |

## Per run: client CPU ms per search@1

| target | 20261005-023459-eshoponweb | 20261005-031556-eshoponweb | median |
|---|---|---|---|
| mongodb | 0.315 | 0.315 | 0.315 |

## Per run: client CPU ms per search@8

| target | 20261005-023459-eshoponweb | 20261005-031556-eshoponweb | median |
|---|---|---|---|
| mongodb | 0.296 | 0.284 | 0.290 |

## Rank per run: p50 (1 = lowest)

| target | 20261005-023459-eshoponweb | 20261005-031556-eshoponweb | band |
|---|---|---|---|
| mongodb | 1 | 1 | 1 |

## Rank per run: QPS@8 (1 = highest)

| target | 20261005-023459-eshoponweb | 20261005-031556-eshoponweb | band |
|---|---|---|---|
| mongodb | 1 | 1 | 1 |

## Rank per run: QPS@1 (1 = highest)

| target | 20261005-023459-eshoponweb | 20261005-031556-eshoponweb | band |
|---|---|---|---|
| mongodb | 1 | 1 | 1 |

## Paired per run: exact p50 / default p50

| target | run | default p50 ms | exact p50 ms | exact - default ms | exact / default | exact recall | exact pass before default@1 |
|---|---|---|---|---|---|---|---|
| mongodb | 20261005-023459-eshoponweb | 1.35 | 1.24 | -0.11 | 0.921 | 1.000 | no |
| mongodb | 20261005-031556-eshoponweb | 1.38 | 1.23 | -0.15 | 0.889 | 1.000 | no |

## Exact / default summary

| target | n | median exact/default | min | max | median exact - default ms | runs exact slower |
|---|---|---|---|---|---|---|
| mongodb | 2 | 0.905 | 0.889 | 0.921 | -0.13 | 0 of 2 |

## Recorded per run: order, passes, index state, durability

| target | run | order | passOrder | warm-up errors | errors | afterLoad ready | afterLoad indexed/total | afterLoad detail | afterSearch ready | afterSearch indexed/total | segments after load / after search | durability | load indexNote | settled | mean ms |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| mongodb | 20261005-023459-eshoponweb | 8 | default@1, default@8, exact | 0 | 0 | true | 524/524 | index status READY, queryable True; mongot holds 524 of 524 documents in 4 segment(s), 4 searched through the HNSW graph (Approximate) | true | 524/524 | 4 segments / 4 segments | Acknowledged writes are journaled before the acknowledgement. The sink sets no write concern, so the server default applies: getDefaultRWConcern gives w majority and the one-member replica set (--replSet gvbmongo in the image) has writeConcernMajorityJournalDefault true, with WiredTiger journaling on (journalCommitInterval 100 ms is only the interval for unacknowledged work). Measured 2026-10-04: 30 acknowledged single-document inserts raised WiredTiger 'log sync operations' by 30 and strace showed fdatasync on /data/db/journal/WiredTigerLog files. A crash loses no acknowledged write. mongot's search index is not part of that promise: it follows the collection asynchronously and is rebuilt from it. | index status READY, queryable True; mongot holds 524 of 524 documents in 4 segment(s), 4 searched through the HNSW graph (Approximate); wait took 1.1 s | yes | 1.38 (1000 / QPS@1) |
| mongodb | 20261005-031556-eshoponweb | 7 | default@1, default@8, exact | 0 | 0 | true | 524/524 | index status READY, queryable True; mongot holds 524 of 524 documents in 4 segment(s), 4 searched through the HNSW graph (Approximate) | true | 524/524 | 4 segments / 4 segments | Acknowledged writes are journaled before the acknowledgement. The sink sets no write concern, so the server default applies: getDefaultRWConcern gives w majority and the one-member replica set (--replSet gvbmongo in the image) has writeConcernMajorityJournalDefault true, with WiredTiger journaling on (journalCommitInterval 100 ms is only the interval for unacknowledged work). Measured 2026-10-04: 30 acknowledged single-document inserts raised WiredTiger 'log sync operations' by 30 and strace showed fdatasync on /data/db/journal/WiredTigerLog files. A crash loses no acknowledged write. mongot's search index is not part of that promise: it follows the collection asynchronously and is rebuilt from it. | index status READY, queryable True; mongot holds 524 of 524 documents in 4 segment(s), 4 searched through the HNSW graph (Approximate); wait took 1.1 s | yes | 1.43 (1000 / QPS@1) |

## Flags

(none)

## Notes

- Load rows/s is reported but not ranked and does not feed any comparison: each load wrote 524 rows, which is too few to separate engines from connection set-up and first-call cost.
- Runs were used together only when their build configuration, CPU governor, CPU partition, warm-up count, exact-mode seconds, seconds per level and every listed target's search settings matched; runs that differed are listed under Runs dropped. Spread is flagged when the largest value is more than 1.15 times the smallest across runs.
- Engines in different bands never overlap: every run of an engine in a faster band beat every run of an engine in a slower band, and the medians on either side of a band boundary are at least 3% apart. Engines in one band are linked by overlapping slowest-to-fastest ranges or by neighboring medians less than 3% apart (an engine varies about 2% from run to run), so these runs do not separate them cleanly. Inside a band they are listed by median, and that order is not a ranking. Bands are drawn only from two runs or more.
- Runs of different commands (run-all against bench), a different engine hosting (container against native) or a different segment layout reported by an engine are refused, never merged: the largest-group rule does not apply to them.

