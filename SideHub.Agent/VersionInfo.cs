using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// Versions reported to the backend on connection: the agent's own (set from the release tag via
/// <c>-p:Version=</c>), the runtime CLIs found on this machine and whether they are logged in.
/// </summary>
public static partial class VersionInfo
{
    /// <summary>Runtimes probed with <c>&lt;cli&gt; --version</c>; keys match the backend runtime names.</summary>
    public static IReadOnlyList<string> ProbedClis { get; } = ["claude", "codex", "gemini", "copilot"];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// The CLIs that say whether they are logged in, and how: a CLI that is not would stop a run on its login screen.
    /// Only a clear answer counts (see <see cref="ParseLoggedIn"/>); an older CLI without the command is left unknown.
    /// </summary>
    private static readonly Dictionary<string, string> AuthStatusCommands = new()
    {
        ["claude"] = "claude auth status",
        ["codex"] = "codex login status",
    };

    private static readonly SemaphoreSlim ProbeLock = new(1, 1);
    private static IReadOnlyDictionary<string, string>? _cliVersions;
    private static IReadOnlyDictionary<string, bool> _cliAuth = new Dictionary<string, bool>();
    private static DateTime _probedAt;

    public static string AgentVersion { get; } = ReadAgentVersion();

    private static string ReadAgentVersion()
    {
        var informational = typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // The SDK appends "+<commit sha>" to the informational version.
        return informational?.Split('+')[0] ?? "unknown";
    }

    /// <summary>The last probe result while it is fresh, otherwise null (probe with <see cref="GetCliVersionsAsync"/>).</summary>
    public static IReadOnlyDictionary<string, string>? CachedCliVersions =>
        _cliVersions is not null && DateTime.UtcNow - _probedAt < CacheTtl ? _cliVersions : null;

    /// <summary>Whether the installed CLIs are logged in, from the same probe as <see cref="CachedCliVersions"/>; a CLI
    /// whose state is unknown is absent. Null while the versions are not probed.</summary>
    public static IReadOnlyDictionary<string, bool>? CachedCliAuth => CachedCliVersions is null ? null : _cliAuth;

    /// <summary>
    /// Detected CLI versions, keyed by runtime. Probed at most once per <see cref="CacheTtl"/> so that
    /// reconnections don't spawn the CLIs again; a CLI that is missing or times out is simply absent.
    /// Slow (login shell + CLI startup, several seconds): never await it on the connection path.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> GetCliVersionsAsync(CancellationToken ct)
    {
        await ProbeLock.WaitAsync(ct);
        try
        {
            if (CachedCliVersions is { } cached)
                return cached;

            var probes = ProbedClis.Select(async cli => (cli, version: await ProbeVersionAsync(cli, ct)));
            var results = await Task.WhenAll(probes);

            var versions = results
                .Where(r => r.version is not null)
                .ToDictionary(r => r.cli, r => r.version!);
            _cliAuth = await ProbeAuthAsync(versions, ct);
            _cliVersions = versions;
            _probedAt = DateTime.UtcNow;
            return _cliVersions;
        }
        finally
        {
            ProbeLock.Release();
        }
    }

    /// <summary>
    /// Probes again whether the installed CLIs are logged in, when one was not (the user may have logged in since,
    /// typically in a terminal that just closed). True when the state changed and is worth reporting.
    /// </summary>
    public static async Task<bool> RefreshCliAuthAsync(CancellationToken ct)
    {
        var versions = CachedCliVersions;
        if (versions is null || !_cliAuth.Values.Contains(false))
            return false;
        await ProbeLock.WaitAsync(ct);
        try
        {
            var previous = _cliAuth;
            _cliAuth = await ProbeAuthAsync(versions, ct);
            return previous.Count != _cliAuth.Count
                || previous.Any(entry => !_cliAuth.TryGetValue(entry.Key, out var now) || now != entry.Value);
        }
        finally
        {
            ProbeLock.Release();
        }
    }

    private static async Task<IReadOnlyDictionary<string, bool>> ProbeAuthAsync(
        IReadOnlyDictionary<string, string> versions, CancellationToken ct)
    {
        var probes = AuthStatusCommands
            .Where(command => versions.ContainsKey(command.Key))
            .Select(async command => (cli: command.Key, output: await RunCliAsync(command.Value, ct)));
        var results = await Task.WhenAll(probes);
        return results
            .Select(r => (r.cli, loggedIn: r.output is { } output ? ParseLoggedIn(r.cli, output.ExitCode, output.Stdout + "\n" + output.Stderr) : null))
            .Where(r => r.loggedIn is not null)
            .ToDictionary(r => r.cli, r => r.loggedIn!.Value);
    }

    /// <summary>
    /// From the command's output (stdout and stderr): claude prints <c>{"loggedIn": true|false, …}</c> on stdout, codex
    /// "Logged in using …" or "Not logged in" on stderr. Anything else (a CLI that predates the command, an error) is
    /// unknown: null.
    /// </summary>
    public static bool? ParseLoggedIn(string cli, int exitCode, string output)
    {
        switch (cli)
        {
            case "claude":
                var match = LoggedInPattern().Match(output);
                return match.Success ? match.Groups[1].Value == "true" : null;
            case "codex":
                if (output.Contains("Not logged in", StringComparison.Ordinal))
                    return false;
                return exitCode == 0 && output.Contains("Logged in", StringComparison.Ordinal) ? true : null;
            default:
                return null;
        }
    }

    private static async Task<string?> ProbeVersionAsync(string cli, CancellationToken ct) =>
        await RunCliAsync($"{cli} --version", ct) is { ExitCode: 0 } result ? ParseVersion(result.Stdout) : null;

    /// <summary>Runs a CLI command line; null when it could not run or timed out.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)?> RunCliAsync(string commandLine, CancellationToken ct)
    {
        // Through a login shell so the user's PATH (nvm, ~/.local/bin, …) resolves the CLI like in a PTY.
        // cmd.exe is resolved to its absolute path and told not to look the CLI up in the daemon's
        // working directory (a repository) before the PATH.
        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (ExecutableResolver.Resolve("cmd.exe") is not { } cmd)
                return null;
            psi = new ProcessStartInfo(cmd) { ArgumentList = { "/c", commandLine } };
            psi.Environment[ExecutableResolver.NoCurrentDirectoryLookupVariable] = "1";
        }
        else
        {
            psi = new ProcessStartInfo(File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh") { ArgumentList = { "-l", "-c", commandLine } };
        }
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            // No terminal and nothing to read: a CLI that took the arguments for a prompt ends instead of waiting.
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (Exception)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return null;
        }
    }

    /// <summary>"2.1.3 (Claude Code)" → "2.1.3", "codex-cli 0.46.0" → "0.46.0".</summary>
    public static string? ParseVersion(string output)
    {
        var match = VersionPattern().Match(output);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?(-[0-9A-Za-z.\-]+)?")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"""loggedIn""\s*:\s*(true|false)")]
    private static partial Regex LoggedInPattern();
}
