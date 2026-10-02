"""C4 design final repair (C4DRR-M02): runs the fail-closed journal_ranges.py on the six analysed journals of a fresh
`repair:structure` run and compares each result with the design repair's committed repair-results/ranges/*.json (same verdict, same
per-level counts, IN runs, journal bytes and page counts). Shows that the stricter checker accepts complete, valid evidence.

Usage:  python journal_ranges_rerun.py <structure output dir> [--json <out.json>]
"""
import hashlib
import json
import os
import re
import subprocess
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

HERE = os.path.dirname(os.path.abspath(__file__))
CHECKER = os.path.join(HERE, 'journal_ranges.py')
CELLS = [  # (label, committed ranges file, target args, layout, expected verdict)
    ('analyse persource hash new-source 1M/3000k', 'persource-hash-new-source-1M-3000k.json', ['--new-source'], 'persource', 'PASS'),
    ('analyse persource hash existing 1M/3000k', 'persource-hash-existing-1M-3000k.json', ['--source', '1'], 'persource', 'PASS'),
    ('analyse global hash new-source 1M/750k', 'global-hash-new-source-1M-750k.json', ['--new-source'], 'global', 'FAIL'),
    ('analyse global hash existing 1M/750k', 'global-hash-existing-1M-750k.json', ['--source', '1'], 'global', 'FAIL'),
    ('analyse persource obj first 2M/6000k d25', 'persource-obj-first-2M-6000k-d25.json', ['--new-source'], 'persource', 'PASS'),
    ('analyse persource objrescan 2M/6000k n1', 'persource-objrescan-2M-6000k-n1.json', ['--source', '1'], 'persource', 'PASS'),
]


def slug(label):
    return re.sub(r'[^A-Za-z0-9]+', '-', label).strip('-')


def main(outdir, json_out):
    lengths = {}
    for line in open(os.path.join(outdir, 'repair-structure.jsonl'), encoding='utf-8-sig'):
        r = json.loads(line)
        m = re.search(r'before COMMIT: page_count \d+, page_size \d+, main \d+ B, journal (\d+) B', r.get('Guard') or '')
        if m:
            lengths[r['Label']] = int(m.group(1))
    rows = []
    for label, committed, target, layout, expected in CELLS:
        base = os.path.join(outdir, 'analysis', slug(label))
        args = [sys.executable, CHECKER, base + '.precommit.journal', base + '.before.sqlite3'] + target + [
            '--layout', layout, '--expect-journal-bytes', str(lengths[label]),
            '--expect-copy-sha256', hashlib.sha256(open(base + '.before.sqlite3', 'rb').read()).hexdigest(), '--json']
        p = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
        new = json.loads(p.stdout)
        old = json.load(open(os.path.join(HERE, 'repair-results', 'ranges', committed), encoding='utf-8'))
        same = {k: new.get(k) == old.get(k) for k in ('verdict', 'journal_bytes', 'database_pages', 'page_size', 'remote_pages', 'in_runs', 'by_tree', 'levels_over_shared_cap')}
        same['records'] = new.get('integrity', {}).get('records') == old['integrity']['records']
        row = {'cell': label, 'expected': expected, 'verdict': new['verdict'], 'exit': p.returncode, 'invalid_reasons': new['invalid_reasons'],
               'journal_bytes': new.get('journal_bytes'), 'recorded_bytes': lengths[label], 'records': new.get('integrity', {}).get('records'),
               'segments': new.get('segments'), 'synced_segments': new.get('synced_segments'),
               'identical_to_committed': all(same.values()), 'differences': [k for k, v in same.items() if not v]}
        rows.append(row)
        print(f"{label}: {new['verdict']} (exit {p.returncode}); identical to the committed analysis: {row['identical_to_committed']} {row['differences'] or ''}")
    ok = all(r['verdict'] == r['expected'] and r['identical_to_committed'] and not r['invalid_reasons'] for r in rows)
    if json_out:
        with open(json_out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(json.dumps({'all_as_expected_and_identical': ok, 'cells': rows}, indent=1) + '\n')
    return 0 if ok else 1


if __name__ == '__main__':
    a = sys.argv[1:]
    sys.exit(main(a[0], a[a.index('--json') + 1] if '--json' in a else None))
