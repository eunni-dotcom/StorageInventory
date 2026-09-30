namespace StorageInventory.Core.Scanning;

/// <summary>Thrown when the scanner's own accounting does not add up. This is a bug, never a filesystem condition,
/// so the scan fails loudly instead of producing reports that might be wrong.</summary>
internal sealed class ScanConsistencyException(string message) : Exception("Internal consistency check failed: " + message);

/// <summary>Completeness summary derived from the aggregated folder table.</summary>
internal sealed record CompletenessSummary(bool RootSubtreeComplete, long LocallyIncompleteFolders, long AffectedAncestorFolders);

/// <summary>
/// Recursive folder totals in one bottom-up pass (no re-scanning), plus the invariants that prove the result.
/// Children always have a larger index than their parent, so walking the table from the end means every folder has
/// received its children's complete totals before it adds its own direct values and passes its finished total to its
/// single parent. Every file is therefore counted exactly once at every ancestor level.
/// </summary>
internal static class FolderAggregator
{
    public static CompletenessSummary Aggregate(IReadOnlyList<FolderState> folders, long scannedFileCount, long scannedBytes)
    {
        if (folders.Count == 0 || folders[0].ParentIndex != -1) throw new ScanConsistencyException("the folder table has no root.");

        foreach (var f in folders)
        {
            if (f.TotalSizeBytes != 0 || f.TotalFileCount != 0 || f.TotalSubfolderCount != 0)
            {
                throw new ScanConsistencyException($"'{f.RelativePath}' was aggregated twice.");
            }
        }

        for (var i = folders.Count - 1; i >= 0; i--)
        {
            var folder = folders[i];
            folder.TotalSizeBytes += folder.DirectSizeBytes;
            folder.TotalFileCount += folder.DirectFileCount;
            if (i == 0) break;

            if (folder.ParentIndex < 0 || folder.ParentIndex >= i)
            {
                throw new ScanConsistencyException($"'{folder.RelativePath}' has parent index {folder.ParentIndex}, which is not before its own index {i}.");
            }
            var parent = folders[folder.ParentIndex];
            parent.TotalSizeBytes += folder.TotalSizeBytes;
            parent.TotalFileCount += folder.TotalFileCount;
            parent.TotalSubfolderCount += 1 + folder.TotalSubfolderCount;
            if (folder.LargestFileBytes > parent.LargestFileBytes)
            {
                parent.LargestFileBytes = folder.LargestFileBytes;
                parent.LargestFileRelativePath = folder.LargestFileRelativePath;
            }
            if (!folder.SubtreeComplete) parent.SubtreeComplete = false;
        }

        Verify(folders, scannedFileCount, scannedBytes);

        long locallyIncomplete = 0, affectedAncestors = 0;
        foreach (var f in folders)
        {
            if (f.Status is FolderScanStatus.Unreadable or FolderScanStatus.Partial) locallyIncomplete++;
            else if (!f.SubtreeComplete) affectedAncestors++;
        }
        return new CompletenessSummary(folders[0].SubtreeComplete, locallyIncomplete, affectedAncestors);
    }

    /// <summary>The scanner's invariants. They prove the accounting is internally consistent; they are NOT proof that
    /// unreadable entries do not exist (those are reported through completeness instead).</summary>
    private static void Verify(IReadOnlyList<FolderState> folders, long scannedFileCount, long scannedBytes)
    {
        var root = folders[0];
        if (root.TotalSizeBytes != scannedBytes)
            throw new ScanConsistencyException($"root total {root.TotalSizeBytes} bytes, but {scannedBytes} bytes were counted file by file.");
        if (root.TotalFileCount != scannedFileCount)
            throw new ScanConsistencyException($"root total {root.TotalFileCount} files, but {scannedFileCount} files were counted.");
        if (root.TotalSubfolderCount != folders.Count - 1)
            throw new ScanConsistencyException($"root has {root.TotalSubfolderCount} subfolders, but {folders.Count - 1} folder records exist below it.");

        long directBytes = 0, directFiles = 0, directSubfolders = 0;
        foreach (var f in folders)
        {
            directBytes += f.DirectSizeBytes;
            directFiles += f.DirectFileCount;
            directSubfolders += f.DirectSubfolderCount;
            if (f.TotalSizeBytes < f.DirectSizeBytes || f.TotalFileCount < f.DirectFileCount || f.TotalSubfolderCount < f.DirectSubfolderCount)
                throw new ScanConsistencyException($"'{f.RelativePath}' has a total smaller than its direct value.");
            if (f.Status == FolderScanStatus.ReparsePointSkipped && (f.DirectFileCount != 0 || f.DirectSubfolderCount != 0))
                throw new ScanConsistencyException($"'{f.RelativePath}' is a skipped reparse point but has contents.");
            if ((f.Status is FolderScanStatus.Unreadable or FolderScanStatus.Partial) && f.SubtreeComplete)
                throw new ScanConsistencyException($"'{f.RelativePath}' could not be fully read but is marked complete.");
        }
        if (directBytes != scannedBytes || directFiles != scannedFileCount)
            throw new ScanConsistencyException("the sum of direct folder values does not equal the files counted.");
        if (directSubfolders != folders.Count - 1)
            throw new ScanConsistencyException("the sum of direct subfolder counts does not equal the folder records below the root.");
    }

    /// <summary>Converts the aggregated table into public records (percentages computed exactly as the reference does).</summary>
    public static FolderInventoryRecord ToRecord(IReadOnlyList<FolderState> folders, int index, string rootFullPath)
    {
        var f = folders[index];
        var rootTotal = folders[0].TotalSizeBytes;
        var rootPrefix = rootFullPath.EndsWith('\\') ? rootFullPath : rootFullPath + "\\";
        double? percentOfParent = null;
        var parentRelative = "";
        if (index != 0)
        {
            var parent = folders[f.ParentIndex];
            parentRelative = parent.RelativePath;
            percentOfParent = parent.TotalSizeBytes > 0 ? f.TotalSizeBytes * 100.0 / parent.TotalSizeBytes : 0;
        }

        return new FolderInventoryRecord
        {
            Name = f.Name,
            RelativePath = f.RelativePath,
            ParentRelativePath = parentRelative,
            FullPath = index == 0 ? rootFullPath : rootPrefix + f.RelativePath,
            Depth = f.Depth,
            DirectSizeBytes = f.DirectSizeBytes,
            TotalSizeBytes = f.TotalSizeBytes,
            DirectFileCount = f.DirectFileCount,
            TotalFileCount = f.TotalFileCount,
            DirectSubfolderCount = f.DirectSubfolderCount,
            TotalSubfolderCount = f.TotalSubfolderCount,
            LargestFileBytes = f.LargestFileBytes >= 0 ? f.LargestFileBytes : null,
            LargestFileRelativePath = f.LargestFileRelativePath,
            PercentOfRoot = rootTotal > 0 ? f.TotalSizeBytes * 100.0 / rootTotal : 0,
            PercentOfParent = percentOfParent,
            CreatedUtc = f.CreatedUtc,
            ModifiedUtc = f.ModifiedUtc,
            Attributes = f.Attributes,
            Status = f.Status,
            StatusReason = f.StatusReason,
            SubtreeComplete = f.SubtreeComplete,
        };
    }
}
