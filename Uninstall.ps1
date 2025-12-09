#Requires -Version 5.1
<#
.SYNOPSIS
    Uninstaller for Simple Windows Screentime
.DESCRIPTION
    Removes the Screen Time service, blocker, config panel, scheduled tasks, and all data.
    Requires the PIN to be cleared first via ConfigPanel (reset all settings).
#>

$ErrorActionPreference = "Stop"

# Configuration
$ServiceName = "WinDisplayCalibration"
$InstallPath = "$env:ProgramFiles\SimpleWindowsScreentime"
$DataPath = "$env:ProgramData\STGuard"
$ConfigPath = "$DataPath\config.json"
$PipeName = "STG_Pipe_7f3a"

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

function Test-PinCleared {
    # Method 1: Try to query service via named pipe
    try {
        $pipeClient = New-Object System.IO.Pipes.NamedPipeClientStream(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $pipeClient.Connect(3000) # 3 second timeout

        $writer = New-Object System.IO.StreamWriter($pipeClient)
        $reader = New-Object System.IO.StreamReader($pipeClient)

        $writer.AutoFlush = $true
        $writer.WriteLine('{"type":"get_state"}')

        $response = $reader.ReadLine()
        $pipeClient.Close()

        if ($response) {
            $state = $response | ConvertFrom-Json
            if ($state.is_setup_mode -eq $true) {
                return $true
            }
            else {
                return $false
            }
        }
    }
    catch {
        # Service not responding, fall back to config file check
    }

    # Method 2: Check config file directly
    if (Test-Path $ConfigPath) {
        try {
            $config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
            if ([string]::IsNullOrEmpty($config.pin_hash) -or [string]::IsNullOrEmpty($config.pin_salt)) {
                return $true
            }
            else {
                return $false
            }
        }
        catch {
            # Config file unreadable, assume PIN is set for safety
            return $false
        }
    }

    # No config file = no PIN set (fresh install or already cleaned)
    return $true
}

function Test-ServiceInstalled {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    return $null -ne $service
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

function Show-PinRequiredError {
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Red
    Write-Host "                    UNINSTALL BLOCKED                           " -ForegroundColor Red
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Red
    Write-Host ""
    Write-Host "  A PIN is currently set. You must clear your PIN before" -ForegroundColor White
    Write-Host "  uninstalling Screen Time." -ForegroundColor White
    Write-Host ""
    Write-Host "  To uninstall:" -ForegroundColor Cyan
    Write-Host "  1. Run STConfigPanel.exe" -ForegroundColor Yellow
    Write-Host "  2. Enter your PIN to access settings" -ForegroundColor Yellow
    Write-Host "  3. Click 'Reset All Settings' and enter your PIN" -ForegroundColor Yellow
    Write-Host "  4. Run this uninstaller again" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  If you forgot your PIN:" -ForegroundColor Cyan
    Write-Host "  1. Wait for the blocker to appear during block hours" -ForegroundColor Yellow
    Write-Host "  2. Click 'Forgot PIN?' to start 48-hour recovery" -ForegroundColor Yellow
    Write-Host "  3. After recovery completes, run this uninstaller" -ForegroundColor Yellow
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

    # Check/request elevation first
    Request-Elevation

    # Check if anything is installed
    $serviceInstalled = Test-ServiceInstalled
    $installFolderExists = Test-Path $InstallPath
    $dataFolderExists = Test-Path $DataPath

    if (-not $serviceInstalled -and -not $installFolderExists -and -not $dataFolderExists) {
        Write-Info "Screen Time does not appear to be installed."
        Read-Host "Press Enter to exit"
        return
    }

    # Check if PIN is cleared
    Write-Step "Checking PIN status..."

    $pinCleared = Test-PinCleared

    if (-not $pinCleared) {
        Show-PinRequiredError
        Read-Host "Press Enter to exit"
        return
    }

    Write-Success "PIN is cleared - uninstall authorized"

    # Confirm
    Write-Host ""
    $confirm = Read-Host "Are you sure you want to completely remove Screen Time? (yes/no)"
    if ($confirm -ne "yes") {
        Write-Info "Uninstall cancelled"
        Read-Host "Press Enter to exit"
        return
    }

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
