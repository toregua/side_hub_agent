namespace SideHub.Agent;

/// <summary>
/// Resolves the binaries the agent spawns itself (<c>node</c>, <c>git</c>, shells…) to an
/// absolute path. A bare name given to <see cref="System.Diagnostics.Process.Start()"/> is looked
/// up in the working directory before the PATH (Unix: after the executable's directory; Windows:
/// <c>CreateProcess</c>, which also appends <c>.exe</c>), and the daemon's working directory is a
/// repository: a committed <c>./node</c> or <c>git.exe</c> would run with the daemon's whole
/// environment. Only absolute PATH entries are searched, then fixed system directories, never
/// the working directory.
/// </summary>
public static class ExecutableResolver
{
    /// <summary>Searched after the PATH, for daemons started with a minimal one.</summary>
    private static readonly string[] UnixFallbackDirectories =
    [
        "/usr/local/bin", "/usr/bin", "/bin", "/opt/homebrew/bin",
    ];

    /// <summary>Set in the environment of the Windows shells the agent starts: <c>cmd.exe</c> then
    /// no longer looks the commands it runs (<c>claude</c>, <c>codex</c>…) up in the current
    /// directory before the PATH.</summary>
    public const string NoCurrentDirectoryLookupVariable = "NoDefaultCurrentDirectoryInExePath";

    /// <summary>The absolute path of <paramref name="name"/> (on Windows, <c>name.exe</c> unless it
    /// already carries an extension), or null when it is not installed.</summary>
    public static string? Resolve(string name) =>
        OperatingSystem.IsWindows()
            ? ResolveWindows(name, Environment.GetEnvironmentVariable("PATH"), WindowsFallbackDirectories(), File.Exists)
            : ResolveUnix(name, Environment.GetEnvironmentVariable("PATH"), UnixFallbackDirectories, IsExecutableFile);

    public static string? ResolveUnix(
        string name,
        string? pathVariable,
        IReadOnlyList<string> fallbackDirectories,
        Func<string, bool> isExecutable)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('/'))
            return null;

        // Empty entries mean the working directory to a shell, and relative ones are resolved
        // against it: both are skipped.
        var directories = (pathVariable ?? "")
            .Split(':')
            .Where(dir => dir.StartsWith('/'))
            .Concat(fallbackDirectories);

        foreach (var dir in directories)
        {
            var candidate = Path.Join(dir, name);
            if (isExecutable(candidate))
                return candidate;
        }
        return null;
    }

    public static string? ResolveWindows(
        string name,
        string? pathVariable,
        IReadOnlyList<string> fallbackDirectories,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Trim('.').Length == 0)
            return null;
        // CreateProcess only ever appends .exe; .cmd/.bat would go through cmd.exe's own parsing.
        var fileName = Path.GetExtension(name).Length > 0 ? name : name + ".exe";

        // Entries may be quoted; only fully qualified ones (C:\…, \\server\…) are kept, the
        // others are resolved against the working directory.
        var directories = (pathVariable ?? "")
            .Split(';')
            .Select(dir => dir.Trim().Trim('"'))
            .Where(IsFullyQualifiedWindowsPath)
            .Concat(fallbackDirectories);

        foreach (var dir in directories)
        {
            var candidate = dir.TrimEnd('\\', '/') + "\\" + fileName;
            if (fileExists(candidate))
                return candidate;
        }
        return null;
    }

    // Path.IsPathFullyQualified follows the running OS: spelled out so the Windows rules are
    // testable anywhere.
    private static bool IsFullyQualifiedWindowsPath(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal);

    private static string[] WindowsFallbackDirectories()
    {
        var system = Environment.SystemDirectory;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return
        [
            system,
            Path.Combine(system, "WindowsPowerShell", "v1.0"),
            Path.Combine(programFiles, "PowerShell", "7"),
            Path.Combine(programFiles, "Git", "cmd"),
            Path.Combine(programFiles, "nodejs"),
        ];
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return File.Exists(path) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & anyExecute) != 0);
        }
        catch
        {
            return false;
        }
    }
}
