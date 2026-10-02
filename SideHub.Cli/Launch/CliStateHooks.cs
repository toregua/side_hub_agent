using System.Text.Json;
using System.Text.RegularExpressions;

namespace SideHub.Cli.Launch;

/// <summary>
/// The hooks <c>sidehub-cli launch</c> adds to a coding CLI so that it reports what it is doing (working,
/// waiting-input, idle) to the agent, through <c>sidehub-cli cli-state</c>. Only per-invocation options are used:
/// the user's own settings files are never touched, and their hooks keep running.
/// <list type="bullet">
/// <item>claude: <c>--settings &lt;json&gt;</c>, whose hooks merge with those of every settings file
/// (https://code.claude.com/docs/en/hooks, "Hook entries merge across settings levels"). Exec form (<c>args</c>,
/// Claude Code 2.1.139+): no shell, so the same JSON works on bash, cmd and PowerShell hosts.</item>
/// <item>codex: <c>-c notify=[…]</c>, codex's program run on <c>agent-turn-complete</c> with the event as its last
/// argument (https://learn.chatgpt.com/docs/config-file/config-advanced). It replaces a <c>notify</c> of the user's
/// config, so it is only added when the user has none. Codex hooks would also report turn starts, but run only
/// once the user trusted them in <c>/hooks</c>: not usable from the command line.</item>
/// </list>
/// gemini and copilot read hooks from settings files only: they report no state.
/// </summary>
public static partial class CliStateHooks
{
    /// <summary>Seconds Claude Code waits for a hook; the notifier itself gives up after about one.</summary>
    private const int HookTimeoutSeconds = 5;

    /// <summary>Claude's notifications that mean a human is needed: a permission dialog, an MCP server asking for
    /// input. <c>idle_prompt</c> (waiting for the next prompt after a while) is not one: the turn already ended.</summary>
    public const string WaitingInputNotifications = "permission_prompt|elicitation_dialog|elicitation_url_dialog";

    /// <summary>
    /// The <c>--settings</c> JSON for claude. UserPromptSubmit and PostToolUse: working (PostToolUse also ends a
    /// permission wait once the tool ran); Notification on a permission or input request, and PreToolUse of
    /// AskUserQuestion: waiting-input; Stop: idle.
    /// </summary>
    /// <param name="program">This program's absolute path (<c>sidehub-cli</c>).</param>
    public static string ClaudeSettings(string program)
    {
        object Hook(string state) => new
        {
            type = "command",
            command = program,
            args = new[] { CliStateCommand.Name, "claude", state },
            timeout = HookTimeoutSeconds,
        };
        object[] Group(string state, string? matcher = null) => matcher is null
            ? [new { hooks = new[] { Hook(state) } }]
            : [new { matcher, hooks = new[] { Hook(state) } }];

        return JsonSerializer.Serialize(new
        {
            hooks = new Dictionary<string, object[]>
            {
                ["UserPromptSubmit"] = Group(CliStateCommand.Working),
                ["PreToolUse"] = Group(CliStateCommand.WaitingInput, "AskUserQuestion"),
                ["PostToolUse"] = Group(CliStateCommand.Working),
                ["Notification"] = Group(CliStateCommand.WaitingInput, WaitingInputNotifications),
                ["Stop"] = Group(CliStateCommand.Idle),
            },
        });
    }

    /// <summary>The <c>-c</c> value for codex: <c>notify=["&lt;program&gt;","cli-state","codex"]</c>, a TOML array
    /// (JSON string escapes are valid TOML basic-string escapes).</summary>
    public static string CodexNotify(string program) =>
        "notify=" + JsonSerializer.Serialize(new[] { program, CliStateCommand.Name, "codex" });

    [GeneratedRegex(@"^\s*(notify|""notify"")\s*=", RegexOptions.Multiline)]
    private static partial Regex TopLevelNotify();

    [GeneratedRegex(@"^\s*\[", RegexOptions.Multiline)]
    private static partial Regex TableHeader();

    /// <summary>Whether the codex config (TOML) sets its own top-level <c>notify</c>, which ours would replace.
    /// Unreadable config: assumed to, so the user's notify is never dropped.</summary>
    public static bool CodexConfigDefinesNotify(string configPath)
    {
        string text;
        try
        {
            if (!File.Exists(configPath))
                return false;
            text = File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        var firstTable = TableHeader().Match(text);
        var topLevel = firstTable.Success ? text[..firstTable.Index] : text;
        return TopLevelNotify().IsMatch(topLevel);
    }

    /// <summary>codex's config.toml: <c>$CODEX_HOME/config.toml</c>, else <c>~/.codex/config.toml</c>.</summary>
    public static string CodexConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome
            ? codexHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(home, "config.toml");
    }
}
