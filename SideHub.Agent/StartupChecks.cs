using System.Diagnostics;
using System.Text;

namespace SideHub.Agent;

/// <summary>
/// Machine checks run once per process, in the background after start, whose failures make the agent useless even when
/// it connects: terminals need pty-helper (Node + node-pty), runs need a coding CLI. Each problem is logged and reported
/// through <see cref="DiagnosticReporter"/>.
/// </summary>
public static class StartupChecks
{
    public record Problem(string Reason, string Detail);

    private static readonly TimeSpan HelperReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly object Gate = new();
    private static Task<IReadOnlyList<Problem>>? _run;

    /// <summary>The problems found on this machine; the checks run on the first call only.</summary>
    public static Task<IReadOnlyList<Problem>> RunOnceAsync(CancellationToken ct)
    {
        lock (Gate)
            return _run ??= RunAsync(ct);
    }

    private static async Task<IReadOnlyList<Problem>> RunAsync(CancellationToken ct)
    {
        var problems = new List<Problem>();
        if (await CheckPtyHelperAsync(ct) is { } helperProblem)
            problems.Add(new Problem(DiagnosticReasons.PtyHelperFailed, helperProblem));

        var clis = await VersionInfo.GetCliVersionsAsync(ct);
        if (clis.Count == 0)
            problems.Add(new Problem(DiagnosticReasons.CliMissing,
                $"none of {string.Join(", ", VersionInfo.ProbedClis)} answered `--version` in a login shell"));
        return problems;
    }

    /// <summary>
    /// Starts pty-helper the way terminals do and waits for its "ready" line, which it prints once node-pty is loaded.
    /// Returns why it did not start (Node missing, native module built for another Node version…), or null.
    /// </summary>
    public static async Task<string?> CheckPtyHelperAsync(CancellationToken ct)
    {
        var helperPath = NodePtyExecutor.ResolveHelperPath();
        if (!File.Exists(helperPath))
            return "pty-helper/index.js is missing from the install folder";
        // Never a bare "node": it would be looked up in the working directory first (see NodePtyExecutor).
        if (ExecutableResolver.Resolve("node") is not { } nodePath)
            return "node not found in the agent's PATH (Node.js is required for terminals)";

        var psi = new ProcessStartInfo(nodePath)
        {
            ArgumentList = { helperPath },
            WorkingDirectory = Path.GetDirectoryName(helperPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelperReadyTimeout);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Contains("\"ready\"", StringComparison.Ordinal))
                    return null;
            }

            // stdout closed before "ready": the helper died while loading
            await process.WaitForExitAsync(timeout.Token);
            return $"pty-helper exited with code {process.ExitCode} before it was ready: {FirstErrorLine(await stderr) ?? "no error output"}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"pty-helper printed no ready signal within {HelperReadyTimeout.TotalSeconds:F0} s";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return $"couldn't start node: {ex.Message}";
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    /// <summary>The line naming the error in a Node crash output ("Error: The module … was compiled against …").</summary>
    public static string? FirstErrorLine(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(l => l.Contains("Error", StringComparison.Ordinal) && !l.StartsWith("at ", StringComparison.Ordinal))
               ?? lines.FirstOrDefault();
    }
}
