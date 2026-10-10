using System.Collections.Concurrent;
using System.Text.Json;
using SideHub.Agent.Models;
using SideHub.Agent.Usage;

namespace SideHub.Agent.Review;

/// <summary>
/// Follows the rounds of the works the backend asked to review (a run, a task, then each fix asked for): a photo of
/// the folder when a round starts, another when it ends, and what changed between them sent as
/// <c>review.changes</c>. The photos stay on this machine (refs under <c>refs/sidehub/reviews/</c>, records in
/// <c>.sidehub/run/reviews/</c>); the diff is read from them on demand (<c>review.diff.request</c>).
/// <para>Comparing two photos, never the folder as it is now, keeps a round's diff the same however late it is read,
/// and leaves out what other works changed between two rounds.</para>
/// </summary>
public sealed class WorkReviewTracker
{
    /// <summary>A round started by pty.start holds the terminal's start: the backend waits 10 s for pty.started.</summary>
    public static readonly TimeSpan LaunchSnapshotTimeout = TimeSpan.FromSeconds(8);
    /// <summary>Other photos (end of a round, start of a fix in an open terminal).</summary>
    public static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(30);
    /// <summary>A review's photos and record are deleted this long after its last round.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    /// <summary>The activity of the folder is only needed while rounds can overlap.</summary>
    public static readonly TimeSpan ActivityRetention = TimeSpan.FromDays(7);
    public const int MaxSummaryLength = 4000;

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions RecordJson = new() { WriteIndented = true };

    /// <summary>The CLI's last message in a terminal since a moment (transcript of its sessions), or null.</summary>
    public delegate string? FinalMessageReader(
        string ptySessionId, string cwd, IReadOnlyList<KeyValuePair<string, string>> cliSessions, DateTimeOffset since);

    private readonly string _directory;
    private readonly string _agentKey;
    private readonly WorkReviewActivity _activity;
    private readonly PendingReviewChangesStore _pending;
    private readonly Func<ReviewChangesMessage, CancellationToken, Task<bool>> _trySend;
    private readonly FinalMessageReader _readFinalMessage;
    private readonly Action<string> _log;
    // The round going on in each terminal.
    private readonly ConcurrentDictionary<string, OpenRound> _open = new();
    // CLI sessions started in each terminal (cliSessionId → provider), in order: the last one is the round's CLI.
    private readonly ConcurrentDictionary<string, List<KeyValuePair<string, string>>> _cliSessions = new();
    // Records are read and written by starts, ends and diffs of any terminal.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    // What the FIFO announces is untrusted: a terminal flooding it must not grow this list without bound.
    private const int MaxCliSessionsPerTerminal = 64;

    /// <param name="directory">The agents' shared <c>.sidehub/run/reviews</c> folder.</param>
    /// <param name="agentKey">Names this agent in the folder's activity.</param>
    /// <param name="trySend">Sends a report; false when the backend is unreachable.</param>
    public WorkReviewTracker(
        string directory, string agentKey, PendingReviewChangesStore pending,
        Func<ReviewChangesMessage, CancellationToken, Task<bool>> trySend,
        FinalMessageReader readFinalMessage, Action<string> log)
    {
        _directory = directory;
        _agentKey = agentKey;
        _activity = new WorkReviewActivity(Path.Combine(directory, "activity.jsonl"));
        _pending = pending;
        _trySend = trySend;
        _readFinalMessage = readFinalMessage;
        _log = log;
        CloseInterruptedRounds();
    }

    public sealed record StartResult(int? Round, string? Error);

    /// <summary>
    /// Starts a round of <paramref name="reviewId"/> in a terminal: round 1 for a work this agent has no record of,
    /// the next one for a fix. Without git or a photo, the round still opens and its report carries the error.
    /// </summary>
    /// <param name="kind"><see cref="ReviewKinds"/>; a fix keeps the work's own when null.</param>
    /// <param name="otherCliBusy">A CLI of another terminal is working in the same folder right now.</param>
    public async Task<StartResult> StartRoundAsync(
        Guid reviewId, string ptySessionId, string cwd, string? kind, Guid? runId, Guid? taskId,
        bool otherCliBusy, TimeSpan timeout, CancellationToken ct)
    {
        if (_open.ContainsKey(ptySessionId))
            return new StartResult(null, ReviewDiffErrors.RoundInProgress);

        await _lock.WaitAsync(ct);
        try
        {
            return await StartLockedAsync(reviewId, ptySessionId, cwd, kind, runId, taskId, otherCliBusy, timeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never in the way of the terminal itself: it starts without a review.
            _log($"Review {reviewId} round could not start in PTY {ptySessionId}: {ex.Message}");
            return new StartResult(null, ReviewErrors.Failed);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<StartResult> StartLockedAsync(
        Guid reviewId, string ptySessionId, string cwd, string? kind, Guid? runId, Guid? taskId,
        bool otherCliBusy, TimeSpan timeout, CancellationToken ct)
    {
        {
            var record = Load(reviewId);
            var previous = record?.Rounds.LastOrDefault();
            var round = new WorkReviewRound
            {
                Round = (previous?.Round ?? 0) + 1,
                Kind = kind ?? previous?.Kind ?? ReviewKinds.Task,
                PtySessionId = ptySessionId,
                RunId = runId ?? previous?.RunId,
                TaskId = taskId ?? previous?.TaskId,
                Agent = _agentKey,
                StartedAt = DateTimeOffset.UtcNow,
                Overlapping = otherCliBusy,
            };

            var repository = await GitRepository.OpenAsync(cwd);
            if (repository is null)
                round.Error = ReviewErrors.NotARepository;
            else
            {
                round.StartHead = await WorkReviewGit.HeadAsync(repository.TopLevel, ct);
                round.StartBranch = await WorkReviewGit.BranchAsync(repository.TopLevel, ct);
                var photo = await WorkReviewGit.SnapshotAsync(
                    repository.TopLevel, WorkReviewGit.RefName(reviewId, round.Round, "start"), timeout, ct);
                round.Start = photo.Commit;
                if (round.Start is null)
                {
                    round.Error = ReviewErrors.SnapshotFailed;
                    _log($"Review {reviewId} round {round.Round}: snapshot failed ({photo.Failure})");
                }
            }

            record ??= new WorkReviewRecord { ReviewId = reviewId, TopLevel = repository?.TopLevel ?? cwd };
            if (repository is not null)
                record.TopLevel = repository.TopLevel;
            record.Rounds.Add(round);
            Save(record);

            _open[ptySessionId] = new OpenRound(reviewId, round.Round, record.TopLevel, cwd);
            _activity.Append(new WorkReviewActivity.Entry(reviewId, round.Round, _agentKey, WorkReviewActivity.Start, round.StartedAt));
            _log(round.Error is null
                ? $"Review {reviewId} round {round.Round} started in PTY {ptySessionId} from {Short(round.Start)}"
                : $"Review {reviewId} round {round.Round} started in PTY {ptySessionId} without a photo ({round.Error})");
            return new StartResult(round.Round, round.Error);
        }
    }

    public bool HasOpenRound(string ptySessionId) => _open.ContainsKey(ptySessionId);

    /// <summary>The terminal could not start after all: its round is dropped without a report.</summary>
    public void Abandon(string ptySessionId)
    {
        _cliSessions.TryRemove(ptySessionId, out _);
        if (_open.TryRemove(ptySessionId, out var open))
            TryAppendActivity(open.ReviewId, open.Round, WorkReviewActivity.End, DateTimeOffset.UtcNow);
    }

    public void RecordCliSession(string ptySessionId, string provider, string cliSessionId)
    {
        var sessions = _cliSessions.GetOrAdd(ptySessionId, _ => []);
        lock (sessions)
        {
            sessions.RemoveAll(s => s.Key == cliSessionId);
            if (sessions.Count >= MaxCliSessionsPerTerminal)
                sessions.RemoveAt(0);
            sessions.Add(new(cliSessionId, provider));
        }
    }

    /// <summary>A CLI started working in <paramref name="cwd"/>: the rounds of the other terminals of that folder
    /// will say that their changes may include its own.</summary>
    public void NoteCliWorking(string ptySessionId, string cwd)
    {
        foreach (var (otherPty, open) in _open)
            if (otherPty != ptySessionId && IsWithin(open.TopLevel, cwd))
                open.OtherCliBusy = true;
    }

    /// <summary>
    /// Ends the round going on in a terminal: photo of the end, report sent (or kept until the backend is back).
    /// Nothing when no round is going on there, or when it is not of <paramref name="kind"/> (a task's
    /// <c>task done</c> never ends a run's round).
    /// </summary>
    public async Task EndRoundAsync(string ptySessionId, string trigger, string? kind, CancellationToken ct)
    {
        try
        {
            await EndRoundCoreAsync(ptySessionId, trigger, kind, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never in the way of what follows (the run's usage).
            _log($"Review round of PTY {ptySessionId} could not end ({trigger}): {ex.Message}");
        }
    }

    private async Task EndRoundCoreAsync(string ptySessionId, string trigger, string? kind, CancellationToken ct)
    {
        if (!_open.TryGetValue(ptySessionId, out var open))
            return;

        ReviewChangesMessage report;
        await _lock.WaitAsync(ct);
        try
        {
            if (Load(open.ReviewId) is not { } record
                || record.Rounds.FirstOrDefault(r => r.Round == open.Round) is not { } round
                || (kind is not null && round.Kind != kind)
                || !_open.TryRemove(new KeyValuePair<string, OpenRound>(ptySessionId, open)))
                return;

            round.EndedAt = DateTimeOffset.UtcNow;
            round.Overlapping |= open.OtherCliBusy;
            IReadOnlyList<ReviewFileChange> files = [];
            IReadOnlyList<ReviewCommit> commits = [];
            if (round.Error is null)
            {
                round.EndHead = await WorkReviewGit.HeadAsync(record.TopLevel, ct);
                round.EndBranch = await WorkReviewGit.BranchAsync(record.TopLevel, ct);
                var photo = await WorkReviewGit.SnapshotAsync(
                    record.TopLevel, WorkReviewGit.RefName(record.ReviewId, round.Round, "end"), SnapshotTimeout, ct);
                round.End = photo.Commit;
                if (photo.Failure is not null)
                    _log($"Review {record.ReviewId} round {round.Round}: snapshot failed ({photo.Failure})");
                if (round.End is null
                    || await WorkReviewGit.ChangesAsync(record.TopLevel, round.Start!, round.End, ct) is not { } changed)
                    round.Error = ReviewErrors.SnapshotFailed;
                else
                {
                    files = changed;
                    commits = await WorkReviewGit.CommitsAsync(record.TopLevel, round.StartHead, round.EndHead, ct) ?? [];
                }
            }
            round.Overlapping |= _activity.Overlaps(record.ReviewId, round.Round, round.StartedAt, round.EndedAt.Value);
            Save(record);
            _activity.Append(new WorkReviewActivity.Entry(
                record.ReviewId, round.Round, _agentKey, WorkReviewActivity.End, round.EndedAt.Value));

            var sessions = CliSessions(ptySessionId);
            var last = sessions.Count > 0 ? sessions[^1] : (KeyValuePair<string, string>?)null;
            report = new ReviewChangesMessage
            {
                ReviewId = record.ReviewId,
                Round = round.Round,
                Kind = round.Kind,
                PtySessionId = ptySessionId,
                RunId = round.RunId,
                TaskId = round.TaskId,
                Provider = last?.Value,
                CliSessionId = last?.Key,
                StartedAt = round.StartedAt,
                EndedAt = round.EndedAt.Value,
                StartBranch = round.StartBranch,
                StartHead = round.StartHead,
                EndBranch = round.EndBranch,
                EndHead = round.EndHead,
                Files = files.Take(WorkReviewGit.MaxFiles).ToList(),
                FilesTruncated = files.Count > WorkReviewGit.MaxFiles,
                FilesChanged = files.Count,
                Insertions = files.Sum(f => f.Insertions ?? 0),
                Deletions = files.Sum(f => f.Deletions ?? 0),
                Commits = commits.Take(WorkReviewGit.MaxCommits).ToList(),
                CommitsTruncated = commits.Count > WorkReviewGit.MaxCommits,
                Summary = Summary(ptySessionId, open.Cwd, sessions, round.StartedAt),
                Overlapping = round.Overlapping,
                Error = round.Error,
            };
        }
        finally
        {
            _lock.Release();
        }

        _log($"Review {report.ReviewId} round {report.Round} ended ({trigger}): " + (report.Error ??
            $"files={report.FilesChanged}, +{report.Insertions} -{report.Deletions}, commits={report.Commits.Count}" +
            (report.Overlapping ? ", overlapping" : "")));
        await SendOrKeepAsync(report, ct);
        await PurgeIfDueAsync(ct);
    }

    /// <summary>The terminal is gone: forget its CLI sessions (after its round ended).</summary>
    public void ForgetTerminal(string ptySessionId) => _cliSessions.TryRemove(ptySessionId, out _);

    /// <summary>The diff of one round of a review, read from its photos.</summary>
    public async Task<ReviewDiffMessage> DiffAsync(string requestId, Guid reviewId, int round, string? path, CancellationToken ct)
    {
        ReviewDiffMessage Answer(string? diff = null, bool truncated = false, string? error = null) => new()
        {
            RequestId = requestId, ReviewId = reviewId, Round = round, Path = path,
            Diff = diff, Truncated = truncated, Error = error,
        };

        if (path is not null && !WorkReviewGit.IsValidPath(path))
            return Answer(error: ReviewDiffErrors.InvalidPath);

        WorkReviewRecord? record;
        await _lock.WaitAsync(ct);
        try { record = Load(reviewId); }
        finally { _lock.Release(); }

        if (record is null)
            return Answer(error: ReviewDiffErrors.UnknownReview);
        if (record.Rounds.FirstOrDefault(r => r.Round == round) is not { } found)
            return Answer(error: ReviewDiffErrors.UnknownRound);
        if (found.Error is not null)
            return Answer(error: found.Error);
        if (found.Start is null || found.End is null)
            return Answer(error: ReviewDiffErrors.RoundOpen);
        if (!await WorkReviewGit.ExistsAsync(record.TopLevel, found.Start, ct)
            || !await WorkReviewGit.ExistsAsync(record.TopLevel, found.End, ct))
            return Answer(error: ReviewDiffErrors.Purged);

        var diff = await WorkReviewGit.DiffAsync(record.TopLevel, found.Start, found.End, path, ct);
        return Answer(diff.Text, diff.Truncated, diff.Error);
    }

    /// <summary>Sends the reports kept while disconnected; stops at the first failure.</summary>
    public async Task ReplayPendingAsync(CancellationToken ct)
    {
        foreach (var report in _pending.LoadAll(_log))
        {
            if (!await _trySend(report, ct))
                return;
            _pending.Delete(report);
            _log($"Replayed pending review changes {report.ReviewId} round {report.Round}");
        }
    }

    /// <summary>Deletes the reviews (record and photos) whose last round is older than <see cref="Retention"/>, at most
    /// once a day.</summary>
    public async Task PurgeIfDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPurge < PurgeInterval)
            return;
        _lastPurge = now;
        await PurgeAsync(now - Retention, ct);
    }

    public async Task PurgeAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        if (!Directory.Exists(_directory))
            return;
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var reviewId)
                    || Load(reviewId) is not { } record)
                    continue;
                var lastActivity = record.Rounds.Select(r => r.EndedAt ?? r.StartedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
                if (lastActivity >= cutoff || record.Rounds.Any(r => _open.Values.Any(o => o.ReviewId == reviewId && o.Round == r.Round)))
                    continue;
                if (Directory.Exists(record.TopLevel))
                    await WorkReviewGit.DeleteRefsAsync(record.TopLevel, reviewId, ct);
                File.Delete(file);
                _log($"Review {reviewId} purged (last round {lastActivity:yyyy-MM-dd})");
            }
            _activity.Prune(DateTimeOffset.UtcNow - ActivityRetention);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Review purge failed: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// At start: the rounds this agent left open when it stopped (its terminals died with it) are closed, and the
    /// backend is told (<see cref="ReviewErrors.Interrupted"/>) at the next connection.
    /// </summary>
    private void CloseInterruptedRounds()
    {
        try
        {
            if (!Directory.Exists(_directory))
                return;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var reviewId)
                    || Load(reviewId) is not { } record)
                    continue;
                var interrupted = record.Rounds.Where(r => r.EndedAt is null && r.Agent == _agentKey).ToList();
                if (interrupted.Count == 0)
                    continue;
                var now = DateTimeOffset.UtcNow;
                foreach (var round in interrupted)
                {
                    round.EndedAt = now;
                    round.Error ??= ReviewErrors.Interrupted;
                    TryAppendActivity(reviewId, round.Round, WorkReviewActivity.End, now);
                    _pending.Save(new ReviewChangesMessage
                    {
                        ReviewId = reviewId, Round = round.Round, Kind = round.Kind, PtySessionId = round.PtySessionId,
                        RunId = round.RunId, TaskId = round.TaskId, StartedAt = round.StartedAt, EndedAt = now,
                        StartBranch = round.StartBranch, StartHead = round.StartHead, Error = ReviewErrors.Interrupted,
                    });
                    _log($"Review {reviewId} round {round.Round} was interrupted by an agent restart");
                }
                Save(record);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"Could not close the interrupted review rounds: {ex.Message}");
        }
    }

    private void TryAppendActivity(Guid reviewId, int round, string @event, DateTimeOffset at)
    {
        try { _activity.Append(new WorkReviewActivity.Entry(reviewId, round, _agentKey, @event, at)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Could not write the review activity: {ex.Message}");
        }
    }

    private string? Summary(string ptySessionId, string cwd, IReadOnlyList<KeyValuePair<string, string>> sessions, DateTimeOffset since)
    {
        try
        {
            if (_readFinalMessage(ptySessionId, cwd, sessions, since) is not { } text)
                return null;
            return text.Length <= MaxSummaryLength ? text : RunUsageCollector.Truncate(text, MaxSummaryLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"Could not read the last message of PTY {ptySessionId}: {ex.Message}");
            return null;
        }
    }

    private List<KeyValuePair<string, string>> CliSessions(string ptySessionId)
    {
        if (!_cliSessions.TryGetValue(ptySessionId, out var sessions))
            return [];
        lock (sessions)
            return [.. sessions];
    }

    private async Task SendOrKeepAsync(ReviewChangesMessage report, CancellationToken ct)
    {
        if (await _trySend(report, ct))
        {
            _pending.Delete(report);
            return;
        }
        _pending.Save(report);
        _log($"Backend unreachable; kept review changes {report.ReviewId} round {report.Round} in {_pending.Directory}");
    }

    private string RecordPath(Guid reviewId) => Path.Combine(_directory, $"{reviewId:N}.json");

    private WorkReviewRecord? Load(Guid reviewId)
    {
        var path = RecordPath(reviewId);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WorkReviewRecord>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log($"Unreadable review record {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private void Save(WorkReviewRecord record)
    {
        PrivateFiles.CreateDirectory(_directory);
        var path = RecordPath(record.ReviewId);
        var tmp = path + ".tmp";
        PrivateFiles.WriteAllText(tmp, JsonSerializer.Serialize(record, RecordJson));
        File.Move(tmp, path, overwrite: true);
    }

    private static bool IsWithin(string topLevel, string path)
    {
        var relative = Path.GetRelativePath(topLevel, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static string Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha ?? "?";

    private sealed class OpenRound(Guid reviewId, int round, string topLevel, string cwd)
    {
        public Guid ReviewId { get; } = reviewId;
        public int Round { get; } = round;
        public string TopLevel { get; } = topLevel;
        public string Cwd { get; } = cwd;
        public volatile bool OtherCliBusy;
    }
}

/// <summary>What the agent keeps about a review on this machine (<c>.sidehub/run/reviews/{reviewId:N}.json</c>).</summary>
public sealed class WorkReviewRecord
{
    public Guid ReviewId { get; set; }
    /// <summary>The repository the photos are in.</summary>
    public string TopLevel { get; set; } = "";
    public List<WorkReviewRound> Rounds { get; set; } = [];
}

public sealed class WorkReviewRound
{
    public int Round { get; set; }
    public string Kind { get; set; } = ReviewKinds.Task;
    public string PtySessionId { get; set; } = "";
    /// <summary>The agent of the round's terminal (several agents share the folder).</summary>
    public string Agent { get; set; } = "";
    public Guid? RunId { get; set; }
    public Guid? TaskId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Photos (snapshot commits) of the start and of the end.</summary>
    public string? Start { get; set; }
    public string? End { get; set; }
    public string? StartHead { get; set; }
    public string? StartBranch { get; set; }
    public string? EndHead { get; set; }
    public string? EndBranch { get; set; }
    public bool Overlapping { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// <c>review.changes</c> reports that could not be sent, one file per round (<c>{reviewId:N}-{round}.json</c>),
/// replayed at the next connection.
/// </summary>
public sealed class PendingReviewChangesStore(string directory) : PendingReportStore<ReviewChangesMessage>(directory)
{
    public void Delete(ReviewChangesMessage report) => Delete(KeyOf(report));

    protected override string KeyOf(ReviewChangesMessage report) => $"{report.ReviewId:N}-{report.Round}";
}
