<#
.SYNOPSIS
    Visual startup gate for a published StorageInventory.exe (TEST CODE ONLY).
.DESCRIPTION
    v1.0.0 started, owned a window and answered UI Automation, yet users could see a blank white window. This gate
    checks pixels. For each requested app theme it copies the exe to a folder OUTSIDE the repository, starts it with no
    DOTNET_ROOT, waits for the setup screen, and asserts with VisualCheck.ps1 that the window shows its interface on
    screen, paints its own opaque background (its landmarks stay visible over a white and a black underlay), and asks
    the compositor for no backdrop material behind it. See VisualCheck.ps1 for the exact rules.

    The per-user app theme (HKCU ...\Themes\Personalize: AppsUseLightTheme, SystemUsesLightTheme) is set for each run
    and restored afterwards, even on failure. With -Cold the single-file extraction folder %TEMP%\.net\StorageInventory
    is removed before the first start, so the first-run path is covered too.

    Needs an interactive, unlocked desktop session. Apart from those two theme values, it writes only its work folder
    (the copied exe and PNGs of the application's client area, no other desktop content). Prints PASS/FAIL lines; exit
    code 0 only when all pass.
.PARAMETER Exe
    The executable to test (default: dist\StorageInventory.exe).
.PARAMETER Themes
    App themes to test: Light, Dark, Current (default: Light, Dark).
.PARAMETER OutDir
    Where to keep the client-area PNGs (default: the work folder).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests\smoke\Test-VisibleStartup.ps1 -Exe C:\temp\StorageInventory.exe -Cold
#>
param(
    [string] $Exe,
    [ValidateSet('Light', 'Dark', 'Current')] [string[]] $Themes = @('Light', 'Dark'),
    [string] $OutDir,
    [switch] $Cold
)
$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\..\dist\StorageInventory.exe' }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
. (Join-Path $PSScriptRoot 'VisualCheck.ps1')
$A = [Windows.Automation.AutomationElement]

$failures = 0
function Check([bool] $ok, [string] $what, [string] $detail = '') {
    if ($ok) { Write-Host "PASS  $what $detail" } else { Write-Host "FAIL  $what $detail" -ForegroundColor Red; $script:failures++ }
}
function Wait-Until([scriptblock] $cond, [int] $seconds = 60) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) { try { if (& $cond) { return $true } } catch { }; Start-Sleep -Milliseconds 200 }
    return $false
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('StorageInventoryVisual_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
if (-not $OutDir) { $OutDir = $work }
[void][IO.Directory]::CreateDirectory($work); [void][IO.Directory]::CreateDirectory($OutDir)
$appDir = Join-Path $work 'app'
[void][IO.Directory]::CreateDirectory($appDir)
Copy-Item -LiteralPath $Exe -Destination $appDir
$copied = Join-Path $appDir 'StorageInventory.exe'
Write-Host "      exe: $Exe ($((Get-Item -LiteralPath $Exe).Length) bytes, SHA-256 $((Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash))"

if ($Cold) {
    $extract = Join-Path ([IO.Path]::GetTempPath()) '.net\StorageInventory'
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    Write-Host "      removed the single-file extraction folder for a cold first start"
}

$setupLandmarks = @(
    @{ Name = 'Storage Inventory'; Type = [Windows.Automation.ControlType]::Text },
    @{ Name = 'Folder to save the reports in'; Type = [Windows.Automation.ControlType]::Edit },
    @{ Name = 'Browse for the folder or drive to scan'; Type = [Windows.Automation.ControlType]::Button },
    @{ Name = 'Browse for the folder to save the reports in'; Type = [Windows.Automation.ControlType]::Button },
    @{ Name = 'Sort individual files largest-first'; Type = [Windows.Automation.ControlType]::CheckBox },
    @{ Name = 'Also create an Excel workbook (.xlsx)'; Type = [Windows.Automation.ControlType]::CheckBox }
)

$personalize = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$saved = @{}
foreach ($n in 'AppsUseLightTheme', 'SystemUsesLightTheme') { $saved[$n] = (Get-ItemProperty -Path $personalize -Name $n -ErrorAction SilentlyContinue).$n }
try {
    foreach ($theme in $Themes) {
        if ($theme -ne 'Current') {
            if (-not (Test-Path $personalize)) { New-Item -Path $personalize -Force | Out-Null }
            $light = [int]($theme -eq 'Light')
            Set-ItemProperty $personalize AppsUseLightTheme $light -Type DWord
            Set-ItemProperty $personalize SystemUsesLightTheme $light -Type DWord
        }
        $psi = New-Object Diagnostics.ProcessStartInfo $copied
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = $appDir
        foreach ($v in 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT(x86)') { [void]$psi.Environment.Remove($v) }
        $proc = [Diagnostics.Process]::Start($psi)
        try {
            $started = Wait-Until { $proc.Refresh(); $proc.HasExited -or $proc.MainWindowHandle -ne 0 } 60
            Check ($started -and -not $proc.HasExited) "[$theme] starts and owns a window" "(pid $($proc.Id))"
            if ($proc.HasExited -or -not $started) { continue }
            $hwnd = $proc.MainWindowHandle
            $win = $A::FromHandle($hwnd)
            $settled = Wait-Until { $null -ne $win.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition($A::NameProperty, 'NOT READY'))) } 30
            Check $settled "[$theme] setup screen ready (UI Automation)"
            $proc.Refresh()
            Check ($proc.Responding -and -not [SiVisual.Native]::IsHungAppWindow($hwnd)) "[$theme] UI thread responding"
            foreach ($r in (Assert-WindowPainted -Hwnd $hwnd -Landmarks $setupLandmarks -Label "[$theme] setup screen" -SavePng (Join-Path $OutDir "setup-$($theme.ToLowerInvariant()).png"))) {
                Check $r.Ok $r.Name $r.Detail
            }
        }
        finally {
            if (-not $proc.HasExited) { [void]$proc.CloseMainWindow(); if (-not $proc.WaitForExit(15000)) { $proc.Kill() } }
        }
        Check ($proc.HasExited -and $proc.ExitCode -eq 0) "[$theme] exits cleanly when closed" "(exit code $(if ($proc.HasExited) { $proc.ExitCode }))"
    }
}
finally {
    foreach ($n in $saved.Keys) {
        if ($null -eq $saved[$n]) { Remove-ItemProperty -Path $personalize -Name $n -ErrorAction SilentlyContinue }
        else { Set-ItemProperty $personalize $n $saved[$n] -Type DWord }
    }
}

Write-Host "      screenshots (application client area only): $OutDir"
if ($failures) { Write-Host "RESULT: $failures FAILED"; exit 1 } else { Write-Host 'RESULT: all visual startup checks passed' }
