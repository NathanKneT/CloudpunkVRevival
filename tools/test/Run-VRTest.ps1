# Plays a scenario in the game through SteamVR's null headset (no real headset needed), or flat with -NoHeadset.
#   tools\test\Run-VRTest.ps1 -Steps tests\steps\01-boot.txt
# Needs PowerShell 7 and a -Dev build of the mod installed (tools\build.ps1 -Dev, then tools\install.ps1).
# It takes over the screen and closes the game and SteamVR at the end, so don't run it while playing.
#
# A step file has one step per line, # for comments:
#   wait <seconds>                  sleep
#   waitlog <regex> [timeout]       wait until the BepInEx log matches (the run fails on timeout)
#   cmd <devcommand...>             send a dev command to the mod (see src/CloudpunkVR/DevCommands.cs)
#   capture <name>                  save SteamVR's headset window to tests/captures/<name>.png
#   mirror <name>                   save the game's own window (the left eye mirror) to <name>-mirror.png
#   unitylog <regex>                count the lines of Unity's own log (output_log.txt) that match so far
# ${Name} in a step comes from -Vars Name=value; ${Width}, ${Height} and ${Stamp} are always there.
# BepInEx's and Unity's logs are copied to tests/logs after the run.
# Your mod settings, save folder, SteamVR settings and free camera shots are always put back, even when a step fails.
#
# Renders: -Width 2560 -Height 1440 (and -Fullscreen) set the game's window and the mod keeps it that size, even if
# the game's own options change it; -Shots <folder> brings free camera shot files in for the run:
#   tools\test\Run-VRTest.ps1 -Steps tests\steps\tools-and-probes\67-render.txt -NoHeadset -Width 2560 -Height 1440 -Shots <your shots folder> -Vars Shot=<shot name>
#
# Several tools testing on one PC: -SharedDir (or CLOUDPUNKVR_SHARED_DIR) is a folder with a lock and a reference
# copy of the SteamVR settings, see NullHeadset.psm1.
#Requires -Version 7
param(
    [Parameter(Mandatory)][string]$Steps,
    [string]$GameDir,
    [string]$CaptureDir = (Join-Path $PSScriptRoot '..\..\tests\captures'),
    [string]$SharedDir = $env:CLOUDPUNKVR_SHARED_DIR,
    [string]$OriginalSha = $env:CLOUDPUNKVR_SETTINGS_SHA,
    [switch]$NoHeadset,
    [switch]$ViaSteam,      # start through Steam like a player would (steam://rungameid), e.g. to catch the Desktop Game Theatre
    [ValidateSet('Auto', 'Always', 'Never', '')][string]$StartInVR = '',
    [int]$StartupTimeout = 180,
    # Bring your own save to start from (a folder of .sav files), so a run doesn't depend on where you last saved.
    [string]$SaveFixture = $env:CLOUDPUNKVR_SAVE_FIXTURE,
    # The game's window (Unity's -screen-width, -screen-height, -screen-fullscreen). Given, the mod keeps that size for
    # the run (--keep-screen-size) so saved frames come out exactly that big; left out, the game's own setting wins.
    [ValidateRange(320, 7680)][int]$Width = 1280,
    [ValidateRange(240, 4320)][int]$Height = 720,
    [switch]$Fullscreen,
    # A folder of free camera shots (*.json), copied into BepInEx\config\CloudpunkVR-shots for the run and put back after.
    [string]$Shots,
    # Values for ${Name} in the steps: -Vars Shot=my-shot (several: -Vars Shot=a,Other=b).
    [string[]]$Vars = @()
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NullHeadset.psm1') -Force

# check the steps' ${...} values up front, not half way through a run
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stepLines = @(Get-Content $Steps)
$values = @{ Width = "$Width"; Height = "$Height"; Stamp = $stamp }
foreach ($v in $Vars) {
    if ($v -notmatch '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') { throw "-Vars: '$v' is not Name=value." }
    $values[$Matches[1]] = $Matches[2].Trim()
}
$missing = @(foreach ($l in $stepLines) {
    if ($l.Trim().StartsWith('#')) { continue }
    foreach ($m in [regex]::Matches($l, '\$\{(\w+)\}')) { if (-not $values.ContainsKey($m.Groups[1].Value)) { $m.Groups[1].Value } }
}) | Sort-Object -Unique
if ($missing) { throw "The steps use $(($missing | ForEach-Object { '${' + $_ + '}' }) -join ', '): give a value with -Vars Name=value." }
# resolved now: PowerShell names are case-blind, so $shots below (the game's shots folder) is the same variable as -Shots
$shotsFrom = $null
if ($Shots) {
    if (-not (Test-Path $Shots -PathType Container)) { throw "-Shots: no folder $Shots." }
    $shotsFrom = (Resolve-Path $Shots).Path
}
$keepSize = $PSBoundParameters.ContainsKey('Width') -or $PSBoundParameters.ContainsKey('Height') -or $Fullscreen
if ($ViaSteam -and $keepSize) { Write-Warning '-Width, -Height and -Fullscreen do nothing with -ViaSteam: Steam starts the game with its own options.' }

# a run closes the game and SteamVR, so it never starts while someone may be playing
if (Get-Process Cloudpunk -ErrorAction SilentlyContinue) { throw 'Cloudpunk is running (someone may be playing). Close it before testing.' }
if (-not $NoHeadset -and (Get-Process vrserver, vrmonitor, vrcompositor -ErrorAction SilentlyContinue)) {
    throw 'SteamVR is running (a headset may be in use). Close it before testing.'
}

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $GameDir) {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    $GameDir = Join-Path $(if ($steam) { [System.IO.Path]::GetFullPath($steam) } else { 'C:\Program Files (x86)\Steam' }) 'steamapps\common\Cloudpunk'
}
$saveBak  = Join-Path $root ".backups\save-$stamp"
$log      = Join-Path $GameDir 'BepInEx\LogOutput.log'
$cfg      = Join-Path $GameDir 'BepInEx\config\CloudpunkVR.cfg'
$devcmd   = Join-Path $GameDir 'BepInEx\config\CloudpunkVR.devcmd'
# while this file exists the mod blocks Steam achievements (`goto` can enter districts ahead of the story). A file,
# because a game started by Steam (-ViaSteam) gets none of our environment. Always deleted after.
$bench    = Join-Path $GameDir 'BepInEx\config\CloudpunkVR.bench'
$launched = Get-Date
$appid    = Join-Path $GameDir 'steam_appid.txt'
$testLogs = Join-Path $root "tests\logs"
# Unity's own log: some of its warnings (field of view, render viewport scale) never reach BepInEx's
$unityLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ion Lands\Cloudpunk\output_log.txt'
New-Item -ItemType Directory -Force $CaptureDir, $testLogs | Out-Null
Initialize-NullHeadset -BackupDir (Join-Path $root '.backups') -SharedDir $SharedDir -OriginalSha $OriginalSha

function Wait-Log([string]$Pattern, [int]$Timeout = 120) {
    $deadline = (Get-Date).AddSeconds($Timeout)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern $Pattern -Quiet)) { return $true }
        if (-not (Get-Process Cloudpunk -ErrorAction SilentlyContinue)) { throw "Game exited while waiting for '$Pattern'" }
        Start-Sleep -Milliseconds 500
        Update-SteamVRLock
        Update-RunOwner
    }
    throw "Timed out waiting for '$Pattern'"
}

# The run needs the dev command file on, a known start mode (VR, flat for -NoHeadset, or -StartInVR to test that
# setting) and the free camera's keys off (nobody's at the keyboard, the real mouse would fly it). Yours come back after.
# Sets one key in its own section (several sections have an Enabled), keeping the file's line ends.
function Set-CfgValue([string]$Text, [string]$Key, [string]$Value, [string]$Section = 'General') {
    $nl = if ($Text -match "`r`n") { "`r`n" } else { "`n" }
    $head = [regex]::Match($Text, "(?m)^\[$([regex]::Escape($Section))\][ \t]*(?=\r?$)")
    if (-not $head.Success) { return $Text.TrimEnd() + "$nl$nl[$Section]$nl$Key = $Value$nl" }
    $start = $head.Index + $head.Length
    $next = [regex]::Match($Text.Substring($start), '(?m)^\[')
    $end = if ($next.Success) { $start + $next.Index } else { $Text.Length }
    $body = $Text.Substring($start, $end - $start)
    $found = [regex]::Match($body, "(?m)^$([regex]::Escape($Key)) = [^\r\n]*")
    $body = if ($found.Success) { $body.Substring(0, $found.Index) + "$Key = $Value" + $body.Substring($found.Index + $found.Length) } else { "$nl$Key = $Value" + $body }
    return $Text.Substring(0, $start) + $body + $Text.Substring($end)
}
# Your settings are kept aside in CloudpunkVR.cfg.before-test while a run changes them, with the run's process (id and
# start time) beside it. A copy left by a run that died (PC crash, console closed) goes back first, the dead run's
# settings kept in .backups; while that run is still going, this one doesn't start. The mod does the same check when
# you launch the game (CloudpunkVR.Patcher, TestSettings.cs).
$cfgPending = "$cfg.before-test"
$cfgOwner = "$cfg.test-owner"
function Get-RunOwner([string]$OwnerFile) {
    # the run holding the settings, if it's still going: a PowerShell process with the same id and start time. Not this
    # one (a run in this console that couldn't clean up), and none after 6 h without a refresh.
    try {
        if (-not (Test-Path $OwnerFile)) { return $null }
        if (((Get-Date) - (Get-Item $OwnerFile).LastWriteTime).TotalHours -gt 6) { return $null }
        $o = (Get-Content $OwnerFile -Raw).Trim() -split '\s+'
        if ($o.Count -lt 2) { return $null }
        $id = [int]$o[0]
        if ($id -eq $PID) { return $null }
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $p -or $p.ProcessName -notin 'powershell', 'pwsh') { return $null }
        # start time unreadable: a PowerShell with that id counts as the run
        try { if ([math]::Abs($p.StartTime.ToUniversalTime().Ticks - [long]$o[1]) -gt [TimeSpan]::TicksPerSecond) { return $null } } catch { }
        return "process $id ($($p.ProcessName))"
    }
    catch { return $null }
}
# the owner file is this run's (it starts with this process's id)
function Test-OwnRun { try { return (Test-Path $cfgOwner) -and ((Get-Content $cfgOwner -Raw) -like "$PID *") } catch { return $false } }
function Update-RunOwner { if (Test-OwnRun) { try { (Get-Item $cfgOwner).LastWriteTime = Get-Date } catch { } } }
function Remove-RunOwner { if (Test-OwnRun) { Remove-Item $cfgOwner -Force -ErrorAction SilentlyContinue } }
# a game started after the run closed its own may have read the test's settings: warn once each, never close it
$warnedGames = @{}
function Write-GamesStartedSinceKill {
    $since = if ($script:killedAt) { $script:killedAt } else { Get-Date }
    foreach ($p in @(Get-Process Cloudpunk -ErrorAction SilentlyContinue)) {
        if ($script:warnedGames.ContainsKey($p.Id)) { continue }
        $started = try { $p.StartTime } catch { $null }
        if ($started -and $started -lt $since) { continue }
        $script:warnedGames[$p.Id] = $true
        Write-Warning "Cloudpunk (process $($p.Id)) started while the run was ending: it may have read the test's settings. Restart it to get yours."
    }
}
# a copy cut short by a crash (empty, or NUL bytes) is never put back: returns why, or null when it's fine
function Test-CfgCopy([string]$Path) {
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        if ($bytes.Length -eq 0) { return 'it is empty' }
        if ([Array]::IndexOf($bytes, [byte]0) -ge 0) { return 'it holds NUL bytes' }
        if ([Text.Encoding]::UTF8.GetString($bytes) -notmatch '(?m)^\[General\]') { return 'it has no [General] section' }
        return $null
    }
    catch { return "it cannot be read ($($_.Exception.Message))" }
}
function Read-CfgValues([string]$Path) {
    $kv = @{}
    $section = ''
    foreach ($l in Get-Content $Path) {
        if ($l -match '^\[(.+)\]\s*$') { $section = $Matches[1]; continue }
        if ($l -match '^([^#\s][^=]*?)\s*=\s*(.*)$') { $kv["[$section] $($Matches[1])"] = $Matches[2].Trim() }
    }
    return $kv
}
if (Test-Path $cfgPending) {
    $owner = Get-RunOwner $cfgOwner
    if ($owner) { throw "Another test run is going ($owner): it holds the mod settings ($cfgPending). Run this one after it." }
    $broken = Test-CfgCopy $cfgPending
    if ($broken) { throw "The kept mod settings $cfgPending look damaged ($broken), so they are not put back: compare it with CloudpunkVR.cfg by hand, keep the right one as CloudpunkVR.cfg and delete $cfgPending before testing." }
    # left by a dead run: its settings kept aside and listed, yours put back, its last commands dropped
    if (Test-Path $cfg) {
        $replaced = Join-Path $root ".backups\CloudpunkVR.cfg.replaced-$stamp"
        New-Item -ItemType Directory -Force (Split-Path $replaced) | Out-Null
        Copy-Item $cfg $replaced -Force
        $mine = Read-CfgValues $cfgPending
        $left = Read-CfgValues $cfg
        $changed = @(foreach ($k in $left.Keys) { if ($mine[$k] -ne $left[$k]) { "$k = $($left[$k]) (yours: $($mine[$k]))" } }) | Sort-Object
        Write-Warning "A run stopped before its cleanup had left its settings (kept in $replaced): $(if ($changed) { $changed -join '; ' } else { 'none differ' })."
    }
    Copy-Item $cfgPending $cfg -Force
    Remove-Item $cfgPending -Force
    Remove-Item $cfgOwner -Force -ErrorAction SilentlyContinue
    if (Test-Path $devcmd) { Remove-Item $devcmd -Force; Write-Warning 'Its last developer commands (CloudpunkVR.devcmd) dropped.' }
    Write-Warning "Mod settings put back from $cfgPending."
}
# checked again: a run could have started the game meanwhile
if (Get-Process Cloudpunk -ErrorAction SilentlyContinue) { throw 'Cloudpunk is running (someone may be playing, or another run started). Close it before testing.' }
$shots    = Join-Path $GameDir 'BepInEx\config\CloudpunkVR-shots'
$shotsBak = Join-Path $root ".backups\shots-$stamp"
$takes    = Join-Path $GameDir 'BepInEx\CloudpunkVR-takes'
$takesBefore = if (Test-Path $takes) { @(Get-ChildItem $takes -Directory | ForEach-Object Name) } else { @() }
$shotsExisted = Test-Path $shots
$shotsSaved = $false

if (-not $NoHeadset) { Enter-SteamVRLock }
$createdAppId = $false
$killedAt = $null
try {
    # inside the try: whatever fails from here, the cleanup puts your settings back
    if (Test-Path $cfg) {
        # the owner first, then your copy (never over one already there), so there's never a copy without an owner
        "$PID $((Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks)" | Set-Content -Path $cfgOwner -NoNewline
        if (-not (Test-Path $cfgPending)) { Copy-Item $cfg $cfgPending }
        $t = Set-CfgValue (Get-Content $cfgPending -Raw) 'CommandFile' 'true' 'Developer'
        $mode = if ($StartInVR) { $StartInVR } elseif ($NoHeadset) { 'Never' } else { 'Always' }
        $t = Set-CfgValue $t 'StartInVR' $mode
        $t = Set-CfgValue $t 'Enabled' 'false' 'FreeCam'
        Set-Content -Path $cfg -Value $t -NoNewline
    }
    Backup-CloudpunkSave -Destination $saveBak
    Write-Host "Save backed up to $saveBak"
    # free camera shots come back as they were (last.json is yours); takes go to the captures
    if ($shotsExisted) { Copy-Item $shots $shotsBak -Recurse -ErrorAction Stop; $shotsSaved = $true; Write-Host "Free camera shots backed up to $shotsBak" }
    if ($shotsFrom) {
        $from = $shotsFrom
        $files = @(Get-ChildItem $from -File -Filter *.json)
        if (-not $files) { throw "-Shots: no .json shot in $from." }
        New-Item -ItemType Directory -Force $shots | Out-Null
        $files | Copy-Item -Destination $shots -Force
        Write-Host "Shots for this run, from $from`: $(($files | ForEach-Object BaseName) -join ', ')"
    }
    if ($SaveFixture -and (Test-Path $SaveFixture)) {
        $saveDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\ION LANDS\Cloudpunk'
        Get-ChildItem $SaveFixture -File -Filter *.sav | Copy-Item -Destination $saveDir -Force
        Write-Host "Starting from the test save in $SaveFixture"
    }
    if (-not $NoHeadset) {
        Start-NullHeadset | Out-Null
        Write-Host 'SteamVR null headset is up.'
    }

    Remove-Item $log -ErrorAction SilentlyContinue
    Get-Date -Format o | Set-Content $bench
    if ($ViaSteam) {
        Start-Process 'steam://rungameid/746850'
        try {
            $until = (Get-Date).AddSeconds(90)
            while (-not (Get-Process Cloudpunk -ErrorAction SilentlyContinue)) {
                if ((Get-Date) -gt $until) { throw 'Steam did not start Cloudpunk within 90 s.' }
                Start-Sleep -Seconds 2
                Update-SteamVRLock
            }
            Wait-Log 'Chainloader startup complete' $StartupTimeout | Out-Null
        }
        catch {
            # whatever Steam shows instead (a prompt, the Theatre) is in the headset view
            if (-not $NoHeadset) { try { Save-HeadsetCapture -Path (Join-Path $CaptureDir 'steam-launch-failed.png') | Out-Null } catch { } }
            throw
        }
    }
    else {
        # lets Cloudpunk.exe start directly instead of relaunching itself through Steam
        if (-not (Test-Path $appid)) { '746850' | Set-Content $appid -NoNewline; $createdAppId = $true }
        $screen = @('-screen-fullscreen', $(if ($Fullscreen) { '1' } else { '0' }), '-screen-width', "$Width", '-screen-height', "$Height")
        if ($keepSize) { $screen += '--keep-screen-size' }
        Write-Host "Game window: $Width x $Height $(if ($Fullscreen) { 'full screen' } else { 'windowed' })$(if ($keepSize) { ', kept by the mod' } else { ', or what the game sets' })"
        Start-Process -FilePath (Join-Path $GameDir 'Cloudpunk.exe') -WorkingDirectory $GameDir -ArgumentList $screen
        Wait-Log 'Chainloader startup complete' $StartupTimeout | Out-Null
    }

    foreach ($raw in $stepLines) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        # ${Name} values, case-blind like the check above
        $line = [regex]::Replace($line, '\$\{(\w+)\}', [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $values[$m.Groups[1].Value] })
        $parts = $line -split '\s+', 2
        Write-Host "> $line"
        switch ($parts[0]) {
            'wait'    { Start-Sleep -Seconds ([double]$parts[1]); Update-SteamVRLock; Update-RunOwner }
            'waitlog' { $a = $parts[1] -split '\s+(?=\d+$)'; Wait-Log $a[0] ($(if ($a.Count -gt 1) { [int]$a[1] } else { 120 })) | Out-Null }
            'cmd'     { Add-Content -Path $devcmd -Value $parts[1]; Start-Sleep -Milliseconds 800 }
            'capture' { if ($NoHeadset) { Write-Host '  (no headset window)' } else { $p = Save-HeadsetCapture -Path (Join-Path $CaptureDir "$($parts[1]).png"); Write-Host "  captured $p" } }
            'mirror'  { $p = Save-GameWindowCapture -Path (Join-Path $CaptureDir "$($parts[1])-mirror.png"); Write-Host "  mirror $p" }
            'unitylog' {
                $n = if (Test-Path $unityLog) { @(Select-String -Path $unityLog -Pattern $parts[1]).Count } else { 0 }
                $line = "unitylog at $(Get-Date -Format HH:mm:ss): $n line(s) match '$($parts[1])'"
                Write-Host "  $line"
                Add-Content -Path (Join-Path $testLogs "unitylog-$stamp.txt") -Value $line
            }
            default   { Write-Warning "Unknown step: $line" }
        }
    }
}
finally {
    # every cleanup step runs even if an earlier one fails, so nothing is left switched over
    $cleanup = @(
        # the time first: a game started after this isn't one the run closed
        { $script:killedAt = Get-Date; Get-Process Cloudpunk -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 },
        { if (Test-Path $bench) { Remove-Item $bench -ErrorAction Stop } },
        # your settings first, so they're back even if the cleanup stops half way
        {
            if (Test-Path $cfgPending) {
                $broken = Test-CfgCopy $cfgPending
                if ($broken) { Write-Warning "The kept mod settings $cfgPending look damaged ($broken): NOT put back. CloudpunkVR.cfg still holds the test's; compare the two by hand." }
                else {
                    Copy-Item $cfgPending $cfg -Force
                    Remove-Item $cfgPending -Force
                    Write-Host 'Mod settings restored.'
                }
            }
            if (-not (Test-Path $cfgPending)) { Remove-RunOwner }
        },
        # a game started before the settings came back read the test's
        { Write-GamesStartedSinceKill },
        { Remove-Item $devcmd -ErrorAction SilentlyContinue },
        { if (Test-Path $log) { Copy-Item $log (Join-Path $testLogs "LogOutput-$stamp.log") } },
        { if (Test-Path $unityLog) { Copy-Item $unityLog (Join-Path $testLogs "output_log-$stamp.txt") } },
        { if (-not $NoHeadset) { Stop-SteamVR } },
        { if (-not $NoHeadset) { $sha = Restore-SteamVRSettings; if ($sha) { Write-Host "steamvr.vrsettings restored, sha256 $($sha.Substring(0,16))" } } },
        { if (Test-Path $saveBak) { Restore-CloudpunkSave -Source $saveBak; Write-Host 'Save folder restored.' } },
        {
            # district maps written by `spots` go to the captures before the shots folder is put back
            $spots = @(Get-ChildItem $shots -Filter 'spots-*.json' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $launched })
            if ($spots.Count -gt 0) {
                $dest = Join-Path $CaptureDir "spots-$stamp"
                New-Item -ItemType Directory -Force $dest | Out-Null
                $spots | Copy-Item -Destination $dest
                Write-Host "District spots copied to $dest"
            }
        },
        {
            # only when your own shots are safe in the backup, or there were none
            if ($shotsSaved -or -not $shotsExisted) {
                if (Test-Path $shots) { Remove-Item $shots -Recurse -Force }
                if ($shotsSaved) { Copy-Item $shotsBak $shots -Recurse; Write-Host 'Free camera shots restored.' }
            }
        },
        {
            $new = if (Test-Path $takes) { @(Get-ChildItem $takes -Directory | Where-Object { $takesBefore -notcontains $_.Name }) } else { @() }
            if ($new.Count -gt 0) {
                $dest = Join-Path $CaptureDir "takes-$stamp"
                New-Item -ItemType Directory -Force $dest | Out-Null
                $new | Move-Item -Destination $dest
                if (-not (Get-ChildItem $takes -Force)) { Remove-Item $takes }
                Write-Host "Free camera takes moved to $((Resolve-Path $dest).Path)"
                foreach ($d in Get-ChildItem $dest -Directory) {
                    $images = @(Get-ChildItem $d.FullName -File | Where-Object { $_.Extension -in '.png', '.jpg' }).Count
                    Write-Host "  $($d.Name): $images image(s)$(if (Test-Path (Join-Path $d.FullName 'relight-cues.txt')) { ', relight-cues.txt' })"
                }
            }
        },
        { if ($createdAppId) { Remove-Item $appid -ErrorAction SilentlyContinue } },
        { if (-not $NoHeadset) { Exit-SteamVRLock; Write-Host 'Lock released.' } },
        {
            # settings still aside (putting them back failed): let go of them so the game's next start or the next run
            # puts them back, unless the copy is damaged
            if (Test-Path $cfgPending) {
                Remove-RunOwner
                $broken = Test-CfgCopy $cfgPending
                if ($broken) { Write-Warning "The mod settings kept in ${cfgPending} look damaged ($broken): neither the next run nor the game puts them back. Compare it with CloudpunkVR.cfg by hand, keep the right one as CloudpunkVR.cfg and delete ${cfgPending}." }
                else { Write-Warning "The mod settings are still aside in ${cfgPending}: the next run, or the game's next start, puts them back." }
            }
            # the test's settings stayed in place through the cleanup: warn about a game started meanwhile too
            Write-GamesStartedSinceKill
        }
    )
    foreach ($step in $cleanup) {
        try { & $step } catch { Write-Warning "Cleanup step failed: $($_.Exception.Message)" }
    }
}
