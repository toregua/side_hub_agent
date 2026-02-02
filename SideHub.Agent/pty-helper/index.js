import * as pty from 'node-pty';
import * as readline from 'readline';

let ptyProcess = null;

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
        ...process.env,
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
