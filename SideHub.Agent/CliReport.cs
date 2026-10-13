using System.Runtime.InteropServices;

namespace SideHub.Agent;

/// <summary>
/// Prints which coding CLIs the agent will run on this machine, whether they are logged in and how they are paid for.
/// Shown at the end of <c>setup</c> (so the install command ends on it) and by <c>sidehub-agent doctor</c>: new users
/// otherwise look for where to give SideHub a model key, while the agent only runs the CLIs as they are configured.
/// </summary>
public static class CliReport
{
    /// <summary>How to sign each CLI in, run as the user the agent runs as.</summary>
    private static readonly Dictionary<string, string> LoginCommands = new()
    {
        ["claude"] = "claude, then /login",
        ["codex"] = "codex login",
        ["agy"] = "agy",
        ["copilot"] = "copilot, then /login",
        ["gemini"] = "gemini",
    };

    private static readonly bool Ascii = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static async Task<int> PrintAsync(CancellationToken ct)
    {
        Console.WriteLine("[SideHub] Checking the coding CLIs on this machine...");
        var versions = await VersionInfo.GetCliVersionsAsync(ct);
        foreach (var line in Lines(versions, VersionInfo.CachedCliAuth, VersionInfo.CachedCliBilling, VersionInfo.CachedCliAccounts))
            Console.WriteLine(line);
        return 0;
    }

    /// <summary>The report, one line each; pure so that it can be tested.</summary>
    public static IReadOnlyList<string> Lines(
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyDictionary<string, bool>? auth,
        IReadOnlyDictionary<string, CliBilling>? billing,
        IReadOnlyDictionary<string, CliAccount>? accounts)
    {
        if (versions.Count == 0)
            return
            [
                "[SideHub] No coding CLI found for this user. Install one and sign it in (your subscription or your API key), e.g.",
                "            npm i -g @anthropic-ai/claude-code && claude      (then /login)",
                "            npm i -g @openai/codex && codex login",
                "          then run: sidehub-agent restart",
            ];

        var lines = new List<string> { "[SideHub] The agent runs these CLIs with their own sign-in:" };
        var width = versions.Max(v => v.Key.Length + v.Value.Length) + 1;
        var loggedOut = false;
        foreach (var cli in VersionInfo.ProbedClis.Where(versions.ContainsKey))
        {
            var name = $"{cli} {versions[cli]}".PadRight(width);
            bool? loggedIn = auth?.TryGetValue(cli, out var value) == true ? value : null;
            var (mark, state) = loggedIn switch
            {
                true => (Ascii ? "ok" : "✓", "logged in" + PaidWith(cli, billing?.GetValueOrDefault(cli), accounts?.GetValueOrDefault(cli))),
                false => ("!", $"not logged in, a task would stop on its login screen: run {LoginCommands[cli]}"),
                null => ("?", $"login not checked, make sure it is signed in ({LoginCommands[cli]})"),
            };
            loggedOut |= loggedIn == false;
            lines.Add($"  {mark,2} {name}  {state}");
        }
        lines.Add("[SideHub] SideHub never asks for a model key: tasks use these subscriptions or API keys, as configured.");
        if (loggedOut)
            lines.Add("[SideHub] Once signed in, run: sidehub-agent restart");
        return lines;
    }

    private static string PaidWith(string cli, CliBilling? billing, CliAccount? account)
    {
        if (account is { Plan: { } copilotPlan })
            return $", GitHub Copilot plan {copilotPlan}";
        return billing switch
        {
            { Mode: CliBilling.Api } => ", with an API key (billed per use by the provider)",
            { Mode: CliBilling.Subscription } => $", with {SubscriptionName(cli, billing.Plan)}",
            _ => "",
        };
    }

    private static string SubscriptionName(string cli, string? plan) => cli switch
    {
        "claude" => plan is null ? "a Claude subscription" : $"a Claude {char.ToUpperInvariant(plan[0])}{plan[1..]} subscription",
        "codex" => "a ChatGPT subscription",
        _ => "a subscription",
    };
}
