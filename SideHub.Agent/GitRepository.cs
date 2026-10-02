using System.Diagnostics;
using System.Text;

namespace SideHub.Agent;

/// <summary>
/// The git repository a directory belongs to, queried through the git CLI. Used to keep the files
/// the agent generates (skill files) out of the user's commits: versioned files are never touched,
/// generated ones are listed in <c>.git/info/exclude</c> (local to the clone, never committed).
/// </summary>
public sealed class GitRepository
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim ExcludeLock = new(1, 1);

    /// <summary>Overrides for settings a repository's own config could use to run a command. The
    /// directory is untrusted (a commit can ship an embedded bare repository whose config sets
    /// <c>core.fsmonitor</c>, run by plain read-only queries); <c>-c</c> wins over every config file,
    /// and <c>safe.bareRepository</c> is only honored from there (not from the repository).</summary>
    private static readonly string[] SafeConfig =
    [
        "-c", "core.fsmonitor=false",
        "-c", "safe.bareRepository=explicit",
        "-c", "core.hooksPath=/dev/null",
    ];

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

        var lines = result.Value.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

    private static async Task<(int ExitCode, string Output)?> RunGitAsync(string workingDirectory, params string[] args)
    {
        // Never a bare "git": it would be looked up in the working directory (the repository) first.
        if (ExecutableResolver.Resolve("git") is not { } gitPath)
            return null;
        try
        {
            var psi = new ProcessStartInfo(gitPath)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // git writes UTF-8 (commit messages, branch names); .NET would decode it with the
                // console code page on Windows.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in SafeConfig.Concat(args))
                psi.ArgumentList.Add(arg);
            // The daemon's environment must not steer git either (GIT_DIR, GIT_CONFIG_PARAMETERS,
            // GIT_CONFIG_COUNT/KEY/VALUE could point it elsewhere or undo the overrides above).
            foreach (var name in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
                psi.Environment.Remove(name);
            // Read-only queries: never take the index lock away from the user's own git commands.
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            using var cts = new CancellationTokenSource(GitTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }
            await stderr;
            return (process.ExitCode, await stdout);
        }
        catch
        {
            return null;
        }
    }
}
