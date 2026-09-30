using System.Runtime.InteropServices;
using System.Text;
using StorageInventory.Core.Scanning;

namespace StorageInventory.Core.Reports;

/// <summary>One entry of the compact sort index: 24 bytes per file, no file record kept.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct SortEntry(long Size, long Offset, int Length, int Sequence);

/// <summary>
/// Streams the Files report and the ScanErrors report while the scan runs. File rows go straight to disk: to the
/// final Files CSV (unsorted mode) or to the run's temporary file plus a compact sort index (sorted mode).
/// Write failures propagate and abort the scan; they are never recorded as scan errors.
/// </summary>
internal sealed class CsvReportSink : IScanSink, IDisposable
{
    private readonly bool _sortFiles;
    private readonly StringBuilder _row = new(512);
    private FileStream? _records;
    private StreamWriter? _errors;
    private byte[] _buffer = new byte[4096];
    private long _offset;

    public CsvReportSink(ReportRun run, bool sortFiles)
    {
        _sortFiles = sortFiles;
        _errors = new StreamWriter(run.CreateNew(run.ErrorsCsv), CsvFormat.Utf8WithBom, 65536) { NewLine = "\r\n" };
        _errors.WriteLine(CsvFormat.ErrorsHeader);

        if (sortFiles)
        {
            _records = run.CreateNew(run.FilesTemporary);
        }
        else
        {
            _records = run.CreateNew(run.FilesCsv);
            WriteBomAndHeader(_records, CsvFormat.FilesHeader);
        }
    }

    /// <summary>Sort index in discovery order (sorted mode only).</summary>
    public List<SortEntry> SortIndex { get; } = [];

    public int LongestRowBytes { get; private set; }

    public void OnFile(in FileInventoryRecord file, int folderIndex)
    {
        var sb = _row.Clear();
        CsvFormat.AppendText(sb, file.FileName); sb.Append(',');
        CsvFormat.AppendText(sb, file.Extension); sb.Append(',');
        CsvFormat.AppendText(sb, FileTypeCatalog.CategoryOf(file.Extension)); sb.Append(',');
        CsvFormat.AppendText(sb, file.RelativePath); sb.Append(',');
        CsvFormat.AppendText(sb, file.RelativeDirectory); sb.Append(',');
        CsvFormat.AppendText(sb, file.FullPath); sb.Append(',');
        sb.Append(CsvFormat.Integer(file.SizeBytes)).Append(',');
        sb.Append(CsvFormat.Scaled(file.SizeBytes, CsvFormat.KB, "0.00")).Append(',');
        sb.Append(CsvFormat.Scaled(file.SizeBytes, CsvFormat.MB, "0.00")).Append(',');
        sb.Append(CsvFormat.Scaled(file.SizeBytes, CsvFormat.GB, "0.000")).Append(',');
        sb.Append(CsvFormat.Date(file.CreatedUtc)).Append(',');
        sb.Append(CsvFormat.Date(file.ModifiedUtc)).Append(',');
        sb.Append(CsvFormat.Date(file.LastAccessUtc)).Append(',');
        CsvFormat.AppendText(sb, file.Attributes.ToString());
        sb.Append("\r\n");

        var length = WriteRow(sb);
        if (_sortFiles)
        {
            if (SortIndex.Count == int.MaxValue) throw new InvalidOperationException("Too many files to sort; use unsorted mode.");
            SortIndex.Add(new SortEntry(file.SizeBytes, _offset, length, SortIndex.Count));
        }
        _offset += length;
        if (length > LongestRowBytes) LongestRowBytes = length;
    }

    public void OnError(ScanErrorRecord error)
    {
        var sb = _row.Clear();
        CsvFormat.AppendText(sb, error.Path); sb.Append(',');
        CsvFormat.AppendText(sb, error.Type.ToString()); sb.Append(',');
        CsvFormat.AppendText(sb, CsvFormat.SingleLine(error.Message));
        _errors!.WriteLine(sb.ToString());
    }

    /// <summary>Flushes and closes the Files stream (temporary or final).</summary>
    public void CloseFileRows()
    {
        _records?.Dispose();
        _records = null;
    }

    public void CloseErrors()
    {
        _errors?.Dispose();
        _errors = null;
    }

    public void Dispose()
    {
        CloseFileRows();
        CloseErrors();
    }

    private int WriteRow(StringBuilder sb)
    {
        var chars = sb.Length;
        var maxBytes = CsvFormat.Utf8NoBom.GetMaxByteCount(chars);
        if (_buffer.Length < maxBytes) _buffer = new byte[Math.Max(maxBytes, _buffer.Length * 2)];
        var charArray = System.Buffers.ArrayPool<char>.Shared.Rent(chars);
        try
        {
            sb.CopyTo(0, charArray, 0, chars);
            var bytes = CsvFormat.Utf8NoBom.GetBytes(charArray, 0, chars, _buffer, 0);
            _records!.Write(_buffer, 0, bytes);
            return bytes;
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(charArray);
        }
    }

    internal static void WriteBomAndHeader(Stream stream, string header)
    {
        stream.Write(CsvFormat.Utf8WithBom.Preamble);
        stream.Write(CsvFormat.Utf8NoBom.GetBytes(header + "\r\n"));
    }
}
