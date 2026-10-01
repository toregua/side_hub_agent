using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// The per-PTY FIFO through which the CLI wrappers report the CLI session id (and title) to the agent.
/// It lives in an agent-owned 0700 folder (<c>.sidehub/run/fifo/</c>), never in a shared one like <c>/tmp</c>: there,
/// another user could create the predictable path first and feed the agent forged events. The PTY session id comes
/// from the backend and is part of the path, so only <see cref="IsValidPtySessionId"/> ids may reach it.
/// </summary>
public static partial class NotifyFifo
{
    private const uint FifoMode = 0b110_000_000; // 0600

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}\z")] // \z, not $: $ also matches before a trailing newline
    private static partial Regex PtySessionIdPattern();

    /// <summary>Whether a PTY session id is safe to use as a file name and in logs: 1 to 128 letters, digits, '_' or '-'.</summary>
    public static bool IsValidPtySessionId(string? ptySessionId) =>
        ptySessionId is not null && PtySessionIdPattern().IsMatch(ptySessionId);

    public static string PathFor(string directory, string ptySessionId)
    {
        if (!IsValidPtySessionId(ptySessionId))
            throw new ArgumentException("Invalid PTY session id.", nameof(ptySessionId));
        return Path.Combine(directory, $"pty-{ptySessionId}.fifo");
    }

    /// <summary>Creates the FIFO (0600) in the folder, created or tightened to 0700. A leftover entry with the same
    /// name is replaced. Returns false with the reason when FIFOs aren't available (Windows) or mkfifo fails.</summary>
    public static bool TryCreate(string directory, string ptySessionId, out string path, out string? error)
    {
        path = PathFor(directory, ptySessionId);
        error = null;
        if (OperatingSystem.IsWindows())
        {
            error = "FIFOs are not supported on Windows";
            return false;
        }

        try
        {
            PrivateFiles.CreateDirectory(directory);
            if (Path.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        try
        {
            if (mkfifo(path, FifoMode) == 0)
                return true;
            error = Marshal.GetLastPInvokeErrorMessage();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = ex.Message;
        }
        return false;
    }

    /// <summary>Removes the FIFO if present. Best effort.</summary>
    public static void Delete(string directory, string ptySessionId)
    {
        try
        {
            var path = PathFor(directory, ptySessionId);
            if (Path.Exists(path))
                File.Delete(path);
        }
        catch { /* ignore */ }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);
}
