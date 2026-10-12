using System.Globalization;
using System.Text;
using SideHub.Agent.Models;

namespace SideHub.Agent.Review;

/// <summary>
/// Where a peer review run (another CLI reading, read-only, what one round of a reviewed work changed) reads the
/// code: a disposable detached git worktree at <c>~/.sidehub/qa/&lt;agentId&gt;/reviews/&lt;runId:N&gt;</c>, checked
/// out at the photo of the end of that round, with the round's diff and its list of files written in
/// <c>.sidehub-review/</c>. One per run, so two peer reviews can run at the same time, and the folder may have moved
/// on since the round (the next tasks of the queue): the reviewer reads the work as it was.
/// <para>In the developer's repository only the worktree's own metadata (<c>.git/worktrees/</c>) changes. The worktree
/// is removed when the run's terminal ends; what an agent stop left behind is removed at the next connection.</para>
/// </summary>
public sealed class PeerReviewCheckout
{
    public const string ReviewKind = "review";
    public const string ReviewIdKey = "SIDEHUB_REVIEW_ID";
    public const string RoundKey = "SIDEHUB_REVIEW_ROUND";
    /// <summary>The folder of the checkout the reviewer is pointed at.</summary>
    public const string ContextFolder = ".sidehub-review";
    public const string DiffFile = "changes.diff";
    public const string FilesFile = "files.txt";

    /// <summary>Written instead of the diff when it is too large (<see cref="WorkReviewGit.DiffAsync"/>).</summary>
    public const string TooLargeNote =
        "The diff of this round is too large to be included here (over 1 MB or 20,000 changed lines).\n" +
        "Open the files listed in .sidehub-review/files.txt instead: this folder holds them as they were at the end " +
        "of the round. Comparing them with the start of the round is not possible, so focus your review on the " +
        "listed files.\n";

    // The backend waits 60 s for pty.started: the worktree of a large repository must fit in it with the diff.
    private static readonly TimeSpan AddTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(20);

    private readonly string _agentKey;
    private readonly WorkReviewTracker _reviews;
    private readonly Action<string> _log;
    // Preparations, removals and the purge of leftovers never run at the same time: the purge must not take a
    // worktree being created for a leftover.
    private readonly SemaphoreSlim _lock = new(1, 1);
    // The worktree of each peer review terminal still running. Guarded by itself.
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);

    /// <summary>The folder of this agent's peer review worktrees.</summary>
    public string ReviewsDirectory { get; }

    /// <param name="agentKey">The agent's id: each agent of a repository has its own worktrees.</param>
    /// <param name="baseDirectory">Folder of the agents' checkouts; <c>~/.sidehub/qa</c> by default.</param>
    public PeerReviewCheckout(string agentKey, WorkReviewTracker reviews, Action<string> log, string? baseDirectory = null)
    {
        _agentKey = agentKey;
        _reviews = reviews;
        _log = log;
        baseDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sidehub", "qa");
        ReviewsDirectory = Path.Combine(baseDirectory, agentKey, "reviews");
    }

    /// <summary>
    /// Checks out the end of round <paramref name="roundText"/> of review <paramref name="reviewIdText"/> (as the
    /// backend sent them) in a new worktree for <paramref name="runId"/> (null: not a run's terminal, refused), writes the round's diff and files in it, and
    /// keeps it until <see cref="ReleaseAsync"/> of <paramref name="ptySessionId"/>. Null (logged) when it cannot be
    /// prepared: the terminal must not start.
    /// </summary>
    public async Task<PreparedCheckout?> PrepareAsync(
        string? reviewIdText, string? roundText, Guid? runId, string ptySessionId, CancellationToken ct)
    {
        // Both come from the backend and end up in paths and git arguments.
        var round = int.TryParse(roundText, NumberStyles.None, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 0;
        if (!Guid.TryParse(reviewIdText, out var reviewId))
            return Fail(reviewIdText, roundText, "invalid review id");
        if (round == 0)
            return Fail(reviewIdText, roundText, "invalid round");
        string Name() => reviewId.ToString("N");
        if (!IsSafeSegment(_agentKey))
            return Fail(Name(), roundText, "invalid agent id");
        // A worktree per run: the terminal must be a run's.
        if (runId is null)
            return Fail(Name(), roundText, "not a run terminal");

        var found = await _reviews.GetRoundSnapshotsAsync(reviewId, round, ct);
        if (found.Snapshots is not { } snapshots)
            return Fail(Name(), roundText, found.Error ?? "no snapshots");

        try { await _lock.WaitAsync(ct); }
        catch (OperationCanceledException) { return Fail(Name(), roundText, "canceled"); }
        var path = Path.Combine(ReviewsDirectory, runId.Value.ToString("N"));
        var created = false;
        try
        {
            // Agent-owned and private, with no link on the way, so the worktree cannot be redirected.
            PrivateFiles.CreateDirectory(ReviewsDirectory);
            // The same run again (its terminal recreated): it starts over from a clean worktree.
            await RemoveAsync(path, snapshots.TopLevel, ct);

            // --force reuses the registration of a worktree deleted by hand. Never "worktree prune": it would also drop
            // the developer's own worktrees that are only missing for now (an unmounted drive).
            var add = await GitAsync(snapshots.TopLevel, AddTimeout, ct,
                "-c", "advice.detachedHead=false", "worktree", "add", "--quiet", "--force", "--detach", path, snapshots.End);
            created = Directory.Exists(path);
            if (add is not { Succeeded: true })
                return Fail(Name(), roundText, $"git worktree add failed ({FirstLine(add?.Error) ?? "timed out"})");

            var diff = await WorkReviewGit.DiffAsync(snapshots.TopLevel, snapshots.Start, snapshots.End, null, ct);
            if (diff.Error is not null)
                return Fail(Name(), roundText, $"could not read the diff ({diff.Error})");
            if (await WorkReviewGit.ChangesAsync(snapshots.TopLevel, snapshots.Start, snapshots.End, ct) is not { } files)
                return Fail(Name(), roundText, $"could not list the changed files ({ReviewDiffErrors.GitFailed})");
            WriteContext(path, diff.Truncated ? TooLargeNote : diff.Text ?? "", FilesList(files));

            if (await GitAsync(snapshots.TopLevel, LocalTimeout, ct, "log", "-1", "--no-show-signature", "--format=%cI", snapshots.End)
                    is not { Succeeded: true } log
                || !DateTimeOffset.TryParse(log.Output.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var commitDate))
                return Fail(Name(), roundText, "could not read the date of the end snapshot");

            // The real path: the CLI records it as its cwd, and the run's transcript is matched against it.
            var prepared = new PreparedCheckout(PathConfinement.RealPath(path), snapshots.End, commitDate);
            lock (_active)
                _active[ptySessionId] = new Entry(path, snapshots.TopLevel);
            created = false;
            _log($"Peer review of {Name()} round {round}: checkout at {path} ({files.Count} files" +
                 (diff.Truncated ? ", diff too large" : "") + ")");
            return prepared;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(Name(), roundText, ex.Message);
        }
        finally
        {
            // A worktree that will not be used is not left behind.
            if (created)
                await RemoveQuietlyAsync(path, snapshots.TopLevel);
            _lock.Release();
        }
    }

    /// <summary>The peer review run of <paramref name="ptySessionId"/> is over: its worktree goes. Nothing for any
    /// other terminal.</summary>
    public async Task ReleaseAsync(string ptySessionId)
    {
        Entry? entry;
        lock (_active)
        {
            if (!_active.Remove(ptySessionId, out entry))
                return;
        }
        await _lock.WaitAsync();
        try
        {
            await RemoveQuietlyAsync(entry.Path, entry.Repository);
            _log($"Peer review checkout {entry.Path} removed (PTY {ptySessionId} ended)");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes the worktrees of no running terminal (an agent stopped while a peer review ran).</summary>
    public async Task PurgeLeftoversAsync(CancellationToken ct)
    {
        if (!Directory.Exists(ReviewsDirectory))
            return;
        await _lock.WaitAsync(ct);
        try
        {
            HashSet<string> inUse;
            lock (_active)
                inUse = _active.Values.Select(e => Path.GetFileName(e.Path)).ToHashSet(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFileSystemEntries(ReviewsDirectory))
            {
                if (inUse.Contains(Path.GetFileName(path)))
                    continue;
                // The repository is unknown here: the worktree's own .git file leads git to it.
                await RemoveQuietlyAsync(path, repository: null);
                _log($"Peer review checkout {path} removed (left over)");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Peer review checkouts purge failed: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>One line per changed file: status, line counts (or "binary"), path, and the previous path of a renamed
    /// or copied file.</summary>
    public static string FilesList(IReadOnlyList<ReviewFileChange> files)
    {
        var text = new StringBuilder();
        foreach (var file in files)
        {
            var counts = file.Binary || file.Insertions is null || file.Deletions is null
                ? "binary"
                : $"+{file.Insertions} -{file.Deletions}";
            text.Append(file.Status).Append('\t').Append(counts).Append('\t').Append(Printable(file.Path));
            if (file.OldPath is { } old)
                text.Append(file.Status == "C" ? "\t(copied from " : "\t(renamed from ").Append(Printable(old)).Append(')');
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Writes <c>.sidehub-review/</c> in the checkout. The photo may hold an entry of that name (a file, a
    /// link): it goes first, and the writes follow no link.</summary>
    private static void WriteContext(string checkout, string diff, string files)
    {
        var folder = Path.Combine(checkout, ContextFolder);
        var info = new FileInfo(folder);
        if (info.LinkTarget is not null || File.Exists(folder))
            File.Delete(folder);
        else if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        ConfinedFile.WriteAllText(checkout, $"{ContextFolder}/{DiffFile}", diff);
        ConfinedFile.WriteAllText(checkout, $"{ContextFolder}/{FilesFile}", files);
    }

    /// <summary><c>git worktree remove --force</c> (from <paramref name="repository"/>, else from the worktree itself),
    /// then the folder, whatever git said.</summary>
    private static async Task RemoveAsync(string path, string? repository, CancellationToken ct)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null || File.Exists(path))
        {
            File.Delete(path);
            return;
        }
        if (!info.Exists)
            return;
        // Without its .git file, git would look for a repository in the folders above the worktree.
        if (File.Exists(Path.Combine(path, ".git")) && (repository is null || Directory.Exists(repository)))
            await GitAsync(repository ?? path, LocalTimeout, ct, "worktree", "remove", "--force", path);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private async Task RemoveQuietlyAsync(string path, string? repository)
    {
        try { await RemoveAsync(path, repository, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Could not remove the peer review checkout {path}: {ex.Message}");
        }
    }

    private static bool IsSafeSegment(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains('/') && !name.Contains('\\');

    // A path is one line of the list, whatever it holds.
    private static string Printable(string path) => string.Concat(path.Select(c => char.IsControl(c) ? '?' : c));

    private static Task<GitCommand.Result?> GitAsync(string directory, TimeSpan timeout, CancellationToken ct, params string[] args) =>
        GitCommand.RunAsync(directory, timeout, args, ct: ct);

    private static string? FirstLine(string? error) =>
        error?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private PreparedCheckout? Fail(string? reviewId, string? round, string reason)
    {
        _log($"Peer review of {Clip(reviewId)} round {Clip(round)} not started: {reason}");
        return null;
    }

    // What the backend sent, as it may be logged: one short line.
    private static string Clip(string? value) =>
        value is null ? "?" : Printable(value.Length <= 64 ? value : value[..64] + "...");

    private sealed record Entry(string Path, string Repository);
}
