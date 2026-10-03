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
  * C4R-M03: a result is never a reason to replace a run. A cancel run whose save published (or never reached its point, or whose rollback did not hold)
    and a save that failed early are VALID, kept, never replaced, MISSED (STOP above 1.5 x the budget on the measured time); a child that died, a record the
    harness refused (exit 3), a busy machine and a failed round remain INVALID and are replaced; what an invalidated run showed is kept
  * C4R-M04: the real orchestrator run end to end (perf_session.main, over a virtual clock, with scripted children, load source and checker verdicts): a
    refused, aborted, interrupted or killed session directory is never run again in place (a new declaration naming it works), every attempt stays
    queryable and reportable, a session whose negative control did not FAIL is TEST-P1 FAILED in session.json, `outcome` and the report, the exit codes
"""
import contextlib
import copy
import datetime
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import types

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gate_model as m  # noqa: E402
import perf_report  # noqa: E402
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


def test_can01e():
    r = Runs().add('c', 'cancel-after-final', publishedAfterCancel=True)
    check('CAN-01e: a cancellation after the final check that published is MET', m.evaluate([cell('c')], r.runs)['cancelAfterFinalCheck']['outcome'] == 'MET')
    r = Runs().add('c', 'cancel-after-final', 'failed', problem='did not publish')
    check('CAN-01e: one that did not publish is MISSED', m.evaluate([cell('c')], r.runs)['cancelAfterFinalCheck']['outcome'] == 'MISSED')
    check('CAN-01e: not planned when no such run exists', m.evaluate([cell('c')], [])['cancelAfterFinalCheck']['outcome'] == 'not planned')


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


def judge_as(series, rec, kind, code=0, err=''):
    s = stub_session(series)
    run = {'runId': 'r', 'cell': 'c', 'kind': kind, 'attempt': 1, 'round': 'block', 'order': 1, 'status': 'invalid', 'reasons': [], 'metrics': {}, 'load': None}
    s.judge(run, {'id': 'c'}, code, rec, err, None, None, rec)
    return run


def test_judge_cancel_records():
    t0 = 1_900_000_000.0
    quiet = series_at(t0 - 5, 30, lambda i: 2.0)

    def cancel_record(**cancel):
        rec = timed_record(t0, 3.0)
        rec['kind'] = 'cancel'
        rec['outcome'] = cancel.get('outcome', 'Cancelled (rolled back)')
        rec['cancel'] = {'point': 2, 'cancelToReturnSeconds': 0.4, 'rowsAtCancel': 1, 'journalAtCancelBytes': 1, 'outcome': 'Cancelled (rolled back)', 'rollback': {'rolledBack': True}, **cancel}
        return rec

    r = judge_as(quiet, cancel_record(requested=True), 'cancel:2')
    check('judge: an honoured, rolled-back cancellation is VALID and classified as an expected cancellation, with its time', r['status'] == 'ok' and r['metrics'].get('cancelClass') == 'expected cancellation' and r['metrics']['cancelSeconds'] == 0.4, str(r))
    r = judge_as(quiet, cancel_record(requested=True, outcome='Published', rollback=None), 'cancel:2')
    check('judge: a cancel run whose save published is VALID (failed), classified missed, with the time it took kept', r['status'] == 'failed' and r['metrics'].get('cancelClass') == 'missed' and r['metrics']['cancelSeconds'] == 0.4, str(r))
    r = judge_as(quiet, cancel_record(requested=False, outcome='Published', rollback=None, cancelToReturnSeconds=0.0), 'cancel:2')
    check('judge: a cancel point that was never reached is VALID (failed), classified never requested, with no time', r['status'] == 'failed' and r['metrics'].get('cancelClass') == 'never requested' and 'cancelSeconds' not in r['metrics'], str(r))
    r = judge_as(quiet, cancel_record(requested=True, rollback={'rolledBack': False, 'problem': 'main file grew'}), 'cancel:2')
    check('judge: a rollback that did not hold is VALID (failed), classified missed', r['status'] == 'failed' and r['metrics'].get('cancelClass') == 'missed', str(r))
    r = judge_as(quiet, cancel_record(), 'cancel:2')
    check('judge: a record from before the "requested" field is judged as a requested cancellation', r['status'] == 'ok' and r['metrics'].get('cancelClass') == 'expected cancellation', str(r))
    rec = cancel_record(requested=True)
    del rec['cancel']
    r = judge_as(quiet, rec, 'cancel:2')
    check('judge: a cancel record without its cancel object cannot be interpreted: an INVALID run (replaceable), not a miss', r['status'] == 'invalid' and any('not fit as raw evidence' in x for x in r['reasons']), str(r))
    r = judge_as(quiet, None, 'cancel:2', code=3, err='gate: the run record is not fit as raw evidence: missing cancel.rollback')
    check('judge: the harness child\'s exit 3 (it refused its own record) is invalid evidence, named as such', r['status'] == 'invalid' and any('exit 3' in x and 'not fit as raw evidence' in x for x in r['reasons']), str(r))
    r = judge_as(quiet, None, 'cancel:2', code=-532462766, err='Unhandled exception. System.IO.IOException: disk error')
    check('judge: a child that died (an unhandled exception) is an invalid run with its message', r['status'] == 'invalid' and any('disk error' in x for x in r['reasons']), str(r))
    rec = timed_record(t0, 3.0, outcome='Failed: The space guard stopped the import at Begin')
    rec['kind'] = 'attribution'
    rec['attribution'] = {}
    r = judge_as(quiet, rec, 'attribution')
    check('judge: an attributed save that failed is VALID (failed) without asking the checker', r['status'] == 'failed' and 'did not publish' in r['metrics']['problem'], str(r))


# ---------------------------------------------------------------------------------------------------------------- the scripted world
# C4R-M03 and C4R-M04 are about what the ORCHESTRATOR does with what the harness children report, and with a session directory it is asked to
# run again, so these tests run the real orchestrator (perf_session.main, Session.go / execute / judge / finish, rebuild, the report) in this
# process, over a VIRTUAL clock (a 60 s quiet check costs nothing). Only what is outside the orchestrator is scripted: the harness children
# (exit code, record, stderr), the load source (a synthetic series: 1% unless a quiet check or a run is scripted busy) and the attribution
# checker's verdict. Nothing here measures anything.
CELL = 'F-2M-25-system'


class VClock:
    """Virtual time: perf_session's time.time() and time.sleep() read and advance it."""

    def __init__(self):
        self.now = 1_900_000_000.0

    def time(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class SynthSampler:
    """The load source, synthetic: one collection per 0.5 s of virtual time; U is the background level except inside a registered busy window."""

    def __init__(self, clock):
        self.clock = clock
        self.series = []
        self.t = clock.now
        self.background = 1.0
        self.busy = []
        self.diag = {}

    def u_at(self, t):
        for a, b, u in self.busy:
            if a <= t < b:
                return u
        return self.background

    def fill(self, upto):
        while self.t + 0.5 <= upto:
            u = self.u_at(self.t)
            self.series.append({'t0': self.t, 't1': self.t + 0.5, 'U': u, 'whole': u, 'own': 0.0})
            self.t += 0.5

    def check(self):
        self.fill(self.clock.now)

    def wait_until(self, wall, timeout=3.0):
        self.fill(wall + 0.5)
        return True

    def stop(self):
        pass


def flow_plan():
    cell = {'id': CELL, 'label': 'first save', 'class': 'Representative', 'statedClass': 'Representative', 'library': 'L0', 'runFamily': 'obj', 'generatorParameters': 'd=0.25',
            'targetFiles': 2_000_000, 'prefillPerSnapshot': 1000, 'prefillKey': 'k', 'kinds': ['timed', 'attribution', 'cancel:1', 'cancel:2', 'cancel:3', 'cancel:4', 'crash'],
            'budgets': {'perf01': True, 'perf14Target': True, 'perf14StopSeconds': 67.5, 'perf15a': True, 'perf15cSeconds': 1.75, 'recovery': True}}
    return {'schema': 1, 'scale': 1.0, 'generatorVersion': 'g', 'binary': {'commit': '1' * 40, 'dirty': False, 'outputHash': 'ab' * 32, 'engine': 'x'}, 'cells': [cell]}


def tree_hashes(directory):
    """Every file of a session directory and its SHA-256: what 'the attempt is preserved' means."""
    out = {}
    for root, _, names in os.walk(directory):
        for n in names:
            path = os.path.join(root, n)
            with open(path, 'rb') as f:
                out[os.path.relpath(path, directory)] = hashlib.sha256(f.read()).hexdigest()
    return out


class Flow:
    """One scripted world: an evidence directory, a virtual clock and a script of what the children do. `script` maps a run mode (and optionally an
    attempt number) to a spec, 'ok' unless given: {'v': 'published'} (a cancel run whose save committed), 'never-requested', 'rollback-failed', 'slow',
    'failed-save' (the save failed at BEGIN), 'crash' (the child dies, no record), 'unfit' (exit 3), 'noisy' (the machine is busy during the run)."""

    def __init__(self):
        self.tmp = tempfile.mkdtemp(prefix='flow-')
        self.evidence = os.path.join(self.tmp, 'evidence')
        self.bench = os.path.join(self.tmp, 'bench')
        self.cache = os.path.join(self.tmp, 'cache')
        self.clock = VClock()
        self.sampler = None
        self.plan = flow_plan()
        self.script = {}
        self.quiet_levels = {}
        self.control_script = {}
        self.attribution_script = {}
        self.raise_at = {}
        self.attempts = {}
        self.calls = []
        self.control_calls = []
        self._undo = []

    # ---- the patches
    def patch(self, obj, name, value):
        self._undo.append((obj, name, getattr(obj, name)))
        setattr(obj, name, value)

    def __enter__(self):
        ps = perf_session
        flow = self
        real_run = subprocess.run
        real_quiet = ps.Session.quiet_check

        def start_load(s):
            import loadsource
            s.ls = loadsource
            s.job = None
            flow.sampler = s.sampler = SynthSampler(flow.clock)

        def quiet(s, name):
            s.sampler.background = flow.quiet_levels.get(name, 1.0)
            try:
                return real_quiet(s, name)
            finally:
                s.sampler.background = 1.0

        def fake_run(cmd, *args, **kwargs):
            if len(cmd) > 2 and os.path.basename(cmd[1]) == 'attribute_run.py':
                return flow.checker(os.path.basename(cmd[2])[:-len('.record.json')])
            return real_run(cmd, *args, **kwargs)

        self.patch(ps.gate_exe, 'git_identity', lambda root: ('1' * 40, False))
        self.patch(ps, 'plan_of', lambda h, scale, commit, dirty: copy.deepcopy(flow.plan))
        self.patch(ps, 'time', types.SimpleNamespace(time=self.clock.time, sleep=self.clock.sleep))
        self.patch(ps.Session, 'child', lambda s, *args, what='child': flow.child(s, args))
        self.patch(ps.Session, 'start_load', start_load)
        self.patch(ps.Session, 'machine_record', lambda s: {})
        self.patch(ps.Session, 'quiet_check', quiet)
        self.patch(subprocess, 'run', fake_run)
        return self

    def __exit__(self, *exc):
        for obj, name, old in reversed(self._undo):
            setattr(obj, name, old)
        shutil.rmtree(self.tmp, ignore_errors=True)
        return False

    # ---- the commands
    def main(self, argv):
        out, err = io.StringIO(), io.StringIO()
        code = exc = None
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            try:
                code = perf_session.main(argv)
            except BaseException as e:      # noqa: BLE001 - the orchestrator re-raises what it records; a test looks at it
                exc = e
        return code, out.getvalue(), err.getvalue(), exc

    def dir(self, sid):
        return os.path.join(self.evidence, sid)

    def declare(self, sid, gate=True, supersedes=None, reason=None):
        argv = ['declare', '--evidence', self.evidence, '--exe', 'x', '--id', sid] + (['--gate'] if gate else [])
        if supersedes:
            argv += ['--supersedes', supersedes]
        if reason:
            argv += ['--reason', reason]
        return self.main(argv)

    def run(self, sid):
        return self.main(['run', '--session', self.dir(sid), '--exe', 'x', '--bench-root', self.bench, '--prefill-cache', self.cache])

    def go(self, sid, **kw):
        declared = self.declare(sid, **kw)
        assert declared[0] == 0, declared[2]
        return self.run(sid)

    # ---- the scripted children
    def spec_for(self, mode, attempt):
        spec = self.script.get((mode, attempt), self.script.get((mode,), 'ok'))
        return {'v': spec} if isinstance(spec, str) else spec

    def child(self, s, args):
        cmd = args[0]
        opt = dict(zip(args[1::2], args[2::2]))
        if cmd == 'prefill':
            os.makedirs(os.path.dirname(opt['--out']), exist_ok=True)
            with open(opt['--out'], 'wb') as f:
                f.write(b'x')
            return 0, [], {'bytes': 1}, ''
        if cmd == 'describe':
            with open(opt['--out'], 'w') as f:
                f.write('{}')
            return 0, [], {}, ''
        if cmd == 'recover':
            self.clock.sleep(3.0)
            return 0, [], {'openSeconds': 0.5, 'state': 'Available', 'hotJournalBytes': 10, 'problem': None}, ''
        if cmd == 'control':
            self.control_calls.append((opt['--variant'], opt['--label']))
            return 0, [], {'schema': 1, 'kind': 'control', 'label': opt['--label'], 'attribution': {'layout': 'global', 'target': {'source': opt['--variant']}}}, ''
        assert cmd == 'run', cmd
        label, mode, cell = opt['--label'], opt['--mode'], opt['--cell']
        if label.startswith('warmup'):
            return self.make(cell, 'timed', label, {'v': 'ok'})
        n = self.attempts[mode] = self.attempts.get(mode, 0) + 1
        self.calls.append((mode, n, label))
        for key in ((mode, n), (mode,)):
            if key in self.raise_at:
                raise self.raise_at[key]
        return self.make(cell, mode, label, self.spec_for(mode, n))

    def make(self, cell, mode, label, spec):
        """(exit code, record, stderr) of one `run` child, as the real harness would write them."""
        v = spec['v']
        secs = spec.get('op', 3.0)
        if v == 'crash':
            self.clock.sleep(1.0)
            return spec.get('code', -532462766), [], None, 'Unhandled exception. System.IO.IOException: disk error'
        start = self.clock.now
        if v == 'noisy' or spec.get('noisy'):
            self.sampler.busy.append((start, start + secs + 2.0, 60.0))
        self.clock.sleep(secs + 2.0)
        rec = timed_record(start, secs)
        rec['label'] = label
        rec['cell'] = {'id': cell}
        rec['library'] = {'growthBytes': 1, 'snapshotsBefore': 3}
        if mode == 'crash':
            rec.update(kind='crash', outcome='Killed', killedLibraryDirectory=os.path.join(self.tmp, 'killed', 'Library'), killedAppData=os.path.join(self.tmp, 'killed', 'AppData'))
            return -1, [], rec, ''
        rec['kind'] = {'timed': 'timed', 'attribution': 'attribution', 'delete': 'delete', 'cancel-after-final': 'cancel-after-final'}.get(mode, 'cancel')
        if mode == 'attribution':
            rec['attribution'] = {'target': {'source': 'new'}, 'layout': 'persource', 'databaseCopy': 'd.sqlite3', 'journalCopy': 'j.journal'}
        if mode == 'delete':
            rec['delete'] = {'seconds': 1.2}
        if mode == 'cancel-after-final':
            rec['cancel'] = {'point': 0, 'outcome': 'Published', 'publishedAfterCancel': True, 'requested': True}
        if rec['kind'] in ('timed', 'attribution', 'delete', 'cancel-after-final') and v == 'failed-save':
            # the save failed at BEGIN: one check, no snapshot
            rec.update(outcome='Failed: The space guard stopped the import at Begin', filesPerSecond=0, importSeconds=0)
            rec['imp11'] = {'spaceChecks': 1, 'checks': [[1, 0, 1, 4096, 4096, 0, 0, 1]], 'commitGrowthEqualsFinalPending': False, 'intervalsOverAllowance': 0, 'largestIntervalGrowthOverAllowance': 0}
        if rec['kind'] == 'cancel':
            c = {'point': int(mode.split(':')[1]), 'pointName': 'p', 'cancelToReturnSeconds': 0.4, 'rowsAtCancel': 10, 'journalAtCancelBytes': 5, 'outcome': 'Cancelled (rolled back)',
                 'rollback': {'rolledBack': True, 'problem': None}, 'publishedAfterCancel': False, 'requested': True}
            rec['outcome'] = 'Cancelled (rolled back)'
            if v == 'published':
                c.update(outcome='Published', rollback=None, cancelToReturnSeconds=spec.get('seconds', 0.5))
                rec['outcome'] = 'Published'
            elif v == 'never-requested':
                c.update(outcome='Published', rollback=None, cancelToReturnSeconds=0.0, rowsAtCancel=0, journalAtCancelBytes=0, requested=False)
                rec['outcome'] = 'Published'
            elif v == 'rollback-failed':
                c['rollback'] = {'rolledBack': False, 'problem': 'main file grew'}
            elif v == 'slow':
                c['cancelToReturnSeconds'] = spec['seconds']
            rec['cancel'] = c
        if v == 'unfit':
            rec.pop('imp11', None)
            rec.pop('cancel', None)
            return 3, [], rec, 'gate: the run record is not fit as raw evidence: missing imp11'
        return 0, [], rec, ''

    def checker(self, label):
        """What attribute_run.py would print and exit with for this record: PASS unless scripted; a control FAILs unless scripted."""
        if label.startswith('control-'):
            variant = label.split('-')[1]
            verdicts = self.control_script.get(variant, ['FAIL'])
            verdict = verdicts[min(int(label.rsplit('-a', 1)[1]), len(verdicts)) - 1]
        else:
            verdict = self.attribution_script.get(label, 'PASS')
        code = {'PASS': 0, 'FAIL': 1, 'INVALID': 2}[verdict]
        out = {'verdict': verdict, 'reasons': [] if verdict == 'PASS' else ['scripted'], 'details': {'remote_pages': 7 if verdict == 'FAIL' else 0, 'levels_over_shared_cap': []}}
        return types.SimpleNamespace(stdout=json.dumps(out) + '\n', stderr='', returncode=code)


def runs_of_kind(directory, kind, cell=CELL):
    _, runs, _, _ = perf_session.rebuild(directory)
    return [r for r in runs if r['cell'] == cell and r['kind'] == kind]


def budget_outcome(directory, budget):
    return perf_session.rebuild(directory)[3]['budgets'][budget]['outcome']


def finished_ok(code, exc):
    return code == perf_session.EXIT_DONE and exc is None


# ---------------------------------------------------------------------------------------------------------------- C4R-M03: the result of a run is never a reason to discard it
def test_flow_baseline():
    with Flow() as f:
        code, out, err, exc = f.go('G1')
        d = f.dir('G1')
        _, runs, controls, ev = perf_session.rebuild(d)
        check('flow: a scripted gate session runs end to end (declare, quiet check, warm-up, five rounds, every block, controls, outcomes)', finished_ok(code, exc) and os.path.exists(os.path.join(d, 'session.json')), f'{code} {exc!r} {err[-300:]}')
        check('flow: every budget is MET and the negative control FAILed as required', all(b['outcome'] == 'MET' for b in ev['budgets'].values()) and ev.get('negativeControl', {}).get('ok') is True, str({k: v['outcome'] for k, v in ev['budgets'].items()}))
        check('flow: the session reads complete, from its files, in session.json and in the report',
              json.load(open(os.path.join(d, 'session.json')))['status'] == 'complete' and 'Status: **complete**' in '\n'.join(perf_report.session_report(d)))
        check('flow: a slot of every kind ran once and a timed slot five times, no replacement', len(runs_of_kind(d, 'timed')) == 5 and len(runs_of_kind(d, 'cancel:2')) == 1 and len(runs_of_kind(d, 'attribution')) == 1)


def test_flow_published_cancel():
    for label, spec, expected, why in (
            ('a cancellation the save did not honour (it published) at once', {'v': 'published', 'seconds': 0.5}, 'MISSED', 'returned 0.50 s'),
            ('a cancellation the save did not honour (it published) after 9 s: above 1.5 x the 1.75 s budget', {'v': 'published', 'seconds': 9.0}, 'STOP', 'above 1.5 x')):
        with Flow() as f:
            f.script[('cancel:2',)] = spec
            code, out, err, exc = f.go('G1')
            d = f.dir('G1')
            runs = runs_of_kind(d, 'cancel:2')
            r = runs[0] if runs else {'status': None, 'reasons': [], 'metrics': {}}
            ev = perf_session.rebuild(d)[3]
            c = [x for x in ev['budgets']['PERF-15 (c)']['cells'] if x['part'] == 'PERF-15 (c) cancel']
            check(f'M03: {label}: the run is VALID (status failed), kept, with its reason', len(runs) == 1 and r['status'] == 'failed' and 'not honoured' in ' '.join(r['reasons']), str(r))
            check(f'M03: ...and it is classified as a miss (cancelClass), a measured behavioural failure, not harness noise', r['metrics'].get('cancelClass') == 'missed' and r['metrics'].get('problem'), str(r['metrics']))
            check(f'M03: ...never replaced: one attempt, one cancel child', len(runs) == 1 and [c for c in f.calls if c[0] == 'cancel:2'] == [('cancel:2', 1, 'F-2M-25-system-cancel2-a1-block-cancel2')], str(f.calls))
            check(f'M03: ...PERF-15 (c) and PERF-16 are {expected} (never MET), and the gate result blocks acceptance',
                  ev['budgets']['PERF-15 (c)']['outcome'] == expected and ev['budgets']['PERF-16 engine part']['outcome'] == expected and ev['budgets']['PERF-15 (c)']['blocksAcceptance'] and why in c[0]['why'], str(c))
            check(f'M03: ...the session itself completes (exit 0) and the other budgets are untouched', finished_ok(code, exc) and ev['budgets']['PERF-01']['outcome'] == 'MET' and ev['budgets']['PERF-15 (a)']['outcome'] == 'MET')


def test_flow_cancel_classes():
    with Flow() as f:
        f.script[('cancel:1',)] = 'never-requested'
        f.script[('cancel:3',)] = 'rollback-failed'
        f.script[('cancel:4',)] = {'v': 'slow', 'seconds': 2.0}
        code, _, _, exc = f.go('G1')
        d = f.dir('G1')
        r1, r3, r4, r2 = (runs_of_kind(d, f'cancel:{p}') for p in (1, 3, 4, 2))
        check('M03: a cancel point that was never reached (no cancellation requested) is a valid miss, never replaced, with its own class',
              len(r1) == 1 and r1[0]['status'] == 'failed' and 'never requested' in ' '.join(r1[0]['reasons']) and r1[0]['metrics'].get('cancelClass') == 'never requested', str(r1))
        check('M03: a rollback that did not hold is a valid miss, never replaced', len(r3) == 1 and r3[0]['status'] == 'failed' and r3[0]['metrics'].get('cancelClass') == 'missed' and 'rollback assertion failed' in ' '.join(r3[0]['reasons']), str(r3))
        check('M03: a slow but honoured cancellation (2.0 s, budget 1.75 s) is an expected cancellation judged on time: valid, MISSED by the budget', len(r4) == 1 and r4[0]['status'] == 'ok' and r4[0]['metrics'].get('cancelClass') == 'expected cancellation', str(r4))
        check('M03: an honoured, rolled-back cancellation is classified as expected', r2[0]['status'] == 'ok' and r2[0]['metrics'].get('cancelClass') == 'expected cancellation', str(r2))
        check('M03: no cancel slot was replaced', all(len(x) == 1 for x in (r1, r2, r3, r4)) and budget_outcome(d, 'PERF-15 (c)') == 'MISSED')


def test_flow_early_failed_save():
    with Flow() as f:
        f.script[('timed', 3)] = 'failed-save'
        f.script[('attribution',)] = 'failed-save'
        code, _, _, exc = f.go('G1')
        d = f.dir('G1')
        timed, attribution = runs_of_kind(d, 'timed'), runs_of_kind(d, 'attribution')
        ev = perf_session.rebuild(d)[3]
        check('M03: a timed save that failed at BEGIN (one IMP-11 check) is a VALID run (failed) and is never replaced: five attempts, the third failed',
              len(timed) == 5 and timed[2]['status'] == 'failed' and 'did not publish' in ' '.join(timed[2]['reasons']) and [r['status'] for r in timed].count('ok') == 4, str([r['status'] for r in timed]))
        check('M03: ...so the cell is MISSED on PERF-01 and PERF-14 (a run of the sample failed), never MET', ev['budgets']['PERF-01']['outcome'] == 'MISSED' and ev['budgets']['PERF-14']['outcome'] == 'MISSED', str({k: v['outcome'] for k, v in ev['budgets'].items()}))
        check('M03: an attribution run whose save failed is a VALID run (failed), never replaced (the checker is not asked: there is no journal), and PERF-15 (a) is MISSED',
              len(attribution) == 1 and attribution[0]['status'] == 'failed' and ev['budgets']['PERF-15 (a)']['outcome'] == 'MISSED' and 'did not publish' in ev['budgets']['PERF-15 (a)']['cells'][0]['why'], str(attribution))
        check('M03: the session completed and kept the failed attempts in the raw table', finished_ok(code, exc) and len(open(os.path.join(d, 'runs.jsonl')).read().splitlines()) >= 5 + 1 + 4 + 1 + 1)


def test_flow_replaceable_failures():
    with Flow() as f:
        f.script[('timed', 2)] = 'crash'
        f.script[('timed', 4)] = 'unfit'
        f.script[('cancel:3', 1)] = 'noisy'
        code, _, _, exc = f.go('G1')
        d = f.dir('G1')
        timed, c3 = runs_of_kind(d, 'timed'), runs_of_kind(d, 'cancel:3')
        check('replacement: a harness child that dies (no record) is an invalid run, kept with its reason, and replaced (a seventh attempt)',
              timed[1]['status'] == 'invalid' and 'harness child failed' in ' '.join(timed[1]['reasons']) and len(timed) == 7 and [r['status'] for r in timed].count('ok') == 5, str([(r['status'], r['reasons']) for r in timed]))
        check('replacement: a record the harness itself refused as raw evidence (exit 3, unreadable) is an invalid run, kept with the reason, and replaced',
              timed[3]['status'] == 'invalid' and 'not fit as raw evidence' in ' '.join(timed[3]['reasons']), str(timed[3]['reasons']))
        check('replacement: a run on a busy machine (U over the run rule) is invalid and replaced', len(c3) == 2 and c3[0]['status'] == 'invalid' and c3[1]['status'] == 'ok', str(c3))
        check('replacement: ...the session completes and the replaced cells are MET', finished_ok(code, exc) and budget_outcome(d, 'PERF-01') == 'MET' and budget_outcome(d, 'PERF-15 (c)') == 'MET')
    with Flow() as f:
        f.script[('cancel:1',)] = 'crash'
        f.go('G1')
        runs = runs_of_kind(f.dir('G1'), 'cancel:1')
        check('replacement: a slot whose child always dies gets at most six attempts and is then NOT MEASURED (never MET)',
              len(runs) == 6 and all(r['status'] == 'invalid' for r in runs) and budget_outcome(f.dir('G1'), 'PERF-15 (c)') == 'NOT MEASURED', str(len(runs)))
    with Flow() as f:
        f.quiet_levels = {'after round2': 40.0}
        f.go('G1')
        timed = runs_of_kind(f.dir('G1'), 'timed')
        check('replacement: a round whose quiet check failed (the environment) is invalid and its runs are replaced, all kept',
              [r['status'] for r in timed].count('invalid') == 1 and len(timed) == 6 and 'quiet check after its round failed' in ' '.join(timed[1]['reasons']), str([r['status'] for r in timed]))


def test_flow_miss_in_an_invalid_run_is_kept():
    with Flow() as f:
        f.script[('cancel:3', 1)] = {'v': 'published', 'seconds': 0.5, 'noisy': True}
        f.go('G1')
        d = f.dir('G1')
        c3 = runs_of_kind(d, 'cancel:3')
        report = '\n'.join(perf_report.session_report(d))
        check('M03: a miss seen in a run that the load rule invalidates is replaced under that rule but NOT lost: the invalid row keeps what the child observed',
              len(c3) == 2 and c3[0]['status'] == 'invalid' and 'not honoured' in c3[0]['metrics'].get('problem', '') and 'not honoured' in ' '.join(c3[0]['reasons']), str(c3[0]))
        check('M03: ...and the report names it in its caveats', 'behavioural failure' in report and c3[0]['runId'] in report)
        check('M03: ...while the replacement (honoured, in a quiet window) decides the cell by the accepted replacement rule', c3[1]['status'] == 'ok' and budget_outcome(d, 'PERF-15 (c)') == 'MET')


def test_cancel_miss_rules():
    rep = [cell('c', cancel=1.75)]

    def with_miss(seconds):
        r = Runs()
        for p in (1, 2, 3, 4):
            if p == 2:
                metrics = {'problem': 'the cancellation was not honoured: Published'}
                if seconds is not None:
                    metrics['cancelSeconds'] = seconds
                r.add('c', f'cancel:{p}', 'failed', **metrics)
            else:
                r.add('c', f'cancel:{p}', cancelSeconds=0.1)
        return r

    for secs, expected in ((None, 'MISSED'), (0.4, 'MISSED'), (2.625, 'MISSED'), (2.63, 'STOP'), (30.0, 'STOP')):
        r = with_miss(secs)
        ev = m.evaluate(rep, r.runs)
        check(f'PERF-15 (c): a cancellation that was not honoured, returned after {secs} s: {expected} (a miss is MISSED; STOP only above 1.5 x the budget on its measured time)',
              ev['budgets']['PERF-15 (c)']['outcome'] == expected and ev['budgets']['PERF-16 engine part']['outcome'] == expected and ev['budgets']['PERF-15 (c)']['blocksAcceptance'], str(ev['budgets']['PERF-15 (c)']))
    check('PERF-15 (c): a miss is never replaced (a valid run) and never reported as met', not m.needs_run(with_miss(0.4).runs, 'c', 'cancel:2') and outcome_of(rep, with_miss(0.4), 'PERF-15 (c)') != 'MET')
    ev = m.evaluate(rep, with_miss(9.0).runs)
    check('PERF-15 (c): the cell figure includes the measured time of a missed run (the largest of the four points)', ev['cells']['c']['cancelSeconds'] == 9.0)


# ---------------------------------------------------------------------------------------------------------------- C4R-M04: an attempt is never run twice, and the negative control is part of the result
def test_flow_quiet_check_refusal():
    with Flow() as f:
        f.quiet_levels = {'before the session': 40.0}
        code, out, err, exc = f.go('G1')
        d = f.dir('G1')
        names = set(os.listdir(d))
        refused = json.load(open(os.path.join(d, 'refused.json'))) if 'refused.json' in names else {}
        quiet = perf_session.read_lines(os.path.join(d, 'quiet.jsonl'))
        check('M04: a failed quiet check before the session refuses it (exit 3) and the session does not start', code == perf_session.EXIT_REFUSED and exc is None and 'session.json' not in names and 'runs.jsonl' not in names, f'{code} {exc!r} {sorted(names)}')
        check('M04: the refusal leaves an explicit attempt record: status, session id, gate flag, the reason',
              refused.get('status') == 'REFUSED' and refused.get('session') == 'G1' and refused.get('gate') is True and 'quiet check' in refused.get('reason', ''), str(refused)[:300])
        check('M04: ...the declared configuration (the manifest and its hash, cells, rounds, scale, load validity)',
              refused.get('manifest', {}).get('sha256') == hashlib.sha256(open(os.path.join(d, 'manifest.json'), 'rb').read()).hexdigest() and refused.get('declared', {}).get('rounds') == 5 and refused.get('declared', {}).get('cells') == [CELL], str(refused)[:300])
        check('M04: ...and the quiet-check evidence: the summary (verdict, U mean, p95) in the record, the raw collections and the check in their files',
              refused.get('quietCheck', {}).get('verdict') == 'NOT QUIET' and refused['quietCheck']['U']['mean'] == 40.0 and len(quiet) == 1 and quiet[0]['verdict'] == 'NOT QUIET'
              and len(perf_session.read_lines(os.path.join(d, 'collections.jsonl'))) >= 120 and len(refused.get('rawCollections') or []) == 120 and refused['rawCollections'][0]['U'] == 40.0
              and all(os.path.exists(os.path.join(d, n)) for n in refused.get('evidence', {}).values()) and refused.get('evidence', {}).get('collections') == 'collections.jsonl', str(refused.get('quietCheck'))[:200])
        before = tree_hashes(d)
        f.quiet_levels = {}                       # "quiet the machine and run it again" is the natural next step
        code2, out2, err2, exc2 = f.run('G1')
        check('M04: running the same session directory again is refused (exit 2), naming the refusal record', code2 == perf_session.EXIT_USAGE and exc2 is None and 'refused.json' in err2, f'{code2} {exc2!r} {err2[-200:]}')
        check('M04: ...and the refused attempt is preserved byte for byte (no run appended, no file rewritten)', tree_hashes(d) == before and not f.calls)
        c, o, e, x = f.declare('G2')
        check('M04: a new gate declaration needs --supersedes: the first declared session is the gate session, and the refused one still counts', c == perf_session.EXIT_USAGE and 'first session declared' in e, e[-300:])
        c, o, e, x = f.declare('G2', supersedes='G1', reason='the quiet check before the session failed')
        m2 = json.load(open(os.path.join(f.dir('G2'), 'manifest.json'))) if os.path.exists(os.path.join(f.dir('G2'), 'manifest.json')) else {}
        check('M04: a NEW session declared with --supersedes and a reason works and names the earlier attempt', c == 0 and m2.get('supersedes') == 'G1' and m2.get('reason'), e[-300:])
        code3, out3, err3, exc3 = f.run('G2')
        check('M04: ...it runs to the end, and the refused attempt is still untouched', finished_ok(code3, exc3) and tree_hashes(d) == before, f'{code3} {exc3!r}')
        att = {a['id']: a for a in perf_session.attempts(f.evidence)} if hasattr(perf_session, 'attempts') else {}
        check('M04: both attempts stay queryable: G1 REFUSED, G2 complete naming G1',
              att.get('G1', {}).get('status') == 'REFUSED' and att.get('G2', {}).get('status') == 'complete' and att.get('G2', {}).get('supersedes') == 'G1' and att.get('G1', {}).get('gate') is True, str(att))
        text = '\n'.join(perf_report.session_report(d))
        check('M04: ...and the refused attempt is reportable in full: status, the failed quiet check, the declared matrix', 'REFUSED' in text and 'NOT QUIET' in text and 'before the session' in text and CELL in text)
        cli = f.main(['attempts', '--evidence', f.evidence])
        check('M04: the attempts command lists them', cli[0] == 0 and 'G1' in cli[1] and 'REFUSED' in cli[1] and 'G2' in cli[1] and 'complete' in cli[1], f'{cli[0]} {cli[1][-300:]} {cli[2][-200:]}')
        c, o, e, x = f.declare('G3', supersedes='G2', reason='retry')
        check('M04: a session that completed validly cannot be superseded (a rerun because of its result)', c == perf_session.EXIT_USAGE and 'not been invalidated' in e, e[-300:])
        c, o, e, x = f.declare('G3', supersedes='G1', reason='retry')
        check('M04: an attempt is superseded once: G1 already has its successor G2', c == perf_session.EXIT_USAGE and 'already superseded' in e, e[-300:])


def test_flow_aborted_session():
    with Flow() as f:
        f.raise_at[('cancel:2',)] = RuntimeError('the scripted child blew up')
        f.declare('G1')
        code, out, err, exc = f.run('G1')
        d = f.dir('G1')
        aborted = json.load(open(os.path.join(d, 'aborted.json'))) if os.path.exists(os.path.join(d, 'aborted.json')) else {}
        check('M04: a session that dies with an exception is recorded ABORTED (status, session id, error, how many runs it had recorded) and the exception is not swallowed',
              isinstance(exc, RuntimeError) and aborted.get('status') == 'ABORTED' and aborted.get('session') == 'G1' and 'blew up' in aborted.get('error', '') and aborted.get('runsRecorded', 0) > 5 and not os.path.exists(os.path.join(d, 'session.json')), str(aborted))
        before = tree_hashes(d)
        children = len(f.calls)
        f.script = {}
        f.raise_at = {}
        code2, out2, err2, exc2 = f.run('G1')
        check('M04: running an aborted session directory again is refused (exit 2) and changes nothing: its partial runs are never mixed with a second pass',
              code2 == perf_session.EXIT_USAGE and 'aborted.json' in err2 and tree_hashes(d) == before and len(f.calls) == children, f'{code2} {exc2!r} {err2[-200:]}')
        runs = perf_session.rebuild(d)[1]
        check('M04: the partial runs of the aborted attempt stay in its raw tables, with distinct run ids and orders (no duplicates)',
              len(runs) > 5 and len({r['runId'] for r in runs}) == len(runs) and [r['order'] for r in runs] == list(range(1, len(runs) + 1)), str(len(runs)))
        text = '\n'.join(perf_report.session_report(d))
        check('M04: ...and the aborted attempt is reportable: ABORTED with its error, the partial table', 'ABORTED' in text and 'blew up' in text and runs[0]['runId'] in text)
        att = {a['id']: a for a in perf_session.attempts(f.evidence)} if hasattr(perf_session, 'attempts') else {}
        check('M04: ...queryable as ABORTED', att.get('G1', {}).get('status') == 'ABORTED', str(att))
        c, o, e, x = f.declare('G2', supersedes='G1', reason='the session aborted: the harness raised an exception')
        check('M04: an aborted attempt is an invalidated session: it can be superseded by a new declaration', c == 0, e[-300:])
    with Flow() as f:
        f.raise_at[('timed', 2)] = KeyboardInterrupt()
        f.declare('G1')
        code, out, err, exc = f.run('G1')
        aborted = json.load(open(os.path.join(f.dir('G1'), 'aborted.json'))) if os.path.exists(os.path.join(f.dir('G1'), 'aborted.json')) else {}
        check('M04: an interrupted session (Ctrl-C) is recorded ABORTED too, and the interrupt still propagates', isinstance(exc, KeyboardInterrupt) and aborted.get('status') == 'ABORTED' and 'KeyboardInterrupt' in aborted.get('error', ''), str(aborted))
        check('M04: ...and cannot be run again', f.run('G1')[0] == perf_session.EXIT_USAGE)
    with Flow() as f:
        f.raise_at[('timed', 2)] = OSError('the scratch volume went away')
        f.declare('G1')
        code, out, err, exc = f.run('G1')
        check('M04: an OSError aborts the session with exit 4 and records it', code == perf_session.EXIT_ABORTED and os.path.exists(os.path.join(f.dir('G1'), 'aborted.json')) and 'went away' in err, f'{code} {exc!r}')
    with Flow() as f:
        f.declare('G1')
        d = f.dir('G1')
        with open(os.path.join(d, 'runs.jsonl'), 'w') as fh:
            fh.write('{"kind":"timed"}\n')                     # a process that was killed mid-session leaves its raw table and nothing else
        code, out, err, exc = f.run('G1')
        att = {a['id']: a for a in perf_session.attempts(f.evidence)} if hasattr(perf_session, 'attempts') else {}
        check('M04: a directory that holds a raw table but no outcome (a killed session) cannot be run again either, and reads INCOMPLETE', code == perf_session.EXIT_USAGE and att.get('G1', {}).get('status') == 'INCOMPLETE', f'{code} {att}')
        declared = f.declare('G2', gate=False)
        att = {a['id']: a for a in perf_session.attempts(f.evidence)} if hasattr(perf_session, 'attempts') else {}
        check('M04: ...a declared session that never started reads as such (and may be run)', declared[0] == 0 and att.get('G2', {}).get('status') == 'declared (not run)', str(att))


def test_flow_negative_control():
    with Flow() as f:
        f.control_script['new'] = ['INVALID', 'INVALID', 'FAIL']
        code, out, err, exc = f.go('G1')
        d = f.dir('G1')
        _, _, controls, ev = perf_session.rebuild(d)
        check('M04: a valid negative control (INVALID evidence captured again, then FAIL) is complete, exit 0, PERF-15 (a) MET',
              finished_ok(code, exc) and [c['verdict'] for c in controls if c['variant'] == 'new'] == ['INVALID', 'INVALID', 'FAIL'] and ev['budgets']['PERF-15 (a)']['outcome'] == 'MET' and ev.get('negativeControl', {}).get('ok') is True, str(controls))
    with Flow() as f:
        f.control_script['new'] = ['PASS']
        code, out, err, exc = f.go('G1')
        d = f.dir('G1')
        session = json.load(open(os.path.join(d, 'session.json')))
        _, _, controls, ev = perf_session.rebuild(d)
        outcome = f.main(['outcome', '--session', d])
        report = '\n'.join(perf_report.session_report(d))
        check('M04: a negative control that PASSED is TEST-P1 FAILED in session.json and the run exits 1', code == perf_session.EXIT_FAILED and session['status'].startswith('TEST-P1 FAILED'), f'{code} {session["status"]}')
        check('M04: ...rebuilt from the raw files: PERF-15 (a) is TEST-P1 FAILED (not MET) and blocks acceptance, and the live and the replayed evaluations agree',
              ev['budgets']['PERF-15 (a)']['outcome'] == 'TEST-P1 FAILED' and ev['budgets']['PERF-15 (a)']['blocksAcceptance'] and session['evaluation']['budgets']['PERF-15 (a)']['outcome'] == 'TEST-P1 FAILED', str(ev['budgets']['PERF-15 (a)']))
        check('M04: ...the outcome command says so and exits 1', outcome[0] == perf_session.EXIT_FAILED and 'TEST-P1 FAILED' in outcome[1], f'{outcome[0]} {outcome[1][-300:]}')
        check('M04: ...the report says so: status, PERF-15 (a) outcome, the control table and its verdict; it does not say complete or MET',
              'TEST-P1 FAILED' in report and 'Status: **complete**' not in report and '| PERF-15 (a) | TEST-P1 FAILED' in report and '| new | 1 | PASS' in report
              and '**TEST-P1 FAILED**: the new-source control PASSED' in report, [l for l in report.splitlines() if 'Status' in l or 'PERF-15 (a) |' in l])
        att = {x['id']: x for x in perf_session.attempts(f.evidence)}
        check('M04: ...and attempts lists the session as TEST-P1 FAILED, not complete', att.get('G1', {}).get('status', '').startswith('TEST-P1 FAILED'), str(att))
        check('M04: ...the control is persisted and never replaced by another control run: one attempt of the variant in the raw judgements and one control child',
              len([c for c in controls if c['variant'] == 'new']) == 1 and [c for c in f.control_calls if c[0] == 'new'] == [('new', 'control-new-a1')], str(f.control_calls))
        check('M04: ...a session whose checker is defective is invalidated by the rules: it can be superseded', f.declare('G2', supersedes='G1', reason='the negative control passed: the checker is defective')[0] == 0)
    with Flow() as f:
        f.control_script['existing'] = ['INVALID'] * 5
        code, out, err, exc = f.go('G1')
        _, _, controls, ev = perf_session.rebuild(f.dir('G1'))
        check('M04: a control that never gave a valid FAIL in five attempts is TEST-P1 FAILED too (no evidence that the checker works), exit 1',
              code == perf_session.EXIT_FAILED and len([c for c in controls if c['variant'] == 'existing']) == 5 and ev['budgets']['PERF-15 (a)']['outcome'] == 'TEST-P1 FAILED', f'{code} {len(controls)}')


def test_control_judgement():
    both = [{'variant': 'new', 'verdict': 'FAIL'}, {'variant': 'existing', 'verdict': 'FAIL'}]
    check('control: both variants FAIL: the checker works', m.judge_control(both)['ok'] and m.judge_control(both)['outcome'] == 'FAILED AS REQUIRED')
    check('control: INVALID evidence is captured again: INVALID then FAIL is fine', m.judge_control([{'variant': 'new', 'verdict': 'INVALID'}] + both)['ok'])
    check('control: a PASS in either variant is a defective checker', not m.judge_control([{'variant': 'new', 'verdict': 'PASS'}, both[1]])['ok'] and not m.judge_control([both[0], {'variant': 'existing', 'verdict': 'PASS'}])['ok'])
    check('control: a PASS is not cured by a later FAIL', not m.judge_control([{'variant': 'new', 'verdict': 'PASS'}] + both)['ok'])
    check('control: a variant without a valid FAIL, or none recorded, is TEST-P1 FAILED', not m.judge_control([both[0]])['ok'] and not m.judge_control([])['ok'] and not m.judge_control([both[0], {'variant': 'existing', 'verdict': 'INVALID'}])['ok'])
    c = [cell('c')]
    r = Runs().add('c', 'attribution', attribution='PASS')
    bad = [{'variant': 'new', 'verdict': 'PASS'}, both[1]]
    ev = m.evaluate(c, r.runs, True, controls=bad)
    check('control: PERF-15 (a) is TEST-P1 FAILED although every attribution PASSes, and blocks acceptance', ev['budgets']['PERF-15 (a)']['outcome'] == 'TEST-P1 FAILED' and ev['budgets']['PERF-15 (a)']['blocksAcceptance'])
    ev = m.evaluate(c, r.runs, False, controls=bad)
    check('control: a session that is not judged reports NOT JUDGED and what it would be', ev['budgets']['PERF-15 (a)']['outcome'] == 'NOT JUDGED' and ev['budgets']['PERF-15 (a)']['wouldBe'] == 'TEST-P1 FAILED')
    check('control: without control information the evaluation is what it was (PERF-15 (a) MET)', m.evaluate(c, r.runs, True)['budgets']['PERF-15 (a)']['outcome'] == 'MET' and m.evaluate(c, r.runs, True, controls=both)['budgets']['PERF-15 (a)']['outcome'] == 'MET')


def test_exit_codes():
    codes = [perf_session.EXIT_DONE, perf_session.EXIT_FAILED, perf_session.EXIT_USAGE, perf_session.EXIT_REFUSED, perf_session.EXIT_ABORTED, getattr(perf_session, 'EXIT_CRASHED', None)]
    check('exit codes: six distinct codes (0 done, 1 negative control failed, 2 usage or refused, 3 quiet check refused, 4 aborted, 5 internal error)', codes == [0, 1, 2, 3, 4, 5], str(codes))
    with tempfile.TemporaryDirectory() as d:
        with open(os.path.join(d, 'manifest.json'), 'w') as f:
            f.write('{')
        p = subprocess.run([sys.executable, perf_session.__file__, 'outcome', '--session', d], capture_output=True, text=True)
        check('exit codes: an unhandled exception in the orchestrator exits 5, no longer the 1 of "the negative control failed"', p.returncode == 5 and 'Traceback' in p.stderr, f'{p.returncode} {p.stderr[-200:]}')


def main():
    tests = (test_statistic_and_replacement, test_perf01, test_perf14, test_cancel_and_recovery, test_attribution, test_precedence_and_independence, test_can01e, test_rebuild, test_orchestrator_judging,
             test_round_invalidation, test_judge_cancel_records, test_cancel_miss_rules, test_control_judgement, test_exit_codes, test_flow_baseline, test_flow_published_cancel, test_flow_cancel_classes, test_flow_early_failed_save,
             test_flow_replaceable_failures, test_flow_miss_in_an_invalid_run_is_kept, test_flow_quiet_check_refusal, test_flow_aborted_session, test_flow_negative_control)
    for f in tests:
        try:
            f()
        except Exception as e:      # noqa: BLE001 - a test that raises is a failed test, reported by name, and the others still run
            check(f'{f.__name__} raised {type(e).__name__}', False, repr(e))
    print(f'{len(FAILS)} failed' if FAILS else 'all session self-tests passed')
    return 1 if FAILS else 0


if __name__ == '__main__':
    sys.exit(main())
