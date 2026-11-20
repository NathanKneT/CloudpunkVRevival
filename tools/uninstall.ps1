<#
.SYNOPSIS
  Removes Cloudpunk VRevival and puts the game files back as Steam installed them.

.DESCRIPTION
  - restores globalgamemanagers and boot.config from the *.cloudpunkvr-original copies, but only while they are still
    the mod's versions: after a game update the game's new files stay and the old copies are just deleted,
  - removes openvr_api.dll from Cloudpunk_Data\Plugins and the mod's BepInEx folders,
  - with -RemoveBepInEx, also removes BepInEx itself (winhttp.dll, doorstop_config.ini, dotnet\, BepInEx\ ...).
  Your saves (AppData\LocalLow\ION LANDS\Cloudpunk) are never touched.
  If anything looks wrong afterwards, Steam > Cloudpunk > Properties > Installed Files > Verify integrity.
#>
param(
    [string]$GameDir,
    [switch]$RemoveBepInEx
)
$ErrorActionPreference = 'Stop'

# Paths go to .NET and -LiteralPath only: a folder name with [ ] in it would be read as a wildcard otherwise.
function Find-Cloudpunk {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    $steam = if ($steam) { [System.IO.Path]::GetFullPath($steam) } else { 'C:\Program Files (x86)\Steam' }
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        $libs += Select-String -LiteralPath $vdf -Encoding UTF8 -Pattern '"path"\s+"([^"]+)"' -AllMatches |
            ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($lib in $libs | Select-Object -Unique) {
        $dir = Join-Path $lib 'steamapps\common\Cloudpunk'
        if (Test-Path -LiteralPath (Join-Path $dir 'Cloudpunk.exe')) { return $dir }
    }
    return $null
}

# Still the mod's version: OpenVR in globalgamemanagers' VR devices, vr-enabled=1 in boot.config.
function Test-ModVersion([string]$File, [string]$Name) {
    if ($Name -eq 'boot.config') { return [System.IO.File]::ReadAllText($File) -match '(?m)^\s*vr-enabled\s*=\s*1' }
    $text = [System.Text.Encoding]::GetEncoding(28591).GetString([System.IO.File]::ReadAllBytes($File))
    return $text.Contains('OpenVR')
}

function Remove-IfThere([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try { Remove-Item -LiteralPath $Path -Recurse -Force }
    catch { Write-Warning "Could not remove ${Path}: $($_.Exception.Message)" }
}

if (-not $GameDir) { $GameDir = Find-Cloudpunk }
if (-not $GameDir -or -not (Test-Path -LiteralPath (Join-Path $GameDir 'Cloudpunk.exe'))) { throw 'Cloudpunk not found. Pass its folder with -GameDir.' }
$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath
Write-Host "Cloudpunk: $GameDir"
if (Get-Process Cloudpunk -ErrorAction SilentlyContinue) { throw 'Close Cloudpunk first.' }

$data = Join-Path $GameDir 'Cloudpunk_Data'
foreach ($name in 'globalgamemanagers', 'boot.config') {
    $file = Join-Path $data $name
    $orig = "$file.cloudpunkvr-original"
    if (-not (Test-Path -LiteralPath $orig)) { continue }
    if ((Test-Path -LiteralPath $file) -and -not (Test-ModVersion $file $name)) {
        # a game update (or a flat start) already put the game's own file there: the old copy would undo the update
        Remove-Item -LiteralPath $orig -Force
        Write-Host "$name is already the game's own (left as it is); its old copy removed"
        continue
    }
    [System.IO.File]::Copy($orig, $file, $true)
    Remove-Item -LiteralPath $orig -Force
    Write-Host "Restored $name"
}
Remove-IfThere (Join-Path $data 'Plugins\openvr_api.dll')
Remove-IfThere (Join-Path $GameDir 'BepInEx\plugins\CloudpunkVR')
Remove-IfThere (Join-Path $GameDir 'BepInEx\patchers\CloudpunkVR')
Remove-IfThere (Join-Path $GameDir 'BepInEx\config\CloudpunkVR.cfg')
Remove-IfThere (Join-Path $GameDir 'BepInEx\config\CloudpunkVR.devcmd')
Write-Host 'Cloudpunk VRevival removed.'

if ($RemoveBepInEx) {
    foreach ($p in 'BepInEx', 'dotnet', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') {
        Remove-IfThere (Join-Path $GameDir $p)
    }
    Write-Host 'BepInEx removed.'
} else {
    Write-Host 'BepInEx stays (other BepInEx mods need it). To remove it too, run UNINSTALL.bat again and answer y.'
}
