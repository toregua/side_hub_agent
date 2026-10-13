using System.Globalization;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// Usage, costs and reliability of this workspace (the cockpit's Pilotage tab), for a user with the ViewCosts
/// permission, from a terminal they opened.
/// </summary>
public static class CostCommands
{
    private static readonly string[] Groupings = ["day", "workspace", "repository", "agent", "model", "user", "origin"];
    private static readonly string[] Splits = ["origin", "workspace", "provider"];
    /// <summary>Longest range the backend accepts (<c>CockpitEndpoints.MaxRangeDays</c>).</summary>
    private const int MaxRangeDays = 366;

    public static async Task<int> UsageAsync(SideHubApiClient client, string[] args, bool json)
    {
        const string usage = "Usage: sidehub-cli cockpit usage [--since <30d|date>] [--until <date>] " +
            "[--by day|workspace|repository|agent|model|user|origin] [--split origin|workspace|provider] [--tz <IANA zone>]";
        var by = (GetOption(args, "--by") ?? "day").ToLowerInvariant();
        var split = GetOption(args, "--split")?.ToLowerInvariant();
        if (!TryReadRange(args, TimeSpan.FromDays(30), out var from, out var to, out var rangeError)
            || !Groupings.Contains(by) || (split is not null && (!Splits.Contains(split) || by != "day")))
        {
            Console.Error.WriteLine(usage);
            if (rangeError is not null)
                Console.Error.WriteLine(rangeError);
            else if (split is not null && by != "day")
                Console.Error.WriteLine("--split only applies to --by day.");
            return 1;
        }

        var result = await client.GetCockpitUsageAsync(from, to, by, split, GetOption(args, "--tz"));

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var totals = result.GetProperty("totals");
        var previous = result.TryGetProperty("previousTotals", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;
        Console.WriteLine($"Usage from {PropDate(result, "from")} to {PropDate(result, "to")}, by {Prop(result, "groupBy")}" +
            $"{(Prop(result, "split") is { } s ? $", split by {s}" : "")} ({Prop(result, "tz")})");
        Console.WriteLine($"Sessions:        {Count(totals, "sessions")}{Delta(totals, previous, "sessions")}");
        Console.WriteLine($"Tokens:          {Count(Tokens(totals))}{DeltaTokens(totals, previous)}: " +
            $"input {Count(totals, "inputTokens")}, output {Count(totals, "outputTokens")}, " +
            $"cache read {Count(totals, "cacheReadTokens")}, cache write {Count(totals, "cacheWriteTokens")}");
        Console.WriteLine($"Cache hit rate:  {(Number(totals, "cacheHitRate") * 100).ToString("0.0", CultureInfo.InvariantCulture)}%");
        Console.WriteLine($"API cost:        {Usd(totals, "apiCost")}{Delta(totals, previous, "apiCost")}");
        Console.WriteLine($"Subscription eq: {Usd(totals, "subscriptionEquivalent")}{Delta(totals, previous, "subscriptionEquivalent")}  (API price of subscription sessions, not a spend)");
        Console.WriteLine($"Runs:            {Count(totals, "runs")}, {Count(totals, "runsSucceeded")} succeeded");
        Console.WriteLine($"Agents:          {Count(totals, "activeAgents")} active of {Count(totals, "totalAgents")}");
        Console.WriteLine($"Duration:        {Duration((long)Number(totals, "durationMs"))}");
        if (IsTrue(totals, "hasUnknownModels"))
            Console.WriteLine("Costs are lower bounds: some tokens are of a model without a known price (rows marked *).");

        var rows = result.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().ToList() : [];
        if (rows.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No sessions in this range.");
            return 0;
        }

        var labels = result.TryGetProperty("splitLabels", out var l) && l.ValueKind == JsonValueKind.Object
            ? l.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? x.Name)
            : [];
        Console.WriteLine();
        Console.WriteLine($"{by.ToUpperInvariant(),-32} {"SESSIONS",9} {"INPUT",13} {"OUTPUT",13} {"CACHE READ",14} {"CACHE WRITE",13} {"API COST",11} {"SUBSCR. EQ",11}");
        Console.WriteLine(new string('-', 124));
        foreach (var row in rows)
        {
            var label = (Prop(row, "label") ?? Prop(row, "key") ?? "-") + (IsTrue(row, "hasUnknownModels") ? " *" : "");
            Console.WriteLine($"{Cut(label, 32),-32} {Count(row, "sessions"),9} {Count(row, "inputTokens"),13} {Count(row, "outputTokens"),13} " +
                $"{Count(row, "cacheReadTokens"),14} {Count(row, "cacheWriteTokens"),13} {Usd(row, "apiCost"),11} {Usd(row, "subscriptionEquivalent"),11}");
            if (row.TryGetProperty("splits", out var splits) && splits.ValueKind == JsonValueKind.Object)
            {
                foreach (var part in splits.EnumerateObject())
                    Console.WriteLine($"  {labels.GetValueOrDefault(part.Name, part.Name)}: {Count(part.Value, "tokens")} tokens, " +
                        $"API {Usd(part.Value, "apiCost")}, subscr. eq {Usd(part.Value, "subscriptionEquivalent")}");
            }
        }
        return 0;
    }

    public static async Task<int> ReliabilityAsync(SideHubApiClient client, string[] args, bool json)
    {
        if (!TryReadRange(args, TimeSpan.FromDays(7), out var from, out var to, out var rangeError))
        {
            Console.Error.WriteLine("Usage: sidehub-cli cockpit reliability [--since <7d|date>] [--until <date>]");
            if (rangeError is not null)
                Console.Error.WriteLine(rangeError);
            return 1;
        }

        var result = await client.GetCockpitReliabilityAsync(from, to);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Reliability from {from:yyyy-MM-dd HH:mm} UTC to {to:yyyy-MM-dd HH:mm} UTC");

        var reasons = Items(result, "endReasons");
        Console.WriteLine();
        Console.WriteLine(reasons.Count == 0 ? "Runs: none ended in this range." : "Runs by end reason:");
        foreach (var reason in reasons)
            Console.WriteLine($"  {Prop(reason, "reason"),-20} {(IsTrue(reason, "succeeded") ? "succeeded" : "failed"),-10} {Count(reason, "count"),8}");

        var workflows = Items(result, "workflows");
        Console.WriteLine();
        Console.WriteLine(workflows.Count == 0 ? "Workflows: no completed execution." : "Workflow durations (completed executions):");
        foreach (var workflow in workflows)
            Console.WriteLine($"  {Cut(Prop(workflow, "name") ?? "-", 36),-36} {Count(workflow, "runs"),5} runs  p50 {Duration((long)Number(workflow, "p50Ms")),-8} " +
                $"p95 {Duration((long)Number(workflow, "p95Ms")),-8} {Prop(workflow, "workflowId")}");

        var schedulers = Items(result, "failingSchedulers");
        Console.WriteLine();
        Console.WriteLine(schedulers.Count == 0 ? "Schedulers: none failed." : "Failing schedulers:");
        foreach (var scheduler in schedulers)
            Console.WriteLine($"  {Cut(Prop(scheduler, "title") ?? "-", 36),-36} {Count(scheduler, "failures")} failed of {Count(scheduler, "total"),-6} {Prop(scheduler, "scheduledPromptId")}");

        var agents = Items(result, "agentsOffline");
        Console.WriteLine();
        Console.WriteLine(agents.Count == 0 ? "Agents: none offline for more than a day." : "Agents offline for more than a day:");
        foreach (var agent in agents)
            Console.WriteLine($"  {Cut(Prop(agent, "name") ?? "-", 36),-36} since {PropDate(agent, "offlineSince"),-22} {Prop(agent, "agentId")}");
        return 0;
    }

    /// <summary>
    /// <c>--since</c> (default <paramref name="defaultWindow"/> back from now) to <c>--until</c> (default now), within the
    /// range the backend accepts.
    /// </summary>
    private static bool TryReadRange(string[] args, TimeSpan defaultWindow, out DateTime from, out DateTime to, out string? error)
    {
        error = null;
        var now = DateTime.UtcNow;
        var sinceArg = GetOption(args, "--since");
        var untilArg = GetOption(args, "--until");
        DateTime? since = sinceArg is null ? now - defaultWindow : RunCommands.ParseTime(sinceArg);
        DateTime? until = untilArg is null ? now : RunCommands.ParseTime(untilArg);
        from = since ?? default;
        to = until ?? default;
        if (since is null || until is null)
            return false;
        if (from >= to)
            error = "--since must be before --until.";
        else if (to - from > TimeSpan.FromDays(MaxRangeDays))
            error = $"The range cannot exceed {MaxRangeDays} days.";
        return error is null;
    }

    private static long Tokens(JsonElement el) =>
        (long)(Number(el, "inputTokens") + Number(el, "outputTokens") + Number(el, "cacheReadTokens") + Number(el, "cacheWriteTokens"));

    private static string DeltaTokens(JsonElement current, JsonElement? previous) =>
        previous is { } prev ? Change(Tokens(current), Tokens(prev)) : "";

    private static string Delta(JsonElement current, JsonElement? previous, string name) =>
        previous is { } prev ? Change(Number(current, name), Number(prev, name)) : "";

    /// <summary>The change versus the window of the same length just before.</summary>
    private static string Change(double current, double previous) =>
        previous == 0
            ? current == 0 ? "" : " (none in the previous period)"
            : $" ({((current - previous) / previous).ToString("+0%;-0%;0%", CultureInfo.InvariantCulture)} vs previous period)";

    private static List<JsonElement> Items(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];

    private static double Number(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Count(JsonElement el, string name) => Count((long)Number(el, name));

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Usd(JsonElement el, string name) =>
        "$" + Number(el, name).ToString("N2", CultureInfo.InvariantCulture);

    private static bool IsTrue(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Duration(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:00}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
            : $"{span.Seconds}s";
    }

    private static string Cut(string text, int width) => text.Length > width ? text[..(width - 3)] + "..." : text;

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
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm UTC") : "-";
}
