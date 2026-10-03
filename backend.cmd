@echo off
REM Runs the mdbolsa sync backend (PostgreSQL + the API) on this machine, no Docker.
REM
REM   backend.cmd            start it (double-click works) and wait for /health
REM   backend.cmd stop       stop the API, then PostgreSQL
REM   backend.cmd status     what is running
REM   backend.cmd reset -Force   delete the local development database
REM
REM The real work is scripts\dev-backend.ps1; this only gets past the default
REM PowerShell execution policy. Nothing is installed system-wide: PostgreSQL goes
REM to %LOCALAPPDATA%\MdBolsa.Backend.Dev. See docs\development.md.

setlocal

set "SCRIPT=%~dp0scripts\dev-backend.ps1"
if not exist "%SCRIPT%" (
  echo Could not find %SCRIPT%
  pause
  exit /b 1
)

if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" start
  echo.
  pause
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
)
endlocal
