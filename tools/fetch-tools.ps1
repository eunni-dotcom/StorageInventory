<#
.SYNOPSIS
    Downloads the repo-local toolchain into tools\ (git-ignored). Nothing is installed machine-wide.
.DESCRIPTION
    - .NET SDK 10.0.401 into tools\dotnet (official zip, pinned SHA-512). Needed to build.
    - PowerShell 7.6.6 portable into tools\pwsh (official zip, pinned SHA-256). Used by the tests: fixture building
      and the PowerShell reference implementation used as a parity oracle.
    - ImportExcel 7.8.10 into tools\psmodules (TESTS ONLY: an independent reader for the optional workbook). Saved with
      PowerShell 7's Save-PSResource, never installed into a module path. It is not part of the application.
    Every download is verified against a pinned hash before it is unpacked. Existing tool folders are not touched:
    delete one first to fetch it again.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$t = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-VerifiedZip([string]$Url, [string]$Algorithm, [string]$ExpectedHash, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { "$Destination already exists; skipped"; return }
    $zip = Join-Path $t ([IO.Path]::GetRandomFileName() + '.zip')
    try {
        Invoke-WebRequest $Url -OutFile $zip -UseBasicParsing
        $actual = (Get-FileHash -LiteralPath $zip -Algorithm $Algorithm).Hash
        if ($actual -ne $ExpectedHash.ToUpperInvariant()) { throw "HASH MISMATCH for $Url`: $actual" }
        "$Algorithm OK: $Url"
        Expand-Archive -LiteralPath $zip -DestinationPath $Destination
    }
    finally {
        if (Test-Path -LiteralPath $zip) { [IO.File]::Delete($zip) }
    }
}

# .NET SDK 10.0.401 (SHA-512 from Microsoft's release metadata)
Get-VerifiedZip 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip' SHA512 `
    '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430' `
    (Join-Path $t 'dotnet')

# PowerShell 7.6.6 portable (SHA-256 from the GitHub release's asset digest)
Get-VerifiedZip 'https://github.com/PowerShell/PowerShell/releases/download/v7.6.6/PowerShell-7.6.6-win-x64.zip' SHA256 `
    '02fe458be20493fbdf43f61ea20610b811ee6c738ab1676c61b9cfcd1a33c860' `
    (Join-Path $t 'pwsh')

# ImportExcel 7.8.10, for tests only. PowerShell 7's PSResourceGet is used so that Windows PowerShell's PowerShellGet
# never tries to bootstrap a NuGet provider on this machine.
$mods = Join-Path $t 'psmodules'
if (Test-Path -LiteralPath (Join-Path $mods 'ImportExcel')) { "$mods\ImportExcel already exists; skipped" }
else {
    [void][IO.Directory]::CreateDirectory($mods)
    $env:SI_FETCH_MODULES = $mods   # passed as data, so any character in the repo path is safe
    try {
        & (Join-Path $t 'pwsh\pwsh.exe') -NoProfile -NonInteractive -Command `
            'Save-PSResource -Name ImportExcel -Version 7.8.10 -Repository PSGallery -TrustRepository -Path $env:SI_FETCH_MODULES'
        if ($LASTEXITCODE -ne 0) { throw 'Saving ImportExcel failed' }
    }
    finally { Remove-Item Env:\SI_FETCH_MODULES }
    'ImportExcel saved: ' + ((Get-ChildItem (Join-Path $mods 'ImportExcel')).Name -join ',')
}
'DONE'
