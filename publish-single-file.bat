@echo off
setlocal
cd /d "%~dp0"
echo ==========================================
echo Publishing VoWin (Single File, Release x64)
echo ==========================================

if "%~1"=="" (
    echo [ERROR] Please provide the release version, for example:
    echo         publish-single-file.bat 1.2.3
    exit /b 2
)

set "VOWIN_VERSION=%~1"

pwsh -NoProfile -File packaging/Prepare-RuntimeBundle.ps1
if errorlevel 1 exit /b 1

dotnet publish VoWin/VoWin.csproj -c Release -r win-x64 --self-contained false -p:Version=%VOWIN_VERSION% -p:VoWinRuntimeBundle="%~dp0packaging\runtime.zip" -o publish/win-x64

if %ERRORLEVEL% equ 0 (
    for %%F in ("publish\win-x64\VoWin.exe") do if %%~zF GTR 33554432 (
        echo [ERROR] VoWin.exe exceeded the 32 MiB release size budget.
        exit /b 1
    )
    echo.
    echo ==========================================
    echo [SUCCESS] Single-file executable ready!
    echo ==========================================
    for %%F in ("publish\win-x64\VoWin.exe") do (
        echo Target File: %%~fF
        echo File Size  : %%~zF bytes
        echo Time Stamp : %%~tF
    )
    echo ==========================================
) else (
    echo [ERROR] Publish failed with code %ERRORLEVEL%
)
