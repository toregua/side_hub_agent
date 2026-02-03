# SideHub Agent

Agent d'exécution de commandes à distance pour la plateforme SideHub. Se connecte via WebSocket pour recevoir et exécuter des commandes shell avec streaming en temps réel.

## Prérequis

- .NET 10.0 Runtime (ou SDK pour compiler depuis les sources)

## Installation

### macOS

#### Option 1 : Téléchargement direct (recommandé)

```bash
# Télécharger la dernière version
curl -fsSL https://www.sidehub.io/api/agent/download/macos -o sidehub-agent

# Rendre exécutable
chmod +x sidehub-agent

# Déplacer dans le PATH (optionnel)
sudo mv sidehub-agent /usr/local/bin/
```

#### Option 2 : Via le script d'installation

```bash
curl -fsSL https://www.sidehub.io/api/agent/install.sh | bash
```

#### Option 3 : Compilation depuis les sources

```bash
# Cloner le repo
git clone https://github.com/votre-org/side_hub_agent.git
cd side_hub_agent

# Publier en self-contained (inclut le runtime .NET)
dotnet publish SideHub.Agent -c Release -r osx-arm64 --self-contained -o ./dist

# Ou pour Intel Mac
dotnet publish SideHub.Agent -c Release -r osx-x64 --self-contained -o ./dist
```

### Windows

#### Option 1 : Téléchargement direct (recommandé)

```powershell
# PowerShell - Télécharger la dernière version
Invoke-WebRequest -Uri "https://www.sidehub.io/api/agent/download/windows" -OutFile "sidehub-agent.exe"

# Déplacer dans un dossier du PATH (optionnel)
Move-Item sidehub-agent.exe "$env:LOCALAPPDATA\Programs\sidehub-agent\sidehub-agent.exe"
```

#### Option 2 : Via le script d'installation

```powershell
irm https://www.sidehub.io/api/agent/install.ps1 | iex
```

#### Option 3 : Compilation depuis les sources

```powershell
# Cloner le repo
git clone https://github.com/votre-org/side_hub_agent.git
cd side_hub_agent

# Publier en self-contained
dotnet publish SideHub.Agent -c Release -r win-x64 --self-contained -o ./dist
```

## Configuration

Créer un dossier `.sidehub/` à la racine de votre repository avec un ou plusieurs fichiers de configuration JSON :

```
mon-repo/
└── .sidehub/
    ├── agent-dev.json
    ├── agent-prod.json
    └── ...
```

Chaque fichier `.json` dans le dossier `.sidehub/` représente un agent qui sera lancé en parallèle.

### Format de configuration

```json
{
  "name": "mon-agent",
  "sidehubUrl": "wss://www.sidehub.io/ws/agent",
  "agentId": "votre-agent-uuid",
  "workspaceId": "votre-workspace-uuid",
  "agentToken": "sh_agent_xxx",
  "workingDirectory": ".",
  "capabilities": ["shell", "claude-code"]
}
```

| Champ | Obligatoire | Description |
|-------|-------------|-------------|
| `name` | Non | Nom d'affichage de l'agent (sinon utilise le nom du fichier) |
| `sidehubUrl` | Oui | URL WebSocket du serveur SideHub |
| `agentId` | Oui | UUID de l'agent |
| `workspaceId` | Oui | UUID du workspace |
| `agentToken` | Oui | Token d'authentification (`sh_agent_xxx`) |
| `workingDirectory` | Oui | Répertoire de travail (relatif ou absolu) |
| `capabilities` | Oui | Capacités de l'agent (`shell`, `claude-code`) |

Les tokens et IDs sont disponibles dans votre dashboard SideHub.

## Utilisation

```bash
# Lancer tous les agents configurés
sidehub-agent

# Depuis le dossier contenant .sidehub/
./sidehub-agent
```

L'agent :
1. Charge toutes les configurations depuis `.sidehub/*.json`
2. Lance chaque agent en parallèle
3. Chaque agent se connecte au serveur SideHub via WebSocket
4. Envoie des heartbeats toutes les 30 secondes
5. Exécute les commandes reçues et stream les résultats

Arrêt propre avec `Ctrl+C` (arrête tous les agents).

## Architecture des releases

### Builds disponibles

| Plateforme | Architecture | Fichier |
|------------|--------------|---------|
| macOS | Apple Silicon (M1/M2/M3) | `sidehub-agent-osx-arm64` |
| macOS | Intel | `sidehub-agent-osx-x64` |
| Windows | x64 | `sidehub-agent-win-x64.exe` |
| Windows | ARM64 | `sidehub-agent-win-arm64.exe` |
| Linux | x64 | `sidehub-agent-linux-x64` |
| Linux | ARM64 | `sidehub-agent-linux-arm64` |

### Self-contained vs Framework-dependent

- **Self-contained** (~80-100 MB) : Inclut le runtime .NET, aucune dépendance
- **Framework-dependent** (~5 MB) : Nécessite .NET 10 installé

Les releases officielles sont self-contained pour simplifier l'installation.

## API de distribution (pour SideHub)

L'API SideHub expose les endpoints suivants pour la distribution :

### Endpoints

```
GET /api/agent/releases
GET /api/agent/releases/latest
GET /api/agent/releases/{version}
GET /api/agent/download/{platform}
GET /api/agent/download/{platform}/{version}
GET /api/agent/install.sh
GET /api/agent/install.ps1
```

### Exemple d'implémentation côté API

Voir la section [Distribution depuis SideHub](#distribution-depuis-sidehub) ci-dessous.

## Distribution depuis SideHub

### Structure recommandée pour l'API

```
/api/agent/
├── releases                    # Liste toutes les versions
├── releases/latest             # Dernière version stable
├── releases/{version}          # Détails d'une version
├── download/{platform}         # Télécharge la dernière version
├── download/{platform}/{version}  # Télécharge une version spécifique
├── install.sh                  # Script d'installation bash
└── install.ps1                 # Script d'installation PowerShell
```

### Workflow de release

1. **Tag une release** sur GitHub (`v1.0.0`)
2. **GitHub Actions** build les binaires pour toutes les plateformes
3. **Upload** les artifacts vers un storage (GitHub Releases, S3, Azure Blob)
4. **API SideHub** expose les endpoints de téléchargement

### Exemple de GitHub Actions workflow

Créer `.github/workflows/release.yml` :

```yaml
name: Release

on:
  push:
    tags:
      - 'v*'

jobs:
  build:
    strategy:
      matrix:
        include:
          - os: macos-latest
            rid: osx-arm64
            artifact: sidehub-agent-osx-arm64
          - os: macos-13
            rid: osx-x64
            artifact: sidehub-agent-osx-x64
          - os: windows-latest
            rid: win-x64
            artifact: sidehub-agent-win-x64.exe
          - os: ubuntu-latest
            rid: linux-x64
            artifact: sidehub-agent-linux-x64

    runs-on: ${{ matrix.os }}

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Publish
        run: |
          dotnet publish SideHub.Agent -c Release -r ${{ matrix.rid }} \
            --self-contained -p:PublishSingleFile=true \
            -o ./publish

      - name: Upload artifact
        uses: actions/upload-artifact@v4
        with:
          name: ${{ matrix.artifact }}
          path: ./publish/*

  release:
    needs: build
    runs-on: ubuntu-latest
    steps:
      - name: Download artifacts
        uses: actions/download-artifact@v4

      - name: Create Release
        uses: softprops/action-gh-release@v2
        with:
          files: |
            sidehub-agent-osx-arm64/*
            sidehub-agent-osx-x64/*
            sidehub-agent-win-x64.exe/*
            sidehub-agent-linux-x64/*
```

## Développement

### Build local

```bash
# Debug
dotnet build SideHub.Agent

# Release
dotnet build SideHub.Agent -c Release

# Run
dotnet run --project SideHub.Agent
```

### Publier pour une plateforme spécifique

```bash
# macOS Apple Silicon (self-contained, single file)
dotnet publish SideHub.Agent -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true

# Windows x64
dotnet publish SideHub.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Licence

Propriétaire - SideHub
