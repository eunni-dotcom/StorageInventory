using System.Buffers.Binary;
using System.Security.Cryptography;
using StorageInventory.History.Library;

namespace StorageInventory.Library;

/// <summary>What the 100-byte header pre-check found (LIB-08 step 4, SEC-32). SQLite never opens a file that is not
/// <see cref="Valid"/>: merely opening a suspect database can roll back, delete or write recovery side files.</summary>
internal enum HeaderOutcome
{
    /// <summary>There is no main file.</summary>
    Absent = 0,

    /// <summary>The file exists but is shorter than the 100-byte header.</summary>
    TooShort = 1,

    /// <summary>The magic string is not <c>SQLite format 3\0</c>.</summary>
    ForeignFile = 2,

    /// <summary>Header bytes 18 and 19 are not both 1: 2 means WAL format, which this Library never uses.</summary>
    WalFormat = 3,

    /// <summary><c>application_id</c> at offset 68 is not 0x53494E56.</summary>
    WrongApplicationId = 4,

    /// <summary><c>user_version</c> at offset 60 is newer than this version supports.</summary>
    NewerSchema = 5,

    /// <summary><c>user_version</c> is not a version this release has ever written (0).</summary>
    UnsupportedUserVersion = 6,

    /// <summary>Magic, format, application id and a supported schema version.</summary>
    Valid = 7,
}

/// <summary>The decoded fields of a header read, and the commit generation (OBS-04b).</summary>
/// <param name="Outcome">The classification.</param>
/// <param name="ChangeCounter">The file change counter at offset 24.</param>
/// <param name="PageCount">The database size in pages at offset 28.</param>
/// <param name="ApplicationId">Offset 68.</param>
/// <param name="UserVersion">Offset 60.</param>
/// <param name="FileLength">The length of the file when it was read.</param>
internal readonly record struct HeaderRead(HeaderOutcome Outcome, uint ChangeCounter, uint PageCount, int ApplicationId, int UserVersion, long FileLength)
{
    /// <summary>The commit generation: (change counter, page count). "Absent" has no generation.</summary>
    internal (uint ChangeCounter, uint PageCount)? Generation => Outcome == HeaderOutcome.Absent || Outcome == HeaderOutcome.TooShort ? null : (ChangeCounter, PageCount);
}

/// <summary>The existence and length of one Library member.</summary>
internal readonly record struct MemberFacts(bool Exists, long Length)
{
    internal bool NonEmpty => Exists && Length > 0;
}

/// <summary>The owned members as found (LIB-02). Anything else in the directory is never inspected, written or deleted.</summary>
internal sealed record MemberSet(MemberFacts Lock, MemberFacts Main, MemberFacts Journal, MemberFacts Wal, MemberFacts Shm);

internal enum LockOutcome
{
    /// <summary>This process holds <c>library.lock</c> with <c>FileShare.None</c> until it exits.</summary>
    Held = 0,

    /// <summary>A sharing violation: another process holds it (CONC-02). Nothing else was inspected.</summary>
    InUse = 1,

    /// <summary>Something else went wrong (access denied, I/O error, the file vanished twice). Retry is possible.</summary>
    Unavailable = 2,
}

/// <summary>The result of <see cref="LibraryStore.AcquireWriterLock"/>.</summary>
/// <param name="Outcome">Held, InUse or Unavailable.</param>
/// <param name="LockPreExisted">Whether <c>library.lock</c> existed at this process's FIRST acquisition (recorded once, LIB-07 step 3).</param>
/// <param name="Detail">The failure, for Unavailable.</param>
internal readonly record struct WriterLockResult(LockOutcome Outcome, bool LockPreExisted, string? Detail);

/// <summary>The outcome of a set-aside (LIB-13 steps 4 and 5).</summary>
/// <param name="Stem">The quarantine stem chosen (<c>library.damaged-yyyyMMdd_HHmmss-xxxxxx</c>), or null when none could be chosen.</param>
/// <param name="Moved">The member names moved, in order.</param>
/// <param name="Failure">Why the sequence stopped early, or null when every present member was moved.</param>
internal sealed record QuarantineResult(string? Stem, IReadOnlyList<string> Moved, string? Failure)
{
    internal bool Complete => Failure is null && Stem is not null;
}

/// <summary>Test-only seam: a hook called after each rename of a set-aside (to crash between renames, TEST-L6).</summary>
internal sealed class LibraryStoreHooks
{
    internal Action<int>? AfterRename { get; init; }
}

/// <summary>
/// Every filesystem operation of the Library that creates or renames something, and the read-only inspections that decide
/// states (§16.2, A-01, A-03, A-04, A-15). It is the ONLY place that uses <c>FileMode.CreateNew</c>, creates the Library
/// directory, opens <c>library.lock</c>, or calls <c>File.Move</c> (and only the set-aside does). StorageInventory itself never
/// deletes a Library file (A-04). Every mutating method takes a <see cref="MutationLease"/> and checks it is current and of an
/// allowed kind BEFORE any I/O (OBS-15, A-25); the read-only ones (<see cref="ReadHeader"/>, member inspection) need none.
/// </summary>
internal sealed class LibraryStore
{
    private readonly LibraryInterlock _interlock;
    private readonly LibraryStoreHooks? _hooks;
    private FileStream? _lockHandle;
    private bool _lockPreExisted;

    internal LibraryStore(string libraryDirectory, string appDataRoot, LibraryInterlock interlock, LibraryStoreHooks? hooks = null)
    {
        Directory = Path.TrimEndingDirectorySeparator(libraryDirectory);
        AppDataRoot = Path.TrimEndingDirectorySeparator(appDataRoot);
        _interlock = interlock;
        _hooks = hooks;
    }

    /// <summary>The Library directory (explicit: the Library code is location-agnostic, LIB-04).</summary>
    internal string Directory { get; }

    /// <summary>The directory that contains the Library directory (the app-data root, LIB-06a/b).</summary>
    internal string AppDataRoot { get; }

    internal string LockPath => Path.Combine(Directory, LibraryNames.LockFile);
    internal string MainPath => Path.Combine(Directory, LibraryNames.MainFile);
    internal string JournalPath => Path.Combine(Directory, LibraryNames.JournalFile);
    internal string WalPath => Path.Combine(Directory, LibraryNames.WalFile);
    internal string ShmPath => Path.Combine(Directory, LibraryNames.ShmFile);

    /// <summary>True once this process holds <c>library.lock</c>; it then holds it until it exits (CONC-01).</summary>
    internal bool HoldsWriterLock => _lockHandle is not null;

    /// <summary>Whether <c>library.lock</c> existed at this process's first acquisition. Meaningful once <see cref="HoldsWriterLock"/>.</summary>
    internal bool LockPreExisted => _lockPreExisted;

    // ---- read-only: path validation and inspection (no lease: A-25 part d) ----

    /// <summary>The Library path rules (§6.4), validated before every open and before every creation step.</summary>
    internal LibraryPathAssessment ValidatePath() => LibraryPathRules.ValidateLibraryDirectory(Directory, AppDataRoot);

    internal bool DirectoryExists() => System.IO.Directory.Exists(Directory);

    /// <summary>Existence and length of the owned members. Only valid as a basis for a decision while this process holds the
    /// writer lock (LIB-07 step 4, LIB-08 step 3): calling it earlier is a defect and throws, so no member is ever inspected before
    /// the lock is held (CONC-01).</summary>
    internal MemberSet InspectMembersUnderLock()
    {
        if (_lockHandle is null) throw new InvalidOperationException("No Library member may be inspected before the writer lock is held (CONC-01).");
        return new MemberSet(Info(LockPath), Info(MainPath), Info(JournalPath), Info(WalPath), Info(ShmPath));
    }

    private static MemberFacts Info(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new MemberFacts(true, info.Length) : new MemberFacts(false, 0);
    }

    /// <summary>Reads and classifies the first 100 bytes of <c>library.sqlite3</c> without SQLite (LIB-08 step 4; also the commit
    /// generation, OBS-04b). <c>FileMode.Open</c>, <c>FileAccess.Read</c>, <c>FileShare.ReadWrite | FileShare.Delete</c>, so it
    /// coexists with SQLite's own handles on Windows (Q-21) and cannot write. A missing file is <see cref="HeaderOutcome.Absent"/>.</summary>
    internal HeaderRead ReadHeader()
    {
        try
        {
            using var stream = new FileStream(MainPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            var buffer = new byte[100];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            return HeaderCheck.Classify(buffer.AsSpan(0, read), stream.Length);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new HeaderRead(HeaderOutcome.Absent, 0, 0, 0, 0, 0);
        }
    }

    // ---- mutations: each takes a lease and checks it before any I/O ----

    /// <summary>Creates the Library directory chain (LIB-07 step 2) and re-checks that no part of it is a reparse point. Only
    /// <c>LibraryStore</c> creates directories (A-01). Returns the path assessment after creation.</summary>
    internal LibraryPathAssessment EnsureLibraryFolder(MutationLease lease)
    {
        _interlock.Require(lease, "create the Library folder", MutationKind.Create, MutationKind.Prepare);
        var before = ValidatePath();
        if (before.Blocked) return before;
        System.IO.Directory.CreateDirectory(Directory);
        return ValidatePath();
    }

    /// <summary>The writer lock, exactly as LIB-07 step 3 (CONC-01): if this process holds it already, continue. Otherwise open
    /// <c>library.lock</c> with <c>FileMode.Open</c>, <c>FileAccess.Read</c>, <c>FileShare.None</c>; if it does not exist create it
    /// with <c>FileMode.CreateNew</c>, <c>FileAccess.Write</c>, <c>FileShare.None</c> and keep that handle; if the create fails
    /// because the file now exists (another process won the race) open it once more as above. A sharing violation at any step is
    /// <see cref="LockOutcome.InUse"/> and nothing else is checked, created or renamed; a second "not found" is Unavailable. The
    /// handle is held until the process exits and is never released and re-acquired in between. No named kernel object is used (A-15).</summary>
    internal WriterLockResult AcquireWriterLock(MutationLease lease)
    {
        _interlock.Require(lease, "acquire the writer lock", MutationKind.Open, MutationKind.Create, MutationKind.Prepare, MutationKind.SetAside);
        if (_lockHandle is not null) return new WriterLockResult(LockOutcome.Held, _lockPreExisted, null);

        try
        {
            _lockHandle = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1);
            _lockPreExisted = true;
            return new WriterLockResult(LockOutcome.Held, true, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // fall through: create it
        }
        catch (Exception ex) when (IsSharingViolation(ex))
        {
            return new WriterLockResult(LockOutcome.InUse, false, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WriterLockResult(LockOutcome.Unavailable, false, ex.Message);
        }

        try
        {
            _lockHandle = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1);
            _lockPreExisted = false;
            return new WriterLockResult(LockOutcome.Held, false, null);
        }
        catch (Exception ex) when (IsSharingViolation(ex))
        {
            return new WriterLockResult(LockOutcome.InUse, false, null);
        }
        catch (Exception ex) when (ex is IOException)
        {
            // Another process won the race to create it: open it once more, as above.
            try
            {
                _lockHandle = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1);
                _lockPreExisted = true;
                return new WriterLockResult(LockOutcome.Held, true, null);
            }
            catch (Exception second) when (IsSharingViolation(second))
            {
                return new WriterLockResult(LockOutcome.InUse, false, null);
            }
            catch (Exception second) when (second is IOException or UnauthorizedAccessException)
            {
                return new WriterLockResult(LockOutcome.Unavailable, false, second.Message);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return new WriterLockResult(LockOutcome.Unavailable, false, ex.Message);
        }
    }

    /// <summary>Creates <c>library.sqlite3</c> as a 0-byte file with <c>FileMode.CreateNew</c> (LIB-07 step 5): never an overwrite,
    /// and SQLite never opens a file in a create mode. Requires the lock to be held. Returns false when the file now exists (the
    /// attempt ends Unavailable, retry); throws nothing for that case.</summary>
    internal bool CreateEmptyDatabase(MutationLease lease)
    {
        _interlock.Require(lease, "create the database file", MutationKind.Create, MutationKind.Prepare);
        if (_lockHandle is null) throw new InvalidOperationException("The writer lock must be held before any Library member is created (CONC-01).");
        if (ValidatePath().Blocked) return false;
        try
        {
            using var created = new FileStream(MainPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1);
            return true;
        }
        catch (IOException) when (File.Exists(MainPath))
        {
            return false;
        }
    }

    /// <summary>Sets the member set aside (LIB-13 steps 4 and 5), the only rename in the product: chooses a stem
    /// <c>library.damaged-&lt;yyyyMMdd_HHmmss&gt;-&lt;6hex&gt;</c> for which none of the four target names exists (a new suffix up to
    /// 16 times), then renames <c>library.sqlite3</c> FIRST, then each present <c>-journal</c>, <c>-wal</c>, <c>-shm</c> to the same
    /// stem, each with <c>File.Move(overwrite: false)</c>. A rename that fails stops the sequence. The caller must hold the writer
    /// lock and have closed every connection of the process (LIB-13 steps 2 and 3); the lock file is never touched, and nothing is
    /// ever deleted.</summary>
    internal QuarantineResult QuarantineSet(MutationLease lease)
    {
        _interlock.Require(lease, "set the Library aside", MutationKind.SetAside);
        if (_lockHandle is null) throw new InvalidOperationException("A process that does not hold the writer lock can never set the Library aside (LIB-13 step 2).");

        string? stem = null;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < 16 && stem is null; attempt++)
        {
            var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            var candidate = $"{LibraryNames.QuarantinePrefix}{stamp}-{suffix}";
            if (!TargetNames(candidate).Any(name => File.Exists(Path.Combine(Directory, name)))) stem = candidate;
        }
        if (stem is null) return new QuarantineResult(null, [], "No unused set-aside name could be found.");

        var moved = new List<string>();
        var members = new[]
        {
            (LibraryNames.MainFile, LibraryNames.QuarantineMainSuffix),
            (LibraryNames.JournalFile, LibraryNames.QuarantineJournalSuffix),
            (LibraryNames.WalFile, LibraryNames.QuarantineWalSuffix),
            (LibraryNames.ShmFile, LibraryNames.QuarantineShmSuffix),
        };
        foreach (var (name, suffix) in members)
        {
            var source = Path.Combine(Directory, name);
            if (!File.Exists(source)) continue;
            // The destination is created only if absent: File.Move with overwrite: false never replaces anything.
            try
            {
                File.Move(source, Path.Combine(Directory, stem + suffix), overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new QuarantineResult(stem, moved, $"{name}: {ex.Message}");
            }
            moved.Add(name);
            _hooks?.AfterRename?.Invoke(moved.Count);
        }
        return new QuarantineResult(stem, moved, null);
    }

    /// <summary>Closes the writer-lock handle the way process exit does, for tests that cannot exit. Production never calls it (the
    /// lock is held until the process exits, CONC-01), and an audit checks that no shipped code does.</summary>
    internal void TestOnlyReleaseWriterLock()
    {
        _lockHandle?.Dispose();
        _lockHandle = null;
    }

    private static IEnumerable<string> TargetNames(string stem) =>
        [stem + LibraryNames.QuarantineMainSuffix, stem + LibraryNames.QuarantineJournalSuffix, stem + LibraryNames.QuarantineWalSuffix, stem + LibraryNames.QuarantineShmSuffix];

    /// <summary>ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33): another handle excludes this open.</summary>
    internal static bool IsSharingViolation(Exception ex) => ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33;
}

/// <summary>The pure header classification of LIB-08 step 4 (A-03), separated so tests can feed it any byte sequence.</summary>
internal static class HeaderCheck
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    internal static HeaderRead Classify(ReadOnlySpan<byte> header, long fileLength)
    {
        if (header.Length < 100) return new HeaderRead(HeaderOutcome.TooShort, 0, 0, 0, 0, fileLength);
        var changeCounter = BinaryPrimitives.ReadUInt32BigEndian(header[24..]);
        var pageCount = BinaryPrimitives.ReadUInt32BigEndian(header[28..]);
        var userVersion = BinaryPrimitives.ReadInt32BigEndian(header[60..]);
        var applicationId = BinaryPrimitives.ReadInt32BigEndian(header[68..]);

        HeaderOutcome outcome;
        if (!header[..16].SequenceEqual(Magic)) outcome = HeaderOutcome.ForeignFile;
        else if (header[18] != 1 || header[19] != 1) outcome = HeaderOutcome.WalFormat;
        else if (applicationId != LibraryNames.ApplicationId) outcome = HeaderOutcome.WrongApplicationId;
        else if (userVersion > LibraryNames.SchemaVersion) outcome = HeaderOutcome.NewerSchema;
        else if (userVersion < 1) outcome = HeaderOutcome.UnsupportedUserVersion;
        else outcome = HeaderOutcome.Valid;
        return new HeaderRead(outcome, changeCounter, pageCount, applicationId, userVersion, fileLength);
    }
}
