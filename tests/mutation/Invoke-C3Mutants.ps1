<#
.SYNOPSIS
    Re-runs the independent mutants of the v1.1 C3 review (nine test-gap mutants, the A-08 reflection bypass and variants of
    it) against a copy of a source tree and says whether the tests kill each one.
.DESCRIPTION
    A mutant is one deliberate defect: exact text replacements in production files. For each one the script copies the tree to a
    scratch folder (the source tree is never modified), applies the edits, builds the suite that is supposed to catch it, runs it
    and compares the failing tests with an unmutated run of the same suite. A mutant is KILLED when at least one test fails that
    does not already fail without it, SURVIVED when none does, and NOT COMPILED when the edit broke the build (inconclusive: the
    mutant has to be rewritten, it proves nothing).

    The unmutated baseline run exists because some suites have failures that do not depend on the mutant (the Core suite on a
    non-Windows machine has 18, all path-syntax or kernel32 tests). Only a NEW failing test counts as a kill.

    Suites:  History = tests\StorageInventory.History.Tests   Core = tests\StorageInventory.Core.Tests
             Audit   = -AuditProject, filtered to SecurityAuditTests. On Windows pass tests\StorageInventory.IntegrationTests;
                       the default, tests\mutation\AuditHarness, compiles the same SecurityAuditTests.cs without the WPF App so
                       the audit can also run where WPF cannot (Linux, a container).

    Reproducing the review's survivors: check out the review commit in a worktree and mutate THAT tree with this script, e.g.
        git worktree add ..\c3-review 98e7647
        .\tests\mutation\Invoke-C3Mutants.ps1 -Source ..\c3-review -AuditProject <same as below>
    Every T-mutant SURVIVES there; against the repaired tree every one is KILLED.

    The script needs only git, the .NET SDK and PowerShell 7 or Windows PowerShell 5.1. It writes nothing outside -Work.
.PARAMETER Source      The git working tree to mutate (default: this repository).
.PARAMETER Work        Scratch folder, created fresh and removed at the end (default: a new folder under the temp directory).
.PARAMETER Only        Run only these mutant ids.
.PARAMETER Dotnet      The dotnet executable (default: dotnet on the PATH, else tools\dotnet\dotnet.exe of -Source).
.PARAMETER BuildArgs   Extra arguments for dotnet build, e.g. -p:EnableWindowsTargeting=true on a non-Windows machine.
.PARAMETER Out         Write the results as Markdown to this file (must not exist).
#>
param(
    [string] $Source = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string] $Work = (Join-Path ([IO.Path]::GetTempPath()) ('SiMutants_' + [guid]::NewGuid().ToString('N').Substring(0, 8))),
    [string[]] $Only,
    [string] $Dotnet,
    [string[]] $BuildArgs = @(),
    [string] $AuditProject = 'tests/mutation/AuditHarness',
    [string] $Out,
    [switch] $KeepWork
)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path
if (Test-Path -LiteralPath $Work) { throw "-Work '$Work' already exists; the script only deletes a folder it made itself." }
if ($Out -and (Test-Path -LiteralPath $Out)) { throw "-Out '$Out' already exists; the script does not overwrite files." }
if (-not $Dotnet) {
    $local = Join-Path $Source 'tools/dotnet/dotnet.exe'
    $Dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } elseif (Test-Path -LiteralPath $local) { $local } else { throw 'No dotnet found: install the .NET 10 SDK or run tools\fetch-tools.ps1.' }
}
# Off Windows the net10.0-windows projects only build with this switch (the reference packs come from NuGet)
if ($BuildArgs.Count -eq 0 -and $PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) { $BuildArgs = @('-p:EnableWindowsTargeting=true') }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# ---------------------------------------------------------------- the mutants
$CoreTrait = 'src/StorageInventory.Core'
$H = 'src/StorageInventory.History/Identity'
$mutants = @(
    @{ Id = 'T1'; Suite = 'History'; Finding = 'C3-M02'; What = 'the Moderate arm of the co-mounted check never reports a clone'
       Edits = @(@{ File = "$H/IdentityMatching.cs"
           Find = '            IdentityConfidence.Moderate => capture.UsableSerial32 is { } s32 && other.Serial32 == s32'
           Replace = '            IdentityConfidence.Moderate => capture.Confidence < 0 && capture.UsableSerial32 is { } s32 && other.Serial32 == s32' }) },
    @{ Id = 'T2'; Suite = 'History'; Finding = 'C3-M02'; What = 'the filesystem name is no longer compared for a mounted clone'
       Edits = @(@{ File = "$H/IdentityMatching.cs"
           Find = '        if (!FileSystemNames.Same(other.FsType, capture.FileSystemName)) return false;'
           Replace = '        if (capture.Confidence < 0) return false;' }) },
    @{ Id = 'T3'; Suite = 'History'; Finding = 'C3-M02'; What = 'the exact root comparison becomes culture-sensitive'
       Edits = @(@{ File = "$H/IdentityMatching.cs"
           Find = 'var exact = sources.Where(s => string.Equals(s.RootInVolume, root, StringComparison.Ordinal)).ToList();'
           Replace = 'var exact = sources.Where(s => string.Equals(s.RootInVolume, root, StringComparison.InvariantCulture)).ToList();' }) },
    @{ Id = 'T4'; Suite = 'History'; Finding = 'C3-M02'; What = 'a SameVolume answer that CREATES a source on the chosen volume loses the UserAsserted basis (attaching to an existing source keeps it)'
       Edits = @(@{ File = "$H/IdentityMatching.cs"
           Find = 'var create = new IdentityDecision.CreateSource(capture.Kind, volumeId, networkRootKey, root, capture.Confidence, basis);'
           Replace = 'var create = new IdentityDecision.CreateSource(capture.Kind, volumeId, networkRootKey, root, capture.Confidence, basis == IdentityBasis.UserAsserted ? IdentityBasis.Evidence : basis);' }) },
    @{ Id = 'T5'; Suite = 'History'; Finding = 'C3-M02'; What = 'a zero 32-bit serial counts as missing minimum evidence'
       Edits = @(@{ File = "$H/MinimumEvidence.cs"
           Find = 'if (!e0.VolumeSerial32.IsAvailable) missing.Add('
           Replace = 'if (!e0.VolumeSerial32.IsAvailable || e0.VolumeSerial32.Value == 0) missing.Add(' }) },
    @{ Id = 'T6'; Suite = 'History'; Finding = 'C3-M02'; What = 'the label comparison ignores case'
       Edits = @(@{ File = "$H/IdentityMatching.cs"
           Find = 'string.Equals(recorded, observed, StringComparison.Ordinal)'
           Replace = 'string.Equals(recorded, observed, StringComparison.OrdinalIgnoreCase)' }) },
    @{ Id = 'T7'; Suite = 'History'; Finding = 'C3-M02'; What = 'two unknown labels corroborate each other'
       Edits = @(@{ File = "$H/IdentityMatching.cs"
           Find = 'recorded is not null && observed is not null && string.Equals(recorded, observed, StringComparison.Ordinal)'
           Replace = 'string.Equals(recorded, observed, StringComparison.Ordinal)' }) },
    @{ Id = 'T8'; Suite = 'History'; Finding = 'C3-M02'; What = 'the underivable-canonical-path branch of the minimum evidence is removed'
       Edits = @(@{ File = "$H/MinimumEvidence.cs"
           Find = 'else if (!SourceLocation.TryDerive(e0.CanonicalPath.Value!, out _))'
           Replace = 'else if (e0.CanonicalPath.Value is null)' }) },
    @{ Id = 'T10c'; Suite = 'Core'; Finding = 'C3-M02'; What = 'the \\.\ device guard of TryDerive is removed'
       Edits = @(@{ File = "$CoreTrait/Identity/SourceLocation.cs"
           Find = 'if (canonicalPath[2..server] is "?" or ".") return false;'
           Replace = 'if (canonicalPath[2..server] is "?") return false;' }) },
    @{ Id = 'T10d'; Suite = 'Core'; Finding = 'C3-M02 (extra)'; What = 'the \\?\ device guard of TryDerive is removed'
       Edits = @(@{ File = "$CoreTrait/Identity/SourceLocation.cs"
           Find = 'if (canonicalPath[2..server] is "?" or ".") return false;'
           Replace = 'if (canonicalPath[2..server] is ".") return false;' }) },
    @{ Id = 'C7'; Suite = 'Audit'; Finding = 'C3-M03'; What = 'a reflective GetFileInformationByHandleEx call with an information class taken from the parameter type (the review''s mutant)'
       Edits = @(@{ File = "$CoreTrait/Paths/NativeMethods.cs"
           Find = '    private const int NameBufferLength = 261;'
           Replace = @'
    internal static object? ReflectiveMutant(SafeFileHandle h)
    {
        var m = typeof(NativeMethods).GetMethod("GetFileInformationByHandleEx", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return m.Invoke(null, new object?[] { h, Enum.ToObject(m.GetParameters()[1].ParameterType, 5), null, 24u });
    }

    private const int NameBufferLength = 261;
'@ }) },
    @{ Id = 'C7b'; Suite = 'Audit'; Finding = 'C3-M03 (extra)'; What = 'the same call with whitespace inside every token the first version of the text rule keyed on'
       Edits = @(@{ File = "$CoreTrait/Paths/NativeMethods.cs"
           Find = '    private const int NameBufferLength = 261;'
           Replace = @'
    internal static object? ReflectiveMutant(SafeFileHandle h)
    {
        var m = typeof (NativeMethods) . GetMethod ("GetFileInformationByHandleEx", System . Reflection . BindingFlags.Static | System . Reflection . BindingFlags.NonPublic)!;
        return m . Invoke (null, new object?[] { h, Enum . ToObject (m . GetParameters ()[1].ParameterType, 5), null, 24u });
    }

    private const int NameBufferLength = 261;
'@ }) },
    @{ Id = 'C7c'; Suite = 'Audit'; Finding = 'C3-M03 (extra)'; What = 'a reflective call written so that no pattern of the text rule matches it (namespace imported as an XML character reference, no typeof, no GetMethod, no Enum.ToObject); only the compiled-assembly rule can see it'
       Edits = @(
         @{ File = "$CoreTrait/StorageInventory.Core.csproj"; Find = '</Project>'; Replace = '  <ItemGroup><Using Include="System.Reflec&#116;ion" /></ItemGroup>' + "`n" + '</Project>' },
         @{ File = "$CoreTrait/Paths/NativeMethods.cs"
            Find = '    private const int NameBufferLength = 261;'
            Replace = @'
    internal static object? ReflectiveMutant(SafeFileHandle h)
    {
        var type = ((Func<int>)(static () => 0)).Method.DeclaringType!.DeclaringType!;
        var m = type.GetTypeInfo().DeclaredMethods.First(x => x.Name == "GetFileInformationByHandleEx");
        return m.Invoke(null, new object?[] { h, System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(m.GetParameters()[1].ParameterType), null, 24u });
    }

    private const int NameBufferLength = 261;
'@ }) }
)
if ($Only) { $mutants = @($mutants | Where-Object { $Only -contains $_.Id }) }

# ---------------------------------------------------------------- the scratch copy
function Invoke-Native([string] $File, [string[]] $Arguments, [string] $In) {
    Push-Location $In
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns a native command's stderr into a terminating error under 'Stop'
    try { $output = & $File @Arguments 2>&1 | Out-String; return @{ Code = $LASTEXITCODE; Output = $output } }
    finally { $ErrorActionPreference = $saved; Pop-Location }
}

if (-not (Test-Path -LiteralPath (Join-Path $Source '.git'))) { throw "-Source '$Source' is not a git working tree (the copy is taken from 'git ls-files')." }
$files = (Invoke-Native 'git' @('ls-files', '--cached', '--others', '--exclude-standard') $Source).Output -split "`r?`n" | Where-Object { $_ }
New-Item -ItemType Directory -Path $Work | Out-Null
$Work = (Resolve-Path -LiteralPath $Work).Path
$copy = Join-Path $Work 'tree'
foreach ($file in $files) {
    $from = Join-Path $Source $file
    if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { continue }
    $to = Join-Path $copy $file
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $to))
    Copy-Item -LiteralPath $from -Destination $to
}

$suites = @{
    History = @{ Project = 'tests/StorageInventory.History.Tests'; Dll = 'StorageInventory.History.Tests.dll'; Filter = @() }
    Core    = @{ Project = 'tests/StorageInventory.Core.Tests'; Dll = 'StorageInventory.Core.Tests.dll'; Filter = @() }
    Audit   = @{ Project = $AuditProject; Dll = 'StorageInventory.IntegrationTests.dll'; Filter = @('SecurityAuditTests') }
}

function Invoke-Suite([string] $Name) {
    $suite = $suites[$Name]
    $project = Join-Path $copy $suite.Project
    $build = Invoke-Native $Dotnet (@('build', $project, '-c', 'Release', '--nologo', '-v', 'q') + $BuildArgs) $copy
    if ($build.Code -ne 0) { return @{ Compiled = $false; Failed = @(); Summary = ($build.Output -split "`r?`n" | Where-Object { $_ -match 'error' } | Select-Object -First 3) -join ' | ' } }
    $dll = Get-ChildItem -LiteralPath (Join-Path $project 'bin/Release') -Recurse -Filter $suite.Dll | Select-Object -First 1
    $run = Invoke-Native $Dotnet (@($dll.FullName) + $suite.Filter) $copy
    $failed = @($run.Output -split "`r?`n" | Where-Object { $_ -match '^FAIL\s+(\S+)' } | ForEach-Object { ($_ -replace '^FAIL\s+(\S+).*', '$1') })
    $summary = ($run.Output -split "`r?`n" | Where-Object { $_ -match '^RESULT ' } | Select-Object -Last 1)
    return @{ Compiled = $true; Failed = $failed; Summary = $summary }
}

$results = New-Object System.Collections.Generic.List[object]
try {
    $baseline = @{}
    foreach ($name in @($mutants | ForEach-Object { $_.Suite } | Select-Object -Unique)) {
        $baseline[$name] = Invoke-Suite $name
        if (-not $baseline[$name].Compiled) { throw "The unmutated $name suite does not build: $($baseline[$name].Summary)" }
        Write-Host ("baseline {0,-8} {1}" -f $name, $baseline[$name].Summary)
    }

    foreach ($m in $mutants) {
        $originals = @{}
        try {
            foreach ($edit in $m.Edits) {
                $path = Join-Path $copy $edit.File
                if (-not $originals.ContainsKey($path)) { $originals[$path] = [IO.File]::ReadAllText($path) }
                $text = [IO.File]::ReadAllText($path)
                $find = $edit.Find.Replace("`r`n", "`n"); $replace = $edit.Replace.Replace("`r`n", "`n")
                $count = ([regex]::Matches($text, [regex]::Escape($find))).Count
                if ($count -ne 1) { throw "mutant $($m.Id): '$find' occurs $count times in $($edit.File), expected exactly once" }
                [IO.File]::WriteAllText($path, $text.Replace($find, $replace), (New-Object Text.UTF8Encoding($false)))
            }
            $r = Invoke-Suite $m.Suite
            if (-not $r.Compiled) { $verdict = 'NOT COMPILED'; $killers = @(); $detail = $r.Summary }
            else {
                $killers = @($r.Failed | Where-Object { $baseline[$m.Suite].Failed -notcontains $_ })
                $verdict = if ($killers.Count -gt 0) { 'KILLED' } else { 'SURVIVED' }
                $detail = $r.Summary
            }
        }
        finally { foreach ($path in $originals.Keys) { [IO.File]::WriteAllText($path, $originals[$path], (New-Object Text.UTF8Encoding($false))) } }
        $results.Add([pscustomobject]@{ Id = $m.Id; Finding = $m.Finding; Suite = $m.Suite; What = $m.What; Verdict = $verdict; Killers = $killers; Detail = $detail })
        Write-Host ("{0,-5} {1,-9} {2,-12} {3}" -f $m.Id, $m.Suite, $verdict, (($results[$results.Count - 1].Killers | Select-Object -First 3) -join ', '))
    }
}
finally {
    if (-not $KeepWork -and (Test-Path -LiteralPath (Join-Path $Work 'tree'))) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
}

$md = New-Object System.Collections.Generic.List[string]
$md.Add("| Mutant | Finding | Suite | Defect | Result | Failing tests that are not failing without it |")
$md.Add("|---|---|---|---|---|---|")
foreach ($r in $results) {
    $tests = if ($r.Killers.Count -gt 0) { (($r.Killers | Select-Object -First 4 | ForEach-Object { '`' + $_ + '`' }) -join '<br>') + $(if ($r.Killers.Count -gt 4) { "<br>(+$($r.Killers.Count - 4) more)" }) } else { '' }
    $md.Add("| $($r.Id) | $($r.Finding) | $($r.Suite) | $($r.What) | **$($r.Verdict)** | $tests |")
}
$md | ForEach-Object { Write-Host $_ }
if ($Out) { Set-Content -LiteralPath $Out -Value ($md -join "`n") -Encoding UTF8 }
$notKilled = @($results | Where-Object { $_.Verdict -ne 'KILLED' })
Write-Host ("{0} mutants, {1} killed, {2} survived, {3} not compiled" -f $results.Count, @($results | Where-Object Verdict -eq 'KILLED').Count, @($results | Where-Object Verdict -eq 'SURVIVED').Count, @($results | Where-Object Verdict -eq 'NOT COMPILED').Count)
exit $(if ($notKilled.Count -eq 0) { 0 } else { 1 })
