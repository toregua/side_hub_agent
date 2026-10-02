namespace SideHub.Agent.Tests;

/// <summary>Anything in the terminal can write to the notification FIFO: only the events the wrappers and sidehub-cli
/// write are accepted, with a UUID session id, a known provider, an absolute cwd and a positive pid.</summary>
public class FifoNotificationTests
{
    private const string SessionId = "8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14";

    private static FifoNotification? Parse(string line) => FifoNotification.Parse(line, out _);

    private static string Rejection(string line)
    {
        Assert.Null(FifoNotification.Parse(line, out var rejection));
        return Assert.IsType<string>(rejection);
    }

    [Fact]
    public void What_the_wrappers_write_is_accepted()
    {
        Assert.Equal(new FifoNotification.CliSessionStarted("claude", SessionId),
            Parse($$"""{"event":"cli-session-started","provider":"claude","cliSessionId":"{{SessionId}}"}"""));
        Assert.Equal(new FifoNotification.CliLaunched("codex", "/root/Github/side_hub", 4242),
            Parse("""{"event":"cli-launched","provider":"codex","cwd":"/root/Github/side_hub","pid":4242}"""));
        Assert.IsType<FifoNotification.RunStepEnded>(Parse("""{"event":"run-step-ended"}"""));
    }

    [Fact]
    public void A_cli_exit_is_accepted_with_or_without_its_session_id()
    {
        Assert.Equal(new FifoNotification.CliExited("claude", SessionId),
            Parse($$"""{"event":"cli-exited","provider":"claude","cliSessionId":"{{SessionId}}"}"""));
        Assert.Equal(new FifoNotification.CliExited("codex", null),
            Parse("""{"event":"cli-exited","provider":"codex","cliSessionId":null}"""));
        Assert.Equal(new FifoNotification.CliExited("codex", null),
            Parse("""{"event":"cli-exited","provider":"codex"}"""));
        Assert.Equal("invalid cliSessionId",
            Rejection("""{"event":"cli-exited","provider":"claude","cliSessionId":"../x"}"""));
        Assert.Equal("invalid cliSessionId",
            Rejection("""{"event":"cli-exited","provider":"claude","cliSessionId":42}"""));
        Assert.Equal("unknown provider", Rejection("""{"event":"cli-exited","provider":"bash"}"""));
    }

    [Fact]
    public void A_launch_without_pid_is_accepted_without_one() =>
        Assert.Equal(new FifoNotification.CliLaunched("codex", "/work", null),
            Parse("""{"event":"cli-launched","provider":"codex","cwd":"/work"}"""));

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("../../.ssh/authorized_keys")]
    [InlineData("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14/../../x")]
    [InlineData("8c1e7a520d3b4f6e9a215b7c3d9e0f14")]
    [InlineData("{8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14}")]
    [InlineData("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14\n")]
    [InlineData("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f1z")]
    [InlineData("")]
    public void A_session_id_that_is_not_a_uuid_is_rejected(string cliSessionId)
    {
        var line = $$"""{"event":"cli-session-started","provider":"claude","cliSessionId":{{System.Text.Json.JsonSerializer.Serialize(cliSessionId)}}}""";
        Assert.Equal("invalid cliSessionId", Rejection(line));
        Assert.False(FifoNotification.IsValidCliSessionId(cliSessionId));
    }

    [Fact]
    public void A_session_id_that_is_not_a_string_is_rejected() =>
        Assert.Equal("invalid cliSessionId",
            Rejection("""{"event":"cli-session-started","provider":"claude","cliSessionId":42}"""));

    [Theory]
    [InlineData("""{"event":"cli-session-started","provider":"../claude","cliSessionId":"8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14"}""")]
    [InlineData("""{"event":"cli-session-started","provider":"Claude","cliSessionId":"8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14"}""")]
    [InlineData("""{"event":"cli-session-started","cliSessionId":"8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14"}""")]
    [InlineData("""{"event":"cli-launched","provider":"bash","cwd":"/work","pid":1}""")]
    public void An_unknown_provider_is_rejected(string line) => Assert.Equal("unknown provider", Rejection(line));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    [InlineData("\"4242\"")]
    [InlineData("null")]
    public void A_pid_that_is_not_positive_is_rejected(string pid) =>
        Assert.Equal("invalid pid",
            Rejection($$"""{"event":"cli-launched","provider":"codex","cwd":"/work","pid":{{pid}}}"""));

    [Theory]
    [InlineData("work")]
    [InlineData("./work")]
    [InlineData("/work/../etc")]
    [InlineData("/work/./x")]
    [InlineData("/work\nINJECTED log line")]
    [InlineData("")]
    public void A_cwd_that_is_not_an_absolute_normalized_path_is_rejected(string cwd)
    {
        var line = $$"""{"event":"cli-launched","provider":"codex","cwd":{{System.Text.Json.JsonSerializer.Serialize(cwd)}},"pid":1}""";
        Assert.Equal("invalid cwd", Rejection(line));
    }

    [Fact]
    public void A_cwd_longer_than_path_max_is_rejected()
    {
        Assert.True(FifoNotification.IsValidCwd("/" + new string('a', FifoNotification.MaxCwdLength - 1)));
        Assert.False(FifoNotification.IsValidCwd("/" + new string('a', FifoNotification.MaxCwdLength)));
    }

    [Theory]
    [InlineData("not json", "malformed JSON")]
    [InlineData("[1,2]", "not a JSON object")]
    [InlineData("""{"event":"pty.stop"}""", "unknown event")]
    [InlineData("""{"provider":"claude"}""", "unknown event")]
    public void Anything_else_is_rejected(string line, string expected) => Assert.Equal(expected, Rejection(line));

    [Fact]
    public void A_line_longer_than_the_limit_is_rejected() =>
        Assert.Equal("line too long", Rejection(new string(' ', FifoNotification.MaxLineLength + 1)));
}
