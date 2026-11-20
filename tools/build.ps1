<#
.SYNOPSIS
  Builds Cloudpunk VRevival. -Package also makes the release zip in artifacts\.
  -Dev builds with the developer commands and probes (DEV), for testing only.

.DESCRIPTION
  Needs the .NET SDK (6 or later) and a Cloudpunk install where BepInEx has run once
  (its BepInEx\interop folder provides the game assemblies to compile against).
#>
param(
    [string]$CloudpunkDir = 'C:\Program Files (x86)\Steam\steamapps\common\Cloudpunk',
    [switch]$Package,
    [switch]$Dev
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (Test-Path $userDotnet) { $dotnet = $userDotnet }
if (-not $dotnet) { throw '.NET SDK not found.' }
if (-not (Test-Path (Join-Path $CloudpunkDir 'BepInEx\interop\Assembly-CSharp.dll'))) {
    throw 'BepInEx\interop is missing: install BepInEx and launch the game once first.'
}

if ($Dev -and $Package) { throw 'Release zips are built without -Dev.' }

$flavour = if ($Dev) { 'true' } else { 'false' }
& $dotnet build (Join-Path $root 'CloudpunkVR.sln') -c Release "-p:CloudpunkDir=$CloudpunkDir" "-p:DevBuild=$flavour" -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if ($Package) {
    $version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
    $stage = Join-Path $root "artifacts\stage"
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    $plugin = Join-Path $stage 'BepInEx\plugins\CloudpunkVR'
    $patcher = Join-Path $stage 'BepInEx\patchers\CloudpunkVR'
    New-Item -ItemType Directory -Force (Join-Path $plugin 'bindings'), $patcher | Out-Null
    Copy-Item (Join-Path $root 'src\CloudpunkVR\bin\Release\CloudpunkVR.dll') $plugin
    Copy-Item (Join-Path $root 'third_party\openvr\openvr_api.dll') $plugin
    Copy-Item (Join-Path $root 'bindings\*.json') (Join-Path $plugin 'bindings')
    Copy-Item (Join-Path $root 'src\CloudpunkVR.Patcher\bin\Release\CloudpunkVR.Patcher.dll') $patcher
    Copy-Item (Join-Path $root 'tools\install.ps1'), (Join-Path $root 'tools\uninstall.ps1'), (Join-Path $root 'tools\INSTALL.bat'), (Join-Path $root 'tools\UNINSTALL.bat') $stage
    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY_NOTICES.txt') $stage
    Copy-Item (Join-Path $root 'third_party\openvr\LICENSE') (Join-Path $stage 'LICENSE-OpenVR.txt')
    $zip = Join-Path $root "artifacts\CloudpunkVRevival-$version.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Remove-Item $stage -Recurse -Force
    Write-Host "Package: $zip"
}
