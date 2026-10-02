# C4 design review and design repair: raw evidence

Supporting material for [`docs/v1.1-c4-design-review.md`](../../v1.1-c4-design-review.md) (the design review, D-52),
[`docs/v1.1-c4-design-repair.md`](../../v1.1-c4-design-repair.md) (its repair after the independent re-review) and
[`docs/v1.1-c4-design-final-repair.md`](../../v1.1-c4-design-final-repair.md) (the final repair of the repair review's three
findings). Nothing here is
product code. **Read [`ERRATUM.md`](ERRATUM.md) before using the design review's figures**: it lists the defects of the original
evidence and which figures they affect. [`MANIFEST.md`](MANIFEST.md) lists every file with its size and SHA-256.

**Units:** MiB = 2^20 bytes, MB = 10^6 bytes; raw files hold bytes. Every script prints UTF-8 whatever the console code page.

## Files

| Path | What it is |
|---|---|
| `harness.patch` | The benchmark harness: `tests/StorageInventory.Library.Tests/DesignReviewBench.cs` and a one-line route in `LibraryBenchmark.cs`. Applies to `035dc90`; touches nothing under `src/`. **Corrected by the design repair** (prefill cache key and environment, explicit child environments, recovery environment recorded, pre-import copy after T0, older snapshots reported verified only when verified, MiB labels, the objective families `obj` and `objrescan` of the repaired §15.4 (`ObjSnapshot`), the IMP-11 summary, the `repair:*` plans). The design review's own runs used its earlier version (git history of this file, `1206a1d`) |
| `probes.patch` | Environment-gated design probes under `src/StorageInventory.Library` (per-source dictionary, key-ordered interning, cache size, batched commits) and, added by the repair, two instruments: `SI_DR_GUARD_LOG` (page count, page size, main and journal length through handles at every check) and `SI_DR_JOURNAL_COPY` (the journal copied immediately before `COMMIT`). **Prototype only, never part of the product**; applied in a scratch worktree on top of `harness.patch` |
| `tables.py` | The design review's tables from `results/` (`python tables.py results [plan ...]`) |
| `b1_table.py` | The design review's §10.2 comparison of C4's mechanics with the per-source dictionary (`python b1_table.py results`); marks with † the cells the erratum's E1 affects |
| `prefill_provenance.py` | Erratum E1: which run first built each prefill, under which environment, and which records ran on a prefill built with key-ordered interning (`python prefill_provenance.py results`) |
| `overlap.py` | Erratum E3: the design review's runs that overlapped one of its builds, with the build windows and their provenance (`python overlap.py results`) |
| `spec_check.py` | The scripted consistency checks of the specification (`python spec_check.py docs/v1.1-preproduction-spec.md`) |
| `name_census.py` | Aggregate-only file-name census of a directory tree (counts, ratios, length histogram; never a name or path) |
| `census/system.json`, `census/data.json` | The two censuses of the repair: the reference machine's system drive and a data volume of the same machine |
| `calibrate.py`, `census/calibration.json` | The objective families' calibration (length quantiles and repeat skew β per name model) derived from the censuses |
| `journal_ranges.py` | PERF-15 (a)'s key-range confinement check, an independent reader of the SQLite file format; **fail-closed since the final repair**: PASS (exit 0), FAIL (1) or INVALID (2) (`python journal_ranges.py <journal> <database copy> --source <id>` or `--new-source`, `--expect-journal-bytes <n>` from the run's record, optionally `--expect-copy-sha256`, `--layout persource\|global`) |
| `journal_ranges_selftest.py` | The checker's negative and positive self-tests on the three analysed runs of `repair:smoke` (`python journal_ranges_selftest.py <analysis dir> <repair-smoke.jsonl> --json <out>`) |
| `journal_ranges_rerun.py` | The fail-closed checker on the six analysed journals of a fresh `repair:structure` run, compared with `repair-results/ranges/*.json` |
| `workload_class.py` | §15.4's workload classes as a total function, checked from the specification's band and matrix tables (`python workload_class.py docs/v1.1-preproduction-spec.md`); also run by `spec_check.py` |
| `load_probe.py` | §15.4's load source **as corrected by the final repair**: the whole-machine PDH counter less the benchmark's own job-object time (`quiet <seconds>` or `run -- <command>`); per-process counters as diagnostics |
| `load_inject.py`, `load_experiments.py` | Exactly measured external load (sustained and short-lived processes) and the experiments that test the load source with it, against an idle null control and in alternating blocks (`--cycles`) |
| `load_harness.py` | The load source around a real `repair:timing` plan run inside the probe's job, between two quiet checks: the induced work (System, Microsoft Defender's engine, DPC and interrupt time) during each measured operation, which sizes the run rule's allowance |
| `repair_tables.py` | The repair document's rerun tables from `repair-results/` (`python repair_tables.py repair-results`) |
| `sanitize.py` | Copies a rerun's raw outputs into `repair-results/` with `<user>`, `<worktree>` and `<scratch>` placeholders and other processes' names removed |
| `privacy_scan.py` | The privacy scan run before committing (identity strings, absolute paths, harvested names, process names) |
| `manifest.py`, `MANIFEST.md` | The inventory and its generator |
| `results/<plan>.jsonl` | The design review's runs: one JSON object per measured run (fields of `DesignReviewBench.Result`); a `crash` record holds the killed child's line and the recovery open's line |
| `results/<plan>.log` | The design review's plan logs (start and end of each plan with other processes' total CPU, every child's console lines, every run's summary). Committed by the repair: the repository's `*.log` rule had kept them out (C4DR-O02); `.gitignore` here re-includes them |
| `repair-results/repair-*.jsonl`, `repair-*.log` | The repair's focused reruns: `structure` (key-range attribution and the negative control), `timing` (three runs of four cells), `rollback` (cancel and crash), `extra` (one stress first save, one stress re-scan, the clean worst-case existing-source cell), `dryrun-timing` (the four cells of `timing` once more, run beside the load probe), and `smoke` (tool checks at 100k files) |
| `repair-results/ranges/*.json` | `journal_ranges.py`'s output for the six analysed journals |
| `repair-results/load/*.json` | The load-validity dry run: the 60 s quiet check, the probe beside the four runs of `dryrun-timing`, and the handle-based process count for comparison (taken with the design repair's per-process probe, which the final repair replaces) |
| `final-repair/` | The final repair's results: `final-repair/classes.md` (`workload_class.py`), `final-repair/checker-selftest.json`, `final-repair/checker-rerun.json`, `final-repair/load/` (the load experiments' probe and injector files with their per-collection series, machine percentages only, `summary.json` and `summary-cycles.json`), `final-repair/harness-load.json` (the probe around a real `repair:timing` run) |

Paths of scratch directories are written `<scratch>`, the scratch worktree `<worktree>` and the user profile `<user>`; the names of
other processes running on the machine are removed from the load lines (their total CPU is kept). Harvested file names (the design
review's real-vocabulary family) and the census's names were never written to any file; only counts appear.

## Reproducing

```powershell
git worktree add ..\si-dr 035dc9016310350ca8f15751ba2160b239becf40
cd ..\si-dr
git apply <repo>\docs\evidence\c4-design-review\harness.patch
git apply <repo>\docs\evidence\c4-design-review\probes.patch          # the probe plans and every repair:* plan need it
.\tools\fetch-tools.ps1
.\build.ps1 -Target Build -Configuration Release
$env:DOTNET_ROOT = (Resolve-Path .\tools\dotnet)                         # the apphost runs on the repo-local runtime
$exe = '.\tests\StorageInventory.Library.Tests\bin\Release\net10.0-windows\StorageInventory.Library.Tests.exe'
$env:SI_DR_KEEP_ANALYSIS = '1'                                          # keep the journal and database copies for journal_ranges.py
& $exe --benchmark dr plan repair:structure results 1                   # also repair:timing (x3), repair:rollback, repair:extra, repair:smoke
python <repo>\docs\evidence\c4-design-review\journal_ranges.py results\analysis\<run>.precommit.journal results\analysis\<run>.before.sqlite3 --source 1 --expect-journal-bytes <n>
# <n>: the "before COMMIT: ... journal <n> B" figure of the run's Guard field in results\repair-structure.jsonl

# the final repair's checks
python <repo>\docs\evidence\c4-design-review\journal_ranges_rerun.py results                       # after repair:structure
python <repo>\docs\evidence\c4-design-review\journal_ranges_selftest.py smoke\analysis smoke\repair-smoke.jsonl --json selftest.json   # after repair:smoke in .\smoke
python <repo>\docs\evidence\c4-design-review\workload_class.py <repo>\docs\v1.1-preproduction-spec.md
python <repo>\docs\evidence\c4-design-review\load_experiments.py load --rounds 2
```

The design review's plans (`repro`, `analyse`, `mixed`, `rescan`, `newsource`, `rollback`, `size`, `extra`, `realrescan`, `big`,
`prefillcheck`; `probe:*` with `probes.patch`) still run; the real-vocabulary families need a names file from
`--benchmark dr harvest <file>` in `SI_DR_REAL_NAMES`, and with the corrected harness their prefills are keyed on that file's hash.
The census and calibration: `python name_census.py C:\ --label "system drive" --json census\system.json`, then
`python calibrate.py census\system.json census\data.json`. Both patches build with no warnings under the repository's
`TreatWarningsAsErrors` (checked by the repair on a fresh checkout of `035dc90`).

Machine, load and method: §3 of the design review; the repair's runs, §13 of the repair document.
