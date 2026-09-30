# Phase A report: PowerShell reference hardening

**STATUS: PASS**

**Reference commit:** `2715dfa`, "Harden PowerShell reference implementation (Phase A)"

## Implementation

`powershell/StorageInventory.ps1` is the behavioural reference specification for the native app. Phase A reviewed seven
findings (A1–A7). Each was confirmed against the code before it was fixed.

| Finding | Verdict | Fix |
|---|---|---|
| **A1** XLSX no-clobber | Correct: an `Exists` check, then `Export-Excel -Path`, left a race window | ImportExcel now builds the workbook **in memory** (`[OfficeOpenXml.ExcelPackage]::new()`, `-PassThru`) and never receives a path. The script writes `.xlsx.partial` with `FileMode.CreateNew`, then `File.Move`, which never replaces an existing file. |
| **A2** Partial workbook | Correct: if the Folders sheet failed after the Files sheet, a workbook was left behind while the status said "not created" | Nothing is written until the whole workbook exists in memory. A leftover file can only have the `.partial` name, and it is named in the status line. |
| **A3** incompleteFolderCount | Correct: an entry-metadata failure left `ScanStatus=OK`, so no warning appeared | The root's `SubtreeComplete` flag drives the headline, the warning and **exit code 2**. The warning separates locally-unreadable folders from affected ancestors. Entry failures now set `Partial:<type>`. |
| **A4** File reparse points | Correct: they were counted but not called out | Counted with the size the listing reports (a symlink counts as itself, usually 0 bytes), never followed, and reported as informational `ReparsePointFile` rows. |
| **A5** Hyperlinks | Correct: URL-shaped strings became hyperlinks | `-NoHyperLinkConversion '*'`. The script refuses to use an ImportExcel version without that parameter. |
| **A6** Superscript device names | Correct. Also, `\d` in .NET matches non-ASCII digits | `(COM\|LPT)[0-9¹²³]`, written as `¹²³` so the file stays pure ASCII. `CONFIG`, `COM10`, `NULL` and `COM٣` are still allowed. |
| **A7** Documentation claims | Correct | Memory is no longer described as "20 bytes per file". Benchmarks are recorded as measurements only. |

Also, report names now include a random six-hex-digit run ID: `Files_20260926_143012_a1b2c3.csv`.

## Safety changes

- **Every report file is created by the script with `CreateNew`,** including the workbook. ImportExcel never writes a
  file.
- **There is exactly one delete and one rename,** each behind three checks: the file was created by this run, it has
  the expected name pattern, and it sits in the validated output folder. The static audit enforces this at the level
  of PowerShell's syntax tree.
- **Excel is post-processing only.** The CSV reports are finished and closed before the workbook step starts, and a
  workbook failure never changes the scan result or the exit code.
- **Cancelling during the workbook step says so,** and states that the CSV reports are complete.

## Correctness

- **Files report checked exactly against ground truth,** including a 422-character path, Korean text, emoji, `[ ]`,
  `$( )`, backticks, apostrophes, commas and formula-like names.
- **Every folder recomputed by brute force** from the reports: direct and total size, file and subfolder counts,
  largest file, PercentOfRoot and PercentOfParent.
- **Completeness:** a denied folder propagates `SubtreeComplete=False` to its ancestors, which stay `ScanStatus=OK`.
  Siblings are unaffected, and skipped links never make a scan incomplete.
- **An invalid FILETIME** is reported as `InvalidTimestamp`, and the file is still counted.
- **The scanned tree is unchanged,** proven by before and after snapshots, both from the listing and from each item's
  own record.

## PowerShell 5.1

Windows PowerShell 5.1.26100.9168: **105 PASS / 0 FAIL / 5 SKIP** (suite `powershell/tests/Run-Tests.ps1 -Excel`).

## PowerShell 7

PowerShell 7.6.6 (portable, `tools\pwsh`): **105 PASS / 0 FAIL / 5 SKIP**.

## ImportExcel

ImportExcel 7.8.10 was saved into `tools\psmodules` only (never installed into a module path) and was exercised
dynamically on both shells. The tests cover:

- Workbook contents: sheets, row counts, no formula cells, no hyperlink cells, and numeric size columns.
- The hyperlink and formula mechanism, with a URL value and a guarded `=` formula.
- A no-clobber race on the final `.xlsx` name and on the `.partial` name.
- The Folders sheet failing after the Files sheet: no workbook and no partial file are left.
- Ctrl+C during the workbook step.

A probe also confirmed that without the apostrophe guard, Export-Excel turns a raw `=1+1` into a **formula even with
`-NoNumberConversion`**. The guard is therefore essential for the workbook as well as for the CSV files.

## Regression suite

Everything in `powershell/tests` is committed and can be re-run (see `powershell/README.md`). It covers:

- A static audit (syntax tree): allow-listed commands, no dynamic invocation, only the expected mutating .NET calls in
  the expected functions, FileStream modes, forbidden patterns, and pure ASCII.
- Correctness against ground truth, and the tree being unchanged.
- Completeness propagation and exit codes.
- Reparse behaviour: a junction loop, a junction pointing outside the tree, and 94 real App Execution Alias file
  reparse points.
- More than 30 path-safety cases: inside or equal to the root, junction root or output, SUBST tripwire, missing root,
  registry provider, device prefix, literal brackets and `$`, relative root, `-NoSort`, output is a file, trailing-dot
  alias, `:` stream syntax, NUL/CON.txt, superscript COM/LPT, and the allowed names.
- Ctrl+C during the scan and during the workbook step.
- The output folder becoming unwritable mid-run, and being unwritable from the start.
- CSV and XLSX no-clobber races, a partial workbook, and hyperlinks.

## Performance

These are measured values only. The full table, environment and caveats are in
[`benchmarks/phase-a-powershell.md`](benchmarks/phase-a-powershell.md). Warm cache, internal SSD, `-SkipExcel`:

| Shell | Files | Sorted (s) | NoSort (s) | Peak WS sampled (MB) |
|---|---:|---:|---:|---:|
| 5.1 | 10,000 | 5.2 | 2.3 | 214 / 221 |
| 5.1 | 60,000 | 12.5 | 9.8 | 218 / 222 |
| 5.1 | 250,000 | 45.6 | 32.6 | 209 / 213 |
| 7.6.6 | 10,000 | 4.6 | 3.3 | 162 / 161 |
| 7.6.6 | 60,000 | 17.2 | 12.4 | 160 / 161 |
| 7.6.6 | 250,000 | 69.0 | 51.1 | 161 / 161 |

No figures are given for larger trees. See the benchmark document for why these don't extrapolate linearly.

## Known skips

| Skip | Reason |
|---|---|
| File symbolic link | Creating one needs Developer Mode or admin; this machine has neither |
| Directory symbolic link | Same. It uses the same `ReparsePoint` check as the junctions, which are tested |
| Volume mount point | Creating one needs admin. It has the same reparse tag as a junction |
| CR/LF in a file name | Can't be represented through Win32 |
| `"` in a file name | Can't be represented through Win32 |

## Known limitations

- **One branch is covered by code review only:** "entry metadata could not be read" (`Partial:<type>`) couldn't be
  provoked on NTFS.
- **Hard links** are counted once per link. **Sizes** are logical `Length`, not size on disk. **Alternate data
  streams** aren't counted.
- **A folder swapped for a junction mid-scan** (between being listed and being entered) would be followed. That needs
  an active adversary during the scan. The traversal still ends, because path length bounds the depth, and it stays
  read-only. The native implementation re-checks attributes just before entering a folder (see the Phase B docs).
- **OneDrive and other cloud placeholder folders** are reparse points, so they are skipped rather than hydrated.
- **Long paths in 5.1:** they worked on the test machine. Where 5.1 can't read one, it is reported as `PathTooLong`.
