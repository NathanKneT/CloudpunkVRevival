<#
.SYNOPSIS
  Installs Cloudpunk VRevival into the Steam copy of Cloudpunk.

.DESCRIPTION
  1. Finds Cloudpunk (Steam app 746850), or uses -GameDir.
  2. Installs BepInEx 6 (IL2CPP, build 793) if it is not there yet: from -BepInExZip, or downloaded from
     builds.bepinex.dev after you confirm (the download is checked against its known SHA-256).
  3. Copies the mod (plugin, preloader patcher, SteamVR bindings, openvr_api.dll).
  The game files themselves are only changed at the next launch, by the patcher, and the originals are kept.

  Run from the folder that contains this script (release zip) or from the repository after a Release build.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1
#>
param(
    [string]$GameDir,
    [string]$BepInExZip,
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'
$BepInExUrl = 'https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip'
$BepInExSha256 = '5B63D7D0E92A2448CCD13B738FF95FF1CC1026C6D718555D4DF8E6C824953AC4'
$TestedBuild = '8245592'

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

function Copy-Tree([string]$From, [string]$To) {
    [System.IO.Directory]::CreateDirectory($To) | Out-Null
    foreach ($f in [System.IO.Directory]::GetFiles($From)) { [System.IO.File]::Copy($f, [System.IO.Path]::Combine($To, [System.IO.Path]::GetFileName($f)), $true) }
    foreach ($d in [System.IO.Directory]::GetDirectories($From)) { Copy-Tree $d ([System.IO.Path]::Combine($To, [System.IO.Path]::GetFileName($d))) }
}

function Copy-File([string]$File, [string]$ToDir) {
    [System.IO.File]::Copy($File, [System.IO.Path]::Combine($ToDir, [System.IO.Path]::GetFileName($File)), $true)
}

function Get-Sha256([string]$File) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($File)
    try { return -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('X2') }) }
    finally { $stream.Dispose(); $sha.Dispose() }
}

# Expand-Archive in Windows PowerShell fails on a destination with [ ] in it.
function Expand-Zip([string]$Zip, [string]$To) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $root = [System.IO.Path]::GetFullPath($To).TrimEnd('\') + '\'
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        foreach ($entry in $archive.Entries) {
            $target = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($root, $entry.FullName))
            if (-not $target.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected path in the zip: $($entry.FullName)" }
            if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) { [System.IO.Directory]::CreateDirectory($target) | Out-Null; continue }
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally { $archive.Dispose() }
}

if (-not $GameDir) { $GameDir = Find-Cloudpunk }
if (-not $GameDir -or -not (Test-Path -LiteralPath (Join-Path $GameDir 'Cloudpunk.exe'))) {
    throw 'Cloudpunk not found. Pass its folder with -GameDir "D:\SteamLibrary\steamapps\common\Cloudpunk".'
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath
Write-Host "Cloudpunk: $GameDir"

$manifest = [System.IO.Path]::GetFullPath((Join-Path $GameDir '..\..\appmanifest_746850.acf'))
if (Test-Path -LiteralPath $manifest) {
    $match = Select-String -LiteralPath $manifest -Pattern '"buildid"\s+"(\d+)"'
    $build = if ($match) { $match.Matches[0].Groups[1].Value } else { $null }
    if ($build -and $build -ne $TestedBuild) { Write-Warning "Game build $build differs from the tested build $TestedBuild. It will probably work; report problems with this number." }
}
if (Get-Process Cloudpunk -ErrorAction SilentlyContinue) { throw 'Close Cloudpunk first.' }

# Where the mod files are: release layout (BepInEx\ next to this script) or repository build output.
$here = $PSScriptRoot
$releaseRoot = if (Test-Path -LiteralPath (Join-Path $here 'BepInEx\plugins\CloudpunkVR\CloudpunkVR.dll')) { $here } else { $null }
$repo = [System.IO.Path]::GetFullPath((Join-Path $here '..'))

# 1. BepInEx
$bepCore = Join-Path $GameDir 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
if (-not (Test-Path -LiteralPath $bepCore)) {
    $downloaded = $null
    try {
        if ($BepInExZip) {
            $BepInExZip = (Resolve-Path -LiteralPath $BepInExZip).ProviderPath
            if ((Get-Sha256 $BepInExZip) -ne $BepInExSha256) {
                Write-Warning 'This BepInEx zip is not the official 6.0.0-be.793 IL2CPP one. Installing it anyway; the mod is made for be.793.'
            }
        } else {
            if (-not $Yes) {
                $answer = Read-Host "BepInEx 6 (IL2CPP) is needed. Download it from builds.bepinex.dev now? [y/N]"
                if ($answer -notmatch '^[yY]') { throw 'BepInEx not installed. Download it yourself and pass -BepInExZip.' }
            }
            $downloaded = Join-Path $env:TEMP 'CloudpunkVR-BepInEx.zip'
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Write-Host 'Downloading BepInEx (about 34 MB)...'
            # not Invoke-WebRequest: its -OutFile reads [ ] in the path as a wildcard, and its progress bar slows it down a lot
            $web = New-Object System.Net.WebClient
            try { $web.DownloadFile($BepInExUrl, $downloaded) }
            finally { $web.Dispose() }
            $hash = Get-Sha256 $downloaded
            if ($hash -ne $BepInExSha256) {
                throw "The BepInEx download is not the expected file (SHA-256 $hash). Nothing was installed. Try again later, or download BepInEx 6.0.0-be.793 IL2CPP yourself and pass -BepInExZip."
            }
            $BepInExZip = $downloaded
        }
        Expand-Zip $BepInExZip $GameDir
    }
    finally {
        if ($downloaded) { Remove-Item -LiteralPath $downloaded -Force -ErrorAction SilentlyContinue }
    }
    Write-Host 'BepInEx installed. The first launch takes a few minutes while it prepares the game (once).'
} else {
    $coreDll = Join-Path $GameDir 'BepInEx\core\BepInEx.Core.dll'
    $version = if (Test-Path -LiteralPath $coreDll) { (Get-Item -LiteralPath $coreDll).VersionInfo.ProductVersion } else { $null }
    if ($version -notmatch '-be\.793\b') {
        $shown = if ($version) { $version } else { 'unknown version' }
        Write-Warning "BepInEx is already installed, but not build 793 ($shown). Cloudpunk VRevival is made for 6.0.0-be.793 and may not load with it."
    }
}

# 2. Mod files
$plugin = Join-Path $GameDir 'BepInEx\plugins\CloudpunkVR'
$patcher = Join-Path $GameDir 'BepInEx\patchers\CloudpunkVR'
[System.IO.Directory]::CreateDirectory((Join-Path $plugin 'bindings')) | Out-Null
[System.IO.Directory]::CreateDirectory($patcher) | Out-Null
if ($releaseRoot) {
    Copy-Tree (Join-Path $releaseRoot 'BepInEx\plugins\CloudpunkVR') $plugin
    Copy-Tree (Join-Path $releaseRoot 'BepInEx\patchers\CloudpunkVR') $patcher
} elseif (Test-Path -LiteralPath (Join-Path $repo 'src\CloudpunkVR\bin\Release\CloudpunkVR.dll')) {
    Copy-File (Join-Path $repo 'src\CloudpunkVR\bin\Release\CloudpunkVR.dll') $plugin
    Copy-File (Join-Path $repo 'third_party\openvr\openvr_api.dll') $plugin
    foreach ($json in [System.IO.Directory]::GetFiles((Join-Path $repo 'bindings'), '*.json')) { Copy-File $json (Join-Path $plugin 'bindings') }
    Copy-File (Join-Path $repo 'src\CloudpunkVR.Patcher\bin\Release\CloudpunkVR.Patcher.dll') $patcher
} else {
    throw 'Mod files not found next to this script, and no Release build in the repository (run tools\build.ps1).'
}

# BepInEx opens a log console window by default; players do not need it (BepInEx\LogOutput.log keeps the log).
$bepCfg = Join-Path $GameDir 'BepInEx\config\BepInEx.cfg'
$utf8 = New-Object System.Text.UTF8Encoding $false
if (Test-Path -LiteralPath $bepCfg) {
    $text = [System.IO.File]::ReadAllText($bepCfg)
    $fixed = [regex]::Replace($text, '(\[Logging\.Console\][^\[]*?\r?\nEnabled = )true', '${1}false')
    if ($fixed -ne $text) { [System.IO.File]::WriteAllText($bepCfg, $fixed, $utf8); Write-Host 'BepInEx console window turned off (the log file stays).' }
} else {
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($bepCfg)) | Out-Null
    [System.IO.File]::WriteAllText($bepCfg, "[Logging.Console]`nEnabled = false`n", $utf8)
}

Write-Host ''
Write-Host 'Cloudpunk VRevival is installed.'
Write-Host '  1. Start SteamVR with your headset (PS VR2: PlayStation VR2 App, then SteamVR).'
Write-Host '  2. Launch Cloudpunk from Steam as usual. The first launch takes a few minutes with nothing on screen: leave it.'
Write-Host '  Settings: in VR, pause menu > VREVIVAL, or BepInEx\config\CloudpunkVR.cfg. To remove the mod: UNINSTALL.bat.'
