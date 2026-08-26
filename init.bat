@echo off
REM Restores this checkout to a working state after `git clean -ffxd`.
REM
REM Entry point for cmd.exe, where a .ps1 cannot be invoked directly. Forwards
REM every argument to init.ps1, so `.\init.bat -Build` works the same way
REM `.\init.ps1 -Build` does from PowerShell.
REM
REM   git clean -ffxd && .\init.bat
REM   git clean -ffxd && .\init.bat -Build
setlocal
set "POWERSHELL=pwsh"
where pwsh >nul 2>&1 || set "POWERSHELL=powershell"
"%POWERSHELL%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0init.ps1" %*
exit /b %ERRORLEVEL%
