#Requires -Version 5.1
<#
.SYNOPSIS
    Quick patch script for Simple Windows Screentime
.DESCRIPTION
    Uninstalls, rebuilds, and reinstalls in one step for faster development iteration.
#>

param(
    [switch]$SkipBuild,
    [switch]$SkipDotNetCheck
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-ColorOutput {
    param([string]$Message, [ConsoleColor]$Color = [ConsoleColor]::White)
    $oldColor = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = $Color
    Write-Host $Message
    $host.UI.RawUI.ForegroundColor = $oldColor
}

function Write-Step { param([string]$Message) Write-ColorOutput "`n==> $Message" Cyan }
function Write-Success { param([string]$Message) Write-ColorOutput "[OK] $Message" Green }
function Write-Err { param([string]$Message) Write-ColorOutput "[X] $Message" Red }

function Test-Administrator {
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Main
Clear-Host
Write-Host ""
Write-Host "=================================================================" -ForegroundColor Magenta
Write-Host "          SIMPLE WINDOWS SCREENTIME - PATCH                      " -ForegroundColor Magenta
Write-Host "=================================================================" -ForegroundColor Magenta
Write-Host ""

# Check elevation
if (-not (Test-Administrator)) {
    Write-Err "This script must be run as Administrator"
    Write-Host "Right-click and select 'Run as Administrator'" -ForegroundColor Yellow
    Read-Host "Press Enter to exit"
    exit 1
}

# Step 1: Run uninstall script
Write-Step "Running uninstall..."
"yes" | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$ScriptDir\Uninstall.ps1"

# Step 2: Build (unless skipped)
if (-not $SkipBuild) {
    Write-Step "Building solution..."

    Push-Location $ScriptDir
    try {
        $buildResult = & dotnet build -c Release 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Err "Build failed:"
            Write-Host $buildResult
            Pop-Location
            Read-Host "Press Enter to exit"
            exit 1
        }
        Write-Success "Build complete"

        Write-Step "Publishing executables..."

        # Publish each project
        $projects = @(
            @{ Path = "SimpleWindowsScreentime.Service"; Name = "WinDisplayCalibration.exe" },
            @{ Path = "SimpleWindowsScreentime.Blocker"; Name = "STBlocker.exe" },
            @{ Path = "SimpleWindowsScreentime.ConfigPanel"; Name = "STConfigPanel.exe" },
            @{ Path = "SimpleWindowsScreentime.Debug"; Name = "STDebug.exe" }
        )

        $publishDir = Join-Path $ScriptDir "publish"
        if (-not (Test-Path $publishDir)) {
            New-Item -Path $publishDir -ItemType Directory | Out-Null
        }

        foreach ($proj in $projects) {
            $projPath = Join-Path $ScriptDir $proj.Path
            & dotnet publish $projPath -c Release -r win-x64 --self-contained false -o $publishDir /p:PublishSingleFile=true 2>$null | Out-Null
            if (Test-Path (Join-Path $publishDir $proj.Name)) {
                Write-Success "Published: $($proj.Name)"
            }
            else {
                Write-Err "Failed to publish: $($proj.Name)"
            }
        }
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host "    Skipping build (using existing publish folder)"
}

# Step 3: Install
Write-Step "Installing..."

$installArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptDir\Install.ps1`""
if ($SkipDotNetCheck) {
    $installArgs += " -SkipDotNetCheck"
}

# Run installer inline (we're already elevated)
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$ScriptDir\Install.ps1" -SkipDotNetCheck

Write-Host ""
Write-Host "=================================================================" -ForegroundColor Green
Write-Host "                    PATCH COMPLETE                               " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
Write-Host ""

Read-Host "Press Enter to exit"
