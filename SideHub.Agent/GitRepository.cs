using System.Diagnostics;

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
    /// <c>.git/info/exclude</c> unless it is already listed.</summary>
    public async Task ExcludeAsync(string path)
    {
        var relative = Path.GetRelativePath(TopLevel, Path.GetFullPath(path)).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return;
        var pattern = "/" + relative;

        await ExcludeLock.WaitAsync();
        try
        {
            var existing = File.Exists(ExcludeFile) ? await File.ReadAllTextAsync(ExcludeFile) : "";
            if (existing.Split('\n').Any(line => line.Trim() == pattern))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(ExcludeFile)!);
            var separator = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";
            await File.AppendAllTextAsync(ExcludeFile, $"{separator}{pattern}\n");
        }
        finally
        {
            ExcludeLock.Release();
        }
    }

    private static async Task<(int ExitCode, string Output)?> RunGitAsync(string workingDirectory, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            // Read-only queries: never take the index lock away from the user's own git commands.
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

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
