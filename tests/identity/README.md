# Identity experiments (v1.1 gate C3)

Reproducible evidence for the platform questions the specification leaves to gate C3: **Q-02** (which identity calls work on which source types), **Q-13** (root directory file IDs) and **Q-19** (side effects of holding a zero-access root handle for a whole scan), and for the manual tests **TEST-I2**, **TEST-I4** and **TEST-I6**. Results are recorded in [`docs/v1.1-c3-implementation-evidence.md`](../../docs/v1.1-c3-implementation-evidence.md).

Everything here runs the **product's own** capture, classification and re-verification code (`StorageInventory.Core` and `StorageInventory.History`) through two test-only commands of `StorageInventory.IntegrationTests`. Nothing is read from or written to the volumes under test: the probe opens the source folder with **desired access 0** and asks six read-only questions.

```powershell
.\build.ps1 -Target Build -Configuration Release
$exe = 'tests\StorageInventory.IntegrationTests\bin\Release\net10.0-windows\StorageInventory.IntegrationTests.dll'
dotnet $exe --identity-probe  E:\ F:\Photos \\nas\media --label "my media"      # the matrix: one block per path
dotnet $exe --identity-hold   E:\ --label "USB stick"                           # holds the handle until you press Enter
```

(The repo-local SDK is `tools\dotnet\dotnet.exe`; use it if no global .NET 10 runtime is installed.)

| Script | Needs | What it does |
|---|---|---|
| `Invoke-IdentityExperiments.ps1` | Release build; elevation only for the virtual-media parts | Runs the matrix, the drive-letter change and every hold experiment that can be automated, and writes one Markdown report. Parts it cannot run are reported as **NOT RUN** with the reason |
| `Provision-IdentityMedia.ps1` | **elevated** PowerShell | Creates disposable VHDX volumes (NTFS x2, FAT32, exFAT, ReFS), a UDF image and two SMB shares (one on FAT32), writes a manifest, and removes it all with `-Remove` |

```powershell
# no administrator rights: every drive, a subfolder, SUBST, SUBST re-pointing, a renamed folder (and, with the switch, a loopback SMB share)
.\tests\identity\Invoke-IdentityExperiments.ps1 -IncludeLoopbackAdminShare -Out $env:TEMP\identity.md

# elevated: add virtual FAT32 / exFAT / ReFS / second NTFS volumes, UDF, SMB shares, a volume lock, surprise removal and swapped media
.\tests\identity\Provision-IdentityMedia.ps1 -Manifest $env:TEMP\identity-media.json
.\tests\identity\Invoke-IdentityExperiments.ps1 -Manifest $env:TEMP\identity-media.json -Out $env:TEMP\identity-full.md
.\tests\identity\Provision-IdentityMedia.ps1 -Remove -Manifest $env:TEMP\identity-media.json
```

## What reading the output means

For every path the probe prints, per native call, the value or the Win32 error, and then:

* **Confidence**: `Strong` (local NTFS/ReFS with a non-zero 64-bit serial), `Moderate` (local FAT, FAT32 or exFAT with a non-zero 32-bit serial), otherwise `PathOnly`, with the reason.
* **ID-13 items available** and **Save to History**: whether Windows gave enough evidence to re-verify the source at the end of a scan. `UNAVAILABLE` means the minimum evidence (canonical path; for a local volume also the filesystem name and 32-bit serial) is missing, and the source would be scanned for reports only.
* **FileIdInfo `NOT PROVIDED`** is a filesystem that answers "invalid parameter" (Win32 error 87) to the query; it does not stop saving, because the 64-bit serial and root file ID are required later only when the call succeeded at preflight.
* **E0..E3 cycle**: `Verified` when nothing changed, which every healthy source must show.

In hold mode the tool prints `E0`, opens the path and holds the handle (`E1`, then the word `HELD`), waits, then prints `E2` (read through the **held** handle) and `E3` (read through a **fresh** open of the same path) and `RESULT=Verified` or `RESULT=IdentityChangedDuringScan`, with every difference. Both outcomes are data: record what happened, do not assume which call fails.

## Manual checklist (hardware this repository's CI does not have)

Record each result in the evidence document's tables with the exact tool output. Use media with nothing important on it for the removal steps, and make sure nothing else is writing to it.

1. **TEST-I1 / Q-13, second and third volume.** `--identity-probe` on `C:\`, another NTFS volume (a VHDX mounted in Disk Management is fine) and a **ReFS Dev Drive** (Settings, System, Storage, Disks and volumes, Create Dev Drive). Record the serials, confidence and **root file ID**; the NTFS values are expected to be the same on every volume (`0x00000000000000000005000000000005`), which is why the root ID is never volume identity. Write down the ReFS value, whatever it is.
2. **TEST-I2, drive-letter change.** Probe a removable or virtual volume at one letter. In Disk Management choose *Change Drive Letter and Paths* and give it another letter, then probe again. Pass when the filesystem, both serials, the confidence and the root in volume are identical (`Invoke-IdentityExperiments.ps1` does this for a VHDX when elevated).
3. **TEST-I4 / Q-02, source types.** Probe the root **and a folder inside it** of: a **FAT32** USB stick or VHDX; an **exFAT** stick or VHDX; a **UDF** disc or mounted ISO (Explorer's *Mount* needs no administrator rights; a UDF ISO can be built with `Provision-IdentityMedia.ps1`'s IMAPI2FS code); an **SMB share on a Windows server**; an **SMB share on a Samba server** (a NAS or a Linux machine); and a **mapped drive letter** for each share, plus its UNC path. Note for each: whether the zero-access open succeeded, every native call's value or Win32 error, and the *Save* line. A source type that fails any item stays at its safe default: `PathOnly`, and not saveable if an ID-13 item is missing.
4. **TEST-I6 / Q-19, USB or removable media, "Safely remove".** `--identity-hold E:\` (your stick), wait for `HELD`, then try the tray's *Safely Remove Hardware and Eject Media*. Record Windows' exact response (an expected one is that the device is in use and cannot be stopped). Press Enter in the console, then try again: it must succeed once the handle is closed. If the answer is anything beyond "blocks Safely remove while the tool runs", **stop**: the gate's stop condition asks for a design review.
5. **TEST-I6, surprise removal and a swapped medium.** `--identity-hold E:\`, wait for `HELD`, **pull the stick**, press Enter. Expect `IdentityChangedDuringScan`, with `E2` failing (the held handle is dead) and `E3` unable to open the path. Repeat, but put a *different* stick at the same letter before pressing Enter: `E3` should read another serial.
6. **TEST-I5 / Q-19, network share and mapped drive.** `--identity-hold X:\` on a mapped letter, then (a) from another machine rename the share's root folder; (b) re-map `X:` to a different share (`net use X: /delete /y`, then `net use X: \\server\other`); (c) try a plain `net use X: /delete` (it asks for confirmation while the handle is held). Record E2 and E3 for each, against a Windows server and a Samba server.
7. **SUBST (no hardware needed).** Covered by the automated experiment; to repeat it by hand: `subst S: C:\A`, `--identity-hold S:\`, then `subst S: /D` and `subst S: C:\B`, press Enter. Re-pointing to `B` must be caught by `E3`; re-pointing to `B` and back to `A` is limitation L-ID2 and is *not* caught.
