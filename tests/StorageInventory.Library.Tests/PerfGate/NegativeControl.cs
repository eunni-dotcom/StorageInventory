using System.Data.Common;
using System.Text.Json;
using StorageInventory.Core;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>
/// The negative control of PERF-15 (a) (§15.4: "the same check on an import into a Library whose name index is keyed by the name
/// alone"; required once per TEST-P1 session; the checker MUST FAIL on it). Production cannot produce that layout (SCH-04 keys the
/// name index by (source_id, utf16)), so the control is built TEST-SIDE with REAL SQLite journaling: a Library from the production DDL
/// except that <c>name</c> is keyed by <c>UNIQUE (utf16)</c> alone, filled with the same generator's L2-like names for several sources
/// (source 2 sharing a quarter of its entries with the target's vocabulary), then an <c>BEGIN IMMEDIATE</c> transaction that inserts the
/// target source's names and observation rows (a new source, or the existing source 1), the LIVE journal copied before the transaction
/// ends, and a ROLLBACK. The record carries the same attribution fields as a run's, with <c>layout = "global"</c>, so the checker reads
/// everything from the record.
/// </summary>
internal static class NegativeControl
{
    private const string GlobalName = """
        CREATE TABLE name (
          name_id   INTEGER PRIMARY KEY,
          source_id INTEGER NOT NULL REFERENCES source (source_id),
          utf16     BLOB    NOT NULL,
          UNIQUE (utf16)
        ) STRICT
        """;

    internal static string[] Ddl() => SchemaSql.CreateAll.Select(sql => sql == SchemaSql.CreateName ? GlobalName : sql).ToArray();

    private static DbCommand Command(DbConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            command.Parameters.Add(p);
        }
        return command;
    }

    private static void Exec(DbConnection c, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(c, sql, parameters);
        command.ExecuteNonQuery();
    }

    /// <summary>The L2-like vocabulary of a source: names v in [0, count), source 2's and 3's sharing a quarter with seed 4 (the target's).</summary>
    private static string NameOf(long v, int source, string model) =>
        source is 2 or 3 && ObjNames.Unit(ObjNames.Mix((ulong)v, (ulong)source, 0x0E6AUL)) < 0.25 ? ObjNames.Name(v, 4, model) : ObjNames.Name(v, source == 3 ? 1 : source, model);

    /// <summary>Builds the control of one variant ("new" or "existing" source) into <paramref name="outDir"/> and returns its record.</summary>
    internal static string Build(string outDir, string variant, string label, int namesPerSource, int targetNames, string model = "system")
    {
        if (variant is not ("new" or "existing")) throw new ArgumentException("variant new or existing");
        Directory.CreateDirectory(outDir);
        var scratch = Path.Combine(GateSupport.BenchRoot(), "SI-Gate-Control", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(scratch);
        try
        {
            var db = Path.Combine(scratch, "control.sqlite3");
            File.WriteAllBytes(db, []);
            LibraryDatabase.EnsureProvider();
            var ids = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var c = RawSqlite.OpenReadWrite(db))
            {
                Exec(c, SchemaSql.SetApplicationId);
                Exec(c, SchemaSql.SetUserVersion);
                Exec(c, "BEGIN");
                foreach (var ddl in Ddl()) Exec(c, ddl);
                Exec(c, "INSERT INTO volume (volume_id, fs_type, serial64, serial32, confidence, display_name, last_label, last_capacity_bytes, first_seen_utc, last_seen_utc) VALUES (1, 'NTFS', 1, 1, 1, 'Data', NULL, 1000, 0, 0)");
                for (var s = 1; s <= 3; s++)
                {
                    AddSource(c, s);
                    AddSnapshot(c, s, state: 2);
                    var root = AddFolder(c, s, ids, "dir0");
                    for (long v = 0; v < namesPerSource; v++) AddFile(c, s, root, ids, NameOf(v, s, model), v);
                }
                Exec(c, "COMMIT");
            }

            // the pre-import copy: after the last commit, with every connection closed; its length and SHA-256 are recorded as it is made
            var copyName = GateSupport.Slug(label) + ".before.sqlite3";
            var (copyLength, copySha) = GateSupport.CopyFlushHash(db, Path.Combine(outDir, copyName));

            // the import: a real write transaction (the journal mode and synchronous setting of the Library's writer), its live journal copied before it ends
            var journalPath = db + "-journal";
            var journalCopyName = GateSupport.Slug(label) + ".precommit.journal";
            long journalBeforeCommit, journalCopyLength;
            using (var c = RawSqlite.OpenReadWrite(db))
            {
                Exec(c, "PRAGMA journal_mode = TRUNCATE");
                Exec(c, "PRAGMA synchronous = FULL");
                Exec(c, "PRAGMA cache_size = -65536");
                Exec(c, "BEGIN IMMEDIATE");
                var target = variant == "new" ? 4 : 1;
                if (variant == "new") AddSource(c, 4);
                AddSnapshot(c, target, state: 1, snapshotId: 4);
                var root = variant == "new" ? AddFolder(c, 4, ids, "dir0") : 1L;
                // names new to the Library go into the one Library-wide dictionary; names it already holds are reused
                for (long v = 0; v < targetNames; v++) AddFile(c, target, root, ids, ObjNames.Name(v, 4, model), v, snapshotId: 4);
                journalBeforeCommit = GateSupport.HandleLength(journalPath);
                using (var src = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = new FileStream(Path.Combine(outDir, journalCopyName), FileMode.Create, FileAccess.Write))
                {
                    src.CopyTo(dst);
                    journalCopyLength = dst.Length;
                }
                Exec(c, "ROLLBACK");
            }
            var record = new
            {
                schema = GateConstants.RecordSchema,
                kind = "control",
                label,
                control = new { variant, layout = "global", namesPerSource, targetNames, model, engine = "production DDL with name keyed by UNIQUE (utf16)" },
                attribution = new AttributionRecord(new TargetRecord(variant == "new" ? "new" : "existing", variant == "new" ? null : 1), "global", journalBeforeCommit, journalCopyName, journalCopyLength, copyName, copyLength, copySha),
            };
            return JsonSerializer.Serialize(record, RecordJson.Options);
        }
        finally
        {
            GateSupport.TryDelete(scratch);
        }
    }

    private static void AddSource(DbConnection c, int s) =>
        Exec(c, "INSERT INTO source (source_id, kind, volume_id, network_root, network_root_key, root_in_volume, identity_confidence, identity_basis, display_name, created_utc) VALUES ($s, 1, 1, NULL, NULL, $root, 1, 1, 'src', 0)",
            ("$s", (long)s), ("$root", Utf16.ToBytes(@"\" + s)));

    private static void AddSnapshot(DbConnection c, int source, int state, long? snapshotId = null)
    {
        var id = snapshotId ?? source;
        Exec(c, "INSERT INTO scan_attempt (attempt_id, session_token, capture_token, source_id, root_path_as_entered, report_folder, run_id, started_utc, ended_utc, outcome, failure_kind, message) VALUES ($id, x'00', x'00', $s, x'00', NULL, 'r', 0, 0, 1, NULL, NULL)",
            ("$id", id), ("$s", (long)source));
        Exec(c, """
            INSERT INTO snapshot (snapshot_id, source_id, attempt_id, state, completeness, run_id, started_utc, finished_utc, published_utc, root_path_as_entered, canonical_root, mount_point, volume_label, capacity_bytes, free_bytes,
              root_dir_file_id, identity_confidence, identity_basis, identity_note, capture_kind, fs_name, library_inside_source, library_rel_path, app_version, scanner_contract, spool_format, schema_version, sqlite_version,
              files, folders, bytes, scan_errors, reparse_points_skipped, file_reparse_points, locally_incomplete_folders, affected_ancestor_folders)
            VALUES ($id, $s, $id, $state, 1, 'r', 0, 0, $published, x'00', x'00', NULL, NULL, NULL, NULL, NULL, 1, 1, NULL, 1, 'NTFS', 0, NULL, '1', 1, 1, 1, '3', 0, 0, 0, 0, 0, 0, 0, 0)
            """, ("$id", id), ("$s", (long)source), ("$state", (long)state), ("$published", state == 2 ? 0L : null));
    }

    private static long NameId(DbConnection c, Dictionary<string, long> ids, string name, int source)
    {
        if (ids.TryGetValue(name, out var id)) return id;
        id = ids.Count + 1;
        ids[name] = id;
        Exec(c, "INSERT INTO name (name_id, source_id, utf16) VALUES ($id, $s, $u)", ("$id", id), ("$s", (long)source), ("$u", Utf16.ToBytes(name)));
        return id;
    }

    private static long AddFolder(DbConnection c, int source, Dictionary<string, long> ids, string name)
    {
        var nameId = NameId(c, ids, name, source);
        long pathId = source;   // one root folder per source: its path_id is the source id
        Exec(c, "INSERT INTO folder_path (path_id, source_id, parent_path_id, name_id, depth) VALUES ($p, $s, NULL, $n, 0)", ("$p", pathId), ("$s", (long)source), ("$n", nameId));
        return pathId;
    }

    private static void AddFile(DbConnection c, int source, long folder, Dictionary<string, long> ids, string name, long seq, long? snapshotId = null)
    {
        var nameId = NameId(c, ids, name, source);
        Exec(c, "INSERT INTO file_obs (snapshot_id, folder_path_id, name_id, seq, size_bytes, modified_utc, created_utc, accessed_utc, attributes) VALUES ($snap, $f, $n, $q, 1, NULL, NULL, NULL, 32)",
            ("$snap", snapshotId ?? source), ("$f", folder), ("$n", nameId), ("$q", seq));
    }
}
