<#
.SYNOPSIS
    Runs the v1.1 C3 platform experiments (Q-02, Q-13, Q-19; manual TEST-I2, TEST-I4, TEST-I6) through the PRODUCT's own capture code and
    writes the results as Markdown.
.DESCRIPTION
    Every reading comes from `StorageInventory.IntegrationTests --identity-probe` / `--identity-hold`, which run Core's evidence capture,
    History's classification and History's E0..E3 re-verification, so the evidence describes the shipped logic, not a second implementation.

    Needs a Release build of the solution (`.\build.ps1 -Target Build -Configuration Release`). Runs without administrator rights; the
    parts that need them (virtual disks, SMB shares, locking a volume) run only when this session is elevated AND a manifest from
    Provision-IdentityMedia.ps1 is given, and are otherwise reported as NOT RUN with the reason. Nothing is ever read from or written
    to the probed volumes; the scratch folders it creates are under -Work and are removed.

    What it covers:
      Part 1  Q-02 and Q-13: a matrix over every drive, a subfolder, a SUBST letter, UNC paths and the provisioned media
      Part 2  TEST-I2: the same volume at a second drive letter has the same identity
      Part 3  Q-19 and TEST-I5/I6: hold the root handle, change the world, read E2 and E3 - SUBST re-point (and away-and-back),
              surprise removal and a swapped medium, a volume lock with and without the held handle, a re-pointed mapped drive,
              a disconnect with the handle held, a share root renamed by another client
.EXAMPLE
    .\tests\identity\Invoke-IdentityExperiments.ps1 -Out C:\temp\identity.md
    .\tests\identity\Invoke-IdentityExperiments.ps1 -Manifest $env:TEMP\identity-media.json -Out C:\temp\identity-full.md
#>
param(
    [string] $Manifest,
    [string] $Out = (Join-Path $env:TEMP 'identity-experiments.md'),
    [string] $Work = (Join-Path $env:TEMP ('SiIdentityWork_' + [guid]::NewGuid().ToString('N').Substring(0, 6))),
    [string] $Dotnet,
    [string] $TestDll,
    [switch] $IncludeLoopbackAdminShare
)
$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Dotnet) { $Dotnet = Join-Path $repo 'tools\dotnet\dotnet.exe' }
if (-not $TestDll) { $TestDll = Join-Path $repo 'tests\StorageInventory.IntegrationTests\bin\Release\net10.0-windows\StorageInventory.IntegrationTests.dll' }
if (-not (Test-Path -LiteralPath $TestDll)) { throw "Build first: $TestDll is missing (run .\build.ps1 -Target Build -Configuration Release)." }
if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path -Parent $Dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$elevated = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$media = if ($Manifest -and (Test-Path -LiteralPath $Manifest)) { Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json } else { $null }
New-Item -ItemType Directory -Force $Work | Out-Null

$md = New-Object System.Collections.Generic.List[string]
function Add-Md([string] $Text = '') { $md.Add($Text) }
function Add-Fence([string] $Text) { Add-Md '```text'; Add-Md ($Text.TrimEnd()); Add-Md '```' }
function Add-NotRun([string] $Name, [string] $Why) { Add-Md "**NOT RUN: $Name.** $Why"; Add-Md }

function Invoke-Probe([string[]] $Paths, [string] $Label, [string] $Json) {
    $args = @($TestDll, '--identity-probe') + $Paths + @('--label', $Label)
    if ($Json) { $args += @('--json', $Json) }
    return (& $Dotnet @args 2>&1 | Out-String)
}

function Get-ProbeJson([string] $Path, [string] $Label) {
    $json = Join-Path $Work ([guid]::NewGuid().ToString('N') + '.json')
    Invoke-Probe @($Path) $Label $json | Out-Null
    if (Test-Path -LiteralPath $json) { return (Get-Content -LiteralPath $json -Raw | ConvertFrom-Json) } else { return $null }
}

# A command-line argument in quotes. A trailing backslash would escape the closing quote, so it is doubled ("M:\\" reads as M:\).
function Quote-Arg([string] $Value) { if ($Value.EndsWith('\')) { $Value += '\' }; return '"' + $Value + '"' }

# Holds the root handle on $Path while $Action runs, then reads E2 and E3. Returns the hold tool's output and the action's own output.
function Invoke-Hold([string] $Name, [string] $Path, [scriptblock] $Action, [int] $TimeoutSec = 90) {
    $outFile = Join-Path $Work "$Name.out.txt"
    $errFile = Join-Path $Work "$Name.err.txt"
    $release = Join-Path $Work "$Name.release"
    Remove-Item -LiteralPath $release -ErrorAction SilentlyContinue
    $argList = @((Quote-Arg $TestDll), '--identity-hold', (Quote-Arg $Path), '--release-file', (Quote-Arg $release), '--timeout', $TimeoutSec, '--label', (Quote-Arg $Name))
    $process = Start-Process -FilePath $Dotnet -ArgumentList $argList -RedirectStandardOutput $outFile -RedirectStandardError $errFile -PassThru -NoNewWindow
    $deadline = (Get-Date).AddSeconds(60)
    $held = $false
    while ((Get-Date) -lt $deadline -and -not $held) {
        Start-Sleep -Milliseconds 250
        if (Test-Path -LiteralPath $outFile) { $held = [bool](Select-String -LiteralPath $outFile -Pattern '^HELD' -Quiet) }
    }
    $actionOutput = ''
    if ($held) { try { $actionOutput = (& $Action 2>&1 | Out-String) } catch { $actionOutput = 'ACTION FAILED: ' + $_.Exception.Message } }
    else { $actionOutput = 'the handle was never reported held; the action was not performed' }
    New-Item -ItemType File -Path $release -Force | Out-Null
    [void]$process.WaitForExit(($TimeoutSec + 30) * 1000)
    $tool = if (Test-Path -LiteralPath $outFile) { Get-Content -LiteralPath $outFile -Raw } else { '' }
    $err = if (Test-Path -LiteralPath $errFile) { Get-Content -LiteralPath $errFile -Raw } else { '' }
    return [pscustomobject]@{ Tool = ($tool + $err); Action = $actionOutput }
}

function Add-HoldResult([string] $Title, [string] $Setup, $Result) {
    Add-Md "#### $Title"
    Add-Md
    if ($Setup) { Add-Md $Setup; Add-Md }
    Add-Md '*What was done while the handle was held:*'
    Add-Fence $Result.Action
    Add-Md '*What the product recorded:*'
    Add-Fence (($Result.Tool -split "`r?`n" | Where-Object { $_ -match '^(E0|E1|E2|E3|RESULT=|HELD|DONE|  )' -or $_ -match 'threw|no release' }) -join "`n")
}

function Get-FreeLetter {
    $used = [IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name[0] }
    foreach ($c in 'Z','Y','X','W','V','U','T','S','R','Q','P') { if ($used -notcontains $c -and -not (Test-Path "$($c):\")) { return $c } }
    return $null
}

# ---------------------------------------------------------------- header
Add-Md '# StorageInventory v1.1 C3: identity experiments'
Add-Md
Add-Md ('Generated by `tests/identity/Invoke-IdentityExperiments.ps1` on {0:u}. OS {1}; elevated: **{2}**; provisioned media manifest: **{3}**.' -f (Get-Date).ToUniversalTime(), [Environment]::OSVersion.VersionString, $elevated, [bool]$media)
Add-Md
if ($media -and $media.notes) { Add-Md '*Media that could not be provisioned:*'; Add-Fence (($media.notes | ConvertTo-Json)); }

# ---------------------------------------------------------------- Part 1: matrix (Q-02, Q-13)
Add-Md '## Part 1: what each source type returns (Q-02, Q-13)'
Add-Md
Add-Md 'Each block is one run of the product''s probe: the raw items of one reading, the confidence the matching rules give, which ID-13 items are available, and the result of an immediate E0..E3 cycle with nothing changed.'
Add-Md

$targets = New-Object System.Collections.Generic.List[object]
foreach ($d in [IO.DriveInfo]::GetDrives()) {
    try { if ($d.IsReady -and $d.DriveType -in 'Fixed', 'Removable', 'CDRom') { $targets.Add(@{ Path = $d.Name; Label = "$($d.DriveType) $($d.DriveFormat) drive" }) } } catch { }
}
$systemFolder = Join-Path $env:SystemRoot 'System32'
$targets.Add(@{ Path = $systemFolder; Label = 'a subfolder source (not a whole volume)' })

$subst = $null
$substA = Join-Path $Work 'SubstTarget\Media'
New-Item -ItemType Directory -Force (Join-Path $substA 'Music') | Out-Null
$letter = Get-FreeLetter
if ($letter) {
    & subst.exe "$($letter):" $substA
    if (Test-Path "$($letter):\") { $subst = $letter; $targets.Add(@{ Path = "$($letter):\"; Label = 'a SUBST letter for a folder' }) }
}

$loopback = $null
if ($IncludeLoopbackAdminShare) {
    $loopLetter = Get-FreeLetter
    if ($loopLetter) {
        & net.exe use "$($loopLetter):" '\\localhost\C$' /persistent:no 2>&1 | Out-Null
        if (Test-Path "$($loopLetter):\") { $loopback = $loopLetter; $targets.Add(@{ Path = "$($loopLetter):\"; Label = 'SMB (Windows server, loopback admin share) through a mapped letter' }) }
    }
    $targets.Add(@{ Path = '\\localhost\C$'; Label = 'SMB (Windows server, loopback admin share) by UNC' })
    $targets.Add(@{ Path = '\\127.0.0.1\C$'; Label = 'the same share by another server spelling (a different source: no names are resolved)' })
}

if ($media) {
    foreach ($entry in @(
            @('ntfsA', 'NTFS on a VHDX (second NTFS volume)'), @('ntfsB', 'NTFS on a second VHDX'), @('fat32', 'FAT32 on a VHDX'), @('exfat', 'exFAT on a VHDX'),
            @('refs', 'ReFS on a VHDX'), @('udf', 'UDF (mounted ISO)'), @('mappedLetterA', 'SMB (Windows server) share on NTFS, through a mapped letter'),
            @('mappedLetterB', 'SMB (Windows server) share served from a FAT32 volume, through a mapped letter'), @('smbShareA', 'SMB share on NTFS by UNC'), @('smbShareB', 'SMB share on FAT32 by UNC'))) {
        $path = $media.($entry[0])
        if ($path) {
            $targets.Add(@{ Path = $path; Label = $entry[1] })
            if ($entry[0] -in 'smbShareA', 'smbShareB', 'mappedLetterA') { $targets.Add(@{ Path = ($path.TrimEnd('\') + '\Media\Music'); Label = $entry[1] + ', a folder inside the share' }) }
        }
    }
}
else { Add-NotRun 'provisioned media (FAT32, exFAT, ReFS, a second VHDX NTFS volume, a UDF image, SMB shares)' 'No manifest was given. Run `Provision-IdentityMedia.ps1` from an elevated shell and pass `-Manifest`, or run the manual checklist in `tests/identity/README.md` with real media.' }

$matrixRows = New-Object System.Collections.Generic.List[string]
foreach ($t in $targets) {
    $text = Invoke-Probe @($t.Path) $t.Label $null
    Add-Md ('<details><summary>{0}: <code>{1}</code></summary>' -f $t.Label, $t.Path)
    Add-Md
    Add-Md ($text -replace '(?s)\r?\n### Matrix.*$', '')
    Add-Md
    Add-Md '</details>'
    Add-Md
    $lines = $text -split "`r?`n"
    $header = [Array]::FindIndex($lines, [Predicate[string]]{ param($l) $l -like '| Source | Input path |*' })
    if ($header -ge 0) { foreach ($l in $lines[($header + 2)..($lines.Length - 1)]) { if ($l.StartsWith('|')) { $matrixRows.Add($l) } } }
}
Add-Md '### Matrix'
Add-Md
Add-Md '| Source | Input path | Open | Filesystem | Serial32 | Serial64 | Root file ID | Label | Capacity / free | Kind | Confidence | FileIdInfo | Save |'
Add-Md '|---|---|---|---|---|---|---|---|---|---|---|---|---|'
foreach ($r in $matrixRows) { Add-Md $r }
Add-Md

# ---------------------------------------------------------------- Part 2: TEST-I2 drive letter change
Add-Md '## Part 2: a drive-letter change does not change the identity (TEST-I2)'
Add-Md
if ($elevated -and $media -and $media.ntfsA) {
    $oldLetter = $media.ntfsA.Substring(0, 1)
    $newLetter = Get-FreeLetter
    $before = Get-ProbeJson $media.ntfsA 'letter before'
    try {
        Get-Partition -DriveLetter $oldLetter | Set-Partition -NewDriveLetter $newLetter
        $after = Get-ProbeJson "$($newLetter):\" 'letter after'
        Get-Partition -DriveLetter $newLetter | Set-Partition -NewDriveLetter $oldLetter
    }
    catch { $after = $null; Add-Md ('Changing the letter failed: `{0}`' -f $_.Exception.Message) }
    if ($before -and $after) {
        $same = ($before.fileSystem -eq $after.fileSystem) -and ($before.serial32 -eq $after.serial32) -and ($before.serial64 -eq $after.serial64) -and ($before.confidence -eq $after.confidence) -and ($before.rootInVolume -eq $after.rootInVolume)
        Add-Md ('| | letter | filesystem | serial32 | serial64 | confidence | root in volume |')
        Add-Md '|---|---|---|---|---|---|---|'
        Add-Md ('| before | `{0}:` | {1} | {2} | {3} | {4} | `{5}` |' -f $oldLetter, $before.fileSystem, $before.serial32, $before.serial64, $before.confidence, $before.rootInVolume)
        Add-Md ('| after | `{0}:` | {1} | {2} | {3} | {4} | `{5}` |' -f $newLetter, $after.fileSystem, $after.serial32, $after.serial64, $after.confidence, $after.rootInVolume)
        Add-Md
        Add-Md ('**Same identity at both letters: {0}.** The matcher takes no drive letter as input, so a Strong volume at either letter is the same source (unit-tested in `MatchingTests`).' -f $(if ($same) { 'YES' } else { 'NO' }))
    }
}
else { Add-NotRun 'TEST-I2 (drive-letter change)' 'Changing the letter of a mounted volume needs an elevated session and the provisioned VHDX volumes (see Provision-IdentityMedia.ps1). Manual form: README step 2 with a USB stick or any second volume.' }
Add-Md

# ---------------------------------------------------------------- Part 3: hold experiments (Q-19, TEST-I5, TEST-I6)
Add-Md '## Part 3: holding the root handle (Q-19, TEST-I5, TEST-I6)'
Add-Md
Add-Md 'Each experiment starts the probe in hold mode (E0, then the handle is opened on the path and HELD, E1), performs the action, then lets the probe read E2 through the HELD handle and E3 through a FRESH open of the same path and evaluate E0 = E1 = E2 = E3.'
Add-Md

# 3a/3b SUBST
if ($letter) {
    $a = Join-Path $Work 'A'; $b = Join-Path $Work 'B'
    New-Item -ItemType Directory -Force (Join-Path $a 'sub'), (Join-Path $b 'sub') | Out-Null
    $l = Get-FreeLetter
    & subst.exe "$($l):" $a
    $r = Invoke-Hold 'subst-repoint' "$($l):\" { & subst.exe "$($l):" /D; & subst.exe "$($l):" $b; "subst $($l): re-pointed from A to B" }
    & subst.exe "$($l):" /D 2>&1 | Out-Null
    Add-HoldResult 'SUBST letter re-pointed while held' "``$($l):`` is a SUBST letter for folder A; it is re-pointed to folder B." $r

    & subst.exe "$($l):" $a
    $r = Invoke-Hold 'subst-away-and-back' "$($l):\" { & subst.exe "$($l):" /D; & subst.exe "$($l):" $b; & subst.exe "$($l):" /D; & subst.exe "$($l):" $a; "subst $($l): re-pointed to B and back to A" }
    & subst.exe "$($l):" /D 2>&1 | Out-Null
    Add-HoldResult 'SUBST letter re-pointed away and back while held (limitation L-ID2)' 'The same letter is re-pointed to B and then back to A before the window closes. The specification documents this as undetectable.' $r
}
else { Add-NotRun 'SUBST experiments' 'No free drive letter.' }

# 3c source folder renamed / deleted by someone else
$renameSource = Join-Path $Work 'RenameMe'
New-Item -ItemType Directory -Force (Join-Path $renameSource 'inner') | Out-Null
$r = Invoke-Hold 'folder-rename' $renameSource { Rename-Item -LiteralPath $renameSource -NewName 'RenameMe2' -ErrorAction Stop; 'renamed RenameMe to RenameMe2' }
Add-HoldResult 'Source folder renamed while held' 'A plain folder, renamed from outside while the handle is held (the held handle does not block it).' $r

# 3d volume lock with and without the held handle (needs an elevated session and a virtual volume)
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class SiVolumeLock {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string f, uint a, uint s, IntPtr sa, uint d, uint fl, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr i, uint il, IntPtr o, uint ol, out uint ret, IntPtr ov);
    // What "Safely remove" and Eject begin with: open the volume and ask the filesystem to lock it (FSCTL_LOCK_VOLUME).
    public static string TryLock(string letter) {
        using (SafeFileHandle h = CreateFileW(@"\\.\" + letter + ":", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
            if (h.IsInvalid) return "could not open the volume: Win32 " + Marshal.GetLastWin32Error();
            uint ret; if (DeviceIoControl(h, 0x00090018, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero)) { DeviceIoControl(h, 0x0009001C, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero); return "LOCKED (and unlocked again)"; }
            return "lock REFUSED: Win32 " + Marshal.GetLastWin32Error();
        }
    }
}
'@ -ErrorAction SilentlyContinue
if ($elevated -and $media -and $media.ntfsA) {
    $vol = $media.ntfsA.Substring(0, 1)
    # a background scanner may hold the new volume for a moment, so the baseline is the first success within a few seconds
    $baselineLock = ''
    for ($try = 0; $try -lt 6; $try++) { $baselineLock = [SiVolumeLock]::TryLock($vol); if ($baselineLock -like 'LOCKED*') { break }; Start-Sleep -Seconds 1 }
    $r = Invoke-Hold 'volume-lock' $media.ntfsA { 'lock attempt while the root handle is held: ' + [SiVolumeLock]::TryLock($vol) }
    $afterLock = [SiVolumeLock]::TryLock($vol)
    Add-HoldResult 'Orderly dismount (volume lock) while the handle is held' ('A volume lock is the first step of "Safely remove" and Eject. Before the hold: **{0}**. After the hold ended: **{1}**.' -f $baselineLock, $afterLock) $r
}
else { Add-NotRun 'volume lock with the handle held' 'Opening a volume for locking needs an elevated session and a virtual volume that is safe to lock (provision with Provision-IdentityMedia.ps1). For real removable media, use the manual "Safely remove" step in README.' }

# 3e surprise removal and a swapped medium (needs virtual disks)
if ($elevated -and $media -and $media.ntfsA -and $media.ntfsB) {
    $root = $media.root
    $fileA = Join-Path $root 'ntfsA.vhdx'; $fileB = Join-Path $root 'ntfsB.vhdx'
    $letterA = $media.ntfsA.Substring(0, 1)
    function Invoke-DiskpartLines([string[]] $Lines) { $s = Join-Path $Work 'dp.txt'; Set-Content -LiteralPath $s -Value $Lines -Encoding ASCII; (& diskpart.exe /s $s 2>&1 | Out-String) }

    $r = Invoke-Hold 'surprise-removal' $media.ntfsA { Invoke-DiskpartLines @("select vdisk file=`"$fileA`"", 'detach vdisk') }
    Add-HoldResult 'Medium removed (virtual disk detached) while held' 'The virtual disk is detached with no warning: the equivalent of pulling a USB stick. Its drive letter disappears.' $r
    Invoke-DiskpartLines @("select vdisk file=`"$fileA`"", 'attach vdisk', 'select partition 1', "assign letter=$letterA noerr") | Out-Null

    $r = Invoke-Hold 'medium-swapped' $media.ntfsA {
        Invoke-DiskpartLines @("select vdisk file=`"$fileA`"", 'detach vdisk') | Out-Null
        Invoke-DiskpartLines @("select vdisk file=`"$fileB`"", 'attach vdisk', 'select partition 1', "assign letter=$letterA noerr")
    }
    Add-HoldResult 'Another medium appears at the same drive letter while held' 'Volume A is detached and volume B (another serial) is attached at the same letter.' $r
    Invoke-DiskpartLines @("select vdisk file=`"$fileB`"", 'detach vdisk') | Out-Null
    Invoke-DiskpartLines @("select vdisk file=`"$fileA`"", 'attach vdisk', 'select partition 1', "assign letter=$letterA noerr") | Out-Null
    Invoke-DiskpartLines @("select vdisk file=`"$fileB`"", 'attach vdisk') | Out-Null
}
else { Add-NotRun 'surprise removal and a swapped medium' 'Needs an elevated session and the provisioned VHDX volumes. Manual form for real media: README step 5.' }

# 3f UDF medium dismounted while held
if ($elevated -and $media -and $media.udf) {
    $isoPath = Join-Path $media.root 'si_udf.iso'
    $r = Invoke-Hold 'udf-dismount' $media.udf { Dismount-DiskImage -ImagePath $isoPath | Out-String }
    Add-HoldResult 'UDF disc image dismounted (ejected) while held' 'The mounted ISO is dismounted, which is what Eject does to a disc.' $r
}
else { Add-NotRun 'UDF eject while held' 'Needs the provisioned UDF image (elevated manifest run). Manual form: README step 5 with a real disc.' }

# 3g mapped drive / SMB: a re-pointed letter, a disconnect with the handle held, a share root renamed by another client
$shareA = $null; $shareB = $null; $mapLetter = $null
if ($media -and $media.smbShareA -and $media.smbShareB -and $media.mappedLetterA) { $shareA = $media.smbShareA; $shareB = $media.smbShareB; $mapLetter = $media.mappedLetterA.Substring(0, 1) }
elseif ($loopback) {
    $others = [IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady -and $_.Name[0] -ne 'C' } | Select-Object -First 1
    if ($others) { $shareA = '\\localhost\C$'; $shareB = "\\localhost\$($others.Name[0])`$"; $mapLetter = $loopback }
}
if ($shareA -and $mapLetter) {
    & net.exe use "$($mapLetter):" /delete /y 2>&1 | Out-Null
    & net.exe use "$($mapLetter):" $shareA /persistent:no 2>&1 | Out-Null
    $r = Invoke-Hold 'mapped-repoint' "$($mapLetter):\" {
        (& net.exe use "$($mapLetter):" /delete /y 2>&1 | Out-String)
        (& net.exe use "$($mapLetter):" $shareB /persistent:no 2>&1 | Out-String)
    }
    Add-HoldResult 'Mapped network drive re-pointed to another share while held' "``$($mapLetter):`` is mapped to ``$shareA`` and is forcibly disconnected and re-mapped to ``$shareB``." $r

    & net.exe use "$($mapLetter):" /delete /y 2>&1 | Out-Null
    & net.exe use "$($mapLetter):" $shareA /persistent:no 2>&1 | Out-Null
    $r = Invoke-Hold 'mapped-disconnect-blocked' "$($mapLetter):\" { 'disconnect WITHOUT force while the handle is held (answering N to the prompt):'; (cmd.exe /c "echo n| net use $($mapLetter): /delete 2>&1" | Out-String) }
    $after = (cmd.exe /c "net use $($mapLetter): /delete 2>&1" | Out-String)
    Add-HoldResult 'Mapped drive disconnected (not forced) while held' ('A normal disconnect while a handle is open asks for confirmation, as it does for any open file. After the hold ended the same command gave: `{0}`' -f ($after -replace '\s+', ' ').Trim()) $r
}
else { Add-NotRun 'mapped-drive experiments' 'No SMB share to map: pass a manifest from an elevated Provision-IdentityMedia.ps1 run, or use -IncludeLoopbackAdminShare. Manual form against a Windows server and a Samba server: README step 6.' }

if ($media -and $media.smbShareA) {
    $unc = $media.smbShareA.TrimEnd('\') + '\Media'
    $backing = Join-Path (Join-Path $media.ntfsA 'share') 'Media'
    $r = Invoke-Hold 'share-root-renamed' $unc { Rename-Item -LiteralPath $backing -NewName 'Media2' -ErrorAction Stop; 'another client (the server side) renamed Media to Media2' }
    if (Test-Path -LiteralPath (Join-Path (Join-Path $media.ntfsA 'share') 'Media2')) { Rename-Item -LiteralPath (Join-Path (Join-Path $media.ntfsA 'share') 'Media2') -NewName 'Media' }
    Add-HoldResult 'Network share folder renamed by another client while held' ('The source is `{0}`.' -f $unc) $r
}
elseif ($IncludeLoopbackAdminShare) {
    $served = Join-Path $Work 'Shared\Media'
    New-Item -ItemType Directory -Force (Join-Path $served 'Music') | Out-Null
    $unc = '\\localhost\C$' + ($served.Substring(2))
    if ($served.Substring(0, 2) -eq 'C:') {
        $r = Invoke-Hold 'share-root-renamed' $unc { Rename-Item -LiteralPath $served -NewName 'Media2' -ErrorAction Stop; 'another client (the local filesystem) renamed Media to Media2' }
        Add-HoldResult 'Network share folder renamed by another client while held (loopback admin share)' ('The source is `{0}`.' -f $unc) $r
    }
}

# ---------------------------------------------------------------- cleanup and output
if ($subst) { & subst.exe "$($subst):" /D 2>&1 | Out-Null }
if ($loopback) { & net.exe use "$($loopback):" /delete /y 2>&1 | Out-Null }
try { [IO.Directory]::Delete($Work, $true) } catch { }
Set-Content -LiteralPath $Out -Value ($md -join "`n") -Encoding UTF8
"wrote $Out"
