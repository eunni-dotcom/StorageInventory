using System.Reflection;
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

/// <summary>
/// TEST CODE ONLY, NEVER CALLED. The reflective call of the C3 review's mutant C7 (finding C3-M03): it reaches a native-style
/// method with an information class taken from the method's own parameter type, so the type's name never appears in the
/// source. The A-08 reflection rules must see the compiled form of this (<c>The_reflection_rules_reject_the_bypass_the_review_demonstrated_and_its_variants</c>).
/// </summary>
internal static class RogueReflectionFixture
{
    internal static object? CallWithAnyInformationClass(object handle)
    {
        var method = typeof(RogueReflectionFixture).GetMethod(nameof(Target), BindingFlags.Static | BindingFlags.NonPublic)!;
        return method.Invoke(null, [handle, Enum.ToObject(method.GetParameters()[1].ParameterType, 5)]);
    }

    private static bool Target(object handle, DayOfWeek informationClass) => handle is not null && informationClass >= 0;
}
