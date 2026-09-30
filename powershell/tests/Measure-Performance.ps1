<#
.SYNOPSIS
    Measures StorageInventory.ps1 wall time and peak memory at several tree sizes, sorted vs -NoSort. TEST CODE ONLY.
.DESCRIPTION
    Builds synthetic trees of small files under %TEMP%\StorageInventoryPerf, runs the script in a child process of the
    chosen shell, samples the child's peak working set, and prints a table. Results are MEASUREMENTS on this machine
    for these synthetic trees only; they are not predictions for other hardware or other tree shapes.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Measure-Performance.ps1 -Shell powershell -Sizes 10000,60000,250000
#>
param(
    [string] $Shell = 'powershell',
    [string] $Sizes = '10000,60000,250000',   # comma-separated (a string so it also works with -File)
    [switch] $KeepTrees
)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'TestLib.ps1')
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:ScriptUnderTest = Join-Path $repoRoot 'powershell\StorageInventory.ps1'
$script:Shell = switch ($Shell) {
    'powershell' { Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }
    'pwsh' { $local = Join-Path $repoRoot 'tools\pwsh\pwsh.exe'; if (Test-Path -LiteralPath $local) { $local } else { (Get-Command pwsh).Source } }
    default { $Shell }
}
$script:WorkRoot = Join-Path ([IO.Path]::GetTempPath()) 'StorageInventoryPerf'
[void][IO.Directory]::CreateDirectory($script:WorkRoot)

$rows = foreach ($size in @($Sizes.Split(',') | ForEach-Object { [int]$_.Trim() })) {
    # 40 files per album, 25 albums per artist.
    $artists = [Math]::Max(1, [int][Math]::Ceiling($size / 1000.0))
    $tree = Join-Path $script:WorkRoot "tree_$size"
    if (-not [IO.Directory]::Exists($tree)) { Write-Host "Building $size-file tree..."; New-BulkFixture -Root $tree -Artists $artists -Albums 25 -Tracks 40 }
    $null = Get-TreeSnapshot -Root $tree   # warm the cache and settle NTFS; every measured run then sees a warm cache
    foreach ($mode in @('Sorted', 'NoSort')) {
        $out = Join-Path $script:WorkRoot ("out_{0}_{1}_{2}" -f $size, $mode, [guid]::NewGuid().ToString('N').Substring(0, 6))
        $scriptArgs = @('-RootPath', $tree, '-OutputPath', $out, '-SkipExcel')
        if ($mode -eq 'NoSort') { $scriptArgs += '-NoSort' }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $proc = Start-InventoryProcess -Arguments $scriptArgs -LogPrefix (Join-Path $script:WorkRoot 'perf')
        $peak = 0L
        while (-not $proc.HasExited) {
            try { $proc.Refresh(); if ($proc.PeakWorkingSet64 -gt $peak) { $peak = $proc.PeakWorkingSet64 } } catch { }
            Start-Sleep -Milliseconds 50
        }
        $sw.Stop()
        $filesCsv = @([IO.Directory]::GetFiles($out, 'Files_*.csv'))[0]
        [pscustomobject]@{
            Files          = $size
            Mode           = $mode
            ExitCode       = $proc.ExitCode
            WallSeconds    = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
            MicrosPerFile  = [Math]::Round($sw.Elapsed.TotalMilliseconds * 1000 / $size, 0)
            PeakWorkingMB  = [Math]::Round($peak / 1MB, 0)
            FilesCsvMB     = [Math]::Round(([IO.FileInfo]::new($filesCsv)).Length / 1MB, 1)
        }
        Remove-Item -LiteralPath $out -Recurse -Force
    }
    if (-not $KeepTrees) { Remove-Item -LiteralPath $tree -Recurse -Force }
}
$shellVersion = (& $script:Shell -NoProfile -Command '$PSVersionTable.PSVersion.ToString()') | Select-Object -Last 1
Write-Host "Shell: $script:Shell ($shellVersion); CPU: $((Get-CimInstance Win32_Processor | Select-Object -First 1).Name)"
$rows | Format-Table -AutoSize | Out-String -Width 200
