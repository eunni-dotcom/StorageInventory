# Native parity report: PowerShell reference ↔ StorageInventory.Core

**STATUS: PASS**

The oracle is the hardened reference `powershell/StorageInventory.ps1` (Phase A, `2715dfa`), unmodified. Each parity
test scans **the same tree** with both implementations and compares the reports:

| Report | Comparison |
|---|---|
| `Files_<run>.csv` | **Line by line, every column, in order.** Ordering is part of the specification: size descending, discovery order for ties (or discovery order with `-NoSort`). |
| `Folders_<run>.csv` | **Line by line, every column, in order** (total size descending, discovery order for ties). |
| `ScanErrors_<run>.csv` | Row count, and per row `Path` and `ErrorType`, in order. `Message` is compared exactly except for three row types whose wording differs intentionally (see below), which are compared by their leading text. |
| Outcome | Reference exit code 0 / 2 ↔ native `Complete` / `Incomplete`; a blocked path is refused by both. |

## Test evidence

`tests/StorageInventory.IntegrationTests/ParityTests.cs`, run on 2026-09-29:

| Tree | Oracle: PowerShell 7.6.6 | Oracle: Windows PowerShell 5.1 |
|---|---|---|
| Phase A adversarial fixture, sorted | Identical | Identical |
| Phase A adversarial fixture, unsorted (`-NoSort`) | Identical | Identical |
| Readable subtree (`Kpop\TWICE`): Complete in both | Identical | Identical |
| Unicode/bracket subtree (`Weird [brackets] $dollar ... 한국어 😀`) | Identical | Identical |
| Real file reparse points (`%LOCALAPPDATA%\Microsoft\WindowsApps`, 94 App Execution Aliases) | Identical | Identical |
| 10,000-file benchmark tree | Identical | Identical |
| 60,000-file and 250,000-file benchmark trees (opt-in, `SI_LARGE_PARITY=1`) | Identical | not run |
| Output inside the source | Both refuse | Both refuse |

The Phase A fixture covers the following, all identical in both implementations:

- Formula-like names (`=HYPERLINK(1).txt`, `-minus`, `+plus`, `@at`, a `-Dash Folder`, a name starting with `'`), commas,
  `[ ]`, `$( )`, backticks, `&`, `;`, Korean and emoji.
- A 422-character path, hidden and read-only attributes, and a 10-level-deep tree.
- A junction loop to the root, and a junction pointing outside the tree.
- Two deny-ACL folders (one nested, so its ancestors become incomplete), and a file with an out-of-range FILETIME.
- Split-archive extensions (`.001`, `.r00`), `.gitignore`, and a file with no extension.

Reproduce:

```powershell
.\build.ps1 -Target Build
tools\dotnet\dotnet.exe exec tests\StorageInventory.IntegrationTests\bin\Debug\net10.0-windows\StorageInventory.IntegrationTests.dll Parity
$env:SI_REFERENCE_SHELL = 'powershell'   # use Windows PowerShell 5.1 as the oracle
$env:SI_LARGE_PARITY = '1'               # include the 60k/250k trees (needs %TEMP%\StorageInventoryPerf)
```

## Equivalent behaviours

- **Enumeration:** metadata only, depth-first with an explicit stack, in the same order. Hidden and system entries are
  included.
- **Folder reparse points** (junctions, links, mount points, placeholders) are listed with `ScanStatus=ReparsePointSkipped`
  and never entered; they never make a subtree incomplete.
- **File reparse points** are counted with their listed size and reported as `ReparsePointFile`.
- **Unreadable folders:** `AccessDenied` / `Unreadable` status, `SubtreeComplete=False` propagated to every ancestor, and
  the scan continues.
- **Invalid timestamps:** a blank date, an `InvalidTimestamp` row, and the file is still counted.
- **Aggregation:** direct and total size, file and subfolder counts, the subtree's largest file, `PercentOfRoot` and
  `PercentOfParent`.
- **Report format:** identical column sets and headers, UTF-8 with BOM, CRLF, RFC 4180 quoting, the formula guard
  (leading apostrophe for `= + - @` tab CR LF), invariant numbers with 1024-based units and fixed decimals, local-time
  dates, and the file-type table (including case-insensitive split-archive rules).
- **Run naming:** `<Report>_<yyyyMMdd_HHmmss>_<6 hex>.csv`, never overwriting (create-new only), and the temporary file
  removed after a sorted run.
- **Outcome:** exit code 2 ↔ `Incomplete`; the root's completeness is authoritative in both.

## Intentional differences

| Area | Reference | Native | Why |
|---|---|---|---|
| Link description in `ScanErrors` | `Junction -> C:\target`, `SymbolicLink -> …` | `Link -> C:\target`, `Mount point -> Volume{…}` | .NET's `LinkTarget` reads a link's own data without following it, but doesn't distinguish a junction from a directory symlink without further native calls. Behaviour is identical. |
| Invalid-timestamp message (PowerShell 7 only) | `Modified time: You cannot call a method on a null-valued expression.` | `Modified time: Not a valid Win32 FileTime. (Parameter 'fileTime')` | PowerShell 7 turns the throwing `LastWriteTime` getter into `$null`, so the reference records a PowerShell error instead of the underlying .NET one. Type, blank date and counting are identical. This is not a behavioural defect, so the reference is left unchanged. |
| Relative paths | Resolved against the PowerShell location | Blocked unless the caller supplies a base directory | A GUI has no meaningful current directory. |
| Workbook | ImportExcel/EPPlus in memory, only if installed | Built-in writer, always available | No dependency, no Excel required. See "Native improvements". |

## Native improvements (safer or more correct)

1. **Alias-aware validation before anything is created.** `GetFinalPathNameByHandle` sees through SUBST drives, 8.3
   names and letter case, so "the output is really inside the source" is blocked up front. The reference caught it only
   mid-scan, after files already existed. The runtime tripwire remains as a second line of defence.
2. **Trailing dots and spaces are always refused.** .NET silently strips them, so both runtimes would otherwise
   rewrite such paths silently.
3. **Re-check before entering a folder.** A folder swapped for a junction after being listed is skipped, not followed
   (a race the reference documented as a limitation). A deterministic test covers it.
4. **Timestamps can't abort a listing.** Unrepresentable timestamps are handled inside the listing transform.
5. **Failure kinds are typed:** `InvalidPaths` / `OutputError` / `OutputInsideScannedTree` / `InternalConsistency` /
   `Unexpected`. Report-write failures can never be mistaken for scan problems.
6. **More invariants:** direct-value sums, totals at least as large as direct values, skipped links being empty,
   unreadable folders never marked complete, no double aggregation. Each is proven to fire by a unit test.
7. **The sort index uses 24 bytes per file in one in-place-sorted array**, with no separate key and item arrays.
8. **Workbook with no formula or hyperlink code path at all,** streamed from the CSVs, so there's no in-memory
   workbook.

## Known OS-dependent cases

- **Directory and file symbolic links** can't be created here without Developer Mode or admin, so those parity cases
  weren't run. They take the same `ReparsePoint` path as the junctions, which were tested.
- **Volume mount points** need admin to create. They have the same reparse tag as junctions.
- **8.3 short-name aliases** are disabled on this test volume, so that validation test is skipped. The SUBST alias
  test ran.
- **CR/LF and `"` in file names** can't be represented through Win32.
- **NTFS folder timestamps:** the first listing of a freshly created tree can show folder modification times that NTFS
  hasn't yet propagated to the parent index. Parity tests settle each tree with one listing first, so both
  implementations observe the same values (the effect was documented in Phase A).
