<#
.SYNOPSIS
    Builds, tests and publishes StorageInventory with the repo-local .NET SDK in tools\dotnet.
.DESCRIPTION
    The .NET CLI and NuGet are pointed at folders inside the repo, so nothing is written to your user profile
    (no ~\.dotnet, no %APPDATA%\NuGet, no user-wide package cache). CLI telemetry is switched off and no MSBuild
    server or node processes are left running.

    The environment variables are set only for the duration of this script and are restored afterwards, even on
    failure, so running .\build.ps1 from an interactive window does not change that window's environment.
.PARAMETER Target
    Build    - build the whole solution
    Test     - build, then the unit tests and the integration tests (including PowerShell parity)
    Bench    - Release build, then the C# benchmarks
    Publish  - clean, then a self-contained single-file win-x64 Release build into dist\ (prints its SHA-256)
    Dotnet   - run any dotnet command in the repo-local environment, e.g. -Target Dotnet -DotnetArgs sln,list
.PARAMETER Configuration
    Debug (default) or Release, for Build and Test.
.EXAMPLE
    .\build.ps1 -Target Test -Configuration Release
#>
param(
    [ValidateSet('Build', 'Test', 'Publish', 'Bench', 'Dotnet')] [string] $Target = 'Build',
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [string[]] $TestArgs = @(),
    [string[]] $DotnetArgs = @()
)
$ErrorActionPreference = 'Stop'
$repo   = $PSScriptRoot
$dotnet = Join-Path $repo 'tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'The .NET SDK is missing. Run tools\fetch-tools.ps1 first.' }

$state = Join-Path $repo 'tools\dotnet-state'
$buildEnvironment = [ordered]@{
    DOTNET_CLI_HOME                      = Join-Path $state 'home'      # instead of %USERPROFILE%\.dotnet
    APPDATA                              = Join-Path $state 'appdata'   # NuGet's user config lands here, not in your profile
    NUGET_PACKAGES                       = Join-Path $repo 'packages'
    NUGET_HTTP_CACHE_PATH                = Join-Path $state 'nuget-http'
    NUGET_PLUGINS_CACHE_PATH             = Join-Path $state 'nuget-plugins'
    DOTNET_ROOT                          = Join-Path $repo 'tools\dotnet'
    DOTNET_CLI_TELEMETRY_OPTOUT          = '1'
    DOTNET_NOLOGO                        = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE    = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH      = 'false'
    DOTNET_GENERATE_ASPNET_CERTIFICATE   = 'false'
    DOTNET_MULTILEVEL_LOOKUP             = '0'
    MSBUILDDISABLENODEREUSE              = '1'
    DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
}

$saved = @{}
foreach ($name in $buildEnvironment.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $buildEnvironment[$name], 'Process')
}
try {
    foreach ($d in @($buildEnvironment.DOTNET_CLI_HOME, $buildEnvironment.APPDATA)) { [void][IO.Directory]::CreateDirectory($d) }
    $sln = Join-Path $repo 'StorageInventory.sln'

    function Invoke-Dotnet {
        & $dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE" }
    }

    switch ($Target) {
        'Dotnet' { Invoke-Dotnet @DotnetArgs }
        'Build' { Invoke-Dotnet build $sln -c $Configuration '-nodeReuse:false' }
        'Test' {
            Invoke-Dotnet build $sln -c $Configuration '-nodeReuse:false'
            foreach ($testProject in @('StorageInventory.Core.Tests', 'StorageInventory.IntegrationTests')) {
                $dir = Join-Path $repo "tests\$testProject"
                if (Test-Path -LiteralPath $dir) { Invoke-Dotnet run --no-build -c $Configuration --project $dir -- @TestArgs }
            }
        }
        'Bench' {
            Invoke-Dotnet build $sln -c Release '-nodeReuse:false'
            Invoke-Dotnet run --no-build -c Release --project (Join-Path $repo 'tests\StorageInventory.IntegrationTests') -- --benchmark @TestArgs
        }
        'Publish' {
            $dist = Join-Path $repo 'dist'
            $app = Join-Path $repo 'src\StorageInventory.App\StorageInventory.App.csproj'
            # Clean first: a publish from clean intermediate output is byte-for-byte reproducible, whereas reusing a
            # library compiled by an earlier solution build gives a different (equally valid) binary.
            Invoke-Dotnet clean $sln -c Release '-nodeReuse:false'
            Invoke-Dotnet clean $app -c Release -r win-x64 '-nodeReuse:false'
            Invoke-Dotnet publish (Join-Path $repo 'src\StorageInventory.App\StorageInventory.App.csproj') -c Release -r win-x64 `
                --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=none' `
                '-p:EnableSingleFileAnalyzer=false' -o $dist '-nodeReuse:false'   # analyzer would need the extra Microsoft.NET.ILLink.Tasks package
            Get-ChildItem -LiteralPath $dist | Select-Object Name, Length | Format-Table -AutoSize
            'SHA-256 ' + (Get-FileHash -LiteralPath (Join-Path $dist 'StorageInventory.exe') -Algorithm SHA256).Hash
        }
    }
}
finally {
    foreach ($name in $buildEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
