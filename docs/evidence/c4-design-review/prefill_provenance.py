"""C4 design repair (erratum E1): which prefill each run of the C4 design review used, and under which environment it was built.

Usage:  python prefill_provenance.py results

The old harness cached a prefill under "<family>-<size>[-persource]" and built it in a child that inherited the triggering run's
whole environment, so a per-source prefill first built by a probe:combined run (SI_DR_PRESORT=chunk) carried key-ordered name ids,
and every later per-source run on that key reused it. This script reads the plan logs ("building prefill <key>" under the run
that triggered it), finds the environment of that run from the result files' Probe field, and marks every result record whose
prefill was built with a variable other than SI_DR_PERSOURCE while the record itself did not set it.
"""
import glob
import json
import os
import re
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')


def family_of(label, fam):
    return {'rescan': 'hash', 'rescanreal': 'real'}.get(fam, fam)


def main(rd):
    probes = {}
    records = []
    for f in sorted(glob.glob(os.path.join(rd, '*.jsonl'))):
        for line in open(f, encoding='utf-8'):
            d = json.loads(line)
            c = d.get('crash', d)
            if 'Label' not in c:
                continue
            probes[c['Label']] = d.get('Probe') or (d.get('crash') or {}).get('Probe') or ''
            records.append((os.path.basename(f), d))
    built = {}
    for f in sorted(glob.glob(os.path.join(rd, '*.log'))):
        label = None
        for line in open(f, encoding='utf-8'):
            if line.startswith('=== '):
                label = line[4:].strip()
            m = re.search(r'building prefill (\S+)', line)
            if m:
                built[m.group(1)] = (os.path.basename(f), label)
    print('| Prefill | First built by (plan log, run) | Environment of that run |')
    print('|---|---|---|')
    env_of = {}
    for key, (log, label) in sorted(built.items()):
        env = ', '.join(sorted(set(re.findall(r'SI_DR_[A-Z_]+=[^;\s]+', probes.get(label, ''))) - {'SI_DR_PROBE_REPORT'})) or 'none'
        env_of[key] = env
        print(f'| `{key}` | {log}: {label} | {env} |')
    print()
    print('| Result file | Run | Its environment | Prefill | Prefill built with key-ordered interning while the run did not use it |')
    print('|---|---|---|---|---|')
    affected = 0
    labels = set()
    for f, d in records:
        c = d.get('crash', d)
        label = c['Label']
        fam = c.get('Family') or ''
        prefill = c.get('PrefillFilesPerSnapshot', '')
        run_env = set(re.findall(r'SI_DR_[A-Z_]+=[^;\s]+', probes.get(label, ''))) - {'SI_DR_PROBE_REPORT'}
        persource = any(e.startswith('SI_DR_PERSOURCE=1') for e in run_env)
        if 'crash' in d:
            # a killed child's record carries only its label: the family, the prefill size and the schema variant are read
            # from the label the plan gave it ("crash persource hash existing 2M/6000k": three snapshots of 2M)
            m = re.search(r'\b(rescanreal|rescan|hash|mixed|append|real)\b.*?(\d+)M/(\d+)k', label)
            if not m:
                continue
            fam, prefill = m.group(1), int(m.group(3)) * 1000 // 3
            persource = 'persource' in label
            run_env = {'SI_DR_PERSOURCE=1 (from the label)'} if persource else set()
            if 'chunked' in label:
                run_env.add('SI_DR_PRESORT=chunk (from the label)')
        if not fam:
            continue
        key = f"{family_of(label, fam)}-{prefill}{'-persource' if persource else ''}"
        if key not in env_of:
            continue
        contaminated = 'SI_DR_PRESORT' in env_of[key] and not any(e.startswith('SI_DR_PRESORT') for e in run_env)
        if contaminated:
            affected += 1
            labels.add(label)
            print(f"| {f} | {label} | {', '.join(sorted(run_env)) or 'none'} | `{key}` | **yes** |")
    print(f'\n{affected} result records ran on a prefill built with SI_DR_PRESORT although they did not set it')
    return labels


def affected_labels(rd):
    """The labels of the affected records, without printing (used by b1_table.py)."""
    import contextlib
    import io
    with contextlib.redirect_stdout(io.StringIO()):
        return main(rd)


if __name__ == '__main__':
    main(sys.argv[1])
