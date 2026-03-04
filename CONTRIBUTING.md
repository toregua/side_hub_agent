# Contributing to SideHub Agent

Thanks for your interest in contributing to the SideHub Agent! This guide will help you get set up.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/) (for PTY helper)
- Git

## Getting started

```bash
# Clone the repository
git clone https://github.com/sidehub-io/side_hub_agent.git
cd side_hub_agent

# Build
dotnet build SideHub.Agent

# Run
dotnet run --project SideHub.Agent
```

## Development workflow

1. **Fork** the repository
2. **Create a branch** from `main`:
   ```bash
   git checkout -b feature/my-feature
   ```
3. **Make your changes** and test locally
4. **Commit** with descriptive messages:
   ```bash
   git commit -m "Add support for custom heartbeat interval"
   ```
5. **Push** and open a Pull Request against `main`

## Branch naming

| Type | Format | Example |
|---|---|---|
| Feature | `feature/<description>` | `feature/custom-heartbeat` |
| Bug fix | `fix/<description>` | `fix/reconnection-loop` |
| Docs | `docs/<description>` | `docs/update-config-guide` |
| Refactor | `refactor/<description>` | `refactor/websocket-client` |

## Project structure

```
SideHub.Agent/
├── Program.cs              # Entry point, CLI routing
├── AgentConfig.cs          # Config loading & validation
├── AgentRunner.cs          # Agent lifecycle orchestration
├── WebSocketClient.cs      # WebSocket connection & protocol
├── CommandExecutor.cs      # Shell command execution
├── NodePtyExecutor.cs      # PTY terminal emulation
├── ClaudeSdkProxy.cs       # Claude Code local proxy
├── DaemonManager.cs        # Background process management
├── RotatingLogWriter.cs    # Log file rotation
├── SystemInfoProvider.cs   # OS/shell detection
├── Commands.cs             # CLI command handlers (start, stop, logs, status)
└── Models/
    ├── AgentMessages.cs    # Agent ↔ Backend protocol messages
    └── CommandMessages.cs  # Command execution messages
```

## Architecture overview

The agent is a .NET 10 console application that:

1. Loads all `.sidehub/*.json` config files
2. Launches one `AgentRunner` per config (in parallel)
3. Each runner creates a `WebSocketClient` that connects to the SideHub backend
4. The WebSocket client handles command execution, PTY sessions, and Claude Code proxying
5. Auto-reconnection with exponential backoff ensures resilience

Key design decisions:
- **Single command at a time** — `CommandExecutor` uses a mutex to prevent concurrent execution
- **PTY via Node.js** — Terminal emulation delegates to a Node.js helper using `node-pty`
- **Claude SDK proxy** — A local WebSocket server decouples the CLI from backend reconnections
- **Log rotation** — Daemon mode uses rotating logs (10 MB default, 3 archives)

## Building & testing

```bash
# Debug build
dotnet build SideHub.Agent

# Release build
dotnet build SideHub.Agent -c Release

# Run directly
dotnet run --project SideHub.Agent

# Publish for your platform
dotnet publish SideHub.Agent -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

## Code style

- Follow existing patterns in the codebase
- Use C# 13 / .NET 10 features where appropriate
- Keep classes focused — one responsibility per file
- Use `Console.WriteLine` with the `[AgentName]` prefix pattern for logging
- Handle cancellation tokens properly for clean shutdown

## Commit messages

Write clear, concise commit messages:

```
Add PTY resize support for terminal sessions
Fix reconnection loop when token is expired
Update README with troubleshooting section
```

- Use imperative mood ("Add feature" not "Added feature")
- Keep the first line under 72 characters
- Add a body for complex changes

## Reporting issues

- Use [GitHub Issues](https://github.com/sidehub-io/side_hub_agent/issues)
- Include: OS, .NET version, agent version, config (redact tokens), and logs
- For bugs, include steps to reproduce

## Pull Request guidelines

- Keep PRs focused — one feature or fix per PR
- Update the README if your change affects usage or configuration
- Ensure the project builds without warnings
- Test on your target platform before submitting

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
