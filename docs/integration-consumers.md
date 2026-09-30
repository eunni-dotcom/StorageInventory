# Consumer integration boundary

This document defines a **boundary**, not an implementation. No consumer code exists in StorageInventory, and
StorageInventory will not be merged into any consumer. Integration is planned for 1.2 (see [roadmap.md](roadmap.md)).

## Division of responsibility

| StorageInventory | Consumer |
|---|---|
| What files exist, where, how large, and their basic filesystem metadata | What the files *are*: media analysis, recognition, enrichment |
| Whether each part of the tree was actually observed | Catalogue of analysed media |
| What changed between observations (1.1) | Deciding what to analyse, re-analyse or retire |
| Volume identity and scan history (1.1) | Content-level duplicate judgement, previews, organisation |
| **Never:** reads contents, hashes, recognises, moves or deletes anything | **Never:** re-implements filesystem discovery |

StorageInventory must not gain content analysis to make a consumer's job easier. If a consumer needs content facts, it
computes them.

## Conceptual flow

```
StorageInventory snapshot / change-set
               |
               v
Consumer catalogue reconciliation
               |
               v
new / changed media candidates
               |
               v
Consumer analysis pipeline
```

## Candidate feed

StorageInventory would offer consumers candidates in these classes (derived from the 1.1 change classes):

| Candidate | Source |
|---|---|
| New file | Added since the reference snapshot |
| Changed file | Size or timestamps differ from the reference snapshot |
| Missing file | Not observed, and its folder was **fully readable** in the newer scan |
| Unknown / unobservable | Not observed because the folder, an ancestor or the scan was incomplete |

## Safety semantics a consumer must be able to rely on

A consumer must be able to distinguish, at minimum:

| State | How it is determined |
|---|---|
| On disk and in the catalogue | Observed in the latest scan and present in the catalogue |
| On disk, not in the catalogue | Observed, not in the catalogue: a new candidate |
| In the catalogue, not observed in the latest **complete** scan | Missing: the parent subtree was fully readable, and the item is absent |
| In the catalogue, under an incomplete or unreadable subtree | **Unknown.** It must not be treated as deleted |
| Changed since last analysed | Observed, but its metadata differs from what the consumer recorded at analysis time |

These **must not collapse into an exists/missing boolean**. "Not observed" does not mean "deleted".

### What v1 already preserves to make this possible

- `ScanCompletionState` per scan: `Complete` / `Incomplete` / `Cancelled` / `Failed`. Only `Complete` and
  `Incomplete` observations are usable at all, and only `Complete` observations can prove absence everywhere.
- `FolderInventoryRecord.Status` (what happened to the folder itself) and `SubtreeComplete` (whether anything beneath
  it failed), for every folder, including skipped reparse points.
- `ScanErrorRecord` rows with typed `ScanErrorType`s for each unreadable location.
- Root-relative paths as in-scan identity, plus raw sizes and UTC timestamps.

A file whose `RelativeDirectory` belongs to a folder with `SubtreeComplete = false` (or whose folder is missing from the
scan because an ancestor was unreadable) is **Unknown** for change-detection purposes.

## Interface options (decided in 1.2)

1. **Direct .NET library use (preferred).** The consumer references StorageInventory.Core (or a later snapshot library) and
   consumes typed records or change-sets. No parsing, and the full completeness information is available.
2. **Structured files** for out-of-process use: NDJSON or JSON change-sets, or an SQLite snapshot file.
3. **Not the CSV reports.** They are display exports (local-time dates, rounded units, formula guard) and are not a
   stable machine contract.

## Open questions for 1.1/1.2

- File identity across renames and moves: NTFS file IDs vs path + metadata heuristics (see
  [architecture.md](architecture.md), "File identity").
- The reference point for "changed since last analysed": the consumer's own record of the metadata at analysis time, or a
  StorageInventory snapshot ID.
- How a consumer learns that a volume is offline (a drive not connected) rather than emptied. This must be Unknown, never
  Missing.
