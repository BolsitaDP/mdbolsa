@echo off
REM Launches the mdbolsa desktop shell.
REM
REM There is no portable .exe to double-click: this is a packaged WinUI 3 app, so
REM the Windows App Runtime only resolves for a process that has package identity.
REM Running the raw MdBolsa.Desktop.WinUI.exe fails with REGDB_E_CLASSNOTREG. The
REM winapp CLI (from the Microsoft.Windows.SDK.BuildTools.WinApp package) builds the
REM app, registers a development package and launches it - this script is just that
REM command, with the version looked up for you.
REM
REM Requires Windows Developer Mode (Settings > Privacy & security > For developers).

setlocal

set "CSPROJ=%~dp0src\Client\MdBolsa.Desktop.WinUI\MdBolsa.Desktop.WinUI.csproj"
set "PKGROOT=%USERPROFILE%\.nuget\packages\microsoft.windows.sdk.buildtools.winapp"

if not exist "%CSPROJ%" (
  echo Could not find %CSPROJ%
  pause
  exit /b 1
)

REM Highest installed version of the winapp CLI.
set "WINAPP="
for /f "delims=" %%d in ('dir /b /ad /o-n "%PKGROOT%" 2^>nul') do (
  if exist "%PKGROOT%\%%d\tools\win-x64\winapp.exe" (
    set "WINAPP=%PKGROOT%\%%d\tools\win-x64\winapp.exe"
    goto :found
  )
)

:found
if "%WINAPP%"=="" (
  echo Could not find winapp.exe under "%PKGROOT%".
  echo Run: dotnet restore src\MdBolsa.sln
  pause
  exit /b 1
)

echo Using %WINAPP%
"%WINAPP%" run "%CSPROJ%" -c Debug --arch x64 --detach
if errorlevel 1 (
  echo.
  echo Launch failed. Common causes:
  echo   - Windows Developer Mode is off ^(Settings ^> Privacy ^& security ^> For developers^)
  echo   - the build failed; run "dotnet build src\MdBolsa.sln" to see the errors
  pause
)
endlocal
