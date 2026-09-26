using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageInventory.Core.Paths;

/// <summary>
/// The ONLY native Windows calls in StorageInventory. Both are read-only: a directory is opened with ZERO access
/// rights (it cannot be read, written or deleted through this handle) purely so Windows can report its canonical
/// location. Used to see through aliases (SUBST drives, 8.3 short names, junctions/links in the path, letter case)
/// when checking that the report folder is outside the scanned tree.
/// </summary>
internal static class NativeMethods
{
    private const uint FileShareReadWriteDelete = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;   // required to open a directory handle
    private const uint VolumeNameDos = 0x0;
    private const uint FileNameNormalized = 0x0;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] buffer, uint bufferLength, uint flags);

    /// <summary>Returns the canonical DOS path of an EXISTING directory (without the \\?\ prefix; UNC paths as
    /// \\server\share\...). Throws <see cref="Win32Exception"/> on failure.</summary>
    internal static string GetFinalDirectoryPath(string directory)
    {
        // desiredAccess = 0: no read, write, delete or attribute access - only a handle to ask "where is this?".
        using var handle = CreateFile(ToExtendedPath(directory), 0, FileShareReadWriteDelete, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, VolumeNameDos | FileNameNormalized);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length < buffer.Length) return StripExtendedPrefix(new string(buffer, 0, (int)length));
            buffer = new char[length + 1];
        }
    }

    /// <summary>C:\a -> \\?\C:\a\ ; \\server\share\a -> \\?\UNC\server\share\a\ (extended form: no MAX_PATH limit,
    /// no further normalisation by Windows). The input must already be a validated, fully-qualified path.</summary>
    internal static string ToExtendedPath(string path)
    {
        var withSlash = path.TrimEnd('\\') + @"\";
        return withSlash.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + withSlash[2..] : @"\\?\" + withSlash;
    }

    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }
}
