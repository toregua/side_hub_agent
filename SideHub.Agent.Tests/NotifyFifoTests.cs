using SideHub.Agent;

namespace SideHub.Agent.Tests;

/// <summary>The notification FIFO path is built from a backend-supplied PTY session id: only plain ids are accepted,
/// and the FIFO lives in an agent-owned 0700 folder instead of /tmp.</summary>
public class NotifyFifoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-fifo-").FullName;
    private string FifoDir => Path.Combine(_dir, "run", "fifo", "a1");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("run-3f2b9c0d4e5f60718293a4b5c6d7e8f9")]
    [InlineData("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14")]
    [InlineData("workflow_1")]
    [InlineData("a")]
    public void Plain_ids_are_valid(string id) => Assert.True(NotifyFifo.IsValidPtySessionId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("-m 0666 /tmp/x")]
    [InlineData("id;rm -rf ~")]
    [InlineData("id\n")]
    [InlineData("é")]
    public void Ids_with_other_characters_are_invalid(string? id) => Assert.False(NotifyFifo.IsValidPtySessionId(id));

    [Fact]
    public void Ids_longer_than_128_characters_are_invalid()
    {
        Assert.True(NotifyFifo.IsValidPtySessionId(new string('a', 128)));
        Assert.False(NotifyFifo.IsValidPtySessionId(new string('a', 129)));
    }

    [Fact]
    public void Path_refuses_an_invalid_id() =>
        Assert.Throws<ArgumentException>(() => NotifyFifo.PathFor(FifoDir, "../x"));

    [Fact]
    public void Path_stays_in_the_agent_folder()
    {
        var path = NotifyFifo.PathFor(FifoDir, "run-abc");

        Assert.Equal(Path.Combine(FifoDir, "pty-run-abc.fifo"), path);
        Assert.DoesNotContain("/tmp/sidehub-pty-", path);
    }

    [Fact]
    public void Creates_a_0600_fifo_in_a_0700_folder()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.True(NotifyFifo.TryCreate(FifoDir, "run-abc", out var path, out var error), error);

        var info = new FileInfo(path);
        Assert.True(info.Exists);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(FifoDir));
        Assert.True(IsFifo(path));
    }

    [Fact]
    public void Tightens_an_existing_folder_and_replaces_a_leftover_entry()
    {
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(FifoDir);
        File.SetUnixFileMode(FifoDir, (UnixFileMode)0b111_111_111);
        var path = NotifyFifo.PathFor(FifoDir, "run-abc");
        File.WriteAllText(path, "forged");

        Assert.True(NotifyFifo.TryCreate(FifoDir, "run-abc", out _, out var error), error);

        Assert.False(PrivateFiles.IsExposed(FifoDir));
        Assert.True(IsFifo(path));
    }

    [Fact]
    public async Task Lines_written_to_the_fifo_are_readable()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.True(NotifyFifo.TryCreate(FifoDir, "run-abc", out var path, out var error), error);

        await using var reader = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        await using (var writer = new StreamWriter(new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)))
            await writer.WriteLineAsync("{\"event\":\"cli-launched\"}");

        using var lines = new StreamReader(reader);
        Assert.Equal("{\"event\":\"cli-launched\"}", await lines.ReadLineAsync());
    }

    [Fact]
    public void Delete_removes_the_fifo_and_ignores_invalid_ids()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.True(NotifyFifo.TryCreate(FifoDir, "run-abc", out var path, out var error), error);

        NotifyFifo.Delete(FifoDir, "run-abc");
        NotifyFifo.Delete(FifoDir, "../../x");

        Assert.False(Path.Exists(path));
    }

    private static bool IsFifo(string path)
    {
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("test") { ArgumentList = { "-p", path } })!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
