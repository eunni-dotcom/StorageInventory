<#
  TEMPORARY C2 diagnostic (evidence branch only). Drives the published exe through repeated scan -> results -> open
  report folder -> close Explorer -> New scan cycles and, whenever the results are showing, asks UI Automation for the
  "New scan" button again and again, counting every time FindFirst does not return it.
#>
param([Parameter(Mandatory)] [string] $Source, [Parameter(Mandatory)] [string] $Exe, [int] $Cycles = 15)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [Windows.Automation.AutomationElement]
$work = Join-Path ([IO.Path]::GetTempPath()) ('SIStress_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
$appDir = Join-Path $work 'app'
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
$Button = [Windows.Automation.ControlType]::Button
$stats = @{ probes = 0; missing = 0; threw = 0 }
function Probe([string]$phase, [int]$times) {
    for ($i = 0; $i -lt $times; $i++) {
        $stats.probes++
        try { if (-not (Find-In $win 'New scan' $Button)) { $stats.missing++; Write-Host "MISSING [$phase] probe $i" } }
        catch { $stats.threw++; Write-Host "THREW [$phase] probe $i : $($_.Exception.GetType().Name) $($_.Exception.Message)" }
    }
}
function Wait-Until([scriptblock]$cond, [int]$seconds) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    while ($t.Elapsed.TotalSeconds -lt $seconds) { try { if (& $cond) { return $true } } catch { }; Start-Sleep -Milliseconds 100 }
    return $false
}
function Set-Text([string]$name, [string]$value) { (Find-In $win $name ([Windows.Automation.ControlType]::Edit)).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
    $reports = Join-Path $work "reports-$cycle"
    Set-Text 'Folder or drive to scan' $Source
    Set-Text 'Folder to save the reports in' $reports
    if (-not (Wait-Until { $null -ne (Find-In $win 'READY TO SCAN') } 15)) { Write-Host "cycle $cycle : not ready"; break }
    (Find-In $win 'Start scan' $Button).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not (Wait-Until { $null -ne (Find-In $win 'SCAN COMPLETE') } 60)) { Write-Host "cycle $cycle : no result"; break }
    Probe "cycle $cycle, results shown (tabs loading)" 15
    (Find-In $win 'Open report folder' $Button).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $leaf = Split-Path $reports -Leaf
    $explorer = $null
    [void](Wait-Until { foreach ($w in $A::RootElement.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, [Windows.Automation.ControlType]::Window)))) { if ($w.Current.Name.StartsWith($leaf, [StringComparison]::OrdinalIgnoreCase)) { $script:explorer = $w; return $true } }; $false } 20)
    if ($explorer) { $explorer.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() } else { Write-Host "cycle $cycle : no Explorer window" }
    Probe "cycle $cycle, right after Explorer closed" 15
    $new = Find-In $win 'New scan' $Button
    if (-not $new) { [void](Wait-Until { $script:new = Find-In $win 'New scan' $Button; $null -ne $script:new } 10) }
    if (-not $new) { Write-Host "cycle $cycle : New scan never found"; break }
    $new.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
"STRESS SUMMARY: $($stats.probes) probes for 'New scan' while results were shown: $($stats.missing) missing, $($stats.threw) threw"
[void]$proc.CloseMainWindow(); [void]$proc.WaitForExit(20000)
