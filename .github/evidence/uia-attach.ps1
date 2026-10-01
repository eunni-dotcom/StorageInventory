<#
  TEMPORARY C2 diagnostic (evidence branch only). After a failed smoke test the app is still running: attach to it and
  check, over time, whether UI Automation can find the result buttons, and dump the control view.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [Windows.Automation.AutomationElement]
$p = Get-Process StorageInventory -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { 'ATTACH: no StorageInventory process is left'; exit 0 }
$win = $A::FromHandle($p.MainWindowHandle)
"ATTACH: pid $($p.Id), window '$($win.Current.Name)', responding=$($p.Responding)"
function Find-In($root, [string]$name, $type) {
    $c = New-Object Windows.Automation.AndCondition((New-Object Windows.Automation.PropertyCondition($A::NameProperty, $name)), (New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, $type)))
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
}
foreach ($delay in 0, 5, 15) {
    Start-Sleep -Seconds $delay
    foreach ($n in 'Open report folder', 'New scan') {
        $t = [Diagnostics.Stopwatch]::StartNew()
        try { $r = if (Find-In $win $n ([Windows.Automation.ControlType]::Button)) { 'found' } else { 'NOT FOUND' } } catch { $r = 'THREW ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message }
        "ATTACH PROBE [+$delay s] Button '$n': $r ($($t.ElapsedMilliseconds) ms)"
    }
}
$walker = [Windows.Automation.TreeWalker]::ControlViewWalker
function Walk($e, [int]$depth) {
    if ($script:n++ -gt 200) { return }
    try { $i = $e.Current; ('  ' * $depth) + "$($i.ControlType.ProgrammaticName -replace 'ControlType\.','') '$($i.Name)' class='$($i.ClassName)'" } catch { ('  ' * $depth) + "<error $($_.Exception.GetType().Name)>" }
    $c = $walker.GetFirstChild($e)
    while ($c) { Walk $c ($depth + 1); $c = $walker.GetNextSibling($c) }
}
$script:n = 0
Walk $win 0
$p.Kill()
