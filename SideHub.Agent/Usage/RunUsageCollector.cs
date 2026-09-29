using System.Collections.Concurrent;
using SideHub.Agent.Models;

namespace SideHub.Agent.Usage;

/// <summary>
/// Measures the token usage of SideHub-launched runs (<c>run-{runId:N}</c> PTYs) and reports it as
/// <c>run.usage</c>. A run is harvested when its step ends (the CLI may stay open) and again when its
/// PTY exits or is stopped; each report replaces the previous one on the backend. Reports that cannot be
/// sent are kept in a <see cref="PendingUsageStore"/> and replayed at the next connection.
/// </summary>
public sealed class RunUsageCollector
{
    private const string RunPtyPrefix = "run-";
    // A launch outside any tracked run only matters to disambiguate runs started around the same time.
    private static readonly TimeSpan LaunchRetention = TimeSpan.FromHours(24);

    private readonly IReadOnlyDictionary<string, IUsageHarvester> _harvestersByProvider;
    private readonly IUsageHarvester _unavailable = new NullHarvester();
    private readonly PendingUsageStore _pending;
    private readonly Func<RunUsageMessage, CancellationToken, Task<bool>> _trySend;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, TrackedRun> _runs = new();
    // CLI launches in every PTY (runs and interactive terminals), guarded by itself.
    private readonly List<CliLaunch> _launches = [];
    // Harvests of one run can race (step end vs exit); serializing them keeps the latest report last.
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="harvestersByProvider">Keyed by the CLI provider reported by the wrappers ("claude", "codex").</param>
    /// <param name="trySend">Sends a report; false when the backend is unreachable.</param>
    public RunUsageCollector(
        IReadOnlyDictionary<string, IUsageHarvester> harvestersByProvider,
        PendingUsageStore pending,
        Func<RunUsageMessage, CancellationToken, Task<bool>> trySend,
        Action<string> log)
    {
        _harvestersByProvider = new Dictionary<string, IUsageHarvester>(harvestersByProvider, StringComparer.OrdinalIgnoreCase);
        _pending = pending;
        _trySend = trySend;
        _log = log;
    }

    /// <summary>
    /// The run id of a backend-launched run: <c>SIDEHUB_RUN_ID</c> from the pty.start env, else the
    /// <c>run-{runId:N}</c> PTY id. Null for any other PTY (interactive terminals are not measured).
    /// </summary>
    public static Guid? ResolveRunId(string ptySessionId, IReadOnlyDictionary<string, string>? additionalEnv)
    {
        if (!ptySessionId.StartsWith(RunPtyPrefix, StringComparison.Ordinal))
            return null;
        if (additionalEnv is not null
            && additionalEnv.TryGetValue("SIDEHUB_RUN_ID", out var envId)
            && Guid.TryParse(envId, out var fromEnv))
            return fromEnv;
        return Guid.TryParse(ptySessionId[RunPtyPrefix.Length..], out var fromId) ? fromId : null;
    }

    public void TrackRun(string ptySessionId, Guid runId, string cwd) =>
        _runs[ptySessionId] = new TrackedRun(ptySessionId, runId, cwd);

    public bool IsTracked(string ptySessionId) => _runs.ContainsKey(ptySessionId);

    public void RecordCliSession(string ptySessionId, string provider, string cliSessionId)
    {
        if (_runs.TryGetValue(ptySessionId, out var run))
            run.CliSessions[cliSessionId] = provider;
    }

    /// <summary>
    /// Records a CLI started in any PTY. A run's own launches tell which CLI it ran; the others let the
    /// harvester detect a session that could belong to another PTY.
    /// </summary>
    public void RecordCliLaunch(
        string ptySessionId, string provider, string cwd, DateTimeOffset at, LaunchObservation? observation = null)
    {
        lock (_launches)
        {
            _launches.RemoveAll(l => l.At < at - LaunchRetention && !_runs.ContainsKey(l.PtySessionId));
            _launches.Add(new CliLaunch(ptySessionId, provider, cwd, at, observation));
        }
    }

    /// <param name="final">The PTY is gone: stop tracking the run after this report.</param>
    public async Task HarvestAsync(string ptySessionId, string trigger, bool final, CancellationToken ct)
    {
        var found = final ? _runs.TryRemove(ptySessionId, out var run) : _runs.TryGetValue(ptySessionId, out run);
        if (!found || run is null)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            var report = await Task.Run(() => BuildReport(run), ct);
            var tokens = report.Models.Sum(m => m.InputTokens + m.OutputTokens + m.CacheReadTokens + m.CacheWriteTokens);
            _log($"Run {run.RunId} usage ({trigger}): source={report.Source}, models={report.Models.Count}, tokens={tokens}");
            await SendOrKeepAsync(report, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Usage harvest failed for run {run.RunId} ({trigger}): {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sends the reports kept while disconnected; stops at the first failure.</summary>
    public async Task ReplayPendingAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var report in _pending.LoadAll(_log))
            {
                if (!await _trySend(report, ct))
                    return;
                _pending.Delete(report.RunId);
                _log($"Replayed pending usage for run {report.RunId}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private RunUsageMessage BuildReport(TrackedRun run)
    {
        var sessions = run.CliSessions.ToArray();
        CliLaunch[] launches;
        lock (_launches)
            launches = _launches.ToArray();
        bool IsOwn(CliLaunch l) => l.PtySessionId == run.PtySessionId;

        var providers = sessions.Select(s => s.Value)
            .Concat(launches.Where(IsOwn).Select(l => l.Provider))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (!_harvestersByProvider.TryGetValue(provider, out var harvester))
                continue;
            var ids = sessions
                .Where(s => string.Equals(s.Value, provider, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Key)
                .ToList();
            var sameCli = launches
                .Where(l => string.Equals(l.Provider, provider, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var models = harvester.Harvest(new RunUsageContext(
                run.RunId, run.Cwd, ids, sameCli.Where(IsOwn).ToList(), sameCli.Where(l => !IsOwn(l)).ToList()));
            if (models is not null)
                return Report(run.RunId, harvester.Source, models);
        }
        return Report(run.RunId, _unavailable.Source, []);
    }

    private async Task SendOrKeepAsync(RunUsageMessage report, CancellationToken ct)
    {
        if (await _trySend(report, ct))
        {
            // An older report kept while disconnected must not overwrite this one at the next replay.
            _pending.Delete(report.RunId);
            return;
        }

        _pending.Save(report);
        _log($"Backend unreachable; kept usage for run {report.RunId} in {_pending.Directory}");
    }

    private static RunUsageMessage Report(Guid runId, string source, IReadOnlyList<ModelUsageReport> models) => new()
    {
        RunId = runId,
        Source = source,
        CollectedAt = DateTimeOffset.UtcNow,
        Models = models,
    };

    private sealed record TrackedRun(string PtySessionId, Guid RunId, string Cwd)
    {
        /// <summary>cliSessionId → provider, as announced by the CLI wrappers.</summary>
        public ConcurrentDictionary<string, string> CliSessions { get; } = new();
    }
}
