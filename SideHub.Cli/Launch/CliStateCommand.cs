using System.Text.Json;

namespace SideHub.Cli.Launch;

/// <summary>
/// <c>sidehub-cli cli-state claude &lt;working|waiting-input|idle&gt;</c> (a Claude Code hook: the hook's JSON on stdin
/// gives the session) and <c>sidehub-cli cli-state codex &lt;event-json&gt;</c> (codex's <c>notify</c> program):
/// tells the agent hosting the terminal what the CLI is doing. Installed by <see cref="CliStateHooks"/>.
/// <para>
/// It runs inside the CLI's turn, so it must never slow it down nor fail it: it writes nothing to stdout (Claude
/// would add it to the conversation on UserPromptSubmit) or stderr, gives up quickly, and always exits 0.
/// </para>
/// </summary>
public static class CliStateCommand
{
    public const string Name = "cli-state";

    public const string Working = "working";
    public const string WaitingInput = "waiting-input";
    public const string Idle = "idle";

    private static readonly HashSet<string> States = [Working, WaitingInput, Idle];

    /// <summary>The hook JSON read from stdin at most (tool results can be large; session_id comes first).</summary>
    public const int MaxHookInputBytes = 256 * 1024;

    private static readonly TimeSpan StdinTimeout = TimeSpan.FromMilliseconds(500);

    public static int Run(string[] args)
    {
        try
        {
            if (Report(args, ReadHookInput) is { } report)
                AgentNotifier.State(report.Provider, report.State, report.CliSessionId);
        }
        catch
        {
            // Never fail the CLI's turn over a status report.
        }
        return 0;
    }

    public sealed record StateReport(string Provider, string State, string? CliSessionId);

    /// <summary>What to report for the arguments, or null when there is nothing to report.</summary>
    /// <param name="hookInput">Reads the hook's stdin (claude only), null when there is none.</param>
    public static StateReport? Report(string[] args, Func<byte[]?> hookInput)
    {
        if (args.Length != 2)
            return null;
        switch (args[0])
        {
            case "claude" when States.Contains(args[1]):
                return new StateReport("claude", args[1], SessionIdFromHookInput(hookInput()));

            // codex runs its notify program with the event as the last argument; only turn ends are reported.
            case "codex":
                return CodexTurnComplete(args[1], out var threadId)
                    ? new StateReport("codex", Idle, threadId)
                    : null;

            default:
                return null;
        }
    }

    /// <summary>The top-level <c>session_id</c> of a Claude Code hook input, when it is a UUID. The input may be cut
    /// short (<see cref="MaxHookInputBytes"/>): what was read is scanned as far as it goes.</summary>
    public static string? SessionIdFromHookInput(byte[]? input)
    {
        if (input is null || input.Length == 0)
            return null;
        try
        {
            var reader = new Utf8JsonReader(input, isFinalBlock: false, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return null;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return null;
                var isSessionId = reader.ValueTextEquals("session_id"u8);
                if (!reader.Read())
                    return null;
                if (isSessionId)
                    return reader.TokenType == JsonTokenType.String && reader.GetString() is { } id && CliLaunchPlan.IsSessionId(id)
                        ? id
                        : null;
                if ((reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) && !reader.TrySkip())
                    return null; // cut short inside a large value before session_id
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether codex's notify argument is an <c>agent-turn-complete</c> event; its <c>thread-id</c> (the
    /// session) when it is a UUID.</summary>
    public static bool CodexTurnComplete(string json, out string? threadId)
    {
        threadId = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "agent-turn-complete")
                return false;
            if (root.TryGetProperty("thread-id", out var thread) && thread.ValueKind == JsonValueKind.String
                && CliLaunchPlan.IsSessionId(thread.GetString()))
                threadId = thread.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The hook input on stdin, bounded in size and time; null when stdin is a terminal (run by hand).</summary>
    private static byte[]? ReadHookInput()
    {
        if (!Console.IsInputRedirected)
            return null;
        var read = Task.Run(() =>
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[MaxHookInputBytes];
            var total = 0;
            int n;
            while (total < buffer.Length && (n = stdin.Read(buffer, total, buffer.Length - total)) > 0)
                total += n;
            return buffer[..total];
        });
        return read.Wait(StdinTimeout) ? read.Result : null;
    }
}
