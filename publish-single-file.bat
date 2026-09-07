@echo off
setlocal
cd /d "%~dp0"
echo ==========================================
echo Publishing VoWin (Single File, Release x64)
echo ==========================================

if exist "publish\win-x64\VoWin.exe" (
    del /f /q "publish\win-x64\VoWin.exe" 2>nul
)

dotnet publish VoWin/VoWin.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64

if %ERRORLEVEL% equ 0 (
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