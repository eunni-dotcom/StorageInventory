"""C4 design final repair (C4DRR-M03): the adversarial tests of the whole-machine load source (load_probe.py).

Usage:  python load_experiments.py <out dir> [--rounds <n>] [--summarise]      (--summarise: recompute summary.json only)
        python load_experiments.py <out dir> --cycles [--rounds <n>]          (the alternating-block experiments below)

Each experiment is one probe session with its per-collection series (machine percentages only). External load comes from
load_inject.py started by this driver, so it is outside the probe's job object; the benchmark stand-in runs inside it.
  idle            the probe alone, 60 s (the quiet check as the specification states it)
  sustained       probe 40 s; from +10 s, four CPU-bound processes for 20 s
  churn           probe 40 s; from +10 s, four concurrent short-lived processes of 0.3 s each, replaced at once, for 20 s
                  (the C4 design repair review's case)
  bench           probe run of one CPU-bound process inside the job: 10 s pause, 20 s busy, 10 s pause (benchmark-only load)
  bench+churn     as bench, with the churn of 'churn' outside the job during the busy 20 s
  churn-cycles         (--cycles) probe 134 s; six 10 s blocks of the same churn, each between 10 s gaps without it
  bench+churn-cycles   (--cycles) as churn-cycles, during a probe run whose benchmark stand-in is busy throughout
For each, the on-window (the injection, or the busy phase of bench) is compared with the off-window of the same session (the
collections before and after it, transitions excluded): the change of U = whole - own, of the whole machine, of own, and of the
diagnostic per-process sums (external, induced, unattributed), against the injected CPU measured exactly by the injector.
Writes <out dir>/<experiment>-r<k>.probe.json and .inject.json, and <out dir>/summary.json.
"""
import json
import os
import subprocess
import sys
import time

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

HERE = os.path.dirname(os.path.abspath(__file__))
PROBE = os.path.join(HERE, 'load_probe.py')
INJECT = os.path.join(HERE, 'load_inject.py')
PY = sys.executable
FIELDS = ('whole', 'own', 'U', 'external_counted', 'induced_counted', 'unattributed', 'dpc_interrupt')


def mean(xs):
    xs = [x for x in xs if x is not None]
    return round(sum(xs) / len(xs), 2) if xs else None


def windows(series, on_start, on_end):
    on = [r for r in series if on_start + 1.0 <= r['wall'] - 0.5 and r['wall'] <= on_end - 0.25]
    off = [r for r in series if r['wall'] < on_start - 0.25 or r['wall'] - 0.5 > on_end + 1.0]
    return on, off


def cycles(name, outdir, k):
    """Alternating blocks: six 10 s blocks of churn, each between 10 s without it, in one probe session (quiet, or a run with a
    benchmark stand-in busy throughout). Each block is compared with the gaps on either side of it, so slow background drift
    cancels; the result is the mean and range of the six per-block changes."""
    probe_json = os.path.join(outdir, f'{name}-r{k}.probe.json')
    inject_json = os.path.join(outdir, f'{name}-r{k}.inject.json')
    if name == 'churn-cycles':
        probe = subprocess.Popen([PY, PROBE, 'quiet', '134', '--series', '--json', probe_json], stdout=subprocess.DEVNULL)
    else:
        probe = subprocess.Popen([PY, PROBE, 'run', '--series', '--json', probe_json, '--', PY, INJECT, 'busy', '130', '--pause', '2'],
                                 stdout=subprocess.DEVNULL)
    time.sleep(2)
    subprocess.run([PY, INJECT, 'churn-cycles', '4', '6', '10', '10', '0.3', '--json', inject_json], stdout=subprocess.DEVNULL, check=True)
    probe.wait()
    p = json.load(open(probe_json, encoding='utf-8'))
    series = p.pop('series')
    inj = json.load(open(inject_json, encoding='utf-8'))
    blocks = inj['blocks']
    edges = [series[0]['wall'] - 0.5] + [x for b in blocks for x in b] + [series[-1]['wall']]
    gaps = [(edges[2 * i], edges[2 * i + 1]) for i in range(len(blocks) + 1)]   # before block 0, between blocks, after the last

    def inside(a, b, lead, tail):
        return [r for r in series if a + lead <= r['wall'] - 0.5 and r['wall'] <= b - tail]
    per = []
    for i, (a, b) in enumerate(blocks):
        on = inside(a, b, 0.5, 0.0)
        off = inside(gaps[i][0], gaps[i][1], 1.0, 0.25) + inside(gaps[i + 1][0], gaps[i + 1][1], 1.0, 0.25)
        per.append({f: round(mean([r[f] for r in on]) - mean([r[f] for r in off]), 2) for f in FIELDS})
    row = {'experiment': name, 'round': k, 'verdict': p['verdict'], 'U': p['other_load_U_percent'], 'whole': p['whole_machine_percent'],
           'own': p['own_percent'], 'collections': p['collections'], 'blocks': len(blocks),
           'injected': {'processes': inj['processes'], 'machine_percent': inj['injected_machine_percent'],
                        'cpu_seconds_per_process': inj['mean_process_life_cpu_seconds']},
           'change_mean': {f: mean([c[f] for c in per]) for f in FIELDS},
           'change_range': {f: [min(c[f] for c in per), max(c[f] for c in per)] for f in FIELDS}, 'per_block': per}
    row['seen_by_U_share_of_injected'] = round(row['change_mean']['U'] / inj['injected_machine_percent'], 3)
    row['seen_by_counted_external_share_of_injected'] = round(row['change_mean']['external_counted'] / inj['injected_machine_percent'], 3)
    return row


def experiment(name, outdir, k):
    if name.endswith('-cycles'):
        return cycles(name, outdir, k)
    probe_json = os.path.join(outdir, f'{name}-r{k}.probe.json')
    inject_json = os.path.join(outdir, f'{name}-r{k}.inject.json')
    inj = None
    if name == 'idle':
        subprocess.run([PY, PROBE, 'quiet', '60', '--series', '--json', probe_json], stdout=subprocess.DEVNULL, check=True)
    elif name in ('sustained', 'churn'):
        probe = subprocess.Popen([PY, PROBE, 'quiet', '40', '--series', '--json', probe_json], stdout=subprocess.DEVNULL)
        time.sleep(10)
        args = ['sustained', '4', '20'] if name == 'sustained' else ['churn', '4', '20', '0.3']
        subprocess.run([PY, INJECT] + args + ['--json', inject_json], stdout=subprocess.DEVNULL, check=True)
        probe.wait()
    else:
        start = time.time()
        probe = subprocess.Popen([PY, PROBE, 'run', '--series', '--json', probe_json, '--', PY, INJECT, 'busy', '20', '--pause', '10'],
                                 stdout=subprocess.DEVNULL)
        if name == 'bench+churn':
            time.sleep(10.3)
            subprocess.run([PY, INJECT, 'churn', '4', '19', '0.3', '--json', inject_json], stdout=subprocess.DEVNULL, check=True)
        probe.wait()
    p = json.load(open(probe_json, encoding='utf-8'))
    series = p.pop('series')
    row = {'experiment': name, 'round': k, 'verdict': p['verdict'], 'U': p['other_load_U_percent'], 'whole': p['whole_machine_percent'],
           'own': p['own_percent'], 'collections': p['collections'],
           'renumbered_discards': p['diagnostics']['renumbered_instance_samples_discarded'],
           'samples_without_rate': p['diagnostics']['new_process_samples_without_rate']}
    if os.path.exists(inject_json):
        inj = json.load(open(inject_json, encoding='utf-8'))
        on_start, on_end = inj['on_wall_start'], inj['on_wall_end']
        row['injected'] = {'processes': inj['processes'], 'machine_percent': inj['injected_machine_percent'],
                           'cpu_seconds_per_process': inj['mean_process_life_cpu_seconds']}
    elif name.startswith('bench'):
        t0 = series[0]['wall'] - series[0]['t']
        on_start, on_end = t0 + 10.0, t0 + 30.0
    else:
        on_start = on_end = None
    if on_start is not None:
        on, off = windows(series, on_start, on_end)
        row['on_collections'], row['off_collections'] = len(on), len(off)
        row['on_mean'] = {f: mean([r[f] for r in on]) for f in FIELDS}
        row['off_mean'] = {f: mean([r[f] for r in off]) for f in FIELDS}
        row['change'] = {f: (round(row['on_mean'][f] - row['off_mean'][f], 2) if row['on_mean'][f] is not None and row['off_mean'][f] is not None else None) for f in FIELDS}
        if inj:
            injected = inj['injected_machine_percent']
            row['seen_by_U_share_of_injected'] = round(row['change']['U'] / injected, 3)
            row['seen_by_counted_external_share_of_injected'] = round(row['change']['external_counted'] / injected, 3)
    return row


def null_deltas(outdir, rounds):
    """The control: the same on/off windows applied to the idle sessions, where nothing was injected (the first 40 s: on 10-30 s;
    the last 40 s: on 30-50 s). Their changes are background drift only and bound the noise of every other experiment's change."""
    out = []
    for k in range(1, rounds + 1):
        series = json.load(open(os.path.join(outdir, f'idle-r{k}.probe.json'), encoding='utf-8'))['series']
        t0 = series[0]['wall'] - series[0]['t']
        for lo in (0.0, 20.0):
            part = [r for r in series if lo <= r['t'] <= lo + 40.0]
            on, off = windows(part, t0 + lo + 10.0, t0 + lo + 30.0)
            out.append({'round': k, 'window_start_s': lo, **{f: round(mean([r[f] for r in on]) - mean([r[f] for r in off]), 2) for f in ('whole', 'U', 'external_counted', 'unattributed')}})
    return out


def main_cycles(outdir, rounds):
    os.makedirs(outdir, exist_ok=True)
    rows = []
    for k in range(1, rounds + 1):
        for name in ('churn-cycles', 'bench+churn-cycles'):
            r = cycles(name, outdir, k)
            rows.append(r)
            print(json.dumps({x: r.get(x) for x in ('experiment', 'round', 'verdict', 'injected', 'change_mean', 'change_range',
                                                      'seen_by_U_share_of_injected', 'seen_by_counted_external_share_of_injected')}))
    with open(os.path.join(outdir, 'summary-cycles.json'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(json.dumps({'logical_processors': os.cpu_count(), 'experiments': rows}, indent=1) + '\n')
    return 0


def main(outdir, rounds, run=True):
    os.makedirs(outdir, exist_ok=True)
    rows = []
    if run:
        for k in range(1, rounds + 1):
            for name in ('idle', 'sustained', 'churn', 'bench', 'bench+churn'):
                r = experiment(name, outdir, k)
                rows.append(r)
                print(json.dumps({x: r.get(x) for x in ('experiment', 'round', 'verdict', 'U', 'change', 'injected',
                                                          'seen_by_U_share_of_injected', 'seen_by_counted_external_share_of_injected')}))
    else:
        rows = json.load(open(os.path.join(outdir, 'summary.json'), encoding='utf-8'))['experiments']
    nulls = null_deltas(outdir, rounds)
    noise = {f: round(max(abs(n[f]) for n in nulls), 2) for f in ('whole', 'U', 'external_counted', 'unattributed')}
    print(json.dumps({'null_deltas': nulls, 'largest_null_change': noise}))
    with open(os.path.join(outdir, 'summary.json'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(json.dumps({'logical_processors': os.cpu_count(), 'experiments': rows, 'idle_null_deltas': nulls,
                            'largest_null_change': noise}, indent=1) + '\n')
    return 0


if __name__ == '__main__':
    a = sys.argv[1:]
    n = int(a[a.index('--rounds') + 1]) if '--rounds' in a else 2
    sys.exit(main_cycles(a[0], n) if '--cycles' in a else main(a[0], n, run='--summarise' not in a))
