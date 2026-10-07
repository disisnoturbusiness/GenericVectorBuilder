BLOCK

The three v5 blockers are fixed in the data, the consolidated numbers recompute exactly, and the observer claims hold up apart from a few small miscounts. Two problems in the published 05b output block it. Neither needs a full rerun: both need a disclosure or text fix and a re-consolidate.

**Blocking**

1. **ClickHouse's server configuration changed between v5 and v6, and the results don't say so.**
   - `deploy/engines/clickhouse-config/gvb-system-logs.xml` and `clickhouse.compose.yaml` were changed at 05:22, after the v5 runs ended at 04:38.
   - The change switches off 21 of ClickHouse's built-in log tables (query_log, text_log, trace_log, processors_profile_log and others). The file's own comment says these writes were "background CPU and disk work inside the engine while a pass is being timed".
   - Nothing in results.json, results.md or consolidated.md mentions it. ClickHouse's engine text is just "ClickHouse 26.3.39.7 (MergeTree + vector_similarity index)", and grep finds no query_log or text_log in either report.
   - v6 against v5 medians: ClickHouse QPS@8 is 16.4% higher (423 to 493), QPS@1 5.8% higher, p50 5.0% lower and exact p50 4.7% lower.
   - Over the same change, the other engines that run natively in a container (qdrant, qdrant-hnsw, mariadb, sql, sql-diskann, pgvector, milvus, weaviate, chroma, typesense) moved 1.6% or less. So the gain comes from the config change, not the new warm-up (inferred, high confidence).
   - This is the same kind of problem as v5 blocker 3: a non-default engine setting the results don't mention. Every other engine's non-default settings are spelled out.
   - Fix: disclose it in ClickHouse's engine text and in the report, then re-consolidate. The other option is to rerun ClickHouse with its log tables at the image default.

2. **The consolidated report misstates the warm-up, which is the exact thing v5 blocker 2 was about.**
   - consolidated.md line 22 says "warm-up searches before each timed pass | 20" (written by `ConsolidatedMarkdown.cs:89`). The web page says the same (`BenchSummaryHtml.cs:275`).
   - From the raw notes, all 156 passes had a warm-up of at least 15.0 s and at least 2,370 searches (the smallest was ClickHouse exact). Most passes had far more.
   - consolidated.md never describes the 15 s rule, the settle trial or the extension of up to 120 s. Anyone reading it would conclude blocker 2 is still there.

**Verified**

- **Recompute:**
  - From the three results.json files: medians, min and max for p50, QPS@1, QPS@8, the 8/1 ratio, recall and exact p50 match consolidated.json for all 19 targets, with zero mismatches.
  - Band entries match, and every band boundary and band link rule holds for p50, QPS@1 and QPS@8.
  - Spread over the 71 figures: largest is 1.045 (redis p50), median 1.009. There are no spread flags.
  - Search settings are identical in all 3 runs for all 19 targets. Seeds 601-603, 0 errors, 0 warm-up errors, 0 busy-box or waited passes, 0 accounting mismatches, governor "performance" in every pass.
- **Blocker 1 (DuckDB and sqlite-vec), fixed:**
  - QPS@8/QPS@1 is 2.13 for DuckDB and 2.13 to 2.17 for sqlite-vec.
  - My own count from watch.jsonl: the benchmark process used 3.974 to 3.994 CPUs at @8, against 1.358 (DuckDB) and 1.047 (sqlite-vec) at @1.
  - sqlite-vec held 9 handles on its .sqlite file and 9 on its -wal; DuckDB held 1.
  - Disclosed in the index text and with the client-engine-share-cores flag.
- **Blocker 2 (JVM engines measured cold), fixed in the data:**
  - Vespa QPS@8 was 1539.8 and 1560.0 with @8 first, and 1595.2 with @8 second.
  - Engine CPU per search, from the cgroups: 2.499 and 2.459 ms first against 2.394 ms second.
  - Elasticsearch and OpenSearch show no effect of pass order.
  - The settled figure is now within 2.4% of the timed figure for the JVM engines. The worst gap in any pass is 15.3% (oracle @8, run 602), which I checked by hand.
- **Blocker 3 (MongoDB), fixed:** MongoDB is in every table and carries the segment-layout-differs-between-runs flag (2, 4 and 2 segments).
- **Observer, recomputed independently from watch.jsonl:**
  - Governor: 19,193 samples inside passes, all "pppppppp".
  - Engine containers ran only on CPUs 2-3,6-7 and the client only on 0-1,4-5. The only threads on other CPUs belonged to the daily gvb-mariadb (cpuset 0-7).
  - Containers: every create and start had cpuset 2-3,6-7, with 0 docker update events.
  - ss: 7,460 samples, 0 failed, 0 docker-proxy sockets.
  - Outside load per pass: median 0.154, max 0.269. Mine minus the tool's ranges from -0.019 to 0.098.
  - gvb-mariadb: one identity across 997 inspections; Com_insert, update, delete, create, drop and alter unchanged; 0 non-LISTEN sockets on 3306.
  - Native SQL Server and Qdrant: same PIDs, AUTO with 8 online schedulers.
  - ClickHouse data folder: 133,645 bytes before, 120,055 after.
  - Lane src, tests and deploy are identical to main. The build (07:14:04) came after the lane's patches (07:13:40).
  - Test logs: t1 failed RunnerTests.Finisher once, t2 was green, t3 fails BenchResultsSummaryTests (the 05b folder name), as the run report says.

**Fix-with (not blocking)**

- The unsettled-target flag (oracle, redis, clickhouse, 3 of 3 runs) tells readers to "rerun before quoting", but those numbers are still quoted in the headline tables. The tables' engine-notes column does not show the flag.
- Run 601's results.md carries a false "WARNING: load average 10.04 ... box was busy" on sqlite-vec (`TargetRunner.cs:384`). The measured outside load was 0.15 to 0.17 CPUs.
- Vespa's 2 to 3.5% order effect remains, from a single "second" run. It only keeps Vespa and OpenSearch in the same QPS@8 band, which errs on the conservative side.
- CPU clock differs by engine in all 3 runs. Most engines ran at 3492 MHz, but sqlite-vec, DuckDB, Chroma, Typesense, Weaviate and ClickHouse ran at about 3592 MHz. That is up to 2.9%, close to the 3% band threshold. The likely cause is turbo depending on how many cores are busy (inferred). This is not disclosed.
- Web: the live :5080 app still serves published-2026-10-04. The test at `BenchResultsSummaryTests.cs:905` must be updated before a redeploy. Once redeployed, 05b gets only the general caveat list.
- Stale docs remain at `MachineControl.cs:68` and `MachineControlRecord.cs:257`. `EngineLifecycle.StartNote` still drops the Milvus retry note (not exercised in these runs).
- gvb-milvus exited with code 134 and gvb-mongodb with 137 at every stop. Milvus's folder grows about 10 MB per run. Neither affects results.
- Errors in the runs report itself:
  - It says 57 container creates; there were 54.
  - Its spread ranking skips pgvector p50 (1.042) and pgvector QPS@8 (1.034).
  - The "irq 0" check proves nothing: the irq column in /proc/stat is 0 since boot on this box.

Files:
- /home/dan/ForClaude/GenericVectorBuilder/deploy/engines/clickhouse-config/gvb-system-logs.xml
- /home/dan/ForClaude/GenericVectorBuilder/src/GenericVectorBuilder.Bench/Report/ConsolidatedMarkdown.cs
- /home/dan/ForClaude/GenericVectorBuilder/src/GenericVectorBuilder.Web/BenchPages/BenchSummaryHtml.cs
- /home/dan/ForClaude/GenericVectorBuilder/bench-results/published-2026-10-05b/consolidated.md
- /home/dan/gvb-work/lanes/v6-final/observer/watch.jsonl