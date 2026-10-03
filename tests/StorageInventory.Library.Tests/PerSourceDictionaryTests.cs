using StorageInventory.Core;
using StorageInventory.History.Library;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// The per-source name dictionary (SCH-04, D-52; D-R1, D-R2) and the attempt's run id (invariant 13; C4-M09). A name belongs to one
/// source: it is stored once for each source that saw it, uniqueness is per (source, bytes), the importer's name cache never
/// serves another source, exact UTF-16 bytes survive, and an attempt that does not carry the snapshot's run id never publishes.
/// </summary>
public static class PerSourceDictionaryTests
{
    private static string Hex(string text) => Convert.ToHexString(Utf16.ToBytes(text));

    private static long Count(LibrarySession session, string sql) => session.Read(r => r.Long(sql));

    private static ImportResult Import(LibrarySession session, ScriptedSnapshot snapshot, string runId, ImportSourceSpec source) =>
        LibraryStateTests.ImportRows(session, snapshot, snapshot.Header(runId), runId, source);

    [Test]
    public static void The_same_name_seen_by_two_sources_is_two_rows_and_a_reimport_reuses_only_its_own()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var snapshot = ScriptedSnapshot.Rich();   // identical names for both sources
        var first = Import(session, snapshot, "run-1", ScriptedSnapshot.NewSource(@"\", 0x1111));
        var second = Import(session, snapshot, "run-2", ScriptedSnapshot.NewSource(@"\Other", 0x2222));
        Assert.True(first.SourceId != second.SourceId, "two sources");

        Assert.Equal(2L, Count(session, $"SELECT count(*) FROM name WHERE utf16 = X'{Hex("docs")}'"), "the name 'docs' is stored once for each source");
        Assert.Equal(1L, Count(session, $"SELECT count(*) FROM name WHERE utf16 = X'{Hex("docs")}' AND source_id = {first.SourceId}"));
        Assert.Equal(1L, Count(session, $"SELECT count(*) FROM name WHERE utf16 = X'{Hex("docs")}' AND source_id = {second.SourceId}"));
        Assert.Equal(first.NewNames, second.NewNames, "the cache of the first import did not serve the second: every name was new to the second source");
        Assert.True(first.NewNames > 0);
        var namesOfFirst = Count(session, $"SELECT count(*) FROM name WHERE source_id = {first.SourceId}");
        var namesOfSecond = Count(session, $"SELECT count(*) FROM name WHERE source_id = {second.SourceId}");
        Assert.Equal(namesOfFirst, namesOfSecond, "both sources hold the same vocabulary, each its own copy");

        // a later snapshot of the first source reuses ITS rows: no new names anywhere
        var third = Import(session, snapshot, "run-3", new ImportSourceSpec.Existing(first.SourceId));
        Assert.Equal(0L, third.NewNames, "every name was already in the source's own dictionary");
        Assert.Equal(namesOfFirst, Count(session, $"SELECT count(*) FROM name WHERE source_id = {first.SourceId}"));
        Assert.Equal(namesOfSecond, Count(session, $"SELECT count(*) FROM name WHERE source_id = {second.SourceId}"), "the other source's dictionary was not touched");
        // the observation rows of the first source point only at the first source's names, and likewise for the second
        Assert.Equal(0L, Count(session, $"SELECT count(*) FROM file_obs f JOIN snapshot s ON s.snapshot_id = f.snapshot_id JOIN name n ON n.name_id = f.name_id WHERE n.source_id <> s.source_id"), "no file row names another source's dictionary row");
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM folder_path p JOIN name n ON n.name_id = p.name_id WHERE n.source_id <> p.source_id"), "no folder path names another source's dictionary row");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void The_schema_makes_a_name_unique_per_source_and_a_source_row_required()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var snapshot = ScriptedSnapshot.Rich();
        var one = Import(session, snapshot, "run-1", ScriptedSnapshot.NewSource(@"\", 0x1111));
        var two = Import(session, snapshot, "run-2", ScriptedSnapshot.NewSource(@"\Other", 0x2222));

        // the same bytes for a third, new source id are fine; the same bytes twice for one source are not
        RawSqlite.Execute(world.Main, ["PRAGMA foreign_keys = OFF", $"INSERT INTO name (source_id, utf16) VALUES (77, X'{Hex("probe")}')", $"INSERT INTO name (source_id, utf16) VALUES (78, X'{Hex("probe")}')"]);
        var sameSource = ThrowsConstraint(world, $"INSERT INTO name (source_id, utf16) VALUES (77, X'{Hex("probe")}')");
        Assert.True(sameSource.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase), "a second row for the same (source, name) is a UNIQUE violation: " + sameSource);
        // the dictionary row of a source that does not exist is refused when foreign keys are on (they are, on every product connection)
        var orphan = ThrowsConstraint(world, $"PRAGMA foreign_keys = ON; INSERT INTO name (source_id, utf16) VALUES (424242, X'{Hex("orphan")}')");
        Assert.True(orphan.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase), "name.source_id references source: " + orphan);
        // STRICT: a TEXT bind into the BLOB column is rejected (the structural backstop of SCH-10)
        var text = ThrowsConstraint(world, "INSERT INTO name (source_id, utf16) VALUES (" + one.SourceId + ", 'plain text')");
        Assert.True(text.Contains("BLOB", StringComparison.OrdinalIgnoreCase), "utf16 is a STRICT BLOB column: " + text);
        Assert.Equal(1L, Count(session, $"SELECT count(*) FROM pragma_index_list('name') WHERE \"unique\" = 1 AND origin = 'u'"), "one automatic unique index on name");
        Assert.Equal("source_id,utf16", string.Join(',', session.Read(r => r.Query("SELECT name FROM pragma_index_info((SELECT name FROM pragma_index_list('name') WHERE origin = 'u')) ORDER BY seqno").Select(row => (string)row[0]!))), "the unique key is (source_id, utf16)");
        _ = two;
        session.TestOnlyShutdown();
    }

    private static string ThrowsConstraint(World world, string sql)
    {
        try
        {
            RawSqlite.Execute(world.Main, sql.Split("; "));
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            return inner.Message;
        }
        throw new AssertionException("expected a constraint failure for: " + sql);
    }

    [Test]
    public static void Names_are_stored_as_exact_UTF16_bytes_per_source_nul_surrogates_composed_decomposed_case_and_long_names()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var longName = new string('x', 32_000) + ".dat";
        string[] fileNames =
        [
            "nul\0inside.txt",                // an embedded NUL is data, not a terminator
            "lone\uD800surrogate.txt",        // an unpaired high surrogate survives
            "lone\uDC00low.txt",              // and a low one
            "caf\u00E9.txt",                  // composed
            "cafe\u0301.txt",                 // decomposed: a different name
            "Case.txt", "case.txt", "CASE.txt",
            longName,
        ];
        var folders = new[] { new ScriptedSnapshot.Folder("", -1), new ScriptedSnapshot.Folder("dir\uD800name", 0), new ScriptedSnapshot.Folder("nul\0folder", 0) };
        var files = fileNames.Select((n, i) => new ScriptedSnapshot.File(1 + i % 2, n, i + 1)).ToList();
        var errors = new[] { new ScriptedSnapshot.Error("dir\uD800name\\nul\0file", ScanErrorType.AccessDenied, "denied \uD800 \0 end") };
        var snapshot = new ScriptedSnapshot(folders, files, errors);
        var one = Import(session, snapshot, "run-1", ScriptedSnapshot.NewSource(@"\", 0x1111));
        var two = Import(session, snapshot, "run-2", ScriptedSnapshot.NewSource(@"\Other", 0x2222));

        foreach (var source in new[] { one.SourceId, two.SourceId })
        {
            foreach (var name in fileNames.Concat(["dir\uD800name", "nul\0folder", ""]))
            {
                var rows = session.Read(r => r.Query($"SELECT utf16, typeof(utf16) FROM name WHERE source_id = {source} AND utf16 = X'{Hex(name)}'"));
                Assert.Equal(1, rows.Count, $"source {source}: exactly one dictionary row for a name of {name.Length} code units");
                Assert.Equal("blob", (string)rows[0][1]!, "bound as a BLOB, never TEXT");
                Assert.SequenceEqual(Utf16.ToBytes(name), (byte[])rows[0][0]!, "the exact code units, byte for byte");
            }
            // the three case variants and the two normalisation forms are all distinct rows
            Assert.Equal(3L, Count(session, $"SELECT count(*) FROM name WHERE source_id = {source} AND utf16 IN (X'{Hex("Case.txt")}', X'{Hex("case.txt")}', X'{Hex("CASE.txt")}')"));
            Assert.Equal(2L, Count(session, $"SELECT count(*) FROM name WHERE source_id = {source} AND utf16 IN (X'{Hex("caf\u00E9.txt")}', X'{Hex("cafe\u0301.txt")}')"));
        }
        // rel_path and message are BLOBs of exact code units too
        var error = session.Read(r => r.Query("SELECT rel_path, message, typeof(rel_path), typeof(message) FROM scan_error WHERE snapshot_id = 1"))[0];
        Assert.SequenceEqual(Utf16.ToBytes("dir\uD800name\\nul\0file"), (byte[])error[0]!);
        Assert.SequenceEqual(Utf16.ToBytes("denied \uD800 \0 end"), (byte[])error[1]!);
        Assert.Equal("blob", (string)error[2]!);
        Assert.Equal("blob", (string)error[3]!);
        Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 1)));
        Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 2)));
        session.TestOnlyShutdown();
    }

    // ---------------------------------------------------------------- invariant 13: the attempt carries the snapshot's run id

    private static AttemptStart Attempt(string? runId) => new(new byte[16], null, Utf16.ToBytes(@"D:\Scripted"), null, runId, DateTime.UtcNow.Ticks);

    private static (ImportException Failure, LibrarySession Session) ImportWithAttemptRunId(string? attemptRunId, string headerRunId)
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var snapshot = ScriptedSnapshot.Rich();
        var captureId = session.NewCaptureId();
        using var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, Attempt(attemptRunId)).GetAwaiter().GetResult();
        using var save = prepare.HandOffToObservation().HandOffToSave(out _);
        var failure = Assert.Throws<ImportException>(() => session.ImportSnapshotAsync(save, attempt, ScriptedSnapshot.NewSource(@"\", 0x1111), snapshot.Header(headerRunId), snapshot).GetAwaiter().GetResult());
        return (failure, session);
    }

    [Test]
    public static void An_attempt_with_a_different_run_id_never_publishes_and_changes_nothing()
    {
        var (failure, session) = ImportWithAttemptRunId("run-of-another-scan", "run-1");
        Assert.Equal(CaptureFailureKind.InvariantViolation, failure.Kind, failure.Message);
        Assert.Contains("run", failure.Message);
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM snapshot"), "no snapshot");
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM source"), "no source was created");
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM name"));
        Assert.Equal(1L, Count(session, "SELECT count(*) FROM scan_attempt WHERE outcome = 1"), "the attempt is still InProgress: it was never published");
        Assert.Equal(1L, Count(session, "SELECT next_snapshot_id FROM library_info"));
        Assert.False(session.Interlock.IsFaulted, "a class A refusal");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void An_attempt_with_no_run_id_never_publishes()
    {
        var (failure, session) = ImportWithAttemptRunId(null, "run-1");
        Assert.Equal(CaptureFailureKind.InvariantViolation, failure.Kind, failure.Message);
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM snapshot"));
        Assert.Equal(1L, Count(session, "SELECT count(*) FROM scan_attempt WHERE outcome = 1"));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void The_final_statement_that_publishes_the_attempt_refuses_a_different_or_missing_run_id()
    {
        // the early check and the final statement are separate statements: the second must hold on its own (a mutant that dropped the
        // run id from it alone would pass the early-check tests above), so it is exercised directly on a writer inside a Save lease
        var world = World.Create();
        var session = world.CreatedSession();
        var captureId = session.NewCaptureId();
        using var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, Attempt("run-A")).GetAwaiter().GetResult();
        var noRun = session.RecordAttemptStartAsync(prepare, Attempt(null)).GetAwaiter().GetResult();
        using var save = prepare.HandOffToObservation().HandOffToSave(out _);
        using var writer = LibraryDatabase.OpenWriter(save, session.Interlock, world.Main, "probe", [MutationKind.Save], false, null);
        writer.Begin(save);
        int Publish(AttemptRef who, string runId)
        {
            using var statement = writer.Prepare(save, ImportSql.PublishAttempt, "$source_id", "$ended_utc", "$attempt_id", "$session_token", "$capture_token", "$run_id");
            return statement.Set(0, (long?)null).Set(1, 1L).Set(2, who.AttemptId).Set(3, session.SessionToken).Set(4, who.CaptureToken).Set(5, runId).ExecuteNonQuery(save);
        }
        Assert.Equal(0, Publish(attempt, "run-B"), "a different run id changes no row");
        Assert.Equal(0, Publish(noRun, "run-A"), "an attempt with no run id changes no row");
        Assert.Equal(1, Publish(attempt, "run-A"), "the matching run id changes exactly one row");
        writer.Rollback(save);
        session.TestOnlyShutdown();
    }
}
