using System.Diagnostics;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// TEST-W1 (Q-21, a C4 STOP condition): on Windows, in the production sequence, reads write nothing. Library open (Open lease), T0
/// (Prepare lease, the writer closed after it), the window opened, reader connections opening, querying and closing throughout it,
/// the window closed. The mutation epoch, the commit generation and the SHA-256 of every member's CONTENTS are unchanged; no member
/// is created or deleted; no write handle is open during the window. Directory-listing sizes and times are recorded as information
/// only (OBS-04c): no detector uses them.
/// </summary>
public static class ReadsTests
{
    [Test]
    public static void Reads_in_the_observation_window_change_no_file_create_none_delete_none_and_open_no_writer()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(2500));   // a database of real size, with a published snapshot

        // production sequence: Prepare (T0, writer closed after it) → window
        var captureId = session.NewCaptureId();
        var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, "w1", 1)).GetAwaiter().GetResult();
        Assert.Equal(0, WriterConnection.OpenWriterCount, "the writer is closed before the lease ends (OBS-12)");
        var observation = prepare.HandOffToObservation();

        // the state at the window's opening (P2): epoch M1, commit generation D1, and every member's contents
        var m1 = session.Interlock.MutationEpoch;
        var d1 = session.Store.ReadHeader().Generation;
        var namesBefore = world.Names();
        var hashesBefore = world.ContentHashes();
        var listingBefore = Listing(world);
        Assert.True(d1 is not null, "the commit generation could be read while the Library is open");

        // reads throughout the window: repeated read-only connections opening, querying and closing; the OBS-04 header read, also
        // while a reader holds an open read transaction (the share-mode question of Q-21)
        for (var round = 0; round < 150; round++)
        {
            Assert.Equal(0, WriterConnection.OpenWriterCount, "no write handle is open during the window");
            session.Read(r =>
            {
                Assert.True(r.Long("SELECT count(*) FROM file_obs") > 0);
                r.Long("SELECT sum(size_bytes) FROM file_obs WHERE snapshot_id = 1");
                r.Query("SELECT folder_path_id, name_id, size_bytes FROM file_obs WHERE snapshot_id = 1 ORDER BY size_bytes DESC, seq LIMIT 50");
                r.Long("SELECT count(*) FROM folder_obs o JOIN folder_path p ON p.path_id = o.path_id WHERE o.snapshot_id = 1");
                var headerWhileReading = session.Store.ReadHeader();   // a header read while a reader is open on the same file
                Assert.Equal(HeaderOutcome.Valid, headerWhileReading.Outcome, "the OBS-04 header read coexists with SQLite's share modes");
                return 0;
            });
            if (round % 25 == 0) Assert.Equal(HeaderOutcome.Valid, session.Store.ReadHeader().Outcome);
        }
        // concurrent readers
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() => { for (var i = 0; i < 25; i++) session.Read(r => r.Long("SELECT count(*) FROM name")); })).ToArray();
        Task.WaitAll(tasks);

        // no write handle: with every reader closed, a non-writing EXCLUSIVE open of each member succeeds only if nobody else holds
        // any handle on it (the lock file is the one handle this process holds on purpose, with FileShare.None)
        foreach (var name in new[] { LibraryNames.MainFile, LibraryNames.JournalFile })
        {
            if (!File.Exists(world.Member(name))) continue;
            using var exclusive = new FileStream(world.Member(name), FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(exclusive.CanRead, $"no handle of any kind is open on {name} during the window");
        }

        // the window closes (P4): M2, D2, and the contents
        var save = observation.HandOffToSave(out var close);
        var d2 = session.Store.ReadHeader().Generation;
        var hashesAfter = world.ContentHashes();
        var listingAfter = Listing(world);
        save.Dispose();

        Assert.True(close.Unchanged, "the mutation epoch is unchanged and no other transition happened (OBS-04a)");
        Assert.Equal(m1, close.EpochAtClose);
        Assert.Equal(d1, d2, "the commit generation is unchanged (OBS-04b)");
        Assert.SequenceEqual(namesBefore, world.Names(), "no member was created or deleted");
        Assert.SequenceEqual(hashesBefore.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value), hashesAfter.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value),
            "the SHA-256 of every member's contents is unchanged");
        Console.WriteLine("Q-21 information (not asserted): listing before the window  " + listingBefore);
        Console.WriteLine("Q-21 information (not asserted): listing after the window   " + listingAfter);
        Console.WriteLine("Q-21 information: directory listing " + (listingBefore == listingAfter ? "was identical" : "differed") + " before and after the window");
        session.TestOnlyShutdown();
    }

    private static string Listing(World world) => string.Join("; ", world.Names().Select(n =>
    {
        var info = new FileInfo(world.Member(n));
        return $"{n} {info.Length} B {info.LastWriteTimeUtc:O}";
    }));

    [Test]
    public static void A_reader_connection_cannot_write_anything()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(10));
        session.TestOnlyShutdown();
        var reader = world.OpenedSession();
        var before = world.ContentHashes();
        Assert.Throws<Exception>(() => reader.Read(r => r.Query("CREATE TABLE intruder (a INTEGER)")));
        Assert.Throws<Exception>(() => reader.Read(r => r.Query("UPDATE snapshot SET files = 0")));
        Assert.SequenceEqual(before.OrderBy(p => p.Key).Select(p => p.Key + p.Value), world.ContentHashes().OrderBy(p => p.Key).Select(p => p.Key + p.Value), "nothing changed");
        reader.TestOnlyShutdown();
    }

    [Test]
    public static void The_commit_generation_advances_with_every_commit_and_never_with_a_read()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var g0 = session.Store.ReadHeader().Generation;
        for (var i = 0; i < 20; i++) session.Read(r => r.Long("SELECT count(*) FROM library_info"));
        Assert.Equal(g0, session.Store.ReadHeader().Generation, "reads never advance it");
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(20));
        var g1 = session.Store.ReadHeader().Generation;
        Assert.True(g1 != g0 && g1!.Value.ChangeCounter > g0!.Value.ChangeCounter, "a commit advances the file change counter");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void The_header_read_coexists_with_a_write_transaction_in_progress()
    {
        // Q-21's second half: the OBS-04 read uses FileShare.ReadWrite | FileShare.Delete, and SQLite's own handle on Windows shares
        // read and write, so the two coexist even while a transaction is open and has spilled pages to the database file
        var world = World.Create();
        var seen = new List<HeaderOutcome>();
        LibrarySession? session = null;
        session = world.CreatedSession();
        var big = new SyntheticSnapshot(30_000, seed: 4);
        var options = new ImportOptions { OnFileRows = count => { if (count % 49_152 == 0) seen.Add(session!.Store.ReadHeader().Outcome); } };
        var captureId = session.NewCaptureId();
        using var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes("x"), null, "run-1", 1)).GetAwaiter().GetResult();
        using var save = prepare.HandOffToObservation().HandOffToSave(out _);
        session.ImportSnapshotAsync(save, attempt, SyntheticSnapshot.NewSource(), big.Header(), big, options).GetAwaiter().GetResult();
        Assert.True(seen.Count >= 1, "the header was read during the transaction");
        Assert.True(seen.All(o => o == HeaderOutcome.Valid), "and was always readable: " + string.Join(",", seen));
        session.TestOnlyShutdown();
    }

    // ---- the in-process gate (CONC-06) ----

    [Test]
    public static void The_gate_lets_readers_coexist_and_a_writer_waits_for_them_and_cancels_them()
    {
        var gate = new ReaderWriterGate();
        var r1 = gate.AcquireReaderAsync().GetAwaiter().GetResult();
        var r2 = gate.AcquireReaderAsync().GetAwaiter().GetResult();
        Assert.Equal(2, gate.ActiveReaders, "readers coexist");

        var writer = gate.AcquireWriterAsync();
        Assert.False(writer.IsCompleted, "the writer waits for the readers");
        Assert.True(r1.Token.IsCancellationRequested && r2.Token.IsCancellationRequested, "in-flight readers are cancelled when a writer asks");
        var lateReader = gate.AcquireReaderAsync();
        Assert.False(lateReader.IsCompleted, "a new reader waits while a writer is waiting (no starvation)");

        r1.Dispose();
        Assert.False(writer.IsCompleted, "one reader is still active");
        r2.Dispose();
        Assert.True(writer.Wait(5000), "the writer proceeds when the last reader released");
        Assert.True(gate.WriterActive);
        Assert.False(lateReader.IsCompleted, "readers wait while the writer holds the gate");
        writer.Result.Dispose();
        Assert.True(lateReader.Wait(5000), "and are admitted when it is released");
        lateReader.Result.Dispose();
        Assert.Equal(0, gate.ActiveReaders);
    }

    [Test]
    public static void The_gate_serves_writers_in_order_admits_waiting_readers_together_and_handles_cancellation()
    {
        var gate = new ReaderWriterGate();
        var w1 = gate.AcquireWriterAsync().GetAwaiter().GetResult();
        var w2 = gate.AcquireWriterAsync();
        var r1 = gate.AcquireReaderAsync();
        var r2 = gate.AcquireReaderAsync();
        w1.Dispose();
        Assert.True(w2.Wait(5000), "the second writer is next");
        Assert.False(r1.IsCompleted || r2.IsCompleted, "readers still wait behind a writer that holds the gate");
        w2.Result.Dispose();
        Assert.True(Task.WaitAll([r1, r2], 5000), "both waiting readers are admitted together");
        Assert.Equal(2, gate.ActiveReaders);
        r1.Result.Dispose();
        r2.Result.Dispose();

        // a waiting reader that is cancelled leaves cleanly; a writer behind the cancelled one is not blocked
        var holder = gate.AcquireWriterAsync().GetAwaiter().GetResult();
        using var cancel = new CancellationTokenSource();
        var cancelled = gate.AcquireReaderAsync(cancel.Token);
        var next = gate.AcquireWriterAsync();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => cancelled.GetAwaiter().GetResult());
        holder.Dispose();
        Assert.True(next.Wait(5000), "the writer proceeds");
        next.Result.Dispose();
        Assert.False(gate.WriterActive);
        Assert.Equal(0, gate.WaitingWriters);

        gate.Close();
        Assert.Throws<ObjectDisposedException>(() => gate.AcquireReaderAsync().GetAwaiter().GetResult());
        Assert.Throws<ObjectDisposedException>(() => gate.AcquireWriterAsync().GetAwaiter().GetResult());
    }

    [Test]
    public static void The_gate_never_admits_a_reader_and_a_writer_together_under_load()
    {
        var gate = new ReaderWriterGate();
        var readers = 0;
        var writers = 0;
        var violations = 0;
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            var random = new Random(t);
            for (var i = 0; i < 400; i++)
            {
                if (random.Next(5) == 0)
                {
                    using var w = gate.AcquireWriterAsync().GetAwaiter().GetResult();
                    if (Interlocked.Increment(ref writers) != 1 || Volatile.Read(ref readers) != 0) Interlocked.Increment(ref violations);
                    Thread.SpinWait(random.Next(200));
                    Interlocked.Decrement(ref writers);
                }
                else
                {
                    using var r = gate.AcquireReaderAsync().GetAwaiter().GetResult();
                    Interlocked.Increment(ref readers);
                    if (Volatile.Read(ref writers) != 0) Interlocked.Increment(ref violations);
                    Thread.SpinWait(random.Next(200));
                    Interlocked.Decrement(ref readers);
                }
            }
        })).ToList();
        var watch = Stopwatch.StartNew();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) Assert.True(thread.Join(60_000), "no deadlock");
        Assert.Equal(0, violations, "a reader and a writer were never inside the gate together");
        Assert.Equal(0, gate.ActiveReaders);
        Assert.False(gate.WriterActive);
    }

    [Test]
    public static void A_writer_cancels_the_readers_of_a_session_and_they_surface_as_cancellation_to_be_re_run()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(30));
        var inside = new ManualResetEventSlim();
        var read = Task.Run(() => session.ReadAsync(r =>
        {
            inside.Set();
            r.Token.WaitHandle.WaitOne(30_000);
            r.Token.ThrowIfCancellationRequested();
            return 1;
        }));
        Assert.True(inside.Wait(10_000));
        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes("x"), null, "r", 1)).GetAwaiter().GetResult();   // takes the gate as a writer
        }
        Assert.Throws<OperationCanceledException>(() => read.GetAwaiter().GetResult());
        Assert.Equal(1L, session.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE run_id = 'r'")), "a re-run after the write sees the new row");
        session.TestOnlyShutdown();
    }
}
