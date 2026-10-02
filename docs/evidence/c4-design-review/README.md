# C4 design review: raw evidence

Supporting material for [`docs/v1.1-c4-design-review.md`](../../v1.1-c4-design-review.md). Nothing here is product code.

| Path | What it is |
|---|---|
| `harness.patch` | The scratch benchmark harness: `tests/StorageInventory.Library.Tests/DesignReviewBench.cs` and a one-line route in `LibraryBenchmark.cs`. Applies to `035dc90`. It touches nothing under `src/` |
| `probes.patch` | The design probes (per-source dictionary, chunked key-ordered interning, page-cache size, batched commits), as environment-gated changes under `src/StorageInventory.Library`. **Prototype only, never part of the product**; applied in a scratch worktree on top of `harness.patch` |
| `tables.py` | Summarises the JSON lines into the tables of the design review (`python tables.py results <plan>`) |
| `b1_table.py` | Builds §10.2's comparison of C4's mechanics with the per-source dictionary from all the result files (`python b1_table.py results`) |
| `spec_check.py` | The scripted consistency checks of the specification edits (§16 of the design review) |
| `results/<plan>.jsonl` | One JSON object per measured run (fields of `DesignReviewBench.Result`); a `crash` run holds the killed child's line and the recovery open's line |
| `results/<plan>.log` | The orchestrator's log: the plan's start and end (with the other processes' CPU, totals only), every child's console lines, every run's summary |

Paths of the scratch directory are written `<scratch>` and the user profile `<user>`; the names of other processes running on the
machine are removed from the load lines (their total CPU is kept). File names harvested for the real-vocabulary family were never
written to any log; only counts appear.

## Reproducing

```powershell
git worktree add ..\si-dr 035dc9016310350ca8f15751ba2160b239becf40
cd ..\si-dr
git apply <repo>\docs\evidence\c4-design-review\harness.patch
.\tools\fetch-tools.ps1
.\build.ps1 -Target Build -Configuration Release
$env:DOTNET_ROOT = (Resolve-Path .\tools\dotnet)       # the apphost runs on the repo-local runtime
$exe = '.\tests\StorageInventory.Library.Tests\bin\Release\net10.0-windows\StorageInventory.Library.Tests.exe'
& $exe --benchmark dr harvest real-names.bin          # names only, for the real-vocabulary family
$env:SI_DR_REAL_NAMES = (Resolve-Path real-names.bin)
& $exe --benchmark dr plan repro results 3             # also: analyse, mixed, rescan, newsource, rollback, size, extra, realrescan, big, prefillcheck
```

The probe plans (`probe:persource`, `probe:persource-real`, `probe:persource-analyse`, `probe:combined`, `probe:combined-analyse`,
`probe:chunked`, `probe:presort`, `probe:cache`, `probe:cache-analyse`, `probe:batch`) need `probes.patch` applied as well. Both patches build with no warnings under the
repository's `TreatWarningsAsErrors`.

Machine, load and method: §3 of the design review.
