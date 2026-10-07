#!/usr/bin/env python3
"""anchors.py REPO  < lines of "path#token"  -> prints FOUND line / MISSING for each, using CodeLines-like comment stripping."""
import sys,re,os
repo=sys.argv[1]
def strip_cs(line, state):
    out=[];q=None;i=0
    while i<len(line):
        c=line[i]; n=line[i+1] if i+1<len(line) else ''
        if state['block']:
            if c=='*' and n=='/': state['block']=False; i+=2; continue
            i+=1; continue
        if q:
            out.append(c)
            if c=='\\' and n: out.append(n); i+=2; continue
            if c==q: q=None
            i+=1; continue
        if c=='/' and n=='/': break
        if c=='/' and n=='*': state['block']=True; i+=2; continue
        if c in '"\'': q=c
        out.append(c); i+=1
    return ''.join(out)
def strip_hash(line):
    q=None
    for i,c in enumerate(line):
        if q:
            if c==q: q=None
            continue
        if c in '"\'': q=c
        elif c=='#' and (i==0 or line[i-1].isspace()): return line[:i]
    return line
def find(path, token):
    ext=os.path.splitext(path)[1].lower()
    try: lines=open(os.path.join(repo,path),encoding='utf-8',errors='replace').read().split('\n')
    except Exception as e: return None
    st={'block':False}
    for i,l in enumerate(lines):
        code=strip_cs(l,st) if ext=='.cs' else strip_hash(l) if ext in('.yaml','.yml','.sh','.py') else l
        if token in code: return i+1,l.strip()[:140]
    return 0,''
for raw in sys.stdin:
    raw=raw.rstrip('\n')
    if not raw.strip() or raw.startswith('//'): continue
    path,_,token=raw.partition('#')
    r=find(path,token)
    if r is None: print('NOFILE ',path)
    elif r[0]==0: print('MISSING',path,'#',token)
    else: print('FOUND  ',path,r[0],'|',r[1])
