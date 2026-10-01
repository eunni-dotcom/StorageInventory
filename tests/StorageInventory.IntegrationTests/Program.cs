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
if (args.Length >= 1 && args[0] == "--identity-probe")
{
    return IdentityProbe.RunProbe(args[1..]);   // v1.1 C3 platform evidence (Q-02, Q-13): read-only, see IdentityProbe
}
if (args.Length >= 1 && args[0] == "--identity-hold")
{
    return IdentityProbe.RunHold(args[1..]);    // v1.1 C3 platform evidence (Q-19, TEST-I6): holds the root handle across a manual step
}

try
{
    return TestRunner.Run(typeof(TestEnvironment).Assembly, args);
}
finally
{
    PhaseAFixture.DisposeShared();
}
