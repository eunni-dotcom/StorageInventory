using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using StorageInventory.Core;
using StorageInventory.Core.Reports;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

public static class WorkbookExportTests
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static PhaseAFixture Fx => PhaseAFixture.Shared;

    private static StorageScanResult ScanFixture(string? root = null)
    {
        var output = Path.Combine(Fx.Base, "xlsx_" + Guid.NewGuid().ToString("N")[..6]);
        var result = new InventoryScanner().Scan(new StorageScanOptions { RootPath = root ?? Fx.Root, OutputPath = output });
        Assert.True(result.Finished, "scan must finish");
        return result;
    }

    private static List<List<string>> SheetCells(string xlsx, int sheetNumber, out int formulas, out int hyperlinks)
    {
        using var zip = ZipFile.OpenRead(xlsx);
        formulas = 0;
        hyperlinks = 0;
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) || e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            using var s = entry.Open();
            var doc = XDocument.Load(s);
            formulas += doc.Descendants().Count(e => e.Name.LocalName == "f");
            hyperlinks += doc.Descendants().Count(e => e.Name.LocalName == "hyperlink" || e.Name.LocalName == "hyperlinks");
            hyperlinks += doc.Descendants().Count(e => (string?)e.Attribute("Type") is { } t && t.EndsWith("/hyperlink", StringComparison.Ordinal));
        }
        using var sheet = zip.GetEntry($"xl/worksheets/sheet{sheetNumber}.xml")!.Open();
        var rows = new List<List<string>>();
        foreach (var row in XDocument.Load(sheet).Descendants(S + "row"))
        {
            var cells = new List<string>();
            foreach (var c in row.Elements(S + "c"))
            {
                var col = ColumnIndex((string)c.Attribute("r")!);
                while (cells.Count < col) cells.Add("");
                cells.Add((string?)c.Attribute("t") == "inlineStr" ? (string)c.Element(S + "is")!.Element(S + "t")! : (string)c.Element(S + "v")!);
            }
            rows.Add(cells);
        }
        return rows;
    }

    private static int ColumnIndex(string cellRef)
    {
        var n = 0;
        foreach (var ch in cellRef.TakeWhile(char.IsLetter)) n = n * 26 + (ch - 'A' + 1);
        return n - 1;
    }

    [Test]
    public static void Workbook_matches_the_csv_reports_and_contains_no_formulas_or_hyperlinks()
    {
        var scan = ScanFixture();
        var export = WorkbookExporter.Export(scan);
        Assert.Equal(WorkbookExportState.Created, export.State, export.Message);
        var xlsx = Assert.NotNull(export.WorkbookPath);
        Assert.Equal(Path.Combine(scan.OutputPath, $"StorageInventory_{scan.RunId}.xlsx"), xlsx);
        Assert.False(File.Exists(xlsx + ".partial"), "the .partial was renamed, not left behind");

        foreach (var (sheet, csv) in new[] { (1, scan.Reports!.FilesCsv), (2, scan.Reports.FoldersCsv) })
        {
            var cells = SheetCells(xlsx, sheet, out var formulas, out var hyperlinks);
            Assert.Equal(0, formulas, "formula elements in the workbook");
            Assert.Equal(0, hyperlinks, "hyperlink elements or relationships in the workbook");
            using var reader = new ReportCsvReader(csv);
            Assert.SequenceEqual(reader.Header, cells[0], $"sheet {sheet} header");
            var r = 1;
            while (reader.ReadRecord() is { } record)
            {
                var row = cells[r++];
                while (row.Count < record.Length) row.Add("");
                Assert.SequenceEqual(record, row, $"sheet {sheet} row {r}");
            }
            Assert.Equal(r, cells.Count, $"sheet {sheet} row count");
        }
        var guarded = SheetCells(xlsx, 1, out _, out _).Single(row => row[0] == "'=HYPERLINK(1).txt");
        Assert.Equal("'=HYPERLINK(1).txt", guarded[0]);
    }

    [Test]
    public static void Workbook_opens_in_an_independent_spreadsheet_library()
    {
        var modules = Path.Combine(TestEnvironment.RepoRoot, "tools", "psmodules", "ImportExcel");
        if (!Directory.Exists(modules)) Assert.Skip("ImportExcel (test-only) is not in tools\\psmodules");
        var scan = ScanFixture();
        var xlsx = WorkbookExporter.Export(scan).WorkbookPath!;
        var r = TestEnvironment.RunTestLib(
            $"Import-Module {TestEnvironment.Quote(modules)}; " +
            $"$p = Open-ExcelPackage -Path {TestEnvironment.Quote(xlsx)}; " +
            "$o = foreach ($ws in $p.Workbook.Worksheets) { $f = 0; $h = 0; foreach ($c in $ws.Cells[$ws.Dimension.Address]) { if ($c.Formula) { $f++ }; if ($c.Hyperlink) { $h++ } }; " +
            "[pscustomobject]@{ Name = $ws.Name; Rows = $ws.Dimension.End.Row; Formulas = $f; Hyperlinks = $h; SizeIsNumber = ($ws.Name -ne 'Files' -or $ws.Cells[2, 7].Value -is [double]) } }; " +
            "Close-ExcelPackage $p -NoSave; $o | ConvertTo-Json -Compress");
        var json = r.StdOut.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith('['));
        var sheets = JsonSerializer.Deserialize<JsonElement[]>(json)!;
        Assert.Equal(2, sheets.Length, r.All);
        Assert.Equal("Files", sheets[0].GetProperty("Name").GetString());
        Assert.Equal((int)scan.Totals.Files + 1, sheets[0].GetProperty("Rows").GetInt32());
        Assert.Equal((int)scan.Totals.Folders + 1, sheets[1].GetProperty("Rows").GetInt32());
        foreach (var s in sheets)
        {
            Assert.Equal(0, s.GetProperty("Formulas").GetInt32());
            Assert.Equal(0, s.GetProperty("Hyperlinks").GetInt32());
            Assert.True(s.GetProperty("SizeIsNumber").GetBoolean(), "size columns are numbers");
        }
    }

    [Test]
    public static void An_existing_workbook_with_the_same_name_is_never_overwritten()
    {
        var scan = ScanFixture(Path.Combine(Fx.Root, "Kpop"));
        var final = Path.Combine(scan.OutputPath, $"StorageInventory_{scan.RunId}.xlsx");
        File.WriteAllText(final, "DECOY WORKBOOK");
        var export = WorkbookExporter.Export(scan);
        Assert.Equal(WorkbookExportState.Failed, export.State);
        Assert.Equal("DECOY WORKBOOK", File.ReadAllText(final));
        Assert.Equal(final + ".partial", export.UnfinishedPath, "the unfinished file this export made is identified");
        Assert.Contains("safe to delete", export.Message);
        Assert.Contains("CSV reports are complete", export.Message);
    }

    [Test]
    public static void An_existing_partial_is_never_overwritten_nor_claimed()
    {
        var scan = ScanFixture(Path.Combine(Fx.Root, "Kpop"));
        var partial = Path.Combine(scan.OutputPath, $"StorageInventory_{scan.RunId}.xlsx.partial");
        File.WriteAllText(partial, "DECOY PARTIAL");
        var export = WorkbookExporter.Export(scan);
        Assert.Equal(WorkbookExportState.Failed, export.State);
        Assert.Equal("DECOY PARTIAL", File.ReadAllText(partial));
        Assert.Null(export.UnfinishedPath, "a file this export did not create is never reported as its own");
        Assert.False(File.Exists(Path.Combine(scan.OutputPath, $"StorageInventory_{scan.RunId}.xlsx")));
    }

    [Test]
    public static void Files_sheet_is_left_out_beyond_the_row_limit()
    {
        var scan = ScanFixture();
        var export = WorkbookExporter.Export(scan, maxDataRows: 5, CancellationToken.None);
        Assert.Equal(WorkbookExportState.Created, export.State, export.Message);
        Assert.True(export.FilesSheetOmitted);
        Assert.Contains("Files sheet was left out", export.Message);
        using var zip = ZipFile.OpenRead(export.WorkbookPath!);
        var names = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open()).Descendants(S + "sheet").Select(e => (string)e.Attribute("name")!).ToList();
        Assert.SequenceEqual(["Folders"], names);
    }

    [Test]
    public static void Cancelled_export_leaves_the_csv_reports_intact()
    {
        var scan = ScanFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var before = File.ReadAllBytes(scan.Reports!.FilesCsv);
        var export = WorkbookExporter.Export(scan, cts.Token);
        Assert.True(export.State is WorkbookExportState.Cancelled or WorkbookExportState.Created, export.State.ToString());
        Assert.SequenceEqual(before, File.ReadAllBytes(scan.Reports.FilesCsv), "CSV unchanged");
    }

    /// <summary>Regression (merge-readiness review): an unreadable Files CSV used to throw out of Export, leaving the
    /// app stuck on "Creating the optional Excel workbook" with no message. It must be a Failed result instead.</summary>
    [Test]
    public static void Unreadable_csv_report_fails_the_export_instead_of_throwing()
    {
        var scan = ScanFixture();
        WorkbookExportResult export;
        using (new FileStream(scan.Reports!.FilesCsv, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            export = WorkbookExporter.Export(scan);
        }
        Assert.Equal(WorkbookExportState.Failed, export.State, export.Message);
        Assert.Null(export.WorkbookPath);
        Assert.Null(export.UnfinishedPath);
        Assert.True(export.Message.Contains("The CSV reports are complete and unaffected.", StringComparison.Ordinal), export.Message);
        Assert.False(File.Exists(Path.Combine(scan.OutputPath, $"StorageInventory_{scan.RunId}.xlsx")), "no workbook");
        Assert.False(File.Exists(Path.Combine(scan.OutputPath, $"StorageInventory_{scan.RunId}.xlsx.partial")), "no partial workbook");
    }

    [Test]
    public static void Only_finished_scans_can_be_exported()
    {
        var cancelled = StorageScanResult.ForCancelled("20260101_000000_abcdef", Fx.Root, Fx.Base, new ScanTotals(), new Dictionary<ScanErrorType, long>(), [], new PhaseTimings());
        Assert.Equal(WorkbookExportState.Failed, WorkbookExporter.Export(cancelled).State);
    }
}
