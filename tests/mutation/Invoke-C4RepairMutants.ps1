<#
.SYNOPSIS
    v1.1 C4 implementation repair: deliberate defects in the A-25 audit, the lease guards, the lock-first ordering and the constant-SQL
    rule, run against a scratch copy of the tree to show that the repaired audits and tests kill each one, for the intended reason.
.DESCRIPTION
    Same method as Invoke-C4Mutants.ps1: a mutant is exact text replacements (each must match exactly once, checked for every mutant
    before any build); the source tree is never modified; a mutant is KILLED when a test fails that passes in the unmutated baseline of
    the same suite AND that test is one the mutant is meant to be caught by ("Intended"); KILLED (UNINTENDED) when only other tests
    fail (the mutant is dead, but not for the reason the audit was written for: look at it); SURVIVED when no test fails that did not
    fail in the baseline; NOT COMPILED when the edit broke the build (inconclusive).

    Suites:  Audit   = tests\StorageInventory.IntegrationTests filtered to LeaseAuditTests, SecurityAuditTests, LibrarySecurityAuditTests
             Library = tests\StorageInventory.Library.Tests filtered to LeaseRuntimeAbuse, LockFirst, SqlCoverage, LeaseValidity
    After the mutants, the whole Library.Tests suite and the whole audit filter are run once on the unmutated copy for the baseline
    counts in the report.

    The scratch copy holds git-tracked and untracked-but-not-ignored files, so the SDK and the NuGet cache are not in it: the script
    uses the SDK and packages of the main checkout (-ToolRoot, found from git's common directory by default).
.PARAMETER Source      The git working tree to mutate (default: this repository).
.PARAMETER ToolRoot    A checkout that holds tools\dotnet and packages (default: the main working tree of -Source's repository).
.PARAMETER Work        Scratch folder, created fresh and removed at the end (keep the path short: the Library tests nest deeply).
.PARAMETER Only        Run only these mutant ids.
.PARAMETER Out         Write the results as Markdown to this file (must not exist).
.PARAMETER KeepWork    Leave the scratch folder in place.
#>
param(
    [string] $Source = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string] $ToolRoot,
    [string] $Work = (Join-Path ([IO.Path]::GetTempPath()) ('SiRm_' + [guid]::NewGuid().ToString('N').Substring(0, 6))),
    [string[]] $Only,
    [string] $Out,
    [switch] $KeepWork
)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path
if (Test-Path -LiteralPath $Work) { throw "-Work '$Work' already exists; the script only deletes a folder it made itself." }
if ($Out -and (Test-Path -LiteralPath $Out)) { throw "-Out '$Out' already exists; the script does not overwrite files." }
function Invoke-Native([string] $File, [string[]] $Arguments, [string] $In) {
    Push-Location $In
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns a native command's stderr into a terminating error under 'Stop'
    try { $output = & $File @Arguments 2>&1 | Out-String; return @{ Code = $LASTEXITCODE; Output = $output } }
    finally { $ErrorActionPreference = $saved; Pop-Location }
}
if (-not $ToolRoot) {
    $common = (Invoke-Native 'git' @('rev-parse', '--path-format=absolute', '--git-common-dir') $Source).Output.Trim()
    $ToolRoot = if ($common) { Split-Path -Parent $common } else { $Source }
    if (-not (Test-Path -LiteralPath (Join-Path $ToolRoot 'tools\dotnet\dotnet.exe'))) { $ToolRoot = $Source }
}
$Dotnet = Join-Path $ToolRoot 'tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $Dotnet)) { throw "tools\dotnet is missing in '$ToolRoot': run tools\fetch-tools.ps1 there, or pass -ToolRoot." }
$state = Join-Path $ToolRoot 'tools\dotnet-state'
$env:DOTNET_CLI_HOME = Join-Path $state 'home'
$env:APPDATA = Join-Path $state 'appdata'
$env:NUGET_PACKAGES = Join-Path $ToolRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $state 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $state 'nuget-plugins'
$env:DOTNET_ROOT = Join-Path $ToolRoot 'tools\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:MSBUILDDISABLENODEREUSE = '1'; $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
$BuildArgs = @('-nodeReuse:false')

# ---------------------------------------------------------------- the mutants
$L = 'src/StorageInventory.Library'
$A = 'tests/StorageInventory.IntegrationTests/IlAudit.cs'
$D = "$L/LibraryDatabase.cs"
$S = "$L/LibrarySession.cs"
$I = "$L/Interlock/LibraryInterlock.cs"
function Edit([string] $File, [string] $Find, [string] $Replace) { @{ File = $File; Find = $Find; Replace = $Replace } }
function Mutant([string] $Id, [string] $Suite, [string] $What, [string[]] $Intended, [object[]] $Edits) { @{ Id = $Id; Suite = $Suite; What = $What; Intended = $Intended; Edits = $Edits } }

$mutants = @(
    # ---- the audit itself ----
    (Mutant 'RA-01' 'Audit' 'the lease-bound-type exemption is back in the A-25 audit (any instance method of a type that holds a lease has authority)' @('LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture', 'LeaseAuditTests.A_25_the_earlier_relaxed_audit') @(
        (Edit $A 'internal static readonly Options Frozen = new();' 'internal static readonly Options Frozen = new() { LeaseBoundTypeExemption = true };'))),
    (Mutant 'RA-02' 'Audit' 'the lease-producer exemption is back (a method that asks the interlock for a lease has authority)' @('LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture', 'LeaseAuditTests.A_25_the_earlier_relaxed_audit') @(
        (Edit $A 'internal static readonly Options Frozen = new();' 'internal static readonly Options Frozen = new() { LeaseProducerExemption = true };'))),
    (Mutant 'RA-03' 'Audit' 'transitive private authority is back (a private helper is allowed when its callers are, a chain)' @('LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture', 'LeaseAuditTests.A_25_the_earlier_relaxed_audit') @(
        (Edit $A 'internal static readonly Options Frozen = new();' 'internal static readonly Options Frozen = new() { TransitivePrivateAuthority = true };'))),
    (Mutant 'RA-04' 'Audit' 'File.Move is dropped from the primitive list' @('LeaseAuditTests.A_25_the_primitive_list_names_every_operation', 'LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture') @(
        (Edit $A '"Open", "OpenWrite", "OpenHandle", "Create", "CreateText", "Delete", "Move", "Copy", "Replace", "Encrypt", "Decrypt",' '"Open", "OpenWrite", "OpenHandle", "Create", "CreateText", "Delete", "Copy", "Replace", "Encrypt", "Decrypt",'))),
    (Mutant 'RA-05' 'Audit' 'sqlite3_step is dropped from the primitive list' @('LeaseAuditTests.A_25_the_primitive_list_names_every_operation', 'LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture') @(
        (Edit $A '"sqlite3_step", "sqlite3_prepare", "sqlite3_prepare_v2",' '"sqlite3_prepare", "sqlite3_prepare_v2",'))),
    (Mutant 'RA-06' 'Audit' 'SqliteCommand.ExecuteNonQuery (any command on a connection) is dropped from the primitive list' @('LeaseAuditTests.A_25_the_primitive_list_names_every_operation', 'LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture') @(
        (Edit $A 'private static readonly string[] CommandPrimitives = ["ExecuteNonQuery", "ExecuteScalar",' 'private static readonly string[] CommandPrimitives = ["ExecuteScalar",'))),
    (Mutant 'RA-07' 'Audit' 'the audit stops following interface and virtual dispatch' @('LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture') @(
        (Edit $A 'if (!call.Dispatches) yield break;' 'if (!call.Dispatches || call.Dispatches) yield break;'))),
    (Mutant 'RA-08' 'Audit' 'a private helper may be called from a method of another type that takes a lease (the "own type" clause is dropped)' @('LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture') @(
        (Edit $A 'return who.All(c => c.Type == unit.Type && c.TakesLease);' 'return who.All(c => c.TakesLease);'))),
    # (C4R-M05 moved both keys into IlAudit.MethodKey, so the one edit below is the same defect as the two it replaced)
    (Mutant 'RA-09' 'Audit' 'callees are matched by name only (overloads hide behind each other)' @('LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture', 'LeaseAuditTests.A_25_part_a_accepts_the_compliant_fixtures') @(
        (Edit $A '        $"{type}::{name}{(genericArity > 0 ? "`" + genericArity : "")}({string.Join(",", parameters)}){(name is "op_Implicit" or "op_Explicit" ? "->" + returnType : "")}";' '        $"{type}::{name}";'))),
    (Mutant 'RA-10' 'Audit' 'the lock-first ordering check never fires (offsets are not compared)' @('LeaseAuditTests.C4_M17_lock_first_rule_rejects') @(
        (Edit $A 'else if (call.Offset < lockAt)' 'else if (call.Offset < -1)'))),
    # ---- the production lease guards ----
    (Mutant 'RA-11' 'Audit' 'WriterConnection.Configure is lease-less again (the earlier shape)' @('LeaseAuditTests.A_25_every_mutation_in_every_first_party_assembly', 'LeaseAuditTests.A_25_the_primitives_have_exactly') @(
        (Edit $D "    internal void Configure(MutationLease lease, bool importCache)`n    {`n        Guard(lease, ""configure the connection"");`n" "    internal void Configure(bool importCache)`n    {`n"),
        (Edit $D 'writer.Configure(lease, importCache);' 'writer.Configure(importCache);'))),
    (Mutant 'RA-12' 'Audit' 'R-20: a lease-less instance method of WriterConnection executes a command on the writer' @('LeaseAuditTests.A_25_every_mutation_in_every_first_party_assembly', 'LeaseAuditTests.A_25_the_lease_less_members_of_WriterConnection') @(
        (Edit $D '    internal bool InTransaction => _inTransaction;' "    internal bool InTransaction => _inTransaction;`n`n    internal void Poke()`n    {`n        using var command = _connection.CreateCommand();`n        command.CommandText = OpenSql.SelectEngine;`n        command.ExecuteScalar();`n    }"))),
    (Mutant 'RA-13' 'Audit' 'R-19: a class that holds a lease and creates the database from an instance method without a lease parameter' @('LeaseAuditTests.A_25_every_mutation_in_every_first_party_assembly') @(
        (Edit "$L/ReaderWriterGate.cs" 'internal sealed class ReaderWriterGate' "internal sealed class MutantHolder(LibraryStore store, MutationLease lease)`n{`n    internal bool Create() => store.CreateEmptyDatabase(lease);`n}`n`ninternal sealed class ReaderWriterGate"))),
    (Mutant 'RA-14' 'Audit' 'an implementation of IQueryRunner keeps a writer and a lease and runs statements with them (the earlier WriterQueryRunner)' @('LeaseAuditTests.A_25_every_mutation_in_every_first_party_assembly') @(
        (Edit "$L/ReaderWriterGate.cs" 'internal sealed class ReaderWriterGate' "internal sealed class MutantRunner(WriterConnection writer, MutationLease lease) : IQueryRunner`n{`n    public object? Scalar(string sql, params (string Name, object? Value)[] parameters) => writer.Scalar(lease, sql, parameters);`n`n    public void Rows(string sql, Action<IRowReader> each, params (string Name, object? Value)[] parameters) => writer.Rows(lease, sql, each, parameters);`n}`n`ninternal sealed class ReaderWriterGate"))),
    (Mutant 'RA-15' 'Audit' 'a default(MutationLease) outside the interlock (A-25 part c)' @('LeaseAuditTests.A_25_part_c_leases_are_constructed_only') @(
        (Edit $S '    internal long NewCaptureId() => Interlocked.Increment(ref _captureCounter);' "    internal long NewCaptureId() => Interlocked.Increment(ref _captureCounter);`n`n    internal static MutationLease MutantZero() => default;"))),
    (Mutant 'RA-16' 'Library' 'Guard accepts a default lease and asks the lease''s own interlock (lease.Owner?.RequireOpenedBy)' @('LeaseRuntimeAbuseTests.A_default_lease', 'LeaseRuntimeAbuseTests.A_lease_of_another') @(
        (Edit $D 'internal void Guard(MutationLease lease, string what) => _interlock.RequireOpenedBy(lease, _openedBy, _operation, what, _allowed);' 'internal void Guard(MutationLease lease, string what) => lease.Owner?.RequireOpenedBy(lease, _openedBy, _operation, what, _allowed);'))),
    (Mutant 'RA-17' 'Library' 'RequireOpenedBy does not compare the opener''s id (any current lease of the interlock of an allowed kind passes)' @('LeaseRuntimeAbuseTests.A_current_lease_of_the_same_interlock', 'LeaseValidityTests') @(
        (Edit $I '            if (CheckLocked(lease, allowed) is null && lease.Id == openedBy.Id) return;' '            if (CheckLocked(lease, allowed) is null) return;'),
        (Edit $I '            failure = CheckLocked(lease, allowed) ?? (lease.Id != openedBy.Id ? "the lease is not the one that opened this connection" : null);' '            failure = CheckLocked(lease, allowed);'))),
    (Mutant 'RA-18' 'Library' 'Rollback no longer checks that the lease is the opener''s' @('LeaseRuntimeAbuseTests.Rollback_after', 'LeaseRuntimeAbuseTests.A_default_lease', 'LeaseRuntimeAbuseTests.A_lease_of_another') @(
        (Edit $D '        _interlock.RequireSameLease(lease, _openedBy, _operation, "roll back");' ''))),
    (Mutant 'RA-19' 'Library' 'BeginCancellationScope does not check the lease' @('LeaseRuntimeAbuseTests.A_default_lease', 'LeaseRuntimeAbuseTests.A_stale_lease') @(
        (Edit $D '        Guard(lease, "start a cancellation scope");' ''))),
    (Mutant 'RA-20' 'Library' 'WriterStatement.ExecuteScalar does not check the lease' @('LeaseRuntimeAbuseTests.A_default_lease', 'LeaseRuntimeAbuseTests.A_stale_lease') @(
        (Edit $D "    internal object? ExecuteScalar(MutationLease lease)`n    {`n        _owner.Guard(lease, ""execute"");`n" "    internal object? ExecuteScalar(MutationLease lease)`n    {`n"))),
    # ---- lock first (C4-M17) ----
    (Mutant 'RA-21' 'Audit' 'R-03: the header of library.sqlite3 is read BEFORE the writer lock in DeriveCore' @('LeaseAuditTests.C4_M17_every_member_access_follows_the_writer_lock', 'LockFirstTests') @(
        (Edit $S "        // The writer lock FIRST (CONC-01): before any member is inspected, created or renamed.`n        var locked = Store.AcquireWriterLock(lease);" "        // The writer lock FIRST (CONC-01): before any member is inspected, created or renamed.`n        _ = Store.ReadHeader();`n        var locked = Store.AcquireWriterLock(lease);"))),
    (Mutant 'RA-22' 'Audit' 'R-03: File.Exists(library.sqlite3) is called BEFORE the writer lock in DeriveCore' @('LeaseAuditTests.C4_M17_only_LibraryStore_touches_the_file_system') @(
        (Edit $S "        // The writer lock FIRST (CONC-01): before any member is inspected, created or renamed.`n        var locked = Store.AcquireWriterLock(lease);" "        // The writer lock FIRST (CONC-01): before any member is inspected, created or renamed.`n        _ = System.IO.File.Exists(System.IO.Path.Combine(Store.Directory, LibraryNames.MainFile));`n        var locked = Store.AcquireWriterLock(lease);"))),
    (Mutant 'RA-23' 'Library' 'the header of library.sqlite3 is read BEFORE the writer lock (run-time view of R-03)' @('LockFirstTests.A_locked_main_file_that_cannot_even_be_opened') @(
        (Edit $S "        // The writer lock FIRST (CONC-01): before any member is inspected, created or renamed.`n        var locked = Store.AcquireWriterLock(lease);" "        // The writer lock FIRST (CONC-01): before any member is inspected, created or renamed.`n        _ = Store.ReadHeader();`n        var locked = Store.AcquireWriterLock(lease);"))),
    (Mutant 'RA-24' 'Audit' 'a new method of LibrarySession inspects the members and never takes the lock' @('LeaseAuditTests.C4_M17_every_member_access_follows_the_writer_lock') @(
        (Edit $S '    internal long NewCaptureId() => Interlocked.Increment(ref _captureCounter);' "    internal long NewCaptureId() => Interlocked.Increment(ref _captureCounter);`n`n    internal bool MutantExists() => Store.InspectMembersUnderLock().Main.Exists;"))),
    # ---- constant SQL (C4-M13) ----
    (Mutant 'RA-25' 'Audit' 'a static readonly built SQL string in Sql/OpenSql.cs' @('LeaseAuditTests.C4_M13_every_field_of_the_Sql_classes') @(
        (Edit "$L/Sql/OpenSql.cs" '    internal const string QuickCheck = "PRAGMA quick_check";' "    internal const string QuickCheck = ""PRAGMA quick_check"";`n    internal static readonly string MutantBuilt = ""SELECT count(*) FROM "" + ""snapshot"";"))),
    (Mutant 'RA-26' 'Audit' 'a forwarder (Execute) is fed a built string' @('LeaseAuditTests.C4_M13_every_field_of_the_Sql_classes') @(
        (Edit $S '                Execute(writer, lease, SchemaSql.SetApplicationId);' "                var statementText = SchemaSql.SetApplicationId + """";`n                Execute(writer, lease, statementText);"))),
    (Mutant 'RA-27' 'Library' 'a DML statement moves into a class whose name ends in Sql but is not static (the plan check would not see it)' @('SqlCoverageTests.A_24_the_plan_check_sees_every_DML_statement') @(
        (Edit "$L/Sql/OpenSql.cs" 'internal static class OpenSql' "internal sealed class MutantSql`n{`n    internal const string Extra = ""SELECT count(*) FROM snapshot"";`n}`n`ninternal static class OpenSql"),
        (Edit 'tests/StorageInventory.Library.Tests/QueryPlanTests.cs' '.Where(t => t.Name.EndsWith("Sql", StringComparison.Ordinal) && t.Namespace == "StorageInventory.Library")' '.Where(t => t.Name.EndsWith("Sql", StringComparison.Ordinal) && t.IsAbstract && t.IsSealed && t.Namespace == "StorageInventory.Library")')))
)
if ($Only) { $mutants = @($mutants | Where-Object { $Only -contains $_.Id }) }

# ---------------------------------------------------------------- the scratch copy
if (-not (Test-Path -LiteralPath (Join-Path $Source '.git'))) { throw "-Source '$Source' is not a git working tree (the copy is taken from 'git ls-files')." }
$files = (Invoke-Native 'git' @('ls-files', '--cached', '--others', '--exclude-standard') $Source).Output -split "`r?`n" | Where-Object { $_ }
$commit = (Invoke-Native 'git' @('rev-parse', 'HEAD') $Source).Output.Trim()
$dirty = (Invoke-Native 'git' @('status', '--porcelain') $Source).Output.Trim().Length -gt 0
New-Item -ItemType Directory -Path $Work | Out-Null
$Work = (Resolve-Path -LiteralPath $Work).Path
# the Library tests delete every other run's scratch folder under the temp folder at start-up (World.InitialiseRunRoot): a private temp
# folder keeps parallel runs (another working tree, another agent) from deleting this one's files under it
$privateTemp = Join-Path $Work 'tmp'
[void][IO.Directory]::CreateDirectory($privateTemp)
$env:TEMP = $privateTemp; $env:TMP = $privateTemp
$copy = Join-Path $Work 'tree'
foreach ($file in $files) {
    $from = Join-Path $Source $file
    if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { continue }
    $to = Join-Path $copy $file
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $to))
    Copy-Item -LiteralPath $from -Destination $to
}
# the audits read the package cache by its repository-relative path (A-20 hashes the native DLL there): give the copy a junction to
# the tool root's packages folder (removing a junction deletes the link only, never the target)
$packages = Join-Path $ToolRoot 'packages'
if (Test-Path -LiteralPath $packages) { New-Item -ItemType Junction -Path (Join-Path $copy 'packages') -Target $packages | Out-Null }

$suites = @{
    Library = @{ Project = 'tests/StorageInventory.Library.Tests'; Dll = 'StorageInventory.Library.Tests.dll'; Filter = @('LeaseRuntimeAbuse', 'LockFirst', 'SqlCoverage', 'LeaseValidity') }
    Audit   = @{ Project = 'tests/StorageInventory.IntegrationTests'; Dll = 'StorageInventory.IntegrationTests.dll'; Filter = @('LeaseAuditTests', 'SecurityAuditTests', 'LibrarySecurityAuditTests') }
    LibraryAll = @{ Project = 'tests/StorageInventory.Library.Tests'; Dll = 'StorageInventory.Library.Tests.dll'; Filter = @() }
}

function Invoke-Suite([string] $Name) {
    $suite = $suites[$Name]
    $project = Join-Path $copy $suite.Project
    $build = Invoke-Native $Dotnet (@('build', $project, '-c', 'Release', '--nologo', '-v', 'q') + $BuildArgs) $copy
    if ($build.Code -ne 0) { return @{ Compiled = $false; Failed = @(); Passed = @(); Summary = ($build.Output -split "`r?`n" | Where-Object { $_ -match 'error' } | Select-Object -First 3) -join ' | ' } }
    $dll = Get-ChildItem -LiteralPath (Join-Path $project 'bin/Release') -Recurse -Filter $suite.Dll | Select-Object -First 1
    # the real-process tests start their own executable again (--child): run the apphost, not 'dotnet <dll>'
    $exe = [IO.Path]::ChangeExtension($dll.FullName, '.exe')
    $run = Invoke-Native $exe $suite.Filter $copy
    $failed = @($run.Output -split "`r?`n" | Where-Object { $_ -match '^FAIL\s+(\S+)' } | ForEach-Object { ($_ -replace '^FAIL\s+(\S+).*', '$1') })
    $passed = @($run.Output -split "`r?`n" | Where-Object { $_ -match '^PASS\s+(\S+)' } | ForEach-Object { ($_ -replace '^PASS\s+(\S+).*', '$1') })
    $summary = ($run.Output -split "`r?`n" | Where-Object { $_ -match '^RESULT ' } | Select-Object -Last 1)
    return @{ Compiled = $true; Failed = $failed; Passed = $passed; Summary = $summary }
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
$baseline = @{}
try {
    foreach ($name in @($mutants | ForEach-Object { $_.Suite } | Select-Object -Unique)) {
        $baseline[$name] = Invoke-Suite $name
        if (-not $baseline[$name].Compiled) { throw "The unmutated $name suite does not build: $($baseline[$name].Summary)" }
        Write-Host ("baseline {0,-8} {1}" -f $name, $baseline[$name].Summary)
        if ($baseline[$name].Failed.Count -gt 0) { throw "The unmutated $name suite fails: $($baseline[$name].Failed -join ', ')" }
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
            $r = Invoke-Suite $m.Suite
            if (-not $r.Compiled) { $verdict = 'NOT COMPILED'; $killers = @(); $detail = $r.Summary }
            else {
                $killers = @($r.Failed | Where-Object { $baseline[$m.Suite].Failed -notcontains $_ })
                $intended = @($killers | Where-Object { $name = $_; @($m.Intended | Where-Object { $name -like "*$_*" }).Count -gt 0 })
                $verdict = if ($intended.Count -gt 0) { 'KILLED' } elseif ($killers.Count -gt 0) { 'KILLED (UNINTENDED)' } else { 'SURVIVED' }
                $detail = $r.Summary
            }
        }
        finally { foreach ($path in $originals.Keys) { [IO.File]::WriteAllText($path, $originals[$path], (New-Object Text.UTF8Encoding($false))) } }
        $results.Add([pscustomobject]@{ Id = $m.Id; Suite = $m.Suite; What = $m.What; Verdict = $verdict; Killers = $killers; Intended = $m.Intended; Detail = $detail })
        Write-Host ("{0,-6} {1,-9} {2,-20} {3}" -f $m.Id, $m.Suite, $verdict, (($results[$results.Count - 1].Killers | Select-Object -First 3) -join ', '))
        if ($verdict -eq 'NOT COMPILED') { Write-Host "       $detail" }
    }

    # the whole suites once, unmutated: the baseline counts of the report
    foreach ($name in @('LibraryAll', 'Audit')) {
        if (-not $baseline.ContainsKey($name) -or $name -eq 'LibraryAll') { $baseline[$name] = Invoke-Suite $name }
        Write-Host ("whole {0,-10} {1}" -f $name, $baseline[$name].Summary)
    }
}
finally {
    if (Test-Path -LiteralPath (Join-Path $copy 'packages')) { (Get-Item -LiteralPath (Join-Path $copy 'packages')).Delete() }
    if (-not $KeepWork -and (Test-Path -LiteralPath (Join-Path $Work 'tree'))) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
}

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# C4 repair: audit and lease mutants")
$md.Add("")
$md.Add("Commit ``$commit``$(if ($dirty) { ' (the working tree had uncommitted changes when this ran)' }), run on $((Get-Date).ToString('yyyy-MM-dd HH:mm')) by ``tests/mutation/Invoke-C4RepairMutants.ps1``.")
$md.Add("")
$md.Add("Baseline (unmutated): " + (($baseline.Keys | Sort-Object | ForEach-Object { "$_ = $($baseline[$_].Summary -replace '^RESULT ', '')" }) -join '; ') + ".")
$md.Add("")
$md.Add("A mutant is KILLED when a test fails that passes in the unmutated baseline of the same suite and that test is one the mutant is meant to be caught by; KILLED (UNINTENDED) when only other tests fail; SURVIVED when none does; NOT COMPILED when the edit broke the build.")
$md.Add("")
$md.Add("| Mutant | Suite | Defect | Result | Intended | Failing tests that are not failing without it |")
$md.Add("|---|---|---|---|---|---|")
foreach ($r in $results) {
    $tests = if ($r.Killers.Count -gt 0) { (($r.Killers | Select-Object -First 5 | ForEach-Object { '`' + $_ + '`' }) -join '<br>') + $(if ($r.Killers.Count -gt 5) { "<br>(+$($r.Killers.Count - 5) more)" }) }
    $md.Add("| $($r.Id) | $($r.Suite) | $($r.What) | **$($r.Verdict)** | $(($r.Intended | ForEach-Object { '`' + $_ + '`' }) -join '<br>') | $tests |")
}
$md.Add("")
$md.Add(("{0} mutants: {1} killed, {2} killed unintended, {3} survived, {4} not compiled." -f $results.Count, @($results | Where-Object Verdict -eq 'KILLED').Count, @($results | Where-Object Verdict -eq 'KILLED (UNINTENDED)').Count, @($results | Where-Object Verdict -eq 'SURVIVED').Count, @($results | Where-Object Verdict -eq 'NOT COMPILED').Count))
$md | ForEach-Object { Write-Host $_ }
if ($Out) { Set-Content -LiteralPath $Out -Value ($md -join "`n") -Encoding UTF8 }
$notKilled = @($results | Where-Object { $_.Verdict -ne 'KILLED' })
exit $(if ($notKilled.Count -eq 0) { 0 } else { 1 })
