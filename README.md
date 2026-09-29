# StorageInventory

**Reusable Windows filesystem intelligence.** StorageInventory answers "what files exist, where, how big, and was the
observation complete?", and later "what changed?". It is strictly **read-only** and never opens, changes or deletes
your files.

- **`StorageInventory.Core`** is the primary product: a UI-independent .NET library for safe, metadata-only filesystem
  observation, usable from a GUI, a CLI, tests or other downstream tools.
- **`StorageInventory.App`** is the first user-facing consumer: a small WPF storage-analysis utility.

See [docs/architecture.md](docs/architecture.md), [docs/roadmap.md](docs/roadmap.md) and
[docs/integration-consumers.md](docs/integration-consumers.md).

## Components

| Path | What it is | Status |
|---|---|---|
| `powershell/` | `StorageInventory.ps1`, the hardened PowerShell implementation and **behavioural reference**, plus its regression suite | Phase A complete ([report](docs/phase-a-report.md)) |
| `src/StorageInventory.Core/` | Native filesystem-observation engine (C#, .NET 10): path policy, enumeration, aggregation, reports | 1.0 complete ([parity](docs/native-parity-report.md), [security](docs/native-security-review.md)) |
| `src/StorageInventory.App/` | WPF application, a client of Core | 1.0 complete ([release](docs/release.md)) |
| `tests/` | Core unit tests, integration/parity/UI/security tests, release smoke test | 1.0 complete |
| `docs/` | Architecture, roadmap, reports, benchmarks, security and release documentation | |

## Safety contract

- Scanned files are never modified, and their contents are never opened or read. Only metadata is enumerated.
- Junctions, symbolic links and other reparse points are never followed.
- No administrator rights, registry changes, services, scheduled tasks, telemetry or network activity (unless you choose
  a network path yourself).
- Reports are only ever created as **new** files in the output folder you choose, which must be outside the scanned
  tree. Existing files are never overwritten.
- A cancelled, failed or partially-readable scan is always reported as such, never as complete.

## Building

Everything uses the repo-local toolchain in `tools\`, fetched and hash-verified by `tools\fetch-tools.ps1` and
git-ignored:

```powershell
.\build.ps1 -Target Build     # Debug build
.\build.ps1 -Target Test      # unit + integration tests
.\build.ps1 -Target Publish   # self-contained single-file win-x64 exe in dist\
```

`build.ps1` keeps all .NET and NuGet state inside the repo, turns off CLI telemetry, and restores your environment
afterwards. `nuget.config` only allows Microsoft's official .NET runtime packs, which the self-contained publish
needs; the code itself references no packages.
