BLOCK

Three reasons survive. I checked all of them against the raw files. My recompute scripts are in /home/dan/gvb-work/lanes/v7-verdict-compile/ (aperf.py, drift.py).

**Surviving reasons**

1. **Engines drift more between sessions than the band rule allows, so the v6 and v7 bands contradict each other.** (numbers lens; confirmed 3 independent ways)
   - **The claim:** consolidated.md:79 and :554 (`ConsolidateFraming.cs` BANDS_LINE) say "an engine varies about 2% from run to run" and draw band boundaries at a 3% gap.
   - **Check A, v6 against v7 results.json:** the same engines moved well past that, and no v6 range overlaps its v7 range.
     - redis: p50 +6.8%, exact p50 +7.9%, QPS@1 -3.6%
     - oracle: p50 +4.7%, QPS@1 -4.2%, exact p50 +4.9%
     - pgvector: p50 +3.2%, QPS@1 -3.1%
     - vespa QPS@1 -5.4%, milvus -5.3%, sql -3.9%
     - qdrant and qdrant-hnsw moved under 1%.
     - redis, oracle and pgvector read 3492 MHz at @1 in v6 as well. So turbo did not move their core clock.
   - **Check B, same-seed turbo test** (v7-constant-clock.r3, A turbo on, B turbo off): pgvector QPS@1 went from 1157.2 to 1160.8 and p50 from 0.8498 to 0.8464. Turning turbo off does not explain pgvector's drift.
   - **Check C, r3 run B against v7-final:** r3 B ran at 00:34 UTC on Oct 6 with the same clock settings as v7-final (no_turbo 1, uncore 1e1e). Its pgvector figures were p50 0.8464 and QPS@1 1160.8. v7-final read p50 0.8695-0.8846 and QPS@1 1107-1126. That is 3-5% drift on one day with nothing changed in the clock settings.
     - Caveat: the r3 code differs from v7-final in its settle logic, and seed 731 runs targets in a different order. I am inferring, not proving, that neither touches the timed path.
   - **The bands disagree:**
     - v6 p50 bands: {pgvector} {qdrant, oracle} {qdrant-hnsw}.
     - v7 p50 bands: {pgvector, qdrant} {qdrant-hnsw, oracle}.
     - So each session says "beat in every run" for a pair the other session calls a tie.
   - **Spread inside v7 alone:** 12 of 71 figures spread more than 3%, and 7 spread more than 8%.
   - **The code knows:** a comment in `SearchRunner.cs` (P50_FLOOR_MS) records that Redis's p50 at one searcher sits on 2 levels 13-15% apart.

2. **The clock pin appears nowhere in the published output, and consolidation does not require it to match.** (all 3 lenses)
   - **Report:** consolidated.md and consolidated.json contain 0 matches for turbo, uncore or MHz, and the settings object has no clock key.
   - **Match rule:** `Consolidator.cs:214-215` matches runs on governor and cpuPartition only. Nothing in Stats/*.cs reads the clock.
   - **Web:** the Machine line (`BenchSummaryHtml.cs:241-252`) stops at governor, partition, build and exact seconds. The rendered summary has 0 mentions of "clock".
   - **The runs did record it,** but only in results.md (lines 522, 522, 523) and as free text in results.json notes[14].
   - **Why it matters:** in the same-seed test, turning turbo off cost sqlitevec 4.5% and typesense 2.9% at QPS@1. The site at :5080 still serves published-2026-10-04, which was measured with turbo on. This is the same class of problem as v6 blocker 1.

3. **A "not comparable" warning on a headline figure is dropped without a word.** (reader lens)
   - **The warning:** all 3 runs flag Oracle default@8 "clock off ... not comparable" (engine CPUs -10.8%, -10.6% and -9.5%). The run 702 web page shows it.
   - **What consolidation does:** none of the 6 consolidated flags is a clock flag, and Oracle QPS@8 is banded as normal.
   - **The warning itself is false:**
     - APERF/MPERF reads 3478.0-3480.1 MHz on the engine CPUs and 3480.7-3482.5 on the client CPUs, with no single CPU below 3474.3 (under 1% off).
     - MSR 0x620 read 1e1e in every one of the 156 passes.
     - Cause: `MachineControlRecord.ClockWarnings` averages scaling_cur_freq, and in those passes every CPU's minimum sample is about 1196 MHz while the median is 3492.
   - **The fix** is a correct clock check, not dropping the Oracle figure.

**Fix-with (not blocking on their own)**
- Base the clock check on APERF/MPERF or the median, store clock state as a structured field, and carry clock flags into consolidation and the web page.
- `engineSettings` is [] for all 19 targets, so the identity check (`ConsolidateIdentity.cs:261`) compares empty values.
- **Runs report errors:**
  - It blames turbo for the slowdown of redis, pgvector, oracle, sql-diskann and opensearch, which is wrong.
  - The ClickHouse "TTL dropped the logs" claim is unsupported for run 702.
  - It counts 2,916 health checks; the real figure is 2,909.
- **ClickHouse:**
  - Run 701 started with an empty data folder, while 702 and 703 started with GBs of old logs; no result records this.
  - Recall varies 0.965-0.995 between runs and is not flagged.
  - About 2-3% of its v6-to-v7 move is unexplained.
- **MongoDB busy-box flags:**
  - The observer's own CPU use pushed those passes over the limit.
  - The legend says the box was busy "when the run started", which is wrong.
  - MongoDB run 703 and OpenSearch run 701 are unexplained outliers.
- **Not stated in the report:** Redis "in memory" (consolidated.md), the heap and buffer settings, and that chroma runs on an unpinned `:latest` tag.
- **Web:** the blocked v6 runs (Oct 5, 07:33, 08:54 and 10:18) are listed as ordinary runs.
- **Method lens only, not checked by me:**
  - dockerd's cgroup is counted as engine work (`MachineControlPinning.cs:156-159`).
  - Device interrupts land on the engine CPUs.

**What clears it**
- Disclose the clock in the consolidated header, the JSON settings and the web Machine line, and add it to the run-matching rule.
- Fix the clock check and carry its flags through.
- For the drift, either:
  - explain it with a controlled before/after, or
  - size the band gap and wording to the session-to-session drift actually measured (about 8%) and disclose the v6 to v7 drift.
- Re-consolidate, then review again. The second route needs no rerun.

**Lens claims overturned or corrected**
- **Numbers lens, "5 spread more than 8%":** it is 7, because opensearch QPS@1 (1.089) and p50 (1.081) also qualify. This strengthens reason 1.
- **Method and reader lenses blamed the clock pin for v7 sitting 2-6% under v6:**
  - Overturned for oracle, redis and pgvector, which ran at 3492 MHz in both sessions. Reader lens listed oracle's 4.2% as a clock effect.
  - For vespa and milvus the clock explains at most part of it: turbo's ceiling is 3600 MHz, so it accounts for 2.9% at most.
  - Reason 2 still stands; only the size of the effect blamed on it is wrong.
- **Method lens, "A rerun is not needed. Fix it, then re-consolidate":** that is not enough on its own, because reason 1 also has to be dealt with (by explanation or by re-sized bands and disclosure).
- **No BLOCK reason was dropped.**