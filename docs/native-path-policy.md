# Native path policy (`StorageInventory.Core.Paths.PathPolicy`)

`PathPolicy.Validate(source, reports)` is the only place path safety is decided. The scanner calls it again before
it starts, whatever the UI has already checked. It **only reads metadata** and creates nothing; a fixture snapshot
test proves this.

Each finding is a `PathIssue` with a severity (`Info` / `Warning` / `Blocked`), a stable `Code`, a plain-language
`Message` and technical `Details`. The scan may start only when nothing is `Blocked`. Suspicious input is never
silently "fixed": every normalisation is reported, at least as `Info`.

## Rules

| Input | Result | Code (Source… / Output…) |
|---|---|---|
| Empty or whitespace | Blocked | `Empty` |
| Surrounding spaces | Trimmed, and reported | `WhitespaceIgnored` (Info) |
| `\\?\…`, `\\.\…` (before or after normalisation) | Blocked | `DevicePath` / `ReservedName` |
| `D:` | Taken to mean `D:\`, and reported | `DriveRootAssumed` (Info) |
| `" * ? < > |` or control characters | Blocked | `InvalidCharacters` |
| Relative (`Media`, `D:Media`, `\Media`) | Blocked, unless a base directory is supplied (`\Media` and `D:Media` stay blocked either way) | `NotFullPath` |
| A folder name ending in `.` or a space | Blocked (checked **before and after** normalisation) | `TrailingDotOrSpace` |
| `CON PRN AUX NUL COM0-9 LPT0-9 COM¹²³ LPT¹²³` as any component, with or without an extension | Blocked. `CONFIG`, `COM10`, `NULL`, `COM٣` and `LPT⁴` are allowed | `ReservedName` |
| `:` after the drive letter | Blocked | `StreamSyntax` |
| Source doesn't exist / is a file | Blocked | `SourceMissing` / `SourceIsFile` |
| Output is a file | Blocked | `OutputIsFile` |
| Output doesn't exist | Allowed; it will be created | `OutputWillBeCreated` (Info) |
| Any existing component of either path is a reparse point | Blocked | `SourceThroughLink` / `OutputThroughLink` |
| Output equal to or inside the source (textually) | Blocked | `OutputInsideSource` |
| Output inside the source at its **real** location | Blocked | `OutputInsideSourceAlias` |
| Real location can't be determined | Warning; the runtime tripwire stays the backstop | `CanonicalCheckUnavailable` |
| Source is another name for a different path | Reported | `SourceIsAlias` (Info) |
| UNC path or mapped network drive | Warning | `SourceNetwork` / `OutputNetwork` |
| Output inside OneDrive | Warning | `OutputCloudSynced` |
| Paths longer than 260 characters | Accepted | |

## Intentional differences from the PowerShell reference

1. **Aliases are blocked before anything is created.** `GetFinalPathNameByHandle` resolves the real location of both
   the source and the deepest existing part of the output, so SUBST drives, 8.3 short names and letter-case variants
   are seen through. The reference only caught these *during* the scan, with its tripwire, after the output folder
   and two report files already existed inside the tree. The native scanner keeps the tripwire as a second line of
   defence, for aliases that Windows itself doesn't resolve, such as `\\localhost\C$`.
   - This needs two read-only kernel32 calls (`CreateFileW` with **zero access rights** plus
     `GetFinalPathNameByHandleW`), in `Paths/NativeMethods.cs`. These two are the path policy's native calls. (v1.1
     gate C3 added four more read-only queries beside them for volume and source identity: six in all, see
     [native-security-review.md](native-security-review.md).)
2. **Trailing dots and spaces are always refused.** Both .NET Framework and .NET 10 silently remove them during
   normalisation. The reference refused them only when the stripped path landed inside the source.
3. **Relative paths are blocked by default.** The reference resolved them against the PowerShell location. A GUI has
   no meaningful current directory, so the native policy requires a full path, unless a caller explicitly supplies a
   base directory.
4. **Link descriptions are simpler:** `Link -> target` / `Mount point -> Volume{…}`. .NET's `LinkTarget` reads the
   link's own data without following it, but it doesn't tell a junction apart from a directory symbolic link without
   more native calls. The reference printed `Junction` / `SymbolicLink`. Behaviour is identical: neither is ever
   followed.

## Test evidence

- `tests/StorageInventory.Core.Tests/PathNormalizationTests.cs`: string rules. Drive roots, relative paths, device
  prefixes, every reserved name including superscripts, allowed look-alikes, stream syntax, trailing dots and spaces,
  forbidden characters, literal `[ ] $( ) ` ' & ;`, Korean and emoji, paths over 500 characters, whole-segment
  containment, UNC.
- `tests/StorageInventory.IntegrationTests/PathPolicyFixtureTests.cs`: the **Phase A fixture**, built by
  `TestLib.ps1 New-Fixture`. Covers a missing source, source or output that is a file, output equal to or inside the
  source (case and trailing-slash variants), a junction source (the loop), a source beneath a junction, output through
  a junction into the source, a **SUBST alias blocked up front** in both directions, a long-path source, the
  Unicode/bracket source, and a proof that validation changes nothing.
- **Skipped here:** directory and file symbolic links (no Developer Mode or admin), and the 8.3 alias (short names are
  disabled on this test volume). All three run automatically where possible.
