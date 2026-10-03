"""The benchmark's load source (§15.4 "Load source: the whole machine, less the benchmark's own time"; C4 design final repair C4DRR-M03).

Windows only, no elevation. The reference is docs/evidence/c4-design-review/load_probe.py, from which the PDH and job-object code is taken;
this module is the library the orchestrator (perf_session.py) uses, with the three details the final recheck (C4DRRR-O03) found implicit
made EXPLICIT and tested (load_selftest.py):

  (a) NEGATIVE SAMPLES.  A per-collection U = whole machine - own can be negative (the job's tick-granular CPU accounting and the idle-based
      machine counter can straddle an interval edge differently). Such values are kept UNCLAMPED: the per-collection errors telescope over a
      window, so the mean stays unbiased; clamping or dropping them would bias U upwards. See `other_load`.
  (b) THE OPERATION'S EDGES.  A run is judged on the collections WHOLLY INSIDE its measured operation: a collection whose interval starts
      before the operation began or ends after it returned is not used; an operation with no collection wholly inside has no verdict, and the
      run is INVALID (never VALID by default). See `collections_inside` and `judge`.
  (c) THE PROCESSOR COUNT.  "The number of logical processors" is the machine's ACTIVE count across ALL processor groups,
      GetActiveProcessorCount(ALL_PROCESSOR_GROUPS) - what \\Processor Information(_Total) spans - and never os.cpu_count() (which honours
      PYTHON_CPU_COUNT and -X cpu_count), os.process_cpu_count(), the affinity mask, a job CPU limit or DOTNET_PROCESSOR_COUNT: a
      process-relative count would inflate the own share and understate U. See `logical_processors`.

Method: one PDH query read without elevation on a fixed 500 ms schedule (120 collections a minute, each covering the interval since the
previous one, so the collections tile the window):
  whole   \\Processor Information(_Total)\\% Processor Time, added with PdhAddEnglishCounterW (locale independent)
  own     the cumulative user + kernel time of every process in a JOB OBJECT (JobObjectBasicAccountingInformation, which keeps the time of
          processes that have exited), as a share of the machine over the interval (divided by the logical processors)
  U       whole - own. U ALONE decides validity. Quiet check: mean <= 5% and 95th percentile <= 15%; run: mean <= 10% and p95 <= 25%.
Diagnostics, recorded and never deciding: \\Process(*) counters paired by position (own / induced = System and MsMpEng / external), the
unattributed share, DPC and interrupt time, \\Processor(_Total), GetSystemTimes and \\Processor Utility. The summed per-process 'external'
share is the OLD method; it is kept only as the labelled negative control of load_regression.py.

Every benchmark-owned process must be in the job: the orchestrator is assigned to it (`Job.assign_self`) so its children inherit it, and
`Job.require_member` fails loudly for any child that is not in it. A failed assignment or accounting query raises; nothing is skipped.
"""
import ctypes
import math
import sys
import threading
import time
from ctypes import wintypes

pdh = ctypes.WinDLL('pdh')
k32 = ctypes.WinDLL('kernel32', use_last_error=True)
k32.CreateJobObjectW.restype = wintypes.HANDLE
k32.GetCurrentProcess.restype = wintypes.HANDLE
k32.OpenProcess.restype = wintypes.HANDLE
k32.GetActiveProcessorCount.restype = wintypes.DWORD
k32.GetActiveProcessorCount.argtypes = [wintypes.WORD]

ALL_PROCESSOR_GROUPS = 0xFFFF
PDH_FMT_LONG = 0x00000100
PDH_FMT_DOUBLE = 0x00000200
PDH_FMT_NOCAP100 = 0x00008000
PDH_MORE_DATA = 0x800007D2
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
INTERVAL = 0.5
INDUCED = {'system', 'msmpeng'}
WHOLE = '\\Processor Information(_Total)\\% Processor Time'
QUIET = {'mean': 5.0, 'p95': 15.0}
RUN = {'mean': 10.0, 'p95': 25.0}


# ---------------------------------------------------------------------------------------------------------------- pure functions
def logical_processors():
    """(c): the machine's active logical processors across ALL processor groups. Raises if Windows cannot say."""
    n = k32.GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)
    if n <= 0:
        raise OSError(f'GetActiveProcessorCount(ALL_PROCESSOR_GROUPS) failed: error {ctypes.get_last_error()}')
    return int(n)


def other_load(whole, own):
    """(a): U = whole machine - own, in percent of the machine, UNCLAMPED (it may be negative)."""
    return whole - own


def collections_inside(series, start, end):
    """(b): the collections wholly inside [start, end] (wall-clock seconds): interval start >= start and interval end <= end."""
    return [c for c in series if c['t0'] >= start and c['t1'] <= end]


def stats(values):
    """Mean, 95th percentile (nearest rank, the reference's definition), maximum and count."""
    if not values:
        return {'mean': None, 'p95': None, 'max': None, 'n': 0}
    s = sorted(values)
    return {'mean': round(sum(s) / len(s), 4), 'p95': round(s[min(len(s) - 1, math.ceil(0.95 * len(s)) - 1)], 4), 'max': round(s[-1], 4), 'n': len(s)}


def judge(u_values, rule):
    """The verdict of a window: {'verdict': 'WITHIN' | 'OVER' | 'NONE', 'mean', 'p95', 'n', 'reasons'}.
    NONE (no collection) is never WITHIN: the caller treats it as INVALID (b)."""
    st = stats(u_values)
    if st['n'] == 0:
        return {**st, 'verdict': 'NONE', 'reasons': ['no collection lies wholly inside the window']}
    reasons = []
    if st['mean'] > rule['mean']:
        reasons.append(f"U mean {st['mean']:.2f}% exceeds {rule['mean']}%")
    if st['p95'] > rule['p95']:
        reasons.append(f"U 95th percentile {st['p95']:.2f}% exceeds {rule['p95']}%")
    return {**st, 'verdict': 'OVER' if reasons else 'WITHIN', 'reasons': reasons}


def quiet_verdict(series):
    """The quiet check over a window of collections: QUIET or NOT QUIET (never QUIET without collections)."""
    j = judge([c['U'] for c in series], QUIET)
    return {**j, 'verdict': 'QUIET' if j['verdict'] == 'WITHIN' else 'NOT QUIET', 'rule': QUIET}


def run_verdict(series, start, end):
    """The run rule over the collections wholly inside the measured operation: VALID, INVALID (over the rule) or INVALID (none inside)."""
    inside = collections_inside(series, start, end)
    j = judge([c['U'] for c in inside], RUN)
    return {**j, 'verdict': 'VALID' if j['verdict'] == 'WITHIN' else 'INVALID', 'rule': RUN, 'window': [start, end]}


# ---------------------------------------------------------------------------------------------------------------- Windows
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


def _check(status, what):
    if status != 0:
        raise OSError(f'{what} failed: 0x{status & 0xFFFFFFFF:08X}')


def _add(query, path):
    h = wintypes.HANDLE()
    _check(pdh.PdhAddEnglishCounterW(query, path, 0, ctypes.byref(h)), 'PdhAddEnglishCounterW ' + path)
    return h


def _array(counter, fmt):
    size = wintypes.DWORD(0)
    count = wintypes.DWORD(0)
    st = pdh.PdhGetFormattedCounterArrayW(counter, fmt, ctypes.byref(size), ctypes.byref(count), None) & 0xFFFFFFFF
    if st != PDH_MORE_DATA:
        _check(st, 'PdhGetFormattedCounterArrayW (size)')
    buf = (ctypes.c_byte * size.value)()
    _check(pdh.PdhGetFormattedCounterArrayW(counter, fmt, ctypes.byref(size), ctypes.byref(count), buf), 'PdhGetFormattedCounterArrayW')
    items = ctypes.cast(buf, ctypes.POINTER(Item))
    out = []
    for i in range(count.value):
        it = items[i]
        out.append((it.szName, it.FmtValue.v.doubleValue if fmt & PDH_FMT_DOUBLE else it.FmtValue.v.longValue, it.FmtValue.CStatus in (0, 1)))
    return out


def _single(counter):
    v = FmtValue()
    t = wintypes.DWORD()
    _check(pdh.PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ctypes.byref(t), ctypes.byref(v)), 'PdhGetFormattedCounterValue')
    return v.v.doubleValue


def _system_times():
    idle, kern, user = FileTime(), FileTime(), FileTime()
    k32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kern), ctypes.byref(user))
    f = lambda t: ((t.hi << 32) | t.lo) / 1e7
    return f(idle), f(kern) + f(user)          # kernel time includes idle time


class Job:
    """A job object that holds the benchmark: this orchestrator (assigned at creation) and every process it starts (they inherit it; no
    breakaway limit is set). Created and used unelevated. A failed assignment or query raises."""

    def __init__(self):
        self.h = k32.CreateJobObjectW(None, None)
        if not self.h:
            raise OSError(f'CreateJobObjectW failed: error {ctypes.get_last_error()}')
        self.assigned_self = False

    def assign_self(self):
        if not k32.AssignProcessToJobObject(wintypes.HANDLE(self.h), wintypes.HANDLE(k32.GetCurrentProcess())):
            raise OSError(f'AssignProcessToJobObject(self) failed: error {ctypes.get_last_error()} (a job that forbids nesting makes this fail; '
                          'run the session outside it)')
        self.assigned_self = True

    def assign_pid(self, pid):
        """Assigns a process (by id) to the job, for one that did not inherit it. Raises on failure."""
        h = k32.OpenProcess(0x0100 | PROCESS_QUERY_LIMITED_INFORMATION, False, pid)      # PROCESS_SET_QUOTA | query
        if not h:
            raise OSError(f'OpenProcess({pid}) failed: error {ctypes.get_last_error()}')
        try:
            if not k32.AssignProcessToJobObject(wintypes.HANDLE(self.h), wintypes.HANDLE(h)):
                raise OSError(f'AssignProcessToJobObject({pid}) failed: error {ctypes.get_last_error()}')
        finally:
            k32.CloseHandle(h)

    def cpu_seconds(self):
        a = Accounting()
        if not k32.QueryInformationJobObject(wintypes.HANDLE(self.h), 1, ctypes.byref(a), ctypes.sizeof(a), None):
            raise OSError(f'QueryInformationJobObject(accounting) failed: error {ctypes.get_last_error()}')
        return (a.TotalUserTime + a.TotalKernelTime) / 1e7, a.TotalProcesses

    def pids(self):
        n = 4096
        buf = (ctypes.c_byte * (8 + 8 * n))()
        if not k32.QueryInformationJobObject(wintypes.HANDLE(self.h), 3, buf, ctypes.sizeof(buf), None):
            raise OSError(f'QueryInformationJobObject(process list) failed: error {ctypes.get_last_error()}')
        count = ctypes.cast(buf, ctypes.POINTER(wintypes.DWORD))[1]
        ids = ctypes.cast(ctypes.byref(buf, 8), ctypes.POINTER(ctypes.c_size_t))
        return {int(ids[i]) for i in range(count)}

    def contains(self, pid):
        """Whether a (running) process is in this job."""
        h = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not h:
            raise OSError(f'OpenProcess({pid}) failed: error {ctypes.get_last_error()}')
        try:
            result = wintypes.BOOL()
            if not k32.IsProcessInJob(wintypes.HANDLE(h), wintypes.HANDLE(self.h), ctypes.byref(result)):
                raise OSError(f'IsProcessInJob({pid}) failed: error {ctypes.get_last_error()}')
            return bool(result.value)
        finally:
            k32.CloseHandle(h)

    def require_member(self, pid, what):
        """Fails loudly when a benchmark-owned process is not in the job (its time would be counted as other load)."""
        if not self.contains(pid):
            raise OSError(f'{what} (pid {pid}) is not in the benchmark job: its CPU time would be counted as other load')


class Sampler:
    """Collects on a fixed 500 ms schedule in a thread until stopped; `series` holds one record per collection:
    t0 and t1 (the wall-clock seconds the interval covers), whole, own, U (unclamped) and the diagnostics."""

    def __init__(self, job, interval=INTERVAL):
        self.job = job
        self.interval = interval
        self.cpus = logical_processors()
        self.series = []
        self.diag = {'renumbered_instance_samples_discarded': 0, 'new_process_samples_without_rate': 0, 'misaligned_collections': 0,
                     'defender_engine_visible': False, 'diagnostic_errors': 0}
        self.error = None
        self._stop = threading.Event()
        self._thread = None
        self._ready = threading.Event()

    def start(self):
        self._thread = threading.Thread(target=self._run, name='load-sampler', daemon=True)
        self._thread.start()
        if not self._ready.wait(10):
            raise OSError('the load sampler did not start: ' + str(self.error))
        if self.error:
            raise OSError('the load sampler failed to start: ' + str(self.error))

    def stop(self):
        self._stop.set()
        if self._thread:
            self._thread.join(10)
        if self.error:
            raise OSError('the load sampler failed: ' + str(self.error))

    def check(self):
        """Raises when the sampler thread has failed (the orchestrator calls this between steps: a dead sampler is never silent)."""
        if self.error:
            raise OSError('the load sampler failed: ' + str(self.error))

    def wait_until(self, wall_time, timeout=3.0):
        """Waits until a collection ending at or after `wall_time` exists (so the window of an operation that just ended is complete)."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            self.check()
            if self.series and self.series[-1]['t1'] >= wall_time:
                return True
            time.sleep(0.05)
        return False

    def window(self, start, end):
        return collections_inside(list(self.series), start, end)

    def _run(self):
        try:
            self._collect()
        except BaseException as e:      # noqa: BLE001 - recorded, raised by check()/stop(); never swallowed
            self.error = f'{type(e).__name__}: {e}'
            self._ready.set()

    def _collect(self):
        cpus = self.cpus
        query = wintypes.HANDLE()
        _check(pdh.PdhOpenQueryW(None, 0, ctypes.byref(query)), 'PdhOpenQueryW')
        whole = _add(query, WHOLE)
        whole_processor = _add(query, '\\Processor(_Total)\\% Processor Time')
        utility = _add(query, '\\Processor Information(_Total)\\% Processor Utility')
        dpc = _add(query, '\\Processor Information(_Total)\\% DPC Time')
        interrupt = _add(query, '\\Processor Information(_Total)\\% Interrupt Time')
        cpu = _add(query, '\\Process(*)\\% Processor Time')
        pid = _add(query, '\\Process(*)\\ID Process')
        _check(pdh.PdhCollectQueryData(query), 'PdhCollectQueryData')
        t_prev = time.perf_counter()
        wall_prev = time.time()
        own_prev, _ = self.job.cpu_seconds()
        st_prev = _system_times()
        start = t_prev
        previous_pid = {}
        self._ready.set()
        k = 0
        while not self._stop.is_set():
            k += 1
            delay = start + k * self.interval - time.perf_counter()
            if delay > 0 and self._stop.wait(delay):
                break
            _check(pdh.PdhCollectQueryData(query), 'PdhCollectQueryData')
            t = time.perf_counter()
            wall = time.time()
            own_now, procs = self.job.cpu_seconds()
            st_now = _system_times()
            dt = t - t_prev
            w = _single(whole)
            own = 100.0 * (own_now - own_prev) / (dt * cpus)
            row = {'t0': round(wall_prev, 3), 't1': round(wall, 3), 'whole': round(w, 3), 'own': round(own, 3), 'U': round(other_load(w, own), 3), 'job_processes': procs}
            try:
                busy = (st_now[1] - st_prev[1]) - (st_now[0] - st_prev[0])
                row.update({'whole_processor_object': round(_single(whole_processor), 3), 'utility': round(_single(utility), 3),
                            'dpc_interrupt': round(_single(dpc) + _single(interrupt), 3),
                            'getsystemtimes': round(100.0 * busy / max(1e-9, st_now[1] - st_prev[1]), 3)})
                own_pids = self.job.pids()
                ids = _array(pid, PDH_FMT_LONG)
                values = _array(cpu, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100)
                if len(ids) != len(values) or any(a[0] != b[0] for a, b in zip(ids, values)):
                    self.diag['misaligned_collections'] += 1
                else:
                    occurrence, current = {}, {}
                    e = i = o = 0.0
                    for (name, p, _), (_, value, valid) in zip(ids, values):
                        if name in ('_Total', 'Idle'):
                            continue
                        n = occurrence.get(name, 0)
                        occurrence[name] = n + 1
                        key = (name, n)
                        current[key] = p
                        if not valid:
                            self.diag['new_process_samples_without_rate'] += 1
                            continue
                        if key in previous_pid and previous_pid[key] != p:
                            self.diag['renumbered_instance_samples_discarded'] += 1
                            continue
                        share = value / cpus
                        base = name.lower()
                        if base == 'msmpeng':
                            self.diag['defender_engine_visible'] = True
                        if p in own_pids:
                            o += share
                        elif base in INDUCED or p == 4:
                            i += share
                        else:
                            e += share
                    previous_pid = current
                    row.update({'external_counted': round(e, 3), 'induced_counted': round(i, 3), 'own_counted': round(o, 3),
                                'unattributed': round(w - own - e - i, 3)})
            except OSError:
                self.diag['diagnostic_errors'] += 1      # diagnostics never decide anything; the decisive fields above were read first
            self.series.append(row)
            t_prev, wall_prev, own_prev, st_prev = t, wall, own_now, st_now
        pdh.PdhCloseQuery(query)


def summarise(series):
    """Totals of a window of collections for the records and the report."""
    def col(k):
        return [r[k] for r in series if r.get(k) is not None]
    return {'collections': len(series), 'U': stats(col('U')), 'whole': stats(col('whole')), 'own': stats(col('own')),
            'external_counted': stats(col('external_counted')), 'induced_counted': stats(col('induced_counted')),
            'unattributed': stats(col('unattributed')), 'dpc_interrupt': stats(col('dpc_interrupt'))}


if __name__ == '__main__':
    print('loadsource is a library; see perf_session.py and load_regression.py', file=sys.stderr)
    sys.exit(2)
