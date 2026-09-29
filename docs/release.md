# Release build (Gate B11)

**STATUS: PASS**

## Output

| Item | Value |
|---|---|
| File | `dist\StorageInventory.exe` (the only file) |
| Size | 130,948,073 bytes (124.9 MB) |
| SHA-256 | `A5EE77F81251880F838A55141D318F87734DFBC8AE2B1FA56B07448EEC9D4B5B` (build of 2026-09-29) |
| Target | `win-x64`, Release, **self-contained**, **single-file**, .NET 10.0.12 runtime bundled |
| Requirements | Windows 10/11 x64. No .NET install, no PowerShell, no ImportExcel, no administrator rights |
| Signing | **Not code-signed**; no signing setup exists. Windows SmartScreen may warn on first run. |
| Machine paths | None embedded (`PathMap`); checked by searching the binary for the build path |

`dist\` is git-ignored: release binaries are **not** committed. No GitHub Release has been created.

## Reproducing the build

```powershell
.\tools\fetch-tools.ps1         # once: repo-local .NET SDK 10.0.401 (hash-verified), not installed machine-wide
.\build.ps1 -Target Publish     # restores 2 packages, then publishes to dist\
```

`build.ps1 -Target Publish` runs:

```
dotnet publish src\StorageInventory.App\StorageInventory.App.csproj -c Release -r win-x64 --self-contained true
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
  -p:EnableSingleFileAnalyzer=false -o dist -nodeReuse:false
```

### What gets downloaded

It downloads exactly **two** packages, both Microsoft's official .NET runtime packs, from nuget.org into the repo-local
`packages\` folder:

- `Microsoft.NETCore.App.Runtime.win-x64` 10.0.12
- `Microsoft.WindowsDesktop.App.Runtime.win-x64` 10.0.12

`nuget.config` uses package source mapping, so NuGet **refuses anything else**. The first publish attempt proved this:
two further packages were refused and never downloaded.

- **`Microsoft.AspNetCore.App.Runtime.win-x64`** was a pre-fetch for frameworks we don't use. It's switched off by
  `DisableTransitiveFrameworkReferenceDownloads`.
- **`Microsoft.NET.ILLink.Tasks`** is needed only by the single-file compatibility *analyzer*, which gives warnings and
  never changes the output. It's switched off with `EnableSingleFileAnalyzer=false`. The code avoids the APIs it checks
  for (such as `Assembly.Location` in the product).

`build.ps1` keeps all .NET and NuGet state inside the repo, turns off CLI telemetry, and restores the caller's
environment afterwards. Your user profile was verified untouched: no `~\.dotnet`, no `%APPDATA%\NuGet`, no `~\.nuget`.

### Single-file behaviour to know about

WPF includes native DLLs that Windows can only load from disk. With `IncludeNativeLibrariesForSelfExtract`, the .NET
host unpacks them on first run to **`%TEMP%\.net\StorageInventory\<hash>\`**, and reuses that folder afterwards.

- This is .NET runtime behaviour, not application code.
- It never touches the scanned tree or the report folder.
- The folder can be deleted at any time; it will be recreated.

A deployment that must avoid it would publish without `IncludeNativeLibrariesForSelfExtract`, giving the exe plus a
few native DLLs alongside it, at the cost of not being a single file.

## Smoke test

`tests\smoke\Invoke-ReleaseSmokeTest.ps1` copies the exe **outside the repository**, starts it with **no
`DOTNET_ROOT`**, and drives it through UI Automation. Run on 2026-09-29, it gave **15/15 PASS**:

| Check | Result |
|---|---|
| Starts from outside the repo with no `DOTNET_ROOT` | PASS |
| Uses its bundled runtime: none of its 74 loaded modules come from an installed or the repo-local .NET. The machine only has .NET 8 installed, so a .NET 10 app can only run on its own runtime | PASS |
| Browse opens the folder picker | PASS |
| Pre-flight blocks a report folder inside the source; Start scan disabled | PASS |
| Pre-flight READY for a valid pair | PASS |
| Scan finishes and shows its outcome; three CSV reports written | PASS |
| Open report folder shows the folder in Explorer (explicit click) | PASS |
| New scan returns to setup | PASS |
| Large scan (250k files) runs with Cancel available; Cancel stops it and says so; no report file left open | PASS |
| Exits cleanly when closed (exit code 0) | PASS |

Reproduce:

```powershell
powershell -ExecutionPolicy Bypass -File tests\smoke\Invoke-ReleaseSmokeTest.ps1 -Source <small folder> -LargeSource <folder with ~250k files>
```

## Test status of the shipped code

- Unit **55/0/0** and integration **88/0/4** against the Release configuration (Gate B10).
- PowerShell parity: identical reports on every fixture (Gate B6).
- Security audit: 9/9 (Gate B10).
- Performance: [benchmarks/native.md](benchmarks/native.md).
