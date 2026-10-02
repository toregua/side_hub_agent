using System.Text.Json;

namespace SideHub.Agent;

/// <summary>
/// A line read from a PTY's notification FIFO (see <see cref="NotifyFifo"/>), validated. Any process of the terminal
/// can write to the FIFO (its path is in the PTY env), so every field is untrusted: the CLI session id ends up in file
/// paths (<c>&lt;cliSessionId&gt;.jsonl</c>), the pid in <c>/proc</c> lookups, the cwd in rollout matching. Only the
/// shapes the wrappers and <c>sidehub-cli</c> write are accepted.
/// </summary>
public abstract record FifoNotification
{
    /// <summary>Longest line read from the FIFO, in characters: longer lines are discarded whole.</summary>
    public const int MaxLineLength = 8192;

    /// <summary>Longest accepted cwd, in characters (Linux PATH_MAX).</summary>
    public const int MaxCwdLength = 4096;

    /// <summary>CLI providers the wrappers announce, as they spell them.</summary>
    public static readonly IReadOnlySet<string> KnownProviders = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude", "codex", "gemini", "copilot",
    };

    /// <summary><c>sidehub-cli workflow step-complete|step-fail</c>: the step is over, the CLI may stay open.</summary>
    public sealed record RunStepEnded : FifoNotification;

    /// <summary>The codex wrapper: <paramref name="Pid"/> is codex's process, null for wrappers that don't send it.</summary>
    public sealed record CliLaunched(string Provider, string Cwd, int? Pid) : FifoNotification;

    /// <summary>The claude wrapper: the session id it passed (or minted) with <c>--session-id</c> / <c>--resume</c>.</summary>
    public sealed record CliSessionStarted(string Provider, string CliSessionId) : FifoNotification;

    /// <summary><c>sidehub-cli launch</c>, once the CLI it started has exited: <paramref name="CliSessionId"/> is the session
    /// it announced, null when it knew none (a new codex session, found by the agent from the launch).</summary>
    public sealed record CliExited(string Provider, string? CliSessionId) : FifoNotification;

    /// <summary><c>sidehub-cli cli-state</c>, run by the CLI's own hooks: the CLI is now <paramref name="State"/> (one of
    /// <see cref="CliStates.All"/>). <paramref name="CliSessionId"/> is the session the hook reported, null when it gave none.</summary>
    public sealed record CliStateChanged(string Provider, string State, string? CliSessionId) : FifoNotification;

    /// <summary>
    /// A CLI session id is accepted only as a canonical UUID (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>): hex digits
    /// and dashes, so it is safe in a file name.
    /// </summary>
    public static bool IsValidCliSessionId(string? cliSessionId) =>
        cliSessionId is { Length: 36 } && Guid.TryParseExact(cliSessionId, "D", out _);

    /// <summary>An absolute, normalized path (no <c>.</c>/<c>..</c> segment) without control characters.</summary>
    public static bool IsValidCwd(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd) || cwd.Length > MaxCwdLength || cwd.Any(char.IsControl)
            || !Path.IsPathFullyQualified(cwd))
            return false;
        try
        {
            return Path.GetFullPath(cwd) == cwd;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a FIFO line. Returns null with the reason in <paramref name="rejection"/> when the line is not one of
    /// the known events or a field is invalid; the reason never quotes the line.
    /// </summary>
    public static FifoNotification? Parse(string line, out string? rejection)
    {
        rejection = null;
        if (line.Length > MaxLineLength)
        {
            rejection = "line too long";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                rejection = "not a JSON object";
                return null;
            }

            switch (String(root, "event"))
            {
                case "run-step-ended":
                    return new RunStepEnded();

                case "cli-launched":
                {
                    var provider = String(root, "provider");
                    if (!IsKnownProvider(provider, out rejection)) return null;
                    var cwd = String(root, "cwd");
                    if (!IsValidCwd(cwd))
                    {
                        rejection = "invalid cwd";
                        return null;
                    }
                    int? pid = null;
                    if (root.TryGetProperty("pid", out var pidProperty))
                    {
                        if (pidProperty.ValueKind != JsonValueKind.Number || !pidProperty.TryGetInt32(out var p) || p <= 0)
                        {
                            rejection = "invalid pid";
                            return null;
                        }
                        pid = p;
                    }
                    return new CliLaunched(provider!, cwd!, pid);
                }

                case "cli-session-started":
                {
                    var provider = String(root, "provider");
                    if (!IsKnownProvider(provider, out rejection)) return null;
                    var cliSessionId = String(root, "cliSessionId");
                    if (!IsValidCliSessionId(cliSessionId))
                    {
                        rejection = "invalid cliSessionId";
                        return null;
                    }
                    return new CliSessionStarted(provider!, cliSessionId!);
                }

                case "cli-exited":
                {
                    var provider = String(root, "provider");
                    if (!IsKnownProvider(provider, out rejection)) return null;
                    if (!TryOptionalCliSessionId(root, out var cliSessionId))
                    {
                        rejection = "invalid cliSessionId";
                        return null;
                    }
                    return new CliExited(provider!, cliSessionId);
                }

                case "cli-state":
                {
                    var provider = String(root, "provider");
                    if (!IsKnownProvider(provider, out rejection)) return null;
                    var state = String(root, "state");
                    if (state is null || !CliStates.All.Contains(state))
                    {
                        rejection = "unknown state";
                        return null;
                    }
                    if (!TryOptionalCliSessionId(root, out var cliSessionId))
                    {
                        rejection = "invalid cliSessionId";
                        return null;
                    }
                    return new CliStateChanged(provider!, state, cliSessionId);
                }

                default:
                    rejection = "unknown event";
                    return null;
            }
        }
        catch (JsonException)
        {
            rejection = "malformed JSON";
            return null;
        }
    }

    /// <summary>An optional <c>cliSessionId</c>: absent or null is accepted (null), anything else must be a valid id.</summary>
    private static bool TryOptionalCliSessionId(JsonElement root, out string? cliSessionId)
    {
        cliSessionId = null;
        if (!root.TryGetProperty("cliSessionId", out var idProperty) || idProperty.ValueKind == JsonValueKind.Null)
            return true;
        cliSessionId = idProperty.ValueKind == JsonValueKind.String ? idProperty.GetString() : null;
        return IsValidCliSessionId(cliSessionId);
    }

    private static bool IsKnownProvider(string? provider, out string? rejection)
    {
        rejection = provider is not null && KnownProviders.Contains(provider) ? null : "unknown provider";
        return rejection is null;
    }

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
