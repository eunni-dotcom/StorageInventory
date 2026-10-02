"""Assemble §10.2's B1 table: C4 mechanics (A) against the per-source dictionary (B1), same cells.
Usage: python b1_table.py <results dir>

C4 design repair: prints UTF-8 whatever the console code page (ERRATUM.md, E6); sizes are MiB (bytes / 2^20, E5); a B1 cell
whose runs used a per-source prefill built with key-ordered interning is marked with a dagger (E1, prefill_provenance.py)."""
import json, glob, os, re, sys, statistics as st

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prefill_provenance import affected_labels

rd = sys.argv[1]
tainted = {re.sub(r' r\d+$', '', l).replace('analyse ', '') for l in affected_labels(rd)}
runs = {}
for f in glob.glob(os.path.join(rd, '*.jsonl')):
    for line in open(f, encoding='utf-8'):
        d = json.loads(line)
        if 'failed' in d or 'crash' in d or d.get('Mode') != 'normal':
            continue
        runs.setdefault(re.sub(r' r\d+$', '', d['Label']).replace('analyse ', ''), []).append(d)

def cell(label):
    ds = runs.get(label)
    if not ds:
        return None
    return {
        'n': len(ds),
        'j': max(d['PeakJournalHandleBytes'] for d in ds) / 1048576,
        'fr': [d['FilesPerSecond'] for d in ds if not d['Label'].startswith('analyse')],
        'dur': st.median([d['ImportSeconds'] for d in ds if not d['Label'].startswith('analyse')] or [0]),
        'g': st.median(d['GrowthBytes'] for d in ds) / 1048576,
        'ws': max((d['PeakWorkingSetBytes'] - d['WorkingSetBeforeBytes']) / 1048576 for d in ds),
    }

def fmt(c):
    if c is None:
        return '–', '–'
    if not c['fr']:
        return f"{c['j']:,.2f} MiB", '(journal-analysis run only)'
    fr = '; '.join(f'{x:,.0f}' for x in c['fr']) if len(c['fr']) <= 3 else f"{st.median(c['fr']):,.0f} (median of {len(c['fr'])}, {min(c['fr']):,.0f}–{max(c['fr']):,.0f})"
    return f"{c['j']:,.2f} MiB", f"{fr} ({c['dur']:.1f} s)"

rows = [
    ('Interleaved, existing source, 1M / 3×1M', 'hash 1M/3000k', 'persource hash existing 1M/3000k', 'worst-case family'),
    ('Interleaved, **new source**, 1M / 3×1M', 'hash new-source 1M/3000k', 'persource hash new-source 1M/3000k', 'worst-case family'),
    ('1% re-scan of an all-unique source, 1M / 3×1M', 'rescan 1% 1M/3000k', 'persource rescan 1% 1M/3000k', 'worst-case family'),
    ('Mixed, **new source**, 1M / 3×1M', None, 'persource mixed new-source 1M/3000k', 'worst-case family'),
    ('Real vocabulary, existing source, 1M / 3×1M', 'real 1M/3000k', 'persource real 1M/3000k', 'informational'),
    ('**Real vocabulary, new source**, 1M / 3×1M', None, 'persource real new-source 1M/3000k', '**acceptance**'),
    ('**1% re-scan of a real-vocabulary source**, 1M / 3×1M', 'rescanreal 1% 1M/3000k', 'persource rescanreal 1% 1M/3000k', '**acceptance**'),
    ('**Real vocabulary, new source**, 2M / 3×2M', 'real new-source 2M/6000k', 'persource real new-source 2M/6000k', '**acceptance**'),
    ('**1% re-scan of a real-vocabulary source**, 2M / 3×2M', 'rescanreal 1% 2M/6000k', 'persource rescanreal 1% 2M/6000k', '**acceptance**'),
    ('Interleaved, existing source, 2M / 3×2M', 'hash 2M/6000k', 'persource hash existing 2M/6000k', 'worst-case'),
    ('Interleaved, new source, 2M / 3×2M', None, 'persource hash new-source 2M/6000k', 'worst-case'),
    ('Mixed, existing source, 2M / 3×2M', 'mixed 2M/6000k', 'persource mixed existing 2M/6000k', 'worst-case'),
]
print('| Cell | Kind (§15.4 of the design review) | A: peak journal | A: rows/s (duration) | B1: peak journal | B1: rows/s (duration) |')
print('|---|---|---:|---|---:|---|')
for title, a, b, kind in rows:
    ca, cb = (cell(a) if a else None), cell(b)
    ja, ra = fmt(ca)
    jb, rb = fmt(cb)
    if b in tainted:
        rb += ' †'
    print(f'| {title} | {kind} | {ja} | {ra} | {jb} | {rb} |')
print('\n† B1 runs on a per-source prefill that a probe:combined run had built with key-ordered interning (SI_DR_PRESORT=chunk): not clean B1 measurements (ERRATUM.md, E1)')
missing = [x for _, a, b, _ in rows for x in (a, b) if x and x not in runs]
print('\nmissing:', missing)
