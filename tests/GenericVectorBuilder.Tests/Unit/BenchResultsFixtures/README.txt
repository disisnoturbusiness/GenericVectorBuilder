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

v8h.consolidated.json  byte for byte the consolidated.json that the consolidate command wrote on 2026-10-08 from the integrated build (the report fixes of the v8h round, as delivered to the repository) on the real v7 and v8 runs and the v5 and v6 basis runs; the shape the v8h web fixes are tested against (ClickHouse's QPS@8 not held, the build sentences, the results hashes, the image references, the clause tables)
v8h.consolidated.md  byte for byte the consolidated.md the same command wrote beside the JSON file above

The tests find this folder from their own source path (see BenchResultsFixtureFiles), so they need no change to Tests.csproj.

The bench-split files are for the tests of the split of the summary page into the Vector search benchmark page (/benchmark) and the full results page (/bench-results).
bench-split.consolidated.json  the v8 test fixture (BenchResultsV8Fixture) with a data line, the recorded queries, the builds, a not-held ClickHouse row and the Redis note added, written with indentation; the input of the pages below
bench-split.consolidated.md  the fixture's report (BenchResultsV8Fixture.REPORT_MD), the second input of the set page
bench-split.results.json  the shared run fixture (BenchResultsFixtures.RUN_RESULTS_V8_JSON), the input of the run page
bench-split.list-page.pre-6865c80.html  the /bench-results page for those inputs, rendered by the code of commit 3f16f85 (the parent of 6865c80, the commit that put a header on that page), with the run folder 20261006-130619-eshoponweb beside the set published-2026-10-08
bench-split.set-page.pre-6865c80.html  the page of the folder published-2026-10-08, rendered by the code of 3f16f85 (byte for byte the page 6865c80 rendered)
bench-split.run-page.pre-6865c80.html  the page of the folder 20261006-130619-eshoponweb, rendered by the code of 3f16f85 (byte for byte the page 6865c80 rendered)
bench-split.header.at-6865c80.html  the header section the /bench-results page had at 6865c80 for those inputs, with the link and the three places that said "below" as they were then
