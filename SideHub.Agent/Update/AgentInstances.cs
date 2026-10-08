using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SideHub.Agent.Update;

/// <summary>A daemon running from a given installation: one per project folder (<c>~/.sidehub/instances.json</c>).</summary>
public sealed record AgentInstance(string Directory, int Pid, bool Supervised);

public static class AgentInstances
{
    /// <summary>
    /// The daemons running from <paramref name="installDirectory"/>. Agents of another installation (another user's,
    /// a second install folder) are left alone: this update neither waits for nor restarts them.
    /// </summary>
    public static List<AgentInstance> Running(string installDirectory)
    {
        var instances = new List<AgentInstance>();
        foreach (var entry in InstanceRegistry.LoadValid())
        {
            var process = new DaemonManager(entry.Directory).GetRunningProcess();
            if (process is null || !RunsFrom(process, installDirectory))
                continue;
            instances.Add(new AgentInstance(entry.Directory, process.Id, AgentService.IsSupervised(entry.Directory)));
        }
        return instances;
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool RunsFrom(Process process, string installDirectory)
    {
        try
        {
            var executable = process.MainModule?.FileName;
            if (executable is null)
                return false;
            // Linux names a replaced executable "<path> (deleted)": it still ran from this folder
            var directory = Path.GetDirectoryName(executable.Replace(" (deleted)", ""));
            return directory is not null && PathsEqual(directory, installDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}

/// <summary>The few POSIX calls .NET does not expose: a graceful SIGINT to a daemon, and leaving the parent's session.</summary>
public static class ProcessSignals
{
    private const int SigInt = 2; // same number on Linux and macOS

    /// <summary>Asks a daemon to stop as Ctrl+C would: it closes its terminals and its connection cleanly.</summary>
    public static bool Interrupt(int pid) => !OperatingSystem.IsWindows() && kill(pid, SigInt) == 0;

    /// <summary>
    /// Starts a new session: a stop of the daemon that launched the updater (its process group, launchd) does not
    /// reach it. Also restores SIGINT, which <c>sh</c> ignores in a background job: the daemons the updater restarts
    /// would inherit that, and the next update could not stop them gracefully.
    /// </summary>
    public static void DetachFromSession()
    {
        if (OperatingSystem.IsWindows())
            return;
        setsid();
        signal(SigInt, IntPtr.Zero); // SIG_DFL
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc")]
    private static extern IntPtr signal(int sig, IntPtr handler);
}
