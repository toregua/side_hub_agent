using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// Automations (doc 61 of SideHub): an automation is a workflow, its schedules are its scheduled triggers. A one-step
/// automation runs a prompt and ends when the CLI exits. The <c>workflow</c> and <c>scheduler</c> commands stay as
/// aliases for the same objects.
/// </summary>
public static class AutomationCommands
{
    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var automations = await client.ListWorkflowsAsync();
        var schedules = await client.GetSchedulersAsync(null);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(automations));
            return 0;
        }
        if (automations.ValueKind != JsonValueKind.Array || automations.GetArrayLength() == 0)
        {
            Console.WriteLine("No automations found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STEPS",-6} {"TRIGGERS",-24} {"LAST RUN",-21} {"NAME"}");
        Console.WriteLine(new string('-', 120));
        foreach (var automation in automations.EnumerateArray())
        {
            var id = Prop(automation, "id") ?? "";
            var steps = automation.TryGetProperty("stepCount", out var sc) ? sc.GetInt32().ToString() : "0";
            var last = Prop(automation, "lastExecutionAt") is { } at && DateTime.TryParse(at, out var dt)
                ? $"{dt:yyyy-MM-dd HH:mm} {Prop(automation, "lastExecutionStatus")}" : "-";
            Console.WriteLine($"{id,-38} {steps,-6} {Triggers(SchedulesOf(schedules, id)),-24} {last,-21} {Prop(automation, "name")}");
        }
        return 0;
    }

    public static async Task<int> GetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = FirstArgument(args);
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli automation get <id>");
            return 1;
        }

        var exit = await WorkflowCommands.GetAsync(client, args, json);
        if (exit != 0 || json)
            return exit;

        var schedules = SchedulesOf(await client.GetSchedulersAsync(null), id);
        Console.WriteLine();
        Console.WriteLine("Triggers:");
        Console.WriteLine("  by hand (sidehub-cli automation run)");
        foreach (var schedule in schedules)
        {
            var status = schedule.TryGetProperty("isActive", out var a) && a.GetBoolean() ? "active" : "paused";
            var next = Prop(schedule, "nextExecutionAt") is { } n && DateTime.TryParse(n, out var dt) ? $", next {dt:yyyy-MM-dd HH:mm} UTC" : "";
            Console.WriteLine($"  schedule {Prop(schedule, "id")}: {Prop(schedule, "cronExpression")} UTC ({status}{next})");
        }
        return 0;
    }

    /// <summary>
    /// A one-step automation: the prompt runs on the agent and ends when the CLI exits. With <c>--cron</c>, SideHub
    /// creates it with its schedule; otherwise it runs by hand.
    /// </summary>
    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var name = GetOption(args, "--name");
        var prompt = GetOption(args, "--prompt");
        var cron = GetOption(args, "--cron");
        var description = GetOption(args, "--description");
        var provider = GetOption(args, "--provider");
        var model = GetOption(args, "--model");
        var agentId = GetOption(args, "--agent") ?? client.DefaultAgentId;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prompt))
        {
            Console.Error.WriteLine("Usage: sidehub-cli automation create --name \"...\" --prompt \"...\" [--cron \"...\" [--description \"...\"]] [--agent <id>] [--provider <cli>] [--model <model>]");
            return 1;
        }

        string automationId;
        string? scheduleId = null;
        JsonElement createdSchedule = default;
        if (!string.IsNullOrEmpty(cron))
        {
            if (string.IsNullOrEmpty(agentId))
            {
                Console.Error.WriteLine("Error: --agent <id> is required (or set SIDEHUB_AGENT_ID env var): a schedule runs on an agent.");
                return 1;
            }
            createdSchedule = await client.CreateSchedulerAsync(name, prompt, cron, description, provider, agentId, workflowId: null);
            scheduleId = Prop(createdSchedule, "id");
            automationId = Prop(createdSchedule, "workflowId") ?? "";
            if (json)
            {
                Console.WriteLine(SideHubApiClient.Serialize(createdSchedule));
                return 0;
            }
        }
        else
        {
            var workflow = await client.CreateWorkflowAsync(name, null, null);
            automationId = Prop(workflow, "id") ?? "";
            var withStep = await client.AddWorkflowStepAsync(automationId, name, prompt, [], name, null, null,
                provider: provider, model: model, completesOnExit: true);
            if (json)
            {
                Console.WriteLine(SideHubApiClient.Serialize(withStep));
                return 0;
            }
        }

        Console.WriteLine($"Created automation: {automationId}");
        if (scheduleId is not null)
            Console.WriteLine($"Schedule: {scheduleId} ({cron} UTC){PausedNotice(createdSchedule)}");
        else
            Console.WriteLine($"Runs by hand: sidehub-cli automation run {automationId}");
        return 0;
    }

    public static Task<int> RunAsync(SideHubApiClient client, string[] args, bool json) =>
        WorkflowCommands.RunAsync(client, args, json);

    public static Task<int> ExecutionsAsync(SideHubApiClient client, string[] args, bool json) =>
        WorkflowCommands.ExecutionsAsync(client, args, json);

    /// <summary>Pauses or resumes every schedule of the automation (it still runs by hand).</summary>
    public static async Task<int> SetSchedulesActiveAsync(SideHubApiClient client, string[] args, bool json, bool active)
    {
        var id = FirstArgument(args);
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine($"Usage: sidehub-cli automation {(active ? "resume" : "pause")} <id>");
            return 1;
        }

        var schedules = SchedulesOf(await client.GetSchedulersAsync(null), id);
        if (schedules.Count == 0)
        {
            Console.WriteLine("This automation has no schedule: it only runs by hand.");
            return 0;
        }
        foreach (var schedule in schedules)
        {
            var scheduleId = Prop(schedule, "id")!;
            if (active) await client.ResumeSchedulerAsync(scheduleId);
            else await client.PauseSchedulerAsync(scheduleId);
        }
        if (json) Console.WriteLine($"{{\"automation\":\"{id}\",\"schedules\":{schedules.Count},\"active\":{active.ToString().ToLowerInvariant()}}}");
        else Console.WriteLine($"{(active ? "Resumed" : "Paused")} {schedules.Count} schedule(s) of automation {id}.");
        return 0;
    }

    /// <summary>Deletes the automation and its schedules.</summary>
    public static async Task<int> DeleteAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = FirstArgument(args);
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli automation delete <id> [--yes]");
            return 1;
        }

        var schedules = SchedulesOf(await client.GetSchedulersAsync(null), id);
        if (!args.Contains("--yes"))
        {
            Console.Error.Write($"Delete automation {id}{(schedules.Count > 0 ? $" and its {schedules.Count} schedule(s)" : "")}? Type 'yes' to confirm: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Aborted.");
                return 1;
            }
        }
        foreach (var schedule in schedules)
            await client.DeleteSchedulerAsync(Prop(schedule, "id")!);
        await client.DeleteWorkflowAsync(id);

        if (json) Console.WriteLine($"{{\"deleted\":\"{id}\"}}");
        else Console.WriteLine($"Deleted automation: {id}");
        return 0;
    }

    /// <summary><c>automation schedule add | update | remove</c>.</summary>
    public static async Task<int> ScheduleAsync(SideHubApiClient client, string[] args, bool json)
    {
        var action = args.FirstOrDefault();
        var rest = args.Skip(1).ToArray();
        switch (action)
        {
            case "add":
                return await AddScheduleAsync(client, rest, json);
            case "update":
                return await SchedulerCommands.UpdateAsync(client, rest, json);
            case "remove":
                return await SchedulerCommands.DeleteAsync(client, rest, json);
            default:
                Console.Error.WriteLine("Usage: sidehub-cli automation schedule add <automationId> --cron \"...\" | update <scheduleId> [--cron \"...\"] | remove <scheduleId>");
                return 1;
        }
    }

    private static async Task<int> AddScheduleAsync(SideHubApiClient client, string[] args, bool json)
    {
        var automationId = FirstArgument(args);
        var cron = GetOption(args, "--cron");
        var description = GetOption(args, "--description");
        var agentId = GetOption(args, "--agent") ?? client.DefaultAgentId;
        if (string.IsNullOrEmpty(automationId) || string.IsNullOrEmpty(cron))
        {
            Console.Error.WriteLine("Usage: sidehub-cli automation schedule add <automationId> --cron \"...\" [--description \"...\"] [--agent <id>]");
            return 1;
        }
        if (string.IsNullOrEmpty(agentId))
        {
            Console.Error.WriteLine("Error: --agent <id> is required (or set SIDEHUB_AGENT_ID env var): a schedule runs on an agent.");
            return 1;
        }

        var automation = await client.GetWorkflowAsync(automationId);
        var schedule = await client.CreateSchedulerAsync(Prop(automation, "name") ?? "Automation", null, cron, description,
            provider: null, agentId, automationId);
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(schedule));
            return 0;
        }
        Console.WriteLine($"Added schedule {Prop(schedule, "id")} ({cron} UTC) to automation {automationId}{PausedNotice(schedule)}.");
        return 0;
    }

    /// <summary>A schedule an agent creates starts paused, until a person activates it (SideHub's agent guardrails).</summary>
    private static string PausedNotice(JsonElement schedule) =>
        schedule.ValueKind == JsonValueKind.Object && schedule.TryGetProperty("isActive", out var a) && a.ValueKind == JsonValueKind.False
            ? ", paused until a person activates it in SideHub" : "";

    private static List<JsonElement> SchedulesOf(JsonElement schedules, string automationId) =>
        schedules.ValueKind == JsonValueKind.Array
            ? schedules.EnumerateArray().Where(s => Prop(s, "workflowId") == automationId).ToList()
            : [];

    private static string Triggers(IReadOnlyList<JsonElement> schedules)
    {
        if (schedules.Count == 0)
            return "by hand";
        var active = schedules.Count(s => s.TryGetProperty("isActive", out var a) && a.GetBoolean());
        return schedules.Count == 1
            ? $"{Prop(schedules[0], "cronExpression")}{(active == 0 ? " (paused)" : "")}"
            : $"{schedules.Count} schedules, {active} active";
    }

    private static string? FirstArgument(string[] args) => args.FirstOrDefault(a => !a.StartsWith("--"));

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
