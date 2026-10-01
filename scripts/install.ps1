# SideHub Agent Installer for Windows
# Requires: Node.js (for PTY terminal support)

param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"

$SideHubApi = if ($env:SIDEHUB_API) { $env:SIDEHUB_API } else { "https://www.sidehub.io/api" }
$InstallDir = if ($env:INSTALL_DIR) { $env:INSTALL_DIR } else { "$env:LOCALAPPDATA\Programs\sidehub-agent" }
# Checksums come straight from GitHub Releases, not through the SideHub API proxy that serves the archive:
# a compromised proxy cannot hand out both a tampered archive and a matching checksum.
$GitHubRepo = if ($env:SIDEHUB_GITHUB_REPO) { $env:SIDEHUB_GITHUB_REPO } else { "toregua/side_hub_agent" }

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

function Get-LatestTag {
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$GitHubRepo/releases/latest" -UseBasicParsing
    } catch {
        return $null
    }
    if ($release.tag_name -match '^v\d') { return $release.tag_name }
    return $null
}

# Verify the archive against the release's checksums.sha256; any failure aborts the install
function Test-ArchiveChecksum {
    param([string]$ArchivePath, [string]$AssetName, [string]$Tag, [string]$TempDir)

    $checksumsUrl = "https://github.com/$GitHubRepo/releases/download/$Tag/checksums.sha256"
    $checksumsPath = Join-Path $TempDir "checksums.sha256"
    try {
        Invoke-WebRequest -Uri $checksumsUrl -OutFile $checksumsPath -UseBasicParsing
    } catch {
        Write-Host "Unable to download checksums from $checksumsUrl" -ForegroundColor Red
        return $false
    }

    # sha256sum format: "<hash>  <name>" (or "<hash> *<name>" in binary mode)
    $expected = $null
    foreach ($line in Get-Content $checksumsPath) {
        $parts = $line.Trim() -split '\s+', 2
        if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $AssetName) {
            $expected = $parts[0].ToLowerInvariant()
            break
        }
    }
    if (-not $expected) {
        Write-Host "No checksum for $AssetName in checksums.sha256 ($Tag)" -ForegroundColor Red
        return $false
    }

    $actual = (Get-FileHash -Path $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Write-Host "Checksum mismatch for ${AssetName}: expected $expected, got $actual" -ForegroundColor Red
        return $false
    }

    Write-Host "SHA256 checksum verified ($AssetName, $Tag)" -ForegroundColor Green
    return $true
}

function Install-SideHubAgent {
    Test-NodeJs

    $platform = Get-Platform

    # Pin "latest" to a tag so the archive and its checksum come from the same release
    if ($Version -eq "latest") {
        $tag = Get-LatestTag
        if (-not $tag) {
            Write-Error "Unable to resolve the latest version from https://github.com/$GitHubRepo/releases"
            exit 1
        }
    } else {
        $tag = "v" + $Version.TrimStart('v')
    }

    $assetName = "sidehub-agent-$platform.zip"
    $url = "$SideHubApi/agent/download/$platform/$tag"

    Write-Host "Downloading SideHub Agent $tag ($platform)..."

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

    if (-not (Test-ArchiveChecksum -ArchivePath $archivePath -AssetName $assetName -Tag $tag -TempDir $tempDir)) {
        Remove-Item -Recurse -Force $tempDir
        Write-Error "Installation aborted: the archive was not extracted."
        exit 1
    }

    Write-Host "Extracting..."
    $extractDir = Join-Path $tempDir "package"
    Expand-Archive -Path $archivePath -DestinationPath $extractDir -Force

    # node_modules ships prebuilt in the archive (npm ci from the lockfile, in the release CI).
    # Archives built before that only carry package.json: install from the registry as before.
    $ptyHelperDir = Join-Path $extractDir "pty-helper"
    if (Test-Path (Join-Path $ptyHelperDir "node_modules\node-pty")) {
        Write-Host "Node.js dependencies bundled in the archive"
    } else {
        Push-Location $ptyHelperDir
        if (Test-Path (Join-Path $ptyHelperDir "package-lock.json")) {
            Write-Host "Installing Node.js dependencies (lockfile)..."
            & npm ci --omit=dev --ignore-scripts --silent
        } else {
            Write-Host "Installing Node.js dependencies (older release, no lockfile)..."
            & npm install --omit=dev --silent
        }
        Pop-Location
    }

    Write-Host "Installing to $InstallDir..."
    if (Test-Path $InstallDir) {
        Remove-Item -Recurse -Force $InstallDir
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    Get-ChildItem -Path $extractDir | Copy-Item -Destination $InstallDir -Recurse -Force

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
    Write-Host "To get started, from your project folder:"
    Write-Host "  sidehub-agent setup --token-stdin   (then paste the token copied from SideHub)"
    Write-Host ""
    Write-Host "Note: Restart your terminal to update the PATH."
}

Install-SideHubAgent
