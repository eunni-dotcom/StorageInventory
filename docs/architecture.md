# StorageInventory architecture

## What StorageInventory is

StorageInventory is **reusable Windows filesystem infrastructure**. It answers questions about filesystem state: what
exists, where, how large, with what basic metadata, whether the observation was complete, and (in later versions) what
changed between observations and which volume they came from.

- **`StorageInventory.Core` is the primary product.** It is a UI-independent .NET library.
- **`StorageInventory.App` (WPF) is its first user-facing consumer.** It has no privileged access to anything.
- **Other consumers** are expected to use Core's contracts rather than reimplement filesystem discovery: a future CLI,
  and downstream tools such as media catalogues or other local data tools that need an accurate picture of what
  exists.

StorageInventory is deliberately **ignorant of file contents**. It never opens, reads, hashes, previews or classifies
files. Media understanding, duplicate judgement and organisation belong downstream (see
[integration-consumers.md](integration-consumers.md)).

```
StorageInventory:  "What files exist, and what changed?"
Consumers:         "What are those files, and what should be done with them?"
```

## Layers

```
+-------------------------------------------------------------------+
|  Consumers                                                        |
|  StorageInventory.App (WPF, v1) | CLI (future) | Other local tools |
+-------------------------------------------------------------------+
            | IStorageInventoryScanner, records, results
+-------------------------------------------------------------------+
|  (future, 1.1)  Persistent inventory / index                      |
|  snapshots, volume identity, history, change detection            |
+-------------------------------------------------------------------+
            | consumes scan observations, never re-scans itself
+-------------------------------------------------------------------+
|  StorageInventory.Core  (v1 priority)                             |
|  Paths/      safe path policy (validation, canonical locations)   |
|  Scanning/   metadata-only enumeration, reparse handling, errors, |
|              incomplete-subtree propagation, aggregation,         |
|              invariants, cancellation, progress, and the internal |
|              observer fan-out (v1.1 C1)                           |
|  Reports/    human/external-tool exports: CSV (authoritative),    |
|              XLSX (optional post-processing)                      |
|  Spool/      internal observation-spool codec (v1.1 C1; no file   |
|              is written until the capture gate, C5)               |
+-------------------------------------------------------------------+
```

### 1. Core: live filesystem understanding (current priority)

| Responsibility | Where |
|---|---|
| Safe path validation, including canonical-location alias checks | `Paths/PathPolicy` |
| Metadata-only enumeration, never following reparse points | `Scanning/` (Gate B3) |
| Identity within a scan: the root-relative path (`.` = root) | records |
| Error classification (`ScanErrorType`) | `Scanning/` |
| Completeness: per folder (`FolderScanStatus`, `SubtreeComplete`) and per scan (`ScanCompletionState`) | contracts |
| Recursive aggregation and self-checked invariants | `Scanning/` (Gate B4) |
| Reports | `Reports/` (Gate B5) |
| Cancellation (`CancellationToken`) and progress (`IProgress<StorageScanProgress>`) | contracts |

**Core rules**

- **No UI dependency.** No WPF, WinForms, dialogs, message boxes, UI-thread assumptions, view models, Explorer
  launching or GUI preferences. `Core_assembly_has_no_UI_framework_references` enforces this.
- **No process launching, no network code, no registry, no content reads.** The only native calls are the two
  read-only calls in `Paths/NativeMethods.cs`.
- **All output mutations go through one owned-file mechanism** (Gate B5): only create-new, only in the validated output
  folder, and every cleanup proven to belong to the current run.
- **User-facing text in Core is a default English rendering.** The stable contract is the codes and enums
  (`PathIssue.Code`, `ScanErrorType`, `ScanFailureKind`, `ScanCompletionState`), so consumers can present them however
  they like.

### 2. Persistent inventory / index (future, 1.1)

Saved snapshots, volume identity, scan history, change detection and historical queries. SQLite is a likely store, but
**it is not part of v1** (see [roadmap.md](roadmap.md)). Core is shaped so this layer can consume scan observations
without re-scanning and without parsing reports.

### 3. Consumers

Consumers depend on Core's public contracts. For .NET consumers such as a catalogue tool, **direct library use is the preferred
integration**. Structured interchange formats (NDJSON/JSON, or an SQLite snapshot file) are options for non-.NET or
out-of-process consumers later. The CSV reports are exports for humans and external tools, **not** an integration
contract.

## Reports vs snapshots

| | Report | Snapshot / index (future) |
|---|---|---|
| Purpose | For people and external tools | Persistent machine-readable filesystem state |
| Format | CSV (authoritative), optional XLSX | Undecided (likely SQLite) |
| Content | Display-oriented columns: local-time dates, KB/MB/GB, percentages, the Excel formula guard | Raw values: bytes, UTC timestamps, attributes, completeness |
| Stability | Column compatibility with the PowerShell reference | Versioned schema |

The two must not be coupled: **CSV columns are never the internal historical format.** For that reason the in-memory
records (`FileInventoryRecord`, `FolderInventoryRecord`) carry raw values (UTC timestamps, byte counts, attributes).
Local-time formatting, unit conversion, file-type categories and the formula guard all belong to the report layer.

## Extension points built in v1 (without implementing future features)

| Future need | v1 provision |
|---|---|
| Snapshot writer, consumer provider, NDJSON export | The traversal emits records to an **internal observer fan-out** (v1.1 C1, below); the CSV report writer is its critical observer. A public contract can be exposed later (1.2) without rewriting traversal. |
| "Unknown", not "deleted", under unreadable folders | Every folder carries `Status` + `SubtreeComplete`; every scan carries `ScanCompletionState` and the error rows. A consumer can always tell *not observed* from *observed absent*. |
| Change detection across scans | Stable root-relative paths, raw sizes and UTC timestamps in records. |
| Volume identity | Not captured in v1. A 1.1 snapshot will record it at scan time; drive letters are never treated as identity. |
| CLI | Core has no UI assumptions, and `IStorageInventoryScanner` is the entire entry point. |
| Multi-million-file scans | Files are streamed, never held as a collection. Memory is per-folder records plus a compact sort index. |

## Scan observers (internal, v1.1 gate C1)

One traversal feeds every consumer of a scan. `ScanEngine` lists each folder once and passes file and error records
to an `ObserverFanOut`, which repeats every call to each attached observer. The contract is `IScanObserver`
(`Scanning/IScanObserver.cs`), which replaced v1's two-method `IScanSink`:

| Call | When |
|---|---|
| `OnScanStarted` | Once, first, as soon as the run is named (not for a scan refused at validation) |
| `OnFile`, `OnError` | During enumeration only, exactly v1's records in v1's order; one contiguous run of files per folder |
| `OnFolderFinalised` | Once per folder, ascending index, after aggregation and its self-check and before the Folders report: totals, `Status`, `StatusReason` and `SubtreeComplete` are final. There is no discovery callback, so no observer sees a folder while its state is still provisional |
| `OnScanEnded` | Once, last, on every path after `OnScanStarted`: Finished (with the result's totals, only after v1's reports are complete), Cancelled or Failed |

Observers are **critical** or **isolated**. The only critical observer is the CSV report sink, wrapped by `OutputGuard`:
its I/O errors abort the scan as `OutputError`, exactly as in v1. Isolated observers (the spool writer, test observers)
are disconnected after an ordinary exception and the scan continues; cancellation with the run's token, and
`OutOfMemoryException` or `InsufficientExecutionStackException`, are never isolated. Delivery is synchronous on the
scanning thread (no queue or writer thread), so cancellation latency is v1's.

All of this is **internal** (`InternalsVisibleTo` the test assemblies only). The public `IStorageInventoryScanner`,
`InventoryScanner.Scan`/`ScanAsync`, options, results and records are unchanged; the public entry points attach no
observer besides the reports. An internal `ScanObserved` entry point attaches isolated observers, for tests and for
the capture pipeline of a later gate.

**The observation spool codec** (`Spool/`) is the first isolated observer: `SpoolWriter` appends a private, versioned
binary record of the scan (format 1: header, file and error records in emission order, the finalised folders, a run
index, a trailer and a SHA-256 over every preceding byte), and `SpoolReader.Verify` is the verification pass that must
succeed before any record is read back. It works over a `Stream` it is given and holds no per-file state. In v1.1 C1 no
spool file exists: the file, its exclusive delete-on-close handle in the report folder and its space policy belong to
the capture gate (C5). The spool is never a snapshot, an export or an interchange format.

## The App shell (v1.1 gate C2)

`StorageInventory.App` is a shell window with a navigation list and the page it shows. In C2 the only page is
**Scan**, which is v1's flow unchanged: `Views/ScanPage` holds v1's page (header and scroller) and shows one of three
stage views, `SetupView`, `ScanProgressView` and `ResultsView`, whose markup is v1's. They bind to the Scan page's view
model (`MainViewModel`) and, through it, to v1's `PreflightViewModel`, `ScanProgressViewModel` and `ResultsViewModel`.
A page is shown through its view (an implicit `DataTemplate` in the shell); `ShellViewModel` lists the pages.

Long-lived state belongs to services created once per app session, not to pages or views (UI-12). `App.OnStartup`
creates one `ScanSession` before the shell. It owns the scanner, the running scan, its cancellation, its progress and
its result, and runs at most one scan at a time; a view can be replaced, or a window that hosts the page closed,
without stopping or restarting the scan. `MainViewModel` keeps v1's names (`Stage`, `Progress`, `Results`, `IsBusy`
and the commands) by delegating to the session through a weak subscription, so the session never keeps a page alive.
Closing the shell during a scan asks, cancels through the session and waits until Core has closed every report file,
as v1 did. Later gates add pages (C8) and the Library services (C4, C5) beside the session without moving the scan.

## File identity (future design topic)

In v1 a file is identified within a scan by its **root-relative path**. That is enough for reports, but not for
detecting moves or renames between scans. The topics to research for 1.1 are:

- **Volume identity:** a stable volume serial or ID, not the drive letter (`D:` may become `E:`).
- **NTFS file IDs** (FRN / 128-bit file ID): stable across renames within a volume, and readable from metadata. They
  can change after defragmentation of some file systems or on copy.
- Path + size + timestamps as a heuristic when IDs are unavailable (FAT/exFAT, network shares).

Constraints that remain: **no content reads and no hashing** in StorageInventory, and nothing invasive (no USN journal
or file watchers) unless snapshot performance proves it necessary.

## Safety contract (applies to every layer)

- Scanned files are never modified, and their contents are never opened or read. Only metadata is enumerated.
- Reparse points are never followed during traversal (`ReparsePointPolicy.NeverFollow` is the only policy).
- No administrator rights, registry or system configuration changes, telemetry, or automatic network activity.
- Existing files are never overwritten. Output lives only in a validated folder outside the scanned tree.
- A cancelled, failed or partially-readable observation is never presented as complete.

## Behavioural reference

`powershell/StorageInventory.ps1` (Phase A, commit `2715dfa`) is the behavioural oracle. Native behaviour is proven
against it by parity tests on shared fixtures (Gate B6), and intentional differences are documented.
