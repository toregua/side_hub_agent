using SideHub.Agent.Models;
using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

public class RunUsageCollectorTests : IDisposable
{
    private const string Cwd = "/work/side_hub";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";
    private readonly ClaudeTemp _claude = new();
    private readonly List<RunUsageMessage> _sent = [];
    private readonly PendingUsageStore _pending;
    private bool _connected = true;

    public RunUsageCollectorTests()
    {
        _pending = new PendingUsageStore(Path.Combine(_claude.Root, "pending-usage"));
    }

    public void Dispose() => _claude.Dispose();

    private RunUsageCollector Collector() => new(
        new Dictionary<string, IUsageHarvester> { ["claude"] = new ClaudeTranscriptHarvester(_claude.Root) },
        _pending,
        (report, _) =>
        {
            if (!_connected) return Task.FromResult(false);
            _sent.Add(report);
            return Task.FromResult(true);
        },
        _ => { });

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
}
