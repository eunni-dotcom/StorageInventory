"""C4 design final repair (C4DRR-M03): the benchmark load source of §15.4, as a stand-alone sampler (no elevation needed).

Usage:  python load_probe.py quiet <seconds> [--json <out.json>] [--series]
        python load_probe.py run [--json <out.json>] [--series] -- <command> [args ...]

  quiet  samples for <seconds> with nothing of the benchmark running but the probe; verdict QUIET or NOT QUIET (§15.4's quiet check)
  run    starts <command> inside the probe's job object (so it and every process it starts are the benchmark's own) and samples until
         it exits; verdict VALID or INVALID by the run rule (the harness applies the rule to the measured operation's collections only;
         this reference applies it to the whole command)

Method (§15.4 as repaired):
  validity source  \\Processor Information(_Total)\\% Processor Time, added with PdhAddEnglishCounterW (locale independent): derived from
                   every logical processor's idle time, so it counts every process however briefly it lives, and interrupt and
                   deferred-procedure time. Collected on a fixed 500 ms schedule (120 collections a minute; each value covers the
                   interval since the previous collection, so the samples tile the window)
  own              the cumulative user + kernel time of every process in the probe's job object (the probe, the command and every
                   process they start, exited ones included: JobObjectBasicAccountingInformation), as a share of the machine over
                   each interval (divided by the number of logical processors)
  other load U     whole machine - own, per collection. Quiet check: mean <= 5% and 95th percentile <= 15%. Run: mean <= 10% and 95th
                   percentile <= 25%
  diagnostics      (recorded, never deciding) \\Process(*)\\% Processor Time and \\Process(*)\\ID Process in the same query, paired by
                   position and keyed by (name, occurrence), normalised, _Total and Idle never counted, renumbered instances and
                   instances without a rate left out and counted; classes own (PIDs in the job), induced (System, PID 4, and MsMpEng)
                   and external; unattributed = U - counted induced - counted external, in CPU percent; also the whole-machine
                   counters \\Processor(_Total)\\% Processor Time and \\Processor Information(_Total)\\% Processor Utility and
                   GetSystemTimes, for comparison only; and the machine's % DPC Time plus % Interrupt Time (part of U: the
                   interrupt work of the benchmark's own I/O is charged to no process)
Only totals are written; no process name other than the two induced ones, no path, no command line.
"""
import ctypes
import json
import math
import os
import subprocess
import sys
import time
from ctypes import wintypes

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

pdh = ctypes.WinDLL('pdh')
k32 = ctypes.WinDLL('kernel32', use_last_error=True)
k32.CreateJobObjectW.restype = wintypes.HANDLE
k32.GetCurrentProcess.restype = wintypes.HANDLE
PDH_FMT_LONG = 0x00000100
PDH_FMT_DOUBLE = 0x00000200
PDH_FMT_NOCAP100 = 0x00008000
PDH_MORE_DATA = 0x800007D2
INTERVAL = 0.5
INDUCED = {'system', 'msmpeng'}
WHOLE = '\\Processor Information(_Total)\\% Processor Time'
QUIET = {'mean': 5.0, 'p95': 15.0}
RUN = {'mean': 10.0, 'p95': 25.0}


class Value(ctypes.Union):
    _fields_ = [('longValue', ctypes.c_long), ('doubleValue', ctypes.c_double), ('largeValue', ctypes.c_longlong)]


class FmtValue(ctypes.Structure):
    _fields_ = [('CStatus', wintypes.DWORD), ('v', Value)]


class Item(ctypes.Structure):
    _fields_ = [('szName', wintypes.LPWSTR), ('FmtValue', FmtValue)]


class Accounting(ctypes.Structure):
    _fields_ = [('TotalUserTime', ctypes.c_longlong), ('TotalKernelTime', ctypes.c_longlong),
                ('ThisPeriodTotalUserTime', ctypes.c_longlong), ('ThisPeriodTotalKernelTime', ctypes.c_longlong),
                ('TotalPageFaultCount', wintypes.DWORD), ('TotalProcesses', wintypes.DWORD), ('ActiveProcesses', wintypes.DWORD),
                ('TotalTerminatedProcesses', wintypes.DWORD)]


class FileTime(ctypes.Structure):
    _fields_ = [('lo', wintypes.DWORD), ('hi', wintypes.DWORD)]


def check(status, what):
    if status != 0:
        raise OSError(f'{what} failed: 0x{status & 0xFFFFFFFF:08X}')


def add(query, path):
    h = wintypes.HANDLE()
    check(pdh.PdhAddEnglishCounterW(query, path, 0, ctypes.byref(h)), 'PdhAddEnglishCounterW ' + path)
    return h


def array(counter, fmt):
    size = wintypes.DWORD(0)
    count = wintypes.DWORD(0)
    st = pdh.PdhGetFormattedCounterArrayW(counter, fmt, ctypes.byref(size), ctypes.byref(count), None) & 0xFFFFFFFF
    if st != PDH_MORE_DATA:
        check(st, 'PdhGetFormattedCounterArrayW (size)')
    buf = (ctypes.c_byte * size.value)()
    check(pdh.PdhGetFormattedCounterArrayW(counter, fmt, ctypes.byref(size), ctypes.byref(count), buf), 'PdhGetFormattedCounterArrayW')
    items = ctypes.cast(buf, ctypes.POINTER(Item))
    out = []
    for i in range(count.value):
        it = items[i]
        valid = it.FmtValue.CStatus in (0, 1)
        out.append((it.szName, it.FmtValue.v.doubleValue if fmt & PDH_FMT_DOUBLE else it.FmtValue.v.longValue, valid))
    return out


def single(counter):
    v = FmtValue()
    t = wintypes.DWORD()
    check(pdh.PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ctypes.byref(t), ctypes.byref(v)), 'PdhGetFormattedCounterValue')
    return v.v.doubleValue


def stats(xs):
    if not xs:
        return {'mean': 0.0, 'p95': 0.0, 'max': 0.0}
    s = sorted(xs)
    return {'mean': round(sum(s) / len(s), 2), 'p95': round(s[min(len(s) - 1, math.ceil(0.95 * len(s)) - 1)], 2), 'max': round(s[-1], 2)}


class Job:
    """A job object holding this probe; every process it starts inherits it."""

    def __init__(self):
        self.h = k32.CreateJobObjectW(None, None)
        if not self.h or not k32.AssignProcessToJobObject(wintypes.HANDLE(self.h), wintypes.HANDLE(k32.GetCurrentProcess())):
            raise OSError(f'job object: error {ctypes.get_last_error()}')

    def cpu_seconds(self):
        a = Accounting()
        if not k32.QueryInformationJobObject(wintypes.HANDLE(self.h), 1, ctypes.byref(a), ctypes.sizeof(a), None):
            raise OSError(f'QueryInformationJobObject: error {ctypes.get_last_error()}')
        return (a.TotalUserTime + a.TotalKernelTime) / 1e7, a.TotalProcesses

    def pids(self):
        n = 4096
        buf = (ctypes.c_byte * (8 + 8 * n))()
        if not k32.QueryInformationJobObject(wintypes.HANDLE(self.h), 3, buf, ctypes.sizeof(buf), None):
            return set()
        count = ctypes.cast(buf, ctypes.POINTER(wintypes.DWORD))[1]
        ids = ctypes.cast(ctypes.byref(buf, 8), ctypes.POINTER(ctypes.c_size_t))
        return {int(ids[i]) for i in range(count)}


def system_times():
    idle, kern, user = FileTime(), FileTime(), FileTime()
    k32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kern), ctypes.byref(user))
    f = lambda t: ((t.hi << 32) | t.lo) / 1e7
    return f(idle), f(kern) + f(user)          # kernel time includes idle time


def sample(job, stop, series=False):
    """Collects every 500 ms (fixed schedule) until stop() is true. Returns the per-collection records."""
    cpus = os.cpu_count()
    query = wintypes.HANDLE()
    check(pdh.PdhOpenQueryW(None, 0, ctypes.byref(query)), 'PdhOpenQueryW')
    whole = add(query, WHOLE)
    whole_processor = add(query, '\\Processor(_Total)\\% Processor Time')
    utility = add(query, '\\Processor Information(_Total)\\% Processor Utility')
    dpc = add(query, '\\Processor Information(_Total)\\% DPC Time')
    interrupt = add(query, '\\Processor Information(_Total)\\% Interrupt Time')
    cpu = add(query, '\\Process(*)\\% Processor Time')
    pid = add(query, '\\Process(*)\\ID Process')
    check(pdh.PdhCollectQueryData(query), 'PdhCollectQueryData')
    t_prev = time.perf_counter()
    own_prev, _ = job.cpu_seconds()
    st_prev = system_times()
    start = t_prev
    previous_pid = {}
    rows = []
    k = 0
    diag = {'renumbered_instance_samples_discarded': 0, 'new_process_samples_without_rate': 0, 'misaligned_collections': 0,
            'defender_engine_visible': False, 'processes_per_sample': []}
    while True:
        k += 1
        delay = start + k * INTERVAL - time.perf_counter()
        if delay > 0:
            time.sleep(delay)
        check(pdh.PdhCollectQueryData(query), 'PdhCollectQueryData')
        t = time.perf_counter()
        own_now, procs = job.cpu_seconds()
        st_now = system_times()
        dt = t - t_prev
        w = single(whole)
        own = 100.0 * (own_now - own_prev) / (dt * cpus)
        row = {'t': round(t - start, 3), 'wall': round(time.time(), 3), 'whole': round(w, 3), 'own': round(own, 3), 'U': round(w - own, 3),
               'whole_processor_object': round(single(whole_processor), 3), 'utility': round(single(utility), 3),
               'dpc_interrupt': round(single(dpc) + single(interrupt), 3)}
        busy = (st_now[1] - st_prev[1]) - (st_now[0] - st_prev[0])
        row['getsystemtimes'] = round(100.0 * busy / max(1e-9, st_now[1] - st_prev[1]), 3)
        own_pids = job.pids()
        ids = array(pid, PDH_FMT_LONG)
        values = array(cpu, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100)
        if len(ids) != len(values) or any(a[0] != b[0] for a, b in zip(ids, values)):
            diag['misaligned_collections'] += 1
            row.update({'external_counted': None, 'induced_counted': None, 'own_counted': None})
        else:
            occurrence, current_pid = {}, {}
            e = i = o = 0.0
            count = 0
            for (name, p, _), (_, value, valid) in zip(ids, values):
                if name in ('_Total', 'Idle'):
                    continue
                n = occurrence.get(name, 0)
                occurrence[name] = n + 1
                key = (name, n)
                current_pid[key] = p
                count += 1
                if not valid:
                    diag['new_process_samples_without_rate'] += 1
                    continue
                if key in previous_pid and previous_pid[key] != p:
                    diag['renumbered_instance_samples_discarded'] += 1
                    continue
                share = value / cpus
                base = name.lower()
                if base == 'msmpeng':
                    diag['defender_engine_visible'] = True
                if p in own_pids:
                    o += share
                elif base in INDUCED or p == 4:
                    i += share
                else:
                    e += share
            previous_pid = current_pid
            diag['processes_per_sample'].append(count)
            row.update({'external_counted': round(e, 3), 'induced_counted': round(i, 3), 'own_counted': round(o, 3),
                        'unattributed': round(w - own - e - i, 3)})
        row['job_processes'] = procs
        rows.append(row)
        t_prev, own_prev, st_prev = t, own_now, st_now
        if stop():
            break
    pdh.PdhCloseQuery(query)
    return rows, diag


def summarise(rows, diag, mode, cpus):
    def col(k):
        return [r[k] for r in rows if r.get(k) is not None]
    s = {'mode': mode, 'counter': WHOLE, 'interval_s': INTERVAL, 'collections': len(rows), 'logical_processors': cpus,
         'seconds': round(rows[-1]['t'], 2) if rows else 0.0,
         'other_load_U_percent': stats(col('U')), 'whole_machine_percent': stats(col('whole')), 'own_percent': stats(col('own')),
         'diagnostics': {
             'external_counted_percent': stats(col('external_counted')), 'induced_counted_percent': stats(col('induced_counted')),
             'own_counted_by_pid_percent': stats(col('own_counted')), 'unattributed_percent': stats(col('unattributed')),
             'processor_object_total_percent': stats(col('whole_processor_object')), 'processor_utility_percent': stats(col('utility')),
             'getsystemtimes_busy_percent': stats(col('getsystemtimes')),
             'dpc_and_interrupt_percent': stats(col('dpc_interrupt')),
             'renumbered_instance_samples_discarded': diag['renumbered_instance_samples_discarded'],
             'new_process_samples_without_rate': diag['new_process_samples_without_rate'],
             'misaligned_collections': diag['misaligned_collections'], 'defender_engine_visible': diag['defender_engine_visible'],
             'processes_per_sample': stats(diag['processes_per_sample']),
             'job_processes_total': rows[-1]['job_processes'] if rows else 0}}
    rule = QUIET if mode == 'quiet' else RUN
    u = s['other_load_U_percent']
    ok = u['mean'] <= rule['mean'] and u['p95'] <= rule['p95']
    s['rule'] = rule
    s['verdict'] = ('QUIET' if ok else 'NOT QUIET') if mode == 'quiet' else ('VALID' if ok else 'INVALID')
    return s


def main(argv):
    series = '--series' in argv
    json_out = argv[argv.index('--json') + 1] if '--json' in argv else None
    job = Job()
    cpus = os.cpu_count()
    if argv[0] == 'quiet':
        n = int(round(float(argv[1]) / INTERVAL))
        collected = []

        def stop():
            collected.append(1)
            return len(collected) >= n
        rows, diag = sample(job, stop, series)
        out = summarise(rows, diag, 'quiet', cpus)
    elif argv[0] == 'run':
        cmd = argv[argv.index('--') + 1:]
        proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        rows, diag = sample(job, lambda: proc.poll() is not None, series)
        out = summarise(rows, diag, 'run', cpus)
        out['command_exit_code'] = proc.returncode
    else:
        raise SystemExit('usage: load_probe.py quiet <seconds> | run -- <command>')
    if series:
        out['series'] = [{k: r.get(k) for k in ('t', 'wall', 'whole', 'own', 'U', 'external_counted', 'induced_counted', 'unattributed', 'dpc_interrupt')} for r in rows]
    text = json.dumps(out, indent=1)
    if json_out:
        with open(json_out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text + '\n')
    print(json.dumps({k: v for k, v in out.items() if k != 'series'}, indent=1))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
