# Native security review (Gate B10)

**STATUS: PASS** (see "Evidence").

> **The v1.1 gate C3 text of this document is an implementer record awaiting independent re-review.** The first independent
> review (`docs/v1.1-c3-review.md`, verdict `C3 REQUIRES REVISION`) found it incomplete or over-general in the places that
> `docs/v1.1-c3-repair-evidence.md` lists (C3-M03, C3-M04, C3-M05) and they are corrected below. The `PASS` above is the v1
> review's; it is not an acceptance of C3.

This review was done separately from implementation, reading the shipped code (`src/`) as an adversary would and asking
*"if this were malicious or buggy, where could it hurt the user's files or privacy?"* The claims below are enforced by
`tests/StorageInventory.IntegrationTests/SecurityAuditTests.cs`, which fails if the code drifts: it checks the source
text, the compiled assemblies' references, the P/Invoke methods found by reflection, and the manifest.

> **v1.1 update (gate C3).** The first-party native surface grew from two to **six read-only `kernel32` calls**: four
> identity queries were added for volume and source identity (RX-04, §7.8 of the v1.1 specification). The inventory below
> lists all six, and [the C3 section](#v11-gate-c3-volume-and-source-identity-six-native-calls) records how they are used,
> what holding a handle does, and what was measured. Nothing is persisted yet (no Library, no database), and the C3 identity
> code is internal.

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
| Identity capture (v1.1 C3) never reads content or changes anything | Holds | The four new calls are read-only queries through a directory handle opened with **desired access 0** (its granted access was measured as `0x00100080`, `SYNCHRONIZE` plus `FILE_READ_ATTRIBUTES`, against `0x00120089` for `GENERIC_READ`), or by path. The handle is opened shared read, write and delete (as the specification states it), and holding it does not stop the source folder itself from being renamed, deleted or listed (measured). It is **not** free of effects: while it is held, Windows refuses to rename or remove a **parent or ancestor** of the source folder, refuses the orderly volume lock that "Safely remove" begins with, and asks for confirmation before a mapped drive is disconnected, exactly as it does for any open folder listing (Q-19, below). A test shows a tree's names, sizes, attributes and timestamps identical before and after capture. |

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
| `CreateFileW` (desired access **0**) + `GetFinalPathNameByHandleW` | `Core/Paths/NativeMethods.cs` | The real location of the source/output folders, to see through aliases; since C3 also the identity handle (below) | No | Two of the product's **six** P/Invokes (checked by reflection); a zero-access handle can't read, write or delete. `CreateFileW` has one call site, and it passes the literal 0 |
| `GetVolumeInformationByHandleW` | `NativeMethods`; called by `Core/Identity/WindowsEvidenceSource` | Filesystem name, 32-bit volume serial, label and flags of the volume holding the opened directory (C3) | No | A query on the zero-access handle; a failure is recorded as evidence with its Win32 error, never thrown and never replaced by a guess |
| `GetFileInformationByHandleEx`, information class **`FileIdInfo` (18) only** | same | The 64-bit volume serial and the 128-bit file ID of the opened directory (C3) | No | The class parameter's type is an enum with that single member; the audit forbids a cast to it and any other class. Fails with Win32 87 on FAT32, exFAT and UDF, which is a support limit, not an access limit (measured) |
| `GetVolumePathNameW` | same | The mount point of the volume holding the canonical path, for display (C3) | No | A path query (there is no handle form). It never decides identity |
| `GetDiskFreeSpaceExW` | same | Capacity and free bytes of that volume, for display and for Moderate corroboration (C3) | No | A path query. Mutable values: never identity |
| `DriveInfo.DriveType` | `PathPolicy.IsNetworkPath` | Warn about network drives | No | |
| `Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })` | `App/Services/ReportOpener.cs` | Open a report or the report folder at the user's click | No (launches the associated app) | Allow-list: this finished run's 3 CSVs, its workbook and its output folder, which must exist. Never automatic. No arguments, no verbs, no elevation |
| `Microsoft.Win32.OpenFolderDialog` | `App/Services/FolderPicker.cs` | Choose a folder | No | Returns a path only |

**Deliberately absent** (and the audit test forbids them): `File.WriteAll*`/`AppendAll*`/`Create`/`Copy`/`Replace`/`SetAttributes`/`Set*Time`/`SetAccessControl`; `Directory.Delete`/`Move`; any `System.Net`, sockets, HTTP or DNS; the registry; `Assembly.Load`, `Activator.CreateInstance`, `Type.GetType`, `Reflection.Emit`, `DynamicMethod`; `Environment.SetEnvironmentVariable`; services, task scheduler and startup entries; isolated storage and settings persistence; `runas`; package references other than the four SQLite packages of `src/StorageInventory.Library` (audit rule A-11).

Reflection is used only by test code (the test runner `tests/Shared/MiniTest.cs`, the audit tests, and the v1.1 C2 shell tests, which count an event's handlers), never in `src/`. WPF itself uses reflection internally for XAML and data binding; that is framework code, not ours.

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

## v1.1 gate C3: volume and source identity (six native calls)

**Added.** Four read-only `kernel32` queries (`GetVolumeInformationByHandleW`, `GetFileInformationByHandleEx` with the
information class `FileIdInfo` only, `GetVolumePathNameW`, `GetDiskFreeSpaceExW`), so a source can later be recognised by
what it is rather than by its drive letter. **Not added:** any write-capable function, any other DLL, COM, WMI, registry
probing, a PowerShell or process fallback, any network-name resolution, or manual native binding (`NativeLibrary`,
`GetProcAddress`, function pointers). The first-party surface after C3 is exactly the six calls of the inventory above, in
one file, and the audit (`The_only_native_calls_are_six_read_only_kernel32_functions` and its negative self-test) fails if
a seventh appears, if one is not in `kernel32`, if `CreateFileW` is called with any desired access but the literal 0, or if
`GetFileInformationByHandleEx` is asked for anything but `FileIdInfo`. The last is enforced by type (a one-member enum)
and by a text rule that allows the enum's name in exactly three kinds of place (its declaration, the P/Invoke's parameter,
and the `.FileIdInfo` argument), so a cast, `default(...)`, `Enum.ToObject(typeof(...))` and `Unsafe.As` are all rejected;
the rule is shown to reject each of them. That rule keys on the type's name, so it cannot see a call that never names the
type: the independent C3 review showed that a reflective call (`MethodInfo.Invoke` with a class value taken from the
parameter's own type) passed it. Reflection is therefore closed separately, as A-18 closes dynamic code: shipped code may
not use it at all, enforced by a text rule over all of `src` and by a rule over the member references of the compiled Core and
History assemblies (the App is left to the text rule: its generated WPF code legitimately uses `Delegate.CreateDelegate`),
each with a negative self-test (`Shipped_code_makes_no_reflective_call_so_the_FileIdInfo_only_rule_cannot_be_bypassed` and
`The_reflection_rules_reject_the_bypass_the_review_demonstrated_and_its_variants`; `tests/mutation/Invoke-C3Mutants.ps1`
re-runs the review's mutant and two variants). What is closed is those shapes: a deliberate change to the audit itself, or to
a project file outside `src`, is for a human reviewer. (Every information class is a read-only query: the point is that the
reviewed surface is exactly one.)

### The zero-access root handle

- **Which path.** The handle is opened on **the path the scan enumerates** (v1's validated, normalised path as entered),
  converted only to the extended `\\?\` form, which adds no normalisation. No handle is ever opened on the canonical path
  read back from it (`CreateFileW` is only given the enumerated path): a SUBST or mapped letter that is re-pointed leaves
  the old target's canonical path unchanged, so opening it would hide exactly the change the end-of-scan check exists to
  find. Two display queries do take a path they are handed (`GetVolumePathNameW` the canonical path, `GetDiskFreeSpaceExW`
  the mount point): there is no handle form of either, they only read, and what they return (mount point, capacity, free
  space) is recorded and never compared (ID-13), so it cannot change a verdict.
- **Which rights.** `CreateFileW` with desired access **0**, sharing read, write and delete, `FILE_FLAG_BACKUP_SEMANTICS`
  (needed to open a directory) and `OPEN_EXISTING`. The granted access mask of such a handle was measured on Windows 11 as
  `0x00100080` (`SYNCHRONIZE` and `FILE_READ_ATTRIBUTES`, which `CreateFileW` itself adds); `GENERIC_READ` gives
  `0x00120089`. So the handle cannot list a directory, read data, write, or delete anything. No content is ever accessed.
- **Failures are data.** Every query returns its Win32 error instead of throwing. A failed or unsupported call becomes an
  evidence item that says so (*unavailable*, *call failed* or *not provided by the source*); nothing is substituted. If the
  canonical path (every source), or the filesystem name and 32-bit serial (a local volume), cannot be obtained at
  preflight, the source cannot be re-verified and is **not saveable** (reports only): the code never falls back to trusting
  the path.
- **Zero access is enough everywhere that was tried.** On NTFS, ReFS, FAT32 and exFAT (virtual disks), UDF (a mounted image
  and a vendor's virtual CD drive; no physical disc), SMB shares served by Windows **on the same machine (loopback)** from
  NTFS and from FAT32, and mapped and SUBST letters, the open, the canonical path and the volume query worked with access 0.
  `FileIdInfo` answers Win32 87 on FAT32, exFAT and UDF with access 0, with `FILE_READ_ATTRIBUTES` and with `GENERIC_READ`
  alike, so it is a filesystem that does not implement the query, not a missing right. That is why the 64-bit serial and the
  root directory ID are required later only when the call succeeded at preflight.

### Handle lifetime and ownership

`RootIdentityHold` is the only owner. A capture opens the enumerated path and reads E1 when the observation window opens,
**holds** the handle for the window, reads E2 through the same handle and E3 through a **fresh** open of the enumerated path
when the scan has returned, and closes the held handle in the same step. A cancelled or failed scan never reaches that step
and disposes the hold instead, which is idempotent. The handle is a `SafeFileHandle` (so a missed disposal is still
released by the runtime, and a closed handle can never be used by accident); the wrapper adds no finalizer. Two kinds of
test cover the release, and they prove different things. The unit tests over a fake provider prove that the hold *asks* for
every handle to be closed, on every ending (a read that throws at E1, E2 or E3, a path that cannot be opened, a scan that is
abandoned, a double dispose). `The_real_root_handle_is_closed_however_the_window_ends` proves that the *real* handle is
released: the handle has no rights and shares everything, so nothing done to the folder can show it, and the test counts
the process's own handles instead, with a control (windows that are held do raise the count, so the measurement can fail),
then 160 windows ending four different ways, with the collector held off so a leak cannot be quietly finalised. C3 does
not call the hold from a scan: v1's scan, its reports and its observers are byte-for-byte unchanged, and a test shows a
scan's three reports identical with the handle held.

### What holding the handle does (Q-19: measured on Windows 11 and on a hosted Windows Server 2025 runner)

Scope of the evidence: the virtual disks are VHDX files and a mounted UDF image; every SMB and mapped-drive row was measured
against a **Windows SMB server on the same machine (loopback)**, not against another machine. A remote Windows server, a
Samba server, a NAS and DFS were not measured.

| Action while the handle is held | Observed |
|---|---|
| Orderly dismount: the volume lock that "Safely remove" and Eject start with (virtual NTFS volume) | **Refused** (Win32 5); it succeeded before the hold and succeeds again after it ends |
| The same lock while an ordinary folder listing is open and no identity handle is held (the control: what a scan already has) | **Refused** too (Win32 5). Listing a folder already blocks an orderly dismount, so the held handle adds no new *kind* of effect. (By construction, not measured: a scan's own listing handles are open only while it enumerates, whereas the held handle spans the whole observation window, so the refusal lasts the window) |
| Rename or delete the **source folder itself** from outside | Allowed (measured). E2, read through the handle, then reports the new canonical name, or a `$Deleted` name; E3, a fresh open of the old path, cannot open it |
| Rename a **parent or ancestor** of the source folder (handle held on `A\B\C`: rename `A\B`, or `A`) | **Blocked: Access denied.** An ordinary open folder listing on the same folder gives the identical result (the control), so this is an effect a scan already has while it enumerates, not one the identity handle introduces; renaming the source itself is allowed in both cases. Reviewer experiment (`docs/v1.1-c3-review.md`, B.3), now also part of `Invoke-IdentityExperiments.ps1` with its control (`docs/v1.1-c3-repair-evidence.md`) |
| List or scan the same tree meanwhile | Unchanged: the three reports are byte-identical to a scan without the handle |
| Mapped network drive: a normal disconnect | Asks for confirmation ("open files and/or incomplete directory searches pending"), as it does for any open handle, including an ordinary open folder listing; declined, the drive stays and the capture still verifies |
| Mapped network drive: a forced disconnect and re-map | The held handle is invalidated (Win32 59); E2 fails and E3 reads the other share |
| Virtual disk detached (what pulling a stick looks like to the volume), or a disc image dismounted with `Dismount-DiskImage` | Not blocked; the held handle fails (Win32 21, not ready) and a fresh open fails (Win32 3), so the capture is not eligible. `Dismount-DiskImage` takes no volume lock, so it is **not** what Eject does on a physical disc |
| Another medium at the same letter | E2 fails; E3 reads the other volume's serials |
| SUBST letter re-pointed | The held handle still reaches the original folder; E3 reaches the new one and detects it. Re-pointed away and back is **not** detected (L-ID2) |
| Source folder renamed away, another folder given its name | If left like that: E2 reports the original's new name (every filesystem) and E3 reaches a different directory (its root file ID on NTFS and ReFS): detected. If everything is put back before the window closes: **not** detected (L-ID2, G0F-O05) |
| A share's backing folder renamed on the **server's own filesystem** (loopback: the same machine, not another client) | E2 reports the new name; E3 cannot open the old one |

Holding the root handle blocks the orderly volume lock that "Safely remove" begins with, makes Windows ask before a mapped
drive is disconnected without force, and makes Windows refuse to rename or remove a parent or ancestor folder of the source,
for as long as it is held. Each of these is produced identically by an ordinary open folder listing (the controls), which a
scan has throughout its enumeration, so the held handle adds **duration** (the observation window outlasts the enumeration)
and no new kind of effect. The independent C3 review (`docs/v1.1-c3-review.md` §15) judged on that basis that the stop
condition of §19 C3 is not triggered; this document does not pre-judge that point. The Windows "Safely Remove Hardware"
dialog itself was not observed. **Not measured here:** that dialog on a physical USB drive, a physical medium swap, a physical
optical disc, a Samba server, a remote Windows server, a NAS, DFS, and interaction with third-party antivirus or indexers
beyond the hosted images. Those are manual steps (`tests/identity/README.md`) and remain open acceptance evidence. A note for the
History screens (gate C8): removing, disconnecting or renaming a parent of a source while a scan runs is refused by Windows.

### Limitations stated, not hidden

- **L-ID1:** a disk clone copies the volume serial, and nothing read distinguishes a clone from its original unless both
  are mounted together (then the user is asked). Asserted as expected behaviour by `MatchingTests`.
- **L-ID2:** a change that is **undone before the scan ends** cannot be detected from start and end evidence. Two cases:
  a letter (drive letter, SUBST or mapped) re-pointed away and back while the original volume stays mounted; and, the same
  limitation applied to the source folder itself (G0F-O05), the source folder renamed away, another folder given its name,
  and both put back. In both the scan may have listed a different namespace in between. A replacement that is left in
  place is detected (E2 on every filesystem, E3 too where the filesystem provides a root file ID). Both cases are asserted
  as documented behaviour, with no polling added to hide them.
- A Windows SMB server returns the **underlying volume's** filesystem name and its serials: both the 32-bit and the 64-bit
  serial for a share served from NTFS, and **only the 32-bit serial** for a share served from FAT32 (`FileIdInfo` answers Win32
  87 there, as on FAT32 itself). Measured against a loopback server; a remote server was not measured. A share can therefore
  look Strong on evidence alone. It is never Strong: confidence also requires a
  local source, so a network share is recognised by its canonical location only, and different spellings of a server
  (`\\nas`, `\\nas.local`, an address) are different sources because no name is ever resolved.

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
