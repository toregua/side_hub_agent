using SideHub.Agent.Update;

namespace SideHub.Agent.Tests.Update;

public class SelfUpdateTests : IDisposable
{
    private readonly TempFolder _temp = new();

    private string Install(bool marker = true)
    {
        var install = _temp.Combine("sidehub-agent");
        Directory.CreateDirectory(install);
        if (marker)
            File.WriteAllText(Path.Combine(install, SelfUpdate.InstallMarker), "");
        return install;
    }

    [Fact]
    public void An_installed_agent_whose_folder_is_writable_can_update_itself()
    {
        var install = Install();

        var support = SelfUpdate.Detect(install + "/", isWindows: false, canWrite: _ => true);

        Assert.True(support.Supported);
        Assert.Equal(install, support.InstallDirectory);
    }

    [Fact]
    public void A_build_run_outside_an_install_cannot()
    {
        var support = SelfUpdate.Detect(Install(marker: false), isWindows: false, canWrite: _ => true);

        Assert.Equal(new SelfUpdateSupport(false, UpdateErrors.NotInstalled, null), support);
    }

    [Fact]
    public void A_system_install_the_agent_cannot_write_falls_back_to_the_command()
    {
        var install = Install();

        // sudo install in /usr/local/lib: the folder's parent is not writable either
        var support = SelfUpdate.Detect(install, isWindows: false, canWrite: path => path == install);

        Assert.False(support.Supported);
        Assert.Equal(UpdateErrors.NotWritable, support.Reason);
    }

    [Fact]
    public void Windows_is_not_supported_yet()
    {
        var support = SelfUpdate.Detect(Install(), isWindows: true, canWrite: _ => true);

        Assert.Equal(UpdateErrors.UnsupportedPlatform, support.Reason);
    }

    [Fact]
    public void The_install_id_is_stable_per_installation_and_differs_between_them()
    {
        var sidehub = _temp.Combine(".sidehub");

        var first = SelfUpdate.InstallId("/usr/local/lib/sidehub-agent", sidehub);
        var again = SelfUpdate.InstallId("/usr/local/lib/sidehub-agent", sidehub);
        var otherFolder = SelfUpdate.InstallId("/home/me/.local/lib/sidehub-agent", sidehub);
        var otherMachine = SelfUpdate.InstallId("/usr/local/lib/sidehub-agent", _temp.Combine("other-home", ".sidehub"));

        Assert.NotNull(first);
        Assert.Equal(32, first.Length);
        Assert.Equal(first, again);
        Assert.NotEqual(first, otherFolder);
        Assert.NotEqual(first, otherMachine);
    }

    public void Dispose() => _temp.Dispose();
}

public class InstallSwapTests : IDisposable
{
    private readonly TempFolder _temp = new();

    private string Folder(string name, string version)
    {
        var path = _temp.Combine(name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "version"), version);
        return path;
    }

    private static string VersionIn(string folder) => File.ReadAllText(Path.Combine(folder, "version"));

    [Fact]
    public void The_staged_release_replaces_the_install_and_the_previous_one_is_kept()
    {
        var install = Folder("sidehub-agent", "1.0.90");
        var staging = Folder("sidehub-agent.staging-1.0.91", "1.0.91");

        InstallSwap.Swap(install, staging);

        Assert.Equal("1.0.91", VersionIn(install));
        Assert.Equal("1.0.90", VersionIn(InstallSwap.PreviousDirectory(install)));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void A_rollback_puts_the_previous_release_back_and_keeps_the_failed_one()
    {
        var install = Folder("sidehub-agent", "1.0.90");
        InstallSwap.Swap(install, Folder("sidehub-agent.staging-1.0.91", "1.0.91"));

        InstallSwap.Rollback(install, "1.0.91");

        Assert.Equal("1.0.90", VersionIn(install));
        Assert.Equal("1.0.91", VersionIn($"{install}.failed-1.0.91"));
        Assert.False(Directory.Exists(InstallSwap.PreviousDirectory(install)));
    }

    [Fact]
    public void A_missing_staging_folder_leaves_the_install_untouched()
    {
        var install = Folder("sidehub-agent", "1.0.90");

        Assert.ThrowsAny<IOException>(() => InstallSwap.Swap(install, _temp.Combine("missing")));

        Assert.Equal("1.0.90", VersionIn(install));
    }

    [Fact]
    public void Clean_up_drops_failed_releases_only()
    {
        var install = Folder("sidehub-agent", "1.0.92");
        Folder("sidehub-agent.failed-1.0.91", "1.0.91");
        Folder("sidehub-agent.previous", "1.0.90");

        InstallSwap.CleanUp(install);

        Assert.False(Directory.Exists($"{install}.failed-1.0.91"));
        Assert.True(Directory.Exists($"{install}.previous"));
    }

    public void Dispose() => _temp.Dispose();
}
