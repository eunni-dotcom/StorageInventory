using StorageInventory.Core.Reports;
using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

public static class XlsxWriterTests
{
    [Test]
    public static void Column_names_follow_spreadsheet_lettering()
    {
        Assert.Equal("A", XlsxWorkbookWriter.ColumnName(0));
        Assert.Equal("Z", XlsxWorkbookWriter.ColumnName(25));
        Assert.Equal("AA", XlsxWorkbookWriter.ColumnName(26));
        Assert.Equal("AZ", XlsxWorkbookWriter.ColumnName(51));
    }

    [Test]
    public static void Characters_XML_cannot_hold_become_replacement_characters_only_in_the_workbook()
    {
        Assert.Equal("plain 한국어 😀", XlsxWorkbookWriter.SanitizeForXml("plain 한국어 😀"));
        Assert.Equal("a�b", XlsxWorkbookWriter.SanitizeForXml("a\uD800b"));          // lone high surrogate
        Assert.Equal("a�b", XlsxWorkbookWriter.SanitizeForXml("a\uDC00b"));          // lone low surrogate
        Assert.Equal("a�b", XlsxWorkbookWriter.SanitizeForXml("a\u0001b"));          // control character
        Assert.Equal("tab\tok", XlsxWorkbookWriter.SanitizeForXml("tab\tok"));
    }
}
