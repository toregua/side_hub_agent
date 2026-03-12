# Audit de Sécurité — SideHub Agent

**Date:** 2026-03-12
**Cible:** `side_hub_agent` (agent .NET 10, exécution distante via WebSocket)
**Scope:** Tout le code source dans `SideHub.Agent/`

## Résumé

L'agent SideHub est un agent d'exécution distante qui reçoit des commandes d'un backend via WebSocket et les exécute localement (commandes shell, écriture de fichiers, sessions PTY, spawn de CLI AI). Par nature, c'est un outil très privilégié. L'audit identifie **14 vulnérabilités** classées par criticité.

| Criticité | Nombre |
|-----------|--------|
| Critique  | 3      |
| Élevé     | 4      |
| Moyen     | 4      |
| Faible    | 3      |

---

## CRITIQUE

### 1. Écriture de fichiers arbitraire sans restriction de chemin

**Fichier:** `WebSocketClient.cs:332-404`

Le protocole `file.write.start`/`file.write.chunk`/`file.write.end` accepte un `path` arbitraire du backend et écrit à cet emplacement sans aucune validation :

```csharp
// Line 370 — aucune vérification du chemin
await File.WriteAllBytesAsync(state.Path, bytes, ct);
```

Un backend compromis ou un attaquant MITM pourrait écrire sur `/etc/passwd`, `/root/.ssh/authorized_keys`, `/etc/cron.d/backdoor`, etc.

**Recommandation:** Restreindre les chemins d'écriture au `workingDirectory` de l'agent. Valider que le chemin résolu (après `Path.GetFullPath`) commence bien par le répertoire autorisé (canonicalize pour éviter les traversées via `../`).

### 2. Pas d'authentification sur le proxy WebSocket local

**Fichier:** `AgentSdkProxy.cs:156-215`

Le serveur WebSocket local sur `127.0.0.1:{port}` accepte toute connexion correspondant à `/ws/agent/{sessionId}`. Il n'y a aucune vérification d'authentification (pas de token, pas de header vérifié) :

```csharp
// Line 181 — le sessionId est le seul "secret"
var sessionId = segments[2];
if (!_sessions.TryGetValue(sessionId, out var session)) { ... }
```

Tout processus local qui connaît (ou peut deviner/bruteforcer) un sessionId peut se connecter et interagir avec la session Claude/Codex/Gemini. Les session IDs sont des UUIDs, donc difficilement devinables, mais ils sont loggés en clair.

**Recommandation:** Ajouter un token d'authentification unique par session, vérifié sur la connexion WebSocket (via query param ou header).

### 3. Pas de limite de taille sur les messages WebSocket

**Fichier:** `WebSocketClient.cs:187-211`, `AgentSdkProxy.cs:223-266`

Le buffer de messages grandit sans limite :

```csharp
// Line 202 — croissance illimitée
messageBuffer.AddRange(buffer.Take(result.Count));
```

Un backend malveillant ou compromis pourrait envoyer un message arbitrairement grand, causant un OOM (denial of service).

**Recommandation:** Implémenter une limite de taille maximale (ex: 50MB) et fermer la connexion si dépassée.

---

## ÉLEVÉ

### 4. Token exposé dans les URLs

**Fichier:** `WebSocketClient.cs:648`, `AgentSdkProxy.cs:73`

Les tokens de session sont passés via les query strings des URLs :

```csharp
// Line 648
var token = System.Web.HttpUtility.ParseQueryString(uriObj.Query)["token"] ?? "";
```

Les tokens dans les URLs se retrouvent dans les logs de proxy, les historiques navigateur, les en-têtes `Referer`, et les logs du serveur. De plus, le token est utilisé dans l'URL backend (`sdkUrl`) reçue du serveur.

**Recommandation:** Transmettre les tokens via des headers d'authentification plutôt que des query params.

### 5. Dictionnaires non thread-safe utilisés en multi-thread

**Fichier:** `WebSocketClient.cs:19-22`

```csharp
private readonly Dictionary<string, (string Path, StringBuilder Data, string? PtyPaste)> _pendingFileWrites = new();
private readonly Dictionary<string, System.Diagnostics.Process> _claudeSdkProcesses = new();
private readonly Dictionary<string, CodexBridge> _codexBridges = new();
private readonly Dictionary<string, GeminiBridge> _geminiBridges = new();
```

Ces dictionnaires sont accédés depuis le thread principal (boucle de réception) ET depuis des tâches en arrière-plan (`Task.Run` pour le monitoring de processus). `Dictionary<>` n'est pas thread-safe — les accès concurrents peuvent causer des corruptions de données silencieuses, des exceptions, ou un comportement indéfini.

**Recommandation:** Remplacer par `ConcurrentDictionary<>` ou protéger les accès avec un `lock`.

### 6. Mode `bypassPermissions` forcé par le backend

**Fichier:** `WebSocketClient.cs:633-639`

```csharp
var permissionMode = rawPermissionMode switch
{
    "pipeline" => "bypassPermissions",
    "auto" => "bypassPermissions",
    "safe" => "default",
    _ => rawPermissionMode
};
```

Les modes `pipeline` et `auto` désactivent toutes les vérifications de sécurité du CLI Claude (file edits, command execution, etc.). Si le backend est compromis, un attaquant obtient une exécution sans aucune restriction via Claude Code.

**Recommandation:** Permettre à l'agent de configurer localement un mode de permission maximum (ex: ne jamais accepter `bypassPermissions` sans approbation locale). Ajouter un paramètre `maxPermissionMode` dans `agent.json`.

### 7. Fuite de variables d'environnement dans les sessions PTY

**Fichier:** `pty-helper/index.js:63-69`

```javascript
env: {
    ...process.env,  // ALL parent env vars leaked
    TERM: 'xterm-256color',
    ...
}
```

Toutes les variables d'environnement du processus agent (qui pourrait contenir des clés API, tokens, mots de passe) sont transmises aux sessions PTY, potentiellement visibles par l'utilisateur ou les commandes exécutées.

**Recommandation:** Filtrer les variables d'environnement sensibles (`AGENT_TOKEN`, `API_KEY`, etc.) ou ne passer qu'une liste blanche de variables.

---

## MOYEN

### 8. Pattern `.gitignore` insuffisant pour les fichiers de configuration

**Fichier:** `.gitignore:38`

```
# Local configuration (contains secrets)
agent.json
```

Seul `agent.json` est exclu. Les autres noms de fichiers de config (`agent2.json`, `rose-d-or-agent.json`, `stremio-agent.json`) ne sont PAS couverts par ce pattern. Si le répertoire `.sidehub/` est créé dans le repo, ces fichiers avec tokens pourraient être commités accidentellement.

**Recommandation:** Ajouter `.sidehub/` au `.gitignore` pour exclure tout le répertoire de configuration.

### 9. Logging excessif d'informations sensibles

**Fichiers:** `WebSocketClient.cs:293,623,653`

```csharp
Log($"Executing: {message.Command}");           // Commandes potentiellement sensibles
Log($"CLI will connect to local proxy: {localUrl}");  // URL avec session info
Log($"Spawning Claude CLI for session {sessionId} with SDK URL: {sdkUrl}"); // URL complète avec token
```

Les commandes exécutées, URLs avec tokens et IDs de session sont loggés en clair dans les fichiers de log (accessibles à tout utilisateur pouvant lire `/root/Github/.sidehub/run/`).

**Recommandation:** Masquer les tokens dans les logs (ex: `sh_agent_***`). Ne pas logger les URLs complètes contenant des tokens.

### 10. Pas de validation du schéma WebSocket (wss vs ws)

**Fichier:** `AgentConfig.cs:88-117`

La validation de config ne vérifie pas que `sidehubUrl` utilise `wss://` (TLS). Un utilisateur pourrait configurer `ws://` (non chiffré), exposant le token d'authentification et toutes les communications en clair.

```csharp
if (string.IsNullOrWhiteSpace(SidehubUrl))
    errors.Add("sidehubUrl is required");
// Pas de vérification wss://
```

**Recommandation:** Valider que l'URL utilise le schéma `wss://` en production, ou au minimum avertir si `ws://` est utilisé.

### 11. Escaping shell fragile

**Fichier:** `CommandExecutor.cs:105-129`

L'escaping pour `cmd.exe` est **inexistant** :

```csharp
"cmd" => ("cmd.exe", $"/c {command}"),  // Aucun escaping !
```

Et l'escaping bash/zsh ne couvre que `\` et `"` :

```csharp
private static string EscapeForShell(string command)
{
    return command.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
```

Bien que l'intention soit d'exécuter des commandes arbitraires (by design), l'utilisation de `ProcessStartInfo.Arguments` (string) au lieu de `ArgumentList` rend l'escaping dépendant de l'implémentation .NET pour le parsing d'arguments.

**Recommandation:** Utiliser `ArgumentList` au lieu de `Arguments` pour un passage d'arguments safe à travers toutes les plateformes :

```csharp
startInfo.ArgumentList.Add("-l");
startInfo.ArgumentList.Add("-c");
startInfo.ArgumentList.Add(command);
```

---

## FAIBLE

### 12. PID file race condition

**Fichier:** `DaemonManager.cs:56-77`

La vérification `IsRunning()` lit le PID file et vérifie si le processus existe. Entre la lecture et l'utilisation, le PID pourrait être recyclé par l'OS (un autre processus pourrait recevoir le même PID). La vérification `ProcessName.Contains("sidehub-agent")` mitigue partiellement ce risque.

### 13. Buffer de messages proxy sans expiration

**Fichier:** `AgentSdkProxy.cs:509-517`

Le buffer de messages (1000 messages max) ne possède pas de politique d'expiration temporelle. Des messages très anciens pourraient être rejoués lors d'une reconnexion, avec des effets inattendus.

**Recommandation:** Ajouter un TTL sur les messages bufferisés (ex: 5 minutes).

### 14. `DisposeAsync` synchrone bloquant

**Fichier:** `WebSocketClient.cs:1013`

```csharp
bridge.DisposeAsync().AsTask().GetAwaiter().GetResult(); // Blocking call on async
```

L'appel synchrone bloquant (`GetResult()`) d'une méthode async peut causer des deadlocks dans certains contextes de synchronisation.

**Recommandation:** Rendre `HandleAgentSdkStop` async et utiliser `await`.

---

## Améliorations architecturales recommandées

| # | Recommandation | Impact |
|---|----------------|--------|
| A | **Sandboxer les écritures fichier** — restreindre `file.write` au `workingDirectory` | Empêche l'écriture arbitraire sur le FS |
| B | **Ajouter `.sidehub/` au .gitignore** | Prévient la fuite de credentials |
| C | **Remplacer `Dictionary` par `ConcurrentDictionary`** pour les maps de sessions | Corrige les data races |
| D | **Utiliser `ArgumentList`** au lieu de `Arguments` dans `CommandExecutor` | Élimine les problèmes d'escaping |
| E | **Ajouter un token d'auth au proxy local** | Empêche le détournement de session local |
| F | **Configurer un `maxPermissionMode` local** dans `agent.json` | L'opérateur contrôle le niveau de permission max |
| G | **Limiter la taille des messages WS** | Prévient les attaques OOM |
