| Run | File rows | Folder rows | Import (BEGIN to COMMIT) | File rows/s | Peak journal | Journal / snapshot growth | DB before | DB after | Growth per file row | Peak working set above the baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| import 1M (run 1) | 999,111 | 250,000 | 7.85 s | 127,333 | 0.19 MB | 0.142% | 93.6 MB | 227.1 MB | 140.1 B | 121 MB |
| import 1M (run 2) | 999,111 | 250,000 | 7.83 s | 127,570 | 0.19 MB | 0.142% | 93.6 MB | 227.1 MB | 140.1 B | 120 MB |
| import 1M (run 3) | 999,111 | 250,000 | 9.34 s | 106,971 | 0.19 MB | 0.142% | 93.6 MB | 227.1 MB | 140.1 B | 121 MB |
| import 2M (run 1) | 1,997,023 | 500,000 | 18.19 s | 109,768 | 0.19 MB | 0.070% | 93.6 MB | 363.9 MB | 141.9 B | 138 MB |
| import 2M (run 2) | 1,997,023 | 500,000 | 17.47 s | 114,300 | 0.19 MB | 0.070% | 93.6 MB | 363.9 MB | 141.9 B | 138 MB |
| import 2M (run 3) | 1,997,023 | 500,000 | 25.55 s | 78,153 | 0.19 MB | 0.070% | 93.6 MB | 363.9 MB | 141.9 B | 139 MB |
| import 10M | 9,995,257 | 2,500,000 | 124.20 s | 80,476 | 0.19 MB | 0.014% | 93.6 MB | 1479.2 MB | 145.4 B | 319 MB |
| 2M, no foreign keys (round 1) | 1,997,023 | 500,000 | 20.85 s | 95,774 | 0.19 MB | 0.070% | 93.6 MB | 363.9 MB | 141.9 B | 138 MB |
| 2M, foreign keys declared (round 1) | 1,997,023 | 500,000 | 28.35 s | 70,450 | 0.19 MB | 0.070% | 93.6 MB | 363.9 MB | 141.9 B | 136 MB |
| 2M, no foreign keys (round 2) | 1,997,023 | 500,000 | 19.87 s | 100,504 | 0.19 MB | 0.070% | 93.6 MB | 363.9 MB | 141.9 B | 135 MB |
| 2M, foreign keys declared (round 2) | 1,997,023 | 500,000 | 24.62 s | 81,118 | 0.19 MB | 0.070% | 93.6 MB | 363.9 MB | 141.9 B | 138 MB |
| 2M, no foreign keys (round 3) | 1,997,023 | 500,000 | 15.66 s | 127,486 | 0.19 MB | 0.070% | 93.6 MB | 363.9 MB | 141.9 B | 138 MB |
| 2M, foreign keys declared (round 3) | 1,997,023 | 500,000 | 24.17 s | 82,640 | 0.19 MB | 0.070% | 93.6 MB | 363.9 MB | 141.9 B | 125 MB |

| Deletion (T-DELETE of the snapshot just imported) | File rows | Duration | Peak journal | Verification of the committed snapshot (invariants 1 to 12) |
|---|---:|---:|---:|---|
| import 2M (run 1) | 1,997,023 | 2.44 s | 127.9 MB | 7.7 s, all hold |
| import 2M (run 2) | 1,997,023 | 2.63 s | 128.2 MB | 7.8 s, all hold |
| import 2M (run 3) | 1,997,023 | 2.48 s | 128.2 MB | 9.7 s, all hold |

| Failure run | File rows attempted | Failure classified as | Time to fail and roll back | Database size before / after the failed import | Older snapshots intact |
|---|---:|---|---:|---|---|
| 2M, SQLITE_FULL at 80 MiB | 2,000,000 | LibraryFull (201) | 3.56 s (rollback alone) | 37.8 MB / 37.8 MB | yes |
