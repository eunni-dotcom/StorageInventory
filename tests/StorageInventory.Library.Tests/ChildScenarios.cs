namespace StorageInventory.Library.Tests;

/// <summary>Scenarios run by a child process (see Program.cs). Filled in by the real-process tests.</summary>
internal static class ChildScenarios
{
    internal static int Run(string scenario, string[] args)
    {
        Console.Error.WriteLine("unknown child scenario " + scenario);
        return 2;
    }
}
