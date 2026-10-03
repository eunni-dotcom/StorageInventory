"""Self-tests of the session rules (gate_model.py and perf_session.rebuild), runnable with plain `python`. No benchmark runs: the runs are synthetic.

Usage:  python session_selftest.py

What is checked (§15.4 and §15.3):
  * replacement and statistic: the median of the FIRST FIVE VALID runs in run order with min and max; replacements only for invalidated runs, at
    most five more attempts; NOT MEASURED after ten attempts; invalid runs stay in the table with their reasons; no valid run is ever discarded or
    replaced for its result (a slow valid run stays in the sample; a failed save is a valid run whose cell is MISSED)
  * every budget at its exact boundary: PERF-01 (100,000 met, below it MISSED, below 50,000 STOP), PERF-14 (45 s, 67.5 s, 90 s), PERF-15 (c)
    (1.75 s and 4.75 s, STOP above 1.5 times), the recovery open (5 s, 7.5 s), PERF-15 (a) (PASS, FAIL, NOT MEASURED)
  * outcomes: STOP over MISSED over NOT MEASURED over MET; a MISSED budget is never reported as met because no stop threshold was crossed;
    PERF-01, PERF-14, PERF-15 (a), PERF-15 (c) and PERF-16's engine part are reported independently; a session that is not judged reports NOT JUDGED
  * a round invalidated after the fact (the quiet check after it failed) is applied when the session is rebuilt from its raw files
"""
import json
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gate_model as m  # noqa: E402
import perf_session  # noqa: E402

FAILS = []


def check(name, ok, detail=''):
    print(f"{'ok ' if ok else 'BAD'} {name}" + (f' -- {detail}' if detail and not ok else ''))
    if not ok:
        FAILS.append(name)


class Runs:
    """A chronological list of synthetic runs."""

    def __init__(self):
        self.runs = []

    def add(self, cell, kind, status='ok', **metrics):
        self.runs.append({'runId': f'{cell}-{kind}-{len(self.runs)}', 'cell': cell, 'kind': kind, 'attempt': len([r for r in self.runs if r['cell'] == cell and r['kind'] == kind]) + 1,
                          'round': 'r', 'order': len(self.runs) + 1, 'status': status, 'reasons': [] if status == 'ok' else ['x'], 'metrics': metrics, 'load': None})
        return self

    def timed(self, cell, rows=None, seconds=None, status='ok'):
        met = {}
        if rows is not None:
            met['filesPerSecond'] = rows
        if seconds is not None:
            met['importSeconds'] = seconds
        return self.add(cell, 'timed', status, **met)


def cell(cid, cls='Representative', perf01=False, target=False, stop=0.0, a=True, cancel=0.0, recovery=False):
    return {'id': cid, 'class': cls, 'budgets': {'perf01': perf01, 'perf14Target': target, 'perf14StopSeconds': stop, 'perf15a': a, 'perf15cSeconds': cancel, 'recovery': recovery}}


def five(cid, rows=None, seconds=None):
    r = Runs()
    for _ in range(5):
        r.timed(cid, rows, seconds)
    return r


def outcome_of(cells, r, budget, judged=True):
    return m.evaluate(cells, r.runs, judged)['budgets'][budget]['outcome']


def test_statistic_and_replacement():
    r = Runs()
    for v in (90_000, 120_000, 100_000):
        r.timed('c', rows=v)
    r.timed('c', rows=10, status='invalid')        # an invalid run: never part of the sample
    r.timed('c', rows=80_000)
    r.timed('c', rows=110_000)
    sample = m.first_valid(r.runs, 'c', 'timed')
    fig = m.figure(sample, 'filesPerSecond')
    check('the cell figure is the median of the first five VALID runs in run order', fig['median'] == 100_000 and fig['min'] == 80_000 and fig['max'] == 120_000, str(fig))
    check('the invalid run is not in the sample and is still in the table', all(x['metrics']['filesPerSecond'] != 10 for x in sample) and len(r.runs) == 6)
    r.timed('c', rows=1)                              # a sixth valid run is not part of the sample
    check('a sixth valid run does not change the sample (only the first five count)', m.figure(m.first_valid(r.runs, 'c', 'timed'), 'filesPerSecond')['median'] == 100_000)

    r = Runs()
    for v in (1, 2, 3, 4):
        r.timed('c', rows=v)
    check('four valid runs: not yet five, no attempts exhausted: needs a run', m.needs_run(r.runs, 'c', 'timed'))
    r.timed('c', rows=5, status='invalid')
    check('an invalidated run earns a replacement (five attempts, four valid)', m.needs_run(r.runs, 'c', 'timed'))
    for _ in range(4):
        r.timed('c', rows=7, status='invalid')
    check('after nine attempts it still may try once more', m.needs_run(r.runs, 'c', 'timed'))
    r.timed('c', rows=7, status='invalid')
    check('after ten attempts without five valid runs: no more attempts', not m.needs_run(r.runs, 'c', 'timed'))
    check('...and the cell is NOT MEASURED (its sample does not exist)', m.first_valid(r.runs, 'c', 'timed') is None)
    check('...which counts as a budget outcome of NOT MEASURED, blocking acceptance', outcome_of([cell('c', perf01=True)], r, 'PERF-01') == 'NOT MEASURED')

    r = five('c', rows=100_000)
    check('five valid runs: no replacement needed', not m.needs_run(r.runs, 'c', 'timed'))
    # a valid but slow run is kept (no valid run is discarded for its result)
    r = Runs()
    for v in (150_000, 150_000, 150_000, 150_000, 1_000):
        r.timed('c', rows=v)
    check('a valid run with a bad result stays in the sample and is not replaced', not m.needs_run(r.runs, 'c', 'timed') and m.figure(m.first_valid(r.runs, 'c', 'timed'), 'filesPerSecond')['min'] == 1_000)
    # a failed save is a valid run: never replaced, and its cell is MISSED
    r = Runs()
    for _ in range(4):
        r.timed('c', rows=150_000)
    r.timed('c', status='failed')
    r.runs[-1]['metrics']['problem'] = 'the save did not publish'
    check('a failed save is a valid run: no replacement', not m.needs_run(r.runs, 'c', 'timed'))
    check('...and its cell is MISSED on PERF-01', outcome_of([cell('c', perf01=True)], r, 'PERF-01') == 'MISSED')
    # single slots
    r = Runs().add('c', 'attribution', 'invalid').add('c', 'attribution', 'invalid')
    check('an INVALID attribution earns a replacement (one valid run is needed)', m.needs_run(r.runs, 'c', 'attribution'))
    for _ in range(4):
        r.add('c', 'attribution', 'invalid')
    check('six attempts (the first and five replacements) is the limit of a single slot', not m.needs_run(r.runs, 'c', 'attribution'))


def test_perf01():
    c = [cell('c', perf01=True)]
    for rows, expected in ((100_000, 'MET'), (100_000.5, 'MET'), (99_999, 'MISSED'), (50_000, 'MISSED'), (49_999, 'STOP'), (10_000, 'STOP')):
        check(f'PERF-01 median {rows:,}: {expected}', outcome_of(c, five('c', rows=rows), 'PERF-01') == expected)
    r = Runs()
    for v in (1, 1, 100_000, 100_000, 100_000):
        r.timed('c', rows=v)
    check('PERF-01 judges the MEDIAN: two slow runs of five do not move a median of 100,000', outcome_of(c, r, 'PERF-01') == 'MET')


def test_perf14():
    rep = [cell('c', target=True, stop=67.5)]
    for secs, expected in ((45.0, 'MET'), (45.01, 'MISSED'), (67.5, 'MISSED'), (67.51, 'STOP')):
        check(f'PERF-14 representative or stress cell, T-IMPORT {secs} s: {expected}', outcome_of(rep, five('c', seconds=secs), 'PERF-14') == expected)
    worst = [cell('w', 'Worst-case', target=False, stop=90.0)]
    for secs, expected in ((60.0, 'MET'), (89.99, 'MET'), (90.0, 'MET'), (90.01, 'STOP')):
        check(f'PERF-14 worst-case cell (no target, stop 90 s), {secs} s: {expected}', outcome_of(worst, five('w', seconds=secs), 'PERF-14') == expected)


def test_cancel_and_recovery():
    def cancel_runs(cid, seconds, extra=None):
        r = Runs()
        for p, s in zip((1, 2, 3, 4), seconds):
            r.add(cid, f'cancel:{p}', cancelSeconds=s)
        return r
    rep = [cell('c', cancel=1.75)]
    for secs, expected in (((0.5, 1.0, 1.75, 0.2), 'MET'), ((0.5, 1.76, 0.1, 0.2), 'MISSED'), ((0.1, 0.1, 2.625, 0.1), 'MISSED'), ((0.1, 2.64, 0.1, 0.1), 'STOP')):
        check(f'PERF-15 (c) representative, points {secs}: {expected}', outcome_of(rep, cancel_runs('c', secs), 'PERF-15 (c)') == expected)
    st = [cell('s', 'Stress', cancel=4.75)]
    check('PERF-15 (c) stress: 4.75 s met, 7.125 s missed, above it STOP',
          outcome_of(st, cancel_runs('s', (4.75, 1, 1, 1)), 'PERF-15 (c)') == 'MET' and outcome_of(st, cancel_runs('s', (7.125, 1, 1, 1)), 'PERF-15 (c)') == 'MISSED'
          and outcome_of(st, cancel_runs('s', (7.13, 1, 1, 1)), 'PERF-15 (c)') == 'STOP')
    r = cancel_runs('c', (0.1, 0.1, 0.1, 0.1))
    r.runs.pop()
    check('a cancel point without a valid run: the cell is NOT MEASURED', outcome_of(rep, r, 'PERF-15 (c)') == 'NOT MEASURED')
    r = cancel_runs('c', (0.1, 0.1, 0.1, 0.1))
    r.runs[1]['status'] = 'failed'
    r.runs[1]['metrics'] = {'problem': 'rollback assertion failed: main file grew'}
    check('a cancel whose rollback assertion failed is MISSED, never met', outcome_of(rep, r, 'PERF-15 (c)') == 'MISSED')
    check('PERF-16\'s engine part is reported separately, from the same figures', 'PERF-16 engine part' in m.evaluate(rep, cancel_runs('c', (1, 1, 1, 1)).runs)['budgets'])
    rc = [cell('c', recovery=True)]
    for secs, expected in ((5.0, 'MET'), (5.01, 'MISSED'), (7.5, 'MISSED'), (7.51, 'STOP')):
        r = Runs().add('c', 'crash', recoverySeconds=secs)
        check(f'recovery open {secs} s: {expected}', outcome_of(rc, r, 'PERF-15 (c)') == expected)
    r = Runs().add('c', 'crash', 'failed', problem='state Damaged')
    check('a recovery that leaves a damaged Library is MISSED', outcome_of(rc, r, 'PERF-15 (c)') == 'MISSED')
    check('no valid kill-and-recover run: NOT MEASURED', outcome_of(rc, Runs(), 'PERF-15 (c)') == 'NOT MEASURED')


def test_attribution():
    c = [cell('c')]
    check('PERF-15 (a) PASS is MET', outcome_of(c, Runs().add('c', 'attribution', attribution='PASS'), 'PERF-15 (a)') == 'MET')
    check('PERF-15 (a) FAIL is MISSED (a page outside the rule is a defect, no stop threshold)', outcome_of(c, Runs().add('c', 'attribution', attribution='FAIL'), 'PERF-15 (a)') == 'MISSED')
    check('PERF-15 (a) with only INVALID captures is NOT MEASURED', outcome_of(c, Runs().add('c', 'attribution', 'invalid'), 'PERF-15 (a)') == 'NOT MEASURED')
    r = Runs().add('c', 'attribution', 'invalid').add('c', 'attribution', attribution='PASS')
    check('PERF-15 (a): an INVALID capture followed by a valid PASS is MET', outcome_of(c, r, 'PERF-15 (a)') == 'MET')


def test_precedence_and_independence():
    cells = [cell('a', perf01=True), cell('b', perf01=True), cell('d', perf01=True)]
    r = five('a', rows=200_000)
    for _ in range(5):
        r.timed('b', rows=60_000)
    for _ in range(5):
        r.timed('d', rows=10_000)
    check('MISSED and STOP together: STOP', outcome_of(cells, r, 'PERF-01') == 'STOP')
    cells = [cell('a', perf01=True), cell('b', perf01=True)]
    check('MET and MISSED together: MISSED', outcome_of(cells, r, 'PERF-01') == 'MISSED')
    check('MISSED is never reported as met because no stop threshold was crossed', m.evaluate(cells, r.runs)['budgets']['PERF-01']['blocksAcceptance'])
    cells = [cell('a', perf01=True), cell('x', perf01=True)]
    check('MET and NOT MEASURED together: NOT MEASURED (blocks acceptance)', outcome_of(cells, five('a', rows=200_000), 'PERF-01') == 'NOT MEASURED')
    both = [cell('a', perf01=True, target=True, stop=67.5, cancel=1.75, recovery=True)]
    rr = five('a', rows=200_000, seconds=30.0)
    for p in (1, 2, 3, 4):
        rr.add('a', f'cancel:{p}', cancelSeconds=0.1)
    rr.add('a', 'crash', recoverySeconds=1.0)
    rr.add('a', 'attribution', attribution='PASS')
    ev = m.evaluate(both, rr.runs)
    check('the five budgets are reported independently and all MET here', {k: v['outcome'] for k, v in ev['budgets'].items()} ==
          {'PERF-01': 'MET', 'PERF-14': 'MET', 'PERF-15 (a)': 'MET', 'PERF-15 (c)': 'MET', 'PERF-16 engine part': 'MET'}, str({k: v['outcome'] for k, v in ev['budgets'].items()}))
    ev = m.evaluate(both, rr.runs, judged=False)
    check('a session that is not judged reports NOT JUDGED and what it would be', all(v['outcome'] == 'NOT JUDGED' and v['wouldBe'] == 'MET' for v in ev['budgets'].values()))
    r = Runs()
    r.add('a', 'timed', filesPerSecond=1, importSeconds=1, tokenGapSeconds=0.5)
    r.add('a', 'timed', filesPerSecond=1, importSeconds=1, tokenGapSeconds=0.51)
    check('the token-check interval (0.5 s) is judged on the largest gap of every valid run', m.evaluate([cell('a')], r.runs)['tokenInterval']['outcome'] == 'MISSED')
    r.runs.pop()
    check('...and met at exactly 0.5 s', m.evaluate([cell('a')], r.runs)['tokenInterval']['outcome'] == 'MET')


def test_rebuild():
    with tempfile.TemporaryDirectory() as d:
        manifest = {'schema': 1, 'id': 's', 'gate': True, 'rounds': 5, 'plan': {'cells': [cell('c', perf01=True)]}}
        json.dump(manifest, open(os.path.join(d, 'manifest.json'), 'w'))
        lines = []
        for i in range(5):
            lines.append({'event': 'judged', 'runId': f'c{i}', 'cell': 'c', 'kind': 'timed', 'attempt': i + 1, 'round': f'round{i + 1}', 'order': i + 1, 'status': 'ok', 'reasons': [], 'metrics': {'filesPerSecond': 150_000.0}, 'load': None})
        lines.append({'event': 'round-invalidated', 'runId': 'c2', 'round': 'round3', 'reason': 'the quiet check after its round failed'})
        with open(os.path.join(d, 'judgements.jsonl'), 'w') as f:
            f.write(''.join(json.dumps(x) + '\n' for x in lines))
        _, runs, controls, ev = perf_session.rebuild(d)
        check('rebuild applies a round invalidation recorded after the run was judged', [r['status'] for r in runs] == ['ok', 'ok', 'invalid', 'ok', 'ok'] and 'quiet check' in runs[2]['reasons'][-1])
        check('...so the cell has four valid runs and is not yet measured (it needs a replacement)', m.needs_run(runs, 'c', 'timed') and ev['budgets']['PERF-01']['outcome'] == 'NOT MEASURED')


class StubSampler:
    def __init__(self, series):
        self.series = series

    def check(self):
        pass

    def wait_until(self, wall, timeout=3.0):
        return True


def stub_session(series, load_validity=True):
    """A Session without a harness: only what judging and round invalidation use."""
    import loadsource
    s = perf_session.Session.__new__(perf_session.Session)
    s.load_validity = load_validity
    s.sampler = StubSampler(series)
    s.ls = loadsource
    s.runs = []
    s.dir = tempfile.mkdtemp(prefix='session-selftest-')
    s.log = lambda text: None
    return s


def timed_record(start, seconds=10.0, outcome='Published'):
    iso = perf_session.iso(__import__('datetime').datetime.fromtimestamp(start, __import__('datetime').timezone.utc))
    return {'schema': 1, 'kind': 'timed', 'label': 'x', 'binary': {}, 'cell': {}, 'operationStartUtc': iso, 'operationSeconds': seconds, 'outcome': outcome, 'library': {'growthBytes': 1},
            'imp11': {'spaceChecks': 3, 'commitGrowthEqualsFinalPending': True, 'intervalsOverAllowance': 0, 'largestIntervalGrowthOverAllowance': 0.1}, 'token': {'maxGapSeconds': 0.2},
            'journal': {'lengthAtFinalCheck': 5}, 'memory': {'peakWorkingSetBytes': 1}, 'filesPerSecond': 123_456.0, 'importSeconds': 9.5, 'files': 100, 'newNames': 5}


def series_at(start, seconds, u):
    n = int(seconds / 0.5)
    return [{'t0': start + 0.5 * i, 't1': start + 0.5 * (i + 1), 'U': u(i), 'whole': u(i), 'own': 0.0} for i in range(n)]


def judge_timed(series, rec, code=0, load_validity=True):
    s = stub_session(series, load_validity)
    run = {'runId': 'r', 'cell': 'c', 'kind': 'timed', 'attempt': 1, 'round': 'round1', 'order': 1, 'status': 'invalid', 'reasons': [], 'metrics': {}, 'load': None}
    s.judge(run, {'id': 'c'}, code, rec, '', None, None, rec)
    return run


def test_orchestrator_judging():
    t0 = 1_900_000_000.0
    quiet = series_at(t0 - 5, 30, lambda i: 2.0)
    r = judge_timed(quiet, timed_record(t0))
    check('a run inside a quiet window is valid and carries its metrics', r['status'] == 'ok' and r['metrics']['filesPerSecond'] == 123_456.0 and r['load']['verdict'] == 'VALID' and r['load']['n'] == 20, str(r))
    busy = series_at(t0 - 5, 30, lambda i: 60.0 if 10 <= i < 30 else 2.0)
    r = judge_timed(busy, timed_record(t0))
    check('a busy machine during the operation (U mean over 10%) invalidates the run, with the reason', r['status'] == 'invalid' and any('mean' in x for x in r['reasons']), str(r['reasons']))
    spike = series_at(t0 - 5, 30, lambda i: 40.0 if i in (12, 13, 14, 15, 16, 17) else 1.0)       # six of 20 collections at 40%: p95 over 25%, mean 8.2%
    r = judge_timed(spike, timed_record(t0))
    check('a p95 over 25% invalidates the run although its mean is under 10%', r['status'] == 'invalid' and any('95th' in x for x in r['reasons']), str(r['reasons']))
    r = judge_timed(busy, timed_record(t0 - 4.9, seconds=4.0))
    check('the same busy collections OUTSIDE the operation do not count', r['status'] == 'ok', str(r['reasons']))
    r = judge_timed(quiet, timed_record(t0 + 0.1, seconds=0.3))
    check('an operation with no collection wholly inside is INVALID, never valid', r['status'] == 'invalid' and any('no collection' in x for x in r['reasons']), str(r['reasons']))
    r = judge_timed(quiet, timed_record(t0, outcome='Failed: LibraryFull'))
    check('a failed save in a quiet window is a VALID run whose result is a failure (never replaced)', r['status'] == 'failed' and 'did not publish' in r['metrics']['problem'])
    r = judge_timed(quiet, None, code=3)
    check('a harness failure (no record) is an invalid run', r['status'] == 'invalid' and any('harness' in x for x in r['reasons']))
    r = judge_timed(busy, timed_record(t0), load_validity=False)
    check('without load validity (smoke) every run is accepted and its load is marked NOT ASSESSED', r['status'] == 'ok' and r['load']['verdict'] == 'NOT ASSESSED')


def test_round_invalidation():
    s = stub_session([])
    s.dir = tempfile.mkdtemp(prefix='session-selftest-')
    s.runs = [{'runId': 'a', 'cell': 'c', 'kind': 'timed', 'round': 'round1', 'status': 'ok', 'reasons': []}, {'runId': 'b', 'cell': 'd', 'kind': 'timed', 'round': 'round1', 'status': 'failed', 'reasons': []},
              {'runId': 'z', 'cell': 'c', 'kind': 'attribution', 'round': 'round1', 'status': 'ok', 'reasons': []}, {'runId': 'o', 'cell': 'c', 'kind': 'timed', 'round': 'round2', 'status': 'ok', 'reasons': []},
              {'runId': 'i', 'cell': 'e', 'kind': 'timed', 'round': 'round1', 'status': 'invalid', 'reasons': ['already']}]
    s.quiet_check = lambda name: {'verdict': 'NOT QUIET', 'U': {'mean': 12.0, 'p95': 40.0}}
    s.after_round('round1')
    check('a failed quiet check after a round invalidates the load-dependent runs of that round, i.e. its runs (valid and failed alike)', [r['status'] for r in s.runs] == ['invalid', 'invalid', 'ok', 'ok', 'invalid'], str([r['status'] for r in s.runs]))
    check('...but never an attribution (its verdict does not depend on load) nor a run of another round', s.runs[2]['status'] == 'ok' and s.runs[3]['status'] == 'ok')
    check('...and the invalidation is recorded in the append-only judgements', len(open(os.path.join(s.dir, 'judgements.jsonl')).read().splitlines()) == 2)
    s.runs[0]['status'] = 'ok'
    s.quiet_check = lambda name: {'verdict': 'QUIET', 'U': {'mean': 1.0, 'p95': 2.0}}
    s.after_round('round1')
    check('a quiet check that passes invalidates nothing', s.runs[0]['status'] == 'ok')


def main():
    for f in (test_statistic_and_replacement, test_perf01, test_perf14, test_cancel_and_recovery, test_attribution, test_precedence_and_independence, test_rebuild, test_orchestrator_judging, test_round_invalidation):
        f()
    print(f'{len(FAILS)} failed' if FAILS else 'all session self-tests passed')
    return 1 if FAILS else 0


if __name__ == '__main__':
    sys.exit(main())
