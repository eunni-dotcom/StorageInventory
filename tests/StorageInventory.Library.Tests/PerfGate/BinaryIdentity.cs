using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>
/// The identity of the binary under measurement (final repair O06): the commit it was built from (given by the orchestrator, which
/// reads it from git, and whether the tree was dirty) and the SHA-256 of its BUILD OUTPUT, so a prefill is never reused across a change
/// of either. The output hash covers every managed assembly, native library, deps and runtime-config file next to the executable, in
/// the order of their relative paths, each as its path and its contents.
/// </summary>
internal static class BinaryIdentity
{
    private static readonly string[] Extensions = [".dll", ".exe", ".json", ".so", ".dylib"];

    /// <summary>The hash of the build output under <paramref name="directory"/>.</summary>
    internal static string OutputHash(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !f.Contains(Path.DirectorySeparatorChar + "SI-Gate", StringComparison.Ordinal))
            .Select(f => (Full: f, Relative: Path.GetRelativePath(directory, f).Replace('\\', '/')))
            .OrderBy(f => f.Relative, StringComparer.Ordinal);
        foreach (var (full, relative) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[1 << 16];
            int read;
            while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, read));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The identity of THIS binary: commit and dirty flag from the orchestrator's arguments, the output hash computed here.</summary>
    internal static BinaryRecord Current(string commit, bool dirty, string engine) => new(commit, dirty, OutputHash(AppContext.BaseDirectory), engine);

    /// <summary>The text of an identity inside a prefill cache key.</summary>
    internal static string KeyPart(BinaryRecord binary) => "b" + (binary.Commit.Length > 12 ? binary.Commit[..12] : binary.Commit) + (binary.Dirty ? "dirty" : "") + "-" + binary.OutputHash[..16];

    /// <summary>The prefill cache key: it names EVERY input that changes the prefill's content (generator version, family, parameters,
    /// size, schema variant, the identity of the binary that builds it). The production schema is the only variant of the gate tooling, so
    /// "persource" is a constant of the key and a change to the schema reaches it through the output hash.</summary>
    internal static string PrefillKey(PrefillSpec prefill, BinaryRecord binary)
    {
        var parts = new List<string> { "g" + GateConstants.GeneratorVersion.ToString(CultureInfo.InvariantCulture), prefill.Family, prefill.PerSnapshot.ToString(CultureInfo.InvariantCulture) };
        if (prefill.Params.Length > 0) parts.Add(prefill.Params.Replace(';', '_').Replace('=', '-'));
        parts.Add("persource");
        parts.Add(KeyPart(binary));
        return string.Join("-", parts);
    }
}
