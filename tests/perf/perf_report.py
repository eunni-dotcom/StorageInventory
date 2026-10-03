"""Renders TEST-P1 sessions into the markdown of docs/benchmarks/library.md (§15.4 "Output": the machine record, the method, the session manifests,
every session (valid or not) with its quiet checks, every run in a raw table with its load record and validity, each budget's outcome, and caveats
only: no extrapolated claim).

Usage:  python perf_report.py <session dir> [<session dir> ...] [--out report.md]

Each session directory is read from its RAW files (manifest.json, machine.json, quiet.jsonl, judgements.jsonl, runs.jsonl, attribution/*.verdict.json,
refused.json, aborted.json), not from session.json: the tables are recomputed, so a report never disagrees with the raw record. Every session given is
reported in full, an invalid, refused or aborted one included, and a session whose negative control did not FAIL says TEST-P1 FAILED in its status, its
PERF-15 (a) row and its control table. Nothing here is a path or a name of the machine.
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import perf_session  # noqa: E402

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')


def table(headers, rows):
    out = ['| ' + ' | '.join(headers) + ' |', '|' + '|'.join('---' for _ in headers) + '|']
    for r in rows:
        out.append('| ' + ' | '.join(str(c).replace('|', '\\|').replace('\n', ' ') for c in r) + ' |')
    return out


def fmt(v, spec=''):
    if v is None:
        return ''
    return format(v, spec) if spec else str(v)


def mib(b):
    return f'{b / 1048576:,.1f}'


def load_json(path):
    return json.load(open(path, encoding='utf-8')) if os.path.exists(path) else None


def machine_section(machine):
    if not machine:
        return ['_No machine record._']
    rows = []

    def put(k, v):
        rows.append((k, v))
    cpu = machine.get('cpu')
    put('CPU', f'{cpu["model"]}, {cpu["cores"]} cores' if isinstance(cpu, dict) else cpu)
    put('Logical processors (all groups, `GetActiveProcessorCount`)', f'{machine.get("logicalProcessors")} in {machine.get("processorGroups")} group(s)')
    put('RAM', f'{machine["ramBytes"] / 2**30:.1f} GiB' if isinstance(machine.get('ramBytes'), int) else machine.get('ramBytes'))
    st = machine.get('storage')
    put('Storage', f'{st["model"]}, {st["busType"]} {st["mediaType"]}' if isinstance(st, dict) else st)
    vol = machine.get('volume')
    put('File system, cluster', f'{vol["fileSystem"]}, {vol["clusterBytes"]} B' if isinstance(vol, dict) else vol)
    os_ = machine.get('os')
    put('OS', f'{os_["caption"]} build {os_["build"]}' if isinstance(os_, dict) else os_)
    put('Power plan, source', f'{machine.get("powerPlan")}, {json.dumps(machine.get("powerSource"))}')
    d = machine.get('defender')
    put('Microsoft Defender', f'real-time protection {d["realTimeProtectionEnabled"]}, engine {d["engineVersion"]}' if isinstance(d, dict) else d)
    put('Defender exclusions', json.dumps(machine.get('defenderExclusions')))
    put('Per-process counters see the Defender engine', machine.get('perProcessCountersSeeDefenderEngine', 'not recorded'))
    e = machine.get('engine')
    if isinstance(e, dict):
        put('SQLite engine, page size, import cache', f'{e.get("engine")}, {e.get("pageSize")} B, {e.get("importCacheKiB")} KiB')
    b = machine.get('binary', {})
    put('Binary', f'commit {b.get("commit")}{" (dirty tree)" if b.get("dirty") else ""}, build output SHA-256 {b.get("outputHash", "")[:16]}')
    put('Runtime tuning variables removed from the children', ', '.join(machine.get('runtimeTuningVariablesRemoved', [])) or 'none')
    return table(['Item', 'Value'], rows)


def manifest_section(m):
    lines = table(['Field', 'Value'], [
        ('Identifier', m['id']), ('Declared (before the first run)', m['declaredUtc']), ('Gate session', m['gate']),
        ('Not a gate session because', '; '.join(m.get('notGateBecause', [])) or ''),
        ('Supersedes', m.get('supersedes') or ''), ('Reason', m.get('reason') or ''), ('Invalidation of the superseded session (derived from its files)', m.get('invalidationOfSuperseded') or ''),
        ('Binary commit, build-output SHA-256', f'{m["binary"]["commit"]}{" (dirty)" if m["binary"]["dirty"] else ""}, {m["binary"]["outputHash"][:16]}'),
        ('Scale', m['scale']), ('Rounds', m['rounds']), ('Run order', m['runOrder']), ('Load validity', m['loadValidity']),
        ('Quiet check', f'{m["quietCheck"]["seconds"]} s, {m["quietCheck"]["collections"]} collections, U mean <= {m["quietCheck"]["meanMax"]}%, p95 <= {m["quietCheck"]["p95Max"]}%'),
        ('Run rule', f'U mean <= {m["runRule"]["meanMax"]}%, p95 <= {m["runRule"]["p95Max"]}% over the collections wholly inside the operation'),
        ('Blocks after the rounds', ', '.join(m['blocksAfterRounds'])),
    ])
    lines += [''] + table(['Cell', 'Class', 'Library before', 'Generator', 'Target files', 'Prefill files each', 'Run kinds'],
                          [(c['id'] + ' ' + c['label'], c['class'], c['library'], c['runFamily'] + (' ' + c['generatorParameters'] if c['generatorParameters'] else ''),
                            f'{c["targetFiles"]:,}', f'{c["prefillPerSnapshot"]:,}', ', '.join(m['kindsOfCell'][c['id']])) for c in m['plan']['cells']])
    return lines


def quiet_section(checks):
    rows = []
    for q in checks:
        if q.get('skipped'):
            rows.append((q['name'], 'skipped', '', '', '', q['reason']))
        else:
            rows.append((q['name'], q['verdict'], q['collections'], f'{q["U"]["mean"]:.2f}', f'{q["U"]["p95"]:.2f}', '; '.join(q['reasons'])))
    return table(['Quiet check', 'Verdict', 'Collections', 'U mean %', 'U p95 %', 'Reasons'], rows)


def runs_section(runs):
    rows = []
    for r in runs:
        m = r['metrics']
        load = r.get('load') or {}
        load_text = (f'{load["verdict"]} ({load.get("n")} collections, mean {load.get("mean"):.1f}%, p95 {load.get("p95"):.1f}%)' if load.get('mean') is not None
                     else load.get('verdict', 'not judged by load' if r['kind'] == 'attribution' else ''))
        rows.append((r['runId'], r['cell'], r['kind'], r['attempt'], r['round'], r['status'], fmt(m.get('filesPerSecond'), ',.0f'), fmt(m.get('importSeconds'), '.2f'),
                     fmt(m.get('cancelSeconds'), '.3f'), fmt(m.get('recoverySeconds'), '.3f'), fmt(m.get('deleteSeconds'), '.2f'),
                     m.get('attribution') or ('published' if m.get('publishedAfterCancel') else m.get('cancelClass', '')), fmt(m.get('tokenGapSeconds'), '.3f'), load_text,
                     '; '.join(r['reasons'])))
    return table(['Run', 'Cell', 'Kind', 'Attempt', 'Round', 'Status', 'Rows/s', 'T-IMPORT s', 'Cancel to return s', 'Recovery open s', 'Delete s', 'Attribution / CAN-01e / cancel class', 'Token gap s', 'Load (U)', 'Reasons'], rows)


def imp11_section(runs):
    rows = []
    for r in runs:
        i = r['metrics'].get('imp11')
        if i:
            m = r['metrics']
            rows.append((r['runId'], r['status'], i['checks'], i['commitEqualsFinalPending'], i.get('intervalsOverAllowance', ''), fmt(i.get('largestIntervalOverAllowance'), '.4f'),
                         mib(m['peakJournalBytes']) if 'peakJournalBytes' in m else '', mib(m['libraryGrowthBytes']) if 'libraryGrowthBytes' in m else '',
                         mib(m['peakWorkingSet']) if 'peakWorkingSet' in m else ''))
    return table(['Run', 'Status', 'Space checks', 'COMMIT growth = final check Λ', 'Intervals over the allowance', 'Largest interval growth / allowance',
                  'Journal before COMMIT MiB', 'Library growth MiB', 'Peak working set MiB'], rows) if rows else ['_No run recorded IMP-11 checks._']


def figures_section(evaluation):
    rows = []
    for cid, e in evaluation['cells'].items():
        t = e['timed']
        rps, secs = t['rowsPerSecond'], t['importSeconds']
        def rng(f, spec):
            if f is None:
                return 'NOT MEASURED'
            if f.get('failed'):
                return 'a run failed'
            return f'{format(f["median"], spec)} ({format(f["min"], spec)} to {format(f["max"], spec)})'
        rows.append((cid, e['class'], f'{t["valid"]} valid of {t["attempts"]}', rng(rps, ',.0f'), rng(secs, '.2f'), fmt(e.get('cancelSeconds'), '.3f'), fmt(e.get('recoverySeconds'), '.3f')))
    return table(['Cell', 'Class', 'Valid runs of attempts', 'Rows/s: median (min to max) of the first five valid', 'T-IMPORT s: median (min to max)', 'Cancel figure s (largest of four)', 'Recovery open s'], rows)


def outcomes_section(evaluation):
    rows = []
    for name, b in evaluation['budgets'].items():
        rows.append((name, b['outcome'] + (f' (would be {b["wouldBe"]})' if not evaluation['judged'] else ''), 'blocks acceptance' if b['blocksAcceptance'] else ''))
    t = evaluation['tokenInterval']
    rows.append(('Token-check interval (CAN-01d, at most 0.5 s)', f'{t["outcome"]} (largest gap {fmt(t["largestGapSeconds"], ".3f")} s over {t["runs"]} runs)', ''))
    rows.append(('A cancellation after the final check publishes (CAN-01e)', evaluation['cancelAfterFinalCheck']['outcome'], ''))
    lines = table(['Budget', 'Outcome', ''], rows)
    detail = []
    for name, b in evaluation['budgets'].items():
        for c in b['cells']:
            detail.append((name, c['cell'], c['part'], c['outcome'], c['why']))
    return lines + [''] + table(['Budget', 'Cell', 'Part', 'Outcome', 'Why'], detail)


def control_section(controls, evaluation):
    lines = table(['Variant', 'Attempt', 'Verdict', 'REMOTE pages', 'Levels over the SHARED cap', 'Reasons'],
                  [(c['variant'], c['attempt'], c['verdict'], fmt(c.get('remotePages')), fmt(c.get('levelsOverSharedCap')), '; '.join(c.get('reasons', []))) for c in controls]) if controls else ['_No negative control ran._']
    control = evaluation.get('negativeControl')
    if control is not None:
        lines += ['', ('Result: ' + control['outcome'] + ' - ' + control['why'] + '.') if control['ok'] else ('**TEST-P1 FAILED**: ' + control['why'] + '.')]
    return lines


def attribution_section(directory):
    folder = os.path.join(directory, 'attribution')
    rows = []
    if os.path.isdir(folder):
        for name in sorted(os.listdir(folder)):
            if name.endswith('.verdict.json') and not name.startswith('control-'):
                v = json.load(open(os.path.join(folder, name), encoding='utf-8'))
                d = v.get('details', {})
                rows.append((name[:-len('.verdict.json')], v['verdict'], d.get('remote_pages', ''), len(d.get('levels_over_shared_cap', [])) if d else '', '; '.join(v.get('reasons', []))[:200]))
    return table(['Attribution run', 'Verdict', 'REMOTE pages', 'Levels over the SHARED cap', 'Reasons'], rows) if rows else ['_No attribution run._']


def caveats(manifest, evaluation, runs, quiet):
    c = []
    if not manifest['gate']:
        c.append('This is NOT a gate session (' + '; '.join(manifest.get('notGateBecause', [])) + '): every budget is NOT JUDGED, and the figures are for information only.')
    if manifest['scale'] != 1.0:
        c.append(f'File counts were scaled by {manifest["scale"]}: a scaled cell is classified by its nominal parameters and gates nothing.')
    if not manifest['loadValidity']:
        c.append('Load validity was skipped (`--no-load-validity`): no quiet check was made, no run was judged on the machine\'s load, and every run is accepted.')
    invalid = [r for r in runs if r['status'] == 'invalid']
    if invalid:
        c.append(f'{len(invalid)} run(s) are invalid (kept in the raw table with their reasons): ' + ', '.join(sorted({r["runId"] for r in invalid}))[:400] + '.')
    seen = [r for r in invalid if r['metrics'].get('problem')]
    if seen:
        c.append(f'{len(seen)} invalid run(s) had shown a behavioural failure before an environmental rule invalidated them (kept in the raw table, with the observation; the replacement runs decide the cell under the accepted rules): '
                 + ', '.join(sorted(r['runId'] for r in seen))[:400] + '.')
    control = evaluation.get('negativeControl')
    if control is not None and not control['ok']:
        c.append('TEST-P1 FAILED: ' + control['why'] + '. The checker is defective, so no PERF-15 (a) verdict of this session can be relied on, and PERF-15 (a) is not met.')
    failed_quiet = [q['name'] for q in quiet if q.get('verdict') == 'NOT QUIET']
    if failed_quiet:
        c.append('Quiet checks that failed: ' + ', '.join(failed_quiet) + '.')
    nm = [f'{b} {x["cell"]}' for b, v in evaluation['budgets'].items() for x in v['cells'] if x['outcome'] == 'NOT MEASURED']
    if nm:
        c.append('NOT MEASURED (counts as MISSED): ' + '; '.join(nm) + '.')
    c.append('Hosted CI figures, if any, are reported beside the reference figures and never meet or miss a target. A figure from a machine other than the reference machine is never an acceptance figure.')
    c.append('PERF-14 is judged on the 2M-file cells (the 1M cells are reported); the attribution, cancel and crash runs are separate runs from the five timed runs (the journal and database copies of an attribution run perturb its timing); '
             'a run of an attribution is valid when the checker gives PASS or FAIL (a deterministic journal needs no quiet machine).')
    return c


def session_report(directory):
    manifest, runs, controls, evaluation = perf_session.rebuild(directory)
    machine = load_json(os.path.join(directory, 'machine.json'))
    quiet = perf_session.read_lines(os.path.join(directory, 'quiet.jsonl'))
    refused = load_json(os.path.join(directory, 'refused.json'))
    aborted = load_json(os.path.join(directory, 'aborted.json'))
    out = [f'## Session {manifest["id"]}', '']
    status = perf_session.status_of(directory, evaluation)
    detail = f' ({refused["reason"]})' if refused else f' ({aborted["error"]}; {aborted.get("runsRecorded", 0)} run(s) recorded before it stopped)' if aborted \
        else ' (a raw table and no outcome: the session did not finish)' if status == 'INCOMPLETE' else ''
    out += [f'Status: **{status}**{detail}.' + (' **Gate session.**' if manifest['gate'] else ' Not a gate session.'), '']
    out += ['### Machine record', ''] + machine_section(machine) + ['']
    out += ['### Manifest (written before the session started)', ''] + manifest_section(manifest) + ['']
    out += ['### Quiet checks', ''] + quiet_section(quiet) + ['']
    out += ['### Runs (raw table: every attempt, valid or not)', ''] + runs_section(runs) + ['']
    out += ['### IMP-11 and resources per run', ''] + imp11_section(runs) + ['']
    out += ['### PERF-15 (a): key-range attribution', ''] + attribution_section(directory) + ['']
    out += ['### Negative control (must FAIL)', ''] + control_section(controls, evaluation) + ['']
    out += ['### Figures per cell', ''] + figures_section(evaluation) + ['']
    out += ['### Outcomes', ''] + outcomes_section(evaluation) + ['']
    out += ['### Caveats', ''] + [f'- {c}' for c in caveats(manifest, evaluation, runs, quiet)] + ['']
    return out


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('sessions', nargs='+')
    ap.add_argument('--out')
    a = ap.parse_args(argv)
    lines = ['# Library benchmark (TEST-P1, §15.4)', '',
             'Generated by `tests/perf/perf_report.py` from the raw files of each session. Units: MiB = 2^20 bytes, MB = 10^6 bytes.', '']
    for s in a.sessions:
        lines += session_report(s)
    text = '\n'.join(lines) + '\n'
    if a.out:
        with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text)
    else:
        sys.stdout.write(text)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
