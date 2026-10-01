using System.Runtime.InteropServices;

namespace SideHub.Agent;

/// <summary>
/// lstat(2) of a path: whether it is a symbolic link and which user owns it, without following the link. .NET exposes
/// neither the owner nor a no-follow stat, so this goes to libc: statx on Linux (its layout is the same on every
/// architecture), lstat on macOS. The owner is null when it can't be read (other libc, Windows).
/// </summary>
public static class FileOwnership
{
    public readonly record struct Status(bool IsSymbolicLink, uint? Owner);

    private const int ENOENT = 2;
    private const int ENOTDIR = 20;
    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFLNK = 0xA000;

    /// <summary>The effective user id of the agent, or null when it can't be read.</summary>
    public static uint? CurrentUser { get; } = ReadCurrentUser();

    /// <summary>The path's status (the link itself when it is one), or null when it doesn't exist.</summary>
    public static Status? Lstat(string path)
    {
        if (TryNativeLstat(path, out var status, out var missing))
            return missing ? null : status;

        // No usable libc: links can still be told apart, the owner can't.
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            return new Status(true, null);
        return info.Exists || Directory.Exists(path) ? new Status(false, null) : null;
    }

    private static bool TryNativeLstat(string path, out Status status, out bool missing)
    {
        status = default;
        missing = false;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        var buffer = new byte[256];
        try
        {
            int result;
            int uidOffset, modeOffset;
            if (OperatingSystem.IsLinux())
            {
                // struct statx: stx_uid at 20, stx_mode at 28.
                result = statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType | StatxMode | StatxUid, buffer);
                (uidOffset, modeOffset) = (20, 28);
            }
            else
            {
                // struct stat (64-bit inodes): st_mode at 4, st_uid at 16.
                result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? lstat_inode64(path, buffer)
                    : lstat(path, buffer);
                (uidOffset, modeOffset) = (16, 4);
            }

            if (result != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno is ENOENT or ENOTDIR)
                {
                    missing = true;
                    return true;
                }
                throw new IOException($"lstat {path}: {Marshal.GetPInvokeErrorMessage(errno)}");
            }

            var mode = BitConverter.ToUInt16(buffer, modeOffset);
            var owner = BitConverter.ToUInt32(buffer, uidOffset);
            status = new Status((mode & S_IFMT) == S_IFLNK, owner);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static uint? ReadCurrentUser()
    {
        if (OperatingSystem.IsWindows()) return null;
        try { return geteuid(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1, StatxMode = 0x2, StatxUid = 0x8;

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, string pathname, int flags, uint mask, byte[] statxbuf);

    [DllImport("libc", SetLastError = true)]
    private static extern int lstat(string pathname, byte[] buf);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int lstat_inode64(string pathname, byte[] buf);

    [DllImport("libc")]
    private static extern uint geteuid();
}
