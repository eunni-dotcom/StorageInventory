namespace StorageInventory.Core.Identity;

/// <summary>
/// A <c>FILE_ID_128</c>: the 16 identifier bytes of a file or directory, read as two little-endian 64-bit halves so that
/// every bit is kept and equality is exact. Rendered most-significant half first, the way <c>fsutil file queryFileID</c>
/// prints it (an NTFS root directory is <c>0x00000000000000000005000000000005</c>: MFT record 5, sequence 5).
/// </summary>
/// <remarks>
/// StorageInventory records only the ID of a source's own root directory, as information (§7.2, FID-02): for a
/// whole-volume source it is <b>not</b> volume identity (on NTFS every volume's root is MFT record 5, measured in C3 for
/// Q-13), and for a subfolder source it only lets a later comparison say "the source folder was recreated". There is no
/// per-file ID anywhere in v1.1 (FID-01, FID-02).
/// </remarks>
internal readonly record struct FileId128(ulong Low, ulong High)
{
    public override string ToString() => $"0x{High:X16}{Low:X16}";
}
