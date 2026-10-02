| Check | Case | Classes | Intended | Result |
|---|---|---|---|---|
| spec: bands | every row partitions its parameter's range, in class order | (none) |  | Pass |
| spec: cases | F d = 25% (system) | Representative | Representative | Pass |
| spec: cases | F d = 25% + ε (system) | Stress | Stress | Pass |
| spec: cases | F d = 60% (system) | Stress | Stress | Pass |
| spec: cases | F d = 60% + ε (system) | Worst-case | Worst-case | Pass |
| spec: cases | F d = 25% (data) | Stress | Stress | Pass |
| spec: cases | F d = 60% (data) | Stress | Stress | Pass |
| spec: cases | F d = 60% + ε (data) | Worst-case | Worst-case | Pass |
| spec: cases | F d = 100% (data) | Worst-case | Worst-case | Pass |
| spec: cases | F d = ε (system) | Representative | Representative | Pass |
| spec: cases | R ρ = 1% (δ = α = 0.5%) | Representative | Representative | Pass |
| spec: cases | R ρ = 1% + ε | Stress | Stress | Pass |
| spec: cases | R ρ = 10% | Stress | Stress | Pass |
| spec: cases | R ρ = 10% + ε | Worst-case | Worst-case | Pass |
| spec: cases | R δ = 0.5% + ε | Stress | Stress | Pass |
| spec: cases | R δ = 5% | Stress | Stress | Pass |
| spec: cases | R δ = 5% + ε | Worst-case | Worst-case | Pass |
| spec: cases | R α = 0.5% + ε | Stress | Stress | Pass |
| spec: cases | R α = 5% | Stress | Stress | Pass |
| spec: cases | R α = 5% + ε | Worst-case | Worst-case | Pass |
| spec: cases | R no churn at all, representative source | Representative | Representative | Pass |
| spec: cases | R 1% re-scan of a 25%-distinct system-model source | Representative | Representative | Pass |
| spec: cases | R 1% re-scan of a 25%-distinct data-model source | Stress | Stress | Pass |
| spec: cases | R 1% re-scan of a 60%-distinct data-model source | Stress | Stress | Pass |
| spec: cases | R 1% re-scan of a 60%-distinct system-model source | Stress | Stress | Pass |
| spec: cases | R 1% re-scan of a 100%-distinct source | Worst-case | Worst-case | Pass |
| spec: cases | R ρ = 1%, δ = 8% (mixed) | Worst-case | Worst-case | Pass |
| spec: cases | R 60% data source at the stress churn bounds (mixed) | Stress | Stress | Pass |
| spec: cases | R ρ = 5%, δ = α = 0.5% (mixed) | Stress | Stress | Pass |
| spec: cases | R 100% source with no churn (mixed) | Worst-case | Worst-case | Pass |
| spec: cases | F at 10M files (scale cell) | Informational | Informational | Pass |
| spec: cases | R at 1M files | Representative | Representative | Pass |
| spec: cases | F at 100k files (smoke size: classified, gates nothing) | Representative | Representative | Pass |
| spec: cases | N1 append at 1M | Informational | Informational | Pass |
| spec: cases | N2 into an existing source at 2M | Worst-case | Worst-case | Pass |
| spec: cases | N2 at 10M | Informational | Informational | Pass |
| spec: cases | N3 into an existing source at 2M | Worst-case | Worst-case | Pass |
| spec: cases | 1% re-scan of an N2 source at 2M | Worst-case | Worst-case | Pass |
| spec: domain | F d = 0 | rejected as not a valid cell |  | Pass |
| spec: domain | F d > 100% | rejected as not a valid cell |  | Pass |
| spec: domain | R ρ + δ > 100% | rejected as not a valid cell |  | Pass |
| spec: matrix | F(1M, 25%, system) [1M] | Representative | Representative | Pass |
| spec: matrix | F(2M, 25%, system) [2M] | Representative | Representative | Pass |
| spec: matrix | R(1M, 25%, system; 1%, 0.5%, 0.5%) [1M] | Representative | Representative | Pass |
| spec: matrix | R(2M, 25%, system; 1%, 0.5%, 0.5%) [2M] | Representative | Representative | Pass |
| spec: matrix | F(2M, 60%, data) [2M] | Stress | Stress | Pass |
| spec: matrix | R(2M, 25%, system; 5%, 2.5%, 2.5%) [2M] | Stress | Stress | Pass |
| spec: matrix | R(2M, 25%, system; 10%, 5%, 5%) [2M] | Stress | Stress | Pass |
| spec: matrix | R(2M, 60%, data; 1%, 0.5%, 0.5%): the routine re-scan of a high-novelty source [2M] | Stress | Stress | Pass |
| spec: matrix | F(2M, 100%, data) [2M] | Worst-case | Worst-case | Pass |
| spec: matrix | N2 into the existing source [2M] | Worst-case | Worst-case | Pass |
| spec: matrix | N3 into the existing source [2M] | Worst-case | Worst-case | Pass |
| spec: matrix | 1% re-scan of an N2 source [2M] | Worst-case | Worst-case | Pass |
| spec: matrix | N1 append [1M] | Informational | Informational | Pass |
| spec: matrix | N1 append [2M] | Informational | Informational | Pass |
| spec: matrix | N1 append [10M] | Informational | Informational | Pass |
| spec: matrix | N2 at 10M [10M] | Informational | Informational | Pass |
| spec: matrix | representative cells (two first saves and two re-scans, 1M and 2M) | 4 | 4 | Pass |
| old rule (negative self-test) | the design repair's nested-bound rule fails these checks | 29 failing checks, among them F d = 25% (system): no class (d 25% in ['Representative', 'Stress']); R ρ = 1% (δ = α = 0.5%): no class (d 25% in ['Representative', 'Stress']; ρ 1% in ['Representative', 'Stress']; δ 0.5% in ['Representative', 'Stress']; α 0.5% in ['Representative', 'Stress']); R 1% re-scan of a 60%-distinct data-model source: no class (ρ 1% in ['Representative', 'Stress']; δ 0.5% in ['Representative', 'Stress']; α 0.5% in ['Representative', 'Stress']); R ρ = 1%, δ = 8% (mixed): no class (d 25% in ['Representative', 'Stress']; ρ 1% in ['Representative', 'Stress']; δ 8% in []; α 0.5% in ['Representative', 'Stress']) |  | Pass |

checks: 59; FAILS: 0
