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
            secs = sections(f.read())
        if results.get("rows") is None or results.get("dimension") is None:
            raise Stop(f"{run}: results.json has no rows or dimension")
        notes += weaviate_notes(run, results, secs, a.repo)
        notes += clickhouse_notes(run, results, secs)
        seed = results.get("runSeed")
        if isinstance(seed, int) and seed >= 800:
            x12, x12_path = observer_x12(a.observer, a.observer_ref, seed)
            notes += witness_notes(run, results, secs, x12, x12_path)
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
