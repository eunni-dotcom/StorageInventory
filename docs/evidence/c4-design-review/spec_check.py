"""Consistency checks of the C4 design review's spec edits."""
import re, sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
results = []
NL = chr(10)

def check(name, ok, detail):
    results.append((name, 'Pass' if ok else 'FAIL', detail))

# 1. new IDs are defined once and referenced
defs = {
    'D-52': r'^\| \*\*D-52\*\* \|',
    'IMP-11': r'^\| IMP-11 \| \*\*Library space',
    'Q-24': r'^\| Q-24 \|',
    'TEST-L9': r'^\| TEST-L9 \|',
}
for k, rx in defs.items():
    n_def = len(re.findall(rx, s, re.M))
    n_ref = len(re.findall(re.escape(k) + r'(?![0-9])', s))
    check(f'{k} defined once and referenced', n_def == 1 and n_ref > 1, f'{n_def} definition, {n_ref} occurrences')

# 2. every TEST-, IMP-, Q-, D- id referenced is defined
def ids(prefix):
    return set(re.findall(prefix + r'[0-9]+[a-z]?', s))
test_defs = set(re.findall(r'^\| (TEST-[A-Z]+[0-9]+) \|', s, re.M))
test_refs = set(re.findall(r'TEST-[A-Z]+[0-9]+', s))
check('every TEST id referenced is defined in §18.1', test_refs <= test_defs, f'{len(test_defs)} defined; undefined: {sorted(test_refs - test_defs)}')
imp_defs = set(re.findall(r'^\| (IMP-[0-9]+) \|', s, re.M))
imp_refs = set(re.findall(r'IMP-[0-9]+', s))
check('every IMP id referenced is defined', imp_refs <= imp_defs, f'defined {sorted(imp_defs, key=lambda x: int(x[4:]))}; undefined {sorted(imp_refs - imp_defs)}')
q_defs = set(re.findall(r'^\| (Q-[0-9]+) \|', s, re.M))
q_refs = set(re.findall(r'Q-[0-9]+', s))
check('every Q id referenced is defined in §21', q_refs <= q_defs, f'{len(q_defs)} defined (Q-01..Q-{max(int(q[2:]) for q in q_defs):02d}); undefined {sorted(q_refs - q_defs)}')
d_defs = set(re.findall(r'^\| (?:\*\*)?(D-[0-9]+)(?:\*\*)? \|', s, re.M))
d_refs = set(re.findall(r'D-[0-9]+', s))
check('every decision id referenced is defined in §20', d_refs <= d_defs, f'{len(d_defs)} defined; undefined {sorted(d_refs - d_defs)}')
perf_defs = set(re.findall(r'^\| (PERF-[0-9]+[ab]?) \|', s, re.M))
perf_refs = set(re.findall(r'PERF-[0-9]+[ab]?', s))
fam = {'PERF-09', 'PERF-02', 'PERF-11'}   # PERF-09 is the retired budget; PERF-02 and PERF-11 name the a/b pairs
check('every PERF id referenced is defined in §15.3 (PERF-02, PERF-11 name the a/b pairs; PERF-09 retired)', perf_refs <= perf_defs | fam, f'undefined {sorted(perf_refs - perf_defs - fam)}')

# 3. stale statements of the replaced design
stale = [
    ('"≤ 5% of the snapshot" only where it is replaced', r'5% of the snapshot', lambda m: all('eplace' in s[max(0, x.start()-300):x.end()+50] or 'C4-B01' in s[max(0, x.start()-300):x.end()+300] for x in m)),
    ('"almost no journal" only in [E-3] readings (§15.1, history)', r'almost no journal', lambda m: all(s.rfind('### 15.1', 0, x.start()) > s.rfind('### 15.2', 0, x.start()) for x in m)),
    ('"journals almost nothing" only as history', r'journals almost nothing', lambda m: len(m) == 0),
    ('"interned library-wide" gone', r'interned library-wide', lambda m: len(m) == 0),
    ('"IMP-01 to IMP-10" only in the §22 history', r'IMP-01 to IMP-10', lambda m: all(x.start() > s.index('## 22. Review repair dispositions') for x in m)),
    ('"name.utf16` is `UNIQUE" gone', r'`name.utf16` is `UNIQUE`', lambda m: len(m) == 0),
    ('"name.utf16 UNIQUE" gone', r'`name.utf16 UNIQUE`', lambda m: len(m) == 0),
    ('"proposed;" budget heading gone', r'Budgets \(proposed', lambda m: len(m) == 0),
    ('hidden staging named only as rejected (in the same line or paragraph)', r'hidden staging', lambda m: all('eject' in s[max(s.rfind(NL + NL, 0, x.start()), s.rfind(NL + '|', 0, x.start())):x.end()+300] for x in m)),
]
for name, rx, ok in stale:
    m = list(re.finditer(rx, s))
    check(name, ok(m), f'{len(m)} occurrence(s)')

# 4. the budget table: every row has 5 columns, a stop column
t = s[s.index('### 15.3 Budgets'):s.index('### 15.4')]
rows = [l for l in t.split('\n') if l.startswith('| PERF-')]
cols_ok = all(l.count(' | ') == 4 for l in rows)
check('§15.3 table: every budget row has the stop-threshold column', cols_ok and len(rows) == 17, f'{len(rows)} rows')

# 5. §15.4 families and cells defined
for k in ['N1 append', 'N2 interleaved', 'N3 mixed', 'N4 real vocabulary', 'N5 re-scan', '**L1**', '**L2**', 'Acceptance cells', 'Worst-case cells', 'Informational cells', '**MET:**', '**MISSED:**', '**NOT MEASURED:**', '**STOP:**']:
    check(f'§15.4 defines {k}', k in s[s.index('### 15.4'):s.index('### 15.5')], '')

# 6. invariant 4 and schema agree
check('§9.2 name table has source_id and the two-column UNIQUE', 'UNIQUE (source_id, utf16)' in s and 'source_id INTEGER NOT NULL REFERENCES source (source_id),   -- names are interned per source' in s, '')
check('§9.5 invariant 4 names the source', "with `source_id` = X" in s, '')

# 7. traceability rows
check('§18.2 lists IMP-11', '| IMP-11 | TEST-L9, TEST-T1, TEST-P1 |' in s, '')

for r in results:
    print(f'| {r[0]} | {r[1]} | {r[2]} |')
print('FAILS:', sum(1 for r in results if r[1] != 'Pass'))
