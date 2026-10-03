"""TEST-P1's session orchestrator (§15.4 "Method", "Session", "Validity", "Replacement and statistic", "Outcomes"; PERF-01, PERF-14, PERF-15, PERF-16).

Commands (see README.md):

  perf_session.py declare --evidence DIR --exe EXE [--dll DLL] [--gate] [...]    writes the SESSION MANIFEST (manifest.json) before anything runs
  perf_session.py run     --session DIR   --exe EXE [--dll DLL] [...]            runs a declared session: quiet check, warm-up, rounds, blocks, replacements
  perf_session.py go      ...                                                    declare and run
  perf_session.py outcome --session DIR                                          recomputes session.json and the outcomes from the raw files
  perf_session.py attempts --evidence DIR [--json]                               lists every session (attempt) of an evidence directory with its status

A session, in order: the manifest (identifier, binary commit and build-output hash, cells, run order, five rounds, declared start, gate or not) is
written FIRST; prefills are built once per cell's prefill parameters by a separate process of the same binary; the QUIET CHECK runs before the
session (a failure refuses it, recorded) and after every round; one warm-up run of the first cell is logged and not recorded; then exactly five
measured runs per cell, round-robin over the cells, each its own child process over a flushed copy of its prefill; then the PERF-15 blocks (the
key-range attribution run of every cell, the four cancel points of every non-informational cell, the kill at the journal's peak with the timed
start-up open, the deletion of a 2M snapshot), each followed by a quiet check; the negative control once; replacement runs for every invalidated run
(at most five more attempts; NOT MEASURED after ten attempts of a five-run cell); the outcomes per budget.

Run validity is decided by the measured environment only (loadsource.py): U = the whole machine less the benchmark's own job-object time, over the
collections WHOLLY inside the measured operation; mean <= 10% and p95 <= 25%, none inside is INVALID. A round whose quiet check after it fails is
invalid and its load-dependent runs are marked invalid and replaced. No valid run is ever discarded or replaced because of its result. A key-range
attribution is valid when the checker (attribute_run.py) gives PASS or FAIL; INVALID evidence is captured again (a deterministic journal needs no quiet machine).

A session directory is an ATTEMPT and is run ONCE. `run` refuses a directory that holds anything of a started session (refused.json, aborted.json,
runs.jsonl, session.json, ...): a refused, aborted or interrupted attempt is kept as it is, never run again in place, and a retry is a NEW session declared
with --supersedes <earlier id> --reason <text> (§15.4 "Reruns": a new session starts only after a documented objective invalidation and is declared as
such). Only a session that was refused, aborted, never completed, whose negative control did not FAIL, or that has cells NOT MEASURED can be superseded;
a session that completed with a valid result, whatever the result, cannot (a rerun because of its result is what the rules forbid). The negative control is
part of the result: a session whose control did not FAIL is TEST-P1 FAILED (session.json, `outcome`, the report) and its PERF-15 (a) is TEST-P1 FAILED.

--no-load-validity (smoke only): the quiet checks and the run rule are skipped, EVERY run is accepted as valid, and the session is NOT a gate session
(its manifest says so before it starts); its budgets are reported as NOT JUDGED. A scaled session (--scale) or one with fewer than the whole matrix or
five rounds is likewise never a gate session.
"""
import argparse
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gate_exe  # noqa: E402
import gate_model as model  # noqa: E402

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

SCHEMA = 1
QUIET_SECONDS = 60
QUIET_COLLECTIONS = 120
CHILD_TIMEOUT = 4 * 3600
DELETE_CELLS = ('F-2M-25-system',)           # the deletion of a 2M snapshot (PERF-10, informational)
AFTER_FINAL_CELLS = ('F-2M-25-system',)      # CAN-01e: a cancellation after the final check publishes (recorded once)
CANCEL_KINDS = ('cancel:1', 'cancel:2', 'cancel:3', 'cancel:4')
BLOCK_ORDER = ['attribution', 'cancel:1', 'cancel:2', 'cancel:3', 'cancel:4', 'crash', 'cancel-after-final', 'delete']
LOAD_INDEPENDENT = ('attribution', 'control')       # judged by the checker, not by the load: a journal is deterministic

# Exit codes of this tool: 0 done (read the outcomes), 1 the negative control did not FAIL (TEST-P1 FAILED), 2 refused or usage (a declaration the rules
# refuse, a session directory that already started), 3 the quiet check before the session failed (refused.json), 4 aborted (an OSError; aborted.json),
# 5 the tool itself failed (an unhandled exception; for a session already started aborted.json says so). Distinct on purpose: an unhandled exception
# used to exit 1, the code of "the negative control failed".
EXIT_DONE, EXIT_FAILED, EXIT_USAGE, EXIT_REFUSED, EXIT_ABORTED, EXIT_CRASHED = 0, 1, 2, 3, 4, 5
HARNESS_UNFIT = 3         # the harness child's exit code for a record it cannot interpret as raw evidence (GateBenchmark.ExitCodeOf); every other result exits 0
STATUS_COMPLETE = 'complete'
STATUS_CONTROL = 'TEST-P1 FAILED: the negative control did not FAIL'
STARTED = ('refused.json', 'aborted.json', 'session.json', 'runs.jsonl', 'judgements.jsonl', 'quiet.jsonl', 'collections.jsonl', 'machine.json', 'events.log')   # files only a started session leaves


class Refusal(Exception):
    pass


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc)


def iso(dt=None):
    return (dt or utc_now()).strftime('%Y-%m-%dT%H:%M:%S.%fZ')


def epoch_of(text):
    return datetime.datetime.fromisoformat(text.replace('Z', '+00:00')).timestamp()


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def append_line(path, obj):
    with open(path, 'a', encoding='utf-8', newline='\n') as f:
        f.write(json.dumps(obj, separators=(',', ':')) + '\n')
        f.flush()
        os.fsync(f.fileno())


def read_lines(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding='utf-8') as f:
        return [json.loads(line) for line in f if line.strip()]


def write_json(path, obj):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(json.dumps(obj, indent=1, ensure_ascii=False) + '\n')


# ---------------------------------------------------------------------------------------------------------------- attempts
def started_files(directory):
    """What a session directory holds of a started session (empty for a declared session that never ran)."""
    return [n for n in STARTED if os.path.exists(os.path.join(directory, n))]


def status_of(directory, evaluation):
    """The status of an attempt, from its raw files and its evaluation: the same words in session.json, `outcome`, `attempts` and the report."""
    if os.path.exists(os.path.join(directory, 'refused.json')):
        return 'REFUSED'
    if os.path.exists(os.path.join(directory, 'aborted.json')):
        return 'ABORTED'
    if not os.path.exists(os.path.join(directory, 'session.json')):
        return 'INCOMPLETE' if started_files(directory) else 'declared (not run)'
    control = evaluation.get('negativeControl')
    return STATUS_CONTROL if control is not None and not control['ok'] else STATUS_COMPLETE


def attempts(evidence):
    """Every session (attempt) of an evidence directory, in declaration order, with its status. Refused, aborted and incomplete attempts are listed like any
    other: an attempt is never removed or overwritten."""
    rows = []
    for name in sorted(os.listdir(evidence)) if os.path.isdir(evidence) else []:
        directory = os.path.join(evidence, name)
        if not os.path.exists(os.path.join(directory, 'manifest.json')):
            continue
        manifest, _, _, evaluation = rebuild(directory)
        rows.append({'id': manifest['id'], 'directory': name, 'gate': manifest['gate'], 'supersedes': manifest.get('supersedes'), 'reason': manifest.get('reason'),
                     'declaredUtc': manifest['declaredUtc'], 'binaryHash': manifest['binary']['outputHash'], 'status': status_of(directory, evaluation)})
    return sorted(rows, key=lambda r: (r['declaredUtc'], r['id']))


def invalidation_reason(directory):
    """The documented objective invalidation of an attempt (§15.4 "Reruns": an invalid session, or cells NOT MEASURED), derived from its files; None when
    it has not been invalidated. A session that completed with a valid result, whatever the result, is never invalidated: a rerun because of its result is
    exactly what the rules forbid."""
    _, _, _, evaluation = rebuild(directory)
    status = status_of(directory, evaluation)
    if status == 'REFUSED':
        return 'it was refused: the quiet check before the session failed'
    if status == 'ABORTED':
        return 'it aborted before it completed (aborted.json)'
    if status == 'INCOMPLETE':
        return 'it never completed (a raw table and no session.json: the process died)'
    if status == STATUS_CONTROL:
        return 'its negative control did not FAIL: the checker is defective'
    not_measured = sorted({f'{b} {c["cell"]}' for b, v in evaluation['budgets'].items() for c in v['cells'] if c['outcome'] == 'NOT MEASURED'})
    return ('cells NOT MEASURED: ' + ', '.join(not_measured)) if not_measured else None


# ---------------------------------------------------------------------------------------------------------------- the manifest
def harness_of(a):
    return gate_exe.Harness(a.exe, a.dll, a.bench_root)


def plan_of(h, scale, commit, dirty):
    args = ['plan', '--scale', repr(scale), '--binary-commit', commit] + (['--dirty'] if dirty else [])
    code, _, plan, err = h.run(*args, timeout=300)
    if code != 0 or plan is None:
        raise Refusal(f'the harness could not produce its plan ({code}): {err[-300:]}')
    for c in plan['cells']:
        if c['class'] != c['statedClass']:
            raise Refusal(f'cell {c["id"]}: the classifier says {c["class"]}, §15.4\'s matrix says {c["statedClass"]}')
    return plan


def declare(a):
    commit, dirty = gate_exe.git_identity(gate_exe.repo_root())
    h = harness_of(a)
    plan = plan_of(h, a.scale, commit, dirty)
    all_ids = [c['id'] for c in plan['cells']]
    ids = a.cells.split(',') if a.cells else all_ids
    unknown = [i for i in ids if i not in all_ids]
    if unknown:
        raise Refusal('unknown cell(s) ' + ', '.join(unknown))
    ids = [i for i in all_ids if i in ids]          # the matrix's order
    load_validity = not a.no_load_validity
    reasons = []
    if a.scale != 1.0:
        reasons.append(f'scale {a.scale} (a scaled cell gates nothing)')
    if ids != all_ids:
        reasons.append('not the whole matrix')
    if a.rounds != 5:
        reasons.append(f'{a.rounds} rounds, not five')
    if not load_validity:
        reasons.append('--no-load-validity')
    if dirty:
        reasons.append('the working tree has uncommitted changes')
    gate = bool(a.gate)
    if gate and reasons:
        raise Refusal('a gate session covers the whole matrix at scale 1 with five rounds, load validity and a clean tree; this one has: ' + '; '.join(reasons))
    session_id = a.id or f'{utc_now().strftime("%Y%m%d-%H%M%S")}-{commit[:8]}-{"gate" if gate else "nongate"}'
    invalidation = None
    if gate:
        earlier = [x for x in attempts(a.evidence) if x['gate'] and x['binaryHash'] == plan['binary']['outputHash']]
        if earlier and not a.supersedes:
            raise Refusal('the first session declared on this binary is the gate session; ' + ', '.join(x['id'] for x in earlier) + ' already is. A later one is designated only when '
                          'an earlier one was invalidated by the rules (--supersedes <id> --reason <text>)')
        if a.supersedes and a.supersedes not in [x['id'] for x in earlier]:
            raise Refusal(f'--supersedes {a.supersedes}: no earlier gate session of this binary has that id')
        if a.supersedes and not a.reason:
            raise Refusal('--supersedes needs --reason')
        if a.supersedes:
            successor = next((x['id'] for x in earlier if x['supersedes'] == a.supersedes), None)
            if successor:
                raise Refusal(f'--supersedes {a.supersedes}: that attempt is already superseded by {successor}; a retry names the latest attempt of the binary')
            invalidation = invalidation_reason(os.path.join(a.evidence, next(x['directory'] for x in earlier if x['id'] == a.supersedes)))
            if invalidation is None:
                raise Refusal(f'--supersedes {a.supersedes}: that session has not been invalidated by the rules (refused, aborted, never completed, its negative control did not FAIL, '
                              'or cells NOT MEASURED); a rerun because of a valid result is not allowed (§15.4 Reruns)')
    directory = os.path.join(a.evidence, session_id)
    if os.path.exists(os.path.join(directory, 'manifest.json')):
        raise Refusal('a manifest already exists for ' + session_id)
    os.makedirs(directory, exist_ok=True)
    chosen = [c for c in plan['cells'] if c['id'] in ids]
    kinds_of = {}
    for c in chosen:
        k = list(c['kinds'])
        if c['id'] in AFTER_FINAL_CELLS:
            k.append('cancel-after-final')
        if c['id'] in DELETE_CELLS:
            k.append('delete')
        kinds_of[c['id']] = k
    manifest = {
        'schema': SCHEMA, 'id': session_id, 'declaredUtc': iso(), 'declaredStartUtc': iso(), 'gate': gate,
        'supersedes': a.supersedes, 'reason': a.reason, 'invalidationOfSuperseded': invalidation,
        'notGateBecause': [] if gate else (reasons or ['--gate was not given']),
        'binary': plan['binary'], 'scale': a.scale, 'generatorVersion': plan['generatorVersion'],
        'cells': ids, 'rounds': a.rounds, 'runOrder': 'round-robin: run 1 of every cell, then run 2, and so on',
        'plannedRounds': [{'round': r, 'cells': ids} for r in range(1, a.rounds + 1)],
        'blocksAfterRounds': [k for k in BLOCK_ORDER if any(k in kinds_of[i] for i in ids)] + ['control'],
        'kindsOfCell': kinds_of,
        'loadValidity': load_validity,
        'quietCheck': {'seconds': QUIET_SECONDS, 'collections': QUIET_COLLECTIONS, 'meanMax': 5.0, 'p95Max': 15.0, 'when': 'before the session and after every round and block'},
        'runRule': {'meanMax': 10.0, 'p95Max': 25.0, 'window': 'collections wholly inside the measured operation; none inside is INVALID'},
        'replacement': {'maxExtraAttempts': 5, 'statistic': 'median of the first five valid runs in run order, with minimum and maximum'},
        'plan': {'cells': chosen},
    }
    write_json(os.path.join(directory, 'manifest.json'), manifest)
    print(f'declared session {session_id}: {"GATE SESSION" if gate else "not a gate session (" + "; ".join(manifest["notGateBecause"]) + ")"}')
    print(directory)
    return directory


# ---------------------------------------------------------------------------------------------------------------- the session
class Session:
    def __init__(self, directory, a):
        self.dir = directory
        self.a = a
        self.manifest = json.load(open(os.path.join(directory, 'manifest.json'), encoding='utf-8'))
        self.h = harness_of(a)
        self.cells = {c['id']: c for c in self.manifest['plan']['cells']}
        self.order = self.manifest['cells']
        self.scale = self.manifest['scale']
        self.binary = self.manifest['binary']
        self.load_validity = self.manifest['loadValidity']
        self.analysis = os.path.join(directory, 'analysis')
        os.makedirs(self.analysis, exist_ok=True)
        os.makedirs(os.path.join(directory, 'attribution'), exist_ok=True)
        self.bench_root = a.bench_root or os.environ.get('SI_BENCH_ROOT') or os.environ.get('TEMP', '.')
        self.cache = a.prefill_cache or os.path.join(self.bench_root, 'SI-Gate-PrefillCache')
        self.runs = []                        # gate_model run dicts, chronological
        self.order_counter = 0
        self.job = None
        self.sampler = None
        self.controls = []
        self.quiet_checks = []
        self.stopped = None
        model.set_rounds(self.manifest['rounds'])

    # ---- logging
    def log(self, text):
        line = f'{iso()} {text}'
        print(line, flush=True)
        with open(os.path.join(self.dir, 'events.log'), 'a', encoding='utf-8', newline='\n') as f:
            f.write(line + '\n')

    def scrub(self, text):
        """No path of this machine in a kept record: the user profile, the temporary folder, the benchmark root and the session directory become tokens."""
        for raw, token in ((getattr(self, 'dir', ''), '<session>'), (getattr(self, 'bench_root', ''), '<scratch>'), (getattr(self, 'cache', ''), '<cache>'), (os.environ.get('TEMP', ''), '<temp>'), (os.path.expanduser('~'), '<user>')):
            if raw:
                for form in {raw, raw.replace('\\', '/'), raw.replace('/', '\\')}:
                    text = text.replace(form, token)
        return text

    # ---- the load source
    def start_load(self):
        import loadsource
        self.ls = loadsource
        try:
            self.job = loadsource.Job()
            self.job.assign_self()
            self.sampler = loadsource.Sampler(self.job)
            self.sampler.start()
        except OSError as e:
            if self.load_validity:
                raise Refusal(f'the load source could not start (a gate session needs it): {e}')
            self.log(f'the load source is unavailable ({e}); this session is not judged on load anyway')
            self.job = self.sampler = None

    def stop_load(self):
        if self.sampler:
            self.sampler.stop()
            with open(os.path.join(self.dir, 'collections.jsonl'), 'a', encoding='utf-8', newline='\n') as f:
                for i, c in enumerate(self.sampler.series):
                    f.write(json.dumps({'i': i, **c}, separators=(',', ':')) + '\n')

    def quiet_check(self, name):
        """§15.4's quiet check: 120 collections (60 s) with no benchmark operation running; U mean <= 5% and p95 <= 15%."""
        if not self.load_validity:
            rec = {'name': name, 'skipped': True, 'reason': '--no-load-validity', 'verdict': 'SKIPPED'}
            self.quiet_checks.append(rec)
            append_line(os.path.join(self.dir, 'quiet.jsonl'), rec)
            return rec
        start = time.time()
        while True:
            self.sampler.check()
            window = [c for c in self.sampler.series if c['t0'] >= start][:QUIET_COLLECTIONS]
            if len(window) >= QUIET_COLLECTIONS:
                break
            time.sleep(0.25)
        v = self.ls.quiet_verdict(window)
        rec = {'name': name, 'startUtc': iso(datetime.datetime.fromtimestamp(window[0]['t0'], datetime.timezone.utc)), 'collections': len(window),
               'verdict': v['verdict'], 'U': {'mean': v['mean'], 'p95': v['p95'], 'max': v['max']}, 'reasons': v['reasons'], 'summary': self.ls.summarise(window)}
        self.quiet_checks.append(rec)
        self.last_quiet_window = list(window)           # the raw collections of the check: a refusal carries them (refused.json)
        append_line(os.path.join(self.dir, 'quiet.jsonl'), rec)
        self.log(f'quiet check "{name}": {v["verdict"]} (U mean {v["mean"]:.2f}%, p95 {v["p95"]:.2f}%, {len(window)} collections)')
        return rec

    # ---- caches
    def ensure_prefill(self, cell):
        key = cell['prefillKey']
        directory = os.path.join(self.cache, key)
        main = os.path.join(directory, 'main.sqlite3')
        marker = os.path.join(directory, 'main.ok')
        if os.path.exists(main) and os.path.exists(marker):
            return main, directory
        os.makedirs(directory, exist_ok=True)
        self.log(f'building prefill {key}')
        code, lines, info, err = self.child('prefill', '--cell', cell['id'], '--scale', repr(self.scale), '--out', main, what='prefill')
        if code != 0:
            raise OSError(f'the prefill {key} failed ({code}): {err[-300:]}')
        write_json(marker, {'key': key, 'built': info})
        return main, directory

    def ensure_stats(self, cell, directory):
        stats = os.path.join(directory, f'stats-{cell["id"]}-s{self.scale}.json')
        if not os.path.exists(stats):
            code, _, _, err = self.child('describe', '--cell', cell['id'], '--scale', repr(self.scale), '--out', stats, what='describe')
            if code != 0:
                raise OSError(f'the statistics of {cell["id"]} failed ({code}): {err[-300:]}')
        return stats

    # ---- children
    def child(self, *args, what='child'):
        """Runs one benchmark-owned child to completion; fails loudly if it is not in the job."""
        before = self.job.cpu_seconds()[1] if self.job else None
        proc = self.h.start(*args)
        if self.job:
            try:
                self.job.require_member(proc.pid, what)
            except OSError as e:
                if proc.poll() is None:
                    proc.kill()
                    raise
                if 'OpenProcess' not in str(e):
                    raise
        try:
            out, err = proc.communicate(timeout=CHILD_TIMEOUT)
        except subprocess.TimeoutExpired:
            proc.kill()
            out, err = proc.communicate()
            return -9, out.splitlines(), None, err + '\n(timed out)'
        if self.job:
            counted = self.job.cpu_seconds()[1] - before
            if counted < 1:
                raise OSError(f'{what} was not accounted by the benchmark job (TotalProcesses unchanged): its CPU time would count as other load')
        return proc.returncode, out.splitlines(), gate_exe.last_json(out.splitlines()), err

    # ---- one run
    def run_args(self, cell, kind, label, main, stats):
        args = ['run', '--cell', cell['id'], '--mode', kind, '--scale', repr(self.scale), '--prefill-from', main, '--stats', stats,
                '--analysis-dir', self.analysis, '--label', label, '--binary-commit', self.binary['commit']]
        if self.binary['dirty']:
            args.append('--dirty')
        return args

    def execute(self, cell, kind, round_name, record=True):
        """One attempt of one slot: runs the child(ren), writes the raw record, judges the run, returns the run dict (None when not recorded)."""
        main, directory = self.ensure_prefill(cell)
        stats = self.ensure_stats(cell, directory)
        attempt = len(model.runs_of(self.runs, cell['id'], kind)) + 1
        label = f'{cell["id"]}-{kind.replace(":", "")}-a{attempt}-{round_name}'
        self.log(f'run {label}')
        started = time.time()
        code, lines, rec, err = self.child(*self.run_args(cell, kind, label, main, stats), what=f'measuring child of {label}')
        ended = time.time()
        recovery = None
        recovery_window = None
        if kind == 'crash' and rec is not None and rec.get('kind') == 'crash':
            rstart = time.time()
            rcode, _, rrec, rerr = self.child('recover', '--dir', rec['killedLibraryDirectory'], '--appdata', rec['killedAppData'],
                                              '--snapshots', str(rec['library']['snapshotsBefore']), what=f'recovery child of {label}')
            recovery_window = (rstart, time.time())
            recovery = rrec if rcode == 0 else None
            try:
                shutil.rmtree(os.path.dirname(rec['killedAppData']), ignore_errors=True)
            except OSError:
                pass
            rec['recovery'] = recovery
            rec['recoveryExit'] = rcode
            rec['killedLibraryDirectory'] = rec['killedAppData'] = '<scratch>'       # no path of the machine is kept in the raw table
            if recovery is None:
                err += f'\nrecovery child failed ({rcode}): {rerr[-200:]}'
        envelope = {'session': self.manifest['id'], 'runId': label, 'cell': cell['id'], 'kind': kind, 'attempt': attempt, 'round': round_name, 'exitCode': code,
                    'startedUtc': iso(datetime.datetime.fromtimestamp(started, datetime.timezone.utc)), 'endedUtc': iso(datetime.datetime.fromtimestamp(ended, datetime.timezone.utc)),
                    'childWallSeconds': round(ended - started, 3), 'stderrTail': self.scrub(err[-400:]) if err else ''}
        line = dict(rec) if isinstance(rec, dict) else {'kind': 'failed', 'label': label}
        line['envelope'] = envelope
        append_line(os.path.join(self.dir, 'runs.jsonl'), line)
        self.order_counter += 1
        run = {'runId': label, 'cell': cell['id'], 'kind': kind, 'attempt': attempt, 'round': round_name, 'order': self.order_counter, 'status': 'invalid', 'reasons': [],
               'metrics': {}, 'load': None}
        self.judge(run, cell, code, rec, err, recovery, recovery_window, line)
        self.runs.append(run)
        append_line(os.path.join(self.dir, 'judgements.jsonl'), {'event': 'judged', **run})
        self.log(f'  -> {run["status"]}' + (': ' + '; '.join(run['reasons']) if run['reasons'] else '') + f' {summary_of(run)}')
        return run

    def judge(self, run, cell, code, rec, err, recovery, recovery_window, line):
        """Fills run['status'], ['reasons'], ['metrics'], ['load'] from the raw record and the measured environment."""
        reasons = run['reasons']
        kind = run['kind']
        if code not in (0, -1) and not (kind == 'crash' and rec and rec.get('kind') == 'crash'):
            # (iii) invalid harness or evidence: the child died, or refused its own record (exit 3: it cannot be interpreted as raw evidence). Replaceable under
            # the accepted environmental rule. Every RESULT of a run (a failed save, a cancellation not honoured) exits 0 and is judged below, never here (C4R-M03).
            fit = ': its run record is not fit as raw evidence' if code == HARNESS_UNFIT else ''
            reasons.append(self.scrub(f'the harness child failed (exit {code}{fit}): {(err or "").strip().splitlines()[-1][:160] if err and err.strip() else "no message"}'))
            return
        if not isinstance(rec, dict):
            reasons.append('the harness child produced no run record')
            return
        problems = fit_problems(rec, kind)
        if problems:
            reasons.append('the run record is not fit as raw evidence: ' + '; '.join(problems))
            return
        # ---- the measured operation's window, and the load rule
        if kind == 'crash':
            if recovery_window is None:
                reasons.append('the kill did not happen or the recovery child did not run')
                return
            window = recovery_window
        else:
            start = epoch_of(rec['operationStartUtc'])
            window = (start, start + rec['operationSeconds'])
        load_ok = True
        if self.load_validity and kind not in LOAD_INDEPENDENT:
            self.sampler.wait_until(window[1] + 0.0)
            v = self.ls.run_verdict(list(self.sampler.series), window[0], window[1])
            run['load'] = {'verdict': v['verdict'], 'n': v['n'], 'mean': v['mean'], 'p95': v['p95'], 'max': v['max'], 'window': [round(window[0], 3), round(window[1], 3)]}
            if v['verdict'] != 'VALID':
                load_ok = False
                reasons.extend(v['reasons'] or ['the run rule is not met'])
        elif not self.load_validity:
            run['load'] = {'verdict': 'NOT ASSESSED', 'reason': '--no-load-validity'}
        m = run['metrics']
        token = rec.get('token') or {}
        if token.get('maxGapSeconds') is not None:
            m['tokenGapSeconds'] = token['maxGapSeconds']
        failed = None
        if kind == 'timed' or kind == 'delete':
            if rec['outcome'] != 'Published':
                failed = f'the save did not publish: {rec["outcome"]}'
            else:
                m.update({'filesPerSecond': rec['filesPerSecond'], 'importSeconds': rec['importSeconds'], 'files': rec['files'], 'newNames': rec['newNames'],
                          'peakJournalBytes': rec['journal']['lengthAtFinalCheck'], 'peakWorkingSet': rec['memory']['peakWorkingSetBytes'],
                          'libraryGrowthBytes': rec['library']['growthBytes']})
                imp = rec['imp11']
                m['imp11'] = {'checks': imp['spaceChecks'], 'commitEqualsFinalPending': imp['commitGrowthEqualsFinalPending'], 'intervalsOverAllowance': imp['intervalsOverAllowance'],
                              'largestIntervalOverAllowance': imp['largestIntervalGrowthOverAllowance']}
                if kind == 'delete':
                    m['deleteSeconds'] = (rec.get('delete') or {}).get('seconds')
        elif kind == 'attribution':
            if rec['outcome'] != 'Published':
                failed = f'the save did not publish: {rec["outcome"]}'       # no journal to attribute: a valid failed run, never replaced (C4R-M03)
            else:
                v = self.attribute(run, line)
                m['attribution'] = v['verdict'] if v['verdict'] in ('PASS', 'FAIL') else None
                if rec.get('imp11'):
                    m['imp11'] = {'checks': rec['imp11']['spaceChecks'], 'commitEqualsFinalPending': rec['imp11']['commitGrowthEqualsFinalPending']}
                if v['verdict'] not in ('PASS', 'FAIL'):
                    reasons.extend(['attribution INVALID: ' + r for r in v['reasons'][:3]])
                    return
                run['status'] = 'ok'
                return
        elif kind == 'cancel-after-final':
            if rec['outcome'] != 'Published' or not (rec.get('cancel') or {}).get('publishedAfterCancel'):
                failed = f'CAN-01e: a cancellation after the final check must publish, but the outcome was {rec["outcome"]}'
            else:
                m['publishedAfterCancel'] = True
        elif kind.startswith('cancel:'):
            # (i) an EXPECTED cancellation: requested, rolled back, asserted. (ii) a MISS, a valid measured behavioural failure that stays in the record and is never
            # replaced: the cancellation was requested and the save published (or failed) instead, the rollback did not hold, or the point was never reached.
            # The time from a requested cancellation to the return is kept for a miss too: PERF-15 (c)'s stop threshold reads it. (iii) invalid evidence returned above.
            c = rec.get('cancel') or {}
            rb = c.get('rollback') or {}
            if c.get('requested') is False:
                failed = f'the cancellation was never requested (cancel point {c.get("point")} was not reached): the save {c.get("outcome")}'
                m['cancelClass'] = 'never requested'
            else:
                if isinstance(c.get('cancelToReturnSeconds'), (int, float)):
                    m['cancelSeconds'] = c['cancelToReturnSeconds']
                if c.get('outcome') != 'Cancelled (rolled back)':
                    failed = f'the cancellation was not honoured: {c.get("outcome")}'
                    m['cancelClass'] = 'missed'
                elif not rb.get('rolledBack'):
                    failed = 'rollback assertion failed: ' + str(rb.get('problem'))
                    m['cancelClass'] = 'missed'
                else:
                    m['cancelClass'] = 'expected cancellation'
                    m['journalAtCancelBytes'] = c['journalAtCancelBytes']
                    m['rowsAtCancel'] = c['rowsAtCancel']
        elif kind == 'crash':
            r = recovery or {}
            if not r:
                failed = 'the recovery child produced no record'
            elif r.get('problem'):
                failed = 'recovery: ' + r['problem']
            elif r.get('state') != 'Available':
                failed = f'recovery left the Library {r.get("state")}'
            else:
                m['recoverySeconds'] = r['openSeconds']
                m['hotJournalBytes'] = r['hotJournalBytes']
        if not load_ok:
            run['status'] = 'invalid'
            if failed:         # replaced under the load rule, but what the run showed is not lost: it stays in the row and the report names it
                m['problem'] = failed
                reasons.append('what the run showed before it was invalidated: ' + failed)
            return
        if failed:
            run['status'] = 'failed'
            m['problem'] = failed
            reasons.append(failed)
            return
        run['status'] = 'ok'

    def attribute(self, run, line):
        """The PERF-15 (a) verdict of an attribution run, from the checker's JSON (not only its exit code)."""
        label = run['runId']
        path = os.path.join(self.dir, 'attribution', label + '.record.json')
        write_json(path, line)
        p = subprocess.run([sys.executable, os.path.join(HERE, 'attribute_run.py'), path, '--analysis-dir', self.analysis], capture_output=True, text=True, encoding='utf-8')
        try:
            out = json.loads(p.stdout.strip().splitlines()[-1])
        except (ValueError, IndexError):
            out = {'verdict': 'INVALID', 'reasons': ['the checker printed no verdict: ' + (p.stderr or '')[-200:]]}
        expected = {'PASS': 0, 'FAIL': 1, 'INVALID': 2}.get(out.get('verdict'))
        if expected is None or p.returncode != expected:
            out = {'verdict': 'INVALID', 'reasons': [f'the checker\'s exit code {p.returncode} disagrees with its verdict {out.get("verdict")!r}']}
        write_json(os.path.join(self.dir, 'attribution', label + '.verdict.json'), out)
        self.log(f'  attribution {out["verdict"]}')
        if not self.a.keep_analysis:
            for k in ('databaseCopy',):
                name = (line.get('attribution') or {}).get(k)
                if name:
                    try:
                        os.remove(os.path.join(self.analysis, name))
                    except OSError:
                        pass
        return out

    # ---- rounds, blocks, replacement
    def invalidate_round(self, round_name, why):
        for r in self.runs:
            if r['round'] == round_name and r['kind'] not in LOAD_INDEPENDENT and r['status'] in ('ok', 'failed'):
                r['status'] = 'invalid'
                r['reasons'].append(why)
                append_line(os.path.join(self.dir, 'judgements.jsonl'), {'event': 'round-invalidated', 'runId': r['runId'], 'round': round_name, 'reason': why})
                self.log(f'  {r["runId"]} invalidated: {why}')

    def after_round(self, round_name):
        q = self.quiet_check(f'after {round_name}')
        if q['verdict'] == 'NOT QUIET':
            self.invalidate_round(round_name, f'the quiet check after its round failed (U mean {q["U"]["mean"]:.2f}%, p95 {q["U"]["p95"]:.2f}%)')

    def kinds_for(self, cid):
        return self.manifest['kindsOfCell'][cid]

    def warmup(self):
        cell = self.cells[self.order[0]]
        main, directory = self.ensure_prefill(cell)
        stats = self.ensure_stats(cell, directory)
        self.log(f'warm-up of {cell["id"]} (logged, not recorded)')
        code, lines, rec, err = self.child(*self.run_args(cell, 'timed', f'warmup-{cell["id"]}', main, stats), what='warm-up child')
        if rec:
            self.log(f'  warm-up: {rec.get("outcome")}, {rec.get("importSeconds", 0):.2f} s, {rec.get("filesPerSecond", 0):,.0f} rows/s (not recorded)')
        else:
            self.log(f'  warm-up: no record (exit {code})')

    def go(self):
        m = self.manifest
        self.log(f'session {m["id"]} ({"GATE SESSION" if m["gate"] else "not a gate session"}) started')
        self.start_load()
        write_json(os.path.join(self.dir, 'machine.json'), self.machine_record())
        # prefills and statistics are built before the quiet check (their disk and anti-malware work must not fall inside it)
        for cid in self.order:
            main, directory = self.ensure_prefill(self.cells[cid])
            self.ensure_stats(self.cells[cid], directory)
        q = self.quiet_check('before the session')
        if q['verdict'] == 'NOT QUIET':
            self.stop_load()                # the raw collections are written first, then the record that points at them
            self.record_refusal(q)
            self.log('REFUSED: the quiet check before the session failed; the session does not start')
            return EXIT_REFUSED
        self.update_machine_defender()
        self.warmup()
        for r in range(1, m['rounds'] + 1):
            name = f'round{r}'
            for cid in self.order:
                self.execute(self.cells[cid], 'timed', name)
            self.after_round(name)
        for kind in BLOCK_ORDER:
            cells = [cid for cid in self.order if kind in self.kinds_for(cid)]
            if not cells:
                continue
            name = 'block-' + kind.replace(':', '')
            for cid in cells:
                self.execute(self.cells[cid], kind, name)
            self.after_round(name)
        self.negative_control()
        # replacement runs: only because an earlier run of the slot was invalidated, at most five more attempts
        pass_no = 0
        while True:
            todo = [(cid, kind) for cid in self.order for kind in self.kinds_for(cid) if model.needs_run(self.runs, cid, kind)]
            if not todo:
                break
            pass_no += 1
            name = f'replacement{pass_no}'
            self.log(f'replacement pass {pass_no}: ' + ', '.join(f'{c}/{k}' for c, k in todo))
            for cid, kind in todo:
                self.execute(self.cells[cid], kind, name)
            self.after_round(name)
        if self.controls and not self.control_ok():
            self.log('THE NEGATIVE CONTROL DID NOT FAIL (or was never valid): the checker is defective and TEST-P1 fails')
        return self.finish()

    # ---- the attempt's own records (an attempt that did not run to the end is kept, with these, and never run again)
    def attempt_record(self, status, **fields):
        m = self.manifest
        with open(os.path.join(self.dir, 'manifest.json'), 'rb') as f:
            digest = hashlib.sha256(f.read()).hexdigest()
        return {'schema': SCHEMA, 'status': status, 'session': m['id'], 'gate': m['gate'], 'supersedes': m.get('supersedes'), **fields,
                'manifest': {'file': 'manifest.json', 'sha256': digest},
                'declared': {'cells': m['cells'], 'rounds': m['rounds'], 'scale': m['scale'], 'loadValidity': m['loadValidity'], 'binary': m['binary']},
                'evidence': {k: n for k, n in (('quietChecks', 'quiet.jsonl'), ('collections', 'collections.jsonl'), ('machine', 'machine.json'), ('runs', 'runs.jsonl'), ('judgements', 'judgements.jsonl'),
                                               ('events', 'events.log')) if os.path.exists(os.path.join(self.dir, n))},
                'retry': f'this attempt is final: a retry is a NEW session declared with --supersedes {m["id"]} --reason <text> (§15.4 Reruns)'}

    def record_refusal(self, q):
        """refused.json: the attempt record of a session the quiet check refused: the check's summary and its raw collections are in it (the whole series is also in collections.jsonl)."""
        write_json(os.path.join(self.dir, 'refused.json'), self.attempt_record('REFUSED', refusedUtc=iso(), reason='the quiet check before the session failed', quietCheck=q,
                                                                                 rawCollections=getattr(self, 'last_quiet_window', None)))

    def record_abort(self, e):
        """aborted.json: the attempt record of a session that stopped before it completed (an exception, an OSError, an interrupt)."""
        self.log(f'SESSION ABORTED: {type(e).__name__}: {e}')
        write_json(os.path.join(self.dir, 'aborted.json'), self.attempt_record('ABORTED', abortedUtc=iso(), error=f'{type(e).__name__}: {e}', runsRecorded=len(self.runs)))

    # ---- the negative control
    def negative_control(self):
        self.log('negative control: the same check on a Library whose name index is keyed by the name alone')
        for variant in ('new', 'existing'):
            for attempt in range(1, 6):
                label = f'control-{variant}-a{attempt}'
                args = ['control', '--variant', variant, '--out', self.analysis, '--label', label, '--binary-commit', self.binary['commit']]
                code, _, rec, err = self.child(*args, what=f'negative-control child {label}')
                if code != 0 or rec is None:
                    result = {'variant': variant, 'attempt': attempt, 'verdict': 'INVALID', 'reasons': ['the control child failed: ' + err[-200:]]}
                else:
                    rec['envelope'] = {'session': self.manifest['id'], 'runId': label, 'kind': 'control', 'attempt': attempt}
                    append_line(os.path.join(self.dir, 'runs.jsonl'), rec)
                    path = os.path.join(self.dir, 'attribution', label + '.record.json')
                    write_json(path, rec)
                    p = subprocess.run([sys.executable, os.path.join(HERE, 'attribute_run.py'), path, '--analysis-dir', self.analysis], capture_output=True, text=True, encoding='utf-8')
                    try:
                        out = json.loads(p.stdout.strip().splitlines()[-1])
                    except (ValueError, IndexError):
                        out = {'verdict': 'INVALID', 'reasons': ['no verdict']}
                    write_json(os.path.join(self.dir, 'attribution', label + '.verdict.json'), out)
                    expected = {'PASS': 0, 'FAIL': 1, 'INVALID': 2}.get(out['verdict'])
                    if expected is None or p.returncode != expected:
                        out = {'verdict': 'INVALID', 'reasons': ['exit code disagrees with the verdict']}
                    result = {'variant': variant, 'attempt': attempt, 'verdict': out['verdict'], 'reasons': out.get('reasons', [])[:3], 'runId': label,
                              'remotePages': out.get('details', {}).get('remote_pages'), 'levelsOverSharedCap': len(out.get('details', {}).get('levels_over_shared_cap', []))}
                self.controls.append(result)
                append_line(os.path.join(self.dir, 'judgements.jsonl'), {'event': 'control', **result})
                self.log(f'  control {variant} attempt {attempt}: {result["verdict"]}')
                if result['verdict'] == 'FAIL':
                    break
                if result['verdict'] == 'PASS':
                    break       # a defective checker: no need to try again
        if not self.a.keep_analysis:
            for name in os.listdir(self.analysis):
                if name.startswith('control-') and name.endswith('.sqlite3'):
                    try:
                        os.remove(os.path.join(self.analysis, name))
                    except OSError:
                        pass

    def control_ok(self):
        return model.judge_control(self.controls)['ok']

    # ---- records
    def machine_record(self):
        import machine_record
        code, _, info, err = self.child('machine', what='machine query')
        rec = machine_record.collect(self.bench_root, info if code == 0 else {'error': err[-200:]})
        rec['binary'] = self.binary
        rec['runtimeTuningVariablesRemoved'] = self.h.environment()[1]
        return rec

    def update_machine_defender(self):
        if not self.sampler:
            return
        path = os.path.join(self.dir, 'machine.json')
        rec = json.load(open(path, encoding='utf-8'))
        rec['perProcessCountersSeeDefenderEngine'] = bool(self.sampler.diag.get('defender_engine_visible'))
        write_json(path, rec)

    def finish(self):
        self.update_machine_defender()
        judged = self.manifest['gate']
        evaluation = model.evaluate(list(self.cells.values()), self.runs, judged=judged, controls=self.controls)       # the control is part of the result
        failed = not evaluation['negativeControl']['ok']
        status = STATUS_CONTROL if failed else STATUS_COMPLETE
        session = {'schema': SCHEMA, 'id': self.manifest['id'], 'status': status, 'gate': self.manifest['gate'], 'manifest': 'manifest.json', 'finishedUtc': iso(),
                   'quietChecks': self.quiet_checks, 'negativeControl': self.controls, 'runs': self.runs, 'evaluation': evaluation,
                   'loadValidity': self.load_validity, 'scale': self.scale}
        self.stop_load()
        write_json(os.path.join(self.dir, 'session.json'), session)
        print_outcomes(evaluation, status)
        return EXIT_FAILED if failed else EXIT_DONE


def summary_of(run):
    m = run['metrics']
    bits = []
    if 'filesPerSecond' in m:
        bits.append(f'{m["filesPerSecond"]:,.0f} rows/s, T-IMPORT {m["importSeconds"]:.2f} s')
    if 'cancelSeconds' in m:
        bits.append(f'cancel-to-return {m["cancelSeconds"]:.3f} s')
    if 'recoverySeconds' in m:
        bits.append(f'recovery open {m["recoverySeconds"]:.3f} s')
    if 'attribution' in m and m['attribution']:
        bits.append('attribution ' + m['attribution'])
    if run.get('load') and run['load'].get('mean') is not None:
        bits.append(f'U mean {run["load"]["mean"]:.1f}% p95 {run["load"]["p95"]:.1f}% over {run["load"]["n"]} collections')
    return '(' + '; '.join(bits) + ')' if bits else ''


def fit_problems(rec, kind):
    """What makes a run record unfit as raw evidence (the Python side of RunRecord.cs's validation)."""
    problems = []
    for k in ('schema', 'kind', 'label', 'binary', 'cell', 'operationStartUtc', 'operationSeconds', 'outcome', 'library'):
        if k not in rec:
            problems.append('missing ' + k)
    if problems:
        return problems
    if kind == 'crash':
        if rec.get('kind') != 'crash':
            problems.append(f'kind is {rec.get("kind")!r}, not crash')
        return problems
    expected = {'timed': 'timed', 'attribution': 'attribution', 'delete': 'delete', 'cancel-after-final': 'cancel-after-final'}.get(kind, 'cancel')
    if rec.get('kind') != expected:
        problems.append(f'kind is {rec.get("kind")!r}, not {expected}')
    if expected == 'cancel' and not (isinstance(rec.get('cancel'), dict) and 'outcome' in rec['cancel']):
        problems.append('missing cancel.outcome')
    if kind in ('timed', 'attribution', 'delete', 'cancel-after-final'):
        for k in ('imp11', 'token', 'journal', 'memory'):
            if not rec.get(k):
                problems.append('missing ' + k)
    return problems


def print_outcomes(evaluation, status):
    print()
    print(f'session status: {status}' + ('' if evaluation['judged'] else ' (NOT a gate session: budgets are NOT JUDGED; the figures below are for information)'))
    for name, b in evaluation['budgets'].items():
        print(f'  {name}: {b["outcome"]}' + (f' (would be {b["wouldBe"]})' if not evaluation['judged'] else ''))
        for c in b['cells']:
            print(f'      {c["cell"]} [{c["part"]}]: {c["outcome"]} - {c["why"]}')
    t = evaluation['tokenInterval']
    print(f'  token-check interval (CAN-01d, <= 0.5 s): {t["outcome"]}, largest gap {t["largestGapSeconds"]}')
    if 'negativeControl' in evaluation:
        print(f'  negative control (must FAIL): {evaluation["negativeControl"]["outcome"]} - {evaluation["negativeControl"]["why"]}')


def rebuild(directory):
    """Rebuilds the runs and the evaluation of a session from its raw files (judgements.jsonl is append-only; round invalidations are events)."""
    manifest = json.load(open(os.path.join(directory, 'manifest.json'), encoding='utf-8'))
    model.set_rounds(manifest['rounds'])
    runs = {}
    ordered = []
    controls = []
    for e in read_lines(os.path.join(directory, 'judgements.jsonl')):
        if e['event'] == 'judged':
            run = {k: v for k, v in e.items() if k != 'event'}
            runs[run['runId']] = run
            ordered.append(run)
        elif e['event'] == 'round-invalidated':
            r = runs[e['runId']]
            r['status'] = 'invalid'
            r['reasons'] = list(r['reasons']) + [e['reason']]
        elif e['event'] == 'control':
            controls.append({k: v for k, v in e.items() if k != 'event'})
    cells = manifest['plan']['cells']
    # the negative control is part of the result of a session that ran to the end (a session that never reached it has none to judge)
    finished = os.path.exists(os.path.join(directory, 'session.json'))
    return manifest, ordered, controls, model.evaluate(cells, ordered, judged=manifest['gate'], controls=controls if controls or finished else None)


def outcome_command(a):
    manifest, runs, controls, evaluation = rebuild(a.session)
    status = status_of(a.session, evaluation)
    print_outcomes(evaluation, f'{status}, rebuilt from the raw files')
    return EXIT_FAILED if status == STATUS_CONTROL else EXIT_DONE


def attempts_command(a):
    rows = attempts(a.evidence)
    if a.json:
        print(json.dumps(rows, indent=1))
        return EXIT_DONE
    if not rows:
        print('no session in ' + a.evidence)
    for r in rows:
        print(f'{r["id"]}  {"gate" if r["gate"] else "not gate"}  {r["status"]}' + (f'  supersedes {r["supersedes"]}' if r['supersedes'] else '') + f'  declared {r["declaredUtc"]}')
    return EXIT_DONE


# ---------------------------------------------------------------------------------------------------------------- the command line
def parser():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='command', required=True)

    def common(p, declare_options=True):
        p.add_argument('--exe', required=True, help='StorageInventory.Library.Tests.exe (the apphost), or the dotnet host with --dll')
        p.add_argument('--dll', help='the test assembly, when --exe is the dotnet host')
        p.add_argument('--bench-root', help='the folder the benchmark Libraries live in (SI_BENCH_ROOT); default %%TEMP%%')
        p.add_argument('--prefill-cache', help='where prefills are cached (default <bench-root>/SI-Gate-PrefillCache)')
        p.add_argument('--keep-analysis', action='store_true', help='keep the database copies of the attribution runs (large at 2M files)')
        if declare_options:
            p.add_argument('--evidence', required=True, help='the evidence directory: one sub-directory per session')
            p.add_argument('--id', help='session identifier (default: time, commit and gate/nongate)')
            p.add_argument('--scale', type=float, default=1.0, help='multiply every file count (smoke); a scaled session is not a gate session')
            p.add_argument('--cells', help='comma-separated cell ids (default: the whole matrix)')
            p.add_argument('--rounds', type=int, default=5)
            p.add_argument('--gate', action='store_true', help='declare this the GATE session (whole matrix, scale 1, five rounds, load validity, clean tree)')
            p.add_argument('--supersedes', help='the earlier gate session this one replaces after an objective invalidation')
            p.add_argument('--reason', help='why (with --supersedes)')
            p.add_argument('--no-load-validity', action='store_true', help='SMOKE ONLY: skip the quiet checks and the run rule; the session is not a gate session')
            p.add_argument('--smoke', action='store_true', help='shorthand for --scale 0.025 --cells F-2M-25-system,R-2M-25-system-1-05-05 --rounds 2')

    common(sub.add_parser('declare', help='write the session manifest'))
    p = sub.add_parser('run', help='run a declared session')
    p.add_argument('--session', required=True)
    common(p, declare_options=False)
    common(sub.add_parser('go', help='declare and run'))
    p = sub.add_parser('outcome', help='recompute the outcomes from a session\'s raw files')
    p.add_argument('--session', required=True)
    p = sub.add_parser('attempts', help='list every session (attempt) of an evidence directory with its status')
    p.add_argument('--evidence', required=True)
    p.add_argument('--json', action='store_true')
    return ap


def main(argv):
    a = parser().parse_args(argv)
    try:
        if a.command == 'outcome':
            return outcome_command(a)
        if a.command == 'attempts':
            return attempts_command(a)
        if a.command in ('declare', 'go') and a.smoke:
            a.scale, a.cells, a.rounds = 0.025, 'F-2M-25-system,R-2M-25-system-1-05-05', 2
        if a.command == 'declare':
            declare(a)
            return EXIT_DONE
        directory = declare(a) if a.command == 'go' else a.session
        started = started_files(directory) if a.command == 'run' else []
        if started:
            # an attempt is run ONCE: whatever became of it (refused, aborted, interrupted, finished), its files are kept as they are and a retry is a new declaration
            raise Refusal(f'{directory} already holds a started session ({", ".join(started)}); a session is run once and its files are kept as they are. '
                          'A retry is a NEW session declared with --supersedes <this id> --reason <text> (§15.4 Reruns)')
        s = Session(directory, a)
        # the binary measured is the binary declared
        commit, dirty = gate_exe.git_identity(gate_exe.repo_root())
        plan = plan_of(s.h, s.scale, s.manifest['binary']['commit'], s.manifest['binary']['dirty'])
        if plan['binary']['outputHash'] != s.manifest['binary']['outputHash']:
            raise Refusal('the build output has changed since the manifest was written (hash ' + plan['binary']['outputHash'][:16] + ' against ' + s.manifest['binary']['outputHash'][:16] + ')')
        try:
            return s.go()
        except BaseException as e:      # noqa: BLE001 - recorded as an aborted session (an interrupt too), then raised
            s.record_abort(e)
            raise
    except Refusal as e:
        print('REFUSED: ' + str(e), file=sys.stderr)
        return EXIT_USAGE
    except OSError as e:
        print(f'ABORTED: {e}', file=sys.stderr)
        return EXIT_ABORTED


if __name__ == '__main__':
    try:
        code = main(sys.argv[1:])
    except Exception:       # noqa: BLE001 - the tool's own failure exits 5, never the 1 of "the negative control failed"
        import traceback
        traceback.print_exc()
        code = EXIT_CRASHED
    sys.exit(code)
