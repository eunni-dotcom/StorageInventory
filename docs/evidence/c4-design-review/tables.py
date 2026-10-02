"""Summarise the design-review benchmark JSONL files into markdown tables (C4 design review).
Usage: python tables.py <results dir> [plan ...]  -> prints markdown. Every figure in docs/v1.1-c4-design-review.md comes from here."""
import json, sys, os, re, statistics as st
from collections import OrderedDict

# C4 design repair: print UTF-8 whatever the console code page (the Windows default cp1252 cannot encode the tables' arrows
# and dashes: ERRATUM.md, E6), and label sizes MiB, which they are (bytes / 2^20: E5)
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

def load(path):
    rows = []
    for line in open(path, encoding='utf-8'):
        line = line.strip()
        if not line:
            continue
        d = json.loads(line)
        rows.append(d)
    return rows

def cell_key(label):
    # strip the run suffix " r1" etc.
    return re.sub(r' r\d+$', '', label)

MB = 1048576.0

def fmt_k(x):
    return f"{x/1000:,.1f}k"

def per_run_table(rows, title):
    out = [f"\n#### {title}\n",
           "| Run | Library before | Snapshot growth | Peak journal (handle) | J / growth | J / Library | File rows/s (BEGIN→COMMIT) | Insertion rows/s | Duration | Peak WS above baseline | New names | Other load mean / p95 |",
           "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for d in rows:
        if 'failed' in d:
            out.append(f"| {d['Label']} | FAILED ({d['failed']}) |||||||||||")
            continue
        if 'crash' in d:
            continue
        ws = (d['PeakWorkingSetBytes'] - d['WorkingSetBeforeBytes']) / MB
        out.append(f"| {d['Label']} | {d['LibraryBeforeBytes']/MB:,.1f} MiB | {d['GrowthBytes']/MB:,.1f} MiB | {d['PeakJournalHandleBytes']/MB:,.2f} MiB | {100*d['JournalOverGrowth']:.1f}% | {100*d['JournalOverLibraryBefore']:.1f}% | {d['FilesPerSecond']:,.0f} | {d['InsertionFilesPerSecond']:,.0f} | {d['ImportSeconds']:.2f} s | {ws:,.0f} MiB | {d['NewNames']:,} | {d['OtherLoadMeanPercent']:.0f}% / {d['OtherLoadP95Percent']:.0f}% |")
    return "\n".join(out)

def cell_table(rows, title):
    cells = OrderedDict()
    for d in rows:
        if 'failed' in d or 'crash' in d or d.get('Mode') == 'cancel':
            continue
        cells.setdefault(cell_key(d['Label']), []).append(d)
    out = [f"\n#### {title}\n",
           "| Cell | Runs | Library before | Growth | Peak journal | J / growth | J / Library | File rows/s median (min–max) | Duration median | Peak WS above baseline (max) | New names | Other load mean (range) |",
           "|---|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---|"]
    for k, ds in cells.items():
        fr = [d['FilesPerSecond'] for d in ds]
        du = [d['ImportSeconds'] for d in ds]
        j = [d['PeakJournalHandleBytes'] for d in ds]
        g = [d['GrowthBytes'] for d in ds]
        L = [d['LibraryBeforeBytes'] for d in ds]
        ws = [(d['PeakWorkingSetBytes'] - d['WorkingSetBeforeBytes']) / MB for d in ds]
        ld = [d['OtherLoadMeanPercent'] for d in ds]
        jm = max(j)
        out.append(f"| {k} | {len(ds)} | {st.median(L)/MB:,.1f} MiB | {st.median(g)/MB:,.1f} MiB | {jm/MB:,.2f} MiB{'' if min(j)==max(j) else ' (min '+format(min(j)/MB,',.2f')+')'} | {100*jm/st.median(g):.1f}% | {100*jm/st.median(L):.1f}% | {st.median(fr):,.0f} ({min(fr):,.0f}–{max(fr):,.0f}) | {st.median(du):.2f} s | {max(ws):,.0f} MiB | {st.median([d['NewNames'] for d in ds]):,.0f} | {st.median(ld):.0f}% ({min(ld):.0f}–{max(ld):.0f}%) |")
    return "\n".join(out)

def analysis_table(rows, title):
    out = [f"\n#### {title}\n",
           "| Run | Journal | Segments (synced) | Distinct pages journalled | Bytes per page | Name index pages journalled / total | Other B-trees journalled (pages) |",
           "|---|---:|---:|---:|---:|---:|---|"]
    for d in rows:
        a = d.get('Analysis')
        if not a:
            continue
        m = re.search(r'journal (\d+) B, (\d+) segment\(s\) \((\d+) synced\), sector (\d+), page (\d+), (\d+) records, (\d+) distinct pages \((\d+) repeats\), (\d+) B per distinct page, original (\d+) pages; journalled/total by B-tree: (.*)$', a)
        if not m:
            out.append(f"| {d['Label']} | (unparsed) {a[:80]} |")
            continue
        jb, seg, syn, sec, ps, rec, dist, rep, bpp, orig, trees = m.groups()
        parts = [p.strip() for p in trees.split(',')]
        name_idx = next((p for p in parts if p.startswith('sqlite_autoindex_name_1 ')), None)
        others = []
        for p in parts:
            mm = re.match(r'(.+) (\d+)/(\d+)$', p)
            if not mm or mm.group(1) == 'sqlite_autoindex_name_1':
                continue
            if int(mm.group(2)) > 0:
                others.append(f"{mm.group(1)} {mm.group(2)}")
        ni = name_idx.split(' ')[-1] if name_idx else '–'
        out.append(f"| {d['Label']} | {int(jb)/MB:,.2f} MiB | {seg} ({syn}) | {int(dist):,} | {bpp} | {ni} | {', '.join(others)} |")
    return "\n".join(out)

def rollback_table(rows, title):
    out = [f"\n#### {title}\n",
           "| Run | Library before | Journal at the stop | Cancel → rolled back | Main after (before) | Crash: next open (recovery) | State after | Older snapshots verify |",
           "|---|---:|---:|---:|---|---:|---|---|"]
    for d in rows:
        if 'crash' in d:
            c, r = d['crash'], d['recover']
            if r is None:
                out.append(f"| {c['Label']} | {c['LibraryBeforeBytes']/MB:,.1f} MiB | {c['JournalAtStopBytes']/MB:,.1f} MiB | – | – | (recover failed) | | |")
            else:
                out.append(f"| {c['Label']} | {c['LibraryBeforeBytes']/MB:,.1f} MiB | {c['JournalAtStopBytes']/MB:,.1f} MiB | – | main {r['MainBeforeBytes']/MB:,.1f} → {r['MainAfterBytes']/MB:,.1f} MiB | {r['OpenSeconds']:.2f} s | {r['State']} | {'yes' if r['OlderSnapshotsVerify'] else 'NO'} |")
        elif d.get('Mode') == 'cancel':
            out.append(f"| {d['Label']} | {d['LibraryBeforeBytes']/MB:,.1f} MiB | {d['JournalAtStopBytes']/MB:,.1f} MiB | {d['CancelToReturnSeconds']:.3f} s | {d['LibraryAfterStopBytes']/MB:,.1f} MiB ({d['LibraryBeforeBytes']/MB:,.1f} MiB) | – | {d.get('Outcome')} | – |")
    return "\n".join(out)

def probe_table(rows, title):
    out = [f"\n#### {title}\n",
           "| Run | Library before | Growth | Peak journal | J / growth | File rows/s | Duration | Peak WS above baseline | New names | Other load | Probe report / outcome |",
           "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|"]
    for d in rows:
        if 'failed' in d or 'crash' in d:
            continue
        ws = (d['PeakWorkingSetBytes'] - d['WorkingSetBeforeBytes']) / MB
        probe = (d.get('Probe') or '').replace('SI_DR_PROBE_REPORT=', '')
        probe = re.sub(r'\S*probe-report\.txt;?\s*', '', probe)
        outc = d.get('Outcome') or ''
        out.append(f"| {d['Label']} | {d['LibraryBeforeBytes']/MB:,.1f} MiB | {d['GrowthBytes']/MB:,.1f} MiB | {d['PeakJournalHandleBytes']/MB:,.2f} MiB | {100*d['JournalOverGrowth']:.1f}% | {d['FilesPerSecond']:,.0f} | {d['ImportSeconds']:.2f} s | {ws:,.0f} MiB | {d['NewNames']:,} | {d['OtherLoadMeanPercent']:.0f}% | {probe} {'' if outc=='Published' else '· ' + outc} |")
    return "\n".join(out)

if __name__ == '__main__':
    rd = sys.argv[1]
    plans = sys.argv[2:] or sorted(f[:-6] for f in os.listdir(rd) if f.endswith('.jsonl'))
    for p in plans:
        path = os.path.join(rd, p + '.jsonl')
        if not os.path.exists(path):
            continue
        rows = load(path)
        if p.startswith('probe-'):
            print(probe_table(rows, p))
            print(analysis_table(rows, p + ' (journal composition)'))
            print(rollback_table(rows, p + ' (rollback)'))
        elif p == 'analyse':
            print(analysis_table(rows, p))
            print(per_run_table(rows, p + ' (timings; perturbed by the journal copy)'))
        elif p == 'rollback':
            print(rollback_table(rows, p))
        else:
            print(cell_table(rows, p + ' (per cell)'))
            print(per_run_table(rows, p + ' (every run)'))
