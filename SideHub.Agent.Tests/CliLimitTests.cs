using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SideHub.Agent.Usage;
using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>
/// A CLI that stops on its plan's usage limit is recognized (doc 60): Claude's StopFailure hook, codex's rate limits in
/// its rollout, and for a CLI without a structured signal its own text at the end of a failed run.
/// </summary>
public class CliLimitTests : IDisposable
{
    private const string Session = "8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14";
    private const string Secret = "4f1c0d2e9b8a7c6d5e4f3a2b1c0d9e8f";
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-limits-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>The lines of a fixture of <c>Fixtures/cli-limits</c>, comments left out.</summary>
    private static string[] Fixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-limits", name))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

    // ---- Claude: StopFailure hook ----

    [Theory]
    [InlineData("rate_limit")]
    [InlineData("billing_error")]
    public void A_stop_failure_on_a_limit_reports_what_claude_said(string error)
    {
        var input = Utf8($$"""{"session_id":"{{Session}}","hook_event_name":"StopFailure","error":"{{error}}","error_details":"429 Too Many Requests","last_assistant_message":"You've hit your session limit · resets 3pm (Europe/Paris)"}""");

        Assert.Equal(new CliStateCommand.LimitReportData("claude", Session, error, "You've hit your session limit · resets 3pm (Europe/Paris)"),
            CliStateCommand.LimitReport(["claude", "limit"], () => input));
    }

    [Fact]
    public void Without_a_last_message_the_error_details_say_why_and_a_long_one_is_cut()
    {
        var details = Utf8($$"""{"session_id":"{{Session}}","error":"rate_limit","error_details":"429 Too Many Requests"}""");
        var longOne = Utf8($$"""{"error":"rate_limit","last_assistant_message":"{{new string('x', 5000)}}"}""");

        Assert.Equal("429 Too Many Requests", CliStateCommand.LimitReport(["claude", "limit"], () => details)!.Message);
        Assert.Equal(CliStateCommand.MaxLimitMessageLength, CliStateCommand.LimitReport(["claude", "limit"], () => longOne)!.Message!.Length);
    }

    [Theory]
    [InlineData("""{"error":"server_error","last_assistant_message":"API Error"}""")]
    [InlineData("""{"error":"authentication_failed"}""")]
    [InlineData("""{"last_assistant_message":"You've hit your session limit"}""")]
    [InlineData("""{"error":"rate_limit","tool_response":"cut short""")]
    [InlineData("not json")]
    public void Another_failure_or_an_unreadable_input_is_no_limit(string input) =>
        Assert.Null(CliStateCommand.LimitReport(["claude", "limit"], () => Utf8(input)));

    [Theory]
    [InlineData("claude", "idle")]
    [InlineData("codex", "limit")]
    [InlineData("claude", null)]
    public void Other_arguments_are_no_limit_and_stdin_is_not_read(string cli, string? state)
    {
        string[] args = state is null ? [cli] : [cli, state];
        Assert.Null(CliStateCommand.LimitReport(args, () => throw new InvalidOperationException("stdin is not read")));
    }

    [Fact]
    public void The_limit_hook_reports_nothing_as_a_state() =>
        Assert.Null(CliStateCommand.Report(["claude", "limit"], () => Utf8("""{"error":"rate_limit"}""")));

    // ---- FIFO ----

    [Fact]
    public void The_fifo_accepts_a_limit_with_or_without_session_and_message()
    {
        var full = FifoNotification.Parse($$"""{"secret":"{{Secret}}","event":"cli-limit","provider":"claude","cliSessionId":"{{Session}}","error":"rate_limit","message":"You've hit your limit\nresets 3pm"}""", Secret, out _);
        var bare = FifoNotification.Parse($$"""{"secret":"{{Secret}}","event":"cli-limit","provider":"claude","error":"billing_error"}""", Secret, out _);

        Assert.Equal(new FifoNotification.CliLimitReached("claude", Session, "rate_limit", "You've hit your limit\nresets 3pm"), full);
        Assert.Equal(new FifoNotification.CliLimitReached("claude", null, "billing_error", null), bare);
    }

    [Theory]
    [InlineData("""{"event":"cli-limit","provider":"claude","error":"server_error"}""", "unknown error")]
    [InlineData("""{"event":"cli-limit","provider":"vim","error":"rate_limit"}""", "unknown provider")]
    [InlineData("""{"event":"cli-limit","provider":"claude","error":"rate_limit","cliSessionId":"../x"}""", "invalid cliSessionId")]
    [InlineData("""{"event":"cli-limit","provider":"claude","error":"rate_limit","message":42}""", "invalid message")]
    public void The_fifo_rejects_a_malformed_limit(string line, string rejection)
    {
        Assert.Null(FifoNotification.Parse("{\"secret\":\"" + Secret + "\"," + line[1..], Secret, out var reason));
        Assert.Equal(rejection, reason);
    }

    [Fact]
    public void The_fifo_rejects_a_limit_message_longer_than_sidehub_cli_sends()
    {
        var line = $$"""{"secret":"{{Secret}}","event":"cli-limit","provider":"claude","error":"rate_limit","message":"{{new string('x', FifoNotification.MaxLimitMessageLength + 1)}}"}""";
        Assert.Null(FifoNotification.Parse(line, Secret, out var reason));
        Assert.Equal("invalid message", reason);
    }

    // ---- Claude's message ----

    [Theory]
    [InlineData("You've hit your session limit · resets 3pm (Europe/Paris)", CliLimitWindows.FiveHour)]
    [InlineData("You’ve hit your weekly limit · resets Oct 20, 9am", CliLimitWindows.Weekly)]
    [InlineData("You've hit your Opus limit", CliLimitWindows.Weekly)]
    [InlineData("You've hit your Fable limit · progress saved", CliLimitWindows.Weekly)]
    [InlineData("You've hit your usage credit limit", CliLimitWindows.Monthly)]
    [InlineData("API Error: 429 rate_limit_error", CliLimitWindows.Unknown)]
    [InlineData(null, CliLimitWindows.Unknown)]
    public void The_window_is_the_limit_claude_names(string? message, string window) =>
        Assert.Equal(window, ClaudeLimitText.Window(message));

    [Fact]
    public void A_reset_within_the_day_is_the_next_time_the_clock_of_its_zone_shows_it()
    {
        var morning = new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero); // 10:00 in Paris
        var evening = new DateTimeOffset(2026, 10, 16, 20, 0, 0, TimeSpan.Zero); // 22:00 in Paris

        Assert.Equal(new DateTimeOffset(2026, 10, 16, 13, 0, 0, TimeSpan.Zero),
            ClaudeLimitText.ResetsAt("You've hit your session limit · resets 3pm (Europe/Paris)", morning, TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 13, 30, 0, TimeSpan.Zero),
            ClaudeLimitText.ResetsAt("You've hit your session limit · resets 3:30pm (Europe/Paris)", evening, TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 22, 0, 0, TimeSpan.Zero),
            ClaudeLimitText.ResetsAt("You've hit your session limit · resets 12am (Europe/Paris)", morning, TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_reset_beyond_a_day_has_its_date_and_without_a_zone_the_machines_is_used()
    {
        var now = new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero),
            ClaudeLimitText.ResetsAt("You've hit your weekly limit · resets Oct 20, 9am (Europe/Paris)", now, TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2027, 1, 4, 8, 0, 0, TimeSpan.Zero),
            ClaudeLimitText.ResetsAt("You've hit your weekly limit · resets Jan 4, 2027, 9am", now, Paris));
    }

    [Theory]
    [InlineData("You've hit your session limit")]
    [InlineData("You've hit your session limit · resets 3pm (Mars/Olympus)")]
    [InlineData("You've hit your weekly limit · resets Feb 30, 9am (UTC)")]
    [InlineData("You've hit your session limit · resets 13pm (UTC)")]
    [InlineData(null)]
    public void A_reset_that_cannot_be_read_is_unknown(string? message) =>
        Assert.Null(ClaudeLimitText.ResetsAt(message, DateTimeOffset.UtcNow, TimeZoneInfo.Utc));

    [Fact]
    public void Claudes_fixtures_give_their_window_and_reset()
    {
        foreach (var line in Fixture("claude-2.1.301.txt"))
        {
            Assert.NotEqual(CliLimitWindows.Unknown, ClaudeLimitText.Window(line));
            Assert.NotNull(ClaudeLimitText.ResetsAt(line, DateTimeOffset.UtcNow, TimeZoneInfo.Utc));
            Assert.Equal(line, CliLimitPatterns.FindLine("claude", "Working…\n" + line + "\n"));
        }
    }

    // ---- codex: rate limits of the rollout ----

    /// <summary>A token_count line of a real rollout (codex-cli 0.162.1, plan prolite), no limit reached, with
    /// <paramref name="change"/> applied to its rate_limits.</summary>
    private static string TokenCount(Action<JsonObject>? change = null)
    {
        var line = JsonNode.Parse(Fixture("codex-0.162.1-token-count.jsonl")[0])!;
        change?.Invoke(line["payload"]!["rate_limits"]!.AsObject());
        return line.ToJsonString();
    }

    private string Rollout(params string[] lines)
    {
        var path = Path.Combine(_dir, $"rollout-2026-10-16T08-00-00-{Session}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void A_real_rollout_without_a_reached_limit_is_no_limit() =>
        Assert.Null(CodexRolloutHarvester.RateLimitOf(Rollout(TokenCount())));

    [Fact]
    public void A_reached_limit_gives_its_window_and_reset_from_the_rollout()
    {
        var path = Rollout(TokenCount(), TokenCount(limits =>
        {
            limits["rate_limit_reached_type"] = "primary";
            limits["primary"]!["used_percent"] = 100.0;
        }));

        var limit = CodexRolloutHarvester.RateLimitOf(path);

        Assert.Equal(new CliLimitReport("codex", CliLimitWindows.Weekly, DateTimeOffset.FromUnixTimeSeconds(1792652439),
            CliLimitSources.Rollout, null, Session), limit);
    }

    [Fact]
    public void The_window_that_ran_out_is_the_fullest_when_the_reached_type_names_none()
    {
        var path = Rollout(TokenCount(limits =>
        {
            limits["rate_limit_reached_type"] = new JsonObject { ["type"] = "limit_reached" };
            limits["secondary"] = new JsonObject { ["used_percent"] = 100.0, ["window_minutes"] = 300, ["resets_at"] = 1792600000 };
        }));

        var limit = CodexRolloutHarvester.RateLimitOf(path)!;

        Assert.Equal(CliLimitWindows.FiveHour, limit.Window);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792600000), limit.ResetsAt);
    }

    [Fact]
    public void Only_the_last_snapshot_counts_and_an_unknown_shape_concludes_nothing()
    {
        var reached = TokenCount(limits => limits["rate_limit_reached_type"] = "primary");
        var shapeless = TokenCount(limits =>
        {
            limits["rate_limit_reached_type"] = "primary";
            limits.Remove("primary");
        });

        Assert.Null(CodexRolloutHarvester.RateLimitOf(Rollout(reached, TokenCount())));
        Assert.Equal(new CliLimitReport("codex", CliLimitWindows.Unknown, null, CliLimitSources.Rollout, null, Session),
            CodexRolloutHarvester.RateLimitOf(Rollout(shapeless)));
    }

    // ---- what the CLIs print ----

    [Theory]
    [InlineData("codex", "codex-0.162.1.txt")]
    [InlineData("agy", "agy-1.3.4.txt")]
    public void Each_fixture_line_is_found_at_the_end_of_the_output(string provider, string fixture)
    {
        foreach (var line in Fixture(fixture))
            Assert.Equal(line, CliLimitPatterns.FindLine(provider, $"$ {provider} exec \"fix it\"\n  {line}  \n$ "));
    }

    [Fact]
    public void A_cli_without_a_structured_signal_is_recognized_from_its_output_once_it_failed()
    {
        var output = "Thinking…\n" + Fixture("agy-1.3.4.txt")[0] + "\n";

        Assert.Equal(new CliLimitReport("agy", CliLimitWindows.Unknown, null, CliLimitSources.Output, Fixture("agy-1.3.4.txt")[0], null),
            CliLimitPatterns.FromOutput("agy", 1, output, null));
        Assert.Null(CliLimitPatterns.FromOutput("agy", 0, output, null));
    }

    [Fact]
    public void Claude_and_codex_never_stop_on_their_output_alone()
    {
        Assert.Null(CliLimitPatterns.FromOutput("codex", 1, Fixture("codex-0.162.1.txt")[0], null));
        Assert.Null(CliLimitPatterns.FromOutput("claude", 1, Fixture("claude-2.1.301.txt")[0], null));
    }

    [Theory]
    [InlineData("agy", "if (rateLimited) throw new Error('rate limit exceeded: quota exhausted');")]
    [InlineData("copilot", "You have exhausted your quota on this model.")]
    [InlineData("codex", "hit your usage limit")]
    public void Code_about_rate_limits_and_another_clis_text_are_no_limit(string provider, string output) =>
        Assert.Null(CliLimitPatterns.FindLine(provider, output));

    // ---- what is sent ----

    [Fact]
    public void The_message_is_masked_then_cut_and_the_reset_sent_in_utc()
    {
        var limit = new CliLimitReport("claude", CliLimitWindows.FiveHour, new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.FromHours(2)),
            CliLimitSources.Hook, "token sk-123 " + new string('x', 400), Session);

        var message = limit.ToMessage("run-1", Guid.Empty, text => text.Replace("sk-123", "***"), DateTime.UnixEpoch);

        Assert.StartsWith("token *** xxx", message.Message);
        Assert.Equal(CliLimitReport.MaxMessageLength, message.Message!.Length);
        Assert.EndsWith("…", message.Message);
        Assert.Equal(new DateTime(2026, 10, 16, 13, 0, 0, DateTimeKind.Utc), message.ResetsAt);
        Assert.Equal(DateTimeKind.Utc, message.ResetsAt!.Value.Kind);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(message));
        Assert.Equal("cli.limit", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("five-hour", json.RootElement.GetProperty("window").GetString());
    }

    [Fact]
    public void A_repeated_limit_is_the_same_one()
    {
        var limit = new CliLimitReport("claude", CliLimitWindows.FiveHour, DateTimeOffset.UnixEpoch, CliLimitSources.Hook, "a", null);

        Assert.True(limit.SameAs(limit with { Message = "b", CliSessionId = Session }));
        Assert.False(limit.SameAs(limit with { ResetsAt = null }));
        Assert.False(limit.SameAs(null));
    }

    // ---- account keys ----

    [Fact]
    public void Claudes_account_comes_from_its_status_and_never_shows_in_its_key()
    {
        const string status = """{"loggedIn": true, "authMethod": "claude.ai", "email": "Dev@Example.com", "orgId": "org-1", "subscriptionType": "max"}""";

        var identity = CliAccountKeys.ClaudeIdentity(status);
        var key = CliAccountKeys.Key("claude", identity!);

        Assert.Equal("account:org-1\ndev@example.com", identity);
        Assert.Matches("^[0-9a-f]{16}$", key);
        Assert.Equal(key, CliAccountKeys.Key("claude", CliAccountKeys.ClaudeIdentity(status.Replace("Dev@", "dev@"))!));
        Assert.Null(CliAccountKeys.ClaudeIdentity("""{"loggedIn": true, "authMethod": "api_key"}"""));
    }

    [Fact]
    public void Clis_without_a_known_account_share_the_machines_and_each_cli_has_its_own_key()
    {
        var keys = CliAccountKeys.For(["claude", "codex", "agy"],
            new Dictionary<string, string?> { ["claude"] = "account:org-1\ndev@example.com" }, CliAccountKeys.MachineIdentity("/home/dev"));

        Assert.Equal(CliAccountKeys.Key("codex", CliAccountKeys.MachineIdentity("/home/dev")), keys["codex"]);
        Assert.NotEqual(keys["codex"], keys["agy"]);
        Assert.NotEqual(keys["codex"], CliAccountKeys.For(["codex"], new Dictionary<string, string?>(), CliAccountKeys.MachineIdentity("/home/other"))["codex"]);
        Assert.Equal(CliAccountKeys.Key("claude", "account:org-1\ndev@example.com"), keys["claude"]);
    }
}
