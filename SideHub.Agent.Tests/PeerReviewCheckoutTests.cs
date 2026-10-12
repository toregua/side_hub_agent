using System.Diagnostics;
using SideHub.Agent.Models;
using SideHub.Agent.Review;

namespace SideHub.Agent.Tests;

/// <summary>
/// Peer review: a disposable worktree at the end photo of a reviewed round, with the round's diff and files, in a
/// real repository laid out like an agent folder (<c>.sidehub/</c> ignored) that keeps moving after the round.
/// </summary>
public sealed class PeerReviewCheckoutTests : IDisposable
{
    private static bool GitAvailable => !OperatingSystem.IsWindows() && ExecutableResolver.Resolve("git") is not null;

    private readonly string _root = Directory.CreateTempSubdirectory("sidehub-peer-review-").FullName;
    private readonly List<string> _logs = [];
    private readonly WorkReviewTracker _tracker;
    private string Repo => Path.Combine(_root, "dev");
    private string Qa => Path.Combine(_root, "qa");
    private string ReviewsDirectory => Path.Combine(Qa, "agent-1", "reviews");

    public PeerReviewCheckoutTests()
    {
        _tracker = new WorkReviewTracker(Path.Combine(Repo, ".sidehub", "run", "reviews"), "agent-1",
            new PendingReviewChangesStore(Path.Combine(_root, "pending")), (_, _) => Task.FromResult(true),
            (_, _, _, _) => null, _ => { });
        if (!GitAvailable)
            return;
        Directory.CreateDirectory(Repo);
        Git("init", "-q", "-b", "main");
        File.WriteAllText(RepoPath(".gitignore"), ".sidehub/\n");
        File.WriteAllText(RepoPath("app.txt"), "one\ntwo\n");
        File.WriteAllText(RepoPath("old-name.txt"), string.Join("\n", Enumerable.Range(1, 20)) + "\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "initial");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string RepoPath(string name) => Path.Combine(Repo, name);

    private PeerReviewCheckout Checkout() => new("agent-1", _tracker, _logs.Add, Qa);

    private static string N(Guid id) => id.ToString("N");

    private Task<PreparedCheckout?> PrepareAsync(PeerReviewCheckout checkout, Guid reviewId, Guid runId, int round = 1) =>
        checkout.PrepareAsync(N(reviewId), round.ToString(), runId, $"run-{N(runId)}", CancellationToken.None);

    /// <summary>One reviewed round: a commit (a line added, a rename) and work left uncommitted (a new file).</summary>
    private async Task<Guid> ReviewedRoundAsync(Action? work = null)
    {
        var reviewId = Guid.NewGuid();
        await _tracker.StartRoundAsync(reviewId, "pty-work", Repo, ReviewKinds.Run, Guid.NewGuid(), null,
            otherCliBusy: false, WorkReviewTracker.SnapshotTimeout, CancellationToken.None);
        (work ?? (() =>
        {
            File.AppendAllText(RepoPath("app.txt"), "three\n");
            Git("mv", "old-name.txt", "new-name.txt");
            Git("commit", "-q", "-am", "rename and add");
            File.WriteAllText(RepoPath("added.txt"), "added\n");
        }))();
        await _tracker.EndRoundAsync("pty-work", "exit", null, CancellationToken.None);
        return reviewId;
    }

    private string EndSnapshot(Guid reviewId, int round = 1) =>
        Git("rev-parse", WorkReviewGit.RefName(reviewId, round, "end")).Trim();

    private string ExpectedPath(Guid runId) => PathConfinement.RealPath(Path.Combine(ReviewsDirectory, N(runId)));

    private string[] Worktrees() =>
        Git("worktree", "list", "--porcelain").Split('\n')
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => PathConfinement.RealPath(l["worktree ".Length..]))
            .ToArray();

    [Fact]
    public async Task The_checkout_is_the_end_of_the_round_not_the_folder_as_it_is_now()
    {
        if (!GitAvailable)
            return;
        var reviewId = await ReviewedRoundAsync();
        // The folder moved on after the round (the next task of the queue).
        File.AppendAllText(RepoPath("app.txt"), "later\n");
        File.WriteAllText(RepoPath("later.txt"), "later");
        var status = Git("status", "--porcelain");
        var head = Git("rev-parse", "HEAD");
        var runId = Guid.NewGuid();

        var prepared = await PrepareAsync(Checkout(), reviewId, runId);

        Assert.NotNull(prepared);
        Assert.Equal(ExpectedPath(runId), prepared.Path);
        Assert.Equal(EndSnapshot(reviewId), prepared.CommitSha);
        Assert.Equal(DateTimeOffset.Parse(Git("log", "-1", "--format=%cI", prepared.CommitSha).Trim()), prepared.CommitDate);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(Path.Combine(prepared.Path, "app.txt")));
        Assert.True(File.Exists(Path.Combine(prepared.Path, "added.txt")));
        Assert.True(File.Exists(Path.Combine(prepared.Path, "new-name.txt")));
        Assert.False(File.Exists(Path.Combine(prepared.Path, "old-name.txt")));
        Assert.False(File.Exists(Path.Combine(prepared.Path, "later.txt")));
        Assert.Contains(prepared.Path, Worktrees());
        // The developer's folder is left as it was.
        Assert.Equal(status, Git("status", "--porcelain"));
        Assert.Equal(head, Git("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task The_checkout_holds_the_diff_and_the_files_of_the_round()
    {
        if (!GitAvailable)
            return;
        var reviewId = await ReviewedRoundAsync();
        File.AppendAllText(RepoPath("app.txt"), "later\n");

        var prepared = await PrepareAsync(Checkout(), reviewId, Guid.NewGuid());

        Assert.NotNull(prepared);
        var context = Path.Combine(prepared.Path, PeerReviewCheckout.ContextFolder);
        var diff = File.ReadAllText(Path.Combine(context, PeerReviewCheckout.DiffFile));
        Assert.Contains("+three", diff);
        Assert.Contains("+added", diff);
        Assert.Contains("rename from old-name.txt", diff);
        Assert.DoesNotContain("later", diff);
        var files = File.ReadAllLines(Path.Combine(context, PeerReviewCheckout.FilesFile)).Order().ToArray();
        Assert.Equal(
            ["A\t+1 -0\tadded.txt", "M\t+1 -0\tapp.txt", "R\t+0 -0\tnew-name.txt\t(renamed from old-name.txt)"],
            files);
    }

    [Fact]
    public async Task A_diff_too_large_is_replaced_by_a_note()
    {
        if (!GitAvailable)
            return;
        var reviewId = await ReviewedRoundAsync(() =>
            File.WriteAllText(RepoPath("big.txt"), string.Join("\n", Enumerable.Range(1, WorkReviewGit.MaxDiffLines + 1)) + "\n"));

        var prepared = await PrepareAsync(Checkout(), reviewId, Guid.NewGuid());

        Assert.NotNull(prepared);
        var context = Path.Combine(prepared.Path, PeerReviewCheckout.ContextFolder);
        Assert.Equal(PeerReviewCheckout.TooLargeNote, File.ReadAllText(Path.Combine(context, PeerReviewCheckout.DiffFile)));
        Assert.Equal($"A\t+{WorkReviewGit.MaxDiffLines + 1} -0\tbig.txt\n", File.ReadAllText(Path.Combine(context, PeerReviewCheckout.FilesFile)));
        Assert.True(File.Exists(Path.Combine(prepared.Path, "big.txt")));
    }

    [Fact]
    public async Task A_link_named_like_the_context_folder_in_the_photo_is_not_followed()
    {
        if (!GitAvailable)
            return;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var reviewId = await ReviewedRoundAsync(() =>
            File.CreateSymbolicLink(RepoPath(PeerReviewCheckout.ContextFolder), outside));

        var prepared = await PrepareAsync(Checkout(), reviewId, Guid.NewGuid());

        Assert.NotNull(prepared);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        var context = new DirectoryInfo(Path.Combine(prepared.Path, PeerReviewCheckout.ContextFolder));
        Assert.Null(context.LinkTarget);
        Assert.True(File.Exists(Path.Combine(context.FullName, PeerReviewCheckout.DiffFile)));
    }

    [Fact]
    public async Task The_worktree_is_removed_when_its_terminal_ends()
    {
        if (!GitAvailable)
            return;
        var reviewId = await ReviewedRoundAsync();
        var checkout = Checkout();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        // Two peer reviews of the same round at the same time, each in its own worktree.
        var a = await PrepareAsync(checkout, reviewId, first);
        var b = await PrepareAsync(checkout, reviewId, second);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a.Path, b.Path);

        await checkout.ReleaseAsync($"run-{N(first)}");
        await checkout.ReleaseAsync("terminal-1");

        Assert.False(Directory.Exists(a.Path));
        Assert.DoesNotContain(a.Path, Worktrees());
        Assert.True(Directory.Exists(b.Path));
        Assert.Contains(b.Path, Worktrees());
        Assert.Equal(EndSnapshot(reviewId), Git("rev-parse", WorkReviewGit.RefName(reviewId, 1, "end")).Trim());
    }

    [Fact]
    public async Task Leftovers_of_a_stopped_agent_are_purged_but_not_the_worktrees_in_use()
    {
        if (!GitAvailable)
            return;
        var reviewId = await ReviewedRoundAsync();
        // Left by the previous run of the agent.
        var leftover = await PrepareAsync(Checkout(), reviewId, Guid.NewGuid());
        File.WriteAllText(Path.Combine(ReviewsDirectory, "stray"), "x");
        var checkout = Checkout();
        var inUse = await PrepareAsync(checkout, reviewId, Guid.NewGuid());
        Assert.NotNull(leftover);
        Assert.NotNull(inUse);

        await checkout.PurgeLeftoversAsync(CancellationToken.None);

        Assert.False(Directory.Exists(leftover.Path));
        Assert.DoesNotContain(leftover.Path, Worktrees());
        Assert.False(File.Exists(Path.Combine(ReviewsDirectory, "stray")));
        Assert.True(Directory.Exists(inUse.Path));
        Assert.Contains(inUse.Path, Worktrees());
    }

    [Fact]
    public async Task An_unknown_review_or_round_is_refused()
    {
        if (!GitAvailable)
            return;
        var reviewId = await ReviewedRoundAsync();
        var unknown = Guid.NewGuid();

        Assert.Null(await PrepareAsync(Checkout(), unknown, Guid.NewGuid()));
        Assert.Null(await PrepareAsync(Checkout(), reviewId, Guid.NewGuid(), round: 2));

        Assert.Contains($"Peer review of {N(unknown)} round 1 not started: {ReviewDiffErrors.UnknownReview}", _logs);
        Assert.Contains($"Peer review of {N(reviewId)} round 2 not started: {ReviewDiffErrors.UnknownRound}", _logs);
        AssertNoWorktree();
    }

    [Fact]
    public async Task A_round_still_going_on_is_refused()
    {
        if (!GitAvailable)
            return;
        var reviewId = Guid.NewGuid();
        await _tracker.StartRoundAsync(reviewId, "pty-work", Repo, ReviewKinds.Run, Guid.NewGuid(), null,
            otherCliBusy: false, WorkReviewTracker.SnapshotTimeout, CancellationToken.None);

        Assert.Null(await PrepareAsync(Checkout(), reviewId, Guid.NewGuid()));

        Assert.Contains($"Peer review of {N(reviewId)} round 1 not started: {ReviewDiffErrors.RoundOpen}", _logs);
        AssertNoWorktree();
    }

    [Fact]
    public async Task A_round_whose_photos_were_purged_is_refused()
    {
        if (!GitAvailable)
            return;
        var reviewId = await ReviewedRoundAsync();
        await WorkReviewGit.DeleteRefsAsync(Repo, reviewId, CancellationToken.None);
        Git("gc", "--quiet", "--prune=now");

        Assert.Null(await PrepareAsync(Checkout(), reviewId, Guid.NewGuid()));

        Assert.Contains($"Peer review of {N(reviewId)} round 1 not started: {ReviewDiffErrors.Purged}", _logs);
        AssertNoWorktree();
    }

    [Theory]
    [InlineData("not-a-guid", "1", "invalid review id")]
    [InlineData(null, "1", "invalid review id")]
    [InlineData("0f1e2d3c4b5a69788796a5b4c3d2e1f0", "0", "invalid round")]
    [InlineData("0f1e2d3c4b5a69788796a5b4c3d2e1f0", "-1", "invalid round")]
    [InlineData("0f1e2d3c4b5a69788796a5b4c3d2e1f0", "1/../..", "invalid round")]
    public async Task What_the_backend_sent_is_checked_first(string? reviewId, string round, string reason)
    {
        var prepared = await Checkout().PrepareAsync(reviewId, round, Guid.NewGuid(), "run-x", CancellationToken.None);

        Assert.Null(prepared);
        Assert.Contains(_logs, l => l.EndsWith($"not started: {reason}", StringComparison.Ordinal));
        Assert.False(Directory.Exists(ReviewsDirectory));
    }

    [Fact]
    public async Task A_terminal_that_is_not_a_run_is_refused()
    {
        var reviewId = Guid.NewGuid();

        Assert.Null(await Checkout().PrepareAsync(N(reviewId), "1", runId: null, "terminal-1", CancellationToken.None));

        Assert.Contains($"Peer review of {N(reviewId)} round 1 not started: not a run terminal", _logs);
    }

    private void AssertNoWorktree()
    {
        Assert.Single(Worktrees());
        Assert.True(!Directory.Exists(ReviewsDirectory) || !Directory.EnumerateFileSystemEntries(ReviewsDirectory).Any());
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo(ExecutableResolver.Resolve("git")!)
        {
            WorkingDirectory = Repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com" }.Concat(args))
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }
}
