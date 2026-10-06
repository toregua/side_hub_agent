using SideHub.Agent.Models;
using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

public class RunUsageCollectorTests : IDisposable
{
    private const string Cwd = "/work/side_hub";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";
    private readonly ClaudeTemp _claude = new();
    private readonly CodexTemp _codex = new();
    private readonly List<RunUsageMessage> _sent = [];
    private readonly List<RunAnswerMessage> _answers = [];
    private readonly PendingUsageStore _pending;
    private readonly PendingRunAnswerStore _pendingAnswers;
    private bool _connected = true;

    public RunUsageCollectorTests()
    {
        _pending = new PendingUsageStore(Path.Combine(_claude.Root, "pending-usage"));
        _pendingAnswers = new PendingRunAnswerStore(Path.Combine(_claude.Root, "pending-usage", "answers"));
    }

    public void Dispose()
    {
        _claude.Dispose();
        _codex.Dispose();
    }

    private RunUsageCollector Collector() => new(
        new Dictionary<string, IUsageHarvester>
        {
            ["claude"] = new ClaudeTranscriptHarvester(_claude.Root),
            ["codex"] = new CodexRolloutHarvester(_codex.Root),
        },
        _pending,
        (report, _) =>
        {
            if (!_connected) return Task.FromResult(false);
            _sent.Add(report);
            return Task.FromResult(true);
        },
        _pendingAnswers,
        (answer, _) =>
        {
            if (!_connected) return Task.FromResult(false);
            _answers.Add(answer);
            return Task.FromResult(true);
        },
        _ => { });

    private static readonly PreparedCheckout Question =
        new(Cwd, "0123456789abcdef0123456789abcdef01234567", new DateTimeOffset(2026, 10, 6, 9, 30, 0, TimeSpan.FromHours(2)));

    [Fact]
    public void ResolveRunId_OnlyForRunPtys_PreferringTheEnv()
    {
        var runId = Guid.NewGuid();
        var envId = Guid.NewGuid();

        Assert.Equal(runId, RunUsageCollector.ResolveRunId($"run-{runId:N}", null));
        Assert.Equal(envId, RunUsageCollector.ResolveRunId($"run-{runId:N}",
            new Dictionary<string, string> { ["SIDEHUB_RUN_ID"] = envId.ToString() }));
        Assert.Null(RunUsageCollector.ResolveRunId("run-not-a-guid", null));
        Assert.Null(RunUsageCollector.ResolveRunId("workflow-abc-01", null));
        Assert.Null(RunUsageCollector.ResolveRunId("terminal-1",
            new Dictionary<string, string> { ["SIDEHUB_RUN_ID"] = envId.ToString() }));
    }

    [Fact]
    public async Task ClaudeRun_ReportsTheTranscriptUsage()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var runId = Guid.NewGuid();
        var collector = Collector();
        collector.TrackRun("run-1", runId, Cwd);
        collector.RecordCliSession("run-1", "claude", SessionId);

        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);

        var report = Assert.Single(_sent);
        Assert.Equal(runId, report.RunId);
        Assert.Equal(ClaudeTranscriptHarvester.SourceName, report.Source);
        Assert.Equal(2, report.Models.Count);
        Assert.False(collector.IsTracked("run-1"));
    }

    [Fact]
    public async Task CodexRun_ReportsItsRolloutUsage()
    {
        var launchedAt = DateTimeOffset.UtcNow;
        _codex.AddRollout(launchedAt.AddSeconds(2), Cwd);
        var runId = Guid.NewGuid();
        var collector = Collector();
        collector.TrackRun("run-1", runId, Cwd);
        collector.RecordCliLaunch("run-1", "codex", Cwd, launchedAt);

        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);

        var report = Assert.Single(_sent);
        Assert.Equal(runId, report.RunId);
        Assert.Equal(CodexRolloutHarvester.SourceName, report.Source);
        Assert.Equal(["gpt-test", "gpt-test-mini"], report.Models.Select(m => m.Model));
    }

    [Fact]
    public async Task CodexRun_WithACodexStartedAlongsideInTheSameCwd_IsReportedUnavailable()
    {
        var launchedAt = DateTimeOffset.UtcNow;
        _codex.AddRollout(launchedAt.AddSeconds(2), Cwd);
        _codex.AddRollout(launchedAt.AddSeconds(4), Cwd, "short.jsonl");
        var collector = Collector();
        collector.TrackRun("run-1", Guid.NewGuid(), Cwd);
        collector.RecordCliLaunch("run-1", "codex", Cwd, launchedAt);
        // An interactive terminal: not a run, but its launch makes the rollouts ambiguous.
        collector.RecordCliLaunch("terminal-1", "codex", Cwd, launchedAt.AddSeconds(1));

        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);

        var report = Assert.Single(_sent);
        Assert.Equal(NullHarvester.Unavailable, report.Source);
        Assert.Empty(report.Models);
    }

    [Fact]
    public async Task RunWithoutAMeasurableCliSession_IsReportedUnavailable()
    {
        var collector = Collector();
        collector.TrackRun("run-gemini", Guid.NewGuid(), Cwd);
        collector.TrackRun("run-codex", Guid.NewGuid(), Cwd);
        collector.RecordCliSession("run-codex", "codex", "some-id");

        await collector.HarvestAsync("run-gemini", "exit", final: true, CancellationToken.None);
        await collector.HarvestAsync("run-codex", "exit", final: true, CancellationToken.None);

        Assert.All(_sent, r =>
        {
            Assert.Equal(NullHarvester.Unavailable, r.Source);
            Assert.Empty(r.Models);
        });
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task ClaudeRunWhoseTranscriptIsMissing_IsReportedUnavailable()
    {
        var collector = Collector();
        collector.TrackRun("run-1", Guid.NewGuid(), Cwd);
        collector.RecordCliSession("run-1", "claude", SessionId);

        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);

        Assert.Equal(NullHarvester.Unavailable, Assert.Single(_sent).Source);
    }

    [Fact]
    public async Task StepEnd_KeepsTrackingTheRun_UntilTheFinalHarvest()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var collector = Collector();
        collector.TrackRun("run-1", Guid.NewGuid(), Cwd);
        collector.RecordCliSession("run-1", "claude", SessionId);

        await collector.HarvestAsync("run-1", "step-ended", final: false, CancellationToken.None);
        Assert.True(collector.IsTracked("run-1"));

        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);
        await collector.HarvestAsync("run-1", "stop", final: true, CancellationToken.None);

        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task UntrackedPty_IsIgnored()
    {
        await Collector().HarvestAsync("terminal-1", "exit", final: true, CancellationToken.None);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Disconnected_KeepsTheReport_AndReplaysItOnReconnection()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var runId = Guid.NewGuid();
        var collector = Collector();
        collector.TrackRun("run-1", runId, Cwd);
        collector.RecordCliSession("run-1", "claude", SessionId);

        _connected = false;
        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);
        Assert.Empty(_sent);
        Assert.True(File.Exists(Path.Combine(_pending.Directory, $"{runId}.json")));

        _connected = true;
        await Collector().ReplayPendingAsync(CancellationToken.None);

        var report = Assert.Single(_sent);
        Assert.Equal(runId, report.RunId);
        Assert.Equal(ClaudeTranscriptHarvester.SourceName, report.Source);
        Assert.Equal(190, report.Models.Single(m => m.Model == "claude-opus-test").OutputTokens);
        Assert.Empty(_pending.LoadAll());
    }

    [Fact]
    public async Task SuccessfulSend_DropsAnOlderPendingReportForTheSameRun()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var collector = Collector();
        collector.TrackRun("run-1", Guid.NewGuid(), Cwd);
        collector.RecordCliSession("run-1", "claude", SessionId);

        _connected = false;
        await collector.HarvestAsync("run-1", "step-ended", final: false, CancellationToken.None);
        _connected = true;
        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);

        Assert.Single(_sent);
        Assert.Empty(_pending.LoadAll());
    }

    [Fact]
    public async Task QuestionRun_SendsItsAnswerOnce_WhenItEnds()
    {
        _claude.AddSession("-work-side-hub", SessionId, withSubagents: true, "answer.jsonl");
        var runId = Guid.NewGuid();
        var collector = Collector();
        collector.TrackRun("run-1", runId, Cwd, Question);
        collector.RecordCliSession("run-1", "claude", SessionId);

        await collector.HarvestAsync("run-1", "step-ended", final: false, CancellationToken.None);
        Assert.Empty(_answers);

        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);
        await collector.HarvestAsync("run-1", "stop", final: true, CancellationToken.None);

        var answer = Assert.Single(_answers);
        Assert.Equal(runId, answer.RunId);
        Assert.Equal("## Answer\n\nLogin issues a **JWT**.\n\nSee `AuthService.cs`.", answer.Text);
        Assert.Equal(Question.CommitSha, answer.CommitSha);
        Assert.Equal(Question.CommitDate, answer.CommitDate);
        Assert.Null(answer.Error);
    }

    [Fact]
    public async Task QuestionRun_ReadsCodexsLastMessage()
    {
        var launchedAt = DateTimeOffset.UtcNow;
        _codex.AddRollout(launchedAt.AddSeconds(2), Cwd, "answer.jsonl", source: "exec");
        var collector = Collector();
        collector.TrackRun("run-1", Guid.NewGuid(), Cwd, Question);
        collector.RecordCliLaunch("run-1", "codex", Cwd, launchedAt);

        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);

        Assert.Equal("## Answer\n\nLogin issues a **JWT**.", Assert.Single(_answers).Text);
    }

    [Fact]
    public async Task QuestionRun_WithoutAFinalMessage_SendsTheReason()
    {
        var collector = Collector();
        collector.TrackRun("run-none", Guid.NewGuid(), Cwd, Question);
        collector.TrackRun("run-missing", Guid.NewGuid(), Cwd, Question);
        collector.RecordCliSession("run-missing", "claude", SessionId);

        await collector.HarvestAsync("run-none", "exit", final: true, CancellationToken.None);
        await collector.HarvestAsync("run-missing", "exit", final: true, CancellationToken.None);

        Assert.All(_answers, a =>
        {
            Assert.Null(a.Text);
            Assert.Equal(Question.CommitSha, a.CommitSha);
        });
        Assert.Equal(["no-cli-session", "no-final-message"], _answers.Select(a => a.Error));
    }

    [Fact]
    public async Task OtherRuns_SendNoAnswer()
    {
        _claude.AddSession("-work-side-hub", SessionId, withSubagents: true, "answer.jsonl");
        var collector = Collector();
        collector.TrackRun("run-1", Guid.NewGuid(), Cwd);
        collector.RecordCliSession("run-1", "claude", SessionId);

        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);

        Assert.Single(_sent);
        Assert.Empty(_answers);
    }

    [Fact]
    public async Task Disconnected_KeepsTheAnswer_AndReplaysItOnReconnection()
    {
        _claude.AddSession("-work-side-hub", SessionId, withSubagents: true, "answer.jsonl");
        var runId = Guid.NewGuid();
        var collector = Collector();
        collector.TrackRun("run-1", runId, Cwd, Question);
        collector.RecordCliSession("run-1", "claude", SessionId);

        _connected = false;
        await collector.HarvestAsync("run-1", "exit", final: true, CancellationToken.None);
        Assert.Empty(_answers);
        Assert.True(File.Exists(Path.Combine(_pendingAnswers.Directory, $"{runId}.json")));

        _connected = true;
        await Collector().ReplayPendingAsync(CancellationToken.None);

        var answer = Assert.Single(_answers);
        Assert.Equal(runId, answer.RunId);
        Assert.StartsWith("## Answer", answer.Text);
        Assert.Equal(Question.CommitDate, answer.CommitDate);
        Assert.Single(_sent);
        Assert.Empty(_pendingAnswers.LoadAll());
        Assert.Empty(_pending.LoadAll());
    }

    [Fact]
    public void Truncate_CapsTheAnswer_WithoutSplittingACharacter()
    {
        Assert.Equal("short", RunUsageCollector.Truncate("short"));

        var cut = RunUsageCollector.MaxAnswerLength - RunUsageCollector.TruncationMarker.Length;
        var truncated = RunUsageCollector.Truncate(new string('a', cut - 1) + "\U0001F600" + new string('b', 100));
        Assert.Equal(new string('a', cut - 1) + RunUsageCollector.TruncationMarker, truncated);
        Assert.True(truncated.Length <= RunUsageCollector.MaxAnswerLength);
    }
}
