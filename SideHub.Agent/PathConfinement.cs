namespace SideHub.Agent;

/// <summary>
/// Path checks shared by everything the backend may point at a location on disk (PTY working
/// directory, file.write target): a path must stay inside the agent's working directory both
/// lexically and once symbolic links are resolved.
/// </summary>
public static class PathConfinement
{
    // Same bound as the kernel's SYMLOOP_MAX: deeper chains are a loop, not a layout.
    private const int MaxLinkDepth = 40;

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or below it (ordinal, no resolution).</summary>
    public static bool IsWithin(string root, string path)
    {
        var trimmedRoot = root.Length > 1 ? root.TrimEnd(Path.DirectorySeparatorChar) : root;
        if (path == trimmedRoot) return true;
        var prefix = trimmedRoot.EndsWith(Path.DirectorySeparatorChar) ? trimmedRoot : trimmedRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Resolves symlinks segment by segment, the last one included and whether they point
    /// at a file or a folder (missing segments are kept as is), so a link inside the working
    /// directory cannot lead outside it. Throws <see cref="IOException"/> on a link loop.</summary>
    public static string RealPath(string path) => RealPath(path, 0);

    private static string RealPath(string path, int depth)
    {
        if (depth > MaxLinkDepth)
            throw new IOException($"Too many levels of symbolic links: {path}");

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "/";
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            string? linkTarget = null;
            try
            {
                linkTarget = new FileInfo(next).LinkTarget;
            }
            catch { /* unreadable: keep the lexical path */ }

            // One hop at a time (a relative target is relative to the link's folder, already resolved)
            // so a loop ends in the depth check instead of passing as a plain path.
            current = linkTarget is null ? next : RealPath(Path.Combine(current, linkTarget), depth + 1);
        }
        return current;
    }
}
