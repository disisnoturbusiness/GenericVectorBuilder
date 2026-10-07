Copies of real run and set files, used by the BenchResults page tests. Each file is a byte-for-byte copy or a trimmed copy of a real file;
the source path and what was cut are named below. SHA256SUMS lists the hashes of the copies.

run-notes.v5.json  notes, conditions.warmupSearches, conditions.machineControl of bench-results/20261005-023459-eshoponweb/results.json (seed 501, a v5 run)
run-notes.v6.json  the same fields of bench-results/20261005-073329-eshoponweb/results.json (seed 601, a v6 run)
run-notes.v7.json  the same fields of bench-results/20261006-130619-eshoponweb/results.json (seed 701, a v7 run)
run-v7-trimmed.results.json  bench-results/20261006-130619-eshoponweb/results.json (seed 701, a v7 run) with every target and every conditions.passes record dropped except those of elasticsearch, mongodb and redis, written with indentation; every value is verbatim
run-v7-engines.json  the engine text (targets[].engine) of all 19 targets of the same run, verbatim, as a map from target name
withdrawn-2026-10-04.consolidated.json  bench-results/published-2026-10-04/consolidated.json as it stood on 2026-10-07 (first published shape)
withdrawn-2026-10-04.consolidated.md    bench-results/published-2026-10-04/consolidated.md as it stood on 2026-10-07 (its own text says "Not publishable as is")
v8-dry.consolidated.json  byte for byte the consolidated.json that the consolidate command wrote on 2026-10-07 18:34 UTC from the integrated build (the report fixes and the web fixes merged, before the freeze commit "Bench v8 freeze 2"), on the three v7 runs with renamed copies of them as the second session and two observer summaries (one for each session), repository root lanes/v8-integrate (the dry check's output, /home/dan/gvb-work/lanes/v8-integrate2.e2e/candidate/; command in /home/dan/gvb-work/lanes/v8-integrate2.logs/e2e-consolidate.sh). Its second session is a copy of the first, so its drift is zero by construction; every field and every printed text is the command's own. It replaces the copy of the 1ebbb3f build (490676 bytes), which lacked the report fixes' fields (observerOthers, recordedTexts, searchConfidence, class)
v8-dry.consolidated.md  byte for byte the consolidated.md the same command wrote beside the JSON file above

The tests find this folder from their own source path (see BenchResultsFixtureFiles), so they need no change to Tests.csproj.
