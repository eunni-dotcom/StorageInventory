using StorageInventory.Library.Tests;
using StorageInventory.Testing;

// Child-process scenarios (TEST-K1, TEST-L6, TEST-L8): the same executable is started with "--child <scenario> <args>" and runs
// one scenario instead of the test run. They exist because writer locks, hot journals and process termination cannot be shown
// with several objects in one process.
if (args is ["--child", var scenario, ..])
{
    return ChildScenarios.Run(scenario, args[2..]);
}

if (args is ["--benchmark", ..])
{
    return LibraryBenchmark.Run(args[1..]);
}

return TestRunner.Run(typeof(World).Assembly, args);
