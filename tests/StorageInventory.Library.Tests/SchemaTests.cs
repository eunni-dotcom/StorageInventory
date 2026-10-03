using StorageInventory.Core;
using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.History.Library;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>TEST-S1 (the schema-1 fingerprint is frozen and unexpected objects are refused) and TEST-S2 (the stable code tables are
/// frozen, and are explicit integers, never enum ordinals).</summary>
public static class SchemaTests
{
    private static List<SchemaRow> SchemaRows(LibrarySession session) => session.Read(r =>
        r.Query(OpenSql.SelectSchemaRows).Select(row => new SchemaRow((string)row[0]!, (string)row[1]!, (string)row[2]!, (string?)row[3])).ToList());

    [Test]
    public static void The_schema_1_fingerprint_and_object_list_are_frozen()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var rows = SchemaRows(session);
        Assert.Equal(LibrarySchema.Schema1Fingerprint, LibrarySchema.Compute(rows), "the fingerprint of a freshly created Library is the frozen constant");
        Assert.Equal(0, LibrarySchema.Differences(rows).Count);
        Assert.SequenceEqual(LibrarySchema.Schema1Objects, rows.Select(r => r.Type + " " + r.Name).Order(StringComparer.Ordinal), "the object list");
        Assert.Equal("1d1a977f5e90563cd03fd8b397f57da4f104835e8309c3ae81da3709ee90e922", LibrarySchema.Schema1Fingerprint, "a change to the constant needs a reviewed schema change");
        Assert.Equal(1397313110L, session.Read(r => r.Long(OpenSql.GetApplicationId)), "application_id = 0x53494E56");
        Assert.Equal(1L, session.Read(r => r.Long(OpenSql.GetUserVersion)), "user_version = 1");
        Assert.Equal(0x53494E56, LibraryNames.ApplicationId);
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Every_table_is_STRICT_and_the_observation_tables_declare_no_foreign_keys()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var rows = SchemaRows(session);
        foreach (var table in rows.Where(r => r.Type == "table"))
        {
            Assert.Contains("STRICT", table.Sql);
        }
        foreach (var name in new[] { "file_obs", "folder_obs", "scan_error", "snapshot_extension_total" })
        {
            Assert.False(rows.Single(r => r.Name == name).Sql!.Contains("REFERENCES", StringComparison.Ordinal), name + " declares no foreign key (Q-07)");
        }
        foreach (var name in new[] { "source", "scan_attempt", "snapshot", "folder_path" })
        {
            Assert.Contains("REFERENCES", rows.Single(r => r.Name == name).Sql);
        }
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Unexpected_triggers_views_indexes_tables_and_altered_columns_change_the_fingerprint()
    {
        foreach (var ddl in new[]
        {
            "CREATE TRIGGER t AFTER DELETE ON snapshot BEGIN SELECT 1; END",
            "CREATE VIEW v AS SELECT 1",
            "CREATE INDEX i ON file_obs (size_bytes)",
            "CREATE TABLE extra (x)",
            "ALTER TABLE library_info ADD COLUMN extra TEXT",
        })
        {
            var world = World.Create();
            world.CreatedSession().TestOnlyShutdown();
            RawSqlite.Execute(world.Main, ddl);
            var session = world.NewSession();
            var status = session.RunStartupOpen();
            Assert.Equal(LibraryState.NotALibrary, status.State, ddl);
            Assert.Equal(LibraryReason.SchemaFingerprint, status.Reason, ddl);
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void The_stable_code_tables_are_frozen_explicit_integers()
    {
        string[] expected =
        [
            "SnapshotState.Importing = 1", "SnapshotState.Published = 2", "SnapshotState.Deleting = 3",
            "ScanCompletionState.Complete = 0", "ScanCompletionState.Incomplete = 1",
            "FolderScanStatus.Ok = 0", "FolderScanStatus.Unreadable = 1", "FolderScanStatus.Partial = 2", "FolderScanStatus.ReparsePointSkipped = 3",
            "ScanErrorType.AccessDenied = 1", "ScanErrorType.NotFound = 2", "ScanErrorType.PathTooLong = 3", "ScanErrorType.IOError = 4", "ScanErrorType.InvalidPath = 5",
            "ScanErrorType.InvalidTimestamp = 6", "ScanErrorType.UnexpectedError = 7", "ScanErrorType.ReparsePointSkipped = 8", "ScanErrorType.ReparsePointFile = 9",
            "IdentityConfidence.Strong = 1", "IdentityConfidence.Moderate = 2", "IdentityConfidence.PathOnly = 3",
            "IdentityBasis.Evidence = 1", "IdentityBasis.UserAsserted = 2", "IdentityBasis.Location = 3",
            "SourceKind.LocalVolume = 1", "SourceKind.Network = 2",
            "AttemptOutcome.InProgress = 1", "AttemptOutcome.Published = 2", "AttemptOutcome.Cancelled = 3", "AttemptOutcome.ScanFailed = 4",
            "AttemptOutcome.NotEligible = 5", "AttemptOutcome.SnapshotFailed = 6", "AttemptOutcome.SaveCancelled = 7", "AttemptOutcome.Interrupted = 8",
            "CaptureFailureKind.InvalidPaths = 1", "CaptureFailureKind.OutputError = 2", "CaptureFailureKind.OutputInsideScannedTree = 3",
            "CaptureFailureKind.InternalConsistency = 4", "CaptureFailureKind.Unexpected = 5",
            "CaptureFailureKind.SpoolWriteFailed = 101", "CaptureFailureKind.SpoolInvalid = 102", "CaptureFailureKind.IdentityChangedDuringScan = 103",
            "CaptureFailureKind.LibraryChangedDuringScan = 104", "CaptureFailureKind.ReportVolumeNearlyFull = 105", "CaptureFailureKind.ReportVolumeSpaceUnknown = 106",
            "CaptureFailureKind.LibraryFull = 201", "CaptureFailureKind.LibraryIoError = 202", "CaptureFailureKind.LibraryBusy = 203",
            "CaptureFailureKind.InvariantViolation = 204", "CaptureFailureKind.SourceChanged = 205", "CaptureFailureKind.LibraryUnavailable = 206",
            "CaptureFailureKind.Catastrophic = 207 (reserved, never written)",
            "LibraryInsideSource.No = 0", "LibraryInsideSource.Yes = 1", "LibraryInsideSource.Unknown = 2",
            "scanner_contract = 1", "spool_format = 1",
        ];
        Assert.SequenceEqual(expected, StableCodes.Describe(), "TEST-S2: the mapping table the code applies");
    }

    [Test]
    public static void The_stored_codes_are_independent_of_the_C_sharp_enum_ordinals()
    {
        // AccessDenied is ordinal 0 but code 1; ReparsePointSkipped is ordinal 7 but code 8: the stored value is the table, not the ordinal.
        Assert.Equal(0, (int)ScanErrorType.AccessDenied);
        Assert.Equal(1, StableCodes.ToCode(ScanErrorType.AccessDenied));
        Assert.Equal(7, (int)ScanErrorType.ReparsePointSkipped);
        Assert.Equal(8, StableCodes.ToCode(ScanErrorType.ReparsePointSkipped));
        // every mapping round-trips, and no mapping is the ordinal where the table says otherwise
        foreach (var v in Enum.GetValues<ScanErrorType>()) Assert.Equal(v, StableCodes.ScanErrorTypeFromCode(StableCodes.ToCode(v)));
        foreach (var v in Enum.GetValues<FolderScanStatus>()) Assert.Equal(v, StableCodes.FolderScanStatusFromCode(StableCodes.ToCode(v)));
        foreach (var v in Enum.GetValues<IdentityConfidence>()) Assert.Equal(v, StableCodes.IdentityConfidenceFromCode(StableCodes.ToCode(v)));
        foreach (var v in Enum.GetValues<IdentityBasis>()) Assert.Equal(v, StableCodes.IdentityBasisFromCode(StableCodes.ToCode(v)));
        foreach (var v in Enum.GetValues<SourceKind>()) Assert.Equal(v, StableCodes.SourceKindFromCode(StableCodes.ToCode(v)));
        foreach (var v in Enum.GetValues<SnapshotState>()) Assert.Equal(v, StableCodes.SnapshotStateFromCode(StableCodes.ToCode(v)));
        foreach (var v in Enum.GetValues<AttemptOutcome>()) Assert.Equal(v, StableCodes.AttemptOutcomeFromCode(StableCodes.ToCode(v)));
        foreach (var v in Enum.GetValues<CaptureFailureKind>().Where(v => v != CaptureFailureKind.Catastrophic)) Assert.Equal(v, StableCodes.CaptureFailureKindFromCode(StableCodes.ToCode(v)));
        foreach (var v in Enum.GetValues<LibraryInsideSource>()) Assert.Equal(v, StableCodes.LibraryInsideSourceFromCode(StableCodes.ToCode(v)));
    }

    [Test]
    public static void An_unknown_stored_code_is_a_data_error_not_a_crash_and_207_is_never_written()
    {
        Assert.Throws<LibraryDataException>(() => StableCodes.ScanErrorTypeFromCode(0));
        Assert.Throws<LibraryDataException>(() => StableCodes.ScanErrorTypeFromCode(10));
        Assert.Throws<LibraryDataException>(() => StableCodes.SnapshotStateFromCode(4));
        Assert.Throws<LibraryDataException>(() => StableCodes.AttemptOutcomeFromCode(99));
        Assert.Throws<LibraryDataException>(() => StableCodes.CaptureFailureKindFromCode(150));
        Assert.Throws<LibraryDataException>(() => StableCodes.FolderScanStatusFromCode(-1));
        Assert.Equal(CaptureFailureKind.Catastrophic, StableCodes.CaptureFailureKindFromCode(207), "readable if a future writer used it");
        Assert.Throws<ArgumentOutOfRangeException>(() => StableCodes.ToCode(CaptureFailureKind.Catastrophic));   // 1.1.0 never writes it (OBS-13)
        Assert.Throws<ArgumentOutOfRangeException>(() => StableCodes.ToStoredCompletion(ScanCompletionState.Cancelled));
        Assert.Throws<ArgumentOutOfRangeException>(() => StableCodes.ToStoredCompletion(ScanCompletionState.Failed));
    }
}
