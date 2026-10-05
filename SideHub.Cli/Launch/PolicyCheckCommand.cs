using System.Text;
using System.Text.Json;

namespace SideHub.Cli.Launch;

/// <summary>
/// <c>sidehub-cli policy check</c>: a Claude Code PreToolUse hook, installed by <c>sidehub-cli launch</c> (see
/// <see cref="CliStateHooks.ClaudeSettings"/>) when the run has a tool policy (<c>$SIDEHUB_TOOL_POLICY_MATCHER</c>,
/// on the tools it matches). It asks SideHub whether the tool call of the hook input may run
/// (<c>POST /api/tool-approvals/check</c>) and answers Claude Code on stdout
/// (https://code.claude.com/docs/en/hooks, "PreToolUse decision control").
/// <para>
/// It fails closed: whatever goes wrong (no input, no token, SideHub unreachable, an unexpected answer), the tool
/// call is denied with the cause, and the command still exits 0 so that Claude Code reads the decision instead of
/// treating the hook as broken.
/// </para>
/// </summary>
public static class PolicyCheckCommand
{
    public const string Domain = "policy";
    public const string Action = "check";

    /// <summary>The run's tool policy: a regex over Claude tool names, set by the backend in the run's PTY.</summary>
    public const string MatcherVariable = "SIDEHUB_TOOL_POLICY_MATCHER";

    /// <summary>Seconds Claude Code waits for the hook: longer than <see cref="HttpTimeout"/>, so the hook always
    /// answers itself.</summary>
    public const int HookTimeoutSeconds = 60;

    /// <summary>The hook JSON read from stdin at most; a larger input (a huge file write) is denied.</summary>
    public const int MaxHookInputBytes = 8 * 1024 * 1024;

    public const string Allow = "allow";
    public const string Approved = "approved";
    public const string Deny = "deny";
    public const string Pause = "pause";

    private static readonly HashSet<string> Decisions = [Allow, Approved, Deny, Pause];

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StdinTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The longest backend error kept in a reason (it is shown to the model).</summary>
    private const int MaxErrorLength = 300;

    public static async Task<int> RunAsync()
    {
        Verdict verdict;
        try
        {
            verdict = await DecideAsync(ReadHookInput(),
                Environment.GetEnvironmentVariable("SIDEHUB_API_URL"),
                Environment.GetEnvironmentVariable("SIDEHUB_AGENT_TOKEN"),
                new SocketsHttpHandler { AllowAutoRedirect = false });
        }
        catch (Exception ex)
        {
            verdict = Failure(ex.GetType().Name);
        }
        try
        {
            if (HookOutput(verdict) is { } output)
                Console.Out.Write(output);
        }
        catch
        {
            // Nothing left to tell Claude Code.
        }
        return 0;
    }

    /// <param name="Decision">One of <see cref="Allow"/>, <see cref="Approved"/>, <see cref="Deny"/>,
    /// <see cref="Pause"/>.</param>
    /// <param name="Reason">Why, shown to the model; null when SideHub gave none.</param>
    public sealed record Verdict(string Decision, string? Reason);

    /// <summary>The tool call a PreToolUse hook input describes.</summary>
    /// <param name="ToolInput">The tool's input, as Claude Code gave it.</param>
    /// <param name="CliSessionId">The claude session, when it is a UUID.</param>
    public sealed record HookInput(string ToolName, JsonElement ToolInput, string? ToolUseId, string? CliSessionId);

    /// <summary>The verdict when the policy could not be checked: the tool call is denied.</summary>
    public static Verdict Failure(string cause) =>
        new(Deny, $"SideHub could not check this action against the workflow's tool policy ({cause}): it is blocked.");

    /// <summary>What to tell Claude Code for the hook input, asking SideHub through <paramref name="handler"/>
    /// (which must not follow redirects itself).</summary>
    public static async Task<Verdict> DecideAsync(byte[]? hookInput, string? apiUrl, string? agentToken, HttpMessageHandler handler)
    {
        if (ParseHookInput(hookInput, out var inputProblem) is not { } input)
            return Failure(inputProblem!);
        if (EnvironmentProblem(apiUrl, agentToken) is { } environmentProblem)
            return Failure(environmentProblem);
        return await CheckAsync(input, apiUrl!, agentToken!, handler);
    }

    /// <summary>Why the API cannot be asked from this terminal, null when it can.</summary>
    public static string? EnvironmentProblem(string? apiUrl, string? agentToken)
    {
        if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(agentToken))
            return "SIDEHUB_API_URL or SIDEHUB_AGENT_TOKEN is not set";
        if (ApiUrlPolicy.RejectionReason(apiUrl) is { } apiUrlProblem)
            return $"invalid SIDEHUB_API_URL: {apiUrlProblem}";
        // Only a backend-launched run has a tool policy, and its token is the run's.
        if (!agentToken.StartsWith("sh_run_", StringComparison.Ordinal))
            return "this terminal has no run token";
        return null;
    }

    /// <summary>The tool call of a PreToolUse hook input, or null with <paramref name="problem"/> when it has
    /// none that can be checked.</summary>
    public static HookInput? ParseHookInput(byte[]? input, out string? problem)
    {
        problem = null;
        if (input is null || input.Length == 0)
        {
            problem = "no hook input on stdin";
            return null;
        }
        if (input.Length > MaxHookInputBytes)
        {
            problem = $"hook input larger than {MaxHookInputBytes / (1024 * 1024)} MiB";
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool_name", out var toolName) || toolName.ValueKind != JsonValueKind.String
                || toolName.GetString() is not { Length: > 0 } name)
            {
                problem = "hook input without tool_name";
                return null;
            }
            JsonElement toolInput;
            if (!root.TryGetProperty("tool_input", out var given) || given.ValueKind == JsonValueKind.Null)
            {
                using var empty = JsonDocument.Parse("{}");
                toolInput = empty.RootElement.Clone();
            }
            else if (given.ValueKind == JsonValueKind.Object)
            {
                toolInput = given.Clone();
            }
            else
            {
                problem = "hook input whose tool_input is not an object";
                return null;
            }
            var toolUseId = root.TryGetProperty("tool_use_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            var sessionId = root.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String
                && CliLaunchPlan.IsSessionId(session.GetString())
                ? session.GetString()
                : null;
            return new HookInput(name, toolInput, toolUseId, sessionId);
        }
        catch (JsonException)
        {
            problem = "hook input is not JSON";
            return null;
        }
    }

    /// <summary>Asks SideHub about the tool call. X-Agent-Token is sent: redirects are only followed within the
    /// API's origin.</summary>
    private static async Task<Verdict> CheckAsync(HookInput input, string apiUrl, string agentToken, HttpMessageHandler handler)
    {
        using var http = new HttpClient(new SameOriginRedirectHandler(handler))
        {
            BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/"),
            Timeout = HttpTimeout,
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        var body = JsonSerializer.Serialize(new
        {
            toolName = input.ToolName,
            toolInput = input.ToolInput,
            toolUseId = input.ToolUseId,
            cliSessionId = input.CliSessionId,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/tool-approvals/check")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Agent-Token", agentToken);
        try
        {
            using var response = await http.SendAsync(request);
            return Interpret((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException)
        {
            return Failure("SideHub is unreachable");
        }
        catch (TaskCanceledException)
        {
            return Failure("SideHub did not answer in time");
        }
    }

    /// <summary>The verdict of SideHub's answer: <c>{ "decision", "reason", "requestId" }</c> on success.</summary>
    public static Verdict Interpret(int statusCode, string body)
    {
        if (statusCode is < 200 or > 299)
            return Failure(ErrorOf(body) is { } error ? $"HTTP {statusCode}: {error}" : $"HTTP {statusCode}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.String)
                return Failure("SideHub's answer has no decision");
            if (decision.GetString() is not { } value || !Decisions.Contains(value))
                return Failure("SideHub's answer has an unknown decision");
            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            return new Verdict(value, string.IsNullOrWhiteSpace(reason) ? null : reason);
        }
        catch (JsonException)
        {
            return Failure("SideHub's answer is not JSON");
        }
    }

    /// <summary>The <c>error</c> (else <c>message</c>) of an error body, cut short; null when it has none.</summary>
    private static string? ErrorOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var field in (string[])["error", "message"])
                if (doc.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text)
                    return text.Length > MaxErrorLength ? text[..MaxErrorLength] + "…" : text;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The hook's stdout for a verdict, null to write nothing. <see cref="Allow"/>: nothing, Claude Code's own
    /// permission flow applies; <see cref="Approved"/>: allowed (a human approved it in SideHub); <see cref="Deny"/>:
    /// denied, the reason goes to the model; <see cref="Pause"/>: denied and the turn stops (a human must decide,
    /// SideHub resumes the session afterwards). Anything else is denied.
    /// </summary>
    public static string? HookOutput(Verdict verdict)
    {
        object Decision(string permission, string reason) => new
        {
            hookEventName = "PreToolUse",
            permissionDecision = permission,
            permissionDecisionReason = reason,
        };

        switch (verdict.Decision)
        {
            case Allow:
                return null;
            case Approved:
                return JsonSerializer.Serialize(new { hookSpecificOutput = Decision("allow", verdict.Reason ?? "Approved in SideHub.") });
            case Deny:
                return JsonSerializer.Serialize(new
                {
                    hookSpecificOutput = Decision("deny", verdict.Reason ?? "Blocked by the workflow's tool policy."),
                });
            case Pause:
                var reason = verdict.Reason ?? "This action waits for a human approval in SideHub.";
                return JsonSerializer.Serialize(new
                {
                    @continue = false,
                    stopReason = reason,
                    hookSpecificOutput = Decision("deny", reason),
                });
            default:
                return HookOutput(Failure("unknown decision"));
        }
    }

    /// <summary>The hook input on stdin, bounded in time, and in size past <see cref="MaxHookInputBytes"/> (a
    /// longer input is returned one chunk too long); null when stdin is a terminal (run by hand) or silent.</summary>
    private static byte[]? ReadHookInput()
    {
        if (!Console.IsInputRedirected)
            return null;
        var read = Task.Run(() =>
        {
            using var stdin = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while (buffer.Length <= MaxHookInputBytes && (n = stdin.Read(chunk)) > 0)
                buffer.Write(chunk, 0, n);
            return buffer.ToArray();
        });
        return read.Wait(StdinTimeout) ? read.Result : null;
    }
}
