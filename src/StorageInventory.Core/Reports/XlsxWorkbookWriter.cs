using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace StorageInventory.Core.Reports;

/// <summary>
/// Writes a minimal Office Open XML workbook (.xlsx) with the built-in ZIP and XML libraries: no Excel, no COM, no
/// third-party package. Safety by construction: this writer has no code path that emits a formula (&lt;f&gt;) or a
/// hyperlink, so no filesystem text can ever become either. Text cells are inline strings holding exactly the CSV
/// text (including the leading-apostrophe formula guard); only known numeric columns become numbers.
/// Rows stream from the CSV reports into the ZIP entries, so the workbook is never held in memory.
/// </summary>
internal static class XlsxWorkbookWriter
{
    /// <summary>Excel's limit: 1,048,576 rows including the header.</summary>
    public const int ExcelMaxDataRows = 1_048_575;

    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public sealed record Sheet(string Name, string CsvPath, IReadOnlySet<string> NumericColumns);

    /// <summary>Writes the workbook to <paramref name="output"/>. Returns the number of data rows per sheet.</summary>
    public static IReadOnlyList<int> Write(Stream output, IReadOnlyList<Sheet> sheets, CancellationToken cancellationToken, Action<long>? onRow = null)
    {
        var rowCounts = new List<int>();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
        {
            for (var i = 0; i < sheets.Count; i++)
            {
                rowCounts.Add(WriteSheet(zip, $"xl/worksheets/sheet{i + 1}.xml", sheets[i], cancellationToken, onRow));
            }
            WriteEntry(zip, "[Content_Types].xml", w =>
            {
                w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Element(w, "Default", ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
                Element(w, "Default", ("Extension", "xml"), ("ContentType", "application/xml"));
                Element(w, "Override", ("PartName", "/xl/workbook.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
                Element(w, "Override", ("PartName", "/xl/styles.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
                for (var i = 0; i < sheets.Count; i++)
                    Element(w, "Override", ("PartName", $"/xl/worksheets/sheet{i + 1}.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
                w.WriteEndElement();
            });
            WriteEntry(zip, "_rels/.rels", w =>
            {
                w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                Element(w, "Relationship", ("Id", "rId1"), ("Type", RelationshipNs + "/officeDocument"), ("Target", "xl/workbook.xml"));
                w.WriteEndElement();
            });
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", w =>
            {
                w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                for (var i = 0; i < sheets.Count; i++)
                    Element(w, "Relationship", ("Id", $"rId{i + 1}"), ("Type", RelationshipNs + "/worksheet"), ("Target", $"worksheets/sheet{i + 1}.xml"));
                Element(w, "Relationship", ("Id", $"rId{sheets.Count + 1}"), ("Type", RelationshipNs + "/styles"), ("Target", "styles.xml"));
                w.WriteEndElement();
            });
            WriteEntry(zip, "xl/workbook.xml", w =>
            {
                w.WriteStartElement("workbook", SpreadsheetNs);
                w.WriteAttributeString("xmlns", "r", null, RelationshipNs);
                w.WriteStartElement("sheets", SpreadsheetNs);
                for (var i = 0; i < sheets.Count; i++)
                {
                    w.WriteStartElement("sheet", SpreadsheetNs);
                    w.WriteAttributeString("name", sheets[i].Name);
                    w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("id", RelationshipNs, $"rId{i + 1}");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteStartElement("definedNames", SpreadsheetNs);
                for (var i = 0; i < sheets.Count; i++)
                {
                    w.WriteStartElement("definedName", SpreadsheetNs);
                    w.WriteAttributeString("name", "_xlnm._FilterDatabase");
                    w.WriteAttributeString("localSheetId", i.ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("hidden", "1");
                    w.WriteString($"'{sheets[i].Name}'!$A$1:${ColumnName(ColumnCount(sheets[i]) - 1)}${rowCounts[i] + 1}");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement();
            });
            WriteEntry(zip, "xl/styles.xml", w =>
            {
                w.WriteStartElement("styleSheet", SpreadsheetNs);
                w.WriteStartElement("fonts", SpreadsheetNs); w.WriteAttributeString("count", "2");
                w.WriteStartElement("font", SpreadsheetNs); w.WriteEndElement();
                w.WriteStartElement("font", SpreadsheetNs); w.WriteStartElement("b", SpreadsheetNs); w.WriteEndElement(); w.WriteEndElement();
                w.WriteEndElement();
                w.WriteStartElement("fills", SpreadsheetNs); w.WriteAttributeString("count", "1");
                w.WriteStartElement("fill", SpreadsheetNs); w.WriteEndElement();
                w.WriteEndElement();
                w.WriteStartElement("borders", SpreadsheetNs); w.WriteAttributeString("count", "1");
                w.WriteStartElement("border", SpreadsheetNs); w.WriteEndElement();
                w.WriteEndElement();
                w.WriteStartElement("cellXfs", SpreadsheetNs); w.WriteAttributeString("count", "2");
                Element(w, "xf", ("fontId", "0"));
                Element(w, "xf", ("fontId", "1"), ("applyFont", "1"));
                w.WriteEndElement();
                w.WriteEndElement();
            });
        }
        return rowCounts;
    }

    private static int ColumnCount(Sheet sheet)
    {
        using var reader = new ReportCsvReader(sheet.CsvPath);
        return reader.Header.Count;
    }

    private static int WriteSheet(ZipArchive zip, string entryName, Sheet sheet, CancellationToken cancellationToken, Action<long>? onRow)
    {
        var rows = 0;
        WriteEntry(zip, entryName, w =>
        {
            using var reader = new ReportCsvReader(sheet.CsvPath);
            var numeric = reader.Header.Select(h => sheet.NumericColumns.Contains(h)).ToArray();
            w.WriteStartElement("worksheet", SpreadsheetNs);
            w.WriteStartElement("sheetViews", SpreadsheetNs);
            w.WriteStartElement("sheetView", SpreadsheetNs); w.WriteAttributeString("workbookViewId", "0");
            Element(w, "pane", ("ySplit", "1"), ("topLeftCell", "A2"), ("activePane", "bottomLeft"), ("state", "frozen"));
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("sheetData", SpreadsheetNs);
            WriteRow(w, 1, reader.Header, null, header: true);
            while (reader.ReadRecord() is { } record)
            {
                if (rows == ExcelMaxDataRows) throw new InvalidOperationException("Internal error: sheet exceeds Excel's row limit.");
                rows++;
                if ((rows & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onRow?.Invoke(rows);
                }
                WriteRow(w, rows + 1, record, numeric, header: false);
            }
            w.WriteEndElement();   // sheetData
            Element(w, "autoFilter", ("ref", $"A1:{ColumnName(reader.Header.Count - 1)}{rows + 1}"));
            w.WriteEndElement();   // worksheet
        });
        return rows;
    }

    private static void WriteRow(XmlWriter w, int rowNumber, IReadOnlyList<string> values, bool[]? numeric, bool header)
    {
        var r = rowNumber.ToString(CultureInfo.InvariantCulture);
        w.WriteStartElement("row", SpreadsheetNs);
        w.WriteAttributeString("r", r);
        for (var c = 0; c < values.Count; c++)
        {
            var value = values[c];
            if (value.Length == 0) continue;
            w.WriteStartElement("c", SpreadsheetNs);
            w.WriteAttributeString("r", ColumnName(c) + r);
            if (header) w.WriteAttributeString("s", "1");
            if (!header && numeric is not null && c < numeric.Length && numeric[c]
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                w.WriteElementString("v", SpreadsheetNs, value);   // already invariant-formatted by the CSV writer
            }
            else
            {
                // Inline string: never a formula, never a hyperlink - there is no other kind of cell this writer emits.
                w.WriteAttributeString("t", "inlineStr");
                w.WriteStartElement("is", SpreadsheetNs);
                w.WriteStartElement("t", SpreadsheetNs);
                w.WriteAttributeString("xml", "space", null, "preserve");
                w.WriteString(SanitizeForXml(value));
                w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    /// <summary>NTFS names can contain characters XML 1.0 cannot (lone surrogates, most control characters). In the
    /// workbook only, those become U+FFFD; the CSV reports are unaffected.</summary>
    internal static string SanitizeForXml(string value)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            var valid = char.IsHighSurrogate(ch)
                ? i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])
                : !char.IsLowSurrogate(ch) || (i > 0 && char.IsHighSurrogate(value[i - 1]));
            valid &= ch is '\t' or '\n' or '\r' || (ch >= 0x20 && ch != '￾' && ch != '￿');
            if (valid) { sb?.Append(ch); continue; }
            sb ??= new StringBuilder(value, 0, i, value.Length);
            sb.Append('�');
        }
        return sb?.ToString() ?? value;
    }

    internal static string ColumnName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    private static void WriteEntry(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = true });
        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
    }

    private static void Element(XmlWriter w, string name, params (string Name, string Value)[] attributes)
    {
        w.WriteStartElement(name);   // no namespace given: inherits the default namespace in scope
        foreach (var (n, v) in attributes) w.WriteAttributeString(n, v);
        w.WriteEndElement();
    }
}
