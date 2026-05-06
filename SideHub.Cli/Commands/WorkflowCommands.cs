namespace SideHub.Cli.Commands;

public static class WorkflowCommands
{
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
