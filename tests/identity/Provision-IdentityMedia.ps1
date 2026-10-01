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
    else is refused, a folder that cannot be listed is refused too, and the manifest records that this script wrote it.
    -Remove validates EVERYTHING before it changes anything: the manifest must be one this script wrote, its root must hold the
    marker, and only then are shares, mappings and disk images touched, each still checked against the manifest (a drive letter
    that no longer points at the share the manifest names is left alone; only the image names this script gives its disks are
    detached; only a marked folder is deleted). On any doubt it refuses and changes nothing, so a wrong -Root or -Manifest
    cannot detach someone else's disk, dismount their ISO, disconnect their mapping or delete their file. The SMB shares are
    open to the CURRENT USER only, not to everyone, and exist only while the experiments run.
.EXAMPLE
    .\Provision-IdentityMedia.ps1 -Manifest $env:TEMP\identity-media.json
    ...run the experiments...
    .\Provision-IdentityMedia.ps1 -Remove -Manifest $env:TEMP\identity-media.json
#>
param(
    [string] $Root = (Join-Path ([IO.Path]::GetTempPath()) 'SiIdentityMedia'),
    [string] $Manifest = (Join-Path ([IO.Path]::GetTempPath()) 'identity-media.json'),
    [int] $SizeMb = 256,
    [switch] $Remove
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IdentitySafety.ps1')

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

# The marker, the root guard, the manifest signature and the teardown rules are in IdentitySafety.ps1 (shared with the fixtures).

function Get-FreeLetters {
    $used = [IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name[0] }
    ('M','N','O','P','Q','R','S','T','U','W' | Where-Object { $used -notcontains $_ -and -not (Test-Path "$($_):\") })
}

function Invoke-Diskpart([string[]] $Lines, [string] $Folder = $Root) {
    $script = Join-Path $Folder ("dp_{0}.txt" -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    Set-Content -LiteralPath $script -Value $Lines -Encoding ASCII
    $output = & diskpart.exe /s $script 2>&1 | Out-String
    Remove-Item -LiteralPath $script -Force -ErrorAction SilentlyContinue
    return $output
}

# The real side effects the teardown may perform. Invoke-MediaTeardown calls them only after it has proved the root is ours.
$RealActions = @{
    GetMapping     = { param($letter) (Get-SmbMapping -LocalPath $letter.Substring(0, 2) -ErrorAction SilentlyContinue | Select-Object -First 1).RemotePath }
    Unmap          = { param($letter) & net.exe use ($letter.Substring(0, 2)) /delete /y 2>&1 | Out-Null }
    GetShare       = { param($name) (Get-SmbShare -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1).Path }
    RemoveShare    = { param($name) Remove-SmbShare -Name $name -Force -ErrorAction SilentlyContinue }
    DismountIso    = { param($file) Dismount-DiskImage -ImagePath $file -ErrorAction SilentlyContinue | Out-Null }
    DetachVhd      = { param($file) Invoke-Diskpart @("select vdisk file=`"$file`"", 'detach vdisk') (Split-Path -Parent $file) | Out-Null }
    RemoveFolder   = { param($path) Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
    RemoveManifest = { param($path) Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}

if ($Remove) {
    $ErrorActionPreference = 'Continue'   # "already gone" is fine while tearing down; native tools write that to stderr
    $outcome = Invoke-MediaTeardown -Root $Root -RootWasSupplied:$PSBoundParameters.ContainsKey('Root') -ManifestPath $Manifest -Actions $RealActions
    if ($outcome.Refused) { Write-Warning "REFUSED: $($outcome.Reason)"; exit 2 }
    $outcome.Removed | ForEach-Object { Write-Output $_ }
    'removed'
    return
}

Assert-ScratchRoot $Root
New-Item -ItemType Directory -Force $Root | Out-Null
$Root = Get-LongPath $Root
Write-MediaMarker $Root
# the manifest says who wrote it: -Remove acts on no other file
$result = [ordered]@{ madeBy = $script:MediaManifestAuthor; schema = $script:MediaManifestSchema; root = $Root }
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
    Invoke-MediaTeardown -Root $Root -RootWasSupplied -State ([pscustomobject]$result) -Actions $RealActions | Out-Null
    throw
}
