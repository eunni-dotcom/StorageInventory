using StorageInventory.IntegrationTests;
using StorageInventory.Testing;

if (args.Length >= 4 && args[0] == "--benchmark-one")
{
    return Benchmarks.RunOne(args[1], args[2] == "sorted", args[3], spool: args.Length >= 5 && args[4] == "spool");
}
if (args.Length >= 2 && args[0] == "--docs-screenshots")
{
    return DocsScreenshots.Run(Path.GetFullPath(args[1]));
}
if (args.Length >= 1 && args[0] == "--benchmark")
{
    return Benchmarks.Run(args.Length >= 2 ? args[1] : "10000,60000,250000");
}

try
{
    return TestRunner.Run(typeof(TestEnvironment).Assembly, args);
}
finally
{
    PhaseAFixture.DisposeShared();
}
