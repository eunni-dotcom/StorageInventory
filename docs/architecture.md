# StorageInventory architecture

## What StorageInventory is

StorageInventory is **reusable Windows filesystem infrastructure**. It answers questions about filesystem state: what
exists, where, how large, with what basic metadata, whether the observation was complete, and (in later versions) what
changed between observations and which volume they came from.

- **`StorageInventory.Core` is the primary product.** It is a UI-independent .NET library.
- **`StorageInventory.App` (WPF) is its first user-facing consumer.** It has no privileged access to anything.
- **Other consumers** are expected to use Core's contracts rather than reimplement filesystem discovery: a future CLI,
  downstream catalogue tools (the primary integration target) and other local data tools.

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
|              invariants, cancellation, progress                   |
|  Reports/    human/external-tool exports: CSV (authoritative),    |
|              XLSX (optional post-processing)                      |
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
| Snapshot writer, consumer provider, NDJSON export | The traversal emits records to an **internal sink**; the CSV report writer is one sink. A public sink contract can be exposed later without rewriting traversal. |
| "Unknown", not "deleted", under unreadable folders | Every folder carries `Status` + `SubtreeComplete`; every scan carries `ScanCompletionState` and the error rows. A consumer can always tell *not observed* from *observed absent*. |
| Change detection across scans | Stable root-relative paths, raw sizes and UTC timestamps in records. |
| Volume identity | Not captured in v1. A 1.1 snapshot will record it at scan time; drive letters are never treated as identity. |
| CLI | Core has no UI assumptions, and `IStorageInventoryScanner` is the entire entry point. |
| Multi-million-file scans | Files are streamed, never held as a collection. Memory is per-folder records plus a compact sort index. |

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

`powershell/StorageInventory.ps1` (Phase A, commit `0eeb636`) is the behavioural oracle. Native behaviour is proven
against it by parity tests on shared fixtures (Gate B6), and intentional differences are documented.
