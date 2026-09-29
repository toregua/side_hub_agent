namespace SideHub.Agent.Usage;

/// <summary>
/// Files a process and its descendants hold open, read from <c>/proc</c> (Linux only). The CLI wrappers
/// <c>exec</c> the npm launcher, which runs the real binary as a child: the file is usually held by a descendant.
/// </summary>
public static class ProcessOpenFiles
{
    public static bool IsSupported(string procRoot = "/proc") => Directory.Exists(Path.Combine(procRoot, "self"));

    /// <summary>
    /// Targets of the open file descriptors of <paramref name="pid"/> and its descendants that match
    /// <paramref name="filter"/>; null when the process is gone.
    /// </summary>
    public static IReadOnlyList<string>? Find(int pid, Func<string, bool> filter, string procRoot = "/proc")
    {
        if (!Directory.Exists(Path.Combine(procRoot, pid.ToString())))
            return null;

        var files = new List<string>();
        foreach (var process in SelfAndDescendants(pid, procRoot))
        {
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(procRoot, process.ToString(), "fd")))
                {
                    if (new FileInfo(fd).LinkTarget is { } target && filter(target) && !files.Contains(target))
                        files.Add(target);
                }
            }
            catch (IOException) { /* exited meanwhile */ }
            catch (UnauthorizedAccessException) { /* another user's process */ }
        }
        return files;
    }

    private static List<int> SelfAndDescendants(int root, string procRoot)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var dir in Directory.EnumerateDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || ParentOf(dir) is not { } ppid)
                continue;
            if (!children.TryGetValue(ppid, out var list))
                children[ppid] = list = [];
            list.Add(pid);
        }

        var result = new List<int> { root };
        for (var i = 0; i < result.Count; i++)
        {
            if (children.TryGetValue(result[i], out var list))
                result.AddRange(list.Where(c => !result.Contains(c)));
        }
        return result;
    }

    /// <summary><c>/proc/&lt;pid&gt;/stat</c>: <c>pid (comm) state ppid …</c>; comm may contain spaces and ')'.</summary>
    private static int? ParentOf(string processDir)
    {
        try
        {
            var stat = File.ReadAllText(Path.Combine(processDir, "stat"));
            var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 1 && int.TryParse(fields[1], out var ppid) ? ppid : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
