"""Regression of the load source against EXTERNAL LOAD (§15.4 "Load source"; C4DRR-M03, C4 design final repair): the final method must catch sustained
external CPU and SHORT-LIVED PROCESS STORMS, and must subtract the benchmark's own CPU. The old per-process method is kept only as a labelled NEGATIVE
CONTROL (it is expected to miss the storm and is never a verdict).

Usage:  python load_regression.py [--quick] [--blocks <n>] [--json <out.json>]        (takes about 5 minutes; --quick about 1.5)

Method (the reference's alternating blocks, docs/evidence/c4-design-review/load_experiments.py, so that slow background drift cancels): in one probe
session the injector switches an exactly measured load on for `block` seconds, off for `gap` seconds, n times. The change of U is the mean over the
blocks of (U in the block's interior) - (U in the interiors of the gaps on either side). The probe is a separate process that assigns ITSELF to the job
object (loadsource.Job) and runs loadsource.Sampler; a benchmark stand-in runs inside the job; the external injectors are started by this driver,
OUTSIDE the job. Injected CPU is measured exactly (GetProcessTimes of every child after it exits).

Experiments and what each must show
  sustained    four CPU-bound external processes in blocks: the change of U is at least HALF of the injected share            (sustained external CPU)
  churn        four concurrent short-lived (0.3 s) external processes, replaced at once, in blocks: the change of U is at least HALF of the injected
               share (the reference measured 110% of it). NEGATIVE CONTROL: the summed per-process 'external' counters (the old method) are recorded
               beside it and are expected to see only a fraction (the reference: 15 to 25%); that figure decides nothing
  bench        a busy benchmark stand-in INSIDE the job in blocks, no external load: the own share rises by about one logical processor's worth and the
               change of U stays within a few points of zero                                                                (own CPU is subtracted)
  bench+churn  the stand-in busy throughout, the storm in blocks: U still rises by at least half of the injected share, own does not change

On a machine that is busy for other reasons the per-block figures scatter; the verdict uses the mean over the blocks and says so. Exit code 0 when
every experiment passes.
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
INJECT = os.path.join(ROOT, 'docs', 'evidence', 'c4-design-review', 'load_inject.py')
PY = sys.executable
FIELDS = ('whole', 'own', 'U', 'external_counted', 'induced_counted', 'unattributed')

SPIN = 'import sys, time\nt = time.perf_counter()\nw = float(sys.argv[1])\nwhile time.perf_counter() - t < w:\n    pass\n'
STAND_IN = ('import sys, time\non, off, n = float(sys.argv[1]), float(sys.argv[2]), int(sys.argv[3])\ntime.sleep(float(sys.argv[4]))\n'
            'for _ in range(n):\n    t = time.perf_counter()\n    while time.perf_counter() - t < on:\n        pass\n    time.sleep(off)\n')
STAND_IN_BUSY = 'import sys, time\nt = time.perf_counter()\nwhile time.perf_counter() - t < float(sys.argv[1]):\n    pass\n'


# ------------------------------------------------------------------------------------------------------------------ the probe process
def probe(argv):
    """python load_regression.py --probe <seconds> <json> [stand-in: cycles <on> <off> <n> <lead> | busy <seconds>]"""
    import loadsource as ls
    seconds, out = float(argv[0]), argv[1]
    job = ls.Job()
    job.assign_self()
    sampler = ls.Sampler(job)
    sampler.start()
    child = None
    if len(argv) > 2 and argv[2] == 'cycles':
        child = subprocess.Popen([PY, '-c', STAND_IN, argv[3], argv[4], argv[5], argv[6]])
    elif len(argv) > 2 and argv[2] == 'busy':
        child = subprocess.Popen([PY, '-c', STAND_IN_BUSY, argv[3]])
    if child:
        job.require_member(child.pid, 'the benchmark stand-in')
    time.sleep(seconds)
    if child and child.poll() is None:
        child.wait(timeout=seconds + 30)
    sampler.stop()
    json.dump({'cpus': sampler.cpus, 'series': sampler.series, 'diag': sampler.diag, 'job_cpu_seconds': job.cpu_seconds()[0]}, open(out, 'w'))
    return 0


# ------------------------------------------------------------------------------------------------------------------ injectors
def cpu_of(proc):
    import ctypes
    from ctypes import wintypes

    class FileTime(ctypes.Structure):
        _fields_ = [('lo', wintypes.DWORD), ('hi', wintypes.DWORD)]
    k32 = ctypes.WinDLL('kernel32', use_last_error=True)
    c, e, k, u = FileTime(), FileTime(), FileTime(), FileTime()
    if not k32.GetProcessTimes(wintypes.HANDLE(int(proc._handle)), ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u)):
        raise OSError('GetProcessTimes failed')
    f = lambda t: ((t.hi << 32) | t.lo) / 1e7
    return f(k) + f(u)


def inject_sustained(n, blocks, on, off):
    """Blocks of n CPU-bound processes (external to the job). Returns the blocks' wall times and the exactly measured injected share."""
    import loadsource as ls
    spans, children = [], []
    time.sleep(off)
    for _ in range(blocks):
        a = time.time()
        procs = [subprocess.Popen([PY, '-c', SPIN, str(on)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) for _ in range(n)]
        for p in procs:
            p.wait()
        spans.append([a, time.time()])
        children += procs
        time.sleep(off)
    total = sum(cpu_of(p) for p in children)
    on_time = sum(b - a for a, b in spans)
    return {'blocks': spans, 'processes': len(children), 'injected_machine_percent': round(100.0 * total / (on_time * ls.logical_processors()), 2)}


def inject_churn(blocks, on, off, work=0.3, concurrent=4):
    """The reference's churn-cycles injector (load_inject.py), run as the driver's own child (outside the probe's job)."""
    p = subprocess.run([PY, INJECT, 'churn-cycles', str(concurrent), str(blocks), str(on), str(off), str(work)], capture_output=True, text=True, encoding='utf-8')
    if p.returncode != 0:
        raise SystemExit('load_inject.py failed: ' + p.stderr[-300:])
    return json.loads(p.stdout)


# ------------------------------------------------------------------------------------------------------------------ analysis
def mean(xs):
    xs = [x for x in xs if x is not None]
    return sum(xs) / len(xs) if xs else None


def inside(series, a, b, lead, tail):
    return [r for r in series if r['t0'] >= a + lead and r['t1'] <= b - tail]


def block_changes(series, blocks):
    """Per block: mean of each field inside the block minus its mean in the gaps on either side."""
    edges = [series[0]['t0']] + [x for b in blocks for x in b] + [series[-1]['t1']]
    gaps = [(edges[2 * i], edges[2 * i + 1]) for i in range(len(blocks) + 1)]
    per = []
    for i, (a, b) in enumerate(blocks):
        on = inside(series, a, b, 0.5, 0.0)
        off = inside(series, *gaps[i], 1.0, 0.25) + inside(series, *gaps[i + 1], 1.0, 0.25)
        if not on or not off:
            continue
        row = {f: round(mean([r.get(f) for r in on]) - mean([r.get(f) for r in off]), 2) for f in FIELDS if mean([r.get(f) for r in on]) is not None and mean([r.get(f) for r in off]) is not None}
        row['gap_whole'] = round(mean([r['whole'] for r in off]), 2)      # the machine's load without the injection: how much room it left
        per.append(row)
    return per


def summarise(per):
    return {f: {'mean': round(mean([p[f] for p in per if f in p]), 2), 'range': [min(p[f] for p in per if f in p), max(p[f] for p in per if f in p)]}
            for f in FIELDS + ('gap_whole',) if any(f in p for p in per)}


# ------------------------------------------------------------------------------------------------------------------ the experiments
def experiment(name, blocks, on, off, tmp):
    total = off + blocks * (on + off) + 4
    out = os.path.join(tmp, name + '.probe.json')
    if name == 'bench':
        pr = subprocess.Popen([PY, os.path.abspath(__file__), '--probe', str(total), out, 'cycles', str(on), str(off), str(blocks), str(off)])
        time.sleep(0.5)
        inj = {'blocks': None}
    elif name == 'bench+churn':
        pr = subprocess.Popen([PY, os.path.abspath(__file__), '--probe', str(total), out, 'busy', str(total)])
        time.sleep(0.5)
        inj = inject_churn(blocks, on, off)
    else:
        pr = subprocess.Popen([PY, os.path.abspath(__file__), '--probe', str(total), out])
        time.sleep(0.5)
        inj = inject_sustained(4, blocks, on, off) if name == 'sustained' else inject_churn(blocks, on, off)
    pr.wait()
    p = json.load(open(out))
    series = p['series']
    cpus = p['cpus']
    if name == 'bench':
        # the stand-in starts after `off` seconds of lead; its busy blocks are [lead + k (on + off), +on]
        t0 = series[0]['t0']
        blocks_wall = [[t0 + 0.5 + off + k * (on + off), t0 + 0.5 + off + k * (on + off) + on] for k in range(blocks)]
    else:
        blocks_wall = inj['blocks']
    per = block_changes(series, blocks_wall)
    return {'experiment': name, 'cpus': cpus, 'blocks_analysed': len(per), 'injected_machine_percent': inj.get('injected_machine_percent'), 'change': summarise(per),
            'diag': p['diag'], 'per_block': per}


def verdicts(results):
    """(description, outcome, detail): outcome True / False / None (INCONCLUSIVE: the machine left too little room for the injected load to show)."""
    out = []
    by = {r['experiment']: r for r in results}

    def seen(r, f):
        return r['change'][f]['mean']

    def rise(desc, r, inj, need_fraction=0.5):
        headroom = 100.0 - r['change']['gap_whole']['mean']
        room = min(inj, max(headroom, 0.0))
        got = seen(r, 'U')
        if headroom < 0.5 * inj:
            out.append((desc, None, f'INCONCLUSIVE: the machine was at {r["change"]["gap_whole"]["mean"]:.0f}% between the blocks, leaving {headroom:.1f} points of room for {inj:.1f} injected; '
                                    f'U rose {got:.1f}. Rerun on a quieter machine'))
        else:
            out.append((desc, got >= need_fraction * room, f'U rose {got:.1f} points for {inj:.1f} injected ({100 * got / inj:.0f}%; the machine had {headroom:.0f} points of room; need at least {need_fraction * room:.1f})'))
    r = by.get('sustained')
    if r:
        rise('sustained external CPU is seen by U', r, r['injected_machine_percent'])
    r = by.get('churn')
    if r:
        inj = r['injected_machine_percent']
        rise('a storm of short-lived processes is seen by U', r, inj)
        out.append(('NEGATIVE CONTROL (informational, decides nothing): the old per-process sum', True,
                    f'the summed external per-process counters rose {seen(r, "external_counted"):.1f} points for {inj:.1f} injected ({100 * seen(r, "external_counted") / inj:.0f}% of it; U saw {100 * seen(r, "U") / inj:.0f}%)'))
    r = by.get('bench')
    if r:
        one_cpu = 100.0 / r['cpus']
        out.append(("the benchmark's own CPU is measured (own rises by about one logical processor)", seen(r, 'own') >= 0.5 * one_cpu, f'own rose {seen(r, "own"):.1f} points (one processor is {one_cpu:.1f})'))
        out.append(("the benchmark's own CPU is subtracted (U does not follow it)", abs(seen(r, 'U')) <= 5.0, f'U changed {seen(r, "U"):+.1f} points while own rose {seen(r, "own"):.1f}'))
    r = by.get('bench+churn')
    if r:
        rise('with the benchmark busy, the storm is still seen by U', r, r['injected_machine_percent'])
        out.append(("...and the benchmark's own share does not change with the storm", abs(seen(r, 'own')) <= 2.0, f'own changed {seen(r, "own"):+.1f} points'))
    return out


def main(argv):
    if argv and argv[0] == '--probe':
        return probe(argv[1:])
    quick = '--quick' in argv
    blocks = int(argv[argv.index('--blocks') + 1]) if '--blocks' in argv else (3 if quick else 5)
    on, off = (6, 6) if quick else (10, 10)
    json_out = argv[argv.index('--json') + 1] if '--json' in argv else None
    import tempfile
    results = []
    with tempfile.TemporaryDirectory(prefix='load-regression-') as tmp:
        for name in ('sustained', 'churn', 'bench', 'bench+churn'):
            print(f'experiment {name} ({blocks} blocks of {on} s between {off} s gaps) ...', flush=True)
            r = experiment(name, blocks, on, off, tmp)
            results.append(r)
            print('  ' + json.dumps({k: r[k] for k in ('blocks_analysed', 'injected_machine_percent')} | {'change_mean': {f: v['mean'] for f, v in r['change'].items()}}), flush=True)
    v = verdicts(results)
    failed = [x for x in v if x[1] is False]
    inconclusive = [x for x in v if x[1] is None]
    print()
    for desc, ok, detail in v:
        print(f"{'ok ' if ok else '???' if ok is None else 'BAD'} {desc}: {detail}")
    print('all load-regression checks passed' if not failed and not inconclusive else
          f'{len(failed)} load-regression check(s) FAILED, {len(inconclusive)} inconclusive (a machine busier than the injected load leaves it no room to show)')
    if json_out:
        with open(json_out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(json.dumps({'experiments': [{k: x[k] for k in x if k != 'per_block'} | {'per_block': x['per_block']} for x in results], 'checks': [{'check': d, 'ok': o, 'detail': t} for d, o, t in v]}, indent=1) + '\n')
    return 1 if failed else 3 if inconclusive else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
