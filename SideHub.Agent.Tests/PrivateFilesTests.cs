using System.Runtime.Versioning;
using SideHub.Agent;
using SideHub.Agent.Models;
using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

/// <summary>The token, logs, PID and pending usage reports must not be readable by other users of the machine.</summary>
[UnsupportedOSPlatform("windows")]
public class PrivateFilesTests : IDisposable
{
    private const UnixFileMode Rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Rwx = Rw | UnixFileMode.UserExecute;
    private const UnixFileMode WorldReadable = Rw | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode WorldListable = Rwx | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                               UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-perms-").FullName;
    private string SidehubDir => Path.Combine(_dir, ".sidehub");
    private string RunDir => Path.Combine(SidehubDir, "run");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static UnixFileMode Mode(string path) => File.GetUnixFileMode(path);

    private static AgentSetup.SetupInfo Info() =>
        new("VPS", "wss://api.sidehub.io/ws/agent", "a1", "w1", null, ["shell"]);

    [Fact]
    public void Setup_writes_the_config_0600_in_a_0700_folder()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = AgentSetup.WriteConfig(_dir, Info(), "sh_agent_x");

        Assert.Equal(Rw, Mode(path));
        Assert.Equal(Rwx, Mode(SidehubDir));
    }

    [Fact]
    public void Setup_tightens_a_config_and_folder_left_world_readable()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = AgentSetup.WriteConfig(_dir, Info(), "old");
        File.SetUnixFileMode(path, WorldReadable);
        File.SetUnixFileMode(SidehubDir, WorldListable);

        AgentSetup.WriteConfig(_dir, Info(), "new");

        Assert.Equal(Rw, Mode(path));
        Assert.Equal(Rwx, Mode(SidehubDir));
    }

    [Fact]
    public async Task Startup_restricts_an_exposed_config_and_warns_once()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = AgentSetup.WriteConfig(_dir, Info(), "sh_agent_x");
        File.SetUnixFileMode(path, WorldReadable);
        File.SetUnixFileMode(SidehubDir, WorldListable);

        var warnings = AgentConfig.RestrictPermissions(_dir, await AgentConfig.LoadAllAsync(_dir));

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains(path) && w.Contains("0600"));
        Assert.Equal(Rw, Mode(path));
        Assert.Equal(Rwx, Mode(SidehubDir));
        Assert.Empty(AgentConfig.RestrictPermissions(_dir, await AgentConfig.LoadAllAsync(_dir)));
    }

    [Fact]
    public void Daemon_run_folder_and_pid_file_are_private()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(RunDir);
        File.SetUnixFileMode(SidehubDir, WorldListable);
        File.SetUnixFileMode(RunDir, WorldListable);
        var manager = new DaemonManager(_dir);

        manager.WritePidFile(42);

        Assert.Equal(Rwx, Mode(SidehubDir));
        Assert.Equal(Rwx, Mode(RunDir));
        Assert.Equal(Rw, Mode(manager.PidFile));
    }

    [Fact]
    public void Log_and_its_archives_stay_0600_across_rotation()
    {
        if (OperatingSystem.IsWindows()) return;
        var log = Path.Combine(RunDir, "sidehub-agent.log");

        using (var writer = new RotatingLogWriter(log, maxFileSizeBytes: 100, maxArchiveCount: 2))
        {
            Assert.Equal(Rwx, Mode(RunDir));
            Assert.Equal(Rw, Mode(log));
            for (var i = 0; i < 500; i++)
                writer.WriteLine("a line long enough to fill the log quickly");
        }

        var files = RotatingLogWriter.GetAllLogFiles(log, 2);
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.Equal(Rw, Mode(f)));
    }

    [Fact]
    public void Opening_the_log_tightens_files_left_by_older_versions()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(RunDir);
        var log = Path.Combine(RunDir, "sidehub-agent.log");
        File.WriteAllText(log, "old");
        File.WriteAllText(log + ".1", "older");
        File.SetUnixFileMode(log, WorldReadable);
        File.SetUnixFileMode(log + ".1", WorldReadable);

        using (new RotatingLogWriter(log)) { }

        Assert.Equal(Rw, Mode(log));
        Assert.Equal(Rw, Mode(log + ".1"));
    }

    [Fact]
    public void Pending_usage_reports_are_private()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new PendingUsageStore(Path.Combine(RunDir, "pending-usage", "a1"));
        var runId = Guid.NewGuid();

        store.Save(new RunUsageMessage { RunId = runId, Source = "test", CollectedAt = DateTimeOffset.UtcNow, Models = [] });

        Assert.Equal(Rwx, Mode(store.Directory));
        Assert.Equal(Rw, Mode(Path.Combine(store.Directory, $"{runId}.json")));
    }
}
