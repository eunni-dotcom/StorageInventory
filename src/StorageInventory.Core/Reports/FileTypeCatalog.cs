using System.Text.RegularExpressions;

namespace StorageInventory.Core.Reports;

/// <summary>
/// Extension to human-readable category, for reports only. The table is the PowerShell reference's, verbatim. Where
/// an extension is ambiguous, the more likely meaning on a personal media/data drive was chosen: .ts = video (not
/// TypeScript), .mdf/.bin = disc image, .sub = subtitle.
/// </summary>
public static partial class FileTypeCatalog
{
    public const string NoExtension = "No Extension";
    public const string Other = "Other";

    private static readonly (string Category, string Extensions)[] Definitions =
    [
        ("Image", "jpg jpeg jpe jfif png gif bmp dib tif tiff webp heic heif avif jxl ico cur svg svgz psd psb xcf ai eps tga dds exr hdr raw cr2 cr3 crw nef nrw arw srf sr2 dng orf rw2 raf pef srw x3f 3fr erf kdc mrw dcr"),
        ("Video", "mp4 m4v mkv mk3d avi mov qt wmv flv f4v webm mpg mpeg mpe m1v m2v mp2v ts m2ts mts tp trp vob 3gp 3g2 ogv ogm rm rmvb asf divx xvid mxf dv y4m h264 h265 hevc bik"),
        ("Audio", "mp3 mp2 flac wav wave aac m4a m4b m4p ogg oga opus wma aiff aif aifc alac ape wv dsf dff mka mid midi amr ac3 eac3 dts caf au snd ra tta tak mpc spx"),
        ("Archive", "zip zipx rar 7z tar gz tgz bz2 tbz tbz2 xz txz zst tzst lz lzma lz4 z cab arj lzh lha ace sit sitx cpio br"),
        ("Document", "pdf doc docx docm dot dotx dotm odt ott rtf txt text md markdown rst tex wpd wps pages xps oxps one"),
        ("Ebook", "epub mobi azw azw3 kfx fb2 djvu djv cbz cbr cb7 cbt lit"),
        ("Spreadsheet", "xls xlsx xlsm xlsb xlt xltx xltm ods ots csv tsv numbers"),
        ("Presentation", "ppt pptx pptm pps ppsx ppsm pot potx potm odp otp key"),
        ("Database", "db db3 sqlite sqlite3 sdb sdf mdb accdb accde dbf frm ibd myd myi ldf ndf kdbx kdb realm fdb gdb nsf"),
        ("Email", "eml emlx msg pst ost mbox mbx"),
        ("Executable/Application", "exe msi msix msixbundle appx appxbundle msp msu dll sys drv ocx cpl scr com efi apk xapk aab ipa jar deb rpm pkg"),
        ("Script/Code", "ps1 psm1 psd1 ps1xml bat cmd vbs vbe wsf js mjs cjs jsx tsx py pyw ipynb rb pl pm php java class kt kts scala groovy gradle c h cpp cc cxx hpp hh cs csx vb fs fsx go rs swift m mm dart lua r sql asm s sh bash zsh fish ahk au3 html htm xhtml css scss sass less vue svelte cshtml aspx jsp"),
        ("Data/Config", "json jsonl ndjson xml xsd xsl xslt yaml yml toml ini cfg conf config inf plist properties reg"),
        ("Subtitle", "srt ass ssa sub idx vtt sup smi sami sbv ttml dfxp usf lrc"),
        ("Torrent", "torrent"),
        ("Disc Image", "iso img bin cue nrg mdf mds ccd dmg vcd toast cdi b5t b6t isz daa uif wbfs gcm cso chd"),
        ("Virtual Disk", "vhd vhdx avhd avhdx vmdk vdi qcow qcow2 ova ovf hdd vmem vmsn vsv"),
        ("Font", "ttf otf ttc woff woff2 fon fnt eot pfb pfm afm"),
        ("Log/Dump", "log etl evtx dmp mdmp hdmp"),
        ("Shortcut", "lnk url website webloc desktop"),
        ("Metadata/Checksum", "nfo sfv md5 sha1 sha256 sha512 par par2 xmp thm aae"),
        ("Temporary/Partial", "tmp temp bak old swp swo part partial crdownload download !qb !ut !bt opdownload aria2"),
    ];

    private static readonly Dictionary<string, string> ByExtension = Build();

    // Case-insensitive, like the reference's PowerShell -match (".R00" is an archive part too).
    [GeneratedRegex(@"^\.(\d{3}|[rz]\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitArchivePart();

    /// <summary>All categories in table order (plus Other and No Extension).</summary>
    public static IReadOnlyList<string> Categories { get; } = [.. Definitions.Select(d => d.Category), Other, NoExtension];

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, extensions) in Definitions)
        {
            foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!map.TryAdd("." + ext, category))
                {
                    throw new InvalidOperationException($"Extension '.{ext}' is listed in more than one category.");
                }
            }
        }
        return map;
    }

    /// <param name="extension">As produced by the scanner: "" or starting with '.'.</param>
    public static string CategoryOf(string extension)
    {
        if (extension.Length == 0) return NoExtension;
        if (ByExtension.TryGetValue(extension, out var category)) return category;
        return SplitArchivePart().IsMatch(extension) ? "Archive" : Other;   // .001 / .r00 / .z01
    }
}
