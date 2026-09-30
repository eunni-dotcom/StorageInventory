# Release build

## v1.0.0

| Item | Value |
|---|---|
| File | `StorageInventory.exe` (the only file needed) |
| Version | 1.0.0 (file version 1.0.0.0, product version 1.0.0) |
| Size | 130,947,561 bytes (124.9 MB) |
| SHA-256 | `20178D5FBAC49B921A023DA39133A79FF8FB6B53B475FCA7FBE7A1A1CD3FC5B4` |
| Target | `win-x64`, Release, **self-contained**, **single-file**, .NET 10.0.12 runtime bundled |
| Requirements | Windows 10 or 11 x64. No .NET installation, PowerShell, Excel or administrator rights |
| Signing | **Not code-signed.** Windows SmartScreen may warn on first run |
| Embedded paths | None: `PathMap` replaces the build directory, and no PDB is produced (`DebugType=none`). The binary was searched for the build path and machine and account names |
| Licences | Bundled .NET runtime: MIT. See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) |

Release binaries are **not committed** (`dist/` is git-ignored); they are attached to the GitHub Release. Ship
`LICENSE` and `THIRD-PARTY-NOTICES.md` alongside the executable.

## Reproducing the build

```powershell
.\tools\fetch-tools.ps1         # once: repo-local .NET SDK 10.0.401 (hash-verified), nothing installed machine-wide
.\build.ps1 -Target Publish     # restore, clean, publish to dist\, print the SHA-256
```

A publish from clean intermediate output is **reproducible byte for byte** with the pinned SDK and runtime packs.
Several clean publishes on the development machine, one from a fresh clone of the release commit with an empty package
cache, and the GitHub Actions CI build on a hosted runner all gave the SHA-256 above. Three things make that hold:

- **`build.ps1 -Target Publish` cleans before publishing.** Reusing a Core library compiled earlier by a solution
  build gives a different, equally valid binary.
- **`.gitattributes` checks out text files with LF on every machine.** Some files, such as `app.manifest`, are
  embedded verbatim. Git for Windows' default `core.autocrlf=true` would otherwise change them, and with them the exe.
- **The version is `1.0.0`, without a `+<commit>` suffix,** so documentation-only commits don't change the
  executable.

`build.ps1 -Target Publish` runs, after `dotnet restore` and `dotnet clean`:

```
dotnet publish src\StorageInventory.App\StorageInventory.App.csproj -c Release -r win-x64 --self-contained true
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
  -p:EnableSingleFileAnalyzer=false -o dist -nodeReuse:false
```

### What gets downloaded

Exactly **two** packages are downloaded, both Microsoft's official .NET runtime packs, from nuget.org into the
repo-local `packages\` folder:

- `Microsoft.NETCore.App.Runtime.win-x64` 10.0.12
- `Microsoft.WindowsDesktop.App.Runtime.win-x64` 10.0.12

`nuget.config` uses package source mapping, so NuGet **refuses anything else**. Two further packages the SDK would
otherwise request are switched off:

- `Microsoft.AspNetCore.App.Runtime.win-x64` is a pre-fetch for frameworks the app doesn't use. It's switched off with
  `DisableTransitiveFrameworkReferenceDownloads`.
- `Microsoft.NET.ILLink.Tasks` is needed only by the single-file compatibility *analyzer*, which produces warnings and
  never changes the output. It's switched off with `EnableSingleFileAnalyzer=false`. The code avoids the APIs that
  analyzer checks, such as `Assembly.Location` in the product.

`build.ps1` keeps all .NET and NuGet state inside the repository, turns off CLI telemetry, and restores the caller's
environment afterwards.

### Single-file behaviour to know about

WPF includes native DLLs that Windows can only load from disk. With `IncludeNativeLibrariesForSelfExtract`, the .NET
host unpacks them on first run to **`%TEMP%\.net\StorageInventory\<hash>\`**, and reuses that folder afterwards.

- This is .NET runtime behaviour, not application code.
- It never touches the scanned tree or the report folder.
- The folder can be deleted at any time, and will be recreated.

A deployment that must avoid it can publish without `IncludeNativeLibrariesForSelfExtract`. That gives the exe plus a
few native DLLs alongside it, at the cost of no longer being a single file.

## Smoke test

`tests\smoke\Invoke-ReleaseSmokeTest.ps1` copies the exe **outside the repository**, starts it with **no
`DOTNET_ROOT`**, and drives it through UI Automation. For v1.0.0 it gave **15/15 PASS**:

| Check | Result |
|---|---|
| Starts from outside the repo with no `DOTNET_ROOT` | PASS |
| Uses its bundled runtime: no loaded module comes from an installed or repo-local .NET | PASS |
| Browse opens the folder picker | PASS |
| Pre-flight blocks a report folder inside the source; Start scan disabled | PASS |
| Pre-flight READY for a valid pair | PASS |
| Scan finishes and shows its outcome; three CSV reports written | PASS |
| Open report folder shows the folder in Explorer (explicit click) | PASS |
| New scan returns to setup | PASS |
| Large scan (250k files) runs with Cancel available; Cancel stops it and says so; no report file left open | PASS |
| Exits cleanly when closed (exit code 0) | PASS |

To reproduce (the second folder must be large enough to cancel mid-scan):

```powershell
powershell -ExecutionPolicy Bypass -File tests\smoke\Invoke-ReleaseSmokeTest.ps1 -Source <small folder> -LargeSource <folder with ~250k files>
```

## Test status of the release

- Unit tests **55/0/0** and integration tests **89/0/4** (4 skipped for missing privileges, 8.3 names or opt-in large
  trees), in the Release configuration.
- CI (GitHub-hosted Windows runner): unit 55/0/0 and integration 86/0/7. The runner lacks the local benchmark trees and
  8.3 names, but can create symbolic links, so the symbolic-link cases run there.
- PowerShell reference suite **105/0/5** on Windows PowerShell 5.1 and PowerShell 7.6.6.
- PowerShell parity: identical reports on every fixture ([native-parity-report.md](native-parity-report.md)).
- Security audit tests: pass ([native-security-review.md](native-security-review.md)).
- Performance: [benchmarks/native.md](benchmarks/native.md).
