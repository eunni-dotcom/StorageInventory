using System.Buffers.Binary;
using System.Security.Cryptography;
using StorageInventory.Core.Scanning;
using StorageInventory.Core.Spool;

namespace StorageInventory.Core.Tests;

/// <summary>One call an observer received, recorded in order.</summary>
public abstract record Observed;
public sealed record ObservedStart(string RunId) : Observed;
public sealed record ObservedFile(FileInventoryRecord File, int FolderIndex) : Observed;
public sealed record ObservedError(ScanErrorRecord Error) : Observed;
public sealed record ObservedFolder(int Index, int ParentIndex, FolderInventoryRecord Folder) : Observed;
public sealed record ObservedEnd(string Outcome, ScanTotals? Totals) : Observed;

/// <summary>Keeps every call (fine for test fixtures; the product never does this).</summary>
internal sealed class RecordingObserver : IScanObserver
{
    public List<Observed> Calls { get; } = [];

    public void OnScanStarted(in ScanStartInfo start) => Calls.Add(new ObservedStart(start.RunId));
    public void OnFile(in FileInventoryRecord file, int folderIndex) => Calls.Add(new ObservedFile(file, folderIndex));
    public void OnError(ScanErrorRecord error) => Calls.Add(new ObservedError(error));
    public void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder) => Calls.Add(new ObservedFolder(index, parentIndex, folder));
    public void OnScanEnded(in ScanEndInfo end) => Calls.Add(new ObservedEnd(end.Outcome.ToString(), end.Totals));
}

/// <summary>
/// A complete, valid observation stream of a scan, generated without a filesystem: a random folder table aggregated
/// by the real <see cref="FolderAggregator"/>, files in one contiguous run per folder (runs in random order, as
/// depth-first pop order is), and error rows of every type interleaved anywhere, including inside runs.
/// </summary>
internal sealed class ObservationScript
{
    public const string RunId = "20261001_120000_abcdef";

    public required List<Observed> Records { get; init; }   // files and errors, in emission order
    public required List<ObservedFolder> Folders { get; init; }
    public required ScanTotals Totals { get; init; }

    /// <summary>Names that stress exactness: unpaired surrogates, surrogate pairs, NFC/NFD and case variants, formula
    /// and quote characters, spaces and dots, a 255-unit name, and characters Windows forbids (the codec must not care).</summary>
    public static readonly string[] EdgeNames =
    [
        "plain.txt", "\uD800", "a\uDC00b", "\uDC00\uD800", "x\uD83D", "\uD83D\uDE00 emoji.png", "é.txt", "e\u0301.txt",
        "Readme.MD", "readme.md", "README.md", "=SUM(A1).csv", "+plus", "-minus", "@at", "'apos'", "\"quoted\"",
        " leading space", "trailing space ", "trailing.dot.", "...", "한국어 파일.mp3", "ß", "ss", "İ", "i\u0307", "\u0345",
        new string('L', 255), "tab\tname", "nul\0name", "\uFFFE\uFFFF", "\u200Bzero-width", "a.b.c.d.e",
    ];

    public static ObservationScript Generate(int seed, int folders = 40, int maxFilesPerFolder = 12, int errors = 15)
    {
        var rng = new Random(seed);
        var table = new List<FolderState> { new() { ParentIndex = -1, Depth = 0, Name = "root" + EdgeNames[seed % EdgeNames.Length], RelativePath = "." } };
        for (var i = 1; i < folders; i++)
        {
            int parent;
            do { parent = rng.Next(i); } while (table[parent].Status is not (FolderScanStatus.Ok or FolderScanStatus.Partial));
            var status = rng.Next(10) switch
            {
                0 => FolderScanStatus.Unreadable,
                1 => FolderScanStatus.Partial,
                2 => FolderScanStatus.ReparsePointSkipped,
                _ => FolderScanStatus.Ok,
            };
            var f = new FolderState
            {
                ParentIndex = parent, Depth = table[parent].Depth + 1, Name = Name(rng, i), RelativePath = "f" + i,
                Status = status, Attributes = (FileAttributes)rng.Next(),
                CreatedUtc = Time(rng), ModifiedUtc = Time(rng),
            };
            if (status is FolderScanStatus.Unreadable or FolderScanStatus.Partial)
            {
                f.StatusReason = (ScanErrorType)rng.Next(0, 7);
                f.SubtreeComplete = false;
            }
            table.Add(f);
            table[parent].DirectSubfolderCount++;
        }

        // Files: one run per listed folder, runs in random order.
        var runs = new List<List<ObservedFile>>();
        long fileCount = 0, bytes = 0;
        foreach (var i in Enumerable.Range(0, folders).OrderBy(_ => rng.Next()))
        {
            var folder = table[i];
            if (folder.Status is not (FolderScanStatus.Ok or FolderScanStatus.Partial)) continue;
            var n = rng.Next(maxFilesPerFolder + 1);
            if (n == 0) continue;
            var run = new List<ObservedFile>();
            for (var k = 0; k < n; k++)
            {
                var size = rng.Next(4) == 0 ? 0 : (long)rng.Next() * rng.Next(1, 1000);
                var name = Name(rng, k);
                var record = new FileInventoryRecord(name, ScanEngine.ExtensionOf(name), "rel\\" + name, folder.RelativePath, "C:\\x\\" + name,
                    size, Time(rng), Time(rng), Time(rng), (FileAttributes)rng.Next());
                folder.DirectFileCount++;
                folder.DirectSizeBytes += size;
                if (size > folder.LargestFileBytes) { folder.LargestFileBytes = size; folder.LargestFileRelativePath = record.RelativePath; }
                fileCount++;
                bytes += size;
                run.Add(new ObservedFile(record, i));
            }
            runs.Add(run);
        }

        var stream = runs.SelectMany(r => r).Cast<Observed>().ToList();
        long scanErrors = 0, reparseSkipped = 0, fileReparse = 0;
        for (var e = 0; e < errors; e++)
        {
            var type = (ScanErrorType)rng.Next(0, 9);
            if (type == ScanErrorType.ReparsePointSkipped) reparseSkipped++;
            else if (type == ScanErrorType.ReparsePointFile) fileReparse++;
            else scanErrors++;
            var message = rng.Next(5) == 0 ? new string('m', rng.Next(0, 3000)) : "Message " + Name(rng, e);
            stream.Insert(rng.Next(stream.Count + 1), new ObservedError(new ScanErrorRecord("C:\\x\\" + Name(rng, e), type, message)));
        }

        var completeness = FolderAggregator.Aggregate(table, fileCount, bytes);
        var finalised = Enumerable.Range(0, folders)
            .Select(i => new ObservedFolder(i, table[i].ParentIndex, FolderAggregator.ToRecord(table, i, "C:\\x")))
            .ToList();
        return new ObservationScript
        {
            Records = stream,
            Folders = finalised,
            Totals = new ScanTotals
            {
                Files = fileCount, Folders = folders, Bytes = bytes, ScanErrors = scanErrors, ReparsePointsSkipped = reparseSkipped,
                FileReparsePoints = fileReparse, LocallyIncompleteFolders = completeness.LocallyIncompleteFolders,
                AffectedAncestorFolders = completeness.AffectedAncestorFolders,
            },
        };
    }

    /// <summary>Plays the script into an observer, as the pipeline would for a finished scan.</summary>
    public void Replay(IScanObserver observer, bool finish = true)
    {
        observer.OnScanStarted(new ScanStartInfo(RunId, "C:\\x", null, new HashSet<string>()));
        foreach (var r in Records)
        {
            if (r is ObservedFile f) observer.OnFile(f.File, f.FolderIndex);
            else observer.OnError(((ObservedError)r).Error);
        }
        foreach (var f in Folders) observer.OnFolderFinalised(f.Index, f.ParentIndex, f.Folder);
        observer.OnScanEnded(finish ? ScanEndInfo.Finished(Totals) : ScanEndInfo.Failed());
    }

    private static string Name(Random rng, int i) => rng.Next(3) == 0 ? EdgeNames[rng.Next(EdgeNames.Length)] : $"name{i}_{rng.Next(1000)}.bin";

    private static DateTime? Time(Random rng) => rng.Next(6) == 0 ? null : new DateTime(rng.NextInt64(0, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
}

/// <summary>Writes spools for tests, and an independent walker of the format-1 layout (written from the format
/// description, not from the product's reader) that locates records for corruption tests.</summary>
internal static class SpoolTestKit
{
    public static readonly byte[] Token = Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray();
    public static readonly DateTimeOffset FixedClock = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => FixedClock;
    }

    public static byte[] Write(ObservationScript script)
    {
        using var stream = new MemoryStream();
        using var writer = new SpoolWriter(stream, Token, new FixedTime());
        script.Replay(writer);
        if (writer.State != SpoolWriterState.Sealed) throw new InvalidOperationException("the writer did not seal: " + writer.State);
        return stream.ToArray();
    }

    public static SpoolVerification Verify(byte[] spool, ScanTotals? expected = null) =>
        SpoolReader.Verify(new MemoryStream(spool, writable: false), Token, expected);

    /// <summary>The bytes before the digest, re-sealed with a correct SHA-256: a rewrite that recomputes the digest.</summary>
    public static byte[] Reseal(byte[] withoutDigest) => [.. withoutDigest, .. SHA256.HashData(withoutDigest)];

    public static byte[] Body(byte[] spool) => spool[..^32];

    public sealed record Located(char Kind, int Offset, int Length, int FolderIndex);

    public sealed record Layout(int HeaderLength, List<Located> Records, List<Located> Folders, int RunIndexOffset, int RunCount, int TrailerOffset);

    /// <summary>Walks a spool by the frozen layout of format 1.</summary>
    public static Layout Walk(byte[] s)
    {
        var p = 8 + 2 + 2 + 16;
        var runIdLength = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(p));
        p += 2 + 2 * runIdLength + 2 + 8;
        var header = p;
        var records = new List<Located>();
        while (s[p] != 0x03)
        {
            var start = p;
            if (s[p] == 0x01)
            {
                var folder = BinaryPrimitives.ReadInt32LittleEndian(s.AsSpan(p + 1));
                var name = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(p + 5));
                p += 43 + 2 * name;
                records.Add(new Located('F', start, p - start, folder));
            }
            else if (s[p] == 0x02)
            {
                var path = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(p + 2));
                var message = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(p + 6 + 2 * path));
                p += 10 + 2 * (path + message);
                records.Add(new Located('E', start, p - start, -1));
            }
            else throw new InvalidOperationException($"tag {s[p]} at {p}");
        }
        var trailer = s.Length - 32 - 113;
        var folders = new List<Located>();
        var count = BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(trailer + 1 + 16));
        for (var k = 0; k < count; k++)
        {
            var name = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(p + 9));
            folders.Add(new Located('D', p, 90 + 2 * name, BinaryPrimitives.ReadInt32LittleEndian(s.AsSpan(p + 1))));
            p += 90 + 2 * name;
        }
        return new Layout(header, records, folders, p, (trailer - p) / 16, trailer);
    }
}
