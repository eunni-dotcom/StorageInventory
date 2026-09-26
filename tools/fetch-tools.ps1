$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$t = Split-Path -Parent $MyInvocation.MyCommand.Path

# .NET SDK 10.0.401 (official, SHA-512 from release metadata)
$sdkUrl  = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip'
$sdkHash = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
$sdkZip  = Join-Path $t 'dotnet-sdk.zip'
Invoke-WebRequest $sdkUrl -OutFile $sdkZip -UseBasicParsing
$h = (Get-FileHash $sdkZip -Algorithm SHA512).Hash
if ($h -ne $sdkHash) { throw "SDK HASH MISMATCH $h" }
'SDK hash OK'
Expand-Archive $sdkZip (Join-Path $t 'dotnet') -Force
[IO.File]::Delete($sdkZip)

# PowerShell 7.6.6 portable (official GitHub release, SHA-256 from release metadata)
$rel   = Invoke-RestMethod 'https://api.github.com/repos/PowerShell/PowerShell/releases/tags/v7.6.6'
$asset = $rel.assets | Where-Object { $_.name -eq 'PowerShell-7.6.6-win-x64.zip' }
$psZip = Join-Path $t 'pwsh.zip'
Invoke-WebRequest $asset.browser_download_url -OutFile $psZip -UseBasicParsing
$h = (Get-FileHash $psZip -Algorithm SHA256).Hash
$expected = $null
if ($asset.PSObject.Properties['digest'] -and $asset.digest) { $expected = $asset.digest.Replace('sha256:', '') }
if (-not $expected) {
    $hashAsset = $rel.assets | Where-Object { $_.name -eq 'hashes.sha256' }
    $content = (Invoke-WebRequest $hashAsset.browser_download_url -UseBasicParsing).Content
    foreach ($line in $content.Split("`n")) { if ($line.Contains('PowerShell-7.6.6-win-x64.zip')) { $expected = $line.Trim().Split(' ')[0] } }
}
if ($h -ne $expected.ToUpper()) { throw "PWSH HASH MISMATCH $h vs $expected" }
'PowerShell hash OK'
Expand-Archive $psZip (Join-Path $t 'pwsh') -Force
[IO.File]::Delete($psZip)

# ImportExcel for TESTS ONLY - saved under tools\, never into PSModulePath
$mods = Join-Path $t 'psmodules'
[void][IO.Directory]::CreateDirectory($mods)
Save-Module -Name ImportExcel -Path $mods -Repository PSGallery -Force
'ImportExcel saved: ' + ((Get-ChildItem (Join-Path $mods 'ImportExcel')).Name -join ',')
'DONE'
