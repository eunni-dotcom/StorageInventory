using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StorageInventory.Core.Reports;

/// <summary>
/// Report formatting, identical to the PowerShell reference: RFC 4180 quoting, the Excel formula guard, invariant
/// numbers (1 KB = 1024 bytes), local-time dates. This is presentation for people and external tools only; it is
/// never used as a machine interchange format.
/// </summary>
internal static partial class CsvFormat
{
    public const string FilesHeader = "FileName,Extension,FileType,RelativePath,RelativeDirectory,FullPath,SizeBytes,SizeKB,SizeMB,SizeGB,CreatedDate,ModifiedDate,LastAccessDate,Attributes";
    public const string FoldersHeader = "FolderName,RelativePath,ParentRelativePath,FullPath,Depth,TotalSizeBytes,TotalSizeKB,TotalSizeMB,TotalSizeGB,TotalSizeTB,PercentOfRoot,PercentOfParent,DirectSizeBytes,DirectSizeMB,DirectFileCount,TotalFileCount,DirectSubfolderCount,TotalSubfolderCount,AverageFileSizeMB,LargestFileSizeMB,LargestFileRelativePath,CreatedDate,ModifiedDate,Attributes,ScanStatus,SubtreeComplete";
    public const string ErrorsHeader = "Path,ErrorType,Message";

    /// <summary>UTF-8 with BOM: Excel needs the BOM to show non-ASCII names (e.g. Korean) correctly.</summary>
    public static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const string FormulaStartCharacters = "=+-@\t\r\n";
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreaks();

    /// <summary>A quoted text field. A value starting with = + - @ tab CR LF gets a leading apostrophe so Excel shows
    /// it as text instead of evaluating it (formula injection); embedded quotes are doubled. Values are only ever text.</summary>
    public static void AppendText(StringBuilder sb, string? value)
    {
        value ??= "";
        sb.Append('"');
        if (value.Length > 0 && FormulaStartCharacters.Contains(value[0])) sb.Append('\'');
        sb.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        sb.Append('"');
    }

    public static string Text(string? value)
    {
        var sb = new StringBuilder();
        AppendText(sb, value);
        return sb.ToString();
    }

    public static string Integer(long value) => value.ToString(Inv);

    /// <summary>bytes / unit, formatted with the given pattern, e.g. ("0.00", 1024) for KB.</summary>
    public static string Scaled(long bytes, double unit, string pattern) => (bytes / unit).ToString(pattern, Inv);

    public static string Number(double value, string pattern) => value.ToString(pattern, Inv);

    /// <summary>Local time; empty when the stored timestamp was not a valid date.</summary>
    public static string Date(DateTime? utc) => utc is { } t ? t.ToLocalTime().ToString(DateFormat, Inv) : "";

    /// <summary>Error messages on one line (same clean-up as the reference).</summary>
    public static string SingleLine(string message) => LineBreaks().Replace(message, " ").Trim();

    public const double KB = 1024.0;
    public const double MB = 1024.0 * 1024.0;
    public const double GB = 1024.0 * 1024.0 * 1024.0;
    public const double TB = 1024.0 * 1024.0 * 1024.0 * 1024.0;
}
