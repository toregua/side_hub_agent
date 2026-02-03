using System.Diagnostics;

namespace SideHub.Agent;

public class CommandExecutor
{
    private readonly string _workingDirectory;
    private bool _isBusy;
    private readonly object _lock = new();
    private Process? _currentProcess;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromHours(1);

    public bool IsBusy
    {
        get { lock (_lock) return _isBusy; }
    }

    public CommandExecutor(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public async Task<int> ExecuteAsync(
        string command,
        string shell,
        Func<string, string, Task> onOutput,
        CancellationToken ct)
    {
        lock (_lock)
        {
            if (_isBusy)
            {
                throw new InvalidOperationException("Agent is already executing a command");
            }
            _isBusy = true;
        }

        using var timeoutCts = new CancellationTokenSource(CommandTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            var (fileName, arguments) = GetShellCommand(shell, command);

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            _currentProcess = process;

            process.Start();

            var stdoutTask = ReadStreamAsync(process.StandardOutput, "stdout", onOutput, linkedToken);
            var stderrTask = ReadStreamAsync(process.StandardError, "stderr", onOutput, linkedToken);

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync(linkedToken);
                return process.ExitCode;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                Console.WriteLine($"[CommandExecutor] Command timed out after {CommandTimeout.TotalMinutes} minutes, killing process");
                await onOutput("stderr", $"\n[TIMEOUT] Command exceeded {CommandTimeout.TotalMinutes} minute limit and was terminated.");
                KillProcess(process);
                return -1;
            }
        }
        finally
        {
            _currentProcess = null;
            lock (_lock)
            {
                _isBusy = false;
            }
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CommandExecutor] Failed to kill process: {ex.Message}");
        }
    }

    private static (string fileName, string arguments) GetShellCommand(string shell, string command)
    {
        // Note: Don't use -i (interactive) flag for non-interactive command execution.
        // It requires a real TTY for job control and produces warnings like:
        // "bash: initialize_job_control: no job control in background: Bad file descriptor"
        return shell.ToLowerInvariant() switch
        {
            "bash" => ("/bin/bash", $"-l -c \"{EscapeForShell(command)}\""),
            "sh" => ("/bin/sh", $"-l -c \"{EscapeForShell(command)}\""),
            "zsh" => ("/bin/zsh", $"-l -c \"{EscapeForShell(command)}\""),
            "powershell" or "pwsh" => ("pwsh", $"-Command \"{EscapeForPowerShell(command)}\""),
            "cmd" => ("cmd.exe", $"/c {command}"),
            _ => throw new ArgumentException($"Unsupported shell: {shell}")
        };
    }

    private static string EscapeForShell(string command)
    {
        return command.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string EscapeForPowerShell(string command)
    {
        return command.Replace("\"", "\\\"");
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        string streamName,
        Func<string, string, Task> onOutput,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                await onOutput(streamName, line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
    }
}
