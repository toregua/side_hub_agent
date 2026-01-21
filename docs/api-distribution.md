# API de Distribution - Implémentation côté SideHub

Ce document décrit comment implémenter les endpoints de distribution dans l'API SideHub.

## Endpoints à implémenter

### GET /api/agent/releases

Liste toutes les versions disponibles.

```json
{
  "releases": [
    {
      "version": "1.2.0",
      "tag": "v1.2.0",
      "date": "2025-01-15T10:30:00Z",
      "platforms": ["osx-arm64", "osx-x64", "win-x64", "win-arm64", "linux-x64", "linux-arm64"]
    },
    {
      "version": "1.1.0",
      "tag": "v1.1.0",
      "date": "2025-01-10T08:00:00Z",
      "platforms": ["osx-arm64", "osx-x64", "win-x64", "linux-x64"]
    }
  ]
}
```

### GET /api/agent/releases/latest

Retourne la dernière version stable.

```json
{
  "version": "1.2.0",
  "tag": "v1.2.0",
  "date": "2025-01-15T10:30:00Z",
  "platforms": {
    "osx-arm64": {
      "url": "https://github.com/.../releases/download/v1.2.0/sidehub-agent-osx-arm64",
      "sha256": "abc123..."
    },
    "osx-x64": {
      "url": "https://github.com/.../releases/download/v1.2.0/sidehub-agent-osx-x64",
      "sha256": "def456..."
    },
    "win-x64": {
      "url": "https://github.com/.../releases/download/v1.2.0/sidehub-agent-win-x64.exe",
      "sha256": "ghi789..."
    }
  }
}
```

### GET /api/agent/download/{platform}

Redirige vers le binaire de la dernière version pour la plateforme spécifiée.

**Plateformes valides**: `osx-arm64`, `osx-x64`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`

**Alias pratiques** (optionnel):
- `macos` → détecte automatiquement arm64 ou x64 via User-Agent
- `windows` → `win-x64`
- `linux` → `linux-x64`

**Réponse**: HTTP 302 redirect vers l'URL GitHub Release

### GET /api/agent/download/{platform}/{version}

Redirige vers une version spécifique.

### GET /api/agent/install.sh

Retourne le script d'installation bash.

```
Content-Type: text/plain
```

### GET /api/agent/install.ps1

Retourne le script d'installation PowerShell.

```
Content-Type: text/plain
```

## Exemple d'implémentation (Node.js/Express)

```typescript
import { Router } from 'express';
import { Octokit } from '@octokit/rest';

const router = Router();
const octokit = new Octokit({ auth: process.env.GITHUB_TOKEN });

const REPO_OWNER = 'votre-org';
const REPO_NAME = 'side_hub_agent';

const PLATFORM_ALIASES: Record<string, string> = {
  'macos': 'osx-arm64',
  'windows': 'win-x64',
  'linux': 'linux-x64',
};

// GET /api/agent/releases
router.get('/releases', async (req, res) => {
  const { data: releases } = await octokit.repos.listReleases({
    owner: REPO_OWNER,
    repo: REPO_NAME,
    per_page: 20,
  });

  const formatted = releases.map(release => ({
    version: release.tag_name.replace('v', ''),
    tag: release.tag_name,
    date: release.published_at,
    platforms: release.assets
      .map(a => a.name.replace('sidehub-agent-', '').replace('.exe', ''))
      .filter(p => !p.includes('checksum')),
  }));

  res.json({ releases: formatted });
});

// GET /api/agent/releases/latest
router.get('/releases/latest', async (req, res) => {
  const { data: release } = await octokit.repos.getLatestRelease({
    owner: REPO_OWNER,
    repo: REPO_NAME,
  });

  const platforms: Record<string, { url: string; sha256?: string }> = {};

  for (const asset of release.assets) {
    if (asset.name === 'checksums.sha256') continue;

    const platform = asset.name
      .replace('sidehub-agent-', '')
      .replace('.exe', '');

    platforms[platform] = {
      url: asset.browser_download_url,
    };
  }

  res.json({
    version: release.tag_name.replace('v', ''),
    tag: release.tag_name,
    date: release.published_at,
    platforms,
  });
});

// GET /api/agent/download/:platform
router.get('/download/:platform', async (req, res) => {
  let platform = req.params.platform;

  // Resolve alias
  if (PLATFORM_ALIASES[platform]) {
    platform = PLATFORM_ALIASES[platform];
  }

  const { data: release } = await octokit.repos.getLatestRelease({
    owner: REPO_OWNER,
    repo: REPO_NAME,
  });

  const isWindows = platform.startsWith('win-');
  const assetName = isWindows
    ? `sidehub-agent-${platform}.exe`
    : `sidehub-agent-${platform}`;

  const asset = release.assets.find(a => a.name === assetName);

  if (!asset) {
    return res.status(404).json({
      error: 'Platform not found',
      availablePlatforms: release.assets
        .map(a => a.name)
        .filter(n => n.startsWith('sidehub-agent-')),
    });
  }

  res.redirect(asset.browser_download_url);
});

// GET /api/agent/download/:platform/:version
router.get('/download/:platform/:version', async (req, res) => {
  let { platform, version } = req.params;

  if (PLATFORM_ALIASES[platform]) {
    platform = PLATFORM_ALIASES[platform];
  }

  const tag = version.startsWith('v') ? version : `v${version}`;

  const { data: release } = await octokit.repos.getReleaseByTag({
    owner: REPO_OWNER,
    repo: REPO_NAME,
    tag,
  });

  const isWindows = platform.startsWith('win-');
  const assetName = isWindows
    ? `sidehub-agent-${platform}.exe`
    : `sidehub-agent-${platform}`;

  const asset = release.assets.find(a => a.name === assetName);

  if (!asset) {
    return res.status(404).json({ error: 'Platform not found for this version' });
  }

  res.redirect(asset.browser_download_url);
});

// GET /api/agent/install.sh
router.get('/install.sh', async (req, res) => {
  const { data: file } = await octokit.repos.getContent({
    owner: REPO_OWNER,
    repo: REPO_NAME,
    path: 'scripts/install.sh',
  });

  const content = Buffer.from((file as any).content, 'base64').toString('utf-8');
  res.type('text/plain').send(content);
});

// GET /api/agent/install.ps1
router.get('/install.ps1', async (req, res) => {
  const { data: file } = await octokit.repos.getContent({
    owner: REPO_OWNER,
    repo: REPO_NAME,
    path: 'scripts/install.ps1',
  });

  const content = Buffer.from((file as any).content, 'base64').toString('utf-8');
  res.type('text/plain').send(content);
});

export default router;
```

## Workflow complet

```
┌─────────────────────────────────────────────────────────────────────┐
│                         RELEASE WORKFLOW                             │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  1. Developer                                                        │
│     └─→ git tag v1.2.0 && git push --tags                           │
│                                                                      │
│  2. GitHub Actions (.github/workflows/release.yml)                   │
│     └─→ Build pour 6 plateformes                                    │
│     └─→ Upload sur GitHub Releases                                  │
│     └─→ Génère checksums.sha256                                     │
│                                                                      │
│  3. GitHub Releases (stockage)                                      │
│     ├── sidehub-agent-osx-arm64                                     │
│     ├── sidehub-agent-osx-x64                                       │
│     ├── sidehub-agent-win-x64.exe                                   │
│     ├── sidehub-agent-win-arm64.exe                                 │
│     ├── sidehub-agent-linux-x64                                     │
│     ├── sidehub-agent-linux-arm64                                   │
│     └── checksums.sha256                                            │
│                                                                      │
│  4. API SideHub                                                      │
│     └─→ /api/agent/download/macos → redirect GitHub                 │
│                                                                      │
│  5. Utilisateur                                                      │
│     └─→ curl ... | bash                                             │
│     └─→ Binary téléchargé et installé                               │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

## Variables d'environnement requises

```env
# API SideHub
GITHUB_TOKEN=ghp_xxx  # Token avec accès read aux releases

# Optionnel: override du repo
AGENT_REPO_OWNER=votre-org
AGENT_REPO_NAME=side_hub_agent
```

## Sécurité

1. **Vérification des checksums**: Les scripts d'installation peuvent vérifier le SHA256
2. **HTTPS only**: Tous les téléchargements via HTTPS
3. **Signature optionnelle**: Signer les binaires avec codesign (macOS) ou signtool (Windows)
