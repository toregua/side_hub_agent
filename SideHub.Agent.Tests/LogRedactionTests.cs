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

public class MaskUrlTests
{
    [Theory]
    [InlineData("wss://api.sidehub.io/ws/agent", "wss://api.sidehub.io/ws/agent")]
    [InlineData("wss://user:pass@api.sidehub.io/ws/agent", "wss://api.sidehub.io/ws/agent")]
    [InlineData("wss://api.sidehub.io/ws/agent?token=sh_agent_abcdef", "wss://api.sidehub.io/ws/agent?token=sh_a***")]
    public void Credentials_never_reach_the_log(string url, string expected) =>
        Assert.Equal(expected, WebSocketClient.MaskUrl(url));
}
