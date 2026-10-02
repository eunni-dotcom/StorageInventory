<#
.SYNOPSIS
    A-20 / TEST-R3 / BLD-14: the native files of the publish output are exactly v1's WPF natives plus e_sqlite3.dll, and the
    e_sqlite3.dll the single-file host extracts is byte-identical to the one in the SQLitePCLRaw.lib.e_sqlite3 package.
.DESCRIPTION
    A single-file publish bundles its native files and the .NET host extracts them to %TEMP%\.net\<app>\<hash>\ when the exe starts
    (docs/release.md). This starts the published exe, waits for the extraction, kills it and checks that directory: its file set,
    the size and SHA-256 of e_sqlite3.dll (the package's runtimes\win-x64\native\e_sqlite3.dll: 1,978,880 bytes), and the app's
    refusal to extract anything else. Nothing is written outside %TEMP%\.net, which the .NET host owns.
    The v1 set is the five WPF natives: D3DCompiler_47_cor3.dll, PenImc_cor3.dll, PresentationNative_cor3.dll,
    vcruntime140_cor3.dll and wpfgfx_cor3.dll.
.PARAMETER Exe       The published StorageInventory.exe (default: dist\StorageInventory.exe).
.PARAMETER Package   The restored e_sqlite3.dll to compare with (default: the repo-local package cache).
#>
param(
    [string] $Exe = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'dist\StorageInventory.exe'),
    [string] $Package = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'packages\sqlitepclraw.lib.e_sqlite3\2.1.12\runtimes\win-x64\native\e_sqlite3.dll')
)
$ErrorActionPreference = 'Stop'
$expected = @('D3DCompiler_47_cor3.dll', 'PenImc_cor3.dll', 'PresentationNative_cor3.dll', 'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll', 'e_sqlite3.dll') | Sort-Object
$pinnedHash = 'B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E'
$pinnedSize = 1978880
if (-not (Test-Path -LiteralPath $Exe)) { throw "The published exe is missing: $Exe" }
if (-not (Test-Path -LiteralPath $Package)) { throw "The package's e_sqlite3.dll is missing: $Package" }

$packageHash = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash
if ($packageHash -ne $pinnedHash -or (Get-Item -LiteralPath $Package).Length -ne $pinnedSize) { throw "The package's e_sqlite3.dll is not the pinned one ($packageHash)." }

$extractionRoot = Join-Path $env:TEMP ('.net\' + [IO.Path]::GetFileNameWithoutExtension($Exe))
$before = @{}
if (Test-Path -LiteralPath $extractionRoot) { Get-ChildItem -LiteralPath $extractionRoot -Directory | ForEach-Object { $before[$_.FullName] = $true } }

# Run it from a fresh copy outside the repository, as the smoke test does, so an extraction directory is created for THIS file
$copyDir = Join-Path ([IO.Path]::GetTempPath()) ('si-nativeset-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $copyDir | Out-Null
$copy = Join-Path $copyDir 'StorageInventory.exe'
Copy-Item -LiteralPath $Exe -Destination $copy
$process = $null
try {
    $process = Start-Process -FilePath $copy -PassThru
    $directory = $null
    for ($i = 0; $i -lt 300 -and -not $directory; $i++) {
        Start-Sleep -Milliseconds 100
        if (Test-Path -LiteralPath $extractionRoot) {
            $directory = Get-ChildItem -LiteralPath $extractionRoot -Directory | Where-Object { -not $before.ContainsKey($_.FullName) -and (Get-ChildItem -LiteralPath $_.FullName -File).Count -ge $expected.Count } | Select-Object -First 1
        }
    }
    if (-not $directory) { throw "No extraction directory with the expected files appeared under $extractionRoot." }
    Start-Sleep -Milliseconds 500
    $found = (Get-ChildItem -LiteralPath $directory.FullName -File | ForEach-Object { $_.Name }) | Sort-Object
    "extraction directory: $($directory.FullName)"
    "extracted files: $($found -join ', ')"
    if (($found -join '|') -ne ($expected -join '|')) { throw "The extracted native files are [$($found -join ', ')], expected exactly [$($expected -join ', ')]." }
    $extracted = Join-Path $directory.FullName 'e_sqlite3.dll'
    $hash = (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash
    "e_sqlite3.dll: $((Get-Item -LiteralPath $extracted).Length) bytes, SHA-256 $hash"
    if ($hash -ne $packageHash) { throw "The extracted e_sqlite3.dll ($hash) differs from the package's ($packageHash)." }
    'PASS: the native file set is the five WPF natives plus e_sqlite3.dll, byte-identical to the package.'
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    Start-Sleep -Milliseconds 300
    Remove-Item -LiteralPath $copyDir -Recurse -Force -ErrorAction SilentlyContinue
}
