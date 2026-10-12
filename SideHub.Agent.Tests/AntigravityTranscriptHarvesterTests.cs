using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

/// <summary>agy's usage, last answer and title, read from its files (lines in the shape agy 1.3.3 writes them).</summary>
public class AntigravityTranscriptHarvesterTests : IDisposable
{
    private const string Id = "c4efcc5e-913f-4753-b43f-3a449128899f";
    private readonly string _root = Directory.CreateTempSubdirectory("sidehub-agy-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AntigravityTranscriptHarvester Harvester => new(_root);

    private static string UserInput(string at, string text, string? model = null) =>
        $$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{at}}","content":"<USER_REQUEST>\n{{text}}\n</USER_REQUEST>{{(model is null ? "" : $"\\n<USER_SETTINGS_CHANGE>\\nThe user changed setting `Model Selection` from {model}. No need to comment on this change if the user doesn't ask about it.\\n</USER_SETTINGS_CHANGE>")}}"}""";

    private static string Response(string at, long input, long cached, long output, string content = "") =>
        $$"""{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"{{at}}","input_tokens":{{input}},"cache_read_tokens":{{cached}},"output_tokens":{{output}},"content":"{{content}}"}""";

    private string Write(params string[] lines)
    {
        var logs = Directory.CreateDirectory(Path.Combine(_root, "brain", Id, ".system_generated", "logs")).FullName;
        var path = Path.Combine(logs, "transcript_full.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static RunUsageContext Run(DateTimeOffset? startedAt = null, params string[] ids) =>
        new(Guid.NewGuid(), "/w", ids, [], [], startedAt);

    [Fact]
    public void A_session_is_summed_per_model_with_cached_input_apart()
    {
        Write(
            UserInput("2026-10-12T09:30:36Z", "Say A.", "None to Gemini 3.8 Flash (Low)"),
            Response("2026-10-12T09:30:36Z", 11868, 0, 1, "A"),
            """{"step_index":2,"type":"SYSTEM_MESSAGE","status":"DONE","created_at":"2026-10-12T09:30:42Z","content":"notice"}""",
            Response("2026-10-12T09:30:42Z", 12068, 8000, 30),
            UserInput("2026-10-12T09:30:47Z", "Say C.", "Gemini 3.8 Flash (Low) to Claude Opus 5.5 (High)"),
            Response("2026-10-12T09:30:47Z", 12349, 0, 3, "C"));

        var models = Harvester.HarvestSession("/w", Id)!;

        Assert.Equal(["claude-opus-5-5-high", "gemini-3.8-flash-low"], models.Select(m => m.Model));
        var gemini = models[1];
        Assert.Equal(11868 + 12068 - 8000, gemini.InputTokens);
        Assert.Equal(8000, gemini.CacheReadTokens);
        Assert.Equal(31, gemini.OutputTokens);
        Assert.Equal(2, gemini.Requests);
        Assert.Equal(12349, models[0].InputTokens);
        Assert.Equal("C", Harvester.LastMessage("/w", Id));
        Assert.Single(Harvester.SessionFiles("/w", Id));
    }

    [Fact]
    public void A_run_counts_the_steps_written_since_it_started_in_a_resumed_conversation()
    {
        Write(
            UserInput("2025-01-01T09:00:00Z", "first", "None to Gemini 3.8 Flash (High)"),
            Response("2025-01-01T09:00:01Z", 1000, 0, 10, "one"),
            UserInput("2025-01-01T10:00:00Z", "second"),
            Response("2025-01-01T10:00:01Z", 2000, 0, 20, "two"));

        var models = Harvester.Harvest(Run(new DateTimeOffset(2025, 1, 1, 9, 59, 0, TimeSpan.Zero), Id))!;

        var only = Assert.Single(models);
        Assert.Equal("gemini-3.8-flash-high", only.Model);
        Assert.Equal(2000, only.InputTokens);
        Assert.Equal(1, only.Requests);
        Assert.Equal("two", Harvester.ReadFinalMessage(Run(null, Id)));
    }

    [Fact]
    public void Nothing_is_read_for_an_unknown_or_unsafe_conversation()
    {
        Write(Response("2026-10-12T09:00:01Z", 1000, 0, 10));

        Assert.Null(Harvester.Harvest(Run(null, "11111111-2222-3333-4444-555555555555")));
        Assert.Null(Harvester.HarvestSession("/w", "../../etc"));
        Assert.Empty(Harvester.SessionFiles("/w", "../../etc"));
        Assert.Null(Harvester.Title("../x"));
    }

    [Fact]
    public void Model_calls_before_any_model_selection_are_kept_as_unknown()
    {
        Write(Response("2026-10-12T09:00:01Z", 1000, 0, 10), "{ not json", "");

        Assert.Equal("unknown", Assert.Single(Harvester.HarvestSession("/w", Id)!).Model);
    }

    [Theory]
    [InlineData("Gemini 3.8 Flash (High)", "gemini-3.8-flash-high")]
    [InlineData("Gemini 3.1 Pro (Low)", "gemini-3.1-pro-low")]
    [InlineData("Claude Sonnet 5.5 (Medium)", "claude-sonnet-5-5-medium")]
    [InlineData("GPT-OSS 120B (Medium)", "gpt-oss-120b-medium")]
    [InlineData("None", "none")]
    [InlineData("<script>", null)]
    [InlineData("  ", null)]
    public void A_display_name_gives_agys_model_id(string name, string? id) =>
        Assert.Equal(id, AntigravityTranscriptHarvester.ModelId(name));

    [Fact]
    public void The_title_is_read_from_the_annotation_with_its_escapes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "annotations"));
        File.WriteAllText(Path.Combine(_root, "annotations", Id + ".pbtxt"), "title:\"Corriger l'\\303\\251cran \\\"login\\\"\"\n");

        Assert.Equal("Corriger l'écran \"login\"", Harvester.Title(Id));
        Assert.Null(AntigravityTranscriptHarvester.TitleFromAnnotation("other:\"x\""));
        Assert.Null(AntigravityTranscriptHarvester.TitleFromAnnotation("title:\"\""));
    }
}
