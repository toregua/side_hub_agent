# SideHub Agent

Remote command execution agent for the [SideHub](https://www.sidehub.io) platform. Connects via WebSocket to receive and execute shell commands with real-time output streaming.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## Quick Start (< 5 minutes)

### 1. Install the agent

Requires [Node.js](https://nodejs.org) (used for PTY terminal support).

```bash
# macOS / Linux
curl -fsSL https://api.sidehub.io/agent/install.sh | bash

# Windows (PowerShell)
irm https://api.sidehub.io/agent/install.ps1 | iex
```

The script downloads the latest release for your platform, installs it in
`/usr/local/lib/sidehub-agent/` and links `sidehub-agent` / `sidehub-cli` into
`/usr/local/bin/`. To install a specific version:

```bash
curl -fsSL https://api.sidehub.io/agent/install.sh | bash -s v1.0.34
```

The script checks the archive against the release's `checksums.sha256` (fetched from GitHub
Releases) and aborts on a mismatch. Each release also carries a build provenance attestation —
see [Verifying a release](SECURITY.md#verifying-a-release).

### 2. Configure

1. Log in to [SideHub](https://www.sidehub.io) and go to **Agents** in your workspace
2. Create a new agent — this generates an `agentId`, `workspaceId`, and `agentToken`
3. In your project directory, create a `.sidehub/` folder with a JSON config file:

```bash
mkdir -p .sidehub
```

```bash
cat > .sidehub/agent.json << 'EOF'
{
  "name": "my-agent",
  "sidehubUrl": "wss://api.sidehub.io/ws/agent",
  "agentId": "<your-agent-uuid>",
  "workspaceId": "<your-workspace-uuid>",
  "agentToken": "sh_agent_<your-token>",
  "workingDirectory": ".",
  "capabilities": ["shell"]
}
EOF
```

Replace the placeholder values with the credentials from your SideHub dashboard.

### 3. Start

```bash
sidehub-agent
```

That's it — the agent connects to SideHub and is ready to receive commands.

### Update

Re-run the install script, then restart the running agents so they pick up the new binaries:

```bash
curl -fsSL https://api.sidehub.io/agent/install.sh | bash
sidehub-agent restart --all -d
sidehub-agent status
```

> Restarting stops every PTY session opened through SideHub on this machine.

## Configuration

Agent configuration files live in `.sidehub/` at the root of your project. Each `.json` file defines one agent instance — all are launched in parallel.

```
my-project/
└── .sidehub/
    ├── agent-dev.json
    ├── agent-staging.json
    └── agent-prod.json
```

### Configuration fields

| Field | Required | Description |
|---|---|---|
| `name` | No | Display name (defaults to filename) |
| `sidehubUrl` | Yes | WebSocket endpoint — `wss://api.sidehub.io/ws/agent` |
| `agentId` | Yes | Agent UUID (from SideHub dashboard) |
| `workspaceId` | Yes | Workspace UUID (from SideHub dashboard) |
| `agentToken` | Yes | Authentication token (prefix `sh_agent_`) |
| `workingDirectory` | Yes | Working directory for command execution (`.` for current, or absolute path) |
| `capabilities` | Yes | Agent capabilities: `"shell"`, `"claude-code"` |
| `allowCommandExecute` | No | Allow one-shot `command.execute` from the backend (default `true`) |
| `allowFileWrite` | No | Allow the backend to write files under `workingDirectory` — terminal image uploads (default `true`) |

### Capabilities

- **`shell`** — Execute shell commands remotely with real-time output streaming
- **`claude-code`** — Proxy Claude Code SDK sessions through the agent

### Example: multi-agent setup

```json
// .sidehub/backend.json
{
  "name": "backend-server",
  "sidehubUrl": "wss://api.sidehub.io/ws/agent",
  "agentId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "workspaceId": "11111111-2222-3333-4444-555555555555",
  "agentToken": "sh_agent_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "workingDirectory": "/var/www/backend",
  "capabilities": ["shell", "claude-code"]
}
```

## Security

**The agent runs whatever the SideHub backend asks for**: it opens terminals and types into
them, runs commands and writes files as the OS user that started it. A compromised backend
means code execution on the agent's machine — the agent is not a sandbox.

What the agent enforces:

- `wss://` only (except `localhost`), token sent in a header
- `pty.start` only spawns allowlisted shells (`bash`, `zsh`, `sh`, `dash`, `fish`, `pwsh`; `cmd`, `powershell`, `pwsh` on Windows), resolved from fixed system directories — any other binary is refused
- PTYs get an allowlisted environment; the backend cannot override `PATH`, `LD_PRELOAD`, rcfiles or agent-owned variables
- PTY working directories and file writes are confined to `workingDirectory`
- `command.execute` and file writes can be disabled with `"allowCommandExecute": false` / `"allowFileWrite": false`
- `setup`, `start` and `restart` refuse to run as `root` unless `--allow-root` (or `SIDEHUB_ALLOW_ROOT=1`) is given; `install.sh` run with `sudo` installs the binaries but does not configure the agent

Run the agent as a dedicated unprivileged user, keep `.sidehub/*.json` out of
version control, and use a container or VM if the machine holds anything you would not hand
to the backend. See [SECURITY.md](SECURITY.md) for the full threat model and how to report a
vulnerability.

## Commands

```
Usage: sidehub-agent [command] [options]

Commands:
  start             Start the agent (default)
    -d, --daemon    Run in background
  stop              Stop the running agent
  logs              Show agent logs
    --no-follow     Print current logs without following
  status            Show agent status
  help              Show help

Options:
  --allow-root      Allow setup/start/restart as root (refused by default)
```

### Examples

```bash
# Start in foreground (default)
sidehub-agent

# Start as background daemon
sidehub-agent start -d

# View logs (follows by default)
sidehub-agent logs

# View logs without following
sidehub-agent logs --no-follow

# Check if the agent is running
sidehub-agent status

# Stop the daemon
sidehub-agent stop
```

### Running as a service

The agent refuses to run as `root` (`--allow-root` overrides it, at your own risk). To start it at boot,
run it under a dedicated user with the templates in [`contrib/`](contrib/) (also installed in
`/usr/local/lib/sidehub-agent/contrib/`):

- **systemd** — [`contrib/systemd/sidehub-agent@.service`](contrib/systemd/sidehub-agent@.service), one
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

- **launchd (macOS)** — [`contrib/launchd/io.sidehub.agent.plist`](contrib/launchd/io.sidehub.agent.plist),
  a per-user LaunchAgent (never a LaunchDaemon, which runs as root): fill in the project path, copy it to
  `~/Library/LaunchAgents/` and `launchctl bootstrap gui/$(id -u) <plist>`.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                     SideHub SaaS                        │
│            https://www.sidehub.io                       │
│                                                         │
│  ┌─────────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │  Angular 18  │  │ .NET 10 API  │  │  PostgreSQL   │  │
│  │  Frontend    │──│  (WebSocket  │──│  + pgvector   │  │
│  │             │  │   handlers)  │  │               │  │
│  └─────────────┘  └──────┬───────┘  └───────────────┘  │
│                          │                              │
└──────────────────────────┼──────────────────────────────┘
                           │ wss://
                           │
          ┌────────────────┼────────────────┐
          │                │                │
    ┌─────┴──────┐  ┌─────┴──────┐  ┌─────┴──────┐
    │   Agent 1  │  │   Agent 2  │  │   Agent N  │
    │ (dev VPS)  │  │ (staging)  │  │ (prod)     │
    └─────┬──────┘  └────────────┘  └────────────┘
          │
          ├── CommandExecutor     Shell command execution
          ├── NodePtyExecutor     PTY terminal sessions
          ├── AgentSdkProxy      Claude Code proxy
          ├── DaemonManager       Background process management
          └── RotatingLogWriter   Log rotation (10 MB)
```

### Core components

| Component | File | Description |
|---|---|---|
| **Entry point** | `Program.cs` | CLI argument parsing, command routing |
| **Config loader** | `AgentConfig.cs` | Loads and validates `.sidehub/*.json` files |
| **Agent runner** | `AgentRunner.cs` | Orchestrates agent lifecycle |
| **WebSocket client** | `WebSocketClient.cs` | Maintains persistent connection to SideHub backend with auto-reconnection |
| **Command executor** | `CommandExecutor.cs` | Executes shell commands with real-time stdout/stderr streaming |
| **PTY executor** | `NodePtyExecutor.cs` | Full terminal emulation via Node.js PTY helper |
| **Agent SDK proxy** | `AgentSdkProxy.cs` | Local WebSocket proxy for Claude Code sessions |
| **Daemon manager** | `DaemonManager.cs` | PID file management, process lifecycle |
| **Log writer** | `RotatingLogWriter.cs` | Automatic log rotation with configurable size |

### WebSocket protocol

**Agent → Backend:**
- `agent.connected` — Sent on connection with capabilities and shell info
- `agent.heartbeat` — Keep-alive every 15 seconds
- `command.output` — Real-time stdout/stderr streaming
- `command.completed` — Command finished (with exit code)
- `command.failed` — Command execution error
- `command.busy` — Agent is busy with another command

**Backend → Agent:**
- `command.execute` — Execute a shell command
- `pty.start` — Start a PTY session
- `pty.input` — Send input to PTY
- `pty.resize` — Resize PTY terminal

### Connection resilience

- **Automatic reconnection** with exponential backoff (1s → 30s max)
- **Heartbeat monitoring** — disconnects after 3 missed ACKs
- **Stability detection** — backoff resets after 60s of stable connection
- **Claude SDK buffering** — buffers up to 1000 messages during backend reconnections

## Building from source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (for PTY helper, optional)

### Build

```bash
git clone https://github.com/sidehub-io/side_hub_agent.git
cd side_hub_agent

# Debug build
dotnet build SideHub.Agent

# Release build
dotnet build SideHub.Agent -c Release

# Run directly
dotnet run --project SideHub.Agent
```

### Publish self-contained binary

```bash
# macOS (Apple Silicon)
dotnet publish SideHub.Agent -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true

# macOS (Intel)
dotnet publish SideHub.Agent -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true

# Linux x64
dotnet publish SideHub.Agent -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true

# Linux ARM64
dotnet publish SideHub.Agent -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true

# Windows x64
dotnet publish SideHub.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

### Available builds

| Platform | Architecture | Artifact |
|---|---|---|
| macOS | Apple Silicon (M1/M2/M3/M4) | `sidehub-agent-osx-arm64` |
| macOS | Intel | `sidehub-agent-osx-x64` |
| Linux | x64 | `sidehub-agent-linux-x64` |
| Linux | ARM64 | `sidehub-agent-linux-arm64` |
| Windows | x64 | `sidehub-agent-win-x64.exe` |
| Windows | ARM64 | `sidehub-agent-win-arm64.exe` |

## Project structure

```
side_hub_agent/
├── SideHub.Agent/
│   ├── Program.cs                 # Entry point & CLI
│   ├── AgentConfig.cs             # Configuration loading
│   ├── AgentRunner.cs             # Agent lifecycle
│   ├── WebSocketClient.cs         # WebSocket connection
│   ├── CommandExecutor.cs         # Shell command execution
│   ├── NodePtyExecutor.cs         # PTY terminal emulation
│   ├── AgentSdkProxy.cs          # Claude Code proxy
│   ├── DaemonManager.cs           # Daemon process management
│   ├── RotatingLogWriter.cs       # Log rotation
│   ├── SystemInfoProvider.cs      # Platform detection
│   ├── Commands.cs                # CLI command handlers
│   ├── Models/
│   │   ├── AgentMessages.cs       # Agent protocol messages
│   │   └── CommandMessages.cs     # Command protocol messages
│   └── pty-helper/                # Node.js PTY helper
│       ├── index.js
│       └── package.json
├── .github/workflows/
│   └── release.yml                # Release builds on tags
├── CONTRIBUTING.md
├── SECURITY.md
├── LICENSE
└── README.md
```

## Troubleshooting

### Agent won't connect

1. Verify your `agentToken` is correct in the config file
2. Check that `sidehubUrl` uses `wss://` (not `ws://`)
3. Ensure your firewall allows outbound WebSocket connections
4. Run `sidehub-agent status` to check if another instance is already running

### "Configuration directory not found"

The agent expects a `.sidehub/` folder in the current working directory. Make sure you run `sidehub-agent` from your project root.

### Daemon won't start

Check logs for details:
```bash
sidehub-agent logs --no-follow
```

If a stale PID file exists, `sidehub-agent status` will clean it up automatically.

## Links

- **SideHub Platform**: [https://www.sidehub.io](https://www.sidehub.io)
- **Issues**: [GitHub Issues](https://github.com/sidehub-io/side_hub_agent/issues)

## License

[MIT](LICENSE)
