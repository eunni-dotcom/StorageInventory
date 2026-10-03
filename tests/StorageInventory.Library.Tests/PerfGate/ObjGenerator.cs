using System.Globalization;
using System.Text;
using StorageInventory.Core;
using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.History.Library;

namespace StorageInventory.Library.Tests.PerfGate;

// Ported LITERALLY from docs/evidence/c4-design-review/harness.patch (the reference implementation named by §15.4): the objective
// generator (ObjParams, ObjNames, ObjSnapshot) and the C4 design review's name families (DrSnapshot: N1 "append", N2 "hash",
// N3 "mixed", and the N2 re-scan "rescan"). The names must stay byte-identical to the reference for the same parameters:
// GeneratorTests pins them. The real-vocabulary families of the reference ("real", "rescanreal") need a harvested names file and
// are not part of TEST-P1's matrix; they are not ported. Changing anything in this file changes a prefill's content, so
// GateConstants.GeneratorVersion must be bumped with it (the prefill cache key carries it).

/// <summary>The synthetic tree of <see cref="SyntheticSnapshot"/> (identical folders, sizes, times and totals) with a choice of file-name
/// distribution. Folder names stay "dir&lt;i % 997&gt;" in every family, so only the file-name dictionary differs between families.</summary>
internal sealed class DrSnapshot : ISnapshotRowSource
{
    private readonly int _folders;
    private readonly int _seed;
    private readonly int _churnSeed;
    private readonly string _family;
    private readonly double _churn;
    private readonly int[] _directFiles;
    private readonly long[] _directBytes;
    private readonly long[] _totalFiles;
    private readonly long[] _totalBytes;
    private readonly long[] _directSubfolders;
    private readonly long[] _totalSubfolders;
    private readonly long[] _seqStart;
    private readonly string _label;
    private const int Branching = 6;

    internal DrSnapshot(int folders, int seed, string label, string family, double churn = 0, int churnSeed = 4)
    {
        _folders = folders;
        _seed = seed;
        _label = label;
        _family = family;
        _churn = churn;
        _churnSeed = churnSeed;
        _directFiles = new int[folders];
        _directBytes = new long[folders];
        _totalFiles = new long[folders];
        _totalBytes = new long[folders];
        _directSubfolders = new long[folders];
        _totalSubfolders = new long[folders];
        _seqStart = new long[folders];
        long seq = 0;
        for (var i = 0; i < folders; i++)
        {
            var count = (int)((uint)Hash(i, 17, _seed) % 9u);
            _directFiles[i] = count;
            _seqStart[i] = seq;
            seq += count;
            long bytes = 0;
            for (var k = 0; k < count; k++) bytes += SizeOf(i, k);
            _directBytes[i] = bytes;
            _totalFiles[i] = count;
            _totalBytes[i] = bytes;
            if (i > 0) _directSubfolders[ParentOf(i)]++;
        }
        for (var i = folders - 1; i >= 1; i--)
        {
            var parent = ParentOf(i);
            _totalFiles[parent] += _totalFiles[i];
            _totalBytes[parent] += _totalBytes[i];
            _totalSubfolders[parent] += 1 + _totalSubfolders[i];
        }
        Files = seq;
        Bytes = _totalBytes[0];
    }

    internal long Files { get; }

    internal long Bytes { get; }

    public int FolderCount => _folders;

    private static int ParentOf(int index) => (index - 1) / Branching;

    private static int Hash(int a, int b, int seed) => unchecked((a * 73856093) ^ (b * 19349663) ^ (seed * 83492791));

    private long SizeOf(int folder, int k) => 1 + (uint)Hash(folder, k + 1000, _seed) % 100_000u;

    internal ImportSnapshotHeader Header(string runId) => new()
    {
        Completion = ScanCompletionState.Complete,
        RunId = runId,
        StartedUtcTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
        FinishedUtcTicks = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc).Ticks,
        RootPathAsEntered = Utf16.ToBytes(@"D:\Media"),
        CanonicalRoot = Utf16.ToBytes(@"D:\Media"),
        MountPoint = Utf16.ToBytes(@"D:\"),
        VolumeLabel = Utf16.ToBytes("Data"),
        CapacityBytes = 1_000_000_000,
        FreeBytes = 500_000_000,
        Confidence = IdentityConfidence.Strong,
        Basis = IdentityBasis.Evidence,
        CaptureKind = SourceKind.LocalVolume,
        FsName = "NTFS",
        Files = Files,
        Folders = _folders,
        Bytes = Bytes,
    };

    public IEnumerable<ImportFolder> Folders()
    {
        for (var i = 0; i < _folders; i++)
        {
            yield return new ImportFolder(i, i == 0 ? -1 : ParentOf(i), i == 0 ? [] : Utf16.ToBytes("dir" + (i % 997)), FolderScanStatus.Ok, null, true, 0x10,
                CreatedTicks: 637_000_000_000_000_000L + i, ModifiedTicks: 637_100_000_000_000_000L + i,
                _directBytes[i], _totalBytes[i], _directFiles[i], _totalFiles[i], _directSubfolders[i], _totalSubfolders[i],
                LargestFileBytes: _totalFiles[i] == 0 ? null : 100_000);
        }
    }

    public IReadOnlyList<ImportFile> FilesOfFolder(int folderIndex)
    {
        var count = _directFiles[folderIndex];
        if (count == 0) return [];
        var files = new ImportFile[count];
        var seen = count > 1 ? new HashSet<string>(StringComparer.Ordinal) : null;
        for (var k = 0; k < count; k++)
        {
            var name = FileName(folderIndex, k);
            // names are unique within a folder (the file_obs key); a rare in-folder collision gets a deterministic suffix
            if (seen is not null) { var n = name; var t = 1; while (!seen.Add(n)) n = name + " (" + t++ + ")"; name = n; }
            files[k] = new ImportFile(Utf16.ToBytes(name), _seqStart[folderIndex] + k, SizeOf(folderIndex, k), 637_100_000_000_000_000L + k, 637_000_000_000_000_000L, 637_200_000_000_000_000L, 0x20);
        }
        return files;
    }

    public IEnumerable<ImportError> Errors() => [];

    /// <summary>The number of files the churn of the "rescan" family renames (gate tooling addition: the realised churn count; it does not
    /// touch a name).</summary>
    internal long CountChurned()
    {
        long n = 0;
        for (var i = 0; i < _folders; i++)
            for (var k = 0; k < _directFiles[i]; k++)
                if (Churned(i, k)) n++;
        return n;
    }

    // ------------------------------------------------------------------------------------------------------------ name families

    private string FileName(int folder, int k) => _family switch
    {
        "append" => AppendName(folder, k, _label, _seed),
        "hash" => HashName(folder, k, _seed),
        "rescan" => Churned(folder, k) ? ChurnName(folder, k, _churnSeed) : HashName(folder, k, _seed),
        "mixed" => MixedName(folder, k, _seed),
        _ => throw new ArgumentException("family " + _family),
    };

    /// <summary>The C4 benchmark's names exactly (SyntheticSnapshot.FileName): unique names sort after every existing name.</summary>
    private static string AppendName(int folder, int k, string label, int seed)
    {
        var unique = (uint)Hash(folder, k, seed) % 2u == 0;
        var stem = unique ? $"file_{label}_{folder}_{k}" : $"common_{(uint)Hash(folder, k, seed) % 5000u}";
        return stem + ".ext" + (k % 7);
    }

    /// <summary>The reviewer's replay names exactly (C4 review §23): the unique half is hash-distributed over the key space.</summary>
    private static string HashName(int folder, int k, int seed)
    {
        var unique = (uint)Hash(folder, k, seed) % 2u == 0;
        var stem = unique
            ? "img" + unchecked((uint)(Hash(folder, k, seed) * 2654435761u) ^ (uint)(folder * 40503 + k * 9973 + seed * 7919)).ToString("x8", CultureInfo.InvariantCulture) + "_" + (folder * 31 + k + seed * 1000003).ToString("x", CultureInfo.InvariantCulture)
            : $"common_{(uint)Hash(folder, k, seed) % 5000u}";
        return stem + ".ext" + (k % 7);
    }

    /// <summary>A file renamed since the earlier snapshot: always a new name, spread over the existing "img" names.</summary>
    private static string ChurnName(int folder, int k, int seed) =>
        "img" + (Mix((ulong)folder, (ulong)k, (ulong)seed * 977) & 0xFFFFFFFFUL).ToString("x8", CultureInfo.InvariantCulture) + "_c" + folder.ToString("x", CultureInfo.InvariantCulture) + "_" + k.ToString(CultureInfo.InvariantCulture) + ".ext" + (k % 7);

    private bool Churned(int folder, int k) => Mix((ulong)folder, (ulong)k, 0xC4u) % 1_000_000UL < (ulong)(_churn * 1_000_000);

    private static ulong Mix(ulong a, ulong b, ulong c)
    {
        var x = a * 0x9E3779B97F4A7C15UL ^ (b + 0x632BE59BD9B4E019UL) * 0xBF58476D1CE4E5B9UL ^ (c + 0x94D049BB133111EBUL);
        x ^= x >> 31; x *= 0xD6E8FEB86659FD93UL; x ^= x >> 29; x *= 0x94D049BB133111EBUL; x ^= x >> 32;
        return x;
    }

    private static readonly string[] Syllables = ["ka", "lo", "mi", "ne", "ru", "sa", "to", "vi", "be", "da", "fo", "gu", "ha", "ji", "pe", "qu", "ri", "su", "te", "wa", "xe", "yo", "zu", "an", "el", "in", "or", "um"];
    private static readonly string[] Extensions = [".jpg", ".png", ".txt", ".dll", ".html", ".xml", ".json", ".mp3", ".pdf", ".docx", ".js", ".css", ".ini", ".log", ".mui", ".cs", ".py", ".zip"];
    private static readonly string[] Cameras = ["IMG_", "DSC_", "DSCN", "PXL_2024", "Screenshot 2025-", "VID_"];

    private static string Word(ulong h)
    {
        var sb = new StringBuilder();
        var n = 2 + (int)(h % 3);
        for (var i = 0; i < n; i++) { sb.Append(Syllables[(int)(h % (ulong)Syllables.Length)]); h /= (ulong)Syllables.Length; }
        sb[0] = char.ToUpperInvariant(sb[0]);
        return sb.ToString();
    }

    /// <summary>A synthetic "realistic" mixture: 40% from a shared Zipf-like vocabulary of 50,000 names (mostly present after any
    /// prefill), 25% camera/sequence names from a finite shared set (IMG_0423.JPG), 35% names unique to this snapshot that start with
    /// a word, so they spread over the key space (about a third of the files bring a new name).</summary>
    private static string MixedName(int folder, int k, int seed)
    {
        var h = Mix((ulong)folder, (ulong)k, (ulong)seed);
        var u = h % 1000;
        if (u < 400)
        {
            var r = (h >> 10) % 1_000_000 / 1_000_000.0;
            var idx = (ulong)(50_000 * r * r * r);   // skewed towards small indexes
            var v = Mix(idx, 7, 7);
            return Word(v) + (v % 5 == 0 ? "_" + Word(v >> 20) : "") + Extensions[(int)(v % (ulong)Extensions.Length)];
        }
        if (u < 650)
        {
            var camera = Cameras[(int)((ulong)folder % (ulong)Cameras.Length)];
            var counter = ((ulong)folder * 7 + (ulong)k) % 10_000;
            return camera + counter.ToString("D4", CultureInfo.InvariantCulture) + (camera == "VID_" ? ".MP4" : ".JPG");
        }
        return Word(h >> 8) + " " + Word(h >> 24) + " " + (Mix((ulong)folder, (ulong)k, (ulong)seed * 31 + 1) % 100_000).ToString(CultureInfo.InvariantCulture) + Extensions[(int)((h >> 40) % (ulong)Extensions.Length)];
    }
}

/// <summary>C4 design repair: the numeric parameters of the objective name families (§15.4 of the repaired specification). A cell is
/// fully described by them before it runs; nothing is harvested from a machine.</summary>
internal sealed record ObjParams(double D, string Model, double Beta, double Omega, double Rho, double Delta, double Alpha)
{
    /// <summary>"system" (the reference machine's system drive) or "data" (a data volume of the same machine): each selects that
    /// census's length quantiles and its repeat skew beta (docs/evidence/c4-design-review/census/calibration.json); a repeat occurrence
    /// takes vocabulary rank floor(V * u^beta), u uniform in [0, 1).</summary>
    internal static ObjParams Parse(string text)
    {
        var map = text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0].Trim(), kv => kv.Length > 1 ? kv[1].Trim() : "", StringComparer.Ordinal);
        string[] known = ["d", "model", "beta", "omega", "rho", "delta", "alpha"];
        foreach (var key in map.Keys)
            if (Array.IndexOf(known, key) < 0) throw new ArgumentException("unknown objective-generator parameter '" + key + "'");   // closed: a typo never silently becomes a default
        double Num(string key, double fallback) => map.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
        var model = map.GetValueOrDefault("model", "system");
        if (model is not ("system" or "data")) throw new ArgumentException("unknown name model '" + model + "' (system or data)");
        return new ObjParams(Num("d", 0.25), model, Num("beta", ObjNames.Beta(model)), Num("omega", 0.25),
            Num("rho", 0), Num("delta", 0), Num("alpha", 0));
    }

    /// <summary>The canonical text of the parameters (round-trip numbers), the form a plan and a record carry.</summary>
    internal string Canonical => string.Create(CultureInfo.InvariantCulture, $"d={D:R};model={Model};beta={Beta:R};omega={Omega:R};rho={Rho:R};delta={Delta:R};alpha={Alpha:R}");

    /// <summary>The part of a parameter string that changes a prefill (the vocabulary), canonical, for the prefill cache key.</summary>
    internal static string PrefillPart(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var p = Parse(text);
        return string.Create(CultureInfo.InvariantCulture, $"d={p.D:R};model={p.Model};beta={p.Beta:R};omega={p.Omega:R}");
    }
}

/// <summary>Deterministic file names for the objective families. A vocabulary entry v of vocabulary seed s is
/// Enc(v) + filler + extension: Enc is five letters (A-Z, a-z) encoding a bijective scramble of v, so every entry of one vocabulary is
/// distinct and entries spread uniformly over the key space; the total length follows the selected census's length quantiles (a name is
/// never shorter than its five letters and extension, so the shortest percentiles are raised to that).</summary>
internal static class ObjNames
{
    /// <summary>File-name length quantiles in UTF-16 code units, 0th to 100th percentile (census/calibration.json, "system").</summary>
    private static readonly int[] SystemQuantiles = [1, 5, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 10, 10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 14, 14, 14, 14, 14, 15, 15, 15, 16, 17, 17, 18, 18, 18, 19, 19, 19, 19, 20, 20, 21, 23, 23, 24, 24, 25, 26, 28, 31, 34, 38, 40, 40, 43, 44, 44, 44, 47, 54, 65, 78, 93, 99, 108, 249];

    /// <summary>The same for the data volume (census/calibration.json, "data").</summary>
    private static readonly int[] DataQuantiles = [1, 6, 7, 8, 9, 10, 10, 10, 12, 12, 13, 14, 15, 16, 17, 17, 17, 17, 18, 18, 18, 19, 19, 20, 20, 21, 21, 21, 21, 21, 22, 23, 23, 23, 23, 23, 24, 24, 25, 27, 28, 29, 30, 32, 32, 32, 32, 34, 35, 37, 37, 39, 40, 40, 40, 40, 40, 40, 42, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 46, 47, 47, 48, 48, 49, 49, 50, 50, 51, 52, 52, 52, 53, 53, 54, 54, 59, 59, 66, 70, 80, 228];

    /// <summary>The repeat skew fitted to each census's share of files carried by its most frequent 1% of names.</summary>
    internal static double Beta(string model) => model == "data" ? 2.558 : 13.501;

    private const long Space = 380_204_032;   // 52^5
    private const long Scramble = 2_654_435_761 % Space;   // odd and not a multiple of 13: a bijection on [0, 52^5)
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const string Filler = "abcdefghijklmnopqrstuvwxyz0123456789_- ";
    private static readonly string[] Extensions = [".dll", ".jpg", ".png", ".txt", ".xml", ".js", ".json", ".html", ".mui", ".pdf", ".mp3", ".log", ".ini", ".cs", ".py", ".zip",
        ".exe", ".dat", ".h", ".cpp", ".css", ".svg", ".gif", ".mp4", ".docx", ".xlsx", ".pyc", ".md", ".cab", ".sys", "", ""];

    internal static string Name(long v, int seed, string lengthModel)
    {
        var quantiles = lengthModel == "data" ? DataQuantiles : SystemQuantiles;
        var h = Mix((ulong)v, (ulong)seed, 0x4E414D45UL);
        var code = (long)(((ulong)((v % Space) * Scramble % Space) + (ulong)seed * 104_729UL) % (ulong)Space);
        var sb = new StringBuilder(64);
        Span<char> enc = stackalloc char[5];
        for (var i = 4; i >= 0; i--) { enc[i] = Letters[(int)(code % 52)]; code /= 52; }
        sb.Append(enc);
        var ext = Extensions[(int)((h >> 20) % (ulong)Extensions.Length)];
        // the length percentile follows a golden-ratio sequence over the vocabulary rank, so the few most frequent names (which carry
        // half the files in the system model) take evenly spread lengths and the file-weighted lengths match the census's per-file ones
        var q = (int)(((v + seed * 7_919L) * 0.6180339887498949 % 1.0) * 10_000);   // 0..9999: percentile with two decimals
        var lo = quantiles[q / 100];
        var hi = quantiles[Math.Min(100, q / 100 + 1)];
        var total = lo + (int)Math.Round((hi - lo) * (q % 100) / 100.0);
        var fill = Math.Max(0, total - 5 - ext.Length);
        var x = h | 1UL;
        for (var i = 0; i < fill; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            sb.Append(Filler[(int)(x % (ulong)Filler.Length)]);
        }
        // a name never ends with a space or starts its extension after one
        while (sb.Length > 5 && sb[^1] == ' ') sb[^1] = '_';
        sb.Append(ext);
        return sb.ToString();
    }

    /// <summary>A repeat occurrence's vocabulary rank: floor(V * u^beta).</summary>
    internal static long RepeatRank(ulong h, long vocabulary, double beta)
    {
        var u = (h >> 11) * (1.0 / (1UL << 53));
        return Math.Min(vocabulary - 1, (long)(vocabulary * Math.Pow(u, beta)));
    }

    internal static ulong Mix(ulong a, ulong b, ulong c)
    {
        var x = a * 0x9E3779B97F4A7C15UL ^ (b + 0x632BE59BD9B4E019UL) * 0xBF58476D1CE4E5B9UL ^ (c + 0x94D049BB133111EBUL);
        x ^= x >> 31; x *= 0xD6E8FEB86659FD93UL; x ^= x >> 29; x *= 0x94D049BB133111EBUL; x ^= x >> 32;
        return x;
    }

    internal static double Unit(ulong h) => (h >> 11) * (1.0 / (1UL << 53));
}

/// <summary>C4 design repair: an objective-family snapshot. The tree (folders, file counts, sizes, times) is DrSnapshot's for the tree
/// seed. File names: a base snapshot of n files over a vocabulary of V = round(D * n) distinct names, in which every vocabulary entry
/// occurs once (at the slots an affine permutation sends below V) and the other n - V slots repeat entries by RepeatRank, never twice in
/// one folder; then, for a re-scan, one churn layer: of the base's files a fraction Delta is deleted, a fraction Rho renamed to names
/// new to the source (spread over the key space), and for a fraction Alpha one file is added beside it with a name already in the
/// vocabulary. Source B of the Library shares a fraction Omega of its vocabulary entries with the target's vocabulary.</summary>
internal sealed class ObjSnapshot : ISnapshotRowSource
{
    private const int Branching = 6;
    private readonly int _folders;
    private readonly int _treeSeed;
    private readonly int _vocabSeed;
    private readonly int? _sharedSeed;
    private readonly int _layer;
    private readonly ObjParams _p;
    private readonly int[] _baseCount;
    private readonly long[] _baseStart;
    private readonly int[] _directFiles;
    private readonly long[] _directBytes;
    private readonly long[] _totalFiles;
    private readonly long[] _totalBytes;
    private readonly long[] _directSubfolders;
    private readonly long[] _totalSubfolders;
    private readonly long[] _seqStart;
    private readonly long _baseFiles;
    private readonly long _vocabulary;
    private readonly long _a;
    private readonly long _b;

    internal ObjSnapshot(int folders, int treeSeed, ObjParams p, int vocabSeed, int? sharedSeed = null, int layer = 0)
    {
        _folders = folders;
        _treeSeed = treeSeed;
        _vocabSeed = vocabSeed;
        _sharedSeed = sharedSeed;
        _layer = layer;
        _p = p;
        _baseCount = new int[folders];
        _baseStart = new long[folders];
        _directFiles = new int[folders];
        _directBytes = new long[folders];
        _totalFiles = new long[folders];
        _totalBytes = new long[folders];
        _directSubfolders = new long[folders];
        _totalSubfolders = new long[folders];
        _seqStart = new long[folders];
        long baseSeq = 0;
        for (var i = 0; i < folders; i++)
        {
            _baseCount[i] = (int)((uint)Hash(i, 17, _treeSeed) % 9u);
            _baseStart[i] = baseSeq;
            baseSeq += _baseCount[i];
        }
        _baseFiles = baseSeq;
        _vocabulary = Math.Max(1, (long)Math.Round(p.D * _baseFiles));
        _a = 2_654_435_761L % Math.Max(2, _baseFiles);
        while (Gcd(_a, _baseFiles) != 1) _a++;
        _b = 40_503L % Math.Max(1, _baseFiles);
        long seq = 0;
        for (var i = 0; i < folders; i++)
        {
            var (count, bytes) = Shape(i);
            _directFiles[i] = count;
            _seqStart[i] = seq;
            seq += count;
            _directBytes[i] = bytes;
            _totalFiles[i] = count;
            _totalBytes[i] = bytes;
            if (i > 0) _directSubfolders[ParentOf(i)]++;
        }
        for (var i = folders - 1; i >= 1; i--)
        {
            var parent = ParentOf(i);
            _totalFiles[parent] += _totalFiles[i];
            _totalBytes[parent] += _totalBytes[i];
            _totalSubfolders[parent] += 1 + _totalSubfolders[i];
        }
        Files = seq;
        Bytes = _totalBytes[0];
    }

    internal long Files { get; }

    internal long Bytes { get; }

    internal long Vocabulary => _vocabulary;

    public int FolderCount => _folders;

    private static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return a; }

    private static int ParentOf(int index) => (index - 1) / Branching;

    private static int Hash(int a, int b, int seed) => unchecked((a * 73856093) ^ (b * 19349663) ^ (seed * 83492791));

    private long SizeOf(int folder, int k) => 1 + (uint)Hash(folder, k + 1000, _treeSeed) % 100_000u;

    private enum Fate { Kept, Deleted, Renamed }

    private Fate FateOf(int folder, int k)
    {
        if (_layer == 0) return Fate.Kept;
        var u = ObjNames.Unit(ObjNames.Mix((ulong)folder, (ulong)k, 0xC4000UL + (ulong)_layer));
        return u < _p.Delta ? Fate.Deleted : u < _p.Delta + _p.Rho ? Fate.Renamed : Fate.Kept;
    }

    private bool Adds(int folder, int k) => _layer != 0 && ObjNames.Unit(ObjNames.Mix((ulong)folder, (ulong)k, 0xADD00UL + (ulong)_layer)) < _p.Alpha;

    private (int Count, long Bytes) Shape(int folder)
    {
        var count = 0;
        long bytes = 0;
        var extra = 0;
        for (var k = 0; k < _baseCount[folder]; k++)
        {
            if (FateOf(folder, k) != Fate.Deleted) { count++; bytes += SizeOf(folder, k); }
            if (Adds(folder, k)) { count++; bytes += SizeOf(folder, _baseCount[folder] + extra++); }
        }
        return (count, bytes);
    }

    internal ImportSnapshotHeader Header(string runId) => new()
    {
        Completion = ScanCompletionState.Complete,
        RunId = runId,
        StartedUtcTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
        FinishedUtcTicks = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc).Ticks,
        RootPathAsEntered = Utf16.ToBytes(@"D:\Media"),
        CanonicalRoot = Utf16.ToBytes(@"D:\Media"),
        MountPoint = Utf16.ToBytes(@"D:\"),
        VolumeLabel = Utf16.ToBytes("Data"),
        CapacityBytes = 1_000_000_000,
        FreeBytes = 500_000_000,
        Confidence = IdentityConfidence.Strong,
        Basis = IdentityBasis.Evidence,
        CaptureKind = SourceKind.LocalVolume,
        FsName = "NTFS",
        Files = Files,
        Folders = _folders,
        Bytes = Bytes,
    };

    public IEnumerable<ImportFolder> Folders()
    {
        for (var i = 0; i < _folders; i++)
        {
            yield return new ImportFolder(i, i == 0 ? -1 : ParentOf(i), i == 0 ? [] : Utf16.ToBytes("dir" + (i % 997)), FolderScanStatus.Ok, null, true, 0x10,
                CreatedTicks: 637_000_000_000_000_000L + i, ModifiedTicks: 637_100_000_000_000_000L + i,
                _directBytes[i], _totalBytes[i], _directFiles[i], _totalFiles[i], _directSubfolders[i], _totalSubfolders[i],
                LargestFileBytes: _totalFiles[i] == 0 ? null : 100_000);
        }
    }

    private string VocabularyName(long v) =>
        _sharedSeed is { } shared && ObjNames.Unit(ObjNames.Mix((ulong)v, (ulong)_vocabSeed, 0x0E6AUL)) < _p.Omega
            ? ObjNames.Name(v, shared, _p.Model)
            : ObjNames.Name(v, _vocabSeed, _p.Model);

    /// <summary>The base snapshot's vocabulary indexes for one folder: first occurrences first, then repeats re-drawn off collisions.</summary>
    private long[] BaseIndexes(int folder)
    {
        var count = _baseCount[folder];
        var result = new long[count];
        var used = new HashSet<long>();
        var repeat = new List<int>();
        for (var k = 0; k < count; k++)
        {
            var t = (long)(((ulong)(_baseStart[folder] + k) * (ulong)_a + (ulong)_b) % (ulong)_baseFiles);
            if (t < _vocabulary) { result[k] = t; used.Add(t); }
            else repeat.Add(k);
        }
        foreach (var k in repeat)
        {
            var t = (long)(((ulong)(_baseStart[folder] + k) * (ulong)_a + (ulong)_b) % (ulong)_baseFiles);
            var v = ObjNames.RepeatRank(ObjNames.Mix((ulong)_vocabSeed, (ulong)t, 0x5EEDUL), _vocabulary, _p.Beta);
            for (var j = 0; !used.Add(v); j++) v = (v + 1 + j * 7_919L) % _vocabulary;
            result[k] = v;
        }
        return result;
    }

    public IReadOnlyList<ImportFile> FilesOfFolder(int folderIndex)
    {
        var count = _directFiles[folderIndex];
        if (count == 0) return [];
        var baseIdx = BaseIndexes(folderIndex);
        var used = new HashSet<long>(baseIdx);
        var files = new List<ImportFile>(count);
        var extra = 0;
        var seq = _seqStart[folderIndex];
        for (var k = 0; k < _baseCount[folderIndex]; k++)
        {
            var fate = FateOf(folderIndex, k);
            if (fate != Fate.Deleted)
            {
                var name = fate == Fate.Renamed
                    ? ObjNames.Name(_vocabulary + (long)_baseFiles * _layer + _baseStart[folderIndex] + k, _vocabSeed, _p.Model)   // new to the source
                    : VocabularyName(baseIdx[k]);
                files.Add(new ImportFile(Utf16.ToBytes(name), seq++, SizeOf(folderIndex, k), 637_100_000_000_000_000L + k, 637_000_000_000_000_000L, 637_200_000_000_000_000L, 0x20));
            }
            if (Adds(folderIndex, k))
            {
                var v = ObjNames.RepeatRank(ObjNames.Mix((ulong)folderIndex, (ulong)k, 0xADD01UL + (ulong)_layer), _vocabulary, _p.Beta);
                for (var j = 0; !used.Add(v); j++) v = (v + 1 + j * 7_919L) % _vocabulary;
                var kk = _baseCount[folderIndex] + extra++;
                files.Add(new ImportFile(Utf16.ToBytes(VocabularyName(v)), seq++, SizeOf(folderIndex, kk), 637_100_000_000_000_000L + kk, 637_000_000_000_000_000L, 637_200_000_000_000_000L, 0x20));
            }
        }
        return files;
    }

    public IEnumerable<ImportError> Errors() => [];

    /// <summary>The realised statistics, computed before the import from the generator alone (recorded with every run).</summary>
    internal (long Files, long Distinct, double MeanLength, long Renamed, long Deleted, long Added, long Once, long Top1) Describe()
    {
        var distinct = new Dictionary<string, int>(StringComparer.Ordinal);
        long files = 0, length = 0, renamed = 0, deleted = 0, added = 0;
        for (var i = 0; i < _folders; i++)
        {
            foreach (var f in FilesOfFolder(i))
            {
                var name = Utf16.ToString(f.Name);
                distinct[name] = distinct.GetValueOrDefault(name) + 1;
                files++;
                length += name.Length;
            }
            for (var k = 0; k < _baseCount[i]; k++)
            {
                var fate = FateOf(i, k);
                if (fate == Fate.Deleted) deleted++;
                if (fate == Fate.Renamed) renamed++;
                if (Adds(i, k)) added++;
            }
        }
        var counts = distinct.Values.OrderByDescending(c => c).ToArray();
        var once = counts.LongCount(c => c == 1);
        var top1 = counts.Take(Math.Max(1, counts.Length / 100)).Sum(c => (long)c);
        return (files, distinct.Count, files == 0 ? 0 : (double)length / files, renamed, deleted, added, once, top1);
    }
}
