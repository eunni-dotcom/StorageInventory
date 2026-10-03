"""Reproduction of C4R-M04 through the scripted world of tests/perf/session_selftest.py (the real orchestrator over a virtual clock; nothing is measured).

Run it from the repository root, once on commit 9725516 (the red commit: it prints the defects) and once on a later commit (it prints the repaired behaviour):

    PYTHONPATH=tests/perf python docs/evidence/c4-repair-followup/m34-m04-repro.py
"""
import sys, json, os
sys.stdout.reconfigure(encoding='utf-8')
import session_selftest as t
import perf_session, perf_report

with t.Flow() as f:
    f.control_script['new'] = ['PASS']
    code, out, err, exc = f.go('G1')
    d = f.dir('G1')
    print('(b) a negative control that PASSED')
    print('  run exit code:', code)
    print('  session.json status:', json.load(open(os.path.join(d, 'session.json'), encoding='utf-8'))['status'])
    rep = '\n'.join(perf_report.session_report(d))
    for line in rep.splitlines():
        if line.startswith('Status:') or line.startswith('| PERF-15 (a) |') or line.startswith('| new |') or line.startswith('| existing |'):
            print('  report:', line)
    print('  outcome command exit code:', f.main(['outcome', '--session', d])[0])
with t.Flow() as f:
    f.quiet_levels = {'before the session': 40.0}
    c1 = f.go('R1')
    d = f.dir('R1')
    print('(a) a refused session run again in place')
    print('  first run exit code (refused):', c1[0], sorted(n for n in os.listdir(d) if n.endswith('.json') or n.endswith('.jsonl')))
    f.quiet_levels = {}
    c2 = f.run('R1')
    print('  second run of the same directory, exit code:', c2[0])
    ids = [r['runId'] for r in perf_session.read_lines(os.path.join(d, 'judgements.jsonl')) if r['event'] == 'judged']
    print('  judged runs:', len(ids), 'distinct ids:', len(set(ids)), '; refused.json still present:', os.path.exists(os.path.join(d, 'refused.json')), '; session.json present:', os.path.exists(os.path.join(d, 'session.json')))
    print('  report status:', [l for l in perf_report.session_report(d) if l.startswith('Status:')])
    print('  quiet.jsonl rows:', len(perf_session.read_lines(os.path.join(d, 'quiet.jsonl'))))
with t.Flow() as f:
    f.raise_at[('cancel:2',)] = RuntimeError('boom')
    f.declare('A1')
    r1 = f.run('A1')
    d = f.dir('A1')
    n1 = len([e for e in perf_session.read_lines(os.path.join(d, 'judgements.jsonl')) if e['event'] == 'judged'])
    f.raise_at = {}
    r2 = f.run('A1')
    ev = [e for e in perf_session.read_lines(os.path.join(d, 'judgements.jsonl')) if e['event'] == 'judged']
    print('(a) an aborted session run again in place')
    print('  first run raised:', type(r1[3]).__name__, '; judged runs after it:', n1, '; second run exit code:', r2[0], '; judged runs now:', len(ev), '; distinct run ids:', len({e['runId'] for e in ev}),
          '; order values repeated:', len([e['order'] for e in ev]) != len({e['order'] for e in ev}))
    print('  aborted.json present:', os.path.exists(os.path.join(d, 'aborted.json')), '; session.json present:', os.path.exists(os.path.join(d, 'session.json')))
