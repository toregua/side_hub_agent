using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>
/// Writes one JSON line to the agent hosting this terminal, through <c>$SIDEHUB_PTY_NOTIFY_FIFO</c>: a FIFO on
/// Linux and macOS, a named pipe (<c>\\.\pipe\…</c>) on Windows. Best-effort: gives up after a second (opening a
/// FIFO without a reader blocks), and does nothing outside a SideHub terminal. Each line carries the terminal's secret
/// (<c>$SIDEHUB_PTY_NOTIFY_SECRET</c>): the agent drops lines without it, so another terminal can't write in this one's name.
/// </summary>
public static class AgentNotifier
{
    public const string ChannelVariable = "SIDEHUB_PTY_NOTIFY_FIFO";
    public const string SecretVariable = "SIDEHUB_PTY_NOTIFY_SECRET";

    private const string PipePrefix = @"\\.\pipe\";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    public static void SessionStarted(string provider, string cliSessionId) =>
        Send(new { @event = "cli-session-started", provider, cliSessionId });

    public static void Launched(string provider, string cwd, int pid) =>
        Send(new { @event = "cli-launched", provider, cwd, pid });

    /// <param name="cliSessionId">The session announced at launch, null when none was (a new codex session).</param>
    public static void Exited(string provider, string? cliSessionId) =>
        Send(new { @event = "cli-exited", provider, cliSessionId });

    /// <param name="state">working, waiting-input or idle (see <see cref="CliStateCommand"/>).</param>
    /// <param name="cliSessionId">The session the CLI's hook reported, null when it gave none.</param>
    public static void State(string provider, string state, string? cliSessionId) =>
        Send(new { @event = "cli-state", provider, state, cliSessionId });

    public static void StepEnded() => Send(new { @event = "run-step-ended" });

    private static void Send(object notification)
    {
        var channel = Environment.GetEnvironmentVariable(ChannelVariable);
        if (string.IsNullOrEmpty(channel))
            return;
        var json = JsonSerializer.SerializeToNode(notification)!.AsObject();
        json["secret"] = Environment.GetEnvironmentVariable(SecretVariable) ?? "";
        var line = Encoding.UTF8.GetBytes(json.ToJsonString() + "\n");

        var write = Task.Run(() =>
        {
            try
            {
                if (channel.StartsWith(PipePrefix, StringComparison.Ordinal))
                {
                    using var pipe = new NamedPipeClientStream(".", channel[PipePrefix.Length..], PipeDirection.Out,
                        PipeOptions.CurrentUserOnly);
                    pipe.Connect(Timeout);
                    pipe.Write(line);
                }
                else if (File.Exists(channel))
                {
                    using var stream = new FileStream(channel, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    stream.Write(line);
                }
            }
            catch { /* agent gone: nothing to notify */ }
        });
        write.Wait(Timeout + TimeSpan.FromMilliseconds(200));
    }
}
