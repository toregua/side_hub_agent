using System.Globalization;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// A workflow's event triggers (Owners and Admins): launch it when a document lands in a folder, a task moves to a
/// status or another workflow ends successfully. Inputs are read from the event's payload (<c>--map param=path</c>).
/// </summary>
public static class WorkflowTriggerCommands
{
    private static readonly string[] Types = ["drive.item.created", "task.status_changed", "workflow.completed"];
    private static readonly string[] TaskStatuses = ["completed", "in-progress"];
    private static readonly string[] ValueFlags =
        ["--type", "--agent", "--provider", "--filter-file", "--folder", "--to-status", "--source-workflow", "--map", "--active"];

    private const string FilterUsage =
        "Filter: --folder <id> [--subfolders] (drive.item.created), --to-status completed|in-progress (task.status_changed),\n" +
        "        --source-workflow <id> (workflow.completed), or --filter-file <json> ({folderId, includeSubfolders, toStatus, sourceWorkflowId}).";

    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var workflowId = Positionals(args).FirstOrDefault();
        if (workflowId is null)
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow triggers <workflowId>");
            return 1;
        }

        var triggers = await client.GetWorkflowEventTriggersAsync(workflowId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(triggers));
            return 0;
        }

        if (triggers.ValueKind != JsonValueKind.Array || triggers.GetArrayLength() == 0)
        {
            Console.WriteLine("No event triggers on this workflow.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"TYPE",-20} {"ACTIVE",-7} {"PROVIDER",-9} {"AGENT",-38} {"FILTER"}");
        Console.WriteLine(new string('-', 150));
        foreach (var trigger in triggers.EnumerateArray())
        {
            var active = trigger.TryGetProperty("isActive", out var a) && a.ValueKind == JsonValueKind.True ? "yes" : "no";
            Console.WriteLine($"{Prop(trigger, "id"),-38} {Prop(trigger, "type"),-20} {active,-7} {Prop(trigger, "provider"),-9} " +
                $"{Prop(trigger, "agentId"),-38} {DescribeFilter(trigger)}");
            if (trigger.TryGetProperty("inputMapping", out var map) && map.ValueKind == JsonValueKind.Object && map.EnumerateObject().Any())
                Console.WriteLine($"{"",-38} inputs: {string.Join(", ", map.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}"))}");
            if (Prop(trigger, "lastTriggeredAt") is not null)
                Console.WriteLine($"{"",-38} last launch: {PropDate(trigger, "lastTriggeredAt")}");
            if (Prop(trigger, "lastError") is { } error)
                Console.WriteLine($"{"",-38} last error ({PropDate(trigger, "lastErrorAt")}): {error}");
        }
        return 0;
    }

    public static async Task<int> AddAsync(SideHubApiClient client, string[] args, bool json)
    {
        const string usage = "Usage: sidehub-cli workflow trigger-add <workflowId> --type drive.item.created|task.status_changed|workflow.completed " +
            "[--agent <id>] [--provider <p>] [filter] [--map param=path]*";
        var workflowId = Positionals(args).FirstOrDefault();
        var type = GetOption(args, "--type");
        object? filter = null;
        string? filterError = null;
        if (workflowId is null || type is null || !Types.Contains(type) || !TryReadFilter(args, type, out filter, out filterError))
        {
            Console.Error.WriteLine(usage);
            Console.Error.WriteLine(FilterUsage);
            if (filterError is not null)
                Console.Error.WriteLine($"Error: {filterError}");
            return 1;
        }
        if (!WorkflowCommands.TryCollectPairs(args, "--map", out var mapping, out var mapError))
        {
            Console.Error.WriteLine($"Error: {mapError}");
            return 1;
        }
        var agentId = GetOption(args, "--agent") ?? client.DefaultAgentId;
        if (agentId is null)
        {
            Console.Error.WriteLine(usage);
            Console.Error.WriteLine("No agent: pass --agent <id> (SIDEHUB_AGENT_ID is not set in this session).");
            return 1;
        }

        var trigger = await client.CreateWorkflowEventTriggerAsync(
            workflowId, type, filter, agentId, GetOption(args, "--provider"), mapping.Count > 0 ? mapping : null);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(trigger));
            return 0;
        }

        Console.WriteLine($"Created event trigger {Prop(trigger, "id")}: {Prop(trigger, "type")} {DescribeFilter(trigger)}, " +
            $"{Prop(trigger, "provider")} on agent {Prop(trigger, "agentId")}.");
        return 0;
    }

    public static async Task<int> UpdateAsync(SideHubApiClient client, string[] args, bool json)
    {
        const string usage = "Usage: sidehub-cli workflow trigger-update <workflowId> <triggerId> [--active true|false] [--agent <id>] " +
            "[--provider <p>] [filter] [--map param=path]* [--clear-map]";
        var ids = Positionals(args);
        var activeArg = GetOption(args, "--active");
        bool? isActive = activeArg switch
        {
            null => null,
            "true" => true,
            "false" => false,
            _ => null
        };
        var hasMap = args.Contains("--map");
        var clearMap = args.Contains("--clear-map");
        var filterRead = TryReadFilter(args, type: null, out var filter, out var filterError);
        if (ids.Count < 2 || (activeArg is not null && isActive is null) || (hasMap && clearMap) || !filterRead)
        {
            Console.Error.WriteLine(usage);
            Console.Error.WriteLine(FilterUsage);
            if (filterError is not null)
                Console.Error.WriteLine($"Error: {filterError}");
            return 1;
        }
        if (!WorkflowCommands.TryCollectPairs(args, "--map", out var mapping, out var mapError))
        {
            Console.Error.WriteLine($"Error: {mapError}");
            return 1;
        }
        var agentId = GetOption(args, "--agent");
        var provider = GetOption(args, "--provider");
        if (filter is null && agentId is null && provider is null && isActive is null && !hasMap && !clearMap)
        {
            Console.Error.WriteLine(usage);
            Console.Error.WriteLine("Nothing to change.");
            return 1;
        }

        var trigger = await client.UpdateWorkflowEventTriggerAsync(
            ids[0], ids[1], filter, agentId, provider, hasMap || clearMap ? mapping : null, isActive);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(trigger));
            return 0;
        }

        var active = trigger.TryGetProperty("isActive", out var a) && a.ValueKind == JsonValueKind.True ? "active" : "inactive";
        Console.WriteLine($"Updated event trigger {Prop(trigger, "id")}: {Prop(trigger, "type")} {DescribeFilter(trigger)}, " +
            $"{Prop(trigger, "provider")} on agent {Prop(trigger, "agentId")}, {active}.");
        return 0;
    }

    public static async Task<int> DeleteAsync(SideHubApiClient client, string[] args, bool json)
    {
        var ids = Positionals(args);
        if (ids.Count < 2)
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow trigger-delete <workflowId> <triggerId> [--yes]");
            return 1;
        }

        if (!args.Contains("--yes"))
        {
            Console.Error.Write($"Delete event trigger {ids[1]}? [y/N] ");
            var answer = Console.ReadLine();
            if (answer is null || !answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        await client.DeleteWorkflowEventTriggerAsync(ids[0], ids[1]);

        if (json) Console.WriteLine($"{{\"deleted\":\"{ids[1]}\"}}");
        else Console.WriteLine($"Deleted event trigger: {ids[1]}");
        return 0;
    }

    /// <summary>
    /// The filter of the flags or of <c>--filter-file</c>; null when none is given. With <paramref name="type"/>, flags
    /// that type does not read are refused (the backend would drop them silently).
    /// </summary>
    private static bool TryReadFilter(string[] args, string? type, out object? filter, out string? error)
    {
        filter = null;
        error = null;
        var file = GetOption(args, "--filter-file");
        var folder = GetOption(args, "--folder");
        var subfolders = args.Contains("--subfolders");
        var toStatus = GetOption(args, "--to-status");
        var source = GetOption(args, "--source-workflow");
        var flags = folder is not null || subfolders || toStatus is not null || source is not null;

        if (file is not null)
        {
            if (flags)
            {
                error = "--filter-file replaces the filter flags.";
                return false;
            }
            if (!File.Exists(file))
            {
                error = $"file not found: {file}";
                return false;
            }
            try
            {
                var document = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file));
                if (document.ValueKind != JsonValueKind.Object)
                {
                    error = $"{file} must hold a JSON object.";
                    return false;
                }
                filter = document;
                return true;
            }
            catch (JsonException ex)
            {
                error = $"{file} is not JSON ({ex.Message}).";
                return false;
            }
        }

        if (type == "workflow.completed" && source is null)
        {
            error = "workflow.completed needs --source-workflow <id>: the workflow whose end launches this one.";
            return false;
        }
        if (!flags)
            return true;
        if (subfolders && folder is null)
            error = "--subfolders needs --folder.";
        else if (toStatus is not null && !TaskStatuses.Contains(toStatus))
            error = $"--to-status must be one of: {string.Join(", ", TaskStatuses)}.";
        else if ((folder is not null ? 1 : 0) + (toStatus is not null ? 1 : 0) + (source is not null ? 1 : 0) > 1)
            error = "--folder, --to-status and --source-workflow belong to different event types: pass one.";
        else if (type is not null && ((type != "drive.item.created" && folder is not null)
            || (type != "task.status_changed" && toStatus is not null)
            || (type != "workflow.completed" && source is not null)))
            error = $"This filter does not apply to {type}.";
        if (error is not null)
            return false;

        filter = new { folderId = folder, includeSubfolders = subfolders, toStatus, sourceWorkflowId = source };
        return true;
    }

    private static string DescribeFilter(JsonElement trigger)
    {
        if (!trigger.TryGetProperty("filter", out var f) || f.ValueKind != JsonValueKind.Object)
            return "-";
        return Prop(trigger, "type") switch
        {
            "drive.item.created" => Prop(f, "folderId") is { } folder
                ? $"in folder {folder}{(f.TryGetProperty("includeSubfolders", out var s) && s.ValueKind == JsonValueKind.True ? " and subfolders" : "")}"
                : "anywhere in the drive",
            "task.status_changed" => $"to {Prop(f, "toStatus") ?? "completed"}",
            "workflow.completed" => $"after workflow {Prop(f, "sourceWorkflowId")}",
            _ => "-"
        };
    }

    private static List<string> Positionals(string[] args)
    {
        var result = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (ValueFlags.Contains(args[i])) { i++; continue; }
            if (!args[i].StartsWith("--")) result.Add(args[i]);
        }
        return result;
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
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string PropDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm UTC") : "-";
}
