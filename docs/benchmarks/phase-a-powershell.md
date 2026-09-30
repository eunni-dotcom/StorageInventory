# Phase A benchmark: PowerShell reference implementation

Measured on 2026-09-26 with `powershell/tests/Measure-Performance.ps1` at commit `2715dfa`.

## Environment

| Item | Value |
|---|---|
| CPU | 12th Gen Intel Core i7-1270P |
| RAM | 32 GB |
| OS | Windows 11 Pro 10.0.26200 |
| Storage | Internal SSD (fixture trees in `%TEMP%`) |
| Shells | Windows PowerShell 5.1.26100.9168; PowerShell 7.6.6 (portable, `tools\pwsh`) |
| Options | `-SkipExcel` for every run |

## Fixture

Synthetic trees built by `New-BulkFixture` in `powershell/tests/TestLib.ps1`:

- 25 albums per artist and 40 files per album, so 1,000 files per artist.
- File sizes are random, 0–599 bytes (seed 42).
- The 10k tree has 10 artists (261 folders), the 60k tree 60 (1,561 folders) and the 250k tree 250 (6,501 folders).

Before the measured runs, each tree was listed once to warm the file-system cache. Every run therefore saw a **warm cache**.

## Measured results

"Wall" is the child process's total run time, including PowerShell start-up (roughly 1–2 s). "Peak WS" is the peak working
set, sampled every 50 ms while the process ran (see caveats).

### Windows PowerShell 5.1

| Files | Mode | Exit | Wall (s) | µs per file | Peak WS (MB) | Files CSV (MB) |
|---:|---|---:|---:|---:|---:|---:|
| 10,000 | Sorted | 0 | 5.2 | 523 | 214 | 2.5 |
| 10,000 | NoSort | 0 | 2.3 | 225 | 221 | 2.5 |
| 60,000 | Sorted | 0 | 12.5 | 208 | 218 | 15.1 |
| 60,000 | NoSort | 0 | 9.8 | 163 | 222 | 15.1 |
| 250,000 | Sorted | 0 | 45.6 | 182 | 209 | 63.5 |
| 250,000 | NoSort | 0 | 32.6 | 130 | 213 | 63.5 |

### PowerShell 7.6.6

| Files | Mode | Exit | Wall (s) | µs per file | Peak WS (MB) | Files CSV (MB) |
|---:|---|---:|---:|---:|---:|---:|
| 10,000 | Sorted | 0 | 4.6 | 459 | 162 | 2.5 |
| 10,000 | NoSort | 0 | 3.3 | 329 | 161 | 2.5 |
| 60,000 | Sorted | 0 | 17.2 | 287 | 160 | 15.1 |
| 60,000 | NoSort | 0 | 12.4 | 207 | 161 | 15.1 |
| 250,000 | Sorted | 0 | 69.0 | 276 | 161 | 63.5 |
| 250,000 | NoSort | 0 | 51.1 | 204 | 161 | 63.5 |

## Observations (directly from the data above)

- **Sorting costs more wall time.** Sorted runs took 1.3–2.3× as long as unsorted at every size and in both shells.
- **Memory stayed flat** from 10k to 250k files: about 210 MB in 5.1 and about 161 MB in PowerShell 7. That is mostly
  the runtime's own baseline. These trees have few folders (at most 6,501), and the file sort index at 250k files is a
  few MB.
- **Windows PowerShell 5.1 was faster than PowerShell 7.6** on this workload. The data doesn't show why, and it is
  noted here without an explanation.
- **Files CSV size** was about 266 bytes per file (63.5 MiB / 250,000). Each row carries the full path (about 90
  characters here), plus the relative path, directory, name, sizes, three timestamps and attributes.
- The µs-per-file figure at 10k is dominated by process start-up, so the 60k and 250k rows are more representative of
  per-file cost.

## What these numbers do NOT show

- **They are not predictions for other sizes.** Sorting is O(n log n), a larger sort index changes cache behaviour,
  and the sorted Files report is built with random reads from the temporary file, which behave very differently on a
  hard disk, or once the file is larger than the OS file cache.
- **They cover a warm cache on an internal SSD only.** A first scan of a USB hard drive is usually limited by the
  drive's directory reads, not by the CPU.
- **They don't show folder-heavy trees.** Memory scales with the number of folders (one record per folder), and these
  trees are file-heavy.
- **Peak memory may be under-reported.** It was sampled, so a short spike in the final sort/write phase just before
  exit could be missed. The native benchmarks (Phase B) measure it in-process instead.
- **They don't cover the workbook step.** Excel generation was switched off (`-SkipExcel`).

## Reproducing

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File powershell\tests\Measure-Performance.ps1 -Shell powershell -Sizes '10000,60000,250000' -KeepTrees
powershell -NoProfile -ExecutionPolicy Bypass -File powershell\tests\Measure-Performance.ps1 -Shell pwsh -Sizes '10000,60000,250000' -KeepTrees
```
