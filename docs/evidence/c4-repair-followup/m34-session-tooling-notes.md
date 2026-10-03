# M34: the TEST-P1 session tooling (C4R-M03 and C4R-M04)

Branch `v1.1/c4-fixes-m34` in the worktree `C:\sw\m34`, created from the review commit `7b41022`. Not pushed, no PR, no merge, `src/` untouched, the specification
untouched, no benchmark or performance session run (`load_regression.py` not run). Scope: the two findings of `docs/v1.1-c4-repair-review.md` §25.3 and §25.4 (and
observation C4R-O12's exit-code collision), nothing else.

| Commit | What |
|---|---|
| `9725516` | **Red commit, by design.** The reproductions: three `GateHarnessTests` and 36 failing `session_selftest.py` checks, with the test seams they need (`GateRunner.TestCancelBehaviour`, `TestSpaceGuard`, the `CancelRecord.Requested` field). Nothing else changes behaviour. Squash it with the fixes if a green history is wanted. |
| `480ad5a` | M03, the harness (C#): `RecordJson.Validate`, `GateBenchmark.ExitCodeOf`, the validation test. Makes the three `GateHarnessTests` pass. |
| `b0671df` | M03 and M04, the Python tooling: `perf_session.py`, `gate_model.py`, `perf_report.py`, README, the committed example report. Makes the 36 pass. |
| `2021c7c` | the mutant runner `tests/mutation/perf_session_mutants.py` (31 mutants) and two assertions added to the failed-control flow |
| this commit | this note |

## 1. C4R-M03: the session tooling invalidates, and replaces, a valid run because of its result

### 1.1 The defect, as reproduced (before the fix)

The review's reading of the code is right, and the defect it describes is in the C# harness: the Python judge already turns a record with `outcome = Published` into a
failed run (`the cancellation was not honoured`), so with exit code 0 the branch is reachable. The red commit `9725516` shows it with the real code path
(`GateBenchmark.Run(["run", ...])`, in process, exactly the child the orchestrator starts), using the test seams `GateRunner.TestCancelBehaviour =
RequestWithoutCancelling` (the harness requests the cancellation and records it but never cancels the token, so the product "never sees it" and publishes) and
`TestSpaceGuard` (a guard that refuses at `BEGIN`):

```
FAIL  GateHarnessTests.ACancellationTheSaveDidNotHonourIsAMeasuredRecordNotAnUnfitOne -- the child exits 0 for a run whose save published despite the cancellation (exit 3 would have the orchestrator replace it): gate: the run record is not fit as raw evidence: missing cancel.rollback : expected <0> but was <3>
FAIL  GateHarnessTests.ASaveThatFailsAtBeginIsAMeasuredRecordNotAnUnfitOne -- the child exits 0 for a save that failed at BEGIN: gate: the run record is not fit as raw evidence: imp11.checks holds fewer than two checks : expected <0> but was <3>
FAIL  GateHarnessTests.ACancelPointThatWasNeverReachedIsRecordedAsNeverRequested -- the record is fit ...: expected <0> but was <1>
RESULT StorageInventory.Library.Tests: 0 PASS, 3 FAIL, 0 SKIP
```

Exit 3 is what `perf_session.Session.judge` turns into `the harness child failed (exit 3)`, status `invalid`, which `needs_run` replaces (at most five more attempts).
A third shape the review does not name takes the same path: a cancel point that was never reached (no cancellation requested, the save publishes), and the review's
"timed record with fewer than two IMP-11 checks" also applies to an **attributed** save that fails at `BEGIN`. On the Python side one more defect showed with it: an
attribution run whose save failed was handed to the checker, which says INVALID (there is no journal), so it was replaced too (red check:
`M03: an attribution run whose save failed is a VALID run`).

### 1.2 The classification of a cancellation-gate run (what the code now does)

| Class | Condition | Run status | Replaced? | PERF-15 (c) / PERF-16 engine part |
|---|---|---|---|---|
| (i) expected cancellation | cancellation **requested**, outcome `Cancelled (rolled back)`, rollback asserted | `ok`, `cancelClass = expected cancellation` | no (valid) | judged on the time, cancel to return: MET up to the budget, MISSED above it, STOP above 1.5 x |
| (ii) miss: not honoured | requested, and the save **published** (or failed) instead | `failed`, `cancelClass = missed`, reason `the cancellation was not honoured: Published` | **never** | MISSED; **STOP** when the measured time from the request to the return is above 1.5 x the budget |
| (ii) miss: rollback did not hold | requested, cancelled, the rollback assertion failed | `failed`, `cancelClass = missed` | **never** | same |
| (ii) miss: never requested | the harness never reached the cancel point (`cancel.requested = false`) | `failed`, `cancelClass = never requested` | **never** | MISSED (no time to read) |
| (iii) invalid harness or evidence | the child died (no record), the child refused its own record (exit 3: unreadable), a cancel record without its `cancel` object, the load rule (U over 10% / 25%), a failed quiet check after the round | `invalid` | yes, under the accepted rules only: at most five more attempts; NOT MEASURED after six | the slot has no valid run: NOT MEASURED (blocks acceptance, never MET) |

**MISSED or STOP, and the rule applied.** The accepted authority (§15.3 PERF-15 (c): "within 1.75 s ... 4.75 s", stop threshold "(c) above 1.5x its budget"; §15.4
"Outcomes": MISSED is a missed target "and none crosses the stop threshold"; PERF-16: engine part is PERF-15 (c), no threshold of its own) defines the stop
threshold on **time**. A cancellation that does not roll back has no "return rolled back" time, but the measured time from the request to the return of
`ImportSnapshotAsync` exists (`cancel.cancelToReturnSeconds`), so I apply the accepted threshold to it whatever the outcome: a miss is **MISSED**, and **STOP** when that
time is above 1.5 x the budget (2.625 s representative, 7.125 s stress and worst-case; `gate_model.judge_cancel`). Misses at the later cancel points that publish within the
threshold are MISSED, which is also what the review says ("MISSED or STOP by its own rule"). This is a **judgement call** (the specification does not say what a cancellation that is
ignored measures); if the owner prefers MISSED always, delete the `over` branch in `judge_cancel` (one mutant, PS-02, pins the present reading). A miss is never reported as MET and `blocksAcceptance`
is true for the session either way.

**Child launch.** A child that cannot be started at all raises `OSError` out of `Session.child`: the **session** aborts (`aborted.json`, exit 4), it is not a replaced run. A
child that starts and then dies or refuses its record is an invalid *run* and is replaced (tests `replacement: ...`). I did not change either.

### 1.3 The change

* `RunRecord.cs` `RecordJson.Validate`: `cancel.rollback` is required only of a cancel run whose save did **not** publish; `cancel.requested` is required of every cancel record; the
  "fewer than two IMP-11 checks" rule applies only to a save that **published**. `CancelRecord` gained `Requested` (false when no cancellation was requested).
* `GateBenchmark.cs`: `ExitCodeOf(mode, record, out problems)`: **0** whenever the record can be interpreted, whatever the run's result; **3** only for a record that cannot be interpreted
  as raw evidence; a crash run's record is judged by the orchestrator (unchanged).
* `GateRunner.cs`: `Requested` is recorded; two test seams (`TestCancelBehaviour`, `TestSpaceGuard`), never set by a benchmark child, off by default.
* `perf_session.py` `Session.judge` / `fit_problems`: the classification above (`cancelClass`, the time of a requested cancellation kept for a miss too, `never requested`), an attributed
  save that failed is `failed` instead of going to the checker, a cancel record without its `cancel` object is unfit, the exit-3 reason says so, and what a run showed before the load
  rule invalidated it (a miss in a busy window) is kept in its row (`metrics.problem`, a reason line) and named in the report's caveats.
* `gate_model.py`: `judge_cancel` (STOP on the measured time of a miss), `judge_attribution` (a failed attributed run is MISSED with its reason), docstrings.
* `perf_report.py`: the cancel class in the raw table, the caveat for an invalidated run that had shown a behavioural failure.

### 1.4 Tests (names)

C# (`GateHarnessTests`, 10 to 14): `ACancellationTheSaveDidNotHonourIsAMeasuredRecordNotAnUnfitOne` (four cancel points, the child exits 0, the record is fit, `requested`, no rollback),
`ACancelPointThatWasNeverReachedIsRecordedAsNeverRequested`, `ASaveThatFailsAtBeginIsAMeasuredRecordNotAnUnfitOne` (timed and attributed, one check, exit 0),
`ValidationAcceptsMeasuredFailuresAndRefusesWhatCannotBeInterpreted` (the three classes, every removed field, `ExitCodeOf` 0 / 3 / crash), and `CancelPointsRollBackAndAreTimed` now asserts `Requested`.

Python (`session_selftest.py`, 73 to 165 ok checks; the new tests are functions): `test_judge_cancel_records` (the judge on records: expected, missed, never requested,
rollback failed, old record without `requested`, no `cancel` object, exit 3, a dead child, a failed attributed save), `test_cancel_miss_rules` (STOP only above 1.5 x, at 2.625 / 2.63 / 30 s, the
figure includes a miss, never replaced), and the end-to-end flows through the real orchestrator: `test_flow_baseline`, `test_flow_published_cancel` (a published cancel run, immediate and after 9 s: valid, kept,
one attempt, MISSED / STOP, PERF-16 the same, the session completes), `test_flow_cancel_classes`, `test_flow_early_failed_save` (a timed save that fails at BEGIN in round 3 and an attributed one: valid, never replaced,
PERF-01 / PERF-14 / PERF-15 (a) MISSED), `test_flow_replaceable_failures` (a child that dies, a record refused with exit 3, a busy machine, a failed round: invalid, kept, **replaced**; a slot whose child always dies:
six attempts then NOT MEASURED), `test_flow_miss_in_an_invalid_run_is_kept`.

## 2. C4R-M04: an attempt can be run again in place, and the report does not carry a failed negative control

### 2.1 The defects, as reproduced (before the fix, on the red commit's tooling, the same scripted world)

```
(b) a negative control that PASSED
  run exit code: 1
  session.json status: TEST-P1 FAILED: the negative control did not FAIL
  report: Status: **complete**. **Gate session.**
  report: | new | 1 | PASS | 0 | 0 |  |
  report: | PERF-15 (a) | MET |  |
  outcome command exit code: 0
(a) a refused session run again in place
  first run exit code (refused): 3 ['collections.jsonl', 'machine.json', 'manifest.json', 'quiet.jsonl', 'refused.json']
  second run of the same directory, exit code: 0
  judged runs: 13 ; refused.json still present: True ; session.json present: True
  report status: ['Status: **REFUSED (the quiet check before the session failed)**. **Gate session.**']
  quiet.jsonl rows: 15
(a) an aborted session run again in place
  first run raised: RuntimeError ; judged runs after it: 7 ; second run exit code: 0 ; judged runs now: 20 ; distinct run ids: 13 ; order values repeated: True
  aborted.json present: True ; session.json present: True
```

The same flows after the fix:

```
(b) ... run exit code: 1 ; session.json status: TEST-P1 FAILED: the negative control did not FAIL
  report: Status: **TEST-P1 FAILED: the negative control did not FAIL**. **Gate session.**
  report: | PERF-15 (a) | TEST-P1 FAILED | blocks acceptance |
  report: | PERF-15 (a) | (negative control) | TEST-P1 | TEST-P1 FAILED | the new-source control PASSED: the checker accepted a Library it must reject |
  outcome command exit code: 1
(a) refused: second run of the same directory, exit code: 2 ; judged runs: 0 ; session.json present: False ; quiet.jsonl rows: 1
(a) aborted: second run exit code: 2 ; judged runs now: 7 ; distinct run ids: 7 ; order values repeated: False
```

(The driver is `docs/evidence/c4-repair-followup/m34-m04-repro.py`, a thin script over `session_selftest.Flow`; run it from the repository root with `PYTHONPATH=tests/perf`, on `9725516` for the first listing and on a later commit for the second. The M03 listing is the red commit's own test run: `run --no-build -c Release --project tests\StorageInventory.Library.Tests -- ACancel ASaveThatFails` on `9725516`.) A third defect appeared with them: an interrupt (Ctrl-C) was not recorded at all (`except Exception`),
and a directory left by a killed process (a raw table, no outcome) could be run again like the others.

### 2.2 The model (what the code now does)

* **A session directory is an attempt and is run once.** `run` refuses (exit 2, before it touches the directory) any directory that holds `refused.json`, `aborted.json`, `session.json`,
  `runs.jsonl`, `judgements.jsonl`, `quiet.jsonl`, `collections.jsonl`, `machine.json` or `events.log` (`STARTED`). A declared session that never started (only `manifest.json`) may be run; a binary-hash
  mismatch or a harness `plan` failure is refused before the attempt starts and leaves it runnable.
* **A retry is a new declaration naming the earlier attempt**, `declare --gate --supersedes <id> --reason <text>`. The existing "first declared session on the binary is the gate session" rule is kept and made
  consistent: the named attempt must be the **latest** gate session of the binary (an attempt is superseded once) and must have been **invalidated by the rules**, derived from its files
  (`invalidation_reason`): refused (quiet check), aborted, never completed (a raw table, no `session.json`), its negative control did not FAIL, or cells NOT MEASURED (§15.4 "Reruns"). A session that completed with a valid
  result, whatever the result, **cannot** be superseded. The derived reason is written into the new manifest (`invalidationOfSuperseded`).
* **The attempt records.** `refused.json` and `aborted.json` carry: status (`REFUSED`, `ABORTED`), session id, gate flag, what it supersedes, the manifest's file and SHA-256, the declared cells / rounds / scale / load
  validity / binary, pointers to the evidence files that exist (`quiet.jsonl`, `collections.jsonl`, `machine.json`, `runs.jsonl`, ...), the reason or the error, and for a refusal the quiet check's summary **and its raw
  120 collections** (the whole series is also in `collections.jsonl`, written before the record) or for an abort the number of runs recorded. An interrupt (`KeyboardInterrupt`) is recorded as an abort too and still propagates.
  The raw tables of an aborted attempt are untouched and have no duplicates.
* **Queryable and reportable.** `perf_session.py attempts --evidence DIR [--json]` lists every attempt with its status (`declared (not run)`, `REFUSED`, `ABORTED`, `INCOMPLETE`, `complete`, `TEST-P1 FAILED: ...`), gate flag
  and supersedes link; `perf_report.py` reports a refused, aborted or incomplete attempt in full (status with its reason or error, machine record, manifest, quiet checks, the partial raw table).
* **The negative control is part of the result.** `gate_model.judge_control` (both variants must give a valid FAIL; a PASS is final and never cured; INVALID evidence is captured again, at most five times, as before; none recorded
  is TEST-P1 FAILED for a finished session); `evaluate(..., controls)` makes PERF-15 (a) `TEST-P1 FAILED` (blocks acceptance; `NOT JUDGED` / would be `TEST-P1 FAILED` for a session that is not judged) and adds
  `evaluation.negativeControl`; `rebuild` passes the controls for a finished session; `Session.finish`, the `outcome` command (exit 1), `attempts` and the report (status line, PERF-15 (a) row and its detail row, the control
  table's verdict line, a caveat) all read it. The control is persisted (`judgements.jsonl` events, `runs.jsonl`) and never replaced by another control run.

### 2.3 Exit codes (documented in `perf_session.py`'s header and `README.md`)

| Code | Meaning | Before |
|---|---|---|
| 0 | done: read the outcomes | same |
| 1 | the negative control did not FAIL (TEST-P1 FAILED); also `outcome` of such a session | same for `run`; `outcome` returned 0 |
| 2 | refused or usage: a declaration the rules refuse, `run` on a directory that already started | `run` on a started directory was only refused when `session.json` existed |
| 3 | the quiet check before the session failed (`refused.json`) | same |
| 4 | aborted by an `OSError` (`aborted.json`) | same |
| 5 | **new**: the tool itself failed (an unhandled exception); for a started session `aborted.json` says so | exited **1**, the code of "the negative control failed" (C4R-O12) |

The harness child's own codes: 0 (any interpretable record), 3 (a record that cannot be interpreted), 2 (usage), anything else a crash.

### 2.4 Tests (names)

Python (`session_selftest.py`): `test_control_judgement` (the pure rule and PERF-15 (a)), `test_exit_codes` (six distinct codes; a real subprocess whose unhandled exception exits 5), and the flows
`test_flow_quiet_check_refusal` (the refusal record in full; the same directory run again: refused, byte-for-byte unchanged; a new declaration needs `--supersedes`; with it a new session runs; both attempts queryable and
reportable, `attempts` command; a completed session cannot be superseded; an attempt is superseded once), `test_flow_aborted_session` (an exception, a `KeyboardInterrupt`, an `OSError` (exit 4), a killed session's raw table
(`INCOMPLETE`), a declared session that never started), `test_flow_negative_control` (INVALID, INVALID, FAIL: valid; PASS: TEST-P1 FAILED in `session.json`, `rebuild`, `outcome`, the report, `attempts`, one attempt only, the
session can be superseded; five INVALID: TEST-P1 FAILED), and the M03 flows above (environmentally invalid runs replaced under the accepted rules).

## 3. Mutants

`tests/mutation/perf_session_mutants.py` (new; the repo's mutant scripts are PowerShell with no Python-suite kind, and these edits are multi-line Python with both quote styles) applies exact-once edits to a scratch copy and
counts a kill only for a test of the mutant's own suite that passes unmutated (`session_selftest.py` for `python`, `GateHarnessTests` built with the repo-local SDK for `library`), and only as intended when the test is
one the mutant is meant to be caught by. Commits: the 30 mutants PS-01 to PS-23 and CS-01 to CS-07 ran on `b0671df` (the tree of that commit plus the runner, uncommitted at the time); PS-24, and the two assertions added to the failed-control
flow afterwards (the report's verdict line and the `attempts` status), ran on `2021c7c`. The added assertions can only kill more, so the first 30 results stand.

| Mutant | Suite | Defect | Result | Failing tests (not failing unmutated) |
|---|---|---|---|---|
| PS-01 | python | a cancellation the save did not honour (it published) is judged an INVALID run and replaced, as the exit-3 path did | **KILLED** | `judge: a cancel run whose save published is VALID (failed), classified missed, with the time it took kept`<br>`M03: a cancellation the save did not honour (it published) at once: the run is VALID (status failed), kept, wi`<br>`M03: ...and it is classified as a miss (cancelClass), a measured behavioural failure, not harness noise`<br>`M03: ...never replaced: one attempt, one cancel child`<br>(+7 more) |
| PS-02 | python | a miss that took 9 s to return is MISSED, never STOP (PERF-15 (c)'s stop threshold is not read on a missed run) | **KILLED** | `PERF-15 (c): a cancellation that was not honoured, returned after 2.63 s: STOP (a miss is MISSED; STOP only ab`<br>`PERF-15 (c): a cancellation that was not honoured, returned after 30.0 s: STOP (a miss is MISSED; STOP only ab`<br>`M03: ...PERF-15 (c) and PERF-16 are STOP (never MET), and the gate result blocks acceptance` |
| PS-03 | python | a cancel point that was never reached is reported as an ordinary miss (its own class and reason are lost) | **KILLED** | `judge: a cancel point that was never reached is VALID (failed), classified never requested, with no time`<br>`judge: a record from before the "requested" field is judged as a requested cancellation`<br>`M03: a cancel point that was never reached (no cancellation requested) is a valid miss, never replaced, with i` |
| PS-04 | python | an attribution run whose save failed is handed to the checker, which says INVALID, and is replaced | **KILLED** | `test_judge_cancel_records raised FileNotFoundError`<br>`M03: an attribution run whose save failed is a VALID run (failed), never replaced (the checker is not asked: t` |
| PS-05 | python | what a run showed before the load rule invalidated it (a miss) is dropped from its row | **KILLED** | `M03: a miss seen in a run that the load rule invalidates is replaced under that rule but NOT lost: the invalid`<br>`M03: ...and the report names it in its caveats` |
| PS-06 | python | the opposite error: a child that died (no record) is a VALID failed run and is never replaced | **KILLED** | `a harness failure (no record) is an invalid run`<br>`judge: the harness child's exit 3 (it refused its own record) is invalid evidence, named as such`<br>`judge: a child that died (an unhandled exception) is an invalid run with its message`<br>`replacement: a harness child that dies (no record) is an invalid run, kept with its reason, and replaced (a se`<br>(+3 more) |
| PS-07 | python | a failed run is not a valid run (it is replaced, like an invalid one) | **KILLED** | `a failed save is a valid run: no replacement`<br>`...and its cell is MISSED on PERF-01`<br>`a cancel whose rollback assertion failed is MISSED, never met`<br>`a recovery that leaves a damaged Library is MISSED`<br>(+19 more) |
| PS-08 | python | a cancel record without its cancel object is read as a miss instead of an unfit record | **KILLED** | `judge: a cancel record without its cancel object cannot be interpreted: an INVALID run (replaceable), not a mi` |
| PS-09 | python | run refuses only a directory that holds session.json (the old rule): a refused, aborted or killed attempt runs again in place | **KILLED** | `M04: running the same session directory again is refused (exit 2), naming the refusal record`<br>`M04: ...and the refused attempt is preserved byte for byte (no run appended, no file rewritten)`<br>`M04: ...it runs to the end, and the refused attempt is still untouched`<br>`M04: running an aborted session directory again is refused (exit 2) and changes nothing: its partial runs are `<br>(+3 more) |
| PS-10 | python | run refuses refused.json and session.json only: an aborted or killed attempt (a raw table) runs again in place | **KILLED** | `M04: running an aborted session directory again is refused (exit 2) and changes nothing: its partial runs are `<br>`M04: the partial runs of the aborted attempt stay in its raw tables, with distinct run ids and orders (no dupl`<br>`M04: ...and cannot be run again`<br>`M04: a directory that holds a raw table but no outcome (a killed session) cannot be run again either, and read` |
| PS-11 | python | rebuild, outcome and the report ignore the negative control | **KILLED** | `flow: every budget is MET and the negative control FAILed as required`<br>`M04: a valid negative control (INVALID evidence captured again, then FAIL) is complete, exit 0, PERF-15 (a) ME`<br>`M04: ...rebuilt from the raw files: PERF-15 (a) is TEST-P1 FAILED (not MET) and blocks acceptance, and the liv`<br>`M04: ...the outcome command says so and exits 1`<br>(+3 more) |
| PS-12 | python | a session whose negative control PASSED finishes complete (exit 0) | **KILLED** | `M04: a negative control that PASSED is TEST-P1 FAILED in session.json and the run exits 1`<br>`M04: a control that never gave a valid FAIL in five attempts is TEST-P1 FAILED too (no evidence that the check` |
| PS-13 | python | a PASS of the control is cured by a later FAIL | **KILLED** | `control: a PASS is not cured by a later FAIL` |
| PS-14 | python | a control that PASSED is captured again (up to five times) until it FAILs | **KILLED** | `M04: ...the control is persisted and never replaced by another control run: one attempt of the variant in the ` |
| PS-15 | python | an unhandled exception exits 1 again, the code of "the negative control failed" | **KILLED** | `exit codes: an unhandled exception in the orchestrator exits 5, no longer the 1 of "the negative control faile` |
| PS-16 | python | an interrupt (Ctrl-C) is not recorded as an aborted attempt | **KILLED** | `M04: an interrupted session (Ctrl-C) is recorded ABORTED too, and the interrupt still propagates` |
| PS-17 | python | a session that completed with a valid result can be superseded (a rerun because of its result) | **KILLED** | `M04: a session that completed validly cannot be superseded (a rerun because of its result)` |
| PS-18 | python | an attempt can be superseded twice | **KILLED** | `M04: an attempt is superseded once: G1 already has its successor G2` |
| PS-19 | python | the refusal record lacks the quiet-check evidence | **KILLED** | `M04: ...and the quiet-check evidence: the summary (verdict, U mean, p95) in the record, the raw collections an` |
| PS-20 | python | a session whose negative control did not FAIL reads complete (status, outcome, attempts, report) | **KILLED** | `M04: ...the outcome command says so and exits 1`<br>`M04: ...the report says so: status, PERF-15 (a) outcome, the control table and its verdict; it does not say co`<br>`M04: ...a session whose checker is defective is invalidated by the rules: it can be superseded` |
| PS-21 | python | a session whose negative control did not FAIL is not an invalidated session (it cannot be superseded) | **KILLED** | `M04: ...a session whose checker is defective is invalidated by the rules: it can be superseded` |
| PS-22 | python | an aborted attempt is not written with its error | **KILLED** | `M04: a session that dies with an exception is recorded ABORTED (status, session id, error, how many runs it ha`<br>`test_flow_aborted_session raised KeyError` |
| PS-23 | python | PERF-15 (a) stays MET when the negative control did not FAIL | **KILLED** | `control: PERF-15 (a) is TEST-P1 FAILED although every attribution PASSes, and blocks acceptance`<br>`control: a session that is not judged reports NOT JUDGED and what it would be`<br>`M04: ...rebuilt from the raw files: PERF-15 (a) is TEST-P1 FAILED (not MET) and blocks acceptance, and the liv`<br>`M04: ...the report says so: status, PERF-15 (a) outcome, the control table and its verdict; it does not say co`<br>(+1 more) |
| PS-24 | python | the report prints no verdict line under the negative control table (a PASSed control is only a row) | **KILLED** | `M04: ...the report says so: status, PERF-15 (a) outcome, the control table and its verdict; it does not say co` |
| CS-01 | library | a cancel record whose save published must carry a rollback again (the harness child exits 3) | **KILLED** | `GateHarnessTests.ACancelPointThatWasNeverReachedIsRecordedAsNeverRequested`<br>`GateHarnessTests.ACancellationTheSaveDidNotHonourIsAMeasuredRecordNotAnUnfitOne`<br>`GateHarnessTests.ValidationAcceptsMeasuredFailuresAndRefusesWhatCannotBeInterpreted` |
| CS-02 | library | a save that failed early (fewer than two IMP-11 checks) is an unfit record again | **KILLED** | `GateHarnessTests.ASaveThatFailsAtBeginIsAMeasuredRecordNotAnUnfitOne`<br>`GateHarnessTests.ValidationAcceptsMeasuredFailuresAndRefusesWhatCannotBeInterpreted` |
| CS-03 | library | the exit-code mapping is reverted: a published cancel run exits 3 again | **KILLED** | `GateHarnessTests.ACancellationTheSaveDidNotHonourIsAMeasuredRecordNotAnUnfitOne`<br>`GateHarnessTests.ValidationAcceptsMeasuredFailuresAndRefusesWhatCannotBeInterpreted` |
| CS-04 | library | a cancel point that was never reached is recorded as requested | **KILLED** | `GateHarnessTests.ACancelPointThatWasNeverReachedIsRecordedAsNeverRequested` |
| CS-05 | library | a cancel record without its "requested" field is fit | **KILLED** | `GateHarnessTests.ValidationAcceptsMeasuredFailuresAndRefusesWhatCannotBeInterpreted` |
| CS-06 | library | a crash run's record is no longer exempt from the exit-3 rule | **KILLED** | `GateHarnessTests.ValidationAcceptsMeasuredFailuresAndRefusesWhatCannotBeInterpreted` |
| CS-07 | library | a record that cannot be interpreted exits 0 (the orchestrator would judge it) | **KILLED** | `GateHarnessTests.ValidationAcceptsMeasuredFailuresAndRefusesWhatCannotBeInterpreted` |

**31 mutants: 31 killed for the intended reason, 0 killed unintended, 0 survived, 0 not compiled.** Unmutated baselines: `session_selftest.py` all checks ok; `GateHarnessTests` 14 PASS.

**Equivalent mutants: none found.** Remarks on the kills: PS-13 (a PASS cured by a later FAIL) is killable only by the pure `judge_control` test, because the orchestrator stops at a PASS and can never produce that sequence (defence in depth,
tested for that reason); in PS-04 and PS-22 a test also fails with an exception (`raised FileNotFoundError` / `raised KeyError`), listed beside the intended failing check. In a first, trial run of the Python suite PS-11 ("rebuild ignores the
control") was killed only by a `KeyError` in a test (KILLED (UNINTENDED)); the tests now read the control with `.get` and assert it, and it is killed by the named checks above.

## 4. Results of the suites (commit `2021c7c`)

| Suite | Result |
|---|---|
| `python tests\perf\session_selftest.py` | **165 ok, 0 bad** (73 before; 103 ok and 36 BAD on the red commit's version of the file) |
| `python tests\perf\load_selftest.py` | 36 ok, 0 bad (unchanged) |
| `python tests\perf\attribute_run_selftest.py` | 77 ok, 0 bad (unchanged) |
| `StorageInventory.Library.Tests`, whole project, Release (`C:\sw\si.ps1 -Tree C:\sw\m34 run --no-build -c Release --project tests\StorageInventory.Library.Tests`) | **203 PASS, 0 FAIL, 0 SKIP** in 230 s (199 before, plus the four new `GateHarnessTests`); `SmokeGateTests.SmokeGateRunsTheWholePathAndMarksItselfNotAGateSession` (the real orchestrator against the real harness) passes with the new tooling |
| `GateHarnessTests` alone | 14 PASS (10 before) |
| `python tests\mutation\perf_session_mutants.py --check-only` | 31 mutants, every edit matches exactly once |

Not run, as instructed: any benchmark or gate session, `load_regression.py`, `verify_generator.py`, the integration audit project (no `src/` file and no audited assembly changed).

## 5. What I did not close, judgement calls, and what was surprising

**Judgement calls (each is a reading of the accepted text that the owner or the re-review may overrule; each is one small edit to undo).**

1. **STOP for a slow miss.** A cancellation that is not honoured is MISSED, and STOP when its measured request-to-return time is above 1.5 x the budget (section 1.2). The specification defines the stop threshold on time and says nothing about a cancellation that is ignored. Undo: the `over` branch of `gate_model.judge_cancel` (mutant PS-02 pins the present reading).
2. **A cancel point that was never reached is a valid miss, not an invalid run.** The accepted invalidity rules are the load rule and "an environmental reason recorded in its log (a disk error, a crash of the harness)"; "the harness did not reach its cancel point" is neither, and replacing it would hide a product that no longer reaches a point. It blocks acceptance (MISSED, never replaced). A deterministic cause ends the same way either way (six attempts, NOT MEASURED).
3. **`--supersedes` is checked against the files of the earlier attempt** (latest of the binary, and refused / aborted / never completed / negative control did not FAIL / cells NOT MEASURED). The review asked for "a new declaration naming the earlier one" and the specification for "a documented objective invalidation"; the old code accepted any free-text reason for any gate session of the binary, which is a rerun-because-of-the-result hole. The check is **tighter than the old behaviour**: an invalidation the files cannot show (an external fault the owner documents) cannot be declared with this tool and would need a reviewed change. Undo: `declare`'s `invalidation_reason` block (mutants PS-17 and PS-18).
4. **An interrupt (Ctrl-C) is recorded as an abort**, which consumes the attempt and makes it supersedable. The aborted attempt stays in the evidence and in the report ("every session, valid or not, is reported in full"), so nothing is hidden, but it does let an operator abandon a session whose first rounds looked bad. That is a process question the tooling cannot answer.
5. **A failed save is a valid run, also when its cause is a real disk error.** That is the review's own rule (§25.3, the repair record §7). The record keeps only the failure's message, not its kind, so the specification's "a disk error" clause cannot be applied automatically.

**Not closed.**

* **No end-to-end test of a published cancel run through the real child process.** The seam (`GateRunner.TestCancelBehaviour`) is a static of the test assembly and works in process (`GateBenchmark.Run`, exactly the child's code path). A seam that reached a child process (an environment variable) would also reach production children, so I did not build one; `SmokeGateTests` (real orchestrator, real harness) is unchanged and covers the honoured path. The Python flows script the child's exit code and record to what the C# tests show it now writes.
* **An unexpected exception out of `ImportSnapshotAsync`** (a product bug that is not an `ImportException` or `OperationCanceledException`) still crashes the child, which the orchestrator reads as "the harness child failed" and replaces. It is the same class of defect as M03 (a behavioural failure treated as harness noise) but the review does not name it, and an IO exception is a legitimate environmental case; a decision is needed on which exception types become a recorded failed save. Not touched.
* **An exit-3 run** (a record the harness could not interpret) keeps its record only in `runs.jsonl` (envelope with the exit code and the stderr tail), not in the judged row; the observation of a miss is kept in the row only for runs invalidated by the load rule or a failed round.
* **`only those cells are rerun`** (§15.4 Reruns, for cells NOT MEASURED): `declare --gate` still requires the whole matrix, as before; a session with cells NOT MEASURED can be superseded, but only by a whole-matrix gate session.
* **Manifest integrity (C4R-O12)** is unchanged: `refused.json` / `aborted.json` record the manifest's SHA-256, nothing re-verifies it; a smoke manifest edited to `gate: true` still runs as a gate session. `--supersedes` on a non-gate declaration is recorded and not validated.
* **Hosted CI** does not run `session_selftest.py` (as the review's C4R-O09 notes); nothing here changes that.
* The committed example smoke session predates `cancel.requested`: its records are judged as requested cancellations (tested), and only its rendered `report.md` was regenerated (three lines).

**Surprising.**

* The Python half of M03 was already correct for a record with exit code 0: the unreachable branch the review describes was unreachable only because of the C# validation. The fix is in the harness; the Python changes are the classification, the STOP reading, the attributed-save case and keeping what an invalidated run showed.
* The same exit-3 path swallowed two shapes the review does not list: a cancel point that was never reached, and an attributed save that fails (which the Python side then handed to the checker as INVALID evidence).
* The whole orchestrator (declare, quiet checks, rounds, blocks, controls, replacement passes, `finish`, `rebuild`, the report) runs in process over a virtual clock in a few hundred milliseconds per session, so the end-to-end flows are cheap (the 165 checks take about 12 s, a dozen and a half full sessions among them); a first version of the scripted world needed no change to the orchestrator beyond what the fixes need.
* `main` turned every `OSError` into exit 4 (an `outcome` on a missing directory says ABORTED), and any other exception into Python's exit 1; the new exit 5 covers the second only.
* The red commit `9725516` is red on purpose; squash it with the two fix commits if the history should be green at every step.
