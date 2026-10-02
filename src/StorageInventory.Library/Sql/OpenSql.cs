namespace StorageInventory.Library;

/// <summary>The constant SQL of connection set-up and Library open (LIB-08, §5.8, A-22): the engine assertion, the pragmas
/// and their read-backs, the open-time checks, T-RECOVER and the corruption confirmation. Every value reaches SQLite as a bound
/// parameter or is part of the constant text; nothing is concatenated (A-05).</summary>
internal static class OpenSql
{
    /// <summary>The first statement of every connection: reads no database page, so a hot journal is not touched by it (§5.8).</summary>
    internal const string SelectEngine = "SELECT sqlite_version(), sqlite_source_id()";

    // ---- the writer's pragmas (§5.8). journal_mode is the first statement that touches the database file. ----
    internal const string SetJournalMode = "PRAGMA journal_mode = TRUNCATE";
    internal const string SetSynchronous = "PRAGMA synchronous = FULL";
    internal const string SetLockingMode = "PRAGMA locking_mode = NORMAL";
    internal const string SetBusyTimeout = "PRAGMA busy_timeout = 5000";
    internal const string SetTempStore = "PRAGMA temp_store = MEMORY";
    internal const string SetForeignKeys = "PRAGMA foreign_keys = ON";
    internal const string SetTrustedSchema = "PRAGMA trusted_schema = OFF";
    internal const string SetImportCacheSize = "PRAGMA cache_size = -65536";

    // ---- read-backs: every setting is asserted by reading it back (A-22) ----
    internal const string GetJournalMode = "PRAGMA journal_mode";
    internal const string GetSynchronous = "PRAGMA synchronous";
    internal const string GetLockingMode = "PRAGMA locking_mode";
    internal const string GetBusyTimeout = "PRAGMA busy_timeout";
    internal const string GetTempStore = "PRAGMA temp_store";
    internal const string GetForeignKeys = "PRAGMA foreign_keys";
    internal const string GetTrustedSchema = "PRAGMA trusted_schema";
    internal const string GetCacheSize = "PRAGMA cache_size";
    internal const string GetApplicationId = "PRAGMA application_id";
    internal const string GetUserVersion = "PRAGMA user_version";

    // ---- LIB-08 steps 6 and 7 ----
    /// <summary>Every row of <c>sqlite_schema</c>: the schema fingerprint is computed over these (SEC-17). Unordered on purpose: the
    /// fingerprint sorts them in .NET, so the query needs no temporary B-tree even over a hostile database (A-24).</summary>
    internal const string SelectSchemaRows = "SELECT type, name, tbl_name, sql FROM sqlite_schema";

    /// <summary>Must be 0: every committed snapshot is Published (SCH-08 #0, D-47).</summary>
    internal const string CountUnpublishedSnapshots = "SELECT count(*) FROM snapshot WHERE state <> 2";

    // ---- T-RECOVER (CONC-08): only attempts left InProgress by ANOTHER session ----
    internal const string RecoverInterruptedAttempts =
        "UPDATE scan_attempt SET outcome = 8, ended_utc = $ended_utc WHERE outcome = 1 AND session_token <> $session_token";

    internal const string TouchLastOpened = "UPDATE library_info SET last_opened_app_version = $app_version WHERE singleton = 1";

    /// <summary>Run after an error that reports corruption, never at every open (§6.7).</summary>
    internal const string QuickCheck = "PRAGMA quick_check";
}
