using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// CONC-01, LIB-07 step 3, C4-M17 ("No member of the Library is inspected before the writer lock is held"). The static half is the audit
/// (LeaseAuditTests: only LibraryStore touches the file system, and every member call follows <c>AcquireWriterLock</c> in IL). Here the
/// run-time half: every member accessor of <see cref="LibraryStore"/> throws without the lock, and a session that cannot get the lock
/// (another handle excludes it, as another process does) leaves every other member byte for byte as it was and opens no connection.
/// </summary>
public static class LockFirstTests
{
    private static MutationLease LeaseOf(LibraryInterlock interlock, MutationKind kind)
    {
        Assert.True(interlock.TryBeginMutation(kind, 0, out var lease, out var refusal), $"a {kind} lease was refused: {refusal}");
        return lease;
    }

    private static LibraryStore NewStore(World world, out LibraryInterlock interlock)
    {
        interlock = new LibraryInterlock();
        return new LibraryStore(world.LibraryDirectory, world.AppData, interlock);
    }

    [Test]
    public static void Every_member_accessor_of_LibraryStore_throws_without_the_writer_lock_and_touches_nothing()
    {
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);   // the directory exists; no member does
        var store = NewStore(world, out var interlock);
        Assert.False(store.HoldsWriterLock, "the premise: this store does not hold the lock");
        var writersBefore = WriterConnection.OpenWriterCount;

        var accessors = new (string Name, Action Call)[]
        {
            ("InspectMembersUnderLock", () => store.InspectMembersUnderLock()),
            ("ReadHeaderUnderLock", () => store.ReadHeaderUnderLock()),
            ("MainPathUnderLock", () => store.MainPathUnderLock()),
            ("MeasureLengthsUnderLock", () => store.MeasureLengthsUnderLock()),
        };
        foreach (var (name, call) in accessors)
        {
            var thrown = Assert.Throws<InvalidOperationException>(call);
            Assert.True(thrown.GetType() == typeof(InvalidOperationException), $"{name} throws the lock refusal, not a lease or I/O error: {thrown.GetType().Name}");
            Assert.Contains("writer lock", thrown.Message);
            Assert.Contains("CONC-01", thrown.Message);
        }

        // the members that create or rename check the lease first and then the lock, before touching anything
        interlock.TakeStartupLease().Dispose();
        using (var create = LeaseOf(interlock, MutationKind.Create))
        {
            var thrown = Assert.Throws<InvalidOperationException>(() => store.CreateEmptyDatabase(create));
            Assert.True(thrown.GetType() == typeof(InvalidOperationException), "CreateEmptyDatabase refuses for the lock, not for the lease: " + thrown.GetType().Name);
            Assert.Contains("writer lock", thrown.Message);
        }
        using (var setAside = LeaseOf(interlock, MutationKind.SetAside))
        {
            var thrown = Assert.Throws<InvalidOperationException>(() => store.QuarantineSet(setAside));
            Assert.True(thrown.GetType() == typeof(InvalidOperationException), "QuarantineSet refuses for the lock, not for the lease: " + thrown.GetType().Name);
            Assert.Contains("writer lock", thrown.Message);
        }
        Assert.False(interlock.IsFaulted, "a correct lease used without the lock is a lifecycle defect of the caller, not a lease violation");
        Assert.SequenceEqual(Array.Empty<string>(), world.Names(), "nothing was created: no lock file, no main file");
        Assert.Equal(writersBefore, WriterConnection.OpenWriterCount, "no writer connection was opened");
    }

    [Test]
    public static void The_accessors_work_once_the_lock_is_held_and_never_before()
    {
        var world = World.Create();
        var store = NewStore(world, out var interlock);
        interlock.TakeStartupLease().Dispose();
        using var lease = LeaseOf(interlock, MutationKind.Create);
        Assert.True(store.EnsureLibraryFolder(lease).Blocked == false, "the folder is created");
        Assert.Throws<InvalidOperationException>(() => store.InspectMembersUnderLock());
        Assert.Equal(LockOutcome.Held, store.AcquireWriterLock(lease).Outcome);
        Assert.True(store.HoldsWriterLock);
        var members = store.InspectMembersUnderLock();
        Assert.True(members.Lock.Exists && !members.Main.Exists, "after the lock: only the lock file exists");
        Assert.Equal(HeaderOutcome.Absent, store.ReadHeaderUnderLock().Outcome);
        Assert.Equal(world.Main, store.MainPathUnderLock());
        store.TestOnlyReleaseWriterLock();
    }

    /// <summary>A world with a created Library whose lock handle has been released, then the lock held by a handle of this test.</summary>
    private static (World World, FileStream Held) LockedLibrary(Action<World>? prepare = null)
    {
        var world = World.Create();
        var session = world.CreatedSession();
        session.TestOnlyShutdown();
        prepare?.Invoke(world);
        return (world, new FileStream(world.Lock, FileMode.Open, FileAccess.Read, FileShare.None));
    }

    private static void AssertUntouched(World world, Dictionary<string, string> before, List<string> namesBefore, string label)
    {
        Assert.SequenceEqual(namesBefore, world.Names(), label + ": no member was created or deleted");
        Assert.SequenceEqual(before.OrderBy(h => h.Key).Select(h => h.Key + "=" + h.Value), world.ContentHashes().OrderBy(h => h.Key).Select(h => h.Key + "=" + h.Value), label + ": every member's contents are byte for byte as they were");
    }

    [Test]
    public static void A_session_that_cannot_take_the_lock_leaves_every_other_member_byte_identical_and_opens_no_connection()
    {
        var writersBefore = WriterConnection.OpenWriterCount;
        var (world, held) = LockedLibrary();
        using (held)
        {
            var before = world.ContentHashes();
            var names = world.Names();
            Assert.True(names.Contains(LibraryNames.MainFile) && names.Contains(LibraryNames.LockFile), "the premise: a created Library, lock and main file");

            var session = world.NewSession();
            var status = session.RunStartupOpen();
            Assert.Equal(LibraryState.InUse, status.State, "the lock is held elsewhere: In use, and nothing else is decided: " + status.Message);
            Assert.False(session.Store.HoldsWriterLock);
            Assert.Equal(writersBefore, WriterConnection.OpenWriterCount, "no writer connection was opened");
            AssertUntouched(world, before, names, "the open of a locked Library");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_locked_Library_is_never_inspected_whatever_its_main_file_or_journal_looks_like()
    {
        // each case plants a member that, if it were inspected BEFORE the lock, would change the answer from "In use" to something else
        var cases = new (string Name, Action<World> Plant)[]
        {
            ("a foreign file where the main file should be (would be Not a Library)", w => File.WriteAllBytes(w.Main, new byte[4096])),
            ("a non-empty journal and no main file (would be Leftover files)", w => { File.Delete(w.Main); File.WriteAllBytes(w.Journal, [1, 2, 3, 4]); }),
            ("a hot journal beside a valid main file (SQLite would roll it back)", w => File.WriteAllBytes(w.Journal, new byte[512])),
            ("a -wal file (would be Not a Library)", w => File.WriteAllBytes(w.Member(LibraryNames.WalFile), [9])),
            ("no main file at all (would be Missing)", w => File.Delete(w.Main)),
            ("a 0-byte main file (would be Not created)", w => File.WriteAllBytes(w.Main, [])),
        };
        foreach (var (name, plant) in cases)
        {
            var writersBefore = WriterConnection.OpenWriterCount;
            var (world, held) = LockedLibrary(plant);
            using (held)
            {
                var before = world.ContentHashes();
                var names = world.Names();
                var session = world.NewSession();
                var status = session.RunStartupOpen();
                Assert.Equal(LibraryState.InUse, status.State, $"{name}: the lock decides first, so the state is In use: {status.Message}");
                Assert.Equal(writersBefore, WriterConnection.OpenWriterCount, name + ": no writer connection was opened");
                AssertUntouched(world, before, names, name);
                session.TestOnlyShutdown();
            }
        }
    }

    [Test]
    public static void A_locked_main_file_that_cannot_even_be_opened_does_not_change_the_answer()
    {
        // a tripwire: the main file itself is held exclusively by this test, so any attempt to open it (the header read, SQLite) would
        // fail with a sharing violation, and the state would be an I/O failure instead of "In use"
        var (world, held) = LockedLibrary();
        using (held)
        using (new FileStream(world.Main, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var session = world.NewSession();
            var status = session.RunStartupOpen();
            Assert.Equal(LibraryState.InUse, status.State, "the main file was never opened before the lock was refused: " + status.Message);
            Assert.NotNull(status.Message);
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_directory_that_exists_with_no_member_gets_only_the_lock_taken_by_the_one_that_wins_it()
    {
        // an empty Library directory and the lock held elsewhere: the loser creates nothing
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);
        using (new FileStream(world.Lock, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var session = world.NewSession();
            var status = session.RunStartupOpen();
            Assert.Equal(LibraryState.InUse, status.State, status.Message);
            Assert.SequenceEqual([LibraryNames.LockFile], world.Names(), "the loser created nothing: only the winner's lock file exists");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_set_aside_by_a_session_that_cannot_take_the_lock_renames_nothing()
    {
        var (world, held) = LockedLibrary();
        using (held)
        {
            var before = world.ContentHashes();
            var names = world.Names();
            var session = world.NewSession();
            session.RunStartupOpen();
            using var lease = World.Lease(session, MutationKind.SetAside);
            var result = session.SetAsideAsync(lease).GetAwaiter().GetResult();
            Assert.Null(result.Quarantine, "no set-aside was attempted");
            Assert.Equal(LibraryState.InUse, result.Status.State, result.Status.Message);
            AssertUntouched(world, before, names, "the set-aside of a locked Library");
            session.TestOnlyShutdown();
        }
    }
}
