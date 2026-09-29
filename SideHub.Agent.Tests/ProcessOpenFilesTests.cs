using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

public class ProcessOpenFilesTests : IDisposable
{
    private readonly string _proc = Path.Combine(Path.GetTempPath(), "sidehub-proc-tests-" + Guid.NewGuid().ToString("N"));

    public ProcessOpenFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_proc, "self"));
        AddProcess(100, "node", parent: 1, "/s/rollout-a.jsonl", "/dev/pts/3");
        AddProcess(101, "co) dex", parent: 100, "/s/rollout-b.jsonl");   // the real binary, a child
        AddProcess(102, "sh", parent: 101, "/s/rollout-a.jsonl");        // a grandchild sharing a file
        AddProcess(200, "codex", parent: 1, "/s/rollout-c.jsonl");       // unrelated
    }

    public void Dispose()
    {
        try { Directory.Delete(_proc, recursive: true); } catch { }
    }

    private void AddProcess(int pid, string comm, int parent, params string[] openFiles)
    {
        var dir = Path.Combine(_proc, pid.ToString());
        Directory.CreateDirectory(Path.Combine(dir, "fd"));
        File.WriteAllText(Path.Combine(dir, "stat"), $"{pid} ({comm}) S {parent} {pid} {pid} 0 -1");
        for (var fd = 0; fd < openFiles.Length; fd++)
            File.CreateSymbolicLink(Path.Combine(dir, "fd", fd.ToString()), openFiles[fd]);
    }

    [Fact]
    public void Find_ReadsTheProcessAndItsDescendants()
    {
        var files = ProcessOpenFiles.Find(100, CodexRolloutHarvester.IsRolloutPath, _proc);

        Assert.NotNull(files);
        Assert.Equal(["/s/rollout-a.jsonl", "/s/rollout-b.jsonl"], files.Order());
    }

    [Fact]
    public void Find_GoneProcess_ReturnsNull()
    {
        Assert.Null(ProcessOpenFiles.Find(999, CodexRolloutHarvester.IsRolloutPath, _proc));
    }

    [Fact]
    public async Task Observation_IsComplete_OnlyWhenTheProcessWasSeenRunningThenExited()
    {
        var calls = 0;
        var watched = new LaunchObservation(TimeSpan.Zero, TimeSpan.Zero);
        await watched.WatchAsync(() => ++calls switch
        {
            1 => [],
            2 => ["/s/rollout-a.jsonl"],
            3 => ["/s/rollout-a.jsonl", "/s/rollout-b.jsonl"],
            _ => null,
        }, CancellationToken.None);

        Assert.True(watched.IsComplete);
        Assert.Equal(["/s/rollout-a.jsonl", "/s/rollout-b.jsonl"], watched.Files);

        var goneAtOnce = new LaunchObservation(TimeSpan.Zero, TimeSpan.Zero);
        await goneAtOnce.WatchAsync(() => null, CancellationToken.None);
        Assert.False(goneAtOnce.IsComplete);
    }
}
