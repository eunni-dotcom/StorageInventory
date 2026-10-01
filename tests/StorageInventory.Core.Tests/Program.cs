using StorageInventory.Testing;

if (args is ["--public-surface", var surfaceFile])
{
    // Writes the TEST-O5 baseline rendering of Core's public surface (see PublicSurfaceTests).
    File.WriteAllText(surfaceFile, StorageInventory.Core.Tests.PublicSurface.Describe(typeof(StorageInventory.Core.StorageScanResult).Assembly));
    return 0;
}

return TestRunner.Run(typeof(StorageInventory.Core.Tests.ContractTests).Assembly, args);
