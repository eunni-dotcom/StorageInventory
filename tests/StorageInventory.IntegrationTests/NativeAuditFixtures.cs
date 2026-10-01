using System.Runtime.InteropServices;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// TEST CODE ONLY, NEVER CALLED. Two P/Invoke declarations that the A-08 audit must reject: a seventh kernel32 function that
/// can WRITE (it deletes a file) and a function from another DLL. They exist so the negative self-test
/// (<c>SecurityAuditTests.The_native_rules_reject_violating_fixtures</c>) can run the real reflection rule over a real
/// violation and prove the audit detects it (A-21). The audit scans first-party shipped assemblies (Core, History, App); this
/// test assembly is not one of them, and nothing here is ever invoked.
/// </summary>
internal static class RogueNativeFixtures
{
    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteFile(string fileName);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
