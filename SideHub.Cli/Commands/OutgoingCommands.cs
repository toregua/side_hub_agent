using System.Globalization;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// The commits of an agent's branch that are not pushed yet, with the review each was made in, and pushing them: the
/// agent pushes the head shown here and refuses if its branch moved since (never forced).
/// </summary>
public static class OutgoingCommands
{
    public static async Task<int> ShowAsync(SideHubApiClient client, string[] args, bool json)
    {
        if (AgentOf(client, args, "Usage: sidehub-cli outgoing show [--agent <id>]") is not { } agentId)
            return 1;

        var outgoing = await client.GetOutgoingCommitsAsync(agentId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(outgoing));
            return 0;
        }

        Print(outgoing);
        return 0;
    }

    public static async Task<int> PushAsync(SideHubApiClient client, string[] args, bool json)
    {
        if (AgentOf(client, args, "Usage: sidehub-cli outgoing push [--agent <id>]") is not { } agentId)
            return 1;

        // The push names the head the person saw: read it now, so what is pushed is what this command shows.
        var outgoing = await client.GetOutgoingCommitsAsync(agentId);
        if (Prop(outgoing, "head") is not { } head || Int(outgoing, "ahead") == 0)
        {
            if (json) Console.WriteLine(SideHubApiClient.Serialize(outgoing));
            else Console.WriteLine($"Nothing to push on {Prop(outgoing, "branch") ?? "the agent's branch"}.");
            return 0;
        }

        if (!json)
        {
            var target = $"{Prop(outgoing, "remote") ?? "?"}/{Prop(outgoing, "remoteBranch") ?? Prop(outgoing, "branch") ?? "?"}";
            Console.WriteLine($"Pushing {Int(outgoing, "ahead")} commits of {Prop(outgoing, "branch")} up to {Short(head)} to {target}...");
            var toReview = Items(outgoing, "commits").Count(c => ReviewStatus(c) is "pending" or "fix-requested");
            if (toReview > 0)
                Console.WriteLine($"Note: {toReview} of them are in a review still to read.");
        }

        var result = await client.PushOutgoingCommitsAsync(agentId, head);
        var outcome = Prop(result, "outcome");

        if (json)
            Console.WriteLine(SideHubApiClient.Serialize(result));
        else if (outcome == "pushed")
            Console.WriteLine($"Pushed {Short(head)}.");
        else
        {
            Console.Error.WriteLine($"Push failed: {Explain(outcome)}");
            if (Prop(result, "detail") is { } detail)
                foreach (var line in detail.ReplaceLineEndings("\n").TrimEnd().Split('\n'))
                    Console.Error.WriteLine($"  {line}");
        }
        return outcome == "pushed" ? 0 : 1;
    }

    private static void Print(JsonElement outgoing)
    {
        var online = outgoing.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True;
        Console.WriteLine($"Agent:    {Prop(outgoing, "agentName")} ({Prop(outgoing, "agentId")}), {(online ? "online" : "offline")}");
        if (Prop(outgoing, "branch") is not { } branch)
        {
            Console.WriteLine("Branch:   not reported by the agent yet");
            return;
        }

        var upstream = outgoing.TryGetProperty("hasUpstream", out var u) && u.ValueKind == JsonValueKind.True
            ? $"{Prop(outgoing, "remote")}/{Prop(outgoing, "remoteBranch")}"
            : $"{Prop(outgoing, "remote") ?? "?"} (no upstream yet)";
        Console.WriteLine($"Branch:   {branch} -> {upstream}, {Int(outgoing, "ahead")} ahead, {Int(outgoing, "behind")} behind");
        Console.WriteLine($"Head:     {Prop(outgoing, "head") ?? "-"}");
        Console.WriteLine($"Updated:  {PropDate(outgoing, "updatedAt")}");
        Console.WriteLine($"Queue:    {(IsTrue(outgoing, "queueIdle") ? "idle" : "busy")}");
        Console.WriteLine($"Ready:    {(IsTrue(outgoing, "ready") ? "yes" : "no")}" +
            (outgoing.TryGetProperty("canPush", out var cp) && cp.ValueKind == JsonValueKind.False ? " (this agent's release cannot push: update it)" : ""));
        if (Prop(outgoing, "error") is { } error)
            Console.WriteLine($"Error:    {error}");

        var commits = Items(outgoing, "commits");
        if (commits.Count == 0)
            return;
        Console.WriteLine();
        Console.WriteLine($"{"COMMIT",-10} {"REVIEW",-14} {"SUBJECT"}");
        foreach (var commit in commits)
        {
            var review = commit.TryGetProperty("review", out var r) && r.ValueKind == JsonValueKind.Object
                ? $" (review {Prop(r, "id")})" : "";
            Console.WriteLine($"{Short(Prop(commit, "sha") ?? ""),-10} {ReviewStatus(commit) ?? "no review",-14} {Prop(commit, "subject")}{review}");
        }
        if (IsTrue(outgoing, "commitsTruncated"))
            Console.WriteLine("... more commits");
    }

    private static string Explain(string? outcome) => outcome switch
    {
        "agent-offline" => "the agent is offline.",
        "agent-too-old" => "the agent's release cannot push: update it.",
        "timeout" => "the agent did not answer in time (it may still be pushing: check again with sidehub-cli outgoing show).",
        "invalid-head" => "the head shown is not a commit id.",
        "head-moved" => "the branch moved since it was read: check sidehub-cli outgoing show and push again.",
        "rejected" => "the remote refused it (its branch moved ahead: pull first).",
        null => "no answer.",
        _ => outcome + "."
    };

    private static string? ReviewStatus(JsonElement commit) =>
        commit.TryGetProperty("review", out var r) && r.ValueKind == JsonValueKind.Object ? Prop(r, "status") ?? "-" : null;

    private static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;

    private static string? AgentOf(SideHubApiClient client, string[] args, string usage)
    {
        var agentId = GetOption(args, "--agent") ?? client.DefaultAgentId;
        if (agentId is null)
        {
            Console.Error.WriteLine(usage);
            Console.Error.WriteLine("No agent: pass --agent <id> (SIDEHUB_AGENT_ID is not set in this session).");
        }
        return agentId;
    }

    private static List<JsonElement> Items(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().ToList() : [];

    private static int Int(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool IsTrue(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string PropDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm UTC") : "-";
}
