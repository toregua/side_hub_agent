# SideHub Agent Installer for Windows
# Requires: Node.js (for PTY terminal support)
#
#   SIDEHUB_INSTALL_DIR  install folder (default %LOCALAPPDATA%\Programs\sidehub-agent); an existing folder
#   is only replaced if it holds a previous agent install
#
# The archive is checked against the release's checksums.sha256, downloaded from GitHub Releases, and
# checksums.sha256 itself must carry a valid signature (checksums.sha256.sig) from the release key below:
# write access to the GitHub release alone is not enough to ship a tampered archive.

param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"

# Downloads live under /agent at the API host root (REST routes are under /api): accept either form
$SideHubApi = if ($env:SIDEHUB_API) { $env:SIDEHUB_API } else { "https://api.sidehub.io" }
$SideHubApi = $SideHubApi.TrimEnd('/') -replace '/api$', ''
$InstallDir = if ($env:SIDEHUB_INSTALL_DIR) { $env:SIDEHUB_INSTALL_DIR } else { "$env:LOCALAPPDATA\Programs\sidehub-agent" }
# Written in every install folder: proves a folder is ours before it is wiped on reinstall
$InstallMarker = ".sidehub-agent-install"
# Checksums come straight from GitHub Releases, not through the SideHub API proxy that serves the archive:
# a compromised proxy cannot hand out both a tampered archive and a matching checksum.
$GitHubRepo = if ($env:SIDEHUB_GITHUB_REPO) { $env:SIDEHUB_GITHUB_REPO } else { "toregua/side_hub_agent" }

# Release signing key (RSA, PKCS#1 v1.5 / SHA-256 over checksums.sha256), the private half is the
# RELEASE_SIGNING_KEY secret of the release workflow. Same key as install.sh (ReleaseSigningKeyTests).
$ReleaseSigningKeyXml = '<RSAKeyValue><Modulus>t9EUPG9pOyOv9va4UK8vI4mJA4nPN7GUiE+Cj7aek6OI3WSl1ma5Bdp7rPYj/aYX+XiM4FMGbzqpLwiYauZzCr4Xlf4Nka/3AKzvl7PorFXCnj1Y5aSw5A5t7loAPEwC6SIQoCvSFmMnDUcbOKjet/jhe2aHFk8AoKIhtD1OPP1fYunv9+pmFNYV3RaoTW4KqIvKviJlPOYiJF+eFu1k2yXQt9js+hzOBztXIlm3FKR9Te/mzEEbdrZo0SCh4r51yxR/n2Gn4SDlHBqSGxrHNqGSMmXNdqsyQuzb6Pu6fYIweRD7gW4gd7bLRDe7bFXHEI2VGlT+5hUEaAfl8FbooLPHu2P7i+UkiPrDzcehNCQ0LQyyyNEZIxEL63zHWCEsmiXBoRXpaJg94i1Y1Zzmnh4Q2DlpTCeVzgUGqgLGUaR4CiJdJRnFQlB+9Rm9UsRIJV5RihAW4R/fEGvBX2Nv0WKMxs9stdwcYmYrk19LJGLlFfpL9c5t9TF0KDB2SiIV</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>'
# Releases published before signing: installed on the checksum alone (with a warning). Any later release
# without a signature is refused, so deleting checksums.sha256.sig does not downgrade the check.
$LastUnsignedVersion = [version]"1.0.61"
# First release whose archive bundles pty-helper's node_modules (npm ci from the lockfile, in the CI).
# Older ones need `npm install` from the registry at install time, running package scripts: refused.
$MinVersion = [version]"1.0.59"

# Check Node.js
function Test-NodeJs {
    try {
        $nodeVersion = & node --version 2>$null
        Write-Host "Node.js $nodeVersion found" -ForegroundColor Green
        return $true
    } catch {
        throw "Node.js is required but not installed. Install it from https://nodejs.org"
    }
}

function Get-Platform {
    $arch = if ([Environment]::Is64BitOperatingSystem) {
        if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
    } else {
        throw "32-bit architecture not supported"
    }
    return "win-$arch"
}

function Get-LatestTag {
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$GitHubRepo/releases/latest" -UseBasicParsing
    } catch {
        Write-Host "GitHub API call failed: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
    if ($release.tag_name -match '^v\d') { return $release.tag_name }
    return $null
}

# Verify checksums.sha256 against its detached signature with the embedded release key
function Test-ChecksumsSignature {
    param([string]$ChecksumsPath, [string]$Tag, [string]$TempDir)

    $sigUrl = "https://github.com/$GitHubRepo/releases/download/$Tag/checksums.sha256.sig"
    $sigPath = Join-Path $TempDir "checksums.sha256.sig"
    try {
        Invoke-WebRequest -Uri $sigUrl -OutFile $sigPath -UseBasicParsing
    } catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -eq 404 -and [version]$Tag.TrimStart('v') -le $LastUnsignedVersion) {
            Write-Host "$Tag predates signed releases: only the SHA256 checksum is verified." -ForegroundColor Yellow
            return $true
        }
        Write-Host "Checksums signature not found ($sigUrl)" -ForegroundColor Red
        return $false
    }

    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $rsa.FromXmlString($ReleaseSigningKeyXml)
        $valid = $rsa.VerifyData(
            [System.IO.File]::ReadAllBytes($ChecksumsPath),
            [System.IO.File]::ReadAllBytes($sigPath),
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    } finally {
        $rsa.Dispose()
    }
    if (-not $valid) {
        Write-Host "Invalid signature for checksums.sha256 ($Tag): release not published by SideHub" -ForegroundColor Red
        return $false
    }
    Write-Host "Release signature verified ($Tag)" -ForegroundColor Green
    return $true
}

# Refuse to wipe a folder that is not a previous agent install (a mistyped SIDEHUB_INSTALL_DIR, Program Files...)
function Test-InstallDir {
    if (-not [System.IO.Path]::IsPathRooted($InstallDir) -or $InstallDir -match '(^|[\\/])\.\.?([\\/]|$)') {
        Write-Host "SIDEHUB_INSTALL_DIR must be an absolute path without . or ..: $InstallDir" -ForegroundColor Red
        return $false
    }
    if (-not (Test-Path -LiteralPath $InstallDir)) { return $true }
    $item = Get-Item -LiteralPath $InstallDir -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        Write-Host "$InstallDir exists and is not a folder" -ForegroundColor Red
        return $false
    }
    # Empty, ours (marker), or an install from before the marker (agent binary + pty-helper)
    $isEmpty = -not (Get-ChildItem -LiteralPath $InstallDir -Force | Select-Object -First 1)
    $hasMarker = Test-Path -LiteralPath (Join-Path $InstallDir $InstallMarker)
    $isLegacyInstall = (Test-Path -LiteralPath (Join-Path $InstallDir "sidehub-agent.exe")) -and
        (Test-Path -LiteralPath (Join-Path $InstallDir "pty-helper"))
    if ($isEmpty -or $hasMarker -or $isLegacyInstall) { return $true }
    Write-Host "$InstallDir is not empty and holds no SideHub Agent install: aborting." -ForegroundColor Red
    $entries = Get-ChildItem -LiteralPath $InstallDir -Force | Select-Object -First 10 -ExpandProperty Name
    Write-Host "Found: $($entries -join ', ')" -ForegroundColor Red
    Write-Host "Pick another folder (SIDEHUB_INSTALL_DIR) or empty it yourself." -ForegroundColor Red
    return $false
}

# Verify the archive against the release's checksums.sha256; any failure aborts the install
function Test-ArchiveChecksum {
    param([string]$ArchivePath, [string]$AssetName, [string]$Tag, [string]$TempDir)

    $checksumsUrl = "https://github.com/$GitHubRepo/releases/download/$Tag/checksums.sha256"
    $checksumsPath = Join-Path $TempDir "checksums.sha256"
    try {
        Invoke-WebRequest -Uri $checksumsUrl -OutFile $checksumsPath -UseBasicParsing
    } catch {
        Write-Host "Unable to download checksums from ${checksumsUrl}: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }

    if (-not (Test-ChecksumsSignature -ChecksumsPath $checksumsPath -Tag $Tag -TempDir $TempDir)) {
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

# Processes running from the install folder: the agent, and pty-helper (node.exe loading its native modules
# from there). Windows locks their files, so the folder cannot be replaced while they run.
function Get-InstallDirProcesses {
    $root = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessId -ne $PID -and (
            ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) -or
            ($_.CommandLine -and $_.CommandLine.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0))
    }
}

# Project folders (from ~\.sidehub\instances.json) whose agent is running, to restart them after the update
function Get-RunningAgentDirs {
    $registry = Join-Path $env:USERPROFILE ".sidehub\instances.json"
    if (-not (Test-Path -LiteralPath $registry)) { return @() }
    try {
        $entries = @(Get-Content -LiteralPath $registry -Raw | ConvertFrom-Json)
    } catch {
        return @()
    }
    foreach ($entry in $entries) {
        if (-not $entry.directory) { continue }
        $pidFile = Join-Path $entry.directory ".sidehub\run\sidehub-agent.pid"
        if (-not (Test-Path -LiteralPath $pidFile)) { continue }
        $agentPid = 0
        if ([int]::TryParse((Get-Content -LiteralPath $pidFile -Raw).Trim(), [ref]$agentPid) -and
            (Get-Process -Id $agentPid -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "sidehub-agent*" })) {
            $entry.directory
        }
    }
}

# Stop the agents running from the install folder; returns the project folders to restart afterwards
function Stop-RunningAgents {
    if (-not @(Get-InstallDirProcesses)) { return @() }
    $dirs = @(Get-RunningAgentDirs)
    if (-not $dirs) {
        Write-Host "No registered project found for the running agent: restart it yourself afterwards (sidehub-agent start -d)." -ForegroundColor Yellow
    }

    Write-Host "Stopping the running SideHub Agent for the update (its terminal sessions will end)..."
    $agentExe = Join-Path $InstallDir "sidehub-agent.exe"
    if (Test-Path -LiteralPath $agentExe) {
        & $agentExe stop --all | Out-Host
    }

    # Agents started by hand (not registered) or a stop that didn't finish: wait, then force
    for ($i = 0; $i -lt 20 -and @(Get-InstallDirProcesses); $i++) { Start-Sleep -Milliseconds 500 }
    foreach ($process in @(Get-InstallDirProcesses)) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    for ($i = 0; $i -lt 10 -and @(Get-InstallDirProcesses); $i++) { Start-Sleep -Milliseconds 500 }

    $remaining = @(Get-InstallDirProcesses)
    if ($remaining) {
        throw ("Processes still running from ${InstallDir} (PID $(($remaining.ProcessId) -join ', ')): " +
            "stop them (sidehub-agent stop --all), then run the installer again.")
    }
    return $dirs
}

function Install-SideHubAgent {
    Test-NodeJs | Out-Null

    $platform = Get-Platform

    # Pin "latest" to a tag so the archive and its checksum come from the same release
    if ($Version -eq "latest") {
        $tag = Get-LatestTag
        if (-not $tag) {
            throw "Unable to resolve the latest version from https://github.com/$GitHubRepo/releases"
        }
    } else {
        $tag = "v" + $Version.TrimStart('v')
    }
    # The tag goes into URLs and messages: a plain version only
    if ($tag -notmatch '^v\d+(\.\d+){1,3}$') {
        throw "Invalid version: $Version (expected 1.0.61 or v1.0.61)"
    }
    if ([version]$tag.TrimStart('v') -lt $MinVersion) {
        throw ("$tag can no longer be installed: releases before v$MinVersion install their Node.js " +
            "dependencies from the npm registry at install time. Install v$MinVersion or later.")
    }

    if (-not (Test-InstallDir)) { throw "Installation aborted: unusable install folder ($InstallDir)." }

    $assetName = "sidehub-agent-$platform.zip"
    $url = "$SideHubApi/agent/download/$platform/$tag"

    if ($Version -eq "latest") {
        Write-Host "Downloading SideHub Agent $tag (latest, $platform)..."
    } else {
        Write-Host "Downloading SideHub Agent $tag ($platform)..."
    }

    # Unique temp directory: a fixed name could be pre-created (or swapped) by another process
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("sidehub-agent-install-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    $restartDirs = @()

    try {
        $archivePath = Join-Path $tempDir "agent.zip"

        try {
            Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing
        } catch {
            throw "Unable to download from ${url}: $($_.Exception.Message)"
        }

        if (-not (Test-ArchiveChecksum -ArchivePath $archivePath -AssetName $assetName -Tag $tag -TempDir $tempDir)) {
            throw "Installation aborted: the archive was not extracted."
        }

        Write-Host "Extracting..."
        $extractDir = Join-Path $tempDir "package"
        Expand-Archive -Path $archivePath -DestinationPath $extractDir -Force

        # node_modules ships prebuilt in the verified archive (npm ci from the lockfile, in the release CI):
        # the install never runs npm.
        if (-not (Test-Path (Join-Path $extractDir "pty-helper\node_modules\node-pty"))) {
            throw "The $tag archive does not bundle pty-helper's Node.js dependencies: aborting."
        }
        Write-Host "Node.js dependencies bundled in the archive"
        New-Item -ItemType File -Path (Join-Path $extractDir $InstallMarker) | Out-Null

        Write-Host "Installing to $InstallDir..."
        if (Test-Path -LiteralPath $InstallDir) {
            # A running agent locks its files: Remove-Item would fail halfway through the folder
            $restartDirs = @(Stop-RunningAgents)
            # Marker last: if a file is still locked, the folder stays recognisable as ours for the next run
            Get-ChildItem -LiteralPath $InstallDir -Force |
                Where-Object { $_.Name -ne $InstallMarker } |
                Remove-Item -Recurse -Force
            Remove-Item -LiteralPath $InstallDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

        Get-ChildItem -Path $extractDir -Force | Copy-Item -Destination $InstallDir -Recurse -Force
    } finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    foreach ($dir in $restartDirs) {
        Write-Host "Restarting the agent in $dir..."
        Push-Location -LiteralPath $dir
        try {
            & (Join-Path $InstallDir "sidehub-agent.exe") start -d | Out-Host
        } finally {
            Pop-Location
        }
    }

    # Add to PATH if not already present
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath -notlike "*$InstallDir*") {
        Write-Host "Adding to user PATH..."
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$InstallDir", "User")
        $env:Path = "$env:Path;$InstallDir"
    }

    Write-Host ""
    Write-Host "SideHub Agent $tag installed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "To get started, from your project folder:"
    Write-Host "  sidehub-agent setup --token-stdin   (then paste the token copied from SideHub)"
    Write-Host ""
    Write-Host "Note: Restart your terminal to update the PATH."
}

Install-SideHubAgent
