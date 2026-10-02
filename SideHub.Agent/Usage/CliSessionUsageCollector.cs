using System.Collections.Concurrent;
using SideHub.Agent.Models;

namespace SideHub.Agent.Usage;

/// <summary>
/// Measures the token usage of the CLI sessions started in terminals (a human typing <c>claude</c>, <c>codex</c>…)
/// and reports it as <c>cli-session.usage</c>: a cumulative snapshot of the whole session, which the backend
/// replaces. A session is reported:
/// <list type="bullet">
/// <item>when its CLI exits (<c>cli-exited</c> from <c>sidehub-cli launch</c>) or its PTY goes away, with
/// <c>final</c> set; it is no longer tracked after that;</item>
/// <item>every <see cref="ReportInterval"/> while it is open, only when its files changed since the last report.</item>
/// </list>
/// Runs (<c>run-*</c> PTYs) are left to <see cref="RunUsageCollector"/>: the backend derives their session's usage
/// from <c>run.usage</c>. Reports that cannot be sent are kept in a <see cref="PendingCliSessionUsageStore"/>
/// (the newest per session) and replayed at the next connection.
/// </summary>
public sealed class CliSessionUsageCollector
{
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(10);

    private const string RunPtyPrefix = "run-";
    // What the FIFO announces is untrusted: a terminal flooding it must not grow the tracked sessions without bound.
    private const int MaxOpenSessionsPerPty = 64;

    private readonly IReadOnlyDictionary<string, ICliSessionUsageHarvester> _harvestersByProvider;
    private readonly PendingCliSessionUsageStore _pending;
    private readonly Func<CliSessionUsageMessage, CancellationToken, Task<bool>> _trySend;
    private readonly Action<string> _log;
    // Open sessions by CLI session id: a session resumed in another terminal moves there.
    private readonly ConcurrentDictionary<string, OpenSession> _open = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _unmeasuredProvidersLogged = new(StringComparer.Ordinal);
    // A periodic report and the final one can race: serializing them keeps the final report last.
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="harvestersByProvider">Keyed by the CLI provider reported by the wrappers ("claude", "codex").</param>
    /// <param name="trySend">Sends a report; false when the backend is unreachable.</param>
    public CliSessionUsageCollector(
        IReadOnlyDictionary<string, ICliSessionUsageHarvester> harvestersByProvider,
        PendingCliSessionUsageStore pending,
        Func<CliSessionUsageMessage, CancellationToken, Task<bool>> trySend,
        Action<string> log)
    {
        _harvestersByProvider = new Dictionary<string, ICliSessionUsageHarvester>(harvestersByProvider, StringComparer.OrdinalIgnoreCase);
        _pending = pending;
        _trySend = trySend;
        _log = log;
    }

    public bool IsOpen(string cliSessionId) => _open.ContainsKey(cliSessionId);

    /// <summary>A CLI session announced in a PTY (<c>cli-session-started</c>, or the codex rollout match).</summary>
    /// <param name="cwd">The PTY's directory: where to look for the session's files first.</param>
    public void SessionStarted(string ptySessionId, string provider, string cliSessionId, string cwd)
    {
        if (ptySessionId.StartsWith(RunPtyPrefix, StringComparison.Ordinal))
            return;
        if (!FifoNotification.IsValidCliSessionId(cliSessionId))
            return;
        if (!_harvestersByProvider.ContainsKey(provider))
        {
            if (_unmeasuredProvidersLogged.TryAdd(provider, 0))
                _log($"No usage harvester for {provider}: its CLI sessions report no cli-session.usage");
            return;
        }
        if (!_open.ContainsKey(cliSessionId)
            && _open.Values.Count(s => s.PtySessionId == ptySessionId) >= MaxOpenSessionsPerPty)
        {
            _log($"PTY {ptySessionId} already has {MaxOpenSessionsPerPty} open CLI sessions; {cliSessionId} not measured");
            return;
        }
        _open[cliSessionId] = new OpenSession(ptySessionId, provider, cliSessionId, cwd);
    }

    /// <summary>
    /// The CLI started by <c>sidehub-cli launch</c> exited in this PTY. With its session id, that session is over;
    /// without (a new codex session, whose id the launcher never knew), every open session of that CLI in the PTY.
    /// </summary>
    public Task CliExitedAsync(string ptySessionId, string provider, string? cliSessionId, CancellationToken ct) =>
        FinishAsync(s => s.PtySessionId == ptySessionId
            && (cliSessionId is not null
                ? s.CliSessionId == cliSessionId
                : string.Equals(s.Provider, provider, StringComparison.OrdinalIgnoreCase)),
            "cli-exit", ct);

    /// <summary>The PTY exited or was stopped: whatever ran in it is over.</summary>
    public Task PtyClosedAsync(string ptySessionId, string trigger, CancellationToken ct) =>
        FinishAsync(s => s.PtySessionId == ptySessionId, trigger, ct);

    /// <summary>Reports the open sessions whose files changed since their last report (<c>final=false</c>).</summary>
    public async Task ReportChangedAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var session in _open.Values.ToList())
            {
                // Finished meanwhile: its final report follows, this one would only precede it.
                if (!_open.TryGetValue(session.CliSessionId, out var current) || !ReferenceEquals(current, session))
                    continue;

                var harvester = _harvestersByProvider[session.Provider];
                var fingerprint = await Task.Run(() => FileFingerprint.Of(harvester.SessionFiles(session.Cwd, session.CliSessionId)), ct);
                if (fingerprint is null || fingerprint == session.LastReported)
                    continue;

                if (await HarvestAndSendAsync(session, harvester, "periodic", final: false, ct))
                    session.LastReported = fingerprint;
            }
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
                _pending.Delete(report.CliSessionId);
                _log($"Replayed pending usage for CLI session {report.CliSessionId}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task FinishAsync(Func<OpenSession, bool> ended, string trigger, CancellationToken ct)
    {
        var finished = new List<OpenSession>();
        foreach (var session in _open.Values.Where(ended).ToList())
        {
            // A CLI session moved to another PTY since the snapshot stays open there.
            if (((ICollection<KeyValuePair<string, OpenSession>>)_open).Remove(new(session.CliSessionId, session)))
                finished.Add(session);
        }
        if (finished.Count == 0)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            foreach (var session in finished)
                await HarvestAndSendAsync(session, _harvestersByProvider[session.Provider], trigger, final: true, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <returns>False when nothing was reported (files not found, harvest failed).</returns>
    private async Task<bool> HarvestAndSendAsync(
        OpenSession session, ICliSessionUsageHarvester harvester, string trigger, bool final, CancellationToken ct)
    {
        try
        {
            var models = await Task.Run(() => harvester.HarvestSession(session.Cwd, session.CliSessionId), ct);
            if (models is null)
            {
                _log($"CLI session {session.CliSessionId} usage ({trigger}): no {session.Provider} session file found");
                return false;
            }

            var report = new CliSessionUsageMessage
            {
                PtySessionId = session.PtySessionId,
                CliSessionId = session.CliSessionId,
                Provider = session.Provider,
                Source = harvester.Source,
                CollectedAt = DateTimeOffset.UtcNow,
                Final = final,
                Models = models,
            };
            var tokens = models.Sum(m => m.InputTokens + m.OutputTokens + m.CacheReadTokens + m.CacheWriteTokens);
            _log($"CLI session {session.CliSessionId} usage ({trigger}): source={report.Source}, models={models.Count}, tokens={tokens}, final={final}");
            await SendOrKeepAsync(report, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Usage harvest failed for CLI session {session.CliSessionId} ({trigger}): {ex.Message}");
            return false;
        }
    }

    private async Task SendOrKeepAsync(CliSessionUsageMessage report, CancellationToken ct)
    {
        if (await _trySend(report, ct))
        {
            // An older snapshot kept while disconnected must not overwrite this one at the next replay.
            _pending.Delete(report.CliSessionId);
            return;
        }

        _pending.Save(report);
        _log($"Backend unreachable; kept usage for CLI session {report.CliSessionId} in {_pending.Directory}");
    }

    private sealed class OpenSession(string ptySessionId, string provider, string cliSessionId, string cwd)
    {
        public string PtySessionId { get; } = ptySessionId;
        public string Provider { get; } = provider;
        public string CliSessionId { get; } = cliSessionId;
        public string Cwd { get; } = cwd;

        /// <summary>State of the session's files at the last periodic report; null before the first.</summary>
        public FileFingerprint? LastReported { get; set; }
    }
}

/// <summary>Size and last write of a set of files: equal fingerprints mean nothing was appended or rewritten.</summary>
public sealed record FileFingerprint(int Files, long TotalLength, DateTime LastWriteUtc)
{
    /// <summary>Null when no file exists.</summary>
    public static FileFingerprint? Of(IReadOnlyList<string> paths)
    {
        var infos = paths.Select(p => new FileInfo(p)).Where(f => f.Exists).ToList();
        return infos.Count == 0
            ? null
            : new FileFingerprint(infos.Count, infos.Sum(f => f.Length), infos.Max(f => f.LastWriteTimeUtc));
    }
}
