<#
.SYNOPSIS
    Shows that the identity script-safety fixtures (tests\identity\Test-IdentityScriptSafety.ps1) can fail: it weakens one guard at a
    time in a scratch copy of tests\identity and requires the fixtures to go red (C3 repair, findings C3-H01 and C3-M01).
.DESCRIPTION
    Each mutant is one exact text replacement in IdentitySafety.ps1 (or a script). KILLED means the fixtures exit non-zero and name
    the failing cases; SURVIVED means a guard was weakened and the fixtures still passed, which is a hole in the fixtures. The
    unmutated copy must pass first. Nothing outside -Work is written; the repository is never modified.
.PARAMETER Source  The repository root (default: this repository).
.PARAMETER Work    Scratch folder, must not exist (default: a new folder under the temp directory); removed at the end.
.PARAMETER Out     Write the results as Markdown to this file (must not exist).
#>
param(
    [string] $Source = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string] $Work = (Join-Path ([IO.Path]::GetTempPath()) ('SiScriptMutants_' + [guid]::NewGuid().ToString('N').Substring(0, 8))),
    [string] $Out
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Work) { throw "-Work '$Work' already exists." }
if ($Out -and (Test-Path -LiteralPath $Out)) { throw "-Out '$Out' already exists." }
$hostExe = (Get-Process -Id $PID).Path
$identity = Join-Path $Source 'tests/identity'

$mutants = @(
    @{ Id = 'S1'; Finding = 'C3-M01'; What = 'teardown does not check the marker before acting'; File = 'IdentitySafety.ps1'
       Find = 'if ($markerText -ne $script:MediaMarkerText) {'; Replace = 'if ($false) {' },
    @{ Id = 'S2'; Finding = 'C3-M01'; What = 'any JSON is accepted as the script''s own manifest'; File = 'IdentitySafety.ps1'
       Find = '    if ($null -eq $Manifest) { return $false }'; Replace = '    return $true' },
    @{ Id = 'S3'; Finding = 'C3-M01'; What = 'every .vhdx and .iso in the root is detached or dismounted, not only the names the script gives its disks'; File = 'IdentitySafety.ps1'
       Find = "'^(?:(?:ntfsA|ntfsB|fat32|exfat|refs)\.vhdx|si_udf\.iso)$'"; Replace = "'\.(vhdx|iso)$'" },
    @{ Id = 'S4'; Finding = 'C3-M01'; What = 'a drive letter is disconnected even when it no longer points at the manifest''s share'; File = 'IdentitySafety.ps1'
       Find = '$target = [string](& $Actions.GetMapping $letter)'; Replace = '$target = $share' },
<<<<<<< HEAD
    @{ Id = 'S5'; Finding = 'C3-M01'; What = 'a share that serves a folder outside the root is removed'; File = 'IdentitySafety.ps1'
       Find = 'if (Test-PathInside $served $full) {'; Replace = 'if ($true) {' },
=======
    @{ Id = 'S5'; Finding = 'C3-M01'; What = 'a share that serves a folder outside the volume the manifest names is removed'; File = 'IdentitySafety.ps1'
       Find = 'if ($backing[$name] -and (Test-WindowsPathInside $served $backing[$name])) {'; Replace = 'if ($true) {' },
>>>>>>> v1.1/c3-repair
    @{ Id = 'S6'; Finding = 'C3-M01'; What = 'an explicit -Root that disagrees with the manifest is accepted'; File = 'IdentitySafety.ps1'
       Find = 'if ($RootWasSupplied -and $Root -and -not (Test-SamePath $Root $effective)) {'; Replace = 'if ($false) {' },
    @{ Id = 'S7'; Finding = 'C3-M01'; What = 'a filesystem root is accepted as the teardown root'; File = 'IdentitySafety.ps1'
       Find = 'if (Test-PathTooGeneral $full) { return (New-RefusedTeardown'; Replace = 'if ($false) { return (New-RefusedTeardown' },
    @{ Id = 'S8'; Finding = 'C3-M01'; What = 'provisioning accepts a non-empty folder that is not ours'; File = 'IdentitySafety.ps1'
       Find = 'if ($first) { throw "-Root'; Replace = 'if ($false) { throw "-Root' },
    @{ Id = 'S9'; Finding = 'C3-H01'; What = 'the scratch cleanup accepts a folder whose marker file does not say what this script writes'; File = 'IdentitySafety.ps1'
       Find = "if ((Get-Content -LiteralPath `$marker -Raw -ErrorAction Stop).Trim() -ne `$script:ScratchMarkerText) { throw 'its marker file is not the one this script writes' }"; Replace = '' },
    @{ Id = 'S9b'; Finding = 'C3-H01'; What = 'the scratch cleanup deletes a folder with no marker at all'; File = 'IdentitySafety.ps1'
       Find = "if (-not (Test-Path -LiteralPath `$marker -PathType Leaf)) { throw 'it has no marker file, so this script did not make it' }
        if ((Get-Content -LiteralPath `$marker -Raw -ErrorAction Stop).Trim() -ne `$script:ScratchMarkerText) { throw 'its marker file is not the one this script writes' }"; Replace = '' },
    @{ Id = 'S10'; Finding = 'C3-H01'; What = 'the scratch cleanup follows a link'; File = 'IdentitySafety.ps1'
       Find = "if (Test-IsReparsePoint `$full) { throw 'it is a link' }"; Replace = '' },
    @{ Id = 'S11'; Finding = 'C3-H01'; What = 'the scratch cleanup deletes a folder that is not directly inside -Work'; File = 'IdentitySafety.ps1'
       Find = "if (-not (Test-SamePath (Split-Path -Parent `$full) `$Parent)) { throw 'it is not directly inside the folder the script was given' }"; Replace = '' },
    @{ Id = 'S12'; Finding = 'C3-H01'; What = '-Out may overwrite an existing file without -Overwrite'; File = 'IdentitySafety.ps1'
       Find = '-and -not $Overwrite) { throw'; Replace = '-and $false) { throw' },
    @{ Id = 'S13'; Finding = 'C3-H01'; What = '-Work may be a filesystem root'; File = 'IdentitySafety.ps1'
       Find = 'if (Test-PathTooGeneral $full) { throw "-Work'; Replace = 'if ($false) { throw "-Work' },
    @{ Id = 'S14'; Finding = 'C3-H01'; What = 'the experiments script uses -Work itself as the scratch folder again (the review commit''s behaviour)'; File = 'Invoke-IdentityExperiments.ps1'
       Find = 'if ($CheckArgumentsOnly) {
    $child = New-ScratchChild $WorkParent $ScratchPrefix'; Replace = 'if ($CheckArgumentsOnly) {
    $child = $WorkParent; New-Item -ItemType Directory -Force $child | Out-Null' }
)

function Invoke-Fixtures([string] $Folder) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $hostExe
    $psi.Arguments = '-NoProfile -NonInteractive -File "' + (Join-Path $Folder 'Test-IdentityScriptSafety.ps1') + '"'
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $text = $stdout.Result + $stderr.Result
    $failed = @($text -split "`r?`n" | Where-Object { $_ -match '^FAIL\s+(\S+)' } | ForEach-Object { ($_ -replace '^FAIL\s+(\S+).*', '$1') })
    return [pscustomobject]@{ Code = $process.ExitCode; Failed = $failed; Summary = ($text -split "`r?`n" | Where-Object { $_ -match '^RESULT ' } | Select-Object -Last 1) }
}

[void][IO.Directory]::CreateDirectory($Work)
$results = New-Object System.Collections.Generic.List[object]
try {
    $baselineFolder = Join-Path $Work 'baseline'
    Copy-Item -LiteralPath $identity -Destination $baselineFolder -Recurse
    $baseline = Invoke-Fixtures $baselineFolder
    Write-Host "baseline: $($baseline.Summary)"
    if ($baseline.Code -ne 0) { throw "The unmutated fixtures fail: $($baseline.Failed -join ', ')" }

    foreach ($m in $mutants) {
        $folder = Join-Path $Work $m.Id
        Copy-Item -LiteralPath $identity -Destination $folder -Recurse
        $path = Join-Path $folder $m.File
        $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
        $count = ([regex]::Matches($text, [regex]::Escape($m.Find))).Count
        if ($count -ne 1) { throw "mutant $($m.Id): '$($m.Find)' occurs $count times in $($m.File), expected once" }
        [IO.File]::WriteAllText($path, $text.Replace($m.Find, $m.Replace), (New-Object Text.UTF8Encoding($false)))
        $r = Invoke-Fixtures $folder
        $verdict = if ($r.Code -ne 0) { 'KILLED' } else { 'SURVIVED' }
        $results.Add([pscustomobject]@{ Id = $m.Id; Finding = $m.Finding; What = $m.What; Verdict = $verdict; Failed = $r.Failed })
        Write-Host ('{0,-4} {1,-9} {2}' -f $m.Id, $verdict, ($r.Failed -join ', '))
    }
}
finally { if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue } }

$md = @('| Mutant | Finding | Weakened guard | Result | Fixture cases that went red |', '|---|---|---|---|---|')
foreach ($r in $results) { $md += "| $($r.Id) | $($r.Finding) | $($r.What) | **$($r.Verdict)** | $((($r.Failed | ForEach-Object { '`' + $_ + '`' }) -join ', ')) |" }
$md | ForEach-Object { Write-Host $_ }
if ($Out) { Set-Content -LiteralPath $Out -Value ($md -join "`n") -Encoding UTF8 }
$survived = @($results | Where-Object Verdict -eq 'SURVIVED').Count
Write-Host ("{0} mutants, {1} killed, {2} survived" -f $results.Count, @($results | Where-Object Verdict -eq 'KILLED').Count, $survived)
exit $(if ($survived -eq 0) { 0 } else { 1 })
