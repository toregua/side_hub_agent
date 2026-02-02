using System.Diagnostics;

namespace SideHub.Agent;

public static class Commands
{
    public static async Task<int> Start(string baseDirectory, bool daemon, CancellationToken ct)
    {
        var manager = new DaemonManager(baseDirectory);

        if (manager.IsRunning())
        {
            var pid = manager.ReadPid();
            Console.WriteLine($"[SideHub] Agent is already running (PID: {pid})");
            Console.WriteLine($"[SideHub] Use 'sidehub-agent stop' to stop it first");
            return 1;
        }

        if (daemon)
        {
            return StartDaemon(baseDirectory, manager);
        }

        return await RunForeground(baseDirectory, ct);
    }

    private static int StartDaemon(string baseDirectory, DaemonManager manager)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            Console.WriteLine("[SideHub] Error: Could not determine executable path");
            return 1;
        }

        manager.EnsureRunDirectory();

        // Pass the log file path to the daemon process
        var logFile = manager.LogFile;
        var pidFile = manager.PidFile;

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = $"--foreground-daemon \"{logFile}\" \"{pidFile}\"",
            WorkingDirectory = baseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };

        // Set environment to prevent terminal attachment
        startInfo.Environment["DOTNET_RUNNING_IN_CONTAINER"] = "true";

        try
        {
            var process = Process.Start(startInfo);
            if (process == null)
            {
                Console.WriteLine("[SideHub] Error: Failed to start daemon process");
                return 1;
            }

            // Give it a moment to start and write its PID
            Thread.Sleep(1000);

            // Check if process started successfully by reading PID file
            var pid = manager.ReadPid();
            if (pid == null)
            {
                Console.WriteLine("[SideHub] Error: Daemon process failed to start");
                return 1;
            }

            if (process.HasExited)
            {
                Console.WriteLine("[SideHub] Error: Daemon process exited immediately");
                Console.WriteLine("[SideHub] Check logs for details: " + logFile);
                manager.RemovePidFile();
                return 1;
            }

            Console.WriteLine($"[SideHub] Agent started in background (PID: {pid})");
            Console.WriteLine($"[SideHub] Logs: {logFile}");
            Console.WriteLine($"[SideHub] Use 'sidehub-agent logs' to view logs");
            Console.WriteLine($"[SideHub] Use 'sidehub-agent stop' to stop");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SideHub] Error starting daemon: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> RunForeground(string baseDirectory, CancellationToken ct)
    {
        var configs = AgentConfig.LoadAll(baseDirectory);

        Console.WriteLine($"[SideHub] Found {configs.Count} agent(s) in .sidehub/");

        var tasks = configs.Select(config =>
        {
            var runner = new AgentRunner(config, baseDirectory);
            return runner.RunAsync(ct);
        }).ToList();

        await Task.WhenAll(tasks);

        Console.WriteLine("[SideHub] All agents shut down");
        return 0;
    }

    public static async Task<int> RunForegroundDaemon(string baseDirectory, string logFile, string pidFile, CancellationToken ct)
    {
        // Write our PID to the file
        File.WriteAllText(pidFile, Environment.ProcessId.ToString());

        // Redirect console output to log file
        using var logWriter = new StreamWriter(logFile, append: true) { AutoFlush = true };
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        var timestampWriter = new TimestampTextWriter(logWriter);
        Console.SetOut(timestampWriter);
        Console.SetError(timestampWriter);

        try
        {
            return await RunForeground(baseDirectory, ct);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);

            // Clean up PID file on exit
            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }
        }
    }

    /// <summary>
    /// TextWriter that prefixes each line with a timestamp.
    /// </summary>
    private class TimestampTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private bool _atLineStart = true;

        public TimestampTextWriter(TextWriter inner) => _inner = inner;

        public override System.Text.Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            if (_atLineStart && value != '\r' && value != '\n')
            {
                _inner.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ");
                _atLineStart = false;
            }

            _inner.Write(value);

            if (value == '\n')
            {
                _atLineStart = true;
            }
        }

        public override void WriteLine(string? value)
        {
            if (_atLineStart)
            {
                _inner.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ");
            }
            _inner.WriteLine(value);
            _atLineStart = true;
        }

        public override void Flush() => _inner.Flush();
    }

    public static async Task<int> Logs(string baseDirectory, bool follow)
    {
        var manager = new DaemonManager(baseDirectory);

        if (!File.Exists(manager.LogFile))
        {
            Console.WriteLine("[SideHub] No log file found");
            Console.WriteLine($"[SideHub] Expected at: {manager.LogFile}");
            return 1;
        }

        using var stream = manager.OpenLogForReading();
        if (stream == null)
        {
            Console.WriteLine("[SideHub] Could not open log file");
            return 1;
        }

        using var reader = new StreamReader(stream);

        // Print existing content
        var existingContent = await reader.ReadToEndAsync();
        if (!string.IsNullOrEmpty(existingContent))
        {
            Console.Write(existingContent);
        }

        if (!follow)
            return 0;

        // Follow mode: keep reading new lines
        Console.WriteLine("[SideHub] Following logs (Ctrl+C to stop)...");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line != null)
                {
                    Console.WriteLine(line);
                }
                else
                {
                    await Task.Delay(100, cts.Token);
                }
            }
        }
        catch (OperationCanceledException) { }

        return 0;
    }

    public static int Stop(string baseDirectory)
    {
        var manager = new DaemonManager(baseDirectory);

        if (!manager.IsRunning())
        {
            Console.WriteLine("[SideHub] No agent is running");
            manager.RemovePidFile(); // Clean up stale PID file
            return 0;
        }

        var pid = manager.ReadPid();
        Console.WriteLine($"[SideHub] Stopping agent (PID: {pid})...");

        if (manager.StopDaemon())
        {
            Console.WriteLine("[SideHub] Agent stopped");
            return 0;
        }
        else
        {
            Console.WriteLine("[SideHub] Failed to stop agent");
            return 1;
        }
    }

    public static int Status(string baseDirectory)
    {
        var manager = new DaemonManager(baseDirectory);
        var pid = manager.ReadPid();

        if (manager.IsRunning())
        {
            Console.WriteLine($"[SideHub] Agent is running (PID: {pid})");
            Console.WriteLine($"[SideHub] Logs: {manager.LogFile}");

            // Show log file size
            if (File.Exists(manager.LogFile))
            {
                var fileInfo = new FileInfo(manager.LogFile);
                Console.WriteLine($"[SideHub] Log size: {FormatBytes(fileInfo.Length)}");
            }

            return 0;
        }
        else
        {
            Console.WriteLine("[SideHub] Agent is not running");
            if (pid != null)
            {
                Console.WriteLine($"[SideHub] Stale PID file found ({pid}), cleaning up...");
                manager.RemovePidFile();
            }
            return 1;
        }
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: sidehub-agent [command] [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  start           Start the agent (default)");
        Console.WriteLine("    -d, --daemon  Run in background");
        Console.WriteLine("  stop            Stop the running agent");
        Console.WriteLine("  logs            Show agent logs");
        Console.WriteLine("    -f, --follow  Follow log output (default)");
        Console.WriteLine("    --no-follow   Don't follow, just print current logs");
        Console.WriteLine("  status          Show agent status");
        Console.WriteLine("  help            Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  sidehub-agent              # Start in foreground");
        Console.WriteLine("  sidehub-agent start -d     # Start in background");
        Console.WriteLine("  sidehub-agent logs         # View and follow logs");
        Console.WriteLine("  sidehub-agent stop         # Stop the agent");
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
