using System.Globalization;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// Runs of the workspace: every execution the backend launched on an agent (workflow step, scheduler, task question,
/// peer review), with how it ended.
/// </summary>
public static class RunCommands
{
    private const int DefaultLimit = 20;

    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var limitArg = GetOption(args, "--limit");
        var pageArg = GetOption(args, "--page");
        var sinceArg = GetOption(args, "--since");
        var untilArg = GetOption(args, "--until");
        var limit = DefaultLimit;
        var page = 1;
        DateTime? since = null, until = null;
        if ((limitArg is not null && (!int.TryParse(limitArg, out limit) || limit < 1))
            || (pageArg is not null && (!int.TryParse(pageArg, out page) || page < 1))
            || (sinceArg is not null && (since = ParseTime(sinceArg)) is null)
            || (untilArg is not null && (until = ParseTime(untilArg)) is null))
        {
            Console.Error.WriteLine("Usage: sidehub-cli run list [--status <status>] [--runtime <cli>] [--agent <id>] [--since <24h|7d|date>] [--until <date>] [--limit N] [--page N]");
            return 1;
        }

        var result = await client.GetRunsAsync(new SideHubApiClient.RunFilter(
            GetOption(args, "--status"), GetOption(args, "--runtime"), GetOption(args, "--agent"),
            since, until, page, limit));

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (!result.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
        {
            Console.WriteLine("No runs found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STATUS",-10} {"END REASON",-17} {"CLI",-8} {"TRIGGER",-14} {"CREATED",-22} {"DURATION"}");
        Console.WriteLine(new string('-', 125));
        foreach (var run in items.EnumerateArray())
        {
            Console.WriteLine($"{Prop(run, "id"),-38} {Prop(run, "status"),-10} {Prop(run, "endReason") ?? "-",-17} " +
                $"{Prop(run, "runtime"),-8} {Prop(run, "trigger"),-14} {PropDate(run, "createdAt"),-22} {Duration(run)}");
        }

        var total = result.TryGetProperty("total", out var t) ? t.GetInt32() : items.GetArrayLength();
        var shown = (page - 1) * limit + items.GetArrayLength();
        if (total > shown)
            Console.WriteLine($"\n{shown} of {total} runs. Next page: --page {page + 1}");
        return 0;
    }

    public static async Task<int> GetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var runId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(runId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli run get <runId>");
            return 1;
        }

        var run = await client.GetRunAsync(runId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(run));
            return 0;
        }

        Console.WriteLine($"Run:        {Prop(run, "id")}");
        Console.WriteLine($"Status:     {Prop(run, "status")}{(Prop(run, "endReason") is { } reason ? $" ({reason})" : "")}");
        if (run.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number)
            Console.WriteLine($"Exit code:  {exit.GetInt32()}");
        Console.WriteLine($"CLI:        {Prop(run, "runtime")}{(Prop(run, "cliVersion") is { } version ? $" {version}" : "")}" +
            $"{(Prop(run, "requestedModel") is { } model ? $", model {model}" : "")}" +
            $"{(run.TryGetProperty("permissionless", out var p) && p.ValueKind == JsonValueKind.True ? ", permissionless" : "")}");
        Console.WriteLine($"Trigger:    {Prop(run, "trigger")}");
        Console.WriteLine($"Agent:      {Prop(run, "agentId")}");
        Console.WriteLine($"Created:    {PropDate(run, "createdAt")}");
        Console.WriteLine($"Started:    {PropDate(run, "startedAt")}");
        Console.WriteLine($"Ended:      {PropDate(run, "endedAt")}");
        Console.WriteLine($"Duration:   {Duration(run)}");
        Console.WriteLine($"Terminal:   {Prop(run, "ptySessionId")}");
        PrintIfSet(run, "cliSessionId", "CLI session");
        PrintIfSet(run, "workflowExecutionId", "Execution");
        PrintIfSet(run, "stepId", "Step");
        PrintIfSet(run, "scheduledPromptExecutionId", "Scheduler execution");
        PrintIfSet(run, "taskId", "Task");
        PrintIfSet(run, "reviewId", "Review");
        PrintIfSet(run, "peerReviewOf", "Peer review of");

        if (run.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine();
            Console.WriteLine("Usage:");
            foreach (var m in models.EnumerateArray())
                Console.WriteLine($"  {Prop(m, "model")}: {Long(m, "inputTokens")} in, {Long(m, "outputTokens")} out, " +
                    $"{Long(m, "cacheReadTokens")} cache read, {Long(m, "cacheWriteTokens")} cache write, {Long(m, "requests")} requests");
        }
        else
        {
            Console.WriteLine("Usage:      not measured");
        }

        if (Prop(run, "errorMessage") is { } error)
        {
            Console.WriteLine();
            Console.WriteLine("Error:");
            foreach (var line in error.Split('\n'))
                Console.WriteLine($"  {line.TrimEnd('\r')}");
        }
        return 0;
    }

    /// <summary>A relative window back from now (<c>30m</c>, <c>24h</c>, <c>7d</c>) or a date, read as UTC.</summary>
    internal static DateTime? ParseTime(string value)
    {
        if (value.Length > 1 && int.TryParse(value[..^1], out var amount) && amount > 0)
        {
            var span = value[^1] switch
            {
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => (TimeSpan?)null
            };
            if (span is { } s)
                return DateTime.UtcNow - s;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
    }

    private static string Duration(JsonElement run)
    {
        if (!run.TryGetProperty("durationMs", out var d) || d.ValueKind != JsonValueKind.Number)
            return "-";
        var span = TimeSpan.FromMilliseconds(d.GetInt64());
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:00}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
            : $"{span.Seconds}s";
    }

    private static void PrintIfSet(JsonElement el, string name, string label)
    {
        if (Prop(el, name) is { } value)
            Console.WriteLine($"{(label + ":").PadRight(12)}{value}");
    }

    private static string Long(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64().ToString("N0", CultureInfo.InvariantCulture) : "0";

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string PropDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm UTC") : "-";
}
