using System.Diagnostics;

namespace SideHub.Agent.Tests;

public class GitRepositoryTests
{
    private static bool GitAvailable => !OperatingSystem.IsWindows() && ExecutableResolver.Resolve("git") is not null;

    [Fact]
    public async Task An_embedded_bare_repository_cannot_run_its_fsmonitor()
    {
        if (!GitAvailable)
            return;

        // A "bare repository" committed as plain files: git discovers it in this directory and,
        // without overrides, runs core.fsmonitor on ls-files / rev-parse.
        var dir = Directory.CreateTempSubdirectory("sidehub-bare-");
        try
        {
            var marker = Path.Combine(dir.FullName, "pwned");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "objects"));
            Directory.CreateDirectory(Path.Combine(dir.FullName, "refs"));
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "HEAD"), "ref: refs/heads/main\n");
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "config"),
                "[core]\n\trepositoryformatversion = 0\n\tbare = false\n\tworktree = .\n" +
                $"\tfsmonitor = \"touch '{marker}'; false\"\n");

            var repository = await GitRepository.OpenAsync(dir.FullName);
            if (repository is not null)
                await repository.IsTrackedAsync(Path.Combine(dir.FullName, "HEAD"));

            Assert.Null(repository);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_regular_repository_is_still_queried()
    {
        if (!GitAvailable)
            return;

        var dir = Directory.CreateTempSubdirectory("sidehub-repo-");
        try
        {
            Git(dir.FullName, "init", "-q");
            var tracked = Path.Combine(dir.FullName, "tracked.txt");
            var untracked = Path.Combine(dir.FullName, "untracked.txt");
            await File.WriteAllTextAsync(tracked, "x");
            await File.WriteAllTextAsync(untracked, "y");
            Git(dir.FullName, "add", "tracked.txt");

            var repository = await GitRepository.OpenAsync(dir.FullName);

            Assert.NotNull(repository);
            Assert.True(await repository.IsTrackedAsync(tracked));
            Assert.False(await repository.IsTrackedAsync(untracked));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_remote_default_branch_comes_from_origin_HEAD()
    {
        if (!GitAvailable)
            return;

        var dir = Directory.CreateTempSubdirectory("sidehub-clone-");
        try
        {
            var origin = Path.Combine(dir.FullName, "origin");
            var clone = Path.Combine(dir.FullName, "clone");
            Directory.CreateDirectory(origin);
            Git(origin, "init", "-q", "-b", "trunk");
            Git(origin, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "init");
            Git(dir.FullName, "clone", "-q", origin, clone);

            var cloned = await GitRepository.OpenAsync(clone);
            var withoutRemote = await GitRepository.OpenAsync(origin);

            Assert.Equal("trunk", await cloned!.RemoteDefaultBranchAsync());
            Assert.Null(await withoutRemote!.RemoteDefaultBranchAsync());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(ExecutableResolver.Resolve("git")!) { WorkingDirectory = cwd };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
