"""The statistics and outcomes of TEST-P1 (§15.4 "Validity", "Replacement and statistic", "Outcomes"; §15.3's budgets): pure functions over the
runs of a session, no I/O, so that session_selftest.py can test every rule.

A RUN of a session is a dict with the fields below; the orchestrator (perf_session.py) builds them from the raw records and the judgements.

    runId, cell, kind, attempt, order       identity and chronological order (the "run order": the first five VALID runs of a cell are the sample)
    status        'ok'       valid, and its result is a measurement
                  'failed'   valid (its environment was fine), but the engine's own result is a failure (a failed save, a rollback assertion
                             that did not hold): it counts as a valid run and is never discarded or replaced for its result; the cell's
                             outcome is MISSED on every budget it carries
                  'invalid'  not valid: the load rule, an invalid round, a harness failure, or (an attribution) INVALID evidence; it
                             stays in the raw tables with its reasons and earns a replacement
    metrics       what the budgets read: filesPerSecond and importSeconds (timed); cancelSeconds (cancel:n); recoverySeconds (crash);
                  attribution = 'PASS' or 'FAIL' (attribution); tokenGapSeconds (timed, attribution, cancel)

Kinds: timed (five valid runs a cell), attribution (one), cancel:1 .. cancel:4 (one each), crash (one), delete (one, informational).

OUTCOMES for a budget at its gate (§15.4): MET: every cell the budget names meets its target. MISSED: a cell misses the target and none crosses
the stop threshold. NOT MEASURED: as MISSED (it blocks acceptance). STOP: a cell crosses its stop threshold. A MISSED budget is never reported as
met because no stop threshold was crossed. Precedence among cells: STOP, then MISSED, then NOT MEASURED, then MET.
"""
import statistics

ROUNDS = 5                                   # exactly five measured runs per cell (a smoke session may declare fewer, and is never a gate session)
EXTRA_ATTEMPTS = 5                           # at most five more attempts, each only because an earlier run of the cell was invalidated
DEFAULT_VALID_NEEDED = 1
DEFAULT_MAX_ATTEMPTS = 6                      # the first attempt plus at most five replacements
STOP_FACTOR = 1.5

PERF01_TARGET = 100_000.0                     # file rows/s, median of five valid runs, every representative cell
PERF01_STOP = 50_000.0                        # a representative cell's median below this stops C4
PERF14_ENGINE_TARGET = 45.0                   # seconds of T-IMPORT at 2M in every representative and stress cell
TOKEN_INTERVAL = 0.5                          # seconds between two looks at the save token (CAN-01d)
RECOVERY_BUDGET = 5.0                         # seconds for the next start-up open after a kill at the journal's peak (PERF-15 (c))
PERF16_ENGINE_FRACTION = {'Representative': 1.75, 'Stress': 4.75, 'Worst-case': 4.75}    # PERF-16's engine part is PERF-15 (c)'s cancel budget

OUTCOME_ORDER = ['STOP', 'MISSED', 'NOT MEASURED', 'MET']


def set_rounds(n):
    """The number of measured runs a cell needs (the manifest's rounds; five for a gate session)."""
    global ROUNDS
    ROUNDS = n


def needed(kind):
    return ROUNDS if kind == 'timed' else DEFAULT_VALID_NEEDED


def max_attempts(kind):
    return ROUNDS + EXTRA_ATTEMPTS if kind == 'timed' else DEFAULT_MAX_ATTEMPTS


def runs_of(runs, cell, kind):
    return sorted((r for r in runs if r['cell'] == cell and r['kind'] == kind), key=lambda r: r['order'])


def valid_runs(runs, cell, kind):
    """The valid runs of a slot in run order ('ok' and 'failed' are both valid; 'invalid' is not)."""
    return [r for r in runs_of(runs, cell, kind) if r['status'] in ('ok', 'failed')]


def needs_run(runs, cell, kind):
    """Whether a slot still needs an attempt: fewer valid runs than required, and attempts left."""
    attempts = len(runs_of(runs, cell, kind))
    return len(valid_runs(runs, cell, kind)) < needed(kind) and attempts < max_attempts(kind)


def first_valid(runs, cell, kind):
    """The sample of a slot: its first `needed` valid runs in run order, or None when it has fewer (NOT MEASURED)."""
    v = valid_runs(runs, cell, kind)
    return v[:needed(kind)] if len(v) >= needed(kind) else None


def figure(sample, metric):
    """Median, minimum and maximum of a metric over a sample; None when a run of the sample failed (the cell then MISSED)."""
    if sample is None:
        return None
    if any(r['status'] == 'failed' for r in sample):
        return {'failed': True, 'values': [r['metrics'].get(metric) for r in sample]}
    values = [r['metrics'].get(metric) for r in sample]
    if any(v is None for v in values):
        return {'failed': True, 'values': values, 'note': f'{metric} was not recorded'}      # never a silent zero
    return {'median': statistics.median(values), 'min': min(values), 'max': max(values), 'values': values, 'failed': False}


def worst(outcomes):
    for o in OUTCOME_ORDER:
        if o in outcomes:
            return o
    return 'NOT MEASURED'


def judge_perf01(fig):
    if fig is None:
        return 'NOT MEASURED', 'fewer than five valid runs'
    if fig['failed']:
        return 'MISSED', 'a run of the sample failed'
    m = fig['median']
    if m < PERF01_STOP:
        return 'STOP', f'median {m:,.0f} rows/s is below the stop threshold {PERF01_STOP:,.0f}'
    if m < PERF01_TARGET:
        return 'MISSED', f'median {m:,.0f} rows/s is below the target {PERF01_TARGET:,.0f}'
    return 'MET', f'median {m:,.0f} rows/s'


def judge_perf14(fig, target, stop):
    if fig is None:
        return 'NOT MEASURED', 'fewer than five valid runs'
    if fig['failed']:
        return 'MISSED', 'a run of the sample failed'
    m = fig['median']
    if stop and m > stop:
        return 'STOP', f'median {m:.1f} s is above the stop threshold {stop:g} s'
    if target and m > PERF14_ENGINE_TARGET:
        return 'MISSED', f'median {m:.1f} s is above the engine target {PERF14_ENGINE_TARGET:g} s'
    return 'MET', f'median {m:.1f} s' + ('' if target else f' (no target: bounded by the stop threshold {stop:g} s)')


def judge_attribution(sample):
    """PERF-15 (a): the single valid attribution of the cell. PASS meets; FAIL is a defect (MISSED, no stop); none valid is NOT MEASURED."""
    if sample is None:
        return 'NOT MEASURED', 'no valid attribution (every capture was INVALID)'
    v = sample[0]['metrics'].get('attribution')
    return ('MET', 'PASS') if v == 'PASS' else ('MISSED', f'the checker says {v}')


def judge_seconds(value, budget, what):
    if value > budget * STOP_FACTOR:
        return 'STOP', f'{what} {value:.2f} s is above {STOP_FACTOR:g} x its budget {budget:g} s'
    if value > budget:
        return 'MISSED', f'{what} {value:.2f} s is above its budget {budget:g} s'
    return 'MET', f'{what} {value:.2f} s'


def judge_cancel(runs, cell, budget):
    """PERF-15 (c): the four cancel points of a cell; its figure is the largest. Each point needs one valid run."""
    points = []
    reasons = []
    outcomes = []
    for p in (1, 2, 3, 4):
        sample = first_valid(runs, cell, f'cancel:{p}')
        if sample is None:
            outcomes.append('NOT MEASURED')
            reasons.append(f'cancel point {p}: no valid run')
            continue
        r = sample[0]
        if r['status'] == 'failed':
            outcomes.append('MISSED')
            reasons.append(f'cancel point {p}: {r["metrics"].get("problem", "failed")}')
            continue
        points.append(r['metrics']['cancelSeconds'])
        o, why = judge_seconds(r['metrics']['cancelSeconds'], budget, f'point {p}')
        outcomes.append(o)
        reasons.append(why)
    if 'NOT MEASURED' in outcomes or not points:
        fig = max(points) if points else None
    else:
        fig = max(points)
    return worst(outcomes), reasons, fig


def judge_recovery(runs, cell):
    sample = first_valid(runs, cell, 'crash')
    if sample is None:
        return 'NOT MEASURED', 'no valid kill-and-recover run', None
    r = sample[0]
    if r['status'] == 'failed':
        return 'MISSED', r['metrics'].get('problem', 'the recovery did not leave a sound Library'), None
    o, why = judge_seconds(r['metrics']['recoverySeconds'], RECOVERY_BUDGET, 'start-up open')
    return o, why, r['metrics']['recoverySeconds']


def evaluate(cells, runs, judged=True):
    """cells: the plan's cells ({id, class, budgets:{...}}). Returns {'cells': {id: {...figures and per-budget outcomes}}, 'budgets': {name: ...}}.
    judged=False (a session that is not a gate session, a smoke scale, or without load validity) reports the figures and marks every budget
    NOT JUDGED."""
    per_cell = {}
    for c in cells:
        cid, b = c['id'], c['budgets']
        entry = {'class': c['class'], 'budgets': {}}
        timed = first_valid(runs, cid, 'timed')
        entry['timed'] = {'valid': len(valid_runs(runs, cid, 'timed')), 'attempts': len(runs_of(runs, cid, 'timed')),
                          'rowsPerSecond': figure(timed, 'filesPerSecond'), 'importSeconds': figure(timed, 'importSeconds')}
        if b['perf01']:
            entry['budgets']['PERF-01'] = judge_perf01(entry['timed']['rowsPerSecond'])
        if b['perf14Target'] or b['perf14StopSeconds']:
            entry['budgets']['PERF-14'] = judge_perf14(entry['timed']['importSeconds'], b['perf14Target'], b['perf14StopSeconds'])
        if b['perf15a']:
            entry['budgets']['PERF-15 (a)'] = judge_attribution(first_valid(runs, cid, 'attribution'))
        if b['perf15cSeconds']:
            o, why, fig = judge_cancel(runs, cid, b['perf15cSeconds'])
            entry['cancelSeconds'] = fig
            entry['budgets']['PERF-15 (c) cancel'] = (o, '; '.join(why))
            o16 = o
            entry['budgets']['PERF-16 engine part'] = (o16, f'the cancel figure against PERF-15 (c)\'s {b["perf15cSeconds"]:g} s (PERF-16: {2 if b["perf15cSeconds"] < 2 else 5} s less 0.25 s reserved for C5)')
        if b['recovery']:
            o, why, secs = judge_recovery(runs, cid)
            entry['recoverySeconds'] = secs
            entry['budgets']['PERF-15 (c) recovery'] = (o, why)
        per_cell[cid] = entry

    budgets = {}
    for name in ('PERF-01', 'PERF-14', 'PERF-15 (a)', 'PERF-15 (c)', 'PERF-16 engine part'):
        results = []
        for cid, e in per_cell.items():
            if name == 'PERF-15 (c)':
                for part in ('PERF-15 (c) cancel', 'PERF-15 (c) recovery'):
                    if part in e['budgets']:
                        results.append((cid, part, *e['budgets'][part]))
            elif name in e['budgets']:
                results.append((cid, name, *e['budgets'][name]))
        outcome = worst([r[2] for r in results]) if results else 'NOT MEASURED'
        budgets[name] = {'outcome': outcome if judged else 'NOT JUDGED', 'wouldBe': outcome, 'blocksAcceptance': judged and outcome != 'MET',
                         'cells': [{'cell': r[0], 'part': r[1], 'outcome': r[2], 'why': r[3]} for r in results]}
    # the token-check interval (CAN-01d): the largest gap between two looks at the save token over every run that recorded one
    gaps = [r['metrics']['tokenGapSeconds'] for r in runs if r['status'] in ('ok', 'failed') and r['metrics'].get('tokenGapSeconds') is not None]
    after_final = [r for r in runs if r['kind'] == 'cancel-after-final']
    can01e = {'runs': len(after_final), 'outcome': ('not planned' if not after_final else 'MET' if any(r['status'] == 'ok' for r in after_final)
                                                  else 'MISSED' if any(r['status'] == 'failed' for r in after_final) else 'NOT MEASURED')}
    if not judged and can01e['outcome'] != 'not planned':
        can01e['outcome'] = 'NOT JUDGED'
    token = {'largestGapSeconds': max(gaps) if gaps else None, 'runs': len(gaps)}
    token['outcome'] = ('NOT MEASURED' if not gaps else 'MET' if max(gaps) <= TOKEN_INTERVAL else 'MISSED') if judged else 'NOT JUDGED'
    return {'cells': per_cell, 'budgets': budgets, 'tokenInterval': token, 'cancelAfterFinalCheck': can01e, 'judged': judged}
