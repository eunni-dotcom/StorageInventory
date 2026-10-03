using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageInventory.Library.Tests.PerfGate;

// The run record (§15.4 "Recorded per run"): ONE JSON object per run, written by the measuring child as the last line of its output
// and appended, by the orchestrator, to the session's append-only raw file. It is the immutable raw result the checkers read. Nothing in
// it is a path of the machine: the analysis artefacts are named by file name inside the analysis directory the session keeps.

/// <summary>The binary under measurement: the commit and the hash of the build output (final repair O06).</summary>
internal sealed record BinaryRecord(string Commit, bool Dirty, string OutputHash, string Engine);

/// <summary>The cell's numeric parameters and class, as the C# classifier computed them from the parameters alone.</summary>
internal sealed record CellRecord(string Id, string Label, string Class, string Kind, long NominalFiles, decimal D, string? Model, decimal Rho, decimal Delta, decimal Alpha,
    string? Family, bool FamilyRescan, string Library, string RunFamily, string GeneratorParameters, double Scale, long TargetFiles, long PrefillPerSnapshot, string PrefillKey);

/// <summary>The generator's realised statistics, computed before the import (§15.4: files, distinct names, mean length, names seen once,
/// share of files in the top 1% of names, churn counts).</summary>
internal sealed record GeneratedRecord(long Files, long Distinct, double MeanLength, long NamesSeenOnce, double Top1ShareOfFiles, long Renamed, long Deleted, long Added, long Vocabulary, string Source);

internal sealed record LibraryRecord(long BeforeBytes, long AfterBytes, long GrowthBytes, long PageSize, long PageCountBefore, long FreelistBefore, long NamesBefore, long NameBytesBefore, long FolderPathsBefore, long SnapshotsBefore);

internal sealed record PhaseRecord(double FoldersMs, double FilesMs, double ErrorsMs, double VerificationMs, double CommitMs);

internal sealed record JournalRecord(long LengthAtFinalCheck, long SampledPeak, long Samples, double LargestSamplingIntervalMs, long PeakJournalBytesReported);

/// <summary>One IMP-11 check: exactly what the importer passed to the check, and the free space of the Library's volume read there.</summary>
internal sealed record CheckRecord(string Kind, long Rows, long PageCount, long PageSize, long MainFileLength, long JournalLength, long PendingGrowth, long FreeBytes);

internal sealed record Imp11Record(IReadOnlyList<long[]> Checks, int SpaceChecks, long FinalPendingGrowth, long CommitGrowth, bool CommitGrowthEqualsFinalPending, long IntervalsOverAllowance,
    double LargestIntervalGrowthOverAllowance, long LargestIntervalGrowthBytes, long LargestIntervalIndex, double LargestIntervalRows);

internal sealed record TokenRecord(long Observations, double MaxGapSeconds);

internal sealed record MemoryRecord(long WorkingSetBeforeBytes, long PeakWorkingSetBytes, double GcPauseMs, int Gen2Collections);

/// <summary>The target of the import, from the harness: an existing source (its id) or "new source". The checker takes it from here.</summary>
internal sealed record TargetRecord(string Source, long? SourceId);

/// <summary>The attribution fields (PERF-15 (a)), MANDATORY in an attribution run's record: the target, the journal's length read through
/// a live handle immediately before COMMIT, the journal copy's name and length, and the pre-import copy's name, length and SHA-256 taken
/// when the copy was made (after T0 commits, before BEGIN).</summary>
internal sealed record AttributionRecord(TargetRecord Target, string Layout, long JournalLengthBeforeCommit, string JournalCopy, long JournalCopyLength,
    string DatabaseCopy, long DatabaseCopyLength, string DatabaseCopySha256);

/// <summary>The state after a cancellation, asserted by the child (PERF-15 (c)): everything rolled back.</summary>
internal sealed record RollbackRecord(bool RolledBack, long SnapshotsAfter, long NamesAfter, long MainLengthBefore, long MainLengthAfter, long JournalLengthAfter, bool OlderSnapshotsVerify, string? Problem);

internal sealed record CancelRecord(int Point, string PointName, double CancelToReturnSeconds, long RowsAtCancel, long JournalAtCancelBytes, string Outcome, RollbackRecord? Rollback, bool PublishedAfterCancel);

internal sealed record RecoveryRecord(double OpenSeconds, string State, long HotJournalBytes, long MainBeforeBytes, long MainAfterBytes, long JournalAfterBytes, long Snapshots, bool OlderSnapshotsVerify, string? Problem);

internal sealed record DeleteRecord(double Seconds, long PeakJournalBytes);

/// <summary>The whole record of one run.</summary>
internal sealed record RunRecord
{
    public int Schema { get; init; } = GateConstants.RecordSchema;
    public required string Kind { get; init; }           // timed | attribution | cancel | crash | delete | cancel-after-final
    public required string Label { get; init; }
    public required BinaryRecord Binary { get; init; }
    public required CellRecord Cell { get; init; }
    public required string OperationStartUtc { get; init; }    // wall clock just before ImportSnapshotAsync is called
    public required double OperationSeconds { get; init; }     // that call to its return (or throw)
    public required string Outcome { get; init; }
    public GeneratedRecord? Generated { get; init; }
    public required LibraryRecord Library { get; init; }
    public long Files { get; init; }
    public long Folders { get; init; }
    public long NewNames { get; init; }
    public double ImportSeconds { get; init; }               // BEGIN IMMEDIATE to COMMIT's return (ImportResult.Elapsed)
    public double FilesPerSecond { get; init; }               // file rows / ImportSeconds, verification included
    public PhaseRecord? Phases { get; init; }
    public JournalRecord? Journal { get; init; }
    public Imp11Record? Imp11 { get; init; }
    public TokenRecord? Token { get; init; }
    public MemoryRecord? Memory { get; init; }
    public AttributionRecord? Attribution { get; init; }
    public CancelRecord? Cancel { get; init; }
    public RecoveryRecord? Recovery { get; init; }
    public DeleteRecord? Delete { get; init; }
    public long JournalAtKillBytes { get; init; }
    public string? KilledLibraryDirectory { get; init; }     // crash runs: where the killed child left its Library (the orchestrator removes it after the recovery)
    public string? KilledAppData { get; init; }
}

/// <summary>JSON of the record and its required-field validation (the C# side of the binding: it knows the fields a checker needs).</summary>
internal static class RecordJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    internal static string Serialize(RunRecord record) => JsonSerializer.Serialize(record, Options);

    internal static RunRecord? Deserialize(string json) => JsonSerializer.Deserialize<RunRecord>(json, Options);

    /// <summary>The problems that make a record unfit as raw evidence of its kind (empty when it is fit). Independent of Python: it reads
    /// the JSON, not the C# object, so it validates what a reader of the file would see.</summary>
    internal static List<string> Validate(string json)
    {
        var problems = new List<string>();
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException ex)
        {
            return ["not JSON: " + ex.Message];
        }
        if (root.ValueKind != JsonValueKind.Object) return ["not a JSON object"];

        bool Has(JsonElement e, string name, JsonValueKind? kind = null) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null && (kind is null || v.ValueKind == kind);
        void Need(JsonElement e, string path, params string[] names)
        {
            foreach (var n in names) if (!Has(e, n)) problems.Add($"missing {path}{n}");
        }

        Need(root, "", "schema", "kind", "label", "binary", "cell", "operationStartUtc", "operationSeconds", "outcome", "library");
        if (root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.GetInt32() != GateConstants.RecordSchema) problems.Add("unknown schema version");
        if (Has(root, "binary")) Need(root.GetProperty("binary"), "binary.", "commit", "outputHash", "engine");
        if (Has(root, "cell")) Need(root.GetProperty("cell"), "cell.", "id", "label", "class", "kind", "nominalFiles", "prefillKey", "scale");
        if (Has(root, "library")) Need(root.GetProperty("library"), "library.", "beforeBytes", "afterBytes", "pageSize");
        var kindText = Has(root, "kind") ? root.GetProperty("kind").GetString() : null;
        if (kindText is "timed" or "attribution" or "cancel-after-final" or "delete")
        {
            Need(root, "", "imp11", "token", "journal", "memory", "phases");
            if (Has(root, "imp11"))
            {
                var imp = root.GetProperty("imp11");
                Need(imp, "imp11.", "checks", "spaceChecks", "finalPendingGrowth", "commitGrowth", "commitGrowthEqualsFinalPending");
                if (Has(imp, "checks", JsonValueKind.Array) && imp.GetProperty("checks").GetArrayLength() < 2) problems.Add("imp11.checks holds fewer than two checks");
            }
            if (Has(root, "token")) Need(root.GetProperty("token"), "token.", "observations", "maxGapSeconds");
            if (Has(root, "journal")) Need(root.GetProperty("journal"), "journal.", "lengthAtFinalCheck", "sampledPeak", "largestSamplingIntervalMs");
            if (kindText == "timed" && Has(root, "outcome") && root.GetProperty("outcome").GetString() == "Published") Need(root, "", "importSeconds", "filesPerSecond", "generated");
        }
        if (kindText == "attribution")
        {
            if (!Has(root, "attribution")) problems.Add("missing attribution");
            else
            {
                var a = root.GetProperty("attribution");
                Need(a, "attribution.", "target", "layout", "journalLengthBeforeCommit", "journalCopy", "journalCopyLength", "databaseCopy", "databaseCopyLength", "databaseCopySha256");
                if (Has(a, "target"))
                {
                    var t = a.GetProperty("target");
                    Need(t, "attribution.target.", "source");
                    if (Has(t, "source") && t.GetProperty("source").GetString() == "existing" && !Has(t, "sourceId")) problems.Add("missing attribution.target.sourceId");
                }
                if (Has(a, "databaseCopySha256") && a.GetProperty("databaseCopySha256").GetString() is not { Length: 64 }) problems.Add("attribution.databaseCopySha256 is not a SHA-256");
            }
        }
        if (kindText == "cancel")
        {
            Need(root, "", "cancel");
            if (Has(root, "cancel")) Need(root.GetProperty("cancel"), "cancel.", "point", "cancelToReturnSeconds", "rollback", "outcome");
        }
        if (kindText == "crash")
        {
            Need(root, "", "journalAtKillBytes");
        }
        return problems;
    }
}
