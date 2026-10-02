# Contributing to StorageInventory

Thanks for your interest. StorageInventory is a small project with a strong safety contract, so contributions are
judged first on whether they keep it.

## Ground rules

- **The scanned tree is read-only.** No change may open file contents, write, rename, move or delete anything in a
  scanned folder, change attributes or timestamps, or follow a junction or symbolic link. All file creation stays in
  `ReportRun` (Core); the only process launch stays in `ReportOpener` (App).
- **No telemetry, network calls, auto-update, registry writes, services or elevation.**
- **No third-party packages** without a strong reason discussed first in an issue. The only ones are the four SQLite packages of
  the v1.1 persistence decision, referenced by `src/StorageInventory.Library` alone and pinned by the committed lock files
  (audit rule A-11); `StorageInventory.Core` and `StorageInventory.History` stay package-free.
- **Honest outcomes.** A cancelled, failed or partially readable scan must never look complete.
- `tests/StorageInventory.IntegrationTests/SecurityAuditTests.cs` enforces much of this mechanically. If it fails,
  treat that as a design discussion, not a test to adjust.

Please open an issue before starting anything large, so we can agree on scope first. The
[roadmap](docs/roadmap.md) shows what is planned and what is permanently out of scope.

## Building and testing

Windows 10 or 11 x64 is required. The toolchain is repo-local, so nothing is installed machine-wide:

```powershell
.\tools\fetch-tools.ps1                          # once: .NET SDK, portable PowerShell 7, ImportExcel (tests only)
.\build.ps1 -Target Test -Configuration Release  # build, then unit and integration tests
.\build.ps1 -Target Publish                      # self-contained single-file exe in dist\
```

- The integration tests build their own fixture trees under `%TEMP%\StorageInventoryTests` (junctions, deny-ACL folders,
  long paths, odd names) and remove them afterwards. Tests whose prerequisites aren't available, such as symbolic links
  without Developer Mode, report SKIP with a reason.
- The PowerShell reference implementation has its own suite: see [powershell/README.md](powershell/README.md).
- Report-format changes must keep parity with the PowerShell reference (`ParityTests`), or explain why they don't.

## Pull requests

- Keep changes focused, and add or update tests for any behaviour change.
- Describe what you tested. For anything touching paths, links, cancellation or report files, say which cases you
  covered.
- Don't attach real inventory reports or screenshots of real drives: they reveal file names. Use synthetic trees.

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE).
