@echo off
setlocal

echo ==========================================
echo   SlopeX Launcher - build self-contained
echo ==========================================
echo.

rem Prefer a user-local SDK (no admin install required)
set "DOTNET="
if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"

rem Make sure the found dotnet really ships an SDK (runtime-only cannot build)
if defined DOTNET (
    "%DOTNET%" --list-sdks >nul 2>nul
    if errorlevel 1 set "DOTNET="
)

if not defined DOTNET (
    where dotnet >nul 2>nul
    if not errorlevel 1 (
        dotnet --list-sdks >nul 2>nul
        if not errorlevel 1 set "DOTNET=dotnet"
    )
)

if not defined DOTNET (
    echo [ERROR] No .NET SDK found.
    echo         Install .NET 8 SDK: https://dotnet.microsoft.com/download
    echo         Or install without admin rights:
    echo           curl -L -o sdk.zip https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.zip
    echo           unzip sdk.zip into %%USERPROFILE%%\.dotnet
    pause
    exit /b 1
)

echo Using: %DOTNET%
"%DOTNET%" --version
echo.
echo Publishing...
echo.

"%DOTNET%" publish "%~dp0src\SlopeX\SlopeX.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=false ^
    -p:DebugType=none ^
    -o "%~dp0"

if errorlevel 1 (
    echo.
    echo [FAILED] Build did not complete.
    pause
    exit /b 1
)

echo.
echo [OK] Output: %~dp0SlopeX.exe
echo      Run SlopeX.exe (single file, no runtime needed)
echo      Game dir defaults to %~dp0.minecraft
echo.
pause
