"""C4 design repair: the benchmark load source of the repaired §15.4, as a stand-alone sampler (no elevation needed).

Usage:  python load_probe.py <seconds> [--exclude-pid <pid> ...] [--exclude-name <process name> ...] [--json <out.json>]

Method (the one §15.4 names; the harness of the C4 repair implements the same):
  counters      \\Process(*)\\% Processor Time and \\Process(*)\\ID Process, added with PdhAddEnglishCounterW (locale independent),
                and \\Processor(_Total)\\% Processor Time for the whole machine; collected together every 500 ms
  normalisation a process's % Processor Time is a share of ONE logical processor; it is divided by the number of logical processors,
                so 100% means the whole machine
  instances     _Total and Idle are never counted; every other instance is attributed by its ID Process value read in the same
                collection; an instance whose PID differs from the previous sample's (PDH renumbers "name#n" instances when processes
                exit) is not counted in that sample and is reported
  classes       own: the benchmark's processes (by PID, or by name for this probe); induced: System (PID 4) and MsMpEng (Microsoft
                Defender's engine), the processes the benchmark's own file I/O makes work; external: everything else
  outputs       per sample the external, induced and whole-machine shares; mean, 95th percentile and maximum of each; the number of
                instances and of PIDs seen, renumbered instances discarded, and whether MsMpEng was visible

Only totals and the names of the two induced processes are written; no other process name is recorded.
"""
import ctypes
import json
import math
import os
import sys
import time
from ctypes import wintypes

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

pdh = ctypes.WinDLL('pdh')
PDH_FMT_LONG = 0x00000100
PDH_FMT_DOUBLE = 0x00000200
PDH_FMT_NOCAP100 = 0x00008000
PDH_MORE_DATA = 0x800007D2
PDH_CSTATUS_VALID_DATA = 0
PDH_CSTATUS_NEW_DATA = 1
INTERVAL = 0.5
INDUCED = {'system', 'msmpeng'}


class Value(ctypes.Union):
    _fields_ = [('longValue', ctypes.c_long), ('doubleValue', ctypes.c_double), ('largeValue', ctypes.c_longlong)]


class FmtValue(ctypes.Structure):
    _fields_ = [('CStatus', wintypes.DWORD), ('v', Value)]


class Item(ctypes.Structure):
    _fields_ = [('szName', wintypes.LPWSTR), ('FmtValue', FmtValue)]


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
        valid = it.FmtValue.CStatus in (PDH_CSTATUS_VALID_DATA, PDH_CSTATUS_NEW_DATA)
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


def sample(seconds, exclude_pids, exclude_names):
    cpus = os.cpu_count()
    query = wintypes.HANDLE()
    check(pdh.PdhOpenQueryW(None, 0, ctypes.byref(query)), 'PdhOpenQueryW')
    cpu = add(query, '\\Process(*)\\% Processor Time')
    pid = add(query, '\\Process(*)\\ID Process')
    total = add(query, '\\Processor(_Total)\\% Processor Time')
    check(pdh.PdhCollectQueryData(query), 'PdhCollectQueryData')
    previous_pid = {}
    external, induced, own, machine, processes = [], [], [], [], []
    pids_seen = set()
    renumbered = 0
    misaligned = 0
    no_rate = 0
    defender_visible = False
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        time.sleep(INTERVAL)
        check(pdh.PdhCollectQueryData(query), 'PdhCollectQueryData')
        # The two wildcard counters of one collection enumerate the Process object's instances in the same order, and instances
        # of one executable share a name (svchost, svchost, ...): pair them by position and key each by (name, occurrence)
        ids = array(pid, PDH_FMT_LONG)
        values = array(cpu, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100)
        if len(ids) != len(values) or any(a[0] != b[0] for a, b in zip(ids, values)):
            misaligned += 1
            continue
        occurrence = {}
        current_pid = {}
        e = i = o = 0.0
        count = 0
        invalid_here = 0
        for (name, p, _), (_, value, valid) in zip(ids, values):
            if name in ('_Total', 'Idle'):
                continue
            k = occurrence.get(name, 0)
            occurrence[name] = k + 1
            key = (name, k)
            current_pid[key] = p
            count += 1
            pids_seen.add(p)
            if not valid:   # a process that started since the previous collection has no rate yet
                invalid_here += 1
                continue
            if key in previous_pid and previous_pid[key] != p:
                renumbered += 1   # PDH paired this rate with another process's earlier raw value
                continue
            share = value / cpus
            base = name.lower()
            if base == 'msmpeng':
                defender_visible = True
            if p in exclude_pids or base in exclude_names:
                o += share
            elif base in INDUCED or p == 4:
                i += share
            else:
                e += share
        previous_pid = current_pid
        no_rate += invalid_here
        processes.append(count)
        external.append(e)
        induced.append(i)
        own.append(o)
        machine.append(single(total))
    pdh.PdhCloseQuery(query)
    return {
        'seconds': seconds, 'interval_s': INTERVAL, 'samples': len(external), 'logical_processors': cpus,
        'processes_per_sample': stats(processes), 'pids_seen': len(pids_seen),
        'renumbered_instance_samples_discarded': renumbered, 'new_process_samples_without_rate': no_rate,
        'misaligned_collections_discarded': misaligned,
        'defender_engine_visible': defender_visible,
        'external_percent': stats(external), 'induced_percent': stats(induced), 'own_percent': stats(own),
        'external_plus_induced_percent': stats([a + b for a, b in zip(external, induced)]),
        'whole_machine_percent': stats(machine),
    }


if __name__ == '__main__':
    a = sys.argv[1:]
    secs = float(a[0])
    ex_pids = {int(a[k + 1]) for k in range(len(a)) if a[k] == '--exclude-pid'}
    ex_names = {a[k + 1].lower() for k in range(len(a)) if a[k] == '--exclude-name'}
    ex_pids.add(os.getpid())
    result = sample(secs, ex_pids, ex_names)
    text = json.dumps(result, indent=1)
    if '--json' in a:
        with open(a[a.index('--json') + 1], 'w', encoding='utf-8') as f:
            f.write(text + '\n')
    print(text)
