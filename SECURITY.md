# Security

## Trust model

The SideHub agent is a **remote execution agent**. The SideHub backend it connects to
(`sidehubUrl`) drives it: it opens terminals, types into them, runs commands and writes
files on the machine where the agent runs, as the OS user that started the agent.

> **A compromised backend, or anyone who holds a valid backend session for your workspace,
> can run arbitrary code on the agent's machine.**

This is by design, not a bug. The agent is not a sandbox, and none of the restrictions below
change that: a backend that can type into a shell (`pty.input`) can do anything that user can
do. Treat the agent like an SSH key handed to the SideHub backend.

### What the backend can do

| Message | Effect |
|---|---|
| `pty.start` / `pty.input` | Spawns an allowlisted shell and types arbitrary input into it |
| `command.execute` | Runs a one-shot command in a shell (can be disabled, see below) |
| `file.write.*`, `terminal.attachment.enqueue` | Writes files under the working directory and pastes text into a PTY (can be disabled, see below) |

### What the agent does protect

These controls reduce mistakes and limit what a *misbehaving* (not malicious) backend can
silently do. They are not a security boundary against a compromised backend.

- **Transport** — `sidehubUrl` must be `wss://` (TLS) except for `localhost`; the agent token
  is sent in a header, never in the URL.
- **Shell allowlist** — `pty.start` only spawns `bash`, `zsh`, `sh`, `dash`, `fish`, `pwsh`
  (Unix) or `cmd`, `powershell`, `pwsh` (Windows). Names are resolved by the agent from fixed
  system directories (`/bin`, `/usr/bin`, `/usr/local/bin`, `/opt/homebrew/bin`), never from
  `PATH`; any other binary or path is refused and no PTY is started. `command.execute` uses
  the same kind of fixed shell list.
- **PTY environment** — the PTY does not inherit the daemon's environment (API keys, cloud
  credentials…), only an allowlist. The backend may only add `SIDEHUB_*` / telemetry
  variables and cannot override `PATH`, `LD_PRELOAD`, rcfiles or the agent-owned `SIDEHUB_*`
  variables (`PtyEnvironmentPolicy`).
- **Working directory** — PTY working directories and `file.write` paths are confined to the
  agent's `workingDirectory`. This only limits where a terminal *starts*; the shell itself
  can still `cd` anywhere.
- **Secrets in logs** — commands, terminal output and tokens are not written to the agent
  logs; setup tokens are kept out of the process arguments.
- **Opt-out switches** — `command.execute` and file writes can be turned off per agent.

### What the agent does not protect

- Code execution by the backend through a PTY (`pty.input`) — always possible.
- Access to anything the agent's OS user can read or write, including outside
  `workingDirectory`.
- The agent token stored in `.sidehub/*.json`: anyone who can read it can impersonate the
  agent. Keep it out of version control (`.sidehub/` should be git-ignored) and readable by
  the agent user only.
- Network egress from the shells and CLIs the agent runs.

## Hardening options (`agent.json`)

| Field | Default | Effect when `false` |
|---|---|---|
| `allowCommandExecute` | `true` | `command.execute` is refused with `command.failed` |
| `allowFileWrite` | `true` | `file.write.*` is refused with `command.failed`; terminal image attachments are dropped |

Both default to `true` so existing setups keep working (terminal image upload relies on file
writes). Turning them off narrows the surface but, as explained above, does not stop a
backend that can open a terminal.

Recommended practice:

- Run the agent as a **dedicated, unprivileged user**, never as `root`.
- Point `workingDirectory` at the project only, and run the agent in a container or VM when
  the machine holds anything you would not hand to the backend.
- Rotate the agent token (delete and recreate the agent in SideHub) if it may have leaked.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report it privately through GitHub's
[private vulnerability reporting](https://github.com/toregua/side_hub_agent/security/advisories/new)
(*Security → Report a vulnerability* on the repository). Include:

- the agent version (the `Agent version:` line in the agent log) and platform,
- the steps to reproduce and the impact,
- whether the issue requires a compromised backend (see the trust model above — issues that
  only restate "the backend can run code on the agent" are expected behavior).

We aim to acknowledge reports within 5 business days and will coordinate a fix and a release
before any public disclosure.
