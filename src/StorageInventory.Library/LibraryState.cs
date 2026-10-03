namespace StorageInventory.Library;

/// <summary>The nine Library states of §6.8 (LIB-09). They are never collapsed into a generic error: each has its own behaviour
/// and its own "never" (recreate silently, overwrite, downgrade, delete).</summary>
internal enum LibraryState
{
    /// <summary>No Library directory, no member files, or an uninitialised 0-byte main file. A record of what was seen then,
    /// never a licence to create (LIB-07 step 4 decides, under the lock).</summary>
    NotCreated = 0,

    /// <summary>Every LIB-08 check passed.</summary>
    Available = 1,

    /// <summary><c>library.lock</c> existed before this open but <c>library.sqlite3</c> does not. Never recreated silently.</summary>
    Missing = 2,

    /// <summary>A non-empty <c>library.sqlite3-journal</c> without a usable main file. Never created beside; never deleted.</summary>
    LeftoverFiles = 3,

    /// <summary>Path blocked, I/O error, access denied, an unexpected SQLite engine, or restart required (Faulted).</summary>
    Unavailable = 4,

    /// <summary>The writer lock is held by another process. No connection is opened and no member is touched.</summary>
    InUse = 5,

    /// <summary><c>user_version</c> is newer than supported. No access at all; never downgraded or modified.</summary>
    Incompatible = 6,

    /// <summary>Wrong magic or application id, a WAL header, an unexpected schema, or <c>-wal</c>/<c>-shm</c> files present.</summary>
    NotALibrary = 7,

    /// <summary><c>SQLITE_CORRUPT</c>/<c>SQLITE_NOTADB</c> confirmed by <c>quick_check</c>, or committed snapshots not Published.</summary>
    Damaged = 8,
}

/// <summary>Why a state was derived, machine-readable (tests and UI help); the words are the UI's.</summary>
internal static class LibraryReason
{
    internal const string None = "";
    internal const string PathBlocked = "path-blocked";
    internal const string UnexpectedEngine = "unexpected-sqlite-engine";
    internal const string IoError = "io-error";
    internal const string AccessDenied = "access-denied";
    internal const string RestartRequired = "restart-required";
    internal const string LockedByAnotherProcess = "locked-by-another-process";
    internal const string WalOrShmPresent = "wal-or-shm-present";
    internal const string ForeignFile = "foreign-file";
    internal const string TooShort = "file-too-short";
    internal const string WalHeader = "wal-format-header";
    internal const string WrongApplicationId = "wrong-application-id";
    internal const string UnsupportedUserVersion = "unsupported-user-version";
    internal const string NewerSchema = "newer-schema";
    internal const string SchemaFingerprint = "schema-fingerprint-mismatch";
    internal const string NotPublishedSnapshot = "committed-snapshot-not-published";
    internal const string Corrupt = "sqlite-corrupt";
    internal const string LockFileMissing = "lock-file-missing";
    internal const string DirectoryMissing = "directory-missing";
    internal const string MainFileMissingLockExisted = "main-file-missing-lock-existed";
    internal const string JournalWithoutMain = "journal-without-main-file";
    internal const string Busy = "busy";
    internal const string DiskFull = "disk-full";
    internal const string ReparsePoint = "reparse-point";
    internal const string Uninitialised = "uninitialised-database";
    internal const string JournalBesideUninitialised = "journal-beside-uninitialised-database";
}

/// <summary>What a Library open or creation found.</summary>
/// <param name="State">The derived state (§6.8).</param>
/// <param name="Reason">A machine-readable reason (see <see cref="LibraryReason"/>).</param>
/// <param name="Message">A plain-language sentence naming the situation, in this layer's own words; the UI words it for users.</param>
/// <param name="RecoveryPending">Available, but T-RECOVER did not complete: browsing works, saving is disabled until a later open succeeds.</param>
/// <param name="RestartRequired">The interlock is Faulted: nothing is available in this process any more (OBS-13).</param>
/// <param name="Directory">The Library directory this status is about.</param>
internal sealed record LibraryStatus(LibraryState State, string Reason, string Message, bool RecoveryPending, bool RestartRequired, string Directory)
{
    /// <summary>True only when reads may be served and saving is possible (Available and no recovery pending).</summary>
    internal bool CanSave => State == LibraryState.Available && !RecoveryPending && !RestartRequired;

    /// <summary>True when published snapshots may be read: Available (recovery pending included).</summary>
    internal bool CanRead => State == LibraryState.Available && !RestartRequired;

    internal static LibraryStatus Of(LibraryState state, string reason, string message, string directory) =>
        new(state, reason, message, RecoveryPending: false, RestartRequired: false, directory);
}

/// <summary>Data read from the Library that is outside what this version can have written: an unknown stable code, a count that
/// does not fit, a malformed value. The Library is untrusted input (SEC-15): this is a data error, never a crash.</summary>
internal sealed class LibraryDataException(string message) : Exception(message);
