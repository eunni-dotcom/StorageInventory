"""C4 design final repair (C4DRR-M03): the load source around a real benchmark plan, to size the run rule's allowance for induced work.

Usage:  python load_harness.py <before.probe.json> <run.probe.json> <after.probe.json> <plan.jsonl> [--json <out.json>]

The plan ran inside the probe's job (`load_probe.py run -- <harness> --benchmark dr plan repair:timing <out> 1`), between two
60 s quiet checks. For each measured operation (from the record's StartedUtc to StartedUtc + ImportSeconds; collections wholly
inside it) it reports the whole machine, own, U, and the induced work the run causes outside its own processes: the counted
System and MsMpEng shares and the machine's DPC and interrupt time. The quiet checks give the same quantities with nothing of the
benchmark running. Induced work during an operation = (induced + DPC/interrupt) during it, less the mean of the two quiet checks.
"""
import json
import math
import sys
from datetime import datetime

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')


def stats(xs):
    xs = sorted(x for x in xs if x is not None)
    if not xs:
        return None
    return {'mean': round(sum(xs) / len(xs), 2), 'p95': round(xs[min(len(xs) - 1, math.ceil(0.95 * len(xs)) - 1)], 2), 'n': len(xs)}


def window(series):
    return {'U': stats([r['U'] for r in series]), 'whole': stats([r['whole'] for r in series]), 'own': stats([r['own'] for r in series]),
            'induced_counted': stats([r['induced_counted'] for r in series]), 'dpc_interrupt': stats([r['dpc_interrupt'] for r in series]),
            'induced_plus_dpc': stats([(r['induced_counted'] or 0) + r['dpc_interrupt'] for r in series if r['induced_counted'] is not None])}


def main(before, run, after, plan, json_out):
    b = json.load(open(before, encoding='utf-8'))
    r = json.load(open(run, encoding='utf-8'))
    a = json.load(open(after, encoding='utf-8'))
    quiet = {'before': {'verdict': b['verdict'], **window(b['series'])}, 'after': {'verdict': a['verdict'], **window(a['series'])}}
    base = (quiet['before']['induced_plus_dpc']['mean'] + quiet['after']['induced_plus_dpc']['mean']) / 2
    ops = []
    for line in open(plan, encoding='utf-8-sig'):
        rec = json.loads(line)
        start = datetime.fromisoformat(rec['StartedUtc'].replace('Z', '+00:00')).timestamp()
        end = start + rec['ImportSeconds']
        inside = [x for x in r['series'] if x['wall'] - 0.5 >= start and x['wall'] <= end]
        w = window(inside)
        ops.append({'cell': rec['Label'], 'import_seconds': round(rec['ImportSeconds'], 2), 'collections': len(inside), **w,
                    'induced_over_quiet_mean': round(w['induced_plus_dpc']['mean'] - base, 2) if w['induced_plus_dpc'] else None,
                    'induced_over_quiet_p95': round(w['induced_plus_dpc']['p95'] - base, 2) if w['induced_plus_dpc'] else None,
                    'run_rule_verdict': ('VALID' if w['U'] and w['U']['mean'] <= 10 and w['U']['p95'] <= 25 else 'INVALID')})
    out = {'quiet_checks': quiet, 'quiet_induced_plus_dpc_mean': round(base, 2), 'plan_run': {'verdict': r['verdict'], 'U': r['other_load_U_percent'],
           'own': r['own_percent'], 'job_processes_total': r['diagnostics']['job_processes_total']}, 'operations': ops,
           'largest_induced_over_quiet_mean': max(o['induced_over_quiet_mean'] for o in ops if o['induced_over_quiet_mean'] is not None),
           'largest_induced_over_quiet_p95': max(o['induced_over_quiet_p95'] for o in ops if o['induced_over_quiet_p95'] is not None)}
    text = json.dumps(out, indent=1)
    if json_out:
        with open(json_out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text + '\n')
    print(text)
    return 0


if __name__ == '__main__':
    x = sys.argv[1:]
    sys.exit(main(x[0], x[1], x[2], x[3], x[x.index('--json') + 1] if '--json' in x else None))
