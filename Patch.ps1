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

# Step 1: Quick uninstall (force, no PIN check)
Write-Step "Stopping service and processes..."

# Stop service first (before killing processes, as service may restart blocker)
$service = Get-Service -Name "WinDisplayCalibration" -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq 'Running') {
        Write-Host "    Stopping service..."
        Stop-Service -Name "WinDisplayCalibration" -Force -ErrorAction SilentlyContinue
        # Wait for service to actually stop
        $timeout = 10
        while ($timeout -gt 0) {
            $service = Get-Service -Name "WinDisplayCalibration" -ErrorAction SilentlyContinue
            if (-not $service -or $service.Status -eq 'Stopped') { break }
            Start-Sleep -Seconds 1
            $timeout--
        }
    }
    # Delete service
    & sc.exe delete "WinDisplayCalibration" 2>$null | Out-Null
    Start-Sleep -Seconds 1
    Write-Success "Service stopped and removed"
}
else {
    Write-Host "    Service not found (clean install)"
}

# Kill any remaining processes
$processNames = @("STBlocker", "STConfigPanel", "WinDisplayCalibration")
foreach ($procName in $processNames) {
    $procs = Get-Process -Name $procName -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        # Wait for process to actually terminate
        Start-Sleep -Milliseconds 500
    }
}
Write-Success "Processes terminated"

# Remove scheduled tasks
$taskNames = @("STG_Monitor", "STG_LogonTrigger", "STG_BootCheck")
foreach ($taskName in $taskNames) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
}
Write-Success "Scheduled tasks removed"

# Remove installation folder (take ownership first to ensure we can delete locked files)
$InstallPath = "$env:ProgramFiles\SimpleWindowsScreentime"
if (Test-Path $InstallPath) {
    Write-Host "    Removing installation folder..."
    try {
        & takeown.exe /F $InstallPath /R /A /D Y 2>$null | Out-Null
        & icacls.exe $InstallPath /reset /T /Q 2>$null | Out-Null
        & icacls.exe $InstallPath /grant "Administrators:F" /T /Q 2>$null | Out-Null
        Remove-Item -Path $InstallPath -Recurse -Force -ErrorAction Stop
        Write-Success "Installation folder removed"
    }
    catch {
        Write-Err "Could not remove installation folder: $_"
    }
}

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
