using System.Runtime.Versioning;

namespace SideHub.Agent;

/// <summary>
/// Files only the agent's user may read: the configs (they hold the agent token), the logs, the PID file and the
/// pending usage reports. Files are 0600 and folders 0700, set at creation (no window where the umask applies) and
/// re-applied to files left by older versions. No-op on Windows, where ACLs are inherited from the profile.
/// </summary>
public static class PrivateFiles
{
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode GroupOrOthers =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [UnsupportedOSPlatformGuard("windows")]
    private static bool Supported => !OperatingSystem.IsWindows();

    /// <summary>Creates the folder (and its missing parents) as 0700, or tightens it to 0700 when it exists.</summary>
    public static void CreateDirectory(string path)
    {
        if (!Supported)
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, PrivateDirectoryMode);
        Restrict(path, PrivateDirectoryMode);
    }

    /// <summary>Writes the file, created as 0600; an existing file is tightened to 0600 first.</summary>
    public static void WriteAllText(string path, string contents)
    {
        RestrictFile(path);
        using var writer = new StreamWriter(path, Options(FileMode.Create));
        writer.Write(contents);
    }

    /// <summary>Opens the file for appending, created as 0600; an existing file is tightened to 0600 first.</summary>
    public static StreamWriter AppendText(string path)
    {
        RestrictFile(path);
        return new StreamWriter(path, Options(FileMode.Append));
    }

    /// <summary>Sets an existing file to 0600. Missing files are ignored.</summary>
    public static void RestrictFile(string path) => Restrict(path, PrivateFileMode);

    /// <summary>Whether the group or other users have any access to the file or folder.</summary>
    public static bool IsExposed(string path) =>
        Supported && Path.Exists(path) && (File.GetUnixFileMode(path) & GroupOrOthers) != 0;

    private static void Restrict(string path, UnixFileMode mode)
    {
        if (!Supported || !Path.Exists(path)) return;
        if (File.GetUnixFileMode(path) != mode)
            File.SetUnixFileMode(path, mode);
    }

    private static FileStreamOptions Options(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.Read };
        if (Supported) options.UnixCreateMode = PrivateFileMode;
        return options;
    }
}
