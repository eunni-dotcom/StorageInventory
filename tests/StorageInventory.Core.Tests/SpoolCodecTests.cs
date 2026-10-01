using System.Buffers.Binary;
using System.Security.Cryptography;
using StorageInventory.Core.Scanning;
using StorageInventory.Core.Spool;
using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

/// <summary>
/// TEST-SP1: the spool codec round trip over a memory stream (format 1, SPOOL-20). The read-back sequence of file,
/// error and finalised folder records is exact (unpaired surrogates included); trailer counts and totals equal the
/// scan's; the digest equals SHA-256 of bytes [0, L - 32) computed independently; the pass V accepts every written
/// spool. Also pins the frozen layout details (G0F-O03) and the writer's lifecycle (only a finished scan seals).
/// </summary>
public static class SpoolCodecTests
{
    private static void AssertRoundTrip(ObservationScript script, string context)
    {
        var spool = SpoolTestKit.Write(script);

        // The digest: SHA-256 of [0, L - 32), computed here independently of the codec.
        Assert.True(SHA256.HashData(spool.AsSpan(0, spool.Length - 32)).AsSpan().SequenceEqual(spool.AsSpan(spool.Length - 32)), context + ": digest");

        var v = SpoolTestKit.Verify(spool, script.Totals);
        Assert.True(v.Passed, $"{context}: V rejected a written spool: {v.Defect} {v.Detail}");
        var s = Assert.NotNull(v.Spool);

        Assert.Equal(ObservationScript.RunId, s.Header.RunId, context);
        Assert.Equal(SpoolTestKit.FixedClock.UtcDateTime, s.Header.CreatedUtc, context);
        Assert.Equal(script.Totals, s.Trailer.Totals, context + ": trailer totals");
        Assert.Equal(script.Totals.Files, s.Trailer.FileRecords, context);
        Assert.Equal(script.Totals.ScanErrors + script.Totals.ReparsePointsSkipped + script.Totals.FileReparsePoints, s.Trailer.ErrorRecords, context);
        Assert.Equal(script.Totals.Folders, s.Trailer.FolderRecords, context);

        // Physical order: files and errors exactly as emitted, sequences included.
        var expected = new List<string>();
        long fileSeq = 0, errorSeq = 0;
        foreach (var r in script.Records)
        {
            expected.Add(r switch
            {
                ObservedFile f => Describe(new SpoolFileRecord(fileSeq++, f.FolderIndex, f.File.FileName, f.File.SizeBytes, f.File.CreatedUtc,
                    f.File.ModifiedUtc, f.File.LastAccessUtc, f.File.Attributes)),
                ObservedError e => Describe(new SpoolErrorRecord(errorSeq++, e.Error.Type, e.Error.Path, e.Error.Message)),
                _ => throw new InvalidOperationException(),
            });
        }
        Assert.SequenceEqual(expected, s.ReadRecordRegion().Select(Describe), context + ": record region");
        Assert.SequenceEqual(script.Records.OfType<ObservedError>().Select(e => e.Error.Path + "|" + e.Error.Message),
            s.ReadErrors().Select(e => e.Path + "|" + e.Message), context + ": errors");

        // Folders: every final value, ascending.
        Assert.SequenceEqual(script.Folders.Select(f => Describe(f.Index, f.ParentIndex, f.Folder)), s.ReadFolders().Select(Describe), context + ": folders");

        // Key-ordered access: each run read by seek gives exactly that folder's files, with their v1 sequences.
        var physical = s.ReadRecordRegion().OfType<SpoolFileRecord>().ToList();
        Assert.Equal(physical.Count, s.Runs.Sum(r => r.Count), context + ": run counts");
        foreach (var run in s.Runs.OrderBy(r => r.FolderIndex))
        {
            var slice = physical.Skip((int)run.FirstSequence).Take(run.Count).Select(Describe).ToList();
            Assert.SequenceEqual(slice, s.ReadRun(run).Select(Describe), $"{context}: run of folder {run.FolderIndex}");
        }
    }

    // Names are compared as exact UTF-16 code units (no culture, no normalisation).
    private static string Units(string s) => string.Join(" ", s.Select(c => ((int)c).ToString("X4")));

    private static string Describe(SpoolRecord r) => r switch
    {
        SpoolFileRecord f => $"F|{f.Sequence}|{f.FolderIndex}|{Units(f.Name)}|{f.SizeBytes}|{f.CreatedUtc?.Ticks}|{f.ModifiedUtc?.Ticks}|{f.LastAccessUtc?.Ticks}|{(int)f.Attributes}",
        SpoolErrorRecord e => $"E|{e.Sequence}|{e.Type}|{Units(e.Path)}|{e.Message.Length}:{e.Message.GetHashCode()}",
        _ => throw new InvalidOperationException(),
    };

    private static string Describe(SpoolFolderRecord f) =>
        $"{f.Index}|{f.ParentIndex}|{Units(f.Name)}|{f.Status}|{f.StatusReason}|{f.SubtreeComplete}|{(int)f.Attributes}|{f.CreatedUtc?.Ticks}|{f.ModifiedUtc?.Ticks}|" +
        $"{f.DirectSizeBytes}|{f.TotalSizeBytes}|{f.DirectFileCount}|{f.TotalFileCount}|{f.DirectSubfolderCount}|{f.TotalSubfolderCount}|{f.LargestFileBytes}";

    private static string Describe(int index, int parent, FolderInventoryRecord f) =>
        $"{index}|{parent}|{Units(f.Name)}|{f.Status}|{f.StatusReason}|{f.SubtreeComplete}|{(int)f.Attributes}|{f.CreatedUtc?.Ticks}|{f.ModifiedUtc?.Ticks}|" +
        $"{f.DirectSizeBytes}|{f.TotalSizeBytes}|{f.DirectFileCount}|{f.TotalFileCount}|{f.DirectSubfolderCount}|{f.TotalSubfolderCount}|{f.LargestFileBytes}";

    [Test]
    public static void Round_trip_is_exact_on_generated_fixtures()
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var script = ObservationScript.Generate(seed, folders: 1 + seed % 60, maxFilesPerFolder: seed % 15, errors: seed % 20);
            AssertRoundTrip(script, $"seed {seed}");
        }
    }

    [Test]
    public static void Round_trip_preserves_every_edge_name_exactly()
    {
        // Every edge name as a file name and as a folder name, in one spool.
        var script = ObservationScript.Generate(7, folders: 3, maxFilesPerFolder: 0, errors: 0);
        var records = ObservationScript.EdgeNames.Select((n, i) => (Observed)new ObservedFile(
            new FileInventoryRecord(n, "", n, ".", "C:\\x\\" + n, i, null, null, null, FileAttributes.Normal), 0)).ToList();
        var root = script.Folders[0].Folder with
        {
            Name = "\uD800root\uDFFF", DirectFileCount = records.Count, DirectSizeBytes = records.Count * (records.Count - 1) / 2,
            TotalFileCount = records.Count, TotalSizeBytes = records.Count * (records.Count - 1) / 2, DirectSubfolderCount = 0, TotalSubfolderCount = 0,
            Status = FolderScanStatus.Ok, StatusReason = null, SubtreeComplete = true,
        };
        var edge = new ObservationScript
        {
            Records = records,
            Folders = [new ObservedFolder(0, -1, root)],
            Totals = new ScanTotals { Files = records.Count, Folders = 1, Bytes = root.TotalSizeBytes },
        };
        AssertRoundTrip(edge, "edge names");
        var names = Assert.NotNull(SpoolTestKit.Verify(SpoolTestKit.Write(edge)).Spool).ReadRecordRegion().OfType<SpoolFileRecord>().Select(f => f.Name).ToList();
        for (var i = 0; i < names.Count; i++)
        {
            Assert.True(string.Equals(ObservationScript.EdgeNames[i], names[i], StringComparison.Ordinal), $"name {i} changed: {Units(ObservationScript.EdgeNames[i])} -> {Units(names[i])}");
        }
    }

    [Test]
    public static void An_empty_scan_round_trips()
    {
        AssertRoundTrip(ObservationScript.Generate(1, folders: 1, maxFilesPerFolder: 0, errors: 0), "root only");
    }

    [Test]
    public static void Frozen_layout_of_format_1()
    {
        // G0F-O03: header bytes, record sizes, trailer fields and their sizes, null encodings and the count mapping.
        var script = ObservationScript.Generate(42, folders: 6, maxFilesPerFolder: 4, errors: 5);
        var s = SpoolTestKit.Write(script);
        Assert.True(s.AsSpan(0, 8).SequenceEqual("SISPOOL1"u8), "magic");
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(8)), "format");
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(10)), "flags");
        Assert.True(s.AsSpan(12, 16).SequenceEqual(SpoolTestKit.Token), "capture token");
        Assert.Equal((ushort)ObservationScript.RunId.Length, BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(28)), "run ID length");
        var p = 30 + 2 * ObservationScript.RunId.Length;
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(p)), "scanner contract");
        Assert.Equal(SpoolTestKit.FixedClock.UtcDateTime.Ticks, BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(p + 2)), "created ticks");

        var layout = SpoolTestKit.Walk(s);
        var files = script.Records.OfType<ObservedFile>().ToList();
        var fileRecords = layout.Records.Where(r => r.Kind == 'F').ToList();
        for (var i = 0; i < files.Count; i++) Assert.Equal(43 + 2 * files[i].File.FileName.Length, fileRecords[i].Length, "file record size");
        var errors = script.Records.OfType<ObservedError>().ToList();
        var errorRecords = layout.Records.Where(r => r.Kind == 'E').ToList();
        for (var i = 0; i < errors.Count; i++) Assert.Equal(10 + 2 * (errors[i].Error.Path.Length + errors[i].Error.Message.Length), errorRecords[i].Length, "error record size");
        for (var i = 0; i < layout.Folders.Count; i++) Assert.Equal(90 + 2 * script.Folders[i].Folder.Name.Length, layout.Folders[i].Length, "folder record size");

        Assert.Equal(s.Length - 145, layout.TrailerOffset, "trailer at L - 145");
        Assert.Equal((byte)0x7F, s[layout.TrailerOffset], "trailer tag");
        long T(int i) => BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(layout.TrailerOffset + 1 + 8 * i));
        Assert.Equal(script.Totals.Files, T(0), "file records = Files");
        Assert.Equal(script.Totals.ScanErrors + script.Totals.ReparsePointsSkipped + script.Totals.FileReparsePoints, T(1), "error records = ScanErrors + ReparsePointsSkipped + FileReparsePoints");
        Assert.Equal(script.Totals.Folders, T(2), "folder records = Folders");
        Assert.Equal((long)layout.RunCount, T(3), "runs");
        Assert.SequenceEqual(new[] { script.Totals.Files, script.Totals.Folders, script.Totals.Bytes, script.Totals.ScanErrors, script.Totals.ReparsePointsSkipped,
            script.Totals.FileReparsePoints, script.Totals.LocallyIncompleteFolders, script.Totals.AffectedAncestorFolders }, Enumerable.Range(4, 8).Select(T), "totals");
        Assert.Equal((long)layout.Folders[0].Offset, T(12), "folder-section offset");
        Assert.Equal((long)layout.RunIndexOffset, T(13), "run-index offset");

        // Run index entries: folder index, absolute offset of the run's first file record, count.
        var expectedRuns = new List<(int, long, int)>();
        foreach (var r in fileRecords)
        {
            if (expectedRuns.Count > 0 && expectedRuns[^1].Item1 == r.FolderIndex) expectedRuns[^1] = (r.FolderIndex, expectedRuns[^1].Item2, expectedRuns[^1].Item3 + 1);
            else expectedRuns.Add((r.FolderIndex, r.Offset, 1));
        }
        var stored = Enumerable.Range(0, layout.RunCount).Select(i => (
            BinaryPrimitives.ReadInt32LittleEndian(s.AsSpan(layout.RunIndexOffset + 16 * i)),
            BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(layout.RunIndexOffset + 16 * i + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(s.AsSpan(layout.RunIndexOffset + 16 * i + 12))));
        Assert.SequenceEqual(expectedRuns, stored, "run index");
    }

    [Test]
    public static void Null_encodings_and_stable_codes_are_frozen()
    {
        Assert.Equal(long.MinValue, SpoolFormat.Ticks(null));
        Assert.Equal((byte)0xFF, SpoolFormat.NoStatusReason);
        var codes = Enum.GetValues<ScanErrorType>().Select(t => SpoolFormat.ErrorCode(t)).ToList();
        Assert.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, codes, "ScanErrorType codes (spec section 9.4)");
        Assert.SequenceEqual(new byte[] { 0, 1, 2, 3 }, Enum.GetValues<FolderScanStatus>().Select(SpoolFormat.StatusCode), "FolderScanStatus codes");
        Assert.Equal(185, SpoolFormat.MinimumLength);
        Assert.Equal(113, SpoolFormat.TrailerBytes);
    }

    [Test]
    public static void Only_a_finished_scan_seals_the_spool()
    {
        var script = ObservationScript.Generate(3);
        foreach (var end in new[] { ScanEndInfo.Cancelled(), ScanEndInfo.Failed() })
        {
            using var stream = new MemoryStream();
            using var writer = new SpoolWriter(stream, SpoolTestKit.Token);
            writer.OnScanStarted(new ScanStartInfo(ObservationScript.RunId, "C:\\x", null, new HashSet<string>()));
            foreach (var f in script.Records.OfType<ObservedFile>()) writer.OnFile(f.File, f.FolderIndex);
            foreach (var f in script.Folders) writer.OnFolderFinalised(f.Index, f.ParentIndex, f.Folder);
            writer.OnScanEnded(end);
            Assert.Equal(SpoolWriterState.Abandoned, writer.State, end.Outcome.ToString());
            var bytes = stream.ToArray();
            Assert.False(SpoolTestKit.Verify(bytes).Passed, end.Outcome + ": an abandoned spool must never verify");
            Assert.False(bytes.Length >= 145 && bytes[^145] == 0x7F, "no trailer is written");
        }
    }

    [Test]
    public static void Cancelled_at_any_point_the_spool_never_verifies()
    {
        // The scan can stop after any record: during enumeration (CAN-01a/b) or during finalisation. Whatever was written
        // up to that point, with the end the pipeline then sends, never passes V.
        var script = ObservationScript.Generate(11, folders: 10, maxFilesPerFolder: 5, errors: 6);
        var calls = script.Records.Count + script.Folders.Count;
        for (var stopAfter = 0; stopAfter <= calls; stopAfter++)
        {
            using var stream = new MemoryStream();
            using var writer = new SpoolWriter(stream, SpoolTestKit.Token);
            writer.OnScanStarted(new ScanStartInfo(ObservationScript.RunId, "C:\\x", null, new HashSet<string>()));
            var done = 0;
            foreach (var r in script.Records)
            {
                if (done++ == stopAfter) break;
                if (r is ObservedFile f) writer.OnFile(f.File, f.FolderIndex); else writer.OnError(((ObservedError)r).Error);
            }
            foreach (var f in script.Folders)
            {
                if (done++ >= stopAfter) break;
                writer.OnFolderFinalised(f.Index, f.ParentIndex, f.Folder);
            }
            writer.OnScanEnded(ScanEndInfo.Cancelled());
            Assert.Equal(SpoolWriterState.Abandoned, writer.State);
            Assert.False(SpoolTestKit.Verify(stream.ToArray()).Passed, $"stopped after {stopAfter} calls");
        }
    }

    [Test]
    public static void A_faulted_writer_never_seals_even_when_told_the_scan_finished()
    {
        var script = ObservationScript.Generate(5);
        using var stream = new FailingStream(failAfterBytes: 200);
        using var writer = new SpoolWriter(stream, SpoolTestKit.Token);
        Exception? fault = null;
        try { script.Replay(writer); } catch (IOException ex) { fault = ex; }
        Assert.NotNull(fault, "the write failure surfaced");
        Assert.Equal(SpoolWriterState.Faulted, writer.State);
        writer.OnScanEnded(ScanEndInfo.Finished(script.Totals));   // the fan-out still ends a faulted observer
        Assert.Equal(SpoolWriterState.Faulted, writer.State, "a faulted writer stays faulted");
    }

    [Test]
    public static void The_writer_refuses_observation_streams_that_break_the_contract()
    {
        var script = ObservationScript.Generate(9, folders: 8, maxFilesPerFolder: 4, errors: 0);
        var files = script.Records.OfType<ObservedFile>().ToList();
        var twoRuns = files.Select(f => f.FolderIndex).Distinct().Take(2).ToList();
        if (twoRuns.Count < 2) throw new InvalidOperationException("fixture needs two runs");

        static SpoolWriter Started(MemoryStream m)
        {
            var w = new SpoolWriter(m, SpoolTestKit.Token);
            w.OnScanStarted(new ScanStartInfo(ObservationScript.RunId, "C:\\x", null, new HashSet<string>()));
            return w;
        }

        // A folder's files not contiguous (SINK-03 #2).
        using (var m = new MemoryStream())
        {
            var w = Started(m);
            var a = files.First(f => f.FolderIndex == twoRuns[0]);
            var b = files.First(f => f.FolderIndex == twoRuns[1]);
            w.OnFile(a.File, a.FolderIndex);
            w.OnFile(b.File, b.FolderIndex);
            Assert.Throws<SpoolFormatException>(() => w.OnFile(a.File, a.FolderIndex));
            Assert.Equal(SpoolWriterState.Faulted, w.State);
        }
        // Folders out of order, or not starting with the root (SINK-03 #4).
        using (var m = new MemoryStream())
        {
            var w = Started(m);
            Assert.Throws<SpoolFormatException>(() => w.OnFolderFinalised(1, 0, script.Folders[1].Folder));
            Assert.Equal(SpoolWriterState.Faulted, w.State);
        }
        using (var m = new MemoryStream())
        {
            var w = Started(m);
            w.OnFolderFinalised(0, -1, script.Folders[0].Folder);
            Assert.Throws<SpoolFormatException>(() => w.OnFolderFinalised(2, 0, script.Folders[2].Folder));
        }
        // A file after the folder section began: folders are emitted only after enumeration.
        using (var m = new MemoryStream())
        {
            var w = Started(m);
            w.OnFolderFinalised(0, -1, script.Folders[0].Folder);
            Assert.Throws<SpoolFormatException>(() => w.OnFile(files[0].File, files[0].FolderIndex));
        }
        // Totals that disagree with what was recorded: never sealed.
        using (var m = new MemoryStream())
        {
            var w = new SpoolWriter(m, SpoolTestKit.Token);
            Assert.Throws<SpoolFormatException>(() => script.Replay(new TotalsOverride(w, script.Totals with { Files = script.Totals.Files + 1 })));
            Assert.Equal(SpoolWriterState.Faulted, w.State);
            Assert.False(SpoolTestKit.Verify(m.ToArray()).Passed);
        }
        // A non-empty destination: a spool is append-only from its first byte.
        using (var m = new MemoryStream([1, 2, 3]))
        {
            Assert.Throws<ArgumentException>(() => new SpoolWriter(m, SpoolTestKit.Token));
        }
    }

    /// <summary>Passes everything through, but reports other totals at the end.</summary>
    private sealed class TotalsOverride(IScanObserver inner, ScanTotals totals) : IScanObserver
    {
        public void OnScanStarted(in ScanStartInfo start) => inner.OnScanStarted(start);
        public void OnFile(in FileInventoryRecord file, int folderIndex) => inner.OnFile(file, folderIndex);
        public void OnError(ScanErrorRecord error) => inner.OnError(error);
        public void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder) => inner.OnFolderFinalised(index, parentIndex, folder);
        public void OnScanEnded(in ScanEndInfo end) => inner.OnScanEnded(ScanEndInfo.Finished(totals));
    }
}

/// <summary>A stream that fails with an I/O error once a number of bytes has been written (a full disk).</summary>
internal sealed class FailingStream(long failAfterBytes) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Length + count > failAfterBytes) throw new IOException("simulated: there is not enough space on the disk");
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Length + buffer.Length > failAfterBytes) throw new IOException("simulated: there is not enough space on the disk");
        base.Write(buffer);
    }
}
