# SideHub Agent

The open source agent that runs coding CLIs (Claude Code, Codex, Gemini CLI, GitHub Copilot CLI) on
your own machines for the [SideHub](https://www.sidehub.io) cockpit.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

The agent is a standalone .NET binary. It opens **one outbound WebSocket** (`wss://`, port 443) to
SideHub and, when asked to, opens terminals (PTYs) in your project folder and types CLI commands into
them. The CLIs run on your machine, with your own subscriptions or API keys; SideHub never sits between
them and the model providers.

This README describes what the agent does, what leaves your machine, and how to install it. The full
threat model is in [SECURITY.md](SECURITY.md).

## How it works

```
                    SideHub cockpit (www.sidehub.io)
                                 ▲
                                 │  wss:// (outbound only, no inbound port)
                                 │
┌────────────────────────────────┼──────────────────────────────────────┐
│ Your machine                   │                                      │
│                                │                                      │
│   sidehub-agent  (one daemon per project folder)                      │
│     ├── WebSocketClient ── pty.start / pty.input / pty.stop …         │
│     ├── NodePtyExecutor ── pty-helper/ (Node.js + node-pty)           │
│     │      └── PTY: bash / zsh / pwsh … in the project folder         │
│     │            └── sidehub-cli launch claude|codex|gemini|copilot   │
│     │                  └── the real CLI ──▶ model provider (your keys)│
│     ├── NotifyFifo ◀── session id, CLI state, CLI exit                │
│     └── Usage/ ──────── token counts read from the CLI transcripts    │
└───────────────────────────────────────────────────────────────────────┘
```

### Everything runs in a terminal

Every execution (a terminal you open in the cockpit, a workflow step, a scheduled prompt) is a PTY on
your machine. The backend starts a PTY (`pty.start`), types a command line into it (`pty.input`) and
receives the terminal output (`pty.output`) as raw ANSI bytes, exactly like a human at a keyboard. There
is no SDK proxy and no structured protocol between SideHub and the CLIs: you can open any run's terminal,
watch it, interrupt it or take it over.

- **`NodePtyExecutor` + `pty-helper/`**: PTYs are driven by a small Node.js helper built on
  [node-pty](https://github.com/microsoft/node-pty) (this is why Node.js is required). Only allowlisted
  shells can be spawned (`bash`, `zsh`, `sh`, `dash`, `fish`, `pwsh`; `cmd`, `powershell`, `pwsh` on
  Windows), always inside the configured `workingDirectory`.
- **Output history**: the recent output of each PTY is kept in memory (`PtyOutputBuffer`) and replayed
  when the backend reconnects, so a terminal survives a network blip. PTYs idle for 30 minutes are
  stopped.

### `sidehub-cli launch`: the single entry point for coding CLIs

Every coding CLI is started through `sidehub-cli launch` (shipped next to the agent), on every OS:

```
sidehub-cli launch [--prompt-env | --prompt-base64 <b64>] <claude|codex|gemini|copilot> [args…]
```

It:

1. finds the real CLI on your `PATH` (skipping the SideHub wrappers);
2. sets the session id up front when the CLI accepts one (`--session-id` for claude, copilot and
   gemini ≥ 0.41), so SideHub can resume the session and read its usage;
3. adds per-invocation state hooks (claude `--settings` hooks, codex `-c notify=`) that report whether
   the CLI is `working`, `waiting-input` or `idle`;
4. gives the CLI the workspace MCP servers the run may use, if any (see below);
5. tells the agent which session runs in the terminal, then runs the CLI.

For runs started by SideHub, the prompt is passed in the `SIDEHUB_PTY_PROMPT` environment variable and
handed to the CLI as a single argument, so the typed line is identical in bash, cmd and PowerShell and
the prompt is never parsed by a shell.

**Workspace MCP servers.** A run's `pty.start` may carry the MCP servers its workflow or scheduler allows
(`mcpServers`, their secrets as `${NAME}` references to workspace secrets of the run). The agent checks
them (`McpServerPolicy`) and passes them in `SIDEHUB_PTY_MCP_SERVERS`; the launcher then configures the
CLI for this invocation only, never through a file in the repository:

| CLI | How | Only these servers? |
|-----|-----|---------------------|
| claude | `--mcp-config <json> --strict-mcp-config`; claude expands `${NAME}` itself | yes |
| codex | `-c mcp_servers.<name>={…}`; secrets read from the environment (`env_vars`, `bearer_token_env_var`, `env_http_headers`, or `/bin/sh -c` for a secret in a stdio server's arguments) | no, added to your own |
| gemini | a temporary system settings file (`GEMINI_CLI_SYSTEM_SETTINGS_PATH`, your machine's system settings copied in) deleted when gemini exits; gemini expands `${NAME}` | yes (`--allowed-mcp-server-names`) |
| copilot | not supported | – |

A server a CLI cannot read its secrets for without putting them on its command line (codex: a secret in
an http URL or inside a header other than `Bearer ${NAME}`; on Windows, anywhere but a variable of the
same name) is left out, with a message in the terminal.

**`cli-wrappers/`** (Linux, macOS): the agent puts small `claude` / `codex` / `gemini` / `copilot`
wrappers first on the terminal's `PATH`. When you type `claude` by hand in a SideHub terminal, the
wrapper simply hands over to `sidehub-cli launch`. Without the launcher, the real CLI runs as if there
were no wrapper.

### `NotifyFifo`: from the terminal back to the agent

`sidehub-cli launch` and the CLI hooks report events (session started, session title, CLI state, CLI
exited) through a per-PTY channel exposed as `$SIDEHUB_PTY_NOTIFY_FIFO`: a `0600` FIFO in an agent-owned
`0700` folder (`.sidehub/run/fifo/`) on Linux and macOS, a named pipe on Windows. The agent treats what
arrives there as untrusted input (validated ids, bounded sizes).

### Token usage (`Usage/`)

After a run, and periodically for CLI sessions typed in terminals, the agent reads the CLI's own local
transcript (`ClaudeTranscriptHarvester` for Claude Code's project JSONL, `CodexRolloutHarvester` for
Codex rollouts) and sends **only token counts per model** (input, output, cache read/write, reasoning):

- `run.usage`: at the end of a run started by SideHub;
- `cli-session.usage`: a cumulative snapshot of a session typed in a terminal, when the CLI exits or
  the PTY closes, and every 10 minutes while it changes.

Reports that cannot be sent (backend unreachable) are queued in `.sidehub/run/pending-usage/` and
replayed at the next connection. Transcripts themselves never leave the machine (except the final
message of a question run, below).

### Questions to a repository (`QuestionCheckout`)

A question run is a read-only headless CLI run (`claude -p`, `codex exec`) answering a question about
the repository. Its `pty.start` carries `SIDEHUB_RUN_KIND=question` and `SIDEHUB_BASE_BRANCH`. It never
runs in the developer's working copy: before spawning the PTY, the agent prepares its own checkout,
`~/.sidehub/qa/<agentId>/checkout`, a detached `git worktree` of the project moved to the tip of
`origin/<branch>` (fetched first, the last fetched tip if the fetch fails), with untracked and ignored
files removed, and starts the PTY there. In the developer's repository only `refs/remotes/origin/<branch>`
and the worktree's metadata (`.git/worktrees/`) change: working tree, index, branches, HEAD and stash
are left alone. While a question run is still in the checkout, the next one reads it as it is. If the
checkout cannot be prepared, the PTY is not started (the backend marks the run `launch-failed`).

When the run ends, the agent reads the CLI's last message from its transcript and sends it as
`run.answer` (queued in `pending-usage/<agentId>/answers/` while the backend is unreachable).

| Variable | Set by | Meaning |
|---|---|---|
| `SIDEHUB_RUN_KIND` | backend | `question` for a question run |
| `SIDEHUB_BASE_BRANCH` | backend | Branch of `origin` the question is about (e.g. `main`) |
| `SIDEHUB_QUESTION_COMMIT` | agent | Commit the checkout is at (the backend cannot set it) |

### One daemon per project

A daemon runs per project folder, with that folder as its working directory. It loads every
`.sidehub/*.json` in the folder (one agent per file, all connected in parallel) and writes its logs and
PID to `.sidehub/run/`. Started daemons are registered in `~/.sidehub/instances.json`, which is what
`--all` operates on.

When a PTY starts, the agent also writes the `sidehub` skill files (`AGENTS.md`, `GEMINI.md`, the
Claude Code skill) into the working directory, so the CLIs know the `sidehub-cli` commands (tasks,
workflow callbacks, drive…). Disable file writes (`"allowFileWrite": false`) to prevent it.

## What leaves your machine, and what doesn't

| Goes to SideHub | Stays on your machine |
|---|---|
| Connection info: agent id, version, OS shells, root folder path, installed CLI versions | Your source code and repositories¹ |
| PTY lifecycle: started, exited (exit code), CLI session id and title | Your secrets, environment variables and credentials |
| CLI state (`working` / `waiting-input` / `idle`) | Model calls: the CLIs talk to the providers directly, with your subscriptions or API keys |
| Token counts per run and per CLI session | CLI transcripts and session files (but the last message of a question run, its answer) |
| The output of the terminals SideHub opens (cockpit terminals and runs) | The agent token (sent only to SideHub, as an authentication header) |
| Output of one-shot `command.execute` commands, if enabled | |
| Whatever a CLI explicitly sends with `sidehub-cli` (task updates, step results, drive notes) | |
| Failure reports when the agent can't install, start or connect (see below) | |

¹ The agent never reads or uploads your files on its own. But a terminal's output is whatever is
printed in it: if a CLI or a command prints a file, that output reaches SideHub like any terminal
output.

All traffic goes through the agent's outbound `wss://` connection and the `sidehub-cli` HTTPS calls to
the same API. **No inbound port, no VPN, no reverse tunnel.**

### Failure reports

A failed install happens on your machine, out of SideHub's sight. So when the agent or its install
script fails, it tells SideHub why with one anonymous HTTPS call (`POST /api/agent/diagnostics`), and
SideHub shows the cause next to the account whose agent never connected.

What a report holds, and nothing else:

| Field | Example |
|---|---|
| `tokenPrefix`: the first 16 characters of the agent token (`sh_agent_` + 7), never the token: it ties the report to the agent, and SideHub drops reports matching no agent | `sh_agent_Ab3xQ9z` |
| `reason` | `handshake-rejected` |
| `detail`: one line, at most 300 characters: tokens masked, your home folder shown as `~`, your user name in paths as `<user>` | `HTTP 401: the token matches no agent…` |
| `agentVersion` | `1.0.80`, `install.sh v1.0.80` |
| `os` | `linux-x64` |

| `reason` | Sent by | When |
|---|---|---|
| `handshake-rejected` | agent | The WebSocket handshake is refused (401/403): the token matches no agent (deleted, or copied incompletely) |
| `backend-unreachable` | agent | The handshake fails otherwise before the first connection: DNS, socket, proxy, TLS, HTTP error |
| `pty-helper-failed` | agent | `pty-helper` doesn't start: Node.js missing from the agent's `PATH`, `node-pty` built for another Node version… |
| `root-refused` | agent, `install.sh` | Run as root without `--allow-root` |
| `cli-missing` | agent | None of `claude`, `codex`, `gemini`, `copilot` answers `--version` |
| `install-node-missing`, `install-download-failed`, `install-verification-failed`, `install-permission-denied`, `install-setup-failed`, `install-failed` | `install.sh`, `install.ps1` | The install stage that failed |

Each cause is sent at most once per agent start (a send that fails for lack of network is retried at
most 3 times), with a 5-second timeout: reporting never delays or blocks the agent. Connection
failures are only reported until the agent first connects: a later outage is not a broken install.
`pty-helper` and the CLIs are checked once, in the background, after each start.

The install scripts only report when they have a token, from `SIDEHUB_SETUP_TOKEN` (or `install.sh --token`):
the setup commands copied from SideHub set it.

## Installation

Requires [Node.js](https://nodejs.org) 18 or later (for the PTY helper) and the CLIs you want to use
(`claude`, `codex`, `gemini`, `copilot`) on the `PATH`.

### Linux / macOS

```bash
curl -fsSL https://api.sidehub.io/agent/install.sh | bash
```

Installs the latest release in `/usr/local/lib/sidehub-agent/` and links `sidehub-agent` / `sidehub-cli`
into `/usr/local/bin/`, through `sudo` when those folders aren't writable. Without `sudo`, or with
`--user`, it installs in `~/.local/lib/sidehub-agent/` and `~/.local/bin/` instead. To install a
specific version:

```bash
curl -fsSL https://api.sidehub.io/agent/install.sh | bash -s v1.0.75
curl -fsSL https://api.sidehub.io/agent/install.sh | bash -s -- --user   # no sudo
```

With `SIDEHUB_SETUP_TOKEN` set (the command copied from SideHub), it then configures and starts the
agent in the current folder (see [Configure](#configure)):

```bash
 curl -fsSL https://api.sidehub.io/agent/install.sh | SIDEHUB_SETUP_TOKEN=<token> bash
```

The token goes through the environment of `bash` only: `--token <token>` also works but puts it in the
command line (visible in `ps`). The leading space keeps the line out of the shell history where it is
set up to (`HISTCONTROL=ignorespace` in bash, `HIST_IGNORE_SPACE` in zsh).

### Windows (PowerShell)

```powershell
irm https://api.sidehub.io/agent/install.ps1 | iex
```

Installs in `%LOCALAPPDATA%\Programs\sidehub-agent`. With `SIDEHUB_SETUP_TOKEN` set (the command copied
from SideHub sets it), it then configures and starts the agent in the current folder, and clears the
variable from the session whether the install succeeded or not.

### What the scripts check

Set `SIDEHUB_INSTALL_DIR` to install elsewhere (an existing folder that does not hold a previous agent
install is refused), and `SIDEHUB_BIN_DIR` for the links (`install.sh`). Versions before `v1.0.59` can
no longer be installed with the scripts. Node.js older than 18 is refused.

The scripts verify the signature of the release's `checksums.sha256` (fetched from GitHub Releases)
against the key embedded in the script, then the archive against it, and abort on any mismatch. The
archive ships `pty-helper`'s Node.js dependencies prebuilt (`npm ci` from the lockfile in the release
CI): nothing is fetched from npm at install time. Each release also carries a build provenance
attestation, and the Windows executables an Authenticode signature: see
[Verifying a release](SECURITY.md#verifying-a-release).

### Configure

1. In [SideHub](https://www.sidehub.io), go to **Agents** in your workspace and create an agent. The
   token is shown once: to get it again, use **Install** on the agent, which regenerates it (the previous
   token stops working and the agent is disconnected until it is set up with the new one).
2. From your project folder, run the setup command shown by SideHub and paste the token:

   ```bash
   cd ~/my-project
   sidehub-agent setup --token-stdin
   ```

   `setup` asks SideHub which agent the token belongs to, keeps the token in your user configuration
   folder (see [Agent token](#agent-token)), writes `.sidehub/agent.json` (`0600`, kept out of git), and
   starts the agent in the background. Add `--no-start` to only write the files. Run again in a folder
   whose agent is running (another agent, a new token), it restarts that agent with the new config.
3. To start it again after a reboot: `sidehub-agent service install` (see
   [Running as a service](#running-as-a-service)).

### Root

`setup`, `start`, `restart` and `service` refuse to run as `root`. On a server where you log in as root,
create an unprivileged user that owns the project:

```bash
adduser sidehub                     # useradd -m -s /bin/bash sidehub on some distros
loginctl enable-linger sidehub      # its services start at boot, without a login
su - sidehub                        # then, as sidehub (claude / codex installed and logged in for it):
git clone <your repository> && cd <it>
sidehub-agent setup --token-stdin
sidehub-agent service install
```

If you really mean it, pass `--allow-root` (or set `SIDEHUB_ALLOW_ROOT=1`): SideHub then controls the
whole machine.

```bash
sidehub-agent start -d --allow-root
 curl -fsSL https://api.sidehub.io/agent/install.sh | SIDEHUB_SETUP_TOKEN=<token> SIDEHUB_ALLOW_ROOT=1 bash
```

`install.sh` run as root without `--allow-root` installs the binaries but does not configure the agent.

### Update

Releases are published from version tags (`v1.0.x`) on this repository; the install scripts pull the
release from GitHub. To update, re-run the install script, then restart the running agents so they pick
up the new binaries:

```bash
curl -fsSL https://api.sidehub.io/agent/install.sh | bash
sidehub-agent restart --all -d
sidehub-agent status --all
```

On Windows, `install.ps1` stops the agents running from the install folder and restarts them itself.

> Restarting stops every PTY session opened through SideHub on this machine.

## Configuration

Each `.json` file in `.sidehub/` defines one agent; all are started in parallel by the folder's daemon.

```json
{
  "name": "my-agent",
  "sidehubUrl": "wss://api.sidehub.io/ws/agent",
  "agentId": "<agent-uuid>",
  "workspaceId": "<workspace-uuid>",
  "workingDirectory": ".",
  "capabilities": ["shell", "claude-code"]
}
```

| Field | Required | Description |
|---|---|---|
| `name` | No | Display name (defaults to the file name) |
| `sidehubUrl` | Yes | `wss://api.sidehub.io/ws/agent` (`ws://` is only accepted for `localhost`) |
| `agentId` | Yes | Agent UUID (from SideHub) |
| `workspaceId` | Yes | Workspace UUID (from SideHub) |
| `workingDirectory` | Yes | Folder PTYs start in and are confined to (`.` or an absolute path) |
| `capabilities` | Yes | Labels reported to SideHub at connection (`setup` writes `["shell", "claude-code"]`) |
| `allowCommandExecute` | No | Allow one-shot `command.execute` from the backend (default `true`) |
| `allowFileWrite` | No | Allow the backend to write files under `workingDirectory`, such as terminal image uploads and skill files (default `true`) |

The agent ignores configs tracked by git, symbolic links and files owned by another user (a config
decides which backend the agent obeys), and tightens `.sidehub/` to `0700` and configs to `0600`.

### Agent token

The token is not in `.sidehub/`, which sits in the project, the working directory of every terminal. It
is kept in one file per agent, `0600` in a `0700` folder:

| OS | Location |
|---|---|
| Linux, macOS | `~/.config/sidehub/tokens/<agentId>.token` (`$XDG_CONFIG_HOME/sidehub/tokens/` when set) |
| Windows | `%LOCALAPPDATA%\SideHub\tokens\<agentId>.token` |

A config written by an older version still holds an `agentToken` field: at startup the agent moves it to
that file and removes it from the config.

## Security

**The agent runs whatever the SideHub backend asks for**: it opens terminals and types into them, runs
commands and writes files as the OS user that started it. A compromised backend means code execution on
the agent's machine: the agent is not a sandbox.

What the agent enforces:

- `wss://` only (except `localhost`), token sent in a header; outbound connection only
- `pty.start` only spawns allowlisted shells, resolved from fixed system directories; any other binary is refused
- PTYs get an allowlisted environment; the backend cannot override `PATH`, `LD_PRELOAD`, rcfiles or agent-owned variables
- PTY working directories and file writes are confined to `workingDirectory`
- `command.execute` and file writes can be disabled with `"allowCommandExecute": false` / `"allowFileWrite": false`
- refuses to run as `root` unless `--allow-root` is given

Run the agent as a dedicated unprivileged user, keep `.sidehub/*.json` out of version control, and use a
container or VM if the machine holds anything you would not hand to the backend. See
[SECURITY.md](SECURITY.md) for the full threat model and how to report a vulnerability.

## Commands

```
Usage: sidehub-agent [command] [options]

Commands:
  setup             Configure this folder for an agent, then start it
    --token-stdin   Read the agent's token (copied from SideHub) from stdin
    --no-start      Only write .sidehub/agent.json
  start             Start the agent (default)
    -d, --daemon    Run in background
    --all           Operate on all registered instances
  stop              Stop the running agent (--all: every instance)
  restart           Stop then start the agent (-d, --all)
  logs              Show agent logs (--no-follow to print and exit)
  status            Show agent status (--all: every instance)
  service install   Start this folder's agent at boot (Linux), at login (macOS) or at logon (Windows)
  service uninstall / service status
  help              Show help

Options:
  --allow-root      Allow setup/start/restart/service as root (refused by default)
```

Logs are in `.sidehub/run/sidehub-agent.log` (rotated at 10 MB, 3 archives).

### Running as a service

From the project folder, once `setup` has run:

```bash
sidehub-agent service install     # uninstall / status
```

It starts this folder's agent again after a reboot, for the current user, without root:

| OS | What it installs | Starts | Restarts after a crash |
|---|---|---|---|
| Linux | systemd user unit `~/.config/systemd/user/sidehub-agent-<folder>-<hash>.service`, and enables lingering (`loginctl enable-linger`; if your distribution refuses it without root, it prints the `sudo` command) | at boot | yes |
| macOS | LaunchAgent `~/Library/LaunchAgents/io.sidehub.agent.<folder>-<hash>.plist` | at login | yes |
| Windows | scheduled task `SideHub Agent <folder>-<hash>` running `sidehub-agent start -d` | at logon | no |

The unit and the LaunchAgent run the agent with the `PATH` of the shell that installed them (so `claude`
from nvm or `~/.local/bin` resolves): run `service install` again after moving a CLI. They write the
usual log and PID files, so `status` and `logs` work as before, and `start`, `stop` and `restart` go
through systemd / launchd (a plain kill would be undone by the restart policy).

For a hardened setup under a dedicated system user, use the templates in [`contrib/`](contrib/)
(also installed in `/usr/local/lib/sidehub-agent/contrib/`):

- **systemd**: [`contrib/systemd/sidehub-agent@.service`](contrib/systemd/sidehub-agent@.service), one
  instance per project folder, `User=sidehub`, `NoNewPrivileges`, `ProtectSystem=full`, `PrivateTmp`…

  ```bash
  sudo useradd --system --create-home --shell /bin/bash sidehub
  # as sidehub, from the project folder: sidehub-agent setup --token-stdin --no-start
  sudo cp /usr/local/lib/sidehub-agent/contrib/systemd/sidehub-agent@.service /etc/systemd/system/
  sudo systemctl daemon-reload
  sudo systemctl enable --now "sidehub-agent@$(systemd-escape --path /home/sidehub/my-project).service"
  ```

  `NoNewPrivileges` means `sudo` does not work in the agent's terminals. The CLIs (`claude`, `codex`,
  `node`) must be on the unit's `PATH`. Stop it with `systemctl stop`, not `sidehub-agent stop`.

- **launchd (macOS)**: [`contrib/launchd/io.sidehub.agent.plist`](contrib/launchd/io.sidehub.agent.plist),
  a per-user LaunchAgent (never a LaunchDaemon, which runs as root): fill in the project path, copy it to
  `~/Library/LaunchAgents/` and `launchctl bootstrap gui/$(id -u) <plist>`.

## WebSocket protocol

**Backend → agent**

| Message | Purpose |
|---|---|
| `pty.start` / `pty.stop` | Open / close a terminal (shell, size, extra environment, which of its keys are secrets, MCP servers of a run) |
| `pty.input` / `pty.resize` | Keystrokes / terminal size |
| `pty.history.request` | Replay a terminal's buffered output |
| `command.execute` | One-shot command (can be disabled) |
| `file.write.start` / `.chunk` / `.end`, `terminal.attachment.enqueue` | File upload into `workingDirectory`, e.g. an image pasted in a terminal (can be disabled) |

**Agent → backend**

| Message | Purpose |
|---|---|
| `agent.connected` / `agent.heartbeat` | Connection (version, shells, CLI versions) and keep-alive every 15 s |
| `pty.started` / `pty.exited` | Terminal spawned (with its real start time) / process ended (exit code) |
| `pty.output` / `pty.history` | Terminal output, live / replayed |
| `pty.cli-session-started` / `pty.cli-session-titled` | Id and title of the CLI session running in a terminal |
| `pty.cli-state` | `working` / `waiting-input` / `idle` |
| `run.usage` / `cli-session.usage` | Token counts per model |
| `run.answer` | Final message of a question run, with the commit it was asked against: `{"runId", "text", "commitSha", "commitDate", "error"}` (`text` null with an `error` such as `no-final-message` when none was found) |
| `command.output` / `command.completed` / `command.failed` / `command.busy` | One-shot command results |

The connection reconnects with exponential backoff (1 s → 30 s, reset after 60 s of stable connection)
and drops after 3 missed heartbeat acknowledgements.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js](https://nodejs.org/).

```bash
git clone https://github.com/toregua/side_hub_agent.git
cd side_hub_agent

dotnet build SideHub.Agent
dotnet test SideHub.Agent.Tests

# Self-contained binaries, as built by the release workflow
dotnet publish SideHub.Agent -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish SideHub.Cli   -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

Releases are built by [`.github/workflows/release.yml`](.github/workflows/release.yml) on each `v*` tag
for `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64` and `win-arm64`.

## Project structure

```
side_hub_agent/
├── SideHub.Agent/                 # sidehub-agent
│   ├── Program.cs, Commands.cs    # CLI: setup, start, stop, restart, logs, status
│   ├── AgentSetup.cs              # `setup`: fetch the agent's config from its token
│   ├── AgentConfig.cs             # Load and validate .sidehub/*.json
│   ├── AgentTokenStore.cs         # Agent tokens, out of the project (~/.config/sidehub/tokens/)
│   ├── AgentRunner.cs             # One runner per config
│   ├── DiagnosticReporter.cs      # Failure reports (install / start / connection) to SideHub
│   ├── StartupChecks.cs           # pty-helper and CLI checks, once per start
│   ├── WebSocketClient.cs         # Backend connection, message dispatch, PTY lifecycle
│   ├── NodePtyExecutor.cs         # PTYs through pty-helper/
│   ├── PtyOutputBuffer.cs         # Output history replayed after a reconnection
│   ├── NotifyFifo.cs              # Terminal → agent channel (FIFO / named pipe)
│   ├── CliStateTracker.cs         # working / waiting-input / idle per PTY
│   ├── SkillInstaller.cs          # AGENTS.md / GEMINI.md / sidehub skill
│   ├── QuestionCheckout.cs        # ~/.sidehub/qa/<agentId>/checkout for question runs
│   ├── GitRepository.cs, GitCommand.cs # git CLI with hardened config and time limits
│   ├── ShellPolicy.cs, PtyEnvironmentPolicy.cs, PathConfinement.cs, RootPolicy.cs …
│   ├── CommandExecutor.cs         # One-shot commands
│   ├── DaemonManager.cs, InstanceRegistry.cs, RotatingLogWriter.cs
│   ├── Usage/                     # Token usage from CLI transcripts, pending queue
│   ├── Models/                    # WebSocket message DTOs
│   ├── cli-wrappers/              # claude / codex / gemini / copilot wrappers (bash)
│   └── pty-helper/                # Node.js + node-pty
├── SideHub.Cli/                   # sidehub-cli
│   ├── Launch/                    # `launch` and `cli-state`
│   └── Commands/                  # tasks, workflows, schedulers, drive, tables…
├── SideHub.Agent.Tests/
├── scripts/                       # install.sh, install.ps1
├── contrib/                       # systemd and launchd templates
└── .github/workflows/release.yml
```

## Troubleshooting

**Agent offline in SideHub**: SideHub shows the last failure the agent reported on its card. On the
machine, `sidehub-agent status` in the project folder; if it isn't running, `sidehub-agent start -d`.
Offline after every reboot: `sidehub-agent service install` (see [Running as a service](#running-as-a-service)).

**Agent won't connect**: check the token (see [Agent token](#agent-token); a `401` at the handshake
means it matches no agent: copy the setup command again from SideHub), that `sidehubUrl` uses `wss://`, and that
outbound HTTPS/WebSocket traffic to `api.sidehub.io` is allowed.

**"Configuration directory not found"**: run `sidehub-agent` from the project folder that holds
`.sidehub/`.

**A CLI does not start in a terminal**: check it is installed for the agent's user (`claude --version`,
`codex --version`…) and on the `PATH` the agent was started with.

**Daemon won't start**: `sidehub-agent logs --no-follow`. As root, it exits unless `--allow-root` is
given. A stale PID file is cleaned up by `sidehub-agent status`.

## Links

- **SideHub**: [https://www.sidehub.io](https://www.sidehub.io)
- **Issues**: [GitHub Issues](https://github.com/toregua/side_hub_agent/issues)
- **Security**: [SECURITY.md](SECURITY.md)

## License

[MIT](LICENSE)
