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
    @{ Id = 'C4R-X02'; Suite = 'Library'; Filter = $gFilter; What = 'the pending growth is the old fixed 32 MiB allowance instead of page_count x page size - main length'; Edits = @(Replace-One $S 'var pending = Math.Max(0L, pageCount * pageSize - main);' 'var pending = 32L * 1024 * 1024;') },
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
