using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

public class WorkflowCommandPairsTests
{
    [Fact]
    public void CollectsRepeatedPairs_KeepingEqualsSignsInValues()
    {
        string[] args = ["--output-id", "abc", "--value", "score=72", "--value", "url=https://x.io/?a=b", "--value", "score=80"];

        Assert.True(WorkflowCommands.TryCollectPairs(args, "--value", out var pairs, out var error));

        Assert.Null(error);
        Assert.Equal(new Dictionary<string, string> { ["score"] = "80", ["url"] = "https://x.io/?a=b" }, pairs);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("=72")]
    public void RejectsPairsWithoutKey(string pair)
    {
        Assert.False(WorkflowCommands.TryCollectPairs(["--input", pair], "--input", out _, out var error));
        Assert.Contains("key=value", error);
    }

    [Fact]
    public void RejectsFlagWithoutValue()
    {
        Assert.False(WorkflowCommands.TryCollectPairs(["wf", "--input"], "--input", out _, out _));
    }
}
