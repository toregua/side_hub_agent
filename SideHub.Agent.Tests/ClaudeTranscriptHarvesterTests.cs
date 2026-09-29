using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

public class ClaudeTranscriptHarvesterTests : IDisposable
{
    private const string Cwd = "/work/side_hub";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";
    private readonly ClaudeTemp _claude = new();

    public void Dispose() => _claude.Dispose();

    private ClaudeTranscriptHarvester Harvester => new(_claude.Root);

    private static RunUsageContext Run(params string[] sessionIds) => new(Guid.NewGuid(), Cwd, sessionIds, [], []);

    [Fact]
    public void EncodeCwd_ReplacesEveryNonAlphanumericCharacter()
    {
        Assert.Equal("-root-Github-side-hub-agent", ClaudeProjectPaths.EncodeCwd("/root/Github/side_hub_agent"));
        Assert.Equal("-tmp-my-dir-v1-2", ClaudeProjectPaths.EncodeCwd("/tmp/my dir/v1.2"));
    }

    [Fact]
    public void Harvest_SumsPerModel_CountingStreamedMessagesOnce_AndIncludingSubagents()
    {
        _claude.AddSession("-work-side-hub", SessionId);

        var models = Harvester.Harvest(Run(SessionId));

        Assert.NotNull(models);
        Assert.Equal(["claude-haiku-test", "claude-opus-test"], models.Select(m => m.Model));

        // msg_A (streamed 3x, last write wins) + msg_B + subagent msg_E (streamed 2x).
        var opus = models.Single(m => m.Model == "claude-opus-test");
        Assert.Equal(3 + 5 + 2, opus.InputTokens);
        Assert.Equal(120 + 40 + 30, opus.OutputTokens);
        Assert.Equal(1000 + 1500 + 200, opus.CacheReadTokens);
        Assert.Equal(500 + 0 + 100, opus.CacheWriteTokens);
        Assert.Equal(3, opus.Requests);
        Assert.Null(opus.ReasoningTokens);

        // msg_C + sidechain msg_D written in the session file itself.
        var haiku = models.Single(m => m.Model == "claude-haiku-test");
        Assert.Equal(110, haiku.InputTokens);
        Assert.Equal(25, haiku.OutputTokens);
        Assert.Equal(2, haiku.Requests);
    }

    [Fact]
    public void Harvest_SkipsSyntheticMessagesAndCorruptedLines()
    {
        _claude.AddSession("-work-side-hub", SessionId, withSubagents: false);

        var models = Harvester.Harvest(Run(SessionId))!;

        Assert.DoesNotContain(models, m => m.Model == "<synthetic>");
        // The half-written msg_Z line (input 999) is ignored.
        Assert.Equal(3 + 5, models.Single(m => m.Model == "claude-opus-test").InputTokens);
    }

    [Fact]
    public void Harvest_ExitedSession_UsesClaudesFinalCostState()
    {
        _claude.AddSession("-work-side-hub", SessionId, withSubagents: true, "cost-state.jsonl");

        var models = Harvester.Harvest(Run(SessionId))!;

        // What /cost shows, including calls absent from the transcript (claude-side-test).
        Assert.Equal(["claude-opus-test[1m]", "claude-side-test"], models.Select(m => m.Model));
        var opus = models[0];
        Assert.Equal(700, opus.InputTokens);
        Assert.Equal(200, opus.OutputTokens);
        Assert.Equal(3000, opus.CacheReadTokens);
        Assert.Equal(650, opus.CacheWriteTokens);
        // Requests come from the transcript: the alias matches the API id (msg_A, msg_B, subagent msg_E).
        Assert.Equal(3, opus.Requests);
        Assert.Equal(0, models[1].Requests);
    }

    [Fact]
    public void Harvest_CostStateFollowedByMoreMessages_IsStale_SoMessagesAreSummed()
    {
        // e.g. the session was resumed after an earlier exit and is still running.
        _claude.AddSession("-work-side-hub", SessionId, withSubagents: false, "cost-state.jsonl", "later-turn.jsonl");

        var models = Harvester.Harvest(Run(SessionId))!;

        var opus = models.Single(m => m.Model == "claude-opus-test");
        Assert.Equal(120 + 40 + 10, opus.OutputTokens);
        Assert.Equal(3, opus.Requests);
        Assert.DoesNotContain(models, m => m.Model == "claude-opus-test[1m]");
    }

    [Fact]
    public void Harvest_MissingTranscript_ReturnsNull()
    {
        Assert.Null(Harvester.Harvest(Run(SessionId)));
    }

    [Fact]
    public void Harvest_FindsTheTranscriptInAnotherProjectDirectory()
    {
        // e.g. Claude hashed a very long cwd, or the cwd was reached through a symlink.
        _claude.AddSession("-some-other-encoding", SessionId, withSubagents: false);

        var models = Harvester.Harvest(Run(SessionId));

        Assert.NotNull(models);
        Assert.Equal(2, models.Count);
    }

    [Fact]
    public void Harvest_SessionsSharingMessages_CountThemOnce_AndAnUnknownSessionIsIgnored()
    {
        const string second = "99999999-2222-3333-4444-555555555555";
        _claude.AddSession("-work-side-hub", SessionId, withSubagents: false);
        _claude.AddSession("-work-side-hub", second, withSubagents: false);

        // A forked session copies its parent's messages into its own file: same ids, counted once.
        var models = Harvester.Harvest(Run(SessionId, second, "missing-session"))!;

        Assert.Equal(2, models.Single(m => m.Model == "claude-opus-test").Requests);
    }
}
