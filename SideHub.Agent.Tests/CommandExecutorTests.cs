namespace SideHub.Agent.Tests;

public class CommandExecutorTests
{
    [Fact]
    public async Task An_oversized_output_line_is_replaced_by_a_notice_and_output_goes_on()
    {
        if (OperatingSystem.IsWindows()) return;
        var executor = new CommandExecutor(Path.GetTempPath());
        var output = new List<(string Stream, string Line)>();

        var exitCode = await executor.ExecuteAsync(
            $"head -c {CommandExecutor.MaxOutputLineLength * 3} /dev/zero | tr '\\0' x; echo; echo done",
            "sh",
            (stream, line) => { output.Add((stream, line)); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var stdout = output.Where(o => o.Stream == "stdout").Select(o => o.Line).ToList();
        Assert.Equal([$"[line longer than {CommandExecutor.MaxOutputLineLength} characters omitted]", "done"], stdout);
    }
}
