# Hosted evidence of the C4 repair: recipe and log excerpts (preserved from the temporary branch)

Preserves, ahead of any deletion of the temporary branch `v1.1/c4-repair-windows-evidence` (review observation C4R-O10), what exists only
there: the workflow, the recipe of the real-volume `SQLITE_FULL` run, and short excerpts of the run's raw log. The branch itself is **not**
deleted or touched. Hosted figures are information only: a hosted run never meets or misses a target (§15.4 "Hosted CI").

| Item | Value |
|---|---|
| Branch | `v1.1/c4-repair-windows-evidence` = `4f7c2790b986c5a5575df921edaa608cda401262` (`00af4f4` + two commits touching only the workflow file); unmerged |
| Run | GitHub Actions run `37123440802` (push to that branch), conclusion `success`, jobs `regression` and `engine`, `windows-2025`, created 2026-10-03T12:36:52Z |
| Code under test | `00af4f4`, which differs from the reviewed `935fc55` in `src`, `tests` and `.github` by one assertion message |
| The workflow | `c4-repair-windows-evidence.workflow.yml.txt` in this folder (verbatim; the `.txt` suffix keeps it from being a live workflow) |

## The recipe of the real-volume `SQLITE_FULL` run (the step "SQLITE_FULL on a really full volume")

On the elevated hosted runner (a developer machine is not elevated, so this is only possible there):

1. `diskpart` script: `create vdisk file="<RUNNER_TEMP>\si-full.vhdx" maximum=200 type=fixed`, `select vdisk`, `attach vdisk`,
   `create partition primary`, `format fs=ntfs quick label=SIFULL`, `assign letter=V` (a fixed 200 MB VHDX, NTFS: 14,770,176 bytes used and
   193,826,816 bytes free after the format).
2. Build `tests\StorageInventory.Library.Tests` in Release (`build.ps1 -Target Dotnet -DotnetArgs build ... -c Release -nodeReuse:false`).
3. `$env:SI_BENCH_ROOT = 'V:'`, then `StorageInventory.Library.Tests.exe --benchmark volume <RUNNER_TEMP>\library-volume.md`: the benchmark
   prefills a Library on the volume (3 snapshots of about 100,000 files in 2 sources, 39.8 MB), then imports a synthetic snapshot of
   1,997,023 files in 500,000 folders; the volume fills and the engine fails the import with `SQLITE_FULL`.
4. Expected and seen: the failure classified `LibraryFull (201)`, the Library exactly as before (3 snapshots, older snapshots intact, database
   39.8 MB before and after). **No space guard is wired in C4** (`ImportOptions` is null in that benchmark), so the failure is the engine's own
   `SQLITE_FULL` through the importer's classification; IMP-11's rule is C5's. The time of 7.07 s is the time to fail plus the rollback (the
   committed excerpt `hosted-volume-excerpt.txt` labels it "rollback alone", which is wrong: review observation C4R-O09).

## Short excerpts of the raw log (run 37123440802)

### Suites (step "Build and test (Release), locked restore")

```text
RESULT StorageInventory.Core.Tests: 106 PASS, 0 FAIL, 0 SKIP in 2.4 s
RESULT StorageInventory.History.Tests: 107 PASS, 0 FAIL, 0 SKIP in 0.2 s
RESULT StorageInventory.Library.Tests: 199 PASS, 0 FAIL, 0 SKIP in 131.6 s
RESULT StorageInventory.IntegrationTests: 193 PASS, 0 FAIL, 7 SKIP in 88.9 s
```

### The seven skipped integration tests

```text
SKIP  ExplorationTests.Largest_files_from_an_unsorted_scan_equal_the_sorted_reports_first_rows_including_ties (1 ms)  -- needs %TEMP%\StorageInventoryPerf\tree_10000 (man
SKIP  ParityTests.Large_benchmark_trees_reports_are_identical_when_requested (0 ms)  -- set SI_LARGE_PARITY=1 to compare the 60k and 250k benchmark trees
SKIP  ParityTests.Ten_thousand_file_benchmark_tree_reports_are_identical (0 ms)  -- benchmark tree %TEMP%\StorageInventoryPerf\tree_10000 not present
SKIP  PathPolicyFixtureTests.Short_8dot3_alias_of_the_source_is_seen_through (19 ms)  -- 8.3 short names are disabled on this volume
SKIP  UiScanTests.Closing_the_window_during_a_scan_waits_until_the_reports_are_closed (2 ms)  -- needs %TEMP%\StorageInventoryPerf\tree_60000 for a scan long enough to in
SKIP  UiScanTests.Declining_to_close_keeps_the_scan_running (0 ms)  -- needs %TEMP%\StorageInventoryPerf\tree_60000 for a scan long enough to interrupt
SKIP  UiScanTests.Progress_is_indeterminate_while_enumerating_and_cancel_stops_safely (0 ms)  -- needs %TEMP%\StorageInventoryPerf\tree_60000 for a scan long enough to in
```

### Publish (locked restore, CI=true)

```text
StorageInventory.exe 133650249
SIZE=133650249
SHA256=DF3B3AAA53D9823D1BF6DBE8D5A731DF5AD1ECDF5B4F1E3E7E6E6D618ADDCBF4
package: microsoft.data.sqlite.core 10.0.12
package: microsoft.netcore.app.runtime.win-x64 10.0.12
package: microsoft.windowsdesktop.app.runtime.win-x64 10.0.12
package: sqlitepclraw.core 2.1.12
package: sqlitepclraw.lib.e_sqlite3 2.1.12
package: sqlitepclraw.provider.e_sqlite3 2.1.12
```

### Native file set

```text
extraction directory: C:\Users\runneradmin\AppData\Local\Temp\.net\StorageInventory\bbCDWzX0F6DU
extracted files: D3DCompiler_47_cor3.dll, e_sqlite3.dll, PenImc_cor3.dll, PresentationNative_cor3.dll, vcruntime140_cor3.dll, wpfgfx_cor3.dll
e_sqlite3.dll: 1978880 bytes, SHA-256 B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E
PASS: the native file set is the five WPF natives plus e_sqlite3.dll, byte-identical to the package.
```

### Native-load experiments (Q-11, Q-22)

```text
PASS  the decoy assembly was built
PASS  the probe opened a Library
PASS  e_sqlite3.dll is loaded from the single-file extraction directory under %TEMP%\.net\<exe>\ -- C:\Users\RUNNER~1\AppData\Local\Temp\.net\SiProbe39de\cixZHdxdcgSS\e_sqlite3.DLL
PASS  its SHA-256 equals the package's runtimes\win-x64\native\e_sqlite3.dll -- B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E
PASS  the engine is SQLite 3.53.3 with the pinned source id
PASS  the managed SQLite assemblies come from the bundle, not from disk
PASS  with a dummy e_sqlite3.dll beside the exe the probe still opens its Library
PASS  the dummy beside the exe is not loaded (the module is still the extracted one) -- C:\Users\RUNNER~1\AppData\Local\Temp\.net\SiProbe39de\cixZHdxdcgSS\e_sqlite3.DLL
PASS  the dummy was not touched
PASS  a byte-identical COPY of the real DLL beside the exe is not loaded either (the module path is the extraction directory) -- C:\Users\RUNNER~1\AppData\Local\Temp\.net\SiProbe39de\cixZHdxdcgSS\e_sqlite3.DLL
PASS  the extracted DLL is gone while the app is closed
PASS  with a dummy planted beside the exe during the run that must re-extract, the host re-extracted the DLL and the app loaded the re-extracted file, not the dummy -- C:\Users\RUNNER~1\AppData\Local\Temp\.net\SiProbe39de\cixZHdxdcgSS\e_sqlite3.DLL
PASS  the re-extracted DLL has the pinned hash
PASS  with SQLitePCLRaw.batteries_v2.dll planted beside the single-file exe the probe still opens its Library
PASS  the planted managed assembly is not loaded (no assembly of that name, no module, no marker)
PASS  the runtime ASKED the application for SQLitePCLRaw.batteries_v2 (Microsoft.Data.Sqlite's by-name lookup) and the request was refused: not found means asked and refused -- Resolving SQLitePCLRaw.batteries_v2 | AssemblyResolve SQLitePCLRaw.batteries_v2, Culture=neutral, PublicKeyToken=null requested by Microsoft.Data.Sqlite
PASS  a by-name request for the planted assembly from the single-file exe is not satisfied -- notfound FileNotFoundException
PASS  a normal (non-single-file) build does not resolve an undeclared assembly beside it by name either -- notfound FileNotFoundException
PASS  CONTROL: the decoy, loaded by explicit path and initialised, writes its marker (it is a working decoy)
PASS  CONTROL: in a normal (non-single-file) build e_sqlite3.dll loads from the build folder beside the executable -- C:\Users\runneradmin\AppData\Local\Temp\si-native-d3b2f82f\control\SQLitePCLRaw.provider.e_sqlite3.dll
PASS  CONTROL: the Q-11 predicate (under the single-file extraction directory) FAILS for that load, so it can tell the two apart -- C:\Users\runneradmin\AppData\Local\Temp\si-native-d3b2f82f\control\SQLitePCLRaw.provider.e_sqlite3.dll
PASS  the Q-11 predicate PASSES for the single-file exe (re-stated beside its control) -- C:\Users\RUNNER~1\AppData\Local\Temp\.net\SiProbe39de\cixZHdxdcgSS\e_sqlite3.DLL
22 of 22 checks passed
```

### Gate tooling at smoke scale (nine classifier tests)

```text
PASS  WorkloadClassTests.FirstSaveBoundariesOnBothNameModels (6 ms)
PASS  WorkloadClassTests.InformationalComesFirst (1 ms)
PASS  WorkloadClassTests.InvalidCellsAreRejectedNotClassified (12 ms)
PASS  WorkloadClassTests.LowChurnRescansTakeTheirSourcesBand (0 ms)
PASS  WorkloadClassTests.MatrixCellsMapToTheClassColumn (5 ms)
PASS  WorkloadClassTests.MixedCasesAreTheHardestBand (0 ms)
PASS  WorkloadClassTests.NamedAdversarialFamiliesAreWorstCase (0 ms)
PASS  WorkloadClassTests.RescanBoundariesOfEveryChurnRatio (0 ms)
PASS  WorkloadClassTests.SweepAgreesWithTheReference (553 ms)
RESULT StorageInventory.Library.Tests: 9 PASS, 0 FAIL, 0 SKIP in 0.6 s
```

### TEST-P1 session step (non-gate; refused by its quiet check, no figure)

```text
^[[36;1m"perf_session exit code: $LASTEXITCODE"^[[0m
declared session 20261003-123732-4f7c2790-nongate: not a gate session (--gate was not given)
2026-10-03T12:37:33.302041Z session 20261003-123732-4f7c2790-nongate (not a gate session) started
2026-10-03T12:46:31.382328Z quiet check "before the session": NOT QUIET (U mean 6.92%, p95 19.83%, 120 collections)
2026-10-03T12:46:31.382749Z REFUSED: the quiet check before the session failed; the session does not start
perf_session exit code: 3
```

### Real SQLITE_FULL on a 200 MB NTFS VHDX (diskpart and the benchmark's lines)

```text
DiskPart successfully created the virtual disk file.
DiskPart successfully selected the virtual disk file.
DiskPart successfully attached the virtual disk file.
DiskPart succeeded in creating the specified partition.
DiskPart successfully formatted the volume.
DiskPart successfully assigned the drive letter or mount point.
V    14770176 193826816
=== 2M, the volume fills up
  prefilled: 3 snapshots of about 100,000 files in 2 sources; database 39.8 MB
  synthetic target: 1,997,023 files in 500,000 folders
  import wall time 7.07 s; peak working set 221 MB (before: 98 MB)
  after the failure: 3 snapshots, older snapshots intact: True, database 39.8 MB (was 39.8 MB)
  1,997,023 files, 7.07 s, 0 file rows/s, peak journal 0.8 MB, DB 39.8 MB
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Failure run | File rows attempted | Failure classified as | Time to fail and roll back | Database size before / after the failed import | Older snapshots intact |
|---|---:|---|---:|---|---|
| 2M, the volume fills up | 2,000,000 | LibraryFull (201) | 7.07 s (rollback alone) | 39.8 MB / 39.8 MB | yes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Failure run | File rows attempted | Failure classified as | Time to fail and roll back | Database size before / after the failed import | Older snapshots intact |
|---|---:|---|---:|---|---|
| 2M, the volume fills up | 2,000,000 | LibraryFull (201) | 7.07 s (rollback alone) | 39.8 MB / 39.8 MB | yes |
V    14770176 193826816
```

## Applicability to the repair-fixes code

The engine path of the real-volume run (the engine's `SQLITE_FULL`, the rollback, the classification) is not touched by the focused repair of
the five Mediums; what changed in the importer is the cadence of the checks and the inputs they read. `docs/v1.1-c4-repair-followup.md` says which
hosted evidence is repeated on the final code and which of the above still stands.
