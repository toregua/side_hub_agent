namespace SideHub.Agent;

/// <summary>
/// Where a <c>file.write</c> from the backend may land: a file inside the agent's working
/// directory once symlinks are resolved, never under <c>.git/</c> (hooks = code execution) or
/// <c>.sidehub/</c> (agent config and token), and never larger than <see cref="MaxFileBytes"/>.
/// </summary>
public static class FileWritePolicy
{
    public const long MaxFileBytes = 50L * 1024 * 1024;

    private static readonly string[] ProtectedDirectories = [".git", ".sidehub"];

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
        if (IsProtected(root, candidate) || IsProtected(realRoot, realCandidate))
        {
            error = $"Path '{candidate}' is in a protected folder ({string.Join(", ", ProtectedDirectories)})";
            return false;
        }

        resolved = realCandidate;
        return true;
    }

    private static bool IsStrictlyWithin(string root, string path) =>
        PathConfinement.IsWithin(root, path) && Path.GetRelativePath(root, path) != ".";

    // Case-insensitive: on macOS/Windows file systems ".GIT" is ".git".
    private static bool IsProtected(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => ProtectedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
