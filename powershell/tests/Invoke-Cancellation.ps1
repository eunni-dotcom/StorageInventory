# TEST CODE ONLY. Runs StorageInventory.ps1 in a runspace and stops it the same way Ctrl+C does (StopProcessing).
#   -Phase Scan  : stop while the tree is being scanned
#   -Phase Excel : stop after the CSV reports are finished, during the optional workbook step
# Emits one JSON object describing what happened.
param(
    [Parameter(Mandatory)] [string] $ScriptUnderTest,
    [Parameter(Mandatory)] [string] $Root,
    [Parameter(Mandatory)] [string] $Out,
    [ValidateSet('Scan', 'Excel')] [string] $Phase = 'Scan'
)
$ErrorActionPreference = 'Stop'

$ps = [PowerShell]::Create()
[void]$ps.AddCommand($ScriptUnderTest).AddParameter('RootPath', $Root).AddParameter('OutputPath', $Out)
$async = $ps.BeginInvoke()

$sw = [Diagnostics.Stopwatch]::StartNew()
$triggered = $false
while ($sw.ElapsedMilliseconds -lt 180000 -and -not $async.IsCompleted) {
    $files = if ([IO.Directory]::Exists($Out)) { [IO.Directory]::GetFiles($Out) } else { @() }
    if ($Phase -eq 'Scan') {
        # The temp file exists: the scan has started (it is created just before the walk begins).
        if (@($files | Where-Object { $_.EndsWith('.unsorted.tmp') }).Count) { $triggered = $true; break }
    }
    else {
        # Folders + Files CSVs exist and the temp file is gone: the CSV stage is finished, the workbook stage is running.
        $hasFolders = @($files | Where-Object { [IO.Path]::GetFileName($_) -like 'Folders_*.csv' }).Count -gt 0
        $hasFiles   = @($files | Where-Object { [IO.Path]::GetFileName($_) -like 'Files_*.csv' }).Count -gt 0
        $hasTemp    = @($files | Where-Object { $_.EndsWith('.unsorted.tmp') }).Count -gt 0
        if ($hasFolders -and $hasFiles -and -not $hasTemp) { Start-Sleep -Milliseconds 500; $triggered = $true; break }
    }
    Start-Sleep -Milliseconds 20
}
$completedBeforeStop = $async.IsCompleted
$ps.Stop()

$locked = @()
foreach ($f in [IO.Directory]::GetFiles($Out)) {
    try { $h = [IO.File]::Open($f, 'Open', 'Read', 'None'); $h.Dispose() } catch { $locked += [IO.Path]::GetFileName($f) }
}
[pscustomobject]@{
    Triggered           = $triggered
    CompletedBeforeStop = $completedBeforeStop
    State               = $ps.InvocationStateInfo.State.ToString()
    HostMessages        = @($ps.Streams.Information | ForEach-Object { [string]$_.MessageData })
    Files               = @([IO.Directory]::GetFiles($Out) | ForEach-Object { [IO.Path]::GetFileName($_) })
    Locked              = $locked
} | ConvertTo-Json -Depth 3
$ps.Dispose()
