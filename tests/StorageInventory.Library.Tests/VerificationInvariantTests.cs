using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// The §9.5 verification gate (SCH-06, IMP-05 c, LIFE-06; C4-M03): one negative test per invariant, each planting the specific
/// defect that the invariant exists to catch in an otherwise correct, committed snapshot (by editing the Library file behind the
/// product's back) and asserting that <see cref="SnapshotVerifier"/> names THAT invariant. A test that only showed "the verifier
/// returns something" would pass for the wrong reason: a verification SQL that always returned 0 lets a corrupt snapshot publish,
/// and these tests are what a mutant of any single invariant has to survive (the reviewer's R-10 and R-11 were two such mutants).
/// The fixture is <see cref="ScriptedSnapshot.Rich"/>: Partial, Unreadable and ReparsePointSkipped folders, an informational error
/// row, incomplete ancestors, and a second source whose own names exist beside the first source's (D-52: invariant 4 is per source).
/// </summary>
public static class VerificationInvariantTests
{
    private static string Hex(string text) => Convert.ToHexString(Utf16.ToBytes(text));

    /// <summary>The path id of a folder of source 1, as a scalar sub-select, found by its exact name.</summary>
    private static string Path(string folder, int source = 1) =>
        $"(SELECT p.path_id FROM folder_path p JOIN name n ON n.name_id = p.name_id WHERE p.source_id = {source} AND n.utf16 = X'{Hex(folder)}')";

    private static string Root(int source = 1) => $"(SELECT path_id FROM folder_path WHERE source_id = {source} AND parent_path_id IS NULL)";

    private static string NameOf(string name, int source) => $"(SELECT name_id FROM name WHERE source_id = {source} AND utf16 = X'{Hex(name)}')";

    /// <summary>A Library with the rich snapshot (id 1, source 1) and a second source with its own snapshot (id 2, source 2).</summary>
    private static (World World, LibrarySession Session) TwoSources()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var first = ScriptedSnapshot.Rich();
        LibraryStateTests.ImportRows(session, first, first.Header("run-1"), "run-1", ScriptedSnapshot.NewSource(@"\", 0x1111));
        var second = ScriptedSnapshot.Rich("2");
        LibraryStateTests.ImportRows(session, second, second.Header("run-2"), "run-2", ScriptedSnapshot.NewSource(@"\Other", 0x2222));
        return (world, session);
    }

    private static string? Verify(LibrarySession session, long snapshotId = 1) => session.Read(r => SnapshotVerifier.VerifyPublished(r, snapshotId));

    /// <summary>Plants one defect and asserts that the verification names the expected invariant (and a keyword of its message).</summary>
    private static void Plants(string defect, string[] edits, string expected)
    {
        var (world, session) = TwoSources();
        Assert.Null(Verify(session, 1), defect + ": the baseline snapshot satisfies every invariant");
        Assert.Null(Verify(session, 2), defect + ": the other source's snapshot does too");
        RawSqlite.Execute(world.Main, ["PRAGMA foreign_keys = OFF", .. edits]);
        var message = Verify(session, 1);
        Assert.True(message is not null && message.Contains(expected, StringComparison.Ordinal), $"{defect}: expected a message containing <{expected}> but was <{message ?? "no violation"}>");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void The_rich_fixture_imports_verifies_and_stores_every_folder_status_and_error_row()
    {
        var (_, session) = TwoSources();
        Assert.Null(Verify(session, 1));
        Assert.Equal(1L, session.Read(r => r.Long("SELECT completeness FROM snapshot WHERE snapshot_id = 1")), "the scan was Incomplete");
        Assert.Equal(1L, session.Read(r => r.Long("SELECT count(*) FROM folder_obs WHERE snapshot_id = 1 AND status = 1")), "one Unreadable folder");
        Assert.Equal(1L, session.Read(r => r.Long("SELECT count(*) FROM folder_obs WHERE snapshot_id = 1 AND status = 2")), "one Partial folder");
        Assert.Equal(1L, session.Read(r => r.Long("SELECT count(*) FROM folder_obs WHERE snapshot_id = 1 AND status = 3")), "one ReparsePointSkipped folder");
        Assert.Equal(2L, session.Read(r => r.Long("SELECT count(*) FROM scan_error WHERE snapshot_id = 1")), "both error rows");
        Assert.Equal(1L, session.Read(r => r.Long("SELECT scan_errors FROM snapshot WHERE snapshot_id = 1")), "scan_errors counts the non-informational row only");
        Assert.Equal(7L, session.Read(r => r.Long("SELECT files FROM snapshot WHERE snapshot_id = 1")));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Invariant_1_one_root_row_with_discovery_index_0()
    {
        Plants("root discovery index", [$"UPDATE folder_obs SET discovery_index = 7 WHERE snapshot_id = 1 AND path_id = {Root()}"], "invariant 1");
        Plants("root row removed", [$"DELETE FROM folder_obs WHERE snapshot_id = 1 AND path_id = {Root()}"], "invariant 1");
    }

    [Test]
    public static void Invariant_2_every_folder_and_file_refers_to_a_folder_path_of_the_source_that_has_a_folder_row()
    {
        Plants("a folder row without a folder path", [$"UPDATE folder_obs SET path_id = 999999 WHERE snapshot_id = 1 AND path_id = {Path("docs")}"], "invariant 2: a folder row does not refer");
        Plants("a folder path of another source", [$"UPDATE folder_path SET source_id = 2 WHERE path_id = {Path("docs")}"], "invariant 2: a folder row does not refer to a folder path of this source");
        // the root's recorded child count is corrected too, so that only the orphaned files are left to find
        Plants("files stored in a folder that has no folder row",
            [$"DELETE FROM folder_obs WHERE snapshot_id = 1 AND path_id = {Path("docs")}", $"UPDATE folder_obs SET direct_subfolders = direct_subfolders - 1 WHERE snapshot_id = 1 AND path_id = {Root()}"],
            "invariant 2: files are stored in folder");
    }

    [Test]
    public static void Invariant_3_parent_closure_the_parent_is_listed_discovered_earlier_and_present()
    {
        Plants("parent row missing", [$"DELETE FROM folder_obs WHERE snapshot_id = 1 AND path_id = {Path("mixed")}"], "invariant 3");
        Plants("parent not listed (status Unreadable)", [$"UPDATE folder_obs SET status = 1 WHERE snapshot_id = 1 AND path_id = {Path("mixed")}"], "invariant 3");
        Plants("parent discovered later than its child", [$"UPDATE folder_obs SET discovery_index = 9 WHERE snapshot_id = 1 AND path_id = {Path("mixed")}"], "invariant 3");
    }

    [Test]
    public static void Invariant_4_name_closure_is_per_source()
    {
        // a name id that exists, but only for ANOTHER source: the library-wide rule would accept it, the per-source rule must not
        Plants("a file named by another source's dictionary row",
            [$"UPDATE file_obs SET name_id = {NameOf("docs2", 2)} WHERE snapshot_id = 1 AND folder_path_id = {Path("docs")} AND name_id = {NameOf("a.TXT", 1)}"],
            "invariant 4: a file name is not in the dictionary of this source");
        Plants("a folder named by another source's dictionary row",
            [$"UPDATE folder_path SET name_id = {NameOf("docs2", 2)} WHERE path_id = {Path("docs")}"],
            "invariant 4: a folder name is not in the dictionary of this source");
        // and the plain case: the name row is gone
        Plants("a file name row deleted", [$"DELETE FROM name WHERE source_id = 1 AND utf16 = X'{Hex("a.TXT")}'"], "invariant 4: a file name");
        Plants("a folder name row deleted", [$"DELETE FROM name WHERE source_id = 1 AND utf16 = X'{Hex("docs")}'"], "invariant 4: a folder name");
    }

    [Test]
    public static void Invariant_5_each_folders_direct_counts_equal_what_is_stored()
    {
        Plants("direct files", [$"UPDATE folder_obs SET direct_files = direct_files + 1 WHERE snapshot_id = 1 AND path_id = {Path("docs")}"], "invariant 5");
        Plants("direct bytes", [$"UPDATE folder_obs SET direct_bytes = direct_bytes + 1 WHERE snapshot_id = 1 AND path_id = {Path("docs")}"], "invariant 5");
        Plants("direct subfolders", [$"UPDATE folder_obs SET direct_subfolders = direct_subfolders + 1 WHERE snapshot_id = 1 AND path_id = {Path("mixed")}"], "invariant 5");
    }

    [Test]
    public static void Invariant_6_sealed_totals_and_the_roots_totals_match_the_data()
    {
        Plants("sealed file total", ["UPDATE snapshot SET files = files + 1 WHERE snapshot_id = 1"], "invariant 6: the data holds");
        Plants("sealed byte total", ["UPDATE snapshot SET bytes = bytes + 1 WHERE snapshot_id = 1"], "invariant 6: the data holds");
        Plants("sealed folder total", ["UPDATE snapshot SET folders = folders + 1 WHERE snapshot_id = 1"], "invariant 6: the data holds");
        Plants("the root's total bytes", [$"UPDATE folder_obs SET total_bytes = total_bytes + 1 WHERE snapshot_id = 1 AND path_id = {Root()}"], "invariant 6: the root totals");
        Plants("the root's total subfolders", [$"UPDATE folder_obs SET total_subfolders = total_subfolders + 1 WHERE snapshot_id = 1 AND path_id = {Root()}"], "invariant 6: the root totals");
    }

    [Test]
    public static void Invariant_7_completeness_mirrors_the_roots_subtree_complete()
    {
        Plants("completeness flipped", ["UPDATE snapshot SET completeness = 1 - completeness WHERE snapshot_id = 1"], "invariant 7");
        Plants("the root marked complete but the snapshot Incomplete", [$"UPDATE folder_obs SET subtree_complete = 1 WHERE snapshot_id = 1 AND path_id = {Root()}"], "invariant 7");
    }

    [Test]
    public static void Invariant_8_an_unlisted_folder_has_no_children_and_no_files()
    {
        // 'docs' is an ordinary folder with three files and consistent totals; calling it skipped must be caught by invariant 8 alone
        Plants("files in a folder recorded as skipped", [$"UPDATE folder_obs SET status = 3 WHERE snapshot_id = 1 AND path_id = {Path("docs")}"], "invariant 8");
        Plants("files in a folder recorded as unreadable", [$"UPDATE folder_obs SET status = 1, subtree_complete = 0 WHERE snapshot_id = 1 AND path_id = {Path("docs")}"], "invariant 8");
    }

    [Test]
    public static void Invariant_9_an_unreadable_or_partial_folder_and_all_its_ancestors_are_not_subtree_complete()
    {
        Plants("an unreadable folder marked subtree-complete", [$"UPDATE folder_obs SET subtree_complete = 1 WHERE snapshot_id = 1 AND path_id = {Path("locked")}"], "invariant 9: an unreadable or partial folder");
        Plants("a partial folder marked subtree-complete", [$"UPDATE folder_obs SET subtree_complete = 1 WHERE snapshot_id = 1 AND path_id = {Path("mixed")}"], "invariant 9: an unreadable or partial folder");
        // the root claims completeness (and the snapshot says Complete, so invariant 7 holds) although a child is incomplete
        Plants("an ancestor of an incomplete folder marked complete",
            ["UPDATE snapshot SET completeness = 0 WHERE snapshot_id = 1", $"UPDATE folder_obs SET subtree_complete = 1 WHERE snapshot_id = 1 AND path_id = {Root()}"],
            "invariant 9: an ancestor");
    }

    [Test]
    public static void Invariant_10_scan_errors_counts_the_non_informational_error_rows()
    {
        Plants("the sealed error count", ["UPDATE snapshot SET scan_errors = scan_errors + 1 WHERE snapshot_id = 1"], "invariant 10");
        Plants("an informational row counted as a real error", ["UPDATE scan_error SET error_type = 1 WHERE snapshot_id = 1 AND error_type = 8"], "invariant 10");
    }

    [Test]
    public static void Invariant_11_each_file_seq_and_each_discovery_index_appears_exactly_once()
    {
        Plants("a duplicate file seq", ["UPDATE file_obs SET seq = 0 WHERE snapshot_id = 1 AND seq = 1"], "invariant 11: file seq");
        Plants("a file seq out of range", ["UPDATE file_obs SET seq = 99 WHERE snapshot_id = 1 AND seq = 1"], "invariant 11: file seq");
        Plants("a duplicate folder discovery index",
            [$"UPDATE folder_obs SET discovery_index = (SELECT discovery_index FROM folder_obs WHERE snapshot_id = 1 AND path_id = {Path("locked")}) WHERE snapshot_id = 1 AND path_id = {Path("link")}"],
            "invariant 11: folder discovery_index");
    }

    [Test]
    public static void Invariant_12_the_extension_totals_sum_to_the_files_and_bytes()
    {
        Plants("extension file total", [$"UPDATE snapshot_extension_total SET files = files + 1 WHERE snapshot_id = 1 AND extension_key = X'{Hex(".txt")}'"], "invariant 12");
        Plants("extension byte total", [$"UPDATE snapshot_extension_total SET bytes = bytes + 1 WHERE snapshot_id = 1 AND extension_key = X'{Hex(".txt")}'"], "invariant 12");
    }

    [Test]
    public static void The_importer_runs_the_same_gate_before_publishing_a_defect_made_in_the_row_stream_rolls_back()
    {
        // not a raw edit: the defect is in what the stream declares, so the in-transaction verification (not a pre-check of the
        // stream) must refuse it, with the invariant named, and nothing may remain
        var world = World.Create();
        var session = world.CreatedSession();
        // two files of the same folder declare the same sequence number: invariant 11
        var folders = new[] { new ScriptedSnapshot.Folder("", -1), new ScriptedSnapshot.Folder("d", 0) };
        var rows = new DuplicateSeqSnapshot(new ScriptedSnapshot(folders, [new ScriptedSnapshot.File(1, "one", 1), new ScriptedSnapshot.File(1, "two", 2)]));
        var failure = Assert.Throws<ImportException>(() => LibraryStateTests.ImportRows(session, rows, rows.Inner.Header(), "run-1", ScriptedSnapshot.NewSource(@"\", 0x3333)));
        Assert.Equal(CaptureFailureKind.InvariantViolation, failure.Kind);
        Assert.Contains("invariant 11", failure.Message);
        Assert.Equal(0L, session.Read(r => r.Long("SELECT count(*) FROM snapshot")));
        Assert.Equal(0L, session.Read(r => r.Long("SELECT count(*) FROM name")), "the names interned before the refusal were rolled back with it");
        session.TestOnlyShutdown();
    }

    /// <summary>Wraps a scripted snapshot and gives its second file the first file's sequence number.</summary>
    private sealed class DuplicateSeqSnapshot(ScriptedSnapshot inner) : ISnapshotRowSource
    {
        internal ScriptedSnapshot Inner { get; } = inner;

        public int FolderCount => Inner.FolderCount;

        public IEnumerable<ImportFolder> Folders() => Inner.Folders();

        public IReadOnlyList<ImportFile> FilesOfFolder(int folderIndex) => [.. Inner.FilesOfFolder(folderIndex).Select(f => f with { Seq = 0 })];

        public IEnumerable<ImportError> Errors() => Inner.Errors();
    }
}
