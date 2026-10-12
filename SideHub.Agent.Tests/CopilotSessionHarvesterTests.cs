using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

/// <summary>copilot's usage and last answer, read from its event log (lines in the shape copilot 1.0.95 writes them).</summary>
public class CopilotSessionHarvesterTests : IDisposable
{
    private const string Id = "bcd49825-e9eb-4925-95d0-46031bf0a1ac";
    private readonly string _root = Directory.CreateTempSubdirectory("sidehub-copilot-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CopilotSessionHarvester Harvester => new(_root);

    private static string Usage(string at, string model, long input, long output, long cacheRead, long cacheWrite, string reasoning = "null") =>
        $$"""{"type":"session.usage_record","data":{"usage":{"model":"{{model}}","provider":{"id":"copilot","kind":"copilot"},"inputTokens":{{input}},"outputTokens":{{output}},"cacheReadTokens":{{cacheRead}},"cacheWriteTokens":{{cacheWrite}},"reasoningTokens":{{reasoning}},"cost":0.33} },"id":"e1","timestamp":"{{at}}","parentId":null}""";

    private static string Message(string at, string content) =>
        $$"""{"type":"assistant.message","data":{"messageId":"m1","model":"claude-haiku-4.5","content":"{{content}}","toolRequests":[]},"id":"e2","timestamp":"{{at}}","parentId":"e1"}""";

    private void Write(params string[] lines)
    {
        var session = Directory.CreateDirectory(Path.Combine(_root, "session-state", Id)).FullName;
        File.WriteAllLines(Path.Combine(session, "events.jsonl"), lines);
    }

    private static RunUsageContext Run(DateTimeOffset? startedAt = null, params string[] ids) =>
        new(Guid.NewGuid(), "/w", ids, [], [], startedAt);

    [Fact]
    public void A_session_is_summed_per_model_with_cache_reads_and_writes_apart()
    {
        Write(
            """{"type":"session.start","data":{"sessionId":"bcd49825-e9eb-4925-95d0-46031bf0a1ac"},"id":"e0","timestamp":"2026-10-12T15:27:01.283Z"}""",
            Usage("2026-10-12T15:27:07.046Z", "claude-haiku-4.5", 22097, 346, 10119, 11968, "219"),
            Message("2026-10-12T15:27:07.050Z", "Creating the files."),
            Usage("2026-10-12T15:27:09.264Z", "claude-haiku-4.5", 22469, 152, 22087, 377),
            Usage("2026-10-12T15:27:11.088Z", "gpt-5.4", 1000, 50, 600, 0, "20"),
            Message("2026-10-12T15:27:12.000Z", "Done."),
            Message("2026-10-12T15:27:12.100Z", " "));

        var models = Harvester.HarvestSession("/w", Id)!;

        Assert.Equal(["claude-haiku-4-5", "gpt-5.4"], models.Select(m => m.Model));
        var claude = models[0];
        Assert.Equal(22097 - 10119 - 11968 + 22469 - 22087 - 377, claude.InputTokens);
        Assert.Equal(10119 + 22087, claude.CacheReadTokens);
        Assert.Equal(11968 + 377, claude.CacheWriteTokens);
        Assert.Equal(346 + 152, claude.OutputTokens);
        Assert.Equal(219, claude.ReasoningTokens);
        Assert.Equal(2, claude.Requests);
        Assert.Equal(400, models[1].InputTokens);
        Assert.Equal("Done.", Harvester.LastMessage("/w", Id));
        Assert.Single(Harvester.SessionFiles("/w", Id));
    }

    [Fact]
    public void A_run_counts_the_calls_made_since_it_started_in_a_resumed_session()
    {
        Write(
            Usage("2025-01-01T09:00:01Z", "claude-haiku-4.5", 1000, 10, 0, 0),
            Message("2025-01-01T09:00:02Z", "one"),
            Usage("2025-01-01T10:00:01Z", "claude-haiku-4.5", 2000, 20, 0, 0),
            Message("2025-01-01T10:00:02Z", "two"));

        var models = Harvester.Harvest(Run(new DateTimeOffset(2025, 1, 1, 9, 59, 0, TimeSpan.Zero), Id))!;

        var only = Assert.Single(models);
        Assert.Equal(2000, only.InputTokens);
        Assert.Equal(1, only.Requests);
        Assert.Equal("two", Harvester.ReadFinalMessage(Run(null, Id)));
    }

    [Fact]
    public void Nothing_is_read_for_an_unknown_or_unsafe_session()
    {
        Write(Usage("2026-10-12T09:00:01Z", "claude-haiku-4.5", 1000, 10, 0, 0));

        Assert.Null(Harvester.Harvest(Run(null, "11111111-2222-3333-4444-555555555555")));
        Assert.Null(Harvester.HarvestSession("/w", "../../etc"));
        Assert.Empty(Harvester.SessionFiles("/w", "../../etc"));
    }

    [Fact]
    public void Broken_lines_are_skipped_and_a_call_without_a_model_is_kept_as_unknown()
    {
        Write(Usage("2026-10-12T09:00:01Z", "", 1000, 10, 0, 0), "{ not json", "");

        Assert.Equal("unknown", Assert.Single(Harvester.HarvestSession("/w", Id)!).Model);
    }

    [Theory]
    [InlineData("claude-haiku-4.5", "claude-haiku-4-5")]
    [InlineData("Claude-Sonnet-5.5", "claude-sonnet-5-5")]
    [InlineData("gpt-5.4", "gpt-5.4")]
    [InlineData("<script>", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Copilots_model_gives_the_priced_id(string? model, string? id) =>
        Assert.Equal(id, CopilotSessionHarvester.ModelId(model));
}
