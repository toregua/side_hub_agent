using System.Text.Json;

namespace SideHub.Cli.Commands;

public static class WorkflowCommands
{
    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var result = await client.ListWorkflowsAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            Console.WriteLine("No workflows found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STEPS",-6} {"NAME",-30} {"LAST EXEC",-19} {"STATUS"}");
        Console.WriteLine(new string('-', 110));
        foreach (var w in result.EnumerateArray())
        {
            var id = Prop(w, "id") ?? "";
            var name = Prop(w, "name") ?? "";
            var stepCount = w.TryGetProperty("stepCount", out var sc) ? sc.GetInt32().ToString() : "0";
            var lastAt = PropDate(w, "lastExecutionAt");
            var lastStatus = Prop(w, "lastExecutionStatus") ?? "-";
            Console.WriteLine($"{id,-38} {stepCount,-6} {Truncate(name, 30),-30} {lastAt,-19} {lastStatus}");
        }
        return 0;
    }

    public static async Task<int> GetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow get <id>");
            return 1;
        }

        var result = await client.GetWorkflowAsync(id);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"ID:          {Prop(result, "id")}");
        Console.WriteLine($"Name:        {Prop(result, "name")}");
        Console.WriteLine($"Description: {Prop(result, "description") ?? "-"}");
        var defaultTimeout = result.TryGetProperty("defaultStepTimeoutMinutes", out var dt) && dt.ValueKind == JsonValueKind.Number
            ? dt.GetInt32() + " min" : "-";
        Console.WriteLine($"Default timeout: {defaultTimeout}");
        Console.WriteLine();
        if (result.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"Steps ({steps.GetArrayLength()}):");
            foreach (var s in steps.EnumerateArray())
            {
                var sid = Prop(s, "id") ?? "";
                var order = s.TryGetProperty("order", out var o) ? o.GetInt32() : 0;
                var name = Prop(s, "name") ?? "";
                var outputPath = Prop(s, "outputPath") ?? "";
                var outputDriveItemId = Prop(s, "outputDriveItemId");
                var timeout = s.TryGetProperty("timeoutMinutes", out var tm) && tm.ValueKind == JsonValueKind.Number
                    ? tm.GetInt32() + " min" : "-";
                Console.WriteLine($"  [{order}] {name} ({sid})");
                Console.WriteLine($"      timeout: {timeout}");
                if (!string.IsNullOrEmpty(outputDriveItemId))
                    Console.WriteLine($"      output → drive item: {outputDriveItemId}");
                else
                    Console.WriteLine($"      output path: {outputPath}");
                if (s.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array && inputs.GetArrayLength() > 0)
                {
                    Console.WriteLine("      inputs:");
                    foreach (var inp in inputs.EnumerateArray())
                    {
                        var t = Prop(inp, "type") ?? "?";
                        if (t == "drive_document")
                            Console.WriteLine($"        - drive: {Prop(inp, "driveItemId")}");
                        else if (t == "previous_step_output")
                            Console.WriteLine($"        - step:  {Prop(inp, "stepId")}");
                        else
                            Console.WriteLine($"        - {t}");
                    }
                }
            }
        }
        return 0;
    }

    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var name = GetOption(args, "--name");
        var description = GetOption(args, "--description");
        var timeoutStr = GetOption(args, "--timeout");

        if (string.IsNullOrEmpty(name))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow create --name \"...\" [--description \"...\"] [--timeout <minutes>]");
            return 1;
        }

        int? timeout = null;
        if (!string.IsNullOrEmpty(timeoutStr))
        {
            if (!int.TryParse(timeoutStr, out var n) || n <= 0)
            {
                Console.Error.WriteLine("Error: --timeout must be a positive integer (minutes).");
                return 1;
            }
            timeout = n;
        }

        var result = await client.CreateWorkflowAsync(name, description, timeout);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Created workflow: {Prop(result, "id")}");
        return 0;
    }

    public static async Task<int> UpdateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        var name = GetOption(args, "--name");
        var description = GetOption(args, "--description");
        var timeoutStr = GetOption(args, "--timeout");
        var clearTimeout = args.Contains("--clear-timeout");

        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow update <id> [--name \"...\"] [--description \"...\"] [--timeout <min> | --clear-timeout]");
            return 1;
        }

        if (name is null && description is null && timeoutStr is null && !clearTimeout)
        {
            Console.Error.WriteLine("Error: at least one field to update is required.");
            return 1;
        }

        int? timeout = null;
        if (!string.IsNullOrEmpty(timeoutStr))
        {
            if (!int.TryParse(timeoutStr, out var n) || n <= 0)
            {
                Console.Error.WriteLine("Error: --timeout must be a positive integer (minutes).");
                return 1;
            }
            timeout = n;
        }

        var result = await client.UpdateWorkflowAsync(id, name, description, timeout, clearTimeout);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Updated workflow: {id}");
        return 0;
    }

    public static async Task<int> DeleteAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow delete <id> [--yes]");
            return 1;
        }

        if (!args.Contains("--yes") && !Confirm($"Delete workflow {id}?"))
            return 1;

        await client.DeleteWorkflowAsync(id);

        if (json) Console.WriteLine($"{{\"deleted\":\"{id}\"}}");
        else Console.WriteLine($"Deleted workflow: {id}");
        return 0;
    }

    public static async Task<int> AddStepAsync(SideHubApiClient client, string[] args, bool json)
    {
        var workflowId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var name = GetOption(args, "--name");
        var prompt = GetOption(args, "--prompt");
        var outputPath = GetOption(args, "--output-path") ?? "";
        var outputDrive = GetOption(args, "--output-drive");
        var timeoutStr = GetOption(args, "--timeout");

        if (string.IsNullOrEmpty(workflowId) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prompt))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow add-step <workflowId> --name \"...\" --prompt \"...\" [--input-drive <itemId>]* [--input-step <stepId>]* [--output-path \"...\"] [--output-drive <itemId>] [--timeout <min>]");
            return 1;
        }

        int? timeout = null;
        if (!string.IsNullOrEmpty(timeoutStr))
        {
            if (!int.TryParse(timeoutStr, out var n) || n <= 0)
            {
                Console.Error.WriteLine("Error: --timeout must be a positive integer (minutes).");
                return 1;
            }
            timeout = n;
        }

        var inputs = CollectInputs(args);

        var result = await client.AddWorkflowStepAsync(workflowId, name, prompt, inputs, outputPath, timeout, outputDrive);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Added step to workflow {workflowId}: {Prop(result, "id")}");
        return 0;
    }

    public static async Task<int> UpdateStepAsync(SideHubApiClient client, string[] args, bool json)
    {
        var positional = args.Where(a => !a.StartsWith("--") && !IsOptionValue(args, a)).ToArray();
        var workflowId = positional.ElementAtOrDefault(0);
        var stepId = positional.ElementAtOrDefault(1);
        var name = GetOption(args, "--name");
        var prompt = GetOption(args, "--prompt");
        var outputPath = GetOption(args, "--output-path");
        var outputDrive = GetOption(args, "--output-drive");
        var timeoutStr = GetOption(args, "--timeout");
        var clearOutputDrive = args.Contains("--clear-output-drive");

        if (string.IsNullOrEmpty(workflowId) || string.IsNullOrEmpty(stepId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow update-step <workflowId> <stepId> [--name] [--prompt] [--input-drive <id>]* [--input-step <id>]* [--output-path] [--output-drive <id> | --clear-output-drive] [--timeout <min>]");
            return 1;
        }

        int? timeout = null;
        if (!string.IsNullOrEmpty(timeoutStr))
        {
            if (!int.TryParse(timeoutStr, out var n) || n <= 0)
            {
                Console.Error.WriteLine("Error: --timeout must be a positive integer (minutes).");
                return 1;
            }
            timeout = n;
        }

        // Inputs are only included if at least one input flag was provided
        // (otherwise we leave them unchanged backend-side).
        var hasInputFlags = HasFlag(args, "--input-drive") || HasFlag(args, "--input-step") || args.Contains("--clear-inputs");
        IEnumerable<SideHubApiClient.StepInputArg>? inputs = null;
        if (hasInputFlags)
            inputs = args.Contains("--clear-inputs") ? Enumerable.Empty<SideHubApiClient.StepInputArg>() : CollectInputs(args);

        var result = await client.UpdateWorkflowStepAsync(workflowId, stepId, name, prompt, inputs, outputPath, timeout, outputDrive, clearOutputDrive);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Updated step {stepId} in workflow {workflowId}");
        return 0;
    }

    public static async Task<int> DeleteStepAsync(SideHubApiClient client, string[] args, bool json)
    {
        var positional = args.Where(a => !a.StartsWith("--") && !IsOptionValue(args, a)).ToArray();
        var workflowId = positional.ElementAtOrDefault(0);
        var stepId = positional.ElementAtOrDefault(1);

        if (string.IsNullOrEmpty(workflowId) || string.IsNullOrEmpty(stepId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow delete-step <workflowId> <stepId> [--yes]");
            return 1;
        }

        if (!args.Contains("--yes") && !Confirm($"Delete step {stepId} from workflow {workflowId}?"))
            return 1;

        var result = await client.RemoveWorkflowStepAsync(workflowId, stepId);

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine($"Deleted step {stepId} from workflow {workflowId}");
        return 0;
    }

    public static async Task<int> ReorderStepsAsync(SideHubApiClient client, string[] args, bool json)
    {
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();
        var workflowId = positional.ElementAtOrDefault(0);
        var orderedStepIds = positional.Skip(1).ToArray();

        if (string.IsNullOrEmpty(workflowId) || orderedStepIds.Length < 2)
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow reorder-steps <workflowId> <stepId1> <stepId2> [...]");
            return 1;
        }

        var result = await client.ReorderWorkflowStepsAsync(workflowId, orderedStepIds);

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine($"Reordered {orderedStepIds.Length} steps in workflow {workflowId}");
        return 0;
    }

    public static async Task<int> RunAsync(SideHubApiClient client, string[] args, bool json)
    {
        var workflowId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var agentId = GetOption(args, "--agent") ?? client.DefaultAgentId;
        var provider = GetOption(args, "--provider") ?? "claude";

        if (string.IsNullOrEmpty(workflowId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow run <workflowId> [--agent <id>] [--provider <p>]");
            return 1;
        }
        if (string.IsNullOrEmpty(agentId))
        {
            Console.Error.WriteLine("Error: --agent <id> is required (or set SIDEHUB_AGENT_ID).");
            return 1;
        }

        var result = await client.RunWorkflowAsync(workflowId, agentId, provider);

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine($"Started workflow execution: {Prop(result, "id") ?? Prop(result, "executionId") ?? "?"}");
        return 0;
    }

    public static async Task<int> ExecutionGetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var execId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(execId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow execution-get <executionId>");
            return 1;
        }

        var result = await client.GetWorkflowExecutionAsync(execId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Execution: {Prop(result, "id")}");
        Console.WriteLine($"Workflow:  {Prop(result, "workflowName")} ({Prop(result, "workflowId")})");
        Console.WriteLine($"Status:    {Prop(result, "status")}");
        Console.WriteLine($"Started:   {PropDate(result, "startedAt")}");
        Console.WriteLine($"Completed: {PropDate(result, "completedAt")}");
        var err = Prop(result, "errorMessage");
        if (!string.IsNullOrEmpty(err)) Console.WriteLine($"Error:     {err}");

        if (result.TryGetProperty("stepExecutions", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine();
            Console.WriteLine("Steps:");
            foreach (var s in steps.EnumerateArray())
            {
                var order = s.TryGetProperty("order", out var o) ? o.GetInt32() : 0;
                var name = Prop(s, "stepName") ?? "";
                var status = Prop(s, "status") ?? "";
                var output = Prop(s, "outputDriveItemId");
                Console.WriteLine($"  [{order}] {name}: {status}{(string.IsNullOrEmpty(output) ? "" : $" → drive {output}")}");
            }
        }
        return 0;
    }

    private static List<SideHubApiClient.StepInputArg> CollectInputs(string[] args)
    {
        var inputs = new List<SideHubApiClient.StepInputArg>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--input-drive") inputs.Add(new SideHubApiClient.StepInputArg("drive", args[i + 1]));
            else if (args[i] == "--input-step") inputs.Add(new SideHubApiClient.StepInputArg("step", args[i + 1]));
        }
        return inputs;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return true;
        return false;
    }

    private static bool IsOptionValue(string[] args, string token)
    {
        // token is at some index; if the previous token is a flag, this token is its value
        var idx = Array.IndexOf(args, token);
        if (idx <= 0) return false;
        var prev = args[idx - 1];
        return prev.StartsWith("--");
    }

    private static bool Confirm(string question)
    {
        Console.Error.Write($"{question} Type 'yes' to confirm: ");
        var input = Console.ReadLine();
        if (!string.Equals(input?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Aborted.");
            return false;
        }
        return true;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string PropDate(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return "-";
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "-";
        var s = v.GetString();
        return DateTime.TryParse(s, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm UTC") : (s ?? "-");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";

    public static async Task<int> StepCompleteAsync(SideHubApiClient client, string[] args, bool json)
    {
        var executionId = Environment.GetEnvironmentVariable("SIDEHUB_WORKFLOW_EXECUTION_ID");
        var stepId = Environment.GetEnvironmentVariable("SIDEHUB_WORKFLOW_STEP_ID");

        if (string.IsNullOrEmpty(executionId) || string.IsNullOrEmpty(stepId))
        {
            Console.Error.WriteLine("Error: SIDEHUB_WORKFLOW_EXECUTION_ID and SIDEHUB_WORKFLOW_STEP_ID must be set.");
            Console.Error.WriteLine("This command can only run inside a workflow step.");
            return 1;
        }

        var outputId = GetOption(args, "--output-id");
        if (string.IsNullOrEmpty(outputId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow step-complete --output-id <guid>");
            return 1;
        }

        var result = await client.CompleteWorkflowStepAsync(executionId, stepId, outputId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Workflow step {stepId} completed (output: {outputId})");
        return 0;
    }

    public static async Task<int> StepFailAsync(SideHubApiClient client, string[] args, bool json)
    {
        var executionId = Environment.GetEnvironmentVariable("SIDEHUB_WORKFLOW_EXECUTION_ID");
        var stepId = Environment.GetEnvironmentVariable("SIDEHUB_WORKFLOW_STEP_ID");

        if (string.IsNullOrEmpty(executionId) || string.IsNullOrEmpty(stepId))
        {
            Console.Error.WriteLine("Error: SIDEHUB_WORKFLOW_EXECUTION_ID and SIDEHUB_WORKFLOW_STEP_ID must be set.");
            Console.Error.WriteLine("This command can only run inside a workflow step.");
            return 1;
        }

        // Reason is the first non-flag positional arg.
        var reason = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(reason))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow step-fail \"<reason>\"");
            return 1;
        }

        var result = await client.FailWorkflowStepAsync(executionId, stepId, reason);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Workflow step {stepId} marked as failed: {reason}");
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
}
