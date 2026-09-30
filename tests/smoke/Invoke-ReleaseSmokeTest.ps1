<#
.SYNOPSIS
    Smoke test of the published StorageInventory.exe (TEST CODE ONLY).
.DESCRIPTION
    Copies dist\StorageInventory.exe to a folder OUTSIDE the repository, starts it WITHOUT any DOTNET_ROOT (so it
    must use its own bundled runtime), and drives it through UI Automation: browse, pre-flight, a complete scan,
    new scan, a cancelled scan, opening the report folder, and a clean exit. Prints PASS/FAIL lines.
.PARAMETER Source
    A folder to scan successfully. .PARAMETER LargeSource: a folder large enough to cancel mid-scan.
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $LargeSource,
    [string] $Exe
)
$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\..\dist\StorageInventory.exe' }   # 5.1: $PSScriptRoot is empty in param defaults
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [Windows.Automation.AutomationElement]
$failures = 0
function Check([bool]$ok, [string]$what, [string]$detail = '') {
    if ($ok) { Write-Host "PASS  $what $detail" } else { Write-Host "FAIL  $what $detail" -ForegroundColor Red; $script:failures++ }
}
function Wait-Until([scriptblock]$cond, [int]$seconds = 60) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) { try { if (& $cond) { return $true } } catch { }; Start-Sleep -Milliseconds 200 }
    return $false
}
function Find-In($root, [string]$name, $type = $null) {
    $c = New-Object Windows.Automation.PropertyCondition($A::NameProperty, $name)
    if ($type) { $c = New-Object Windows.Automation.AndCondition($c, (New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, $type))) }
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
}
function Invoke-Button($root, [string]$name) { (Find-In $root $name ([Windows.Automation.ControlType]::Button)).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Set-Text($root, [string]$name, [string]$value) { (Find-In $root $name ([Windows.Automation.ControlType]::Edit)).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Has($root, [string]$name) { $null -ne (Find-In $root $name) }
# A top-level window whose title starts with the text (Windows 11 titles Explorer windows "<folder> - File Explorer").
# Dialogs owned by the app appear under the app's window in UI Automation, so both places are searched.
function Find-Window([string]$titleStart, $owner = $null) {
    foreach ($scope in @($A::RootElement, $owner)) {
        if ($null -eq $scope) { continue }
        $depth = if ($scope -eq $A::RootElement) { [Windows.Automation.TreeScope]::Children } else { [Windows.Automation.TreeScope]::Descendants }
        $windows = $scope.FindAll($depth, (New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, [Windows.Automation.ControlType]::Window)))
        foreach ($w in $windows) { if ($w.Current.Name.StartsWith($titleStart, [StringComparison]::OrdinalIgnoreCase)) { return $w } }
    }
    return $null
}

# 1. Copy the exe outside the repository; start it with no DOTNET_ROOT in its environment.
$work = Join-Path ([IO.Path]::GetTempPath()) ('StorageInventorySmoke_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
$appDir = Join-Path $work 'app'; $reports = Join-Path $work 'reports'
[void][IO.Directory]::CreateDirectory($appDir)
Copy-Item -LiteralPath $Exe -Destination $appDir
$copied = Join-Path $appDir 'StorageInventory.exe'
$psi = New-Object Diagnostics.ProcessStartInfo $copied
$psi.UseShellExecute = $false
foreach ($v in 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT(x86)') { [void]$psi.Environment.Remove($v) }
$proc = [Diagnostics.Process]::Start($psi)
Check (Wait-Until { $proc.Refresh(); $proc.MainWindowHandle -ne 0 } 60) 'Starts from outside the repo with no DOTNET_ROOT' "(pid $($proc.Id))"
$win = $A::FromHandle($proc.MainWindowHandle)

# Runtime evidence: the process must not load anything from the repo-local SDK.
$proc.Refresh()
$modules = @($proc.Modules | ForEach-Object { $_.FileName })
$fromSdk = @($modules | Where-Object { $_ -like '*StorageInventory\tools\dotnet*' -or $_ -like '*\Program Files\dotnet\*' })
Check ($fromSdk.Count -eq 0) 'Uses its bundled runtime (no module from an installed or repo-local .NET)' "($($modules.Count) modules)"
$coreclr = @($modules | Where-Object { $_ -like '*coreclr*' -or $_ -like '*hostfxr*' })
Write-Host "      runtime modules: $(if ($coreclr) { $coreclr -join '; ' } else { 'embedded in the single-file host' })"

# 2. Browse opens the folder dialog (cancelled again).
Invoke-Button $win 'Browse for the folder or drive to scan'
$dialogOpen = Wait-Until { $null -ne (Find-Window 'Choose a folder or drive to scan' $win) } 15
Check $dialogOpen 'Browse opens the folder picker'
if ($dialogOpen) { (Find-Window 'Choose a folder or drive to scan' $win).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }

# 3. Pre-flight: blocked when the reports would go inside the source, ready otherwise.
Set-Text $win 'Folder or drive to scan' $Source
Set-Text $win 'Folder to save the reports in' (Join-Path $Source 'reports-inside')
Check (Wait-Until { Has $win 'BLOCKED' } 15) 'Pre-flight blocks reports inside the source'
Check (-not (Find-In $win 'Start scan' ([Windows.Automation.ControlType]::Button)).Current.IsEnabled) 'Start scan disabled while blocked'
Set-Text $win 'Folder to save the reports in' $reports
Check (Wait-Until { Has $win 'READY TO SCAN' } 15) 'Pre-flight READY for a valid pair'

# 4. A complete scan.
Invoke-Button $win 'Start scan'
Check (Wait-Until { (Has $win 'SCAN COMPLETE') -or (Has $win 'SCAN FINISHED, BUT INCOMPLETE') } 300) 'Scan finishes and shows its outcome'
$csvs = @(Get-ChildItem -LiteralPath $reports -Filter '*.csv')
Check ($csvs.Count -eq 3) 'Three CSV reports written' "($($csvs.Name -join ', '))"

# 5. Open the report folder (explicit click): an Explorer window for that folder appears; close it again.
$before = @(Get-Process explorer -ErrorAction SilentlyContinue | ForEach-Object Id)
Invoke-Button $win 'Open report folder'
$leaf = Split-Path $reports -Leaf
$explorerOpened = Wait-Until { $null -ne (Find-Window $leaf) } 20
Check $explorerOpened 'Open report folder shows the folder in Explorer'
if ($explorerOpened) { (Find-Window $leaf).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }

# 6. New scan, then a cancelled scan of a large tree.
Invoke-Button $win 'New scan'
Check (Wait-Until { Has $win 'Folder or drive to scan' } 10) 'New scan returns to setup'
Set-Text $win 'Folder or drive to scan' $LargeSource
$cancelReports = Join-Path $work 'reports-cancel'
Set-Text $win 'Folder to save the reports in' $cancelReports
Check (Wait-Until { Has $win 'READY TO SCAN' } 15) 'Ready for the large scan'
Invoke-Button $win 'Start scan'
Check (Wait-Until { (Find-In $win 'Cancel scan' ([Windows.Automation.ControlType]::Button)).Current.IsEnabled } 30) 'Scan running with Cancel available'
Invoke-Button $win 'Cancel scan'
Check (Wait-Until { Has $win 'SCAN CANCELLED' } 60) 'Cancel stops the scan and says so'
$locked = @(Get-ChildItem -LiteralPath $cancelReports | Where-Object { try { $h = [IO.File]::Open($_.FullName, 'Open', 'Read', 'None'); $h.Dispose(); $false } catch { $true } })
Check ($locked.Count -eq 0) 'No report file left open after cancelling'

# 7. Clean exit.
[void]$proc.CloseMainWindow()
Check ($proc.WaitForExit(20000)) 'Exits cleanly when closed' "(exit code $(if ($proc.HasExited) { $proc.ExitCode }))"

$extract = Join-Path ([IO.Path]::GetTempPath()) '.net\StorageInventory'
Write-Host "      single-file native extraction folder: $extract (exists: $(Test-Path -LiteralPath $extract))"
Write-Host "      work folder: $work"
if ($failures) { Write-Host "RESULT: $failures FAILED"; exit 1 } else { Write-Host 'RESULT: all smoke checks passed' }
