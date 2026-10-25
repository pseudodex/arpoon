#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Arpoon-Setup-x64.exe: a self-contained, single-click Windows
    installer. No .NET runtime or Npcap needs to be pre-installed by the user
    (Npcap is checked and offered post-install if missing).

.USAGE
    From the repo root, in PowerShell:
        .\packaging\windows\build_installer.ps1

    Requires the .NET 8 SDK and Inno Setup 6 (both installed automatically via
    winget if missing and winget is available).
#>

param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
$ProjectPath = Join-Path $RepoRoot "src\Arpoon\Arpoon.csproj"

function Ensure-Tool {
    param([string]$Command, [string]$WingetId, [string]$FriendlyName)
    if (Get-Command $Command -ErrorAction SilentlyContinue) { return }

    Write-Host "[*] $FriendlyName not found. Attempting install via winget..." -ForegroundColor Yellow
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install -e --id $WingetId --accept-source-agreements --accept-package-agreements
    }
    if (-not (Get-Command $Command -ErrorAction SilentlyContinue)) {
        throw "$FriendlyName is required but could not be installed automatically. Install it manually and re-run this script."
    }
}

function Find-ISCC {
    $iscc = Get-Command iscc -ErrorAction SilentlyContinue
    if ($iscc) { return $iscc.Source }

    $candidates = @(
        "$Env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${Env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

Write-Host "[1/3] Checking .NET 8 SDK..." -ForegroundColor Cyan
Ensure-Tool -Command "dotnet" -WingetId "Microsoft.DotNet.SDK.8" -FriendlyName ".NET 8 SDK"

Write-Host "[2/3] Publishing self-contained Arpoon.exe (win-x64)..." -ForegroundColor Cyan
dotnet publish $ProjectPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "[3/3] Compiling the installer with Inno Setup..." -ForegroundColor Cyan
$isccPath = Find-ISCC
if (-not $isccPath) {
    Write-Host "[*] Inno Setup not found. Attempting install via winget..." -ForegroundColor Yellow
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install -e --id JRSoftware.InnoSetup --accept-source-agreements --accept-package-agreements
    }
    $isccPath = Find-ISCC
}
if (-not $isccPath) {
    throw "Inno Setup (ISCC.exe) is required but could not be found or installed automatically. " +
          "Install it from https://jrsoftware.org/isdl.php and re-run this script."
}

$issPath = Join-Path $ScriptDir "Arpoon.iss"
& $isccPath "/DMyAppVersion=$Version" $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC compilation failed." }

$outputExe = Join-Path $ScriptDir "dist\Arpoon-Setup-x64.exe"
Write-Host ""
Write-Host "Done! Installer created at:" -ForegroundColor Green
Write-Host "  $outputExe" -ForegroundColor Green
