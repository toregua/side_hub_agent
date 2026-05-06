using SideHub.Cli;
using SideHub.Cli.Commands;

var apiUrl = Environment.GetEnvironmentVariable("SIDEHUB_API_URL");
var agentToken = Environment.GetEnvironmentVariable("SIDEHUB_AGENT_TOKEN");
var workspaceId = Environment.GetEnvironmentVariable("SIDEHUB_WORKSPACE_ID");
var taskId = Environment.GetEnvironmentVariable("SIDEHUB_TASK_ID");
var defaultAgentId = Environment.GetEnvironmentVariable("SIDEHUB_AGENT_ID");
var pipelineMode = Environment.GetEnvironmentVariable("SIDEHUB_PIPELINE_MODE");

if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(agentToken) || string.IsNullOrEmpty(workspaceId))
{
    Console.Error.WriteLine("Missing required environment variables: SIDEHUB_API_URL, SIDEHUB_AGENT_TOKEN, SIDEHUB_WORKSPACE_ID");
    return 1;
}

if (!apiUrl.StartsWith("https://") && !apiUrl.StartsWith("http://"))
{
    Console.Error.WriteLine($"Invalid SIDEHUB_API_URL: '{apiUrl}' — must start with http:// or https://");
    return 1;
}

if (!agentToken.StartsWith("sh_agent_"))
{
    Console.Error.WriteLine("Invalid SIDEHUB_AGENT_TOKEN format — must start with 'sh_agent_'.");
    return 1;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: sidehub-cli <domain> <action> [options]");
    Console.Error.WriteLine("Domains: drive, table, task, scheduler, workflow");
    Console.Error.WriteLine("  drive list [--parent <id>]");
    Console.Error.WriteLine("  drive read <pageId>");
    Console.Error.WriteLine("  drive download <pageId> [--output <path>] [--stdout] [--url-only]");
    Console.Error.WriteLine("  drive create --title \"...\" [--content \"...\"] [--parent <id>] [--type page|spreadsheet]");
    Console.Error.WriteLine("  drive update <pageId> [--title \"...\"] [--content \"...\"]");
    Console.Error.WriteLine("  drive search <query>");
    Console.Error.WriteLine("  table show <pageId> [--json]");
    Console.Error.WriteLine("  table append-row <pageId> --col \"Name=Value\" [--col ...]");
    Console.Error.WriteLine("  table set-cell <pageId> --row <rowId> --col <colName> --value \"...\"");
    Console.Error.WriteLine("  table delete-row <pageId> --row <rowId>");
    Console.Error.WriteLine("  table add-column <pageId> --name \"...\" [--type text|image|dropdown] [--options \"val=Label,val2=Label 2\"]");
    Console.Error.WriteLine("  task list [--status <status>]");
    Console.Error.WriteLine("  task create --title \"...\" [--description \"...\"] [--type <type>]");
    Console.Error.WriteLine("  task comment [<taskId>] --text \"...\"");
    Console.Error.WriteLine("  task blocker [<taskId>] --reason \"...\"");
    Console.Error.WriteLine("  scheduler list [--active | --paused]");
    Console.Error.WriteLine("  scheduler get <id>");
    Console.Error.WriteLine("  scheduler create --title \"...\" --prompt \"...\" --cron \"...\" [--agent <id>] [--description \"...\"] [--provider <provider>]");
    Console.Error.WriteLine("  scheduler update <id> [--title \"...\"] [--prompt \"...\"] [--cron \"...\"] [--description \"...\"] [--agent <id>] [--provider <provider>]");
    Console.Error.WriteLine("  scheduler delete <id> [--yes]");
    Console.Error.WriteLine("  scheduler pause <id>");
    Console.Error.WriteLine("  scheduler resume <id>");
    Console.Error.WriteLine("  scheduler trigger <id>");
    Console.Error.WriteLine("  scheduler executions <id>");
    Console.Error.WriteLine("  workflow step-complete --output-id <guid>");
    Console.Error.WriteLine("  workflow step-fail \"<reason>\"");
    return 1;
}

var domain = args[0].ToLowerInvariant();
var action = args[1].ToLowerInvariant();
var restArgs = args[2..];
var jsonOutput = restArgs.Contains("--json");

// Defense-in-depth: block write commands in plan mode
var writeActions = new HashSet<string> { "create", "update", "comment", "blocker", "delete", "pause", "resume", "trigger", "append-row", "set-cell", "delete-row", "add-column" };
if (pipelineMode == "plan" && writeActions.Contains(action))
{
    Console.Error.WriteLine($"Error: write command '{action}' is not allowed in plan mode");
    return 1;
}

using var client = new SideHubApiClient(apiUrl, agentToken, workspaceId, defaultAgentId);

try
{
    return (domain, action) switch
    {
        ("drive", "list") => await DriveCommands.ListAsync(client, restArgs, jsonOutput),
        ("drive", "read") => await DriveCommands.ReadAsync(client, restArgs, jsonOutput),
        ("drive", "download") => await DriveCommands.DownloadAsync(client, restArgs, jsonOutput),
        ("drive", "create") => await DriveCommands.CreateAsync(client, restArgs, jsonOutput),
        ("drive", "update") => await DriveCommands.UpdateAsync(client, restArgs, jsonOutput),
        ("drive", "search") => await DriveCommands.SearchAsync(client, restArgs, jsonOutput),
        ("table", "show") => await TableCommands.ShowAsync(client, restArgs, jsonOutput),
        ("table", "append-row") => await TableCommands.AppendRowAsync(client, restArgs, jsonOutput),
        ("table", "set-cell") => await TableCommands.SetCellAsync(client, restArgs, jsonOutput),
        ("table", "delete-row") => await TableCommands.DeleteRowAsync(client, restArgs, jsonOutput),
        ("table", "add-column") => await TableCommands.AddColumnAsync(client, restArgs, jsonOutput),
        ("task", "list") => await TaskCommands.ListAsync(client, restArgs, jsonOutput),
        ("task", "create") => await TaskCommands.CreateAsync(client, restArgs, jsonOutput),
        ("task", "comment") => await TaskCommands.CommentAsync(client, restArgs, taskId, jsonOutput),
        ("task", "blocker") => await TaskCommands.BlockerAsync(client, restArgs, taskId, jsonOutput),
        ("scheduler", "list") => await SchedulerCommands.ListAsync(client, restArgs, jsonOutput),
        ("scheduler", "get") => await SchedulerCommands.GetAsync(client, restArgs, jsonOutput),
        ("scheduler", "create") => await SchedulerCommands.CreateAsync(client, restArgs, jsonOutput),
        ("scheduler", "update") => await SchedulerCommands.UpdateAsync(client, restArgs, jsonOutput),
        ("scheduler", "delete") => await SchedulerCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("scheduler", "pause") => await SchedulerCommands.PauseAsync(client, restArgs, jsonOutput),
        ("scheduler", "resume") => await SchedulerCommands.ResumeAsync(client, restArgs, jsonOutput),
        ("scheduler", "trigger") => await SchedulerCommands.TriggerAsync(client, restArgs, jsonOutput),
        ("scheduler", "executions") => await SchedulerCommands.ExecutionsAsync(client, restArgs, jsonOutput),
        ("workflow", "step-complete") => await WorkflowCommands.StepCompleteAsync(client, restArgs, jsonOutput),
        ("workflow", "step-fail") => await WorkflowCommands.StepFailAsync(client, restArgs, jsonOutput),
        _ => Error($"Unknown command: {domain} {action}")
    };
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"API error: {ex.Message}");
    return 1;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static int Error(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
