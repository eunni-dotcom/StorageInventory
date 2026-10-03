# Library engine benchmark (v1.1 C4 repair, TEST-P1, Q-17)

**Status: no performance figure is claimed. PERF-01, PERF-14, PERF-15 and PERF-16 are NOT MEASURED.**

TEST-P1 is rebuilt to §15.4 (the accepted design authority: D-52 and D-53) and is ready to run, but a gate session starts only after an
objective quiet check, and the check failed on both machines available to the repair. This file therefore reports the tooling's
verification, the quiet-check records, the one real-volume `SQLITE_FULL` measurement and the exact commands for the owner's session.
It replaces the file of the blocked implementation, which is kept, marked historical, as `library-c4-blocked.md`.

## 1. Outcomes (§15.4 "Outcomes")

| Budget | Outcome | Why |
|---|---|---|
| PERF-01 (import throughput, representative and stress cells) | **NOT MEASURED** | the quiet precondition failed (section 2); no gate session was declared |
| PERF-14 (engine share of the save, 2M cells) | **NOT MEASURED** | same |
| PERF-15 (journal size and its page attribution, with the negative control) | **NOT MEASURED** at gate scale. The attribution gate and its negative control are verified at smoke scale (section 3) | same |
| PERF-16 (cancellation points, the kill at the journal's peak and the timed next open, CAN-01e) | **NOT MEASURED** at gate scale; the four cancel points, the kill and CAN-01e run end to end at smoke scale (section 3) | same |

None of these is MISSED or STOPPED: a figure that does not exist is not a failed figure. The C4 gate's acceptance (§19 C4) needs MET for PERF-01,
PERF-15 and PERF-14's engine share, so it is **pending** this measurement.

## 2. The quiet precondition (§15.4 "Method")

A gate session needs 120 collections (60 s) of whole-machine load with no benchmark operation running, mean at most 5% and 95th percentile at
most 15%, measured by `tests/perf/loadsource.py` (the PDH whole-machine counter, the benchmark's own job-object CPU subtracted, negative samples kept,
the logical-processor count over all groups).

| Machine | When | Result | Consequence |
|---|---|---|---|
| Development workstation (12th Gen Intel Core i7-1270P, Windows 11 Pro 10.0.26200), with the owner's own applications open | the day of the repair, after the mutant runs had finished | **NOT QUIET**: U mean 61.09%, p95 76.48%, max 82.57% (of which external counted processes 24.9%, induced 10.0%, unattributed 26.2%, DPC and interrupt 1.9%; the benchmark's own 0.0%) | no gate session; the owner's applications were not closed or manipulated to force a valid run (`docs/evidence/c4-repair/quiet-check-workstation.json`) |
| Hosted `windows-2025` runner (run 37123440802) | the evidence workflow's session, after its prefills | **NOT QUIET**: U mean 6.92%, p95 19.83%, 120 collections; the orchestrator recorded `REFUSED` and exited 3 | the hosted matrix did not run (a hosted figure would in any case never meet or miss a target, §15.4 "Hosted CI") |

## 3. What was verified instead

All at smoke scale or on synthetic input, through the real code path, on the workstation and in the hosted run:

| Item | Evidence |
|---|---|
| Generator | byte-identical port of the reference N1/N2/N3 families; pinned digests of 8 cells and 3 vocabulary slices; the reference's recorded 2M statistics reproduced (`GeneratorTests`) |
| Workload class | a total function over a closed family list; every boundary and boundary + epsilon; 331,356 cells compared with the reference function (`WorkloadClassTests`) |
| Attribution gate | `attribute_run.py` wraps the reference `journal_ranges.py` (vendored unmodified, SHA-256 pinned); 77/77 self-test cases, among them a missing pre-import hash = INVALID and a copy altered in an unjournalled page |
| Negative control | a real SQLite journal of a Library-wide dictionary (test-side) FAILS the gate on both variants (new source: 160 REMOTE pages; existing source: a level over the SHARED cap) (`NegativeControlBuildsRealJournalArtefacts`) |
| Load source | `load_selftest.py`; `load_regression.py` shows a sustained load and a short-lived process storm are caught and the benchmark's own CPU is subtracted (one run: the whole-machine source saw 91% of an injected storm, the old per-process sum 44%) |
| Session | `session_selftest.py`: replacement rules, every budget at its exact boundary, precedence of outcomes, round invalidation; the refusal path ran live |
| End to end | `SmokeGateTests`: an F and an R cell at about 50,000 files through prefill, copy, probes, run record and attribution (PASS), with the control FAILing; a scaled `--gate` declaration is refused. Example outputs: `tests/perf/example/` |
| Library suite | 199 of 199 pass, including the 28 harness tests |

## 4. `SQLITE_FULL` on a really full volume (Q-17, TEST-L9, D-R6)

Hosted run 37123440802, `engine` job: a 200 MB fixed VHDX formatted NTFS (14.8 MB used, 193.8 MB free before), mounted as `V:`, prefilled with 3 snapshots of
about 100,000 files in 2 sources (database 39.8 MB), then a synthetic 2M-file import (1,997,023 files in 500,000 folders) by the shipped code with no space guard
wired (C5 wires it), so the failure is the engine's own `SQLITE_FULL`.

| Run | File rows attempted | Failure classified as | Time to fail and roll back | Database before / after | Older snapshots intact |
|---|---:|---|---:|---|---|
| 2M, the volume fills up | 2,000,000 | `LibraryFull` (201) | 7.07 s | 39.8 MB / 39.8 MB | yes |

Import wall time 7.07 s, peak working set 221 MB (98 MB before). Raw excerpt: `docs/evidence/c4-repair/hosted-volume-excerpt.txt`. This is one hosted measurement on one
machine class; it shows the classification and the rollback to the original size, not a timing target.

## 5. To produce the gate figures (the owner's session)

On a machine that is quiet (no browser, no build, no scan; Defender not scanning the benchmark folder; on AC power), from a clean tree at the frozen commit:

```powershell
$env:DOTNET_ROOT = (Resolve-Path .\tools\dotnet)
.\build.ps1 -Target Build -Configuration Release
$exe = (Resolve-Path .\tests\StorageInventory.Library.Tests\bin\Release\net10.0-windows\StorageInventory.Library.Tests.exe)
python tests\perf\perf_session.py declare --gate --evidence docs\evidence\c4-gate --exe $exe --bench-root D:\gate-scratch
python tests\perf\perf_session.py run --session docs\evidence\c4-gate\<id> --exe $exe --bench-root D:\gate-scratch
python tests\perf\perf_report.py docs\evidence\c4-gate\<id> --out docs\benchmarks\library-gate.md
```

`tests/perf/README.md` describes the session directory, the exit codes and what each outcome means. The report is recomputed from the session's raw files.
