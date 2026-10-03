using System.Buffers.Binary;
using System.Security.Cryptography;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// The header pre-check (LIB-08 step 4, SEC-32, TEST-L5; C4-M08) and the uninitialised states (LIB-07 step 7, §6.8 Not created, TEST-L3;
/// C4-M01). "SQLite never opens a suspect file" is proved with a FORGED HOT JOURNAL: a rollback journal that SQLite would play back
/// into its main file the moment it opened it (it rewrites a page and deletes the journal). Beside every suspect main file the
/// journal must survive byte for byte and the main file must not change; had SQLite been given the file, both would have changed.
/// An unchanged journal is therefore evidence that the pre-check refused the file first, which "no journal was created" is not.
/// </summary>
public static class HeaderPreCheckTests
{
    private static readonly byte[] JournalMagic = [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7];

    /// <summary>A rollback journal of the format SQLite writes: header (magic, record count, nonce, original size in pages, sector
    /// size, page size) padded to a sector, then records (page number, page image, checksum). SQLite plays it back at the first
    /// read of a database it is opened beside.</summary>
    private static byte[] ForgedHotJournal(uint originalPages, params (uint PageNumber, byte[] Image)[] records)
    {
        const int sector = 512, pageSize = 4096;
        const uint nonce = 0x12345678;
        var header = new byte[sector];
        JournalMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)records.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), nonce);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), originalPages);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), sector);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), pageSize);
        var all = new List<byte>(header);
        foreach (var (pageNumber, image) in records)
        {
            if (image.Length != pageSize) throw new ArgumentException("a journal record holds one 4,096-byte page image");
            var number = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(number, pageNumber);
            all.AddRange(number);
            all.AddRange(image);
            var checksum = nonce;
            for (var i = pageSize - 200; i > 0; i -= 200) checksum += image[i];
            var tail = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(tail, checksum);
            all.AddRange(tail);
        }
        return [.. all];
    }

    private static byte[] ValidLibraryBytes()
    {
        var source = World.Create();
        source.CreatedSession().TestOnlyShutdown();
        return File.ReadAllBytes(source.Main);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static byte[] Patched(byte[] bytes, int offset, params byte[] values)
    {
        var copy = (byte[])bytes.Clone();
        values.CopyTo(copy, offset);
        return copy;
    }

    /// <summary>A suspect main file with a forged hot journal beside it; opens the Library; returns the state and proves the file and
    /// the journal were left exactly as they were and that no connection was opened.</summary>
    private static LibraryStatus OpenBesideAHotJournal(string name, byte[] main)
    {
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);
        File.WriteAllBytes(world.Main, main);
        // a journal that would overwrite page 1 of the main file with 0xAB bytes if SQLite ever played it back
        var page = new byte[4096];
        Array.Fill(page, (byte)0xAB);
        File.WriteAllBytes(world.Journal, ForgedHotJournal(originalPages: Math.Max(1u, (uint)(main.Length / 4096)), (1u, page)));
        var mainBefore = Sha(world.Main);
        var journalBefore = Sha(world.Journal);
        var writersBefore = WriterConnection.WritersOpenedTotal;
        var readersBefore = ReaderConnection.ReadersOpenedTotal;

        var session = world.NewSession();
        var status = session.RunStartupOpen();
        Assert.Equal(mainBefore, Sha(world.Main), name + ": the main file is byte-identical (SQLite never played the journal into it)");
        Assert.True(File.Exists(world.Journal), name + ": the journal was not deleted (SQLite deletes a journal it has played back)");
        Assert.Equal(journalBefore, Sha(world.Journal), name + ": the journal is byte-identical");
        Assert.Equal(writersBefore, WriterConnection.WritersOpenedTotal, name + ": no writer connection was opened");
        Assert.Equal(readersBefore, ReaderConnection.ReadersOpenedTotal, name + ": no reader connection was opened");
        Assert.False(File.Exists(world.Member(LibraryNames.WalFile)) || File.Exists(world.Member(LibraryNames.ShmFile)), name + ": no -wal or -shm");
        session.TestOnlyShutdown();
        return status;
    }

    [Test]
    public static void SQLite_never_opens_a_suspect_file_even_beside_a_hot_journal_that_it_would_play_back()
    {
        var valid = ValidLibraryBytes();
        var cases = new (string Name, byte[] Main, LibraryState State, string Reason)[]
        {
            ("foreign file", [.. Enumerable.Repeat((byte)'x', 8192)], LibraryState.NotALibrary, LibraryReason.ForeignFile),
            ("magic differs only after byte 8", Patched(valid, 12, (byte)'X'), LibraryState.NotALibrary, LibraryReason.ForeignFile),
            ("magic differs only in the last byte", Patched(valid, 15, 1), LibraryState.NotALibrary, LibraryReason.ForeignFile),
            ("WAL-format header (byte 18)", Patched(valid, 18, 2), LibraryState.NotALibrary, LibraryReason.WalHeader),
            ("WAL-format header (byte 19 alone)", Patched(valid, 19, 2), LibraryState.NotALibrary, LibraryReason.WalHeader),
            ("wrong application id", Patched(valid, 68, 0, 0, 0, 7), LibraryState.NotALibrary, LibraryReason.WrongApplicationId),
            ("application id 0 with a schema", Patched(valid, 68, 0, 0, 0, 0), LibraryState.NotALibrary, LibraryReason.WrongApplicationId),
            ("user_version 0", Patched(valid, 60, 0, 0, 0, 0), LibraryState.NotALibrary, LibraryReason.UnsupportedUserVersion),
            ("newer user_version 2", Patched(valid, 60, 0, 0, 0, 2), LibraryState.Incompatible, LibraryReason.NewerSchema),
            ("newer user_version 5", Patched(valid, 60, 0, 0, 0, 5), LibraryState.Incompatible, LibraryReason.NewerSchema),
            ("newer user_version 2^31-1", Patched(valid, 60, 0x7F, 0xFF, 0xFF, 0xFF), LibraryState.Incompatible, LibraryReason.NewerSchema),
            ("negative user_version", Patched(valid, 60, 0xFF, 0xFF, 0xFF, 0xFF), LibraryState.NotALibrary, LibraryReason.UnsupportedUserVersion),
        };
        foreach (var (name, main, state, reason) in cases)
        {
            var status = OpenBesideAHotJournal(name, main);
            Assert.Equal(state, status.State, $"{name}: {status.Reason}: {status.Message}");
            Assert.Equal(reason, status.Reason, name);
        }
    }

    [Test]
    public static void The_control_SQLite_really_plays_the_forged_journal_back_when_it_is_allowed_to_open_the_file()
    {
        // without this control the "unchanged" assertions above could pass for a journal SQLite would simply ignore: here the same
        // forged journal beside the same kind of file is given to SQLite, which rewrites page 1 and deletes the journal
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);
        var valid = ValidLibraryBytes();
        File.WriteAllBytes(world.Main, valid);
        var page = new byte[4096];
        Array.Fill(page, (byte)0xAB);
        File.WriteAllBytes(world.Journal, ForgedHotJournal(originalPages: (uint)(valid.Length / 4096), (1u, page)));
        var before = Sha(world.Main);
        try { RawSqlite.Execute(world.Main, "SELECT count(*) FROM sqlite_schema"); }
        catch (Exception) { /* page 1 is now 0xAB bytes: the file is no longer a database, which is the point */ }
        Assert.True(Sha(world.Main) != before, "SQLite played the journal back into the main file");
        Assert.False(File.Exists(world.Journal), "and deleted it: a read-write connection finishes a hot journal it is given");
        Assert.True(File.ReadAllBytes(world.Main).Take(16).All(b => b == 0xAB), "page 1 is the journalled image");
    }

    [Test]
    public static void Every_byte_of_the_magic_and_of_the_format_bytes_is_checked_alone_and_a_valid_header_is_valid()
    {
        var valid = ValidLibraryBytes().AsSpan(0, 108).ToArray();
        Assert.Equal(HeaderOutcome.Valid, HeaderCheck.Classify(valid, 8192).Outcome, "an untouched header");
        for (var i = 0; i < 16; i++)
        {
            var header = (byte[])valid.Clone();
            header[i] ^= 0x01;
            Assert.Equal(HeaderOutcome.ForeignFile, HeaderCheck.Classify(header, 8192).Outcome, $"magic byte {i} changed alone");
        }
        foreach (var offset in new[] { 18, 19 })
        {
            foreach (var value in new byte[] { 0, 2, 3, 255 })
            {
                var header = (byte[])valid.Clone();
                header[offset] = value;
                Assert.Equal(HeaderOutcome.WalFormat, HeaderCheck.Classify(header, 8192).Outcome, $"format byte {offset} = {value} alone");
            }
        }
        for (var i = 0; i < 4; i++)
        {
            var header = (byte[])valid.Clone();
            header[68 + i] ^= 0x01;
            Assert.Equal(HeaderOutcome.WrongApplicationId, HeaderCheck.Classify(header, 8192).Outcome, $"application id byte {i} changed alone");
        }
        foreach (var version in new[] { 2, 3, 5, 100, int.MaxValue })
        {
            var header = (byte[])valid.Clone();
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(60), version);
            Assert.Equal(HeaderOutcome.NewerSchema, HeaderCheck.Classify(header, 8192).Outcome, $"user_version {version} is newer than this schema 1");
        }
        foreach (var version in new[] { 0, -1, int.MinValue })
        {
            var header = (byte[])valid.Clone();
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(60), version);
            Assert.Equal(HeaderOutcome.UnsupportedUserVersion, HeaderCheck.Classify(header, 8192).Outcome, $"user_version {version}");
        }
        Assert.Equal(HeaderOutcome.TooShort, HeaderCheck.Classify(valid.AsSpan(0, 99), 99).Outcome, "99 bytes");
        Assert.Equal(HeaderOutcome.Valid, HeaderCheck.Classify(valid.AsSpan(0, 100), 8192).Outcome, "exactly the 100 header bytes classify a Library");
    }

    // ---------------------------------------------------------------- C4-M01: the second clause of LIB-07 step 7

    private static World EmptiedDatabase(long userVersion = 0, bool leaveATable = false)
    {
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);
        File.WriteAllBytes(world.Main, []);
        // SQLite itself makes it: a table is created and dropped, so the schema is empty again and pages remain in the free list
        RawSqlite.Execute(world.Main, ["CREATE TABLE scratch (a INTEGER, b TEXT)", "INSERT INTO scratch VALUES (1, 'x')", .. leaveATable ? Array.Empty<string>() : ["DROP TABLE scratch"], $"PRAGMA user_version = {userVersion}"]);
        return world;
    }

    [Test]
    public static void A_database_with_application_id_0_user_version_0_and_no_schema_objects_is_uninitialised_and_initialised_again_on_the_next_save()
    {
        var world = EmptiedDatabase();
        Assert.True(World.Length(world.Main) > 0, "a real, non-empty SQLite file");
        var before = Sha(world.Main);
        var writers = WriterConnection.WritersOpenedTotal;
        var session = world.NewSession();
        var status = session.RunStartupOpen();
        Assert.Equal(LibraryState.NotCreated, status.State, $"uninitialised, like a 0-byte file: {status.Reason}: {status.Message}");
        Assert.Equal(LibraryReason.Uninitialised, status.Reason);
        Assert.Equal(before, Sha(world.Main), "the open changed nothing");
        Assert.Equal(writers, WriterConnection.WritersOpenedTotal, "and opened no connection");
        Assert.False(File.Exists(world.Member(LibraryNames.WalFile)));

        // the next save (a Prepare lease: lock first, state re-derived under it) initialises it: T-CREATE, not a refusal
        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            var created = session.PrepareForSave(prepare);
            Assert.Equal(LibraryState.Available, created.State, created.Message);
        }
        Assert.Equal(1397313110L, session.Read(r => r.Long(OpenSql.GetApplicationId)), "application_id written");
        Assert.True(LibrarySchema.Matches(session.Read(r => r.Query(OpenSql.SelectSchemaRows).Select(row => new SchemaRow((string)row[0]!, (string)row[1]!, (string)row[2]!, row[3] as string)).ToList())), "the schema is schema 1");
        Assert.True(world.Names().All(n => n is LibraryNames.LockFile or LibraryNames.MainFile or LibraryNames.JournalFile), "nothing was set aside: " + string.Join(", ", world.Names()));
        session.TestOnlyShutdown();

        // and the explicit "Create a new, empty Library" action behaves the same
        var other = EmptiedDatabase();
        var explicitSession = other.OpenedSession();
        Assert.Equal(LibraryState.NotCreated, explicitSession.Status.State);
        using (var create = World.Lease(explicitSession, MutationKind.Create)) Assert.Equal(LibraryState.Available, explicitSession.CreateLibrary(create).State);
        explicitSession.TestOnlyShutdown();
    }

    [Test]
    public static void A_non_empty_journal_beside_an_uninitialised_database_is_Leftover_files_and_SQLite_never_plays_it_back()
    {
        // an empty database is uninitialised, but a journal beside it is recovery material for a file that is not ours: SQLite would
        // play it back into the database during a creation, so creation is refused and nothing is opened or changed
        var world = EmptiedDatabase();
        var page = new byte[4096];
        Array.Fill(page, (byte)0xAB);
        File.WriteAllBytes(world.Journal, ForgedHotJournal(originalPages: (uint)(World.Length(world.Main) / 4096), (1u, page)));
        var main = Sha(world.Main);
        var journal = Sha(world.Journal);
        var writers = WriterConnection.WritersOpenedTotal;
        var session = world.NewSession();
        var status = session.RunStartupOpen();
        Assert.Equal(LibraryState.LeftoverFiles, status.State, $"{status.Reason}: {status.Message}");
        Assert.Equal(LibraryReason.JournalBesideUninitialised, status.Reason);
        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            Assert.Equal(LibraryState.LeftoverFiles, session.PrepareForSave(prepare).State, "a save does not create over it either");
        }
        Assert.Equal(main, Sha(world.Main), "the database is byte-identical");
        Assert.Equal(journal, Sha(world.Journal), "and so is the journal: SQLite never played it back");
        Assert.Equal(writers, WriterConnection.WritersOpenedTotal, "no connection was opened");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void An_empty_schema_with_a_user_version_or_a_remaining_table_is_not_uninitialised_and_is_never_overwritten()
    {
        foreach (var (name, world) in new (string, World)[] { ("user_version 1", EmptiedDatabase(userVersion: 1)), ("a table left behind", EmptiedDatabase(leaveATable: true)) })
        {
            var before = Sha(world.Main);
            var session = world.NewSession();
            var status = session.RunStartupOpen();
            Assert.Equal(LibraryState.NotALibrary, status.State, $"{name}: {status.Reason}");
            using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
            {
                var attempted = session.PrepareForSave(prepare);
                Assert.Equal(LibraryState.NotALibrary, attempted.State, name + ": a save does not create over it");
            }
            Assert.Equal(before, Sha(world.Main), name + ": the file was never overwritten");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void An_interrupted_T_CREATE_that_SQLite_rolls_back_to_an_empty_database_is_uninitialised_not_a_foreign_file()
    {
        // the crash window of the review (C4-M01): SQLite wrote every page of T-CREATE into the main file (a valid header with the
        // Library's application id) and died before it truncated the journal. The journal records the original size, 0 pages, so the
        // next open rolls the file back to nothing. The header pre-check sees a valid Library; SQLite then rolls the file back; the
        // application id then reads 0. That is an empty database, not "Not a Library, set it aside".
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);
        File.WriteAllBytes(world.Main, ValidLibraryBytes());
        File.WriteAllBytes(world.Journal, ForgedHotJournal(originalPages: 0));
        Assert.Equal(HeaderOutcome.Valid, HeaderCheck.Classify(File.ReadAllBytes(world.Main).AsSpan(0, 108), World.Length(world.Main)).Outcome, "the header of the half-created file is a Library's");

        var session = world.NewSession();
        var status = session.RunStartupOpen();
        Assert.Equal(LibraryState.NotCreated, status.State, $"{status.Reason}: {status.Message}");
        Assert.Equal(LibraryReason.Uninitialised, status.Reason);
        Assert.Equal(0L, World.Length(world.Main), "SQLite rolled the file back to its original size, 0 pages");
        Assert.True(World.Length(world.Journal) <= 0, "and SQLite finished with the journal");
        Assert.True(world.Names().All(n => n is LibraryNames.LockFile or LibraryNames.MainFile or LibraryNames.JournalFile), "nothing was set aside");

        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            Assert.Equal(LibraryState.Available, session.PrepareForSave(prepare).State, "initialised again on the next save");
        }
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(40), "after-the-crash");
        Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 1)));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_non_empty_journal_beside_an_absent_or_empty_main_file_is_Leftover_files_and_nothing_is_deleted()
    {
        // what a kill DURING T-CREATE (before SQLite wrote any page to the main file) leaves: a 0-byte main file and a journal with a
        // sector of header; creation must refuse (SQLite would delete that journal) and the user is offered a set-aside (LIB-07 step 4)
        foreach (var mainExists in new[] { true, false })
        {
            var world = World.Create();
            Directory.CreateDirectory(world.LibraryDirectory);
            if (mainExists) File.WriteAllBytes(world.Main, []);
            var journal = new byte[512];   // a zero magic: not hot, but not empty either
            File.WriteAllBytes(world.Journal, journal);
            var session = world.NewSession();
            Assert.Equal(LibraryState.LeftoverFiles, session.RunStartupOpen().State, $"main exists: {mainExists}");
            using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
            {
                Assert.Equal(LibraryState.LeftoverFiles, session.PrepareForSave(prepare).State, "creation is refused beside a non-empty journal");
            }
            Assert.Equal(512L, World.Length(world.Journal), "the journal was not deleted");
            Assert.Equal(mainExists ? 0L : -1L, World.Length(world.Main), "and no database was created beside it");
            session.TestOnlyShutdown();
        }
    }
}
