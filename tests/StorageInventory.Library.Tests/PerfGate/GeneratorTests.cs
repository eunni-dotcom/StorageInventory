using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>
/// The objective generator (§15.4) is deterministic: every machine produces the same names. The port in ObjGenerator.cs was taken
/// literally from the reference (<c>ObjSnapshot</c> and the design-review families in docs/evidence/c4-design-review/harness.patch); these
/// tests pin its output (a digest of the first 1,000 names of a cell and of vocabulary slices), so any change to a name is a test
/// failure and must come with GateConstants.GeneratorVersion.
/// </summary>
public static class GeneratorTests
{
    private static string Digest(IEnumerable<string> names)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var n in names) { hash.AppendData(Encoding.UTF8.GetBytes(n)); hash.AppendData([0]); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24];
    }

    /// <summary>The names of the first folders of a snapshot, in folder and emission order, up to <paramref name="count"/>.</summary>
    private static List<string> FirstNames(ISnapshotRowSource rows, int count)
    {
        var names = new List<string>();
        for (var i = 0; i < rows.FolderCount && names.Count < count; i++)
            foreach (var f in rows.FilesOfFolder(i))
            {
                names.Add(Utf16.ToString(f.Name));
                if (names.Count == count) break;
            }
        return names;
    }

    private static ObjSnapshot Obj(string parameters, int folders = 12_500, int treeSeed = 4, int vocabSeed = 4, int? shared = null, int layer = 0) =>
        new(folders, treeSeed, ObjParams.Parse(parameters), vocabSeed, shared, layer);

    [Test]
    public static void ObjectiveNamesAreDeterministic()
    {
        var a = FirstNames(Obj("d=0.25;model=system"), 1_000);
        var b = FirstNames(Obj("d=0.25;model=system"), 1_000);
        Assert.Equal(1_000, a.Count, "names");
        Assert.SequenceEqual(a, b, "two instances");
        var c = FirstNames(Obj("d=0.25;model=system", vocabSeed: 1), 1_000);
        Assert.True(!a.SequenceEqual(c), "another vocabulary seed gives other names");
    }

    /// <summary>Compares every pin of a test and reports all that differ at once (a changed generator needs GateConstants.GeneratorVersion bumped and the pins re-derived).</summary>
    private sealed class Pins
    {
        private readonly List<string> _wrong = [];

        internal void Check(string what, string expected, string actual)
        {
            if (expected != actual) _wrong.Add($"{what}: expected {expected}, was {actual}");
        }

        internal void Done() => Assert.True(_wrong.Count == 0, "a generated name changed (bump GateConstants.GeneratorVersion): " + string.Join("; ", _wrong));
    }

    [Test]
    public static void PinnedFirstThousandNames()
    {
        var pins = new Pins();
        void Pin(string what, string expected, string actual) => pins.Check(what, expected, actual);
        Pin("F system d=25%", "b0aa9f172d9d64a7269f9537", Digest(FirstNames(Obj("d=0.25;model=system"), 1_000)));
        Pin("F data d=60%", "92d3f9229a8dfb6c6e8d6fb6", Digest(FirstNames(Obj("d=0.6;model=data"), 1_000)));
        Pin("source B (shares a quarter of its vocabulary)", "c8f7b4058cc1439cbfe718f1", Digest(FirstNames(Obj("d=0.25;model=system", treeSeed: 2, vocabSeed: 2, shared: 4), 1_000)));
        Pin("re-scan layer 4", "e7327047714cde8b38a8e20a", Digest(FirstNames(Obj("d=0.25;model=system;rho=0.01;delta=0.005;alpha=0.005", treeSeed: 1, vocabSeed: 1, layer: 4), 1_000)));
        Pin("N1 append", "b7c3f4ffcab703364c3a521f", Digest(FirstNames(new DrSnapshot(12_500, 4, "target", "append"), 1_000)));
        Pin("N2 hash", "1ba4850b9a652ffedac4a790", Digest(FirstNames(new DrSnapshot(12_500, 4, "target", "hash"), 1_000)));
        Pin("N3 mixed", "7192c4695476e99d29f0e0c6", Digest(FirstNames(new DrSnapshot(12_500, 4, "target", "mixed"), 1_000)));
        Pin("N2 re-scan", "a1979302807e4ceadbf2e767", Digest(FirstNames(new DrSnapshot(12_500, 1, "target", "rescan", churn: 0.01, churnSeed: 4), 1_000)));
        pins.Done();
    }

    [Test]
    public static void PinnedVocabularySlices()
    {
        // ObjNames.Name is the generator's atom: a vocabulary entry of a seed under a model
        var pins = new Pins();
        void Pin(string what, string expected, string actual) => pins.Check(what, expected, actual);
        Pin("system seed 1, entries 0..9999", "beb827bb99ff6d89268983c4", Digest(Enumerable.Range(0, 10_000).Select(v => ObjNames.Name(v, 1, "system"))));
        Pin("data seed 4, entries 0..9999", "6bba75ed42b75b87f4e1d282", Digest(Enumerable.Range(0, 10_000).Select(v => ObjNames.Name(v, 4, "data"))));
        Pin("system seed 4, entries 1,000,000..1,009,999", "7e65a16838a66cc205ab9188", Digest(Enumerable.Range(1_000_000, 10_000).Select(v => ObjNames.Name(v, 4, "system"))));
        pins.Done();
    }

    [Test]
    public static void RealisedStatisticsFollowTheParameters()
    {
        // F(n, d, m): V = round(d x n) entries, every one occurring (the base occurs once per entry), so the realised distinct share IS d
        var rows = Obj("d=0.25;model=system", folders: 12_500);
        var stats = rows.Describe();
        Assert.Equal(rows.Files, stats.Files, "files");
        Assert.Equal(rows.Vocabulary, stats.Distinct, "every vocabulary entry occurs");
        Assert.True(Math.Abs((double)stats.Distinct / stats.Files - 0.25) < 0.001, "distinct share is d");
        Assert.True(stats.Once * 100 / stats.Distinct >= 40, "many names are seen once (a skewed repeat distribution)");
        Assert.Equal(0L, stats.Renamed + stats.Deleted + stats.Added, "a first save has no churn");
        Assert.True(stats.MeanLength > 15 && stats.MeanLength < 30, "mean length near the census's 20.8: " + stats.MeanLength.ToString(CultureInfo.InvariantCulture));
        var data = Obj("d=0.6;model=data").Describe();
        Assert.True(Math.Abs((double)data.Distinct / data.Files - 0.6) < 0.001, "data model d");
        Assert.True(data.MeanLength > 25, "the data model's names are longer (census mean 34.6): " + data.MeanLength.ToString(CultureInfo.InvariantCulture));
        // a re-scan renames a fraction rho of the files to names new to the source, deletes delta, and adds alpha
        var rescan = Obj("d=0.25;model=system;rho=0.01;delta=0.005;alpha=0.005", treeSeed: 1, vocabSeed: 1, layer: 4).Describe();
        var baseFiles = Obj("d=0.25;model=system", treeSeed: 1, vocabSeed: 1).Describe().Files;
        Assert.True(Math.Abs(rescan.Renamed - 0.01 * baseFiles) < 0.2 * 0.01 * baseFiles, $"renamed {rescan.Renamed} of {baseFiles}");
        Assert.True(Math.Abs(rescan.Deleted - 0.005 * baseFiles) < 0.3 * 0.005 * baseFiles, $"deleted {rescan.Deleted}");
        Assert.True(Math.Abs(rescan.Added - 0.005 * baseFiles) < 0.3 * 0.005 * baseFiles, $"added {rescan.Added}");
    }

    [Test]
    public static void ReferenceStatisticsAtTwoMillion()
    {
        // the C4 design repair recorded the realised statistics the REFERENCE generator produced at 2M files (repair-results/*.jsonl,
        // "generated ... files, ... distinct names, mean length ..."): the port reproduces them exactly, which ties its names to the reference
        var first = GateRunner.BuildTarget(GateMatrix.Find("F-2M-25-system")).Describe();
        Assert.Equal(1_997_023L, first.Files, "F(2M, 25%, system) files");
        Assert.Equal(499_256L, first.Distinct, "F(2M, 25%, system) distinct names");
        Assert.Equal("21.76", first.MeanLength.ToString("0.00", CultureInfo.InvariantCulture), "mean length");
        Assert.Equal("56.6", (100.0 * first.NamesSeenOnce / first.Distinct).ToString("0.0", CultureInfo.InvariantCulture), "names seen once, % of distinct");
        Assert.Equal("53.7", (100.0 * first.Top1ShareOfFiles).ToString("0.0", CultureInfo.InvariantCulture), "files in the top 1% of names");
        var rescan = GateRunner.BuildTarget(GateMatrix.Find("R-2M-25-system-1-05-05")).Describe();
        Assert.Equal(2_001_522L, rescan.Files, "R(2M, 25%, system; 1%, 0.5%, 0.5%) files");
        Assert.Equal(516_090L, rescan.Distinct, "distinct names");
        Assert.Equal(500_427L, rescan.Vocabulary, "base vocabulary");
        Assert.Equal((20_016L, 10_086L, 9_901L), (rescan.Renamed, rescan.Deleted, rescan.Added), "renamed, deleted, added");
    }

    [Test]
    public static void FolderNamesRepeatAndNoFolderRepeatsAName()
    {
        var rows = Obj("d=0.25;model=system");
        var folders = rows.Folders().Take(5_000).ToList();
        Assert.Equal("dir1", Utf16.ToString(folders[1].Name), "folder names are dir<i mod 997>");
        Assert.Equal("dir0", Utf16.ToString(folders[997].Name), "dir<997 mod 997>");
        for (var i = 0; i < 2_000; i++)
        {
            var names = rows.FilesOfFolder(i).Select(f => Utf16.ToString(f.Name)).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count(), "folder " + i + " holds no name twice");
        }
    }

    [Test]
    public static void ParametersAreClosedAndCanonical()
    {
        Assert.Throws<ArgumentException>(() => ObjParams.Parse("d=0.25;modle=system"));
        Assert.Throws<ArgumentException>(() => ObjParams.Parse("d=0.25;model=mixed"));
        var p = ObjParams.Parse("d=0.25;model=data;rho=0.01");
        Assert.Equal(2.558, p.Beta, "the data model's repeat skew");
        Assert.Equal("d=0.25;model=data;beta=2.558;omega=0.25", ObjParams.PrefillPart("model=data;d=0.25;rho=0.01"), "the prefill part names the vocabulary only");
    }

    [Test]
    public static void FamiliesHaveTheirShapes()
    {
        // N1: the unique half sorts after every earlier name; N2: the unique half is hash-spread; N3: a realistic mixture
        var append = FirstNames(new DrSnapshot(2_000, 4, "target", "append"), 500);
        Assert.True(append.Any(n => n.StartsWith("file_target_", StringComparison.Ordinal)) && append.Any(n => n.StartsWith("common_", StringComparison.Ordinal)), "N1's two halves");
        var hash = FirstNames(new DrSnapshot(2_000, 4, "target", "hash"), 500);
        Assert.True(hash.Any(n => n.StartsWith("img", StringComparison.Ordinal)) && hash.Any(n => n.StartsWith("common_", StringComparison.Ordinal)), "N2's two halves");
        var mixed = FirstNames(new DrSnapshot(2_000, 4, "target", "mixed"), 500);
        Assert.True(mixed.Distinct().Count() > 100, "N3 is a mixture");
        var rescan = new DrSnapshot(12_500, 1, "target", "rescan", churn: 0.01, churnSeed: 4);
        Assert.True(rescan.CountChurned() > 0, "the N2 re-scan renames files");
        Assert.Throws<ArgumentException>(() => FirstNames(new DrSnapshot(100, 4, "target", "real"), 10));
    }
}
