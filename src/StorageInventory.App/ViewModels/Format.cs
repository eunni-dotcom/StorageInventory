using System.Globalization;

namespace StorageInventory.App.ViewModels;

/// <summary>Display formatting (binary units, as Windows Explorer uses: 1 KB = 1024 bytes).</summary>
public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        if (bytes < 1024) return bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(value >= 100 ? "N0" : "N2", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static string Elapsed(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public static string Percent(double p) => p.ToString("0.0", CultureInfo.CurrentCulture) + " %";
}
