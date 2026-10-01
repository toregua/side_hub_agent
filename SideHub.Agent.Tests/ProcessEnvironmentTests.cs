using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

/// <summary>A pid announced through the FIFO is only watched if the process carries the PTY's session id.</summary>
public class ProcessEnvironmentTests : IDisposable
{
    private readonly string _proc = Directory.CreateTempSubdirectory("sidehub-environ-").FullName;

    public ProcessEnvironmentTests()
    {
        AddProcess(100, "PATH=/usr/bin", "SIDEHUB_PTY_SESSION_ID=run-abc", "HOME=/root");
        AddProcess(200, "SIDEHUB_PTY_SESSION_ID=run-abcd");
    }

    public void Dispose() => Directory.Delete(_proc, recursive: true);

    private void AddProcess(int pid, params string[] environment)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_proc, pid.ToString())).FullName;
        File.WriteAllText(Path.Combine(dir, "environ"), string.Join('\0', environment) + "\0");
    }

    [Fact]
    public void The_exact_variable_is_found() =>
        Assert.True(ProcessEnvironment.Has(100, "SIDEHUB_PTY_SESSION_ID", "run-abc", _proc));

    [Fact]
    public void Another_ptys_process_is_not_one_of_this_pty() =>
        Assert.False(ProcessEnvironment.Has(200, "SIDEHUB_PTY_SESSION_ID", "run-abc", _proc));

    [Theory]
    [InlineData(300)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_missing_process_is_not_one_of_this_pty(int pid) =>
        Assert.False(ProcessEnvironment.Has(pid, "SIDEHUB_PTY_SESSION_ID", "run-abc", _proc));
}
