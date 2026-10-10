using System.Diagnostics;
using System.Text;

namespace SideHub.Agent;

/// <summary>
/// Runs the git CLI with the overrides that keep a repository (and the daemon's environment) from making git run
/// commands or look elsewhere, and a time limit after which git is killed.
/// </summary>
public static class GitCommand
{
    /// <summary>Enough for local queries; network commands pass their own limit.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // Windows: "/dev/null" would be C:\dev\null, a folder any local user may create and fill with hooks. NUL is a
    // reserved device name, so no "NUL\post-checkout" can exist, whatever the drive or the working directory.
    private static readonly string HooksPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>Overrides for settings a repository's own config could use to run a command. The
    /// directory is untrusted (a commit can ship an embedded bare repository whose config sets
    /// <c>core.fsmonitor</c>, run by plain read-only queries); <c>-c</c> wins over every config file,
    /// and <c>safe.bareRepository</c> is only honored from there (not from the repository).</summary>
    private static readonly string[] SafeConfig =
    [
        "-c", "core.fsmonitor=false",
        "-c", "safe.bareRepository=explicit",
        "-c", $"core.hooksPath={HooksPath}",
    ];

    // The user's own choice of ssh transport for a fetch (a key, a jump host). It comes from the daemon's
    // environment, like SSH_AUTH_SOCK, not from a repository, and cannot point git at another repository or
    // undo the overrides above.
    private static readonly HashSet<string> SshVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "GIT_SSH", "GIT_SSH_COMMAND", "GIT_SSH_VARIANT",
    };

    public sealed record Result(int ExitCode, string Output, string Error)
    {
        public bool Succeeded => ExitCode == 0;
    }

    /// <summary>Runs git in <paramref name="workingDirectory"/>; null when git is unavailable, could not start, or
    /// was killed after <paramref name="timeout"/> (or on <paramref name="ct"/>).</summary>
    /// <param name="keepSshEnvironment">Keep the daemon's GIT_SSH* variables (commands that reach a remote).</param>
    /// <param name="environment">Variables set for this command only, after the daemon's GIT_* ones are removed
    /// (e.g. GIT_INDEX_FILE): chosen by the agent, never by the repository.</param>
    public static async Task<Result?> RunAsync(
        string workingDirectory, TimeSpan timeout, IEnumerable<string> args,
        bool keepSshEnvironment = false, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        // Never a bare "git": it would be looked up in the working directory (the repository) first.
        if (ExecutableResolver.Resolve("git") is not { } gitPath)
            return null;
        try
        {
            var psi = new ProcessStartInfo(gitPath)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // git writes UTF-8 (commit messages, branch names); .NET would decode it with the
                // console code page on Windows.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in SafeConfig.Concat(args))
                psi.ArgumentList.Add(arg);
            // The daemon's environment must not steer git either (GIT_DIR, GIT_CONFIG_PARAMETERS,
            // GIT_CONFIG_COUNT/KEY/VALUE could point it elsewhere or undo the overrides above).
            foreach (var name in psi.Environment.Keys
                         .Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                         .Where(k => !keepSshEnvironment || !SshVariables.Contains(k))
                         .ToList())
                psi.Environment.Remove(name);
            // Never take the index lock away from the user's own git commands for a mere refresh.
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            if (environment is not null)
                foreach (var (name, value) in environment)
                    psi.Environment[name] = value;

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
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
            return new Result(process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            return null;
        }
    }
}
