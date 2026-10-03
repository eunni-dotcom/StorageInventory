<#
.SYNOPSIS
    v1.1 C4: deliberate defects in the Library's interlock, store, open checks, importer and audits, run against a scratch copy
    of the tree to show that the tests kill each one.
.DESCRIPTION
    Same method as Invoke-C3Mutants.ps1: one mutant is exact text replacements in production files; the source tree is never
    modified; a mutant is KILLED when a test fails that does not fail in the unmutated baseline run, SURVIVED when none does and
    NOT COMPILED when the edit broke the build (inconclusive: the mutant proves nothing until rewritten).

    Suites:  Library = tests\StorageInventory.Library.Tests (interlock, store, open, import, real processes)
             Audit   = tests\StorageInventory.IntegrationTests filtered to SecurityAuditTests and LibrarySecurityAuditTests

    The mutants C4-01 to C4-A4 are the original C4 suite (22). The C4 implementation repair adds C4R-* (tests\mutation\C4RepairMutants.ps1,
    dot-sourced below); a mutant may carry its own Filter, the test classes it is MEANT to be caught by, so that a kill is for the
    intended reason and a run of the whole set takes minutes, not hours. The focused repair of the five Mediums of the repair review adds
    C4F-* (tests\mutation\C4FixMutants.ps1: the cadence inside one big folder and TEST-L9's inputs). -Set Original runs only the first,
    -Set Repair only the second, -Set Fixes only the third.

    The scratch copy holds git-tracked files only, so the NuGet cache and the SDK are not in it: the script uses -Source's own
    tools\dotnet and packages (restore finds everything in the cache; the committed lock files are part of the copy).
.PARAMETER Source      The git working tree to mutate (default: this repository).
.PARAMETER Work        Scratch folder, created fresh and removed at the end.
.PARAMETER Only        Run only these mutant ids.
.PARAMETER Out         Write the results as Markdown to this file (must not exist).
.PARAMETER KeepWork    Leave the scratch folder in place.
#>
param(
    [string] $Source = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string] $Work = (Join-Path ([IO.Path]::GetTempPath()) ('SiC4Mutants_' + [guid]::NewGuid().ToString('N').Substring(0, 8))),
    [string[]] $Only,
    [ValidateSet('All', 'Original', 'Repair', 'Fixes')] [string] $Set = 'All',
    [string] $Out,
    [switch] $KeepWork
)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path
if (Test-Path -LiteralPath $Work) { throw "-Work '$Work' already exists; the script only deletes a folder it made itself." }
if ($Out -and (Test-Path -LiteralPath $Out)) { throw "-Out '$Out' already exists; the script does not overwrite files." }
$Dotnet = Join-Path $Source 'tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $Dotnet)) { throw 'tools\dotnet is missing in -Source: run tools\fetch-tools.ps1.' }
$state = Join-Path $Source 'tools\dotnet-state'
$env:DOTNET_CLI_HOME = Join-Path $state 'home'
$env:APPDATA = Join-Path $state 'appdata'
$env:NUGET_PACKAGES = Join-Path $Source 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $state 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $state 'nuget-plugins'
$env:DOTNET_ROOT = Join-Path $Source 'tools\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:MSBUILDDISABLENODEREUSE = '1'; $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
$BuildArgs = @('-nodeReuse:false')

# ---------------------------------------------------------------- the mutants
$L = 'src/StorageInventory.Library'
$I = "$L/Interlock/LibraryInterlock.cs"
$mutants = @(
    @{ Id = 'C4-01'; Suite = 'Library'; What = 'a stale lease id still authorises a mutation (OBS-15)'
       Edits = @(@{ File = $I; Find = 'if (lease.Id == 0 || _leaseId != lease.Id || _kind != InterlockStateKind.Mutating) return "the lease is stale'; Replace = 'if (lease.Id == 0 || _kind != InterlockStateKind.Mutating) return "the lease is stale' }) },
    @{ Id = 'C4-02'; Suite = 'Library'; What = 'a mutation may begin while a scan is Observing'
       Edits = @(@{ File = $I; Find = "only handed over from a scan's observation window.""; }`n            else if (_kind != InterlockStateKind.Idle)"; Replace = "only handed over from a scan's observation window.""; }`n            else if (_kind == InterlockStateKind.Mutating)" }) },
    @{ Id = 'C4-03'; Suite = 'Library'; What = 'an observation window may open while a mutation is running'
       Edits = @(@{ File = $I; Find = "refusal = FaultedReason; }`n            else if (_kind != InterlockStateKind.Idle) { refusal = RefusalLocked(); }`n            else`n            {`n                var id = _nextLeaseId++;`n                _kind = InterlockStateKind.Observing;"; Replace = "refusal = FaultedReason; }`n            else if (_kind == InterlockStateKind.Observing) { refusal = RefusalLocked(); }`n            else`n            {`n                var id = _nextLeaseId++;`n                _kind = InterlockStateKind.Observing;" }) },
    @{ Id = 'C4-04'; Suite = 'Library'; What = 'the mutation epoch does not advance when a mutation begins (OBS-04a)'
       Edits = @(@{ File = $I; Find = "_owner = owner;`n                _leaseId = id;`n                _epoch++;"; Replace = "_owner = owner;`n                _leaseId = id;" }) },
    @{ Id = 'C4-05'; Suite = 'Library'; What = 'a mutation lease id is reused'
       Edits = @(@{ File = $I; Find = "var id = _nextLeaseId++;`n                _kind = InterlockStateKind.Mutating;`n                _mutation = kind;"; Replace = "var id = 1 + 0 * _nextLeaseId++;`n                _kind = InterlockStateKind.Mutating;`n                _mutation = kind;" }) },
    @{ Id = 'C4-06'; Suite = 'Library'; What = 'ending a lease with a resource still open does not fault'
       Edits = @(@{ File = $I; Find = '                if (_resources != 0 || _flaggedUnclean)'; Replace = '                if (_resources < 0)' }) },
    @{ Id = 'C4-07'; Suite = 'Library'; What = 'a lease of another session authorises a mutation'
       Edits = @(@{ File = $I; Find = "MutationKind[] allowed)`n    {`n        if (lease.Owner != this) return lease.Owner is null ? ""no lease (a default lease value)"" : ""a lease of another LibrarySession"";"; Replace = "MutationKind[] allowed)`n    {`n        if (lease.Owner is null) return ""no lease (a default lease value)"";" }) },
    @{ Id = 'C4-08'; Suite = 'Library'; What = 'the header check accepts a WAL-format file'
       Edits = @(@{ File = "$L/LibraryStore.cs"; Find = 'else if (header[18] != 1 || header[19] != 1) outcome = HeaderOutcome.WalFormat;'; Replace = 'else if (header[18] > 100) outcome = HeaderOutcome.WalFormat;' }) },
    @{ Id = 'C4-09'; Suite = 'Library'; What = 'the writer lock is taken with sharing allowed (a second writer process gets in)'
       Edits = @(@{ File = "$L/LibraryStore.cs"; Find = "FileShare.None, bufferSize: 1);`n            _lockPreExisted = true;"; Replace = "FileShare.ReadWrite, bufferSize: 1);`n            _lockPreExisted = true;" }) },
    @{ Id = 'C4-10'; Suite = 'Library'; What = 'a Library with a different schema fingerprint is not refused'
       Edits = @(@{ File = "$L/LibrarySession.cs"; Find = 'if (differences.Count > 0) return State(LibraryState.NotALibrary,'; Replace = 'if (differences.Count > 1000) return State(LibraryState.NotALibrary,' }) },
    @{ Id = 'C4-11'; Suite = 'Library'; What = 'an unpublished snapshot left by a crash is not detected at open'
       Edits = @(@{ File = "$L/LibrarySession.cs"; Find = 'if (Convert.ToInt64(q.Scalar(OpenSql.CountUnpublishedSnapshots)) != 0)'; Replace = 'if (Convert.ToInt64(q.Scalar(OpenSql.CountUnpublishedSnapshots)) < 0)' }) },
    @{ Id = 'C4-12'; Suite = 'Library'; What = 'the in-transaction verification failure is not raised'
       Edits = @(@{ File = "$L/Import/SnapshotImporter.cs"; Find = 'if (failure is not null) throw new ImportException('; Replace = 'if (failure is not null && failure.Length < 0) throw new ImportException(' }) },
    @{ Id = 'C4-13'; Suite = 'Library'; What = 'COMMIT is not guarded by the lease (OBS-15 before COMMIT)'
       Edits = @(@{ File = "$L/LibraryDatabase.cs"; Find = "        _faults?.BeforeCommit?.Invoke();`n        Guard(lease, ""commit the transaction"");"; Replace = "        _faults?.BeforeCommit?.Invoke();" }) },
    @{ Id = 'C4-14'; Suite = 'Library'; What = 'a stored error-type code is mapped to the wrong stable code'
       Edits = @(@{ File = "$L/StableCodes.cs"; Find = 'ScanErrorType.AccessDenied => 1,'; Replace = 'ScanErrorType.AccessDenied => 2,' }) },
    @{ Id = 'C4-15'; Suite = 'Library'; What = 'the journal is not made durable: synchronous NORMAL'
       Edits = @(@{ File = "$L/Sql/OpenSql.cs"; Find = 'PRAGMA synchronous = FULL"'; Replace = 'PRAGMA synchronous = NORMAL"' }) },
    @{ Id = 'C4-16'; Suite = 'Library'; What = 'readers open the Library read-write'
       Edits = @(@{ File = "$L/LibraryDatabase.cs"; Find = 'Mode = SqliteOpenMode.ReadOnly, Pooling = false'; Replace = 'Mode = SqliteOpenMode.ReadWrite, Pooling = false' }) },
    @{ Id = 'C4-17'; Suite = 'Library'; What = 'the set-aside leaves the journal behind'
       Edits = @(@{ File = "$L/LibraryStore.cs"; Find = "            (LibraryNames.JournalFile, LibraryNames.QuarantineJournalSuffix),`n"; Replace = '' }) },
    @{ Id = 'C4-18'; Suite = 'Library'; What = 'a save does not preempt active readers (CONC-06)'
       Edits = @(@{ File = "$L/ReaderWriterGate.cs"; Find = 'toPreempt = [.. _activeReaders];'; Replace = 'toPreempt = [];' }) },
    @{ Id = 'C4-A1'; Suite = 'Audit'; What = 'a seventh first-party kernel32 P/Invoke in the Library (A-09, A-25)'
       Edits = @(@{ File = "$L/ReaderWriterGate.cs"; Find = 'internal sealed class ReaderWriterGate'; Replace = "internal static class MutantNative { [System.Runtime.InteropServices.DllImport(""kernel32.dll"")] internal static extern uint GetTickCount(); }`n`ninternal sealed class ReaderWriterGate" }) },
    @{ Id = 'C4-A2'; Suite = 'Audit'; What = 'a File.Move outside the set-aside (A-25, the only rename)'
       Edits = @(@{ File = "$L/ReaderWriterGate.cs"; Find = 'internal sealed class ReaderWriterGate'; Replace = "internal static class MutantMove { internal static void M(string a, string b) => System.IO.File.Move(a, b); }`n`ninternal sealed class ReaderWriterGate" }) },
    @{ Id = 'C4-A3'; Suite = 'Audit'; What = 'the Library stops hiding SQLite types from its consumers (PrivateAssets removed)'
       Edits = @(@{ File = "$L/StorageInventory.Library.csproj"; Find = '<PackageReference Include="Microsoft.Data.Sqlite.Core" Version="10.0.12" PrivateAssets="compile" />'; Replace = '<PackageReference Include="Microsoft.Data.Sqlite.Core" Version="10.0.12" />' }) },
    @{ Id = 'C4-A4'; Suite = 'Audit'; What = 'an ATTACH statement appears in the Library SQL (A-06)'
       Edits = @(@{ File = "$L/Sql/OpenSql.cs"; Find = 'internal const string SetSynchronous = "PRAGMA synchronous = FULL";'; Replace = "internal const string SetSynchronous = ""PRAGMA synchronous = FULL"";`n    internal const string MutantAttach = ""ATTACH DATABASE 'x.db' AS other"";" }) }
)
. (Join-Path $PSScriptRoot 'C4RepairMutants.ps1')
. (Join-Path $PSScriptRoot 'C4FixMutants.ps1')
if ($Set -eq 'Original') { } elseif ($Set -eq 'Repair') { $mutants = @($repairMutants) } elseif ($Set -eq 'Fixes') { $mutants = @($fixMutants) } else { $mutants = @($mutants) + @($repairMutants) + @($fixMutants) }
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

# the audits read the package cache by its repository-relative path (A-20 hashes the native DLL there): give the copy a junction to
# -Source's packages folder (removing a junction deletes the link only, never the target)
if (Test-Path -LiteralPath (Join-Path $Source 'packages')) { New-Item -ItemType Junction -Path (Join-Path $copy 'packages') -Target (Join-Path $Source 'packages') | Out-Null }

$suites = @{
    Library = @{ Project = 'tests/StorageInventory.Library.Tests'; Dll = 'StorageInventory.Library.Tests.dll'; Filter = @() }
    Audit   = @{ Project = 'tests/StorageInventory.IntegrationTests'; Dll = 'StorageInventory.IntegrationTests.dll'; Filter = @('SecurityAuditTests', 'LibrarySecurityAuditTests') }
}

function Invoke-Suite([string] $Name, [string[]] $FilterOverride) {
    $suite = $suites[$Name]
    $filter = if ($FilterOverride) { $FilterOverride } else { $suite.Filter }
    $project = Join-Path $copy $suite.Project
    $build = Invoke-Native $Dotnet (@('build', $project, '-c', 'Release', '--nologo', '-v', 'q') + $BuildArgs) $copy
    if ($build.Code -ne 0) { return @{ Compiled = $false; Failed = @(); Summary = ($build.Output -split "`r?`n" | Where-Object { $_ -match 'error' } | Select-Object -First 3) -join ' | ' } }
    $dll = Get-ChildItem -LiteralPath (Join-Path $project 'bin/Release') -Recurse -Filter $suite.Dll | Select-Object -First 1
    # the real-process tests start their own executable again (--child): run the apphost, not 'dotnet <dll>'
    $exe = [IO.Path]::ChangeExtension($dll.FullName, '.exe')
    $run = Invoke-Native $exe $filter $copy
    $failed = @($run.Output -split "`r?`n" | Where-Object { $_ -match '^FAIL\s+(\S+)' } | ForEach-Object { ($_ -replace '^FAIL\s+(\S+).*', '$1') })
    $summary = ($run.Output -split "`r?`n" | Where-Object { $_ -match '^RESULT ' } | Select-Object -Last 1)
    return @{ Compiled = $true; Failed = $failed; Summary = $summary }
}

# every edit must match exactly once before any build is started (a typo should cost seconds, not an hour)
foreach ($m in $mutants) {
    foreach ($edit in $m.Edits) {
        $text = [IO.File]::ReadAllText((Join-Path $copy $edit.File)).Replace("`r`n", "`n")
        $count = ([regex]::Matches($text, [regex]::Escape($edit.Find.Replace("`r`n", "`n")))).Count
        if ($count -ne 1) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue; throw "mutant $($m.Id): the text to replace occurs $count times in $($edit.File), expected exactly once" }
    }
}

$results = New-Object System.Collections.Generic.List[object]
try {
    $baseline = @{}
    function Key($m) { "$($m.Suite)|$(@($m.Filter) -join ',')" }
    foreach ($key in @($mutants | ForEach-Object { Key $_ } | Select-Object -Unique)) {
        $first = @($mutants | Where-Object { (Key $_) -eq $key })[0]
        $baseline[$key] = Invoke-Suite $first.Suite $first.Filter
        if (-not $baseline[$key].Compiled) { throw "The unmutated $key suite does not build: $($baseline[$key].Summary)" }
        Write-Host ("baseline {0,-60} {1}" -f $key, $baseline[$key].Summary)
    }

    foreach ($m in $mutants) {
        $originals = @{}
        try {
            foreach ($edit in $m.Edits) {
                $path = Join-Path $copy $edit.File
                if (-not $originals.ContainsKey($path)) { $originals[$path] = [IO.File]::ReadAllText($path) }
                $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
                $find = $edit.Find.Replace("`r`n", "`n"); $replace = $edit.Replace.Replace("`r`n", "`n")
                $count = ([regex]::Matches($text, [regex]::Escape($find))).Count
                if ($count -ne 1) { throw "mutant $($m.Id): '$find' occurs $count times in $($edit.File), expected exactly once" }
                [IO.File]::WriteAllText($path, $text.Replace($find, $replace), (New-Object Text.UTF8Encoding($false)))
            }
            $r = Invoke-Suite $m.Suite $m.Filter
            if (-not $r.Compiled) { $verdict = 'NOT COMPILED'; $killers = @(); $detail = $r.Summary }
            else {
                $killers = @($r.Failed | Where-Object { $baseline[(Key $m)].Failed -notcontains $_ })
                $verdict = if ($killers.Count -gt 0) { 'KILLED' } else { 'SURVIVED' }
                $detail = $r.Summary
            }
        }
        finally { foreach ($path in $originals.Keys) { [IO.File]::WriteAllText($path, $originals[$path], (New-Object Text.UTF8Encoding($false))) } }
        $results.Add([pscustomobject]@{ Id = $m.Id; Suite = $m.Suite; What = $m.What; Verdict = $verdict; Killers = $killers; Detail = $detail })
        Write-Host ("{0,-6} {1,-9} {2,-12} {3}" -f $m.Id, $m.Suite, $verdict, (($results[$results.Count - 1].Killers | Select-Object -First 3) -join ', '))
        if ($verdict -eq 'NOT COMPILED') { Write-Host "       $detail" }
    }
}
finally {
    if (Test-Path -LiteralPath (Join-Path $copy 'packages')) { (Get-Item -LiteralPath (Join-Path $copy 'packages')).Delete() }
    if (-not $KeepWork -and (Test-Path -LiteralPath (Join-Path $Work 'tree'))) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
}

$md = New-Object System.Collections.Generic.List[string]
$md.Add("| Mutant | Suite | Defect | Result | Failing tests that are not failing without it |")
$md.Add("|---|---|---|---|---|")
foreach ($r in $results) {
    $tests = if ($r.Killers.Count -gt 0) { (($r.Killers | Select-Object -First 4 | ForEach-Object { '`' + $_ + '`' }) -join '<br>') + $(if ($r.Killers.Count -gt 4) { "<br>(+$($r.Killers.Count - 4) more)" }) }
    $md.Add("| $($r.Id) | $($r.Suite) | $($r.What) | **$($r.Verdict)** | $tests |")
}
$md | ForEach-Object { Write-Host $_ }
if ($Out) { Set-Content -LiteralPath $Out -Value ($md -join "`n") -Encoding UTF8 }
$notKilled = @($results | Where-Object { $_.Verdict -ne 'KILLED' })
Write-Host ("{0} mutants, {1} killed, {2} survived, {3} not compiled" -f $results.Count, @($results | Where-Object Verdict -eq 'KILLED').Count, @($results | Where-Object Verdict -eq 'SURVIVED').Count, @($results | Where-Object Verdict -eq 'NOT COMPILED').Count)
exit $(if ($notKilled.Count -eq 0) { 0 } else { 1 })
