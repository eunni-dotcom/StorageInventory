using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// TEST CODE ONLY. Reads back the access mask Windows actually GRANTED to a handle (<c>NtQueryInformationFile</c>,
/// <c>FileAccessInformation</c>), so TEST-I1 can show that the handle the product opens with desired access 0 carries no list,
/// read, write or delete right, and a contrast handle opened for reading shows that the probe does see rights when they are
/// there. Nothing in <c>src</c> uses these declarations: the A-08 audit covers the first-party shipped assemblies, of which
/// this test assembly is not one. The same measurement is made on every volume type by
/// <c>tests/identity/Invoke-IdentityExperiments.ps1</c> (part 1b).
/// </summary>
internal static class GrantedAccessProbe
{
    /// <summary>Rights <c>CreateFileW</c> itself adds to a request for none.</summary>
    internal const uint Synchronize = 0x00100000;

    internal const uint FileReadAttributes = 0x00000080;

    /// <summary>Rights that would let a handle list, read, write or delete (the ones a zero-access handle must NOT have).</summary>
    internal const uint FileListDirectory = 0x00000001;
    internal const uint FileAddFile = 0x00000002;
    internal const uint FileAddSubdirectory = 0x00000004;
    internal const uint FileReadEa = 0x00000008;
    internal const uint FileWriteEa = 0x00000010;
    internal const uint FileTraverse = 0x00000020;
    internal const uint FileDeleteChild = 0x00000040;
    internal const uint FileWriteAttributes = 0x00000100;
    internal const uint Delete = 0x00010000;
    internal const uint ReadControl = 0x00020000;
    internal const uint WriteDac = 0x00040000;
    internal const uint WriteOwner = 0x00080000;

    private const uint GenericRead = 0x80000000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const int FileAccessInformationClass = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(SafeFileHandle handle, out IoStatusBlock ioStatus, out uint accessFlags, uint length, int informationClass);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>The access mask the handle was granted.</summary>
    internal static uint GrantedTo(SafeFileHandle handle)
    {
        var status = NtQueryInformationFile(handle, out _, out var access, sizeof(uint), FileAccessInformationClass);
        if (status != 0) throw new InvalidOperationException($"NtQueryInformationFile failed: 0x{status:X8}");
        return access;
    }

    /// <summary>The contrast: a directory handle opened the way a listing needs (GENERIC_READ), which the product never does.</summary>
    internal static SafeFileHandle OpenForReading(string extendedDirectoryPath)
    {
        var handle = CreateFile(extendedDirectoryPath, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new InvalidOperationException("the contrast handle did not open: Win32 " + Marshal.GetLastWin32Error());
        return handle;
    }
}
