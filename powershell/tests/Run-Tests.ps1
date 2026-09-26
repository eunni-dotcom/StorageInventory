<#
.SYNOPSIS
    Regression suite for StorageInventory.ps1. TEST CODE ONLY.

.DESCRIPTION
    Builds throw-away fixture trees under %TEMP%\StorageInventoryTests (junctions, deny-ACLs, odd names, long paths),
    runs the script under test in a separate process of the chosen shell, and checks the reports, the console output,
    the exit codes and that the scanned tree is unchanged. Prints PASS / FAIL / SKIP per check.

.PARAMETER Shell
    'powershell' (Windows PowerShell 5.1) or 'pwsh' (uses tools\pwsh\pwsh.exe, else pwsh on PATH), or a full path.

.PARAMETER Excel
    Run the workbook tests. Needs ImportExcel; tools\psmodules is added to PSModulePath for those runs only.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-Tests.ps1 -Shell powershell -Excel
#>
param(
    [string] $Shell = 'powershell',
    [switch] $Excel,
    [switch] $KeepArtifacts
)
# 'Continue': native tools (icacls, mklink, subst) write expected messages to stderr, which 5.1 would treat as fatal
# under 'Stop'. .NET exceptions still stop the harness, and every behaviour is checked by an explicit assertion.
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'TestLib.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:ScriptUnderTest = Join-Path $repoRoot 'powershell\StorageInventory.ps1'
$toolsModules = Join-Path $repoRoot 'tools\psmodules'
switch ($Shell) {
    'powershell' { $script:Shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }
    'pwsh' {
        $local = Join-Path $repoRoot 'tools\pwsh\pwsh.exe'
        $script:Shell = if (Test-Path -LiteralPath $local) { $local } else { (Get-Command pwsh -ErrorAction Stop).Source }
    }
    default { $script:Shell = $Shell }
}
$shellVersion = (& $script:Shell -NoProfile -Command '$PSVersionTable.PSVersion.ToString()') | Select-Object -Last 1
$script:WorkRoot = Join-Path ([IO.Path]::GetTempPath()) ('StorageInventoryTests\' + [IO.Path]::GetFileNameWithoutExtension($script:Shell) + '_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
[void][IO.Directory]::CreateDirectory($script:WorkRoot)
Write-Host "Script under test: $script:ScriptUnderTest"
Write-Host "Shell:             $script:Shell ($shellVersion)"
Write-Host "Work folder:       $script:WorkRoot"
Write-Host ''

try {
    #region ---- Static audit of the script source -----------------------------------------------------------------
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:ScriptUnderTest, [ref]$tokens, [ref]$parseErrors)
    Assert-True 'Static' 'Script parses without errors' ($parseErrors.Count -eq 0)
    $nonAscii = @([IO.File]::ReadAllBytes($script:ScriptUnderTest) | Where-Object { $_ -gt 127 }).Count
    Assert-True 'Static' 'Script is pure ASCII (no hidden Unicode; safe to copy/paste into 5.1)' ($nonAscii -eq 0) "$nonAscii non-ASCII bytes"

    $defined = @($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | ForEach-Object { $_.Name })
    $commandAsts = @($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true))
    $dynamic = @($commandAsts | Where-Object { -not $_.GetCommandName() })
    Assert-True 'Static' 'No dynamically-invoked commands (every command is a literal name)' ($dynamic.Count -eq 0)
    $allowed = @('Write-Host', 'Write-Warning', 'Write-Progress', 'Write-Verbose', 'Set-StrictMode', 'Get-Module', 'Import-Module',
                 'Import-Csv', 'Export-Excel', 'Sort-Object', 'Select-Object', 'Get-Command')
    $external = @($commandAsts | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ -and $defined -notcontains $_ } | Sort-Object -Unique)
    $unexpected = @($external | Where-Object { $allowed -notcontains $_ })
    Assert-True 'Static' 'Only allow-listed commands are invoked' ($unexpected.Count -eq 0) (($external -join ', ') + $(if ($unexpected) { '  UNEXPECTED: ' + ($unexpected -join ', ') } else { '' }))

    $statics = @($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and $args[0].Static }, $true))
    $staticNames = @($statics | ForEach-Object { $_.Expression.TypeName.FullName + '::' + $_.Member.Value })
    $mutating = @($staticNames | Where-Object { $_ -match '::(Delete|Move|Copy|Replace|Create|CreateDirectory|WriteAll\w*|AppendAll\w*|SetAttributes|Set\w*Time\w*|Encrypt|Decrypt)$' })
    $expectedMutating = @('System.IO.Directory::CreateDirectory', 'System.IO.File::Delete', 'System.IO.File::Move')
    Assert-True 'Static' 'Only CreateDirectory, File.Delete and File.Move are used as static mutating .NET calls, once each' `
        ((($mutating | Sort-Object) -join ',') -eq (($expectedMutating | Sort-Object) -join ',')) ($mutating -join ', ')
    $deleteCall = $statics | Where-Object { $_.Member.Value -eq 'Delete' }
    $moveCall   = $statics | Where-Object { $_.Member.Value -eq 'Move' }
    $enclosing = { param($node) $p = $node.Parent; while ($p -and $p -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) { $p = $p.Parent }; if ($p) { $p.Name } else { '<script>' } }
    Assert-True 'Static' 'File.Delete only inside Remove-OwnTemporaryFile' ((& $enclosing $deleteCall) -eq 'Remove-OwnTemporaryFile')
    Assert-True 'Static' 'File.Move only inside Complete-OwnWorkbookFile' ((& $enclosing $moveCall) -eq 'Complete-OwnWorkbookFile')
    $fileStreams = @($statics | Where-Object { $_.Expression.TypeName.FullName -eq 'System.IO.FileStream' })
    $streamModes = @($fileStreams | ForEach-Object { $_.Arguments[1].Extent.Text + '/' + $_.Arguments[2].Extent.Text })
    Assert-True 'Static' 'FileStream is only opened as CreateNew+Write or Open+Read' `
        (@($streamModes | Where-Object { $_ -notin @('[System.IO.FileMode]::CreateNew/[System.IO.FileAccess]::Write', '[System.IO.FileMode]::Open/[System.IO.FileAccess]::Read') }).Count -eq 0) ($streamModes -join '; ')

    $source = [IO.File]::ReadAllText($script:ScriptUnderTest)
    $forbidden = [ordered]@{
        'Remove/Move/Rename/Copy-Item' = '\b(Remove|Move|Rename|Copy|New|Set|Clear)-Item\b'
        'Content/Out-File cmdlets'     = '\b(Set|Add|Clear)-Content\b|\bOut-File\b'
        'ACL changes'                  = '\bSet-Acl\b|\b(icacls|takeown|cacls)\b'
        'Dynamic code'                 = '\bInvoke-Expression\b|\biex\b|ScriptBlock\]::Create|\.InvokeScript\(|\bAdd-Type\b|DllImport|\bReflection\b'
        'Process launching'            = '\bStart-(Process|Job)\b|Diagnostics\.Process\b|\bInvoke-(Item|Command)\b'
        'Network'                      = '\bInvoke-(WebRequest|RestMethod)\b|\b(iwr|irm|curl|wget|bitsadmin|certutil)\b|System\.Net\b|\b(Socket|TcpClient|HttpClient|WebClient)\b'
        'Registry / system config'     = '\bHK(LM|CU):|\breg\.exe\b|Microsoft\.Win32\.Registry|\bschtasks\b|\bsc\.exe\b|\bRegister-\w+|ExecutionPolicy'
        'Encoded/obfuscated'           = 'FromBase64String|EncodedCommand|-enc\s'
        'Scope escapes'                = '\$global:|\$env:\w+\s*='
        'External executables'         = '\b(cmd|powershell|pwsh|robocopy|xcopy|diskpart|cipher|format)\.exe\b'
    }
    foreach ($k in $forbidden.Keys) {
        $hits = [regex]::Matches($source, $forbidden[$k]).Count
        Assert-True 'Static' "No $k" ($hits -eq 0) "$hits hit(s)"
    }
    #endregion

    #region ---- Main fixture: correctness, completeness, reparse behaviour, tree unchanged ----------------------
    $fx = New-Fixture -Base (Join-Path $script:WorkRoot 'main')
    foreach ($k in $fx.Features.Keys) { Write-Host ("Fixture feature {0}: {1}" -f $k, $fx.Features[$k]) }
    $null = Get-TreeSnapshot -Root $fx.Root   # first listing lets NTFS settle lazily-updated folder timestamps
    $beforeListing = Get-TreeSnapshot -Root $fx.Root
    $beforeRecords = Get-TreeSnapshot -Root $fx.Root -TrueValues
    $mainOut = Join-Path $script:WorkRoot 'main-out'
    $run = Invoke-Inventory @('-RootPath', $fx.Root, '-OutputPath', $mainOut, '-SkipExcel')
    Assert-True 'Tree' 'Scanned tree unchanged (listing view)' ($beforeListing -eq (Get-TreeSnapshot -Root $fx.Root))
    Assert-True 'Tree' 'Scanned tree unchanged (per-item records)' ($beforeRecords -eq (Get-TreeSnapshot -Root $fx.Root -TrueValues))

    $reports = Test-InventoryReports -Area 'Reports' -OutDir $mainOut -Expected $fx.Expected -RootPath $fx.Root
    $folderBy = @{}; foreach ($f in $reports.Folders) { $folderBy[$f.RelativePath] = $f }
    $errors = @(Import-Csv -LiteralPath (Get-LatestReport $mainOut 'ScanErrors_') -Encoding UTF8)
    Assert-True 'Output' 'Temporary file removed after a successful sorted run' (@([IO.Directory]::GetFiles($mainOut, '*.tmp')).Count -eq 0)
    Assert-True 'Output' 'Report names carry timestamp + run ID' ((Get-RunIdFromReport (Get-LatestReport $mainOut 'Files_')) -ne $null)

    # A3: completeness
    Assert-True 'Complete' 'Exit code 2 when some folders could not be read' ($run.ExitCode -eq 2) "exit $($run.ExitCode)"
    Assert-True 'Complete' 'Console does NOT claim "Scan complete."' ($run.Output -notmatch 'Scan complete\.')
    Assert-True 'Complete' 'Console states the scan is INCOMPLETE and totals are lower bounds' ($run.Output -match 'INCOMPLETE' -and $run.Output -match 'LOWER BOUNDS')
    Assert-True 'Complete' 'Warning distinguishes locally-unreadable folders (2) from affected parents (3)' ($run.Output -match '2 folder\(s\) could not be fully read themselves, and 3 parent folder\(s\)')
    Assert-True 'Complete' 'Root SubtreeComplete=False' ($folderBy['.'].SubtreeComplete -eq 'False')
    Assert-True 'Complete' 'Denied folder: ScanStatus=AccessDenied, SubtreeComplete=False' ($folderBy['Denied'].ScanStatus -eq 'AccessDenied' -and $folderBy['Denied'].SubtreeComplete -eq 'False')
    Assert-True 'Complete' 'Nested denied folder: AccessDenied' ($folderBy['Kpop\aespa\Private'].ScanStatus -eq 'AccessDenied')
    Assert-True 'Complete' 'Ancestors of a denied folder: ScanStatus=OK but SubtreeComplete=False' `
        ($folderBy['Kpop\aespa'].ScanStatus -eq 'OK' -and $folderBy['Kpop\aespa'].SubtreeComplete -eq 'False' -and $folderBy['Kpop'].SubtreeComplete -eq 'False')
    Assert-True 'Complete' 'Unaffected sibling stays SubtreeComplete=True' ($folderBy['Kpop\TWICE'].SubtreeComplete -eq 'True' -and $folderBy['Deep'].SubtreeComplete -eq 'True')
    Assert-True 'Complete' 'Skipped links are NOT counted as incomplete' ($folderBy['Loop'].SubtreeComplete -eq 'True' -and $folderBy['Kpop\LinkOut'].SubtreeComplete -eq 'True')
    if ($fx.Features['InvalidTimestamp'] -eq 'created') {
        $badTime = @($errors | Where-Object { $_.ErrorType -eq 'InvalidTimestamp' -and $_.Path -like '*bad-time.dat' })
        Assert-True 'Complete' 'Invalid timestamp: reported as InvalidTimestamp, file still counted, folder still complete' `
            ($badTime.Count -ge 1 -and $folderBy['Odd'].TotalFileCount -eq '1' -and $folderBy['Odd'].SubtreeComplete -eq 'True')
    }
    else { Add-Result 'Complete' 'Invalid timestamp handling' 'SKIP' 'could not stamp an out-of-range FILETIME on this machine' }

    # A4: reparse points
    Assert-True 'Reparse' 'Junction loop listed, not followed' ($folderBy['Loop'].ScanStatus -eq 'ReparsePointSkipped' -and $folderBy['Loop'].TotalFileCount -eq '0')
    Assert-True 'Reparse' 'Junction pointing outside the tree listed, not followed (5 MB outside file not counted)' `
        ($folderBy['Kpop\LinkOut'].ScanStatus -eq 'ReparsePointSkipped' -and @($reports.Files | Where-Object { $_.FileName -eq 'big-outside.bin' }).Count -eq 0)
    Assert-True 'Reparse' 'Junction rows in ScanErrors state the link type and target' `
        (@($errors | Where-Object { $_.ErrorType -eq 'ReparsePointSkipped' -and $_.Message -match 'Junction -> ' }).Count -ge 2)
    $expectedErrors = 2 + $(if ($fx.Features['InvalidTimestamp'] -eq 'created') { 1 } else { 0 })
    $expectedLinks = 2 + $(if ($fx.Features['Symlinks'] -eq 'created') { 1 } else { 0 })
    Assert-True 'Reparse' "Console separates skipped links ($expectedLinks) from scan errors ($expectedErrors)" `
        ($run.Output -match "Scan errors: $expectedErrors\b" -and $run.Output -match "Folder links not followed \(by design\): $expectedLinks\b")
    if ($fx.Features['Symlinks'] -eq 'created') {
        $fileLink = @($reports.Files | Where-Object { $_.FileName -eq 'file-link.bin' })
        Assert-True 'Reparse' 'File symbolic link counted once with the size of the link (0), target not followed' ($fileLink.Count -eq 1 -and $fileLink[0].SizeBytes -eq '0')
        Assert-True 'Reparse' 'File symbolic link reported as ReparsePointFile with its target' (@($errors | Where-Object { $_.ErrorType -eq 'ReparsePointFile' -and $_.Message -match 'SymbolicLink' }).Count -eq 1)
        Assert-True 'Reparse' 'Directory symbolic link listed, not followed' ($folderBy['Links\dir-link'].ScanStatus -eq 'ReparsePointSkipped')
    }
    else {
        Add-Result 'Reparse' 'File symbolic link' 'SKIP' 'cannot create symlinks without Developer Mode/admin'
        Add-Result 'Reparse' 'Directory symbolic link' 'SKIP' 'cannot create symlinks without Developer Mode/admin (same code path as junctions: ReparsePoint attribute)'
    }
    Add-Result 'Reparse' 'Volume mount point' 'SKIP' 'creating one needs admin; it uses the same reparse tag and code path as a junction'
    foreach ($k in @('CRLF', 'DoubleQuote')) {
        if ($fx.Features[$k] -ne 'created') { Add-Result 'Names' "File name containing $k" 'SKIP' $fx.Features[$k] }
    }

    # A4: real FILE reparse points (Windows App Execution Aliases), scanned read-only.
    $appAliases = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps'
    $aliasFiles = if ([IO.Directory]::Exists($appAliases)) { @((Find-ReparsePoints $appAliases) | Where-Object { $_ -is [IO.FileInfo] }) } else { @() }
    if ($aliasFiles.Count -gt 0) {
        $aliasOut = Join-Path $script:WorkRoot 'alias-out'
        $null = Invoke-Inventory @('-RootPath', $appAliases, '-OutputPath', $aliasOut, '-SkipExcel')
        $aliasErrors = @(Import-Csv -LiteralPath (Get-LatestReport $aliasOut 'ScanErrors_') -Encoding UTF8 | Where-Object { $_.ErrorType -eq 'ReparsePointFile' })
        $aliasRows = @(Import-Csv -LiteralPath (Get-LatestReport $aliasOut 'Files_') -Encoding UTF8 | Where-Object { $_.Attributes -match 'ReparsePoint' })
        $sizeMismatch = @($aliasRows | Where-Object { $row = $_; $real = $aliasFiles | Where-Object { $_.FullName -eq $row.FullPath }; -not $real -or [long]$row.SizeBytes -ne $real.Length })
        Assert-True 'Reparse' "Real file reparse points (App Execution Aliases): all $($aliasFiles.Count) counted with their listed size and reported as ReparsePointFile" `
            ($aliasErrors.Count -eq $aliasFiles.Count -and $aliasRows.Count -eq $aliasFiles.Count -and $sizeMismatch.Count -eq 0) "rows $($aliasRows.Count), notes $($aliasErrors.Count)"
    }
    else { Add-Result 'Reparse' 'Real file reparse points' 'SKIP' 'no App Execution Alias files on this machine' }

    # A complete scan says so and exits 0.
    $okRun = Invoke-Inventory @('-RootPath', (Join-Path $fx.Root 'Kpop\TWICE'), '-OutputPath', (Join-Path $script:WorkRoot 'complete-out'), '-SkipExcel')
    Assert-True 'Complete' 'Complete scan: exit 0 and "Scan complete."' ($okRun.ExitCode -eq 0 -and $okRun.Output -match 'Scan complete\.')
    $noExcel = Invoke-Inventory @('-RootPath', (Join-Path $fx.Root 'Kpop\TWICE'), '-OutputPath', (Join-Path $script:WorkRoot 'noexcel-out'))
    if (-not (Get-Module -ListAvailable -Name ImportExcel)) {
        Assert-True 'Excel' 'ImportExcel absent: workbook skipped, CSVs stated as complete, nothing installed' ($noExcel.Output -match 'ImportExcel module is not installed \(nothing was installed\)')
    }
    #endregion

    #region ---- Safety cases -------------------------------------------------------------------------------------
    $r = $fx.Root
    $case = {
        param([string] $Name, [string[]] $CaseArgs, [string] $Pattern, [string] $WorkDir = $script:WorkRoot)
        $res = Invoke-Inventory -Arguments $CaseArgs -WorkingDirectory $WorkDir
        Assert-True 'Safety' $Name ($res.Output -match $Pattern) ("exit $($res.ExitCode)")
        return $res
    }
    $o = Join-Path $r 'InsideOut'
    $null = & $case 'Output inside root is refused' @('-RootPath', $r, '-OutputPath', $o) 'is the same as, or inside'
    Assert-True 'Safety' '  ... and no output folder was created inside the root' (-not [IO.Directory]::Exists($o))
    $null = & $case 'Output equal to root (case + trailing slash variant) is refused' @('-RootPath', $r, '-OutputPath', ($r.ToUpper() + '\')) 'is the same as, or inside'
    $null = & $case 'Root that is a junction is refused' @('-RootPath', (Join-Path $r 'Loop'), '-OutputPath', (Join-Path $script:WorkRoot 'o3')) 'passes through a reparse point'
    $jn = Join-Path $script:WorkRoot 'jn-into-root'
    $null = & cmd.exe /c mklink /J "$jn" "$r\Empty" 2>&1
    $null = & $case 'Output through a junction into the root is refused' @('-RootPath', $r, '-OutputPath', (Join-Path $jn 'rep')) 'passes through a reparse point'
    Assert-True 'Safety' '  ... and nothing was created through the junction' (@([IO.Directory]::GetFileSystemEntries("$r\Empty")).Count -eq 0)

    $letter = [char[]](70..90) | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -Last 1
    $null = & subst.exe "$($letter):" "$r\Kpop"
    try {
        $aliasOut = Join-Path $r 'Kpop\aliasout'
        $res = & $case "SUBST alias ($($letter):) with output inside the real root is stopped by the tripwire" @('-RootPath', "$($letter):\", '-OutputPath', $aliasOut, '-SkipExcel') "own report file inside the scanned tree"
        Assert-True 'Safety' '  ... exit code is failure and the partial files are listed' ($res.ExitCode -ne 0 -and $res.Output -match 'INCOMPLETE report files')
    }
    finally { $null = & subst.exe "$($letter):" /D }

    $null = & $case 'Missing root is refused' @('-RootPath', (Join-Path $script:WorkRoot 'nope'), '-OutputPath', (Join-Path $script:WorkRoot 'o6')) 'does not exist'
    Assert-True 'Safety' '  ... and no output folder was created' (-not [IO.Directory]::Exists((Join-Path $script:WorkRoot 'o6')))
    $null = & $case 'Non-filesystem provider path is refused' @('-RootPath', 'HKCU:\Software', '-OutputPath', (Join-Path $script:WorkRoot 'o7')) 'not a file-system path'
    $null = & $case 'Device path prefix is refused' @('-RootPath', "\\?\$r", '-OutputPath', (Join-Path $script:WorkRoot 'o8')) 'device-path prefix'
    $weirdDir = @(Get-ChildItem -LiteralPath $r -Directory | Where-Object { $_.Name -like 'Weird*' })[0].FullName
    $o9 = Join-Path $script:WorkRoot 'o9 [x] $(whoami)'
    $null = & $case 'Root/output containing [ ] $ ` quotes and Unicode are treated literally' @('-RootPath', $weirdDir, '-OutputPath', $o9, '-SkipExcel') 'Files: 2'
    Assert-True 'Safety' '  ... output folder name kept literally' ([IO.Directory]::Exists($o9))
    $null = & $case 'Relative -RootPath resolves against the current location' @('-RootPath', '.\root\Kpop\TWICE', '-OutputPath', (Join-Path $script:WorkRoot 'o10'), '-SkipExcel') ([regex]::Escape("Root: $r\Kpop\TWICE")) (Join-Path $script:WorkRoot 'main')
    $o12 = Join-Path $script:WorkRoot 'o12'
    $null = & $case '-NoSort completes' @('-RootPath', $r, '-OutputPath', $o12, '-NoSort', '-SkipExcel') 'Files report \(discovery order\)'
    Assert-True 'Safety' '  ... and never creates a temporary file' (@([IO.Directory]::GetFiles($o12, '*.tmp')).Count -eq 0)
    $f13 = Join-Path $script:WorkRoot 'afile.txt'; [IO.File]::WriteAllText($f13, 'KEEP')
    $null = & $case 'Output path that is an existing file is refused' @('-RootPath', "$r\Kpop", '-OutputPath', $f13) 'is an existing file'
    Assert-True 'Safety' '  ... and the file is untouched' ([IO.File]::ReadAllText($f13) -eq 'KEEP')
    $null = & $case 'Trailing-dot alias of the root is refused' @('-RootPath', $r, '-OutputPath', ($r + '.\sneaky')) "(is the same as, or inside|ending in '\.')"
    Assert-True 'Safety' '  ... nothing created' (-not [IO.Directory]::Exists((Join-Path $r 'sneaky')))
    $null = & $case 'Alternate-data-stream syntax is refused' @('-RootPath', "$r\Kpop", '-OutputPath', ($script:WorkRoot + '\o15:stream')) "(contains ':'|not a valid path)"
    $null = & $case 'Device name NUL is refused' @('-RootPath', "$r\Kpop", '-OutputPath', ($script:WorkRoot + '\nul')) 'device name'
    $null = & $case 'Device name CON.txt (with extension) is refused' @('-RootPath', "$r\Kpop", '-OutputPath', ($script:WorkRoot + '\CON.txt\x')) 'device name'
    foreach ($sup in @(@('COM', 0xB9), @('COM', 0xB2), @('LPT', 0xB3))) {
        $name = $sup[0] + [char]$sup[1]
        $null = & $case "Superscript device name $name is refused" @('-RootPath', "$r\Kpop", '-OutputPath', ($script:WorkRoot + '\' + $name)) 'device name'
    }
    $null = & $case ('Superscript device name with extension (LPT' + [char]0xB9 + '.log) is refused') @('-RootPath', "$r\Kpop", '-OutputPath', ($script:WorkRoot + '\LPT' + [char]0xB9 + '.log')) 'device name'
    foreach ($allowedName in @('CONFIG', 'COM10', 'LPT', 'NULL', ('COM' + [char]0x0663))) {
        $null = & $case "Legitimate name '$allowedName' is allowed" @('-RootPath', "$r\Kpop\TWICE", '-OutputPath', ($script:WorkRoot + '\' + $allowedName), '-SkipExcel') 'Scan complete\.'
    }
    #endregion

    #region ---- Bulk fixture: cancellation, write failure, CSV no-clobber race -----------------------------------
    $bulk = Join-Path $script:WorkRoot 'bulk'
    New-BulkFixture -Root $bulk -Artists 20 -Albums 25 -Tracks 40   # 20,000 files
    $null = Get-TreeSnapshot -Root $bulk   # settle NTFS lazily-updated folder timestamps (first listing of a new tree)
    $bulkBefore = Get-TreeSnapshot -Root $bulk
    $bulkBeforeRecords = Get-TreeSnapshot -Root $bulk -TrueValues

    $cancel = & $script:Shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-Cancellation.ps1') `
        -ScriptUnderTest $script:ScriptUnderTest -Root $bulk -Out (Join-Path $script:WorkRoot 'cancel-out') -Phase Scan | Out-String | ConvertFrom-Json
    Assert-True 'Cancel' 'Ctrl+C during the scan stops the run' ($cancel.Triggered -and -not $cancel.CompletedBeforeStop -and $cancel.State -eq 'Stopped') "state $($cancel.State)"
    Assert-True 'Cancel' '  ... reports "did NOT complete" and lists the INCOMPLETE files' ((@($cancel.HostMessages) -join "`n") -match 'did NOT complete' -and (@($cancel.HostMessages) -join "`n") -match 'INCOMPLETE report files')
    Assert-True 'Cancel' '  ... no report file is left locked' (@($cancel.Locked).Count -eq 0) (@($cancel.Locked) -join ', ')
    Assert-True 'Cancel' '  ... no Folders report is produced' (@(@($cancel.Files) | Where-Object { $_ -like 'Folders_*' }).Count -eq 0)

    # Output write failure mid-run: once the scan is under way, deny "create files" on the output folder.
    $wfOut = Join-Path $script:WorkRoot 'writefail-out'
    $proc = Start-InventoryProcess -Arguments @('-RootPath', $bulk, '-OutputPath', $wfOut, '-SkipExcel') -LogPrefix (Join-Path $script:WorkRoot 'writefail')
    $tmp = Wait-ForFile -Folder $wfOut -Filter '*.unsorted.tmp'
    $null = & icacls.exe $wfOut /deny "$($env:USERNAME):(WD)"
    $proc.WaitForExit()
    $null = & icacls.exe $wfOut /remove:d $env:USERNAME
    $wfText = [IO.File]::ReadAllText((Join-Path $script:WorkRoot 'writefail.out.txt')) + [IO.File]::ReadAllText((Join-Path $script:WorkRoot 'writefail.err.txt'))
    Assert-True 'WriteFail' 'Output folder becomes unwritable mid-run: run fails (non-zero exit)' ($null -ne $tmp -and $proc.ExitCode -ne 0) "exit $($proc.ExitCode)"
    Assert-True 'WriteFail' '  ... says "did NOT complete", never "Scan complete."' ($wfText -match 'did NOT complete' -and $wfText -notmatch 'Scan complete\.')
    Assert-True 'WriteFail' '  ... no Folders report exists' (@([IO.Directory]::GetFiles($wfOut, 'Folders_*')).Count -eq 0)

    # Unwritable output folder from the start.
    $roOut = Join-Path $script:WorkRoot 'readonly-out'; [void][IO.Directory]::CreateDirectory($roOut)
    $null = & icacls.exe $roOut /deny "$($env:USERNAME):(WD)"
    $ro = Invoke-Inventory @('-RootPath', "$($fx.Root)\Kpop\TWICE", '-OutputPath', $roOut, '-SkipExcel')
    $null = & icacls.exe $roOut /remove:d $env:USERNAME
    Assert-True 'WriteFail' 'Unwritable output folder: run fails cleanly, creates nothing' ($ro.ExitCode -ne 0 -and @([IO.Directory]::GetFiles($roOut)).Count -eq 0 -and $ro.Output -notmatch 'Scan complete\.')

    # CSV no-clobber race: a file appears with the exact name the script is about to create.
    $raceOut = Join-Path $script:WorkRoot 'race-out'
    $proc = Start-InventoryProcess -Arguments @('-RootPath', $bulk, '-OutputPath', $raceOut, '-SkipExcel') -LogPrefix (Join-Path $script:WorkRoot 'race')
    $tmp = Wait-ForFile -Folder $raceOut -Filter '*.unsorted.tmp'
    $decoy = Join-Path $raceOut ('Folders_' + (Get-RunIdFromReport $tmp) + '.csv')
    [IO.File]::WriteAllText($decoy, 'DECOY - MUST NOT BE OVERWRITTEN')
    $proc.WaitForExit()
    $raceText = [IO.File]::ReadAllText((Join-Path $script:WorkRoot 'race.out.txt')) + [IO.File]::ReadAllText((Join-Path $script:WorkRoot 'race.err.txt'))
    Assert-True 'NoClobber' 'CSV name taken mid-run: existing file untouched, run fails instead of overwriting' `
        ([IO.File]::ReadAllText($decoy) -eq 'DECOY - MUST NOT BE OVERWRITTEN' -and $proc.ExitCode -ne 0 -and $raceText -match 'did NOT complete')

    Assert-True 'Tree' 'Bulk tree unchanged after cancellation / failure / race tests (listing view)' ($bulkBefore -eq (Get-TreeSnapshot -Root $bulk))
    Assert-True 'Tree' 'Bulk tree unchanged after cancellation / failure / race tests (per-item records)' ($bulkBeforeRecords -eq (Get-TreeSnapshot -Root $bulk -TrueValues))
    #endregion

    #region ---- Excel workbook tests ------------------------------------------------------------------------------
    if (-not $Excel) {
        Add-Result 'Excel' 'Workbook tests' 'SKIP' 'run with -Excel'
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $toolsModules 'ImportExcel'))) {
        Add-Result 'Excel' 'Workbook tests' 'SKIP' "ImportExcel not found in $toolsModules"
    }
    else {
        $savedModulePath = $env:PSModulePath
        $env:PSModulePath = $toolsModules + ';' + $env:PSModulePath   # child processes only; restored below
        try {
            Import-Module (Join-Path $toolsModules 'ImportExcel') -Force   # for inspecting the produced workbooks
            $readWorkbook = {
                param([string] $Path)
                $pkg = [OfficeOpenXml.ExcelPackage]::new([IO.FileInfo]::new($Path))
                try {
                    $info = [ordered]@{ Sheets = @($pkg.Workbook.Worksheets | ForEach-Object { $_.Name }); Formulas = 0; Hyperlinks = 0; Rows = @{}; GuardedTextOk = $true; NumericSize = $true }
                    foreach ($ws in $pkg.Workbook.Worksheets) {
                        $info.Rows[$ws.Name] = $ws.Dimension.End.Row
                        foreach ($cell in $ws.Cells[$ws.Dimension.Address]) {
                            if ($cell.Formula) { $info.Formulas++ }
                            if ($cell.Hyperlink) { $info.Hyperlinks++ }
                            if ($cell.Value -is [string] -and $cell.Value.StartsWith("'=") -and $cell.Formula) { $info.GuardedTextOk = $false }
                        }
                        if ($ws.Name -eq 'Files' -and $ws.Dimension.End.Row -ge 2 -and $ws.Cells[2, 7].Value -isnot [double]) { $info.NumericSize = $false }
                    }
                    return [pscustomobject]$info
                }
                finally { $pkg.Dispose() }
            }

            # End-to-end on the adversarial fixture (incomplete tree, formula-like names).
            $xOut = Join-Path $script:WorkRoot 'excel-out'
            $x = Invoke-Inventory @('-RootPath', $fx.Root, '-OutputPath', $xOut)
            $xlsx = @([IO.Directory]::GetFiles($xOut, '*.xlsx'))
            Assert-True 'Excel' 'Workbook created next to complete CSVs; no .partial left' ($xlsx.Count -eq 1 -and @([IO.Directory]::GetFiles($xOut, '*.partial')).Count -eq 0)
            if ($xlsx.Count -eq 1) {
                $wb = & $readWorkbook $xlsx[0]
                $xFiles = @(Import-Csv -LiteralPath (Get-LatestReport $xOut 'Files_') -Encoding UTF8).Count
                $xFolders = @(Import-Csv -LiteralPath (Get-LatestReport $xOut 'Folders_') -Encoding UTF8).Count
                Assert-True 'Excel' 'Workbook has Files and Folders sheets with every row of the same run''s CSVs' `
                    (($wb.Sheets -join ',') -eq 'Files,Folders' -and $wb.Rows['Files'] -eq ($xFiles + 1) -and $wb.Rows['Folders'] -eq ($xFolders + 1)) "files $xFiles, folders $xFolders"
                Assert-True 'Excel' 'No formula cells anywhere (formula-like names stay text)' ($wb.Formulas -eq 0 -and $wb.GuardedTextOk)
                Assert-True 'Excel' 'No hyperlink cells anywhere' ($wb.Hyperlinks -eq 0)
                Assert-True 'Excel' 'Size columns are real numbers' $wb.NumericSize
            }
            Assert-True 'Excel' 'Workbook does not turn an incomplete scan into a complete one (still exit 2)' ($x.ExitCode -eq 2 -and $x.Output -notmatch 'Scan complete\.')

            # Mechanism check with values the filesystem cannot produce (a URL and a raw '=' formula), same options as the script.
            $probePkg = [OfficeOpenXml.ExcelPackage]::new()
            try {
                $null = [pscustomobject]@{ Url = 'https://example.com/a'; Guarded = "'=1+1" } |
                    Export-Excel -ExcelPackage $probePkg -PassThru -NoHyperLinkConversion '*' -WorksheetName 'T' -NoNumberConversion @('Url', 'Guarded')
                $ws = $probePkg.Workbook.Worksheets['T']
                Assert-True 'Excel' 'Script options: a URL value stays plain text (no hyperlink)' (-not $ws.Cells[2, 1].Hyperlink -and $ws.Cells[2, 1].Value -eq 'https://example.com/a')
                Assert-True 'Excel' 'Script options: an apostrophe-guarded =formula stays text' (-not $ws.Cells[2, 2].Formula -and $ws.Cells[2, 2].Value -eq "'=1+1")
            }
            finally { $probePkg.Dispose() }

            # Workbook no-clobber race: the final .xlsx name appears while the scan is running.
            $wOut = Join-Path $script:WorkRoot 'xrace-out'
            $proc = Start-InventoryProcess -Arguments @('-RootPath', $bulk, '-OutputPath', $wOut) -LogPrefix (Join-Path $script:WorkRoot 'xrace')
            $tmp = Wait-ForFile -Folder $wOut -Filter '*.unsorted.tmp'
            $id = Get-RunIdFromReport $tmp
            $decoy = Join-Path $wOut "StorageInventory_$id.xlsx"
            [IO.File]::WriteAllText($decoy, 'DECOY WORKBOOK')
            $proc.WaitForExit()
            $t = [IO.File]::ReadAllText((Join-Path $script:WorkRoot 'xrace.out.txt'))
            Assert-True 'Excel' 'Existing workbook with the same name is never overwritten' ([IO.File]::ReadAllText($decoy) -eq 'DECOY WORKBOOK')
            Assert-True 'Excel' '  ... the unfinished .partial is identified as such, CSVs still complete, exit 0' `
                ($t -match 'Not created' -and $t -match [regex]::Escape("StorageInventory_$id.xlsx.partial") -and $t -match 'Scan complete\.' -and $proc.ExitCode -eq 0) "exit $($proc.ExitCode)"

            # Same race on the .partial name itself.
            $pOut = Join-Path $script:WorkRoot 'prace-out'
            $proc = Start-InventoryProcess -Arguments @('-RootPath', $bulk, '-OutputPath', $pOut) -LogPrefix (Join-Path $script:WorkRoot 'prace')
            $tmp = Wait-ForFile -Folder $pOut -Filter '*.unsorted.tmp'
            $decoy = Join-Path $pOut ('StorageInventory_' + (Get-RunIdFromReport $tmp) + '.xlsx.partial')
            [IO.File]::WriteAllText($decoy, 'DECOY PARTIAL')
            $proc.WaitForExit()
            $t = [IO.File]::ReadAllText((Join-Path $script:WorkRoot 'prace.out.txt'))
            Assert-True 'Excel' 'Existing .partial with the same name is never overwritten, and not claimed as ours' `
                ([IO.File]::ReadAllText($decoy) -eq 'DECOY PARTIAL' -and $t -match 'Not created' -and $t -notmatch 'was left at' -and @([IO.Directory]::GetFiles($pOut, '*.xlsx')).Count -eq 0)

            # A2: the Files worksheet succeeds, then the Folders worksheet fails (its CSV is locked by another process).
            $lOut = Join-Path $script:WorkRoot 'xlock-out'
            $proc = Start-InventoryProcess -Arguments @('-RootPath', $bulk, '-OutputPath', $lOut) -LogPrefix (Join-Path $script:WorkRoot 'xlock')
            $script:heldLock = $null
            $folders = Wait-ForFile -Folder $lOut -Filter 'Folders_*.csv' -Until {
                param($p) try { $script:heldLock = [IO.File]::Open($p, 'Open', 'Read', 'None'); $true } catch { $false }
            }
            $proc.WaitForExit()
            if ($script:heldLock) { $script:heldLock.Dispose() }
            $t = [IO.File]::ReadAllText((Join-Path $script:WorkRoot 'xlock.out.txt'))
            Assert-True 'Excel' 'Folders worksheet fails after Files worksheet: no workbook and no partial file left, status says not created' `
                ($null -ne $folders -and @([IO.Directory]::GetFiles($lOut, '*.xlsx*')).Count -eq 0 -and $t -match 'Not created \(Excel export failed' -and $t -notmatch 'was left at')
            Assert-True 'Excel' '  ... the CSV result is unaffected (exit 0, "Scan complete.")' ($proc.ExitCode -eq 0 -and $t -match 'Scan complete\.')

            # Ctrl+C during the workbook step.
            $cx = & $script:Shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-Cancellation.ps1') `
                -ScriptUnderTest $script:ScriptUnderTest -Root $bulk -Out (Join-Path $script:WorkRoot 'xcancel-out') -Phase Excel | Out-String | ConvertFrom-Json
            $msgs = @($cx.HostMessages) -join "`n"
            Assert-True 'Excel' 'Ctrl+C during the workbook step: says the CSV reports were completed, no file locked, no final .xlsx' `
                ($cx.Triggered -and -not $cx.CompletedBeforeStop -and $msgs -match 'CSV reports were completed' -and @($cx.Locked).Count -eq 0 -and @(@($cx.Files) | Where-Object { $_ -like '*.xlsx' }).Count -eq 0) "state $($cx.State)"
        }
        finally {
            $env:PSModulePath = $savedModulePath
        }
    }
    #endregion
}
finally {
    Write-Host ''
    $summary = $script:Results | Group-Object Outcome | ForEach-Object { '{0} {1}' -f $_.Count, $_.Name }
    Write-Host ("RESULT ($shellVersion): " + ($summary -join ', '))
    $csv = Join-Path ([IO.Path]::GetDirectoryName($script:WorkRoot)) ("results_" + [IO.Path]::GetFileName($script:WorkRoot) + '.csv')
    $script:Results | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
    Write-Host "Results saved to $csv"
    if (-not $KeepArtifacts) { Remove-Fixture -Base $script:WorkRoot }
}
if (@($script:Results | Where-Object { $_.Outcome -eq 'FAIL' }).Count -gt 0) { exit 1 }
