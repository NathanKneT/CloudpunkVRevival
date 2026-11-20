# Helpers to test the mod without a headset, using SteamVR's built-in "null" HMD driver.
#
# A run switches <Steam>\config\steamvr.vrsettings to the null driver and always puts the settings back.
# Two modes, picked by Initialize-NullHeadset:
#   - one user (default): the settings file is copied to the backup folder before the run and restored after.
#   - shared machine, when other tools also run SteamVR tests on this PC: pass -SharedDir (or set
#     CLOUDPUNKVR_SHARED_DIR). That folder holds steamvr.vrsettings.original (the real settings, always restored)
#     and steamvr.lock (one run at a time, a lock older than 20 minutes is stale).
#     -OriginalSha (or CLOUDPUNKVR_SETTINGS_SHA) is an optional hash prefix checked before and after the run.

$ErrorActionPreference = 'Stop'

$script:SharedDir   = $null
$script:LockFile    = $null
$script:Original    = $null
$script:OriginalSha = $null
$script:SingleUser  = $true
$script:LockOwner   = 'Cloudpunk VR test'
$steamPath = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
$script:SteamDir    = if ($steamPath) { [System.IO.Path]::GetFullPath($steamPath) } else { 'C:\Program Files (x86)\Steam' }
$script:Settings    = Join-Path $script:SteamDir 'config\steamvr.vrsettings'
$script:VrProcesses = 'vrserver','vrcompositor','vrmonitor','vrdashboard','vrwebhelper','vrstartup','vrservice','vrprismhost'

function Initialize-NullHeadset {
    param(
        [Parameter(Mandatory)][string]$BackupDir,
        [string]$SharedDir = $env:CLOUDPUNKVR_SHARED_DIR,
        [string]$OriginalSha = $env:CLOUDPUNKVR_SETTINGS_SHA
    )
    if ($SharedDir) {
        $script:SharedDir = (Resolve-Path $SharedDir).Path
        $script:LockFile = Join-Path $script:SharedDir 'steamvr.lock'
        $script:Original = Join-Path $script:SharedDir 'steamvr.vrsettings.original'
        $script:SingleUser = $false
        if (-not (Test-Path $script:Original)) { throw "Shared mode needs $script:Original (a copy of the real SteamVR settings)." }
    } else {
        New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
        $script:Original = Join-Path $BackupDir 'steamvr.vrsettings.before-test'
        $script:SingleUser = $true
    }
    $script:OriginalSha = $OriginalSha
}

function Enter-SteamVRLock {
    param([int]$TimeoutMinutes = 30)
    if (-not $script:LockFile) { return }
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (Test-Path $script:LockFile) {
        $age = (Get-Date) - (Get-Item $script:LockFile).LastWriteTime
        if ($age.TotalMinutes -gt 20) {
            Write-Warning "Removing stale SteamVR lock ($([int]$age.TotalMinutes) min): $(Get-Content $script:LockFile -Raw)"
            Remove-Item $script:LockFile -Force
            break
        }
        if ((Get-Date) -gt $deadline) { throw "SteamVR lock still held: $(Get-Content $script:LockFile -Raw)" }
        Start-Sleep -Seconds 15
    }
    "$script:LockOwner, started $(Get-Date -Format s)" | Set-Content -Path $script:LockFile -Encoding utf8
}

function Update-SteamVRLock {
    if ($script:LockFile -and (Test-Path $script:LockFile)) { (Get-Item $script:LockFile).LastWriteTime = Get-Date }
}

function Exit-SteamVRLock {
    if ($script:LockFile -and (Test-Path $script:LockFile) -and ((Get-Content $script:LockFile -Raw) -like "$script:LockOwner*")) {
        Remove-Item $script:LockFile -Force
    }
}

function Get-FileSha { param($Path) (Get-FileHash -Algorithm SHA256 -Path $Path).Hash }

function Test-OriginalSha([string]$Path) {
    if (-not $script:OriginalSha) { return $true }
    return (Get-FileSha $Path).StartsWith($script:OriginalSha, [StringComparison]::OrdinalIgnoreCase)
}

function Set-NullDriverSettings {
    if ($script:SingleUser) {
        # never overwrite a backup left by an interrupted run, it holds the real settings
        if (Test-Path $script:Original) { Write-Warning "Keeping the existing settings backup $script:Original from an earlier run." }
        else { Copy-Item $script:Settings $script:Original }
    }
    if (-not (Test-OriginalSha $script:Original)) { throw 'The original settings file has an unexpected hash; not touching SteamVR.' }
    $s = Get-Content $script:Original -Raw | ConvertFrom-Json -AsHashtable
    if (-not $s.ContainsKey('steamvr'))   { $s['steamvr'] = @{} }
    if (-not $s.ContainsKey('dashboard')) { $s['dashboard'] = @{} }
    $s['steamvr']['forcedDriver'] = 'null'
    # only the null driver loads, so a plugged-in headset never wakes up during a test
    $s['steamvr']['activateMultipleDrivers'] = $false
    $s['steamvr']['enableHomeApp'] = $false
    $s['dashboard']['enableDashboard'] = $false
    $s['driver_null'] = [ordered]@{
        enable = $true; windowWidth = 1600; windowHeight = 900
        renderWidth = 1512; renderHeight = 1680; displayFrequency = 90
    }
    $s | ConvertTo-Json -Depth 10 | Set-Content -Path $script:Settings -Encoding utf8
}

function Restore-SteamVRSettings {
    if (-not $script:Original -or -not (Test-Path $script:Original)) { return $null }
    Copy-Item $script:Original $script:Settings -Force
    $sha = Get-FileSha $script:Settings
    if (-not (Test-OriginalSha $script:Settings)) { throw "Restored steamvr.vrsettings hash mismatch: $sha" }
    if ($script:SingleUser) { Remove-Item $script:Original -Force }
    return $sha
}

function Stop-SteamVR {
    foreach ($n in $script:VrProcesses) { Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}

function Start-NullHeadset {
    param([int]$TimeoutSeconds = 90)
    Stop-SteamVR
    Set-NullDriverSettings
    Start-Process 'steam://run/250820'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $fallbackAt = (Get-Date).AddSeconds(15)
    $fallbackDone = $false
    while ((Get-Date) -lt $deadline) {
        # Steam sometimes drops the steam:// request, then SteamVR is started directly
        if (-not $fallbackDone -and (Get-Date) -gt $fallbackAt -and -not (Get-Process vrserver, vrstartup -ErrorAction SilentlyContinue)) {
            $fallbackDone = $true
            $startup = Join-Path $script:SteamDir 'steamapps\common\SteamVR\bin\win64\vrstartup.exe'
            if (Test-Path $startup) { Write-Host 'Steam did not start SteamVR; starting it directly.'; Start-Process $startup }
        }
        $w = Get-Process vrcompositor -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'Headset Window' }
        if ($w) { Start-Sleep -Seconds 3; return $w }
        Start-Sleep -Seconds 2
    }
    throw 'SteamVR null headset did not come up (no "Headset Window").'
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
// PrintWindow into a GDI bitmap; PowerShell turns the HBITMAP into a PNG (no System.Drawing compile reference needed).
public static class WindowCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    public static IntPtr Grab(IntPtr hwnd) {
        // Per-monitor DPI aware while measuring: with display scaling, a DPI-unaware caller gets the window's size in
        // scaled-down units while PrintWindow draws real pixels, and the capture would keep only its top-left part.
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        try { return GrabAware(hwnd); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    static IntPtr GrabAware(IntPtr hwnd) {
        RECT r; GetClientRect(hwnd, out r);
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        PrintWindow(hwnd, mem, 3); // PW_CLIENTONLY | PW_RENDERFULLCONTENT
        SelectObject(mem, old);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        return bmp;
    }
}
'@ -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.Drawing

function Save-HeadsetCapture {
    param([Parameter(Mandatory)][string]$Path)
    $p = Get-Process vrcompositor -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'Headset Window' } | Select-Object -First 1
    if (-not $p) { throw 'No SteamVR "Headset Window" to capture.' }
    $h = [WindowCapture]::Grab($p.MainWindowHandle)
    try { $bmp = [System.Drawing.Image]::FromHbitmap($h); $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }
    finally { [void][WindowCapture]::DeleteObject($h) }
    Update-SteamVRLock
    return $Path
}

function Save-GameWindowCapture {
    param([Parameter(Mandatory)][string]$Path)
    # the game's own window (Unity's window class), not BepInEx's console, which can be the process's main window
    $hwnd = [WindowCapture]::FindWindow('UnityWndClass', [NullString]::Value)  # $null would arrive as an empty title
    if ($hwnd -eq [IntPtr]::Zero) { return $null }
    $h = [WindowCapture]::Grab($hwnd)
    try { $bmp = [System.Drawing.Image]::FromHbitmap($h); $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }
    finally { [void][WindowCapture]::DeleteObject($h) }
    return $Path
}

function Backup-CloudpunkSave {
    param([Parameter(Mandatory)][string]$Destination)
    $src = Join-Path $env:USERPROFILE 'AppData\LocalLow\ION LANDS\Cloudpunk'
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    if (Test-Path $src) { Get-ChildItem $src -Force | Copy-Item -Destination $Destination -Recurse -Force }
}

function Restore-CloudpunkSave {
    param([Parameter(Mandatory)][string]$Source)
    $dst = Join-Path $env:USERPROFILE 'AppData\LocalLow\ION LANDS\Cloudpunk'
    foreach ($f in Get-ChildItem $Source -File) {
        Copy-Item $f.FullName (Join-Path $dst $f.Name) -Force
    }
    # anything the run added (new save slots, Unity logs, analytics) goes, so the folder matches the backup
    foreach ($f in Get-ChildItem $dst -Force) {
        if (-not (Test-Path (Join-Path $Source $f.Name))) { Remove-Item $f.FullName -Recurse -Force }
    }
}

Export-ModuleMember -Function *
