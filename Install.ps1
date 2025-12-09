#Requires -Version 5.1
<#
.SYNOPSIS
    Installer for Simple Windows Screentime
.DESCRIPTION
    Installs the Screen Time service, blocker, and config panel applications.
    Automatically requests elevation if not running as administrator.
#>

param(
    [switch]$SkipDotNetCheck
)

$ErrorActionPreference = "Stop"

# Configuration
$ServiceName = "WinDisplayCalibration"
$ServiceDisplayName = "Windows Display Calibration Service"
$ServiceDescription = "Manages display calibration and color profiles"
$InstallPath = "$env:ProgramFiles\SimpleWindowsScreentime"
$DataPath = "$env:ProgramData\STGuard"
$BackupPath = "$DataPath\backup"

# File names
$ServiceExe = "WinDisplayCalibration.exe"
$BlockerExe = "STBlocker.exe"
$ConfigPanelExe = "STConfigPanel.exe"

# .NET 8 Desktop Runtime download URL (x64)
$DotNet8Url = "https://download.visualstudio.microsoft.com/download/pr/907765b0-2bf8-494e-93aa-5ef9571c9f10/a9308e540e804aeb8ed927394e8c2b57/windowsdesktop-runtime-8.0.11-win-x64.exe"

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
        if ($SkipDotNetCheck) {
            $arguments += " -SkipDotNetCheck"
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

function Test-DotNet8Installed {
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
        if ($LASTEXITCODE -eq 0 -and $runtimes) {
            $desktopRuntime = $runtimes | Where-Object { $_ -match "Microsoft\.WindowsDesktop\.App 8\." }
            return $null -ne $desktopRuntime
        }
    }
    catch {}

    # Also check registry as backup
    $regPath = "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
    if (Test-Path $regPath) {
        $versions = Get-ChildItem $regPath -ErrorAction SilentlyContinue
        if ($versions | Where-Object { $_.PSChildName -match "^8\." }) {
            return $true
        }
    }

    return $false
}

function Install-DotNet8 {
    Write-Step "Checking .NET 8 Desktop Runtime..."

    if ($SkipDotNetCheck) {
        Write-Info "Skipping .NET check as requested"
        return $true
    }

    if (Test-DotNet8Installed) {
        Write-Success ".NET 8 Desktop Runtime is installed"
        return $true
    }

    Write-Warning ".NET 8 Desktop Runtime not found. Required for installation."
    Write-Info "Downloading .NET 8 Desktop Runtime installer..."

    $tempPath = "$env:TEMP\dotnet8-runtime-installer.exe"

    try {
        # Download with progress
        $webClient = New-Object System.Net.WebClient
        $webClient.DownloadFile($DotNet8Url, $tempPath)

        Write-Success "Download complete"
        Write-Info "Running .NET 8 installer... Please complete the installation wizard."

        $process = Start-Process -FilePath $tempPath -ArgumentList "/install", "/quiet", "/norestart" -Wait -PassThru

        if ($process.ExitCode -eq 0 -or $process.ExitCode -eq 3010) {
            Write-Success ".NET 8 Desktop Runtime installed successfully"

            # Verify installation
            if (Test-DotNet8Installed) {
                return $true
            }
        }

        # If silent install failed, try interactive
        Write-Warning "Silent installation did not complete. Launching interactive installer..."
        Start-Process -FilePath $tempPath -Wait

        if (Test-DotNet8Installed) {
            Write-Success ".NET 8 Desktop Runtime installed successfully"
            return $true
        }

        Write-Error ".NET 8 installation failed or was cancelled"
        return $false
    }
    catch {
        Write-Error "Failed to download or install .NET 8: $_"
        return $false
    }
    finally {
        if (Test-Path $tempPath) {
            Remove-Item $tempPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Stop-ExistingService {
    Write-Step "Stopping existing service if present..."

    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service) {
            if ($service.Status -eq 'Running') {
                Stop-Service -Name $ServiceName -Force -ErrorAction Stop
                Write-Success "Service stopped"
            }
            else {
                Write-Info "Service was not running"
            }
        }
        else {
            Write-Info "No existing service found"
        }
    }
    catch {
        Write-Warning "Could not stop service: $_"
    }

    # Kill any running blocker processes
    Get-Process -Name "STBlocker" -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Create-Directories {
    Write-Step "Creating installation directories..."

    try {
        if (-not (Test-Path $InstallPath)) {
            New-Item -Path $InstallPath -ItemType Directory -Force | Out-Null
            Write-Success "Created: $InstallPath"
        }
        else {
            Write-Info "Directory exists: $InstallPath"
        }

        if (-not (Test-Path $DataPath)) {
            New-Item -Path $DataPath -ItemType Directory -Force | Out-Null
            Write-Success "Created: $DataPath"
        }
        else {
            Write-Info "Directory exists: $DataPath"
        }

        if (-not (Test-Path $BackupPath)) {
            New-Item -Path $BackupPath -ItemType Directory -Force | Out-Null
            Write-Success "Created: $BackupPath"
        }
        else {
            Write-Info "Directory exists: $BackupPath"
        }

        return $true
    }
    catch {
        Write-Error "Failed to create directories: $_"
        return $false
    }
}

function Copy-ProgramFiles {
    Write-Step "Copying program files..."

    $scriptDir = Split-Path -Parent $MyInvocation.PSCommandPath
    if ([string]::IsNullOrEmpty($scriptDir)) {
        $scriptDir = $PSScriptRoot
    }
    if ([string]::IsNullOrEmpty($scriptDir)) {
        $scriptDir = Get-Location
    }

    $publishDir = Join-Path $scriptDir "publish"

    # Check if publish directory exists
    if (-not (Test-Path $publishDir)) {
        # Try looking for files in script directory
        $publishDir = $scriptDir
    }

    $files = @($ServiceExe, $BlockerExe, $ConfigPanelExe)
    $copiedCount = 0

    foreach ($file in $files) {
        $sourcePath = Join-Path $publishDir $file
        $destPath = Join-Path $InstallPath $file

        if (Test-Path $sourcePath) {
            try {
                Copy-Item -Path $sourcePath -Destination $destPath -Force
                Write-Success "Copied: $file"
                $copiedCount++
            }
            catch {
                Write-Error "Failed to copy $file`: $_"
            }
        }
        else {
            Write-Warning "File not found: $sourcePath"
        }
    }

    if ($copiedCount -eq 0) {
        Write-Error "No program files were copied. Please ensure the publish folder contains the built executables."
        return $false
    }

    return $true
}

function Create-BackupCopies {
    Write-Step "Creating backup copies..."

    $files = @($ServiceExe, $BlockerExe, $ConfigPanelExe)

    foreach ($file in $files) {
        $sourcePath = Join-Path $InstallPath $file
        $backupDest = Join-Path $BackupPath $file

        if (Test-Path $sourcePath) {
            try {
                Copy-Item -Path $sourcePath -Destination $backupDest -Force
                Write-Success "Backed up: $file"
            }
            catch {
                Write-Warning "Failed to backup $file`: $_"
            }
        }
    }

    return $true
}

function Set-FolderPermissions {
    Write-Step "Setting folder permissions..."

    try {
        # Set restrictive permissions on install folder
        # SYSTEM: Full Control
        # Administrators: Read & Execute

        $acl = Get-Acl $InstallPath
        $acl.SetAccessRuleProtection($true, $false) # Disable inheritance

        # Remove existing rules
        $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) } | Out-Null

        # Add SYSTEM full control
        $systemRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            "NT AUTHORITY\SYSTEM",
            "FullControl",
            "ContainerInherit,ObjectInherit",
            "None",
            "Allow"
        )
        $acl.AddAccessRule($systemRule)

        # Add Administrators read/execute
        $adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            "BUILTIN\Administrators",
            "ReadAndExecute",
            "ContainerInherit,ObjectInherit",
            "None",
            "Allow"
        )
        $acl.AddAccessRule($adminRule)

        Set-Acl -Path $InstallPath -AclObject $acl
        Write-Success "Set permissions on install folder"

        # Set permissions on data folder (allow SYSTEM full control, Admins full control for config editing)
        $dataAcl = Get-Acl $DataPath
        $dataAcl.SetAccessRuleProtection($true, $false)
        $dataAcl.Access | ForEach-Object { $dataAcl.RemoveAccessRule($_) } | Out-Null
        $dataAcl.AddAccessRule($systemRule)

        $adminFullRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            "BUILTIN\Administrators",
            "FullControl",
            "ContainerInherit,ObjectInherit",
            "None",
            "Allow"
        )
        $dataAcl.AddAccessRule($adminFullRule)

        Set-Acl -Path $DataPath -AclObject $dataAcl
        Write-Success "Set permissions on data folder"

        return $true
    }
    catch {
        Write-Warning "Failed to set folder permissions: $_"
        return $true # Non-fatal
    }
}

function Install-WindowsService {
    Write-Step "Installing Windows service..."

    $servicePath = Join-Path $InstallPath $ServiceExe

    if (-not (Test-Path $servicePath)) {
        Write-Error "Service executable not found at: $servicePath"
        return $false
    }

    try {
        # Remove existing service if present
        $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($existingService) {
            Write-Info "Removing existing service..."
            & sc.exe delete $ServiceName | Out-Null
            Start-Sleep -Seconds 2
        }

        # Create new service
        Write-Info "Creating service..."
        $result = & sc.exe create $ServiceName binPath= "`"$servicePath`"" start= auto DisplayName= "$ServiceDisplayName"

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to create service: $result"
            return $false
        }

        # Set description
        & sc.exe description $ServiceName "$ServiceDescription" | Out-Null

        # Configure recovery options (restart immediately on all failures)
        & sc.exe failure $ServiceName reset= 0 actions= restart/0/restart/0/restart/0 | Out-Null

        Write-Success "Service installed successfully"
        return $true
    }
    catch {
        Write-Error "Failed to install service: $_"
        return $false
    }
}

function Backup-ServiceConfiguration {
    Write-Step "Backing up service configuration..."

    try {
        $backupFile = Join-Path $BackupPath "service.reg"
        $regPath = "HKLM\SYSTEM\CurrentControlSet\Services\$ServiceName"

        & reg.exe export $regPath $backupFile /y 2>$null

        if ($LASTEXITCODE -eq 0) {
            Write-Success "Service configuration backed up"
        }
        else {
            Write-Warning "Could not backup service configuration"
        }

        return $true
    }
    catch {
        Write-Warning "Failed to backup service configuration: $_"
        return $true # Non-fatal
    }
}

function Create-ScheduledTasks {
    Write-Step "Creating scheduled tasks..."

    $tasks = @(
        @{
            Name = "STG_Monitor_1"
            Trigger = "MINUTE"
            Modifier = "1"
        },
        @{
            Name = "STG_Monitor_2"
            Trigger = "MINUTE"
            Modifier = "1"
        },
        @{
            Name = "STG_LogonTrigger"
            Trigger = "ONLOGON"
        },
        @{
            Name = "STG_BootCheck"
            Trigger = "ONSTART"
        }
    )

    $checkScript = @"
`$serviceName = '$ServiceName'
`$service = Get-Service -Name `$serviceName -ErrorAction SilentlyContinue
if (`$null -eq `$service) {
    `$backupReg = '$BackupPath\service.reg'
    if (Test-Path `$backupReg) {
        reg import `$backupReg 2>`$null
        Start-Sleep -Seconds 2
    }
}
`$service = Get-Service -Name `$serviceName -ErrorAction SilentlyContinue
if (`$null -ne `$service -and `$service.Status -ne 'Running') {
    Start-Service -Name `$serviceName -ErrorAction SilentlyContinue
}
"@

    foreach ($task in $tasks) {
        try {
            # Delete existing task
            & schtasks.exe /Delete /TN $task.Name /F 2>$null

            # Create script file for this task
            $scriptPath = Join-Path $DataPath "$($task.Name).ps1"
            $checkScript | Out-File -FilePath $scriptPath -Encoding UTF8 -Force

            # Build schtasks arguments
            $args = @("/Create", "/TN", $task.Name, "/RU", "SYSTEM", "/RL", "HIGHEST", "/F")

            if ($task.Trigger -eq "MINUTE") {
                $args += @("/SC", "MINUTE", "/MO", $task.Modifier)
            }
            elseif ($task.Trigger -eq "ONLOGON") {
                $args += @("/SC", "ONLOGON")
            }
            elseif ($task.Trigger -eq "ONSTART") {
                $args += @("/SC", "ONSTART")
            }

            $args += @("/TR", "powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$scriptPath`"")

            & schtasks.exe @args | Out-Null

            if ($LASTEXITCODE -eq 0) {
                Write-Success "Created task: $($task.Name)"
            }
            else {
                Write-Warning "Failed to create task: $($task.Name)"
            }
        }
        catch {
            Write-Warning "Error creating task $($task.Name): $_"
        }
    }

    return $true
}

function Create-RegistryRunKey {
    Write-Step "Creating startup registry entry..."

    try {
        $servicePath = Join-Path $InstallPath $ServiceExe
        $regPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"

        Set-ItemProperty -Path $regPath -Name "WinDisplayCalibration" -Value $servicePath -Force
        Write-Success "Registry run key created"
        return $true
    }
    catch {
        Write-Warning "Failed to create registry run key: $_"
        return $true # Non-fatal
    }
}

function Start-ServiceNow {
    Write-Step "Starting service..."

    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        Start-Sleep -Seconds 2

        $service = Get-Service -Name $ServiceName
        if ($service.Status -eq 'Running') {
            Write-Success "Service started successfully"
            return $true
        }
        else {
            Write-Warning "Service status: $($service.Status)"
            return $false
        }
    }
    catch {
        Write-Error "Failed to start service: $_"
        return $false
    }
}

function Show-Summary {
    Write-Host "`n" -NoNewline
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
    Write-Host "                    INSTALLATION COMPLETE                       " -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Install location: $InstallPath" -ForegroundColor White
    Write-Host "  Data location:    $DataPath" -ForegroundColor White
    Write-Host ""
    Write-Host "  To configure settings, run:" -ForegroundColor Cyan
    Write-Host "    $InstallPath\$ConfigPanelExe" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Or wait until block hours to see the setup screen." -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Default block hours: 1:00 AM - 7:00 AM" -ForegroundColor Gray
    Write-Host ""
}

# Main installation
function Main {
    Clear-Host
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "          SIMPLE WINDOWS SCREENTIME INSTALLER                   " -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""

    # Check/request elevation
    Request-Elevation

    $success = $true

    # Step 1: Check .NET
    if (-not (Install-DotNet8)) {
        Write-Error "Cannot proceed without .NET 8 Desktop Runtime"
        $success = $false
    }

    if ($success) {
        # Step 2: Stop existing service
        Stop-ExistingService

        # Step 3: Create directories
        if (-not (Create-Directories)) {
            $success = $false
        }
    }

    if ($success) {
        # Step 4: Copy files
        if (-not (Copy-ProgramFiles)) {
            $success = $false
        }
    }

    if ($success) {
        # Step 5: Create backups
        Create-BackupCopies

        # Step 6: Set permissions
        Set-FolderPermissions

        # Step 7: Install service
        if (-not (Install-WindowsService)) {
            $success = $false
        }
    }

    if ($success) {
        # Step 8: Backup service config
        Backup-ServiceConfiguration

        # Step 9: Create scheduled tasks
        Create-ScheduledTasks

        # Step 10: Create registry entry
        Create-RegistryRunKey

        # Step 11: Start service
        Start-ServiceNow

        # Show summary
        Show-Summary
    }
    else {
        Write-Host ""
        Write-Error "Installation failed. Please check the errors above."
    }

    Write-Host ""
    Read-Host "Press Enter to exit"
}

# Run main
Main
