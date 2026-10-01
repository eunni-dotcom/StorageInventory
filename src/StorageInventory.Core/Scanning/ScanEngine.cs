using System.IO.Enumeration;
using StorageInventory.Core.Paths;

namespace StorageInventory.Core.Scanning;

/// <summary>Thrown when the scan meets one of this run's own report files: the output folder is reachable from the
/// source through an alias the up-front checks could not see. The scan stops at once.</summary>
internal sealed class OutputInsideScannedTreeException(string foundPath)
    : Exception($"Found this run's own report file inside the scanned tree ('{foundPath}'). The report folder is reachable from the source through an alias, so the scan was stopped to avoid writing into the scanned tree.")
{
    public string FoundPath { get; } = foundPath;
}

/// <summary>
/// The traversal. Metadata only: folders are listed, files are never opened, and reparse points are never followed.
/// Depth-first with an explicit stack (no recursion), in the same order as the PowerShell reference so that parity
/// can be proven row by row. Files and error rows are streamed to an <see cref="IScanObserver"/> (in the pipeline, the
/// <see cref="ObserverFanOut"/>, so one traversal feeds every consumer); only one <see cref="FolderState"/> per folder
/// is kept. The engine calls only <see cref="IScanObserver.OnFile"/> and <see cref="IScanObserver.OnError"/>: folder
/// states stay provisional and private to it until the pipeline finalises them after aggregation (SINK-08).
/// </summary>
internal sealed class ScanEngine
{
    private static readonly EnumerationOptions ListingOptions = new()
    {
        RecurseSubdirectories = false,   // we walk ourselves, so reparse points can be checked before entering
        IgnoreInaccessible = false,      // every unreadable folder must be reported, never silently skipped
        AttributesToSkip = 0,            // include hidden and system entries
        ReturnSpecialDirectories = false,
    };

    private readonly string _rootFullPath;
    private readonly IScanObserver _observer;
    private readonly IReadOnlySet<string> _ownReportNames;
    private readonly Action<string>? _onProgress;

    public ScanEngine(string rootFullPath, IScanObserver observer, IReadOnlySet<string> ownReportNames, Action<string>? onProgress = null)
    {
        _rootFullPath = rootFullPath;
        _observer = observer;
        _ownReportNames = ownReportNames;
        _onProgress = onProgress;
    }

    /// <summary>One entry per folder, in discovery order (index 0 = root; parents before children).</summary>
    public List<FolderState> Folders { get; } = [];

    public long FileCount { get; private set; }
    public long TotalBytes { get; private set; }

    /// <summary>Real errors (informational reparse-point rows excluded).</summary>
    public long ScanErrorCount { get; private set; }

    public long ReparsePointsSkipped { get; private set; }
    public long FileReparsePoints { get; private set; }
    public Dictionary<ScanErrorType, long> ErrorCounts { get; } = [];

    /// <summary>Walks the tree. Throws <see cref="OperationCanceledException"/> on cancellation,
    /// <see cref="OutputInsideScannedTreeException"/> for the tripwire, and lets observer exceptions through.</summary>
    public void Run(CancellationToken cancellationToken)
    {
        AddRoot();
        var pending = new Stack<int>();
        pending.Push(0);
        var entriesSinceCheck = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderIndex = pending.Pop();
            var folder = Folders[folderIndex];
            var directoryPath = folder.PendingFullPath!;
            folder.PendingFullPath = null;
            var isRoot = folderIndex == 0;
            _onProgress?.Invoke(folder.RelativePath);

            if (!isRoot && BecameReparsePoint(folder, directoryPath)) continue;

            IEnumerator<RawEntry> entries;
            try
            {
                entries = new FileSystemEnumerable<RawEntry>(directoryPath, Transform, ListingOptions).GetEnumerator();
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                var (type, message) = ScanErrorClassifier.Classify(ex);
                AddError(directoryPath, type, message);
                folder.Status = FolderScanStatus.Unreadable;
                folder.StatusReason = type;
                folder.SubtreeComplete = false;
                continue;
            }

            using (entries)
            {
                while (true)
                {
                    RawEntry entry;
                    try
                    {
                        if (!entries.MoveNext()) break;
                        entry = entries.Current;
                    }
                    catch (Exception ex) when (IsReadFailure(ex))
                    {
                        var (type, message) = ScanErrorClassifier.Classify(ex);
                        AddError(directoryPath, type, "Listing stopped part-way: " + message);
                        folder.Status = FolderScanStatus.Partial;
                        folder.StatusReason = type;
                        folder.SubtreeComplete = false;
                        break;
                    }

                    if (++entriesSinceCheck >= 256)
                    {
                        entriesSinceCheck = 0;
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (entry.IsDirectory)
                    {
                        AddChildFolder(folderIndex, folder, isRoot, entry, pending);
                    }
                    else
                    {
                        AddFile(folderIndex, folder, isRoot, entry);
                    }
                }
            }
        }
    }

    private void AddRoot()
    {
        var root = new DirectoryInfo(_rootFullPath);
        var state = new FolderState
        {
            ParentIndex = -1,
            Depth = 0,
            Name = root.Name,
            RelativePath = ".",
            Attributes = root.Attributes,
            PendingFullPath = _rootFullPath,
        };
        state.CreatedUtc = ReadTimestamp(() => root.CreationTimeUtc, root.FullName, "Created");
        state.ModifiedUtc = ReadTimestamp(() => root.LastWriteTimeUtc, root.FullName, "Modified");
        Folders.Add(state);
    }

    private void AddChildFolder(int parentIndex, FolderState parent, bool parentIsRoot, in RawEntry entry, Stack<int> pending)
    {
        var child = new FolderState
        {
            ParentIndex = parentIndex,
            Depth = parent.Depth + 1,
            Name = entry.Name,
            RelativePath = parentIsRoot ? entry.Name : parent.RelativePath + "\\" + entry.Name,
            Attributes = entry.Attributes,
            CreatedUtc = entry.CreatedUtc,
            ModifiedUtc = entry.ModifiedUtc,
        };
        if (entry.CreatedError is not null) AddError(entry.FullPath, ScanErrorType.InvalidTimestamp, "Created time: " + entry.CreatedError);
        if (entry.ModifiedError is not null) AddError(entry.FullPath, ScanErrorType.InvalidTimestamp, "Modified time: " + entry.ModifiedError);

        var childIndex = Folders.Count;
        Folders.Add(child);
        parent.DirectSubfolderCount++;

        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // SAFETY: junctions, symbolic links, mount points and placeholders are NEVER entered.
            child.Status = FolderScanStatus.ReparsePointSkipped;
            AddError(entry.FullPath, ScanErrorType.ReparsePointSkipped,
                "Not followed: " + PathPolicy.DescribeReparsePoint(new DirectoryInfo(entry.FullPath)));
            return;
        }

        child.PendingFullPath = entry.FullPath;
        pending.Push(childIndex);
    }

    private void AddFile(int folderIndex, FolderState folder, bool folderIsRoot, in RawEntry entry)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // SAFETY: a file reparse point is counted with the size the listing reports (a symbolic link counts as
            // itself); its target is never opened or followed.
            AddError(entry.FullPath, ScanErrorType.ReparsePointFile,
                $"Counted as a file with its listed size of {entry.Length} bytes; target not followed: {PathPolicy.DescribeReparsePoint(new FileInfo(entry.FullPath))}");
        }

        if (_ownReportNames.Contains(entry.Name)) throw new OutputInsideScannedTreeException(entry.FullPath);

        var relativePath = folderIsRoot ? entry.Name : folder.RelativePath + "\\" + entry.Name;
        folder.DirectSizeBytes += entry.Length;
        folder.DirectFileCount++;
        if (entry.Length > folder.LargestFileBytes)
        {
            folder.LargestFileBytes = entry.Length;
            folder.LargestFileRelativePath = relativePath;
        }
        FileCount++;
        TotalBytes += entry.Length;

        if (entry.CreatedError is not null) AddError(entry.FullPath, ScanErrorType.InvalidTimestamp, "Created time: " + entry.CreatedError);
        if (entry.ModifiedError is not null) AddError(entry.FullPath, ScanErrorType.InvalidTimestamp, "Modified time: " + entry.ModifiedError);
        if (entry.AccessError is not null) AddError(entry.FullPath, ScanErrorType.InvalidTimestamp, "Last-access time: " + entry.AccessError);

        var record = new FileInventoryRecord(entry.Name, ExtensionOf(entry.Name), relativePath, folder.RelativePath, entry.FullPath,
            entry.Length, entry.CreatedUtc, entry.ModifiedUtc, entry.AccessUtc, entry.Attributes);
        _observer.OnFile(record, folderIndex);

        if (FileCount % 1024 == 0) _onProgress?.Invoke(folder.RelativePath);
    }

    /// <summary>
    /// Improvement over the reference: re-reads a folder's attributes immediately before entering it. A folder that
    /// was replaced by a junction or link after being listed is then skipped instead of followed. This narrows, but
    /// cannot close, the race; traversal still terminates in any case because every level lengthens the path, which
    /// Windows bounds at 32,767 characters.
    /// </summary>
    private bool BecameReparsePoint(FolderState folder, string directoryPath)
    {
        FileAttributes now;
        try
        {
            now = File.GetAttributes(directoryPath);   // GetFileAttributesEx: reads the entry itself, never its target
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return false;   // let the listing report the failure exactly as the reference does
        }
        if (!now.HasFlag(FileAttributes.ReparsePoint)) return false;

        folder.Status = FolderScanStatus.ReparsePointSkipped;
        folder.Attributes = now;
        AddError(directoryPath, ScanErrorType.ReparsePointSkipped,
            "Became a reparse point after it was listed; not followed: " + PathPolicy.DescribeReparsePoint(new DirectoryInfo(directoryPath)));
        return true;
    }

    private DateTime? ReadTimestamp(Func<DateTime> read, string path, string kind)
    {
        try
        {
            return read();
        }
        catch (ArgumentException ex)
        {
            AddError(path, ScanErrorType.InvalidTimestamp, $"{kind} time: {ex.Message}");
            return null;
        }
    }

    private void AddError(string path, ScanErrorType type, string message)
    {
        if (type == ScanErrorType.ReparsePointSkipped) ReparsePointsSkipped++;
        else if (type == ScanErrorType.ReparsePointFile) FileReparsePoints++;
        else ScanErrorCount++;
        ErrorCounts[type] = ErrorCounts.GetValueOrDefault(type) + 1;
        _observer.OnError(new ScanErrorRecord(path, type, message));
    }

    /// <summary>Failures of reading the scanned tree. Cancellation and out-of-memory are never swallowed.</summary>
    private static bool IsReadFailure(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException);

    /// <summary>Same rule as the reference: from the last '.', unless the name ends with '.' (".gitignore" -> ".gitignore").</summary>
    internal static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[dot..] : "";
    }

    /// <summary>What the listing tells us about one entry. Built by a transform that never throws: a timestamp Windows
    /// stores but .NET cannot represent becomes null plus an error message, instead of aborting the listing.</summary>
    private readonly record struct RawEntry(
        string Name, string FullPath, bool IsDirectory, FileAttributes Attributes, long Length,
        DateTime? CreatedUtc, DateTime? ModifiedUtc, DateTime? AccessUtc,
        string? CreatedError, string? ModifiedError, string? AccessError);

    private static RawEntry Transform(ref FileSystemEntry e)
    {
        DateTime? created = null, modified = null, accessed = null;
        string? createdError = null, modifiedError = null, accessError = null;
        try { created = e.CreationTimeUtc.UtcDateTime; } catch (ArgumentException ex) { createdError = ex.Message; }
        try { modified = e.LastWriteTimeUtc.UtcDateTime; } catch (ArgumentException ex) { modifiedError = ex.Message; }
        try { accessed = e.LastAccessTimeUtc.UtcDateTime; } catch (ArgumentException ex) { accessError = ex.Message; }
        var isDirectory = e.IsDirectory;
        return new RawEntry(e.FileName.ToString(), e.ToFullPath(), isDirectory, e.Attributes, isDirectory ? 0 : e.Length,
            created, modified, accessed, createdError, modifiedError, accessError);
    }
}
