# Shared helpers for the StorageInventory PowerShell regression suite. TEST CODE ONLY - dot-sourced by Run-Tests.ps1.
# The fixtures deliberately create junctions, deny-ACLs and odd names inside a throw-away folder under %TEMP%.
Set-StrictMode -Version 3.0

$script:Results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param([string] $Area, [string] $Name, [ValidateSet('PASS', 'FAIL', 'SKIP')] [string] $Outcome, [string] $Detail = '')
    $script:Results.Add([pscustomobject]@{ Area = $Area; Test = $Name; Outcome = $Outcome; Detail = $Detail })
    $colour = @{ PASS = 'Green'; FAIL = 'Red'; SKIP = 'Yellow' }[$Outcome]
    $suffix = if ($Detail) { "  -- $Detail" } else { '' }
    Write-Host ('{0,-4}  [{1}] {2}{3}' -f $Outcome, $Area, $Name, $suffix) -ForegroundColor $colour
}

function Assert-True {
    param([string] $Area, [string] $Name, [bool] $Condition, [string] $Detail = '')
    Add-Result -Area $Area -Name $Name -Outcome $(if ($Condition) { 'PASS' } else { 'FAIL' }) -Detail $Detail
}

function Invoke-Inventory {
    # Runs the script under test in a separate process of the shell under test and captures all output.
    param([string[]] $Arguments, [string] $WorkingDirectory = $script:WorkRoot)
    Push-Location -LiteralPath $WorkingDirectory
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $script:Shell -NoProfile -ExecutionPolicy Bypass -File $script:ScriptUnderTest @Arguments 2>&1 | Out-String -Width 400
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    finally {
        $ErrorActionPreference = $previous
        Pop-Location
    }
}

function Start-InventoryProcess {
    # Starts the script asynchronously (for tests that must act while it runs). Returns the process object.
    param([string[]] $Arguments, [string] $LogPrefix)
    $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:ScriptUnderTest) + $Arguments
    $quoted = ($all | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $proc = Start-Process -FilePath $script:Shell -ArgumentList $quoted -PassThru -NoNewWindow `
        -RedirectStandardOutput "$LogPrefix.out.txt" -RedirectStandardError "$LogPrefix.err.txt"
    $null = $proc.Handle   # 5.1 quirk: without caching the handle now, ExitCode is $null after the process ends
    return $proc
}

function Wait-ForFile {
    # Polls a folder until a file matching the pattern exists (and optionally until the predicate is true).
    param([string] $Folder, [string] $Filter, [int] $TimeoutMs = 120000, [scriptblock] $Until)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if ([IO.Directory]::Exists($Folder)) {
            $hit = @([IO.Directory]::GetFiles($Folder, $Filter))
            if ($hit.Count -gt 0 -and ($null -eq $Until -or (& $Until $hit[0]))) { return $hit[0] }
        }
        Start-Sleep -Milliseconds 20
    }
    return $null
}

function Get-RunIdFromReport([string] $Path) {
    # Files_20260926_143012_a1b2c3.unsorted.tmp -> 20260926_143012_a1b2c3
    $name = [IO.Path]::GetFileName($Path)
    if ($name -match '^[A-Za-z]+_(\d{8}_\d{6}_[0-9a-f]{6})\.') { return $Matches[1] }
    return $null
}

function Test-SymlinkPrivilege {
    $probe = Join-Path $script:WorkRoot ('symprobe_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    [IO.File]::WriteAllText("$probe.target", 'x')
    $null = & cmd.exe /c mklink "$probe.link" "$probe.target" 2>&1
    $ok = [IO.File]::Exists("$probe.link")
    if ($ok) { [IO.File]::Delete("$probe.link") }
    [IO.File]::Delete("$probe.target")
    return $ok
}

function Set-FileTimeRaw {
    # Test-only: stamps a file with a raw FILETIME that .NET cannot represent, to exercise InvalidTimestamp handling.
    param([string] $Path, [long] $FileTime)
    if (-not ('StorageInventoryTests.NativeTime' -as [type])) {
        Add-Type -Namespace StorageInventoryTests -Name NativeTime -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle h, ref long creation, ref long access, ref long write);
'@
    }
    $handle = [StorageInventoryTests.NativeTime]::CreateFileW($Path, 0x100, 7, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)  # FILE_WRITE_ATTRIBUTES, OPEN_EXISTING
    try {
        $c = [long]0; $a = [long]0; $w = $FileTime   # 0 = leave unchanged
        return [StorageInventoryTests.NativeTime]::SetFileTime($handle, [ref]$c, [ref]$a, [ref]$w)
    }
    finally { $handle.Dispose() }
}

function New-Fixture {
    # Builds the adversarial test tree. Returns the root, the outside folder, the list of files that MUST be counted,
    # and which optional features could be created on this machine.
    param([string] $Base)
    $root    = Join-Path $Base 'root'
    $outside = Join-Path $Base 'outside'
    [void][IO.Directory]::CreateDirectory($root)
    [void][IO.Directory]::CreateDirectory($outside)
    $expected = [System.Collections.Generic.List[object]]::new()
    $features = [ordered]@{}

    $newFile = {
        param([string] $Rel, [long] $Size, [bool] $Counted = $true)
        $p = [IO.Path]::Combine($root, $Rel)
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))
        $fs = [IO.File]::Create($p); $fs.SetLength($Size); $fs.Dispose()
        if ($Counted) { $expected.Add([pscustomobject]@{ RelativePath = $Rel; Size = $Size }) }
    }

    & $newFile 'a.txt' 100
    & $newFile '=HYPERLINK(1).txt' 10
    & $newFile '-minus.txt' 20
    & $newFile '@at.txt' 30
    & $newFile '+plus.txt' 40
    & $newFile 'a,b.txt' 50
    & $newFile 'NoExtFile' 60
    & $newFile '.gitignore' 70
    & $newFile 'archive.7z.001' 80
    & $newFile 'movie.r00' 90
    & $newFile "'apostrophe-start.txt" 14
    & $newFile 'Kpop\aespa\Photos\image01.jpg' 1000
    & $newFile 'Kpop\aespa\Photos\image02.JPG' 2500
    & $newFile 'Kpop\aespa\Videos\clip.mkv' 1048576
    & $newFile 'Kpop\TWICE\song.flac' 300000
    & $newFile 'Kpop\readme.txt' 200
    & $newFile 'Kpop\=sub-formula.txt' 11
    & $newFile '-Dash Folder\@inner.txt' 12
    & $newFile '-Dash Folder\normal.txt' 13
    $weird = "Weird [brackets] `$dollar ``backtick 'apos' & amp ; semi (paren) " + [char]0xD55C + [char]0xAD6D + [char]0xC5B4 + ' ' + [char]::ConvertFromUtf32(0x1F600)
    & $newFile "$weird\file[1].txt" 123
    & $newFile "$weird\sub `$(Get-Date)\x.ps1" 5
    & $newFile 'Deep\d1\d2\d3\d4\d5\d6\d7\d8\d9\d10\bottom.bin' 4096
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'Empty'))
    & $newFile 'Hidden.txt' 15
    [IO.File]::SetAttributes((Join-Path $root 'Hidden.txt'), 'Hidden, ReadOnly, Archive')
    & $newFile ('Long\' + ('L' * 120) + '\' + ('M' * 120) + '\longfile.dat') 777

    # Names Windows cannot represent through the Win32 API are recorded as such rather than silently skipped.
    foreach ($pair in @(@('CRLF', "cr`r`nlf.txt"), @('DoubleQuote', 'quote".txt'))) {
        try { [IO.File]::Create((Join-Path $root $pair[1])).Dispose(); $features[$pair[0]] = 'created' }
        catch { $features[$pair[0]] = 'not representable on Windows' }
    }

    # Invalid timestamp: counted normally, timestamp left blank and reported as InvalidTimestamp.
    & $newFile 'Odd\bad-time.dat' 321
    $features['InvalidTimestamp'] = if (Set-FileTimeRaw -Path (Join-Path $root 'Odd\bad-time.dat') -FileTime 0x7FFFFFFFFFFFFFF0) { 'created' } else { 'could not set' }

    # Access denied: one folder at the top level and one nested, to test propagation to ancestors.
    & $newFile 'Denied\secret.txt' 1000 $false
    & $newFile 'Kpop\aespa\Private\hidden-secret.bin' 2000 $false
    foreach ($d in @('Denied', 'Kpop\aespa\Private')) { $null = & icacls.exe (Join-Path $root $d) /deny "$($env:USERNAME):(RD)" }

    # Junctions (no privilege needed): a loop back to the root, and one pointing outside the tree.
    $fs = [IO.File]::Create((Join-Path $outside 'big-outside.bin')); $fs.SetLength(5MB); $fs.Dispose()
    $null = & cmd.exe /c mklink /J "$root\Kpop\LinkOut" "$outside" 2>&1
    $null = & cmd.exe /c mklink /J "$root\Loop" "$root" 2>&1

    # Symbolic links need SeCreateSymbolicLinkPrivilege (admin or Developer Mode). Created only if available.
    $features['Symlinks'] = 'unavailable (needs Developer Mode or admin)'
    if (Test-SymlinkPrivilege) {
        [void][IO.Directory]::CreateDirectory((Join-Path $root 'Links'))
        $null = & cmd.exe /c mklink "$root\Links\file-link.bin" "$outside\big-outside.bin" 2>&1
        $null = & cmd.exe /c mklink /D "$root\Links\dir-link" "$outside" 2>&1
        # A file symbolic link is counted with the size of the link itself (0), never the 5 MB target.
        $expected.Add([pscustomobject]@{ RelativePath = 'Links\file-link.bin'; Size = 0 })
        $features['Symlinks'] = 'created'
    }

    return [pscustomobject]@{ Root = $root; Outside = $outside; Expected = $expected; Features = $features }
}

function New-BulkFixture {
    # A plain tree of many small files, used where a test needs a scan that lasts a few seconds.
    param([string] $Root, [int] $Artists = 40, [int] $Albums = 25, [int] $Tracks = 40)
    $rng = [Random]::new(42)
    for ($a = 0; $a -lt $Artists; $a++) {
        for ($b = 0; $b -lt $Albums; $b++) {
            $d = [IO.Path]::Combine($Root, "Artist$a", "Album$b")
            [void][IO.Directory]::CreateDirectory($d)
            for ($t = 0; $t -lt $Tracks; $t++) {
                $fs = [IO.File]::Create([IO.Path]::Combine($d, "track$t.mp3")); $fs.SetLength($rng.Next(0, 600)); $fs.Dispose()
            }
        }
    }
}

function Find-ReparsePoints {
    # Lists reparse points under Base WITHOUT descending into any of them (Get-ChildItem -Recurse in 5.1 may follow them).
    param([string] $Base)
    $found = [System.Collections.Generic.List[IO.FileSystemInfo]]::new()
    $stack = [System.Collections.Generic.Stack[string]]::new(); $stack.Push($Base)
    while ($stack.Count) {
        $d = $stack.Pop()
        try { $items = [IO.DirectoryInfo]::new($d).GetFileSystemInfos() } catch { continue }
        foreach ($i in $items) {
            if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { $found.Add($i) }
            elseif ($i -is [IO.DirectoryInfo]) { $stack.Push($i.FullName) }
        }
    }
    return , $found
}

function Remove-Fixture {
    # Junction-safe cleanup of a throw-away test tree: lift deny ACEs, remove links WITHOUT following them, then delete.
    param([string] $Base)
    if (-not [IO.Directory]::Exists($Base)) { return }
    if (-not $Base.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to clean '$Base': not under %TEMP%." }
    $null = & icacls.exe $Base /remove:d $env:USERNAME /T /C /Q 2>&1
    foreach ($link in (Find-ReparsePoints $Base)) {
        if ($link -is [IO.DirectoryInfo]) { $null = & cmd.exe /c rmdir "$($link.FullName)" 2>&1 } else { [IO.File]::Delete($link.FullName) }
    }
    if ((Find-ReparsePoints $Base).Count -eq 0) { Remove-Item -LiteralPath $Base -Recurse -Force }
    else { Write-Warning "Left $Base in place: it still contains reparse points." }
}

function Get-TreeSnapshot {
    # Independent snapshot of the tree (never follows reparse points). Default: values as the parent's listing shows
    # them. -TrueValues: re-read from each item's own record. Used to prove the scan changed nothing.
    param([string] $Root, [switch] $TrueValues)
    $lines = [System.Collections.Generic.List[string]]::new()
    $stack = [System.Collections.Generic.Stack[string]]::new()
    $stack.Push($Root)
    while ($stack.Count) {
        $d = $stack.Pop()
        try { $items = [IO.DirectoryInfo]::new($d).GetFileSystemInfos() } catch { $lines.Add("UNREADABLE|$d"); continue }
        foreach ($i in $items) {
            $isDir = $i -is [IO.DirectoryInfo]
            $v = $i
            if ($TrueValues) { if ($isDir) { $v = [IO.DirectoryInfo]::new($i.FullName) } else { $v = [IO.FileInfo]::new($i.FullName) }; $v.Refresh() }
            $len = if ($isDir) { -1 } else { $v.Length }
            $c = try { $v.CreationTimeUtc.Ticks } catch { 'BADTIME' }
            $w = try { $v.LastWriteTimeUtc.Ticks } catch { 'BADTIME' }
            $lines.Add(('{0}|{1}|{2}|{3}|{4}' -f $i.FullName, $len, $c, $w, $v.Attributes))
            if ($isDir -and -not ($i.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $stack.Push($i.FullName) }
        }
    }
    $lines.Sort([StringComparer]::Ordinal)
    return ($lines -join "`n")
}

function Get-LatestReport([string] $OutDir, [string] $Prefix) {
    $f = @(Get-ChildItem -LiteralPath $OutDir -Filter "$Prefix*.csv" | Sort-Object LastWriteTime)
    if ($f.Count -eq 0) { return $null }
    return $f[-1].FullName
}

function Test-InventoryReports {
    # Independent verification of one run's reports against ground truth and internal invariants.
    param([string] $Area, [string] $OutDir, [object[]] $Expected, [string] $RootPath)

    $files   = @(Import-Csv -LiteralPath (Get-LatestReport $OutDir 'Files_') -Encoding UTF8)
    $folders = @(Import-Csv -LiteralPath (Get-LatestReport $OutDir 'Folders_') -Encoding UTF8)
    $unguard = { param($v) if ($v.Length -ge 2 -and $v[0] -eq "'" -and '=+-@'.IndexOf($v[1]) -ge 0) { $v.Substring(1) } else { $v } }

    $got = @($files | ForEach-Object { (& $unguard $_.RelativePath) + '|' + $_.SizeBytes } | Sort-Object)
    $exp = @($Expected | ForEach-Object { $_.RelativePath + '|' + $_.Size } | Sort-Object)
    $diff = @(Compare-Object $exp $got)
    Assert-True $Area 'Files report matches ground truth exactly (path + size)' ($diff.Count -eq 0) ("expected $($exp.Count), reported $($got.Count)" + $(if ($diff.Count) { '; diff: ' + (($diff | ForEach-Object { $_.SideIndicator + $_.InputObject }) -join ' ; ') } else { '' }))

    $missing = @($files | Where-Object { -not [IO.File]::Exists($_.FullPath) })
    Assert-True $Area 'Every FullPath exists on disk (Unicode/special characters round-trip)' ($missing.Count -eq 0) "$($missing.Count) missing"

    $sizes = [long[]]@($files | ForEach-Object { [long]$_.SizeBytes })
    $ok = $true; for ($i = 1; $i -lt $sizes.Length; $i++) { if ($sizes[$i] -gt $sizes[$i - 1]) { $ok = $false; break } }
    Assert-True $Area 'Files report sorted by size, largest first' $ok
    $fsizes = [long[]]@($folders | ForEach-Object { [long]$_.TotalSizeBytes })
    $ok = $true; for ($i = 1; $i -lt $fsizes.Length; $i++) { if ($fsizes[$i] -gt $fsizes[$i - 1]) { $ok = $false; break } }
    Assert-True $Area 'Folders report sorted by total size, largest first' $ok

    # Brute-force recomputation of every folder from the Files/Folders reports.
    $byRel = @{}; foreach ($f in $folders) { $byRel[(& $unguard $f.RelativePath)] = $f }
    $bad = [System.Collections.Generic.List[string]]::new()
    $root = $byRel['.']
    foreach ($f in $folders) {
        $rel = & $unguard $f.RelativePath
        $prefix = if ($rel -eq '.') { '' } else { $rel + '\' }
        $direct = @($files | Where-Object { (& $unguard $_.RelativeDirectory) -eq $rel })
        $total  = @($files | Where-Object { $rel -eq '.' -or (& $unguard $_.RelativePath).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
        $dSub = @($folders | Where-Object { (& $unguard $_.ParentRelativePath) -eq $rel -and $_.RelativePath -ne '.' })
        $tSub = @($folders | Where-Object { $r = & $unguard $_.RelativePath; $r -ne '.' -and ($rel -eq '.' -or $r.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) })
        $dSize = [long]0; foreach ($x in $direct) { $dSize += [long]$x.SizeBytes }
        $tSize = [long]0; foreach ($x in $total) { $tSize += [long]$x.SizeBytes }
        $ok = ([long]$f.DirectSizeBytes -eq $dSize) -and ([long]$f.TotalSizeBytes -eq $tSize) -and
              ([long]$f.DirectFileCount -eq $direct.Count) -and ([long]$f.TotalFileCount -eq $total.Count) -and
              ([long]$f.DirectSubfolderCount -eq $dSub.Count) -and ([long]$f.TotalSubfolderCount -eq $tSub.Count)
        if ($total.Count) {
            $max = [long]0; foreach ($x in $total) { if ([long]$x.SizeBytes -gt $max) { $max = [long]$x.SizeBytes } }
            $ok = $ok -and ([math]::Abs([double]$f.LargestFileSizeMB - [math]::Round($max / 1MB, 2)) -lt 0.006)
        }
        if ([long]$root.TotalSizeBytes -gt 0) { $ok = $ok -and ([math]::Abs([double]$f.PercentOfRoot - ([long]$f.TotalSizeBytes * 100.0 / [long]$root.TotalSizeBytes)) -lt 0.0006) }
        if ($rel -ne '.') {
            $p = $byRel[(& $unguard $f.ParentRelativePath)]
            if ([long]$p.TotalSizeBytes -gt 0) { $ok = $ok -and ([math]::Abs([double]$f.PercentOfParent - ([long]$f.TotalSizeBytes * 100.0 / [long]$p.TotalSizeBytes)) -lt 0.0006) }
            $ok = $ok -and ($f.FullPath -eq ($RootPath.TrimEnd('\') + '\' + $rel))
        }
        if (-not $ok) { $bad.Add($rel) }
    }
    Assert-True $Area "Brute-force recomputation of all $($folders.Count) folders (direct/total size, counts, largest file, percentages)" ($bad.Count -eq 0) ($bad -join '; ')

    $convBad = @($files | Where-Object {
        $b = [long]$_.SizeBytes
        [double]$_.SizeKB -ne [math]::Round($b / 1KB, 2) -or [double]$_.SizeMB -ne [math]::Round($b / 1MB, 2) -or [double]$_.SizeGB -ne [math]::Round($b / 1GB, 3)
    })
    Assert-True $Area 'KB/MB/GB use 1024-based units with 2/2/3 decimals' ($convBad.Count -eq 0)

    $guardBad = [System.Collections.Generic.List[string]]::new()
    foreach ($row in $files) {
        $realRel = $row.FullPath.Substring($RootPath.TrimEnd('\').Length + 1)
        $realDir = [IO.Path]::GetDirectoryName($realRel); if (-not $realDir) { $realDir = '.' }
        foreach ($pair in @(@($row.FileName, [IO.Path]::GetFileName($row.FullPath)), @($row.RelativePath, $realRel), @($row.RelativeDirectory, $realDir))) {
            $want = if ('=+-@'.IndexOf($pair[1][0]) -ge 0) { "'" + $pair[1] } else { $pair[1] }
            if ($pair[0] -cne $want) { $guardBad.Add("[$($pair[0])] want [$want]") }
        }
    }
    foreach ($row in $folders) {
        if ($row.RelativePath -eq '.') { continue }
        $realRel = $row.FullPath.Substring($RootPath.TrimEnd('\').Length + 1)
        $want = if ('=+-@'.IndexOf($realRel[0]) -ge 0) { "'" + $realRel } else { $realRel }
        if ($row.RelativePath -cne $want) { $guardBad.Add("folder [$($row.RelativePath)] want [$want]") }
    }
    Assert-True $Area 'Formula guard applied exactly where the real value starts with = + - @ (all text columns)' ($guardBad.Count -eq 0) ($guardBad -join '; ')

    return [pscustomobject]@{ Files = $files; Folders = $folders }
}
