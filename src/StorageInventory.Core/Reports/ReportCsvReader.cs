using System.Text;

namespace StorageInventory.Core.Reports;

/// <summary>
/// Streaming RFC 4180 reader for StorageInventory's own CSV reports (quoted fields, doubled quotes, line breaks
/// inside quotes, UTF-8 with or without BOM). Reads one record at a time, so a multi-million-row Files report is
/// never loaded into memory. Read-only: the file is opened with FileAccess.Read.
/// </summary>
public sealed class ReportCsvReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly StringBuilder _field = new();

    public ReportCsvReader(string path)
    {
        _reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        Header = ReadRecord() ?? throw new InvalidDataException($"'{path}' has no header row.");
    }

    public IReadOnlyList<string> Header { get; }

    /// <summary>Index of a column by name, or -1.</summary>
    public int ColumnIndex(string name)
    {
        for (var i = 0; i < Header.Count; i++) if (string.Equals(Header[i], name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>The next record's fields, or null at the end of the file.</summary>
    public string[]? ReadRecord()
    {
        var fields = new List<string>();
        _field.Clear();
        var inQuotes = false;
        var any = false;
        while (true)
        {
            var c = _reader.Read();
            if (c < 0)
            {
                if (!any) return null;
                if (inQuotes) throw new InvalidDataException("Unterminated quoted field at end of file.");
                fields.Add(_field.ToString());
                return [.. fields];
            }
            any = true;
            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (_reader.Peek() == '"') { _reader.Read(); _field.Append('"'); }
                    else inQuotes = false;
                }
                else _field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': inQuotes = true; break;
                case ',': fields.Add(_field.ToString()); _field.Clear(); break;
                case '\r': break;
                case '\n': fields.Add(_field.ToString()); return [.. fields];
                default: _field.Append(ch); break;
            }
        }
    }

    /// <summary>All records as dictionaries keyed by column name (for small reports and tests only).</summary>
    public static List<Dictionary<string, string>> ReadAll(string path)
    {
        using var reader = new ReportCsvReader(path);
        var rows = new List<Dictionary<string, string>>();
        while (reader.ReadRecord() is { } record)
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < reader.Header.Count; i++) row[reader.Header[i]] = i < record.Length ? record[i] : "";
            rows.Add(row);
        }
        return rows;
    }

    public void Dispose() => _reader.Dispose();
}
