# Library engine benchmark (v1.1 C4, TEST-P1, Q-07, Q-17)

Raw per-run tables: [library-raw.md](library-raw.md) (written by `StorageInventory.Library.Tests.exe --benchmark all`; the console
log of the same run is the source of the phase timings quoted here).

## Method

- Release build, win-x64, the real schema 1 Library with `synchronous=FULL`, rollback journal (TRUNCATE), `foreign_keys=ON`.
- Before every measured import the Library already holds three snapshots of about 250,000 files in two sources (93.6 MB), so the
  `name` and `folder_path` UNIQUE indexes modify existing pages, which are journalled (the case PERF-15 asks about).
- The measured span is `BEGIN IMMEDIATE` to `COMMIT` of one `ImportSnapshotAsync`, including the in-transaction verification of
  invariants 1 to 12. Rows come from a synthetic in-memory source (key order, 4 files per folder, shared names).
- Runs: 3 x 1M, 3 x 2M (each followed by a snapshot deletion), 1 x 10M, 3 paired rounds of 2M with and without declared observation
  foreign keys (Q-07), and one 2M import against a database capped at 80 MiB (SQLITE_FULL, Q-17 at engine level).
- Measured on a development workstation that was **not quiet**: other work ran on it during parts of the run, so the spread
  between runs is real and is reported, not averaged away.

## Results

| Scale | Runs (file rows/s) | Median | Phases (ms), best run |
|---|---|---:|---|
| 1M | 127,333 / 127,570 / 106,971 | 127,333 | folders 1306, files 4074, verification 1982, commit 482 |
| 2M | 109,768 / 114,300 / 78,153 | 109,768 | folders 3023, files 9365, verification 5096, commit 707 |
| 10M | 80,476 (one run) | 80,476 | folders 21,207, files 68,924, verification 32,965, commit 1103 |

- **PERF-01 (>= 100,000 file rows/s):** met on the median at 1M and 2M. Two of six runs at those scales, and the single 10M run,
  were below 100k (78k to 107k); the 10M run had a 3.8 s GC pause total and a heavier machine load. An earlier run of the same code
  on a quiet machine gave 130k rows/s at 2M and 127k rows/s at 10M (78.9 s), so the slow runs are attributed to load, but that
  attribution is an inference, not a measurement. The sustained, quiet-machine rate is not proven for every run: **PERF-01 is met on
  the median, with run-to-run variance down to 78k rows/s**. The section 15.4 stop condition (below 50,000) did not fire in any run.
- **PERF-14 engine share (saving phase at 2M <= 60 s):** 17.5 s to 25.6 s for the whole import including verification inside the
  transaction. Well inside 60 s; the spool-read share belongs to C5.
- **PERF-15 (journal <= 5% of snapshot size):** peak journal 0.19 MB at every scale (0.14% at 1M, 0.014% at 10M). Met.
- Database growth: about 140 to 145 bytes per file row. Peak working set above baseline: 121 MB (1M), 138 MB (2M), 319 MB (10M).
- **Deletion (T-DELETE) of a 2M snapshot:** 2.4 to 2.6 s, peak journal about 128 MB (deleting journals the pages it frees; this is
  expected, not covered by PERF-15). Separate read-only verification of a committed 2M snapshot: 7.7 s to 9.7 s.
- **Q-17 (engine level):** a 2M import against a database capped at 80 MiB failed with `LibraryFull` (code 201) after 3.56 s,
  rolled back completely, size unchanged (37.8 MB), the three earlier snapshots intact. A real full volume was **not run** (needs
  administrator rights to create and fill a VHD; only possible on a hosted runner).

## Q-07: cost of declaring observation foreign keys

Three paired rounds at 2M files, same Library, same machine, run alternately:

| Round | Without FKs | With FKs | Overhead (time) |
|---|---:|---:|---:|
| 1 | 20.85 s | 28.35 s | +36% |
| 2 | 19.87 s | 24.62 s | +24% |
| 3 | 15.66 s | 24.17 s | +54% |

Median overhead +36% (range +24% to +54%), far above the 10% threshold the spec sets. **Decision: observation foreign keys are not
declared.** Integrity of observation rows is carried by the in-transaction verification (invariants 1 to 12), which runs for every
import. Caveat: the noise on this machine is of the same order as the smaller overheads, but the direction was the same in all
three rounds and the smallest overhead is still more than twice the threshold.

## Not measured / caveats

- No end-to-end (capture to Library) figure: that is C5 (PERF-14 in full, PERF-02 to PERF-03).
- One 10M run only; the 10M figure is therefore a single sample under load.
- Medians of three are weak statistics; the individual runs are in the raw file.
