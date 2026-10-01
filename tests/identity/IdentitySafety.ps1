<#
.SYNOPSIS
    The path and ownership guards shared by Invoke-IdentityExperiments.ps1 and Provision-IdentityMedia.ps1 (C3 repair, findings
    C3-H01 and C3-M01). Dot-source it; it defines functions only and does nothing when loaded.
.DESCRIPTION
    Two rules, applied everywhere these scripts touch a folder, a disk image, a share or a drive mapping:

      1. A script deletes, detaches, dismounts, disconnects or unshares only what it can PROVE it made: a unique folder it
         created itself, carrying a marker file it wrote, or an image/share/mapping that the marker folder's own manifest names
         and that still points where the manifest says. A folder, file or letter that the caller merely supplied is never
         enough, whatever it contains.
      2. Every check happens BEFORE the first side effect. When a check cannot be completed (the folder cannot be read, the
         manifest is not ours) the answer is "refuse and touch nothing", never "carry on".

    The logic is kept in functions that take their side effects as injectable actions, so tests\identity\Test-IdentityScriptSafety.ps1
    can run every refusal on any machine, with recording stand-ins instead of diskpart, and prove that nothing was called.
    Written for Windows PowerShell 5.1 and PowerShell 7.
#>

$script:ScratchMarker = '.si-identity-scratch'
$script:ScratchMarkerText = 'Made by tests/identity (IdentitySafety.ps1). Safe to delete together with the script that made it.'
$script:MediaMarker = '.si-identity-media'
$script:MediaMarkerText = 'Made by Provision-IdentityMedia.ps1; safe to delete with -Remove.'
$script:MediaManifestAuthor = 'Provision-IdentityMedia.ps1'
$script:MediaManifestSchema = 1
$script:PathComparison = if ([IO.Path]::DirectorySeparatorChar -eq '\') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

# ------------------------------------------------------------------ paths

# The absolute path without a trailing separator (a filesystem root keeps its own).
function Get-NormalizedFullPath([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    if ($full.Length -gt $root.Length) { $full = $full.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) }
    return $full
}

function Test-SamePath([string] $A, [string] $B) { return [string]::Equals((Get-NormalizedFullPath $A), (Get-NormalizedFullPath $B), $script:PathComparison) }

# Is $Child strictly inside $Folder (lexically; both absolute)?
function Test-PathInside([string] $Child, [string] $Folder) {
    $c = Get-NormalizedFullPath $Child
    $f = Get-NormalizedFullPath $Folder
    if (-not $f.EndsWith([string][IO.Path]::DirectorySeparatorChar)) { $f += [IO.Path]::DirectorySeparatorChar }
    return ($c.Length -gt $f.Length -and $c.StartsWith($f, $script:PathComparison))
}

# A filesystem root, or too short to be a deliberate scratch location. (The length is a variable so a fixture can show that a
# folder which carries the marker is still refused when it is "too general".)
$script:MinimumScratchPathLength = 8
function Test-PathTooGeneral([string] $Path) {
    $full = Get-NormalizedFullPath $Path
    return ($full.Length -lt $script:MinimumScratchPathLength -or [string]::Equals([IO.Path]::GetPathRoot($full), $full, $script:PathComparison))
}

function Test-IsReparsePoint([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    return (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

# ------------------------------------------------------------------ Invoke-IdentityExperiments.ps1: -Out and -Work

# -Out: refuse a folder, refuse an existing file unless the caller said -Overwrite, refuse a folder that does not exist.
# Nothing is created or changed here. Returns the absolute path.
function Resolve-OutputFile([string] $Path, [switch] $Overwrite) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw '-Out is empty.' }
    $full = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $full -PathType Container) { throw "-Out '$full' is a folder. Pass the report file to write." }
    $directory = Split-Path -Parent $full
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "-Out '$full': the folder '$directory' does not exist." }
    if ((Test-Path -LiteralPath $full -PathType Leaf) -and -not $Overwrite) { throw "-Out '$full' already exists. Refusing to overwrite it; delete it first or pass -Overwrite." }
    return $full
}

# -Work is the folder the scratch folder is created IN, never the scratch folder itself, so nothing that is already there
# can be deleted. Nothing is created here. Returns the absolute path.
function Resolve-WorkParent([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw '-Work is empty.' }
    $full = Get-NormalizedFullPath $Path
    if (Test-PathTooGeneral $full) { throw "-Work '$full' is a filesystem root or too general. Pass a folder to create the scratch folder in." }
    if (Test-Path -LiteralPath $full -PathType Leaf) { throw "-Work '$full' is a file." }
    return $full
}

# A new, uniquely named folder inside $Parent, marked as made by this script. The only folder these scripts ever delete.
function New-ScratchChild([string] $Parent, [string] $Prefix) {
    [void][IO.Directory]::CreateDirectory($Parent)
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $child = Join-Path $Parent ($Prefix + [guid]::NewGuid().ToString('N').Substring(0, 8))
        if (Test-Path -LiteralPath $child) { continue }
        [void][IO.Directory]::CreateDirectory($child)
        Set-Content -LiteralPath (Join-Path $child $script:ScratchMarker) -Value $script:ScratchMarkerText -Encoding ASCII
        return (Get-NormalizedFullPath $child)
    }
    throw "Could not find an unused name for a scratch folder inside '$Parent'."
}

# Deletes $Path only if it is exactly what New-ScratchChild made: a direct child of $Parent, named with the prefix and eight
# hex digits, carrying the marker with its text, and not a link. Anything else is left alone with a warning. True when deleted.
function Remove-OwnedScratch([string] $Path, [string] $Parent, [string] $Prefix) {
    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
        $full = Get-NormalizedFullPath $Path
        $leaf = Split-Path -Leaf $full
        if (-not (Test-SamePath (Split-Path -Parent $full) $Parent)) { throw 'it is not directly inside the folder the script was given' }
        if ($leaf -notmatch ('^' + [regex]::Escape($Prefix) + '[0-9a-f]{8}$')) { throw 'its name is not one the script gives its scratch folders' }
        $marker = Join-Path $full $script:ScratchMarker
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'it has no marker file, so this script did not make it' }
        if ((Get-Content -LiteralPath $marker -Raw -ErrorAction Stop).Trim() -ne $script:ScratchMarkerText) { throw 'its marker file is not the one this script writes' }
        if (Test-IsReparsePoint $full) { throw 'it is a link' }
    }
    catch { Write-Warning "Left '$Path' alone: $($_.Exception.Message)."; return $false }
    try { [IO.Directory]::Delete($full, $true); return $true }
    catch { Write-Warning "Could not delete '$full': $($_.Exception.Message)"; return $false }
}

# ------------------------------------------------------------------ Provision-IdentityMedia.ps1: -Root and -Remove

# Provisioning: -Root must not exist, or be empty, or be a folder this script made (marker). A folder that cannot be listed is
# refused: "I could not look" is never "it is empty".
function Assert-ScratchRoot([string] $Root) {
    if (Test-PathTooGeneral $Root) { throw "-Root '$Root' is too general to use as a scratch folder." }
    if (-not (Test-Path -LiteralPath $Root)) { return }
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "-Root '$Root' is a file." }
    if (Test-Path -LiteralPath (Join-Path $Root $script:MediaMarker) -PathType Leaf) { return }
    try { $first = Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop | Select-Object -First 1 }
    catch { throw "-Root '$Root' cannot be listed ($($_.Exception.Message)), so it cannot be shown to be empty. Refusing to use or delete it." }
    if ($first) { throw "-Root '$Root' exists, is not empty and was not made by this script (no $script:MediaMarker marker). Refusing to use or delete it; pass a folder that does not exist." }
}

function Write-MediaMarker([string] $Root) { Set-Content -LiteralPath (Join-Path $Root $script:MediaMarker) -Value $script:MediaMarkerText -Encoding ASCII }

# A manifest is ours only if it says so (author and schema) and names a root. Any other JSON is not.
function Test-ProvisionManifest($Manifest) {
    if ($null -eq $Manifest) { return $false }
    $names = @($Manifest.PSObject.Properties.Name)
    if ($names -notcontains 'madeBy' -or $names -notcontains 'schema' -or $names -notcontains 'root') { return $false }
    return ($Manifest.madeBy -eq $script:MediaManifestAuthor -and [string]$Manifest.schema -eq [string]$script:MediaManifestSchema -and -not [string]::IsNullOrWhiteSpace([string]$Manifest.root))
}

function New-RefusedTeardown([string] $Reason) { return [pscustomobject]@{ Refused = $true; Reason = $Reason; Removed = @() } }

# The shapes of what Provision-IdentityMedia.ps1 makes inside its root; anything else in there is not detached or dismounted.
$script:OwnedImagePattern = '^(?:(?:ntfsA|ntfsB|fat32|exfat|refs)\.vhdx|si_udf\.iso)$'

<#
    Tears down what Provision-IdentityMedia.ps1 made. Order is the safety property: first read and validate everything that
    decides what may be touched (manifest, root, marker), and only when ALL of it checks out perform side effects, each one
    re-verified against the manifest. On any doubt return a Refused result and call no action at all.

    $Actions (all required; the real ones are in Provision-IdentityMedia.ps1, tests pass recorders):
      GetMapping  { param($letter) }  the remote path a drive letter points at now, or $null
      Unmap       { param($letter) }
      GetShare    { param($name) }    the local folder a share serves now, or $null
      RemoveShare { param($name) }
      DismountIso { param($file) }
      DetachVhd   { param($file) }
      RemoveFolder   { param($path) }
      RemoveManifest { param($path) }
#>
function Invoke-MediaTeardown {
    param(
        [string] $Root,
        [switch] $RootWasSupplied,
        [string] $ManifestPath,
        $State,
        [hashtable] $Actions
    )
    $removed = New-Object System.Collections.Generic.List[string]

    # 1. the manifest, if there is one: it must be readable JSON that this script wrote
    $manifestIsOurs = $false
    if (-not $State -and $ManifestPath -and (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        try { $State = Get-Content -LiteralPath $ManifestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
        catch { return (New-RefusedTeardown "'$ManifestPath' cannot be read as JSON ($($_.Exception.Message)); nothing was changed.") }
        if (-not (Test-ProvisionManifest $State)) { return (New-RefusedTeardown "'$ManifestPath' was not written by $script:MediaManifestAuthor (no madeBy/schema/root); nothing was changed.") }
        $manifestIsOurs = $true
    }
    elseif ($State) {
        if (-not (Test-ProvisionManifest $State)) { return (New-RefusedTeardown 'the state passed in is not one this script made; nothing was changed.') }
    }

    # 2. which folder: the one the manifest names; an explicit -Root that disagrees is refused rather than guessed at
    $effective = $Root
    if ($State) {
        $effective = [string]$State.root
        if ($RootWasSupplied -and $Root -and -not (Test-SamePath $Root $effective)) {
            return (New-RefusedTeardown "-Root '$Root' is not the folder the manifest names ('$effective'); nothing was changed.")
        }
    }
    if ([string]::IsNullOrWhiteSpace($effective)) { return (New-RefusedTeardown 'no -Root and no manifest: there is nothing this script can show it made.') }

    # 3. the folder must be provably ours: not general, a real folder, not a link, holding the marker with the right text
    $full = Get-NormalizedFullPath $effective
    if (Test-PathTooGeneral $full) { return (New-RefusedTeardown "'$full' is a filesystem root or too general; nothing was changed.") }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { return (New-RefusedTeardown "'$full' does not exist, so nothing in it can be shown to be ours; nothing was changed.") }
    $marker = Join-Path $full $script:MediaMarker
    $markerText = $null
    if (Test-Path -LiteralPath $marker -PathType Leaf) { try { $markerText = (Get-Content -LiteralPath $marker -Raw -ErrorAction Stop).Trim() } catch { $markerText = $null } }
    if ($markerText -ne $script:MediaMarkerText) { return (New-RefusedTeardown "'$full' has no valid $script:MediaMarker marker, so this script did not make it; its disk images, shares and mappings were not touched.") }
    try { if (Test-IsReparsePoint $full) { return (New-RefusedTeardown "'$full' is a link; nothing was changed.") } }
    catch { return (New-RefusedTeardown "'$full' cannot be inspected ($($_.Exception.Message)); nothing was changed.") }

    # ---- from here on the folder is proven ours; every action is still checked against what the manifest says ----

    # 4. drive letters: only a letter the manifest names that STILL points at the share the manifest names
    if ($State) {
        foreach ($pair in @(@('mappedLetterA', 'smbShareA'), @('mappedLetterB', 'smbShareB'))) {
            $letter = [string]$State.($pair[0]); $share = [string]$State.($pair[1])
            if (-not $letter) { continue }
            if ($letter -notmatch '^[A-Za-z]:\\?$') { Write-Warning "Skipped the mapping '$letter': not a drive letter."; continue }
            $target = [string](& $Actions.GetMapping $letter)
            if ($share -and $target -and [string]::Equals($target.TrimEnd('\'), $share.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
                & $Actions.Unmap $letter; $removed.Add("unmapped $letter")
            }
            else { Write-Warning "Left the mapping $letter alone: it does not point at '$share' any more (it points at '$target')." }
        }
    }

    # 5. shares: only the two names this script creates, and only while they still serve a folder inside the marked root
    foreach ($name in 'SiEvidenceA', 'SiEvidenceB') {
        $served = [string](& $Actions.GetShare $name)
        if (-not $served) { continue }
        if (Test-PathInside $served $full) { & $Actions.RemoveShare $name; $removed.Add("removed share $name") }
        else { Write-Warning "Left the share $name alone: it serves '$served', which is not inside '$full'." }
    }

    # 6. images: only files directly inside the marked root, with the names this script gives them
    foreach ($file in @(Get-ChildItem -LiteralPath $full -File -ErrorAction SilentlyContinue)) {
        if ($file.Name -notmatch $script:OwnedImagePattern) { if ($file.Extension -in '.iso', '.vhdx') { Write-Warning "Left '$($file.Name)' alone: not an image this script makes." }; continue }
        if ($file.Extension -eq '.iso') { & $Actions.DismountIso $file.FullName; $removed.Add("dismounted $($file.Name)") }
        else { & $Actions.DetachVhd $file.FullName; $removed.Add("detached $($file.Name)") }
    }

    # 7. the folder (marker re-read just before) and the manifest (only if it passed validation)
    $markerAgain = $null
    try { $markerAgain = (Get-Content -LiteralPath $marker -Raw -ErrorAction Stop).Trim() } catch { $markerAgain = $null }
    if ($markerAgain -eq $script:MediaMarkerText) { & $Actions.RemoveFolder $full; $removed.Add("removed folder $full") }
    else { Write-Warning "Left '$full' alone: its marker changed while tearing down." }
    if ($manifestIsOurs) { & $Actions.RemoveManifest $ManifestPath; $removed.Add("removed manifest $ManifestPath") }

    return [pscustomobject]@{ Refused = $false; Reason = ''; Removed = $removed.ToArray() }
}
