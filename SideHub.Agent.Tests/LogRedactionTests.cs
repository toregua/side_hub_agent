using SideHub.Agent;

namespace SideHub.Agent.Tests;

public class LogRedactionTests
{
    [Theory]
    [InlineData("claude -p --model opus \"$SIDEHUB_PTY_PROMPT\"; exit\r", "claude")]
    [InlineData("  codex exec \"secret prompt\"", "codex")]
    [InlineData("sidehub-cli_2.x run", "sidehub-cli_2.x")]
    [InlineData("API_KEY=abc claude", "?")]
    [InlineData("/home/me/bin/tool --flag", "?")]
    [InlineData("", "?")]
    public void Only_a_plain_program_name_is_logged(string input, string expected) =>
        Assert.Equal(expected, WebSocketClient.ProgramNameForLog(input));
}
