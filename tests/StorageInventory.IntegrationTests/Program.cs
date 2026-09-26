using StorageInventory.IntegrationTests;
using StorageInventory.Testing;

try
{
    return TestRunner.Run(typeof(TestEnvironment).Assembly, args);
}
finally
{
    PhaseAFixture.DisposeShared();
}
