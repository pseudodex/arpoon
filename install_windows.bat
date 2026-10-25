@echo off
REM ═══════════════════════════════════════════════════════
REM  Arpoon - Windows Installer
REM  Installs .NET 8 SDK and Npcap if missing, then builds.
REM ═══════════════════════════════════════════════════════

echo.
echo ╔══════════════════════════════════════╗
echo ║       Arpoon - Windows Installer       ║
echo ╚══════════════════════════════════════╝
echo.

REM ── Check for Administrator ──
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [!] This script requires Administrator privileges.
    echo     Right-click and select "Run as Administrator".
    pause
    exit /b 1
)

REM ── .NET 8 SDK ──
echo [1/3] Checking .NET 8 SDK...
set "PATH=%PATH%;%ProgramFiles%\dotnet"
where dotnet >nul 2>&1
if %errorlevel% equ 0 (
    dotnet --list-sdks | findstr /R "^8\." >nul 2>&1
)
if %errorlevel% neq 0 (
    echo [!] .NET 8 SDK not found. Installing via winget...
    winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements --silent
    if %errorlevel% neq 0 (
        echo [ERROR] Automatic .NET 8 SDK install failed. Install it manually from:
        echo         https://dotnet.microsoft.com/download/dotnet/8.0
        pause
        exit /b 1
    )
    set "PATH=%PATH%;%ProgramFiles%\dotnet"
    where dotnet >nul 2>&1
    if %errorlevel% neq 0 (
        echo [ERROR] .NET SDK installed but not found on PATH. Close this window and re-run the installer.
        pause
        exit /b 1
    )
)
echo [OK] .NET 8 SDK ready.

REM ── Npcap ──
echo [2/3] Checking Npcap...
if exist "C:\Windows\System32\Npcap\wpcap.dll" (
    echo [OK] Npcap found.
) else if exist "C:\Windows\System32\wpcap.dll" (
    echo [OK] WinPcap/Npcap found.
) else (
    echo [!] Npcap not found. Downloading the latest installer...
    set "NPCAP_TMP=%TEMP%\npcap-setup.exe"
    powershell -NoProfile -Command ^
        "$ProgressPreference='SilentlyContinue';" ^
        "$page = Invoke-WebRequest -UseBasicParsing 'https://npcap.com/#download';" ^
        "$ver = ([regex]::Matches($page.Content, 'npcap-(\d+\.\d+)\.exe') | ForEach-Object { [version]$_.Groups[1].Value } | Sort-Object -Descending | Select-Object -First 1).ToString();" ^
        "if (-not $ver) { exit 1 };" ^
        "Invoke-WebRequest -UseBasicParsing \"https://npcap.com/dist/npcap-$ver.exe\" -OutFile '%NPCAP_TMP%'"
    if %errorlevel% neq 0 (
        echo [ERROR] Could not download Npcap automatically. Install it manually from:
        echo         https://nmap.org/npcap/  ^(check "WinPcap API-compatible Mode"^)
        pause
        exit /b 1
    )
    echo     Launching the Npcap installer — please click through it
    echo     ^(check "Install Npcap in WinPcap API-compatible Mode"^), then
    echo     this script will continue automatically once it closes.
    start /wait "" "%NPCAP_TMP%"
    del /q "%NPCAP_TMP%" >nul 2>&1
    if not exist "C:\Windows\System32\Npcap\wpcap.dll" if not exist "C:\Windows\System32\wpcap.dll" (
        echo [ERROR] Npcap still not detected after install. Re-run this script once it's installed.
        pause
        exit /b 1
    )
    echo [OK] Npcap installed.
)

REM ── Build project ──
echo [3/3] Building Arpoon...
cd /d "%~dp0src\Arpoon"
dotnet restore -r win-x64
dotnet build --configuration Release -r win-x64 --self-contained false -p:RuntimeIdentifiers=win-x64

if %errorlevel% neq 0 (
    echo [ERROR] Build failed. Check the errors above.
    pause
    exit /b 1
)

echo.
echo ╔══════════════════════════════════════╗
echo ║    Installation complete! [OK]       ║
echo ╚══════════════════════════════════════╝
echo.
echo Run start_windows.bat to launch Arpoon.
echo Note: Requires Administrator privileges.
echo.
pause
