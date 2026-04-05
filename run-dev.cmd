@echo off
setlocal
set SCRIPT_DIR=%~dp0
set "PS_EXE=pwsh"
where pwsh >nul 2>nul
if not "%ERRORLEVEL%"=="0" set "PS_EXE=powershell"
%PS_EXE% -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%run-dev.ps1" %*
exit /b %ERRORLEVEL%
