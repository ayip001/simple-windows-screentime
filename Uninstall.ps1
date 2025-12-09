#Requires -Version 5.1
<#
.SYNOPSIS
    Uninstaller for Simple Windows Screentime
.DESCRIPTION
    Removes the Screen Time service, blocker, config panel, scheduled tasks, and all data.
    Requires the -Force flag to proceed (safety measure since this bypasses PIN verification).
.PARAMETER Force
    Required to actually perform the uninstall. Without this flag, the script shows a warning.
#>

param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Configuration
$ServiceName = "WinDisplayCalibration"
$InstallPath = "$env:ProgramFiles\SimpleWindowsScreentime"
$DataPath = "$env:ProgramData\STGuard"

$TaskNames = @("STG_Monitor_1", "STG_Monitor_2", "STG_LogonTrigger", "STG_BootCheck")

function Write-ColorOutput {
    param(
        [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::White
    )
    $oldColor = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = $Color
    Write-Host $Message
    $host.UI.RawUI.ForegroundColor = $oldColor
}

function Write-Success { param([string]$Message) Write-ColorOutput "✓ $Message" Green }
function Write-Warning { param([string]$Message) Write-ColorOutput "⚠ $Message" Yellow }
function Write-Error { param([string]$Message) Write-ColorOutput "✗ $Message" Red }
function Write-Info { param([string]$Message) Write-ColorOutput "  $Message" Cyan }
function Write-Step { param([string]$Message) Write-ColorOutput "`n► $Message" White }

function Test-Administrator {
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Request-Elevation {
    if (-not (Test-Administrator)) {
        Write-Warning "Requesting administrator privileges..."

        $scriptPath = $MyInvocation.PSCommandPath
        if ([string]::IsNullOrEmpty($scriptPath)) {
            $scriptPath = $PSCommandPath
        }

        $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
        if ($Force) {
            $arguments += " -Force"
        }

        try {
            Start-Process PowerShell -Verb RunAs -ArgumentList $arguments -Wait
        }
        catch {
            Write-Error "Failed to elevate. Please run this script as Administrator."
            Read-Host "Press Enter to exit"
            exit 1
        }
        exit 0
    }
}

function Stop-BlockerProcesses {
    Write-Step "Stopping blocker processes..."

    try {
        $processes = Get-Process -Name "STBlocker" -ErrorAction SilentlyContinue
        if ($processes) {
            $processes | Stop-Process -Force
            Write-Success "Blocker processes stopped"
        }
        else {
            Write-Info "No blocker processes running"
        }
    }
    catch {
        Write-Warning "Could not stop blocker processes: $_"
    }
}

function Stop-AndRemoveService {
    Write-Step "Stopping and removing service..."

    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

        if ($service) {
            if ($service.Status -eq 'Running') {
                Write-Info "Stopping service..."
                Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 2
            }

            Write-Info "Removing service..."
            & sc.exe delete $ServiceName | Out-Null
            Start-Sleep -Seconds 2
            Write-Success "Service removed"
        }
        else {
            Write-Info "Service not found"
        }
    }
    catch {
        Write-Warning "Error removing service: $_"
    }
}

function Remove-ScheduledTasks {
    Write-Step "Removing scheduled tasks..."

    foreach ($taskName in $TaskNames) {
        try {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            if ($task) {
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
                Write-Success "Removed task: $taskName"
            }
            else {
                # Try with schtasks as fallback
                & schtasks.exe /Delete /TN $taskName /F 2>$null
                if ($LASTEXITCODE -eq 0) {
                    Write-Success "Removed task: $taskName"
                }
                else {
                    Write-Info "Task not found: $taskName"
                }
            }
        }
        catch {
            Write-Warning "Error removing task $taskName`: $_"
        }
    }
}

function Remove-RegistryEntries {
    Write-Step "Removing registry entries..."

    try {
        # Remove run key
        $runKeyPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
        $value = Get-ItemProperty -Path $runKeyPath -Name "WinDisplayCalibration" -ErrorAction SilentlyContinue
        if ($value) {
            Remove-ItemProperty -Path $runKeyPath -Name "WinDisplayCalibration" -Force
            Write-Success "Removed registry run key"
        }
        else {
            Write-Info "Registry run key not found"
        }
    }
    catch {
        Write-Warning "Error removing registry entries: $_"
    }
}

function Remove-InstallFolder {
    Write-Step "Removing installation folder..."

    if (Test-Path $InstallPath) {
        try {
            # Take ownership first
            Write-Info "Taking ownership of install folder..."
            & takeown.exe /F $InstallPath /R /A /D Y 2>$null | Out-Null
            & icacls.exe $InstallPath /reset /T /Q 2>$null | Out-Null
            & icacls.exe $InstallPath /grant "Administrators:F" /T /Q 2>$null | Out-Null

            Remove-Item -Path $InstallPath -Recurse -Force -ErrorAction Stop
            Write-Success "Installation folder removed"
        }
        catch {
            Write-Warning "Could not fully remove installation folder: $_"
            Write-Info "You may need to manually delete: $InstallPath"
        }
    }
    else {
        Write-Info "Installation folder not found"
    }
}

function Remove-DataFolder {
    Write-Step "Removing data folder..."

    if (Test-Path $DataPath) {
        try {
            # Take ownership first
            Write-Info "Taking ownership of data folder..."
            & takeown.exe /F $DataPath /R /A /D Y 2>$null | Out-Null
            & icacls.exe $DataPath /reset /T /Q 2>$null | Out-Null
            & icacls.exe $DataPath /grant "Administrators:F" /T /Q 2>$null | Out-Null

            Remove-Item -Path $DataPath -Recurse -Force -ErrorAction Stop
            Write-Success "Data folder removed"
        }
        catch {
            Write-Warning "Could not fully remove data folder: $_"
            Write-Info "You may need to manually delete: $DataPath"
        }
    }
    else {
        Write-Info "Data folder not found"
    }
}

function Show-Warning {
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Yellow
    Write-Host "                         WARNING                                " -ForegroundColor Yellow
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  This script will completely remove Simple Windows Screentime" -ForegroundColor White
    Write-Host "  without requiring PIN verification." -ForegroundColor White
    Write-Host ""
    Write-Host "  For normal uninstallation, use the ConfigPanel app to reset" -ForegroundColor Cyan
    Write-Host "  settings with your PIN." -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  To proceed with force uninstall, run:" -ForegroundColor Yellow
    Write-Host "    .\Uninstall.ps1 -Force" -ForegroundColor Green
    Write-Host ""
}

function Show-Summary {
    Write-Host "`n" -NoNewline
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
    Write-Host "                    UNINSTALL COMPLETE                          " -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Simple Windows Screentime has been removed." -ForegroundColor White
    Write-Host ""
}

# Main
function Main {
    Clear-Host
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "          SIMPLE WINDOWS SCREENTIME UNINSTALLER                 " -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""

    if (-not $Force) {
        Show-Warning
        Read-Host "Press Enter to exit"
        return
    }

    # Confirm
    Write-Host ""
    $confirm = Read-Host "Are you sure you want to completely remove Screen Time? (yes/no)"
    if ($confirm -ne "yes") {
        Write-Info "Uninstall cancelled"
        Read-Host "Press Enter to exit"
        return
    }

    # Check/request elevation
    Request-Elevation

    # Perform uninstall
    Stop-BlockerProcesses
    Stop-AndRemoveService
    Remove-ScheduledTasks
    Remove-RegistryEntries
    Remove-InstallFolder
    Remove-DataFolder

    Show-Summary

    Read-Host "Press Enter to exit"
}

# Run main
Main
