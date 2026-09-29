using SideHub.Agent;

namespace SideHub.Agent.Tests;

public class VersionInfoTests
{
    [Theory]
    [InlineData("2.1.3 (Claude Code)\n", "2.1.3")]
    [InlineData("codex-cli 0.46.0\n", "0.46.0")]
    [InlineData("0.9.0-nightly.20250101\n", "0.9.0-nightly.20250101")]
    [InlineData("command not found", null)]
    public void ParseVersion_ShouldExtractTheVersionNumber(string output, string? expected)
    {
        Assert.Equal(expected, VersionInfo.ParseVersion(output));
    }
}
