@echo off
setlocal
rem One-click removal of Cloudpunk VRevival: puts the game files back as Steam installed them and removes the mod.
rem It asks whether to remove BepInEx too. Your saves are never touched. Same as running uninstall.ps1.
set "REMOVE_BEPINEX="
if "%~1"=="" set /p "REMOVE_BEPINEX=Also remove BepInEx? Other BepInEx mods need it. [y/N] "
if /i "%REMOVE_BEPINEX:~0,1%"=="y" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" -RemoveBepInEx
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
)
echo.
pause
