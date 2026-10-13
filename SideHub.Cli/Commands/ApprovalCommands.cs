using System.Globalization;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// What workflows wait for someone to decide: approval steps (approve, reject, send the document back with changes) and
/// tool calls a step stopped for under its workflow's tool policy (approve or deny).
/// </summary>
public static class ApprovalCommands
{
    private const string AwaitingApproval = "awaiting-approval";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// The cockpit lists one pending approval per execution; each execution is then read for every step waiting on a
    /// decision, its instructions and the pages the steps before it wrote.
    /// </summary>
    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var now = await client.GetCockpitNowAsync();
        var executionIds = Items(now, "attention")
            .Where(item => Prop(item, "kind") == "approval-pending")
            .Select(item => item.TryGetProperty("target", out var target) ? Prop(target, "workflowExecutionId") : null)
            .OfType<string>()
            .Distinct()
            .ToList();

        var pending = new List<PendingApproval>();
        foreach (var executionId in executionIds)
        {
            var execution = await client.GetWorkflowExecutionAsync(executionId);
            pending.AddRange(PendingIn(execution));
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(pending, JsonOptions));
            return 0;
        }

        if (pending.Count == 0)
        {
            Console.WriteLine("No approval is pending.");
            return 0;
        }

        Console.WriteLine($"Pending approvals ({pending.Count}):");
        foreach (var p in pending)
        {
            Console.WriteLine();
            Console.WriteLine($"{p.WorkflowName} > {p.StepName}");
            Console.WriteLine($"  Execution:  {p.ExecutionId}");
            Console.WriteLine($"  Step:       {p.StepId}");
            if (p.ToolApprovalId is { } toolApprovalId)
            {
                var call = p.ToolApproval;
                Console.WriteLine($"  Kind:       tool call {toolApprovalId}");
                if (call is { } c)
                {
                    Console.WriteLine($"  Call:       {Prop(c, "summary")}");
                    Console.WriteLine($"  Rule:       {Prop(c, "rule")} (approver: {Prop(c, "approverRole")}, on timeout: {Prop(c, "onTimeout")})");
                    if (Prop(c, "expiresAt") is not null)
                        Console.WriteLine($"  Expires:    {PropDate(c, "expiresAt")}");
                }
            }
            else
            {
                Console.WriteLine("  Kind:       approval step");
                if (p.Approval is { } a)
                {
                    Console.WriteLine($"  Approver:   {Prop(a, "approverRole")}" +
                        (Prop(a, "expiresAt") is null ? "" : $", expires {PropDate(a, "expiresAt")} (then {Prop(a, "onTimeout")})"));
                    if (Prop(a, "instructions") is { } instructions)
                    {
                        Console.WriteLine("  Instructions:");
                        foreach (var line in instructions.ReplaceLineEndings("\n").TrimEnd().Split('\n'))
                            Console.WriteLine($"    {line}");
                    }
                    if (Prop(a, "documentDriveItemId") is { } document)
                        Console.WriteLine($"  Document to review: sidehub-cli drive read {document}");
                }
            }

            if (p.Pages.Count > 0)
            {
                Console.WriteLine("  Written by the steps before:");
                foreach (var page in p.Pages)
                    Console.WriteLine($"    {page.StepName}: sidehub-cli drive read {page.DriveItemId}");
            }

            Console.WriteLine("  Decide:");
            if (p.ToolApprovalId is { } id)
            {
                Console.WriteLine($"    sidehub-cli approval tool approve {id} [--comment \"...\"]");
                Console.WriteLine($"    sidehub-cli approval tool deny {id} [--comment \"...\"]");
            }
            else
            {
                Console.WriteLine($"    sidehub-cli approval approve {p.ExecutionId} {p.StepId} [--comment \"...\"]");
                Console.WriteLine($"    sidehub-cli approval reject {p.ExecutionId} {p.StepId} [--comment \"...\"]");
                Console.WriteLine($"    sidehub-cli approval request-changes {p.ExecutionId} {p.StepId} --comment \"...\"");
            }
        }
        return 0;
    }

    public static Task<int> ApproveAsync(SideHubApiClient client, string[] args, bool json) =>
        DecideStepAsync(client, args, json, "approve");

    public static Task<int> RejectAsync(SideHubApiClient client, string[] args, bool json) =>
        DecideStepAsync(client, args, json, "reject");

    public static Task<int> RequestChangesAsync(SideHubApiClient client, string[] args, bool json) =>
        DecideStepAsync(client, args, json, "request-changes");

    /// <summary><c>approval tool approve|deny &lt;toolApprovalId&gt;</c>.</summary>
    public static async Task<int> ToolAsync(SideHubApiClient client, string[] args, bool json)
    {
        var positionals = Positionals(args, "--comment");
        var decision = positionals.ElementAtOrDefault(0);
        var toolApprovalId = positionals.ElementAtOrDefault(1);
        if (decision is not ("approve" or "deny") || toolApprovalId is null)
        {
            Console.Error.WriteLine("Usage: sidehub-cli approval tool approve|deny <toolApprovalId> [--comment \"...\"]");
            return 1;
        }

        var result = await client.DecideToolApprovalAsync(toolApprovalId, decision, GetOption(args, "--comment"));

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Tool call {toolApprovalId}: {Prop(result, "status") ?? (decision == "approve" ? "approved" : "denied")}.");
        return 0;
    }

    private static async Task<int> DecideStepAsync(SideHubApiClient client, string[] args, bool json, string decision)
    {
        var positionals = Positionals(args, "--comment");
        var executionId = positionals.ElementAtOrDefault(0);
        var stepId = positionals.ElementAtOrDefault(1);
        var comment = GetOption(args, "--comment");
        // Changes are sent back to the agent step before: without a comment it would not know what to change.
        if (executionId is null || stepId is null || (decision == "request-changes" && string.IsNullOrWhiteSpace(comment)))
        {
            Console.Error.WriteLine(decision == "request-changes"
                ? "Usage: sidehub-cli approval request-changes <executionId> <stepId> --comment \"...\""
                : $"Usage: sidehub-cli approval {decision} <executionId> <stepId> [--comment \"...\"]");
            return 1;
        }

        var result = await client.DecideWorkflowStepAsync(executionId, stepId, decision, comment);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var outcome = Prop(result, "decision") ?? decision;
        Console.WriteLine($"Step {stepId}: {outcome}. Execution {executionId} is {Prop(result, "executionStatus") ?? "unknown"}." +
            (Prop(result, "revisedStepId") is { } revised ? $" Step {revised} runs again with the comment." : ""));
        return 0;
    }

    private static IEnumerable<PendingApproval> PendingIn(JsonElement execution)
    {
        var steps = Items(execution, "stepExecutions").OrderBy(s => Int(s, "order")).ToList();
        foreach (var step in steps.Where(s => Prop(s, "status") == AwaitingApproval))
        {
            var order = Int(step, "order");
            var toolApprovalId = Prop(step, "pendingToolApprovalId");
            var toolApproval = toolApprovalId is null
                ? (JsonElement?)null
                : Items(step, "toolApprovals").Where(t => Prop(t, "id") == toolApprovalId).Select(t => (JsonElement?)t).FirstOrDefault();
            var approval = step.TryGetProperty("approval", out var a) && a.ValueKind == JsonValueKind.Object ? a : (JsonElement?)null;
            var pages = steps
                .Where(s => Int(s, "order") < order && Prop(s, "outputDriveItemId") is not null)
                .Select(s => new StepPage(Prop(s, "stepId")!, Prop(s, "stepName") ?? "", Prop(s, "outputDriveItemId")!))
                .ToList();
            yield return new PendingApproval(
                Prop(execution, "id")!, Prop(execution, "workflowName") ?? "", Prop(step, "stepId")!, Prop(step, "stepName") ?? "",
                toolApprovalId is null ? "approval" : "tool", toolApprovalId, toolApproval, approval, pages);
        }
    }

    /// <param name="Kind"><c>approval</c> (an approval step) or <c>tool</c> (a tool call the step stopped for).</param>
    private sealed record PendingApproval(
        string ExecutionId, string WorkflowName, string StepId, string StepName, string Kind,
        string? ToolApprovalId, JsonElement? ToolApproval, JsonElement? Approval, IReadOnlyList<StepPage> Pages);

    private sealed record StepPage(string StepId, string StepName, string DriveItemId);

    private static List<JsonElement> Items(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().ToList() : [];

    private static int Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    /// <summary>The arguments that are neither a flag nor the value of one of <paramref name="valueFlags"/>.</summary>
    private static List<string> Positionals(string[] args, params string[] valueFlags)
    {
        var result = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (valueFlags.Contains(args[i])) { i++; continue; }
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
