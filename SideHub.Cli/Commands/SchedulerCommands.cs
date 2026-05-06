using System.Text.Json;

namespace SideHub.Cli.Commands;

public static class SchedulerCommands
{
    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        bool? active = null;
        if (args.Contains("--active")) active = true;
        else if (args.Contains("--paused")) active = false;

        var result = await client.GetSchedulersAsync(active);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            Console.WriteLine("No schedulers found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STATUS",-9} {"CRON",-18} {"NEXT EXECUTION",-22} {"TITLE"}");
        Console.WriteLine(new string('-', 110));
        foreach (var item in result.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var isActive = item.TryGetProperty("isActive", out var a) && a.GetBoolean();
            var status = isActive ? "active" : "paused";
            var cron = item.TryGetProperty("cronExpression", out var c) ? c.GetString() ?? "" : "";
            var next = item.TryGetProperty("nextExecutionAt", out var n) && n.ValueKind != JsonValueKind.Null
                ? DateTime.Parse(n.GetString()!).ToString("yyyy-MM-dd HH:mm UTC") : "-";
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            Console.WriteLine($"{id,-38} {status,-9} {cron,-18} {next,-22} {title}");
        }
        return 0;
    }

    public static async Task<int> GetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli scheduler get <id>");
            return 1;
        }

        var result = await client.GetSchedulerAsync(id);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var title = Prop(result, "title");
        var cron = Prop(result, "cronExpression");
        var desc = Prop(result, "scheduleDescription");
        var isActive = result.TryGetProperty("isActive", out var a) && a.GetBoolean();
        var provider = Prop(result, "provider") ?? "-";
        var next = PropDate(result, "nextExecutionAt");
        var last = PropDate(result, "lastExecutedAt");
        var count = result.TryGetProperty("executionCount", out var ec) ? ec.GetInt32().ToString() : "0";
        var error = Prop(result, "lastError") ?? "-";
        var prompt = Prop(result, "prompt") ?? "";

        Console.WriteLine($"Title:       {title}");
        Console.WriteLine($"Cron:        {cron}");
        Console.WriteLine($"Description: {(string.IsNullOrEmpty(desc) ? "-" : desc)}");
        Console.WriteLine($"Status:      {(isActive ? "active" : "paused")}");
        Console.WriteLine($"Provider:    {provider}");
        Console.WriteLine($"Next:        {next}");
        Console.WriteLine($"Last:        {last}");
        Console.WriteLine($"Executions:  {count}");
        Console.WriteLine($"Last error:  {error}");
        Console.WriteLine($"Prompt:");
        foreach (var line in prompt.Split('\n'))
            Console.WriteLine($"  {line}");

        return 0;
    }

    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var title = GetOption(args, "--title");
        var prompt = GetOption(args, "--prompt");
        var cron = GetOption(args, "--cron");
        var description = GetOption(args, "--description");
        var provider = GetOption(args, "--provider");
        var agentId = GetOption(args, "--agent") ?? client.DefaultAgentId;

        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(prompt) || string.IsNullOrEmpty(cron))
        {
            Console.Error.WriteLine("Usage: sidehub-cli scheduler create --title \"...\" --prompt \"...\" --cron \"...\" [--agent <id>] [--description \"...\"] [--provider <provider>]");
            return 1;
        }

        if (string.IsNullOrEmpty(agentId))
        {
            Console.Error.WriteLine("Error: --agent <id> is required (or set SIDEHUB_AGENT_ID env var). Schedulers must be assigned to an agent.");
            return 1;
        }

        var result = await client.CreateSchedulerAsync(title, prompt, cron, description, provider, agentId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var id = result.TryGetProperty("id", out var i) ? i.GetString() : "";
        Console.WriteLine($"Created scheduler: {id}");
        return 0;
    }

    public static async Task<int> UpdateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        var title = GetOption(args, "--title");
        var prompt = GetOption(args, "--prompt");
        var cron = GetOption(args, "--cron");
        var description = GetOption(args, "--description");
        var provider = GetOption(args, "--provider");
        var agentId = GetOption(args, "--agent");

        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli scheduler update <id> [--title \"...\"] [--prompt \"...\"] [--cron \"...\"] [--description \"...\"] [--agent <id>] [--provider <provider>]");
            return 1;
        }

        if (title is null && prompt is null && cron is null && description is null && provider is null && agentId is null)
        {
            Console.Error.WriteLine("Error: at least one field to update is required.");
            return 1;
        }

        var result = await client.UpdateSchedulerAsync(id, title, prompt, cron, description, provider, agentId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Updated scheduler: {id}");
        return 0;
    }

    public static async Task<int> DeleteAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli scheduler delete <id> [--yes]");
            return 1;
        }

        if (!args.Contains("--yes"))
        {
            Console.Error.Write($"Delete scheduler {id}? [y/N] ");
            var answer = Console.ReadLine();
            if (answer is null || !answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        await client.DeleteSchedulerAsync(id);

        if (json)
        {
            Console.WriteLine("{}");
            return 0;
        }

        Console.WriteLine($"Deleted scheduler: {id}");
        return 0;
    }

    public static async Task<int> PauseAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli scheduler pause <id>");
            return 1;
        }

        var result = await client.PauseSchedulerAsync(id);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Paused scheduler: {id}");
        return 0;
    }

    public static async Task<int> ResumeAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli scheduler resume <id>");
            return 1;
        }

        var result = await client.ResumeSchedulerAsync(id);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Resumed scheduler: {id}");
        return 0;
    }

    public static async Task<int> TriggerAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli scheduler trigger <id>");
            return 1;
        }

        var result = await client.TriggerSchedulerAsync(id);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Triggered scheduler: {id}");
        return 0;
    }

    public static async Task<int> ExecutionsAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli scheduler executions <id>");
            return 1;
        }

        var result = await client.GetSchedulerExecutionsAsync(id);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            Console.WriteLine("No executions found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STATUS",-10} {"STARTED",-22} {"COMPLETED",-22} {"ERROR"}");
        Console.WriteLine(new string('-', 120));
        foreach (var item in result.EnumerateArray())
        {
            var eid = Prop(item, "id") ?? "";
            var status = Prop(item, "status") ?? "";
            var started = PropDate(item, "startedAt");
            var completed = PropDate(item, "completedAt");
            var error = Prop(item, "errorMessage") ?? "-";
            if (error.Length > 40) error = error[..37] + "...";
            Console.WriteLine($"{eid,-38} {status,-10} {started,-22} {completed,-22} {error}");
        }
        return 0;
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    private static string PropDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? DateTime.Parse(v.GetString()!).ToString("yyyy-MM-dd HH:mm UTC") : "-";
}
