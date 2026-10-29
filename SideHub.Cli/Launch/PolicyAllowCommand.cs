using System.Text.Json;

namespace SideHub.Cli.Launch;

/// <summary>
/// <c>sidehub-cli policy allow</c>: a Claude Code PreToolUse hook, installed by <c>sidehub-cli launch</c> (see
/// <see cref="CliStateHooks.ClaudeSettings"/>) on the MCP tools (<see cref="Matcher"/>) of a permissionless claude run
/// without a tool policy (<c>$SIDEHUB_ALLOW_MACHINE_MCP</c>, set by the backend). Such a run starts with
/// <c>--permission-mode default</c> and an allow list that cannot name the machine's own MCP tools (plugins, servers,
/// claude.ai connectors: claude takes no <c>mcp__*</c> wildcard there), so <c>claude -p</c> would refuse them all.
/// The hook allows them, whatever the input, and always exits 0.
/// </summary>
public static class PolicyAllowCommand
{
    public const string Action = "allow";

    /// <summary>"1" when the run may use the machine's MCP tools, set by the backend in the run's PTY.</summary>
    public const string Variable = "SIDEHUB_ALLOW_MACHINE_MCP";

    /// <summary>The tools the hook allows: every MCP tool (a regex over Claude tool names).</summary>
    public const string Matcher = "mcp__.*";

    /// <summary>Seconds Claude Code waits for the hook; it answers at once.</summary>
    public const int HookTimeoutSeconds = 10;

    private static readonly TimeSpan StdinTimeout = TimeSpan.FromSeconds(5);

    public static int Run()
    {
        try
        {
            DrainStdin();
            Console.Out.Write(HookOutput());
        }
        catch
        {
            // Nothing left to tell Claude Code: its own permission flow applies.
        }
        return 0;
    }

    /// <summary>The hook's stdout: the tool call is allowed.</summary>
    public static string HookOutput() => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "PreToolUse",
            permissionDecision = "allow",
            permissionDecisionReason = "SideHub permissionless run",
        },
    });

    /// <summary>Reads the hook input and drops it (Claude Code writes it whole), bounded in time; nothing to read when
    /// stdin is a terminal (run by hand).</summary>
    private static void DrainStdin()
    {
        if (!Console.IsInputRedirected)
            return;
        var drain = Task.Run(() =>
        {
            using var stdin = Console.OpenStandardInput();
            stdin.CopyTo(Stream.Null);
        });
        drain.Wait(StdinTimeout);
    }
}
