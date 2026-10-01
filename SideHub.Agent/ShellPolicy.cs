using System.Runtime.InteropServices;

namespace SideHub.Agent;

/// <summary>
/// Which shells a <c>pty.start</c> may spawn. The backend only names a shell (<c>bash</c>,
/// <c>zsh</c>…); the agent resolves it to a binary from fixed system directories, so a
/// backend cannot make the PTY run an arbitrary executable (<c>/tmp/x</c>, <c>python</c>…).
/// </summary>
public static class ShellPolicy
{
    private static readonly HashSet<string> UnixShells = new(StringComparer.Ordinal)
    {
        "bash", "zsh", "sh", "dash", "fish", "pwsh",
    };

    private static readonly HashSet<string> WindowsShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh",
    };

    /// <summary>Where Unix shells are looked up — never the PATH, which the daemon may have
    /// inherited from anywhere.</summary>
    private static readonly string[] UnixShellDirectories =
    [
        "/bin", "/usr/bin", "/usr/local/bin", "/opt/homebrew/bin",
    ];

    /// <summary>
    /// Resolves the shell a <c>pty.start</c> asked for. Empty means the platform default.
    /// A bare name must be allowlisted and is resolved locally; an absolute path is accepted
    /// only when it names an allowlisted shell inside one of those system directories.
    /// Anything else is refused.
    /// </summary>
    public static bool TryResolve(string? requested, out string resolved) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? TryResolveWindows(requested, out resolved)
            : TryResolveUnix(requested, UnixShellDirectories, File.Exists, out resolved);

    public static bool TryResolveUnix(
        string? requested,
        IReadOnlyList<string> directories,
        Func<string, bool> fileExists,
        out string resolved)
    {
        resolved = string.Empty;
        var name = string.IsNullOrWhiteSpace(requested) ? SystemInfoProvider.GetDefaultShell() : requested.Trim();

        if (name.Contains('/'))
        {
            var fileName = Path.GetFileName(name);
            if (!UnixShells.Contains(fileName) || !directories.Contains(Path.GetDirectoryName(name)) || !fileExists(name))
                return false;
            resolved = name;
            return true;
        }

        if (!UnixShells.Contains(name))
            return false;

        foreach (var dir in directories)
        {
            var candidate = $"{dir}/{name}";
            if (fileExists(candidate))
            {
                resolved = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool TryResolveWindows(string? requested, out string resolved)
    {
        // Windows resolves these fixed names from its system search path; anything carrying a
        // path or an unknown name is refused.
        var name = string.IsNullOrWhiteSpace(requested) ? SystemInfoProvider.GetDefaultShell() : requested.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        resolved = WindowsShells.Contains(name) ? name.ToLowerInvariant() : string.Empty;
        return resolved.Length > 0;
    }
}
