using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// Versions reported to the backend on connection: the agent's own (set from the release tag via
/// <c>-p:Version=</c>) and the runtime CLIs found on this machine.
/// </summary>
public static partial class VersionInfo
{
    /// <summary>Runtimes probed with <c>&lt;cli&gt; --version</c>; keys match the backend runtime names.</summary>
    private static readonly string[] Clis = ["claude", "codex", "gemini", "copilot"];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private static readonly SemaphoreSlim ProbeLock = new(1, 1);
    private static IReadOnlyDictionary<string, string>? _cliVersions;
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

            var probes = Clis.Select(async cli => (cli, version: await ProbeAsync(cli, ct)));
            var results = await Task.WhenAll(probes);

            _cliVersions = results
                .Where(r => r.version is not null)
                .ToDictionary(r => r.cli, r => r.version!);
            _probedAt = DateTime.UtcNow;
            return _cliVersions;
        }
        finally
        {
            ProbeLock.Release();
        }
    }

    private static async Task<string?> ProbeAsync(string cli, CancellationToken ct)
    {
        // Through a login shell so the user's PATH (nvm, ~/.local/bin, …) resolves the CLI like in a PTY.
        var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", $"{cli} --version" } }
            : new ProcessStartInfo(File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh") { ArgumentList = { "-l", "-c", $"{cli} --version" } };
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            _ = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return process.ExitCode == 0 ? ParseVersion(await stdout) : null;
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
}
