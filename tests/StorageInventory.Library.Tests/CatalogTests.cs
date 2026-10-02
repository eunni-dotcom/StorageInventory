using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>The read boundary (SEC-15, SEC-16): the Library's contents are untrusted. An honest Library reads back exactly; a Library
/// edited to hold values this version cannot have written yields a data error, not a crash, and nothing read is used as a path.</summary>
public static class CatalogTests
{
    [Test]
    public static void An_honest_Library_reads_back_exactly()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var first = LibraryStateTests.ImportOne(session, new SyntheticSnapshot(120, seed: 1));
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(120, seed: 2, label: "b"), "run-2", new ImportSourceSpec.Existing(first.SourceId));
        var list = session.Read(r => LibraryCatalog.ListSnapshots(r));
        Assert.Equal(2, list.Count);
        Assert.SequenceEqual([1L, 2L], list.Select(s => s.SnapshotId), "oldest first (SCH-11)");
        Assert.True(list.All(s => s.State == SnapshotState.Published && s.Completion == ScanCompletionState.Complete && s.SourceId == first.SourceId));
        Assert.Equal(first.Files, list[0].Files);
        Assert.Equal(120L, list[0].Folders);
        Assert.Equal(1, session.Read(r => LibraryCatalog.ListSnapshots(r, limit: 1)).Count, "bounded by the limit");
        Assert.Equal(1, session.Read(r => LibraryCatalog.ListSnapshots(r, limit: -5)).Count, "a nonsense limit is clamped, not obeyed");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Hostile_values_are_data_errors_not_crashes()
    {
        // (column, hostile value): each passes SQLite's own constraints and the open-time checks, and each must be refused by the catalogue
        var cases = new (string Sql, string Expect)[]
        {
            ("UPDATE snapshot SET identity_confidence = 99", "IdentityConfidence"),
            ("UPDATE snapshot SET identity_basis = 0", "IdentityBasis"),
            ("UPDATE snapshot SET files = -5", "files"),
            ("UPDATE snapshot SET bytes = -1", "bytes"),
            ("UPDATE snapshot SET folders = 0", "folders"),
            ("UPDATE snapshot SET schema_version = 7", "schema_version"),
            ("UPDATE snapshot SET snapshot_id = 0 WHERE snapshot_id = 1", "snapshot_id"),
        };
        foreach (var (sql, expect) in cases)
        {
            var world = LibraryStateTests.LibraryWithSnapshot(30);
            RawSqlite.Execute(world.Main, sql);
            var session = world.NewSession();
            Assert.Equal(LibraryState.Available, session.RunStartupOpen().State, "the edit is invisible to the open-time checks: " + sql);
            var failure = Assert.Throws<LibraryDataException>(() => session.Read(r => LibraryCatalog.ListSnapshots(r)));
            Assert.Contains(expect, failure.Message);
            Assert.False(session.Interlock.IsFaulted, "a data error is class A: " + sql);
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void Names_and_paths_are_checked_as_exact_UTF_16_bytes_and_never_become_paths()
    {
        Assert.Equal(4, LibraryCatalog.Utf16Bytes(new byte[] { 0x41, 0, 0x00, 0xD8 }, "name").Length, "an unpaired surrogate is kept exactly");
        Assert.Throws<LibraryDataException>(() => LibraryCatalog.Utf16Bytes(new byte[3], "name"));
        Assert.Throws<LibraryDataException>(() => LibraryCatalog.Utf16Bytes(new byte[2 * 32_768], "path"));
        Assert.Throws<LibraryDataException>(() => LibraryCatalog.Utf16Bytes("text", "name"));
        Assert.Throws<LibraryDataException>(() => LibraryCatalog.Utf16Bytes(null, "name"));
        Assert.Throws<LibraryDataException>(() => LibraryCatalog.Integer("5", "n"));
        Assert.Throws<LibraryDataException>(() => LibraryCatalog.Integer(5.5, "n"));
        Assert.Throws<LibraryDataException>(() => LibraryCatalog.Integer(null, "n"));
        Assert.Throws<LibraryDataException>(() => LibraryCatalog.Integer(10L, "n", max: 5));
        Assert.Equal(5L, LibraryCatalog.Integer(5L, "n", min: 0, max: 5));
    }

    [Test]
    public static void A_name_with_an_unpaired_surrogate_round_trips_exactly_and_two_case_variants_stay_two_names()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var odd = new byte[] { 0x61, 0x00, 0x00, 0xD8, 0x62, 0x00 };          // "a", a lone high surrogate, "b"
        var snapshot = new OneFolderSnapshot([odd, Utf16.ToBytes("Readme.TXT"), Utf16.ToBytes("README.txt"), Utf16.ToBytes("readme.txt")]);
        LibraryStateTests.ImportRows(session, snapshot, snapshot.Header());
        var names = session.Read(r => r.Query("SELECT n.utf16 FROM file_obs f JOIN name n ON n.name_id = f.name_id WHERE f.snapshot_id = 1").Select(row => (byte[])row[0]!).ToList());
        Assert.Equal(4, names.Count, "four distinct names: case variants are different names (NAME-02), the lone surrogate is not replaced");
        Assert.True(names.Any(n => n.SequenceEqual(odd)), "the unpaired surrogate is stored exactly (NAME-01)");
        // the extension totals lower-case the extension: ".TXT" and ".txt" share one total; the name with a lone surrogate has no extension
        var totals = session.Read(r => r.Query("SELECT extension_key, files FROM snapshot_extension_total ORDER BY extension_key").Select(row => (Utf16.ToString((byte[])row[0]!), (long)row[1]!)).ToList());
        Assert.SequenceEqual([("", 1L), (".txt", 3L)], totals);
        session.TestOnlyShutdown();
    }

    /// <summary>A snapshot of one folder holding the given file names.</summary>
    private sealed class OneFolderSnapshot(byte[][] names) : ISnapshotRowSource
    {
        private readonly ImportFile[] _files = names.Select((n, i) => new ImportFile(n, i, 10 + i, null, null, null, 0x20)).ToArray();

        public int FolderCount => 1;

        public IEnumerable<ImportFolder> Folders()
        {
            yield return new ImportFolder(0, -1, [], FolderScanStatus.Ok, null, true, 0x10, null, null, _files.Sum(f => f.Size), _files.Sum(f => f.Size), _files.Length, _files.Length, 0, 0, _files.Max(f => f.Size));
        }

        public IReadOnlyList<ImportFile> FilesOfFolder(int folderIndex) => _files;

        public IEnumerable<ImportError> Errors() => [];

        internal ImportSnapshotHeader Header() => new SyntheticSnapshot(1).Header() with { Files = _files.Length, Folders = 1, Bytes = _files.Sum(f => f.Size) };
    }
}
