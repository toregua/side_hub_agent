using System.Runtime.InteropServices;
using Pty.Net;

namespace SideHub.Agent;

public class PtyExecutor : IAsyncDisposable
{
    private readonly string _workingDirectory;
    private IPtyConnection? _pty;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private Func<string, Task>? _onOutput;
    private Func<int, Task>? _onExit;
    private readonly object _lock = new();
    private bool _hasExited;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _pty != null && !_hasExited;
            }
        }
    }

    public PtyExecutor(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public async Task StartAsync(
        string shell,
        Func<string, Task> onOutput,
        Func<int, Task> onExit,
        int columns = 120,
        int rows = 30,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_pty != null)
                throw new InvalidOperationException("PTY session already running");
            _hasExited = false;
        }

        _onOutput = onOutput;
        _onExit = onExit;

        var (app, args, env) = GetShellConfig(shell);

        // Set terminal dimensions via environment variables
        env["COLUMNS"] = columns.ToString();
        env["LINES"] = rows.ToString();
        // Disable zsh PROMPT_SP (partial line indicator that fills width with spaces)
        env["PROMPT_EOL_MARK"] = "";

        var options = new PtyOptions
        {
            Name = "SideHub Terminal",
            App = app,
            CommandLine = args,
            Cwd = _workingDirectory,
            Cols = columns,
            Rows = rows,
            Environment = env
        };

        _pty = await PtyProvider.SpawnAsync(options, ct);

        // Force resize immediately after spawn (workaround for Pty.Net not applying initial dimensions on macOS)
        _pty.Resize(columns, rows);

        // Subscribe to exit event
        _pty.ProcessExited += (sender, args) =>
        {
            lock (_lock)
            {
                _hasExited = true;
            }
            // Fire and forget the async callback
            _ = _onExit?.Invoke(args.ExitCode);
        };

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readTask = ReadOutputAsync(_readCts.Token);
    }

    public async Task WriteAsync(string input, CancellationToken ct = default)
    {
        if (_pty == null)
            throw new InvalidOperationException("PTY session not started");

        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        await _pty.WriterStream.WriteAsync(bytes, ct);
        await _pty.WriterStream.FlushAsync(ct);
    }

    public void Resize(int columns, int rows)
    {
        _pty?.Resize(columns, rows);
    }

    private async Task ReadOutputAsync(CancellationToken ct)
    {
        if (_pty == null) return;

        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var bytesRead = await _pty.ReaderStream.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                var output = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                if (_onOutput != null)
                {
                    await _onOutput(output);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception)
        {
            // Stream closed
        }
    }

    private static (string app, string[] args, Dictionary<string, string> env) GetShellConfig(string shell)
    {
        var env = new Dictionary<string, string>
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor"
        };

        // Copy important environment variables
        foreach (var key in new[] { "PATH", "HOME", "USER", "LANG", "LC_ALL", "SHELL" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
                env[key] = value;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return shell.ToLowerInvariant() switch
            {
                "powershell" or "pwsh" => ("pwsh.exe", Array.Empty<string>(), env),
                "cmd" => ("cmd.exe", Array.Empty<string>(), env),
                _ => ("pwsh.exe", Array.Empty<string>(), env)
            };
        }
        else
        {
            // macOS / Linux
            var shellPath = shell.ToLowerInvariant() switch
            {
                "bash" => "/bin/bash",
                "sh" => "/bin/sh",
                "zsh" => "/bin/zsh",
                _ => "/bin/zsh"
            };

            // Use login shell (-l) to load profile
            // -o NO_PROMPT_SP disables the partial line indicator that fills width with spaces
            return (shellPath, new[] { "-l", "-o", "NO_PROMPT_SP" }, env);
        }
    }

    public async Task StopAsync()
    {
        _readCts?.Cancel();

        if (_readTask != null)
        {
            try
            {
                await _readTask;
            }
            catch
            {
                // Ignore
            }
        }

        lock (_lock)
        {
            if (_pty != null)
            {
                _pty.Kill();
                _pty.Dispose();
                _pty = null;
            }
        }

        _readCts?.Dispose();
        _readCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
