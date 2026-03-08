# SideHub Agent

Remote command execution agent for the [SideHub](https://www.sidehub.io) platform. Connects via WebSocket to receive and execute shell commands with real-time output streaming.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## Quick Start (< 5 minutes)

### 1. Install the agent

**macOS**
```bash
curl -fsSL https://www.sidehub.io/api/agent/download/macos -o sidehub-agent
chmod +x sidehub-agent
sudo mv sidehub-agent /usr/local/bin/
```

**Linux**
```bash
curl -fsSL https://www.sidehub.io/api/agent/download/linux -o sidehub-agent
chmod +x sidehub-agent
sudo mv sidehub-agent /usr/local/bin/
```

**Windows (PowerShell)**
```powershell
Invoke-WebRequest -Uri "https://www.sidehub.io/api/agent/download/windows" -OutFile "sidehub-agent.exe"
```

Or use the install scripts:
```bash
# macOS / Linux
curl -fsSL https://www.sidehub.io/api/agent/install.sh | bash

# Windows (PowerShell)
irm https://www.sidehub.io/api/agent/install.ps1 | iex
```

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
  "sidehubUrl": "wss://www.sidehub.io/ws/agent",
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
| `sidehubUrl` | Yes | WebSocket endpoint — `wss://www.sidehub.io/ws/agent` |
| `agentId` | Yes | Agent UUID (from SideHub dashboard) |
| `workspaceId` | Yes | Workspace UUID (from SideHub dashboard) |
| `agentToken` | Yes | Authentication token (prefix `sh_agent_`) |
| `workingDirectory` | Yes | Working directory for command execution (`.` for current, or absolute path) |
| `capabilities` | Yes | Agent capabilities: `"shell"`, `"claude-code"` |

### Capabilities

- **`shell`** — Execute shell commands remotely with real-time output streaming
- **`claude-code`** — Proxy Claude Code SDK sessions through the agent

### Example: multi-agent setup

```json
// .sidehub/backend.json
{
  "name": "backend-server",
  "sidehubUrl": "wss://www.sidehub.io/ws/agent",
  "agentId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "workspaceId": "11111111-2222-3333-4444-555555555555",
  "agentToken": "sh_agent_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "workingDirectory": "/var/www/backend",
  "capabilities": ["shell", "claude-code"]
}
```

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
