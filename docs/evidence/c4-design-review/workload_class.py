"""C4 design final repair (C4DRR-M01): the workload classification of §15.4 as a total function, and its mechanical self-test.

Usage:  python workload_class.py docs/v1.1-preproduction-spec.md [--json <out.json>]

Reads, from §15.4 of the specification, the band table ("| Parameter of the cell | Representative band | Stress band | Worst-case
band |") and the TEST-P1 matrix table ("| Cell | Kind | Files | Source | ρ | δ | α | Library before | Class |"), then checks:
  1. bands      every row's bands are disjoint intervals that together cover the parameter's whole range, in the order
                representative < stress < worst-case (no gap, no overlap: a value lies in exactly one band)
  2. cells      every case below (boundaries at 25%, 60%, 1%, 10%, 0.5%, 5% and just above each; low-churn re-scans of 25%, 60% and
                100%-distinct sources; mixed cases; the named families) matches exactly one band per parameter and exactly one
                class predicate (zero or several is a failure), and the class equals the intended one
  3. matrix     every row of the matrix table has exactly one class, equal to its Class column; both representative first saves
                and both representative re-scans are Representative
  4. old rule   the design repair's nested-bound table with "the hardest of them" (C4DRR-M01) must FAIL check 2: the
                representative cells match two rows and a routine re-scan of a 60% data-model source matches none
Exit code 0 when every check passes, 1 otherwise. Arithmetic is exact (fractions), so "25% + ε" is strictly above 25%.

The function (§15.4): (1) Informational for a named informational family (N1 append; any cell of more than 2M files);
(2) else Worst-case for a named adversarial family (N2, N3 into an existing source; a re-scan of an N2 source); (3) else, for a
generator cell, the hardest of its parameters' bands: a first save F(n, d, m) has its source parameter d (row of model m); a re-scan
R(n, d, m; ρ, δ, α) has d and its three churn parameters.
"""
import json
import re
import sys
from fractions import Fraction as Fr

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

ORDER = ['Representative', 'Stress', 'Worst-case']
DOMAIN = {'d': (Fr(0), False, Fr(100), True), 'ρ': (Fr(0), True, Fr(100), True), 'δ': (Fr(0), True, Fr(100), True),
          'α': (Fr(0), True, Fr(100), True)}          # (low, low closed, high, high closed), in percent
EPS = Fr(1, 10 ** 6)                                   # one millionth of a percentage point
TWO_M = 2_000_000
INFORMATIONAL_FAMILIES = {'N1'}
ADVERSARIAL_FAMILIES = {'N2', 'N3', 'N2 re-scan'}

BAND = re.compile(r'^\s*([0-9.]+)%?\s*(<|≤)\s*(d|ρ|δ|α)\s*(<|≤)\s*([0-9.]+)%\s*$')


def parse_interval(text):
    t = text.replace('*', '').strip()
    if t.lower() in ('none', '—', '-'):
        return None
    m = BAND.match(t)
    if not m:
        raise ValueError(f'band "{text}" is not of the form "a < x ≤ b%"')
    return (Fr(m.group(1)), m.group(2) == '≤', Fr(m.group(5)), m.group(4) == '≤', m.group(3))


def contains(iv, v):
    lo, lo_c, hi, hi_c, _ = iv
    return (v > lo or (lo_c and v == lo)) and (v < hi or (hi_c and v == hi))


def table(text, header_start):
    lines = text.split('\n')
    for i, l in enumerate(lines):
        if l.strip().startswith(header_start):
            rows = []
            for r in lines[i + 2:]:
                r = r.strip()
                if not r.startswith('|'):
                    break
                rows.append([c.strip() for c in r.strip('|').split('|')])
            return rows
    raise ValueError(f'table "{header_start}" not found')


def parse_bands(text):
    """{(parameter, model or None): [interval or None per class]} from the band table."""
    bands = {}
    for row in table(text, '| Parameter of the cell |'):
        first = row[0]
        sym = re.search(r'`(d|ρ|δ|α)`', first)
        if not sym:
            raise ValueError(f'band row without a parameter: {first}')
        model = 'system' if '**system**' in first else 'data' if '**data**' in first else None
        bands[(sym.group(1), model)] = [parse_interval(c) for c in row[1:4]]
    return bands


def check_bands(bands):
    problems = []
    for (sym, model), ivs in bands.items():
        present = [(k, iv) for k, iv in enumerate(ivs) if iv is not None]
        if any(iv[4] != sym for _, iv in present):
            problems.append(f'{sym}/{model}: a band names another parameter')
            continue
        lo, lo_c, hi, hi_c = DOMAIN[sym]
        order = [k for k, _ in present]
        if order != sorted(order):
            problems.append(f'{sym}/{model}: bands not in the order representative < stress < worst-case')
        seq = sorted((iv for _, iv in present), key=lambda iv: (iv[0], not iv[1]))
        if not seq or seq[0][0] != lo or seq[0][1] != lo_c:
            problems.append(f'{sym}/{model}: the bands do not start at the range\'s lower end')
        if not seq or seq[-1][2] != hi or seq[-1][3] != hi_c:
            problems.append(f'{sym}/{model}: the bands do not end at the range\'s upper end')
        for a, b in zip(seq, seq[1:]):
            if a[2] != b[0] or a[3] == b[1]:
                problems.append(f'{sym}/{model}: gap or overlap at {a[2]}%')
    return problems


def bands_of(cell, bands):
    """[(parameter, value, [classes whose band contains it])] for a generator cell."""
    out = [('d', cell['d'], [ORDER[k] for k, iv in enumerate(bands[('d', cell['model'])]) if iv and contains(iv, cell['d'])])]
    if cell['kind'] == 're-scan':
        for sym in ('ρ', 'δ', 'α'):
            out.append((sym, cell[sym], [ORDER[k] for k, iv in enumerate(bands[(sym, None)]) if iv and contains(iv, cell[sym])]))
    return out


def valid(cell):
    if cell.get('family') in INFORMATIONAL_FAMILIES | ADVERSARIAL_FAMILIES:
        return True
    if not (0 < cell['d'] <= 100):
        return False
    if cell['kind'] == 're-scan':
        if any(not (0 <= cell[s] <= 100) for s in ('ρ', 'δ', 'α')) or cell['ρ'] + cell['δ'] > 100:
            return False
    return True


def predicates(cell, bands):
    """Each class's predicate, evaluated independently: exactly one must hold."""
    fam = cell.get('family')
    informational = fam in INFORMATIONAL_FAMILIES or cell['n'] > TWO_M
    if informational or fam in ADVERSARIAL_FAMILIES:
        return {'Informational': informational, 'Worst-case': not informational, 'Stress': False, 'Representative': False}, []
    per = bands_of(cell, bands)
    one_each = all(len(c) == 1 for _, _, c in per)
    got = [c[0] for _, _, c in per if len(c) == 1]
    return {'Informational': False,
            'Worst-case': one_each and 'Worst-case' in got,
            'Stress': one_each and 'Worst-case' not in got and 'Stress' in got,
            'Representative': one_each and all(g == 'Representative' for g in got)}, per


def classify(cell, bands):
    p, per = predicates(cell, bands)
    hits = [k for k, v in p.items() if v]
    return hits, per


def F(n, d, model):
    return {'kind': 'first save', 'n': n, 'd': Fr(d), 'model': model}


def R(n, d, model, rho, delta, alpha):
    return {'kind': 're-scan', 'n': n, 'd': Fr(d), 'model': model, 'ρ': Fr(rho), 'δ': Fr(delta), 'α': Fr(alpha)}


def fam(name, n):
    return {'kind': 'family', 'family': name, 'n': n, 'd': Fr(0), 'model': None}


M = 1_000_000
CASES = [  # (description, cell, intended class)
    ('F d = 25% (system)', F(2 * M, 25, 'system'), 'Representative'),
    ('F d = 25% + ε (system)', F(2 * M, 25 + EPS, 'system'), 'Stress'),
    ('F d = 60% (system)', F(2 * M, 60, 'system'), 'Stress'),
    ('F d = 60% + ε (system)', F(2 * M, 60 + EPS, 'system'), 'Worst-case'),
    ('F d = 25% (data)', F(2 * M, 25, 'data'), 'Stress'),
    ('F d = 60% (data)', F(2 * M, 60, 'data'), 'Stress'),
    ('F d = 60% + ε (data)', F(2 * M, 60 + EPS, 'data'), 'Worst-case'),
    ('F d = 100% (data)', F(2 * M, 100, 'data'), 'Worst-case'),
    ('F d = ε (system)', F(2 * M, EPS, 'system'), 'Representative'),
    ('R ρ = 1% (δ = α = 0.5%)', R(2 * M, 25, 'system', 1, Fr(1, 2), Fr(1, 2)), 'Representative'),
    ('R ρ = 1% + ε', R(2 * M, 25, 'system', 1 + EPS, Fr(1, 2), Fr(1, 2)), 'Stress'),
    ('R ρ = 10%', R(2 * M, 25, 'system', 10, Fr(1, 2), Fr(1, 2)), 'Stress'),
    ('R ρ = 10% + ε', R(2 * M, 25, 'system', 10 + EPS, Fr(1, 2), Fr(1, 2)), 'Worst-case'),
    ('R δ = 0.5% + ε', R(2 * M, 25, 'system', 1, Fr(1, 2) + EPS, Fr(1, 2)), 'Stress'),
    ('R δ = 5%', R(2 * M, 25, 'system', 1, 5, Fr(1, 2)), 'Stress'),
    ('R δ = 5% + ε', R(2 * M, 25, 'system', 1, 5 + EPS, Fr(1, 2)), 'Worst-case'),
    ('R α = 0.5% + ε', R(2 * M, 25, 'system', 1, Fr(1, 2), Fr(1, 2) + EPS), 'Stress'),
    ('R α = 5%', R(2 * M, 25, 'system', 1, Fr(1, 2), 5), 'Stress'),
    ('R α = 5% + ε', R(2 * M, 25, 'system', 1, Fr(1, 2), 5 + EPS), 'Worst-case'),
    ('R no churn at all, representative source', R(2 * M, 25, 'system', 0, 0, 0), 'Representative'),
    ('R 1% re-scan of a 25%-distinct system-model source', R(2 * M, 25, 'system', 1, Fr(1, 2), Fr(1, 2)), 'Representative'),
    ('R 1% re-scan of a 25%-distinct data-model source', R(2 * M, 25, 'data', 1, Fr(1, 2), Fr(1, 2)), 'Stress'),
    ('R 1% re-scan of a 60%-distinct data-model source', R(2 * M, 60, 'data', 1, Fr(1, 2), Fr(1, 2)), 'Stress'),
    ('R 1% re-scan of a 60%-distinct system-model source', R(2 * M, 60, 'system', 1, Fr(1, 2), Fr(1, 2)), 'Stress'),
    ('R 1% re-scan of a 100%-distinct source', R(2 * M, 100, 'data', 1, Fr(1, 2), Fr(1, 2)), 'Worst-case'),
    ('R ρ = 1%, δ = 8% (mixed)', R(2 * M, 25, 'system', 1, 8, Fr(1, 2)), 'Worst-case'),
    ('R 60% data source at the stress churn bounds (mixed)', R(2 * M, 60, 'data', 10, 5, 5), 'Stress'),
    ('R ρ = 5%, δ = α = 0.5% (mixed)', R(2 * M, 25, 'system', 5, Fr(1, 2), Fr(1, 2)), 'Stress'),
    ('R 100% source with no churn (mixed)', R(2 * M, 100, 'system', 0, 0, 0), 'Worst-case'),
    ('F at 10M files (scale cell)', F(10 * M, 25, 'system'), 'Informational'),
    ('R at 1M files', R(1 * M, 25, 'system', 1, Fr(1, 2), Fr(1, 2)), 'Representative'),
    ('F at 100k files (smoke size: classified, gates nothing)', F(100_000, 25, 'system'), 'Representative'),
    ('N1 append at 1M', fam('N1', 1 * M), 'Informational'),
    ('N2 into an existing source at 2M', fam('N2', 2 * M), 'Worst-case'),
    ('N2 at 10M', fam('N2', 10 * M), 'Informational'),
    ('N3 into an existing source at 2M', fam('N3', 2 * M), 'Worst-case'),
    ('1% re-scan of an N2 source at 2M', fam('N2 re-scan', 2 * M), 'Worst-case'),
]
INVALID_CELLS = [('F d = 0', F(2 * M, 0, 'system')), ('F d > 100%', F(2 * M, 101, 'data')),
                 ('R ρ + δ > 100%', R(2 * M, 25, 'system', 60, 50, 0))]

OLD_RULE = """| Parameter of the cell | Representative band | Stress band | Worst-case band |
|---|---|---|---|
| `d`, name model **system** | 0 < d ≤ 25% | 0 < d ≤ 60% | 60% < d ≤ 100% |
| `d`, name model **data** | none | 0 < d ≤ 60% | 60% < d ≤ 100% |
| `ρ` | 0 ≤ ρ ≤ 1% | 0 ≤ ρ ≤ 10% | 10% < ρ ≤ 100% |
| `δ` | 0 ≤ δ ≤ 0.5% | 0 ≤ δ ≤ 5% | none |
| `α` | 0 ≤ α ≤ 0.5% | 0 ≤ α ≤ 5% | none |
"""
# The design repair's rule, restated as bands: rows of nested upper bounds ("stress: d ≤ 60%; as representative, but ρ ≤ 10%, δ ≤ 5%,
# α ≤ 5%"), nothing above 5% for δ and α, and a stress re-scan only of "the dictionary of a representative first save" (that last
# condition is not expressible as a band; the check below also flags the re-scan of a 60% data-model source as matching no row).


def size(text):
    t = text.strip().upper()
    return int(Fr(t[:-1]) * (M if t.endswith('M') else 1000)) if t[-1] in 'MK' else int(t)


def pct(text):
    t = text.strip()
    return None if t in ('–', '-', '—', '') else Fr(t.rstrip('%'))


def matrix_cells(text):
    cells = []
    for row in table(text, '| Cell | Kind |'):
        name, kind, files, src, rho, delta, alpha, _lib, cls = row[:9]
        kind_l = kind.lower()
        for n_text in files.split(';'):
            n = size(n_text)
            if kind_l.startswith('family'):
                c = fam(kind.split(' ', 1)[1].strip(), n)
            else:
                model, d = [x.strip() for x in src.split(',')]
                c = {'kind': 'first save' if kind_l == 'first save' else 're-scan', 'n': n, 'd': pct(d), 'model': model}
                if c['kind'] == 're-scan':
                    c.update({'ρ': pct(rho), 'δ': pct(delta), 'α': pct(alpha)})
            cells.append((f'{name} [{n_text.strip()}]', c, cls.replace('*', '').strip()))
    return cells


def run_checks(bands, label):
    results = []
    for p in check_bands(bands):
        results.append({'check': f'{label}: bands', 'case': p, 'ok': False})
    if not any(r['check'] == f'{label}: bands' for r in results):
        results.append({'check': f'{label}: bands', 'case': 'every row partitions its parameter\'s range, in class order', 'ok': True})
    for desc, cell, intended in CASES:
        try:
            hits, per = classify(cell, bands)
        except KeyError as e:
            hits, per = [], []
        multi = [f'{s} {float(v):g}% in {c}' for s, v, c in per if len(c) != 1]
        results.append({'check': f'{label}: cases', 'case': desc, 'classes': hits, 'intended': intended,
                        'parameters_not_in_exactly_one_band': multi, 'ok': hits == [intended] and not multi})
    for desc, cell in INVALID_CELLS:
        results.append({'check': f'{label}: domain', 'case': desc, 'ok': not valid(cell), 'classes': 'rejected as not a valid cell' if not valid(cell) else 'accepted'})
    return results


def main(spec, json_out):
    text = open(spec, encoding='utf-8').read()
    s154 = text[text.index('### 15.4'):text.index('### 15.5')]
    bands = parse_bands(s154)
    results = run_checks(bands, 'spec')
    reps = 0
    for desc, cell, stated in matrix_cells(s154):
        hits, per = classify(cell, bands)
        results.append({'check': 'spec: matrix', 'case': desc, 'classes': hits, 'intended': stated, 'ok': hits == [stated]})
        reps += hits == ['Representative']
    results.append({'check': 'spec: matrix', 'case': 'representative cells (two first saves and two re-scans, 1M and 2M)', 'classes': reps,
                    'intended': 4, 'ok': reps == 4})
    old = run_checks(parse_bands(OLD_RULE), 'old rule')
    old_fails = [r for r in old if not r['ok']]
    named = ['F d = 25% (system)', 'R ρ = 1% (δ = α = 0.5%)', 'R 1% re-scan of a 60%-distinct data-model source', 'R ρ = 1%, δ = 8% (mixed)']
    shown = [r for r in old_fails if r['case'] in named]
    results.append({'check': 'old rule (negative self-test)', 'case': 'the design repair\'s nested-bound rule fails these checks',
                    'classes': f'{len(old_fails)} failing checks, among them ' + '; '.join(
                        f"{r['case']}: {', '.join(r['classes']) or 'no class'} ({'; '.join(r['parameters_not_in_exactly_one_band'])})" for r in shown),
                    'ok': len(old_fails) > 0 and len(shown) == len(named)})
    bad = [r for r in results if not r['ok']]
    print('| Check | Case | Classes | Intended | Result |')
    print('|---|---|---|---|---|')
    for r in results:
        cls = r.get('classes')
        cls = ', '.join(cls) if isinstance(cls, list) else ('' if cls is None else str(cls))
        if r.get('parameters_not_in_exactly_one_band'):
            cls += ' (' + '; '.join(r['parameters_not_in_exactly_one_band']) + ')'
        print(f"| {r['check']} | {r['case']} | {cls or '(none)'} | {r.get('intended', '')} | {'Pass' if r['ok'] else 'FAIL'} |")
    print()
    print(f'checks: {len(results)}; FAILS: {len(bad)}')
    if json_out:
        with open(json_out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(json.dumps({'checks': len(results), 'fails': len(bad), 'results': results}, indent=1, ensure_ascii=False, default=str) + '\n')
    return 0 if not bad else 1


if __name__ == '__main__':
    a = sys.argv[1:]
    sys.exit(main(a[0], a[a.index('--json') + 1] if '--json' in a else None))
