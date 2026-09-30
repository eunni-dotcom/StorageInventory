#Requires -Version 5.1
<#
.SYNOPSIS
    Read-only storage inventory of a directory tree. Writes per-file and per-folder CSV reports
    (plus an optional Excel workbook) showing exactly what is consuming storage.

.DESCRIPTION
    The tree under -RootPath is walked ONCE using .NET directory enumeration. Only file-system
    METADATA is read (names, sizes, timestamps, attributes). No file is ever opened, and nothing
    inside the scanned tree is created, modified, moved, renamed or deleted.

    Reports written to -OutputPath (<id> = run timestamp + random run ID, e.g. 20260926_143012_a1b2c3):

      Files_<id>.csv          One row per file. Sorted largest-first unless -NoSort is used.
      Folders_<id>.csv        One row per folder (including the root) with direct and recursive
                              totals, percentages and largest file. Sorted by TotalSizeBytes, largest first.
      ScanErrors_<id>.csv     Everything that could not be read, plus informational rows for every
                              reparse point (junction / symbolic link / mount point / placeholder):
                              folder reparse points are NOT entered; file reparse points are counted
                              with the size the directory listing reports, and their targets are not followed.
      StorageInventory_<id>.xlsx
                              Optional post-processing of the COMPLETED CSV reports. Only if the ImportExcel
                              module is ALREADY installed and -SkipExcel was not given. Never installed by
                              this script. The CSV files are always the authoritative result.

    Safety guarantees (enforced in code, see the comments marked SAFETY):
      * OutputPath must not be RootPath or anywhere inside it; the script refuses to run otherwise.
      * RootPath and OutputPath must not pass through a junction, symbolic link or other reparse point.
      * Every report file (including the workbook) is created by this script with FileMode.CreateNew,
        so an existing file can never be overwritten. ImportExcel builds the workbook in memory only
        and is never given a file path.
      * Reparse points found during the scan are recorded but never followed (no loops, no leaving
        the drive, no wandering onto network shares).
      * No network access, no external programs, no registry access, no elevation, no dynamic code.

    Exit code: 0 = scan complete; 2 = scan finished but some folders/entries could not be read, so
    totals are lower bounds; 1 (terminating error) = failed or cancelled.

.PARAMETER RootPath
    Folder or drive to inventory. Defaults to the current PowerShell location.
    A bare drive letter such as 'D:' is treated as the drive root 'D:\'.
    A UNC path (\\server\share\folder) is scanned only if you name it explicitly here.

.PARAMETER OutputPath
    Folder that receives the reports. Created if missing. Must be OUTSIDE RootPath.
    Defaults to '<your user profile>\StorageInventory' (not a OneDrive-synced folder).

.PARAMETER NoSort
    Write Files_<id>.csv in discovery order instead of largest-first. This avoids the temporary
    file and the in-memory sort index (20 bytes of index data per file, plus working arrays of
    similar size while sorting).

.PARAMETER SkipExcel
    Do not create the .xlsx workbook. ImportExcel is then neither searched for nor loaded, so the script runs
    nothing except PowerShell's own built-in commands and .NET.

.EXAMPLE
    .\StorageInventory.ps1 -RootPath 'D:\' -OutputPath "$env:USERPROFILE\Inventory"

    Inventory the whole of drive D:, writing the reports to C:\Users\<you>\Inventory.

.EXAMPLE
    .\StorageInventory.ps1 -RootPath 'D:\Media' -OutputPath 'E:\Reports'

    Inventory one folder. Paths inside the report are relative to D:\Media.

.EXAMPLE
    .\StorageInventory.ps1 -RootPath 'D:\' -OutputPath 'C:\Reports' -NoSort -SkipExcel

    Fastest mode for very large drives: unsorted Files CSV, no temporary file, no Excel.

.OUTPUTS
    None to the pipeline. Report files are written to OutputPath and a summary to the console.

.NOTES
    Temporary file: unless -NoSort is used, file rows are first streamed to
    'Files_<id>.unsorted.tmp' in OutputPath (never anywhere else). After the scan the rows are
    copied into Files_<id>.csv in size order, and the temporary file is deleted. That delete is the
    ONLY delete operation in the script, and it is restricted to that exact file created by this run.
    If the run is interrupted the temporary file is left in place (it is plain CSV text) and the
    console lists it.

    Workbook: written first as 'StorageInventory_<id>.xlsx.partial' (CreateNew) and then renamed to
    the final name with a rename that fails rather than replace an existing file. A file with the
    final .xlsx name is therefore always complete; a leftover '.partial' file is always incomplete.

    ErrorType values in ScanErrors: AccessDenied, NotFound (vanished during the scan), PathTooLong,
    IOError (locked, device not ready, network error...), InvalidPath, InvalidTimestamp,
    UnexpectedError, plus two informational types that are NOT errors:
    ReparsePointSkipped (a folder link that was not entered) and ReparsePointFile (a file reparse
    point counted with its listed size; for a symbolic link that is the size of the link itself).

    ScanStatus (Folders report) describes the folder itself: OK, AccessDenied/NotFound/... (could not
    be listed), Partial:<ErrorType> (listed, but some entries could not be read), ReparsePointSkipped.
    SubtreeComplete=False means this folder OR something beneath it could not be fully read.

    Excel formula safety: any text value that begins with = + - @ (or a tab/CR/LF) is written with a
    leading apostrophe so that Excel shows it as text instead of evaluating it as a formula.
    FullPath columns always start with a drive letter or \\ and are therefore never altered.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string] $RootPath = '.',

    [Parameter(Position = 1)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputPath = [System.IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), 'StorageInventory'),

    [switch] $NoSort,

    [switch] $SkipExcel
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

#region ---- Types and constants ---------------------------------------------------------------------

# One record per folder. Files are NOT kept in memory; only these per-folder counters are.
class InventoryFolder {
    [int]    $ParentIndex            # index of the parent record (-1 for the root). Always smaller than this record's index.
    [int]    $Depth                  # 0 = root
    [string] $Name
    [string] $RelativePath           # '.' for the root
    [string] $CreatedText
    [string] $ModifiedText
    [string] $Attributes
    [long]   $DirectSizeBytes        # files directly inside this folder
    [long]   $DirectFileCount
    [long]   $DirectSubfolderCount
    [long]   $TotalSizeBytes         # this folder plus all descendants (filled in by the aggregation pass)
    [long]   $TotalFileCount
    [long]   $TotalSubfolderCount
    [long]   $LargestFileBytes = -1  # largest file anywhere in this subtree (-1 = no files)
    [string] $LargestFileRelativePath = ''
    [string] $ScanStatus = 'OK'
    [bool]   $SubtreeComplete = $true
    [System.IO.DirectoryInfo] $PendingDirectory   # set only while the folder is waiting to be listed
}

$Inv                = [System.Globalization.CultureInfo]::InvariantCulture  # '.' decimal separator regardless of Windows locale
$DateFormat         = 'yyyy-MM-dd HH:mm:ss'
$Utf8Bom            = [System.Text.UTF8Encoding]::new($true)   # Excel needs the BOM to show non-ASCII names (e.g. Korean) correctly
$Utf8NoBom          = [System.Text.UTF8Encoding]::new($false)
$ExcelMaxDataRows   = 1048575                                  # Excel worksheet limit: 1,048,576 rows including the header
$ProgressIntervalMs = 500
$FormulaStartChars  = "=+-@`t`r`n"                             # a cell starting with one of these is treated as a formula by Excel

$FilesHeader   = 'FileName,Extension,FileType,RelativePath,RelativeDirectory,FullPath,SizeBytes,SizeKB,SizeMB,SizeGB,CreatedDate,ModifiedDate,LastAccessDate,Attributes'
$FoldersHeader = 'FolderName,RelativePath,ParentRelativePath,FullPath,Depth,TotalSizeBytes,TotalSizeKB,TotalSizeMB,TotalSizeGB,TotalSizeTB,PercentOfRoot,PercentOfParent,DirectSizeBytes,DirectSizeMB,DirectFileCount,TotalFileCount,DirectSubfolderCount,TotalSubfolderCount,AverageFileSizeMB,LargestFileSizeMB,LargestFileRelativePath,CreatedDate,ModifiedDate,Attributes,ScanStatus,SubtreeComplete'
$ErrorsHeader  = 'Path,ErrorType,Message'

# Text columns, so that ImportExcel does not turn a file called '2024' into a number.
$FilesTextColumns   = @('FileName', 'Extension', 'FileType', 'RelativePath', 'RelativeDirectory', 'FullPath', 'CreatedDate', 'ModifiedDate', 'LastAccessDate', 'Attributes')
$FoldersTextColumns = @('FolderName', 'RelativePath', 'ParentRelativePath', 'FullPath', 'LargestFileRelativePath', 'CreatedDate', 'ModifiedDate', 'Attributes', 'ScanStatus', 'SubtreeComplete')

# Extension -> human-readable category. Where an extension is ambiguous, the more likely meaning on a
# personal media/data drive was chosen: .ts = video (not TypeScript), .mdf/.bin = disc image, .sub = subtitle.
$FileCategoryDefinitions = [ordered]@{
    'Image'                  = 'jpg jpeg jpe jfif png gif bmp dib tif tiff webp heic heif avif jxl ico cur svg svgz psd psb xcf ai eps tga dds exr hdr raw cr2 cr3 crw nef nrw arw srf sr2 dng orf rw2 raf pef srw x3f 3fr erf kdc mrw dcr'
    'Video'                  = 'mp4 m4v mkv mk3d avi mov qt wmv flv f4v webm mpg mpeg mpe m1v m2v mp2v ts m2ts mts tp trp vob 3gp 3g2 ogv ogm rm rmvb asf divx xvid mxf dv y4m h264 h265 hevc bik'
    'Audio'                  = 'mp3 mp2 flac wav wave aac m4a m4b m4p ogg oga opus wma aiff aif aifc alac ape wv dsf dff mka mid midi amr ac3 eac3 dts caf au snd ra tta tak mpc spx'
    'Archive'                = 'zip zipx rar 7z tar gz tgz bz2 tbz tbz2 xz txz zst tzst lz lzma lz4 z cab arj lzh lha ace sit sitx cpio br'
    'Document'               = 'pdf doc docx docm dot dotx dotm odt ott rtf txt text md markdown rst tex wpd wps pages xps oxps one'
    'Ebook'                  = 'epub mobi azw azw3 kfx fb2 djvu djv cbz cbr cb7 cbt lit'
    'Spreadsheet'            = 'xls xlsx xlsm xlsb xlt xltx xltm ods ots csv tsv numbers'
    'Presentation'           = 'ppt pptx pptm pps ppsx ppsm pot potx potm odp otp key'
    'Database'               = 'db db3 sqlite sqlite3 sdb sdf mdb accdb accde dbf frm ibd myd myi ldf ndf kdbx kdb realm fdb gdb nsf'
    'Email'                  = 'eml emlx msg pst ost mbox mbx'
    'Executable/Application' = 'exe msi msix msixbundle appx appxbundle msp msu dll sys drv ocx cpl scr com efi apk xapk aab ipa jar deb rpm pkg'
    'Script/Code'            = 'ps1 psm1 psd1 ps1xml bat cmd vbs vbe wsf js mjs cjs jsx tsx py pyw ipynb rb pl pm php java class kt kts scala groovy gradle c h cpp cc cxx hpp hh cs csx vb fs fsx go rs swift m mm dart lua r sql asm s sh bash zsh fish ahk au3 html htm xhtml css scss sass less vue svelte cshtml aspx jsp'
    'Data/Config'            = 'json jsonl ndjson xml xsd xsl xslt yaml yml toml ini cfg conf config inf plist properties reg'
    'Subtitle'               = 'srt ass ssa sub idx vtt sup smi sami sbv ttml dfxp usf lrc'
    'Torrent'                = 'torrent'
    'Disc Image'             = 'iso img bin cue nrg mdf mds ccd dmg vcd toast cdi b5t b6t isz daa uif wbfs gcm cso chd'
    'Virtual Disk'           = 'vhd vhdx avhd avhdx vmdk vdi qcow qcow2 ova ovf hdd vmem vmsn vsv'
    'Font'                   = 'ttf otf ttc woff woff2 fon fnt eot pfb pfm afm'
    'Log/Dump'               = 'log etl evtx dmp mdmp hdmp'
    'Shortcut'               = 'lnk url website webloc desktop'
    'Metadata/Checksum'      = 'nfo sfv md5 sha1 sha256 sha512 par par2 xmp thm aae'
    'Temporary/Partial'      = 'tmp temp bak old swp swo part partial crdownload download !qb !ut !bt opdownload aria2'
}

$FileTypeByExtension = [System.Collections.Hashtable]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($category in $FileCategoryDefinitions.Keys) {
    foreach ($extensionWord in ($FileCategoryDefinitions[$category] -split '\s+')) {
        if ($extensionWord.Length -eq 0) { continue }
        $key = '.' + $extensionWord
        if ($FileTypeByExtension.ContainsKey($key)) { throw "Internal error: extension '$key' is listed in more than one category." }
        $FileTypeByExtension[$key] = $category
    }
}

#endregion

#region ---- Helper functions ------------------------------------------------------------------------

function Resolve-FileSystemPath {
    # Converts user input to an absolute, normalised file-system path.
    # SAFETY: the input is treated strictly as a literal path - no wildcard expansion, no evaluation.
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $ParameterName
    )

    $candidate = $Path.Trim()
    if ($candidate -match '^[\\/]{2}[?.][\\/]') {
        throw "$ParameterName '$Path' uses a device-path prefix (\\?\ or \\.\), which this script does not accept. Use a normal path such as 'D:\Media' or '\\server\share\folder'."
    }
    # To Windows, 'D:' alone means "the current directory on drive D". The user almost certainly means the drive root.
    if ($candidate -match '^[A-Za-z]:$') { $candidate += '\' }

    $provider = $null
    $drive = $null
    try {
        # "Unresolved" = no wildcard globbing; relative paths resolve against the current PowerShell location.
        $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($candidate, [ref]$provider, [ref]$drive)
        $fullPath = [System.IO.Path]::GetFullPath($resolved)
    }
    catch {
        throw "$ParameterName '$Path' is not a valid path: $($_.Exception.Message)"
    }
    if ($null -eq $provider -or $provider.Name -ne 'FileSystem') {
        throw "$ParameterName '$Path' is not a file-system path."
    }

    # Drop trailing separators, but keep the one that makes 'D:\' a drive root.
    $fullPath = $fullPath.TrimEnd('\', '/')
    if ($fullPath -match '^[A-Za-z]:$') { $fullPath += '\' }

    # SAFETY: reject spellings that Windows silently rewrites, which could make two different-looking paths the same
    # folder and defeat the "output must be outside the root" check:
    #  - device paths produced by normalisation (e.g. 'C:\Temp\nul' becomes '\\.\nul') and reserved device names,
    #    including the historical superscript forms COM1-3/LPT1-3 written with U+00B9, U+00B2, U+00B3. [0-9] is used
    #    instead of \d because .NET's \d also matches non-ASCII digits, which Windows does NOT reserve;
    #  - a ':' after the drive letter (alternate-data-stream syntax such as 'D:\Out:stream');
    #  - a folder name ending in '.' or ' ' (Windows strips these, so 'D:\Media.\Out' is really 'D:\Media\Out').
    if ($fullPath -match '^[\\/]{2}[?.][\\/]' -or
        $fullPath -match '(^|\\)(CON|PRN|AUX|NUL|(COM|LPT)[0-9\u00B9\u00B2\u00B3])(\.[^\\]*)?(\\|$)') {
        throw "$ParameterName '$Path' refers to a Windows device name (CON, NUL, COM1...), which is not allowed."
    }
    if ($fullPath.IndexOf(':', 2) -ge 0) {
        throw "$ParameterName '$Path' contains ':' after the drive letter, which is not allowed."
    }
    if ($fullPath -match '[. ](\\|$)') {
        throw "$ParameterName '$Path' has a folder name ending in '.' or a space, which Windows silently changes. Please use the exact folder name."
    }
    return $fullPath
}

function Test-IsSameOrInside {
    # True if CandidatePath equals ContainerPath or lies anywhere beneath it (case-insensitive, whole path segments).
    param(
        [Parameter(Mandatory)] [string] $CandidatePath,
        [Parameter(Mandatory)] [string] $ContainerPath
    )
    $candidate = $CandidatePath.TrimEnd('\') + '\'
    $container = $ContainerPath.TrimEnd('\') + '\'
    return $candidate.StartsWith($container, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-ReparsePointDescription {
    # Describes a reparse point for the report. LinkType/Target are read-only PowerShell properties that read
    # the link's reparse data without following it.
    param([Parameter(Mandatory)] [System.IO.FileSystemInfo] $Item)

    $linkType = $null
    $target = ''
    try {
        $linkType = $Item.LinkType
        $target = @($Item.Target) -join '; '
    }
    catch {
        # Reading the reparse data can fail (e.g. access denied). Fall through to the generic description.
        $linkType = $null
    }
    if ($linkType) {
        if ($target) { return "$linkType -> $target" }
        return [string]$linkType
    }
    return 'reparse point of undetermined type (e.g. a cloud-storage placeholder, or a link whose target could not be read)'
}

function Find-ReparsePointInPath {
    # Walks from Path up to its drive/share root. Returns a description of the first existing component that is a
    # reparse point (junction, symbolic link, mount point, placeholder...), or $null if there is none.
    param([Parameter(Mandatory)] [string] $Path)

    $current = [System.IO.DirectoryInfo]::new($Path)
    while ($null -ne $current) {
        if ($current.Exists -and (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            return '{0} [{1}]' -f $current.FullName, (Get-ReparsePointDescription -Item $current)
        }
        $current = $current.Parent
    }
    return $null
}

function Test-IsNetworkPath {
    # True for UNC paths and mapped network drives. GetDriveType is a read-only query.
    param([Parameter(Mandatory)] [string] $Path)
    if ($Path.StartsWith('\\')) { return $true }
    try {
        return ([System.IO.DriveInfo]::new($Path.Substring(0, 1)).DriveType -eq [System.IO.DriveType]::Network)
    }
    catch {
        return $false
    }
}

function New-ReportFileStream {
    # SAFETY: the ONLY way this script creates a file. The file must sit directly inside the validated output folder,
    # and FileMode.CreateNew makes the call fail if the file already exists, so nothing can ever be overwritten.
    param([Parameter(Mandatory)] [string] $Path)

    if (-not [string]::Equals([System.IO.Path]::GetDirectoryName($Path), $script:OutputFolder, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Internal safety check failed: refusing to create '$Path' outside the output folder."
    }
    $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read, 1048576)
    $script:CreatedReportFiles.Add($Path)
    return $stream
}

function New-ReportWriter {
    # UTF-8 (with BOM) text writer on top of New-ReportFileStream.
    param([Parameter(Mandatory)] [string] $Path)
    return [System.IO.StreamWriter]::new((New-ReportFileStream -Path $Path), $script:Utf8Bom, 65536)
}

function Remove-OwnTemporaryFile {
    # SAFETY: the ONLY delete operation in this script. It refuses anything except the '.unsorted.tmp' file that THIS
    # run created (via New-ReportFileStream) directly inside the output folder.
    param([Parameter(Mandatory)] [string] $Path)

    $isOwnFile   = $script:CreatedReportFiles.Contains($Path)
    $isTempName  = $Path.EndsWith('.unsorted.tmp', [System.StringComparison]::OrdinalIgnoreCase)
    $isInOutput  = [string]::Equals([System.IO.Path]::GetDirectoryName($Path), $script:OutputFolder, [System.StringComparison]::OrdinalIgnoreCase)
    if (-not ($isOwnFile -and $isTempName -and $isInOutput)) {
        throw "Internal safety check failed: refusing to delete '$Path'."
    }
    [System.IO.File]::Delete($Path)
    [void]$script:CreatedReportFiles.Remove($Path)
}

function Complete-OwnWorkbookFile {
    # SAFETY: the ONLY rename in this script. It gives the finished '.xlsx.partial' file that THIS run created its final
    # name. File.Move never replaces an existing file (it throws instead), so nothing can be overwritten.
    param(
        [Parameter(Mandatory)] [string] $PartialPath,
        [Parameter(Mandatory)] [string] $FinalPath
    )
    $isOwnFile    = $script:CreatedReportFiles.Contains($PartialPath)
    $isPartial    = $PartialPath.EndsWith('.xlsx.partial', [System.StringComparison]::OrdinalIgnoreCase)
    $isFinalXlsx  = [string]::Equals($FinalPath + '.partial', $PartialPath, [System.StringComparison]::OrdinalIgnoreCase)
    $bothInOutput = [string]::Equals([System.IO.Path]::GetDirectoryName($PartialPath), $script:OutputFolder, [System.StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals([System.IO.Path]::GetDirectoryName($FinalPath), $script:OutputFolder, [System.StringComparison]::OrdinalIgnoreCase)
    if (-not ($isOwnFile -and $isPartial -and $isFinalXlsx -and $bothInOutput)) {
        throw "Internal safety check failed: refusing to rename '$PartialPath'."
    }
    [System.IO.File]::Move($PartialPath, $FinalPath)
    [void]$script:CreatedReportFiles.Remove($PartialPath)
    $script:CreatedReportFiles.Add($FinalPath)
}

function Get-CsvTextBody {
    # The formula-guard rule: a value starting with = + - @ tab CR LF gets a leading apostrophe so Excel shows it as
    # text instead of evaluating it (CSV/formula injection). Embedded double quotes are doubled (RFC 4180).
    # Returns the field body WITHOUT the surrounding quotes. The value is only ever treated as text.
    param([string] $Value)
    $body = $Value.Replace('"', '""')
    if ($Value.Length -gt 0 -and $script:FormulaStartChars.IndexOf($Value[0]) -ge 0) {
        $body = "'" + $body
    }
    return $body
}

function ConvertTo-CsvField {
    # A complete, quoted CSV text field.
    param([string] $Value)
    return '"' + (Get-CsvTextBody $Value) + '"'
}

function Get-ScanErrorInfo {
    # Classifies an exception (unwrapping PowerShell wrapper exceptions) into a short ErrorType plus message.
    param([Parameter(Mandatory)] [System.Exception] $Exception)

    $current = $Exception
    while ($null -ne $current) {
        $type = $null
        if ($current -is [System.UnauthorizedAccessException] -or $current -is [System.Security.SecurityException]) { $type = 'AccessDenied' }
        elseif ($current -is [System.IO.PathTooLongException]) { $type = 'PathTooLong' }
        elseif ($current -is [System.IO.DirectoryNotFoundException] -or $current -is [System.IO.FileNotFoundException]) { $type = 'NotFound' }
        elseif ($current -is [System.IO.IOException]) { $type = 'IOError' }
        elseif ($current -is [System.ArgumentException] -or $current -is [System.NotSupportedException]) { $type = 'InvalidPath' }

        if ($null -ne $type) { return [pscustomobject]@{ ErrorType = $type; Message = $current.Message } }
        $current = $current.InnerException
    }
    return [pscustomobject]@{ ErrorType = 'UnexpectedError'; Message = $Exception.Message }
}

function Add-ScanErrorRecord {
    # Appends one row to ScanErrors_<ts>.csv and updates the counters.
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $ErrorType,
        [string] $Message
    )
    $cleanMessage = ([string]$Message -replace '\s*[\r\n]+\s*', ' ').Trim()
    $row = @(
        (ConvertTo-CsvField $Path),
        (ConvertTo-CsvField $ErrorType),
        (ConvertTo-CsvField $cleanMessage)
    ) -join ','
    $script:ErrorWriter.WriteLine($row)

    # Reparse-point rows are informational (behaviour by design), not scan failures.
    if ($ErrorType -eq 'ReparsePointSkipped') { $script:ReparseSkipCount++ }
    elseif ($ErrorType -eq 'ReparsePointFile') { $script:ReparseFileCount++ }
    else { $script:ErrorCount++ }
    $script:ErrorCountByType[$ErrorType] = 1 + [int]$script:ErrorCountByType[$ErrorType]
}

function Get-TimestampText {
    # Formats a folder's Created/Modified time. Corrupt timestamps are logged and left blank instead of aborting.
    param(
        [Parameter(Mandatory)] [System.IO.FileSystemInfo] $Item,
        [Parameter(Mandatory)] [string] $Kind
    )
    try {
        if ($Kind -eq 'Created') { $value = $Item.CreationTime } else { $value = $Item.LastWriteTime }
        return $value.ToString($script:DateFormat, $script:Inv)
    }
    catch {
        Add-ScanErrorRecord -Path $Item.FullName -ErrorType 'InvalidTimestamp' -Message ("$Kind time: " + $_.Exception.Message)
        return ''
    }
}

function Add-FolderRecord {
    # Creates the record for a newly discovered folder and returns its index.
    # Because a folder is only discovered while its parent is being listed, a child's index is always
    # greater than its parent's. The aggregation pass relies on this.
    param(
        [Parameter(Mandatory)] [System.IO.DirectoryInfo] $Directory,
        [Parameter(Mandatory)] [int] $ParentIndex,
        [Parameter(Mandatory)] [int] $Depth,
        [Parameter(Mandatory)] [string] $RelativePath
    )
    $record = [InventoryFolder]::new()
    $record.ParentIndex  = $ParentIndex
    $record.Depth        = $Depth
    $record.Name         = $Directory.Name
    $record.RelativePath = $RelativePath
    $record.Attributes   = $Directory.Attributes.ToString()
    $record.CreatedText  = Get-TimestampText -Item $Directory -Kind 'Created'
    $record.ModifiedText = Get-TimestampText -Item $Directory -Kind 'Modified'
    $script:Folders.Add($record)
    return ($script:Folders.Count - 1)
}

function Set-StableTieOrder {
    # Array.Sort is not stable. Within each run of equal keys, restore ascending original order so the output is
    # deterministic (and a folder always appears before an equally-sized child). Keys/Items are sorted in place.
    param(
        [Parameter(Mandatory)] [long[]] $Keys,
        [Parameter(Mandatory)] [int[]] $Items
    )
    $count = $Keys.Length
    $runStart = 0
    while ($runStart -lt $count) {
        $runEnd = $runStart + 1
        while ($runEnd -lt $count -and $Keys[$runEnd] -eq $Keys[$runStart]) { $runEnd++ }
        if (($runEnd - $runStart) -gt 1) { [System.Array]::Sort($Items, $runStart, $runEnd - $runStart) }
        $runStart = $runEnd
    }
}

function Format-ByteSize {
    # Human-readable binary size (1 KB = 1024 bytes) for console output.
    param([long] $Bytes)
    if ($Bytes -ge 1TB) { return ($Bytes / 1TB).ToString('0.00', $script:Inv) + ' TB' }
    if ($Bytes -ge 1GB) { return ($Bytes / 1GB).ToString('0.00', $script:Inv) + ' GB' }
    if ($Bytes -ge 1MB) { return ($Bytes / 1MB).ToString('0.00', $script:Inv) + ' MB' }
    if ($Bytes -ge 1KB) { return ($Bytes / 1KB).ToString('0.00', $script:Inv) + ' KB' }
    return "$Bytes bytes"
}

function Format-Elapsed {
    param([TimeSpan] $Span)
    return '{0:00}:{1:00}:{2:00}' -f [Math]::Floor($Span.TotalHours), $Span.Minutes, $Span.Seconds
}

function Show-ScanProgress {
    param([string] $Root, [long] $FileCount, [long] $ByteCount, [int] $FolderCount, [long] $ErrorCount, [string] $CurrentFolder)
    $activity = 'Scanning {0}  |  Files: {1:N0}  |  Data: {2}  |  Folders: {3:N0}  |  Errors: {4:N0}' -f $Root, $FileCount, (Format-ByteSize $ByteCount), $FolderCount, $ErrorCount
    Write-Progress -Activity $activity -Status ('Current folder: ' + $CurrentFolder)
}

#endregion

#region ---- Validate parameters (nothing is created or written until every check has passed) -------

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

$rootFull   = Resolve-FileSystemPath -Path $RootPath   -ParameterName 'RootPath'
$outputFull = Resolve-FileSystemPath -Path $OutputPath -ParameterName 'OutputPath'

if (-not [System.IO.Directory]::Exists($rootFull)) {
    throw "RootPath '$rootFull' does not exist, is not a folder, or is not accessible."
}

# SAFETY: never write into the tree being scanned.
if (Test-IsSameOrInside -CandidatePath $outputFull -ContainerPath $rootFull) {
    throw ("OutputPath '{0}' is the same as, or inside, RootPath '{1}'. So that nothing is ever written into the tree being " +
           "scanned, please choose an output folder outside it (for example on another drive).") -f $outputFull, $rootFull
}

# SAFETY: a junction/symlink in either path could make the two overlap in reality, or make the scan land somewhere
# other than where the path text suggests (another drive, a network share).
$rootReparse = Find-ReparsePointInPath -Path $rootFull
if ($null -ne $rootReparse) {
    throw "RootPath passes through a reparse point: $rootReparse. Please supply the real (target) folder path directly."
}
$outputReparse = Find-ReparsePointInPath -Path $outputFull
if ($null -ne $outputReparse) {
    throw "OutputPath passes through a reparse point: $outputReparse. Please choose an ordinary folder for the reports."
}
if ([System.IO.File]::Exists($outputFull)) {
    throw "OutputPath '$outputFull' is an existing file, not a folder."
}

if (Test-IsNetworkPath -Path $rootFull) {
    Write-Warning "RootPath '$rootFull' is a network location. It is scanned only because you named it explicitly; this can be slow."
}
if (Test-IsNetworkPath -Path $outputFull) {
    Write-Warning "OutputPath '$outputFull' is a network location: the reports (which list your file names) will be written across the network."
}
foreach ($oneDriveVariable in @('OneDrive', 'OneDriveConsumer', 'OneDriveCommercial')) {
    $oneDriveFolder = [Environment]::GetEnvironmentVariable($oneDriveVariable)
    if ($oneDriveFolder -and (Test-IsSameOrInside -CandidatePath $outputFull -ContainerPath $oneDriveFolder)) {
        Write-Warning "OutputPath is inside OneDrive ($oneDriveFolder). OneDrive will upload the reports, which list all your file and folder names."
        break
    }
}

# Per-run identity: timestamp plus a random run ID, so repeated runs never collide with (or overwrite) earlier
# reports, and every file this run creates is recognisably its own.
$runId           = [datetime]::Now.ToString('yyyyMMdd_HHmmss', $Inv) + '_' + [guid]::NewGuid().ToString('N').Substring(0, 6)
$filesCsvPath    = [System.IO.Path]::Combine($outputFull, "Files_$runId.csv")
$filesTempPath   = [System.IO.Path]::Combine($outputFull, "Files_$runId.unsorted.tmp")
$foldersCsvPath  = [System.IO.Path]::Combine($outputFull, "Folders_$runId.csv")
$errorsCsvPath   = [System.IO.Path]::Combine($outputFull, "ScanErrors_$runId.csv")
$xlsxPath        = [System.IO.Path]::Combine($outputFull, "StorageInventory_$runId.xlsx")
$xlsxPartialPath = $xlsxPath + '.partial'
$reportPaths     = @($filesCsvPath, $filesTempPath, $foldersCsvPath, $errorsCsvPath, $xlsxPath, $xlsxPartialPath)

foreach ($reportPath in $reportPaths) {
    if ([System.IO.File]::Exists($reportPath) -or [System.IO.Directory]::Exists($reportPath)) {
        throw "'$reportPath' already exists. Nothing was overwritten; please run again."
    }
}

#endregion

#region ---- Scan -----------------------------------------------------------------------------------

if (-not [System.IO.Directory]::Exists($outputFull)) {
    [void][System.IO.Directory]::CreateDirectory($outputFull)
    Write-Host "Created output folder: $outputFull"
}

# Script-level state shared with the helper functions.
$script:OutputFolder       = $outputFull
$script:CreatedReportFiles = [System.Collections.Generic.List[string]]::new()
$script:Folders            = [System.Collections.Generic.List[InventoryFolder]]::new()
$script:ErrorWriter        = $null
$script:ErrorCount         = 0
$script:ReparseSkipCount   = 0
$script:ReparseFileCount   = 0
$script:ErrorCountByType   = @{}

# SAFETY tripwire: if one of this run's own report files is ever seen during the scan, the output folder is reachable
# from RootPath through an alias that the up-front checks could not detect (SUBST drive, \\localhost\D$ share,
# 8.3 short name...). The scan then stops at once.
$ownReportNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($reportPath in $reportPaths) { [void]$ownReportNames.Add([System.IO.Path]::GetFileName($reportPath)) }

$sortFiles         = -not $NoSort
$recordStream      = $null   # file rows: the temp file (sorted mode) or the final Files CSV (-NoSort)
$sortedFilesStream = $null
$tempReader        = $null
$foldersWriter     = $null
$completed         = $false   # everything, including the optional workbook step, finished
$csvCompleted      = $false   # the authoritative CSV reports are finished and closed
$excelStatus       = ''

try {
    $script:ErrorWriter = New-ReportWriter -Path $errorsCsvPath
    $script:ErrorWriter.WriteLine($ErrorsHeader)

    if ($sortFiles) {
        # Per-file sort index: size, byte offset and byte length of the row in the temp file (~20 bytes per file).
        $recordSizes   = [System.Collections.Generic.List[long]]::new()
        $recordOffsets = [System.Collections.Generic.List[long]]::new()
        $recordLengths = [System.Collections.Generic.List[int]]::new()
        $recordStream  = New-ReportFileStream -Path $filesTempPath
    }
    else {
        $recordStream = New-ReportFileStream -Path $filesCsvPath
        $preamble = $Utf8Bom.GetPreamble()
        $recordStream.Write($preamble, 0, $preamble.Length)
        $headerBytes = $Utf8NoBom.GetBytes($FilesHeader + "`r`n")
        $recordStream.Write($headerBytes, 0, $headerBytes.Length)
    }

    $rootDirectory = [System.IO.DirectoryInfo]::new($rootFull)
    $rootPrefix    = if ($rootFull.EndsWith('\')) { $rootFull } else { $rootFull + '\' }
    [void](Add-FolderRecord -Directory $rootDirectory -ParentIndex -1 -Depth 0 -RelativePath '.')
    $script:Folders[0].PendingDirectory = $rootDirectory

    # Depth-first walk with an explicit stack (no recursion, so very deep trees cannot overflow the call stack).
    $pendingFolders = [System.Collections.Generic.Stack[int]]::new()
    $pendingFolders.Push(0)

    $fileCount       = [long]0
    $totalBytes      = [long]0
    $recordOffset    = [long]0
    $maxRecordLength = 0
    $nextProgressMs  = 0

    Write-Host "Scanning:   $rootFull"
    Write-Host "Reports to: $outputFull"
    Write-Host 'Mode:       read-only metadata scan (reparse points are listed, never followed). Press Ctrl+C to cancel.'

    while ($pendingFolders.Count -gt 0) {
        $folderIndex = $pendingFolders.Pop()
        $folder      = $script:Folders[$folderIndex]
        $directory   = $folder.PendingDirectory
        $folder.PendingDirectory = $null

        $directoryRelative = $folder.RelativePath
        $isRootFolder      = ($folderIndex -eq 0)
        $childDepth        = $folder.Depth + 1

        # CSV text shared by every file row in this folder, computed once here instead of once per file.
        # The formula guard for a RelativePath depends only on its FIRST character, which (outside the root)
        # belongs to this folder's relative path - so the guarded prefix can be computed once per folder.
        $relativeDirectoryField = ConvertTo-CsvField $directoryRelative
        if ($isRootFolder) { $relativePathPrefix = '' } else { $relativePathPrefix = Get-CsvTextBody ($directoryRelative + '\') }

        if ($stopwatch.ElapsedMilliseconds -ge $nextProgressMs) {
            Show-ScanProgress -Root $rootFull -FileCount $fileCount -ByteCount $totalBytes -FolderCount $script:Folders.Count -ErrorCount $script:ErrorCount -CurrentFolder $directoryRelative
            $nextProgressMs = $stopwatch.ElapsedMilliseconds + $ProgressIntervalMs
        }

        # Open the directory listing. Any failure (access denied, vanished, path too long, device not ready...)
        # is logged, the folder is marked, and the scan moves on.
        $enumerator = $null
        try {
            $enumerator = $directory.EnumerateFileSystemInfos().GetEnumerator()
        }
        catch {
            $info = Get-ScanErrorInfo -Exception $_.Exception
            Add-ScanErrorRecord -Path $directory.FullName -ErrorType $info.ErrorType -Message $info.Message
            $folder.ScanStatus = $info.ErrorType
            $folder.SubtreeComplete = $false
            continue
        }

        try {
            while ($true) {
                # Only MoveNext/Current read the scanned file system here. They are guarded on their own so that a
                # failure writing the REPORT (e.g. output disk full) is never mistaken for a scan error and ignored.
                try {
                    if (-not $enumerator.MoveNext()) { break }
                    $entry = $enumerator.Current
                }
                catch {
                    $info = Get-ScanErrorInfo -Exception $_.Exception
                    Add-ScanErrorRecord -Path $directory.FullName -ErrorType $info.ErrorType -Message ('Listing stopped part-way: ' + $info.Message)
                    $folder.ScanStatus = 'Partial:' + $info.ErrorType
                    $folder.SubtreeComplete = $false
                    break
                }

                # ---------------- Sub-folder ----------------
                if ($entry -is [System.IO.DirectoryInfo]) {
                    if ($isRootFolder) { $childRelative = $entry.Name } else { $childRelative = $directoryRelative + '\' + $entry.Name }
                    $childIndex = Add-FolderRecord -Directory $entry -ParentIndex $folderIndex -Depth $childDepth -RelativePath $childRelative
                    $folder.DirectSubfolderCount++

                    if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                        # SAFETY: junctions, symbolic links, mount points and placeholders are NEVER followed. Following
                        # them could loop forever, count data twice, leave this drive, or reach a network share.
                        $script:Folders[$childIndex].ScanStatus = 'ReparsePointSkipped'
                        Add-ScanErrorRecord -Path $entry.FullName -ErrorType 'ReparsePointSkipped' -Message ('Not followed: ' + (Get-ReparsePointDescription -Item $entry))
                    }
                    else {
                        $script:Folders[$childIndex].PendingDirectory = $entry
                        $pendingFolders.Push($childIndex)
                    }
                    continue
                }

                # ---------------- File ----------------
                # All values below come from the directory listing itself; the file is never opened.
                try {
                    $fileName       = $entry.Name
                    $fullPath       = $entry.FullName
                    $length         = $entry.Length
                    $attributeFlags = $entry.Attributes
                    $attributes     = $attributeFlags.ToString()
                }
                catch {
                    # The entry exists but could not be counted, so this folder's own contents are incomplete.
                    $info = Get-ScanErrorInfo -Exception $_.Exception
                    Add-ScanErrorRecord -Path $directory.FullName -ErrorType $info.ErrorType -Message ('Could not read the metadata of an entry: ' + $info.Message)
                    if ($folder.ScanStatus -eq 'OK') { $folder.ScanStatus = 'Partial:' + $info.ErrorType }
                    $folder.SubtreeComplete = $false
                    continue
                }

                if (($attributeFlags -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    # SAFETY: a FILE reparse point (file symbolic link, cloud placeholder, deduplicated file...) is counted
                    # as a file using the size the directory listing reports - for a symbolic link that is the link itself,
                    # usually 0 bytes. Its target is never opened or followed. Recorded so the behaviour is visible.
                    Add-ScanErrorRecord -Path $fullPath -ErrorType 'ReparsePointFile' -Message ('Counted as a file with its listed size of ' + $length.ToString($Inv) + ' bytes; target not followed: ' + (Get-ReparsePointDescription -Item $entry))
                }

                if ($ownReportNames.Contains($fileName)) {
                    throw ("Found this run's own report file inside the scanned tree ('{0}'). OutputPath '{1}' is reachable from RootPath " +
                           "through an alias (SUBST drive, network share, 8.3 short name...). The scan was stopped so that nothing more is " +
                           "written there. Please choose an OutputPath on a different drive.") -f $fullPath, $outputFull
                }

                $dotIndex = $fileName.LastIndexOf('.')
                if ($dotIndex -ge 0 -and $dotIndex -lt ($fileName.Length - 1)) { $extension = $fileName.Substring($dotIndex) } else { $extension = '' }
                if ($extension.Length -eq 0) {
                    $fileType = 'No Extension'
                }
                else {
                    $fileType = $FileTypeByExtension[$extension]
                    if ($null -eq $fileType) {
                        # Split-archive parts such as .001 / .r00 / .z01
                        if ($extension -match '^\.(\d{3}|[rz]\d{2})$') { $fileType = 'Archive' } else { $fileType = 'Other' }
                    }
                }
                if ($isRootFolder) { $relativeFilePath = $fileName } else { $relativeFilePath = $directoryRelative + '\' + $fileName }

                # Aggregation: each file is added to exactly ONE folder record (the folder being listed).
                # Totals for ancestors are derived later, bottom-up, so nothing is counted twice.
                $folder.DirectSizeBytes += $length
                $folder.DirectFileCount++
                if ($length -gt $folder.LargestFileBytes) {
                    $folder.LargestFileBytes = $length
                    $folder.LargestFileRelativePath = $relativeFilePath
                }
                $fileCount++
                $totalBytes += $length

                # Timestamps are cached from the listing. A corrupt value is logged and left blank.
                try { $createdText = $entry.CreationTime.ToString($DateFormat, $Inv) }
                catch { $createdText = ''; Add-ScanErrorRecord -Path $fullPath -ErrorType 'InvalidTimestamp' -Message ('Created time: ' + $_.Exception.Message) }
                try { $modifiedText = $entry.LastWriteTime.ToString($DateFormat, $Inv) }
                catch { $modifiedText = ''; Add-ScanErrorRecord -Path $fullPath -ErrorType 'InvalidTimestamp' -Message ('Modified time: ' + $_.Exception.Message) }
                try { $accessedText = $entry.LastAccessTime.ToString($DateFormat, $Inv) }
                catch { $accessedText = ''; Add-ScanErrorRecord -Path $fullPath -ErrorType 'InvalidTimestamp' -Message ('Last-access time: ' + $_.Exception.Message) }

                # CSV quoting is inlined here because this runs once per file (a PowerShell function call costs more
                # than everything else on this path combined). The file-name check is the same rule as Get-CsvTextBody.
                # Extension (starts with '.'), FullPath (starts with a drive letter or \\), FileType (fixed table value)
                # and Attributes (enum names) can never start with a formula character, so they only need quoting.
                $escapedName  = $fileName.Replace('"', '""')
                $fileNameBody = $escapedName
                if ($FormulaStartChars.IndexOf($fileName[0]) -ge 0) { $fileNameBody = "'" + $escapedName }
                $fileNameField = '"' + $fileNameBody + '"'
                if ($isRootFolder) { $relativePathField = $fileNameField } else { $relativePathField = '"' + $relativePathPrefix + $escapedName + '"' }

                $row = @(
                    $fileNameField,
                    ('"' + $extension.Replace('"', '""') + '"'),
                    ('"' + $fileType + '"'),
                    $relativePathField,
                    $relativeDirectoryField,
                    ('"' + $fullPath.Replace('"', '""') + '"'),
                    $length.ToString($Inv),
                    ($length / 1KB).ToString('0.00', $Inv),
                    ($length / 1MB).ToString('0.00', $Inv),
                    ($length / 1GB).ToString('0.000', $Inv),
                    $createdText,
                    $modifiedText,
                    $accessedText,
                    ('"' + $attributes + '"')
                ) -join ','

                $rowBytes = $Utf8NoBom.GetBytes($row + "`r`n")
                $recordStream.Write($rowBytes, 0, $rowBytes.Length)
                if ($sortFiles) {
                    $recordSizes.Add($length)
                    $recordOffsets.Add($recordOffset)
                    $recordLengths.Add($rowBytes.Length)
                }
                $recordOffset += $rowBytes.Length
                if ($rowBytes.Length -gt $maxRecordLength) { $maxRecordLength = $rowBytes.Length }

                if (($fileCount % 4096) -eq 0 -and $stopwatch.ElapsedMilliseconds -ge $nextProgressMs) {
                    Show-ScanProgress -Root $rootFull -FileCount $fileCount -ByteCount $totalBytes -FolderCount $script:Folders.Count -ErrorCount $script:ErrorCount -CurrentFolder $directoryRelative
                    $nextProgressMs = $stopwatch.ElapsedMilliseconds + $ProgressIntervalMs
                }
            }
        }
        finally {
            $enumerator.Dispose()
        }
    }

    $recordStream.Dispose()
    $recordStream = $null

    #endregion

    #region ---- Aggregate folder totals (one bottom-up pass, no re-scanning) ------------------------

    Write-Progress -Activity 'Storage inventory' -Status 'Calculating folder totals...'

    # Children always have a larger index than their parent, so walking the list from the end means every folder's
    # children have already added their (complete) totals into it by the time it is reached. Each folder then adds
    # its own direct files once and passes its finished total to its single parent once. Every file is therefore
    # counted exactly once at every ancestor level - never twice.
    for ($i = $script:Folders.Count - 1; $i -ge 0; $i--) {
        $folder = $script:Folders[$i]
        $folder.TotalSizeBytes += $folder.DirectSizeBytes
        $folder.TotalFileCount += $folder.DirectFileCount
        if ($i -eq 0) { break }

        if ($folder.ParentIndex -lt 0 -or $folder.ParentIndex -ge $i) {
            throw "Internal error: folder '$($folder.RelativePath)' has an invalid parent index."
        }
        $parent = $script:Folders[$folder.ParentIndex]
        $parent.TotalSizeBytes      += $folder.TotalSizeBytes
        $parent.TotalFileCount      += $folder.TotalFileCount
        $parent.TotalSubfolderCount += 1 + $folder.TotalSubfolderCount
        if ($folder.LargestFileBytes -gt $parent.LargestFileBytes) {
            $parent.LargestFileBytes = $folder.LargestFileBytes
            $parent.LargestFileRelativePath = $folder.LargestFileRelativePath
        }
        if (-not $folder.SubtreeComplete) { $parent.SubtreeComplete = $false }
    }

    # Self-check: the root's totals must equal what was counted file by file during the scan.
    $rootRecord = $script:Folders[0]
    if ($rootRecord.TotalSizeBytes -ne $totalBytes -or $rootRecord.TotalFileCount -ne $fileCount -or
        $rootRecord.TotalSubfolderCount -ne ($script:Folders.Count - 1)) {
        throw ('Internal consistency check failed: root total {0} bytes / {1} files / {2} folders, but the scan counted {3} bytes / {4} files / {5} folders.' -f
               $rootRecord.TotalSizeBytes, $rootRecord.TotalFileCount, $rootRecord.TotalSubfolderCount, $totalBytes, $fileCount, ($script:Folders.Count - 1))
    }

    # Completeness. The ROOT's SubtreeComplete flag is authoritative for the whole scan. The counts below only explain it:
    #   locally incomplete = the folder itself could not be listed, or some of its entries could not be read;
    #   affected ancestors = the folder was read fine, but something beneath it was not;
    #   skipped links      = reparse points not entered BY DESIGN - they never make a scan incomplete.
    $scanIsComplete           = $rootRecord.SubtreeComplete
    $locallyIncompleteCount   = 0
    $affectedAncestorCount    = 0
    foreach ($folder in $script:Folders) {
        if ($folder.ScanStatus -ne 'OK' -and $folder.ScanStatus -ne 'ReparsePointSkipped') { $locallyIncompleteCount++ }
        elseif (-not $folder.SubtreeComplete) { $affectedAncestorCount++ }
    }

    #endregion

    #region ---- Folders report --------------------------------------------------------------------------

    $folderCount = $script:Folders.Count
    $folderKeys  = [long[]]::new($folderCount)
    $folderOrder = [int[]]::new($folderCount)
    for ($i = 0; $i -lt $folderCount; $i++) {
        $folderKeys[$i]  = -$script:Folders[$i].TotalSizeBytes   # negated so an ascending sort puts the largest first
        $folderOrder[$i] = $i
    }
    [System.Array]::Sort($folderKeys, $folderOrder)
    Set-StableTieOrder -Keys $folderKeys -Items $folderOrder

    $foldersWriter = New-ReportWriter -Path $foldersCsvPath
    $foldersWriter.WriteLine($FoldersHeader)
    $rootTotal = $rootRecord.TotalSizeBytes
    $written = 0
    foreach ($index in $folderOrder) {
        $f = $script:Folders[$index]
        if ($index -eq 0) {
            $parentRelative  = ''
            $folderFullPath  = $rootFull
            $percentOfParent = ''
        }
        else {
            $parentRecord    = $script:Folders[$f.ParentIndex]
            $parentRelative  = $parentRecord.RelativePath
            $folderFullPath  = $rootPrefix + $f.RelativePath
            $percentOfParent = if ($parentRecord.TotalSizeBytes -gt 0) { ($f.TotalSizeBytes * 100.0 / $parentRecord.TotalSizeBytes).ToString('0.000', $Inv) } else { '0.000' }
        }
        $percentOfRoot = if ($rootTotal -gt 0) { ($f.TotalSizeBytes * 100.0 / $rootTotal).ToString('0.000', $Inv) } else { '0.000' }
        $averageMB     = if ($f.TotalFileCount -gt 0) { ($f.TotalSizeBytes / $f.TotalFileCount / 1MB).ToString('0.00', $Inv) } else { '' }
        $largestMB     = if ($f.LargestFileBytes -ge 0) { ($f.LargestFileBytes / 1MB).ToString('0.00', $Inv) } else { '' }

        $row = @(
            (ConvertTo-CsvField $f.Name),
            (ConvertTo-CsvField $f.RelativePath),
            (ConvertTo-CsvField $parentRelative),
            (ConvertTo-CsvField $folderFullPath),
            $f.Depth.ToString($Inv),
            $f.TotalSizeBytes.ToString($Inv),
            ($f.TotalSizeBytes / 1KB).ToString('0.00', $Inv),
            ($f.TotalSizeBytes / 1MB).ToString('0.00', $Inv),
            ($f.TotalSizeBytes / 1GB).ToString('0.000', $Inv),
            ($f.TotalSizeBytes / 1TB).ToString('0.0000', $Inv),
            $percentOfRoot,
            $percentOfParent,
            $f.DirectSizeBytes.ToString($Inv),
            ($f.DirectSizeBytes / 1MB).ToString('0.00', $Inv),
            $f.DirectFileCount.ToString($Inv),
            $f.TotalFileCount.ToString($Inv),
            $f.DirectSubfolderCount.ToString($Inv),
            $f.TotalSubfolderCount.ToString($Inv),
            $averageMB,
            $largestMB,
            (ConvertTo-CsvField $f.LargestFileRelativePath),
            $f.CreatedText,
            $f.ModifiedText,
            (ConvertTo-CsvField $f.Attributes),
            (ConvertTo-CsvField $f.ScanStatus),
            $f.SubtreeComplete.ToString()
        ) -join ','
        $foldersWriter.WriteLine($row)

        $written++
        if (($written % 8192) -eq 0 -and $stopwatch.ElapsedMilliseconds -ge $nextProgressMs) {
            Write-Progress -Activity 'Storage inventory' -Status ('Writing Folders report: {0:N0} / {1:N0}' -f $written, $folderCount)
            $nextProgressMs = $stopwatch.ElapsedMilliseconds + $ProgressIntervalMs
        }
    }
    $foldersWriter.Dispose()
    $foldersWriter = $null

    #endregion

    #region ---- Files report: copy rows from the temp file in size order (skipped with -NoSort) -------

    if ($sortFiles) {
        $sortCount = $recordSizes.Count
        $fileKeys  = [long[]]::new($sortCount)
        $fileOrder = [int[]]::new($sortCount)
        for ($i = 0; $i -lt $sortCount; $i++) {
            $fileKeys[$i]  = -$recordSizes[$i]
            $fileOrder[$i] = $i
        }
        $recordSizes = $null
        Write-Progress -Activity 'Storage inventory' -Status ('Sorting {0:N0} files by size...' -f $sortCount)
        [System.Array]::Sort($fileKeys, $fileOrder)
        Set-StableTieOrder -Keys $fileKeys -Items $fileOrder
        $fileKeys = $null

        $sortedFilesStream = New-ReportFileStream -Path $filesCsvPath
        $preamble = $Utf8Bom.GetPreamble()
        $sortedFilesStream.Write($preamble, 0, $preamble.Length)
        $headerBytes = $Utf8NoBom.GetBytes($FilesHeader + "`r`n")
        $sortedFilesStream.Write($headerBytes, 0, $headerBytes.Length)

        # Read-only access to OUR OWN temporary file (never to anything in the scanned tree).
        $tempReader = [System.IO.FileStream]::new($filesTempPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read, 65536)
        $buffer = [byte[]]::new([Math]::Max($maxRecordLength, 1))
        for ($k = 0; $k -lt $sortCount; $k++) {
            $recordIndex  = $fileOrder[$k]
            $recordLength = $recordLengths[$recordIndex]
            $tempReader.Position = $recordOffsets[$recordIndex]
            $bytesRead = 0
            while ($bytesRead -lt $recordLength) {
                $chunk = $tempReader.Read($buffer, $bytesRead, $recordLength - $bytesRead)
                if ($chunk -le 0) { throw "Unexpected end of temporary file '$filesTempPath'." }
                $bytesRead += $chunk
            }
            $sortedFilesStream.Write($buffer, 0, $recordLength)

            if (($k % 16384) -eq 0 -and $stopwatch.ElapsedMilliseconds -ge $nextProgressMs) {
                Write-Progress -Activity 'Storage inventory' -Status ('Writing sorted Files report: {0:N0} / {1:N0}' -f $k, $sortCount) -PercentComplete ([int](100.0 * $k / [Math]::Max($sortCount, 1)))
                $nextProgressMs = $stopwatch.ElapsedMilliseconds + $ProgressIntervalMs
            }
        }
        $tempReader.Dispose()
        $tempReader = $null
        $sortedFilesStream.Dispose()
        $sortedFilesStream = $null
        $recordOffsets = $null
        $recordLengths = $null
        $fileOrder = $null

        Remove-OwnTemporaryFile -Path $filesTempPath
    }

    $script:ErrorWriter.Dispose()
    $script:ErrorWriter = $null
    Write-Progress -Activity 'Storage inventory' -Completed

    # All CSV reports are now written and closed. They are the authoritative result; nothing below changes them.
    $csvCompleted = $true

    #endregion

    #region ---- Optional Excel workbook: post-processing of the COMPLETED CSV reports ------------------
    # SAFETY (no-clobber): ImportExcel builds the workbook IN MEMORY and is never given a file path, so it cannot
    # create, open or overwrite any file. This script writes the bytes itself through New-ReportFileStream (CreateNew)
    # to a '.xlsx.partial' name, then renames that to the final name with a rename that never replaces an existing
    # file. A failure here never changes the scan result.

    if ($SkipExcel) {
        $excelStatus = 'Skipped (-SkipExcel). The CSV files contain the complete results.'
    }
    else {
        # Get-Module -ListAvailable only reads module manifests; nothing is loaded unless ImportExcel is found.
        $excelModule = @(Get-Module -ListAvailable -Name 'ImportExcel') | Sort-Object -Property Version -Descending | Select-Object -First 1
        if ($null -eq $excelModule) {
            $excelStatus = 'Not created: the ImportExcel module is not installed (nothing was installed). The CSV files contain the complete results.'
        }
        else {
            $excelPackage   = $null
            $workbookStream = $null
            try {
                Write-Host 'Creating Excel workbook (this can take several minutes for large reports)...'
                # Loaded by name from your installed modules (never from a path found by the scan), scoped to this script.
                Import-Module -ModuleInfo $excelModule -Scope Local -ErrorAction Stop
                $exportExcel = Get-Command -Name 'Export-Excel' -ErrorAction Stop
                if (-not $exportExcel.Parameters.ContainsKey('NoHyperLinkConversion')) {
                    throw "ImportExcel $($excelModule.Version) cannot switch off automatic hyperlink creation (-NoHyperLinkConversion). Please update ImportExcel."
                }

                $excelPackage = [OfficeOpenXml.ExcelPackage]::new()   # in memory: no file behind it
                # Cells stay plain text: values that Export-Excel would treat as formulas (leading '=') already carry the
                # apostrophe guard, text columns are never converted to numbers, and nothing becomes a hyperlink.
                $excelOptions = @{
                    ExcelPackage          = $excelPackage
                    PassThru              = $true
                    NoHyperLinkConversion = '*'
                    FreezeTopRow          = $true
                    AutoFilter            = $true
                    BoldTopRow            = $true
                }
                $excelNotes = ''
                if ($fileCount -le $ExcelMaxDataRows) {
                    $null = Import-Csv -LiteralPath $filesCsvPath -Encoding UTF8 |
                        Export-Excel @excelOptions -WorksheetName 'Files' -NoNumberConversion $FilesTextColumns
                }
                else {
                    $excelNotes = (' The Files worksheet was omitted: {0:N0} files exceeds the Excel limit of {1:N0} rows (see the Files CSV).' -f $fileCount, $ExcelMaxDataRows)
                }
                $null = Import-Csv -LiteralPath $foldersCsvPath -Encoding UTF8 |
                    Export-Excel @excelOptions -WorksheetName 'Folders' -NoNumberConversion $FoldersTextColumns
                $workbookBytes = $excelPackage.GetAsByteArray()

                $workbookStream = New-ReportFileStream -Path $xlsxPartialPath
                $workbookStream.Write($workbookBytes, 0, $workbookBytes.Length)
                $workbookStream.Dispose()
                $workbookStream = $null
                Complete-OwnWorkbookFile -PartialPath $xlsxPartialPath -FinalPath $xlsxPath
                $excelStatus = $xlsxPath + $excelNotes
            }
            catch {
                $excelStatus = 'Not created (Excel export failed: ' + $_.Exception.Message + '). The CSV files contain the complete results.'
                if ($script:CreatedReportFiles.Contains($xlsxPartialPath)) {
                    $excelStatus += " An unfinished workbook created by this run was left at '$xlsxPartialPath'. It is not a valid report and is safe to delete."
                }
            }
            finally {
                if ($null -ne $workbookStream) { $workbookStream.Dispose() }
                if ($null -ne $excelPackage) { $excelPackage.Dispose() }
            }
        }
    }

    #endregion

    $completed = $true
}
finally {
    # Runs on success, on error and on Ctrl+C: close every report file so nothing is left locked or half-flushed.
    foreach ($openItem in @($recordStream, $sortedFilesStream, $tempReader, $foldersWriter, $script:ErrorWriter)) {
        if ($null -ne $openItem) {
            try { $openItem.Dispose() } catch { Write-Verbose "Could not close a report file: $($_.Exception.Message)" }
        }
    }
    if (-not $completed) {
        try {
            Write-Progress -Activity 'Storage inventory' -Completed
            Write-Host ''
            if ($csvCompleted) {
                Write-Host 'The CSV reports were completed, but the run stopped during the optional Excel export. The scanned files were not touched.' -ForegroundColor Yellow
                if ($script:CreatedReportFiles.Contains($xlsxPartialPath)) {
                    Write-Host "INCOMPLETE workbook created by this run (safe to delete): $xlsxPartialPath" -ForegroundColor Yellow
                }
            }
            else {
                Write-Host 'The inventory did NOT complete (cancelled or failed). The scanned files were not touched.' -ForegroundColor Yellow
                if ($script:CreatedReportFiles.Count -gt 0) {
                    Write-Host 'INCOMPLETE report files created by this run (safe to delete):' -ForegroundColor Yellow
                    foreach ($createdFile in $script:CreatedReportFiles) { Write-Host "  $createdFile" }
                }
            }
        }
        catch {
            # Deliberately empty: the console may be unavailable while PowerShell is stopping (Ctrl+C).
            # Every report file was already closed above, which is the part that matters.
        }
    }
}

#region ---- Summary ----------------------------------------------------------------------------------

$stopwatch.Stop()
Write-Host ''
if ($scanIsComplete) {
    Write-Host 'Scan complete.' -ForegroundColor Green
}
else {
    Write-Host 'Scan finished but INCOMPLETE: some folders or entries could not be read, so the totals below are LOWER BOUNDS.' -ForegroundColor Yellow
}
Write-Host ''
Write-Host "Root: $rootFull"
Write-Host ('Files: {0:N0}' -f $fileCount)
Write-Host ('Folders: {0:N0}' -f $script:Folders.Count)
Write-Host ('Total data: {0}' -f (Format-ByteSize $totalBytes))
Write-Host ('Scan errors: {0:N0}' -f $script:ErrorCount)
Write-Host ('Folder links not followed (by design): {0:N0}    File reparse points counted, not followed: {1:N0}' -f $script:ReparseSkipCount, $script:ReparseFileCount)
if (-not $scanIsComplete) {
    Write-Warning ('Totals are incomplete. {0:N0} folder(s) could not be fully read themselves, and {1:N0} parent folder(s) contain them. Rows with SubtreeComplete=False show lower-bound totals. See the ScanErrors report.' -f $locallyIncompleteCount, $affectedAncestorCount)
}
if ($script:ErrorCountByType.ContainsKey('PathTooLong') -and $PSVersionTable.PSVersion.Major -lt 6) {
    Write-Warning ('{0:N0} item(s) had paths longer than Windows PowerShell 5.1 can handle. Run the script in PowerShell 7 (pwsh) to include them.' -f $script:ErrorCountByType['PathTooLong'])
}
Write-Host ''
Write-Host ('Files report{0}:' -f $(if ($sortFiles) { ' (largest first)' } else { ' (discovery order)' }))
Write-Host "  $filesCsvPath"
Write-Host ''
Write-Host 'Folders report (largest first):'
Write-Host "  $foldersCsvPath"
Write-Host ''
Write-Host 'Errors:'
Write-Host "  $errorsCsvPath"
Write-Host ''
Write-Host 'Excel workbook:'
Write-Host "  $excelStatus"
Write-Host ''

$shown = 0
foreach ($index in $folderOrder) {
    $f = $script:Folders[$index]
    if ($f.Depth -ne 1) { continue }
    if ($shown -eq 0) { Write-Host 'Largest top-level folders:' }
    $share = if ($rootTotal -gt 0) { $f.TotalSizeBytes * 100.0 / $rootTotal } else { 0 }
    $marker = if ($f.ScanStatus -eq 'ReparsePointSkipped') { '  (link, not followed)' } elseif (-not $f.SubtreeComplete) { '  (INCOMPLETE)' } else { '' }
    Write-Host ('  {0,7:N2} %  {1,12}  {2}{3}' -f $share, (Format-ByteSize $f.TotalSizeBytes), $f.RelativePath, $marker)
    $shown++
    if ($shown -ge 10) { break }
}
if ($shown -gt 0) { Write-Host '' }

Write-Host 'Elapsed time:'
Write-Host ('  ' + (Format-Elapsed $stopwatch.Elapsed))

#endregion

# A finished run is not necessarily a complete scan: exit code 2 tells automation the totals are lower bounds.
if (-not $scanIsComplete) { exit 2 }
