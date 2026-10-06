using System.Text;

namespace SideHub.Agent;

/// <summary>
/// The git repository a directory belongs to, queried through the git CLI. Used to keep the files
/// the agent generates (skill files) out of the user's commits: versioned files are never touched,
/// generated ones are listed in <c>.git/info/exclude</c> (local to the clone, never committed).
/// </summary>
public sealed class GitRepository
{
    private static readonly SemaphoreSlim ExcludeLock = new(1, 1);

    public string TopLevel { get; }
    public string ExcludeFile { get; }

    private GitRepository(string topLevel, string excludeFile)
    {
        TopLevel = topLevel;
        ExcludeFile = excludeFile;
    }

    /// <summary>The repository containing <paramref name="directory"/>, or null when it is not in a
    /// git work tree (or git is unavailable).</summary>
    public static async Task<GitRepository?> OpenAsync(string directory)
    {
        var result = await RunGitAsync(directory,
            "rev-parse", "--path-format=absolute", "--show-toplevel", "--git-path", "info/exclude");
        if (result is not { ExitCode: 0 })
            return null;

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 2 ? new GitRepository(lines[0], lines[1]) : null;
    }

    /// <summary>Whether <paramref name="path"/> is versioned. Any answer other than a clear
    /// "not tracked" (git failure, timeout) counts as tracked, so the file is left alone.</summary>
    public async Task<bool> IsTrackedAsync(string path)
    {
        var result = await RunGitAsync(TopLevel, "ls-files", "--error-unmatch", "--", Path.GetFullPath(path));
        return result is not { ExitCode: 1 };
    }

    /// <summary>Adds <paramref name="path"/> (anchored to the repository root) to
    /// <c>.git/info/exclude</c> unless it is already listed. The only write the agent makes under
    /// <c>.git/</c> (protected for <see cref="FileWritePolicy"/>): it goes through
    /// <see cref="ConfinedFile"/> so no link is followed. Returns whether the pattern was added.</summary>
    public async Task<bool> ExcludeAsync(string path)
    {
        var relative = Path.GetRelativePath(TopLevel, Path.GetFullPath(path)).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;
        var pattern = "/" + relative;

        await ExcludeLock.WaitAsync();
        try
        {
            // Relative to the git directory git reported: neither info/ nor exclude may be a link.
            var gitDirectory = Path.GetDirectoryName(Path.GetDirectoryName(ExcludeFile))!;
            var relativeExclude = Path.GetRelativePath(gitDirectory, ExcludeFile);
            var existing = ConfinedFile.ReadAllTextOrNull(gitDirectory, relativeExclude) ?? "";
            if (existing.Split('\n').Any(line => line.Trim() == pattern))
                return false;

            var separator = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";
            await using var file = ConfinedFile.Open(gitDirectory, relativeExclude, FileMode.Append, FileAccess.Write);
            await file.WriteAsync(Encoding.UTF8.GetBytes($"{separator}{pattern}\n"));
            return true;
        }
        finally
        {
            ExcludeLock.Release();
        }
    }

    private static Task<GitCommand.Result?> RunGitAsync(string workingDirectory, params string[] args) =>
        GitCommand.RunAsync(workingDirectory, GitCommand.DefaultTimeout, args);
}
