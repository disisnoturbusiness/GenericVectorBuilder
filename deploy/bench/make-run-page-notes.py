#!/usr/bin/env python3
"""
make-run-page-notes.py: writes deploy/bench/run-page-notes.json, the list of recorded statements in the benchmark runs that a published set
marks as false or misleading, which the web pages print as corrections on the page of each run.

Why a generator and not a hand-typed list: every figure in a correction comes from a recorded field of the run (results.json), from a repository
file (the Weaviate compose file, the ClickHouse reset code) or from a saved observer file (analysis-80N.txt, line X12), and every statement is
checked to exist in the run's results.md under the engine's own heading. A statement that cannot be found stops the script (exit 1); nothing is
guessed and nothing is written half way (the output is written to a temporary file and renamed).

What it lists (the same rules for every run it is given):
  weaviate     "weaviate.compose.yaml sets no persistence variable" in the durability text: false, the compose file sets PERSISTENCE_DATA_PATH.
  clickhouse   "N added by this load" when N is more than ten times, or less than a tenth of, the raw vector payload (rows x dimension x 4 bytes):
               misleading, the figure is the growth of the whole data folder, not what the load wrote.
  clickhouse   "(the folder was steady)" in the reset text of a run that has one: misleading, it describes the folder after the reset.
  any engine   "N at the start" in the data folder line of a run that has an observer file, when the observer's line X12 lists the engine as
               differing by more than one percent from the folder size read when its container was created: misleading.
  vespa 803    "N added by this load" when X12 lists the engine: misleading, the figure rests on the start reading X12 disputes.
  any run      "Measured end to end through each engine's .NET client" (the framing line of results.md): false, the engines of the fact sheet whose protocol row names the benchmark's own
               HttpClient REST code are not reached through a .NET client of the engine; every one of them is cited by the code line the fact sheet gives.
  any run      "an earlier run gave about 0.09 at 100,000" (a clause of the MariaDB index text): unbacked, no log was saved for it, the summary drops it, and a saved re-run (design/bench-inputs/
               mariadb-effort-2026-10-07) gives the range of recall@10 at ef 100 at 524 and at 2,000 vectors, computed here from its run.log.

  any run      "so every CPU's clock is held at its ceiling of N MHz whatever the engine runs" (a clause of the machine control note, which the run page prints in the report text and, for a run with no
               clock block, again on its clock line): for a v8 run (seed 800 or more) false, the saved observer analysis (analysis-80N.txt, line X02) reads a pass under the pin, quoted with its CPU,
               its MHz and its basis points; for a v7 run (seed 700 to 799) unbacked, no saved reading of that run backs "whatever the engine runs", the summary drops the clause. Any other run that
               holds the clause stops the script, because no rule says what to call it.

Every external action is a local file read; there is no network and no subprocess. Run it from anywhere:
  make-run-page-notes.py --repo REPO --results REPO/bench-results --observer REPO/design/bench-inputs/observer-v8 --out REPO/deploy/bench/run-page-notes.json RUN...
The observer files (analysis-801.txt to analysis-803.txt) are byte copies of the run operator's analysis, kept in the repository so the sources of the
corrections that cite them stay readable; the sources name them by their repository path.
"""
import argparse
import ast
import json
import os
import re
import sys
import tempfile

MIB = 1024 * 1024
GIB = 1024 * MIB
WEAVIATE_STATEMENT = "weaviate.compose.yaml sets no persistence variable"
FRAMING_STATEMENT = "Measured end to end through each engine's .NET client"
MARIADB_STATEMENT = "an earlier run gave about 0.09 at 100,000"
CLOCK_STATEMENT = re.compile(r"so every CPU's clock is held at its ceiling of (\d+) MHz whatever the engine runs")
WORST_READING = re.compile(r"X02: .*?worst deviation ([\d.]+) bp \(([\w-]+)/(\S+) cpu(\d+) ([\d.]+) MHz\)(?:, limit (\d+) bp)?")
V7_FIRST_SEED = 700
V8_FIRST_SEED = 800
BP_TOLERANCE = 0.1
FACTS_FILE = "src/GenericVectorBuilder.Bench/Report/engine-facts.json"
CLASSES_FILE = "deploy/bench/recorded-text-classes.json"
MARIADB_DIR = "design/bench-inputs/mariadb-effort-2026-10-07"
RECALL_LINE = re.compile(r"^\s*RECALL@10 on (\d+) random 1024-dim vectors, 50 queries: ef 20 [\d.]+, ef 100 ([\d.]+), ef 3200 [\d.]+\s*$")
STEADY_STATEMENT = "(the folder was steady)"
RAW_FACTOR = 10
FLOAT_BYTES = 4


class Stop(Exception):
    """A rule could not be applied; the message says which run, which engine and what was missing."""


def fmt_bytes(n):
    """Bytes as the bench tool formats a size in its reports (two decimals, KiB / MiB / GiB)."""
    if n >= GIB:
        return f"{n / GIB:.2f}".rstrip("0").rstrip(".") + " GiB"
    if n >= MIB:
        return f"{n / MIB:.2f}".rstrip("0").rstrip(".") + " MiB"
    if n >= 1024:
        return f"{n / 1024:.2f}".rstrip("0").rstrip(".") + " KiB"
    return f"{n} B"


def whole(n):
    """A byte count with thousands separators."""
    return f"{n:,}"


def read_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def sections(md):
    """results.md as {heading text: [lines]} for the headings of level 2 to 4; a line belongs to the last heading before it."""
    out, current = {}, ""
    for line in md.split("\n"):
        m = re.match(r"^(#{2,4}) (.+?)\s*$", line)
        if m:
            current = m.group(2)
        out.setdefault(current, []).append(line)
    return out


def find_line(lines, statement, run, target):
    """The one line of the target's section that holds the statement; stop when there is none."""
    hits = [ln for ln in lines if statement in ln]
    if not hits:
        raise Stop(f"{run}: statement not found under heading {target}: {statement}")
    return hits[0]


def observer_x12(observer, ref, seed):
    """The tuples of line X12 of the observer's analysis of one v8 run as {target: (tool bytes, witness bytes)} and the repository path to cite; empty when there is no file."""
    name = f"analysis-{seed}.txt"
    path = os.path.join(observer, name)
    cite = f"{ref}/{name}"
    if not os.path.exists(path):
        return {}, cite
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("INFO  X12"):
                m = re.search(r"differ by more than 1 percent: (\[.*?\]);", line)
                if not m:
                    raise Stop(f"{path}: line X12 has no list of differing targets")
                return {t: (a, b) for t, a, b in ast.literal_eval(m.group(1))}, cite
    raise Stop(f"{path}: no line X12")


def note(run, target, field, statement, kind, text, sources):
    return {"runs": [run], "target": target, "field": field, "statement": statement, "kind": kind, "note": text, "sources": sources}


def weaviate_notes(run, results, secs, repo):
    """The false Weaviate statement, with the compose file's own line as the source."""
    t = next((x for x in results["targets"] if x["name"] == "weaviate"), None)
    if t is None or WEAVIATE_STATEMENT not in json.dumps(t):
        return []
    find_line(secs.get("weaviate", []), WEAVIATE_STATEMENT, run, "weaviate")
    compose = os.path.join(repo, "deploy/engines/weaviate.compose.yaml")
    with open(compose, encoding="utf-8") as f:
        line = next((ln.strip() for ln in f if re.match(r"^\s*PERSISTENCE_DATA_PATH\s*:", ln)), None)
    if line is None:
        raise Stop(f"{compose}: no PERSISTENCE_DATA_PATH line, so the statement cannot be called false")
    text = f"deploy/engines/weaviate.compose.yaml sets {line.replace(': ', ' to ', 1)}. The compose file does set a persistence variable."
    return [note(run, "weaviate", "durability", WEAVIATE_STATEMENT, "false", text, [
        {"kind": "file", "ref": f"deploy/engines/weaviate.compose.yaml#{line}", "value": line},
        {"kind": "results", "ref": f"results@{run}:targets[weaviate].durability#sets no persistence variable", "value": "sets no persistence variable"}])]


def clickhouse_notes(run, results, secs):
    """The ClickHouse growth figure when it is far above the vector payload, and the 'steady' wording of the reset text."""
    t = next((x for x in results["targets"] if x["name"] == "clickhouse"), None)
    if t is None:
        return []
    out = []
    rows, dim = results["rows"], results["dimension"]
    raw = rows * dim * FLOAT_BYTES
    disk = t.get("disk") or {}
    text = disk.get("text") or ""
    m = re.match(r"^(.+? added by this load) \((.+?) \(whole engine data folder\), (.+?) before\)$", text)
    figure = disk.get("bytes", 0)
    if m and ( figure > RAW_FACTOR * raw or figure * RAW_FACTOR < raw ):
        find_line(secs.get("clickhouse", []), m.group(1), run, "clickhouse")
        body = (f"The figure is the growth of the whole data folder over the engine's turn, as the same line says ({m.group(2)} at the end, {m.group(3)} before). "
                f"The load itself is {rows} vectors of {dim} dimensions, {whole(raw)} bytes as {FLOAT_BYTES}-byte floats ({fmt_bytes(raw)}); the figure is {whole(disk['bytes'])} bytes.")
        out.append(note(run, "clickhouse", "disk.text", m.group(1), "misleading", body, [
            {"kind": "results", "ref": f"results@{run}:targets[clickhouse].disk.text", "value": text},
            {"kind": "results", "ref": f"results@{run}:rows", "value": str(rows)},
            {"kind": "results", "ref": f"results@{run}:dimension", "value": str(dim)}]))
    reset = ((t.get("dataFolder") or {}).get("reset")) or ""
    if STEADY_STATEMENT in reset:
        find_line(secs.get("clickhouse", []), STEADY_STATEMENT, run, "clickhouse")
        mm = re.search(r"data folder (.+?) before and (.+?) after \(the folder was steady\)", reset)
        if not mm:
            raise Stop(f"{run}: the ClickHouse reset text has the steady wording and no before and after sizes")
        body = (f"The words describe the folder after the reset: it stayed within the tolerance over a stability window before the after reading was taken. "
                f"They do not say the reset left the folder unchanged; the same sentence records {mm.group(1)} before and {mm.group(2)} after.")
        out.append(note(run, "clickhouse", "dataFolder.reset", STEADY_STATEMENT, "misleading", body, [
            {"kind": "file", "ref": "src/GenericVectorBuilder.Bench/Targets/ClickHouseStartState.cs#the folder was steady", "value": "the folder was steady"},
            {"kind": "file", "ref": "src/GenericVectorBuilder.Bench/Targets/ClickHouseStartState.cs#True when the folder stayed within the tolerance over a full stability window before the deadline.",
             "value": "True when the folder stayed within the tolerance over a full stability window before the deadline."},
            {"kind": "results", "ref": f"results@{run}:targets[clickhouse].dataFolder.reset", "value": reset}]))
    return out


def witness_notes(run, results, secs, x12, x12_path):
    """The start figures of the data folder that the observer's witness disputes, and Vespa's growth figure when it is disputed."""
    out = []
    for target, (tool, witness) in sorted(x12.items()):
        t = next((x for x in results["targets"] if x["name"] == target), None)
        if t is None:
            raise Stop(f"{run}: the observer lists {target}, which the run does not hold")
        folder = t.get("dataFolder") or {}
        if folder.get("bytesAtStart") != tool:
            raise Stop(f"{run}: {target} bytesAtStart is {folder.get('bytesAtStart')} in results.json and {tool} in line X12")
        lines = secs.get(target, [])
        pattern = re.compile(r"^- Data folder \S+: (.+? at the start)")
        hit = next((pattern.match(ln) for ln in lines if pattern.match(ln)), None)
        if hit is None:
            raise Stop(f"{run}: no 'Data folder ... at the start' line under heading {target}")
        pct = abs(tool - witness) * 100.0 / witness
        side = "above" if tool > witness else "below"
        body = (f"A witness read this data folder at {whole(witness)} bytes ({fmt_bytes(witness)}) when the container was created, before the engine ran. "
                f"The tool recorded {whole(tool)} bytes ({fmt_bytes(tool)}) as the start, {pct:.0f} percent {side} the witness. The two readings disagree; this line prints the tool's.")
        src = [{"kind": "file", "ref": f"{x12_path}#X12", "value": f"({target!r}, {tool}, {witness})"},
               {"kind": "results", "ref": f"results@{run}:targets[{target}].dataFolder.bytesAtStart", "value": str(tool)}]
        out.append(note(run, target, "dataFolder.bytesAtStart", hit.group(1), "misleading", body, src))
        disk = (t.get("disk") or {}).get("text") or ""
        dm = re.match(r"^(.+? added by this load) \(", disk)
        end = folder.get("bytesAtEnd")
        if target == "vespa" and dm and end is not None:
            find_line(lines, dm.group(1), run, target)
            grown = end - witness
            body = (f"The figure rests on the tool's start reading ({whole(tool)} bytes), which the witness disputes (above). From the witness's reading and the recorded end "
                    f"({whole(end)} bytes) the folder grew by {whole(grown)} bytes ({fmt_bytes(grown)}) over the turn.")
            out.append(note(run, target, "disk.text", dm.group(1), "misleading", body, src + [
                {"kind": "results", "ref": f"results@{run}:targets[{target}].dataFolder.bytesAtEnd", "value": str(end)}]))
    return out


def holds(md, statement):
    """The lines of a results.md that hold the statement."""
    return [ln for ln in md.split("\n") if statement in ln]


def file_line_with(repo, path, token):
    """The one line of a repository file that holds the token; stop when there is none, so a source is never cited for a line that is not there."""
    with open(os.path.join(repo, path), encoding="utf-8") as f:
        for ln in f:
            if token in ln:
                return ln.rstrip("\n")
    raise Stop(f"{path}: no line holds the source token: {token}")


def names(items):
    """Names as 'a', 'a and b' or 'a, b and c'."""
    return items[0] if len(items) == 1 else ", ".join(items[:-1]) + " and " + items[-1]


def framing_notes(run, results, md, repo):
    """The old framing line of results.md, false for the engines the benchmark reaches with its own HttpClient REST code (the fact sheet's protocol rows say which)."""
    if not holds(md, FRAMING_STATEMENT):
        return []
    present = {t["name"] for t in results["targets"]}
    facts = read_json(os.path.join(repo, FACTS_FILE))
    rows = {}
    for fact in facts:
        if fact.get("kind") == "protocol" and fact["text"].startswith("HttpClient") and fact["target"] in present and fact["target"] not in rows:
            rows[fact["target"]] = fact["source"]
    if not rows:
        raise Stop(f"{run}: the framing line is in results.md and the fact sheet names no engine reached through HttpClient")
    sources = []
    for target in sorted(rows):
        path, _, token = rows[target].partition("#")
        file_line_with(repo, path, token)
        sources.append({"kind": "file", "ref": rows[target], "value": token})
    text = (f"The statement is false for {len(rows)} of the {len(present)} engines of this run: {names(sorted(rows))} are reached through the benchmark's own HttpClient REST code, "
            f"not through a .NET client of the engine. For those engines the figure is the cost of the benchmark's own request code. A code line of each is cited.")
    return [note(run, "", "framing line", FRAMING_STATEMENT, "false", text, sources)]


def mariadb_recall_range(repo):
    """The lowest and highest recall@10 at ef 100 of the saved MariaDB re-run for 524 and for 2,000 vectors, with the log line of each and the number of repeats."""
    found = {}
    with open(os.path.join(repo, MARIADB_DIR, "run.log"), encoding="utf-8") as f:
        for ln in f:
            m = RECALL_LINE.match(ln)
            if m:
                found.setdefault(int(m.group(1)), []).append((float(m.group(2)), m.group(2), ln.strip()))
    for size in (524, 2000):
        if not found.get(size):
            raise Stop(f"{MARIADB_DIR}/run.log: no RECALL@10 line for {size} vectors")
    if len(found[524]) != len(found[2000]):
        raise Stop(f"{MARIADB_DIR}/run.log: {len(found[524])} repeats at 524 vectors and {len(found[2000])} at 2,000")
    return {size: (min(rows), max(rows)) for size, rows in found.items()}, len(found[524])


def mariadb_notes(run, md, repo):
    """The MariaDB clause that cites a measurement no saved source backs: unbacked, with the saved re-run's range as the figures that replace it."""
    if not holds(md, MARIADB_STATEMENT):
        return []
    ranges, repeats = mariadb_recall_range(repo)
    (lo524, hi524), (lo2000, hi2000) = ranges[524], ranges[2000]
    text = (f"No log was saved for this figure, and the summary does not print the clause. A saved re-run of the MariaDB effort test ({repeats} repeats, 524 and 2,000 random 1024-dimension vectors) "
            f"read recall@10 at ef 100 of {lo524[1]} to {hi524[1]} at 524 vectors and {lo2000[1]} to {hi2000[1]} at 2,000. The 100,000-row figure was not re-measured the same way and is dropped.")
    sources = []
    for token in ("for which no log was saved", "The 100,000-row figure was not re-measured the same way, so the report drops it."):
        file_line_with(repo, f"{MARIADB_DIR}/SOURCES.txt", token)
        sources.append({"kind": "file", "ref": f"{MARIADB_DIR}/SOURCES.txt#{token}", "value": token})
    for _, _, line in (lo524, hi524, lo2000, hi2000):
        sources.append({"kind": "file", "ref": f"{MARIADB_DIR}/run.log#{line}", "value": line})
    drop = '"drop": "mariadb-recall"'
    file_line_with(repo, CLASSES_FILE, drop)
    sources.append({"kind": "file", "ref": f"{CLASSES_FILE}#{drop}", "value": drop})
    return [note(run, "", "index", MARIADB_STATEMENT, "unbacked", text, sources)]


def observer_worst(observer, ref, seed, run):
    """The observer's worst per-CPU clock reading of one v8 run (line X02 of its saved analysis) and the repository path to cite; stops when the file or the line is missing."""
    name = f"analysis-{seed}.txt"
    path = os.path.join(observer, name)
    if not os.path.exists(path):
        raise Stop(f"{run}: the observer analysis {path} is not there, so the clock clause cannot be called false")
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = WORST_READING.search(line)
            if m:
                return m, line, f"{ref}/{name}"
    raise Stop(f"{path}: no line X02 with a worst reading")


def clock_notes(run, results, md, repo, observer, observer_ref):
    """The clock clause of the machine control note: false for a v8 run (the saved observer reading puts a pass under the pin), unbacked for a v7 run (no saved reading of it)."""
    found = sorted({m.group(0) for m in CLOCK_STATEMENT.finditer(md)})
    if not found:
        return []
    if len(found) != 1:
        raise Stop(f"{run}: the report holds {len(found)} different forms of the clock clause: {found}")
    statement = found[0]
    ceiling = int(CLOCK_STATEMENT.search(statement).group(1))
    seed = results.get("runSeed")
    if not isinstance(seed, int):
        raise Stop(f"{run}: the report holds the clock clause and results.json has no runSeed, so the rule for v7 and v8 cannot be applied")
    index = next((i for i, n in enumerate(results.get("notes") or []) if statement in n), None)
    if index is None:
        raise Stop(f"{run}: the clock clause is in results.md and in no entry of results.json notes")
    drop = '"drop": "clock-held"'
    file_line_with(repo, CLASSES_FILE, drop)
    sources = [{"kind": "results", "ref": f"results@{run}:notes[{index}]#{statement}", "value": statement}]
    if seed >= V8_FIRST_SEED:
        m, _, cite = observer_worst(observer, observer_ref, seed, run)
        bp, target, label, cpu, mhz = float(m.group(1)), m.group(2), m.group(3), m.group(4), float(m.group(5))
        if mhz >= ceiling or abs((ceiling - mhz) * 10000.0 / ceiling - bp) > BP_TOLERANCE:
            raise Stop(f"{run}: the observer's worst reading ({mhz} MHz, {bp} bp) is not {bp} bp under the pin of {ceiling} MHz")
        token = f"worst deviation {m.group(1)} bp ({target}/{label} cpu{cpu} {m.group(5)} MHz)"
        file_line_with(repo, f"{cite}", token)
        limit = f" That is inside the observer's own limit of {m.group(6)} basis points: the pin held to within that, and it did not hold at the ceiling." if m.group(6) else ""
        pass_name = f"{label} pass (eight searchers at once)" if label.endswith("@8") else f"{label} pass"
        text = (f"The observer's APERF and MPERF readings of this run put {target}'s {pass_name} under the pin: CPU {cpu} read {m.group(5)} MHz against {ceiling} MHz, "
                f"{m.group(1)} basis points under it ({bp / 100.0:.3f} percent).{limit} The clause says every CPU's clock is held at its ceiling whatever the engine runs; "
                f"for that pass it was not. The summary does not print the clause.")
        sources.append({"kind": "file", "ref": f"{cite}#{token}", "value": token})
        kind = "false"
    elif seed >= V7_FIRST_SEED:
        words = "Byte copies of the run operator's analysis of the three v8 runs (seeds 801, 802, 803)"
        file_line_with(repo, f"{observer_ref}/SOURCES.txt", words)
        text = (f"No saved reading backs the clause for this run. The APERF and MPERF analyses kept in {observer_ref} are of the three v8 runs, not of this one, and the summary does not print the clause. "
                f"In the v8 runs, made under the same pin, the observer read a pass under it (see the correction to the clause on those pages).")
        sources.append({"kind": "file", "ref": f"{observer_ref}/SOURCES.txt#{words}", "value": words})
        kind = "unbacked"
    else:
        raise Stop(f"{run}: the report holds the clock clause and its seed {seed} is neither a v7 run (700 to 799) nor a v8 run (800 or more)")
    sources.append({"kind": "file", "ref": f"{CLASSES_FILE}#{drop}", "value": drop})
    return [note(run, "", "notes", statement, kind, text, sources)]


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", required=True)
    ap.add_argument("--results", required=True)
    ap.add_argument("--observer", required=True)
    ap.add_argument("--observer-ref", default="design/bench-inputs/observer-v8", help="the repository path the sources cite for the observer files")
    ap.add_argument("--out", required=True)
    ap.add_argument("runs", nargs="+")
    a = ap.parse_args(argv)
    notes = []
    for run in a.runs:
        base = os.path.join(a.results, run)
        results = read_json(os.path.join(base, "results.json"))
        with open(os.path.join(base, "results.md"), encoding="utf-8") as f:
            md = f.read()
        secs = sections(md)
        if results.get("rows") is None or results.get("dimension") is None:
            raise Stop(f"{run}: results.json has no rows or dimension")
        notes += weaviate_notes(run, results, secs, a.repo)
        notes += clickhouse_notes(run, results, secs)
        seed = results.get("runSeed")
        if isinstance(seed, int) and seed >= 800:
            x12, x12_path = observer_x12(a.observer, a.observer_ref, seed)
            notes += witness_notes(run, results, secs, x12, x12_path)
        notes += framing_notes(run, results, md, a.repo)
        notes += mariadb_notes(run, md, a.repo)
        notes += clock_notes(run, results, md, a.repo, a.observer, a.observer_ref)
    doc = {"format": "run-page-notes-1", "generatedBy": "deploy/bench/make-run-page-notes.py", "runs": a.runs, "notes": notes}
    out_dir = os.path.dirname(os.path.abspath(a.out))
    fd, tmp = tempfile.mkstemp(dir=out_dir, prefix=".run-page-notes.", suffix=".tmp")
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1, ensure_ascii=False)
        f.write("\n")
    os.replace(tmp, a.out)
    print(f"wrote {len(notes)} notes for {len(a.runs)} runs to {a.out}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Stop as e:
        print(f"STOP: {e}", file=sys.stderr)
        sys.exit(1)
