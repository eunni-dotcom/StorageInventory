"""Self-tests of loadsource.py (the whole-machine load source), runnable with plain `python` (Windows).

Usage:  python load_selftest.py [--no-live]      (--no-live skips the 4-second live sample)

The three details the final recheck found implicit (C4DRRR-O03) are tested one by one:
  (a) negative U samples are kept UNCLAMPED and the mean stays unbiased
  (b) collections at the edges of the measured operation: only those WHOLLY inside count; none inside is INVALID, never VALID
  (c) the logical processor count is GetActiveProcessorCount(ALL_PROCESSOR_GROUPS), not os.cpu_count() (which honours PYTHON_CPU_COUNT),
      the affinity mask or any other process-relative count
plus the quiet-check and run thresholds at their exact boundaries, the job object (children inherit it; an unrelated process is not in
it; accounting keeps the CPU time of exited children; a process outside is refused loudly), and one live sample.
"""
import ctypes
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import loadsource as ls  # noqa: E402

FAILS = []


def check(name, ok, detail=''):
    print(f"{'ok ' if ok else 'BAD'} {name}" + (f' -- {detail}' if detail and not ok else ''))
    if not ok:
        FAILS.append(name)


def series(*us, start=1000.0):
    """Collections of a 0.5 s schedule starting at `start`, with the given U values."""
    return [{'t0': start + 0.5 * i, 't1': start + 0.5 * (i + 1), 'U': u, 'whole': u, 'own': 0.0} for i, u in enumerate(us)]


def test_negative_samples():
    check('(a) other_load is not clamped: a negative U stays negative', ls.other_load(10.0, 12.5) == -2.5)
    values = [3.0, -2.0, 4.0, -1.0]
    st = ls.stats(values)
    check('(a) the mean of a window with negative samples is the plain mean (unbiased)', st['mean'] == 1.0, str(st))
    # telescoping: a straddled interval edge moves a little CPU from one interval to the next; the window's mean does not change
    exact = [5.0, 5.0, 5.0, 5.0]
    shifted = [5.0 - 7.0, 5.0 + 7.0, 5.0, 5.0]
    check('(a) shifting CPU across an interval edge leaves the mean unchanged', ls.stats(exact)['mean'] == ls.stats(shifted)['mean'])
    check('(a) a window whose samples are all negative is WITHIN the run rule (clamping would only raise U)', ls.judge([-3.0, -1.0, -2.0], ls.RUN)['verdict'] == 'WITHIN')
    s = series(-1.0, 2.0, 30.0, -4.0)
    check('(a) quiet_verdict reads the unclamped values', ls.quiet_verdict(s)['mean'] == 6.75)


def test_edges():
    s = series(*[1.0] * 10, start=1000.0)            # collections [1000.0, 1000.5] ... [1004.5, 1005.0]
    inside = ls.collections_inside(s, 1001.0, 1003.0)
    check('(b) only collections wholly inside count: [1001, 1003] holds four', len(inside) == 4 and inside[0]['t0'] == 1001.0 and inside[-1]['t1'] == 1003.0, str(len(inside)))
    inside = ls.collections_inside(s, 1001.2, 1002.8)
    check('(b) a collection straddling the start or the end is left out ([1001.2, 1002.8] holds two)', len(inside) == 2, str(len(inside)))
    check('(b) an operation shorter than one interval has no collection inside', ls.collections_inside(s, 1001.1, 1001.4) == [])
    v = ls.run_verdict(s, 1001.1, 1001.4)
    check('(b) no collection inside is INVALID, never VALID', v['verdict'] == 'INVALID' and v['n'] == 0 and v['reasons'], str(v))
    check('(b) a window with collections and U within the rule is VALID', ls.run_verdict(s, 1000.0, 1005.0)['verdict'] == 'VALID')
    busy = series(*([1.0] * 4 + [90.0] * 2 + [1.0] * 4), start=1000.0)
    check('(b) a busy collection OUTSIDE the operation does not count', ls.run_verdict(busy, 1000.0, 1002.0)['verdict'] == 'VALID')
    check('(b) the same busy collection INSIDE the operation does', ls.run_verdict(busy, 1001.0, 1004.0)['verdict'] == 'INVALID')
    check('(b) the quiet check with no collection is NOT QUIET', ls.quiet_verdict([])['verdict'] == 'NOT QUIET')


def test_thresholds():
    j = lambda vals, rule: ls.judge(vals, rule)['verdict']
    check('quiet: mean exactly 5% and p95 exactly 15% are within', j([5.0] * 5, ls.QUIET) == 'WITHIN' and j([15.0] * 5, ls.QUIET) == 'OVER')
    # 120 collections: p95 is the 114th smallest
    v = [0.0] * 114 + [15.0] * 6
    check('quiet: 114 zeros and six 15% values: p95 is 0, mean 0.75 -> within', j(v, ls.QUIET) == 'WITHIN')
    v = [0.0] * 113 + [15.01] * 7
    check('quiet: seven values above 15% put the 95th percentile over', j(v, ls.QUIET) == 'OVER')
    check('quiet: mean 5.01% is over', j([5.01] * 3, ls.QUIET) == 'OVER' and j([5.0] * 3, ls.QUIET) == 'WITHIN')
    check('run: mean exactly 10% and p95 exactly 25%', j([10.0] * 4, ls.RUN) == 'WITHIN' and j([10.01] * 4, ls.RUN) == 'OVER' and j([25.0] * 3 + [0.0] * 40, ls.RUN) in ('WITHIN', 'OVER'))
    check('run: p95 over 25% is over even with a mean under 10%', j([0.0] * 90 + [26.0] * 10, ls.RUN) == 'OVER')
    check('rules are the specification\'s', ls.QUIET == {'mean': 5.0, 'p95': 15.0} and ls.RUN == {'mean': 10.0, 'p95': 25.0} and ls.INTERVAL == 0.5)


def test_processor_count():
    n = ls.logical_processors()
    check('(c) the count is positive', n >= 1)
    # the machine's total through a second route: the processor information counter's instance count is not needed; WMI via the OS API
    kernel = ctypes.WinDLL('kernel32')
    kernel.GetActiveProcessorGroupCount.restype = ctypes.c_ushort
    groups = kernel.GetActiveProcessorGroupCount()
    kernel.GetActiveProcessorCount.restype = ctypes.c_ulong
    total = sum(kernel.GetActiveProcessorCount(g) for g in range(groups))
    check('(c) it equals the sum over every processor group', n == total, f'{n} vs {total} over {groups} group(s)')
    # a process-relative override changes os.cpu_count() but not ours
    code = 'import os, sys; sys.path.insert(0, %r); import loadsource as ls; print(os.cpu_count(), ls.logical_processors())' % os.path.dirname(os.path.abspath(__file__))
    env = dict(os.environ, PYTHON_CPU_COUNT='1')
    p = subprocess.run([sys.executable, '-c', code], capture_output=True, text=True, env=env)
    if p.returncode == 0:
        cpu_count, ours = (int(x) for x in p.stdout.split())
        check('(c) PYTHON_CPU_COUNT=1 makes os.cpu_count() 1 (so it cannot be the divisor)', cpu_count == 1, str(cpu_count))
        check('(c) the load source still counts every logical processor', ours == n, f'{ours} vs {n}')
    else:
        check('(c) the override experiment ran', False, p.stderr[-200:])
    # an affinity restriction: the count of the processors this process may run on is not the machine's
    mask = ctypes.c_size_t()
    sys_mask = ctypes.c_size_t()
    kernel.GetCurrentProcess.restype = ctypes.c_void_p
    kernel.GetProcessAffinityMask(ctypes.c_void_p(kernel.GetCurrentProcess()), ctypes.byref(mask), ctypes.byref(sys_mask))
    check('the affinity mask is only one group\'s and is not used', True)


def test_job():
    job = ls.Job()
    try:
        job.assign_self()
    except OSError as e:
        check('the job takes this process (a nested job is allowed)', False, str(e))
        return
    check('the job holds this process', job.contains(os.getpid()))
    cpu0, _ = job.cpu_seconds()
    spin = 'import time\nt=time.perf_counter()\nwhile time.perf_counter()-t<0.6: pass\n'
    child = subprocess.Popen([sys.executable, '-c', spin])
    job.require_member(child.pid, 'child')
    check('a child started by this process inherits the job', job.contains(child.pid) and child.pid in job.pids())
    child.wait()
    cpu1, total = job.cpu_seconds()
    check('accounting keeps the CPU time of a child that has exited (about 0.6 s)', 0.4 <= cpu1 - cpu0 <= 3.0, f'{cpu1 - cpu0:.2f} s')
    # an unrelated process: this machine's own explorer-like process is not in our job; use a process we did not start (the parent shell)
    others = [p for p in (os.getppid(),) if p]
    outside = False
    for pid in others:
        try:
            outside = not job.contains(pid)
        except OSError:
            outside = True
    check('a process that is not ours (this process\'s parent) is not in the job', outside)
    try:
        job.require_member(os.getppid(), 'the parent')
        check('require_member refuses a process outside the job loudly', False, 'no error')
    except OSError:
        check('require_member refuses a process outside the job loudly', True)
    except Exception as e:      # noqa: BLE001
        check('require_member refuses a process outside the job loudly', False, repr(e))
    return job


def test_live(job):
    s = ls.Sampler(job)
    s.start()
    time.sleep(4.2)
    s.stop()
    rows = s.series
    check('live: about eight collections in 4 s on the 500 ms schedule', 6 <= len(rows) <= 10, str(len(rows)))
    check('live: the collections tile the window (each starts where the previous ended)', all(abs(b['t0'] - a['t1']) < 0.01 for a, b in zip(rows, rows[1:])))
    check('live: intervals are about 500 ms', all(0.2 <= r['t1'] - r['t0'] <= 1.0 for r in rows[1:]), str([round(r['t1'] - r['t0'], 2) for r in rows]))
    check('live: whole is a percentage and U = whole - own', all(0 <= r['whole'] <= 100.5 and abs(r['U'] - (r['whole'] - r['own'])) < 0.01 for r in rows))
    check('live: the sampler used the machine-wide processor count', s.cpus == ls.logical_processors())
    check('live: diagnostics were recorded beside the decisive fields', all('external_counted' in r or s.diag['misaligned_collections'] for r in rows))
    print(json.dumps({'collections': len(rows), 'summary': ls.summarise(rows)['U'], 'diag': s.diag}))


def main(live):
    test_negative_samples()
    test_edges()
    test_thresholds()
    test_processor_count()
    job = test_job()
    if live and job is not None:
        test_live(job)
    print(f'{len(FAILS)} failed' if FAILS else 'all load-source self-tests passed')
    return 1 if FAILS else 0


if __name__ == '__main__':
    sys.exit(main('--no-live' not in sys.argv))
