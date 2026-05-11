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
    Console.Error.WriteLine("Domains: drive, sqlite, table, task, scheduler, workflow");
    Console.Error.WriteLine("  drive list [--parent <id>]");
    Console.Error.WriteLine("  drive read <pageId>");
    Console.Error.WriteLine("  drive download <pageId> [--output <path>] [--stdout] [--url-only]");
    Console.Error.WriteLine("  drive create --title \"...\" [--content \"...\" | --file <path>] [--parent <id>] [--type page|spreadsheet|folder]");
    Console.Error.WriteLine("  drive update <pageId> [--title \"...\"] [--content \"...\" | --file <path>]");
    Console.Error.WriteLine("  drive delete <id> [--yes]");
    Console.Error.WriteLine("  drive move <id> --parent <newParentId|root> [--after <siblingId>]");
    Console.Error.WriteLine("  drive mkdir --title \"...\" [--parent <id>]");
    Console.Error.WriteLine("  drive upload <localPath> [--parent <id>] [--name \"...\"]");
    Console.Error.WriteLine("  drive recent [--limit N]");
    Console.Error.WriteLine("  drive usage");
    Console.Error.WriteLine("  drive search <query>");
    Console.Error.WriteLine("  drive create-json --title \"file.json\" [--content '<json>' | --file <path>] [--parent <id>]");
    Console.Error.WriteLine("  drive query <id> --path \"$.foo.bar\" [--raw]");
    Console.Error.WriteLine("  drive patch <id> [--set <pointer>=<value>]* [--delete <pointer>]* [--ops-file <path>]");
    Console.Error.WriteLine("  sqlite query <itemId> --sql \"SELECT ...\" [--param V]* [--row-limit N] [--timeout SEC] [--json]");
    Console.Error.WriteLine("  sqlite exec <itemId> --sql \"INSERT/UPDATE/DELETE\" [--param V]* [--allow-ddl] [--timeout SEC] [--json]");
    Console.Error.WriteLine("  sqlite schema <itemId> [--json]");
    Console.Error.WriteLine("  sqlite create --title \"name\" [--schema \"CREATE TABLE ...\" | --schema-file <path>] [--parent <id>] [--json]");
    Console.Error.WriteLine("  table show <pageId> [--json]");
    Console.Error.WriteLine("  table append-row <pageId> --col \"Name=Value\" [--col ...]");
    Console.Error.WriteLine("  table set-cell <pageId> --row <rowId> --col <colName> --value \"...\"");
    Console.Error.WriteLine("  table delete-row <pageId> --row <rowId>");
    Console.Error.WriteLine("  table add-column <pageId> --name \"...\" [--type text|image|dropdown] [--options \"val=Label,val2=Label 2\"]");
    Console.Error.WriteLine("  task list [--status <status>]");
    Console.Error.WriteLine("  task get <taskId>");
    Console.Error.WriteLine("  task create --title \"...\" [--description \"...\"] [--type <type>]");
    Console.Error.WriteLine("  task update <taskId> [--title \"...\"] [--description \"...\"] [--type <type>]");
    Console.Error.WriteLine("  task delete <taskId> [--yes]");
    Console.Error.WriteLine("  task status <taskId> --status <status>");
    Console.Error.WriteLine("  task comment [<taskId>] --text \"...\"");
    Console.Error.WriteLine("  task blocker [<taskId>] --reason \"...\"");
    Console.Error.WriteLine("  task resolve-blocker <taskId>");
    Console.Error.WriteLine("  task drive-link-add <taskId> --item <driveItemId>");
    Console.Error.WriteLine("  task drive-link-remove <taskId> --item <driveItemId>");
    Console.Error.WriteLine("  task search --query \"...\" [--status <s>] [--type <t>]");
    Console.Error.WriteLine("  scheduler list [--active | --paused]");
    Console.Error.WriteLine("  scheduler get <id>");
    Console.Error.WriteLine("  scheduler create --title \"...\" (--prompt \"...\" | --workflow <id>) --cron \"...\" [--agent <id>] [--description \"...\"] [--provider <provider>]");
    Console.Error.WriteLine("  scheduler update <id> [--title] [--prompt | --workflow <id>] [--cron] [--description] [--agent] [--provider]");
    Console.Error.WriteLine("  scheduler delete <id> [--yes]");
    Console.Error.WriteLine("  scheduler pause <id>");
    Console.Error.WriteLine("  scheduler resume <id>");
    Console.Error.WriteLine("  scheduler trigger <id>");
    Console.Error.WriteLine("  scheduler executions <id>");
    Console.Error.WriteLine("  workflow list");
    Console.Error.WriteLine("  workflow get <id>");
    Console.Error.WriteLine("  workflow create --name \"...\" [--description \"...\"] [--timeout <min>]");
    Console.Error.WriteLine("  workflow update <id> [--name] [--description] [--timeout <min> | --clear-timeout]");
    Console.Error.WriteLine("  workflow delete <id> [--yes]");
    Console.Error.WriteLine("  workflow add-step <wfId> --name \"...\" --prompt \"...\" [--input-drive <itemId>]* [--input-step <stepId>]* [--output-path \"...\"] [--output-drive <itemId>] [--timeout <min>]");
    Console.Error.WriteLine("  workflow update-step <wfId> <stepId> [--name] [--prompt] [--input-drive]* [--input-step]* [--clear-inputs] [--output-path] [--output-drive | --clear-output-drive] [--timeout]");
    Console.Error.WriteLine("  workflow delete-step <wfId> <stepId> [--yes]");
    Console.Error.WriteLine("  workflow reorder-steps <wfId> <stepId1> <stepId2> [...]");
    Console.Error.WriteLine("  workflow run <wfId> [--agent <id>] [--provider <p>]");
    Console.Error.WriteLine("  workflow execution-get <executionId>");
    Console.Error.WriteLine("  workflow step-complete --output-id <guid>");
    Console.Error.WriteLine("  workflow step-fail \"<reason>\"");
    return 1;
}

var domain = args[0].ToLowerInvariant();
var action = args[1].ToLowerInvariant();
var restArgs = args[2..];
var jsonOutput = restArgs.Contains("--json");

// Defense-in-depth: block write commands in plan mode
var writeActions = new HashSet<string>
{
    "create", "update", "comment", "blocker", "delete", "pause", "resume", "trigger",
    "append-row", "set-cell", "delete-row", "add-column",
    "move", "mkdir", "upload",
    "add-step", "update-step", "delete-step", "reorder-steps", "run",
    "status", "drive-link-add", "drive-link-remove", "resolve-blocker",
    "exec"
};
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
        ("drive", "delete") => await DriveCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("drive", "move") => await DriveCommands.MoveAsync(client, restArgs, jsonOutput),
        ("drive", "mkdir") => await DriveCommands.MkdirAsync(client, restArgs, jsonOutput),
        ("drive", "upload") => await DriveCommands.UploadAsync(client, restArgs, jsonOutput),
        ("drive", "recent") => await DriveCommands.RecentAsync(client, restArgs, jsonOutput),
        ("drive", "usage") => await DriveCommands.UsageAsync(client, restArgs, jsonOutput),
        ("drive", "search") => await DriveCommands.SearchAsync(client, restArgs, jsonOutput),
        ("drive", "create-json") => await DriveCommands.CreateJsonAsync(client, restArgs, jsonOutput),
        ("drive", "query") => await DriveCommands.QueryAsync(client, restArgs, jsonOutput),
        ("drive", "patch") => await DriveCommands.PatchAsync(client, restArgs, jsonOutput),
        ("sqlite", "query") => await SqliteCommands.QueryAsync(client, restArgs, jsonOutput),
        ("sqlite", "exec") => await SqliteCommands.ExecAsync(client, restArgs, jsonOutput),
        ("sqlite", "schema") => await SqliteCommands.SchemaAsync(client, restArgs, jsonOutput),
        ("sqlite", "create") => await SqliteCommands.CreateAsync(client, restArgs, jsonOutput),
        ("table", "show") => await TableCommands.ShowAsync(client, restArgs, jsonOutput),
        ("table", "append-row") => await TableCommands.AppendRowAsync(client, restArgs, jsonOutput),
        ("table", "set-cell") => await TableCommands.SetCellAsync(client, restArgs, jsonOutput),
        ("table", "delete-row") => await TableCommands.DeleteRowAsync(client, restArgs, jsonOutput),
        ("table", "add-column") => await TableCommands.AddColumnAsync(client, restArgs, jsonOutput),
        ("task", "list") => await TaskCommands.ListAsync(client, restArgs, jsonOutput),
        ("task", "get") => await TaskCommands.GetAsync(client, restArgs, jsonOutput),
        ("task", "create") => await TaskCommands.CreateAsync(client, restArgs, jsonOutput),
        ("task", "update") => await TaskCommands.UpdateAsync(client, restArgs, jsonOutput),
        ("task", "delete") => await TaskCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("task", "status") => await TaskCommands.StatusAsync(client, restArgs, jsonOutput),
        ("task", "comment") => await TaskCommands.CommentAsync(client, restArgs, taskId, jsonOutput),
        ("task", "blocker") => await TaskCommands.BlockerAsync(client, restArgs, taskId, jsonOutput),
        ("task", "resolve-blocker") => await TaskCommands.ResolveBlockerAsync(client, restArgs, jsonOutput),
        ("task", "drive-link-add") => await TaskCommands.DriveLinkAddAsync(client, restArgs, jsonOutput),
        ("task", "drive-link-remove") => await TaskCommands.DriveLinkRemoveAsync(client, restArgs, jsonOutput),
        ("task", "search") => await TaskCommands.SearchAsync(client, restArgs, jsonOutput),
        ("scheduler", "list") => await SchedulerCommands.ListAsync(client, restArgs, jsonOutput),
        ("scheduler", "get") => await SchedulerCommands.GetAsync(client, restArgs, jsonOutput),
        ("scheduler", "create") => await SchedulerCommands.CreateAsync(client, restArgs, jsonOutput),
        ("scheduler", "update") => await SchedulerCommands.UpdateAsync(client, restArgs, jsonOutput),
        ("scheduler", "delete") => await SchedulerCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("scheduler", "pause") => await SchedulerCommands.PauseAsync(client, restArgs, jsonOutput),
        ("scheduler", "resume") => await SchedulerCommands.ResumeAsync(client, restArgs, jsonOutput),
        ("scheduler", "trigger") => await SchedulerCommands.TriggerAsync(client, restArgs, jsonOutput),
        ("scheduler", "executions") => await SchedulerCommands.ExecutionsAsync(client, restArgs, jsonOutput),
        ("workflow", "list") => await WorkflowCommands.ListAsync(client, restArgs, jsonOutput),
        ("workflow", "get") => await WorkflowCommands.GetAsync(client, restArgs, jsonOutput),
        ("workflow", "create") => await WorkflowCommands.CreateAsync(client, restArgs, jsonOutput),
        ("workflow", "update") => await WorkflowCommands.UpdateAsync(client, restArgs, jsonOutput),
        ("workflow", "delete") => await WorkflowCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("workflow", "add-step") => await WorkflowCommands.AddStepAsync(client, restArgs, jsonOutput),
        ("workflow", "update-step") => await WorkflowCommands.UpdateStepAsync(client, restArgs, jsonOutput),
        ("workflow", "delete-step") => await WorkflowCommands.DeleteStepAsync(client, restArgs, jsonOutput),
        ("workflow", "reorder-steps") => await WorkflowCommands.ReorderStepsAsync(client, restArgs, jsonOutput),
        ("workflow", "run") => await WorkflowCommands.RunAsync(client, restArgs, jsonOutput),
        ("workflow", "execution-get") => await WorkflowCommands.ExecutionGetAsync(client, restArgs, jsonOutput),
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
