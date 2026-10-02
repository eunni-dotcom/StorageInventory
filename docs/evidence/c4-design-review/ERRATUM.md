# Erratum to the C4 design review's evidence

The C4 design review (`docs/v1.1-c4-design-review.md`, evidence commit `1206a1d`, document `7d85d78`) took decision D-52. Its
independent re-review (`docs/v1.1-c4-design-rereview.md`, `1824bff`) confirmed D-52 and found defects in the evidence behind
some of its secondary figures (C4DR-M06 and observations C4DR-O01 to O06, O08, O12). This erratum states each defect, what it
affects and what replaces it. The design review's document is left as written, with a pointer to this file; the specification
was corrected by the C4 design repair (`docs/v1.1-c4-design-repair.md`). Every item below can be recomputed from the committed
files with the script named beside it. Units: MiB = 2^20 bytes, MB = 10^6 bytes.

**What none of these defects touches:** the reproduction of C4-B01 (journals reproduced to the byte by the re-review and again by
the repair), the mechanism (one journal page per existing dictionary leaf that receives a new name), the journal figures of the
per-source dictionary (the repair's clean reruns: 247,328 B for a new source and 62,927,656 B for an existing one at 1M into 3 × 1M,
identical to the re-review's clean reruns), the rejection of staging and of key-ordered interning, and D-52 itself.

## E1. Prefill-cache contamination of the per-source cells (C4DR-M06)

- **Defect.** The harness cached each prefill under `<family>-<size>[-persource]` and built it in a child that inherited the
  triggering run's whole environment. The first per-source prefills of the hash, mixed and append families were built by
  `probe:combined` runs, that is with `SI_DR_PRESORT=chunk` (key-ordered interning), and every later per-source run on those
  sizes reused them. Such a prefill holds the same names and observations as a clean one but different `name_id` assignment and a
  different page count (the re-review: 101,792 against 102,237 pages for `hash-1000000-persource`).
- **Which prefills** (`python prefill_provenance.py results`): `hash-1000000-persource`, `hash-2000000-persource`,
  `mixed-1000000-persource` and `append-1000000-persource` were first built under `SI_DR_PRESORT=chunk`; `real-1000000-persource`,
  `real-2000000-persource` and `mixed-2000000-persource` were built clean.
- **Affected records: 10**, all with `SI_DR_PERSOURCE=1` and no `SI_DR_PRESORT` of their own: `persource hash existing 1M/3000k`,
  `persource hash new-source 1M/3000k`, `persource rescan 1% 1M/3000k` (`probe-persource`); `persource mixed new-source 1M/3000k`,
  `persource hash existing 2M/6000k`, `persource hash new-source 2M/6000k` (`probe-persource-real`); `analyse persource hash
  new-source 1M/3000k`, `analyse persource hash existing 1M/3000k`, `cancel persource hash existing 2M/6000k`, `crash persource hash
  existing 2M/6000k` (`probe-persource-analyse`). `b1_table.py` marks their cells with †.
- **What is withdrawn.** As clean measurements of the per-source dictionary alone: the throughput of those cells in the design
  review's §10.2 B1 table; the reading "B1 bounds the cost, it does not make the same-source work cheaper (39k rows/s either way)";
  the "D-52's alike" half of the old PERF-14 basis; the 2M cancel (3.094 s) and crash (1.41 s) figures of a per-source existing
  source as clean B1 figures (their journals stand). The journals of these cells stand: journal composition does not depend on
  `name_id` assignment within the measured precision (the clean existing-source rerun journals within 1% of the design review's).
- **What replaces them** (the repair's clean reruns, `repair-results/`, informational: the machine was loaded): `persource hash
  existing 1M/3000k` 48,846 / 67,670 / 55,171 rows/s (and 47,410 in the analysed run), journal 62,927,656 B; `persource hash
  new-source 1M/3000k` 71,820 / 119,831 / 96,888 rows/s (61,793 analysed), journal 247,328 B; `persource hash existing 2M/6000k`
  43,669 rows/s (45.7 s), journal 127,665,176 B (121.75 MiB, against 120.22 MiB on the contaminated prefill). The one worst-case
  existing-source cell of the design review that was clean, `persource mixed existing 2M/6000k` (37,441 rows/s, 53.3 s), stands.
- **Correction of the harness** (`harness.patch`): the prefill cache key names the generator version, family, parameters, size,
  schema variant and, for the real vocabulary, the names file's hash; the prefill child's environment holds only `SI_DR_PERSOURCE`
  and `SI_DR_REAL_NAMES`; every child's environment is built explicitly, so nothing from the orchestrator's shell leaks into it.

## E2. The `NotALibrary` record and the recovery child's environment (C4DR-O01)

- **Defect.** In `results/probe-combined-analyse.jsonl`, the crash cell `crash persource+chunked hash existing 2M/6000k` recovered
  the hot journal correctly (126,047,776 B; main file 1,090,662,400 B at the kill, 835,309,568 B after recovery, its exact size
  before the import) but the start-up open derived **`NotALibrary`**, and the record says `"OlderSnapshotsVerify": true` although
  the older snapshots were never verified (the state was not Available). The design review did not mention it.
- **Cause.** A harness artefact, not a product defect: that cell ran on the first probe build, whose recovery child did not receive
  the run's `SI_DR_PERSOURCE`, so the per-source schema failed the normal schema fingerprint (the probe bypasses the fingerprint only
  when `SI_DR_PERSOURCE=1`). Later builds passed the run's variables to the recovery child.
- **Correction.** The repaired harness passes the run's variables to the recovery child explicitly and records them with the result
  (`recoverEnv`), and records `OlderSnapshotsVerify` as `null` with the reason when the check was skipped. The repair's crash cell
  (`crash persource objrescan 2M/6000k n1`) recovered 30,325,728 B of journal in 0.875 s, derived Available and verified the older
  snapshots, with `{"SI_DR_PERSOURCE": "1"}` recorded.

## E3. Build-overlap count and the figures that rest on overlapped runs (C4DR-O03)

- **Defect.** The design review's §4.5 says twelve measured imports overlapped a build but enumerates eleven (two analysis runs
  instead of three). Two of its six build windows (15:57:42 and 16:28:22 UTC) have no surviving build log.
- **Recomputed** (`python overlap.py results`): **12** overlapped runs: `hash 2M/750k r3`, `append 1M/3000k r3` and `hash 1M/3000k
  r3` (reproduction); `mixed 1M/3000k r2` and `real 1M/3000k r2`; `analyse append 1M/3000k` and two `persource+chunked` analysis
  runs; the two whole-snapshot `presort` runs; two `persource+chunked mixed` runs. The script carries the six windows with their
  provenance (four from build logs, two from the session's own overlap script only).
- **Figures that rest on an overlapped run:** "PERF-01 below its stop line in one of the three 1M / 3×1M runs" and the "36k"
  lower bound (both the overlapped `hash 1M/3000k r3`, 35,823 rows/s; the clean runs are 50,823 and 53,340). The B2 table also used
  three analysis-run timings (59,688; 57,826; 70,363) although its §4.4 says analysis timings are not used. None changes a
  decision; the repaired specification no longer quotes them.

## E4. Missing `.log` evidence (C4DR-O02)

- **Defect.** The README listed `results/<plan>.log` as evidence, but the repository's `*.log` rule kept the files out of
  `1206a1d`; the plan-level load figures ("other readable processes 1.9 to 7.2 of 16 CPUs") could not be checked from the
  repository.
- **Correction.** The 21 plan logs, the design session's own copies with other processes' names already removed, are committed by
  the repair after a privacy scan (`privacy_scan.py`: no identity string, private path, harvested name or process name found);
  `.gitignore` in this directory re-includes them; `MANIFEST.md` lists every file with its SHA-256.

## E5. Units (C4DR-O06)

- The design review declares MB = 2^20 bytes. Its staging table printed the peaks in binary but the totals in decimal:
  "798 MB" is 798,324,032 B = 761.3 MiB; "2,195 MB" is 2,195,195,168 B = 2,093.5 MiB; "380 MB" is 380,100,872 B = 362.5 MiB.
  The peaks are binary: 109,840,680 B = 104.75 MiB; 37,071,944 B = 35.36 MiB; 99,978,720 B = 95.35 MiB; the failure left the
  main file 464.9 MiB against 392.5 MiB, 72.4 MiB more.
- The amended specification printed binary figures labelled MB without a convention ("57 pages (0.23 MB)" for 235,016 B =
  0.224 MiB = 0.235 MB). The repaired specification states MiB = 2^20 B and MB = 10^6 B and labels the D-52 figures MiB; the
  new-source journal is quoted at its pre-`COMMIT` peak, 60 pages, 247,328 B = 0.24 MiB.
- "Raised the working set by 322 MB" (cache probe) is the total above the pre-import baseline (321 to 322 MiB); the increase over
  the 64 MiB cache's 132 to 135 MiB is about 190 MiB (C4DR-O09).
- "Recovery at the next open costs about 8 ms per MB" holds for one cell; the cost per MiB ranged from 7.6 to 18.9 ms (C4DR-O05).
- The repaired scripts label sizes MiB and print bytes where a figure is decisive.

## E6. Python encoding (C4DR-O04)

- **Defect.** `tables.py` failed with `UnicodeEncodeError` under the Windows console code page (cp1252 cannot encode "→" and "–")
  and needed `PYTHONIOENCODING=utf-8`. Reproduced by the repair before its correction.
- **Correction.** Every script of this directory reconfigures its output to UTF-8; the repair ran each on a cp1252 console.

## E7. Other statements corrected

- **"Real trees repeat names heavily: about 10% of the harvested names were new to their source"** (design review §6, §12) is an
  artefact: the real-vocabulary family cycled a harvested list into the generator's slots (`list[slot % length]`), which caps the
  distinct share at the list's distinct count over the file count (10.2% at 1M, 5.1% at 2M). At natural size the harvested trees
  are 52% to 67% distinct (C4DR-H01). The repaired §15.4 replaces the family with objective, census-calibrated generators.
- **"In all 92 normal runs"** the handle and `FileInfo` samplers agreed: the committed evidence holds 118 published normal runs, and
  the property holds in all 130 non-crash records (C4DR-O04).
- **The pre-import copy predated T0** (C4DR-O12): page 1 of the copy differed from the journal's original image. The repaired
  harness copies after T0; the repair's analysed journals show 0 records whose image differs from the copy.
- **"Storing a name once per source costs almost nothing"** rested on vocabularies that overlap by 1.5%; the re-review's estimate
  (about 26 to 70 MiB per duplicated source dictionary, a few percent of a Library with history) replaces it (C4DR-O08).
