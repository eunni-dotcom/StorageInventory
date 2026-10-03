# The C4 implementation repair's mutants (dot-sourced by Invoke-C4Mutants.ps1; it defines $repairMutants). Each is exact text
# replacements in production files, run against a scratch copy; a mutant is KILLED only when a test of its own Filter fails that does
# not fail in the unmutated run. The Filter names the test classes the defect is MEANT to be caught by, so a kill is for the intended
# reason: a mutant of one verification invariant is run against VerificationInvariantTests alone, and so on.
#
# Always-false replacements use a runtime expression (never the constant false): the compiler reports unreachable code (CS0162) as a
# warning, the build treats warnings as errors, and a mutant that does not compile proves nothing.

$L = 'src/StorageInventory.Library'
$V = "$L/Import/SnapshotVerifier.cs"
$S = "$L/Import/SnapshotImporter.cs"
$X = "$L/Sql/ImportSql.cs"
$N = "$L/LibrarySession.cs"
$D = "$L/LibraryDatabase.cs"
$T = "$L/LibraryStore.cs"
$vFilter = @('VerificationInvariantTests')
$cFilter = @('CancellationTests', 'SpaceGuardTests')
$gFilter = @('SpaceGuardTests')
$kFilter = @('ClassCReportingTests')
$wFilter = @('WriterOpenGuardTests')
$hFilter = @('HeaderPreCheckTests')
$dFilter = @('PerSourceDictionaryTests', 'SchemaTests', 'ImportTests')

function Replace-One([string] $File, [string] $Find, [string] $Replace) { @{ File = $File; Find = $Find; Replace = $Replace } }

$repairMutants = @(
    # ---- §9.5 verification: one mutant per invariant (C4-M03, the reviewer's R-10 and R-11 included)
    @{ Id = 'C4R-V01'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 1 (one root row, index 0) is not evaluated'; Edits = @(Replace-One $V 'if (rootRows != 1 || rootIndex != 0) return' 'if (rootRows < 0) return') },
    @{ Id = 'C4R-V02'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 2 (folder rows refer to folder paths of the source) is not evaluated'; Edits = @(Replace-One $V 'if (Count(q, ImportSql.VerifyFolderPathsOfSource, snapshotAndSource) != 0) return' 'if (Count(q, ImportSql.VerifyFolderPathsOfSource, snapshotAndSource) < 0) return') },
    @{ Id = 'C4R-V03'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 3 (parent closure) is not evaluated (reviewer mutant R-10)'; Edits = @(Replace-One $V 'if (Count(q, ImportSql.VerifyParentClosure, snapshot) != 0) return' 'if (Count(q, ImportSql.VerifyParentClosure, snapshot) < 0) return') },
    @{ Id = 'C4R-V04'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 4 for file names is not evaluated (reviewer mutant R-11)'; Edits = @(Replace-One $V 'if (Count(q, ImportSql.VerifyFileNames, snapshotAndSource) != 0) return' 'if (Count(q, ImportSql.VerifyFileNames, snapshotAndSource) < 0) return') },
    @{ Id = 'C4R-V05'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 4 for folder names is not evaluated'; Edits = @(Replace-One $V 'if (Count(q, ImportSql.VerifyFolderNames, snapshotAndSource) != 0) return' 'if (Count(q, ImportSql.VerifyFolderNames, snapshotAndSource) < 0) return') },
    @{ Id = 'C4R-V06'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 4 for file names drops the source condition (a name of another source passes)'; Edits = @(Replace-One $X 'LEFT JOIN name n ON n.name_id = f.name_id AND n.source_id = $source_id' 'LEFT JOIN name n ON n.name_id = f.name_id') },
    @{ Id = 'C4R-V07'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 4 for folder names drops the source condition'; Edits = @(Replace-One $X 'LEFT JOIN name n ON n.name_id = p.name_id AND n.source_id = $source_id' 'LEFT JOIN name n ON n.name_id = p.name_id') },
    @{ Id = 'C4R-V08'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 5 (direct files and bytes of a folder) is not evaluated'; Edits = @(Replace-One $V 'if (r.GetInt64(1) != files || r.GetInt64(2) != bytes) problem =' 'if (r.GetInt64(1) < 0) problem =') },
    @{ Id = 'C4R-V09'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 5 (direct subfolders of a folder) is not evaluated'; Edits = @(Replace-One $V 'else if (r.GetInt64(3) != children.GetValueOrDefault(path)) problem =' 'else if (r.GetInt64(3) < 0) problem =') },
    @{ Id = 'C4R-V10'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 6 (sealed totals against the data) is not evaluated'; Edits = @(Replace-One $V 'if (measuredFiles != files || measuredBytes != bytes || measuredFolders != folders)' 'if (measuredFiles < 0)') },
    @{ Id = 'C4R-V11'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 6 (the root totals) is not evaluated'; Edits = @(Replace-One $V 'if (rootFiles != files || rootBytes != bytes || rootSubfolders != folders - 1)' 'if (rootFiles < -5)') },
    @{ Id = 'C4R-V12'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 7 (completeness mirrors the root) is not evaluated'; Edits = @(Replace-One $V 'if ((rootComplete == 1) != expectComplete) return' 'if (rootComplete < -5) return') },
    @{ Id = 'C4R-V13'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 8 (an unlisted folder has no children or files) is not evaluated'; Edits = @(Replace-One $V 'if (Count(q, ImportSql.VerifyNoChildrenOfUnlisted, snapshotAndSource) != 0) return' 'if (Count(q, ImportSql.VerifyNoChildrenOfUnlisted, snapshotAndSource) < 0) return') },
    @{ Id = 'C4R-V14'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 9 (an unreadable or partial folder is not subtree-complete) is not evaluated'; Edits = @(Replace-One $V 'if (Count(q, ImportSql.VerifyIncompleteFolders, snapshot) != 0) return' 'if (Count(q, ImportSql.VerifyIncompleteFolders, snapshot) < 0) return') },
    @{ Id = 'C4R-V15'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 9 (incompleteness propagates to the ancestors) is not evaluated'; Edits = @(Replace-One $V 'if (Count(q, ImportSql.VerifyIncompleteAncestors, snapshot) != 0) return' 'if (Count(q, ImportSql.VerifyIncompleteAncestors, snapshot) < 0) return') },
    @{ Id = 'C4R-V16'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 10 (scan_errors counts the non-informational rows) is not evaluated'; Edits = @(Replace-One $V 'if (Count(q, ImportSql.CountRealScanErrors, snapshot) != realErrors) return' 'if (Count(q, ImportSql.CountRealScanErrors, snapshot) < -5) return') },
    @{ Id = 'C4R-V17'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 11 for file sequence numbers is not evaluated'; Edits = @(Replace-One $V 'if (seqProblem is not null) return' 'if (seqProblem is not null && files < 0) return') },
    @{ Id = 'C4R-V18'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 11 for folder discovery indexes is not evaluated'; Edits = @(Replace-One $V 'if (indexProblem is not null) return' 'if (indexProblem is not null && folders < 0) return') },
    @{ Id = 'C4R-V19'; Suite = 'Library'; Filter = $vFilter; What = 'invariant 12 (extension totals) is not evaluated'; Edits = @(Replace-One $V 'if (extFiles != files || extBytes != bytes) return' 'if (extFiles < -5) return') },

    # ---- invariant 13: the attempt carries the snapshot's run id (C4-M09)
    @{ Id = 'C4R-R01'; Suite = 'Library'; Filter = @('PerSourceDictionaryTests'); What = 'the early attempt check ignores the run id'; Edits = @(Replace-One $X 'AND capture_token = $capture_token AND run_id = $run_id AND outcome = 1";' 'AND capture_token = $capture_token AND outcome = 1";') },
    @{ Id = 'C4R-R02'; Suite = 'Library'; Filter = @('PerSourceDictionaryTests'); What = 'the final statement that publishes the attempt ignores the run id'
       Edits = @(Replace-One $X "capture_token = `$capture_token AND run_id = `$run_id AND outcome = 1`n        `"`"`";" "capture_token = `$capture_token AND outcome = 1`n        `"`"`";") },

    # ---- cancellation and IMP-11 (C4-M02, D-R3, D-R7)
    @{ Id = 'C4R-X01'; Suite = 'Library'; Filter = $cFilter; What = 'the final check before COMMIT is skipped (no last look at the save token, no final space check)'; Edits = @(Replace-One $S '        GuardPoint(writer, lease, state, options, lengths, SpaceCheckKind.Final);' '        // mutant: no final check') },
    @{ Id = 'C4R-X02'; Suite = 'Library'; Filter = $gFilter; What = 'the pending growth is the old fixed 32 MiB allowance instead of page_count x page size - main length'; Edits = @(Replace-One $S 'check = new SpaceCheck(kind, PendingGrowth(pageCount, pageSize, main), pageCount, pageSize, main, journal, state.Rows);' 'check = new SpaceCheck(kind, 32L * 1024 * 1024, pageCount, pageSize, main, journal, state.Rows);') },
    @{ Id = 'C4R-X03'; Suite = 'Library'; Filter = $gFilter; What = 'the row check cadence is 16,384 rows instead of 4,096'; Edits = @(Replace-One $S 'internal const int GuardInterval = 4_096;' 'internal const int GuardInterval = 16_384;') },
    @{ Id = 'C4R-X04'; Suite = 'Library'; Filter = $gFilter; What = 'an exception from the space guard escapes instead of rolling back as LibraryFull'; Edits = @(Replace-One $S 'throw new ImportException(CaptureFailureKind.LibraryFull, $"The space guard failed at {kind}: {ex.Message}", ex);' 'throw;') },
    @{ Id = 'C4R-X05'; Suite = 'Library'; Filter = $gFilter; What = 'an unreadable input is ignored instead of rolling back as LibraryFull'; Edits = @(Replace-One $S 'throw new ImportException(CaptureFailureKind.LibraryFull, $"An input of the space check at {kind} could not be read, so the free space cannot be judged.", ex);' 'return;') },
    @{ Id = 'C4R-X06'; Suite = 'Library'; Filter = $gFilter; What = 'a check does not look at the save token before it asks the guard'
       Edits = @(Replace-One $S "        writer.CheckCurrent(lease);`n        state.Checker.Check();`n`n        var guard = options?.SpaceGuard;" "        writer.CheckCurrent(lease);`n`n        var guard = options?.SpaceGuard;") },
    @{ Id = 'C4R-X07'; Suite = 'Library'; Filter = $cFilter; What = 'the verification runs without the progress-callback cancellation scope'; Edits = @(Replace-One $S 'using (writer.BeginCancellationScope(lease, cancellation, state.Checker))' 'using (System.IO.Stream.Null)') },

    # ---- class C (C4-M04)
    @{ Id = 'C4R-K01'; Suite = 'Library'; Filter = $kFilter; What = 'T0 does not report a class C failure to the interlock'; Edits = @(Replace-One $N '            lease.ClassCFailure(ex);   // OBS-13: a class C failure in T0 ends in Faulted, not in a clean Idle' '            _ = ex;') },
    @{ Id = 'C4R-K02'; Suite = 'Library'; Filter = $kFilter; What = 'T-OUTCOME does not report a class C failure'
       Edits = @(Replace-One $N "return await RecordAttemptOutcomeCoreAsync(lease, attempt, outcome, failure, message, cancellation).ConfigureAwait(false);`n        }`n        catch (Exception ex) when (LibraryInterlock.IsClassC(ex))`n        {`n            lease.ClassCFailure(ex);" "return await RecordAttemptOutcomeCoreAsync(lease, attempt, outcome, failure, message, cancellation).ConfigureAwait(false);`n        }`n        catch (Exception ex) when (LibraryInterlock.IsClassC(ex))`n        {`n            _ = ex;") },
    @{ Id = 'C4R-K03'; Suite = 'Library'; Filter = $kFilter; What = 'T-DELETE does not report a class C failure'
       Edits = @(Replace-One $N "await DeleteSnapshotCoreAsync(lease, snapshotId, cancellation).ConfigureAwait(false);`n        }`n        catch (Exception ex) when (LibraryInterlock.IsClassC(ex))`n        {`n            lease.ClassCFailure(ex);" "await DeleteSnapshotCoreAsync(lease, snapshotId, cancellation).ConfigureAwait(false);`n        }`n        catch (Exception ex) when (LibraryInterlock.IsClassC(ex))`n        {`n            _ = ex;") },
    @{ Id = 'C4R-K04'; Suite = 'Library'; Filter = $kFilter; What = 'a writer that cannot be closed does not fault the interlock'; Edits = @(Replace-One $D '            _openedBy.ReleaseFailed("writer connection: " + ex.GetType().Name);' '            _ = ex;') },

    # ---- the gate and the re-checks before every writer open (C4-M10, C4-M11)
    @{ Id = 'C4R-G01'; Suite = 'Library'; Filter = $wFilter; What = 'the open takes no gate: an active reader is not preempted'
       Edits = @(Replace-One $N "using var exclusive = Gate.AcquireWriter();`n        WriterConnection writer;`n        try`n        {`n            writer = LibraryDatabase.OpenWriter(lease, Interlock, Store.MainPathUnderLock(), `"open the Library`"" "WriterConnection writer;`n        try`n        {`n            writer = LibraryDatabase.OpenWriter(lease, Interlock, Store.MainPathUnderLock(), `"open the Library`"") },
    @{ Id = 'C4R-G02'; Suite = 'Library'; Filter = $wFilter; What = 'T-IMPORT does not re-check the path and the header before it opens the writer'; Edits = @(Replace-One $N '            RevalidateBeforeWriter("T-IMPORT");' '            _ = 0;') },
    @{ Id = 'C4R-G03'; Suite = 'Library'; Filter = $wFilter; What = 'the header re-check accepts any header that exists'; Edits = @(Replace-One $N 'if (header.Outcome != HeaderOutcome.Valid) throw new LibraryChangedException(' 'if (header.Outcome == HeaderOutcome.Absent) throw new LibraryChangedException(') },
    @{ Id = 'C4R-G04'; Suite = 'Library'; Filter = $wFilter; What = 'the re-check through SQLite (application id, version, schema fingerprint) never refuses'; Edits = @(Replace-One $N 'if (CheckIdentity(writer, lease) is { } refusal) throw new LibraryChangedException(' 'if (CheckIdentity(writer, lease) is { } refusal && refusal.Reason == "never") throw new LibraryChangedException(') },

    # ---- states and the header pre-check (C4-M01, C4-M08; the reviewer's R-14, R-15, R-16)
    @{ Id = 'C4R-S01'; Suite = 'Library'; Filter = $hFilter; What = 'an empty database (application id 0, user_version 0, no schema) is not recognised before SQLite'; Edits = @(Replace-One $T "        else if (applicationId == 0 && userVersion == 0 && IsEmptyPageOne(header)) outcome = HeaderOutcome.EmptyDatabase;`n" '') },
    @{ Id = 'C4R-S02'; Suite = 'Library'; Filter = $hFilter; What = 'a database that SQLite rolled back to empty is reported Not a Library'; Edits = @(Replace-One $N 'return refusal.Reason == LibraryReason.WrongApplicationId && IsEmptyDatabase(writer, lease) ? Opened.Uninitialised : new Opened(refusal);' 'return new Opened(refusal);') },
    @{ Id = 'C4R-H01'; Suite = 'Library'; Filter = $hFilter; What = 'the magic is compared on 8 bytes only (reviewer mutant R-14)'; Edits = @(Replace-One $T 'if (!header[..16].SequenceEqual(Magic)) outcome' 'if (!header[..8].SequenceEqual(Magic.AsSpan(0, 8))) outcome') },
    @{ Id = 'C4R-H02'; Suite = 'Library'; Filter = $hFilter; What = 'application id 0 is accepted by the header check (reviewer mutant R-15)'; Edits = @(Replace-One $T 'else if (applicationId != LibraryNames.ApplicationId) outcome' 'else if (applicationId != LibraryNames.ApplicationId && applicationId != 0) outcome') },
    @{ Id = 'C4R-H03'; Suite = 'Library'; Filter = $hFilter; What = 'a newer schema is accepted up to user_version 5 (reviewer mutant R-16)'; Edits = @(Replace-One $T 'else if (userVersion > LibraryNames.SchemaVersion) outcome' 'else if (userVersion > LibraryNames.SchemaVersion + 4) outcome') },

    # ---- the per-source dictionary (D-52, D-R1, D-R2) and exact UTF-16 (SCH-10)
    @{ Id = 'C4R-D01'; Suite = 'Library'; Filter = $dFilter; What = 'the name dictionary is unique Library-wide again (UNIQUE (utf16))'; Edits = @(Replace-One "$L/Sql/SchemaSql.cs" '          UNIQUE (source_id, utf16)' '          UNIQUE (utf16)') },
    @{ Id = 'C4R-D02'; Suite = 'Library'; Filter = $dFilter; What = 'the name cache survives an import and serves the next one (ids leak across sources)'
       Edits = @(Replace-One $S '        private Dictionary<byte[], long> _young = new(ByteArrayComparer.Instance);' '        private static Dictionary<byte[], long> _young = new(ByteArrayComparer.Instance);'; Replace-One $S '        private Dictionary<byte[], long> _old = new(ByteArrayComparer.Instance);' '        private static Dictionary<byte[], long> _old = new(ByteArrayComparer.Instance);') },
    @{ Id = 'C4R-D03'; Suite = 'Library'; Filter = $dFilter; What = 'a new name is inserted under source 1 whatever the import''s source'; Edits = @(Replace-One $S 'id = isNew ? _insert.Set(0, _sourceId).Set(1, name).ExecuteInsert(lease)' 'id = isNew ? _insert.Set(0, 1L).Set(1, name).ExecuteInsert(lease)') },
    @{ Id = 'C4R-D04'; Suite = 'Library'; Filter = $dFilter; What = 'a name is bound as TEXT instead of an exact UTF-16 BLOB'; Edits = @(Replace-One $D ': raw.sqlite3_bind_blob(_statement, _indexes[index], value));' ': raw.sqlite3_bind_text(_statement, _indexes[index], System.Text.Encoding.Unicode.GetString(value)));') }
)

# ---- C4R-M05: the A-25 audit must reject a method that differs from a leased helper only by generic arity, and a delegate that
# borrows its host's lease and escapes the host. Suite 'Audit' (the Library builds, the audit tests run), each mutant with an Intended list:
# a mutant counts as KILLED only when one of those tests fails. A-xx are genuine violations (production edits), E-xx are CONTROLS the audit
# must ACCEPT (Equivalent: no test may fail), S-xx are defects in the audit itself (IlAudit.cs, IlClosures.cs) that its own fixtures must catch.
# SQL text in an edit is always an OpenSql constant, so that no unrelated A-05 rule fires.
$m05Filter = @('LeaseAuditTests', 'SecurityAuditTests', 'LibrarySecurityAuditTests')
$m05Primary = @('LeaseAuditTests.A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a')
$m05Fixtures = @('LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture', 'LeaseAuditTests.A_25_the_frozen_wording_alone', 'LeaseAuditTests.A_25_overloads_that_differ')
$m05Anchor = '    internal bool InTransaction => _inTransaction;'
$m05Configure = '        Guard(lease, "configure the connection");'
$m05Audit = 'tests/StorageInventory.IntegrationTests/IlAudit.cs'
$m05Closures = 'tests/StorageInventory.IntegrationTests/IlClosures.cs'

# A control adds a delegate that borrows authority to the product, which changes the audited SET of such delegates (the four of
# SnapshotImporter.Execute and LibrarySession.CheckIdentity, pinned by A_25_the_real_assemblies_have_unambiguous_methods...): that test
# is expected to fail for a control, and is Tolerated; no other test of the filter (in particular the one that asserts that the audit
# of the product finds no violation, A_25_every_mutation_in_every_first_party_assembly...) may.
$m05Pin = @('LeaseAuditTests.A_25_the_real_assemblies_have_unambiguous_methods')

function M05([string] $Id, [string] $What, [object[]] $Edits, [string[]] $Intended = $m05Primary, [bool] $Equivalent = $false, [string[]] $Tolerated = @()) {
    @{ Id = $Id; Suite = 'Audit'; Filter = $m05Filter; What = $What; Intended = $Intended; Equivalent = $Equivalent; Tolerated = $Tolerated; Edits = $Edits }
}
# members added to WriterConnection, and statements added at the start of WriterConnection.Configure (which takes the lease)
function Members([string] $Text) { Replace-One $D $m05Anchor ($m05Anchor + "`n`n" + $Text.TrimEnd()) }
function InConfigure([string] $Text) { Replace-One $D $m05Configure ($m05Configure + "`n" + $Text.TrimEnd()) }

$m05Retained = @'
    private Func<string, object?>? _retained;

    internal object? Poke(string sql) => _retained!(sql);
'@
$m05AcquireHead = @'
    private Func<bool>? _retainedCreate;

    internal bool PokeCreate() => _retainedCreate!();

    internal WriterLockResult AcquireWriterLock(MutationLease lease)
'@
$m05AcquireBody = @'
        _interlock.Require(lease, "acquire the writer lock", MutationKind.Open, MutationKind.Create, MutationKind.Prepare, MutationKind.SetAside);
        _retainedCreate = () => CreateEmptyDatabase(lease);
'@
$m05KeptRunner = @'
    internal long NewCaptureId() => Interlocked.Increment(ref _captureCounter);

    private DelegateQueryRunner? _keptRunner;

    internal object? PokeRunner() => _keptRunner!.Scalar(OpenSql.GetApplicationId);
'@

$repairMutants += @(
    # ---- (1) generic arity: the reviewer's mutant and its variants
    (M05 'C4R-M05-A01' 'the reviewer''s arity mutant: a private generic RvH<T> that Configure calls, then an internal NON-generic RvH(string) that runs a command on the writer without a lease' @(
        (Members @'
    private T? RvH<T>(string sql) where T : class => null;

    internal object? RvH(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
'@),
        (InConfigure '        _ = RvH<object>(OpenSql.SelectEngine);'))),
    (M05 'C4R-M05-A02' 'the same with the order reversed: a private non-generic RvH(string) that Configure calls, then an internal GENERIC RvH<T>(string) that runs a command without a lease' @(
        (Members @'
    private object? RvH(string sql) => sql;

    internal object? RvH<T>(string sql) where T : class
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
'@),
        (InConfigure '        _ = RvH(OpenSql.SelectEngine);'))),
    (M05 'C4R-M05-A03' 'overloads that differ only by HOW MANY type parameters: a private RvH<T> that Configure calls, an internal RvH<T, U> that runs a command without a lease' @(
        (Members @'
    private T? RvH<T>(string sql) where T : class => null;

    internal object? RvH<T, U>(string sql) where T : class where U : class
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
'@),
        (InConfigure '        _ = RvH<object>(OpenSql.SelectEngine);'))),
    (M05 'C4R-M05-A04' 'a lease-less WriterConnection.Poke runs a command through a private generic helper that is called only under a lease, while an internal method reaches the primitive only through the generic overload' @(
        (Members @'
    private void Helper(string sql) => _ = sql;

    internal object? Poke(string sql) => Helper<object>(sql);

    private object? Helper<T>(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
'@),
        (InConfigure '        Helper(OpenSql.SelectEngine);'))),

    # ---- (2) a lease captured in a delegate that escapes the host
    (M05 'C4R-M05-A05' 'the reviewer''s retained-delegate mutant: Configure stores sql => Scalar(lease, sql) in a field, the lease-less Poke runs it' @(
        (Members $m05Retained),
        (InConfigure '        _retained = sql => Scalar(lease, sql);'))),
    (M05 'C4R-M05-A06' 'the same with LibraryStore.CreateEmptyDatabase(lease) retained by AcquireWriterLock and run by a lease-less PokeCreate' @(
        (Replace-One $T '    internal WriterLockResult AcquireWriterLock(MutationLease lease)' $m05AcquireHead),
        (Replace-One $T '        _interlock.Require(lease, "acquire the writer lock", MutationKind.Open, MutationKind.Create, MutationKind.Prepare, MutationKind.SetAside);' $m05AcquireBody))),
    (M05 'C4R-M05-A07' 'the delegate is stored in a STATIC field' @(
        (Members @'
    private static Func<string, object?>? _retainedStatic;

    internal static object? PokeStatic(string sql) => _retainedStatic!(sql);
'@),
        (InConfigure '        _retainedStatic = sql => Scalar(lease, sql);'))),
    (M05 'C4R-M05-A08' 'a lease-taking method RETURNS the closure it built (Detach); any caller can run it after the lease ended' @(
        (Members '    internal Func<object?> Detach(MutationLease lease, string sql) => () => Scalar(lease, sql);'))),
    (M05 'C4R-M05-A09' 'a lease-LESS method returns a closure that uses the lease the writer kept (the earlier audit rejected this by its host rule)' @(
        (Members '    internal Func<object?> Leak(string sql) => () => Scalar(_openedBy, sql);')) @('LeaseAuditTests.A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a')),
    (M05 'C4R-M05-A10' 'the closure is handed to another lease-less method that keeps it (Keep)' @(
        (Members @'
    private Func<string, object?>? _retained;

    internal object? Poke(string sql) => _retained!(sql);

    private void Keep(Func<string, object?> action) => _retained = action;
'@),
        (InConfigure '        Keep(sql => Scalar(lease, sql));'))),
    (M05 'C4R-M05-A11' 'a LOCAL FUNCTION that captures the lease is converted to a delegate and stored' @(
        (Members $m05Retained),
        (InConfigure @'
        Func<string, object?> armed = Local;
        _retained = armed;

        object? Local(string sql) => Scalar(lease, sql);
'@))),
    (M05 'C4R-M05-A12' 'a lambda in a lease-less PRIVATE host (called only by Configure, so the frozen wording accepts it) stores a closure that uses the writer''s kept lease' @(
        (Members @'
    private Func<string, object?>? _retained;

    internal object? Poke(string sql) => _retained!(sql);

    private void Arm() => _retained = sql => Scalar(_openedBy, sql);
'@),
        (InConfigure '        Arm();'))),
    (M05 'C4R-M05-A13' 'the runner built from lease-capturing closures is kept in a field by CheckIdentity and used by a lease-less method' @(
        (Replace-One $N '    internal long NewCaptureId() => Interlocked.Increment(ref _captureCounter);' $m05KeptRunner),
        (Replace-One $N "            (sql, each, parameters) => writer.Rows(lease, sql, each, parameters));`n        if (Convert.ToInt64(q.Scalar(OpenSql.GetApplicationId)) != LibraryNames.ApplicationId)" "            (sql, each, parameters) => writer.Rows(lease, sql, each, parameters));`n        _keptRunner = q;`n        if (Convert.ToInt64(q.Scalar(OpenSql.GetApplicationId)) != LibraryNames.ApplicationId)"))),
    (M05 'C4R-M05-A14' 'the verifier keeps the query runner it is handed in a static (the importer''s runner, built from lease-capturing closures, now outlives the import)' @(
        (Replace-One $V '    internal const int CheckpointRows = 65_536;' "    internal const int CheckpointRows = 65_536;`n`n    internal static IQueryRunner? Kept;"),
        (Replace-One $V '        (string, object?)[] snapshot = [("$snapshot_id", snapshotId)];' "        Kept = q;`n        (string, object?)[] snapshot = [(`"`$snapshot_id`", snapshotId)];"))),
    (M05 'C4R-M05-A15' 'an ITERATOR that holds the lease and mutates when somebody enumerates it, possibly after the lease ended' @(
        (Members @'
    internal IEnumerable<object?> Lazy(MutationLease lease, string sql)
    {
        yield return Scalar(lease, sql);
    }
'@))),
    (M05 'C4R-M05-A16' 'a delegate that takes a lease is invoked by a lease-less method with the lease the writer kept' @(
        (Members @'
    private Func<WriterConnection, MutationLease, object?>? _leasedOperation;

    internal object? PokeOperation() => _leasedOperation!(this, _openedBy);
'@),
        (InConfigure '        _leasedOperation = static (writer, held) => writer.PageSize(held);'))),
    (M05 'C4R-M05-A17' 'the closure is subscribed to an event' @(
        (Members @'
    internal event Func<string, object?>? Hook;

    internal object? Raise(string sql) => Hook?.Invoke(sql);
'@),
        (InConfigure '        Hook += sql => Scalar(lease, sql);'))),
    (M05 'C4R-M05-A18' 'the closure is handed to the thread pool' @(
        (InConfigure '        _ = Task.Run(() => Scalar(lease, OpenSql.SelectEngine));'))),
    (M05 'C4R-M05-A19' 'an ASYNC lambda that captures the lease is stored (its work is in a state machine)' @(
        (Members @'
    private Func<string, Task<object?>>? _retainedAsync;

    internal Task<object?> PokeAsync(string sql) => _retainedAsync!(sql);
'@),
        (InConfigure @'
        _retainedAsync = async sql =>
        {
            await Task.Yield();
            return Scalar(lease, sql);
        };
'@))),
    (M05 'C4R-M05-A20' 'the closure is put in an array' @(
        (Members @'
    private Func<string, object?>[]? _retainedArray;

    internal object? PokeArray(string sql) => _retainedArray![0](sql);
'@),
        (InConfigure '        _retainedArray = [sql => Scalar(lease, sql)];'))),

    # ---- controls: shapes the audit must ACCEPT (an EQUIVALENT result is the expected one; a failing test is a false positive)
    (M05 'C4R-M05-E01' 'CONTROL: the closure is handed to a helper that only runs it' @(
        (Members '    private object? Apply(Func<string, object?> action) => action(OpenSql.SelectEngine);'),
        (InConfigure '        _ = Apply(sql => Scalar(lease, sql));')) @('LeaseAuditTests') $true $m05Pin),
    (M05 'C4R-M05-E02' 'CONTROL: the closure is built and run inside the host' @(
        (InConfigure @'
        Func<string, object?> run = sql => Scalar(lease, sql);
        _ = run(OpenSql.SelectEngine);
'@)) @('LeaseAuditTests') $true $m05Pin),
    (M05 'C4R-M05-E03' 'CONTROL: overloads that differ by generic arity, all private and called only under the lease' @(
        (Members @'
    private void Same(string text) => _ = text;

    private void Same<T>(string text) where T : class => _ = text;
'@),
        (InConfigure @'
        Same("a");
        Same<object>("b");
'@)) @('LeaseAuditTests') $true $m05Pin),
    (M05 'C4R-M05-E04' 'CONTROL: a lambda that takes the lease itself is stored and run by a method that takes one' @(
        (Members '    private Func<WriterConnection, MutationLease, object?>? _operation3;'),
        (InConfigure @'
        _operation3 = static (writer, held) => writer.PageSize(held);
        _ = _operation3(this, lease);
'@)) @('LeaseAuditTests') $true $m05Pin),

    # ---- defects in the audit itself: its own fixtures must catch each
    (M05 'C4R-M05-S01' 'the audit''s method key drops the generic arity again' @(
        (Replace-One $m05Audit '        $"{type}::{name}{(genericArity > 0 ? "`" + genericArity : "")}({string.Join(",", parameters)}){(name is "op_Implicit" or "op_Explicit" ? "->" + returnType : "")}";' '        $"{type}::{name}({string.Join(",", parameters)}){(name is "op_Implicit" or "op_Explicit" ? "->" + returnType : "")}";')) $m05Fixtures),
    (M05 'C4R-M05-S02' 'two methods with one identity are not rejected' @(
        (Replace-One $m05Audit '            .Where(g => g.Count() > 1).Select(g => (g.Key, g.ToList())).ToList();' '            .Where(g => g.Count() > 1 && g.Key.Length < 0).Select(g => (g.Key, g.ToList())).ToList();')) $m05Fixtures),
    (M05 'C4R-M05-S03' 'the closure rule is not run' @(
        (Replace-One $m05Audit '        violations.AddRange(IlClosures.Violations(model, inScope, leasedOperations, allow));' '        _ = IlClosures.Violations(model, inScope, leasedOperations, allow);')) $m05Fixtures),
    (M05 'C4R-M05-S04' 'a store to an instance field is no longer a sink' @(
        (Replace-One $m05Closures '                else Report("field", $"is stored in the field {field?.Owner}::{field?.Name}{(obj.Tag == Tag.This ? " of the instance (outside a constructor)" : "")}, where it outlives the call", value.Taint);' '                else _ = field;')) $m05Fixtures),
    (M05 'C4R-M05-S05' 'a store to a static field is no longer a sink' @(
        (Replace-One $m05Closures '                Report("static", $"is stored in the static field {field?.Owner}::{field?.Name}", value.Taint);' '                _ = field;')) $m05Fixtures),
    (M05 'C4R-M05-S06' 'a store to an array element is no longer a sink' @(
        (Replace-One $m05Closures '                Report("array", "is stored in an array element", value.Taint);' '                _ = value;')) $m05Fixtures),
    (M05 'C4R-M05-S07' 'a returned delegate is no longer a sink' @(
        (Replace-One $m05Closures '                if (returnsValue) Report("return", "is returned to the caller", Pop().Taint);' '                if (returnsValue) _ = Pop();')) $m05Fixtures),
    (M05 'C4R-M05-S08' 'a call into code the audit cannot read is no longer a sink' @(
        (Replace-One $m05Closures '            if (!benign) Report("external",' '            if (!benign && callees.Count < 0) Report("external",')) $m05Fixtures),
    (M05 'C4R-M05-S09' 'a callee whose summary lets the parameter escape is ignored' @(
        (Replace-One $m05Closures '                if (s.Escape) Report("call", $"is passed to {callee.Type}::{callee.Name} (parameter {a}), which {s.Reason}", args[a].Taint);' '                if (s.Escape && a < 0) Report("call", $"is passed to {callee.Type}::{callee.Name} (parameter {a}), which {s.Reason}", args[a].Taint);')) $m05Fixtures),
    (M05 'C4R-M05-S10' 'an object built from a delegate by a constructor no longer carries it (the constructor''s stores are forgotten)' @(
        (Replace-One $m05Closures '                    if (isNew) result = result.Union(held);' '                    if (isNew && field.Length < 0) result = result.Union(held);')) $m05Fixtures),
    (M05 'C4R-M05-S11' 'the arguments of a delegate invocation are not judged against the targets of that delegate type' @(
        (Replace-One $m05Closures '                    if (s.Escape) Report("call", $"is passed to {call.Short}, which can run {target.Name} ({target.Type}), and that {s.Reason}", args[a].Taint);' '                    if (s.Escape && a < 0) Report("call", $"is passed to {call.Short}, which can run {target.Name} ({target.Type}), and that {s.Reason}", args[a].Taint);')) $m05Fixtures),
    (M05 'C4R-M05-S12' 'no method counts as an authority closure' @(
        (Replace-One $m05Closures '    private bool IsAuthorityTarget(IlAudit.Physical target) => !target.TakesLease && !_allow.Contains(target.Key) && Reaches(target);' '    private bool IsAuthorityTarget(IlAudit.Physical target) => !target.TakesLease && !_allow.Contains(target.Key) && Reaches(target) && target.Name.Length < 0;')) $m05Fixtures),
    (M05 'C4R-M05-S13' 'the work of an async lambda (its state machine) is not part of what the lambda reaches' @(
        (Replace-One $m05Closures '        if (_stateMachines.TryGetValue((method.Type, method.Name), out var machines)) foreach (var body in machines) yield return body;' '        if (_stateMachines.TryGetValue((method.Type, method.Name), out var machines) && machines.Count < 0) foreach (var body in machines) yield return body;')) $m05Fixtures),
    (M05 'C4R-M05-S14' 'an iterator that reaches a mutation is accepted' @(
        (Replace-One $m05Closures '            if (!Reaches(body)) continue;' '            if (!Reaches(body) || body.Name.Length > 0) continue;')) $m05Fixtures),
    (M05 'C4R-M05-S15' 'the invocation of a delegate that takes a lease is not a leased operation' @(
        (Replace-One $m05Audit '        if (c.Name != "Invoke" || !c.HasThis || !IsDelegateType(model, c.Type)) return false;' '        if (c.Name != "Invoke" || !c.HasThis || !IsDelegateType(model, c.Type) || c.Name.Length > 0) return false;')) ($m05Fixtures + @('LeaseAuditTests.A_25_the_real_assemblies_have_unambiguous_methods')))
)
