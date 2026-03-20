# Side Hub MCP Server - Plan de Developpement

## Objectif

Creer un serveur MCP (Model Context Protocol) integre a l'agent Side Hub qui donne aux LLM (Claude, Codex, Gemini) la capacite d'interagir nativement avec Side Hub : creer des pages Drive, des taches, ajouter des commentaires, etc.

Le MCP server doit etre **automatiquement injecte** a chaque session CLI, quel que soit le provider.

---

## Architecture

```
Claude/Codex/Gemini CLI
    |
    |-- MCP stdio --> SideHub.Mcp (process enfant)
    |                     |
    |                     |-- HTTP --> api.sidehub.io (REST API)
    |                     |           (auth: X-Agent-Token)
    |                     |
    |                     |-- Contexte via env vars:
    |                         SIDEHUB_API_URL
    |                         SIDEHUB_AGENT_TOKEN
    |                         SIDEHUB_WORKSPACE_ID
    |                         SIDEHUB_TASK_ID (optionnel)
    |                         SIDEHUB_TASK_TITLE (optionnel)
    |
    |-- stdin/stdout --> Agent proxy (existant)
```

### Composants

| Composant | Repo | Description |
|-----------|------|-------------|
| `SideHub.Mcp` | `side_hub_agent` | Nouveau projet .NET console, transport stdio, protocole MCP JSON-RPC |
| Auth middleware | `side_hub` (backend) | Nouveau scheme `AgentToken` pour les endpoints REST |
| Injection MCP config | `side_hub_agent` | Generation dynamique du `.mcp.json` au spawn de chaque CLI |

### Pourquoi un projet separe dans le repo agent ?

- **Isolation** : le MCP server est un process enfant du CLI, pas de l'agent
- **Deploiement** : publie en meme temps que l'agent, meme repertoire de binaires
- **Simplicite** : console app .NET standard, stdin/stdout, pas de serveur HTTP a gerer

---

## Structure du projet

```
side_hub_agent/
  SideHub.Agent/              (existant)
  SideHub.Mcp/                (nouveau)
    SideHub.Mcp.csproj
    Program.cs                 (point d'entree stdio, boucle JSON-RPC)
    McpServer.cs               (dispatch des tool calls)
    SideHubApiClient.cs        (client HTTP pour l'API Side Hub)
    Tools/
      DriveTools.cs            (create_drive_page, update_drive_page, read_drive_page, list_drive)
      TaskTools.cs             (create_task, list_tasks, add_task_comment, create_blocker)
```

---

## Catalogue d'outils MCP

### Drive

| Outil | Description | Parametres | Endpoint backend |
|-------|-------------|------------|------------------|
| `sidehub_create_drive_page` | Creer une page dans le Drive du workspace | `title` (required), `content` (required, markdown/html), `parentId?` (guid, dossier cible) | `POST /api/workspaces/{wid}/drive` |
| `sidehub_update_drive_page` | Mettre a jour le contenu d'une page existante | `pageId` (required), `title?`, `content?` | `PUT /api/drive/{id}` |
| `sidehub_read_drive_page` | Lire le contenu d'une page Drive | `pageId` (required) | `GET /api/drive/{id}` |
| `sidehub_list_drive` | Lister les elements du Drive (pages, dossiers) | `parentId?` (filtrer par dossier) | `GET /api/workspaces/{wid}/drive` |

### Taches

| Outil | Description | Parametres | Endpoint backend |
|-------|-------------|------------|------------------|
| `sidehub_create_task` | Creer une nouvelle tache dans le workspace | `title` (required), `description?`, `type?` (feature/bug-fix/pr-review) | `POST /api/workspaces/{wid}/tasks` |
| `sidehub_list_tasks` | Lister les taches du workspace | `status?` (pending/in-progress/completed) | `GET /api/workspaces/{wid}/tasks` |
| `sidehub_add_task_comment` | Ajouter un commentaire a une tache | `taskId` (required), `text` (required) | `POST /api/workspaces/{wid}/tasks/{id}/comments` |
| `sidehub_create_blocker` | Signaler un blocage sur une tache | `taskId` (required), `reason` (required) | `POST /api/workspaces/{wid}/tasks/{id}/blocker` |

### Notes sur le catalogue

- Les outils sont **volontairement limites** a la Phase 1 : lecture/ecriture Drive + gestion basique des taches
- Pas d'outils destructifs (pas de delete, pas de complete_task) pour eviter les actions non reversibles
- `SIDEHUB_WORKSPACE_ID` est injecte via env var, les outils n'ont pas besoin de demander le workspace
- `SIDEHUB_TASK_ID` est optionnel : quand present, `add_task_comment` et `create_blocker` l'utilisent par defaut

---

## Changements backend requis (repo `side_hub`)

### 1. Authentification agent sur les endpoints REST

**Probleme** : Les endpoints REST n'acceptent que des JWT utilisateur. Les tokens agent (`sh_agent_*`) ne fonctionnent que sur le WebSocket.

**Solution** : Ajouter un scheme d'authentification `AgentToken` qui permet aux agents d'appeler les endpoints REST.

```csharp
// Nouveau middleware : AgentTokenAuthenticationHandler
// - Extrait le token de l'en-tete X-Agent-Token (ou Authorization: AgentToken sh_agent_...)
// - Hash SHA256 + lookup en base (meme logique que AgentWebSocketHandler)
// - Cree un ClaimsPrincipal avec :
//   - Claim "AgentId" = agent.Id
//   - Claim "WorkspaceId" = agent.WorkspaceId
//   - Role "Agent" (pour distinguer des users humains)
```

**Endpoints concernes** : Uniquement ceux utilises par le MCP server :
- `POST /api/workspaces/{wid}/drive` (create page)
- `PUT /api/drive/{id}` (update page)
- `GET /api/drive/{id}` (read page)
- `GET /api/workspaces/{wid}/drive` (list drive)
- `POST /api/workspaces/{wid}/tasks` (create task)
- `GET /api/workspaces/{wid}/tasks` (list tasks)
- `POST /api/workspaces/{wid}/tasks/{id}/comments` (add comment)
- `POST /api/workspaces/{wid}/tasks/{id}/blocker` (create blocker)

**Securite** : Le middleware valide que `{wid}` dans l'URL correspond au `WorkspaceId` de l'agent. Un agent ne peut pas acceder aux ressources d'un autre workspace.

### 2. Tracking de l'origine agent

Ajouter un champ optionnel `createdByAgentId` (ou similaire) aux entites Drive et Task pour tracer les elements crees par un agent vs. un humain. Utile pour l'UI (badge "cree par Agent X").

---

## Injection automatique du MCP dans les CLI

### Principe

Au moment du spawn de chaque CLI (Claude, Codex, Gemini), l'agent :
1. Genere un fichier `.mcp.json` temporaire dans `/tmp/sidehub-mcp-{sessionId}.json`
2. Passe le fichier en argument au CLI
3. Le CLI lance le MCP server Side Hub comme process enfant (stdio)
4. Le fichier temporaire est nettoye a la fin de la session

### Contenu du `.mcp.json` genere

```json
{
  "mcpServers": {
    "sidehub": {
      "command": "/usr/local/lib/sidehub-agent/sidehub-mcp",
      "env": {
        "SIDEHUB_API_URL": "https://api.sidehub.io",
        "SIDEHUB_AGENT_TOKEN": "sh_agent_...",
        "SIDEHUB_WORKSPACE_ID": "guid",
        "SIDEHUB_TASK_ID": "guid-or-empty",
        "SIDEHUB_TASK_TITLE": "titre-de-la-tache"
      }
    }
  }
}
```

### Modifications par provider

#### Claude CLI

Fichier : `WebSocketClient.cs` (ligne ~740)

Ajouter `--mcp-config <path>` aux arguments :
```
claude --sdk-url <url> --model <model> --mcp-config /tmp/sidehub-mcp-{sessionId}.json ...
```

#### Codex CLI

Fichier : `CodexBridge.cs` (ligne ~95)

Codex supporte MCP via un fichier de config. Verifier le flag exact (`--mcp-config` ou variable d'environnement `CODEX_MCP_CONFIG`). Si Codex ne supporte pas le flag directement, placer le `.mcp.json` dans le working directory (Codex le detecte automatiquement).

#### Gemini CLI

Fichier : `GeminiBridge.cs` (ligne ~258)

Meme approche que Claude : `--mcp-config <path>` si supporte par le CLI Gemini. Sinon, placer dans le working directory ou utiliser la variable d'environnement appropriee.

> **Action requise** : Verifier la documentation de chaque CLI pour le flag exact de configuration MCP. Les noms de flags peuvent varier.

---

## Plan d'implementation

### Etape 1 : Backend - Auth agent REST (repo `side_hub`)

**Fichiers a creer/modifier :**
- [ ] `SideHub.Api/Authentication/AgentTokenAuthenticationHandler.cs` (nouveau)
- [ ] `SideHub.Api/Authentication/AgentTokenAuthenticationOptions.cs` (nouveau)
- [ ] `Program.cs` : enregistrer le scheme AgentToken
- [ ] Endpoints concernes : ajouter `.RequireAuthorization(policy)` avec policy acceptant Agent OU User

**Critere de validation :**
```bash
curl -H "X-Agent-Token: sh_agent_..." https://api.sidehub.io/api/workspaces/{wid}/drive
# Doit retourner 200 avec le contenu du Drive
```

### Etape 2 : MCP Server - Projet et protocole (repo `side_hub_agent`)

**Fichiers a creer :**
- [ ] `SideHub.Mcp/SideHub.Mcp.csproj` (console app .NET, self-contained linux-x64)
- [ ] `SideHub.Mcp/Program.cs` (boucle stdio JSON-RPC, init MCP)
- [ ] `SideHub.Mcp/McpServer.cs` (dispatch initialize, tools/list, tools/call)
- [ ] `SideHub.Mcp/SideHubApiClient.cs` (HttpClient configure avec token agent)

**Protocole MCP a implementer :**
1. `initialize` : retourne server info + capabilities (tools)
2. `tools/list` : retourne le catalogue d'outils avec schemas JSON
3. `tools/call` : execute l'outil, appelle l'API Side Hub, retourne le resultat

**Critere de validation :**
```bash
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}' | ./sidehub-mcp
# Doit retourner une reponse JSON-RPC valide avec la liste des tools
```

### Etape 3 : MCP Server - Outils Drive (repo `side_hub_agent`)

**Fichiers a creer :**
- [ ] `SideHub.Mcp/Tools/DriveTools.cs`

**Outils :**
- [ ] `sidehub_create_drive_page` (title, content, parentId?)
- [ ] `sidehub_update_drive_page` (pageId, title?, content?)
- [ ] `sidehub_read_drive_page` (pageId)
- [ ] `sidehub_list_drive` (parentId?)

**Critere de validation :**
```bash
echo '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"sidehub_create_drive_page","arguments":{"title":"Test","content":"Hello"}}}' | SIDEHUB_API_URL=... SIDEHUB_AGENT_TOKEN=... SIDEHUB_WORKSPACE_ID=... ./sidehub-mcp
# Doit creer une page et retourner l'ID
```

### Etape 4 : MCP Server - Outils Taches (repo `side_hub_agent`)

**Fichiers a creer :**
- [ ] `SideHub.Mcp/Tools/TaskTools.cs`

**Outils :**
- [ ] `sidehub_create_task` (title, description?, type?)
- [ ] `sidehub_list_tasks` (status?)
- [ ] `sidehub_add_task_comment` (taskId?, text) -- taskId par defaut = env SIDEHUB_TASK_ID
- [ ] `sidehub_create_blocker` (taskId?, reason) -- idem

### Etape 5 : Injection MCP config dans les CLI (repo `side_hub_agent`)

**Fichiers a modifier :**
- [ ] `SideHub.Agent/WebSocketClient.cs` : generer `.mcp.json` + ajouter `--mcp-config` au spawn Claude
- [ ] `SideHub.Agent/CodexBridge.cs` : idem pour Codex
- [ ] `SideHub.Agent/GeminiBridge.cs` : idem pour Gemini
- [ ] Methode commune `McpConfigGenerator.GenerateConfig(sessionId, agentToken, workspaceId, taskId?)` dans un nouveau fichier utilitaire

**Critere de validation :**
- Lancer une tache pipeline via le cockpit Side Hub
- L'agent Claude voit les outils `sidehub_*` dans sa liste d'outils
- L'agent peut creer une page Drive sans instruction explicite du prompt

### Etape 6 : Build et deploiement

**Modifications au build/deploy :**
- [ ] Ajouter `SideHub.Mcp` au script de publish (`dotnet publish SideHub.Mcp/SideHub.Mcp.csproj ...`)
- [ ] Copier le binaire `sidehub-mcp` dans `/usr/local/lib/sidehub-agent/`
- [ ] Mettre a jour le CLAUDE.md avec les nouvelles commandes de build

**Commandes de build :**
```bash
export PATH="$HOME/.dotnet:$PATH"
cd /root/Github/side_hub_agent

# Build agent (existant)
dotnet publish SideHub.Agent/SideHub.Agent.csproj -c Release -o SideHub.Agent/publish --self-contained -r linux-x64 --verbosity quiet

# Build MCP server (nouveau)
dotnet publish SideHub.Mcp/SideHub.Mcp.csproj -c Release -o SideHub.Mcp/publish --self-contained -r linux-x64 --verbosity quiet

# Deployer les deux
cp -r SideHub.Agent/publish/* /usr/local/lib/sidehub-agent/
cp -r SideHub.Mcp/publish/* /usr/local/lib/sidehub-agent/
```

---

## Permissions et securite

### Outils en mode `plan` (read-only)

Quand l'agent tourne en mode `plan`, seuls les outils de lecture sont autorises :
- `sidehub_read_drive_page`
- `sidehub_list_drive`
- `sidehub_list_tasks`

Les outils d'ecriture (`create_drive_page`, `create_task`, etc.) doivent retourner une erreur claire : `"Tool not available in plan mode. Switch to execute mode to create content."`

**Implementation** : Le mode est passe via env var `SIDEHUB_PIPELINE_MODE` (plan/execute/terminal). Le MCP server filtre les outils selon le mode.

### Validation workspace

Le MCP server recoit `SIDEHUB_WORKSPACE_ID` via env var. Tous les appels API sont scopes a ce workspace. Le middleware backend valide en plus que le workspace dans l'URL correspond a celui de l'agent.

### Pas d'outils destructifs

Phase 1 n'expose pas :
- `delete` (taches, pages, fichiers)
- `complete_task` (l'agent ne peut pas se marquer comme termine -- c'est le pipeline orchestrator qui gere ca)
- `assign_agent` (l'agent ne peut pas s'auto-assigner des taches)

Ces garde-fous empechent les effets de bord indesirables.

---

## Points d'attention

### MCP config detection par provider

Chaque CLI detecte les MCP servers differemment. A verifier :

| Provider | Flag connu | Alternative |
|----------|-----------|-------------|
| Claude | `--mcp-config <path>` | `.mcp.json` dans le working directory |
| Codex | A verifier | `.codex/mcp.json` ou env var |
| Gemini | A verifier | `.gemini/mcp.json` ou env var |

> Risque : si un provider ne supporte pas le flag `--mcp-config`, il faudra placer le fichier dans le working directory du projet, ce qui peut entrer en conflit avec un `.mcp.json` existant. Solution de fallback : merger la config Side Hub dans le `.mcp.json` existant du projet.

### Performance

Le MCP server est un process qui reste actif pendant toute la session CLI. Chaque tool call = 1 requete HTTP vers l'API Side Hub. La latence ajoutee est celle d'un aller-retour HTTPS (~50-200ms). Acceptable pour des operations ponctuelles (creer une page, ajouter un commentaire).

### Limites de taille du contenu Drive

Les pages Drive n'ont pas de limite de taille explicite cote backend, mais le MCP server devrait limiter `content` a ~100KB pour eviter les abus. Les fichiers binaires ne sont pas supportes via MCP (utiliser l'upload Drive classique).

---

## Ordre de priorite

| # | Etape | Effort | Dependances |
|---|-------|--------|-------------|
| 1 | Auth agent REST (backend) | Faible | Aucune |
| 2 | MCP Server - protocole de base | Moyen | Aucune (parallelisable avec 1) |
| 3 | MCP Server - outils Drive | Moyen | 1 + 2 |
| 4 | MCP Server - outils Taches | Faible | 1 + 2 |
| 5 | Injection MCP config dans CLI | Moyen | 2 |
| 6 | Build et deploiement | Faible | 3 + 4 + 5 |

Les etapes 1 et 2 sont parallelisables. Le chemin critique est : `1 -> 3 -> 6` et `2 -> 5 -> 6`.

---

## Resultat attendu

Un utilisateur Side Hub lance une tache "Audit SEO de example.com" via le cockpit. L'agent Claude recoit le prompt et, grace aux outils MCP Side Hub automatiquement injectes :

1. Analyse le site via Playwright (MCP existant)
2. Cree une page Drive "Rapport d'audit SEO - example.com" avec `sidehub_create_drive_page`
3. Cree des taches de suivi ("Corriger les balises title", "Optimiser les images") avec `sidehub_create_task`
4. Ajoute un commentaire sur la tache originale avec le resume de l'audit via `sidehub_add_task_comment`

Tout cela sans aucune configuration manuelle de l'utilisateur -- le MCP Side Hub est injecte automatiquement par l'agent.
