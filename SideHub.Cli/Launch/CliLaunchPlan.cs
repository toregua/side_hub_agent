namespace SideHub.Cli.Launch;

/// <summary>
/// What <c>sidehub-cli launch</c> runs for a coding CLI, and what it reports to the agent: the CLI session id
/// (so SideHub can later run <c>--resume &lt;id&gt;</c>) or, for codex, which has no pre-set id, its launch.
/// Pure: the arguments are only inspected and extended, never interpreted by a shell.
/// </summary>
public sealed record CliLaunchPlan(string Cli, IReadOnlyList<string> Arguments, string? SessionId, bool ReportLaunch)
{
    public static readonly IReadOnlySet<string> KnownClis = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude", "codex", "gemini", "copilot",
    };

    /// <summary>First gemini release that accepts <c>--session-id</c> (and resumes a session by its UUID).</summary>
    public static readonly Version GeminiSessionIdVersion = new(0, 41, 0);

    // Subcommands that start no conversation: no session id is minted for them.
    private static readonly Dictionary<string, HashSet<string>> NonSessionSubcommands = new(StringComparer.Ordinal)
    {
        ["claude"] = ["auth", "config", "doctor", "install", "mcp", "migrate-installer", "plugin", "plugins", "setup-token", "update", "upgrade"],
        ["copilot"] = ["help", "init", "login", "logout", "mcp", "memories", "plugin", "plugins", "sessions", "update", "version"],
        ["gemini"] = ["extension", "extensions", "hook", "hooks", "mcp", "skill", "skills"],
        ["codex"] = ["a", "app-server", "apply", "archive", "cloud", "completion", "debug", "features", "help", "login", "logout", "mcp", "mcp-server", "sandbox", "unarchive"],
    };

    private static readonly HashSet<string> InfoOptions = ["--help", "-h", "--version", "-v", "-V"];

    // Options through which the caller already chose the session: the launcher leaves it alone.
    private static readonly HashSet<string> SessionOptions = ["--session-id", "--resume", "-r", "--continue", "--session-file", "--connect", "--from-pr"];
    private static readonly HashSet<string> SessionIdOptions = ["--session-id", "--resume", "-r"];

    /// <param name="cli">One of <see cref="KnownClis"/>.</param>
    /// <param name="arguments">The CLI's arguments, as given.</param>
    /// <param name="prompt">Appended as the last argument when set.</param>
    /// <param name="geminiVersion">The installed gemini's version, null when unknown.</param>
    /// <param name="newSessionId">Mints a session UUID.</param>
    public static CliLaunchPlan For(string cli, IReadOnlyList<string> arguments, string? prompt, Version? geminiVersion, Func<Guid> newSessionId)
    {
        if (!KnownClis.Contains(cli))
            throw new ArgumentException($"Unknown CLI '{cli}'.", nameof(cli));

        var args = arguments.ToList();
        var startsConversation = !args.Any(InfoOptions.Contains)
            && !(args.Count > 0 && NonSessionSubcommands[cli].Contains(args[0]));

        string? sessionId = null;
        if (cli == "codex")
        {
            // `codex resume <id>`: the session is known; a new one is found by the agent from the launch.
            if (args.Count >= 2 && args[0] == "resume" && IsSessionId(args[1]))
                sessionId = args[1];
        }
        else if (ChosenSession(args, cli, out var chosen))
        {
            sessionId = chosen;
        }
        else if (startsConversation && AcceptsSessionId(cli, geminiVersion))
        {
            sessionId = newSessionId().ToString("D");
            args.InsertRange(0, ["--session-id", sessionId]);
        }

        if (prompt is not null)
            args.Add(prompt);

        return new CliLaunchPlan(cli, args, sessionId, ReportLaunch: cli == "codex" && startsConversation);
    }

    private static bool AcceptsSessionId(string cli, Version? geminiVersion) =>
        cli != "gemini" || (geminiVersion is not null && geminiVersion >= GeminiSessionIdVersion);

    /// <summary>Whether the arguments already pick the session; <paramref name="sessionId"/> is its UUID when given.</summary>
    private static bool ChosenSession(List<string> args, string cli, out string? sessionId)
    {
        sessionId = null;
        var chosen = false;
        for (var i = 0; i < args.Count; i++)
        {
            var (name, value) = SplitOption(args[i]);
            // claude's -c is --continue; other CLIs give -c another meaning.
            if (!SessionOptions.Contains(name) && !(cli == "claude" && name == "-c"))
                continue;
            chosen = true;
            if (!SessionIdOptions.Contains(name))
                continue;
            value ??= i + 1 < args.Count ? args[i + 1] : null;
            if (IsSessionId(value))
                sessionId = value;
        }
        return chosen;
    }

    private static (string Name, string? Value) SplitOption(string arg)
    {
        var eq = arg.IndexOf('=');
        return arg.StartsWith("--", StringComparison.Ordinal) && eq > 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
    }

    /// <summary>The agent only accepts canonical UUIDs (they end up in file names).</summary>
    public static bool IsSessionId(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out _);
}
