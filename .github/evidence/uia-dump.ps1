<#
  TEMPORARY C2 diagnostic (evidence branch only). Starts the published exe, runs a scan to Results, and dumps what
  UI Automation can see, step by step: the control view under the main window, and whether FindFirst reaches each
  result button, before and after the exploration tabs load and after the report folder is opened in Explorer.
#>
param([Parameter(Mandatory)] [string] $Source, [string] $Exe)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [Windows.Automation.AutomationElement]
$work = Join-Path ([IO.Path]::GetTempPath()) ('SIUia_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
$appDir = Join-Path $work 'app'; $reports = Join-Path $work 'reports'
[void][IO.Directory]::CreateDirectory($appDir)
Copy-Item -LiteralPath $Exe -Destination $appDir
$proc = Start-Process -FilePath (Join-Path $appDir 'StorageInventory.exe') -PassThru
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 60) { $proc.Refresh(); if ($proc.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 200 }
$win = $A::FromHandle($proc.MainWindowHandle)
function Find-In($root, [string]$name, $type = $null) {
    $c = New-Object Windows.Automation.PropertyCondition($A::NameProperty, $name)
    if ($type) { $c = New-Object Windows.Automation.AndCondition($c, (New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, $type))) }
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
}
function Probe([string]$when) {
    foreach ($n in 'Open Files report', 'Open report folder', 'New scan') {
        $t = [Diagnostics.Stopwatch]::StartNew()
        try { $e = Find-In $win $n ([Windows.Automation.ControlType]::Button); $r = if ($e) { 'found' } else { 'NOT FOUND' } } catch { $r = 'THREW ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message }
        Write-Host ("PROBE [{0}] Button '{1}': {2} ({3} ms)" -f $when, $n, $r, $t.ElapsedMilliseconds)
    }
}
function Dump([string]$when, $walker, [int]$max = 400) {
    Write-Host "DUMP [$when] $($walker.GetType().Name)"
    $count = 0
    function Walk($e, [int]$depth) {
        if ($script:count -ge $max) { return }
        $script:count++
        try { $i = $e.Current; $line = ('  ' * $depth) + "$($i.ControlType.ProgrammaticName -replace 'ControlType\.','') '$($i.Name)' id='$($i.AutomationId)' class='$($i.ClassName)' offscreen=$($i.IsOffscreen) control=$($i.IsControlElement)" } catch { $line = ('  ' * $depth) + "<error $($_.Exception.GetType().Name)>" }
        Write-Host $line
        try { $c = $walker.GetFirstChild($e) } catch { Write-Host (('  ' * ($depth + 1)) + "<GetFirstChild threw $($_.Exception.GetType().Name)>"); return }
        while ($c) {
            Walk $c ($depth + 1)
            try { $c = $walker.GetNextSibling($c) } catch { Write-Host (('  ' * ($depth + 1)) + "<GetNextSibling threw $($_.Exception.GetType().Name)>"); break }
        }
    }
    $script:count = 0
    Walk $win 0
}
function Set-Text([string]$name, [string]$value) { (Find-In $win $name ([Windows.Automation.ControlType]::Edit)).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
Set-Text 'Folder or drive to scan' $Source
Set-Text 'Folder to save the reports in' $reports
Start-Sleep -Seconds 2
(Find-In $win 'Start scan' ([Windows.Automation.ControlType]::Button)).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
$sw.Restart(); while ($sw.Elapsed.TotalSeconds -lt 60 -and -not (Find-In $win 'SCAN COMPLETE')) { Start-Sleep -Milliseconds 200 }
Probe 'results shown'
Start-Sleep -Seconds 8
Probe 'after the tabs loaded'
Dump 'after the tabs loaded' ([Windows.Automation.TreeWalker]::ControlViewWalker)
(Find-In $win 'Open report folder' ([Windows.Automation.ControlType]::Button)).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 6
$leaf = Split-Path $reports -Leaf
foreach ($w in $A::RootElement.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, [Windows.Automation.ControlType]::Window)))) {
    if ($w.Current.Name.StartsWith($leaf, [StringComparison]::OrdinalIgnoreCase)) { Write-Host "closing Explorer window '$($w.Current.Name)'"; $w.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
}
Start-Sleep -Seconds 2
Probe 'after Explorer'
Dump 'after Explorer' ([Windows.Automation.TreeWalker]::ControlViewWalker)
Dump 'after Explorer (raw)' ([Windows.Automation.TreeWalker]::RawViewWalker) 600
[void]$proc.CloseMainWindow(); [void]$proc.WaitForExit(20000)
