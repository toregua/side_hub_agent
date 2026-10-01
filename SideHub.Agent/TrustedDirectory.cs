namespace SideHub.Agent;

/// <summary>
/// Whether a folder may go on the PATH of the terminals the agent opens: every command typed there (claude, codex,
/// sidehub-cli…) is looked up in it first. It must exist (a missing folder could be created later by someone else),
/// and neither it nor any of its parents may be writable by another user: each one belongs to root or to the agent's
/// user and grants no write access to the group or others, except sticky folders like /tmp where others can't
/// replace entries they don't own.
/// </summary>
public static class TrustedDirectory
{
    private const UnixFileMode GroupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    /// <summary>Why the folder can't be trusted, or null when it can. Links are resolved first: the check applies to
    /// the folders the path really goes through.</summary>
    public static string? UntrustedReason(string path)
    {
        if (!Directory.Exists(path))
            return $"{path} does not exist";
        if (OperatingSystem.IsWindows())
            return null;

        string realPath;
        try { realPath = Path.TrimEndingDirectorySeparator(PathConfinement.RealPath(path)); }
        catch (IOException ex) { return ex.Message; }
        var isTarget = true;
        for (var current = realPath; current is not null; current = Path.GetDirectoryName(current))
        {
            if (FileOwnership.Lstat(current) is { Owner: { } owner }
                && owner != 0 && FileOwnership.CurrentUser is { } user && owner != user)
                return $"{current} belongs to another user (uid {owner})";

            var mode = File.GetUnixFileMode(current);
            // The sticky bit protects existing entries of a parent, not the content of the folder itself.
            var sticky = !isTarget && (mode & UnixFileMode.StickyBit) != 0;
            if ((mode & GroupOrOtherWrite) != 0 && !sticky)
                return $"{current} is writable by other users";
            isTarget = false;
        }
        return null;
    }
}
