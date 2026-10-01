using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SideHub.Agent;

/// <summary>
/// Opens a file below a trusted root without following any symbolic link on the way. On Unix each
/// folder is opened relative to its parent's descriptor with <c>O_NOFOLLOW</c> (<c>openat</c>), so
/// a link committed in the repository, or planted after a path check, makes the open fail instead
/// of redirecting the write: there is no gap between checking a path and using it. Missing folders
/// are created when the mode creates the file. Windows has no <c>openat</c>: every component is
/// checked for reparse points right before the open (best effort, the window remains).
/// </summary>
public static class ConfinedFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Opens <paramref name="relativePath"/> below the real path of <paramref name="root"/>.
    /// Throws <see cref="IOException"/> when a component is a symbolic link (or not a folder), and
    /// <see cref="FileNotFoundException"/> when the file does not exist and the mode does not create it.
    /// Supported modes: Open, OpenOrCreate, Create, CreateNew, Append.</summary>
    public static FileStream Open(string root, string relativePath, FileMode mode, FileAccess access)
    {
        if (mode is FileMode.Truncate)
            throw new ArgumentException($"Unsupported file mode {mode}", nameof(mode));
        var segments = SplitRelative(relativePath);
        var realRoot = PathConfinement.RealPath(root);
        var stream = OperatingSystem.IsWindows()
            ? OpenChecked(realRoot, segments, mode, access)
            : OpenNoFollow(realRoot, segments, mode, access);
        if (mode is FileMode.Append)
            stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    /// <summary>The text of <paramref name="relativePath"/>, or null when it does not exist. Links are
    /// followed only while they stay below <paramref name="root"/>: a committed link to a file
    /// outside (e.g. <c>~/.ssh/id_rsa</c>) must not be read and copied into a generated file.</summary>
    public static string? ReadAllTextOrNull(string root, string relativePath)
    {
        var segments = SplitRelative(relativePath);
        var realRoot = PathConfinement.RealPath(root);
        var realPath = PathConfinement.RealPath(Path.Combine([realRoot, .. segments]));
        if (!PathConfinement.IsWithin(realRoot, realPath))
            throw new IOException($"'{relativePath}' resolves to '{realPath}', outside '{realRoot}'");
        return File.Exists(realPath) ? File.ReadAllText(realPath) : null;
    }

    /// <summary>Writes <paramref name="content"/> (UTF-8, no BOM) to <paramref name="relativePath"/>,
    /// replacing it, without following any link.</summary>
    public static void WriteAllText(string root, string relativePath, string content)
    {
        using var stream = Open(root, relativePath, FileMode.Create, FileAccess.Write);
        var bytes = Utf8NoBom.GetBytes(content);
        stream.Write(bytes);
    }

    private static string[] SplitRelative(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\0'))
            throw new ArgumentException($"'{relativePath}' is not a relative path", nameof(relativePath));
        var segments = relativePath.Split(['/', Path.DirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            throw new ArgumentException($"'{relativePath}' must name a file below the root", nameof(relativePath));
        return segments;
    }

    private static bool Creates(FileMode mode) =>
        mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate or FileMode.Append;

    private static FileStream OpenChecked(string realRoot, string[] segments, FileMode mode, FileAccess access)
    {
        var current = realRoot;
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"'{current}' is a link, refusing to follow it");
            }
            else if (i < segments.Length - 1 && Creates(mode))
            {
                Directory.CreateDirectory(current);
            }
        }
        return new FileStream(current, mode, access, FileShare.None);
    }

    private static FileStream OpenNoFollow(string realRoot, string[] segments, FileMode mode, FileAccess access)
    {
        var flags = UnixFlags.Current;
        var dir = open(realRoot, flags.Directory | flags.CloExec);
        if (dir < 0)
            throw ErrorFor(realRoot, Marshal.GetLastPInvokeError(), flags);
        try
        {
            foreach (var segment in segments[..^1])
            {
                if (Creates(mode) && mkdirat(dir, segment, NewDirectoryMode) != 0 && Marshal.GetLastPInvokeError() != EEXIST)
                    throw ErrorFor(segment, Marshal.GetLastPInvokeError(), flags);
                var next = openat(dir, segment, flags.Directory | flags.NoFollow | flags.CloExec, 0);
                if (next < 0)
                    throw ErrorFor(segment, Marshal.GetLastPInvokeError(), flags);
                close(dir);
                dir = next;
            }

            var name = segments[^1];
            var accessFlags = access switch
            {
                FileAccess.Read => 0,
                FileAccess.Write => O_WRONLY,
                _ => O_RDWR,
            };
            var modeFlags = mode switch
            {
                FileMode.Create => flags.Trunc,
                FileMode.Append => flags.Append,
                _ => 0,
            };
            // O_NONBLOCK: opening a FIFO for writing fails at once instead of hanging the agent.
            var baseFlags = accessFlags | modeFlags | flags.NoFollow | flags.NoCtty | flags.NonBlock | flags.CloExec;

            var fd = mode is FileMode.CreateNew ? -1 : openat(dir, name, baseFlags, 0);
            var errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            if ((fd < 0 && errno == ENOENT || mode is FileMode.CreateNew) && Creates(mode))
            {
                fd = openat(dir, name, baseFlags | flags.Creat | flags.Excl, NewFileMode);
                errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
                // openat is variadic: on Apple arm64 the mode is read from the stack, not from the
                // register P/Invoke puts it in, so the file gets its intended mode explicitly.
                if (fd >= 0 && OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                    fchmod(fd, NewFileMode);
            }
            if (fd < 0)
            {
                if (errno == ENOENT)
                    throw new FileNotFoundException($"'{name}' does not exist", name);
                if (errno == EEXIST)
                    throw new IOException($"'{name}' already exists");
                throw ErrorFor(name, errno, flags);
            }
            return new FileStream(new SafeFileHandle((IntPtr)fd, ownsHandle: true), access, bufferSize: 0);
        }
        finally
        {
            close(dir);
        }
    }

    private static IOException ErrorFor(string name, int errno, UnixFlags flags) =>
        errno == flags.Eloop || errno == ENOTDIR
            ? new IOException($"'{name}' is a symbolic link or not a folder, refusing to follow it")
            : new IOException($"Cannot open '{name}': {Marshal.GetPInvokeErrorMessage(errno)}");

    private const int O_WRONLY = 0x1, O_RDWR = 0x2;
    private const int ENOENT = 2, ENOTDIR = 20, EEXIST = 17;
    private const uint NewFileMode = 0x1A4;      // 0644
    private const uint NewDirectoryMode = 0x1ED; // 0755

    /// <summary>open(2) flag values differ between Linux architectures and macOS.</summary>
    private sealed record UnixFlags(int Creat, int Excl, int NoCtty, int Trunc, int Append, int NonBlock,
        int Directory, int NoFollow, int CloExec, int Eloop)
    {
        public static readonly UnixFlags Current =
            OperatingSystem.IsMacOS()
                ? new(Creat: 0x200, Excl: 0x800, NoCtty: 0x20000, Trunc: 0x400, Append: 0x8, NonBlock: 0x4,
                    Directory: 0x100000, NoFollow: 0x100, CloExec: 0x1000000, Eloop: 62)
                : RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm
                    ? new(Creat: 0x40, Excl: 0x80, NoCtty: 0x100, Trunc: 0x200, Append: 0x400, NonBlock: 0x800,
                        Directory: 0x4000, NoFollow: 0x8000, CloExec: 0x80000, Eloop: 40)
                    : new(Creat: 0x40, Excl: 0x80, NoCtty: 0x100, Trunc: 0x200, Append: 0x400, NonBlock: 0x800,
                        Directory: 0x10000, NoFollow: 0x20000, CloExec: 0x80000, Eloop: 40);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int dirfd, string pathname, int flags, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdirat(int dirfd, string pathname, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(int fd, uint mode);

    [DllImport("libc")]
    private static extern int close(int fd);
}
