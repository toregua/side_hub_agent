# SideHub Agent Installer for Windows
# Requires: Node.js (for PTY terminal support)

param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"

$SideHubApi = if ($env:SIDEHUB_API) { $env:SIDEHUB_API } else { "https://www.sidehub.io/api" }
$InstallDir = if ($env:INSTALL_DIR) { $env:INSTALL_DIR } else { "$env:LOCALAPPDATA\Programs\sidehub-agent" }

# Check Node.js
function Test-NodeJs {
    try {
        $nodeVersion = & node --version 2>$null
        Write-Host "Node.js $nodeVersion found" -ForegroundColor Green
        return $true
    } catch {
        Write-Error "Node.js is required but not installed. Install it from https://nodejs.org"
        exit 1
    }
}

function Get-Platform {
    $arch = if ([Environment]::Is64BitOperatingSystem) {
        if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
    } else {
        Write-Error "32-bit architecture not supported"
        exit 1
    }
    return "win-$arch"
}

function Install-SideHubAgent {
    Test-NodeJs

    $platform = Get-Platform

    if ($Version -eq "latest") {
        $url = "$SideHubApi/agent/download/$platform"
    } else {
        $url = "$SideHubApi/agent/download/$platform/$Version"
    }

    Write-Host "Downloading SideHub Agent ($platform)..."

    # Create temp directory
    $tempDir = Join-Path $env:TEMP "sidehub-agent-install"
    if (Test-Path $tempDir) {
        Remove-Item -Recurse -Force $tempDir
    }
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    $archivePath = Join-Path $tempDir "agent.zip"

    try {
        Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing
    } catch {
        Write-Error "Error: Unable to download from $url"
        Remove-Item -Recurse -Force $tempDir
        exit 1
    }

    Write-Host "Extracting..."
    Expand-Archive -Path $archivePath -DestinationPath $tempDir -Force

    Write-Host "Installing Node.js dependencies..."
    $ptyHelperDir = Join-Path $tempDir "pty-helper"
    Push-Location $ptyHelperDir
    & npm install --silent
    Pop-Location

    Write-Host "Installing to $InstallDir..."
    if (Test-Path $InstallDir) {
        Remove-Item -Recurse -Force $InstallDir
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    # Copy all files except the archive
    Get-ChildItem -Path $tempDir -Exclude "agent.zip" | Copy-Item -Destination $InstallDir -Recurse -Force

    # Cleanup
    Remove-Item -Recurse -Force $tempDir

    # Add to PATH if not already present
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath -notlike "*$InstallDir*") {
        Write-Host "Adding to user PATH..."
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$InstallDir", "User")
        $env:Path = "$env:Path;$InstallDir"
    }

    Write-Host ""
    Write-Host "SideHub Agent installed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "To get started:"
    Write-Host "  1. Create an agent.json file with your configuration"
    Write-Host "  2. Run: sidehub-agent"
    Write-Host ""
    Write-Host "Note: Restart your terminal to update the PATH."
}

Install-SideHubAgent
