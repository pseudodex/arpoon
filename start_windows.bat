@echo off
REM ═══════════════════════════════════════════════════════
REM  Arpoon - Windows Launcher
REM  Launches the built GUI directly. Arpoon.exe's manifest
REM  requests elevation itself (native UAC prompt), and it is
REM  built as WinExe, so no console window appears.
REM ═══════════════════════════════════════════════════════

setlocal

set "APP_EXE=%~dp0src\Arpoon\bin\Release\net8.0\win-x64\Arpoon.exe"
if not exist "%APP_EXE%" set "APP_EXE=%~dp0src\Arpoon\bin\Release\net8.0\Arpoon.exe"

if not exist "%APP_EXE%" (
    echo [ERROR] Arpoon not built. Run install_windows.bat first.
    pause
    exit /b 1
)

start "" "%APP_EXE%"
exit /b 0
