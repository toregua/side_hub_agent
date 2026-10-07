using SideHub.Agent;

namespace SideHub.Agent.Tests;

public class VersionInfoTests
{
    [Theory]
    [InlineData("2.1.3 (Claude Code)\n", "2.1.3")]
    [InlineData("codex-cli 0.46.0\n", "0.46.0")]
    [InlineData("GitHub Copilot CLI 1.0.90.\nRun 'copilot update' to check for updates.\n", "1.0.90")]
    [InlineData("0.9.0-nightly.20250101\n", "0.9.0-nightly.20250101")]
    [InlineData("command not found", null)]
    public void ParseVersion_ShouldExtractTheVersionNumber(string output, string? expected)
    {
        Assert.Equal(expected, VersionInfo.ParseVersion(output));
    }

    [Theory]
    [InlineData("claude", 0, "{\n  \"loggedIn\": true,\n  \"authMethod\": \"claude.ai\"\n}", true)]
    [InlineData("claude", 1, "{\n  \"loggedIn\": false,\n  \"authMethod\": \"none\"\n}", false)]
    [InlineData("claude", 1, "Error: Raw mode is not supported on the current process.stdin", null)]
    [InlineData("codex", 0, "Logged in using ChatGPT\n", true)]
    [InlineData("codex", 1, "Not logged in\n", false)]
    [InlineData("codex", 2, "error: unrecognized subcommand 'status'", null)]
    [InlineData("gemini", 0, "anything", null)]
    public void ParseLoggedIn_ShouldOnlyTrustAClearAnswer(string cli, int exitCode, string stdout, bool? expected)
    {
        Assert.Equal(expected, VersionInfo.ParseLoggedIn(cli, exitCode, stdout));
    }
}
