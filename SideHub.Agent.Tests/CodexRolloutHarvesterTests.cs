using SideHub.Agent.Models;
using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

public class CodexRolloutHarvesterTests : IDisposable
{
    private const string Cwd = "/work/side_hub";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly CodexTemp _codex = new();

    public void Dispose() => _codex.Dispose();

    private CodexRolloutHarvester Harvester => new(_codex.Root);

    private static CliLaunch Launch(string pty, DateTimeOffset at, string cwd = Cwd) => new(pty, "codex", cwd, at);

    private static RunUsageContext Run(CliLaunch[] launches, params CliLaunch[] others) =>
        new(Guid.NewGuid(), Cwd, [], launches, others);

    private static long Total(IEnumerable<ModelUsageReport> models) =>
        models.Sum(m => m.InputTokens + m.CacheReadTokens + m.OutputTokens);

    [Fact]
    public void Harvest_ReadsTheLaunchesRollout_SplitPerModel()
    {
        _codex.AddRollout(T0.AddSeconds(2), Cwd);

        var models = Harvester.Harvest(Run([Launch("run-a", T0)]));

        Assert.NotNull(models);
        Assert.Equal(["gpt-test", "gpt-test-mini"], models.Select(m => m.Model));

        // Up to the model switch: the first cumulative total (repeated once, counted once).
        var first = models[0];
        Assert.Equal(20000 - 15000, first.InputTokens);
        Assert.Equal(15000, first.CacheReadTokens);
        Assert.Equal(300, first.OutputTokens);
        Assert.Equal(100, first.ReasoningTokens);
        Assert.Equal(0, first.CacheWriteTokens);
        Assert.Equal(1, first.Requests);

        // After it: the increase of the last cumulative total.
        var second = models[1];
        Assert.Equal(30000 - 25000, second.InputTokens);
        Assert.Equal(25000, second.CacheReadTokens);
        Assert.Equal(500, second.OutputTokens);
        Assert.Equal(150, second.ReasoningTokens);
        Assert.Equal(1, second.Requests);

        // Together: the last token_count (input 50000 incl. cached, output 800); the half-written line is ignored.
        Assert.Equal(50000 + 800, Total(models));
    }

    [Fact]
    public void Harvest_ExecSession_IsRead()
    {
        _codex.AddRollout(T0.AddSeconds(1), Cwd, "short.jsonl", source: "exec");

        var model = Assert.Single(Harvester.Harvest(Run([Launch("run-a", T0)]))!);

        Assert.Equal("gpt-test", model.Model);
        Assert.Equal(200, model.InputTokens);
        Assert.Equal(800, model.CacheReadTokens);
        Assert.Equal(7, model.OutputTokens);
    }

    [Fact]
    public void Harvest_WithoutLaunch_ReturnsNull()
    {
        _codex.AddRollout(T0.AddSeconds(2), Cwd);

        Assert.Null(Harvester.Harvest(Run([])));
    }

    [Fact]
    public void Harvest_IgnoresRolloutsThatCannotBeTheLaunches()
    {
        _codex.AddRollout(T0.AddSeconds(2), "/work/other");                         // another directory
        _codex.AddRollout(T0.AddMinutes(5), Cwd);                                   // started too late
        _codex.AddRollout(T0.AddHours(-2), Cwd);                                    // resumed older session
        _codex.AddRollout(T0.AddSeconds(3), Cwd, source: "vscode");                 // SideHub's app-server
        _codex.AddRollout(T0.AddSeconds(4), Cwd,
            source: """{"subagent":{"thread_spawn":{"parent_thread_id":"x","depth":1}}}""");

        Assert.Null(Harvester.Harvest(Run([Launch("run-a", T0)])));
    }

    [Fact]
    public void Harvest_ConcurrentRunsInTheSameCwd_AreUnavailable_NotMixed()
    {
        _codex.AddRollout(T0.AddSeconds(2), Cwd);
        _codex.AddRollout(T0.AddSeconds(22), Cwd, "short.jsonl");
        var a = Launch("run-a", T0);
        var b = Launch("run-b", T0.AddSeconds(20));

        Assert.Null(Harvester.Harvest(Run([a], b)));
        Assert.Null(Harvester.Harvest(Run([b], a)));
    }

    [Fact]
    public void Harvest_RunsInTheSameCwdStartedApart_EachGetsItsOwnRollout()
    {
        _codex.AddRollout(T0.AddSeconds(2), Cwd);
        _codex.AddRollout(T0.AddMinutes(5).AddSeconds(2), Cwd, "short.jsonl");
        var a = Launch("run-a", T0);
        var b = Launch("run-b", T0.AddMinutes(5));

        Assert.Equal(50000 + 800, Total(Harvester.Harvest(Run([a], b))!));
        Assert.Equal(1000 + 7, Total(Harvester.Harvest(Run([b], a))!));
    }

    [Fact]
    public void Harvest_ConcurrentRunsInDifferentCwds_EachGetsItsOwnRollout()
    {
        _codex.AddRollout(T0.AddSeconds(2), Cwd);
        _codex.AddRollout(T0.AddSeconds(2), "/work/other", "short.jsonl");
        var a = Launch("run-a", T0);
        var b = Launch("run-b", T0, "/work/other");

        Assert.Equal(50000 + 800, Total(Harvester.Harvest(Run([a], b))!));
        Assert.Equal(1000 + 7, Total(Harvester.Harvest(Run([b], a))!));
    }

    [Fact]
    public void Harvest_TwoRolloutsNearOneLaunch_IsUnavailable()
    {
        // e.g. a codex started by hand in the same directory, unknown to SideHub.
        _codex.AddRollout(T0.AddSeconds(2), Cwd);
        _codex.AddRollout(T0.AddSeconds(10), Cwd, "short.jsonl");

        Assert.Null(Harvester.Harvest(Run([Launch("run-a", T0)])));
    }

    [Fact]
    public void Harvest_SeveralLaunchesOfTheRun_AddTheirRollouts()
    {
        _codex.AddRollout(T0.AddSeconds(2), Cwd);
        _codex.AddRollout(T0.AddMinutes(10).AddSeconds(2), Cwd, "short.jsonl");

        var models = Harvester.Harvest(Run([Launch("run-a", T0), Launch("run-a", T0.AddMinutes(10))]))!;

        Assert.Equal(50000 + 800 + 1000 + 7, Total(models));
        Assert.Equal(2, models.Single(m => m.Model == "gpt-test").Requests);
    }

    /// <summary>A launch whose process was seen holding <paramref name="files"/>, then exited or not.</summary>
    private static async Task<CliLaunch> Observed(string pty, DateTimeOffset at, bool exited, params string[] files)
    {
        var observation = new LaunchObservation(TimeSpan.Zero, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        try
        {
            await observation.WatchAsync(() =>
            {
                if (calls++ == 0) return files;
                if (exited) return null;
                cts.Cancel();
                return files;
            }, cts.Token);
        }
        catch (OperationCanceledException) { }
        return new CliLaunch(pty, "codex", Cwd, at, observation);
    }

    [Fact]
    public async Task Harvest_ConcurrentRunsInTheSameCwd_WhenObserved_EachGetsItsOwnRollout()
    {
        var first = _codex.AddRollout(T0.AddSeconds(2), Cwd);
        var second = _codex.AddRollout(T0.AddSeconds(22), Cwd, "short.jsonl");
        var a = await Observed("run-a", T0, exited: false, first);
        var b = await Observed("run-b", T0.AddSeconds(20), exited: true, second);

        Assert.Equal(50000 + 800, Total(Harvester.Harvest(Run([a], b))!));
        Assert.Equal(1000 + 7, Total(Harvester.Harvest(Run([b], a))!));
    }

    [Fact]
    public async Task Harvest_UnobservedLaunch_IgnoresRolloutsHeldByObservedOnes()
    {
        _codex.AddRollout(T0.AddSeconds(2), Cwd);
        var other = _codex.AddRollout(T0.AddSeconds(22), Cwd, "short.jsonl");
        var b = await Observed("terminal-1", T0.AddSeconds(20), exited: false, other);

        Assert.Equal(50000 + 800, Total(Harvester.Harvest(Run([Launch("run-a", T0)], b))!));
    }

    [Fact]
    public async Task Harvest_ObservedLaunchThatNeverOpenedARollout_IsNotGivenANearbyOne()
    {
        // codex failed before starting a session; another agent's codex started in the same cwd meanwhile.
        _codex.AddRollout(T0.AddSeconds(2), Cwd);
        var a = await Observed("run-a", T0, exited: true);

        Assert.Null(Harvester.Harvest(Run([a])));
    }

    [Fact]
    public async Task Harvest_ObservedResumedSession_CountsOnlyWhatFollowsTheLaunch()
    {
        // The fixture's responses are at 00:00:05 (gpt-test) and 00:01:03 (gpt-test-mini).
        var resumed = _codex.AddRollout(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Cwd);
        var a = await Observed("run-a", new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero), exited: true, resumed);

        var model = Assert.Single(Harvester.Harvest(Run([a]))!);

        Assert.Equal("gpt-test-mini", model.Model);
        Assert.Equal(30000 + 500, Total([model]));
    }

    [Fact]
    public async Task Harvest_ObservedLaunchWithSeveralSessions_AddsThem()
    {
        // e.g. /new, or a sub-agent rollout the process also holds (not counted: not a top-level session).
        var first = _codex.AddRollout(T0.AddSeconds(2), Cwd);
        var second = _codex.AddRollout(T0.AddMinutes(10), Cwd, "short.jsonl");
        var subagent = _codex.AddRollout(T0.AddMinutes(1), Cwd, "short.jsonl",
            source: """{"subagent":{"thread_spawn":{"parent_thread_id":"x","depth":1}}}""");
        var a = await Observed("run-a", T0, exited: true, first, second, subagent);

        Assert.Equal(50000 + 800 + 1000 + 7, Total(Harvester.Harvest(Run([a]))!));
    }

    [Fact]
    public void Harvest_MissingSessionsRoot_ReturnsNull()
    {
        var harvester = new CodexRolloutHarvester(Path.Combine(_codex.Root, "missing"));

        Assert.Null(harvester.Harvest(Run([Launch("run-a", T0)])));
    }

    [Fact]
    public void ReadFinalMessage_IsTheLastAgentMessageOfTheCompletedTask()
    {
        _codex.AddRollout(T0.AddSeconds(2), Cwd, "answer.jsonl", source: "exec");

        // task_complete wins over the earlier agent_message.
        Assert.Equal("## Answer\n\nLogin issues a **JWT**.", Harvester.ReadFinalMessage(Run([Launch("run-a", T0)])));
    }

    [Fact]
    public void ReadFinalMessage_FallsBackToTheLastAgentMessage()
    {
        _codex.AddRollout(T0.AddSeconds(2), Cwd);

        Assert.Equal("<answer>", Harvester.ReadFinalMessage(Run([Launch("run-a", T0)])));
    }

    [Fact]
    public void ReadFinalMessage_IsNullWithoutAMessageOrARollout()
    {
        _codex.AddRollout(T0.AddSeconds(2), Cwd, "short.jsonl");

        Assert.Null(Harvester.ReadFinalMessage(Run([Launch("run-a", T0)])));
        Assert.Null(Harvester.ReadFinalMessage(Run([Launch("run-b", T0.AddHours(1))])));
    }
}
