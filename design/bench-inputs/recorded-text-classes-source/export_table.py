#!/usr/bin/env python3
"""export_table.py consolidated.json OUT.tsv : the sweep table, one row per clause of every recorded text the report prints and one per fact row."""
import json,sys
d=json.load(open(sys.argv[1]))
rows=[('kind','where','clause','class','printed','basis')]
for t in d['recordedTexts']:
    where=(t['target']+' '+t['field']) if t['target'] else f"run note {t['note']}"
    for c in t['clauses']:
        rows.append(('text',where,c['text'].replace('\t',' ').replace('\n',' '),c['class'],'yes' if c['printed'] else 'no: '+str(c['notPrinted']),'; '.join(c['basis'])))
for w in d['why']:
    for f in w['facts']:
        rows.append(('fact',w['target']+' '+f['kind'],f['text'],f['class'],'yes',f['confidence']+' | '+f['source']))
open(sys.argv[2],'w',encoding='utf-8').write('\n'.join('\t'.join(r) for r in rows)+'\n')
from collections import Counter
print(len(rows)-1,'rows',dict(Counter((r[0],r[3]) for r in rows[1:])))
