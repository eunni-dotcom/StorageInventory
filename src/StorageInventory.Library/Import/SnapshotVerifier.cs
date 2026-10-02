using System.Collections;
using Microsoft.Data.Sqlite;

namespace StorageInventory.Library;

/// <summary>Runs constant queries; implemented by a read-only connection (tests, later the History services) and by the writer
/// inside its transaction, which is the only connection that can see the uncommitted rows of the import.</summary>
internal interface IQueryRunner
{
    object? Scalar(string sql, params (string Name, object? Value)[] parameters);

    void Rows(string sql, Action<SqliteDataReader> each, params (string Name, object? Value)[] parameters);
}

/// <summary>The writer's queries inside its transaction, guarded by the lease on every statement (OBS-15).</summary>
internal sealed class WriterQueryRunner(WriterConnection writer, MutationLease lease) : IQueryRunner
{
    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var statement = writer.Prepare(lease, sql, [.. parameters.Select(p => p.Name)]);
        for (var i = 0; i < parameters.Length; i++) statement.Set(i, parameters[i].Value);
        return statement.ExecuteScalar(lease);
    }

    public void Rows(string sql, Action<SqliteDataReader> each, params (string Name, object? Value)[] parameters)
    {
        using var statement = writer.Prepare(lease, sql, [.. parameters.Select(p => p.Name)]);
        for (var i = 0; i < parameters.Length; i++) statement.Set(i, parameters[i].Value);
        statement.ExecuteReader(lease, reader => { while (reader.Read()) each(reader); });
    }
}

/// <summary>
/// The Library-side mirror of <c>FolderAggregator.Verify</c> (§9.5, SCH-06, IMP-05 c): invariants 1 to 12 for one snapshot, checked
/// with aggregate SQL and two streaming bitmaps, so that no temporary B-tree is needed (A-24, SEC-05). T-IMPORT runs it before
/// its final statements and rolls back on any failure; tests run it over committed snapshots too. Invariants 0 and 13 are
/// established by the final statements and the open-time check.
/// </summary>
internal static class SnapshotVerifier
{
    /// <summary>Verifies invariants 1 to 12. Returns null when every one holds, otherwise the first that does not.</summary>
    internal static string? Verify(IQueryRunner q, long snapshotId, long sourceId, long files, long bytes, long folders, long realErrors, bool expectComplete)
    {
        (string, object?)[] snapshot = [("$snapshot_id", snapshotId)];
        (string, object?)[] snapshotAndSource = [("$snapshot_id", snapshotId), ("$source_id", sourceId)];

        // 1: one root row, discovery index 0
        long rootRows = 0, rootIndex = -1;
        q.Rows(ImportSql.VerifyRoot, r => { rootRows = r.GetInt64(0); rootIndex = r.GetInt64(1); }, snapshot);
        if (rootRows != 1 || rootIndex != 0) return $"invariant 1: {rootRows} root rows, root discovery index {rootIndex}";

        // 2: closure of paths and folders
        if (Count(q, ImportSql.VerifyFolderPathsOfSource, snapshotAndSource) != 0) return "invariant 2: a folder row does not refer to a folder path of this source";
        if (Count(q, ImportSql.VerifyFileFolders, snapshot) != 0) return "invariant 2: a file row refers to a folder with no row in this snapshot";

        // 3: parent closure
        if (Count(q, ImportSql.VerifyParentClosure, snapshot) != 0) return "invariant 3: a folder has no listed, earlier parent in this snapshot";

        // 4: name closure
        if (Count(q, ImportSql.VerifyFileNames, snapshot) != 0) return "invariant 4: a file name is not in the dictionary";
        if (Count(q, ImportSql.VerifyFolderNames, snapshot) != 0) return "invariant 4: a folder name is not in the dictionary";

        // 5: per-folder direct totals
        if (Count(q, ImportSql.VerifyDirectTotals, snapshotAndSource) != 0) return "invariant 5: a folder's direct counts differ from the rows stored beneath it";

        // 6: sealed totals against the data, and the root's totals
        long measuredFiles = 0, measuredBytes = 0, measuredFolders = 0;
        q.Rows(ImportSql.MeasureSnapshot, r => { measuredFiles = r.GetInt64(0); measuredBytes = r.GetInt64(1); measuredFolders = r.GetInt64(2); }, snapshot);
        if (measuredFiles != files || measuredBytes != bytes || measuredFolders != folders)
        {
            return $"invariant 6: the data holds {measuredFiles} files, {measuredBytes} bytes, {measuredFolders} folders; sealed totals are {files}, {bytes}, {folders}";
        }
        long rootFiles = -1, rootBytes = -1, rootSubfolders = -1, rootComplete = -1;
        q.Rows(ImportSql.SelectRootTotals, r => { rootFiles = r.GetInt64(0); rootBytes = r.GetInt64(1); rootSubfolders = r.GetInt64(2); rootComplete = r.GetInt64(3); }, snapshot);
        if (rootFiles != files || rootBytes != bytes || rootSubfolders != folders - 1)
        {
            return $"invariant 6: the root totals are {rootFiles} files, {rootBytes} bytes, {rootSubfolders} subfolders; sealed totals are {files}, {bytes}, {folders - 1} subfolders";
        }

        // 7: completeness mirrors the root
        if ((rootComplete == 1) != expectComplete) return "invariant 7: completeness does not match the root's subtree_complete";

        // 8 and 9: unlisted folders have no children; incompleteness propagates upwards
        if (Count(q, ImportSql.VerifyNoChildrenOfUnlisted, snapshotAndSource) != 0) return "invariant 8: a skipped or unreadable folder has children";
        if (Count(q, ImportSql.VerifyIncompleteFolders, snapshot) != 0) return "invariant 9: an unreadable or partial folder is marked subtree-complete";
        if (Count(q, ImportSql.VerifyIncompleteAncestors, snapshot) != 0) return "invariant 9: an ancestor of an incomplete folder is marked subtree-complete";

        // 10: scan_errors counts the non-informational error rows
        if (Count(q, ImportSql.CountRealScanErrors, snapshot) != realErrors) return "invariant 10: scan_errors differs from the non-informational error rows";

        // 11: each seq 0..files-1 and each discovery index 0..folders-1 exactly once, by streaming bitmaps (no sort, no temp B-tree)
        var seqProblem = CheckPermutation(q, ImportSql.StreamFileSequences, snapshot, files);
        if (seqProblem is not null) return "invariant 11: file seq " + seqProblem;
        var indexProblem = CheckPermutation(q, ImportSql.StreamDiscoveryIndexes, snapshot, folders);
        if (indexProblem is not null) return "invariant 11: folder discovery_index " + indexProblem;

        // 12: extension totals sum to files and bytes
        long extFiles = -1, extBytes = -1;
        q.Rows(ImportSql.SumExtensionTotals, r => { extFiles = r.GetInt64(0); extBytes = r.GetInt64(1); }, snapshot);
        if (extFiles != files || extBytes != bytes) return $"invariant 12: extension totals sum to {extFiles} files and {extBytes} bytes";
        return null;
    }

    /// <summary>Verifies a COMMITTED snapshot against its own sealed totals (read from its snapshot row), as every use of a snapshot
    /// does (D-47). Returns null when invariants 1 to 12 hold.</summary>
    internal static string? VerifyPublished(IQueryRunner q, long snapshotId)
    {
        long source = 0, files = 0, folders = 0, bytes = 0, errors = 0, completeness = 0;
        var found = false;
        q.Rows(ReadSql.SelectSnapshotSealed, r =>
        {
            found = true;
            source = r.GetInt64(0); files = r.GetInt64(1); folders = r.GetInt64(2); bytes = r.GetInt64(3); errors = r.GetInt64(4); completeness = r.GetInt64(5);
        }, ("$snapshot_id", snapshotId));
        if (!found) return "the snapshot row does not exist";
        return Verify(q, snapshotId, source, files, bytes, folders, errors, completeness == 0);
    }

    private static long Count(IQueryRunner q, string sql, (string, object?)[] parameters) =>
        Convert.ToInt64(q.Scalar(sql, parameters) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The streamed values must be exactly 0 .. expected-1, each once. Returns a description of the first problem.</summary>
    private static string? CheckPermutation(IQueryRunner q, string sql, (string, object?)[] parameters, long expected)
    {
        if (expected < 0 || expected > int.MaxValue) return "total is out of range";
        var seen = new BitArray((int)expected);
        long count = 0;
        string? problem = null;
        q.Rows(sql, r =>
        {
            if (problem is not null) return;
            var value = r.GetInt64(0);
            count++;
            if (value < 0 || value >= expected) problem = $"value {value} is outside 0..{expected - 1}";
            else if (seen[(int)value]) problem = $"value {value} appears twice";
            else seen[(int)value] = true;
        }, parameters);
        if (problem is null && count != expected) problem = $"has {count} rows, expected {expected}";
        return problem;
    }
}
