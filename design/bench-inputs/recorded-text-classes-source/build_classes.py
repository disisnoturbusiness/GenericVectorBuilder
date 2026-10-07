#!/usr/bin/env python3
"""build_classes.py REPO OUT  -- builds deploy/bench/recorded-text-classes.json from classes_spec.py and checks it against the real v7 claim runs.

Why this exists: the list that classifies every clause of the recorded durability and index texts and of the run notes is long. classes_spec.py holds it in a short
form (the start of each span, its class, what backs it); this script cuts each text into its spans, checks that the spans cover the whole text of all three v7 runs,
checks every basis (a results token in every run, a code, compose, log or test line in its file, a quote of a saved page) and writes the JSON the consolidate command reads.
The consolidate command checks the JSON again on every run, so editing the JSON by hand is also safe; this keeps it reviewable.
Run: python3 build_classes.py REPO_ROOT deploy/bench/recorded-text-classes.json   (anchors.py must sit beside it; no network, no engine, read only on REPO_ROOT)
"""
import html
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
import classes_spec as spec  # noqa: E402

REPO = sys.argv[1]
OUT = sys.argv[2]
RUNS = ['20261006-130619-eshoponweb', '20261006-142724-eshoponweb', '20261006-154837-eshoponweb']
CLASS_NAMES = {'RB': 'read-back', 'ME': 'measured', 'DO': 'documented', 'UN': 'unverified'}


def load_runs():
    runs = []
    for r in RUNS:
        with open(os.path.join(REPO, 'bench-results', r, 'results.json'), encoding='utf-8') as f:
            runs.append((r, json.load(f)))
    return runs


def target_of(run, name):
    for t in run['targets']:
        if t['name'] == name:
            return t
    raise SystemExit(f'target {name} not in run')


def flatten(v):
    if isinstance(v, str):
        return v
    if isinstance(v, list):
        return '\n'.join(flatten(x) for x in v)
    if isinstance(v, dict):
        return '\n'.join(flatten(x) for x in v.values())
    return str(v)


def results_path(run, target, path):
    """target path like load.indexNote or indexState.afterLoad.detail"""
    t = target_of(run, target)
    cur = t
    for part in path.split('.'):
        cur = cur[part]
    return flatten(cur)


def code_find(path, token):
    import subprocess
    p = subprocess.run([sys.executable, os.path.join(os.path.dirname(__file__), 'anchors.py'), REPO], input=f'{path}#{token}\n', capture_output=True, text=True)
    return p.stdout.startswith('FOUND')


def doc_nodes(path):
    s = open(os.path.join(REPO, path), encoding='utf-8', errors='replace').read()
    vis = re.sub(r'<(script|style)\b.*?</\1\s*>|<!--.*?-->', ' ', s, flags=re.S | re.I)
    nodes = [re.sub(r'\s+', ' ', html.unescape(n)).strip() for n in re.split(r'<[^>]*>', vis)]
    return [n for n in nodes if n]


DOC_CACHE = {}


def check_basis(source, problems, runs, label):
    if source.startswith('results:'):
        path, _, token = source[len('results:'):].partition('#')
        m = re.match(r'targets\[([^\]]+)\]\.(.+)', path)
        if m is None:
            for name, run in runs:
                cur = run
                try:
                    for part in re.split(r'\.', re.sub(r'\[[^\]]*\]', '', path)):
                        cur = cur[part]
                except (KeyError, TypeError):
                    problems.append(f'{label}: no field {path} in {name}')
                    continue
                if token not in flatten(cur):
                    problems.append(f'{label}: token not in {name}: {source}')
            return
        for name, run in runs:
            try:
                text = results_path(run, m.group(1), m.group(2))
            except KeyError:
                problems.append(f'{label}: {source}: no field in {name}')
                continue
            if token not in text:
                problems.append(f'{label}: token not in {name}: {source}')
        return
    if source.startswith('doc:'):
        path, _, quote = source[4:].partition('#')
        nodes = DOC_CACHE.setdefault(path, doc_nodes(path))
        if not any(quote in n for n in nodes):
            problems.append(f'{label}: quote not inside one node: {source}')
        return
    path, _, token = source.partition('#')
    if not os.path.exists(os.path.join(REPO, path)):
        problems.append(f'{label}: no file {path}')
        return
    if not code_find(path, token):
        problems.append(f'{label}: token not on a code line: {source}')


def build():
    runs = load_runs()
    out_texts = []
    problems = []
    for (target, field), spans in sorted(spec.SPEC.items()):
        texts = set()
        for name, run in runs:
            t = target_of(run, target)
            texts.add(t.get(field) or '')
        if len(texts) != 1:
            problems.append(f'{target}.{field}: the three v7 runs differ')
        text = texts.pop()
        if not text:
            problems.append(f'{target}.{field}: empty text')
            continue
        starts = []
        pos = 0
        for sp in spans:
            marker = sp[0]
            at = text.find(marker, pos)
            if at < 0:
                problems.append(f'{target}.{field}: marker not found after {pos}: {marker[:60]!r}')
                break
            starts.append(at)
            pos = at + 1
        else:
            # a span starts at a word, not at the punctuation that joins it to the span before: the joiner stays with the earlier span
            for i in range(1, len(starts)):
                while text[starts[i]] in ':;, ':
                    starts[i] += 1
            if starts[0] != 0:
                problems.append(f'{target}.{field}: first marker is not at the start (text starts {text[:50]!r})')
            entry_spans = []
            for i, sp in enumerate(spans):
                end = starts[i + 1] if i + 1 < len(starts) else len(text)
                span_text = text[starts[i]:end].strip()
                cls, basis = sp[1], sp[2]
                opts = sp[3] if len(sp) > 3 else {}
                label = f'{target}.{field}[{i}]'
                if cls in ('RB', 'ME', 'DO') and not basis:
                    problems.append(f'{label}: class {cls} needs a basis')
                if cls == 'UN' and basis:
                    problems.append(f'{label}: an unverified span carries a basis')
                for b in basis:
                    check_basis(b, problems, runs, label)
                d = {'text': span_text, 'class': CLASS_NAMES[cls], 'basis': basis}
                d.update(opts)
                entry_spans.append(d)
            out_texts.append({'target': target, 'field': field, 'spans': entry_spans})
    return out_texts, problems


def check_notes(problems, runs):
    for name, run in runs:
        for i, note in enumerate(run['notes']):
            if note.startswith('WARNING'):
                continue
            entries = [n for n in spec.NOTES if re.search(n['match'], note)]
            if len(entries) != 1:
                problems.append(f'{name} notes[{i}]: {len(entries)} entries match: {note[:70]!r}')
                continue
            pos = 0
            for k, sp in enumerate(entries[0]['spans']):
                while pos < len(note) and note[pos] == ' ':
                    pos += 1
                m = re.compile(sp[0].lstrip(' '), re.S).match(note, pos)
                if not m or m.end() == pos:
                    problems.append(f'{name} notes[{i}] span {k}: pattern does not match at {pos}: {note[pos:pos+60]!r}')
                    break
                pos = m.end()
            else:
                if note[pos:].strip():
                    problems.append(f'{name} notes[{i}]: uncovered tail {note[pos:pos+60]!r}')
            for sp in entries[0]['spans']:
                for b in sp[2]:
                    check_basis(b, problems, runs[:1], f'notes[{i}]')


def main():
    texts, problems = build()
    check_notes(problems, load_runs())
    for p in problems:
        print('PROBLEM', p)
    result = {'texts': texts}
    if hasattr(spec, 'extra'):
        result.update(spec.extra(REPO))
    with open(OUT, 'w', encoding='utf-8') as f:
        json.dump(result, f, indent=1, ensure_ascii=False)
        f.write('\n')
    n = sum(len(t['spans']) for t in texts)
    print(f'{len(texts)} texts, {n} spans, {len(problems)} problems')


if __name__ == '__main__':
    main()
