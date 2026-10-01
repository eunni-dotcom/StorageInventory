<#
.SYNOPSIS
    Safety fixtures for the two identity test-tooling scripts (C3 repair, findings C3-H01 and C3-M01). Two fixture sets.
.DESCRIPTION
    Set 1 (C3-H01) runs the REAL Invoke-IdentityExperiments.ps1, in a child process, with -CheckArgumentsOnly against hostile
    -Work and -Out values: an existing folder full of data, the temp folder itself, a relative '.', a filesystem root, a file, an
    existing report file and folder. Pass means: either the script refuses (non-zero exit, a reason, nothing changed) or, where it
    is allowed to proceed, every byte that was in the folder before is still there afterwards.

    Set 2 (C3-M01) runs the teardown logic of Provision-IdentityMedia.ps1 (Invoke-MediaTeardown, the code -Remove runs) with
    RECORDING stand-ins for every side effect: Dismount-DiskImage, diskpart detach, net use /delete, Remove-SmbShare, deletion.
    Pass means that for every wrong -Root or -Manifest the recorder is EMPTY (no image was dismounted, no disk detached, no
    mapping or share touched, nothing deleted) and the supplied files are intact; and that for a genuine marked root only the
    things the manifest vouches for are touched, in the right order. This is the property the finding asks for: the checks come
    before the first side effect.

    Runs on any machine with PowerShell 5.1 or 7 (Windows, Linux, macOS): no administrator rights, no diskpart, no SMB. A case
    that cannot run on this machine (creating a symbolic link without the privilege, simulating an unreadable folder as root) is
    reported SKIP with the reason, never silently passed.

    -IncludeRealMedia (Windows, elevated) adds an end-to-end case with a REAL disk: a small VHDX is attached from an unmarked folder,
    the real Provision-IdentityMedia.ps1 -Remove is pointed at that folder and at an unrelated JSON file, and the disk must still be
    attached afterwards and the JSON file must still exist.
.EXAMPLE
    pwsh -NoProfile -File tests\identity\Test-IdentityScriptSafety.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests\identity\Test-IdentityScriptSafety.ps1 -IncludeRealMedia
#>
param([switch] $IncludeRealMedia)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IdentitySafety.ps1')

$HostExe = (Get-Process -Id $PID).Path
$ExperimentsScript = Join-Path $PSScriptRoot 'Invoke-IdentityExperiments.ps1'
$ProvisionScript = Join-Path $PSScriptRoot 'Provision-IdentityMedia.ps1'
$SandboxParent = [IO.Path]::GetTempPath()
$Sandbox = New-ScratchChild $SandboxParent 'SiSafetyFixtures_'
$Results = New-Object System.Collections.Generic.List[object]
$IsWindowsHost = ([IO.Path]::DirectorySeparatorChar -eq '\')

# ------------------------------------------------------------------ plumbing

function Add-Result([string] $Name, [string] $Status, [string] $Detail = '') {
    $Results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    $line = '{0,-4} {1}' -f $Status, $Name
    if ($Detail) { $line += "  -- $Detail" }
    Write-Host $line
}

function Test-Case([string] $Name, [scriptblock] $Body) {
    try { & $Body; Add-Result $Name 'PASS' }
    catch {
        if ($_.Exception.Message -like 'SKIP:*') { Add-Result $Name 'SKIP' $_.Exception.Message.Substring(5).Trim() }
        else { Add-Result $Name 'FAIL' $_.Exception.Message }
    }
}

function Skip([string] $Reason) { throw "SKIP: $Reason" }
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }

function New-Case([string] $Label) {
    $dir = Join-Path $Sandbox ($Label + '_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    [void][IO.Directory]::CreateDirectory($dir)
    return (Get-NormalizedFullPath $dir)
}

# A folder full of "the user's own data": nested, hidden, read-only and empty items.
function New-UserData([string] $Dir) {
    [void][IO.Directory]::CreateDirectory((Join-Path $Dir 'Reports\2025'))
    [void][IO.Directory]::CreateDirectory((Join-Path $Dir 'EmptyFolder'))
    Set-Content -LiteralPath (Join-Path $Dir 'thesis.docx') -Value 'irreplaceable 1'
    Set-Content -LiteralPath (Join-Path $Dir 'Reports\2025\q1.xlsx') -Value 'irreplaceable 2'
    Set-Content -LiteralPath (Join-Path $Dir '.hidden-config') -Value 'irreplaceable 3'
    $readonly = Join-Path $Dir 'readonly.txt'
    Set-Content -LiteralPath $readonly -Value 'irreplaceable 4'
    Set-ItemProperty -LiteralPath $readonly -Name IsReadOnly -Value $true
}

# Every item below $Dir with its type and a content hash, as one string: equal strings mean nothing was added, removed or changed.
function Get-TreeSnapshot([string] $Dir) {
    $base = (Get-NormalizedFullPath $Dir).Length + 1
    $lines = foreach ($item in (Get-ChildItem -LiteralPath $Dir -Recurse -Force)) {
        $relative = $item.FullName.Substring($base)
        if ($item.PSIsContainer) { "D $relative" } else { 'F {0} {1}' -f $relative, (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
    }
    return (($lines | Sort-Object) -join "`n")
}

function Quote-Argument([string] $Value) { return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }

# Runs a script in a child host with its own environment (TEMP/TMPDIR) and working directory.
function Invoke-Child([string] $Script, [string[]] $Arguments, [hashtable] $Environment = @{}, [string] $WorkingDirectory = $Sandbox) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $HostExe
    $all = @('-NoProfile', '-NonInteractive', '-File', $Script) + $Arguments
    $psi.Arguments = ($all | ForEach-Object { Quote-Argument $_ }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $WorkingDirectory
    foreach ($key in $Environment.Keys) { $psi.EnvironmentVariables[$key] = [string]$Environment[$key] }
    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    return [pscustomobject]@{ Code = $process.ExitCode; Output = ($stdout.Result + $stderr.Result) }
}

# ================================================================== SET 1: Invoke-IdentityExperiments.ps1 (C3-H01)

Write-Host "`n=== Fixture set 1: Invoke-IdentityExperiments.ps1 -Work and -Out (C3-H01) ==="

Test-Case '1.01 -Work = an existing folder full of data: refused or left byte-for-byte intact' {
    $case = New-Case 'work-data'; $work = Join-Path $case 'results'; New-UserData $work
    $before = Get-TreeSnapshot $work
    $r = Invoke-Child $ExperimentsScript @('-CheckArgumentsOnly', '-Work', $work, '-Out', (Join-Path $case 'report.md'))
    Check ($r.Code -eq 0) "expected the guarded script to accept it and leave the data alone, exit $($r.Code): $($r.Output)"
    Check ((Get-TreeSnapshot $work) -eq $before) 'the folder the caller supplied changed'
    Check ($r.Output -match 'removed=True') "the scratch folder it made was not removed: $($r.Output)"
}

Test-Case '1.02 -Work = a relative "." inside a data folder: nothing of the folder is touched' {
    $case = New-Case 'work-dot'; $work = Join-Path $case 'results'; New-UserData $work
    $before = Get-TreeSnapshot $work
    $r = Invoke-Child $ExperimentsScript @('-CheckArgumentsOnly', '-Work', '.', '-Out', (Join-Path $case 'report.md')) @{} $work
    Check ($r.Code -eq 0) "exit $($r.Code): $($r.Output)"
    Check ((Get-TreeSnapshot $work) -eq $before) 'the current folder changed'
}

Test-Case '1.03 -Work omitted while the temp folder holds user data: the default is harmless' {
    $case = New-Case 'work-temp'; $temp = Join-Path $case 'temp'; New-UserData $temp
    $before = Get-TreeSnapshot $temp
    $r = Invoke-Child $ExperimentsScript @('-CheckArgumentsOnly', '-Out', (Join-Path $case 'report.md')) @{ TEMP = $temp; TMP = $temp; TMPDIR = $temp }
    Check ($r.Code -eq 0) "exit $($r.Code): $($r.Output)"
    Check ((Get-TreeSnapshot $temp) -eq $before) 'the temp folder changed'
}

Test-Case '1.04 -Work = a filesystem root is refused' {
    $root = [IO.Path]::GetPathRoot($Sandbox)
    $r = Invoke-Child $ExperimentsScript @('-CheckArgumentsOnly', '-Work', $root, '-Out', (Join-Path (New-Case 'work-root') 'report.md'))
    Check ($r.Code -ne 0) "exit $($r.Code): it accepted '$root'"
    Check ($r.Output -match 'too general') "no reason given: $($r.Output)"
}

Test-Case '1.05 -Work = an existing file is refused and the file is intact' {
    $case = New-Case 'work-file'; $file = Join-Path $case 'results.txt'; Set-Content -LiteralPath $file -Value 'keep me'
    $r = Invoke-Child $ExperimentsScript @('-CheckArgumentsOnly', '-Work', $file, '-Out', (Join-Path $case 'report.md'))
    Check ($r.Code -ne 0) "exit $($r.Code)"
    Check ((Get-Content -LiteralPath $file -Raw).Trim() -eq 'keep me') 'the file changed'
}

Test-Case '1.06 -Work = a folder that does not exist: made, a child is made and removed, the new parent stays empty' {
    $case = New-Case 'work-new'; $work = Join-Path $case 'a\b\c'
    $r = Invoke-Child $ExperimentsScript @('-CheckArgumentsOnly', '-Work', $work, '-Out', (Join-Path $case 'report.md'))
    Check ($r.Code -eq 0) "exit $($r.Code): $($r.Output)"
    Check ((Test-Path -LiteralPath $work -PathType Container) -and -not (Get-ChildItem -LiteralPath $work -Force)) 'the new parent should exist and be empty'
}

Test-Case '1.07 -Out = an existing file is refused, the file is intact and no scratch folder was made' {
    $case = New-Case 'out-file'; $work = Join-Path $case 'work'; New-UserData $work; $out = Join-Path $case 'results.csv'; Set-Content -LiteralPath $out -Value 'last quarter'
    $before = Get-TreeSnapshot $work
    $r = Invoke-Child $ExperimentsScript @('-CheckArgumentsOnly', '-Work', $work, '-Out', $out)
    Check ($r.Code -ne 0) "exit $($r.Code): it overwrote an existing file"
    Check ($r.Output -match 'already exists') "no reason given: $($r.Output)"
    Check ((Get-Content -LiteralPath $out -Raw).Trim() -eq 'last quarter') 'the existing file changed'
    Check ((Get-TreeSnapshot $work) -eq $before) 'a scratch folder was created although -Out was refused'
}

Test-Case '1.08 -Out = an existing folder is refused' {
    $case = New-Case 'out-folder'; $out = Join-Path $case 'reports'; New-UserData $out; $before = Get-TreeSnapshot $out
    $r = Invoke-Child $ExperimentsScript @('-CheckArgumentsOnly', '-Work', (Join-Path $case 'work'), '-Out', $out)
    Check ($r.Code -ne 0) "exit $($r.Code)"
    Check ((Get-TreeSnapshot $out) -eq $before) 'the folder changed'
}

Test-Case '1.09 -Out in a folder that does not exist is refused' {
    $case = New-Case 'out-missing'
    $r = Invoke-Child $ExperimentsScript @('-CheckArgumentsOnly', '-Work', (Join-Path $case 'work'), '-Out', (Join-Path $case 'nowhere\report.md'))
    Check ($r.Code -ne 0) "exit $($r.Code)"
    Check ($r.Output -match 'does not exist') "no reason given: $($r.Output)"
}

Test-Case '1.10 -Out = an existing file with -Overwrite is accepted (and is not written by the check)' {
    $case = New-Case 'out-overwrite'; $out = Join-Path $case 'results.md'; Set-Content -LiteralPath $out -Value 'old'
    $r = Invoke-Child $ExperimentsScript @('-CheckArgumentsOnly', '-Overwrite', '-Work', (Join-Path $case 'work'), '-Out', $out)
    Check ($r.Code -eq 0) "exit $($r.Code): $($r.Output)"
    Check ((Get-Content -LiteralPath $out -Raw).Trim() -eq 'old') 'the check wrote the report'
}

Test-Case '1.11 the scratch folder is new every time, inside -Work, and never -Work itself' {
    $case = New-Case 'unique'; $work = Join-Path $case 'work'; [void][IO.Directory]::CreateDirectory($work)
    $names = @(1..5 | ForEach-Object { (New-ScratchChild $work 'SiIdentityWork_') })
    Check (@($names | Select-Object -Unique).Count -eq 5) 'two scratch folders got the same name'
    foreach ($name in $names) { Check ((Test-PathInside $name $work) -and -not (Test-SamePath $name $work)) "'$name' is not a child of -Work" }
}

Test-Case '1.12 the scratch cleanup deletes only a folder that proves it is ours' {
    $case = New-Case 'cleanup'; $parent = Join-Path $case 'parent'; New-UserData $parent
    $prefix = 'SiIdentityWork_'

    # a folder with the right name and the right place but no marker
    $noMarker = Join-Path $parent ($prefix + 'aaaaaaaa'); [void][IO.Directory]::CreateDirectory($noMarker); Set-Content -LiteralPath (Join-Path $noMarker 'data.txt') -Value 'x'
    Check (-not (Remove-OwnedScratch $noMarker $parent $prefix 3>$null)) 'deleted a folder without the marker'
    Check (Test-Path -LiteralPath (Join-Path $noMarker 'data.txt')) 'its contents went missing'

    # a marker with the wrong text
    $wrongText = Join-Path $parent ($prefix + 'bbbbbbbb'); [void][IO.Directory]::CreateDirectory($wrongText); Set-Content -LiteralPath (Join-Path $wrongText '.si-identity-scratch') -Value 'something else'
    Check (-not (Remove-OwnedScratch $wrongText $parent $prefix 3>$null)) 'deleted a folder whose marker is not ours'

    # a made folder with a name the script does not give, and one in the wrong parent
    $made = New-ScratchChild $parent $prefix
    $renamed = Join-Path $parent 'Documents'; Rename-Item -LiteralPath $made -NewName 'Documents'
    Check (-not (Remove-OwnedScratch $renamed $parent $prefix 3>$null)) 'deleted a folder with a name the script never uses'
    $elsewhere = New-ScratchChild (Join-Path $case 'other') $prefix
    Check (-not (Remove-OwnedScratch $elsewhere $parent $prefix 3>$null)) 'deleted a folder that is not directly inside -Work'

    # the -Work folder itself, even carrying a marker, is never "the scratch folder"
    Set-Content -LiteralPath (Join-Path $parent '.si-identity-scratch') -Value 'Made by tests/identity (IdentitySafety.ps1). Safe to delete together with the script that made it.'
    Check (-not (Remove-OwnedScratch $parent $parent $prefix 3>$null)) 'deleted -Work itself'
    Check (Test-Path -LiteralPath (Join-Path $parent 'thesis.docx')) 'the user data in -Work went missing'

    # a genuine one goes, and only it
    $genuine = New-ScratchChild $parent $prefix
    Check (Remove-OwnedScratch $genuine $parent $prefix) 'did not delete its own scratch folder'
    Check (-not (Test-Path -LiteralPath $genuine)) 'its own scratch folder is still there'
}

Test-Case '1.13 the scratch cleanup never deletes through a link' {
    $case = New-Case 'cleanup-link'; $parent = Join-Path $case 'parent'; [void][IO.Directory]::CreateDirectory($parent)
    $target = Join-Path $case 'precious'; New-UserData $target
    # even a target that carries the marker text must be refused when it is reached through a link
    Set-Content -LiteralPath (Join-Path $target '.si-identity-scratch') -Value 'Made by tests/identity (IdentitySafety.ps1). Safe to delete together with the script that made it.'
    $link = Join-Path $parent 'SiIdentityWork_cccccccc'
    try { New-Item -ItemType SymbolicLink -Path $link -Target $target -ErrorAction Stop | Out-Null }
    catch { Skip "cannot create a symbolic link here ($($_.Exception.Message))" }
    $before = Get-TreeSnapshot $target
    Check (-not (Remove-OwnedScratch $link $parent 'SiIdentityWork_' 3>$null)) 'deleted through a link'
    Check ((Get-TreeSnapshot $target) -eq $before) 'the link target changed'
}

# ================================================================== SET 2: Provision-IdentityMedia.ps1 -Remove (C3-M01)

Write-Host "`n=== Fixture set 2: Provision-IdentityMedia.ps1 -Remove teardown order and ownership (C3-M01) ==="

# Recording stand-ins for every side effect. Mapping and share tables say what the machine "has" right now.
function New-Recorder([hashtable] $Mappings = @{}, [hashtable] $Shares = @{}) {
    $log = New-Object System.Collections.Generic.List[string]
    $actions = @{
        GetMapping     = { param($letter) $log.Add("GetMapping $letter") | Out-Null; $Mappings[$letter.Substring(0, 2).ToUpperInvariant()] }.GetNewClosure()
        Unmap          = { param($letter) $log.Add("Unmap $($letter.Substring(0, 2).ToUpperInvariant())") | Out-Null }.GetNewClosure()
        GetShare       = { param($name) $log.Add("GetShare $name") | Out-Null; $Shares[$name] }.GetNewClosure()
        RemoveShare    = { param($name) $log.Add("RemoveShare $name") | Out-Null }.GetNewClosure()
        DismountIso    = { param($file) $log.Add("DismountIso $(Split-Path -Leaf $file)") | Out-Null }.GetNewClosure()
        DetachVhd      = { param($file) $log.Add("DetachVhd $(Split-Path -Leaf $file)") | Out-Null }.GetNewClosure()
        RemoveFolder   = { param($path) $log.Add('RemoveFolder') | Out-Null; Remove-Item -LiteralPath $path -Recurse -Force }.GetNewClosure()
        RemoveManifest = { param($path) $log.Add('RemoveManifest') | Out-Null; Remove-Item -LiteralPath $path -Force }.GetNewClosure()
    }
    return [pscustomobject]@{ Log = $log; Actions = $actions }
}

# The calls that CHANGE something; the Get* calls only look. A refusal must produce none of the former, and none at all of either
# before the root is proven ours.
function Get-Changes($Recorder) { return @($Recorder.Log | Where-Object { $_ -notmatch '^Get' }) }

function New-ImageFiles([string] $Dir, [string[]] $Names = @('ntfsA.vhdx', 'fat32.vhdx', 'si_udf.iso', 'user-vm.vhdx', 'installer.iso')) {
    foreach ($name in $Names) { Set-Content -LiteralPath (Join-Path $Dir $name) -Value "stand-in for $name" }
}

function Write-Manifest([string] $Path, [string] $Root, [hashtable] $Extra = @{}) {
    $m = [ordered]@{ madeBy = 'Provision-IdentityMedia.ps1'; schema = 1; root = $Root; smbShareA = '\\localhost\SiEvidenceA'; mappedLetterA = 'R:\'; smbShareB = '\\localhost\SiEvidenceB'; mappedLetterB = 'S:\' }
    foreach ($key in $Extra.Keys) { $m[$key] = $Extra[$key] }
    ($m | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $Path -Encoding UTF8
}

Test-Case '2.01 an UNMARKED folder holding mounted-looking images, named by a genuine manifest: refused, zero actions, everything intact' {
    $case = New-Case 'unmarked-manifest'; $root = Join-Path $case 'VMs'; [void][IO.Directory]::CreateDirectory($root); New-ImageFiles $root
    $manifest = Join-Path $case 'identity-media.json'; Write-Manifest $manifest $root
    $before = Get-TreeSnapshot $case
    $rec = New-Recorder @{ 'R:' = '\\localhost\SiEvidenceA' } @{ SiEvidenceA = $root }
    $out = Invoke-MediaTeardown -Root $root -RootWasSupplied -ManifestPath $manifest -Actions $rec.Actions
    Check $out.Refused "not refused: $($out | Out-String)"
    Check ($rec.Log.Count -eq 0) "something was called before the marker was checked: $($rec.Log -join '; ')"
    Check ((Get-TreeSnapshot $case) -eq $before) 'a file or the manifest changed'
}

Test-Case '2.02 "-Remove -Root D:\VMs": an unmarked folder without a manifest is refused with zero actions' {
    $case = New-Case 'unmarked-root'; $root = Join-Path $case 'VMs'; [void][IO.Directory]::CreateDirectory($root); New-ImageFiles $root
    $before = Get-TreeSnapshot $case
    $rec = New-Recorder
    $out = Invoke-MediaTeardown -Root $root -RootWasSupplied -ManifestPath (Join-Path $case 'absent.json') -Actions $rec.Actions
    Check $out.Refused 'not refused'
    Check ($rec.Log.Count -eq 0) "something was called: $($rec.Log -join '; ')"
    Check ((Get-TreeSnapshot $case) -eq $before) 'a file changed'
}

Test-Case '2.03 "-Remove -Manifest C:\proj\appsettings.json": a file that is not ours is refused and not deleted' {
    $case = New-Case 'foreign-manifest'; $manifest = Join-Path $case 'appsettings.json'
    Set-Content -LiteralPath $manifest -Value '{ "ConnectionStrings": { "Default": "Server=prod;Database=orders" }, "root": "x" }'
    $before = Get-TreeSnapshot $case
    $rec = New-Recorder
    $out = Invoke-MediaTeardown -Root (Join-Path $case 'whatever') -ManifestPath $manifest -Actions $rec.Actions
    Check $out.Refused 'not refused'
    Check ($out.Reason -match 'not written by') "reason: $($out.Reason)"
    Check ($rec.Log.Count -eq 0) "something was called: $($rec.Log -join '; ')"
    Check ((Get-TreeSnapshot $case) -eq $before) 'the file changed'
}

Test-Case '2.04 a foreign manifest does not become acceptable because -Root is a genuine marked folder' {
    $case = New-Case 'foreign-manifest-marked'; $root = Join-Path $case 'media'; [void][IO.Directory]::CreateDirectory($root); Write-MediaMarker $root; New-ImageFiles $root
    $manifest = Join-Path $case 'other.json'; Set-Content -LiteralPath $manifest -Value ('{ "madeBy": "somebody else", "schema": 1, "root": "' + ($root -replace '\\', '\\\\') + '" }')
    $before = Get-TreeSnapshot $case
    $rec = New-Recorder
    $out = Invoke-MediaTeardown -Root $root -RootWasSupplied -ManifestPath $manifest -Actions $rec.Actions
    Check ($out.Refused -and $rec.Log.Count -eq 0) "refused=$($out.Refused) calls=$($rec.Log -join '; ')"
    Check ((Get-TreeSnapshot $case) -eq $before) 'something changed'
}

Test-Case '2.05 a manifest that is not JSON is refused and left alone' {
    $case = New-Case 'bad-json'; $manifest = Join-Path $case 'identity-media.json'; Set-Content -LiteralPath $manifest -Value 'this { is not json'
    $before = Get-TreeSnapshot $case
    $rec = New-Recorder
    $out = Invoke-MediaTeardown -Root (Join-Path $case 'media') -ManifestPath $manifest -Actions $rec.Actions
    Check ($out.Refused -and $rec.Log.Count -eq 0) "refused=$($out.Refused) calls=$($rec.Log -join '; ')"
    Check ((Get-TreeSnapshot $case) -eq $before) 'the file changed'
}

Test-Case '2.06 a genuine manifest whose root holds a marker with the WRONG text is refused with zero actions' {
    $case = New-Case 'wrong-marker'; $root = Join-Path $case 'media'; [void][IO.Directory]::CreateDirectory($root); New-ImageFiles $root
    Set-Content -LiteralPath (Join-Path $root '.si-identity-media') -Value 'planted by someone else'
    $manifest = Join-Path $case 'identity-media.json'; Write-Manifest $manifest $root
    $before = Get-TreeSnapshot $case
    $rec = New-Recorder
    $out = Invoke-MediaTeardown -Root $root -ManifestPath $manifest -Actions $rec.Actions
    Check ($out.Refused -and $rec.Log.Count -eq 0) "refused=$($out.Refused) calls=$($rec.Log -join '; ')"
    Check ((Get-TreeSnapshot $case) -eq $before) 'something changed'
}

Test-Case '2.07 a root that does not exist, named by a genuine manifest, is refused with zero actions' {
    $case = New-Case 'missing-root'; $manifest = Join-Path $case 'identity-media.json'; Write-Manifest $manifest (Join-Path $case 'gone')
    $before = Get-TreeSnapshot $case
    $rec = New-Recorder @{ 'R:' = '\\localhost\SiEvidenceA' } @{ SiEvidenceA = (Join-Path $case 'gone') }
    $out = Invoke-MediaTeardown -Root (Join-Path $case 'gone') -ManifestPath $manifest -Actions $rec.Actions
    Check ($out.Refused -and $rec.Log.Count -eq 0) "refused=$($out.Refused) calls=$($rec.Log -join '; ')"
    Check ((Get-TreeSnapshot $case) -eq $before) 'the manifest changed'
}

Test-Case '2.08 a filesystem root or a too-general folder is refused even with a marker in it' {
    $general = [IO.Path]::GetPathRoot($Sandbox)
    $case = New-Case 'general'; $manifest = Join-Path $case 'identity-media.json'; Write-Manifest $manifest $general
    $rec = New-Recorder
    $out = Invoke-MediaTeardown -Root $general -ManifestPath $manifest -Actions $rec.Actions
    Check ($out.Refused -and $rec.Log.Count -eq 0) "refused=$($out.Refused) calls=$($rec.Log -join '; ')"
    Check (Test-Path -LiteralPath $manifest) 'the manifest was deleted'
}

Test-Case '2.08b a MARKED folder is still refused when it counts as too general (the guard is not only a side effect of the marker check)' {
    $case = New-Case 'marked-general'; $root = Join-Path $case 'media'; [void][IO.Directory]::CreateDirectory($root); Write-MediaMarker $root; New-ImageFiles $root
    $manifest = Join-Path $case 'identity-media.json'; Write-Manifest $manifest $root
    $before = Get-TreeSnapshot $case
    $saved = $script:MinimumScratchPathLength
    try {
        $script:MinimumScratchPathLength = 100000      # every path is now "too general"
        $rec = New-Recorder
        $out = Invoke-MediaTeardown -Root $root -ManifestPath $manifest -Actions $rec.Actions
    }
    finally { $script:MinimumScratchPathLength = $saved }
    Check ($out.Refused -and $out.Reason -match 'too general' -and $rec.Log.Count -eq 0) "refused=$($out.Refused) reason=$($out.Reason) calls=$($rec.Log -join '; ')"
    Check ((Get-TreeSnapshot $case) -eq $before) 'something changed'
    $rec2 = New-Recorder
    Check (-not (Invoke-MediaTeardown -Root $root -ManifestPath $manifest -Actions $rec2.Actions).Refused) 'with the normal threshold the same folder is torn down'
}

Test-Case '2.09 an explicit -Root that is not the folder the manifest names is refused with zero actions' {
    $case = New-Case 'root-mismatch'
    $real = Join-Path $case 'media'; [void][IO.Directory]::CreateDirectory($real); Write-MediaMarker $real; New-ImageFiles $real
    $other = Join-Path $case 'VMs'; [void][IO.Directory]::CreateDirectory($other); New-ImageFiles $other
    $manifest = Join-Path $case 'identity-media.json'; Write-Manifest $manifest $real
    $before = Get-TreeSnapshot $case
    $rec = New-Recorder
    $out = Invoke-MediaTeardown -Root $other -RootWasSupplied -ManifestPath $manifest -Actions $rec.Actions
    Check ($out.Refused -and $rec.Log.Count -eq 0) "refused=$($out.Refused) calls=$($rec.Log -join '; ')"
    Check ((Get-TreeSnapshot $case) -eq $before) 'something changed'
}

Test-Case '2.10 a genuine marked root tears down only what the manifest vouches for, and only in the safe order' {
    $case = New-Case 'genuine'; $root = Join-Path $case 'media'; [void][IO.Directory]::CreateDirectory($root); Write-MediaMarker $root; New-ImageFiles $root
    $outside = Join-Path $case 'users-own-share-folder'; [void][IO.Directory]::CreateDirectory($outside)
    $manifest = Join-Path $case 'identity-media.json'; Write-Manifest $manifest $root
    # R: still points at our share; S: has since been re-mapped by the user to something else; SiEvidenceA is ours, SiEvidenceB now serves another folder
    $rec = New-Recorder @{ 'R:' = '\\localhost\SiEvidenceA'; 'S:' = '\\fileserver\finance' } @{ SiEvidenceA = (Join-Path $root 'share'); SiEvidenceB = $outside }
    $out = Invoke-MediaTeardown -Root (Join-Path $case 'ignored-because-the-manifest-names-the-root') -ManifestPath $manifest -Actions $rec.Actions
    Check (-not $out.Refused) "refused: $($out.Reason)"
    $changes = Get-Changes $rec
    Check ($changes -contains 'Unmap R:') 'did not disconnect our own mapping'
    Check ($changes -notcontains 'Unmap S:') 'disconnected a letter that no longer points at our share'
    Check ($changes -contains 'RemoveShare SiEvidenceA') 'did not remove our share'
    Check ($changes -notcontains 'RemoveShare SiEvidenceB') 'removed a share that serves a folder outside the root'
    Check ($changes -contains 'DismountIso si_udf.iso' -and $changes -contains 'DetachVhd ntfsA.vhdx' -and $changes -contains 'DetachVhd fat32.vhdx') 'did not dismount or detach our own images'
    Check (@($changes | Where-Object { $_ -match 'user-vm|installer' }).Count -eq 0) "touched an image it did not make: $($changes -join '; ')"
    $folder = [array]::IndexOf($changes, 'RemoveFolder'); $man = [array]::IndexOf($changes, 'RemoveManifest')
    $lastSideEffect = (0..($changes.Count - 1) | Where-Object { $changes[$_] -match '^(Unmap|RemoveShare|Dismount|Detach)' } | Measure-Object -Maximum).Maximum
    Check ($folder -gt $lastSideEffect -and $man -gt $folder) "the folder or manifest was removed before the last dismount, detach or disconnect: $($changes -join '; ')"
    Check (-not (Test-Path -LiteralPath $root) -and -not (Test-Path -LiteralPath $manifest)) 'the marked folder and the manifest should be gone'
    Check (Test-Path -LiteralPath $outside) 'a folder outside the root went missing'
}

Test-Case '2.11 the failure path (an in-memory state, no manifest file yet) tears down a marked root and deletes no manifest' {
    $case = New-Case 'failure-path'; $root = Join-Path $case 'media'; [void][IO.Directory]::CreateDirectory($root); Write-MediaMarker $root; New-ImageFiles $root @('ntfsA.vhdx')
    $state = [pscustomobject]@{ madeBy = 'Provision-IdentityMedia.ps1'; schema = 1; root = $root; smbShareA = '\\localhost\SiEvidenceA'; mappedLetterA = 'R:\' }
    $rec = New-Recorder @{ 'R:' = '\\localhost\SiEvidenceA' } @{ SiEvidenceA = (Join-Path $root 'share') }
    $out = Invoke-MediaTeardown -Root $root -RootWasSupplied -State $state -Actions $rec.Actions
    Check (-not $out.Refused) "refused: $($out.Reason)"
    $changes = Get-Changes $rec
    Check ($changes -contains 'DetachVhd ntfsA.vhdx' -and $changes -contains 'RemoveFolder' -and $changes -notcontains 'RemoveManifest') "changes: $($changes -join '; ')"
}

Test-Case '2.12 a state that is not ours is refused with zero actions' {
    $case = New-Case 'foreign-state'; $root = Join-Path $case 'media'; [void][IO.Directory]::CreateDirectory($root); Write-MediaMarker $root
    $rec = New-Recorder
    $out = Invoke-MediaTeardown -Root $root -RootWasSupplied -State ([pscustomobject]@{ root = $root }) -Actions $rec.Actions
    Check ($out.Refused -and $rec.Log.Count -eq 0) "refused=$($out.Refused) calls=$($rec.Log -join '; ')"
    Check (Test-Path -LiteralPath $root) 'the folder was removed'
}

Test-Case '2.13 provisioning refuses a folder that is not empty and not ours, a file, a root, and a folder it cannot list' {
    $case = New-Case 'assert-root'
    $data = Join-Path $case 'data'; New-UserData $data
    $threw = { param($path) try { Assert-ScratchRoot $path; $false } catch { $true } }
    Check (& $threw $data) 'accepted a folder full of data'
    $file = Join-Path $case 'afile'; Set-Content -LiteralPath $file -Value 'x'
    Check (& $threw $file) 'accepted a file'
    Check (& $threw ([IO.Path]::GetPathRoot($Sandbox))) 'accepted a filesystem root'
    $empty = Join-Path $case 'empty'; [void][IO.Directory]::CreateDirectory($empty)
    Check (-not (& $threw $empty)) 'refused an empty folder'
    Check (-not (& $threw (Join-Path $case 'new'))) 'refused a folder that does not exist'
    $ours = Join-Path $case 'ours'; [void][IO.Directory]::CreateDirectory($ours); Write-MediaMarker $ours; Set-Content -LiteralPath (Join-Path $ours 'ntfsA.vhdx') -Value 'x'
    Check (-not (& $threw $ours)) 'refused a folder it made earlier'
    Check ((Get-TreeSnapshot $data) -ne '') 'the data folder changed'
}

Test-Case '2.14 a folder that cannot be listed is refused, not treated as empty (fail closed)' {
    if ($IsWindowsHost) { Skip 'simulating an unreadable folder needs an ACL change; covered by the code path "catch, throw", reviewed in IdentitySafety.ps1' }
    if ((& id -u) -eq '0') { Skip 'running as root, which can read every folder' }
    $case = New-Case 'unreadable'; $locked = Join-Path $case 'locked'; [void][IO.Directory]::CreateDirectory($locked); Set-Content -LiteralPath (Join-Path $locked 'x') -Value 'x'
    & chmod 000 $locked
    try {
        $refused = $false
        try { Assert-ScratchRoot $locked } catch { $refused = $true }
        Check $refused 'accepted a folder it could not list'
    }
    finally { & chmod 755 $locked }
}

Test-Case '2.15 only a manifest that says who wrote it is accepted' {
    $good = [pscustomobject]@{ madeBy = 'Provision-IdentityMedia.ps1'; schema = 1; root = 'x' }
    Check (Test-ProvisionManifest $good) 'rejected a genuine manifest'
    foreach ($bad in @(
            $null,
            [pscustomobject]@{ root = 'x' },
            [pscustomobject]@{ madeBy = 'Provision-IdentityMedia.ps1'; schema = 1 },
            [pscustomobject]@{ madeBy = 'Provision-IdentityMedia.ps1'; schema = 2; root = 'x' },
            [pscustomobject]@{ madeBy = 'other'; schema = 1; root = 'x' },
            [pscustomobject]@{ madeBy = 'Provision-IdentityMedia.ps1'; schema = 1; root = '' })) {
        Check (-not (Test-ProvisionManifest $bad)) "accepted: $($bad | ConvertTo-Json -Compress)"
    }
}

if ($IncludeRealMedia) {
    Test-Case '2.16 REAL DISK: -Remove pointed at an unmarked folder and an unrelated JSON file leaves an attached VHDX attached' {
        if (-not $IsWindowsHost) { Skip 'needs Windows (diskpart)' }
        $elevated = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $elevated) { Skip 'needs an elevated session to attach a VHDX' }
        $case = New-Case 'real-disk'; $vms = Join-Path $case 'VMs'; [void][IO.Directory]::CreateDirectory($vms)
        $vhdx = Join-Path $vms 'ntfsA.vhdx'                    # a name the real script would treat as its own, in a folder it did not make
        $dp = Join-Path $case 'dp.txt'
        Set-Content -LiteralPath $dp -Encoding ASCII -Value @("create vdisk file=`"$vhdx`" maximum=64 type=expandable", "select vdisk file=`"$vhdx`"", 'attach vdisk')
        $manifest = Join-Path $case 'appsettings.json'; Set-Content -LiteralPath $manifest -Value '{ "Logging": { "Level": "Warning" } }'
        try {
            & diskpart.exe /s $dp | Out-Null
            Check ((Get-DiskImage -ImagePath $vhdx).Attached) 'could not attach the test disk'
            $r1 = Invoke-Child $ProvisionScript @('-Remove', '-Root', $vms, '-Manifest', (Join-Path $case 'no-such.json'))
            $r2 = Invoke-Child $ProvisionScript @('-Remove', '-Manifest', $manifest)
            Check ($r1.Code -eq 2 -and $r1.Output -match 'REFUSED') "-Remove -Root <unmarked folder> did not refuse: exit $($r1.Code): $($r1.Output)"
            Check ($r2.Code -eq 2 -and $r2.Output -match 'REFUSED') "-Remove -Manifest <foreign json> did not refuse: exit $($r2.Code): $($r2.Output)"
            Check ((Get-DiskImage -ImagePath $vhdx).Attached) 'THE DISK WAS DETACHED'
            Check ((Test-Path -LiteralPath $manifest) -and (Test-Path -LiteralPath $vhdx)) 'a file went missing'
        }
        finally {
            Set-Content -LiteralPath $dp -Encoding ASCII -Value @("select vdisk file=`"$vhdx`"", 'detach vdisk')
            & diskpart.exe /s $dp | Out-Null
        }
    }
}

# ------------------------------------------------------------------ result

# the fixtures plant read-only files, which a recursive delete on Windows will not remove
Get-ChildItem -LiteralPath $Sandbox -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object { try { $_.IsReadOnly = $false } catch { } }
[void](Remove-OwnedScratch $Sandbox $SandboxParent 'SiSafetyFixtures_')
$fail = @($Results | Where-Object Status -eq 'FAIL').Count
$skip = @($Results | Where-Object Status -eq 'SKIP').Count
$pass = @($Results | Where-Object Status -eq 'PASS').Count
Write-Host ("`nRESULT identity script safety fixtures: {0} PASS, {1} FAIL, {2} SKIP (host: {3} {4})" -f $pass, $fail, $skip, $PSVersionTable.PSEdition, $PSVersionTable.PSVersion)
exit $(if ($fail -eq 0) { 0 } else { 1 })
