#!/usr/bin/env python3
"""Checks the benchmark's recall tie rule against the eShopOnWeb data it ran on, and writes tie-check-eshoponweb-2026-10-07.json.

Why this file exists: the benchmark's method note says a hit whose exact similarity ties the k-th best (within 1e-5) also counts
"because duplicate rows embed to identical vectors". Whether that clause applies to THIS data is a fact about the data, so the report
states it from this saved check and not from the note.

What it does (read only; nothing is written to any database):
  1. reads every row of GenericVectorBuilder.dbo.gvb_eshoponweb (ChunkId order) with sqlcmd, the way the benchmark's own reader does;
  2. normalizes each vector to length 1 (the benchmark does the same, so a dot product is the cosine);
  3. counts pairs of rows whose dot product is at least 1 - EPSILON (duplicate vectors);
  4. reads the 20 golden query vectors from the benchmark's own cache file, normalizes them, and for each query ranks every row by dot product;
     the benchmark's rule (BenchMath.RecallAtK) counts a returned row as correct when its score is at least the k-th best score less
     EPSILON, so the number of rows OUTSIDE the exact top k that this rule can add is the count of rows with score >= kth - EPSILON.
The arithmetic is double precision on the float32 values read; the smallest gap printed (query 10th best to 11th best) is several times EPSILON and float32 rounding is about 1e-7, so precision does not change a count.

Run: python3 tie-check-eshoponweb.py OUT.json   (needs sqlcmd on PATH and the SQL Server on 127.0.0.1,1433; every external call has a timeout)
"""
import hashlib
import itertools
import json
import math
import operator
import os
import subprocess
import sys
import time

EPSILON = 1e-5
TOP = 10
TABLE = "dbo.gvb_eshoponweb"
DATABASE = "GenericVectorBuilder"
SERVER = "127.0.0.1,1433"
QUERY_CACHE = "/home/dan/gvb-data/bench-cache/golden-eshoponweb-68df42ca69efa079.json"
SQLCMD_TIMEOUT_SECONDS = 300


def read_rows():
    """Returns [(chunk_id, vector)] in ChunkId order, from one read-only SELECT."""
    sql = f"SET NOCOUNT ON; SELECT ChunkId, CAST( Embedding AS NVARCHAR(MAX) ) FROM {TABLE} ORDER BY ChunkId;"
    command = ["sqlcmd", "-S", SERVER, "-U", "claude", "-P", "", "-C", "-l", "15", "-d", DATABASE, "-y", "0", "-s", "|", "-Q", sql]
    done = subprocess.run(command, capture_output=True, text=True, timeout=SQLCMD_TIMEOUT_SECONDS, check=False)
    if done.returncode != 0:
        raise SystemExit(f"sqlcmd failed with exit {done.returncode}: {done.stderr[:300]}")
    rows = []
    for line in done.stdout.splitlines():
        if not line.strip():
            continue
        chunk_id, _, vector_text = line.partition("|")
        if not vector_text.lstrip().startswith("["):
            continue  # the header line and its underline
        rows.append((chunk_id.strip(), json.loads(vector_text)))
    return rows


def normalize(vector):
    norm = math.sqrt(sum(x * x for x in vector))
    if not math.isfinite(norm) or norm == 0:
        raise SystemExit("a vector has no usable length")
    return [x / norm for x in vector]


def dot(a, b):
    return sum(itertools.starmap(operator.mul, zip(a, b)))


def main():
    out = sys.argv[1]
    started = time.time()
    rows = read_rows()
    if len(rows) == 0:
        raise SystemExit("no rows read")
    dims = {len(v) for _, v in rows}
    if len(dims) != 1:
        raise SystemExit(f"rows have different dimensions: {sorted(dims)}")
    vectors = [normalize(v) for _, v in rows]
    distinct_text = len({json.dumps(v) for _, v in rows})

    duplicate_pairs = 0
    max_pair = -1.0
    for i in range(len(vectors)):
        for j in range(i + 1, len(vectors)):
            d = dot(vectors[i], vectors[j])
            if d > max_pair:
                max_pair = d
            if d >= 1.0 - EPSILON:
                duplicate_pairs += 1

    with open(QUERY_CACHE, "rb") as handle:
        cache_bytes = handle.read()
    queries = json.loads(cache_bytes)
    per_query = []
    for q, query in enumerate(queries):
        query = normalize(query)
        scores = sorted((dot(query, v) for v in vectors), reverse=True)
        kth = scores[TOP - 1]
        after = scores[TOP]
        tied_outside = sum(1 for s in scores[TOP:] if s >= kth - EPSILON)
        per_query.append({"query": q, "kthScore": round(kth, 6), "nextScore": round(after, 6), "gap": round(kth - after, 6), "rowsOutsideTopKWithinEpsilon": tied_outside})

    result = {
        "what": "tie-rule check of the benchmark's recall on its own data (see tie-check-eshoponweb.py)",
        "table": TABLE,
        "database": DATABASE,
        "rows": len(rows),
        "dimension": dims.pop(),
        "distinctVectorTexts": distinct_text,
        "epsilon": EPSILON,
        "top": TOP,
        "duplicatePairs": duplicate_pairs,
        "maxPairwiseDot": round(max_pair, 6),
        "queryCache": os.path.basename(QUERY_CACHE),
        "queryCacheSha256": hashlib.sha256(cache_bytes).hexdigest(),
        "queries": len(queries),
        "queriesWithRowsOutsideTopKWithinEpsilon": sum(1 for p in per_query if p["rowsOutsideTopKWithinEpsilon"] > 0),
        "smallestGap": min(p["gap"] for p in per_query),
        "perQuery": per_query,
        "seconds": round(time.time() - started, 1),
    }
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=1)
        handle.write("\n")
    print(f"rows {result['rows']}, duplicate pairs {duplicate_pairs}, max pairwise dot {result['maxPairwiseDot']}, queries with a tied row {result['queriesWithRowsOutsideTopKWithinEpsilon']} of {len(queries)}, smallest gap {result['smallestGap']}")


if __name__ == "__main__":
    main()
