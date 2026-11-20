@echo off
rem One-click install of Cloudpunk VRevival: finds Cloudpunk in your Steam libraries, installs BepInEx 6 if needed
rem (it asks before downloading) and copies the mod. Same as running install.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
