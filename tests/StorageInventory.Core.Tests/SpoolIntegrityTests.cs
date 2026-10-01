using System.Buffers.Binary;
using System.Security.Cryptography;
using StorageInventory.Core.Scanning;
using StorageInventory.Core.Spool;
using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

/// <summary>
/// TEST-SP7: codec integrity (D-50, SPOOL-23) over a memory stream, as corrected after G0F-M07.
/// <list type="bullet">
/// <item><description><b>Byte integrity, digest not recomputed:</b> one byte changed at each position class, truncation
/// at several offsets, bytes appended after the digest, an incorrect digest: V rejects every one.</description></item>
/// <item><description><b>Structure, digest recomputed:</b> each rewrite is re-sealed with a correct SHA-256, so only the
/// structural invariants of SPOOL-23 (3) to (6) can catch it, and they do.</description></item>
/// <item><description><b>Documented boundary, asserted:</b> rewrites that keep every structural invariant are accepted,
/// because format 1 carries no per-record sequence. Their protection is the writer and the exclusive handle (TEST-SP1,
/// and TEST-H2 from C6).</description></item>
/// </list>
/// No record is returned to any caller for a rejected spool: <see cref="SpoolVerification.Spool"/> is null.
/// </summary>
public static class SpoolIntegrityTests
{
    // Physical runs, in emission order: folder 2 (three files, an error inside), folder 0, folder 1, folder 4, folder 3.
    // In key order (by folder) the run of folder 2 is read third although it is physically first: "read out of key order".
    private static ObservationScript Fixture()
    {
        var table = new List<FolderState>
        {
            new() { ParentIndex = -1, Depth = 0, Name = "root", RelativePath = ".", DirectSubfolderCount = 3 },
            new() { ParentIndex = 0, Depth = 1, Name = "alpha", RelativePath = "alpha", DirectSubfolderCount = 1 },
            new() { ParentIndex = 0, Depth = 1, Name = "beta", RelativePath = "beta" },
            new() { ParentIndex = 0, Depth = 1, Name = "gamma", RelativePath = "gamma" },
            new() { ParentIndex = 1, Depth = 2, Name = "delta", RelativePath = @"alpha\delta", Status = FolderScanStatus.Partial, StatusReason = ScanErrorType.AccessDenied, SubtreeComplete = false },
        };
        var t0 = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var records = new List<Observed>();
        long files = 0, bytes = 0;
        void File(int folder, string name, long size)
        {
            records.Add(new ObservedFile(new FileInventoryRecord(name, ScanEngine.ExtensionOf(name), name, table[folder].RelativePath, @"C:\x\" + name,
                size, t0, t0.AddDays(size), null, FileAttributes.Archive), folder));
            table[folder].DirectFileCount++;
            table[folder].DirectSizeBytes += size;
            table[folder].LargestFileBytes = Math.Max(table[folder].LargestFileBytes, size);
            files++;
            bytes += size;
        }
        void Error(ScanErrorType type, string path) => records.Add(new ObservedError(new ScanErrorRecord(path, type, "message for " + path)));

        Error(ScanErrorType.InvalidTimestamp, @"C:\x\first");
        File(2, "b-one.txt", 10); Error(ScanErrorType.AccessDenied, @"C:\x\inside-run"); File(2, "b-two.txt", 20); File(2, "b-three.txt", 30);
        File(0, "r-aaa.txt", 1); File(0, "r-bbb.txt", 2);
        Error(ScanErrorType.IOError, @"C:\x\between");
        File(1, "a-one.txt", 100); File(1, "a-two.txt", 200);
        File(4, "d-one.txt", 5);
        File(3, "g-xyz.txt", 7);
        Error(ScanErrorType.ReparsePointSkipped, @"C:\x\link");

        var completeness = FolderAggregator.Aggregate(table, files, bytes);
        return new ObservationScript
        {
            Records = records,
            Folders = Enumerable.Range(0, table.Count).Select(i => new ObservedFolder(i, table[i].ParentIndex, FolderAggregator.ToRecord(table, i, @"C:\x"))).ToList(),
            Totals = new ScanTotals
            {
                Files = files, Folders = table.Count, Bytes = bytes, ScanErrors = 3, ReparsePointsSkipped = 1,
                LocallyIncompleteFolders = completeness.LocallyIncompleteFolders, AffectedAncestorFolders = completeness.AffectedAncestorFolders,
            },
        };
    }

    private static readonly Lazy<(ObservationScript Script, byte[] Spool, SpoolTestKit.Layout Layout)> Original = new(() =>
    {
        var script = Fixture();
        var spool = SpoolTestKit.Write(script);
        return (script, spool, SpoolTestKit.Walk(spool));
    });

    private static byte[] Spool => (byte[])Original.Value.Spool.Clone();
    private static SpoolTestKit.Layout Layout => Original.Value.Layout;
    private static ScanTotals Totals => Original.Value.Script.Totals;

    private static SpoolTestKit.Located FileRecord(string name) =>
        Layout.Records.Where(r => r.Kind == 'F').Single(r => NameAt(Original.Value.Spool, r) == name);

    private static string NameAt(byte[] s, SpoolTestKit.Located r) =>
        SpoolFormat.ReadUtf16(s.AsSpan(r.Offset + 7, 2 * BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(r.Offset + 5))));

    private static void Rejected(byte[] spool, SpoolDefect expected, string context, ScanTotals? totals = null, byte[]? token = null)
    {
        var v = SpoolReader.Verify(new MemoryStream(spool, writable: false), token ?? SpoolTestKit.Token, totals ?? Totals);
        Assert.False(v.Passed, context + ": accepted");
        Assert.Null(v.Spool, context + ": a rejected spool must expose no record");
        Assert.Equal(expected, v.Defect, $"{context} ({v.Detail})");
    }

    private static void RejectedByIntegrity(byte[] spool, string context)
    {
        var v = SpoolTestKit.Verify(spool, Totals);
        Assert.False(v.Passed, context + ": accepted");
        Assert.Null(v.Spool, context);
        Assert.True(v.Defect is SpoolDefect.DigestMismatch or SpoolDefect.TooShort, $"{context}: {v.Defect} ({v.Detail})");
    }

    private static VerifiedSpool Accepted(byte[] spool, string context)
    {
        var v = SpoolTestKit.Verify(spool, Totals);
        Assert.True(v.Passed, $"{context}: rejected as {v.Defect} ({v.Detail})");
        return Assert.NotNull(v.Spool);
    }

    private static byte[] Resealed(byte[] spool, Action<byte[]> rewrite)
    {
        var body = SpoolTestKit.Body(spool);
        rewrite(body);
        return SpoolTestKit.Reseal(body);
    }

    private static void SetI64(byte[] s, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(offset), value);
    private static long GetI64(byte[] s, int offset) => BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(offset));
    private static int TrailerField(int i) => Layout.TrailerOffset + 1 + 8 * i;

    [Test]
    public static void The_unmodified_fixture_passes()
    {
        var s = Accepted(Spool, "original");
        Assert.SequenceEqual(new[] { 2, 0, 1, 4, 3 }, s.Runs.Select(r => r.FolderIndex), "physical run order");
    }

    // ---- Byte integrity: the digest is not recomputed ----

    [Test]
    public static void One_changed_byte_in_every_position_class_is_rejected()
    {
        var bTwo = FileRecord("b-two.txt");
        var positions = new (string Class, int Offset)[]
        {
            ("header (run ID)", 30),
            ("header (created time)", Layout.HeaderLength - 1),
            ("file record (size)", FileRecord("r-aaa.txt").Offset + 7 + 18),
            ("error record (message)", Layout.Records.First(r => r.Kind == 'E').Offset + 12),
            ("inside a run read out of key order", bTwo.Offset + bTwo.Length - 5),
            ("folder section", Layout.Folders[2].Offset + 30),
            ("run index", Layout.RunIndexOffset + 5),
            ("trailer field", TrailerField(6)),
            ("trailer tag", Layout.TrailerOffset),
            ("digest", Original.Value.Spool.Length - 1),
        };
        foreach (var (what, offset) in positions)
        {
            foreach (var mask in new byte[] { 0x01, 0x80, 0xFF })
            {
                var s = Spool;
                s[offset] ^= mask;
                Rejected(s, SpoolDefect.DigestMismatch, $"{what} @ {offset} ^ 0x{mask:X2}");
            }
        }
    }

    [Test]
    public static void Truncation_at_any_offset_is_rejected()
    {
        var length = Original.Value.Spool.Length;
        foreach (var keep in new[] { length - 1, length - 31, length - 32, length - 33, length - 145, length - 146, Layout.RunIndexOffset,
                                     Layout.Folders[0].Offset, length / 2, SpoolFormat.MinimumLength, SpoolFormat.MinimumLength - 1, 30, 1, 0 })
        {
            RejectedByIntegrity(Original.Value.Spool[..keep], $"truncated to {keep} of {length} bytes");
        }
    }

    [Test]
    public static void Bytes_appended_after_the_digest_are_rejected()
    {
        var digest = Original.Value.Spool[^32..];
        foreach (var extra in new[] { new byte[1], digest, new byte[1000], "SISPOOL1"u8.ToArray() })
        {
            Rejected([.. Original.Value.Spool, .. extra], SpoolDefect.DigestMismatch, $"{extra.Length} bytes appended");
        }
    }

    [Test]
    public static void An_incorrect_digest_is_rejected()
    {
        var body = SpoolTestKit.Body(Original.Value.Spool);
        Rejected([.. body, .. new byte[32]], SpoolDefect.DigestMismatch, "zero digest");
        Rejected([.. body, .. SHA256.HashData(body[..^1])], SpoolDefect.DigestMismatch, "digest of a shorter range");
        Rejected([.. body, .. SHA256.HashData([.. body, 0])], SpoolDefect.DigestMismatch, "digest of a longer range");
        Rejected([.. body, .. SHA512.HashData(body)[..32]], SpoolDefect.DigestMismatch, "another hash");
    }

    // ---- Structure: each rewrite seals a new, correct digest ----

    [Test]
    public static void A_folders_run_split_in_two_is_rejected()
    {
        // Move the last record of folder 2's run (b-three) to just after folder 0's run: folder 2 now starts two runs.
        var bThree = FileRecord("b-three.txt");
        var rBbb = FileRecord("r-bbb.txt");
        var s = Resealed(Spool, b =>
        {
            var moved = b[bThree.Offset..(bThree.Offset + bThree.Length)];
            var between = b[(bThree.Offset + bThree.Length)..(rBbb.Offset + rBbb.Length)];
            between.CopyTo(b, bThree.Offset);
            moved.CopyTo(b, bThree.Offset + between.Length);
        });
        Rejected(s, SpoolDefect.RunSplit, "run split");
    }

    [Test]
    public static void Whole_records_of_different_folders_exchanged_are_rejected()
    {
        // Two single-record runs exchanged, folder index fields included: every folder keeps its own files, but the
        // rebuilt run list (folder 3 before folder 4) no longer equals the stored run index.
        var d = FileRecord("d-one.txt");
        var g = FileRecord("g-xyz.txt");
        Assert.Equal(d.Length, g.Length, "fixture: equal record sizes");
        var s = Resealed(Spool, b =>
        {
            var first = b[d.Offset..(d.Offset + d.Length)];
            b.AsSpan(g.Offset, g.Length).CopyTo(b.AsSpan(d.Offset));
            first.CopyTo(b, g.Offset);
        });
        Rejected(s, SpoolDefect.RunIndexMismatch, "single-record runs exchanged");

        // A record exchanged into another folder's multi-record run: that folder's run is split.
        var bTwo = FileRecord("b-two.txt");
        var aOne = FileRecord("a-one.txt");
        Assert.Equal(bTwo.Length, aOne.Length, "fixture: equal record sizes");
        var s2 = Resealed(Spool, b =>
        {
            var first = b[bTwo.Offset..(bTwo.Offset + bTwo.Length)];
            b.AsSpan(aOne.Offset, aOne.Length).CopyTo(b.AsSpan(bTwo.Offset));
            first.CopyTo(b, aOne.Offset);
        });
        Rejected(s2, SpoolDefect.RunSplit, "records of folders 1 and 2 exchanged");
    }

    [Test]
    public static void Reordered_run_index_entries_are_rejected()
    {
        var s = Resealed(Spool, b =>
        {
            var first = b[Layout.RunIndexOffset..(Layout.RunIndexOffset + 16)];
            b.AsSpan(Layout.RunIndexOffset + 16, 16).CopyTo(b.AsSpan(Layout.RunIndexOffset));
            first.CopyTo(b, Layout.RunIndexOffset + 16);
        });
        Rejected(s, SpoolDefect.RunIndexMismatch, "run index entries 0 and 1 exchanged");
    }

    [Test]
    public static void Record_tags_and_lengths_out_of_bounds_are_rejected()
    {
        var between = Layout.Records.Where(r => r.Kind == 'E').ElementAt(2);
        Rejected(Resealed(Spool, b => b[between.Offset] = 0x09), SpoolDefect.BadRecordTag, "unknown tag 0x09");
        Rejected(Resealed(Spool, b => b[FileRecord("a-two.txt").Offset] = 0x7F), SpoolDefect.BadRecordTag, "trailer tag inside the record region");
        var firstError = Layout.Records.First(r => r.Kind == 'E');
        Rejected(Resealed(Spool, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(firstError.Offset + 2), 0x7FFFFFFF)),
            SpoolDefect.RecordOutOfBounds, "error path length beyond the file");
        Rejected(Resealed(Spool, b => b[firstError.Offset + 1] = 42), SpoolDefect.RecordOutOfBounds, "error type code 42");
        Rejected(Resealed(Spool, b => SetI64(b, FileRecord("g-xyz.txt").Offset + 7 + 18, -7)), SpoolDefect.RecordOutOfBounds, "negative size");
        Rejected(Resealed(Spool, b => SetI64(b, FileRecord("g-xyz.txt").Offset + 7 + 18 + 8, long.MaxValue)), SpoolDefect.RecordOutOfBounds, "ticks beyond DateTime");
        Rejected(Resealed(Spool, b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(FileRecord("g-xyz.txt").Offset + 1), -1)), SpoolDefect.RecordOutOfBounds, "negative folder index");
        var v = SpoolTestKit.Verify(Resealed(Spool, b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(FileRecord("b-one.txt").Offset + 5), 0xFFFF)), Totals);
        Assert.False(v.Passed || v.Defect == SpoolDefect.DigestMismatch, $"file name length beyond its record: {v.Defect}");
    }

    [Test]
    public static void Per_tag_counts_that_disagree_with_the_trailer_are_rejected()
    {
        Rejected(Resealed(Spool, b => SetI64(b, TrailerField(0), GetI64(b, TrailerField(0)) + 1)), SpoolDefect.RecordCountMismatch, "file records");
        Rejected(Resealed(Spool, b => SetI64(b, TrailerField(1), GetI64(b, TrailerField(1)) - 1)), SpoolDefect.RecordCountMismatch, "error records");
        Rejected(Resealed(Spool, b => SetI64(b, TrailerField(2), GetI64(b, TrailerField(2)) + 1)), SpoolDefect.RecordCountMismatch, "folder records");
    }

    [Test]
    public static void Folder_section_order_and_parent_violations_are_rejected()
    {
        var f3 = Layout.Folders[3].Offset;
        var f4 = Layout.Folders[4].Offset;
        var f2 = Layout.Folders[2].Offset;
        Rejected(Resealed(Spool, b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(f3 + 1), 4)), SpoolDefect.FolderSectionInvalid, "index out of order");
        Rejected(Resealed(Spool, b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(f4 + 5), 4)), SpoolDefect.FolderSectionInvalid, "parent equal to the child");
        Rejected(Resealed(Spool, b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(f2 + 5), 3)), SpoolDefect.FolderSectionInvalid, "parent above the child");
        Rejected(Resealed(Spool, b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(Layout.Folders[0].Offset + 5), 0)), SpoolDefect.FolderSectionInvalid, "root with a parent");
        Rejected(Resealed(Spool, b => b[f3] = 0x01), SpoolDefect.FolderSectionInvalid, "a record that is not a folder inside the folder section");
    }

    [Test]
    public static void Folder_closure_violations_are_rejected()
    {
        // Values after the name: status u8, reason u8, subtree u8, attributes i32, two ticks, then the seven i64 counts.
        int Tail(int folder) => Layout.Folders[folder].Offset + 11 + 2 * Original.Value.Script.Folders[folder].Folder.Name.Length;
        Rejected(Resealed(Spool, b => b[Tail(4) + 2] = 1), SpoolDefect.FolderClosureViolation, "a Partial folder marked complete");
        Rejected(Resealed(Spool, b => b[Tail(2)] = 1), SpoolDefect.FolderClosureViolation, "an Unreadable folder with files");
        Rejected(Resealed(Spool, b => SetI64(b, Tail(1) + 23 + 16, 3)), SpoolDefect.FolderClosureViolation, "direct files differ from the run");
        Rejected(Resealed(Spool, b => { SetI64(b, Tail(1) + 23, 299); SetI64(b, Tail(1) + 23 + 8, 304); }), SpoolDefect.FolderClosureViolation, "direct bytes differ from the run");
        Rejected(Resealed(Spool, b => { SetI64(b, Tail(3) + 23 + 32, 1); SetI64(b, Tail(3) + 23 + 40, 1); }), SpoolDefect.FolderClosureViolation, "a subfolder count with no subfolder");
        Rejected(Resealed(Spool, b => b[Tail(0) + 2] = 1), SpoolDefect.FolderClosureViolation, "incompleteness not propagated to the root");
    }

    [Test]
    public static void Recomputed_totals_that_disagree_with_the_trailer_are_rejected()
    {
        for (var field = 4; field <= 11; field++)
        {
            var f = field;
            Rejected(Resealed(Spool, b => SetI64(b, TrailerField(f), GetI64(b, TrailerField(f)) + 1)), SpoolDefect.TotalsMismatch, $"trailer total {f - 4}");
        }
        Rejected(Spool, SpoolDefect.ResultTotalsMismatch, "trailer against the scan result", Totals with { ScanErrors = Totals.ScanErrors + 1 });
    }

    [Test]
    public static void Trailer_offsets_that_do_not_match_the_positions_found_are_rejected()
    {
        Rejected(Resealed(Spool, b => SetI64(b, TrailerField(12), GetI64(b, TrailerField(12)) + 1)), SpoolDefect.TrailerInvalid, "folder-section offset");
        Rejected(Resealed(Spool, b => SetI64(b, TrailerField(13), GetI64(b, TrailerField(13)) - 16)), SpoolDefect.TrailerInvalid, "run-index offset");
        Rejected(Resealed(Spool, b => SetI64(b, TrailerField(3), GetI64(b, TrailerField(3)) + 1)), SpoolDefect.TrailerInvalid, "run count");
        Rejected(Resealed(Spool, b => b[Layout.TrailerOffset] = 0x7E), SpoolDefect.TrailerInvalid, "trailer tag");
    }

    [Test]
    public static void Header_violations_are_rejected()
    {
        Rejected(Resealed(Spool, b => b[0] = (byte)'X'), SpoolDefect.BadMagic, "magic");
        Rejected(Resealed(Spool, b => b[8] = 2), SpoolDefect.UnsupportedFormat, "format 2");
        Rejected(Resealed(Spool, b => b[10] = 1), SpoolDefect.UnsupportedFlags, "flags");
        Rejected(Spool, SpoolDefect.CaptureTokenMismatch, "another capture's token", token: new byte[16]);
        Rejected(Resealed(Spool, b => b[Layout.HeaderLength - 10] = 2), SpoolDefect.BadHeader, "scanner contract 2");
        Rejected(Resealed(Spool, b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), 0)), SpoolDefect.BadHeader, "empty run ID");
    }

    // ---- Documented boundary: accepted, because format 1 carries no per-record sequence ----

    [Test]
    public static void Two_records_of_the_same_folder_exchanged_within_its_run_are_accepted()
    {
        var a = FileRecord("r-aaa.txt");
        var bb = FileRecord("r-bbb.txt");
        var s = Resealed(Spool, b =>
        {
            var first = b[a.Offset..(a.Offset + a.Length)];
            b.AsSpan(bb.Offset, bb.Length).CopyTo(b.AsSpan(a.Offset));
            first.CopyTo(b, bb.Offset);
        });
        var spool = Accepted(s, "same-folder exchange");
        Assert.SequenceEqual(["r-bbb.txt", "r-aaa.txt"], spool.ReadRun(spool.Runs.Single(r => r.FolderIndex == 0)).Select(f => f.Name), "the exchange is undetectable by V");

        // Within a run, records of different sizes too: the run starts where it did and ends where it did.
        var two = FileRecord("b-two.txt");
        var three = FileRecord("b-three.txt");
        var s2 = Resealed(Spool, b =>
        {
            var x = b[two.Offset..(two.Offset + two.Length)];
            var y = b[three.Offset..(three.Offset + three.Length)];
            y.CopyTo(b, two.Offset);
            x.CopyTo(b, two.Offset + y.Length);
        });
        Accepted(s2, "same-folder exchange of different sizes");
    }

    // G0E-O09: "names exchanged between two records whose folder indexes stay in place" are accepted only when the run
    // index still matches: within one run; across runs only for names of equal encoded length, or with the run index
    // rewritten to match. Names of different lengths exchanged across runs with the run index left as it was move later
    // runs' offsets and are rejected by SPOOL-23 step (4).
    [Test]
    public static void Names_exchanged_with_folder_indexes_in_place_are_accepted_only_while_the_run_index_matches()
    {
        // Within one run, different lengths.
        Accepted(SwapNames("b-one.txt", "b-three.txt"), "within one run");
        // Across two runs, equal encoded lengths.
        var equal = Accepted(SwapNames("a-one.txt", "g-xyz.txt"), "across runs, equal lengths");
        Assert.Equal("g-xyz.txt", equal.ReadRun(equal.Runs.Single(r => r.FolderIndex == 1)).First().Name, "the exchange is undetectable by V");
        // Across two runs, different lengths, run index unchanged: rejected.
        Rejected(SwapNames("b-three.txt", "a-two.txt"), SpoolDefect.RunIndexMismatch, "across runs, different lengths");
        // The same, with the run index rewritten to match: accepted (only a rewrite of the spool could do this).
        Accepted(RewriteRunIndex(SwapNames("b-three.txt", "a-two.txt")), "across runs, run index rewritten");
    }

    private static byte[] SwapNames(string first, string second)
    {
        var a = FileRecord(first);
        var b = FileRecord(second);
        var s = SpoolTestKit.Body(Spool);
        var nameA = s[(a.Offset + 7)..(a.Offset + 7 + 2 * first.Length)];
        var nameB = s[(b.Offset + 7)..(b.Offset + 7 + 2 * second.Length)];
        var (lo, hi, loName, hiName) = a.Offset < b.Offset ? (a, b, nameB, nameA) : (b, a, nameA, nameB);
        var loOld = 2 * NameAt(Original.Value.Spool, lo).Length;
        var hiOld = 2 * NameAt(Original.Value.Spool, hi).Length;
        // Rebuild: [.. lo header][new lo name][lo tail .. hi header][new hi name][hi tail ..]
        byte[] Record(SpoolTestKit.Located r, int oldNameBytes, byte[] newName)
        {
            var head = s[r.Offset..(r.Offset + 7)];
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(5), (ushort)(newName.Length / 2));
            return [.. head, .. newName, .. s[(r.Offset + 7 + oldNameBytes)..(r.Offset + r.Length)]];
        }
        byte[] body = [.. s[..lo.Offset], .. Record(lo, loOld, loName), .. s[(lo.Offset + lo.Length)..hi.Offset], .. Record(hi, hiOld, hiName), .. s[(hi.Offset + hi.Length)..]];
        return SpoolTestKit.Reseal(body);
    }

    private static byte[] RewriteRunIndex(byte[] spool)
    {
        var layout = SpoolTestKit.Walk(spool);
        var body = SpoolTestKit.Body(spool);
        var runs = new List<(int Folder, int Offset, int Count)>();
        foreach (var r in layout.Records.Where(r => r.Kind == 'F'))
        {
            if (runs.Count > 0 && runs[^1].Folder == r.FolderIndex) runs[^1] = runs[^1] with { Count = runs[^1].Count + 1 };
            else runs.Add((r.FolderIndex, r.Offset, 1));
        }
        for (var i = 0; i < runs.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(layout.RunIndexOffset + 16 * i), runs[i].Folder);
            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(layout.RunIndexOffset + 16 * i + 4), runs[i].Offset);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(layout.RunIndexOffset + 16 * i + 12), runs[i].Count);
        }
        return SpoolTestKit.Reseal(body);
    }

    [Test]
    public static void Random_single_byte_rewrites_are_never_accepted_unless_structurally_identical()
    {
        // Fuzz: any one-byte change, re-sealed, is either rejected with a structural defect (never a crash), or keeps
        // every structural invariant (a byte inside a name, a time, attributes, or a folder's created/modified time).
        var rng = new Random(20261001);
        var original = Original.Value.Spool;
        for (var i = 0; i < 4000; i++)
        {
            var body = SpoolTestKit.Body(original);
            var offset = rng.Next(body.Length);
            body[offset] ^= (byte)rng.Next(1, 256);
            var v = SpoolTestKit.Verify(SpoolTestKit.Reseal(body), Totals);
            if (v.Passed) continue;
            Assert.False(v.Defect is SpoolDefect.DigestMismatch or SpoolDefect.None, $"offset {offset}: {v.Defect}");
            Assert.Null(v.Spool);
        }
    }
}
