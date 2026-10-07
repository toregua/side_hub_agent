using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// The per-PTY FIFO through which the CLI wrappers report the CLI session id (and title) to the agent.
/// It lives in an agent-owned 0700 folder (<c>.sidehub/run/fifo/</c>), never in a shared one like <c>/tmp</c>: there,
/// another user could create the predictable path first and feed the agent forged events. The folder is refused when it,
/// or a folder up to <c>.sidehub/</c>, is a link or belongs to another user (see <see cref="PrivateFiles"/>). The PTY session id comes
/// from the backend and is part of the path, so only <see cref="IsValidPtySessionId"/> ids may reach it.
/// Every PTY of the user can open every FIFO of that folder (same user, and the folder can be listed): each line must
/// carry the secret of its own PTY (<see cref="SecretVariable"/>), only given to that PTY's environment, so another
/// terminal can't write forged events (a CLI session id pointing at a transcript it wrote, a run step it didn't end).
/// The same applies to the Windows named pipe.
/// </summary>
public static partial class NotifyFifo
{
    /// <summary>The PTY's notification secret, which <c>sidehub-cli</c> joins to every line it writes.</summary>
    public const string SecretVariable = "SIDEHUB_PTY_NOTIFY_SECRET";

    /// <summary>A fresh secret for one PTY: 256 random bits, in hex.</summary>
    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private const uint FifoMode = 0b110_000_000; // 0600

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}\z")] // \z, not $: $ also matches before a trailing newline
    private static partial Regex PtySessionIdPattern();

    /// <summary>Whether a PTY session id is safe to use as a file name and in logs: 1 to 128 letters, digits, '_' or '-'.</summary>
    public static bool IsValidPtySessionId(string? ptySessionId) =>
        ptySessionId is not null && PtySessionIdPattern().IsMatch(ptySessionId);

    /// <summary>Prefix of a Windows named pipe path.</summary>
    public const string PipePrefix = @"\\.\pipe\";

    /// <summary>The Windows named pipe that stands in for the FIFO. Pipe names are machine-wide: the agent's key
    /// keeps agents apart.</summary>
    public static string PipeNameFor(string agentKey, string ptySessionId)
    {
        if (!IsValidPtySessionId(agentKey))
            throw new ArgumentException("Invalid agent key.", nameof(agentKey));
        if (!IsValidPtySessionId(ptySessionId))
            throw new ArgumentException("Invalid PTY session id.", nameof(ptySessionId));
        return $"sidehub-{agentKey}-{ptySessionId}";
    }

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
