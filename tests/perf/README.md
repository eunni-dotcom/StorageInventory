# TEST-P1: the performance-gate tooling

The tooling that runs and judges the C4 engine benchmark of `docs/v1.1-preproduction-spec.md` §15.4 on the repaired production code (PERF-01,
PERF-14, PERF-15, PERF-16's engine part). Two halves:

* **The harness** (C#): `tests/StorageInventory.Library.Tests/PerfGate/`, run as `StorageInventory.Library.Tests.exe --benchmark gate <command>`.
  One command is one fresh process: `plan`, `prefill`, `describe`, `run`, `recover`, `control`, `machine` (see `GateBenchmark.cs`).
  It holds the ported objective generator and name families (`ObjGenerator.cs`), §15.4's workload classes as a total function
  (`WorkloadClass.cs`), the 16 cells of the matrix (`GateMatrix.cs`), the run record (`RunRecord.cs`), the measuring child (`GateRunner.cs`) and the
  test-side negative control (`NegativeControl.cs`).
* **The tools** (Python, standard library only; this directory): the orchestrator and report, the fail-closed attribution gate, the whole-machine
  load source, and their self-tests. The reference tools stay untouched in `docs/evidence/c4-design-review/`; `vendor/journal_ranges.py` is an
  unmodified copy of the reference checker, pinned by SHA-256 in `attribute_run.py`.

## Running a real gate session (the owner's, on the reference machine)

Build Release from a clean tree at the frozen commit (the manifest records the commit and the SHA-256 of the build output, and the session refuses
to run on any other binary). The machine must be quiet: no browser, no build, no scan, Defender not scanning the benchmark folder, on AC power.

```powershell
$env:DOTNET_ROOT = (Resolve-Path .\tools\dotnet)                                # the apphost runs on the repo-local runtime
.\build.ps1 -Target Build -Configuration Release
$exe = (Resolve-Path .\tests\StorageInventory.Library.Tests\bin\Release\net10.0-windows\StorageInventory.Library.Tests.exe)

# 1. declare: writes <evidence>\<id>\manifest.json BEFORE anything runs (identifier, binary commit and build-output hash, the 16 cells, the run order,
#    five rounds, the declared start, "gate": true). It refuses a gate declaration that is not the whole matrix at scale 1 with five rounds, with load
#    validity, from a clean tree, and a second gate session for the same binary unless it supersedes the latest attempt AND that attempt was invalidated
#    by the rules (--supersedes <id> --reason <text>; see "Attempts" below).
python tests\perf\perf_session.py declare --gate --evidence docs\evidence\c4-gate --exe $exe --bench-root D:\gate-scratch

# 2. run it: prefills (once per cell's prefill parameters, in a separate process of the same binary), the QUIET CHECK before the session (60 s; a
#    failure refuses the session and records the refusal), one logged warm-up, five rounds round-robin over the cells with a quiet check after each,
#    the attribution run of every cell, the four cancel points of every non-informational cell, the kill at the journal's peak with the timed
#    start-up open, a cancellation after the final check (CAN-01e: it publishes), the deletion of a 2M snapshot, the negative control, replacement runs for every invalidated run, and the outcomes.
python tests\perf\perf_session.py run --session docs\evidence\c4-gate\<id> --exe $exe --bench-root D:\gate-scratch

# (declare and run in one step:  perf_session.py go --gate --evidence ... --exe $exe)

# 3. report: the markdown tables of docs/benchmarks/library.md, recomputed from the session's raw files (every session given is reported in full)
python tests\perf\perf_report.py docs\evidence\c4-gate\<id> --out docs\benchmarks\library-gate.md
python tests\perf\perf_session.py outcome --session docs\evidence\c4-gate\<id>        # the outcomes only
```

The session directory holds: `manifest.json` (declared first), `machine.json`, `quiet.jsonl` (every quiet check), `runs.jsonl` (the append-only raw run
records, one per attempt), `judgements.jsonl` (append-only: each run's validity and reasons, and round invalidations), `collections.jsonl` (every load
collection of the session), `attribution/` (the checker's verdict for every attribution and control run), `analysis/` (journal copies; the database
copies of the attribution runs are deleted after their verdict unless `--keep-analysis`, they are over 1 GB at 2M files), `events.log`, `session.json`
(derived), and `refused.json` or `aborted.json` when the session did not run to the end.

**Exit codes** (distinct on purpose; the same for `run`, `go` and, where it applies, `outcome`):

| Code | Meaning |
|---|---|
| 0 | done: read the outcomes |
| 1 | the negative control did not FAIL (the checker is defective: TEST-P1 FAILED; the session and its PERF-15 (a) say so) |
| 2 | refused or usage: a declaration the rules refuse (not the whole matrix, a second gate session, `--supersedes` without an invalidation), or `run` on a directory that already holds a started session |
| 3 | the quiet check before the session failed: the session was refused (`refused.json`) |
| 4 | aborted by an `OSError` (`aborted.json`) |
| 5 | this tool itself failed (an unhandled exception; for a session already started `aborted.json` says so). It used to exit 1, the code of "the negative control failed" |

The harness child (`--benchmark gate run`) exits **0** whenever its record can be interpreted, whatever the run's result (a save that failed, a cancellation
the product did not honour, a rollback that did not hold are measured results), and **3** only for a record that cannot be interpreted as raw evidence.

### Attempts: a session directory is run once

A session directory is an **attempt**. `run` refuses any directory that holds anything of a started session (`refused.json`, `aborted.json`, `session.json`,
`runs.jsonl`, `judgements.jsonl`, `quiet.jsonl`, `events.log`, ...): a refused, aborted, interrupted or finished attempt is kept exactly as it is, never run again
in place (a second pass would append to the same raw tables, with clashing run identifiers and orders), and the report and `attempts` list it like any other.
A **retry is a new session**, declared with `--supersedes <earlier id> --reason <text>` (§15.4 "Reruns": a new session starts only after a documented
objective invalidation and is declared as such). The declaration is checked against the files of the earlier attempt:

* it must be the **latest** gate session of the binary (an attempt is superseded once);
* it must have been **invalidated by the rules**: refused (the quiet check before the session failed), aborted (an exception, an `OSError` or an interrupt),
  never completed (a raw table and no `session.json`: the process died), its negative control did not FAIL, or cells NOT MEASURED. The derived reason is
  written into the new manifest (`invalidationOfSuperseded`). A session that completed with a valid result, **whatever the result**, cannot be superseded:
  a rerun because of a result is what the rules forbid.

`refused.json` and `aborted.json` are explicit attempt records: status, session identifier, gate flag, what it supersedes, the manifest (file and SHA-256),
the declared cells, rounds, scale and binary, where the evidence is (`quiet.jsonl` and `collections.jsonl` for a quiet check, the raw tables), the reason
or error, and how far the session had got. `python tests\perf\perf_session.py attempts --evidence <dir> [--json]` lists every attempt of an evidence directory with
its status (`declared (not run)`, `REFUSED`, `ABORTED`, `INCOMPLETE`, `complete`, `TEST-P1 FAILED: ...`).

**Smoke** (minutes, never a gate session): `python tests\perf\perf_session.py go --smoke --no-load-validity --evidence $env:TEMP\gate-smoke --exe $exe`
runs one first save and one re-scan at about 50,000 files, two rounds, through the real path. `--no-load-validity` skips the quiet checks and the run
rule and marks the session NOT a gate session (every budget is reported NOT JUDGED). `--scale s` and `--cells a,b` scale and subset the matrix; a class
is always decided by the cell's NOMINAL parameters, never by the size run or by a result. The C# test `SmokeGateTests` runs this end to end.

`example/smoke-session/` is the output of one such smoke session (small; no path or name of the machine; `report.md` is `perf_report.py`'s rendering of
it) and `example/load-regression-quick.json` is one `load_regression.py --quick` run. They are examples of the file formats, not results: the budgets
of the smoke session are NOT JUDGED.

## What the session decides, and by what

* **Validity** is the measured environment only (`loadsource.py`, §15.4): `U` = `\Processor Information(_Total)\% Processor Time` (PDH, added with
  `PdhAddEnglishCounterW`, collected every 500 ms) minus the benchmark's own time from a **job object** that holds the orchestrator and every child
  (`JobObjectBasicAccountingInformation`, exited children included; an unelevated job; a child outside it is refused loudly). Quiet check: `U` mean
  <= 5% and p95 <= 15% over 120 collections. Run: mean <= 10% and p95 <= 25% over the collections **wholly inside** the measured operation (none inside
  is INVALID). The three details the final recheck left implicit (C4DRRR-O03) are explicit and tested in `load_selftest.py`: negative `U` samples are
  **kept unclamped**; only collections wholly inside the operation count; the processor count is `GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)`.
  Per-process counters are diagnostics and never decide. `load_regression.py` shows that `U` catches sustained external CPU and a storm of
  short-lived processes and subtracts the benchmark's own CPU, with the old per-process sum recorded only as a labelled negative control.
* **A run** is its own child process over a flushed copy of its cell's prefill; prefills are cached under a key naming every input, including the
  binary's commit and the hash of its build output. **A round** is invalid when the quiet check after it fails; its load-dependent runs are marked
  invalid and replaced (at most five more attempts; NOT MEASURED after ten attempts of a five-run cell). **No valid run is discarded or replaced for its
  result**: a failed save is a valid run whose cell is MISSED (also a save that failed at BEGIN, before a second IMP-11 check, and an attributed save that
  failed). The cell's figure is the median of its first five valid runs in run order, with the minimum and maximum. Invalid runs, rounds and sessions stay in
  the raw tables with their reasons; what a run showed before an environmental rule invalidated it (for example a cancellation the save did not honour) stays
  in its row, and the report names it.
* **A cancel run** is classified (`cancelClass` in the run's metrics, shown in the report): **expected cancellation** (requested, rolled back, asserted:
  judged on its time against PERF-15 (c)); **missed** (requested, and the save published or failed instead, or the rollback did not hold) or **never
  requested** (the harness did not reach the cancel point): a valid, MEASURED failure that stays in the record and is never replaced (PERF-15 (c) and PERF-16's
  engine part are MISSED, and STOP when the time from the request to the return is above 1.5 times the budget, which is PERF-15 (c)'s stop threshold read on
  the measured time); **invalid** (the child died or refused its own record, the load rule, a failed quiet check after the round): replaced under the accepted
  environmental rules only. A child that cannot be started at all is an `OSError`: the session aborts (recorded) instead.
* **PERF-15 (a)** (`attribute_run.py`): one attribution run per cell; the database is copied after T0 commits and before BEGIN (its length and SHA-256
  recorded as it is made) and the journal immediately before COMMIT (its length read through a live handle recorded). The checker reads the target,
  the lengths and the hash **only from the run's record**; the hash is mandatory; the verdict is `{"verdict": "PASS" | "FAIL" | "INVALID", ...}` with
  exit codes 0/1/2 and callers decide on the verdict. INVALID is captured again; it is never PASS. A journal is deterministic, so an attribution run is
  judged by the checker, not by the load. The **negative control** (a Library whose name index is keyed by the name alone, built test-side with real
  SQLite journaling, importing into a new and into an existing source) must FAIL once per session; PASS means the checker is defective. It is part of the
  result: a session whose control did not FAIL (or never gave a valid FAIL in five attempts) is **TEST-P1 FAILED** in `session.json`, `outcome`, `attempts` and
  the report, its PERF-15 (a) is TEST-P1 FAILED and blocks acceptance, and the control is never replaced by another run (a PASS is final; only INVALID
  evidence is captured again).
* **Outcomes** per budget (MET / MISSED / NOT MEASURED / STOP, `gate_model.py`): PERF-01 (median of five valid runs >= 100,000 rows/s in the four
  representative cells; STOP below 50,000), PERF-14 (T-IMPORT <= 45 s in representative and stress cells at 2M; STOP above 67.5 s, or 90 s in a worst-case
  cell), PERF-15 (a), PERF-15 (c) (the largest of the four cancel points <= 1.75 s representative, 4.75 s otherwise; the start-up open after the kill
  <= 5 s; STOP above 1.5 times), PERF-16's engine part (the same cancel figures), and the token-check interval (<= 0.5 s). A MISSED budget is never
  reported as met because no stop threshold was crossed. A session that is not a gate session reports NOT JUDGED.

## Self-tests (all runnable with plain `python`, no benchmark needed)

| Command | What it proves |
|---|---|
| `python tests\perf\attribute_run_selftest.py` | 77 cases on small real captures (`fixtures/gate-fixtures.zip`): the reference's negative cases, a record without the copy hash, a wrong copy differing only in an unjournalled page (and that the reference checker alone passes it), wrong copies, wrong targets, altered lengths, empty / truncated / bad-checksum journals, the strict interface |
| `python tests\perf\session_selftest.py` | the replacement rules, the median of the first five valid runs, every budget at its exact boundary, the precedence of outcomes, round invalidation, the run rule on synthetic collections; and the real orchestrator end to end over a virtual clock with scripted children (a cancel that published, a save that failed at BEGIN, a child that dies, a refused, aborted or interrupted session, a re-run, a new declaration after a refusal, a failed and a valid negative control, the exit codes) |
| `python tests\mutation\perf_session_mutants.py [--suite python\|library] [--check-only]` | the C4R-M03 / C4R-M04 mutants of this tooling and of the harness's record validation and exit code, each applied to a scratch copy and killed (or not) by `session_selftest.py` or `GateHarnessTests` for its intended reason (minutes for the Python suite, an SDK build per mutant for the C# one) |
| `python tests\perf\load_selftest.py` | the three explicit load details, the thresholds at their boundaries, the job object, one live sample |
| `python tests\perf\load_regression.py [--quick]` | live: external CPU, a short-lived-process storm and the benchmark's own CPU (about 5 minutes; inconclusive on a machine already saturated) |
| `python tests\perf\verify_generator.py --exe $exe` | the C# generator reproduces the reference's recorded statistics at 2M files, figure for figure |
| `python tests\perf\fixtures\make_fixtures.py --exe $exe` | regenerates the fixtures from the harness |
| `python tests\StorageInventory.Library.Tests\PerfGate\golden\make_class_sweep.py` | regenerates the golden class sweep from the Python reference |
