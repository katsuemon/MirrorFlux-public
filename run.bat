@echo off
setlocal
cd /d "%~dp0"

set "DOTNET_CMD=dotnet"
if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET_CMD=%ProgramFiles%\dotnet\dotnet.exe"
)

REM Keep the .NET CLI / NuGet cache outside the cloud-synced project folder
set "DOTNET_CLI_HOME=%LOCALAPPDATA%\MirrorFlux\.dotnet"
set "NUGET_PACKAGES=%LOCALAPPDATA%\MirrorFlux\.dotnet\.nuget\packages"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_NOLOGO=1"

echo [1/2] Building MirrorFlux (Debug)...
taskkill /f /im MirrorFlux.App.exe /im mf.exe /im MirrorFlux.exe >nul 2>&1
"%DOTNET_CMD%" build MirrorFlux.csproj -c Debug -nologo -v m
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed! Check errors above.
    pause
    exit /b 1
)

REM The Debug build keeps the internal assembly name; only the package renames it to mf.exe.
REM Never fall back to an older mf.exe: it would hide the build that just succeeded.
set "EXE_PATH=bin\Debug\net10.0-windows\win-x64\MirrorFlux.App.exe"
if not exist "%EXE_PATH%" (
    if exist "bin\Debug\net10.0-windows\MirrorFlux.App.exe" (
        set "EXE_PATH=bin\Debug\net10.0-windows\MirrorFlux.App.exe"
    )
)

if not exist "%EXE_PATH%" (
    echo.
    echo [ERROR] Executable MirrorFlux.App.exe not found!
    pause
    exit /b 1
)

for %%P in ("%EXE_PATH%") do set "EXE_DIR=%%~dpP"
if not exist "%EXE_DIR%mf.ini" (
    if exist "mf.default.ini" (
        copy /y "mf.default.ini" "%EXE_DIR%mf.ini" >nul
    )
)

echo [2/2] Launching MirrorFlux (%EXE_PATH%)...
echo.
echo =======================================================
echo   MirrorFlux started successfully!
echo =======================================================
echo.
start "" "%EXE_PATH%"
exit /b 0
