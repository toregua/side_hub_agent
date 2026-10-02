using System.Text.Json;
using System.Text.RegularExpressions;

namespace SideHub.Cli.Launch;

/// <summary>
/// Finds the installed coding CLI behind a name, skipping SideHub's own wrappers (which call the launcher:
/// resolving to one of them would loop). Only absolute PATH entries are searched, never the current
/// directory: it is a repository, where a committed <c>./claude</c> must not run.
/// </summary>
public static partial class RealCli
{
    // Windows: what a shell would run for a bare name. .ps1 shims are skipped: npm installs a .cmd next to them.
    private static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat"];

    /// <summary>The program to start and the arguments to put before the CLI's own.</summary>
    public sealed record Target(string FileName, IReadOnlyList<string> LeadingArguments, string? ScriptPath);

    public static Target? Resolve(string cli, string? pathVariable, IReadOnlyCollection<string> skippedDirectories)
    {
        var path = OperatingSystem.IsWindows()
            ? FindWindows(cli, pathVariable, skippedDirectories)
            : FindUnix(cli, pathVariable, skippedDirectories);
        if (path is null)
            return null;

        // An npm .cmd shim re-parses the arguments through cmd.exe, which mangles quotes, % and newlines
        // in a prompt: run its script with node directly instead.
        if (OperatingSystem.IsWindows() && path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            && NpmShimScript(path) is { } script)
        {
            var bundledNode = Path.Combine(Path.GetDirectoryName(path)!, "node.exe");
            var node = File.Exists(bundledNode) ? bundledNode : FindWindows("node", pathVariable, skippedDirectories);
            if (node is not null)
                return new Target(node, [script], script);
        }

        return new Target(path, [], ResolveLinks(path));
    }

    public static string? FindUnix(string name, string? pathVariable, IReadOnlyCollection<string> skippedDirectories)
    {
        foreach (var dir in (pathVariable ?? "").Split(':').Where(d => d.StartsWith('/')))
        {
            if (IsSkipped(dir, skippedDirectories))
                continue;
            var candidate = Path.Join(dir, name);
            if (IsExecutableFile(candidate) && !IsSkipped(Path.GetDirectoryName(ResolveLinks(candidate))!, skippedDirectories))
                return candidate;
        }
        return null;
    }

    public static string? FindWindows(string name, string? pathVariable, IReadOnlyCollection<string> skippedDirectories)
    {
        var directories = (pathVariable ?? "").Split(';')
            .Select(d => d.Trim().Trim('"'))
            .Where(d => Path.IsPathFullyQualified(d));
        foreach (var dir in directories)
        {
            if (IsSkipped(dir, skippedDirectories))
                continue;
            foreach (var extension in WindowsExtensions)
            {
                var candidate = Path.Join(dir, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    [GeneratedRegex(@"""%dp0%\\([^""]+)""\s+%\*", RegexOptions.IgnoreCase)]
    private static partial Regex NpmShimTarget();

    /// <summary>The script an npm (cmd-shim) <c>.cmd</c> runs with node, or null when it isn't one.</summary>
    public static string? NpmShimScript(string cmdPath)
    {
        try
        {
            var match = NpmShimTarget().Match(File.ReadAllText(cmdPath));
            if (!match.Success)
                return null;
            var relative = match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
            var script = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cmdPath)!, relative));
            return File.Exists(script) ? script : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The version in the <c>package.json</c> of <paramref name="packageName"/> that holds
    /// <paramref name="file"/> (the CLI's script, links resolved), or null.</summary>
    public static Version? PackageVersion(string? file, string packageName)
    {
        for (var dir = file is null ? null : Path.GetDirectoryName(file); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var manifest = Path.Combine(dir, "package.json");
            if (!File.Exists(manifest))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || name.GetString() != packageName)
                    continue;
                if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                    && Version.TryParse(version.GetString()!.Split('-', '+')[0], out var parsed))
                    return parsed;
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
        return null;
    }

    private static bool IsSkipped(string dir, IReadOnlyCollection<string> skippedDirectories)
    {
        var normalized = Normalize(dir);
        return skippedDirectories.Any(s => string.Equals(Normalize(s), normalized,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private static string Normalize(string dir)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(ResolveLinks(Path.GetFullPath(dir)));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return dir;
        }
    }

    private static string ResolveLinks(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return File.Exists(path) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & anyExecute) != 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
