using System.Runtime.Versioning;

namespace SideHub.Agent.Tests;

/// <summary>Folders put in front of the terminal PATH must not be replaceable by another user.</summary>
[UnsupportedOSPlatform("windows")]
public class TrustedDirectoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-trusted-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Sub(string name, UnixFileMode mode)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, mode);
        return path;
    }

    private const UnixFileMode Rwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode ReadExec = UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [Fact]
    public void A_folder_only_its_owner_can_write_is_trusted()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Null(TrustedDirectory.UntrustedReason(Sub("lib", Rwx | ReadExec)));
    }

    [Fact]
    public void A_missing_folder_is_refused() =>
        Assert.NotNull(TrustedDirectory.UntrustedReason(Path.Combine(_dir, "missing")));

    [Fact]
    public void A_world_writable_folder_is_refused_even_when_sticky()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.NotNull(TrustedDirectory.UntrustedReason(Sub("open", Rwx | ReadExec | UnixFileMode.OtherWrite)));
        Assert.NotNull(TrustedDirectory.UntrustedReason(Sub("tmp", Rwx | ReadExec | UnixFileMode.OtherWrite | UnixFileMode.StickyBit)));
    }

    [Fact]
    public void A_folder_inside_a_group_writable_parent_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        var parent = Sub("parent", Rwx | ReadExec | UnixFileMode.GroupWrite);
        var child = Path.Combine(parent, "lib");
        Directory.CreateDirectory(child);

        Assert.NotNull(TrustedDirectory.UntrustedReason(child));
    }

    [Fact]
    public void A_folder_inside_a_sticky_world_writable_parent_is_trusted()
    {
        if (OperatingSystem.IsWindows()) return;
        // The temp folder itself (/tmp) is 1777: others can't replace what they don't own.
        var child = Sub("lib", Rwx | ReadExec);
        Assert.Null(TrustedDirectory.UntrustedReason(child));
    }
}
