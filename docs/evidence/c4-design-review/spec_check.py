"""Consistency checks of the C4 design review's spec edits and of the C4 design repair's (scripted part of §23.1).

Usage:  python spec_check.py docs/v1.1-preproduction-spec.md        prints a markdown table and the number of failures
"""
import re
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

p = sys.argv[1]
s = open(p, encoding='utf-8').read()
results = []
NL = chr(10)


def check(group, name, ok, detail=''):
    results.append((group, name, 'Pass' if ok else 'FAIL', detail))


def section(start, end):
    return s[s.index(start):s.index(end)]


def row(prefix):
    hits = [l for l in s.split(NL) if l.startswith(prefix)]
    return hits[0] if len(hits) == 1 else ''


S154 = section('### 15.4', '### 15.5')
S153 = section('### 15.3 Budgets', '### 15.4')

# ---------------------------------------------------------------------------------------------------- identifiers
G = 'IDs'
defs = {
    'D-52': r'^\| \*\*D-52\*\* \|',
    'D-53': r'^\| \*\*D-53\*\* \|',
    'IMP-11': r'^\| IMP-11 \| \*\*Library space',
    'Q-24': r'^\| Q-24 \|',
    'TEST-L9': r'^\| TEST-L9 \|',
}
for k, rx in defs.items():
    n_def = len(re.findall(rx, s, re.M))
    n_ref = len(re.findall(re.escape(k) + r'(?![0-9])', s))
    check(G, f'{k} defined once and referenced', n_def == 1 and n_ref > 1, f'{n_def} definition, {n_ref} occurrences')
test_defs = set(re.findall(r'^\| (TEST-[A-Z]+[0-9]+) \|', s, re.M))
test_refs = set(re.findall(r'TEST-[A-Z]+[0-9]+', s))
check(G, 'every TEST id referenced is defined in §18.1', test_refs <= test_defs, f'{len(test_defs)} defined; undefined: {sorted(test_refs - test_defs)}')
imp_defs = set(re.findall(r'^\| (IMP-[0-9]+) \| \*\*', s, re.M)) | set(re.findall(r'^\| (IMP-[0-9]+) \| (?!TEST)', s, re.M))
imp_refs = set(re.findall(r'IMP-[0-9]+', s))
check(G, 'every IMP id referenced is defined', imp_refs <= imp_defs, f'{len(imp_defs)} defined; undefined {sorted(imp_refs - imp_defs)}')
q_defs = set(re.findall(r'^\| (Q-[0-9]+) \|', s, re.M))
q_refs = set(re.findall(r'Q-[0-9]+', s))
check(G, 'every Q id referenced is defined in §21', q_refs <= q_defs, f'{len(q_defs)} defined (Q-01..Q-{max(int(q[2:]) for q in q_defs):02d}); undefined {sorted(q_refs - q_defs)}')
d_defs = set(re.findall(r'^\| (?:\*\*)?(D-[0-9]+)(?:\*\*)? \|', s, re.M))
d_refs = set(re.findall(r'D-[0-9]+', s))
check(G, 'every decision id referenced is defined in §20', d_refs <= d_defs, f'{len(d_defs)} defined (D-01..D-{max(int(d[2:]) for d in d_defs):02d}); undefined {sorted(d_refs - d_defs)}')
perf_defs = set(re.findall(r'^\| (PERF-[0-9]+[ab]?) \|', s, re.M))
perf_refs = set(re.findall(r'PERF-[0-9]+[ab]?', s))
fam = {'PERF-09', 'PERF-02', 'PERF-11'}
check(G, 'every PERF id referenced is defined in §15.3 (PERF-02, PERF-11 name the a/b pairs; PERF-09 retired)', perf_refs <= perf_defs | fam, f'undefined {sorted(perf_refs - perf_defs - fam)}')
c4dr = set(re.findall(r'C4DR-[HMO][0-9]+', s))
check(G, 'the seven re-review findings are dispositioned in §22.5', all(f'**{k}**' in section('### 22.5', '## 23.') for k in ['C4DR-H01', 'C4DR-M01', 'C4DR-M02', 'C4DR-M03', 'C4DR-M04', 'C4DR-M05', 'C4DR-M06']), f'{len(c4dr)} C4DR ids referenced')

# ---------------------------------------------------------------------------------------------------- stale text
G = 'stale'
stale = [
    ('"≤ 5% of the snapshot" only where it is replaced', r'5% of the snapshot', lambda m: all('eplace' in s[max(0, x.start()-300):x.end()+50] or 'C4-B01' in s[max(0, x.start()-300):x.end()+300] for x in m)),
    ('"almost no journal" only in [E-3] readings (§15.1)', r'almost no journal', lambda m: all(s.rfind('### 15.1', 0, x.start()) > s.rfind('### 15.2', 0, x.start()) for x in m)),
    ('"journals almost nothing" gone', r'journals almost nothing', lambda m: not m),
    ('"interned library-wide" gone', r'interned library-wide', lambda m: not m),
    ('"IMP-01 to IMP-10" only in the §22 history', r'IMP-01 to IMP-10', lambda m: all(x.start() > s.index('## 22. Review repair dispositions') for x in m)),
    ('"name.utf16 UNIQUE" gone', r'`name.utf16 UNIQUE`|`name.utf16` is `UNIQUE`', lambda m: not m),
    ('"proposed;" budget heading gone', r'Budgets \(proposed', lambda m: not m),
    ('hidden staging named only as rejected', r'hidden staging', lambda m: all('eject' in s[max(s.rfind(NL + NL, 0, x.start()), s.rfind(NL + '|', 0, x.start())):x.end()+300] for x in m)),
    ('Library-wide dictionary named only as history, rejection or the negative control', r'Library-wide (?:name )?dictionar', lambda m: all(any(w in s[max(0, x.start()-400):x.end()+400] for w in ('C4-B01', 'C4\'s mechanics', 'negative control', 'blocked schema', 'eject', 'C4 design review', 'C4-H02')) for x in m)),
    ('the cycled real vocabulary (N4) is not a family any more', r'N4 real vocabulary|\*\*N4\b|N5 re-scan', lambda m: not m),
    ('"one name in ten" only as the corrected artefact', r'one name in ten', lambda m: all('artefact' in s[max(0, x.start()-300):x.end()+300] or 'C4DR-H01' in s[max(0, x.start()-300):x.end()+300] for x in m)),
    ('no universal 2 s Stop promise ("returns within 2 s (PERF-16)")', r'returns within 2 s \(PERF-16\)', lambda m: not m),
    ('"acceptance cells\' name families" gone (PERF-16 narrowing)', r"acceptance cells' name families|name families of the acceptance cells", lambda m: not m),
    ('PERF-14 engine part no longer "the same 60 s"', r'held to the same 60 s', lambda m: not m),
    ('API-dependent "account can read" load rule gone', r"CPU time the benchmark's account can read|readable other processes", lambda m: not m),
    ('vacuous hosted-CI sentence gone', r'hosted cell beyond a stop threshold must be measured', lambda m: not m),
    ('validity typo "(1) exceeded" gone', r'only if \(1\) exceeded', lambda m: not m),
    ('P6 "dictionary and observation rows in key order" corrected', r'stream the spool into dictionary and observation rows in key order', lambda m: not m),
    ('old guarantee "does not take the Library\'s volume below" gone', r'it does not take the Library\'s volume below', lambda m: not m),
    ('"4,096 rows add at most about 21 MiB" gone', r'4,096 rows add at most about 21 MiB', lambda m: not m),
    ('"Quiet machine" with GetSystemTimes over 30 s gone', r'whole CPU use over 30 s', lambda m: not m),
]
full = s
# the audit section (§23) quotes the superseded phrases it searched for; it is not searched itself
s = full[:full.index('## 23. Consistency audit')] + full[full.index('## 24. Pre-production exit'):]
for name, rx, ok in stale:
    m = list(re.finditer(rx, s))
    check(G, name, ok(m), f'{len(m)} occurrence(s)')
s = full
s191 = section('### 19.1 Readiness summary', '### C1:')
check(G, '§19.1 no longer lists Q-17 as OPEN for C4', 'Q-12, Q-17, Q-21' not in s191 and 'Q-17 is resolved' in s191)
for prefix in ['| IMP-09 | **Performance', '| **D-52** |', '| Q-17 | The one-transaction', '| **C4-B01** PERF-15', '| PERF-15 | **The transient']:
    r = row(prefix)
    label = prefix.strip('| ').split(' |')[0].replace('*', '').split(' ')[0]
    check(G, f'binary sizes labelled MiB in the {label} row', r != '' and not re.search(r'\d MB\b', r), f'{len(re.findall(r"[0-9] MB", r))} MB, {len(re.findall(r"[0-9] MiB", r))} MiB')

# ---------------------------------------------------------------------------------------------------- C4DR-H01
G = 'H01'
for k in ['F(n, d, m)', 'R(n, d, m; ρ, δ, α)', '**system**', '**data**', 'β = 13.5', 'β = 2.56', 'never from its result', '**Representative** (acceptance)', '**Stress**', '**Worst-case**', '**Informational**', 'hardest of them', 'Why these boundaries', 'F(1M, 25%, system)', 'F(2M, 60%, data)', 'ρ = 10%', 'F(2M, 100%, data)', '**N1**', '**N2**', '**N3**', '**L1**', '**L2**']:
    check(G, f'§15.4 states {k}', k in S154)
check(G, 'TEST-P2 measures real re-scan churn', 'two captures' in row('| TEST-P2 |'))
check(G, 'D-53 records the high-novelty product decision', 'stress' in row('| **D-53** |') and 'not by PERF-01' in row('| **D-53** |'))
check(G, 'PERF-01 judged on representative cells, NOT MEASURED', 'representative' in row('| PERF-01 |') and 'NOT MEASURED' in row('| PERF-01 |'))

# ---------------------------------------------------------------------------------------------------- C4DR-M01
G = 'M01'
imp11 = row('| IMP-11 | **Library space')
for k in ['`Λ` = max(0, `P` × `S` − `L`)', '`R_f` = `M_L` + `Λ_f` + `R_C`', '`R_k` = `M_L` + `Λ_k` + `I_L`', 'equality continues', 'cannot be read', 'residual race', 'an allowance, not a bound', 'The model, stated exactly', '2 × `B_n`', '`R_C` = 1 MiB']:
    check(G, f'IMP-11 states {k}', k in imp11)
l9 = row('| TEST-L9 |')
for k in ['exactly at the reserve; one byte below', 'query failure', 'external consumption between checks', 'pending COMMIT write-out', 'rollback after a guard stop', 'genuine SQLITE_FULL despite the guard']:
    check(G, f'TEST-L9 covers "{k}"', k in l9)
check(G, 'Q-24 measures COMMIT growth against Λ_f + R_C', '`Λ_f` + `R_C`' in row('| Q-24 |'))
check(G, '§19 C4 requires the pending-growth reading', 'pending-growth reading' in section('### C4:', '### C5:'))
check(G, '§19 C5 states the model and the residual race', 'residual race' in section('### C5:', '### C6:'))

# ---------------------------------------------------------------------------------------------------- C4DR-M02
G = 'M02'
p15 = row('| PERF-15 | **The transient')
for k in ['Key-range confinement', 'W = 2 positions', '2 × (W + 1) = 6', 'Status of W', 'balance_nonroot', 'rightmost page of the level']:
    check(G, f'PERF-15 (a) states {k}', k in p15)
for k in ['first key column', 'IN', 'ADJACENT', 'REMOTE', 'SHARED', 'Negative control', 'must FAIL', 'copied\n    after T0'.replace('\n    ', ' ')]:
    check(G, f'§15.4 attribution states {k}', k in S154.replace(NL + '    ', ' '))
check(G, 'TEST-P1 requires the attribution and the negative control', 'negative control' in row('| TEST-P1 |'))

# ---------------------------------------------------------------------------------------------------- C4DR-M03
G = 'M03'
can = section('### 11.5', '### 11.6')
for k in ['0.5 s apart', 'inside the in-transaction verification', 'every 4,096 inserted rows']:
    check(G, f'CAN-01d states {k}', k in can)
for k in ['**detection**', '**rollback**', '**release**', '**2 s**', '**1.75 s**', '**5 s**', '**4.75 s**', 'representative saves only']:
    check(G, f'CAN-03 states {k}', k in can)
check(G, 'OBS-11 refers to PERF-16 by class', 'for the save\'s workload class' in s)
check(G, 'IMP-05: cancellation in verification is SaveCancelled', 'never `InvariantViolation`' in row('| IMP-05 |'))
check(G, 'UI-10 has "Stopping…"', '"Stopping…"' in section('### 14.3', '### 14.4'))
p16 = row('| PERF-16 |')
check(G, 'PERF-16: 2 s representative, 5 s stress and worst-case, worst cancel point', '**≤ 2 s**' in p16 and '**≤ 5 s**' in p16 and 'worst cancel point' in p16)
check(G, 'PERF-15 (c): four cancel points, 1.75 s and 4.75 s', 'four cancel points' in p15 and '**1.75 s**' in p15 and '**4.75 s**' in p15)

# ---------------------------------------------------------------------------------------------------- C4DR-M04
G = 'M04'
p14 = row('| PERF-14 |')
for k in ['Three numbers, never one', 'the C5 end-to-end target', 'the C4 engine target', '**≤ 45 s**', '**above 67.5 s**', '**above 90 s**', '15 s reserved']:
    check(G, f'PERF-14 states {k}', k in p14)
check(G, 'CAP-04 aligned with the engine target', '45 s' in row('| CAP-04 |'))
check(G, 'TEST-P2 measures PERF-14 end to end in representative and stress cells', 'PERF-14 end to end' in row('| TEST-P2 |'))

# ---------------------------------------------------------------------------------------------------- C4DR-M05
G = 'M05'
for k in ['Load source (the only one)', 'PdhAddEnglishCounterW', '\\Process(*)\\% Processor Time', '\\Process(*)\\ID Process', '**500 ms**', '`_Total` and `Idle`', 'renumbers', '`MsMpEng`', 'process ID 4', 'divided by the number of logical processors', '**mean ≤ 5%**', '**95th percentile ≤ 15%**', 'after every round', 'declared before it starts', 'gate session', 'exactly five measured runs per cell', 'No valid run is ever discarded', 'median of its first five valid runs', 'documented objective invalidation']:
    check(G, f'§15.4 method states {k}', k in S154)

# ---------------------------------------------------------------------------------------------------- C4DR-M06 and structure
G = 'M06'
check(G, 'the erratum is referenced', 'ERRATUM.md' in s)
check(G, '§15.4 cache key names every input', 'names **every** input' in S154)
G = 'structure'
t = S153
rows_ = [l for l in t.split(NL) if l.startswith('| PERF-')]
check(G, '§15.3 table: every budget row has five columns', all(l.count(' | ') == 4 for l in rows_) and len(rows_) == 17, f'{len(rows_)} rows')
check(G, '§9.2 name table has source_id and the two-column UNIQUE', 'UNIQUE (source_id, utf16)' in s)
check(G, '§9.5 invariant 4 names the source', 'with `source_id` = X' in s)
check(G, '§18.2 lists IMP-11', '| IMP-11 | TEST-L9, TEST-T1, TEST-P1 |' in s)
check(G, '§23 scopes its table and points to §23.1', '### 23.1 Audit of the C4 design repair' in s and 'Scope of the table below' in s)
check(G, 'status block names the repair and the focused re-review', 'C4 design repair' in s[:3000] and 'focused re-review' in s[:3000])

if __name__ == '__main__':
    print('| Group | Check | Result | Detail |')
    print('|---|---|---|---|')
    for r in results:
        print(f'| {r[0]} | {r[1]} | {r[2]} | {r[3]} |')
    print()
    print(f'checks: {len(results)}; FAILS: {sum(1 for r in results if r[2] != "Pass")}')
