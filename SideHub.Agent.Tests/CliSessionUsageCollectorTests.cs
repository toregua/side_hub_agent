using SideHub.Agent.Models;
using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

public class CliSessionUsageCollectorTests : IDisposable
{
    private const string Cwd = "/work/side_hub";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";
    private readonly ClaudeTemp _claude = new();
    private readonly CodexTemp _codex = new();
    private readonly List<CliSessionUsageMessage> _sent = [];
    private readonly PendingCliSessionUsageStore _pending;
    private bool _connected = true;

    public CliSessionUsageCollectorTests()
    {
        _pending = new PendingCliSessionUsageStore(Path.Combine(_claude.Root, "pending-usage", "cli-sessions"));
    }

    public void Dispose()
    {
        _claude.Dispose();
        _codex.Dispose();
    }

    private CliSessionUsageCollector Collector() => new(
        new Dictionary<string, ICliSessionUsageHarvester>
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
        _ => { });

    private string ClaudeSessionFile => Path.Combine(_claude.Root, "-work-side-hub", SessionId + ".jsonl");

    /// <summary>As if Claude had written one more line (a new turn).</summary>
    private void AppendToSession() =>
        File.AppendAllText(ClaudeSessionFile, "\n{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"more\"}}");

    [Fact]
    public async Task Periodic_ReportsAnOpenSession_OnlyWhenItsFilesChanged()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var collector = Collector();
        collector.SessionStarted("terminal-1", "claude", SessionId, Cwd);

        await collector.ReportChangedAsync(CancellationToken.None);
        var first = Assert.Single(_sent);
        Assert.Equal("terminal-1", first.PtySessionId);
        Assert.Equal(SessionId, first.CliSessionId);
        Assert.Equal("claude", first.Provider);
        Assert.Equal(ClaudeTranscriptHarvester.SourceName, first.Source);
        Assert.False(first.Final);
        Assert.NotEmpty(first.Models);

        await collector.ReportChangedAsync(CancellationToken.None);
        Assert.Single(_sent);

        AppendToSession();
        await collector.ReportChangedAsync(CancellationToken.None);
        Assert.Equal(2, _sent.Count);
        Assert.False(_sent[1].Final);
        Assert.True(collector.IsOpen(SessionId));
    }

    [Fact]
    public async Task Periodic_SkipsASessionWhoseFilesDoNotExistYet()
    {
        var collector = Collector();
        collector.SessionStarted("terminal-1", "claude", SessionId, Cwd);

        await collector.ReportChangedAsync(CancellationToken.None);

        Assert.Empty(_sent);
        Assert.True(collector.IsOpen(SessionId));
    }

    [Fact]
    public async Task CliExit_SendsAFinalReport_EvenUnchanged_AndStopsTrackingTheSession()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var collector = Collector();
        collector.SessionStarted("terminal-1", "claude", SessionId, Cwd);
        await collector.ReportChangedAsync(CancellationToken.None);

        await collector.CliExitedAsync("terminal-1", "claude", SessionId, CancellationToken.None);

        Assert.Equal(2, _sent.Count);
        Assert.True(_sent[1].Final);
        Assert.Equal(_sent[0].Models.Select(m => m.OutputTokens), _sent[1].Models.Select(m => m.OutputTokens));
        Assert.False(collector.IsOpen(SessionId));

        AppendToSession();
        await collector.ReportChangedAsync(CancellationToken.None);
        await collector.PtyClosedAsync("terminal-1", "exit", CancellationToken.None);
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task CliExit_OfAnotherSession_LeavesThisOneOpen()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var collector = Collector();
        collector.SessionStarted("terminal-1", "claude", SessionId, Cwd);

        await collector.CliExitedAsync("terminal-1", "claude", "99999999-2222-3333-4444-555555555555", CancellationToken.None);
        await collector.CliExitedAsync("terminal-2", "claude", SessionId, CancellationToken.None);

        Assert.Empty(_sent);
        Assert.True(collector.IsOpen(SessionId));
    }

    [Fact]
    public async Task CodexExit_WithoutSessionId_FinishesTheCodexSessionsOfThatPty()
    {
        var rollout = _codex.AddRollout(DateTimeOffset.UtcNow, Cwd);
        var codexSession = CodexRolloutHarvester.SessionIdOf(rollout)!;
        _claude.AddSession("-work-side-hub", SessionId);
        var collector = Collector();
        collector.SessionStarted("terminal-1", "codex", codexSession, Cwd);
        collector.SessionStarted("terminal-1", "claude", SessionId, Cwd);

        await collector.CliExitedAsync("terminal-1", "codex", null, CancellationToken.None);

        var report = Assert.Single(_sent);
        Assert.True(report.Final);
        Assert.Equal(codexSession, report.CliSessionId);
        Assert.Equal(CodexRolloutHarvester.SourceName, report.Source);
        Assert.Equal(["gpt-test", "gpt-test-mini"], report.Models.Select(m => m.Model));
        Assert.True(collector.IsOpen(SessionId));
    }

    [Fact]
    public async Task PtyClose_SendsTheFinalReportOfItsSessions()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var collector = Collector();
        collector.SessionStarted("terminal-1", "claude", SessionId, Cwd);

        await collector.PtyClosedAsync("terminal-1", "exit", CancellationToken.None);

        Assert.True(Assert.Single(_sent).Final);
        Assert.False(collector.IsOpen(SessionId));
    }

    [Fact]
    public async Task RunPtys_AreLeftToRunUsage()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var collector = Collector();
        collector.SessionStarted($"run-{Guid.NewGuid():N}", "claude", SessionId, Cwd);

        await collector.ReportChangedAsync(CancellationToken.None);
        await collector.CliExitedAsync("run-1", "claude", SessionId, CancellationToken.None);

        Assert.False(collector.IsOpen(SessionId));
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task CliWithoutHarvester_IsSkipped(string provider)
    {
        var collector = Collector();
        collector.SessionStarted("terminal-1", provider, SessionId, Cwd);

        await collector.PtyClosedAsync("terminal-1", "exit", CancellationToken.None);

        Assert.False(collector.IsOpen(SessionId));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Disconnected_KeepsOnlyTheNewestSnapshotOfASession_AndReplaysIt()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var collector = Collector();
        collector.SessionStarted("terminal-1", "claude", SessionId, Cwd);

        _connected = false;
        await collector.ReportChangedAsync(CancellationToken.None);
        Assert.False(Assert.Single(_pending.LoadAll()).Final);

        AppendToSession();
        await collector.CliExitedAsync("terminal-1", "claude", SessionId, CancellationToken.None);
        var queued = Assert.Single(_pending.LoadAll());
        Assert.True(queued.Final);
        Assert.Equal(SessionId, queued.CliSessionId);

        _connected = true;
        await Collector().ReplayPendingAsync(CancellationToken.None);

        var report = Assert.Single(_sent);
        Assert.True(report.Final);
        Assert.Equal("terminal-1", report.PtySessionId);
        Assert.Equal(ClaudeTranscriptHarvester.SourceName, report.Source);
        Assert.NotEmpty(report.Models);
        Assert.Empty(_pending.LoadAll());
    }

    [Fact]
    public async Task SuccessfulSend_DropsAnOlderQueuedSnapshot()
    {
        _claude.AddSession("-work-side-hub", SessionId);
        var collector = Collector();
        collector.SessionStarted("terminal-1", "claude", SessionId, Cwd);

        _connected = false;
        await collector.ReportChangedAsync(CancellationToken.None);
        _connected = true;
        await collector.CliExitedAsync("terminal-1", "claude", SessionId, CancellationToken.None);

        Assert.True(Assert.Single(_sent).Final);
        Assert.Empty(_pending.LoadAll());
    }

    [Fact]
    public void Message_SerializesTheAgreedContract()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new CliSessionUsageMessage
        {
            PtySessionId = "terminal-1",
            CliSessionId = SessionId,
            Provider = "claude",
            Source = ClaudeTranscriptHarvester.SourceName,
            CollectedAt = new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero),
            Final = true,
            Models = [new ModelUsageReport { Model = "m", InputTokens = 1, OutputTokens = 2, Requests = 1 }],
        });

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("cli-session.usage", root.GetProperty("type").GetString());
        Assert.Equal("terminal-1", root.GetProperty("ptySessionId").GetString());
        Assert.Equal(SessionId, root.GetProperty("cliSessionId").GetString());
        Assert.Equal("claude", root.GetProperty("provider").GetString());
        Assert.Equal("claude-transcript", root.GetProperty("source").GetString());
        Assert.True(root.GetProperty("final").GetBoolean());
        Assert.True(root.GetProperty("collectedAt").TryGetDateTimeOffset(out _));
        Assert.Equal(2, root.GetProperty("models")[0].GetProperty("outputTokens").GetInt64());
    }
}
