"""Builds fixtures/gate-fixtures.zip: the small REAL runs the self-tests of attribute_run.py use (smoke scale).

Usage:  python make_fixtures.py --exe <StorageInventory.Library.Tests.exe> [--dll <assembly>] [--scale 0.01] [--out gate-fixtures.zip]

Four real captures made with the harness (PerfGate), each a run record and the two artefacts it names:
  fix-F             F(2M, 25%, system) at smoke scale, a first save into a NEW source of the production (per-source) schema   -> expected PASS
  fix-R             R(2M, 25%, system; 1%, 0.5%, 0.5%) at smoke scale, a re-scan into the EXISTING source 1                     -> expected PASS
  fix-control-new   the negative control (name index keyed by the name alone, real SQLite journaling), a new source                -> expected FAIL
  fix-control-exist the same, importing into the existing source 1                                                                -> expected FAIL
No path or name of this machine is in them: the record holds file names inside the archive; the databases hold generated names only.
"""
import argparse
import json
import os
import shutil
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import gate_exe  # noqa: E402


def need(code, record, what, err=''):
    if code != 0 or record is None:
        raise SystemExit(f'{what} failed ({code}): {err[-400:]}')
    return record


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('--exe', required=True)
    ap.add_argument('--dll')
    ap.add_argument('--scale', type=float, default=0.005)
    ap.add_argument('--out', default=os.path.join(HERE, 'gate-fixtures.zip'))
    ap.add_argument('--names', type=int, default=5000)
    ap.add_argument('--target-names', type=int, default=2000)
    a = ap.parse_args(argv)
    h = gate_exe.Harness(a.exe, a.dll)
    tmp = tempfile.mkdtemp(prefix='gate-fixtures-')
    try:
        analysis = os.path.join(tmp, 'analysis')
        os.makedirs(analysis)
        prefill = os.path.join(tmp, 'prefill.sqlite3')
        code, _, rec, err = h.run('prefill', '--cell', 'F-2M-25-system', '--scale', a.scale, '--out', prefill)
        need(code, rec, 'prefill', err)
        records = {}
        for label, cell in (('fix-F', 'F-2M-25-system'), ('fix-R', 'R-2M-25-system-1-05-05')):
            code, _, rec, err = h.run('run', '--cell', cell, '--mode', 'attribution', '--scale', a.scale, '--prefill-from', prefill, '--analysis-dir', analysis,
                                      '--label', label, '--binary-commit', 'fixture')
            records[label] = need(code, rec, label, err)
        for label, variant in (('fix-control-new', 'new'), ('fix-control-exist', 'existing')):
            code, _, rec, err = h.run('control', '--variant', variant, '--out', analysis, '--label', label, '--names', a.names, '--target-names', a.target_names)
            records[label] = need(code, rec, label, err)
        # databases with the same SHA-256 are stored once, under a name made of the hash, and the records are rewritten to name that file
        # (fixtures only: a gate session's records are never rewritten)
        stored = {}
        for label, rec in records.items():
            att = rec['attribution']
            sha = att['databaseCopySha256']
            stored.setdefault(sha, att['databaseCopy'])
            att['databaseCopy'] = f'{sha[:16]}.before.sqlite3'
        with zipfile.ZipFile(a.out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
            for label, rec in records.items():
                z.writestr(f'records/{label}.json', json.dumps(rec, indent=1))
                z.write(os.path.join(analysis, rec['attribution']['journalCopy']), f'analysis/{rec["attribution"]["journalCopy"]}')
            for sha, original in stored.items():
                z.write(os.path.join(analysis, original), f'analysis/{sha[:16]}.before.sqlite3')
        total = sum(os.path.getsize(os.path.join(analysis, n)) for n in os.listdir(analysis))
        print(f'{a.out}: {os.path.getsize(a.out):,} B (raw artefacts {total:,} B)')
        return 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
