using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// Where a file written into the working directory may land, whether the backend sent it
/// (<c>file.write</c>, terminal attachments) or the agent generates it (skill files): a file inside
/// the agent's working directory once symlinks are resolved, never under <c>.git/</c> (hooks = code
/// execution) or <c>.sidehub/</c> (agent config, logs), never one of the files tools load as
/// code or command configuration (<see cref="ProtectedFiles"/>), and never larger than
/// <see cref="MaxFileBytes"/>. The write itself goes through <see cref="OpenWrite"/>, which follows
/// no link, so a link planted between the check and the write cannot redirect it.
/// </summary>
public static partial class FileWritePolicy
{
    public const long MaxFileBytes = 50L * 1024 * 1024;

    private static readonly string[] ProtectedDirectories = [".git", ".sidehub"];

    /// <summary>Files that make a tool run commands as soon as it opens the folder (hooks, MCP
    /// servers, tasks, direnv), matched at any depth.</summary>
    private static readonly string[][] ProtectedFiles =
    [
        [".claude", "settings.json"],
        [".claude", "settings.local.json"],
        [".mcp.json"],
        [".gemini", "settings.json"],
        [".codex", "config.toml"],
        [".envrc"],
        [".vscode", "tasks.json"],
        [".vscode", "settings.json"],
        [".vscode", "launch.json"],
    ];

    /// <summary>
    /// Resolves <paramref name="requested"/> (relative paths resolve against the working directory)
    /// to the real path the file is written to. Returns false with the reason when the target is
    /// outside the working directory, reached through a link leading outside, or protected.
    /// </summary>
    public static bool TryResolveTarget(string workingDirectory, string requested, out string resolved, out string error)
    {
        resolved = string.Empty;
        error = string.Empty;

        string root, candidate, realRoot, realCandidate;
        try
        {
            root = Path.GetFullPath(workingDirectory);
            candidate = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));
            realRoot = PathConfinement.RealPath(root);
            realCandidate = PathConfinement.RealPath(candidate);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            error = $"Path '{requested}' cannot be resolved";
            return false;
        }

        if (!IsStrictlyWithin(root, candidate))
        {
            error = $"Path '{candidate}' is outside the allowed working directory";
            return false;
        }
        if (!IsStrictlyWithin(realRoot, realCandidate))
        {
            error = $"Path '{candidate}' resolves to '{realCandidate}', outside the allowed working directory";
            return false;
        }
        if (OperatingSystem.IsWindows()
            && (HasWindowsAlias(Path.GetRelativePath(root, candidate)) || HasWindowsAlias(Path.GetRelativePath(realRoot, realCandidate))))
        {
            error = $"Path '{candidate}' uses a short (8.3) name or an alternate data stream";
            return false;
        }
        if (IsProtected(Path.GetRelativePath(root, candidate)) || IsProtected(Path.GetRelativePath(realRoot, realCandidate)))
        {
            error = $"Path '{candidate}' is protected ({string.Join(", ", ProtectedDirectories)} folders and tool configuration files)";
            return false;
        }

        resolved = realCandidate;
        return true;
    }

    /// <summary>
    /// Opens <paramref name="relativePath"/> (relative to the real working directory, e.g. a path
    /// returned by <see cref="TryResolveTarget"/> made relative) for writing, creating missing
    /// folders, without following any symbolic link (<see cref="ConfinedFile"/>). Refuses protected
    /// paths. <paramref name="fullPath"/> is where the file is.
    /// </summary>
    public static FileStream OpenWrite(string workingDirectory, string relativePath, FileMode mode, out string fullPath)
    {
        if ((OperatingSystem.IsWindows() && HasWindowsAlias(relativePath)) || IsProtected(relativePath))
            throw new UnauthorizedAccessException($"Path '{relativePath}' is protected");
        fullPath = Path.Combine(PathConfinement.RealPath(workingDirectory), relativePath);
        return ConfinedFile.Open(workingDirectory, relativePath, mode, FileAccess.Write);
    }

    /// <summary>Writes a file the agent generates (skill files…) with the same rules as <see cref="OpenWrite"/>.</summary>
    public static void WriteAllText(string workingDirectory, string relativePath, string content)
    {
        using var stream = OpenWrite(workingDirectory, relativePath, FileMode.Create, out _);
        stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
    }

    private static bool IsStrictlyWithin(string root, string path) =>
        PathConfinement.IsWithin(root, path) && Path.GetRelativePath(root, path) != ".";

    /// <summary>Whether a path relative to the working directory is in a protected folder or is a
    /// protected file. Case-insensitive (on macOS/Windows file systems ".GIT" is ".git"), and
    /// segments are compared the way those file systems see them (<see cref="NormalizeSegment"/>).</summary>
    public static bool IsProtected(string relativePath)
    {
        var segments = relativePath
            .Split(['/', Path.DirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeSegment)
            .ToArray();
        if (segments.Any(segment => ProtectedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            return true;
        return ProtectedFiles.Any(file => segments.Length >= file.Length
            && segments[^file.Length..].SequenceEqual(file, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A path segment as a case-insensitive file system may resolve it: Unicode-normalized (NFC)
    /// without format characters (HFS+ ignores e.g. U+200C, so ".g‌it" is ".git"), and without
    /// the trailing dots and spaces Windows drops (".git." and ".git " are ".git"). Only used to
    /// decide whether a path is protected, never to build the path written to.
    /// </summary>
    private static string NormalizeSegment(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (var rune in segment.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is not UnicodeCategory.Format)
                sb.Append(rune.ToString());
        }
        return sb.ToString().TrimEnd('.', ' ');
    }

    /// <summary>
    /// Whether a relative path names a file through a Windows alias that a textual comparison cannot
    /// see through: an 8.3 short name (<c>GIT~1</c> for <c>.git</c>) or an alternate data stream
    /// (<c>.git::$INDEX_ALLOCATION</c> is the <c>.git</c> folder). Meaningful on Windows only: on other
    /// systems "~1" and ':' are ordinary characters.
    /// </summary>
    public static bool HasWindowsAlias(string relativePath) =>
        relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Contains(':') || ShortName().IsMatch(segment));

    [GeneratedRegex(@"~\d+(\.[^.]*)?$")]
    private static partial Regex ShortName();
}
