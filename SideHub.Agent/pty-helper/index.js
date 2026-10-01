import * as pty from 'node-pty';
import * as readline from 'readline';

let ptyProcess = null;

// Daemon environment variables passed through to PTY sessions. Everything else is dropped:
// the daemon may have been started from a shell holding secrets (API keys, cloud credentials,
// tokens) that the terminal must not inherit. What the shell needs beyond this list comes from
// the user's own profile (login shell / rcfile) or from the agent (config.env).
// Names are compared in upper case (Windows environment names are case-insensitive).
const ALLOWED_ENV_NAMES = new Set([
  // Identity, paths, locale
  'HOME', 'USER', 'LOGNAME', 'SHELL', 'PATH', 'LANG', 'LANGUAGE', 'TZ', 'TMPDIR', 'EDITOR', 'VISUAL', 'PAGER',
  // Session plumbing (sockets, not secrets)
  'SSH_AUTH_SOCK', 'DBUS_SESSION_BUS_ADDRESS', 'WSL_DISTRO_NAME', 'WSL_INTEROP',
  // Network proxies, needed by the CLIs behind a corporate proxy
  'HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY', 'ALL_PROXY',
  // Toolchain locations
  'NVM_DIR', 'NVM_BIN', 'NVM_INC', 'VOLTA_HOME', 'PNPM_HOME', 'BUN_INSTALL',
  'DOTNET_ROOT', 'JAVA_HOME', 'GOPATH', 'GOROOT', 'CARGO_HOME', 'RUSTUP_HOME',
  'HOMEBREW_PREFIX', 'HOMEBREW_CELLAR', 'HOMEBREW_REPOSITORY',
  // CLI configuration directories
  'CLAUDE_CONFIG_DIR', 'CODEX_HOME',
  // Windows system variables
  'SYSTEMROOT', 'SYSTEMDRIVE', 'WINDIR', 'COMSPEC', 'PATHEXT', 'OS', 'TEMP', 'TMP',
  'USERNAME', 'USERDOMAIN', 'USERPROFILE', 'HOMEDRIVE', 'HOMEPATH', 'COMPUTERNAME',
  'APPDATA', 'LOCALAPPDATA', 'PROGRAMDATA', 'PROGRAMFILES', 'PROGRAMFILES(X86)', 'PROGRAMW6432',
  'COMMONPROGRAMFILES', 'COMMONPROGRAMFILES(X86)', 'PUBLIC',
  'PROCESSOR_ARCHITECTURE', 'NUMBER_OF_PROCESSORS',
]);
const ALLOWED_ENV_PREFIXES = ['LC_', 'XDG_'];

function filterDaemonEnv(env) {
  const filtered = {};
  for (const [key, value] of Object.entries(env)) {
    const name = key.toUpperCase();
    if (ALLOWED_ENV_NAMES.has(name) || ALLOWED_ENV_PREFIXES.some(prefix => name.startsWith(prefix))) {
      filtered[key] = value;
    }
  }
  return filtered;
}

const rl = readline.createInterface({
  input: process.stdin,
  output: process.stdout,
  terminal: false
});

function send(msg) {
  console.log(JSON.stringify(msg));
}

function handleMessage(line) {
  try {
    const msg = JSON.parse(line);

    switch (msg.type) {
      case 'start':
        startPty(msg);
        break;
      case 'input':
        if (ptyProcess) {
          ptyProcess.write(msg.data);
        }
        break;
      case 'resize':
        if (ptyProcess) {
          ptyProcess.resize(msg.cols, msg.rows);
        }
        break;
      case 'stop':
        stopPty();
        break;
      case 'ping':
        send({ type: 'pong', id: msg.id, ptyRunning: ptyProcess !== null });
        break;
    }
  } catch (e) {
    send({ type: 'error', message: e.message });
  }
}

function startPty(config) {
  if (ptyProcess) {
    send({ type: 'error', message: 'PTY already running' });
    return;
  }

  const shell = config.shell || process.env.SHELL || '/bin/bash';
  const cwd = config.cwd || process.cwd();
  const cols = config.cols || 80;
  const rows = config.rows || 24;
  const extraEnv = config.env || {};

  const env = {
    ...filterDaemonEnv(process.env),
    TERM: 'xterm-256color',
    COLORTERM: 'truecolor',
    COLUMNS: String(cols),
    LINES: String(rows),
    ...extraEnv
  };

  // Default to a login shell so .profile / .bashrc run. When the agent
  // provided a SideHub rcfile (only meaningful for bash), invoke bash as an
  // interactive non-login shell with --rcfile pointing at that file — our
  // rcfile manually re-sources the standard init files and then re-prepends
  // the cli-wrappers dir to PATH so user PATH overrides can't shadow it.
  let shellArgs = ['-l'];
  const sidehubBashrc = env.SIDEHUB_BASHRC;
  const isBash = /(^|\/)bash$/.test(shell);
  if (isBash && sidehubBashrc) {
    shellArgs = ['--rcfile', sidehubBashrc, '-i'];
  }

  try {
    ptyProcess = pty.spawn(shell, shellArgs, {
      name: 'xterm-256color',
      cols: cols,
      rows: rows,
      cwd: cwd,
      env: env
    });

    ptyProcess.onData((data) => {
      send({ type: 'output', data: data });
    });

    ptyProcess.onExit(({ exitCode, signal }) => {
      send({ type: 'exit', exitCode: exitCode, signal: signal });
      ptyProcess = null;
    });

    send({ type: 'started', shell: shell, pid: ptyProcess.pid });
  } catch (e) {
    send({ type: 'error', message: e.message });
  }
}

function stopPty() {
  if (ptyProcess) {
    ptyProcess.kill();
    ptyProcess = null;
  }
}

rl.on('line', handleMessage);

rl.on('close', () => {
  stopPty();
  process.exit(0);
});

// Signal ready
send({ type: 'ready' });
