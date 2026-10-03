"""C4R-M03 and C4R-M04 (the TEST-P1 session tooling): deliberate defects in the Python tooling (tests/perf) and in the C# harness (tests/StorageInventory.Library.Tests/PerfGate),
each applied to a scratch copy of the tree, to show that the tests kill each one for the intended reason.

Same method as Invoke-C4Mutants.ps1 / Invoke-C4RepairMutants.ps1 (exact-once text edits; the source tree is never modified; KILLED when a test of the mutant's own suite fails that
passes in the unmutated baseline AND is one the mutant is meant to be caught by, KILLED (UNINTENDED) when only other tests fail, SURVIVED, NOT COMPILED), and the two suites those
scripts have no kind for:

  python   tests/perf/session_selftest.py, run in the scratch copy (a check that prints BAD is a failing test); no build
  library  tests/StorageInventory.Library.Tests filtered to GateHarnessTests (built and run in the scratch copy with the repo-local SDK of the main checkout)

Usage:  python tests/mutation/perf_session_mutants.py [--suite python|library|all] [--only PS-01,CS-01] [--check-only] [--out results.md] [--work DIR] [--tool-root DIR]
It is a Python script, not PowerShell, because the edits are multi-line Python text with both kinds of quote.
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
PERF = 'tests/perf'
PGATE = 'tests/StorageInventory.Library.Tests/PerfGate'


def edit(file, find, replace):
    return {'file': file, 'find': find, 'replace': replace}


def mutant(id_, suite, what, intended, *edits):
    return {'id': id_, 'suite': suite, 'what': what, 'intended': intended, 'edits': list(edits)}


MUTANTS = [
    # ---------------------------------------------------------------------------------------------------- C4R-M03, Python
    mutant('PS-01', 'python', 'a cancellation the save did not honour (it published) is judged an INVALID run and replaced, as the exit-3 path did', ['M03:'],
           edit(f'{PERF}/perf_session.py', '''                    failed = f'the cancellation was not honoured: {c.get("outcome")}'
                    m['cancelClass'] = 'missed'
''', '''                    reasons.append('the harness child failed (exit 3)')
                    return
''')),
    mutant('PS-02', 'python', 'a miss that took 9 s to return is MISSED, never STOP (PERF-15 (c)\'s stop threshold is not read on a missed run)', ['M03:', 'PERF-15 (c): a cancellation that was not honoured'],
           edit(f'{PERF}/gate_model.py', "outcomes.append('STOP' if over else 'MISSED')", "outcomes.append('MISSED')")),
    mutant('PS-03', 'python', 'a cancel point that was never reached is reported as an ordinary miss (its own class and reason are lost)', ['M03:'],
           edit(f'{PERF}/perf_session.py', "            if c.get('requested') is False:", "            if c.get('requested') is None:")),
    mutant('PS-04', 'python', 'an attribution run whose save failed is handed to the checker, which says INVALID, and is replaced', ['M03:'],
           edit(f'{PERF}/perf_session.py', '''            if rec['outcome'] != 'Published':
                failed = f'the save did not publish: {rec["outcome"]}'       # no journal''', '''            if rec['outcome'] != 'Published' and rec['outcome'] is None:
                failed = f'the save did not publish: {rec["outcome"]}'       # no journal''')),
    mutant('PS-05', 'python', 'what a run showed before the load rule invalidated it (a miss) is dropped from its row', ['M03:'],
           edit(f'{PERF}/perf_session.py', '''                m['problem'] = failed
                reasons.append('what the run showed before it was invalidated: ' + failed)
''', '''                pass
''')),
    mutant('PS-06', 'python', 'the opposite error: a child that died (no record) is a VALID failed run and is never replaced', ['replacement:'],
           edit(f'{PERF}/perf_session.py', '''            return
        if not isinstance(rec, dict):
            reasons.append('the harness child produced no run record')''', '''            run['status'] = 'failed'
            return
        if not isinstance(rec, dict):
            reasons.append('the harness child produced no run record')''')),
    mutant('PS-07', 'python', 'a failed run is not a valid run (it is replaced, like an invalid one)', ['M03:', 'a failed save is a valid run'],
           edit(f'{PERF}/gate_model.py', "    return [r for r in runs_of(runs, cell, kind) if r['status'] in ('ok', 'failed')]", "    return [r for r in runs_of(runs, cell, kind) if r['status'] == 'ok']")),
    mutant('PS-08', 'python', 'a cancel record without its cancel object is read as a miss instead of an unfit record', ['judge:'],
           edit(f'{PERF}/perf_session.py', "    if expected == 'cancel' and not (isinstance(rec.get('cancel'), dict) and 'outcome' in rec['cancel']):", "    if expected == 'cancel' and False:")),
    # ---------------------------------------------------------------------------------------------------- C4R-M04, Python
    mutant('PS-09', 'python', 'run refuses only a directory that holds session.json (the old rule): a refused, aborted or killed attempt runs again in place', ['M04:'],
           edit(f'{PERF}/perf_session.py', "        started = started_files(directory) if a.command == 'run' else []",
                "        started = ['session.json'] if a.command == 'run' and os.path.exists(os.path.join(directory, 'session.json')) else []")),
    mutant('PS-10', 'python', 'run refuses refused.json and session.json only: an aborted or killed attempt (a raw table) runs again in place', ['M04:'],
           edit(f'{PERF}/perf_session.py', "STARTED = ('refused.json', 'aborted.json', 'session.json', 'runs.jsonl', 'judgements.jsonl', 'quiet.jsonl', 'collections.jsonl', 'machine.json', 'events.log')",
                "STARTED = ('refused.json', 'session.json')")),
    mutant('PS-11', 'python', 'rebuild, outcome and the report ignore the negative control', ['M04:'],
           edit(f'{PERF}/perf_session.py', "controls=controls if controls or finished else None)", "controls=None)")),
    mutant('PS-12', 'python', 'a session whose negative control PASSED finishes complete (exit 0)', ['M04:'],
           edit(f'{PERF}/perf_session.py', "        failed = not evaluation['negativeControl']['ok']", "        failed = False")),
    mutant('PS-13', 'python', 'a PASS of the control is cured by a later FAIL', ['control:'],
           edit(f'{PERF}/gate_model.py', "        if 'PASS' in verdicts:", "        if 'PASS' in verdicts and 'FAIL' not in verdicts:")),
    mutant('PS-14', 'python', 'a control that PASSED is captured again (up to five times) until it FAILs', ['M04:'],
           edit(f'{PERF}/perf_session.py', '''                if result['verdict'] == 'PASS':
                    break       # a defective checker: no need to try again
''', '''                pass
''')),
    mutant('PS-15', 'python', 'an unhandled exception exits 1 again, the code of "the negative control failed"', ['exit codes:'],
           edit(f'{PERF}/perf_session.py', '        code = EXIT_CRASHED\n', '        code = 1\n')),
    mutant('PS-16', 'python', 'an interrupt (Ctrl-C) is not recorded as an aborted attempt', ['M04:'],
           edit(f'{PERF}/perf_session.py', '        except BaseException as e:      # noqa: BLE001 - recorded as an aborted session (an interrupt too), then raised',
                '        except Exception as e:      # noqa: BLE001 - recorded as an aborted session (an interrupt too), then raised')),
    mutant('PS-17', 'python', 'a session that completed with a valid result can be superseded (a rerun because of its result)', ['M04:'],
           edit(f'{PERF}/perf_session.py', '            if invalidation is None:', '            if False:')),
    mutant('PS-18', 'python', 'an attempt can be superseded twice', ['M04:'],
           edit(f'{PERF}/perf_session.py', '            if successor:', '            if False:')),
    mutant('PS-19', 'python', 'the refusal record lacks the quiet-check evidence', ['M04:'],
           edit(f'{PERF}/perf_session.py', "reason='the quiet check before the session failed', quietCheck=q,", "reason='the quiet check before the session failed',")),
    mutant('PS-20', 'python', 'a session whose negative control did not FAIL reads complete (status, outcome, attempts, report)', ['M04:'],
           edit(f'{PERF}/perf_session.py', "    return STATUS_CONTROL if control is not None and not control['ok'] else STATUS_COMPLETE", '    return STATUS_COMPLETE')),
    mutant('PS-21', 'python', 'a session whose negative control did not FAIL is not an invalidated session (it cannot be superseded)', ['M04:'],
           edit(f'{PERF}/perf_session.py', "    if status == STATUS_CONTROL:\n        return 'its negative control did not FAIL: the checker is defective'\n", '')),
    mutant('PS-22', 'python', 'an aborted attempt is not written with its error', ['M04:'],
           edit(f'{PERF}/perf_session.py', "abortedUtc=iso(), error=f'{type(e).__name__}: {e}', runsRecorded=len(self.runs))", "abortedUtc=iso(), runsRecorded=len(self.runs))")),
    mutant('PS-23', 'python', 'PERF-15 (a) stays MET when the negative control did not FAIL', ['M04:', 'control:'],
           edit(f'{PERF}/gate_model.py', "        if not control['ok']:\n            b = budgets['PERF-15 (a)']", "        if not control['ok'] and False:\n            b = budgets['PERF-15 (a)']")),
    mutant('PS-24', 'python', 'the report prints no verdict line under the negative control table (a PASSed control is only a row)', ['M04:'],
           edit(f'{PERF}/perf_report.py', "        lines += ['', ('Result: ' + control['outcome'] + ' - ' + control['why'] + '.') if control['ok'] else ('**TEST-P1 FAILED**: ' + control['why'] + '.')]", "        pass")),
    # ---------------------------------------------------------------------------------------------------- C4R-M03, C#
    mutant('CS-01', 'library', 'a cancel record whose save published must carry a rollback again (the harness child exits 3)', ['ACancellationTheSaveDidNotHonour', 'ValidationAcceptsMeasuredFailures', 'ACancelPointThatWasNeverReached'],
           edit(f'{PGATE}/RunRecord.cs', 'if (Has(cancel, "outcome") && cancel.GetProperty("outcome").GetString() != "Published") Need(cancel, "cancel.", "rollback");', 'if (Has(cancel, "outcome")) Need(cancel, "cancel.", "rollback");')),
    mutant('CS-02', 'library', 'a save that failed early (fewer than two IMP-11 checks) is an unfit record again', ['ASaveThatFailsAtBegin', 'ValidationAcceptsMeasuredFailures'],
           edit(f'{PGATE}/RunRecord.cs', 'if (published && Has(imp, "checks", JsonValueKind.Array)', 'if (Has(imp, "checks", JsonValueKind.Array)')),
    mutant('CS-03', 'library', 'the exit-code mapping is reverted: a published cancel run exits 3 again', ['ACancellationTheSaveDidNotHonour', 'ValidationAcceptsMeasuredFailures', 'ACancelPointThatWasNeverReached'],
           edit(f'{PGATE}/GateBenchmark.cs', 'return problems.Count > 0 && mode != "crash" ? 3 : 0;', 'return (problems.Count > 0 || record.Cancel is { Rollback: null, PublishedAfterCancel: false }) && mode != "crash" ? 3 : 0;')),
    mutant('CS-04', 'library', 'a cancel point that was never reached is recorded as requested', ['ACancelPointThatWasNeverReached'],
           edit(f'{PGATE}/GateRunner.cs', 'rollback, published && o.Mode == "cancel-after-final", cancelled);', 'rollback, published && o.Mode == "cancel-after-final", true);')),
    mutant('CS-05', 'library', 'a cancel record without its "requested" field is fit', ['ValidationAcceptsMeasuredFailures'],
           edit(f'{PGATE}/RunRecord.cs', '"point", "cancelToReturnSeconds", "outcome", "requested");', '"point", "cancelToReturnSeconds", "outcome");')),
    mutant('CS-06', 'library', 'a crash run\'s record is no longer exempt from the exit-3 rule', ['ValidationAcceptsMeasuredFailures'],
           edit(f'{PGATE}/GateBenchmark.cs', 'return problems.Count > 0 && mode != "crash" ? 3 : 0;', 'return problems.Count > 0 ? 3 : 0;')),
    mutant('CS-07', 'library', 'a record that cannot be interpreted exits 0 (the orchestrator would judge it)', ['ValidationAcceptsMeasuredFailures'],
           edit(f'{PGATE}/GateBenchmark.cs', 'return problems.Count > 0 && mode != "crash" ? 3 : 0;', 'return 0;')),
]


# ---------------------------------------------------------------------------------------------------------------- scratch copy and runs
def run(cmd, cwd, env=None, timeout=None):
    p = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=timeout)
    return p.returncode, p.stdout + p.stderr


def read(path):
    with open(path, 'rb') as f:
        return f.read().decode('utf-8').replace('\r\n', '\n')


def write(path, text):
    with open(path, 'wb') as f:
        f.write(text.encode('utf-8'))


def tool_env(tool_root, private_temp):
    env = dict(os.environ)
    state = os.path.join(tool_root, 'tools', 'dotnet-state')
    env.update({'DOTNET_CLI_HOME': os.path.join(state, 'home'), 'APPDATA': os.path.join(state, 'appdata'), 'NUGET_PACKAGES': os.path.join(tool_root, 'packages'),
                'NUGET_HTTP_CACHE_PATH': os.path.join(state, 'nuget-http'), 'NUGET_PLUGINS_CACHE_PATH': os.path.join(state, 'nuget-plugins'), 'DOTNET_ROOT': os.path.join(tool_root, 'tools', 'dotnet'),
                'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1', 'DOTNET_MULTILEVEL_LOOKUP': '0', 'MSBUILDDISABLENODEREUSE': '1', 'DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER': '1',
                'TEMP': private_temp, 'TMP': private_temp})
    return env


class Suites:
    def __init__(self, copy, tool_root, private_temp):
        self.copy = copy
        self.tool_root = tool_root
        self.env = tool_env(tool_root, private_temp)

    def python(self):
        code, out = run([sys.executable, os.path.join(self.copy, PERF, 'session_selftest.py')], self.copy, timeout=900)
        failed = [re.sub(r'\s+--\s.*$', '', line[4:]).strip() for line in out.splitlines() if line.startswith('BAD ')]
        summary = next((line for line in reversed(out.splitlines()) if 'self-tests passed' in line or 'failed' in line), '')
        crashed = not any(line.startswith('ok ') for line in out.splitlines())
        return {'compiled': not crashed, 'failed': failed, 'summary': summary or out[-300:]}

    def library(self):
        dotnet = os.path.join(self.tool_root, 'tools', 'dotnet', 'dotnet.exe')
        project = os.path.join(self.copy, 'tests', 'StorageInventory.Library.Tests')
        code, out = run([dotnet, 'build', project, '-c', 'Release', '--nologo', '-v', 'q', '-nodeReuse:false'], self.copy, self.env, timeout=1800)
        if code != 0:
            return {'compiled': False, 'failed': [], 'summary': ' | '.join([line for line in out.splitlines() if 'error' in line][:3])}
        exe = None
        for root, _, names in os.walk(os.path.join(project, 'bin', 'Release')):
            if 'StorageInventory.Library.Tests.exe' in names:
                exe = os.path.join(root, 'StorageInventory.Library.Tests.exe')
                break
        code, out = run([exe, 'GateHarnessTests'], self.copy, self.env, timeout=1800)
        failed = [re.match(r'FAIL\s+(\S+)', line).group(1) for line in out.splitlines() if re.match(r'FAIL\s+(\S+)', line)]
        summary = next((line for line in reversed(out.splitlines()) if line.startswith('RESULT ')), '')
        return {'compiled': True, 'failed': failed, 'summary': summary}


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--source', default=ROOT)
    ap.add_argument('--suite', choices=['python', 'library', 'all'], default='all')
    ap.add_argument('--only', help='comma-separated mutant ids')
    ap.add_argument('--check-only', action='store_true', help='only check that every edit matches exactly once')
    ap.add_argument('--out')
    ap.add_argument('--work')
    ap.add_argument('--tool-root')
    a = ap.parse_args(argv)
    source = os.path.abspath(a.source)
    if a.out and os.path.exists(a.out):
        raise SystemExit(f'--out {a.out} already exists; the script does not overwrite files')
    mutants = [m for m in MUTANTS if a.suite in ('all', m['suite']) and (not a.only or m['id'] in a.only.split(','))]
    work = a.work or tempfile.mkdtemp(prefix='SiPm_')
    if a.work:
        if os.path.exists(work):
            raise SystemExit(f'--work {work} already exists; the script only deletes a folder it made itself')
        os.makedirs(work)
    copy = os.path.join(work, 'tree')
    private_temp = os.path.join(work, 'tmp')
    os.makedirs(private_temp)
    code, listing = run(['git', 'ls-files', '--cached', '--others', '--exclude-standard'], source)
    commit = run(['git', 'rev-parse', 'HEAD'], source)[1].strip()
    dirty = bool(run(['git', 'status', '--porcelain'], source)[1].strip())
    for rel in [x for x in listing.splitlines() if x]:
        src = os.path.join(source, rel)
        if os.path.isfile(src):
            dst = os.path.join(copy, rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copyfile(src, dst)
    results = []
    junction = None
    try:
        for m in mutants:                  # every edit must match exactly once before anything runs
            for e in m['edits']:
                count = read(os.path.join(copy, e['file'])).count(e['find'].replace('\r\n', '\n'))
                if count != 1:
                    raise SystemExit(f'mutant {m["id"]}: the text to replace occurs {count} times in {e["file"]}, expected exactly once')
        print(f'{len(mutants)} mutants: every edit matches exactly once')
        if a.check_only:
            return 0
        tool_root = a.tool_root
        if not tool_root:
            common = run(['git', 'rev-parse', '--path-format=absolute', '--git-common-dir'], source)[1].strip()
            tool_root = os.path.dirname(common) if common else source
            if not os.path.exists(os.path.join(tool_root, 'tools', 'dotnet', 'dotnet.exe')):
                tool_root = source
        if any(m['suite'] == 'library' for m in mutants) and os.path.isdir(os.path.join(tool_root, 'packages')):
            junction = os.path.join(copy, 'packages')
            run(['cmd', '/c', 'mklink', '/J', junction, os.path.join(tool_root, 'packages')], copy)
        suites = Suites(copy, tool_root, private_temp)
        baseline = {}
        for name in sorted({m['suite'] for m in mutants}):
            baseline[name] = getattr(suites, name)()
            print(f'baseline {name}: {baseline[name]["summary"]}')
            if not baseline[name]['compiled'] or baseline[name]['failed']:
                raise SystemExit(f'the unmutated {name} suite does not pass: {baseline[name]["failed"] or baseline[name]["summary"]}')
        for m in mutants:
            originals = {}
            try:
                for e in m['edits']:
                    path = os.path.join(copy, e['file'])
                    originals.setdefault(path, read(path))
                    text = read(path)
                    write(path, text.replace(e['find'].replace('\r\n', '\n'), e['replace'].replace('\r\n', '\n'), 1))
                r = getattr(suites, m['suite'])()
                if not r['compiled']:
                    verdict, killers = 'NOT COMPILED', []
                else:
                    killers = [f for f in r['failed'] if f not in baseline[m['suite']]['failed']]
                    intended = [f for f in killers if any(i in f for i in m['intended'])]
                    verdict = 'KILLED' if intended else 'KILLED (UNINTENDED)' if killers else 'SURVIVED'
            finally:
                for path, text in originals.items():
                    write(path, text)
            results.append({**m, 'verdict': verdict, 'killers': killers, 'summary': r['summary']})
            print(f'{m["id"]:6} {m["suite"]:8} {verdict:20} {", ".join(killers[:3])}', flush=True)
    finally:
        if junction and os.path.exists(junction):
            os.rmdir(junction)
        shutil.rmtree(work, ignore_errors=True)
    lines = ['# TEST-P1 session tooling: mutants of C4R-M03 and C4R-M04', '',
             f'Commit `{commit}`{" (the working tree had uncommitted changes when this ran)" if dirty else ""}, `tests/mutation/perf_session_mutants.py`.', '',
             'KILLED: a test of the mutant\'s own suite fails that passes unmutated and is one the mutant is meant to be caught by; KILLED (UNINTENDED): only other tests fail; SURVIVED: none fails.', '',
             '| Mutant | Suite | Defect | Result | Failing tests (not failing unmutated) |', '|---|---|---|---|---|']
    for r in results:
        shown = '<br>'.join(f'`{k[:110]}`' for k in r['killers'][:4]) + (f'<br>(+{len(r["killers"]) - 4} more)' if len(r['killers']) > 4 else '')
        lines.append(f'| {r["id"]} | {r["suite"]} | {r["what"]} | **{r["verdict"]}** | {shown} |')
    killed = sum(1 for r in results if r['verdict'] == 'KILLED')
    lines += ['', f'{len(results)} mutants: {killed} killed, {sum(1 for r in results if r["verdict"] == "KILLED (UNINTENDED)")} killed unintended, '
              f'{sum(1 for r in results if r["verdict"] == "SURVIVED")} survived, {sum(1 for r in results if r["verdict"] == "NOT COMPILED")} not compiled.']
    print('\n'.join(lines))
    if a.out:
        write(a.out, '\n'.join(lines) + '\n')
    return 0 if killed == len(results) else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
