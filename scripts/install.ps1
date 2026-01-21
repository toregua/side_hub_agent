# SideHub Agent Installer for Windows

param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"

$SideHubApi = if ($env:SIDEHUB_API) { $env:SIDEHUB_API } else { "https://www.sidehub.io/api" }
$InstallDir = if ($env:INSTALL_DIR) { $env:INSTALL_DIR } else { "$env:LOCALAPPDATA\Programs\sidehub-agent" }

function Get-Platform {
    $arch = if ([Environment]::Is64BitOperatingSystem) {
        if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
    } else {
        Write-Error "Architecture 32-bit non supportée"
        exit 1
    }
    return "win-$arch"
}

function Install-SideHubAgent {
    $platform = Get-Platform

    if ($Version -eq "latest") {
        $url = "$SideHubApi/agent/download/$platform"
    } else {
        $url = "$SideHubApi/agent/download/$platform/$Version"
    }

    Write-Host "Téléchargement de SideHub Agent ($platform)..."

    # Create install directory
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }

    $exePath = Join-Path $InstallDir "sidehub-agent.exe"

    try {
        Invoke-WebRequest -Uri $url -OutFile $exePath -UseBasicParsing
    } catch {
        Write-Error "Erreur: Impossible de télécharger depuis $url"
        exit 1
    }

    # Add to PATH if not already present
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath -notlike "*$InstallDir*") {
        Write-Host "Ajout au PATH utilisateur..."
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$InstallDir", "User")
        $env:Path = "$env:Path;$InstallDir"
    }

    Write-Host ""
    Write-Host "SideHub Agent installé avec succès!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Pour commencer:"
    Write-Host "  1. Créez un fichier agent.json avec votre configuration"
    Write-Host "  2. Lancez: sidehub-agent"
    Write-Host ""
    Write-Host "Note: Redémarrez votre terminal pour que le PATH soit mis à jour."
}

Install-SideHubAgent
