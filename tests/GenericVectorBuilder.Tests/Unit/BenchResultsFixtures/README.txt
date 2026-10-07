Verbatim copies of real run and set files, used by the BenchResults page tests. Each file is a byte-for-byte copy or a
trimmed JSON of a real file; the source path is named below. SHA256SUMS lists the hashes of the copies.

run-notes.v5.json  notes, conditions.warmupSearches, conditions.machineControl of bench-results/20261005-023459-eshoponweb/results.json (seed 501, a v5 run)
run-notes.v6.json  the same fields of bench-results/20261005-073329-eshoponweb/results.json (seed 601, a v6 run)
run-notes.v7.json  the same fields of bench-results/20261006-130619-eshoponweb/results.json (seed 701, a v7 run)
withdrawn-2026-10-04.consolidated.json  bench-results/published-2026-10-04/consolidated.json as it stood on 2026-10-07 (first published shape)
withdrawn-2026-10-04.consolidated.md    bench-results/published-2026-10-04/consolidated.md as it stood on 2026-10-07 (its own text says "Not publishable as is")

The tests find this folder from their own source path (see BenchResultsFixtureFiles), so they need no change to Tests.csproj.
