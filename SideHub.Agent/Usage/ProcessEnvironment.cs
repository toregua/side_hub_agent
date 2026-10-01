using System.Text;

namespace SideHub.Agent.Usage;

/// <summary>
/// The environment a process started with, read from <c>/proc/&lt;pid&gt;/environ</c> (Linux only). A pid announced
/// through a PTY's notification FIFO is only trusted if the process carries that PTY's <c>SIDEHUB_PTY_SESSION_ID</c>:
/// otherwise anything in the terminal could point the agent at another PTY's CLI.
/// </summary>
public static class ProcessEnvironment
{
    // Linux caps argv + envp of an exec at a few MB; an honest environment is far below this.
    private const int MaxEnvironBytes = 1 << 20;

    /// <summary>Whether <paramref name="pid"/> started with <c>key=value</c> in its environment. False when the
    /// process is gone, belongs to another user, or <c>/proc</c> is unavailable.</summary>
    public static bool Has(int pid, string key, string value, string procRoot = "/proc")
    {
        if (pid <= 0)
            return false;
        try
        {
            using var stream = File.OpenRead(Path.Combine(procRoot, pid.ToString(), "environ"));
            var buffer = new byte[MaxEnvironBytes];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0)
                read += n;

            var expected = Encoding.UTF8.GetBytes(key + "=" + value);
            foreach (var entry in buffer.AsSpan(0, read).Split((byte)0))
            {
                if (buffer.AsSpan(0, read)[entry].SequenceEqual(expected))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
