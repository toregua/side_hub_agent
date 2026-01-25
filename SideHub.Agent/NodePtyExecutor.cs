using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SideHub.Agent;

public class NodePtyExecutor : IAsyncDisposable
{
    private readonly string _workingDirectory;
    private readonly string _helperPath;
    private Process? _nodeProcess;
    private Func<string, Task>? _onOutput;
    private Func<int, Task>? _onExit;
    private readonly object _lock = new();
    private bool _hasExited;
    private bool _isStopping;
    private Task? _readTask;
    private int _columns;
    private int _rows;
    private readonly PtyOutputBuffer _outputBuffer = new();

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _nodeProcess != null && !_hasExited && !_nodeProcess.HasExited;
            }
        }
    }

    /// <summary>
    /// Gets all buffered PTY output history.
    /// </summary>
    public string GetBufferedOutput() => _outputBuffer.GetAll();

    /// <summary>
    /// Gets the current buffer size in bytes.
    /// </summary>
    public int BufferSize => _outputBuffer.Size;

    public NodePtyExecutor(string workingDirectory)
    {
        _workingDirectory = workingDirectory;

        // Find the pty-helper relative to the executable
        var exeDir = AppContext.BaseDirectory;
        _helperPath = Path.Combine(exeDir, "pty-helper", "index.js");

        // Fallback to development path
        if (!File.Exists(_helperPath))
        {
            _helperPath = Path.Combine(
                Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar)) ?? "",
                "pty-helper", "index.js"
            );
        }
    }

    public async Task StartAsync(
        string shell,
        Func<string, Task> onOutput,
        Func<int, Task> onExit,
        int columns = 80,
        int rows = 24,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_nodeProcess != null)
                throw new InvalidOperationException("PTY session already running");
            _hasExited = false;
            _isStopping = false;
        }

        _onOutput = onOutput;
        _onExit = onExit;
        _columns = columns;
        _rows = rows;

        if (!File.Exists(_helperPath))
        {
            throw new FileNotFoundException($"PTY helper not found at: {_helperPath}");
        }

        Console.WriteLine($"[NodePty] Starting helper: {_helperPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = _helperPath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _nodeProcess = new Process { StartInfo = startInfo };
        _nodeProcess.Start();

        // Start reading output
        _readTask = ReadOutputAsync(ct);

        // Wait for ready signal
        await Task.Delay(100, ct);

        // Send start command
        var startCmd = new
        {
            type = "start",
            shell = shell,
            cwd = _workingDirectory,
            cols = columns,
            rows = rows
        };

        await SendCommandAsync(startCmd, ct);
    }

    public async Task WriteAsync(string input, CancellationToken ct = default)
    {
        if (_nodeProcess == null || _hasExited)
            return;

        var cmd = new { type = "input", data = input };
        await SendCommandAsync(cmd, ct);
    }

    public async Task ResizeAsync(int columns, int rows)
    {
        if (_nodeProcess == null || _hasExited)
            return;

        _columns = columns;
        _rows = rows;

        var cmd = new { type = "resize", cols = columns, rows = rows };
        await SendCommandAsync(cmd, CancellationToken.None);
    }

    public void Resize(int columns, int rows)
    {
        _ = ResizeAsync(columns, rows);
    }

    private async Task SendCommandAsync(object cmd, CancellationToken ct)
    {
        if (_nodeProcess?.StandardInput == null) return;

        var json = JsonSerializer.Serialize(cmd);
        await _nodeProcess.StandardInput.WriteLineAsync(json.AsMemory(), ct);
        await _nodeProcess.StandardInput.FlushAsync();
    }

    private async Task ReadOutputAsync(CancellationToken ct)
    {
        if (_nodeProcess?.StandardOutput == null) return;

        try
        {
            while (!ct.IsCancellationRequested && !_hasExited)
            {
                var line = await _nodeProcess.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.GetProperty("type").GetString();

                    switch (type)
                    {
                        case "ready":
                            Console.WriteLine("[NodePty] Helper ready");
                            break;

                        case "started":
                            var shell = root.GetProperty("shell").GetString();
                            Console.WriteLine($"[NodePty] PTY started with {shell}");
                            break;

                        case "output":
                            var data = root.GetProperty("data").GetString();
                            if (data != null)
                            {
                                _outputBuffer.Write(data);
                                if (_onOutput != null)
                                {
                                    await _onOutput(data);
                                }
                            }
                            break;

                        case "exit":
                            var exitCode = root.GetProperty("exitCode").GetInt32();
                            Console.WriteLine($"[NodePty] PTY exited with code {exitCode}");
                            lock (_lock)
                            {
                                _hasExited = true;
                                if (_isStopping) return;
                            }
                            if (_onExit != null)
                            {
                                await _onExit(exitCode);
                            }
                            break;

                        case "error":
                            var message = root.GetProperty("message").GetString();
                            Console.WriteLine($"[NodePty] Error: {message}");
                            break;
                    }
                }
                catch (JsonException)
                {
                    // Not JSON, might be stderr leak
                    Console.WriteLine($"[NodePty] Non-JSON: {line}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NodePty] Read error: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        lock (_lock)
        {
            _isStopping = true;
        }

        if (_nodeProcess != null)
        {
            try
            {
                // Send stop command
                var cmd = new { type = "stop" };
                await SendCommandAsync(cmd, CancellationToken.None);

                // Give it time to clean up
                await Task.Delay(200);

                if (!_nodeProcess.HasExited)
                {
                    _nodeProcess.Kill();
                }
            }
            catch
            {
                // Ignore errors during stop
            }

            _nodeProcess.Dispose();
            _nodeProcess = null;
        }

        lock (_lock)
        {
            _hasExited = true;
        }

        if (_readTask != null)
        {
            try
            {
                await _readTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore
            }
        }

        _outputBuffer.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
