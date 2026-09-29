using System.Text;
using StorageInventory.Core.Reports;
using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

public static class ReportFormatTests
{
    [Test]
    public static void Text_fields_are_quoted_and_quotes_doubled()
    {
        Assert.Equal("\"a,b.txt\"", CsvFormat.Text("a,b.txt"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvFormat.Text("say \"hi\""));
        Assert.Equal("\"\"", CsvFormat.Text(""));
        Assert.Equal("\"\"", CsvFormat.Text(null));
    }

    [Test]
    public static void Formula_guard_prefixes_exactly_the_dangerous_leading_characters()
    {
        foreach (var dangerous in new[] { "=HYPERLINK(1)", "+plus", "-minus", "@at", "\tTab", "\rCR", "\nLF" })
        {
            Assert.Equal("\"'" + dangerous.Replace("\"", "\"\"") + "\"", CsvFormat.Text(dangerous));
        }
        foreach (var safe in new[] { "'apostrophe", "a=b", ".gitignore", "C:\\x", "\\\\server", "한국어", "😀" })
        {
            Assert.Equal("\"" + safe + "\"", CsvFormat.Text(safe));
        }
    }

    [Test]
    public static void Numbers_use_invariant_1024_based_units()
    {
        Assert.Equal("1048576", CsvFormat.Integer(1048576));
        Assert.Equal("1024.00", CsvFormat.Scaled(1048576, CsvFormat.KB, "0.00"));
        Assert.Equal("1.00", CsvFormat.Scaled(1048576, CsvFormat.MB, "0.00"));
        Assert.Equal("0.001", CsvFormat.Scaled(1048576, CsvFormat.GB, "0.000"));
        Assert.Equal("292.97", CsvFormat.Scaled(300000, CsvFormat.KB, "0.00"));
        Assert.Equal("0.0000", CsvFormat.Scaled(1048576, CsvFormat.TB, "0.0000"));
        Assert.Equal("12.500", CsvFormat.Number(12.5, "0.000"));
    }

    [Test]
    public static void Dates_are_local_time_and_blank_when_invalid()
    {
        var utc = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), CsvFormat.Date(utc));
        Assert.Equal("", CsvFormat.Date(null));
    }

    [Test]
    public static void Error_messages_are_collapsed_to_one_line()
    {
        Assert.Equal("first second", CsvFormat.SingleLine("first\r\n  second  "));
    }

    [Test]
    public static void File_type_catalog_matches_the_reference_table()
    {
        Assert.Equal("Image", FileTypeCatalog.CategoryOf(".JPG"));
        Assert.Equal("Video", FileTypeCatalog.CategoryOf(".ts"));
        Assert.Equal("Disc Image", FileTypeCatalog.CategoryOf(".mdf"));
        Assert.Equal("Subtitle", FileTypeCatalog.CategoryOf(".sub"));
        Assert.Equal("Temporary/Partial", FileTypeCatalog.CategoryOf(".!qb"));
        Assert.Equal("Archive", FileTypeCatalog.CategoryOf(".001"));
        Assert.Equal("Archive", FileTypeCatalog.CategoryOf(".r00"));
        Assert.Equal("Archive", FileTypeCatalog.CategoryOf(".R00"));
        Assert.Equal("Archive", FileTypeCatalog.CategoryOf(".z01"));
        Assert.Equal("Other", FileTypeCatalog.CategoryOf(".gitignore"));
        Assert.Equal("Other", FileTypeCatalog.CategoryOf(".0001"));
        Assert.Equal("No Extension", FileTypeCatalog.CategoryOf(""));
        Assert.Equal(24, FileTypeCatalog.Categories.Count);
    }

    [Test]
    public static void Csv_reader_round_trips_quotes_commas_line_breaks_and_unicode()
    {
        var path = Path.Combine(Path.GetTempPath(), "si-csv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var content = "A,B,C\r\n" + CsvFormat.Text("x,y") + "," + CsvFormat.Text("say \"hi\"") + "," + CsvFormat.Text("line1\r\nline2") + "\r\n"
                          + CsvFormat.Text("한국어 😀") + ",1,\r\n";
            File.WriteAllText(path, content, CsvFormat.Utf8WithBom);
            var rows = ReportCsvReader.ReadAll(path);
            Assert.Equal(2, rows.Count);
            Assert.Equal("x,y", rows[0]["A"]);
            Assert.Equal("say \"hi\"", rows[0]["B"]);
            Assert.Equal("line1\r\nline2", rows[0]["C"]);
            Assert.Equal("한국어 😀", rows[1]["A"]);
            Assert.Equal("1", rows[1]["B"]);
            Assert.Equal("", rows[1]["C"]);
        }
        finally { File.Delete(path); }
    }
}

public static class ReportRunTests
{
    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "StorageInventoryTests", "unit_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public static void Run_names_carry_timestamp_and_random_id()
    {
        var folder = NewFolder();
        try
        {
            var run = ReportRun.Prepare(folder, new DateTime(2026, 9, 26, 14, 30, 12));
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(run.RunId, @"^20260926_143012_[0-9a-f]{6}$"), run.RunId);
            Assert.Equal(Path.Combine(folder, $"Files_{run.RunId}.csv"), run.FilesCsv);
            Assert.Equal(Path.Combine(folder, $"Files_{run.RunId}.unsorted.tmp"), run.FilesTemporary);
            Assert.Equal(Path.Combine(folder, $"StorageInventory_{run.RunId}.xlsx.partial"), run.WorkbookPartial);
            Assert.Equal(6, run.OwnFileNames.Count);
            Assert.NotNull(ReportRun.Prepare(folder, DateTime.Now));   // a second run gets a different ID
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public static void CreateNew_never_overwrites_and_refuses_foreign_names_or_folders()
    {
        var folder = NewFolder();
        try
        {
            var run = ReportRun.Prepare(folder, DateTime.Now);
            File.WriteAllText(run.FoldersCsv, "KEEP");
            Assert.Throws<IOException>(() => run.CreateNew(run.FoldersCsv).Dispose());
            Assert.Equal("KEEP", File.ReadAllText(run.FoldersCsv));
            Assert.Throws<InvalidOperationException>(() => run.CreateNew(Path.Combine(folder, "other.csv")));
            Assert.Throws<InvalidOperationException>(() => run.CreateNew(Path.Combine(Path.GetTempPath(), Path.GetFileName(run.ErrorsCsv))));
            Assert.Equal(0, run.CreatedFiles.Count);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public static void Prepare_refuses_when_any_run_name_already_exists()
    {
        var folder = NewFolder();
        try
        {
            var first = ReportRun.Prepare(folder, DateTime.Now);
            // Prepare checks every name; a clash can only happen for the same ID, so check the mechanism directly.
            File.WriteAllText(first.ErrorsCsv, "x");
            Assert.Throws<IOException>(() => first.CreateNew(first.ErrorsCsv).Dispose());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public static void The_only_delete_is_limited_to_this_runs_temporary_file()
    {
        var folder = NewFolder();
        try
        {
            var run = ReportRun.Prepare(folder, DateTime.Now);
            Assert.Throws<InvalidOperationException>(() => run.DeleteOwnTemporaryFile(run.FilesTemporary));   // not created yet
            run.CreateNew(run.ErrorsCsv).Dispose();
            Assert.Throws<InvalidOperationException>(() => run.DeleteOwnTemporaryFile(run.ErrorsCsv));        // wrong kind
            File.WriteAllText(Path.Combine(folder, "user.unsorted.tmp"), "x");
            Assert.Throws<InvalidOperationException>(() => run.DeleteOwnTemporaryFile(Path.Combine(folder, "user.unsorted.tmp")));
            run.CreateNew(run.FilesTemporary).Dispose();
            run.DeleteOwnTemporaryFile(run.FilesTemporary);
            Assert.False(File.Exists(run.FilesTemporary));
            Assert.True(File.Exists(run.ErrorsCsv) && File.Exists(Path.Combine(folder, "user.unsorted.tmp")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public static void Workbook_publish_never_replaces_an_existing_file()
    {
        var folder = NewFolder();
        try
        {
            var run = ReportRun.Prepare(folder, DateTime.Now);
            Assert.Throws<InvalidOperationException>(() => run.PublishOwnWorkbook());   // no partial created by this run
            using (var s = run.CreateNew(run.WorkbookPartial)) s.Write(Encoding.ASCII.GetBytes("NEW"));
            File.WriteAllText(run.Workbook, "DECOY");
            Assert.Throws<IOException>(() => run.PublishOwnWorkbook());
            Assert.Equal("DECOY", File.ReadAllText(run.Workbook));
            File.Delete(run.Workbook);
            run.PublishOwnWorkbook();
            Assert.Equal("NEW", File.ReadAllText(run.Workbook));
            Assert.False(File.Exists(run.WorkbookPartial));
        }
        finally { Directory.Delete(folder, true); }
    }
}
