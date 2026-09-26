# StorageInventory

A **read-only** storage inventory tool for Windows. Point it at a drive or folder and it tells you exactly where the
space is going, without ever opening, changing or deleting your files.

## Components

| Path | What it is | Status |
|---|---|---|
| `powershell/` | `StorageInventory.ps1`, the hardened PowerShell implementation and **behavioural reference**, plus its regression suite | Phase A complete ([report](docs/phase-a-report.md)) |
| `src/StorageInventory.Core/` | Native scanning engine (C#, .NET 10): path policy, enumeration, aggregation, reports | Phase B in progress |
| `src/StorageInventory.App/` | Native WPF application, a client of Core | Phase B, planned |
| `tests/` | Core unit tests and integration tests, including parity with the PowerShell reference | Phase B in progress |
| `docs/` | Reports, benchmarks, architecture, security and release documentation | |

## Safety contract

- Scanned files are never modified, and their contents are never opened or read. Only metadata is enumerated.
- Junctions, symbolic links and other reparse points are never followed.
- No administrator rights, registry changes, services, scheduled tasks, telemetry or network activity (unless you choose
  a network path yourself).
- Reports are only ever created as **new** files in the output folder you choose, which must be outside the scanned
  tree. Existing files are never overwritten.
- A cancelled, failed or partially-readable scan is always reported as such, never as complete.

## Building (Phase B)

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
