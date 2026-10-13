using System.Globalization;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// What waits for someone in the workspace (approvals, works to review, failures, blocked tasks), what runs next, the
/// agents and the latest runs: the cockpit home page, as text. Also who this session's token speaks for.
/// </summary>
public static class CockpitCommands
{
    private const int SectionLimit = 5;

    public static async Task<int> NowAsync(SideHubApiClient client, bool json)
    {
        var now = await client.GetCockpitNowAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(now));
            return 0;
        }

        var attention = Items(now, "attention");
        Console.WriteLine(attention.Count == 0 ? "Nothing needs attention." : $"Needs attention ({attention.Count}):");
        foreach (var item in attention)
        {
            var title = Prop(item, "title") + (Prop(item, "stepName") is { } step ? $" > {step}" : "");
            Console.WriteLine($"  {Prop(item, "kind"),-25} {PropDate(item, "at"),-22} {title}");
            if (Prop(item, "detail") is { } detail)
                Console.WriteLine($"  {"",-25} {"",-22} {OneLine(detail)}");
            if (Changes(item) is { } changes)
                Console.WriteLine($"  {"",-25} {"",-22} {changes}");
            if (Target(item) is { Length: > 0 } target)
                Console.WriteLine($"  {"",-25} {"",-22} {target}");
        }

        var upcoming = Items(now, "upcoming");
        if (upcoming.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Next scheduled runs:");
            foreach (var run in upcoming.Take(SectionLimit))
            {
                var agent = Prop(run, "agentName") ?? "-";
                var kind = run.TryGetProperty("isWorkflow", out var w) && w.ValueKind == JsonValueKind.True ? "workflow" : "prompt";
                Console.WriteLine($"  {PropDate(run, "at"),-22} {Prop(run, "title")} ({kind}, {agent}) scheduler={Prop(run, "scheduledPromptId")}");
            }
        }

        var agents = Items(now, "agents");
        if (agents.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Agents:");
            foreach (var agent in agents)
            {
                var online = agent.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True;
                var since = Prop(agent, "since") is null ? "" : $" since {PropDate(agent, "since")}";
                Console.WriteLine($"  {Prop(agent, "name"),-30} {(online ? "online" : "offline") + since,-34} {Prop(agent, "id")}");
            }
        }

        var recent = Items(now, "recentRuns");
        if (recent.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Latest runs:");
            foreach (var run in recent.Take(SectionLimit))
            {
                var title = (Prop(run, "title") ?? Prop(run, "trigger")) + (Prop(run, "stepName") is { } step ? $" > {step}" : "");
                var status = Prop(run, "status") + (Prop(run, "endReason") is { } reason ? $" ({reason})" : "");
                var id = Prop(run, "runId") is { } runId ? $"run={runId}" : "";
                Console.WriteLine($"  {PropDate(run, "at"),-22} {status,-28} {title} {id}");
            }
        }

        var memory = Items(now, "recentMemory");
        if (memory.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("New in memory:");
            foreach (var page in memory.Take(SectionLimit))
                Console.WriteLine($"  {PropDate(page, "createdAt"),-22} {Prop(page, "title")} ({Prop(page, "id")})");
        }
        return 0;
    }

    public static async Task<int> WhoamiAsync(SideHubApiClient client, bool json)
    {
        var me = await client.GetIdentityAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(me));
            return 0;
        }

        var agent = $"{Prop(me, "agentName")} ({Prop(me, "agentId")})";
        var actingUser = me.TryGetProperty("actingUser", out var u) && u.ValueKind == JsonValueKind.Object ? u : (JsonElement?)null;
        switch (Prop(me, "token"))
        {
            case "run":
                Console.WriteLine($"Acting as agent {agent}, for run {Prop(me, "runId")} launched by SideHub.");
                break;
            case "terminal" when actingUser is { } user && Prop(user, "role") is { } role:
                Console.WriteLine($"Acting as {Prop(user, "email")} ({role}) from a terminal they opened on agent {agent}.");
                break;
            case "terminal" when actingUser is { } user:
                Console.WriteLine($"Terminal opened by {Prop(user, "email")}, who is no longer a member of this workspace: acting as agent {agent}.");
                break;
            case "terminal":
                Console.WriteLine($"Acting as agent {agent}, in a terminal SideHub opened on its own (queued task).");
                break;
            default:
                Console.WriteLine($"Acting as agent {agent}, with its own token.");
                break;
        }
        Console.WriteLine($"Workspace: {Prop(me, "workspaceId")}");
        if (Prop(me, "ptySessionId") is { } pty)
            Console.WriteLine($"Terminal:  {pty}");
        return 0;
    }

    private static List<JsonElement> Items(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];

    private static string? Changes(JsonElement item)
    {
        if (item.TryGetProperty("changes", out var c) && c.ValueKind == JsonValueKind.Object)
            return $"{c.GetProperty("files").GetInt32()} files, +{c.GetProperty("insertions").GetInt32()} -{c.GetProperty("deletions").GetInt32()}";
        if (item.TryGetProperty("push", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            var toReview = p.TryGetProperty("toReview", out var r) ? r.GetInt32() : 0;
            return $"{p.GetProperty("commits").GetInt32()} commits to push on {Prop(p, "branch") ?? "?"}" +
                (toReview > 0 ? $", {toReview} still to review" : "");
        }
        return null;
    }

    /// <summary>The ids of the item's target, as <c>name=id</c> pairs to pass to other commands.</summary>
    private static string Target(JsonElement item)
    {
        if (!item.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object)
            return "";
        return string.Join(" ", target.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => $"{TargetName(p.Name)}={p.Value.GetString()}"));
    }

    private static string TargetName(string property) => property switch
    {
        "workflowExecutionId" => "execution",
        "scheduledPromptId" => "scheduler",
        _ => property.EndsWith("Id", StringComparison.Ordinal) ? property[..^2] : property
    };

    private static string OneLine(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 100 ? line[..97] + "..." : line;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string PropDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm UTC") : "-";
}
