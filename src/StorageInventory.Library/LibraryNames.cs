namespace StorageInventory.Library;

/// <summary>The constants of the Library: the owned file names (LIB-02, §6.3), the application id and schema version, and the
/// pinned SQLite engine (§5.5, BLD-15). Nothing in the Library directory outside this set is ever created, written or deleted.</summary>
internal static class LibraryNames
{
    /// <summary>The cross-process writer lock (CONC-01). 0 bytes, never written, renamed or deleted.</summary>
    internal const string LockFile = "library.lock";

    /// <summary>The database. Created by <c>LibraryStore</c> with <c>FileMode.CreateNew</c>; SQLite never creates it.</summary>
    internal const string MainFile = "library.sqlite3";

    /// <summary>SQLite's rollback journal (journal_mode TRUNCATE): 0 bytes at rest, non-empty only during a write transaction or
    /// after a crash, when it is essential recovery material.</summary>
    internal const string JournalFile = "library.sqlite3-journal";

    /// <summary>Never present: their presence means another program opened the file (LIB-08 step 3).</summary>
    internal const string WalFile = "library.sqlite3-wal";

    /// <summary>Never present (see <see cref="WalFile"/>).</summary>
    internal const string ShmFile = "library.sqlite3-shm";

    /// <summary>The set-aside stem prefix: <c>library.damaged-&lt;yyyyMMdd_HHmmss&gt;-&lt;6hex&gt;</c> (LIB-13).</summary>
    internal const string QuarantinePrefix = "library.damaged-";

    internal const string QuarantineMainSuffix = ".sqlite3";
    internal const string QuarantineJournalSuffix = ".sqlite3-journal";
    internal const string QuarantineWalSuffix = ".sqlite3-wal";
    internal const string QuarantineShmSuffix = ".sqlite3-shm";

    /// <summary><c>PRAGMA application_id</c>: 0x53494E56, "SINV" (§5.8).</summary>
    internal const int ApplicationId = 0x53494E56;

    /// <summary><c>PRAGMA user_version</c>: the schema version. This release writes and opens schema 1 only (§9.6).</summary>
    internal const int SchemaVersion = 1;

    /// <summary>The SQLite version <c>e_sqlite3.dll</c> must report, exactly, at every open (BLD-15). Not a lower bound.</summary>
    internal const string PinnedSqliteVersion = "3.53.3";

    /// <summary>The source id it must report, exactly (§5.5).</summary>
    internal const string PinnedSqliteSourceId = "2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62";

    /// <summary>The app version recorded in <c>library_info</c> and on every snapshot. v1.1 is under development; C12 sets 1.1.0.</summary>
    internal const string AppVersion = "1.1.0-dev";

    internal static bool IsWalOrShm(string fileName) =>
        string.Equals(fileName, WalFile, StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, ShmFile, StringComparison.OrdinalIgnoreCase);
}
