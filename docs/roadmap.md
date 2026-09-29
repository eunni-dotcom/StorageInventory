# StorageInventory roadmap

Version numbers describe **conceptual stages**. The sequencing may change if the architecture suggests it, but a later
stage never starts at the expense of an earlier stage's quality gates.

## 1.0: native scanner and application (current)

The audited native replacement for the PowerShell reference.

| Gate | Scope | Status |
|---|---|---|
| Phase A | Hardened PowerShell reference and regression suite | Done (`0eeb636`) |
| B0 | Repository reconciliation, Phase A report, build bootstrap | Done |
| B1 | Core contracts and models | Done |
| B2 | Safe paths and filesystem policy | Done |
| B3 | Metadata enumeration engine | Done |
| B4 | Aggregation and correctness invariants | Done |
| B5 | CSV report pipeline (streaming, no-clobber, sorted or unsorted) | Done |
| B6 | PowerShell ↔ C# behavioural parity | Done |
| B7 | WPF shell and pre-flight UX | Done |
| B8 | Live progress and cancellation | Done |
| B9 | Results experience (overview, largest folders and files, file types, errors) | Done |
| B10 | Adversarial and security review | Done |
| B11 | Self-contained single-file win-x64 release build | Done |

Also in 1.0 (done): optional XLSX export as post-processing of the completed CSV reports, with no Excel installation and
no COM.

**Not in 1.0:** SQLite, persistent snapshots, diffing, history UI, downstream consumer code, USN journal, file
watchers, hashing, content inspection.

## 1.1: persistent inventory

- **Snapshots:** preserve a completed scan as a machine-readable snapshot (raw values, not report columns).
- **Volume identity:** identify volumes by stable volume metadata, never by drive letter. `D:` → `E:` is the same
  volume.
- **Scan history:** when each volume was observed, and how complete each observation was.
- **Persistent index:** SQLite or equivalent, introduced only once snapshots need it.
- **Change detection**, classifying each item as one of:

  | Class | Meaning |
  |---|---|
  | Added | Observed now, not in the earlier snapshot |
  | Changed | Observed in both, and size or timestamps differ |
  | Missing | In the earlier snapshot, not observed now, **and its parent folder was fully readable now** |
  | Unchanged | Observed in both with the same metadata |
  | Unknown | Could not be observed now (an ancestor was unreadable, or the scan was incomplete there) |

  **Unknown is not Missing.** Absence under an unreadable or incomplete subtree must never be reported as deletion.
- **Historical analytics:** bytes added since a snapshot, folder growth over time, file-count growth, fast-growing
  locations, last-seen dates, completeness history.
- **Multi-volume overview:** several drives or archives shown as separately identified volumes.

## 1.2: integration

- A stable, versioned integration contract (see [integration-consumers.md](integration-consumers.md)).
- A consumer provider: a new/changed candidate feed and catalogue reconciliation support.
- Missing vs Unknown semantics carried end to end.
- Structured interchange for out-of-process consumers (NDJSON/JSON or an SQLite snapshot file), where direct .NET use
  isn't practical.

## Later

- A CLI consumer, for example:

  ```
  storageinventory scan D:\
  storageinventory scan D:\Media --output C:\Reports
  storageinventory snapshots
  storageinventory diff --from previous --to latest
  storageinventory status
  ```

- Other filesystem-aware tools as consumers.
- Incremental scanning (USN journal or change notifications), **only** if measured snapshot performance proves full
  rescans inadequate.

## Permanently out of scope for StorageInventory

Image recognition, EXIF or media interpretation, perceptual hashing, duplicate judgement, content classification,
thumbnails or previews, content organisation, moving or deleting files, cleanup
automation, accounts, cloud sync, telemetry, update services. These are downstream concerns (for example a catalogue tool) or not
wanted at all.
