# Native benchmark: StorageInventory.Core (.NET 10)

Measured on 2026-09-29 with `build.ps1 -Target Bench`, i.e.
`StorageInventory.IntegrationTests.dll --benchmark 10000,60000,250000` in the **Release** configuration.

## Method

- **Same machine and trees as the Phase A benchmark** ([phase-a-powershell.md](phase-a-powershell.md)): 12th Gen Intel
  Core i7-1270P (16 logical CPUs), 32 GB, internal SSD, and the synthetic trees in `%TEMP%\StorageInventoryPerf`
  (40 files per album, 25 albums per artist, files of 0–599 bytes).
- **Each run is a fresh child process.** Peak working set is read by the process itself at the end of the run
  (`Process.PeakWorkingSet64`), so it is exact, not sampled. Phase times come from `StorageScanResult.Timings`.
- **Warm cache:** every tree is walked once before its runs, so every measured run sees a warm cache.
- **Background load:** CPU load was 66% when the run started, from other activity on the machine, so these are
  conservative figures.
- "Total" is measured inside the process and excludes process start-up (unlike the PowerShell wall times).

## Measured results

| Files | Mode | Enumerate (s) | Aggregate (s) | Folders report (s) | Sort + copy (s) | Total (s) | Peak working set (MB) | µs per file | Files CSV (MB) |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 10,000 | Sorted | 0.16 | 0.00 | 0.00 | 0.10 | 0.29 | 46 | 29 | 2.5 |
| 10,000 | Unsorted | 0.16 | 0.00 | 0.01 | — | 0.20 | 45 | 20 | 2.5 |
| 60,000 | Sorted | 0.82 | 0.00 | 0.02 | 0.79 | 1.66 | 52 | 28 | 15.1 |
| 60,000 | Unsorted | 0.76 | 0.00 | 0.02 | — | 0.81 | 48 | 14 | 15.1 |
| 250,000 | Sorted | 2.32 | 0.01 | 0.03 | 3.98 | 6.36 | 62 | 25 | 63.5 |
| 250,000 | Unsorted | 1.86 | 0.01 | 0.02 | — | 1.92 | 51 | 8 | 63.5 |

**An earlier run was discarded.** It warmed only each tree's top folder, so the first run of each size paid for a cold
cache (250k sorted: 10.1 s of enumeration against 2.4 s unsorted). The method was corrected to walk the whole tree
first.

## Comparison with the PowerShell reference (same trees, both warm)

| Files | Mode | Native total (s) | PowerShell 5.1 wall (s) | PowerShell 7.6 wall (s) | Native peak WS (MB) | PowerShell peak WS, sampled (MB) |
|---:|---|---:|---:|---:|---:|---:|
| 250,000 | Sorted | 6.4 | 45.6 | 69.0 | 62 | 209 (5.1) / 161 (7) |
| 250,000 | Unsorted | 1.9 | 32.6 | 51.1 | 51 | 213 / 161 |

The reports being compared are identical (see [native-parity-report.md](../native-parity-report.md)).

## Observations (from the data above only)

- **Sorting is the main extra cost.** At 250k files, sort + copy (3.98 s) takes longer than enumeration itself. The
  copy phase reads each row back from the temporary file by offset (random reads), then writes it in order.
- **Aggregation and the Folders report are negligible** for these trees (at most 6,501 folders).
- **Peak memory rises slowly** with file count (45 → 62 MB from 10k to 250k in sorted mode), consistent with the compact
  24-byte-per-file sort index plus one record per folder.

## Not claimed

- **No predictions for larger trees.** Sorting is O(n log n), and the copy phase's random reads behave very
  differently once the temporary file is larger than the OS cache, or when the report folder is on a hard disk. Memory
  depends heavily on the number of *folders*, which these trees keep small.
- **No cold-cache or USB hard-drive figures.** A first scan of an external HDD is usually limited by the drive's
  directory reads.

## Trade-off: external sort

A disk-backed external merge sort (sorted runs plus a k-way merge) would make the copy phase sequential and keep memory
flat regardless of file count, at the cost of more temporary files (each needing an owned-file delete) and more code.
With the index at 24 bytes per file (about 240 MB of index at 10 million files) and the measured costs above, the
simpler index approach is kept for v1. Revisit if measurements on multi-million-file drives or HDD output show the
copy phase dominating. **Unsorted mode already avoids it entirely.**

## Reproducing

```powershell
.\build.ps1 -Target Bench                      # needs the trees in %TEMP%\StorageInventoryPerf
powershell -File powershell\tests\Measure-Performance.ps1 -Shell pwsh -Sizes '10000,60000,250000' -KeepTrees   # builds the trees
```
