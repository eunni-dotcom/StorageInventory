<#
.SYNOPSIS
    Creates disposable test media for the v1.1 C3 identity experiments (Q-02, Q-13, Q-19; TEST-I1, I2, I4, I6) and removes it again.
.DESCRIPTION
    Needs an ELEVATED PowerShell (attaching a VHDX and creating an SMB share need administrator rights; the product never does,
    INV-04). Everything it makes lives under -Root and is torn down by -Remove (also run on any failure while provisioning):

      two NTFS volumes, a FAT32 volume, an exFAT volume and a ReFS volume, each on its own small expandable VHDX
      a UDF volume (an ISO image built with IMAPI2FS and mounted)
      two SMB shares served by this machine, one of them on the FAT32 volume, each mapped to a drive letter

    A media type that cannot be made here (for example ReFS on a Windows edition without it) is reported with the reason and left
    out of the manifest: an unavailable type is a result, not a failure. The manifest is a JSON file mapping a name to its root path.

    Safety: -Root must not exist, or must be empty, or must be a folder this script made earlier (it holds a marker file); anything
    else is refused, and -Remove deletes -Root only when the marker is there, so a wrong path cannot be wiped. The SMB shares are
    open to the CURRENT USER only, not to everyone, and exist only while the experiments run.
.EXAMPLE
    .\Provision-IdentityMedia.ps1 -Manifest $env:TEMP\identity-media.json
    ...run the experiments...
    .\Provision-IdentityMedia.ps1 -Remove -Manifest $env:TEMP\identity-media.json
#>
param(
    [string] $Root = (Join-Path $env:TEMP 'SiIdentityMedia'),
    [string] $Manifest = (Join-Path $env:TEMP 'identity-media.json'),
    [int] $SizeMb = 256,
    [switch] $Remove
)
$ErrorActionPreference = 'Stop'

function Test-Elevated {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Elevated)) { throw 'Run this from an elevated PowerShell: attaching VHDX files and creating SMB shares needs administrator rights.' }

Add-Type -Namespace SiPath -Name Native -MemberDefinition '[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint GetLongPathNameW(string shortPath, System.Text.StringBuilder longPath, uint length);'
# A hosted runner's %TEMP% is an 8.3 path (C:\Users\RUNNER~1\...); virtual-disk tooling is happier with one spelling, so use the long form.
function Get-LongPath([string] $Path) {
    $builder = New-Object System.Text.StringBuilder 1024
    $length = [SiPath.Native]::GetLongPathNameW($Path, $builder, 1024)
    if ($length -gt 0 -and $length -lt 1024) { return $builder.ToString() } else { return $Path }
}
if (Test-Path -LiteralPath $Root) { $Root = Get-LongPath $Root }

# A folder this script created holds this marker; nothing else is ever used or deleted as -Root.
$Marker = '.si-identity-media'
function Assert-ScratchRoot {
    if ($Root.Length -lt 8 -or [IO.Path]::GetPathRoot($Root) -eq $Root) { throw "-Root '$Root' is too general to use as a scratch folder." }
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $ours = Test-Path -LiteralPath (Join-Path $Root $Marker)
    $empty = -not (Get-ChildItem -LiteralPath $Root -Force -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not $ours -and -not $empty) { throw "-Root '$Root' exists, is not empty and was not made by this script (no $Marker marker). Refusing to use or delete it; pass a folder that does not exist." }
}

function Get-FreeLetters {
    $used = [IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name[0] }
    ('M','N','O','P','Q','R','S','T','U','W' | Where-Object { $used -notcontains $_ -and -not (Test-Path "$($_):\") })
}

function Invoke-Diskpart([string[]] $Lines) {
    $script = Join-Path $Root ("dp_{0}.txt" -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    Set-Content -LiteralPath $script -Value $Lines -Encoding ASCII
    $output = & diskpart.exe /s $script 2>&1 | Out-String
    Remove-Item -LiteralPath $script -Force -ErrorAction SilentlyContinue
    return $output
}

function Remove-All($state) {
    $ErrorActionPreference = 'Continue'   # "already gone" is fine while tearing down; native tools write that to stderr
    if (-not $state -and (Test-Path -LiteralPath $Manifest)) { $state = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json }
    # the manifest knows where the media were made, whatever -Root says now (the marker below still decides what may be deleted)
    if ($state -and $state.root) { $script:Root = [string]$state.root }
    foreach ($mapping in 'mappedLetterA', 'mappedLetterB') { if ($state -and $state.$mapping) { & net.exe use ($state.$mapping.Substring(0, 2)) /delete /y 2>&1 | Out-Null } }
    foreach ($share in 'SiEvidenceA', 'SiEvidenceB') { Remove-SmbShare -Name $share -Force -ErrorAction SilentlyContinue }
    foreach ($image in Get-ChildItem -LiteralPath $Root -Filter *.iso -ErrorAction SilentlyContinue) { Dismount-DiskImage -ImagePath $image.FullName -ErrorAction SilentlyContinue | Out-Null }
    foreach ($vhd in Get-ChildItem -LiteralPath $Root -Filter *.vhdx -ErrorAction SilentlyContinue) {
        Invoke-Diskpart @("select vdisk file=`"$($vhd.FullName)`"", 'detach vdisk') | Out-Null
    }
    if (Test-Path -LiteralPath $Root) {
        if (Test-Path -LiteralPath (Join-Path $Root $Marker)) { Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue }
        else { Write-Warning "Left '$Root' alone: it has no $Marker marker, so this script did not make it." }
    }
    Remove-Item -LiteralPath $Manifest -Force -ErrorAction SilentlyContinue
    'removed'
}

if ($Remove) { Remove-All $null; return }

Assert-ScratchRoot
New-Item -ItemType Directory -Force $Root | Out-Null
$Root = Get-LongPath $Root
Set-Content -LiteralPath (Join-Path $Root $Marker) -Value 'Made by Provision-IdentityMedia.ps1; safe to delete with -Remove.' -Encoding ASCII
$result = [ordered]@{}
$notes = [ordered]@{}
$letters = [System.Collections.Queue]::new(@(Get-FreeLetters))
try {

function New-VirtualVolume([string] $Name, [string] $FileSystem, [string] $Label, [int] $Mb) {
    if ($letters.Count -eq 0) { $notes[$Name] = 'no free drive letter'; return }
    $letter = $letters.Dequeue()
    $file = Join-Path $Root "$Name.vhdx"
    $out = Invoke-Diskpart @(
        "create vdisk file=`"$file`" maximum=$Mb type=expandable", "select vdisk file=`"$file`"", 'attach vdisk',
        'create partition primary', "format fs=$FileSystem quick label=$Label", "assign letter=$letter")
    if (-not (Test-Path "$($letter):\")) { $notes[$Name] = "could not create or format ($FileSystem): " + ($out -replace '\s+', ' ').Trim(); $letters.Enqueue($letter); return }
    $result[$Name] = "$($letter):\"
}

New-VirtualVolume 'ntfsA' 'ntfs' 'SI_NTFS_A' $SizeMb
New-VirtualVolume 'ntfsB' 'ntfs' 'SI_NTFS_B' $SizeMb
New-VirtualVolume 'fat32' 'fat32' 'SI_FAT32' $SizeMb
New-VirtualVolume 'exfat' 'exfat' 'SI_EXFAT' $SizeMb
New-VirtualVolume 'refs' 'refs' 'SI_REFS' 4096   # ReFS wants a larger volume; the file stays sparse

# --- UDF: an image made with IMAPI2FS (no ADK needed) and mounted (no administrator rights needed to mount an ISO, but it is here) ---
try {
    $content = Join-Path $Root 'udf-content'
    New-Item -ItemType Directory -Force (Join-Path $content 'Photos') | Out-Null
    Set-Content -LiteralPath (Join-Path $content 'Photos\readme.txt') -Value 'identity evidence'
    $iso = Join-Path $Root 'si_udf.iso'
    Add-Type -TypeDefinition @'
using System; using System.IO; using System.Runtime.InteropServices; using System.Runtime.InteropServices.ComTypes;
public static class SiIsoStream {
    public static void Save(object o, string path, int block, int blocks) {
        IStream s = (IStream)o; byte[] buf = new byte[block * 64]; IntPtr pRead = Marshal.AllocHGlobal(4);
        try { using (FileStream fs = File.Create(path)) { int remaining = blocks; while (remaining > 0) { int n = Math.Min(remaining, 64); s.Read(buf, n * block, pRead); fs.Write(buf, 0, Marshal.ReadInt32(pRead)); remaining -= n; } } }
        finally { Marshal.FreeHGlobal(pRead); }
    }
}
'@
    $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $fsi.FileSystemsToCreate = 4          # FsiFileSystemUDF
    $fsi.UDFRevision = 0x200
    $fsi.VolumeName = 'SI_UDF'
    $fsi.Root.AddTree($content, $false)
    $image = $fsi.CreateResultImage()
    [SiIsoStream]::Save($image.ImageStream, $iso, $image.BlockSize, $image.TotalBlocks)
    $mounted = Mount-DiskImage -ImagePath $iso -StorageType ISO -PassThru
    $volume = $mounted | Get-Volume
    if ($volume.DriveLetter) { $result['udf'] = "$($volume.DriveLetter):\" } else { $notes['udf'] = 'the image mounted without a drive letter' }
}
catch { $notes['udf'] = 'could not build or mount a UDF image: ' + $_.Exception.Message }

# --- SMB: two shares served by this machine; one of them is on the FAT32 volume ---
foreach ($spec in @(@{ Name = 'SiEvidenceA'; Key = 'mappedLetterA'; Share = 'smbShareA'; Backing = 'ntfsA' }, @{ Name = 'SiEvidenceB'; Key = 'mappedLetterB'; Share = 'smbShareB'; Backing = 'fat32' })) {
    try {
        if (-not $result[$spec.Backing]) { throw "the backing volume ($($spec.Backing)) is not available" }
        $folder = Join-Path $result[$spec.Backing] 'share'
        New-Item -ItemType Directory -Force (Join-Path $folder 'Media\Music') | Out-Null
        Set-Content -LiteralPath (Join-Path $folder 'Media\Music\song.txt') -Value 'x'
        # open to the current user only (never 'Everyone'): the share exists only while the experiments run, on this machine
        New-SmbShare -Name $spec.Name -Path $folder -FullAccess ([Security.Principal.WindowsIdentity]::GetCurrent().Name) | Out-Null
        $unc = "\\localhost\$($spec.Name)"
        $result[$spec.Share] = $unc
        if ($letters.Count -gt 0) {
            $letter = $letters.Dequeue()
            & net.exe use "$($letter):" $unc /persistent:no 2>&1 | Out-Null
            if (Test-Path "$($letter):\") { $result[$spec.Key] = "$($letter):\" } else { $notes[$spec.Key] = 'the share could not be mapped to a letter' }
        }
    }
    catch { $notes[$spec.Share] = 'no SMB share: ' + $_.Exception.Message }
}

$result['root'] = $Root
$result['notes'] = $notes
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Manifest -Encoding UTF8
Write-Output (Get-Content -LiteralPath $Manifest -Raw)
}
catch {
    # a failure while provisioning must not leave attached disks, shares or mappings behind
    Remove-All ([pscustomobject]$result) | Out-Null
    throw
}
