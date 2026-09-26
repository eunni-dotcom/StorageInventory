# StorageInventory.ps1 — PowerShell reference implementation

A read-only storage inventory of a directory tree. This script is the **behavioural reference specification** for the
native app in `src/`. Change it only deliberately, and re-run the regression suite afterwards.

## Usage

```powershell
.\StorageInventory.ps1 -RootPath 'D:\' -OutputPath "$env:USERPROFILE\Inventory"
.\StorageInventory.ps1 -RootPath 'D:\Media' -OutputPath 'C:\Reports' -NoSort -SkipExcel
```

`Get-Help .\StorageInventory.ps1 -Full` documents every parameter, report column, ErrorType and ScanStatus value.

The script refuses to run if `-OutputPath` is the scan root or anywhere inside it, including through a junction,
symbolic link, trailing-dot spelling, device name or alternate-data-stream syntax. Aliases it cannot see in advance
(SUBST drives, `\\localhost\D$` shares, 8.3 short names) are caught by a runtime tripwire, which stops the scan.

**Exit codes:** `0` = the scan is complete. `2` = the scan finished, but some folders or entries could not be read, so
the totals are lower bounds. `1` = the run failed or was cancelled.

## Safety contract (what the code enforces)

| Property | Mechanism |
|---|---|
| Scanned files never modified or opened | Only `DirectoryInfo.EnumerateFileSystemInfos` and the metadata that listing returns |
| Reparse points never followed | Folder reparse points are listed and skipped. File reparse points are counted at the size the listing reports, and their targets are never read |
| Reports never overwrite anything | Every report file is created with `FileMode.CreateNew` in `New-ReportFileStream` |
| Workbook no-clobber | ImportExcel builds the workbook **in memory**, with no file path. The script writes `.xlsx.partial` with CreateNew, then `File.Move` (which never replaces an existing file) |
| Only one delete | `Remove-OwnTemporaryFile`: this run's own `.unsorted.tmp`, in the output folder, only |
| Only one rename | `Complete-OwnWorkbookFile`: this run's own `.xlsx.partial` becomes its final name |
| Incomplete scans can't pass as complete | The root's `SubtreeComplete` flag drives the headline, the warning and exit code 2 |
| Formula injection | Text starting with `= + - @` gets a leading `'`, so the CSV and the workbook show it as text |
| No hyperlinks | `-NoHyperLinkConversion '*'`. The script refuses to use an ImportExcel version that lacks it |

## Regression tests

```powershell
# from powershell\tests, in Windows PowerShell 5.1:
.\Run-Tests.ps1 -Shell powershell -Excel
.\Run-Tests.ps1 -Shell pwsh -Excel          # uses tools\pwsh\pwsh.exe if present
.\Measure-Performance.ps1 -Shell pwsh -Sizes 10000,60000,250000
```

Fixtures are built under `%TEMP%\StorageInventoryTests` and removed afterwards by a junction-safe cleanup. The Excel
tests need ImportExcel in `tools\psmodules`, which `tools\fetch-tools.ps1` saves there. It is never installed into
your module path.

| File | Purpose |
|---|---|
| `tests\Run-Tests.ps1` | Static audit, correctness against ground truth, safety cases, cancellation, write failure, no-clobber races, workbook tests |
| `tests\TestLib.ps1` | Fixture builder (junction loop, outward junction, deny-ACL folders, long path, Unicode/emoji, formula-like names, invalid FILETIME), snapshots and report verifier |
| `tests\Invoke-Cancellation.ps1` | Stops the script the way Ctrl+C does, either mid-scan or mid-workbook |
| `tests\Measure-Performance.ps1` | Wall time and peak working set at several sizes, sorted versus unsorted |

Test code uses things the script never uses (icacls, mklink, subst, Add-Type for an invalid FILETIME, Start-Process),
to *build* hostile conditions. None of it ships.

## Known limitations

- **Not tested here:** file and directory **symbolic links** need Developer Mode or admin, and mount points need
  admin. Junctions and real file reparse points (App Execution Aliases) are tested. Directory symbolic links and mount
  points go through the same `ReparsePoint` attribute check as junctions.
- **Not triggerable:** the "entry metadata could not be read" branch (`Partial:<type>` on an otherwise-listed folder)
  could not be provoked on NTFS, so it is covered by code review only.
- **Not representable:** CR/LF and `"` in file names can't be created through Win32, so they are untested. Quoting
  handles them anyway.
- **Hard links:** each link is counted separately. **Sizes:** these are logical `Length` values, not size on disk.
  **Alternate data streams:** not counted.
- **PowerShell 5.1 and paths over 260 characters:** these worked on the test machine. Where 5.1 can't read one, it is
  reported as `PathTooLong`.
