using System.Diagnostics;

namespace SideHub.Agent;

public class CommandExecutor
{
    private readonly string _workingDirectory;
    private bool _isBusy;
    private readonly object _lock = new();
    private Process? _currentProcess;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromHours(1);

    /// <summary>Longest output line forwarded, in characters: a longer one is replaced by a notice, never buffered whole.</summary>
    public const int MaxOutputLineLength = 1024 * 1024;

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
            var (fileName, args) = GetShellCommand(shell, command);

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            // Same allowlist as the PTYs: the command must not inherit the daemon's secrets.
            DaemonEnvironmentPolicy.Restrict(psi.Environment);
            if (OperatingSystem.IsWindows())
                psi.Environment[ExecutableResolver.NoCurrentDirectoryLookupVariable] = "1";

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

    private static (string fileName, string[] args) GetShellCommand(string shell, string command)
    {
        // Note: Don't use -i (interactive) flag for non-interactive command execution.
        // It requires a real TTY for job control and produces warnings like:
        // "bash: initialize_job_control: no job control in background: Bad file descriptor"
        //
        // Uses ArgumentList (via string[]) instead of a single Arguments string
        // so .NET handles escaping correctly across all platforms.
        //
        // The binary comes from ShellPolicy, like a pty.start: fixed system folders on Unix (never the PATH),
        // absolute PATH entries only on Windows. bash/sh/zsh run as login shells (-l): the command sees the user's
        // profile, as an interactive terminal does (see SECURITY.md).
        var name = shell.ToLowerInvariant();
        string[] args = name switch
        {
            "bash" or "sh" or "zsh" => ["-l", "-c", command],
            "powershell" or "pwsh" => ["-Command", command],
            "cmd" => ["/c", command],
            _ => throw new ArgumentException($"Unsupported shell: {shell}")
        };
        // "powershell" has always meant PowerShell 7 (pwsh) here.
        var binary = name == "powershell" ? "pwsh" : name;
        if (!ShellPolicy.TryResolve(binary, out var fileName))
            throw new ArgumentException($"Shell not found: {binary}");
        return (fileName, args);
    }

    private static async Task ReadStreamAsync(
        StreamReader stream,
        string streamName,
        Func<string, string, Task> onOutput,
        CancellationToken ct)
    {
        // The command's output is not ours: a line without a newline must not grow the daemon's memory
        // until it takes down every agent it hosts.
        var reader = new BoundedLineReader(stream, MaxOutputLineLength);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await reader.ReadLineAsync(ct) is not { } line) break;
                await onOutput(streamName, line.TooLong
                    ? $"[line longer than {MaxOutputLineLength} characters omitted]"
                    : line.Text);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
    }
}
