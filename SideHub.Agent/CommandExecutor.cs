using System.Diagnostics;

namespace SideHub.Agent;

public class CommandExecutor
{
    private readonly string _workingDirectory;
    private bool _isBusy;
    private readonly object _lock = new();

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

            process.Start();

            var stdoutTask = ReadStreamAsync(process.StandardOutput, "stdout", onOutput, ct);
            var stderrTask = ReadStreamAsync(process.StandardError, "stderr", onOutput, ct);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);

            return process.ExitCode;
        }
        finally
        {
            lock (_lock)
            {
                _isBusy = false;
            }
        }
    }

    private static (string fileName, string arguments) GetShellCommand(string shell, string command)
    {
        return shell.ToLowerInvariant() switch
        {
            "bash" => ("/bin/bash", $"-c \"{EscapeForShell(command)}\""),
            "sh" => ("/bin/sh", $"-c \"{EscapeForShell(command)}\""),
            "zsh" => ("/bin/zsh", $"-c \"{EscapeForShell(command)}\""),
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
