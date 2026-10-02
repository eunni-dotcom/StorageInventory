using StorageInventory.Core;
using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.History.Library;

namespace StorageInventory.Library;

/// <summary>§9.4 SnapshotState. Only 2 is ever committed: 1 and 3 exist only inside T-IMPORT and T-DELETE.</summary>
internal enum SnapshotState
{
    Importing = 1,
    Published = 2,
    Deleting = 3,
}

/// <summary>§9.4 AttemptOutcome.</summary>
internal enum AttemptOutcome
{
    InProgress = 1,
    Published = 2,
    Cancelled = 3,
    ScanFailed = 4,
    NotEligible = 5,
    SnapshotFailed = 6,
    SaveCancelled = 7,
    Interrupted = 8,
}

/// <summary>§9.4 CaptureFailureKind. 207 (Catastrophic) is reserved and never written by 1.1.0: after a class C failure the
/// process writes nothing more to the Library (OBS-13); a test asserts nothing writes it.</summary>
internal enum CaptureFailureKind
{
    // With ScanFailed
    InvalidPaths = 1,
    OutputError = 2,
    OutputInsideScannedTree = 3,
    InternalConsistency = 4,
    Unexpected = 5,

    // With NotEligible
    SpoolWriteFailed = 101,
    SpoolInvalid = 102,
    IdentityChangedDuringScan = 103,
    LibraryChangedDuringScan = 104,
    ReportVolumeNearlyFull = 105,
    ReportVolumeSpaceUnknown = 106,

    // With SnapshotFailed
    LibraryFull = 201,
    LibraryIoError = 202,
    LibraryBusy = 203,
    InvariantViolation = 204,
    SourceChanged = 205,
    LibraryUnavailable = 206,

    /// <summary>Reserved. Never written.</summary>
    Catastrophic = 207,
}

/// <summary>
/// The explicit integer mappings of SCH-07 / §9.4 (TEST-S2 freezes them). Stored values are NEVER enum ordinals: each mapping
/// is a <c>switch</c> over the named member, so reordering or renumbering a C# enum cannot change what a stored value means.
/// Reading is range-checked: an unknown stored code is a <see cref="LibraryDataException"/>, a data error and not a crash
/// (SEC-16).
/// </summary>
internal static class StableCodes
{
    // ---- Core enums (public in Core; their ordinals are NOT the stored codes) ----

    internal static int ToCode(ScanErrorType type) => type switch
    {
        ScanErrorType.AccessDenied => 1,
        ScanErrorType.NotFound => 2,
        ScanErrorType.PathTooLong => 3,
        ScanErrorType.IOError => 4,
        ScanErrorType.InvalidPath => 5,
        ScanErrorType.InvalidTimestamp => 6,
        ScanErrorType.UnexpectedError => 7,
        ScanErrorType.ReparsePointSkipped => 8,
        ScanErrorType.ReparsePointFile => 9,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No stable code for this scan error type."),
    };

    internal static ScanErrorType ScanErrorTypeFromCode(long code) => code switch
    {
        1 => ScanErrorType.AccessDenied,
        2 => ScanErrorType.NotFound,
        3 => ScanErrorType.PathTooLong,
        4 => ScanErrorType.IOError,
        5 => ScanErrorType.InvalidPath,
        6 => ScanErrorType.InvalidTimestamp,
        7 => ScanErrorType.UnexpectedError,
        8 => ScanErrorType.ReparsePointSkipped,
        9 => ScanErrorType.ReparsePointFile,
        _ => throw Unknown("ScanErrorType", code),
    };

    internal static int ToCode(FolderScanStatus status) => status switch
    {
        FolderScanStatus.Ok => 0,
        FolderScanStatus.Unreadable => 1,
        FolderScanStatus.Partial => 2,
        FolderScanStatus.ReparsePointSkipped => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No stable code for this folder status."),
    };

    internal static FolderScanStatus FolderScanStatusFromCode(long code) => code switch
    {
        0 => FolderScanStatus.Ok,
        1 => FolderScanStatus.Unreadable,
        2 => FolderScanStatus.Partial,
        3 => FolderScanStatus.ReparsePointSkipped,
        _ => throw Unknown("FolderScanStatus", code),
    };

    /// <summary>The stored completeness: 0 Complete, 1 Incomplete. Cancelled and Failed are never stored on a snapshot.</summary>
    internal static int ToStoredCompletion(ScanCompletionState state) => state switch
    {
        ScanCompletionState.Complete => 0,
        ScanCompletionState.Incomplete => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Only a finished scan is stored."),
    };

    internal static ScanCompletionState CompletionFromStored(long code) => code switch
    {
        0 => ScanCompletionState.Complete,
        1 => ScanCompletionState.Incomplete,
        _ => throw Unknown("ScanCompletionState", code),
    };

    // ---- History / Core internals (stable codes are the schema's) ----

    internal static int ToCode(IdentityConfidence value) => value switch
    {
        IdentityConfidence.Strong => 1,
        IdentityConfidence.Moderate => 2,
        IdentityConfidence.PathOnly => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No stable code for this confidence."),
    };

    internal static IdentityConfidence IdentityConfidenceFromCode(long code) => code switch
    {
        1 => IdentityConfidence.Strong,
        2 => IdentityConfidence.Moderate,
        3 => IdentityConfidence.PathOnly,
        _ => throw Unknown("IdentityConfidence", code),
    };

    internal static int ToCode(IdentityBasis value) => value switch
    {
        IdentityBasis.Evidence => 1,
        IdentityBasis.UserAsserted => 2,
        IdentityBasis.Location => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No stable code for this basis."),
    };

    internal static IdentityBasis IdentityBasisFromCode(long code) => code switch
    {
        1 => IdentityBasis.Evidence,
        2 => IdentityBasis.UserAsserted,
        3 => IdentityBasis.Location,
        _ => throw Unknown("IdentityBasis", code),
    };

    internal static int ToCode(SourceKind value) => value switch
    {
        SourceKind.LocalVolume => 1,
        SourceKind.Network => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No stable code for this source kind."),
    };

    internal static SourceKind SourceKindFromCode(long code) => code switch
    {
        1 => SourceKind.LocalVolume,
        2 => SourceKind.Network,
        _ => throw Unknown("SourceKind", code),
    };

    internal static int ToCode(LibraryInsideSource value) => value switch
    {
        LibraryInsideSource.No => 0,
        LibraryInsideSource.Yes => 1,
        LibraryInsideSource.Unknown => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No stable code for this value."),
    };

    internal static LibraryInsideSource LibraryInsideSourceFromCode(long code) => code switch
    {
        0 => LibraryInsideSource.No,
        1 => LibraryInsideSource.Yes,
        2 => LibraryInsideSource.Unknown,
        _ => throw Unknown("LibraryInsideSource", code),
    };

    // ---- Library enums ----

    internal static int ToCode(SnapshotState value) => value switch
    {
        SnapshotState.Importing => 1,
        SnapshotState.Published => 2,
        SnapshotState.Deleting => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No stable code for this snapshot state."),
    };

    internal static SnapshotState SnapshotStateFromCode(long code) => code switch
    {
        1 => SnapshotState.Importing,
        2 => SnapshotState.Published,
        3 => SnapshotState.Deleting,
        _ => throw Unknown("SnapshotState", code),
    };

    internal static int ToCode(AttemptOutcome value) => value switch
    {
        AttemptOutcome.InProgress => 1,
        AttemptOutcome.Published => 2,
        AttemptOutcome.Cancelled => 3,
        AttemptOutcome.ScanFailed => 4,
        AttemptOutcome.NotEligible => 5,
        AttemptOutcome.SnapshotFailed => 6,
        AttemptOutcome.SaveCancelled => 7,
        AttemptOutcome.Interrupted => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No stable code for this attempt outcome."),
    };

    internal static AttemptOutcome AttemptOutcomeFromCode(long code) => code switch
    {
        1 => AttemptOutcome.InProgress,
        2 => AttemptOutcome.Published,
        3 => AttemptOutcome.Cancelled,
        4 => AttemptOutcome.ScanFailed,
        5 => AttemptOutcome.NotEligible,
        6 => AttemptOutcome.SnapshotFailed,
        7 => AttemptOutcome.SaveCancelled,
        8 => AttemptOutcome.Interrupted,
        _ => throw Unknown("AttemptOutcome", code),
    };

    internal static int ToCode(CaptureFailureKind value) => value switch
    {
        CaptureFailureKind.InvalidPaths => 1,
        CaptureFailureKind.OutputError => 2,
        CaptureFailureKind.OutputInsideScannedTree => 3,
        CaptureFailureKind.InternalConsistency => 4,
        CaptureFailureKind.Unexpected => 5,
        CaptureFailureKind.SpoolWriteFailed => 101,
        CaptureFailureKind.SpoolInvalid => 102,
        CaptureFailureKind.IdentityChangedDuringScan => 103,
        CaptureFailureKind.LibraryChangedDuringScan => 104,
        CaptureFailureKind.ReportVolumeNearlyFull => 105,
        CaptureFailureKind.ReportVolumeSpaceUnknown => 106,
        CaptureFailureKind.LibraryFull => 201,
        CaptureFailureKind.LibraryIoError => 202,
        CaptureFailureKind.LibraryBusy => 203,
        CaptureFailureKind.InvariantViolation => 204,
        CaptureFailureKind.SourceChanged => 205,
        CaptureFailureKind.LibraryUnavailable => 206,
        // 207 Catastrophic is reserved and is never written by 1.1.0 (OBS-13): writing it is a defect, so there is no mapping.
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No stable code is written for this failure kind."),
    };

    internal static CaptureFailureKind CaptureFailureKindFromCode(long code) => code switch
    {
        1 => CaptureFailureKind.InvalidPaths,
        2 => CaptureFailureKind.OutputError,
        3 => CaptureFailureKind.OutputInsideScannedTree,
        4 => CaptureFailureKind.InternalConsistency,
        5 => CaptureFailureKind.Unexpected,
        101 => CaptureFailureKind.SpoolWriteFailed,
        102 => CaptureFailureKind.SpoolInvalid,
        103 => CaptureFailureKind.IdentityChangedDuringScan,
        104 => CaptureFailureKind.LibraryChangedDuringScan,
        105 => CaptureFailureKind.ReportVolumeNearlyFull,
        106 => CaptureFailureKind.ReportVolumeSpaceUnknown,
        201 => CaptureFailureKind.LibraryFull,
        202 => CaptureFailureKind.LibraryIoError,
        203 => CaptureFailureKind.LibraryBusy,
        204 => CaptureFailureKind.InvariantViolation,
        205 => CaptureFailureKind.SourceChanged,
        206 => CaptureFailureKind.LibraryUnavailable,
        207 => CaptureFailureKind.Catastrophic,   // reserved: readable (a future writer may have used it), never written
        _ => throw Unknown("CaptureFailureKind", code),
    };

    /// <summary><c>snapshot.scanner_contract</c>: 1 = v1.0 traversal and metadata semantics.</summary>
    internal const int ScannerContractV1 = 1;

    /// <summary><c>snapshot.spool_format</c>: 1 (§11.7).</summary>
    internal const int SpoolFormat1 = 1;

    /// <summary>The whole mapping, one line per member, for TEST-S2: it renders the table the code actually applies, so the
    /// frozen expectation cannot drift from the behaviour.</summary>
    internal static IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        foreach (var v in Enum.GetValues<SnapshotState>()) lines.Add($"SnapshotState.{v} = {ToCode(v)}");
        foreach (var v in new[] { ScanCompletionState.Complete, ScanCompletionState.Incomplete }) lines.Add($"ScanCompletionState.{v} = {ToStoredCompletion(v)}");
        foreach (var v in Enum.GetValues<FolderScanStatus>()) lines.Add($"FolderScanStatus.{v} = {ToCode(v)}");
        foreach (var v in Enum.GetValues<ScanErrorType>()) lines.Add($"ScanErrorType.{v} = {ToCode(v)}");
        foreach (var v in Enum.GetValues<IdentityConfidence>()) lines.Add($"IdentityConfidence.{v} = {ToCode(v)}");
        foreach (var v in Enum.GetValues<IdentityBasis>()) lines.Add($"IdentityBasis.{v} = {ToCode(v)}");
        foreach (var v in Enum.GetValues<SourceKind>()) lines.Add($"SourceKind.{v} = {ToCode(v)}");
        foreach (var v in Enum.GetValues<AttemptOutcome>()) lines.Add($"AttemptOutcome.{v} = {ToCode(v)}");
        foreach (var v in Enum.GetValues<CaptureFailureKind>().Where(v => v != CaptureFailureKind.Catastrophic)) lines.Add($"CaptureFailureKind.{v} = {ToCode(v)}");
        lines.Add($"CaptureFailureKind.{CaptureFailureKind.Catastrophic} = 207 (reserved, never written)");
        foreach (var v in Enum.GetValues<LibraryInsideSource>()) lines.Add($"LibraryInsideSource.{v} = {ToCode(v)}");
        lines.Add($"scanner_contract = {ScannerContractV1}");
        lines.Add($"spool_format = {SpoolFormat1}");
        return lines;
    }

    private static LibraryDataException Unknown(string type, long code) => new($"The Library holds a {type} code ({code}) that this version does not know.");
}
