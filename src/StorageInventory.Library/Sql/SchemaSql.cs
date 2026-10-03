namespace StorageInventory.Library;

/// <summary>
/// The schema-1 DDL of §9.2, as constant text (A-05: SQL command text lives only in <c>Sql/*.cs</c>). The statements are
/// executed in this order inside T-CREATE, which is one <c>BEGIN IMMEDIATE</c> transaction with <c>synchronous = FULL</c>.
/// Their text is frozen: <see cref="LibrarySchema.Fingerprint"/> hashes what SQLite stores for them, and TEST-S1 pins that hash.
/// <para>Foreign keys are declared on the catalogue and dictionary tables only. They are deliberately NOT declared on
/// <c>file_obs</c>, <c>folder_obs</c>, <c>scan_error</c> and <c>snapshot_extension_total</c>: Q-07 measured the cost of declaring
/// them (docs/v1.1-c4-implementation-evidence.md) and integrity never depends on them (§9.5, verified inside T-IMPORT).</para>
/// </summary>
internal static class SchemaSql
{
    internal const string SetApplicationId = "PRAGMA application_id = 1397313110";

    internal const string SetUserVersion = "PRAGMA user_version = 1";

    internal const string CreateLibraryInfo = """
        CREATE TABLE library_info (
          singleton               INTEGER PRIMARY KEY CHECK (singleton = 1),
          library_id              TEXT    NOT NULL,
          created_utc             INTEGER NOT NULL,
          created_app_version     TEXT    NOT NULL,
          last_opened_app_version TEXT    NOT NULL,
          next_snapshot_id        INTEGER NOT NULL CHECK (next_snapshot_id >= 1)
        ) STRICT
        """;

    internal const string CreateVolume = """
        CREATE TABLE volume (
          volume_id            INTEGER PRIMARY KEY,
          fs_type              TEXT    NOT NULL,
          serial64             INTEGER,
          serial32             INTEGER,
          confidence           INTEGER NOT NULL,
          display_name         TEXT    NOT NULL,
          last_label           BLOB,
          last_capacity_bytes  INTEGER,
          first_seen_utc       INTEGER NOT NULL,
          last_seen_utc        INTEGER NOT NULL
        ) STRICT
        """;

    internal const string CreateVolumeBySerial = "CREATE INDEX volume_by_serial ON volume (fs_type, serial64, serial32)";

    internal const string CreateSource = """
        CREATE TABLE source (
          source_id            INTEGER PRIMARY KEY,
          kind                 INTEGER NOT NULL,
          volume_id            INTEGER REFERENCES volume (volume_id),
          network_root         BLOB,
          network_root_key     BLOB,
          root_in_volume       BLOB    NOT NULL,
          identity_confidence  INTEGER NOT NULL,
          identity_basis       INTEGER NOT NULL,
          display_name         TEXT    NOT NULL,
          created_utc          INTEGER NOT NULL,
          CHECK ((kind = 1 AND volume_id IS NOT NULL AND network_root_key IS NULL)
              OR (kind = 2 AND volume_id IS NULL     AND network_root_key IS NOT NULL))
        ) STRICT
        """;

    internal const string CreateSourceLocalKey = "CREATE UNIQUE INDEX source_local_key ON source (volume_id, root_in_volume) WHERE kind = 1";

    internal const string CreateSourceNetworkKey = "CREATE UNIQUE INDEX source_network_key ON source (network_root_key, root_in_volume) WHERE kind = 2";

    internal const string CreateScanAttempt = """
        CREATE TABLE scan_attempt (
          attempt_id           INTEGER PRIMARY KEY,
          session_token        BLOB    NOT NULL,
          capture_token        BLOB    NOT NULL,
          source_id            INTEGER REFERENCES source (source_id),
          root_path_as_entered BLOB    NOT NULL,
          report_folder        BLOB,
          run_id               TEXT,
          started_utc          INTEGER NOT NULL,
          ended_utc            INTEGER,
          outcome              INTEGER NOT NULL,
          failure_kind         INTEGER,
          message              TEXT
        ) STRICT
        """;

    internal const string CreateScanAttemptBySource = "CREATE INDEX scan_attempt_by_source ON scan_attempt (source_id, attempt_id)";

    internal const string CreateSnapshot = """
        CREATE TABLE snapshot (
          snapshot_id                INTEGER PRIMARY KEY,
          source_id                  INTEGER NOT NULL REFERENCES source (source_id),
          attempt_id                 INTEGER NOT NULL UNIQUE REFERENCES scan_attempt (attempt_id),
          state                      INTEGER NOT NULL CHECK (state IN (1, 2, 3)),
          completeness               INTEGER NOT NULL CHECK (completeness IN (0, 1)),
          run_id                     TEXT    NOT NULL,
          started_utc                INTEGER NOT NULL,
          finished_utc               INTEGER NOT NULL,
          published_utc              INTEGER,
          root_path_as_entered       BLOB    NOT NULL,
          canonical_root             BLOB    NOT NULL,
          mount_point                BLOB,
          volume_label               BLOB,
          capacity_bytes             INTEGER,
          free_bytes                 INTEGER,
          root_dir_file_id           BLOB,
          identity_confidence        INTEGER NOT NULL,
          identity_basis             INTEGER NOT NULL,
          identity_note              TEXT,
          capture_kind               INTEGER NOT NULL CHECK (capture_kind IN (1, 2)),
          fs_name                    TEXT,
          library_inside_source      INTEGER NOT NULL CHECK (library_inside_source IN (0, 1, 2)),
          library_rel_path           BLOB,
          app_version                TEXT    NOT NULL,
          scanner_contract           INTEGER NOT NULL,
          spool_format               INTEGER NOT NULL,
          schema_version             INTEGER NOT NULL,
          sqlite_version             TEXT    NOT NULL,
          files INTEGER NOT NULL, folders INTEGER NOT NULL, bytes INTEGER NOT NULL, scan_errors INTEGER NOT NULL,
          reparse_points_skipped INTEGER NOT NULL, file_reparse_points INTEGER NOT NULL,
          locally_incomplete_folders INTEGER NOT NULL, affected_ancestor_folders INTEGER NOT NULL,
          CHECK (state <> 2 OR published_utc IS NOT NULL)
        ) STRICT
        """;

    internal const string CreateSnapshotBySource = "CREATE INDEX snapshot_by_source ON snapshot (source_id, snapshot_id)";

    /// <summary>The per-source name dictionary (SCH-04, D-52): a name belongs to ONE source, and uniqueness is per (source, bytes). A
    /// source's names therefore occupy a key range of their own in the automatic unique index, so importing one source modifies no
    /// other source's dictionary pages and journals none of them (PERF-15 (a)). The automatic index keeps its name,
    /// <c>sqlite_autoindex_name_1</c>.</summary>
    internal const string CreateName = """
        CREATE TABLE name (
          name_id   INTEGER PRIMARY KEY,
          source_id INTEGER NOT NULL REFERENCES source (source_id),
          utf16     BLOB    NOT NULL,
          UNIQUE (source_id, utf16)
        ) STRICT
        """;

    internal const string CreateFolderPath = """
        CREATE TABLE folder_path (
          path_id        INTEGER PRIMARY KEY,
          source_id      INTEGER NOT NULL REFERENCES source (source_id),
          parent_path_id INTEGER REFERENCES folder_path (path_id),
          name_id        INTEGER NOT NULL REFERENCES name (name_id),
          depth          INTEGER NOT NULL
        ) STRICT
        """;

    internal const string CreateFolderPathChild = "CREATE UNIQUE INDEX folder_path_child ON folder_path (source_id, parent_path_id, name_id)";

    internal const string CreateFolderPathRoot = "CREATE UNIQUE INDEX folder_path_root ON folder_path (source_id) WHERE parent_path_id IS NULL";

    internal const string CreateFolderObs = """
        CREATE TABLE folder_obs (
          snapshot_id                  INTEGER NOT NULL,
          path_id                      INTEGER NOT NULL,
          discovery_index              INTEGER NOT NULL,
          status                       INTEGER NOT NULL,
          status_reason                INTEGER,
          subtree_complete             INTEGER NOT NULL,
          attributes                   INTEGER NOT NULL,
          created_utc                  INTEGER,
          modified_utc                 INTEGER,
          direct_bytes INTEGER NOT NULL, total_bytes INTEGER NOT NULL,
          direct_files INTEGER NOT NULL, total_files INTEGER NOT NULL,
          direct_subfolders INTEGER NOT NULL, total_subfolders INTEGER NOT NULL,
          largest_file_bytes           INTEGER,
          PRIMARY KEY (snapshot_id, path_id)
        ) STRICT, WITHOUT ROWID
        """;

    internal const string CreateFileObs = """
        CREATE TABLE file_obs (
          snapshot_id    INTEGER NOT NULL,
          folder_path_id INTEGER NOT NULL,
          name_id        INTEGER NOT NULL,
          seq            INTEGER NOT NULL,
          size_bytes     INTEGER NOT NULL,
          modified_utc   INTEGER,
          created_utc    INTEGER,
          accessed_utc   INTEGER,
          attributes     INTEGER NOT NULL,
          PRIMARY KEY (snapshot_id, folder_path_id, name_id)
        ) STRICT, WITHOUT ROWID
        """;

    internal const string CreateScanError = """
        CREATE TABLE scan_error (
          snapshot_id INTEGER NOT NULL,
          seq         INTEGER NOT NULL,
          rel_path    BLOB    NOT NULL,
          error_type  INTEGER NOT NULL,
          message     BLOB    NOT NULL,
          PRIMARY KEY (snapshot_id, seq)
        ) STRICT, WITHOUT ROWID
        """;

    internal const string CreateSnapshotExtensionTotal = """
        CREATE TABLE snapshot_extension_total (
          snapshot_id   INTEGER NOT NULL,
          extension_key BLOB    NOT NULL,
          files         INTEGER NOT NULL,
          bytes         INTEGER NOT NULL,
          PRIMARY KEY (snapshot_id, extension_key)
        ) STRICT, WITHOUT ROWID
        """;

    internal const string InsertLibraryInfo = """
        INSERT INTO library_info (singleton, library_id, created_utc, created_app_version, last_opened_app_version, next_snapshot_id)
        VALUES (1, $library_id, $created_utc, $app_version, $app_version, 1)
        """;
}
