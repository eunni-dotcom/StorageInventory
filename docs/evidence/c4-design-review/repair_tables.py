"""C4 design repair: the tables of docs/v1.1-c4-design-repair.md, recomputed from the committed result files.

Usage:  python repair_tables.py <results dir>        (reads repair-*.jsonl and ranges/*.json)

Every figure of the repair document's rerun sections comes from here. Units: MiB = 2^20 B, MB = 10^6 B; bytes are exact.
"""
import json
import os
import re
import statistics as st
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

MIB = 1048576.0


def load(rd):
    runs = []
    for f in sorted(os.listdir(rd)):
        if f.startswith('repair-') and f.endswith('.jsonl'):
            for line in open(os.path.join(rd, f), encoding='utf-8'):
                if line.strip():
                    d = json.loads(line)
                    d['_plan'] = f[:-6]
                    runs.append(d)
    return runs


def guard(text):
    """The IMP-11 summary line written by the harness, as numbers."""
    if not text:
        return {}
    g = {}
    for key, rx in [('checks', r'checks (\d+)'), ('max_pending', r'max pending main growth at any check (\d+) B'),
                    ('max_interval', r'largest drop per 4,096 rows \(scaled from 16,384-row intervals\) (\d+) B'),
                    ('verif_main', r'during verification main grew (-?\d+) B'), ('verif_journal', r'journal (-?\d+) B; before COMMIT'),
                    ('pre_pending', r'before COMMIT: .*? pending (\d+) B'), ('commit_added', r'COMMIT added (-?\d+) B'),
                    ('difference', r'difference (-?\d+) B'), ('post_journal', r'\), journal (\d+) B'),
                    ('end_pending', r'end of rows: pending (\d+) B')]:
        m = re.search(rx, text)
        if m:
            g[key] = int(m.group(1))
    return g


def cell(label):
    return re.sub(r' r\d+$', '', label)


def main(rd):
    runs = load(rd)
    normal = [d for d in runs if 'Label' in d and d.get('Mode') == 'normal' and 'failed' not in d]
    print('## Cells (every normal run; timings informational: the machine was loaded)\n')
    print('| Plan | Cell | Runs | Library before | Peak journal (handle, before COMMIT) | Growth | New names | File rows/s (each run) | Median | T-IMPORT (s, each run) | Verification (s) | Other load (whole machine, mean) |')
    print('|---|---|---:|---:|---:|---:|---:|---|---:|---|---|---|')
    cells = {}
    for d in normal:
        cells.setdefault((d['_plan'], cell(d['Label'])), []).append(d)
    for (plan, label), ds in cells.items():
        fr = [d['FilesPerSecond'] for d in ds]
        secs = ' / '.join('%.1f' % d['ImportSeconds'] for d in ds)
        verif = ' / '.join('%.1f' % (d['PhaseVerificationMs'] / 1000) for d in ds)
        loads = ' / '.join('%.0f%%' % d['OtherLoadMeanPercent'] for d in ds)
        print(f"| {plan} | {label} | {len(ds)} | {ds[0]['LibraryBeforeBytes']/MIB:,.1f} MiB | {max(d['PeakJournalHandleBytes'] for d in ds):,} B ({max(d['PeakJournalHandleBytes'] for d in ds)/MIB:,.2f} MiB) "
              f"| {st.median(d['GrowthBytes'] for d in ds)/MIB:,.1f} MiB | {ds[0]['NewNames']:,} | {' / '.join(f'{x:,.0f}' for x in fr)} | {st.median(fr):,.0f} "
              f"| {secs} | {verif} | {loads} |")

    print('\n## Generator statistics (computed before each import from the parameters alone)\n')
    seen = set()
    for d in runs:
        g = d.get('Generated') or (d.get('crash') or {}).get('Generated')
        if g and g not in seen:
            seen.add(g)
            print(f"- `{d.get('Params')}`: {g.split('; generated ', 1)[-1]}")

    print('\n## IMP-11 instrument (probe build: page_count, page size, main and journal length through handles at every check)\n')
    print('| Run | Checks | Largest pending growth at a check | Pending at the end of rows | Main growth during verification | Pending at the final check (Λ_f) | Main growth during COMMIT | COMMIT − Λ_f | Journal growth during verification | Journal after COMMIT | Largest growth per 4,096 rows |')
    print('|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
    diffs = []
    for d in runs:
        if 'Label' not in d:
            continue
        g = guard(d.get('Guard'))
        if not g:
            continue
        if 'commit_added' in g:
            diffs.append(g['difference'])
        print(f"| {d['Label']} | {g.get('checks', '–')} | {g.get('max_pending', 0):,} B | {g.get('end_pending', 0):,} B | {g.get('verif_main', '–') if 'verif_main' not in g else format(g['verif_main'], ',') + ' B'} "
              f"| {format(g['pre_pending'], ',') + ' B' if 'pre_pending' in g else '– (no COMMIT)'} | {format(g['commit_added'], ',') + ' B' if 'commit_added' in g else '–'} "
              f"| {format(g['difference'], ',') + ' B' if 'difference' in g else '–'} | {format(g['verif_journal'], ',') + ' B' if 'verif_journal' in g else '–'} "
              f"| {format(g['post_journal'], ',') + ' B' if 'post_journal' in g else '–'} | {g.get('max_interval', 0):,} B |")
    print(f"\nCOMMIT − Λ_f over {len(diffs)} committed runs: min {min(diffs) if diffs else '–'} B, max {max(diffs) if diffs else '–'} B")

    print('\n## Cancellation and crash recovery\n')
    print('| Run | Journal at the stop | Cancel → rolled back | Main file after (before) | Next open after the kill | Main at the kill → after recovery | State; older snapshots | Recovery child environment |')
    print('|---|---:|---:|---|---:|---|---|---|')
    for d in runs:
        if d.get('Mode') == 'cancel':
            print(f"| {d['Label']} | {d['JournalAtStopBytes']:,} B ({d['JournalAtStopBytes']/MIB:.1f} MiB) | {d['CancelToReturnSeconds']:.3f} s | {d['LibraryAfterStopBytes']:,} B ({d['LibraryBeforeBytes']:,} B) | – | – | {d.get('Outcome')} | – |")
        elif 'crash' in d:
            c, r = d['crash'], d['recover'] or {}
            print(f"| {c['Label']} | {c['JournalAtStopBytes']:,} B ({c['JournalAtStopBytes']/MIB:.1f} MiB) | – | – | {r.get('OpenSeconds', float('nan')):.3f} s | {r.get('MainBeforeBytes', 0):,} B → {r.get('MainAfterBytes', 0):,} B (before: {c['LibraryBeforeBytes']:,} B) "
                  f"| {r.get('State')}; {'verified' if r.get('OlderSnapshotsVerify') is True else 'NOT verified' if r.get('OlderSnapshotsVerify') is None else 'FAILED'} | {json.dumps(d.get('recoverEnv'))} |")

    rg = os.path.join(rd, 'ranges')
    if os.path.isdir(rg):
        print('\n## PERF-15 (a) key-range attribution (journal_ranges.py)\n')
        print('| Journal | Layout | Target | Verdict | Records (checksum failures, images unlike the copy) | REMOTE | Levels over the SHARED cap | Name index per level: journalled / IN / ADJACENT / REMOTE / SHARED |')
        print('|---|---|---|---|---|---:|---|---|')
        for f in sorted(os.listdir(rg)):
            if not f.endswith('.json'):
                continue
            r = json.load(open(os.path.join(rg, f), encoding='utf-8'))
            ni = r['by_tree'].get('sqlite_autoindex_name_1', {})
            per = '; '.join(f"L{l}: {c.get('journalled', 0)} / {c.get('IN', 0)} / {c.get('ADJACENT', 0)} / {c.get('REMOTE', 0)} / {c.get('SHARED', 0)}" for l, c in ni.items())
            over = ', '.join(f"{t} L{l}: {n}" for t, l, n in r['levels_over_shared_cap']) or 'none'
            print(f"| {f[:-5]} | {r['dictionary_layout']} | {r['target_source']}{' (new)' if r['new_source'] else ''} | **{r['verdict']}** | {r['integrity']['records']:,} ({r['integrity']['checksum_failures']}, {r['integrity']['image_mismatch']}) | {r['remote_pages']:,} | {over} | {per} |")


if __name__ == '__main__':
    main(sys.argv[1])
