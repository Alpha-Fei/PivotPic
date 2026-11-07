@echo off
rem ================================================================
rem  PivotPic - one-click regression entry (double-click me)
rem
rem  All logic and all Chinese output live in scripts\run-tests.ps1,
rem  because cmd.exe cannot reliably parse a .cmd file containing
rem  non-ASCII text: it loses byte synchronization and splits lines
rem  mid-way (e.g. "dotnet.exe" gets executed as "et.exe"). Keeping
rem  this file strictly ASCII avoids that whole class of bug.
rem
rem  Usage: double-click, or run-tests.cmd -DryRun (flow check only)
rem  Exit code: 0 = all passed, 1 = something failed.
rem ================================================================

chcp 65001 >nul
setlocal

rem .NET 8 SDK lives in the user profile, not on PATH.
set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

set "PS_SCRIPT=%~dp0scripts\run-tests.ps1"

if not exist "%PS_SCRIPT%" goto :no_script

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
set "EXITCODE=%ERRORLEVEL%"
goto :finish

:no_script
echo [X] Missing script: "%PS_SCRIPT%"
set "EXITCODE=1"

:finish
echo.
pause
exit /b %EXITCODE%
