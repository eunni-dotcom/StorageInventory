using System.Windows;
using StorageInventory.App.ViewModels;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// TEST CODE ONLY. `--docs-screenshots &lt;folder&gt;` regenerates the README screenshot from a small SYNTHETIC tree
/// built under %TEMP%\StorageInventoryTests, so the published image never shows a real drive, real names or desktop
/// contents. The results screen shows no paths. The tree and its reports are removed afterwards.
/// </summary>
public static class DocsScreenshots
{
    private static readonly (string Folder, int Files, long BytesPerFile)[] Layout =
    [
        ("Videos", 6, 9_000_000),
        ("Photos", 240, 180_000),
        ("Music", 120, 150_000),
        ("Projects", 400, 12_000),
        ("Documents", 150, 20_000),
        ("Archive", 3, 700_000),
        ("Downloads", 12, 45_000),
    ];

    public static int Run(string outputFolder)
    {
        var work = Path.Combine(TestEnvironment.WorkRoot, "docs_" + Guid.NewGuid().ToString("N")[..6]);
        var tree = Path.Combine(work, "Demo");
        var reports = Path.Combine(work, "reports");
        try
        {
            foreach (var (folder, files, bytes) in Layout)
            {
                var dir = Directory.CreateDirectory(Path.Combine(tree, folder, "2025"));
                for (var i = 0; i < files; i++)
                {
                    using var f = new FileStream(Path.Combine(dir.FullName, $"{folder.ToLowerInvariant()}_{i:D3}.dat"), FileMode.CreateNew);
                    f.SetLength(bytes + i * 97);
                }
            }

            UiHost.SetTheme(ThemeMode.Light);
            var (w, vm) = UiSetupTests.OpenWindow();
            try
            {
                UiHost.Invoke(() => { vm.SourcePath = tree; vm.OutputPath = reports; vm.CreateWorkbook = false; });
                Assert.True(UiHost.WaitUntil(() => vm.StartScanCommand.CanExecute(null), TimeSpan.FromSeconds(10)), "pre-flight ready");
                UiHost.Invoke(() => vm.StartScanCommand.Execute(null));
                Assert.True(UiHost.WaitUntil(() => vm.Stage == AppStage.Results, TimeSpan.FromSeconds(60)), "results shown");
                UiHost.Render(w, Path.Combine(outputFolder, "results.png"));
            }
            finally { UiSetupTests.Close(w); }
            Console.WriteLine($"Wrote {Path.Combine(outputFolder, "results.png")}");
            return 0;
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }
}
