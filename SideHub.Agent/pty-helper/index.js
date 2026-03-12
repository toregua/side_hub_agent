import * as pty from 'node-pty';
import * as readline from 'readline';

let ptyProcess = null;

// Patterns for environment variable names that should NOT be leaked to PTY sessions
const SENSITIVE_ENV_PATTERNS = [
  /^AGENT_TOKEN$/i,
  /^SIDEHUB.*TOKEN$/i,
  /^SIDEHUB.*SECRET$/i,
  /^API_KEY$/i,
  /^SECRET/i,
  /^TOKEN$/i,
  /^AWS_SECRET/i,
  /^AWS_SESSION_TOKEN$/i,
  /^ANTHROPIC_API_KEY$/i,
  /^OPENAI_API_KEY$/i,
  /^DATABASE_URL$/i,
  /^DB_PASSWORD$/i,
  /^REDIS_PASSWORD$/i,
  /^PASSWORD/i,
  /^PRIVATE_KEY$/i,
  /KEY$/i,
  /SECRET$/i,
  /CREDENTIAL/i,
  /^GH_TOKEN$/i,
  /^GITHUB_TOKEN$/i,
  /^NPM_TOKEN$/i,
  /^NUGET_API_KEY$/i,
];

function filterSensitiveEnv(env) {
  const filtered = {};
  for (const [key, value] of Object.entries(env)) {
    if (!SENSITIVE_ENV_PATTERNS.some(pattern => pattern.test(key))) {
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

  try {
    ptyProcess = pty.spawn(shell, ['-l'], {
      name: 'xterm-256color',
      cols: cols,
      rows: rows,
      cwd: cwd,
      env: {
        ...filterSensitiveEnv(process.env),
        TERM: 'xterm-256color',
        COLORTERM: 'truecolor',
        COLUMNS: String(cols),
        LINES: String(rows)
      }
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
