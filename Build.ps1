#Requires -Version 5.1
<#
.SYNOPSIS
    Build script for Simple Windows Screentime
.DESCRIPTION
    Builds all projects and publishes them to the publish folder.
.PARAMETER Configuration
    Build configuration (Debug or Release). Default is Release.
.PARAMETER Clean
    Clean before building.
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionPath = Join-Path $ScriptDir "SimpleWindowsScreentime.sln"
$PublishPath = Join-Path $ScriptDir "publish"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "[OK] $Message" -ForegroundColor Green
}

function Write-Err {
    param([string]$Message)
    Write-Host "[X] $Message" -ForegroundColor Red
}

# Check for dotnet CLI
Write-Step "Checking for .NET SDK..."
try {
    $dotnetVersion = & dotnet --version
    Write-Success ".NET SDK version: $dotnetVersion"
}
catch {
    Write-Err ".NET SDK not found. Please install .NET 8 SDK."
    exit 1
}

# Clean if requested
if ($Clean) {
    Write-Step "Cleaning solution..."

    if (Test-Path $PublishPath) {
        Remove-Item -Path $PublishPath -Recurse -Force
    }

    & dotnet clean $SolutionPath -c $Configuration --nologo -v q
    Write-Success "Clean complete"
}

# Restore packages
Write-Step "Restoring NuGet packages..."
& dotnet restore $SolutionPath --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Err "Restore failed"
    exit 1
}
Write-Success "Restore complete"

# Build solution
Write-Step "Building solution ($Configuration)..."
& dotnet build $SolutionPath -c $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Err "Build failed"
    exit 1
}
Write-Success "Build complete"

# Create publish directory
if (-not (Test-Path $PublishPath)) {
    New-Item -Path $PublishPath -ItemType Directory | Out-Null
}

# Publish each project
$projects = @(
    @{
        Name = "Service"
        Path = "SimpleWindowsScreentime.Service"
        OutputName = "WinDisplayCalibration.exe"
    },
    @{
        Name = "Blocker"
        Path = "SimpleWindowsScreentime.Blocker"
        OutputName = "STBlocker.exe"
    },
    @{
        Name = "ConfigPanel"
        Path = "SimpleWindowsScreentime.ConfigPanel"
        OutputName = "STConfigPanel.exe"
    },
    @{
        Name = "Debug"
        Path = "SimpleWindowsScreentime.Debug"
        OutputName = "STDebug.exe"
    }
)

foreach ($project in $projects) {
    Write-Step "Publishing $($project.Name)..."

    $projectPath = Join-Path $ScriptDir $project.Path
    $tempPublishPath = Join-Path $projectPath "bin\$Configuration\net8.0-windows\win-x64\publish"

    & dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained false --nologo -v q

    if ($LASTEXITCODE -ne 0) {
        Write-Err "Publish failed for $($project.Name)"
        exit 1
    }

    # Copy to publish folder
    $sourceExe = Join-Path $tempPublishPath $project.OutputName
    if (Test-Path $sourceExe) {
        Copy-Item -Path $sourceExe -Destination $PublishPath -Force
        Write-Success "Published: $($project.OutputName)"
    }
    else {
        # Try alternative path
        $altPath = Join-Path $projectPath "bin\$Configuration\net8.0-windows\publish\$($project.OutputName)"
        if (Test-Path $altPath) {
            Copy-Item -Path $altPath -Destination $PublishPath -Force
            Write-Success "Published: $($project.OutputName)"
        }
        else {
            Write-Err "Could not find published executable for $($project.Name)"
        }
    }
}

# Copy installer scripts
Write-Step "Copying installer scripts..."
Copy-Item -Path (Join-Path $ScriptDir "Install.ps1") -Destination $PublishPath -Force
Copy-Item -Path (Join-Path $ScriptDir "Uninstall.ps1") -Destination $PublishPath -Force
Write-Success "Scripts copied"

Write-Host ""
Write-Host "=================================================================" -ForegroundColor Green
Write-Host "                      BUILD COMPLETE                             " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Output folder: $PublishPath" -ForegroundColor White
Write-Host ""
Write-Host "  Contents:" -ForegroundColor Cyan
Get-ChildItem $PublishPath | ForEach-Object {
    Write-Host "    - $($_.Name)" -ForegroundColor Gray
}
Write-Host ""
Write-Host "  To install, run Install.ps1 as Administrator" -ForegroundColor Yellow
Write-Host ""
