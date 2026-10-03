"""Generates class-sweep.txt.gz, the golden file of WorkloadClassTests.SweepAgreesWithTheReference.

Usage:  python make_class_sweep.py            (from anywhere; paths are found relative to this file)

It runs the PYTHON REFERENCE of §15.4's class function (docs/evidence/c4-design-review/workload_class.py, read from the specification's own
band table) over a deterministic sweep of cells, once, and stores one character per cell in the sweep's enumeration order:
R Representative, S Stress, W Worst-case, I Informational, X not a valid cell. The C# test enumerates the same cells in the same order and
compares its own classification (WorkloadClassifier) with the stored characters. The C# tests never call Python.

The enumeration order is part of the contract (the C# test repeats it):
  1. first saves:  for n in SIZES, for model in MODELS, for d in D_VALUES
  2. re-scans:     for n in SIZES, for model in MODELS, for d in D_VALUES, for rho in RHO_VALUES, for delta in CHURN_VALUES, for alpha in CHURN_VALUES
  3. named families (closed list; the forms outside it are 'X' by the C# rule C4DRRR-O01 and are checked individually by the C# tests):
     for family in (N1, N2, N3), for n in SIZES, for into_new in (False, True), for rescan in (False, True)
Percent values are exact decimals ("25.000001" is strictly above 25%).
"""
import gzip
import hashlib
import importlib.util
import os
import sys
from fractions import Fraction as Fr

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, *['..'] * 4))
REF = os.path.join(ROOT, 'docs', 'evidence', 'c4-design-review', 'workload_class.py')
SPEC = os.path.join(ROOT, 'docs', 'v1.1-preproduction-spec.md')

SIZES = [100_000, 1_000_000, 2_000_000, 2_000_001, 3_000_000, 10_000_000]
MODELS = ['system', 'data']
D_VALUES = ['0', '0.000001', '1', '10', '24.999999', '25', '25.000001', '30', '40', '50', '59.999999', '60', '60.000001', '70', '80', '90', '99.999999', '100', '100.000001']
RHO_VALUES = ['0', '0.5', '0.999999', '1', '1.000001', '5', '9.999999', '10', '10.000001', '50', '100', '100.000001']
CHURN_VALUES = ['0', '0.25', '0.499999', '0.5', '0.500001', '2.5', '4.999999', '5', '5.000001', '50', '100']
FAMILIES = ['N1', 'N2', 'N3']
CODE = {'Informational': 'I', 'Representative': 'R', 'Stress': 'S', 'Worst-case': 'W'}


def load_reference():
    spec = importlib.util.spec_from_file_location('workload_class_reference', REF)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    wc = load_reference()
    text = open(SPEC, encoding='utf-8').read()
    bands = wc.parse_bands(text[text.index('### 15.4'):text.index('### 15.5')])
    problems = wc.check_bands(bands)
    if problems:
        raise SystemExit('the specification\'s bands do not partition their ranges: ' + '; '.join(problems))

    def code(cell):
        if not wc.valid(cell):
            return 'X'
        hits, _ = wc.classify(cell, bands)
        if len(hits) != 1:
            raise SystemExit(f'the reference gave {len(hits)} classes for {cell}')
        return CODE[hits[0]]

    out = []
    for n in SIZES:
        for model in MODELS:
            for d in D_VALUES:
                out.append(code(wc.F(n, Fr(d), model)))
    for n in SIZES:
        for model in MODELS:
            for d in D_VALUES:
                for rho in RHO_VALUES:
                    for delta in CHURN_VALUES:
                        for alpha in CHURN_VALUES:
                            out.append(code(wc.R(n, Fr(d), model, Fr(rho), Fr(delta), Fr(alpha))))
    for family in FAMILIES:
        for n in SIZES:
            for into_new in (False, True):
                for rescan in (False, True):
                    closed = (family == 'N1' and not rescan) or (family == 'N2' and not into_new) or (family == 'N3' and not into_new and not rescan)
                    if not closed:
                        out.append('X')
                    else:
                        ref_family = 'N2 re-scan' if (family == 'N2' and rescan) else family
                        out.append(code(wc.fam(ref_family, n)))
    body = ''.join(out)
    data = (f'{len(body)} {hashlib.sha256(body.encode()).hexdigest()}\n' + body).encode()
    path = os.path.join(HERE, 'class-sweep.txt.gz')
    with open(path, 'wb') as raw, gzip.GzipFile(fileobj=raw, mode='wb', mtime=0, filename='') as z:
        z.write(data)
    counts = {c: body.count(c) for c in 'RSWIX'}
    print(f'{len(body)} cells; {counts}; {os.path.getsize(path)} bytes')


if __name__ == '__main__':
    sys.exit(main())
