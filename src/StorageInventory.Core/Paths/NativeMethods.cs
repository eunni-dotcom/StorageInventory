using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageInventory.Core.Paths;

/// <summary>
/// The ONLY native Windows calls in StorageInventory: six read-only <c>kernel32</c> functions (v1.1 gate C3; v1.0 had
/// the first two). A directory is opened with ZERO access rights (the handle carries only the synchronise and
/// read-attributes rights that <c>CreateFileW</c> itself adds, so it cannot list, read, write or delete anything) purely
/// so Windows can answer questions about it:
/// <list type="bullet">
/// <item><description><c>CreateFileW</c> (desired access 0): the handle. Every call site passes the literal 0.</description></item>
/// <item><description><c>GetFinalPathNameByHandleW</c>: the canonical location of the opened object (SUBST drives, mapped
/// drives, 8.3 names and letter case resolved).</description></item>
/// <item><description><c>GetVolumeInformationByHandleW</c>: filesystem name, 32-bit volume serial, label, flags.</description></item>
/// <item><description><c>GetFileInformationByHandleEx</c>, information class <c>FileIdInfo</c> (18) ONLY: the 64-bit volume
/// serial and the 128-bit file ID of the opened directory. The parameter's type has that one class as its only member.</description></item>
/// <item><description><c>GetVolumePathNameW</c> and <c>GetDiskFreeSpaceExW</c>: queries by path (no handle) for the mount
/// point and the capacity and free space.</description></item>
/// </list>
/// None writes. Win32 failures are returned as error codes, never thrown, so a failed query becomes recorded evidence
/// instead of an invented value (§7.2, ID-13).
/// </summary>
internal static class NativeMethods
{
    private const uint FileShareReadWriteDelete = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;   // required to open a directory handle
    private const uint VolumeNameDos = 0x0;
    private const uint FileNameNormalized = 0x0;
    private const int NameBufferLength = 261;                  // MAX_PATH + 1: volume labels and filesystem names are far shorter

    /// <summary>The only <c>FILE_INFO_BY_HANDLE_CLASS</c> value StorageInventory ever passes. A one-member enum, and the
    /// security audit allows the type's name in exactly three places in <c>src</c> (this declaration, the P/Invoke's
    /// parameter, and the one call's <c>FileInfoByHandleClass.FileIdInfo</c> argument), so asking for another information
    /// class means writing something the audit rejects. (Every class is a read-only query; the point is that the surface is
    /// reviewed as exactly one.)</summary>
    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18,
    }

    /// <summary><c>FILE_ID_INFO</c>: the 64-bit volume serial followed by the 128-bit file ID (16 identifier bytes, read here
    /// as two little-endian 64-bit halves).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfoNative
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] buffer, uint bufferLength, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(SafeFileHandle file, [Out] char[] volumeNameBuffer, uint volumeNameBufferLength,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, [Out] char[] fileSystemNameBuffer,
        uint fileSystemNameBufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass informationClass,
        out FileIdInfoNative information, uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailableToCaller, out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    /// <summary>The single place a directory handle is created. Desired access is the literal 0: no read, write or delete
    /// rights, only a handle to ask questions through. Sharing allows every other opener (read, write and delete), so the
    /// handle never blocks a rename or delete of the directory, only unmounting the volume.</summary>
    private static SafeFileHandle OpenZeroAccess(string extendedPath) =>
        CreateFile(extendedPath, 0, FileShareReadWriteDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);

    /// <summary>Opens <paramref name="directory"/> exactly as given (converted only to the extended form, which adds no
    /// further normalisation) with a zero-access handle. Returns null and the Win32 error when it cannot be opened. The
    /// caller owns the handle and must dispose it.</summary>
    internal static SafeFileHandle? TryOpenDirectoryZeroAccess(string directory, out int win32Error)
    {
        var handle = OpenZeroAccess(ToExtendedPath(directory));
        if (handle.IsInvalid)
        {
            win32Error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return null;
        }
        win32Error = 0;
        return handle;
    }

    /// <summary>Returns the canonical DOS path of an EXISTING directory (without the \\?\ prefix; UNC paths as
    /// \\server\share\...). Throws <see cref="Win32Exception"/> on failure.</summary>
    internal static string GetFinalDirectoryPath(string directory)
    {
        // desiredAccess = 0: no read, write, delete or attribute access - only a handle to ask "where is this?".
        using var handle = TryOpenDirectoryZeroAccess(directory, out var openError) ?? throw new Win32Exception(openError);
        return TryGetFinalPath(handle, out var path, out var error) ? path! : throw new Win32Exception(error);
    }

    /// <summary>The canonical location of the object behind an open handle. The path is read from the handle, so it follows
    /// the object (a renamed directory reports its new name; a deleted one a <c>$Deleted</c> name).</summary>
    internal static bool TryGetFinalPath(SafeFileHandle handle, out string? path, out int win32Error)
    {
        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, VolumeNameDos | FileNameNormalized);
            if (length == 0)
            {
                win32Error = Marshal.GetLastWin32Error();
                path = null;
                return false;
            }
            if (length < buffer.Length)
            {
                win32Error = 0;
                path = StripExtendedPrefix(new string(buffer, 0, (int)length));
                return true;
            }
            buffer = new char[length + 1];
        }
    }

    /// <summary>Filesystem name, label, 32-bit serial and flags of the volume behind an open handle.</summary>
    internal readonly record struct VolumeInformation(string FileSystemName, string VolumeLabel, uint SerialNumber, uint FileSystemFlags,
        uint MaximumComponentLength);

    internal static bool TryGetVolumeInformation(SafeFileHandle handle, out VolumeInformation information, out int win32Error)
    {
        var label = new char[NameBufferLength];
        var fileSystem = new char[NameBufferLength];
        if (!GetVolumeInformationByHandle(handle, label, (uint)label.Length, out var serial, out var maximumComponent, out var flags,
                fileSystem, (uint)fileSystem.Length))
        {
            win32Error = Marshal.GetLastWin32Error();
            information = default;
            return false;
        }
        win32Error = 0;
        information = new VolumeInformation(NullTerminated(fileSystem), NullTerminated(label), serial, flags, maximumComponent);
        return true;
    }

    /// <summary>The 64-bit volume serial and the 128-bit file ID of the directory behind an open handle (information class
    /// <c>FileIdInfo</c> only).</summary>
    internal readonly record struct FileIdInformation(ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh);

    internal static bool TryGetFileId(SafeFileHandle handle, out FileIdInformation information, out int win32Error)
    {
        if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo, out var native, (uint)Unsafe.SizeOf<FileIdInfoNative>()))
        {
            win32Error = Marshal.GetLastWin32Error();
            information = default;
            return false;
        }
        win32Error = 0;
        information = new FileIdInformation(native.VolumeSerialNumber, native.FileIdLow, native.FileIdHigh);
        return true;
    }

    /// <summary>The mount point of the volume that contains <paramref name="path"/> (<c>C:\</c>, or <c>\\server\share\</c>),
    /// without the \\?\ prefix. Queried by path: there is no handle form of this function.</summary>
    internal static bool TryGetVolumePathName(string path, out string? mountPoint, out int win32Error)
    {
        var extended = ToExtendedPath(path);
        var buffer = new char[extended.Length + 8];
        if (!GetVolumePathName(extended, buffer, (uint)buffer.Length))
        {
            win32Error = Marshal.GetLastWin32Error();
            mountPoint = null;
            return false;
        }
        win32Error = 0;
        mountPoint = StripExtendedPrefix(NullTerminated(buffer));
        return true;
    }

    /// <summary>Capacity and free space of the volume that holds <paramref name="directory"/>.</summary>
    internal readonly record struct DiskSpace(long CapacityBytes, long FreeBytesAvailableToCaller, long TotalFreeBytes);

    internal static bool TryGetDiskFreeSpace(string directory, out DiskSpace space, out int win32Error)
    {
        if (!GetDiskFreeSpaceEx(ToExtendedPath(directory), out var available, out var total, out var totalFree))
        {
            win32Error = Marshal.GetLastWin32Error();
            space = default;
            return false;
        }
        win32Error = 0;
        space = new DiskSpace(Clamp(total), Clamp(available), Clamp(totalFree));
        return true;
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

    private static string NullTerminated(char[] buffer)
    {
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
