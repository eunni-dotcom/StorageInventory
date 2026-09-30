# Native security review (Gate B10)

**STATUS: PASS** (see "Evidence").

This review was done separately from implementation, reading the shipped code (`src/`) as an adversary would and asking
*"if this were malicious or buggy, where could it hurt the user's files or privacy?"* The claims below are enforced by
`tests/StorageInventory.IntegrationTests/SecurityAuditTests.cs`, which fails if the code drifts: it checks the source
text, the compiled assemblies' references, the P/Invoke methods found by reflection, and the manifest.

## Safety contract: verdict

| Property | Verdict | Evidence |
|---|---|---|
| Scanned files never modified | Holds | The only calls that touch the scanned tree are `FileSystemEnumerable` (listing), `File.GetAttributes` (re-check before entering a folder), `FileSystemInfo.LinkTarget` (reads a link's own data) and `DirectoryInfo` timestamps/attributes. There are no write, delete, move or attribute APIs anywhere except `ReportRun`, which only touches the validated output folder. Before/after snapshots are identical in the tests. |
| Scanned file contents never opened | Holds | No `File.Open*`/`FileStream` on anything but our own reports; `new FileStream` exists only in `ReportRun` (CreateNew), `ReportWriters` (read-only, our temporary file) and `ReportCsvReader` (read-only, our reports). |
| Reparse points never followed | Holds | Folder reparse points are listed and skipped; file reparse points are counted with their listed size. The only policy value is `NeverFollow`, and there is a re-check before each folder is entered. |
| No admin rights | Holds | The manifest says `asInvoker`; there is no `runas`/`Verb`. Unreadable folders become `AccessDenied` rows. |
| No registry or system configuration changes | Holds | No `Microsoft.Win32.Registry` use or reference; no services, scheduled tasks, startup entries or environment changes. |
| No network activity | Holds | No `System.Net` use and no reference from Core. Network paths are only touched if the user types one, with a warning. |
| No telemetry, analytics or updater | Holds | None present; the audit test forbids the terms and APIs. |
| Existing files never overwritten | Holds | Creation is only `FileMode.CreateNew`; the only rename is `File.Move(..., overwrite: false)`. Tests exist for races on CSV, `.xlsx` and `.partial` names. |
| Cancellation and failure leave the tree untouched and never look complete | Holds | Four distinct `ScanCompletionState`s; `Cancelled`/`Failed` expose no reports and list every created file as incomplete; nothing is deleted automatically. |
| Every output mutation has a narrow purpose | Holds | See the inventory below. |

## API inventory: everything that can write, delete, move, launch or call native code

| API | Where (only here) | Purpose | Writes? | Scope and guards |
|---|---|---|---|---|
| `Directory.CreateDirectory` | `Core/Reports/ReportRun.cs` `EnsureOutputFolder` | Create the report folder if missing | Yes (one folder) | Only the validated output folder, after `PathPolicy.Validate` has passed |
| `new FileStream(…, FileMode.CreateNew, FileAccess.Write, FileShare.Read)` | `ReportRun.CreateNew` | Create report, temporary and partial-workbook files | Yes (new files) | Only this run's six names, only directly in the output folder, only while the folder is not a reparse point; fails rather than overwrite |
| `File.Delete` | `ReportRun.DeleteOwnTemporaryFile` | Remove this run's `Files_<run>.unsorted.tmp` after a sorted report is written | Yes (one file) | Must be created by this run, be in the output folder and be exactly that temporary name; no folder or recursive delete exists anywhere |
| `File.Move(…, overwrite: false)` | `ReportRun.PublishOwnWorkbook` | `.xlsx.partial` to `.xlsx` | Renames one file | Must be created by this export, be in the output folder, and have the matching names; never replaces a file |
| `new FileStream(…, Open, Read)` | `ReportWriters.WriteSortedFiles`, `ReportCsvReader` | Read our own temporary file and reports | No | Own files only |
| `FileSystemEnumerable<T>` | `Core/Scanning/ScanEngine.cs` | List one folder (metadata) | No | Never recursive by itself; never entered for reparse points |
| `File.GetAttributes`, `DirectoryInfo` properties, `FileSystemInfo.LinkTarget` | `ScanEngine`, `PathPolicy`, `ReportRun` | Read attributes, timestamps and link targets | No | Link data is read, never followed |
| `CreateFileW` (desired access **0**) + `GetFinalPathNameByHandleW` | `Core/Paths/NativeMethods.cs` | The real location of the source/output folders, to see through aliases | No | The **only** P/Invoke in the product (checked by reflection); a zero-access handle can't read, write or delete |
| `DriveInfo.DriveType` | `PathPolicy.IsNetworkPath` | Warn about network drives | No | |
| `Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })` | `App/Services/ReportOpener.cs` | Open a report or the report folder at the user's click | No (launches the associated app) | Allow-list: this finished run's 3 CSVs, its workbook and its output folder, which must exist. Never automatic. No arguments, no verbs, no elevation |
| `Microsoft.Win32.OpenFolderDialog` | `App/Services/FolderPicker.cs` | Choose a folder | No | Returns a path only |

**Deliberately absent** (and the audit test forbids them): `File.WriteAll*`/`AppendAll*`/`Create`/`Copy`/`Replace`/`SetAttributes`/`Set*Time`/`SetAccessControl`; `Directory.Delete`/`Move`; any `System.Net`, sockets, HTTP or DNS; the registry; `Assembly.Load`, `Activator.CreateInstance`, `Type.GetType`, `Reflection.Emit`, `DynamicMethod`; `Environment.SetEnvironmentVariable`; services, task scheduler and startup entries; isolated storage and settings persistence; `runas`; package references.

Reflection is used only by the test runner (`tests/Shared/MiniTest.cs`) and the audit tests, never in `src/`. WPF itself uses reflection internally for XAML and data binding; that is framework code, not ours.

## Adversarial checks and results

| Concern | Result |
|---|---|
| **Path canonicalisation** | Device prefixes, reserved names (including superscripts), stream syntax, trailing dots and spaces (checked before *and* after normalisation) and forbidden characters are blocked. Relative paths are blocked unless there's an explicit base. |
| **Aliases** (SUBST, 8.3, case, junctions in the path) | Blocked before anything is created, using real locations from `GetFinalPathNameByHandle`. A runtime tripwire stops the scan if one of this run's own report names ever appears inside the scanned tree. |
| **Symlink/junction behaviour** | Never followed; a junction loop, a junction pointing outside the tree and real file reparse points are all tested. Symbolic links (file and directory) are tested in CI, where the runner can create them: both are blocked as sources, the directory link is skipped during a scan, the file link is counted at its own size, and parity with the PowerShell reference holds. |
| **Race: folder replaced by a junction after listing** | Re-checked right before entry, and skipped (deterministic test). The window is narrowed, not closed; traversal still terminates, because path length bounds depth. |
| **Race: report name taken during the run** | Create-new fails and the run reports `OutputError`; the other file is untouched (tests on CSV, `.xlsx` and `.partial`). |
| **Race: output folder replaced by a link** | Every creation re-checks the folder, with a test. During the scan the run's own open report handles also stop the folder from being renamed. |
| **CSV injection** | A leading `= + - @` tab CR LF gets an apostrophe, on every text column (tested per real name). |
| **XLSX formula/hyperlink injection** | Structurally impossible: the writer has no code path for `<f>` or hyperlinks. Tested by parsing the XML and reading back with an independent library. |
| **Cancellation** | Stops at safe points, closes every stream and lists incomplete files. Closing the window waits for this. |
| **Partial output** | Never presented as valid. `Cancelled`/`Failed` results carry no `Reports`; incomplete files are listed and never deleted silently. |
| **Output write failure** | Reported as `OutputError`, never as a scan error (tests: folder unwritable from the start and mid-run). |
| **Launching** | Only on a click, allow-listed to this run's own reports and folder. A `.csv` opens in whatever app the user has associated with `.csv`, which is the user's own choice. |
| **Content inspection, hashing, thumbnails, media metadata** | None. |

## Residual risks (accepted, documented)

1. **The folder-swap race** is narrowed but not eliminable without handle-relative directory enumeration (`NtQueryDirectoryFile` on a handle opened with `FILE_FLAG_OPEN_REPARSE_POINT`), which would add native code. The consequence is bounded: read-only traversal of the link target.
2. **`\\localhost\C$`-style aliases** aren't resolved by `GetFinalPathNameByHandle`. The runtime tripwire catches them, after the first report files have been created in the output folder.
3. **Hard links** are counted once per link (no file IDs in v1). This is a reporting matter, not a safety one.
4. **Single-file native-library extraction (release build):** the .NET host unpacks WPF's native DLLs to a per-user folder under `%TEMP%\.net\` on first run. This is .NET runtime behaviour, not application code, and never touches the scanned tree or the report folder. See `docs/release.md`.
5. **ShellExecute** of a report uses the user's file associations. We only ever pass our own report paths, never scanned paths.

## Evidence

- `SecurityAuditTests`: 9/9 PASS.
- The full suites were re-run against the **Release configuration** (the code that ships): Core unit tests **55/0/0**, integration **89/0/4**. The integration suite covers the adversarial Phase A fixture, PowerShell parity, the report pipeline, cancellation and write failures, UI and security audit. The 4 skips are symlink creation and 8.3 names (unavailable here) and the opt-in large-tree parity. No test folders were left behind.
- Phase A reference suite: 105/0/5 on both shells (unchanged).

## Merge-readiness review (v1.0.0)

An independent pre-merge review re-read the shipped code for source-drive writes, delete/rename/move paths, link
traversal, output containment, cancellation and close races, partial-file publication, and network, registry,
elevation, shell or persistence behaviour. It found no safety defect. It found and fixed one robustness defect
(medium):

- **Unreadable CSV report during the optional workbook step.** The workbook step counted the Files CSV rows outside its
  error handling. If that report could not be read (locked, removed or malformed), the exception escaped. The app then
  stayed on "Creating the optional Excel workbook" with no message.
- **Impact.** The CSV reports were complete and the scanned tree was unaffected.
- **Fix.** The failure is now reported as a failed workbook export, with a regression test that fails on the old
  code.

The release executable was also checked for embedded build paths and machine or account names (none), and was
smoke-tested from outside the repository.
