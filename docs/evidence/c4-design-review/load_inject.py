"""C4 design final repair (C4DRR-M03): controlled external CPU load for testing the load source, measured exactly.

Usage:  python load_inject.py sustained <processes> <seconds> [--json <out.json>]
        python load_inject.py churn <concurrent> <seconds> <work seconds> [--json <out.json>]
        python load_inject.py churn-cycles <concurrent> <cycles> <on seconds> <off seconds> <work seconds> [--json <out.json>]
        python load_inject.py busy <seconds> [--pause <seconds>]          (one CPU-bound process; the benchmark stand-in)

  sustained  <processes> CPU-bound processes that live for <seconds>
  churn      keeps <concurrent> short-lived CPU-bound processes running for <seconds>: each spins for <work seconds> and exits, and is
             replaced at once (the C4 design repair review's case: four of about 0.3 s each)
  busy       sleeps --pause seconds, spins for <seconds>, sleeps --pause seconds (used inside the probe's job as a benchmark run)
The injected CPU is measured exactly: every child's user + kernel time from its process handle after it exits (GetProcessTimes),
plus this launcher's own. Reports the on-window (wall-clock start and end) and the injected CPU as a share of the machine over it.
"""
import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
SPIN = 'import sys, time\nt = time.perf_counter()\nw = float(sys.argv[1])\nwhile time.perf_counter() - t < w:\n    pass\n'


class FileTime(ctypes.Structure):
    _fields_ = [('lo', wintypes.DWORD), ('hi', wintypes.DWORD)]


def cpu_of(proc):
    c, e, k, u = FileTime(), FileTime(), FileTime(), FileTime()
    if not k32.GetProcessTimes(wintypes.HANDLE(int(proc._handle)), ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u)):
        raise OSError(f'GetProcessTimes: error {ctypes.get_last_error()}')
    f = lambda t: ((t.hi << 32) | t.lo) / 1e7
    return f(k) + f(u)


def spawn(work):
    return subprocess.Popen([sys.executable, '-c', SPIN, str(work)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def main(argv):
    json_out = argv[argv.index('--json') + 1] if '--json' in argv else None
    mode = argv[0]
    if mode == 'busy':
        pause = float(argv[argv.index('--pause') + 1]) if '--pause' in argv else 0.0
        time.sleep(pause)
        t = time.perf_counter()
        while time.perf_counter() - t < float(argv[1]):
            pass
        time.sleep(pause)
        return 0
    own0 = time.process_time()
    start_wall, start = time.time(), time.perf_counter()
    done = []
    if mode == 'sustained':
        n, seconds = int(argv[1]), float(argv[2])
        procs = [spawn(seconds) for _ in range(n)]
        for p in procs:
            p.wait()
        done = procs
    elif mode == 'churn':
        n, seconds, work = int(argv[1]), float(argv[2]), float(argv[3])
        running = [spawn(work) for _ in range(n)]
        while running:
            time.sleep(0.005)
            for p in list(running):
                if p.poll() is not None:
                    running.remove(p)
                    done.append(p)
                    if time.perf_counter() - start < seconds:
                        running.append(spawn(work))
    elif mode == 'churn-cycles':     # alternating on and off blocks, so that background drift cancels in the comparison
        n, cycles, on_s, off_s, work = int(argv[1]), int(argv[2]), float(argv[3]), float(argv[4]), float(argv[5])
        blocks = []
        for _ in range(cycles):
            time.sleep(off_s)
            b0, b0_wall = time.perf_counter(), time.time()
            running = [spawn(work) for _ in range(n)]
            while running:
                time.sleep(0.005)
                for p in list(running):
                    if p.poll() is not None:
                        running.remove(p)
                        done.append(p)
                        if time.perf_counter() - b0 < on_s - work:      # no child outlives its block
                            running.append(spawn(work))
            blocks.append([round(b0_wall, 3), round(time.time(), 3)])
        time.sleep(off_s)
    else:
        raise SystemExit(__doc__)
    end_wall, end = time.time(), time.perf_counter()
    children = sum(cpu_of(p) for p in done)
    launcher = time.process_time() - own0
    window = end - start
    out = {'mode': mode, 'processes': len(done), 'on_wall_start': round(start_wall, 3), 'on_wall_end': round(end_wall, 3),
           'seconds': round(window, 3), 'children_cpu_seconds': round(children, 3), 'launcher_cpu_seconds': round(launcher, 3),
           'injected_machine_percent': round(100.0 * (children + launcher) / (window * os.cpu_count()), 2),
           'mean_process_life_cpu_seconds': round(children / max(1, len(done)), 3)}
    if mode == 'churn-cycles':
        on_time = sum(b - a for a, b in blocks)
        out.update({'blocks': blocks, 'on_seconds': round(on_time, 3),
                    'injected_machine_percent': round(100.0 * children / (on_time * os.cpu_count()), 2)})   # over the on blocks
    text = json.dumps(out, indent=1)
    if json_out:
        with open(json_out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text + '\n')
    print(text)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
