using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Web;
using SideHub.Cli;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>
/// <c>sidehub-cli</c> commands for repository questions, MCP servers, workflow event triggers and costs: the route,
/// method, query string and JSON body each sends, against a local fake API, and the usage errors that never reach it.
/// </summary>
public sealed class CliLot4CommandsTests : IDisposable
{
    private const string WorkspaceId = "11111111-1111-1111-1111-111111111111";
    private const string QuestionsPath = $"/api/workspaces/{WorkspaceId}/repository-questions";
    private const string McpPath = $"/api/workspaces/{WorkspaceId}/mcp-servers";
    private const string TriggersPath = "/api/workflows/wf-1/event-triggers";

    private readonly FakeSideHubApi _api = new();
    private readonly SideHubApiClient _client;
    private readonly List<string> _files = [];

    public CliLot4CommandsTests() => _client = new SideHubApiClient(_api.Url, "sh_pty_test", WorkspaceId, "agent-1");

    public void Dispose()
    {
        _client.Dispose();
        _api.Dispose();
        foreach (var file in _files)
            File.Delete(file);
    }

    private static string[] Args(string line) => line.Split('|', StringSplitOptions.RemoveEmptyEntries);

    private string TempJson(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sidehub-cli-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        _files.Add(path);
        return path;
    }

    private static string Question(string status, string? answer = null, string? error = null) =>
        JsonSerializer.Serialize(new
        {
            id = "q-1", repositoryId = "repo-1", agentId = "agent-1", runId = "run-1", userId = "user-1",
            question = "Where is the login?", status, answer, commitSha = answer is null ? null : "abc1234",
            commitDate = answer is null ? null : "2026-10-12T14:00:00+02:00", error,
            askedAt = "2026-10-13T08:00:00Z", settledAt = status == "pending" ? null : "2026-10-13T08:03:00Z"
        });

    private static string Asked => $$"""{"question":{{Question("pending")}},"agentName":"vps"}""";

    // --- Repository questions ---

    [Fact]
    public async Task Repository_ask_posts_the_question_then_reads_it_until_answered()
    {
        _api.Respond("/repository-questions", Asked, HttpStatusCode.Accepted);
        _api.Respond("/repository-questions/q-1", Question("pending"));
        var waits = 0;

        var code = await RepositoryCommands.AskAsync(_client, ["side_hub", "Where", "is the login?"], json: false, () =>
        {
            if (++waits == 2)
                _api.Respond("/repository-questions/q-1", Question("answered", answer: "In AuthEndpoints.cs"));
            return Task.CompletedTask;
        });

        Assert.Equal(0, code);
        Assert.Equal([$"POST {QuestionsPath}", $"GET {QuestionsPath}/q-1", $"GET {QuestionsPath}/q-1"], _api.Methods);
        var body = _api.Calls[0].Json;
        Assert.Equal("side_hub", body.GetProperty("repository").GetString());
        Assert.Equal("Where is the login?", body.GetProperty("question").GetString());
    }

    [Fact]
    public async Task Repository_ask_without_waiting_only_posts()
    {
        _api.Respond("/repository-questions", Asked, HttpStatusCode.Accepted);

        Assert.Equal(0, await RepositoryCommands.AskAsync(_client, ["repo-1", "Why?", "--no-wait"], json: false, () => Task.CompletedTask));
        Assert.Equal($"POST {QuestionsPath}", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Repository_ask_fails_on_a_failed_question()
    {
        _api.Respond("/repository-questions", Asked, HttpStatusCode.Accepted);
        _api.Respond("/repository-questions/q-1", Question("failed", error: "The search run failed (timeout)."));

        Assert.Equal(1, await RepositoryCommands.AskAsync(_client, ["repo-1", "Why?"], json: false, () => Task.CompletedTask));
        Assert.Equal(2, _api.Calls.Count);
    }

    [Fact]
    public async Task Repository_ask_gives_up_after_its_timeout()
    {
        _api.Respond("/repository-questions", Asked, HttpStatusCode.Accepted);
        _api.Respond("/repository-questions/q-1", Question("pending"));

        Assert.Equal(1, await RepositoryCommands.AskAsync(_client, ["repo-1", "Why?", "--timeout", "1"], json: true, () => Task.CompletedTask));
        Assert.Equal(1 + 60 / RepositoryCommands.PollSeconds, _api.Calls.Count);
    }

    [Fact]
    public async Task Repository_ask_shows_why_it_cannot_start()
    {
        _api.Respond("/repository-questions", """{"error":"No agent online for side_hub."}""", HttpStatusCode.Conflict);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            RepositoryCommands.AskAsync(_client, ["side_hub", "Why?"], json: false, () => Task.CompletedTask));
        Assert.Equal("No agent online for side_hub.", error.Message);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("side_hub")]
    [InlineData("side_hub| ")]
    [InlineData("side_hub|Why?|--timeout|0")]
    [InlineData("side_hub|Why?|--timeout|soon")]
    public async Task Repository_ask_usage_errors_never_call_the_api(string line)
    {
        Assert.Equal(1, await RepositoryCommands.AskAsync(_client, Args(line), json: false, () => Task.CompletedTask));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Repository_question_reads_one_question()
    {
        _api.Respond("/repository-questions/q-1", Question("answered", answer: "Line 1\nLine 2"));

        Assert.Equal(0, await RepositoryCommands.QuestionAsync(_client, ["q-1"], json: false));
        Assert.Equal($"GET {QuestionsPath}/q-1", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Repository_questions_sends_its_limit()
    {
        _api.Respond("/repository-questions", $"[{Question("answered", answer: "x")},{Question("pending")}]");

        Assert.Equal(0, await RepositoryCommands.QuestionsAsync(_client, ["--limit", "5"], json: false));
        Assert.Equal($"{QuestionsPath}?limit=5", Assert.Single(_api.Requests));
    }

    [Theory]
    [InlineData("repository question", "")]
    [InlineData("repository questions", "--limit|0")]
    [InlineData("repository questions", "--limit|all")]
    public async Task Repository_question_usage_errors_never_call_the_api(string command, string line)
    {
        var code = command.EndsWith("questions")
            ? await RepositoryCommands.QuestionsAsync(_client, Args(line), json: false)
            : await RepositoryCommands.QuestionAsync(_client, Args(line), json: false);
        Assert.Equal(1, code);
        Assert.Empty(_api.Calls);
    }

    // --- MCP servers ---

    private const string Server = """
        {"name":"github","transport":"stdio","command":"npx","args":["-y","@mcp/github"],"env":{"GITHUB_TOKEN":"${GH_TOKEN}"},
         "url":null,"headers":{},"description":"GitHub issues","secrets":[],"secretsFile":null,"revision":2,
         "referencedSecrets":["GH_TOKEN"],"missingSecrets":[],"usedBy":[]}
        """;

    [Fact]
    public async Task Mcp_list_reads_the_servers()
    {
        _api.Respond("/mcp-servers", $"[{Server}]");

        Assert.Equal(0, await McpCommands.ListAsync(_client, json: false));
        Assert.Equal($"GET {McpPath}", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Mcp_set_puts_a_stdio_definition()
    {
        _api.Respond("/mcp-servers/github", Server, HttpStatusCode.Created);

        var code = await McpCommands.SetAsync(_client, Args(
            "github|--command|npx|--arg|-y|--arg|@mcp/github|--arg|--read-only|--env|GITHUB_TOKEN=${GH_TOKEN}|--env|LOG=a=b" +
            "|--secret|GH_TOKEN|--secrets-file|dotenv|--description|GitHub issues"), json: false);

        Assert.Equal(0, code);
        var call = Assert.Single(_api.Calls);
        Assert.Equal("PUT", call.Method);
        Assert.Equal($"{McpPath}/github", call.PathAndQuery);
        var body = call.Json;
        Assert.Equal("stdio", body.GetProperty("transport").GetString());
        Assert.Equal("npx", body.GetProperty("command").GetString());
        Assert.Equal(["-y", "@mcp/github", "--read-only"], body.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("${GH_TOKEN}", body.GetProperty("env").GetProperty("GITHUB_TOKEN").GetString());
        Assert.Equal("a=b", body.GetProperty("env").GetProperty("LOG").GetString());
        Assert.Equal(["GH_TOKEN"], body.GetProperty("secrets").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("dotenv", body.GetProperty("secretsFile").GetString());
        Assert.Equal("GitHub issues", body.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("url").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("headers").ValueKind);
    }

    [Fact]
    public async Task Mcp_set_puts_an_http_definition()
    {
        _api.Respond("/mcp-servers/linear", """{"name":"linear","transport":"http","url":"https://mcp.linear.app/mcp","revision":1}""");

        Assert.Equal(0, await McpCommands.SetAsync(_client,
            ["linear", "--url", "https://mcp.linear.app/mcp", "--header", "Authorization=Bearer ${LINEAR_KEY}"], json: true));

        var body = Assert.Single(_api.Calls).Json;
        Assert.Equal("http", body.GetProperty("transport").GetString());
        Assert.Equal("https://mcp.linear.app/mcp", body.GetProperty("url").GetString());
        Assert.Equal("Bearer ${LINEAR_KEY}", body.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("command").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("args").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("env").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("secrets").ValueKind);
    }

    [Fact]
    public async Task Mcp_set_from_a_file_sends_it_as_is()
    {
        _api.Respond("/mcp-servers/pw", """{"name":"pw","transport":"stdio","revision":1}""");
        var file = TempJson("""{"transport":"stdio","command":"npx","args":["@playwright/mcp","--secrets","${secrets_file}"],"secrets":["SITE_PASSWORD"],"secretsFile":"dotenv"}""");

        Assert.Equal(0, await McpCommands.SetAsync(_client, ["pw", "--file", file], json: false));

        var body = Assert.Single(_api.Calls).Json;
        Assert.Equal(["transport", "command", "args", "secrets", "secretsFile"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("${secrets_file}", body.GetProperty("args")[2].GetString());
    }

    [Theory]
    [InlineData("--command|npx")]
    [InlineData("github")]
    [InlineData("github|--command|npx|--url|https://x.test")]
    [InlineData("github|--command|npx|--header|A=b")]
    [InlineData("github|--url|https://x.test|--env|A=b")]
    [InlineData("github|--url|https://x.test|--secret|A")]
    [InlineData("github|--file|x.json|--command|npx")]
    [InlineData("github|--command|npx|--env|NOEQUALS")]
    [InlineData("github|--file|/nonexistent/sidehub.json")]
    public async Task Mcp_set_usage_errors_never_call_the_api(string line)
    {
        Assert.Equal(1, await McpCommands.SetAsync(_client, Args(line), json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Mcp_set_refuses_a_file_that_is_not_an_object()
    {
        var file = TempJson("""["npx"]""");

        Assert.Equal(1, await McpCommands.SetAsync(_client, ["github", "--file", file], json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void Mcp_set_spots_literal_secrets()
    {
        var entries = new Dictionary<string, string>
        {
            ["API_KEY"] = "sk-live-123",
            ["GITHUB_TOKEN"] = "${GH_TOKEN}",
            ["Authorization"] = "Bearer ${LINEAR_KEY}",
            ["X-Api-Key"] = "abc",
            ["PORT"] = "3000",
        };

        Assert.Equal(["API_KEY", "X-Api-Key"], McpCommands.LiteralSecrets(entries));
    }

    [Fact]
    public async Task Mcp_delete_sends_a_delete()
    {
        _api.Respond("/mcp-servers/github", "", HttpStatusCode.NoContent);

        Assert.Equal(0, await McpCommands.DeleteAsync(_client, ["github", "--yes"], json: true));
        Assert.Equal($"DELETE {McpPath}/github", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Mcp_delete_needs_a_name()
    {
        Assert.Equal(1, await McpCommands.DeleteAsync(_client, ["--yes"], json: false));
        Assert.Empty(_api.Calls);
    }

    // --- Workflow event triggers ---

    private const string Trigger = """
        {"id":"tr-1","workflowId":"wf-1","type":"drive.item.created","filter":{"folderId":"f-1","includeSubfolders":true,"toStatus":null,"sourceWorkflowId":null},
         "agentId":"agent-1","provider":"claude","inputMapping":{"doc":"title"},"isActive":true,"createdByUserId":"u-1",
         "createdAt":"2026-10-13T08:00:00Z","updatedAt":"2026-10-13T08:00:00Z","lastTriggeredAt":"2026-10-13T09:00:00Z",
         "lastError":"Agent offline","lastErrorAt":"2026-10-13T09:30:00Z"}
        """;

    [Fact]
    public async Task Workflow_triggers_lists_them()
    {
        _api.Respond("/event-triggers", $"[{Trigger}]");

        Assert.Equal(0, await WorkflowTriggerCommands.ListAsync(_client, ["wf-1"], json: false));
        Assert.Equal($"GET {TriggersPath}", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Workflow_trigger_add_posts_a_folder_trigger_on_this_agent()
    {
        _api.Respond("/event-triggers", Trigger, HttpStatusCode.Created);

        Assert.Equal(0, await WorkflowTriggerCommands.AddAsync(_client, Args(
            "wf-1|--type|drive.item.created|--folder|f-1|--subfolders|--map|doc=title|--map|text=content"), json: false));

        var call = Assert.Single(_api.Calls);
        Assert.Equal($"POST {TriggersPath}", $"{call.Method} {call.PathAndQuery}");
        var body = call.Json;
        Assert.Equal("drive.item.created", body.GetProperty("type").GetString());
        Assert.Equal("agent-1", body.GetProperty("agentId").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("provider").ValueKind);
        Assert.Equal("f-1", body.GetProperty("filter").GetProperty("folderId").GetString());
        Assert.True(body.GetProperty("filter").GetProperty("includeSubfolders").GetBoolean());
        Assert.Equal("title", body.GetProperty("inputMapping").GetProperty("doc").GetString());
        Assert.Equal("content", body.GetProperty("inputMapping").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Workflow_trigger_add_chains_a_workflow_with_its_agent_and_provider()
    {
        _api.Respond("/event-triggers", Trigger, HttpStatusCode.Created);

        Assert.Equal(0, await WorkflowTriggerCommands.AddAsync(_client, Args(
            "wf-1|--type|workflow.completed|--source-workflow|wf-0|--agent|agent-2|--provider|codex"), json: true));

        var body = Assert.Single(_api.Calls).Json;
        Assert.Equal("wf-0", body.GetProperty("filter").GetProperty("sourceWorkflowId").GetString());
        Assert.Equal("agent-2", body.GetProperty("agentId").GetString());
        Assert.Equal("codex", body.GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("inputMapping").ValueKind);
    }

    [Fact]
    public async Task Workflow_trigger_add_reads_a_filter_file()
    {
        _api.Respond("/event-triggers", Trigger, HttpStatusCode.Created);
        var file = TempJson("""{"toStatus":"in-progress"}""");

        Assert.Equal(0, await WorkflowTriggerCommands.AddAsync(_client, ["wf-1", "--type", "task.status_changed", "--filter-file", file], json: true));

        var filter = Assert.Single(_api.Calls).Json.GetProperty("filter");
        Assert.Equal(["toStatus"], filter.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("--type|drive.item.created")]
    [InlineData("wf-1")]
    [InlineData("wf-1|--type|drive.created")]
    [InlineData("wf-1|--type|workflow.completed")]
    [InlineData("wf-1|--type|drive.item.created|--to-status|completed")]
    [InlineData("wf-1|--type|task.status_changed|--to-status|done")]
    [InlineData("wf-1|--type|drive.item.created|--subfolders")]
    [InlineData("wf-1|--type|drive.item.created|--folder|f-1|--filter-file|x.json")]
    [InlineData("wf-1|--type|drive.item.created|--map|title")]
    public async Task Workflow_trigger_add_usage_errors_never_call_the_api(string line)
    {
        Assert.Equal(1, await WorkflowTriggerCommands.AddAsync(_client, Args(line), json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Workflow_trigger_add_needs_an_agent()
    {
        using var client = new SideHubApiClient(_api.Url, "sh_pty_test", WorkspaceId);

        Assert.Equal(1, await WorkflowTriggerCommands.AddAsync(client, ["wf-1", "--type", "drive.item.created"], json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Workflow_trigger_update_patches_only_what_changes()
    {
        _api.Respond("/event-triggers/tr-1", Trigger);

        Assert.Equal(0, await WorkflowTriggerCommands.UpdateAsync(_client, ["wf-1", "tr-1", "--active", "false"], json: false));

        var call = Assert.Single(_api.Calls);
        Assert.Equal($"PATCH {TriggersPath}/tr-1", $"{call.Method} {call.PathAndQuery}");
        var body = call.Json;
        Assert.False(body.GetProperty("isActive").GetBoolean());
        foreach (var kept in new[] { "filter", "agentId", "provider", "inputMapping" })
            Assert.Equal(JsonValueKind.Null, body.GetProperty(kept).ValueKind);
    }

    [Fact]
    public async Task Workflow_trigger_update_replaces_the_filter_and_clears_the_mapping()
    {
        _api.Respond("/event-triggers/tr-1", Trigger);

        Assert.Equal(0, await WorkflowTriggerCommands.UpdateAsync(_client,
            ["wf-1", "tr-1", "--to-status", "in-progress", "--clear-map", "--agent", "agent-2"], json: true));

        var body = Assert.Single(_api.Calls).Json;
        Assert.Equal("in-progress", body.GetProperty("filter").GetProperty("toStatus").GetString());
        Assert.Empty(body.GetProperty("inputMapping").EnumerateObject());
        Assert.Equal("agent-2", body.GetProperty("agentId").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("isActive").ValueKind);
    }

    [Theory]
    [InlineData("wf-1|tr-1")]
    [InlineData("wf-1|--active|false")]
    [InlineData("wf-1|tr-1|--active|maybe")]
    [InlineData("wf-1|tr-1|--map|a=b|--clear-map")]
    [InlineData("wf-1|tr-1|--folder|f-1|--to-status|completed")]
    public async Task Workflow_trigger_update_usage_errors_never_call_the_api(string line)
    {
        Assert.Equal(1, await WorkflowTriggerCommands.UpdateAsync(_client, Args(line), json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Workflow_trigger_delete_sends_a_delete()
    {
        _api.Respond("/event-triggers/tr-1", "", HttpStatusCode.NoContent);

        Assert.Equal(0, await WorkflowTriggerCommands.DeleteAsync(_client, ["wf-1", "tr-1", "--yes"], json: false));
        Assert.Equal($"DELETE {TriggersPath}/tr-1", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Workflow_trigger_delete_needs_both_ids()
    {
        Assert.Equal(1, await WorkflowTriggerCommands.DeleteAsync(_client, ["tr-1", "--yes"], json: false));
        Assert.Empty(_api.Calls);
    }

    // --- Costs ---

    private const string Usage = """
        {"from":"2026-09-13T00:00:00Z","to":"2026-10-13T00:00:00Z","groupBy":"day","split":"provider","tz":"UTC",
         "totals":{"sessions":12,"inputTokens":1000,"outputTokens":2000,"cacheReadTokens":3000,"cacheWriteTokens":400,"apiCost":1.234,
                   "subscriptionEquivalent":10.5,"hasUnknownModels":true,"cacheHitRate":0.75,"runs":5,"runsSucceeded":4,"activeAgents":2,
                   "totalAgents":3,"durationMs":3723000},
         "previousTotals":{"sessions":10,"inputTokens":0,"outputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0,"apiCost":0,
                   "subscriptionEquivalent":5,"hasUnknownModels":false,"cacheHitRate":0,"runs":0,"runsSucceeded":0,"activeAgents":1,
                   "totalAgents":3,"durationMs":0},
         "rows":[{"key":"2026-10-12","label":"2026-10-12","color":null,"sessions":12,"inputTokens":1000,"outputTokens":2000,
                  "cacheReadTokens":3000,"cacheWriteTokens":400,"apiCost":1.234,"subscriptionEquivalent":10.5,"hasUnknownModels":true,
                  "durationMs":3723000,"splits":{"claude":{"tokens":6400,"apiCost":1.234,"subscriptionEquivalent":10.5}}}],
         "splitLabels":{"claude":"Claude"}}
        """;

    private static Dictionary<string, string?> Query(string pathAndQuery)
    {
        var query = HttpUtility.ParseQueryString(new Uri("http://x" + pathAndQuery).Query);
        return query.AllKeys.ToDictionary(k => k!, k => query[k]);
    }

    [Fact]
    public async Task Cockpit_usage_asks_for_the_last_30_days_by_day()
    {
        _api.Respond("/cockpit/usage", Usage);

        Assert.Equal(0, await CostCommands.UsageAsync(_client, [], json: false));

        var request = Assert.Single(_api.Requests);
        Assert.StartsWith("/api/cockpit/usage?", request);
        var query = Query(request);
        Assert.Equal(["from", "to", "workspaceId", "groupBy"], query.Keys);
        Assert.Equal(WorkspaceId, query["workspaceId"]);
        Assert.Equal("day", query["groupBy"]);
        var from = DateTime.Parse(query["from"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse(query["to"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        Assert.Equal(TimeSpan.FromDays(30), to - from);
        Assert.InRange(DateTime.UtcNow - to, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cockpit_usage_sends_its_grouping_split_and_zone()
    {
        _api.Respond("/cockpit/usage", Usage);

        Assert.Equal(0, await CostCommands.UsageAsync(_client,
            Args("--since|2026-10-01|--until|2026-10-08|--by|DAY|--split|provider|--tz|Europe/Paris"), json: true));

        var query = Query(Assert.Single(_api.Requests));
        Assert.Equal("2026-10-01T00:00:00.0000000Z", query["from"]);
        Assert.Equal("2026-10-08T00:00:00.0000000Z", query["to"]);
        Assert.Equal("day", query["groupBy"]);
        Assert.Equal("provider", query["split"]);
        Assert.Equal("Europe/Paris", query["tz"]);
    }

    [Theory]
    [InlineData("--by|week")]
    [InlineData("--by|model|--split|provider")]
    [InlineData("--split|team")]
    [InlineData("--since|yesterday")]
    [InlineData("--since|2026-10-08|--until|2026-10-01")]
    [InlineData("--since|2024-01-01|--until|2026-01-01")]
    public async Task Cockpit_usage_usage_errors_never_call_the_api(string line)
    {
        Assert.Equal(1, await CostCommands.UsageAsync(_client, Args(line), json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Cockpit_reliability_asks_for_the_last_7_days()
    {
        _api.Respond("/cockpit/reliability", """
            {"endReasons":[{"reason":"callback-complete","succeeded":true,"count":40},{"reason":"timeout","succeeded":false,"count":2}],
             "workflows":[{"workflowId":"wf-1","name":"Morning","runs":7,"p50Ms":65000,"p95Ms":4000000}],
             "failingSchedulers":[{"scheduledPromptId":"s-1","title":"SEO","failures":2,"total":7}],
             "agentsOffline":[{"agentId":"a-9","name":"laptop","workspaceId":"w","offlineSince":"2026-10-01T00:00:00Z"}]}
            """);

        Assert.Equal(0, await CostCommands.ReliabilityAsync(_client, [], json: false));

        var request = Assert.Single(_api.Requests);
        Assert.StartsWith("/api/cockpit/reliability?", request);
        var query = Query(request);
        Assert.Equal(["from", "to", "workspaceId"], query.Keys);
        var from = DateTime.Parse(query["from"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse(query["to"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        Assert.Equal(TimeSpan.FromDays(7), to - from);
    }

    [Fact]
    public async Task Cockpit_reliability_refuses_an_unreadable_range()
    {
        Assert.Equal(1, await CostCommands.ReliabilityAsync(_client, ["--until", "later"], json: false));
        Assert.Empty(_api.Calls);
    }
}
