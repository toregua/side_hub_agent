using System.Diagnostics;
using System.Text;
using SideHub.Cli.Launch;

namespace SideHub.Cli;

/// <summary>
/// Read-only git queries about the directory a command runs in. They never fail the command: no git, not a
/// repository, an error or a timeout all give null.
/// </summary>
public static class GitWorkTree
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Whether <c>git status --porcelain</c> lists anything; null outside a work tree or when git fails.</summary>
    public static async Task<bool?> HasUncommittedChangesAsync(string directory) =>
        await RunAsync(directory, "status", "--porcelain") is { } status ? status.Trim().Length > 0 : null;

    /// <summary>The full hash of HEAD; null outside a repository, before the first commit or when git fails.</summary>
    public static async Task<string?> HeadAsync(string directory) =>
        await RunAsync(directory, "rev-parse", "--verify", "--quiet", "HEAD") is { } head && head.Trim() is { Length: > 0 } sha
            ? sha
            : null;

    /// <summary>git's standard output, or null when it could not run, failed or timed out.</summary>
    private static async Task<string?> RunAsync(string directory, params string[] args)
    {
        // Never a bare "git": it would be looked up in the current directory (the repository) first.
        var path = Environment.GetEnvironmentVariable("PATH");
        var git = OperatingSystem.IsWindows() ? RealCli.FindWindows("git", path, []) : RealCli.FindUnix("git", path, []);
        if (git is null)
            return null;
        try
        {
            var psi = new ProcessStartInfo(git)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            // A repository's config must not run a command for a mere status.
            foreach (var arg in new[] { "-c", "core.fsmonitor=false" }.Concat(args))
                psi.ArgumentList.Add(arg);
            // Never take the index lock away from the agent's own git commands.
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            using var cts = new CancellationTokenSource(Timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }
            await stderr;
            return process.ExitCode == 0 ? await stdout : null;
        }
        catch
        {
            return null;
        }
    }
}
