<#
.SYNOPSIS
    Runs the v1.1 C3 platform experiments (Q-02, Q-13, Q-19; manual TEST-I2, TEST-I4, TEST-I6) through the PRODUCT's own capture code and
    writes the results as Markdown.
.DESCRIPTION
    Every reading comes from `StorageInventory.IntegrationTests --identity-probe` / `--identity-hold`, which run Core's evidence capture,
    History's classification and History's E0..E3 re-verification, so the evidence describes the shipped logic, not a second implementation.

    Needs a Release build of the solution (`.\build.ps1 -Target Build -Configuration Release`). Runs without administrator rights; the
    parts that need them (virtual disks, SMB shares, locking a volume) run only when this session is elevated AND a manifest from
    Provision-IdentityMedia.ps1 is given, and are otherwise reported as NOT RUN with the reason. The probe and the hold commands
    only open and query the sources; the experiments themselves do change the world on purpose (they re-point a SUBST letter, rename
    and delete scratch folders, lock, detach and re-letter the PROVISIONED volumes, and rename the folder a provisioned share serves),
    which is why they run only against scratch folders and media this tooling made.

    Folders and files (C3 repair, finding C3-H01):
      -Work is the folder the scratch folder is created IN. The script makes a NEW, uniquely named child of it (SiIdentityWork_<8 hex>),
           marks it, works only inside it, and at the end deletes only that child, and only after re-checking the marker. Whatever
           -Work already holds is never read, changed or deleted, so pointing it at an existing folder (or $env:TEMP, or .) is harmless.
           A filesystem root, a path shorter than 8 characters or a file is refused.
      -Out  is refused when it is a folder, when its folder does not exist, or when the file already exists, unless -Overwrite is given.
      -CheckArgumentsOnly validates -Work and -Out, creates and removes the scratch folder, and stops (no build, no elevation, no
           experiment); tests\identity\Test-IdentityScriptSafety.ps1 uses it to prove the guards.

    What it covers:
      Part 1  Q-02 and Q-13: a matrix over every drive, a subfolder, a SUBST letter, UNC paths and the provisioned media
      Part 2  TEST-I2: the same volume at a second drive letter has the same identity
      Part 3  Q-19 and TEST-I5/I6: hold the root handle, change the world, read E2 and E3 - SUBST re-point (and away-and-back),
              the source folder and its PARENT and GRANDPARENT renamed (with the open-listing control), surprise removal and a
              swapped medium, a volume lock with and without the held handle, a re-pointed mapped drive, a disconnect with the
              handle held, a share's backing folder renamed on the server's own filesystem (a loopback server, not another client)
.EXAMPLE
    .\tests\identity\Invoke-IdentityExperiments.ps1 -Out C:\temp\identity.md
    .\tests\identity\Invoke-IdentityExperiments.ps1 -Manifest $env:TEMP\identity-media.json -Out C:\temp\identity-full.md
#>
param(
    [string] $Manifest,
    [string] $Out = (Join-Path ([IO.Path]::GetTempPath()) ('identity-experiments_' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '_' + [guid]::NewGuid().ToString('N').Substring(0, 4) + '.md')),
    [string] $Work = [IO.Path]::GetTempPath(),
    [string] $Dotnet,
    [string] $TestDll,
    [switch] $IncludeLoopbackAdminShare,
    [switch] $Overwrite,
    [switch] $CheckArgumentsOnly
)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'IdentitySafety.ps1')

# C3-H01: every argument that names a folder or file is validated BEFORE anything is built, started or created.
$ScratchPrefix = 'SiIdentityWork_'
$Out = Resolve-OutputFile $Out -Overwrite:$Overwrite
$WorkParent = Resolve-WorkParent $Work
if ($CheckArgumentsOnly) {
    $child = New-ScratchChild $WorkParent $ScratchPrefix
    $removedChild = Remove-OwnedScratch $child $WorkParent $ScratchPrefix
    "arguments ok: out=$Out; scratch folder $child created inside $WorkParent and removed=$removedChild"
    return
}
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
$Work = New-ScratchChild $WorkParent $ScratchPrefix   # a new folder of ours; the only folder this script will delete
try {

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
function Invoke-Hold([string] $Name, [string] $Path, [scriptblock] $Action, [int] $TimeoutSec = 90, [string] $Mode = 'hold') {
    $outFile = Join-Path $Work "$Name.out.txt"
    $errFile = Join-Path $Work "$Name.err.txt"
    $release = Join-Path $Work "$Name.release"
    Remove-Item -LiteralPath $release -ErrorAction SilentlyContinue
    # Mode 'enumerate' is the CONTROL: no identity handle, only a folder listing left open, which is what a scan always has
    $flag = if ($Mode -eq 'enumerate') { '--identity-enumerate' } else { '--identity-hold' }
    $argList = @((Quote-Arg $TestDll), $flag, (Quote-Arg $Path), '--release-file', (Quote-Arg $release), '--timeout', $TimeoutSec, '--label', (Quote-Arg $Name))
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
    Add-Fence (($Result.Tool -split "`r?`n" | Where-Object { $_ -match '^(E0|E1|E2|E3|RESULT=|HELD|DONE|ENUMERATING|  )' -or $_ -match 'threw|no release' }) -join "`n")
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
$manifestRoots = @(); if ($media) { $manifestRoots = @($media.ntfsA, $media.ntfsB, $media.fat32, $media.exfat, $media.refs, $media.udf | Where-Object { $_ }) }
foreach ($d in [IO.DriveInfo]::GetDrives()) {
    try { if ($d.IsReady -and $d.DriveType -in 'Fixed', 'Removable', 'CDRom' -and $manifestRoots -notcontains $d.Name) { $targets.Add(@{ Path = $d.Name; Label = "$($d.DriveType) $($d.DriveFormat) drive" }) } } catch { }
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

# ---------------------------------------------------------------- Part 1b: access or support?
Add-Md '### Is a failed `FileIdInfo` about access or about support?'
Add-Md
Add-Md 'The gate stops if any identity call needs MORE than zero access. This asks the same two calls through a handle opened with progressively richer access rights, and prints the access mask each handle was actually granted (`0x00100080` is `SYNCHRONIZE` plus `FILE_READ_ATTRIBUTES`, which is all a desired access of 0 carries). If the answer never changes with the access requested, a failure is the filesystem not implementing the query (Win32 87), not a missing right. (This probe is test tooling; the product always opens with access 0.)'
Add-Md
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class SiAccessProbe {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string f, uint a, uint s, IntPtr sa, uint d, uint fl, IntPtr t);
    [StructLayout(LayoutKind.Sequential)] struct FILE_ID_INFO { public ulong Serial; public ulong Lo; public ulong Hi; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_ID_INFO info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetVolumeInformationByHandleW(SafeFileHandle h, char[] vn, uint vns, out uint serial, out uint max, out uint flags, char[] fs, uint fss);
    [StructLayout(LayoutKind.Sequential)] struct IO_STATUS_BLOCK { public IntPtr Status; public IntPtr Information; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationFile(SafeFileHandle h, out IO_STATUS_BLOCK iosb, out uint info, uint length, int cls);
    public static string Try(string root, uint access) {
        using (SafeFileHandle h = CreateFileW(@"\\?\" + root, access, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
            if (h.IsInvalid) return "open failed, Win32 " + Marshal.GetLastWin32Error();
            // the access mask the opened handle actually carries (FileAccessInformation = 8): 0x00100080 is SYNCHRONIZE | FILE_READ_ATTRIBUTES
            uint mask; IO_STATUS_BLOCK iosb; string g = NtQueryInformationFile(h, out iosb, out mask, 4, 8) == 0 ? "granted 0x" + mask.ToString("X8") : "granted unknown";
            FILE_ID_INFO i; string a = GetFileInformationByHandleEx(h, 18, out i, 24) ? "FileIdInfo OK" : "FileIdInfo FAILED, Win32 " + Marshal.GetLastWin32Error();
            uint s, m, f; char[] vn = new char[261], fs = new char[261];
            string b = GetVolumeInformationByHandleW(h, vn, 261, out s, out m, out f, fs, 261) ? "VolumeInformation OK" : "VolumeInformation FAILED, Win32 " + Marshal.GetLastWin32Error();
            return g + "; " + a + "; " + b;
        }
    }
}
'@ -ErrorAction SilentlyContinue
Add-Md '| Volume | Access 0 (the product) | FILE_READ_ATTRIBUTES (0x80) | GENERIC_READ (0x80000000) |'
Add-Md '|---|---|---|---|'
foreach ($candidate in $targets) {
    if ($candidate.Path -match '^[A-Za-z]:\\$') {
        $cells = foreach ($access in [uint32]0, [uint32]0x80, [uint32]2147483648) { [SiAccessProbe]::Try($candidate.Path, $access) }
        Add-Md ('| {0} `{1}` | {2} |' -f $candidate.Label, $candidate.Path, ($cells -join ' | '))
    }
}
Add-Md

# ---------------------------------------------------------------- Part 2: TEST-I2 drive letter change
Add-Md '## Part 2: a drive-letter change does not change the identity (TEST-I2)'
Add-Md
if ($elevated -and $media) {
    # volumes that back no SMB share, so changing a letter disturbs nothing else: a Strong NTFS volume, a Moderate exFAT volume, a Strong ReFS volume
    Add-Md '| volume | letter | filesystem | serial32 | serial64 | confidence | root in volume |'
    Add-Md '|---|---|---|---|---|---|---|'
    $verdicts = New-Object System.Collections.Generic.List[string]
    foreach ($pair in @(@('ntfsB', 'NTFS (Strong)'), @('exfat', 'exFAT (Moderate)'), @('refs', 'ReFS (Strong)'))) {
        $path = $media.($pair[0])
        if (-not $path) { $verdicts.Add("$($pair[1]): NOT RUN, the volume was not provisioned"); continue }
        $oldLetter = $path.Substring(0, 1)
        $newLetter = Get-FreeLetter
        $before = Get-ProbeJson $path 'letter before'
        $after = $null
        try {
            Get-Partition -DriveLetter $oldLetter -ErrorAction Stop | Set-Partition -NewDriveLetter $newLetter -ErrorAction Stop
            $after = Get-ProbeJson "$($newLetter):\" 'letter after'
            Get-Partition -DriveLetter $newLetter -ErrorAction Stop | Set-Partition -NewDriveLetter $oldLetter -ErrorAction Stop
        }
        catch { $verdicts.Add("$($pair[1]): changing the letter failed: $($_.Exception.Message)") }
        if ($before -and $after) {
            $same = ($before.fileSystem -eq $after.fileSystem) -and ($before.serial32 -eq $after.serial32) -and ($before.serial64 -eq $after.serial64) -and ($before.confidence -eq $after.confidence) -and ($before.rootInVolume -eq $after.rootInVolume)
            Add-Md ('| {0} | `{1}:` (before) | {2} | {3} | {4} | {5} | `{6}` |' -f $pair[1], $oldLetter, $before.fileSystem, $before.serial32, $before.serial64, $before.confidence, $before.rootInVolume)
            Add-Md ('| {0} | `{1}:` (after) | {2} | {3} | {4} | {5} | `{6}` |' -f $pair[1], $newLetter, $after.fileSystem, $after.serial32, $after.serial64, $after.confidence, $after.rootInVolume)
            $verdicts.Add(('{0}: same identity at both letters: **{1}**' -f $pair[1], $(if ($same) { 'YES' } else { 'NO' })))
        }
    }
    Add-Md
    foreach ($v in $verdicts) { Add-Md "* $v" }
    Add-Md
    Add-Md 'The matcher takes no drive letter as input, so a Strong volume, or a Moderate one that also corroborates, at either letter is the same source (unit-tested in `MatchingTests`; this proves the evidence it is given really is letter-independent).'
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

# 3c2 renaming a PARENT or an ANCESTOR of the source while the handle is held (Q-19, C3-M04), with the control: an ordinary open
# folder listing, which a scan has throughout its enumeration. The source folder is A\B\C; Windows refuses to rename a folder that
# has an open handle anywhere below it, so the expectation is BLOCKED for A\B and A and allowed for C, for both the held handle and the control.
foreach ($mode in 'hold', 'enumerate') {
    $base = Join-Path $Work "Ancestor_$mode"
    $ancestorSource = Join-Path $base 'A\B\C'
    New-Item -ItemType Directory -Force $ancestorSource | Out-Null
    1..3 | ForEach-Object { Set-Content -LiteralPath (Join-Path $ancestorSource "f$_.txt") -Value 'x' }   # the control's listing must stay open part-way
    $renameAttempts = @(
        @{ What = 'rename the PARENT of the source (A\B -> B2)'; Path = (Join-Path $base 'A\B'); New = 'B2' },
        @{ What = 'rename the GRANDPARENT of the source (A -> A2)'; Path = (Join-Path $base 'A'); New = 'A2' },
        @{ What = 'rename the SOURCE itself (C -> C2)'; Path = $ancestorSource; New = 'C2' })
    $r = Invoke-Hold "ancestor-rename-$mode" $ancestorSource {
        foreach ($attempt in $renameAttempts) {
            try { Rename-Item -LiteralPath $attempt.Path -NewName $attempt.New -ErrorAction Stop; "$($attempt.What): ALLOWED"; Rename-Item -LiteralPath (Join-Path (Split-Path -Parent $attempt.Path) $attempt.New) -NewName (Split-Path -Leaf $attempt.Path) -ErrorAction Stop }
            catch { "$($attempt.What): BLOCKED ($($_.Exception.Message))" }
        }
    } -Mode $mode
    if ($mode -eq 'hold') { Add-HoldResult 'Parent and grandparent of the source renamed while the handle is held (C3-M04)' 'The identity handle is held on `A\B\C`; three renames are attempted from outside, each undone if it succeeded.' $r }
    else { Add-HoldResult 'CONTROL: the same renames while an ordinary folder listing is open on `A\B\C` (what a scan does)' 'No identity handle is held here. Compare with the block above: the same renames give the same answers, so the held handle adds no kind of effect that listing the folder does not already have.' $r }
}

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
if ($elevated -and $media -and $media.ntfsB) {
    # ntfsB has no SMB share on it: a share's server would hold its own handle on the volume and refuse every lock
    $lockTarget = $media.ntfsB
    $vol = $lockTarget.Substring(0, 1)
    # a background scanner may hold a new volume for a moment, so the baseline is the first success within a few seconds
    $baselineLock = ''
    for ($try = 0; $try -lt 8; $try++) { $baselineLock = [SiVolumeLock]::TryLock($vol); if ($baselineLock -like 'LOCKED*') { break }; Start-Sleep -Seconds 1 }
    $r = Invoke-Hold 'volume-lock' $lockTarget { 'lock attempt while the root handle is held: ' + [SiVolumeLock]::TryLock($vol) }
    $afterLock = [SiVolumeLock]::TryLock($vol)
    Add-HoldResult 'Orderly dismount (volume lock) while the handle is held' ('A volume lock is the first step of "Safely remove" and Eject. Before the hold: **{0}**. After the hold ended: **{1}**.' -f $baselineLock, $afterLock) $r
}
else { Add-NotRun 'volume lock with the handle held' 'Opening a volume for locking needs an elevated session and a virtual volume that is safe to lock (provision with Provision-IdentityMedia.ps1). For real removable media, use the manual "Safely remove" step in README.' }

# the control: the same lock attempt while a plain folder listing is open, which is what a scan has open while it lists
if ($elevated -and $media -and $media.ntfsB) {
    $controlFolder = Join-Path $media.ntfsB 'scan-control'
    New-Item -ItemType Directory -Force (Join-Path $controlFolder 'a'), (Join-Path $controlFolder 'b') | Out-Null
    $r = Invoke-Hold 'control-listing-lock' $controlFolder { 'lock attempt while a folder listing is open: ' + [SiVolumeLock]::TryLock($vol) } -Mode 'enumerate'
    Add-HoldResult 'CONTROL: the volume lock while an ordinary folder listing is open (what a scan does)' 'No identity handle is held here. A directory enumeration is simply left open on the volume, as it is throughout a scan; this shows whether the held identity handle adds a kind of effect that listing already has.' $r
}

# 3e surprise removal and a swapped medium (needs virtual disks). The storage cmdlets are used (not diskpart's path matching).
function Get-ImageState([string] $File) { $i = Get-DiskImage -ImagePath $File -ErrorAction SilentlyContinue; if ($i) { "Attached=$($i.Attached)" } else { 'image not found' } }
function Set-ImageDetached([string] $File) { Dismount-DiskImage -ImagePath $File -ErrorAction SilentlyContinue | Out-Null; Start-Sleep -Milliseconds 800; Get-ImageState $File }
function Set-ImageAttached([string] $File, [string] $Letter) {
    Mount-DiskImage -ImagePath $File -ErrorAction SilentlyContinue | Out-Null
    Start-Sleep -Milliseconds 800
    $partition = Get-DiskImage -ImagePath $File | Get-Disk | Get-Partition | Where-Object { $_.Type -in 'Basic', 'IFS' } | Select-Object -First 1
    if ($partition -and $partition.DriveLetter -ne $Letter[0]) { try { $partition | Set-Partition -NewDriveLetter $Letter } catch { "could not assign ${Letter}: $($_.Exception.Message)" } }
    Get-ImageState $File
}
if ($elevated -and $media -and $media.ntfsA -and $media.ntfsB) {
    $root = $media.root
    $fileA = Join-Path $root 'ntfsA.vhdx'; $fileB = Join-Path $root 'ntfsB.vhdx'
    $letterA = $media.ntfsA.Substring(0, 1); $letterB = $media.ntfsB.Substring(0, 1)

    $r = Invoke-Hold 'surprise-removal' $media.ntfsA { 'before: ' + (Get-ImageState $fileA); 'detaching the virtual disk with the root handle held: ' + (Set-ImageDetached $fileA) }
    Add-HoldResult 'Medium removed (virtual disk detached) while held' 'The virtual disk is detached with no warning: the equivalent of pulling a USB stick. Its drive letter disappears.' $r
    Set-ImageAttached $fileA $letterA | Out-Null

    $r = Invoke-Hold 'medium-swapped' $media.ntfsA {
        'detaching volume A: ' + (Set-ImageDetached $fileA)
        $partition = Get-Partition -DriveLetter $letterB -ErrorAction Stop
        $partition | Set-Partition -NewDriveLetter $letterA
        "volume B (another serial) now has the letter ${letterA}:"
    }
    Add-HoldResult 'Another medium appears at the same drive letter while held' 'Volume A is detached and volume B (another serial) takes the same letter.' $r
    try { Get-Partition -DriveLetter $letterA -ErrorAction Stop | Set-Partition -NewDriveLetter $letterB } catch { }
    Set-ImageAttached $fileA $letterA | Out-Null
}
else { Add-NotRun 'surprise removal and a swapped medium' 'Needs an elevated session and the provisioned VHDX volumes. Manual form for real media: README step 5.' }
# 3f UDF medium dismounted while held
if ($elevated -and $media -and $media.udf) {
    $isoPath = Join-Path $media.root 'si_udf.iso'
    $r = Invoke-Hold 'udf-dismount' $media.udf { Dismount-DiskImage -ImagePath $isoPath | Out-String }
    Add-HoldResult 'UDF disc image dismounted (Dismount-DiskImage) while held' 'The mounted ISO is dismounted. Dismount-DiskImage takes no volume lock, so this is NOT what Eject does on a physical disc; it shows what the held handle sees when the image goes away.' $r
}
else { Add-NotRun 'UDF eject while held' 'Needs the provisioned UDF image (elevated manifest run). Manual form: README step 5 with a real disc.' }

# 3g mapped drive / SMB: a re-pointed letter, a disconnect with the handle held, a share's backing folder renamed on the server
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

    & net.exe use "$($mapLetter):" $shareA /persistent:no 2>&1 | Out-Null
    $r = Invoke-Hold 'control-listing-disconnect' "$($mapLetter):\" { 'disconnect WITHOUT force while a folder listing is open (answering N to the prompt):'; (cmd.exe /c "echo n| net use $($mapLetter): /delete 2>&1" | Out-String) } -Mode 'enumerate'
    Add-HoldResult 'CONTROL: the same disconnect while an ordinary folder listing is open (what a scan does)' 'No identity handle is held here, only a directory enumeration left open on the share.' $r
}
else { Add-NotRun 'mapped-drive experiments' 'No SMB share to map: pass a manifest from an elevated Provision-IdentityMedia.ps1 run, or use -IncludeLoopbackAdminShare. Manual form against a Windows server and a Samba server: README step 6.' }

if ($media -and $media.smbShareA) {
    $unc = $media.smbShareA.TrimEnd('\') + '\Media'
    $backing = Join-Path (Join-Path $media.ntfsA 'share') 'Media'
    $r = Invoke-Hold 'share-root-renamed' $unc { Rename-Item -LiteralPath $backing -NewName 'Media2' -ErrorAction Stop; 'the server side (the share''s own filesystem) renamed Media to Media2' }
    if (Test-Path -LiteralPath (Join-Path (Join-Path $media.ntfsA 'share') 'Media2')) { Rename-Item -LiteralPath (Join-Path (Join-Path $media.ntfsA 'share') 'Media2') -NewName 'Media' }
    Add-HoldResult 'Network share''s backing folder renamed on the server while held' ('The source is `{0}`.' -f $unc) $r
}
elseif ($IncludeLoopbackAdminShare) {
    $served = Join-Path $Work 'Shared\Media'
    New-Item -ItemType Directory -Force (Join-Path $served 'Music') | Out-Null
    $unc = '\\localhost\C$' + ($served.Substring(2))
    if ($served.Substring(0, 2) -eq 'C:') {
        $r = Invoke-Hold 'share-root-renamed' $unc { Rename-Item -LiteralPath $served -NewName 'Media2' -ErrorAction Stop; 'the local filesystem (the share''s own backing folder) renamed Media to Media2' }
        Add-HoldResult 'Network share''s backing folder renamed on the server while held (loopback admin share)' ('The source is `{0}`.' -f $unc) $r
    }
}

# ---------------------------------------------------------------- cleanup and output
if ($subst) { & subst.exe "$($subst):" /D 2>&1 | Out-Null }
if ($loopback) { & net.exe use "$($loopback):" /delete /y 2>&1 | Out-Null }
Set-Content -LiteralPath $Out -Value ($md -join "`n") -Encoding UTF8
"wrote $Out"
}
finally {
    # deletes the scratch folder this run made (marker re-checked), also when an experiment threw; nothing else
    [void](Remove-OwnedScratch $Work $WorkParent $ScratchPrefix)
}
