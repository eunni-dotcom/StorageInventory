"""C4 design repair (erratum E3): the measured imports of the C4 design review that overlapped one of its idle-priority builds.

Usage:  python overlap.py results

The six build windows (UTC, 2026-10-02) of the design session, with their provenance. Four are the modification time of a
surviving build log minus the elapsed time it prints; two have no surviving log and come from the session's own overlap
script, which is the only record of them.
"""
import datetime as dt
import glob
import json
import os
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

WINDOWS = [
    ('15:57:42', '15:57:52', 'no surviving build log (the session overlap script only)'),
    ('15:58:24', '15:58:42', 'probe-build.txt: written 15:58:42.6, "Time Elapsed 00:00:17.51"'),
    ('16:05:01', '16:05:21', 'probe-build2.txt: written 16:05:20.8, "Time Elapsed 00:00:18.63"'),
    ('16:23:42', '16:24:11', 'pristine2-build.txt: written 16:24:10.5, "Time Elapsed 00:00:27.01"'),
    ('16:28:22', '16:28:52', 'no surviving build log (the session overlap script only)'),
    ('16:31:46', '16:32:15', 'bin-pristine3-build.txt (16:32:00.5, 13.56 s) and bin-probes3-build.txt (16:32:14.0, 12.89 s), one window'),
]


def main(rd):
    wins = [(dt.datetime.fromisoformat('2026-10-02T' + a), dt.datetime.fromisoformat('2026-10-02T' + b), why) for a, b, why in WINDOWS]
    print('| Build window (UTC) | Provenance |')
    print('|---|---|')
    for a, b, why in wins:
        print(f'| {a:%H:%M:%S} to {b:%H:%M:%S} | {why} |')
    print()
    print('| Result file | Run | Overlap |')
    print('|---|---|---:|')
    hits = []
    for f in sorted(glob.glob(os.path.join(rd, '*.jsonl'))):
        for line in open(f, encoding='utf-8'):
            d = json.loads(line)
            if 'StartedUtc' not in d:
                continue
            s = dt.datetime.fromisoformat(d['StartedUtc'][:26])
            e = s + dt.timedelta(seconds=max(d['ImportSeconds'], 1))
            for a, b, _ in wins:
                if s < b and e > a:
                    hits.append((os.path.basename(f), d['Label'], (min(b, e) - max(a, s)).total_seconds()))
    for f, label, sec in hits:
        print(f'| {f} | {label} | {sec:.1f} s |')
    print(f'\n{len(hits)} overlapped runs')


if __name__ == '__main__':
    main(sys.argv[1])
