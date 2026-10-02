<#
.SYNOPSIS
    v1.1 C4: Q-11, Q-22 and the loading half of TEST-R2. Where does a single-file StorageInventory load SQLite from, and is anything
    planted beside the executable ignored?
.DESCRIPTION
    The App itself does not call the Library until C5, so this publishes StorageInventory.Library.Tests (which has a
    "--native-probe" mode that opens a Library and prints the loaded SQLite module and assemblies) with EXACTLY the App's publish
    settings: self-contained, single file, IncludeNativeLibrariesForSelfExtract. The loading mechanism (the .NET host's extraction
    to %TEMP%\.net\<exe name>\<hash>\ and its probing) is the same. The experiments, each on a fresh copy of the exe outside the repo:

      Q-11  1. run it: e_sqlite3.dll is loaded from the extraction directory, and its SHA-256 equals the package's native DLL;
            2. plant a dummy e_sqlite3.dll (text) and then a byte-identical COPY of the real one beside the exe: neither is loaded
               (the module path is still the extraction directory's) and the engine still reports SQLite 3.53.3;
            3. delete the extracted DLL while the app is closed and run again: the host re-extracts it, same hash.
      Q-22  4. plant a managed decoy named SQLitePCLRaw.batteries_v2.dll (which writes a marker file if it is ever initialised)
               beside the exe: it is not loaded;
            5. the control: the same decoy beside a NON-single-file build of the probe IS found by name, so the experiment can
               tell "loaded" from "ignored".

    Writes only under -Work and %TEMP%\.net\<probe name>, which the .NET host owns. Exit code 0 only when every check passes.
.PARAMETER Work       Scratch folder (default: a new folder under %TEMP%).
.PARAMETER KeepWork   Leave the scratch folder in place.
#>
param(
    [string] $Work = (Join-Path ([IO.Path]::GetTempPath()) ('si-native-' + [guid]::NewGuid().ToString('N').Substring(0, 8))),
    [switch] $KeepWork
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$build = Join-Path $repo 'build.ps1'
$pinnedHash = 'B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E'
$package = Join-Path $repo 'packages\sqlitepclraw.lib.e_sqlite3\2.1.12\runtimes\win-x64\native\e_sqlite3.dll'
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $pinnedHash) { throw "The package's e_sqlite3.dll is not the pinned one." }

$results = New-Object System.Collections.Generic.List[string]
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    $line = "$(if ($ok) { 'PASS' } else { 'FAIL' })  $name$(if ($detail) { ' -- ' + $detail })"
    $results.Add($line)
    Write-Host $line
}
function Run-Probe([string] $exe, [hashtable] $environment = @{}) {
    $saved = @{}
    foreach ($k in $environment.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $environment[$k]) }
    try {
        $scratch = Join-Path $Work ('scratch-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        $output = & $exe --native-probe $scratch 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = @($output) }
    }
    finally { foreach ($k in $environment.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
}
function Field($result, [string] $prefix) { @($result.Lines | Where-Object { $_.StartsWith($prefix) } | ForEach-Object { $_.Substring($prefix.Length) }) }

New-Item -ItemType Directory -Path $Work | Out-Null
try {
    # ---- build the probe the way the App is published, and the decoy
    $publish = Join-Path $Work 'publish'
    & $build -Target Dotnet -DotnetArgs @('publish', (Join-Path $repo 'tests\StorageInventory.Library.Tests\StorageInventory.Library.Tests.csproj'), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-p:EnableSingleFileAnalyzer=false', '-o', $publish, '-nodeReuse:false') | Out-Null
    # the control uses the plain (framework-dependent) build of the probe; a publish step before this script may have cleaned it
    & $build -Target Dotnet -DotnetArgs @('build', (Join-Path $repo 'tests\StorageInventory.Library.Tests\StorageInventory.Library.Tests.csproj'), '-c', 'Release', '-nodeReuse:false') | Out-Null
    $decoyOut = Join-Path $Work 'decoy'
    & $build -Target Dotnet -DotnetArgs @('build', (Join-Path $repo 'tests\smoke\PlantedBatteries\PlantedBatteries.csproj'), '-c', 'Release', '-o', $decoyOut, '-nodeReuse:false') | Out-Null
    $decoy = Join-Path $decoyOut 'SQLitePCLRaw.batteries_v2.dll'
    Check 'the decoy assembly was built' (Test-Path -LiteralPath $decoy) | Out-Null

    $name = 'SiProbe' + [guid]::NewGuid().ToString('N').Substring(0, 4)
    $extractionRoot = Join-Path $env:TEMP ".net\$name"
    $runDir = Join-Path $Work 'run'
    New-Item -ItemType Directory -Path $runDir | Out-Null
    $exe = Join-Path $runDir "$name.exe"
    Copy-Item -LiteralPath (Join-Path $publish 'StorageInventory.Library.Tests.exe') -Destination $exe
    "publish: $((Get-Item -LiteralPath $exe).Length) bytes; files beside the exe: $((Get-ChildItem -LiteralPath $runDir -File | ForEach-Object Name) -join ', ')"

    # ---- Q-11 (1)
    $first = Run-Probe $exe
    $first.Lines | ForEach-Object { "  | $_" }
    Check 'the probe opened a Library' ($first.ExitCode -eq 0 -and (Field $first 'state=') -match '^Available') | Out-Null
    $module = Field $first 'module=' | Where-Object { $_ -like '*e_sqlite3.dll' } | Select-Object -First 1
    Check 'e_sqlite3.dll is loaded from the single-file extraction directory under %TEMP%\.net\<exe>\' ($module -and $module.StartsWith($extractionRoot + '\', [StringComparison]::OrdinalIgnoreCase)) $module | Out-Null
    $extractedHash = if ($module) { (Get-FileHash -LiteralPath $module -Algorithm SHA256).Hash } else { '' }
    Check 'its SHA-256 equals the package''s runtimes\win-x64\native\e_sqlite3.dll' ($extractedHash -eq $pinnedHash) $extractedHash | Out-Null
    Check 'the engine is SQLite 3.53.3 with the pinned source id' ((Field $first 'engine=') -eq '3.53.3 2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62') | Out-Null
    Check 'the managed SQLite assemblies come from the bundle, not from disk' (@(Field $first 'assembly=' | Where-Object { $_ -notmatch 'location=<bundle>' }).Count -eq 0) | Out-Null

    # ---- Q-11 (2): a dummy and then a real copy beside the exe
    $planted = Join-Path $runDir 'e_sqlite3.dll'
    Set-Content -LiteralPath $planted -Value 'this is not a DLL: a marker planted beside the executable' -Encoding ascii
    $dummyBytes = [IO.File]::ReadAllBytes($planted)
    $second = Run-Probe $exe
    $moduleDummy = Field $second 'module=' | Where-Object { $_ -like '*e_sqlite3.dll' } | Select-Object -First 1
    Check 'with a dummy e_sqlite3.dll beside the exe the probe still opens its Library' ($second.ExitCode -eq 0) | Out-Null
    Check 'the dummy beside the exe is not loaded (the module is still the extracted one)' ($moduleDummy -and $moduleDummy.StartsWith($extractionRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and $moduleDummy -ne $planted) $moduleDummy | Out-Null
    Check 'the dummy was not touched' ([Convert]::ToBase64String([IO.File]::ReadAllBytes($planted)) -eq [Convert]::ToBase64String($dummyBytes)) | Out-Null
    Copy-Item -LiteralPath $package -Destination $planted -Force
    $third = Run-Probe $exe
    $moduleCopy = Field $third 'module=' | Where-Object { $_ -like '*e_sqlite3.dll' } | Select-Object -First 1
    Check 'a byte-identical COPY of the real DLL beside the exe is not loaded either (the module path is the extraction directory)' ($third.ExitCode -eq 0 -and $moduleCopy -and $moduleCopy.StartsWith($extractionRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and $moduleCopy -ne $planted) $moduleCopy | Out-Null
    Remove-Item -LiteralPath $planted -Force

    # ---- Q-11 (3): the host re-extracts a deleted file
    if ($module) {
        Remove-Item -LiteralPath $module -Force
        Check 'the extracted DLL is gone while the app is closed' (-not (Test-Path -LiteralPath $module)) | Out-Null
        $fourth = Run-Probe $exe
        $again = Field $fourth 'module=' | Where-Object { $_ -like '*e_sqlite3.dll' } | Select-Object -First 1
        Check 'the host re-extracted it and the app loaded the re-extracted file' ($fourth.ExitCode -eq 0 -and (Test-Path -LiteralPath $module) -and $again -and $again.StartsWith($extractionRoot + '\', [StringComparison]::OrdinalIgnoreCase)) $again | Out-Null
        Check 'the re-extracted DLL has the pinned hash' ((Get-FileHash -LiteralPath $module -Algorithm SHA256).Hash -eq $pinnedHash) | Out-Null
    }

    # ---- Q-22: the managed decoy
    $marker = Join-Path $Work 'decoy-was-initialised.txt'
    Copy-Item -LiteralPath $decoy -Destination (Join-Path $runDir 'SQLitePCLRaw.batteries_v2.dll')
    $fifth = Run-Probe $exe @{ SI_PLANT_MARKER = $marker }
    Check 'with SQLitePCLRaw.batteries_v2.dll planted beside the single-file exe the probe still opens its Library' ($fifth.ExitCode -eq 0) | Out-Null
    Check 'the planted managed assembly is not loaded (no assembly of that name, no module, no marker)' (-not (Test-Path -LiteralPath $marker) -and @($fifth.Lines | Where-Object { $_ -like 'assembly=SQLitePCLRaw.batteries_v2*' -or $_ -like 'module=*batteries_v2*' }).Count -eq 0) | Out-Null
    Remove-Item -LiteralPath (Join-Path $runDir 'SQLitePCLRaw.batteries_v2.dll') -Force

    # ---- the controls. The product never asks for the decoy (A-25). These ask on the experiment's behalf so that "ignored" can be
    #      told from "nothing asked" and from "the decoy is a dud": (a) a by-name request, which the runtime answers from the
    #      application's dependency list only (so an undeclared assembly beside a NORMAL build is not found either), and (b) the same
    #      decoy loaded by explicit path and initialised, which must write the marker.
    Copy-Item -LiteralPath $decoy -Destination (Join-Path $runDir 'SQLitePCLRaw.batteries_v2.dll')
    $single = Run-Probe $exe @{ SI_PROBE_RESOLVE_DECOY = '1' }
    $singleResolve = [string]((Field $single 'decoy-resolve=') | Select-Object -First 1)
    Check 'a by-name request for the planted assembly from the single-file exe is not satisfied' ($single.ExitCode -eq 0 -and $singleResolve -like 'notfound*') $singleResolve | Out-Null
    Remove-Item -LiteralPath (Join-Path $runDir 'SQLitePCLRaw.batteries_v2.dll') -Force
    $control = Join-Path $Work 'control'
    Copy-Item -LiteralPath (Join-Path $repo 'tests\StorageInventory.Library.Tests\bin\Release\net10.0-windows') -Destination $control -Recurse
    Copy-Item -LiteralPath $decoy -Destination (Join-Path $control 'SQLitePCLRaw.batteries_v2.dll')
    $controlEnv = @{ DOTNET_ROOT = (Join-Path $repo 'tools\dotnet') }
    $byName = Run-Probe (Join-Path $control 'StorageInventory.Library.Tests.exe') ($controlEnv + @{ SI_PROBE_RESOLVE_DECOY = '1' })
    $byNameResolve = [string]((Field $byName 'decoy-resolve=') | Select-Object -First 1)
    Check 'a normal (non-single-file) build does not resolve an undeclared assembly beside it by name either' ($byNameResolve -like 'notfound*') $byNameResolve | Out-Null
    if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
    $loaded = Run-Probe (Join-Path $control 'StorageInventory.Library.Tests.exe') ($controlEnv + @{ SI_PROBE_LOAD_DECOY = (Join-Path $control 'SQLitePCLRaw.batteries_v2.dll'); SI_PLANT_MARKER = $marker })
    Check 'CONTROL: the decoy, loaded by explicit path and initialised, writes its marker (it is a working decoy)' ((Test-Path -LiteralPath $marker) -and @(Field $loaded 'marker-after=') -contains 'PLANTED-ASSEMBLY-WAS-INITIALISED') | Out-Null
}
finally {
    if (Test-Path -LiteralPath (Join-Path $env:TEMP ".net\$name")) { Remove-Item -LiteralPath (Join-Path $env:TEMP ".net\$name") -Recurse -Force -ErrorAction SilentlyContinue }
    if (-not $KeepWork) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
}
$failed = @($results | Where-Object { $_ -like 'FAIL*' }).Count
"$($results.Count - $failed) of $($results.Count) checks passed"
exit ([int]($failed -ne 0))
