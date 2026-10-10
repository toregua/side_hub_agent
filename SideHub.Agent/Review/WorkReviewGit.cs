using SideHub.Agent.Models;

namespace SideHub.Agent.Review;

/// <summary>
/// The git side of a work review: photos of a folder (snapshot commits), and what changed between two of them.
/// <para>A photo is a commit of the work tree as it is, uncommitted changes and new untracked files included (not the
/// ignored ones), built in a temporary index: the repository's files, index and branches are left as they were. Only
/// objects and a ref under <c>refs/sidehub/reviews/</c> (which keeps the photo from <c>git gc</c>) are added.</para>
/// </summary>
public static class WorkReviewGit
{
    public const string RefPrefix = "refs/sidehub/reviews/";

    /// <summary>Most files listed in a round's summary; the counts still cover them all.</summary>
    public const int MaxFiles = 200;
    /// <summary>Most commits listed in a round's summary.</summary>
    public const int MaxCommits = 20;
    /// <summary>Largest diff sent to the backend, in characters.</summary>
    public const int MaxDiffLength = 1024 * 1024;
    /// <summary>Above this many changed lines, the diff is not even computed: the front asks file by file.</summary>
    public const int MaxDiffLines = 20_000;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    // The agent's own folders are never part of a photo, even in a repository that forgot to ignore them. Taken out
    // of the index after the add: an exclude pathspec naming an ignored folder makes "git add" fail.
    private static readonly string[] OwnFolders = [".sidehub", ".sidehub-images"];

    // Plain diffs: no external diff program or text conversion configured by the repository, no colors.
    private static readonly string[] DiffOptions = ["--no-ext-diff", "--no-textconv", "--no-color", "-M"];

    /// <summary>A photo's commit, or why there is none (the git step that failed and its first error line).</summary>
    public sealed record Snapshot(string? Commit, string? Failure);

    /// <summary>Takes a photo of the work tree of <paramref name="topLevel"/> and keeps it under
    /// <paramref name="refName"/>. No commit when git failed or took longer than <paramref name="timeout"/>.</summary>
    public static async Task<Snapshot> SnapshotAsync(string topLevel, string refName, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var (tree, failure) = await TreeAsync(topLevel, copyIndex: true, timeout, cts.Token);
            // A copied index git cannot use (a split index…): start from an empty one, slower but complete.
            if (tree is null)
                (tree, failure) = await TreeAsync(topLevel, copyIndex: false, timeout, cts.Token);
            if (tree is null)
                return new Snapshot(null, failure);

            var head = await HeadAsync(topLevel, cts.Token);
            List<string> args =
            [
                // The repository's identity is not needed (and may be unset): the photo is signed by SideHub.
                "-c", "user.name=SideHub", "-c", "user.email=agent@sidehub.io",
                "commit-tree", "--no-gpg-sign", tree, "-m", "SideHub work review snapshot",
            ];
            if (head is not null)
                args.AddRange(["-p", head]);
            var commit = await RunAsync(topLevel, timeout, args, cts.Token);
            if (commit is not { Succeeded: true })
                return new Snapshot(null, Failure("commit-tree", commit));
            var sha = commit.Output.Trim();

            var reference = await RunAsync(topLevel, timeout, ["update-ref", refName, sha], cts.Token);
            return reference is { Succeeded: true }
                ? new Snapshot(sha, null)
                : new Snapshot(null, Failure("update-ref", reference));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Snapshot(null, $"took longer than {timeout.TotalSeconds:0} s");
        }
    }

    /// <summary>"step: first line of git's error", or "step: timed out" when git was killed.</summary>
    private static string Failure(string step, GitCommand.Result? result) =>
        result is null
            ? $"{step}: git unavailable or timed out"
            : $"{step}: {result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {result.ExitCode}"}";

    /// <summary>The tree of the work tree, staged in a temporary index (a copy of the repository's, so unchanged files
    /// are not read again); null when git failed.</summary>
    private static async Task<(string? Tree, string? Failure)> TreeAsync(string topLevel, bool copyIndex, TimeSpan timeout, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"sidehub-review-{Guid.NewGuid():N}.index");
        try
        {
            if (copyIndex)
            {
                var indexPath = await RunAsync(topLevel, timeout, ["rev-parse", "--path-format=absolute", "--git-path", "index"], ct);
                if (indexPath is not { Succeeded: true })
                    return (null, Failure("rev-parse", indexPath));
                var index = indexPath.Output.Trim();
                if (File.Exists(index))
                    File.Copy(index, temporary);
            }

            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = temporary };
            // --ignore-errors: an unreadable file is left out instead of failing the whole photo.
            var add = await RunAsync(topLevel, timeout, ["add", "--all", "--ignore-errors", "--", "."], ct, environment);
            if (add is not { Succeeded: true })
                return (null, Failure("add", add));
            var own = await RunAsync(topLevel, timeout,
                ["rm", "-r", "--cached", "--quiet", "--ignore-unmatch", "--", .. OwnFolders], ct, environment);
            if (own is not { Succeeded: true })
                return (null, Failure("rm", own));
            var tree = await RunAsync(topLevel, timeout, ["write-tree"], ct, environment);
            return tree is { Succeeded: true } ? (tree.Output.Trim(), null) : (null, Failure("write-tree", tree));
        }
        catch (IOException ex)
        {
            return (null, $"temporary index: {ex.Message}");
        }
        finally
        {
            foreach (var file in new[] { temporary, temporary + ".lock" })
                try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    /// <summary>The full hash of HEAD; null before the first commit or when git fails.</summary>
    public static async Task<string?> HeadAsync(string topLevel, CancellationToken ct) =>
        await RunAsync(topLevel, QueryTimeout, ["rev-parse", "--verify", "--quiet", "HEAD"], ct) is { Succeeded: true } head
        && head.Output.Trim() is { Length: > 0 } sha
            ? sha
            : null;

    /// <summary>The checked-out branch; null when HEAD is detached or git fails.</summary>
    public static async Task<string?> BranchAsync(string topLevel, CancellationToken ct) =>
        await RunAsync(topLevel, QueryTimeout, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct) is { Succeeded: true } branch
        && branch.Output.Trim() is { Length: > 0 } name
            ? name
            : null;

    /// <summary>The files changed between two photos, with their line counts; null when git failed.</summary>
    public static async Task<IReadOnlyList<ReviewFileChange>?> ChangesAsync(
        string topLevel, string from, string to, CancellationToken ct)
    {
        if (await RunAsync(topLevel, QueryTimeout, ["diff", .. DiffOptions, "--name-status", "-z", from, to], ct)
                is not { Succeeded: true } nameStatus
            || await RunAsync(topLevel, QueryTimeout, ["diff", .. DiffOptions, "--numstat", "-z", from, to], ct)
                is not { Succeeded: true } numstat)
            return null;

        var counts = ParseNumstat(numstat.Output);
        return ParseNameStatus(nameStatus.Output)
            .Select(file => counts.TryGetValue(file.Path, out var count)
                ? file with { Insertions = count.Insertions, Deletions = count.Deletions, Binary = count.Binary }
                : file)
            .ToList();
    }

    /// <summary>The commits reachable from <paramref name="endHead"/> but not from <paramref name="startHead"/> (all of
    /// them when there was no commit at the start), newest first, at most <see cref="MaxCommits"/> + 1 so the caller
    /// knows when there were more; null when git failed.</summary>
    public static async Task<IReadOnlyList<ReviewCommit>?> CommitsAsync(
        string topLevel, string? startHead, string? endHead, CancellationToken ct)
    {
        if (endHead is null || endHead == startHead)
            return [];
        var range = startHead is null ? endHead : $"{startHead}..{endHead}";
        if (await RunAsync(topLevel, QueryTimeout,
                ["log", "--no-show-signature", "--no-decorate", "-z", "--format=%H%x09%s", $"-n{MaxCommits + 1}", range, "--"], ct)
                is not { Succeeded: true } log)
            return null;
        return log.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.TrimStart('\n').Split('\t', 2))
            .Where(parts => parts[0].Length > 0)
            .Select(parts => new ReviewCommit { Sha = parts[0], Subject = parts.Length > 1 ? parts[1] : "" })
            .ToList();
    }

    public sealed record Diff(string? Text, bool Truncated, string? Error);

    /// <summary>The diff between two photos, of one file when <paramref name="path"/> is given (a path relative to the
    /// repository, taken literally). Too large: <see cref="Diff.Truncated"/> without text.</summary>
    public static async Task<Diff> DiffAsync(string topLevel, string from, string to, string? path, CancellationToken ct)
    {
        string[] pathspec = path is null ? [] : ["--", path];
        if (path is null)
        {
            if (await RunAsync(topLevel, QueryTimeout, ["diff", .. DiffOptions, "--numstat", "-z", from, to], ct)
                    is not { Succeeded: true } numstat)
                return new Diff(null, false, ReviewDiffErrors.GitFailed);
            var lines = ParseNumstat(numstat.Output).Values.Sum(c => (long)(c.Insertions ?? 0) + (c.Deletions ?? 0));
            if (lines > MaxDiffLines)
                return new Diff(null, true, null);
        }

        // --literal-pathspecs: a path like ":(top)…" or "*" names that file, nothing else.
        if (await RunAsync(topLevel, QueryTimeout, ["--literal-pathspecs", "diff", .. DiffOptions, from, to, .. pathspec], ct)
                is not { Succeeded: true } diff)
            return new Diff(null, false, ReviewDiffErrors.GitFailed);
        return diff.Output.Length > MaxDiffLength
            ? new Diff(null, true, null)
            : new Diff(diff.Output, false, null);
    }

    /// <summary>Whether <paramref name="path"/> may name a file of the repository in a diff request: relative, inside it,
    /// no control character.</summary>
    public static bool IsValidPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length <= FifoNotification.MaxCwdLength
        && !path.Any(char.IsControl) && !path.StartsWith('/') && !path.StartsWith('\\') && !Path.IsPathRooted(path)
        && !path.Replace('\\', '/').Split('/').Any(segment => segment == "..");

    /// <summary>Whether a commit given back by the agent's own records still exists in the repository.</summary>
    public static async Task<bool> ExistsAsync(string topLevel, string commit, CancellationToken ct) =>
        await RunAsync(topLevel, QueryTimeout, ["cat-file", "-e", $"{commit}^{{commit}}"], ct) is { Succeeded: true };

    /// <summary>Deletes the refs that keep the photos of a review.</summary>
    public static async Task DeleteRefsAsync(string topLevel, Guid reviewId, CancellationToken ct)
    {
        var prefix = $"{RefPrefix}{reviewId:N}/";
        if (await RunAsync(topLevel, QueryTimeout, ["for-each-ref", "--format=%(refname)", prefix], ct)
                is not { Succeeded: true } refs)
            return;
        foreach (var name in refs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                await RunAsync(topLevel, QueryTimeout, ["update-ref", "-d", name], ct);
    }

    public static string RefName(Guid reviewId, int round, string side) => $"{RefPrefix}{reviewId:N}/{round}/{side}";

    /// <summary><c>git diff --name-status -z</c>: <c>M\0path\0</c>, or <c>R100\0old\0new\0</c> for a rename or copy.</summary>
    public static List<ReviewFileChange> ParseNameStatus(string output)
    {
        var fields = output.Split('\0');
        var files = new List<ReviewFileChange>();
        for (var i = 0; i + 1 < fields.Length; i++)
        {
            var status = fields[i];
            if (status.Length == 0)
                continue;
            if ((status[0] == 'R' || status[0] == 'C') && i + 2 < fields.Length)
            {
                files.Add(new ReviewFileChange { Status = status[..1], OldPath = fields[i + 1], Path = fields[i + 2] });
                i += 2;
            }
            else
            {
                files.Add(new ReviewFileChange { Status = status[..1], Path = fields[i + 1] });
                i += 1;
            }
        }
        return files;
    }

    public readonly record struct LineCount(int? Insertions, int? Deletions, bool Binary);

    /// <summary><c>git diff --numstat -z</c>, keyed by the (new) path: <c>3\t1\tpath\0</c>, or
    /// <c>3\t1\t\0old\0new\0</c> for a rename; a binary file counts <c>-\t-</c>.</summary>
    public static Dictionary<string, LineCount> ParseNumstat(string output)
    {
        var fields = output.Split('\0');
        var counts = new Dictionary<string, LineCount>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length; i++)
        {
            var parts = fields[i].TrimStart('\n').Split('\t', 3);
            if (parts.Length < 3)
                continue;
            var binary = parts[0] == "-" && parts[1] == "-";
            var count = new LineCount(
                int.TryParse(parts[0], out var added) ? added : null,
                int.TryParse(parts[1], out var deleted) ? deleted : null,
                binary);
            if (parts[2].Length > 0)
                counts[parts[2]] = count;
            else if (i + 2 < fields.Length)
            {
                counts[fields[i + 2]] = count;
                i += 2;
            }
        }
        return counts;
    }

    private static Task<GitCommand.Result?> RunAsync(
        string topLevel, TimeSpan timeout, IEnumerable<string> args, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null) =>
        GitCommand.RunAsync(topLevel, timeout, args, ct: ct, environment: environment);
}
