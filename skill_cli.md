# Side Hub Skill + CLI - Plan de Developpement

## Objectif

Donner aux LLM (Claude, Codex, Gemini) la capacite d'interagir nativement avec Side Hub (Drive, taches, commentaires) via un **CLI `sidehub-cli`** combine a un **skill installe une fois** dans le working directory de chaque provider.

Cette approche remplace le plan MCP initial : plus simple, universelle (tout provider qui peut executer du Bash), et sans protocole JSON-RPC a maintenir.

---

## Architecture

```
Claude/Codex/Gemini CLI
    |
    |-- Bash --> /usr/local/lib/sidehub-agent/sidehub-cli <commande>
    |                |
    |                |-- HTTP --> api.sidehub.io (REST API)
    |                |           (auth: X-Agent-Token)
    |                |
    |                |-- Contexte via env vars:
    |                    SIDEHUB_API_URL
    |                    SIDEHUB_AGENT_TOKEN
    |                    SIDEHUB_WORKSPACE_ID
    |                    SIDEHUB_TASK_ID (optionnel)
    |                    SIDEHUB_TASK_TITLE (optionnel)
    |
    |-- Skill installe (fichier config) --> Le LLM decouvre le CLI via CLAUDE.md / AGENTS.md / GEMINI.md
```

### Pourquoi Skill + CLI plutot que MCP ?

| Critere | MCP | Skill + CLI |
|---------|-----|-------------|
| Compatibilite providers | Chaque provider detecte MCP differemment | Universel — tout provider qui execute du Bash |
| Complexite | JSON-RPC 2.0, stdio framing, process enfant | CLI standard, sous-commandes, stdout texte/JSON |
| Testabilite | Penible (stdin/stdout JSON-RPC) | `sidehub-cli drive list` dans un terminal |
| Maintenance | Gerer le protocole + injection `.mcp.json` par provider | Un seul binaire + un fichier texte de skill |
| Process | Process enfant actif toute la session | Execution ponctuelle a chaque appel |

---

## Composant 1 : CLI `sidehub-cli`

### Structure du projet

```
side_hub_agent/
  SideHub.Agent/              (existant)
  SideHub.Cli/                (nouveau)
    SideHub.Cli.csproj
    Program.cs                 (point d'entree, dispatch sous-commandes)
    SideHubApiClient.cs        (client HTTP pour l'API Side Hub)
    Commands/
      DriveCommands.cs         (drive list, drive read, drive create, drive update)
      TaskCommands.cs          (task list, task create, task comment, task blocker)
```

### Catalogue de commandes

#### Drive

```bash
# Lister les elements du Drive
sidehub-cli drive list [--parent <folderId>]
# Output: tableau (id, title, type, updatedAt)

# Lire une page
sidehub-cli drive read <pageId>
# Output: contenu markdown de la page

# Creer une page
sidehub-cli drive create --title "Rapport audit" --content "# Contenu markdown..."  [--parent <folderId>]
# Output: id de la page creee

# Mettre a jour une page
sidehub-cli drive update <pageId> [--title "Nouveau titre"] [--content "Nouveau contenu..."]
# Output: confirmation
```

#### Taches

```bash
# Lister les taches
sidehub-cli task list [--status pending|in-progress|completed]
# Output: tableau (id, title, status, type, assignee)

# Creer une tache
sidehub-cli task create --title "Fix les balises title" [--description "..."] [--type feature|bug-fix|pr-review]
# Output: id de la tache creee

# Ajouter un commentaire
sidehub-cli task comment [<taskId>] --text "Avancement : 50% termine"
# Si taskId omis, utilise SIDEHUB_TASK_ID (tache en cours)

# Signaler un blocage
sidehub-cli task blocker [<taskId>] --reason "Acces refuse au repo X"
# Si taskId omis, utilise SIDEHUB_TASK_ID
```

### Format de sortie

- Par defaut : texte lisible (tableaux, messages de confirmation)
- Option `--json` : sortie JSON brute (utile si le LLM veut parser la reponse)
- Erreurs sur stderr avec code de retour non-zero

### Configuration via variables d'environnement

| Variable | Description | Source |
|----------|-------------|--------|
| `SIDEHUB_API_URL` | URL de l'API Side Hub | Derivee de `sidehubUrl` dans agent config |
| `SIDEHUB_AGENT_TOKEN` | Token d'authentification agent | `agentToken` dans agent config |
| `SIDEHUB_WORKSPACE_ID` | ID du workspace courant | `workspaceId` dans agent config |
| `SIDEHUB_TASK_ID` | ID de la tache en cours (optionnel) | Passe par l'agent au spawn de session |
| `SIDEHUB_TASK_TITLE` | Titre de la tache en cours (optionnel) | Passe par l'agent au spawn de session |

---

## Composant 2 : Skill (fichiers installes par provider)

### Principe

Au lieu d'injecter le skill dans chaque system prompt (couteux en tokens, repetitif), l'agent **ecrit une fois** un fichier de skill dans le working directory. Chaque provider a sa convention :

| Provider | Fichier | Chargement |
|----------|---------|------------|
| Claude | `CLAUDE.md` (section ajoutee) ou `.claude/commands/sidehub.md` | Lu automatiquement au demarrage de session |
| Codex | `AGENTS.md` | Lu automatiquement au demarrage de session |
| Gemini | `GEMINI.md` | Lu automatiquement au demarrage de session |

L'agent s'assure que le fichier existe dans le working directory **avant de spawner le CLI**. S'il existe deja, il ne le recree pas (idempotent).

### Contenu du skill (commun a tous les providers)

```markdown
# Side Hub Integration

Tu as acces au CLI `sidehub-cli` pour interagir avec le workspace Side Hub.
Les variables d'environnement sont deja configurees dans ta session.

## Commandes disponibles

### Drive (documentation, livrables)
- `sidehub-cli drive list` — Lister les pages/dossiers du Drive
- `sidehub-cli drive read <pageId>` — Lire le contenu d'une page
- `sidehub-cli drive create --title "..." --content "..."` — Creer une page
- `sidehub-cli drive update <pageId> --title "..." --content "..."` — Modifier une page

### Taches
- `sidehub-cli task list` — Lister les taches du workspace
- `sidehub-cli task create --title "..." --description "..."` — Creer une tache
- `sidehub-cli task comment --text "..."` — Commenter la tache en cours
- `sidehub-cli task blocker --reason "..."` — Signaler un blocage sur la tache en cours

## Quand utiliser ces commandes

- **Livrables** : quand tu produis un resultat significatif (rapport, analyse, documentation),
  cree une page Drive avec `sidehub-cli drive create`
- **Avancement** : signale ta progression via `sidehub-cli task comment` a chaque etape cle
- **Blocage** : si tu es bloque, utilise `sidehub-cli task blocker` au lieu de tourner en boucle
- **Sous-taches** : si tu identifies du travail supplementaire, cree des taches avec `sidehub-cli task create`

## Conventions
- Contenu Drive en markdown
- Commentaires concis (1-3 phrases)
- Ne pas creer de pages Drive pour des resultats intermediaires trivials
```

### Installation par provider

#### Claude

**Approche preferee** : ecrire `.claude/commands/sidehub.md` dans le working directory.
Claude le detecte automatiquement comme skill invocable (`/sidehub`), et il apparait dans la liste des skills disponibles.

**Alternative** : ajouter une section `# Side Hub Integration` dans le `CLAUDE.md` du working directory (si il existe deja, append ; sinon, creer).

#### Codex

Ecrire `AGENTS.md` dans le working directory (ou appender si existant).
Codex lit ce fichier au demarrage de chaque session.

#### Gemini

Ecrire `GEMINI.md` dans le working directory (ou appender si existant).
Gemini lit ce fichier au demarrage de chaque session.

### Implementation dans l'agent

**Fichier** : `SideHub.Agent/SkillInstaller.cs` (nouveau)

```csharp
public class SkillInstaller
{
    // Ecrit les fichiers skill dans le working directory si absents
    public static void EnsureSkillFiles(string workingDirectory, string provider)
    {
        // Contenu commun du skill (embarque dans l'agent)
        var skillContent = GetSkillContent();

        switch (provider)
        {
            case "claude":
                EnsureClaudeSkill(workingDirectory, skillContent);
                break;
            case "codex":
                EnsureAgentsMd(workingDirectory, skillContent);
                break;
            case "gemini":
                EnsureGeminiMd(workingDirectory, skillContent);
                break;
        }
    }
}
```

Appele depuis `WebSocketClient.cs`, `CodexBridge.cs`, `GeminiBridge.cs` **avant** le spawn du process CLI.

### Injection des env vars

Les env vars restent injectees au spawn via `ProcessStartInfo.Environment` (inchange) :

```csharp
startInfo.Environment["SIDEHUB_API_URL"] = apiUrl;
startInfo.Environment["SIDEHUB_AGENT_TOKEN"] = _config.AgentToken;
startInfo.Environment["SIDEHUB_WORKSPACE_ID"] = _config.WorkspaceId;
startInfo.Environment["SIDEHUB_TASK_ID"] = taskId ?? "";
startInfo.Environment["SIDEHUB_TASK_TITLE"] = taskTitle ?? "";
```

---

## Composant 3 : Changements backend (repo `side_hub`)

### Authentification agent sur les endpoints REST

**Probleme** : Les endpoints REST n'acceptent que des JWT utilisateur. Les tokens agent (`sh_agent_*`) ne fonctionnent que sur le WebSocket.

**Solution** : Ajouter un scheme d'authentification `AgentToken`.

**Fichiers a creer/modifier :**
- `SideHub.Api/Authentication/AgentTokenAuthenticationHandler.cs` (nouveau)
- `SideHub.Api/Authentication/AgentTokenAuthenticationOptions.cs` (nouveau)
- `Program.cs` : enregistrer le scheme AgentToken

**Logique** :
```
1. Extraire le token de l'en-tete X-Agent-Token (ou Authorization: AgentToken sh_agent_...)
2. Hash SHA256 + lookup en base (meme logique que AgentWebSocketHandler)
3. Creer un ClaimsPrincipal avec AgentId, WorkspaceId, Role "Agent"
4. Valider que {wid} dans l'URL correspond au WorkspaceId de l'agent
```

**Endpoints concernes** :
- `GET /api/workspaces/{wid}/drive` (list)
- `POST /api/workspaces/{wid}/drive` (create)
- `GET /api/drive/{id}` (read)
- `PUT /api/drive/{id}` (update)
- `GET /api/workspaces/{wid}/tasks` (list)
- `POST /api/workspaces/{wid}/tasks` (create)
- `POST /api/workspaces/{wid}/tasks/{id}/comments` (comment)
- `POST /api/workspaces/{wid}/tasks/{id}/blocker` (blocker)

---

## Plan d'implementation

### Etape 1 : Backend - Auth agent REST (repo `side_hub`)

- [ ] Creer `AgentTokenAuthenticationHandler.cs`
- [ ] Enregistrer le scheme dans `Program.cs`
- [ ] Ajouter l'autorisation Agent sur les endpoints concernes
- [ ] Tester : `curl -H "X-Agent-Token: sh_agent_..." https://api.sidehub.io/api/workspaces/{wid}/drive` → 200

### Etape 2 : CLI - Projet et client HTTP (repo `side_hub_agent`)

- [ ] Creer `SideHub.Cli/SideHub.Cli.csproj` (console app .NET, self-contained linux-x64)
- [ ] Creer `SideHub.Cli/Program.cs` (parsing sous-commandes, dispatch)
- [ ] Creer `SideHub.Cli/SideHubApiClient.cs` (HttpClient avec X-Agent-Token, base URL)
- [ ] Tester : `SIDEHUB_API_URL=... SIDEHUB_AGENT_TOKEN=... sidehub-cli drive list` → affiche le Drive

### Etape 3 : CLI - Commandes Drive

- [ ] `sidehub-cli drive list [--parent <id>]`
- [ ] `sidehub-cli drive read <pageId>`
- [ ] `sidehub-cli drive create --title "..." --content "..." [--parent <id>]`
- [ ] `sidehub-cli drive update <pageId> [--title "..."] [--content "..."]`

### Etape 4 : CLI - Commandes Taches

- [ ] `sidehub-cli task list [--status <status>]`
- [ ] `sidehub-cli task create --title "..." [--description "..."] [--type <type>]`
- [ ] `sidehub-cli task comment [<taskId>] --text "..."`
- [ ] `sidehub-cli task blocker [<taskId>] --reason "..."`

### Etape 5 : Installation skill + env vars dans les CLI (repo `side_hub_agent`)

- [ ] Creer `SideHub.Agent/SkillInstaller.cs` : ecrit les fichiers skill dans le working directory selon le provider
- [ ] Modifier `WebSocketClient.cs` : appeler SkillInstaller + injecter env vars au spawn Claude
- [ ] Modifier `CodexBridge.cs` : appeler SkillInstaller + injecter env vars au spawn Codex
- [ ] Modifier `GeminiBridge.cs` : appeler SkillInstaller + injecter env vars au spawn Gemini
- [ ] Tester : lancer une session via le cockpit, verifier que le LLM voit les commandes `sidehub-cli`

### Etape 6 : Build et deploiement

- [ ] Ajouter `SideHub.Cli` au script de publish
- [ ] Copier le binaire `sidehub-cli` dans `/usr/local/lib/sidehub-agent/`
- [ ] S'assurer que `/usr/local/lib/sidehub-agent/` est dans le PATH des sessions CLI
- [ ] Mettre a jour le CLAUDE.md avec les nouvelles commandes de build

**Commandes de build :**
```bash
export PATH="$HOME/.dotnet:$PATH"
cd /root/Github/side_hub_agent

# Build agent (existant)
dotnet publish SideHub.Agent/SideHub.Agent.csproj -c Release -o SideHub.Agent/publish --self-contained -r linux-x64 --verbosity quiet

# Build CLI (nouveau)
dotnet publish SideHub.Cli/SideHub.Cli.csproj -c Release -o SideHub.Cli/publish --self-contained -r linux-x64 --verbosity quiet

# Deployer les deux
cp -r SideHub.Agent/publish/* /usr/local/lib/sidehub-agent/
cp -r SideHub.Cli/publish/* /usr/local/lib/sidehub-agent/
```

---

## Gestion du mode plan (read-only)

Quand l'agent tourne en mode `plan`, le fichier skill installe indique que seules les commandes de lecture sont autorisees :
- `sidehub-cli drive list`, `sidehub-cli drive read`, `sidehub-cli task list`

Le CLI lui-meme n'a pas besoin de bloquer les commandes d'ecriture — c'est le skill qui guide le LLM. Mais en securite defense-en-profondeur, on peut passer `SIDEHUB_PIPELINE_MODE=plan` et faire refuser les commandes d'ecriture dans le CLI aussi.

---

## Securite

- **Pas d'outils destructifs** (Phase 1) : pas de delete, pas de complete_task, pas d'assign_agent
- **Validation workspace** : le backend valide que le workspace dans l'URL correspond a celui de l'agent
- **Token non expose** : le token est dans une env var, pas dans le system prompt ni dans les arguments CLI
- **Limite de contenu** : le CLI limite `--content` a ~100KB pour le Drive

---

## Ordre de priorite

| # | Etape | Effort | Dependances | Parallelisable |
|---|-------|--------|-------------|----------------|
| 1 | Auth agent REST (backend) | Faible | Aucune | Oui (avec 2) |
| 2 | CLI - projet + client HTTP | Moyen | Aucune | Oui (avec 1) |
| 3 | CLI - commandes Drive | Moyen | 1 + 2 | Non |
| 4 | CLI - commandes Taches | Faible | 1 + 2 | Oui (avec 3) |
| 5 | Installation skill + env vars | Moyen | 2 | Oui (avec 3/4) |
| 6 | Build et deploiement | Faible | 3 + 4 + 5 | Non |

Chemin critique : `1 → 3 → 6` et `2 → 5 → 6`.

---

## Resultat attendu

Un utilisateur Side Hub lance une tache "Audit SEO de example.com" via le cockpit. L'agent Claude recoit le prompt et, grace au skill installe dans le working directory et au CLI `sidehub-cli` dans le PATH :

1. Analyse le site via les outils disponibles
2. Cree une page Drive avec `sidehub-cli drive create --title "Rapport d'audit SEO - example.com" --content "..."`
3. Cree des taches de suivi avec `sidehub-cli task create --title "Corriger les balises title"`
4. Reporte son avancement avec `sidehub-cli task comment --text "Audit termine, 3 problemes identifies"`

Tout cela sans aucune configuration manuelle — le skill est installe et les env vars sont injectees automatiquement par l'agent au spawn de la session CLI.
