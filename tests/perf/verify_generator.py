"""Checks the C# port of the objective generator against the REFERENCE'S recorded output.

Usage:  python verify_generator.py --exe <StorageInventory.Library.Tests.exe> [--dll <assembly>]

The C4 design repair recorded, for each run of the reference harness (docs/evidence/c4-design-review/harness.patch, `ObjSnapshot`), the realised
statistics of the names it generated at 2M files: files, distinct names, mean length, names seen once, files in the top 1% of names, the base
vocabulary and the churn counts (docs/evidence/c4-design-review/repair-results/*.jsonl, field "Generated"). This script has the port compute the
same statistics for the same cells (`--benchmark gate describe`, about 7 s each) and compares every figure. They must be equal: the statistics
depend on every name the generator produces, so a port that differed in a name's length, a rank draw or a permutation would differ here.
(GeneratorTests.ReferenceStatisticsAtTwoMillion pins two of these cells in the test suite; GeneratorTests pins the first 1,000 names of more.)
"""
import argparse
import glob
import json
import os
import re
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gate_exe  # noqa: E402

PATTERN = re.compile(r'params (?P<params>\S+) .*?generated (?P<files>\d+) files, (?P<distinct>\d+) distinct names \([\d.]+%\), mean length (?P<mean>[\d.]+) code units, '
                     r'names seen once (?P<once>[\d.]+)% of distinct, files in the top 1% of names (?P<top1>[\d.]+)%; base vocabulary (?P<vocab>\d+); '
                     r'churn: renamed to new names (?P<renamed>\d+), deleted (?P<deleted>\d+), added with existing names (?P<added>\d+)')
CELLS = {'d=0.25;model=system': 'F-2M-25-system', 'd=0.60;model=data': 'F-2M-60-data',
         'd=0.25;model=system;rho=0.01;delta=0.005;alpha=0.005': 'R-2M-25-system-1-05-05', 'd=0.25;model=system;rho=0.10;delta=0.05;alpha=0.05': 'R-2M-25-system-10-5-5'}


def reference_values():
    root = gate_exe.repo_root()
    found = {}
    for path in glob.glob(os.path.join(root, 'docs', 'evidence', 'c4-design-review', 'repair-results', 'repair-*.jsonl')):
        for line in open(path, encoding='utf-8-sig'):
            rec = json.loads(line)
            rec = rec.get('crash', rec)
            m = PATTERN.search(rec.get('Generated') or '')
            if m and m['params'] in CELLS:
                found[CELLS[m['params']]] = m.groupdict()
    return found


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('--exe', required=True)
    ap.add_argument('--dll')
    a = ap.parse_args(argv)
    h = gate_exe.Harness(a.exe, a.dll)
    reference = reference_values()
    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for cell, ref in sorted(reference.items()):
            out = os.path.join(tmp, cell + '.json')
            code, _, _, err = h.run('describe', '--cell', cell, '--scale', '1', '--out', out)
            if code != 0:
                print(f'BAD {cell}: describe failed ({code}): {err[-200:]}')
                bad += 1
                continue
            got = json.load(open(out, encoding='utf-8'))
            checks = [('files', got['files'], int(ref['files'])), ('distinct names', got['distinct'], int(ref['distinct'])), ('base vocabulary', got['vocabulary'], int(ref['vocab']) if got['vocabulary'] else 0),
                      ('mean length (2 decimals)', f'{got["meanLength"]:.2f}', ref['mean']), ('names seen once, % of distinct (1 decimal)', f'{100.0 * got["namesSeenOnce"] / got["distinct"]:.1f}', ref['once']),
                      ('files in the top 1% of names, % (1 decimal)', f'{100.0 * got["top1ShareOfFiles"]:.1f}', ref['top1']), ('renamed', got['renamed'], int(ref['renamed'])),
                      ('deleted', got['deleted'], int(ref['deleted'])), ('added', got['added'], int(ref['added']))]
            for name, mine, theirs in checks:
                ok = mine == theirs
                bad += not ok
                print(f"{'ok ' if ok else 'BAD'} {cell}: {name}: port {mine}, reference {theirs}")
    print(f'{len(reference)} cells compared; ' + ('all figures equal the reference\'s' if not bad else f'{bad} figure(s) differ'))
    return 1 if bad or not reference else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
