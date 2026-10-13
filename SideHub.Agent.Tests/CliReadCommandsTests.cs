using System.Net;
using SideHub.Cli;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>
/// Read commands of <c>sidehub-cli</c> (runs, cockpit, whoami, executions, archived memory): the routes and filters they
/// send, against a local fake API.
/// </summary>
public sealed class CliReadCommandsTests : IDisposable
{
    private const string WorkspaceId = "11111111-1111-1111-1111-111111111111";
    private readonly FakeSideHubApi _api = new();
    private readonly SideHubApiClient _client;

    public CliReadCommandsTests() => _client = new SideHubApiClient(_api.Url, "sh_pty_test", WorkspaceId, "agent-1");

    public void Dispose()
    {
        _client.Dispose();
        _api.Dispose();
    }

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("24h", 24 * 60)]
    [InlineData("7d", 7 * 24 * 60)]
    public void Since_reads_a_window_back_from_now(string value, int minutes)
    {
        var parsed = RunCommands.ParseTime(value);

        Assert.NotNull(parsed);
        Assert.InRange(DateTime.UtcNow - parsed!.Value, TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Since_reads_a_date_as_utc()
    {
        var parsed = RunCommands.ParseTime("2026-10-01");

        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("0h")]
    [InlineData("12w")]
    public void Since_refuses_what_it_cannot_read(string value) => Assert.Null(RunCommands.ParseTime(value));

    [Fact]
    public async Task Run_list_sends_its_filters()
    {
        _api.Respond("/runs", """{"items":[],"total":0,"page":2,"pageSize":5}""");

        var code = await RunCommands.ListAsync(_client,
            ["--status", "failed", "--runtime", "codex", "--agent", "a-1", "--since", "2026-10-01", "--limit", "5", "--page", "2"], json: true);

        Assert.Equal(0, code);
        var request = Assert.Single(_api.Requests);
        Assert.StartsWith($"/api/workspaces/{WorkspaceId}/runs?", request);
        Assert.Contains("status=failed", request);
        Assert.Contains("runtime=codex", request);
        Assert.Contains("agentId=a-1", request);
        Assert.Contains("from=2026-10-01T00%3A00%3A00.0000000Z", request);
        Assert.Contains("page=2", request);
        Assert.Contains("pageSize=5", request);
    }

    [Fact]
    public async Task Run_list_refuses_an_unreadable_since_without_calling_the_api()
    {
        var code = await RunCommands.ListAsync(_client, ["--since", "yesterday"], json: false);

        Assert.Equal(1, code);
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Run_get_reads_the_run_in_the_workspace()
    {
        _api.Respond("/runs/r-1", """{"id":"r-1","status":"failed","endReason":"process-exit","exitCode":1,"runtime":"claude","errorMessage":"Last output:\nnot logged in"}""");

        Assert.Equal(0, await RunCommands.GetAsync(_client, ["r-1"], json: false));
        Assert.Equal($"/api/workspaces/{WorkspaceId}/runs/r-1", Assert.Single(_api.Requests));
    }

    [Fact]
    public async Task Cockpit_now_is_scoped_to_the_workspace()
    {
        _api.Respond("/cockpit/now", """
            {"attention":[{"kind":"review-pending","title":"Fix login","at":"2026-10-13T08:00:00Z","target":{"reviewId":"rv-1"},"changes":{"files":2,"insertions":12,"deletions":3}}],
             "upcoming":[],"recentMemory":[],"runContexts":{},"agents":[],"recentRuns":[]}
            """);

        Assert.Equal(0, await CockpitCommands.NowAsync(_client, json: false));
        Assert.Equal($"/api/cockpit/now?workspaceId={WorkspaceId}", Assert.Single(_api.Requests));
    }

    [Fact]
    public async Task Whoami_asks_who_the_token_speaks_for()
    {
        _api.Respond("/agent/me", """
            {"token":"terminal","agentId":"agent-1","agentName":"vps","workspaceId":"w","runId":null,"ptySessionId":"term-1",
             "actingUser":{"id":"u","email":"me@example.com","role":"owner"}}
            """);

        Assert.Equal(0, await CockpitCommands.WhoamiAsync(_client, json: false));
        Assert.Equal("/api/agent/me", Assert.Single(_api.Requests));
    }

    [Fact]
    public async Task Execution_runs_name_each_run_after_its_step()
    {
        _api.Respond("/workflow-executions/e-1/runs", """[{"id":"r-1","stepId":"s-1","attempt":1,"status":"succeeded","runtime":"claude"}]""");
        _api.Respond("/workflow-executions/e-1", """{"id":"e-1","stepExecutions":[{"stepId":"s-1","stepName":"Write"}]}""");

        Assert.Equal(0, await WorkflowCommands.ExecutionRunsAsync(_client, ["e-1"], json: false));
        Assert.Equal(["/api/workflow-executions/e-1/runs", "/api/workflow-executions/e-1"], _api.Requests);
    }

    [Fact]
    public async Task Workflow_export_writes_the_document_to_a_file()
    {
        _api.Respond("/export", """{"schema":"sidehub.workflow/v1","name":"SEO"}""");
        var output = Path.Combine(Path.GetTempPath(), $"sidehub-export-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(0, await WorkflowCommands.ExportAsync(_client, ["wf-1", "--output", output], json: false));
            Assert.Equal("/api/workflows/wf-1/export", Assert.Single(_api.Requests));
            Assert.Contains("\"sidehub.workflow/v1\"", await File.ReadAllTextAsync(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Drive_restore_posts_to_the_item()
    {
        _api.Respond("/restore", "", HttpStatusCode.NoContent);

        Assert.Equal(0, await DriveCommands.RestoreAsync(_client, ["d-1"], json: true));
        Assert.Equal("POST /api/drive/d-1/restore", Assert.Single(_api.Methods));
    }

    private const string Review = """
        {"id":"rv-1","agentId":"agent-1","source":"task","provider":"codex","title":"Fix login","status":"pending",
         "pendingSince":"2026-10-13T08:00:00Z","updatedAt":"2026-10-13T08:00:00Z","taskId":"t-1",
         "rounds":[
           {"index":1,"runId":null,"startedAt":"2026-10-13T07:00:00Z","endedAt":"2026-10-13T07:30:00Z","endBranch":"main",
            "files":[{"path":"src/login.ts","oldPath":null,"status":"modified","insertions":10,"deletions":2,"binary":false}],
            "filesTruncated":false,"filesChanged":1,"insertions":10,"deletions":2,
            "commits":[{"sha":"0123456789abcdef","subject":"Fix login"}],"commitsTruncated":false,
            "summary":"Done.","error":null,"comment":null,"peerReviews":[]},
           {"index":2,"runId":"run-2","startedAt":"2026-10-13T07:40:00Z","endedAt":"2026-10-13T07:50:00Z","endBranch":"main",
            "files":[{"path":"src/a b.ts","oldPath":null,"status":"added","insertions":3,"deletions":0,"binary":false}],
            "filesTruncated":false,"filesChanged":1,"insertions":3,"deletions":0,"commits":[],"commitsTruncated":false,
            "summary":"Fixed.","error":null,"comment":"Handle empty passwords",
            "peerReviews":[{"id":"p-1","provider":"claude","model":null,"status":"done","error":null,
              "recommendations":[{"id":"x","severity":"minor","file":"src/a b.ts","line":3,"title":"Rename","detail":null}],"sent":null}]}],
         "decisions":[{"action":"request-fix","at":"2026-10-13T07:35:00Z","userId":"u","comment":"Handle empty passwords","fixRunId":"run-2"}]}
        """;

    [Fact]
    public async Task Review_list_sends_its_filters()
    {
        _api.Respond("/reviews", $"[{Review}]");

        Assert.Equal(0, await ReviewCommands.ListAsync(_client, ["--status", "pending", "--task", "t-1", "--run", "r-1", "--limit", "5"], json: false));

        var request = Assert.Single(_api.Requests);
        Assert.Equal($"/api/workspaces/{WorkspaceId}/reviews?status=pending&taskId=t-1&runId=r-1&take=5", request);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("--limit", "0")]
    public async Task Review_list_refuses_bad_filters_without_calling_the_api(params string[] values)
    {
        string[] args = values.Length == 1 ? ["--status", values[0]] : values;

        Assert.Equal(1, await ReviewCommands.ListAsync(_client, args, json: false));
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Review_get_prints_the_rounds()
    {
        _api.Respond("/reviews/rv-1", Review);

        Assert.Equal(0, await ReviewCommands.GetAsync(_client, ["rv-1"], json: false));
        Assert.Equal("/api/reviews/rv-1", Assert.Single(_api.Requests));
    }

    [Fact]
    public async Task Review_diff_reads_the_last_round_by_default()
    {
        _api.Respond("/reviews/rv-1", Review);
        _api.Respond("/diff", "diff --git a/x b/x\n", contentType: "text/x-diff");

        Assert.Equal(0, await ReviewCommands.DiffAsync(_client, ["rv-1"], json: false));
        Assert.Equal(["/api/reviews/rv-1", "/api/reviews/rv-1/diff?round=2"], _api.Requests);
    }

    [Fact]
    public async Task Review_diff_of_one_file_of_a_round()
    {
        _api.Respond("/diff", "diff --git a/x b/x\n", contentType: "text/x-diff");

        Assert.Equal(0, await ReviewCommands.DiffAsync(_client, ["rv-1", "--round", "1", "--path", "src/a b.ts"], json: false));
        Assert.Equal("/api/reviews/rv-1/diff?round=1&path=src%2Fa%20b.ts", Assert.Single(_api.Requests));
    }

    [Fact]
    public async Task Review_diff_too_large_lists_the_files_to_ask_for()
    {
        _api.Respond("/reviews/rv-1", Review);
        _api.Respond("/diff", """{"error":"The diff is too large: open the files one by one.","code":"too-large"}""", HttpStatusCode.RequestEntityTooLarge);

        Assert.Equal(1, await ReviewCommands.DiffAsync(_client, ["rv-1"], json: false));
        Assert.Equal(["/api/reviews/rv-1", "/api/reviews/rv-1/diff?round=2"], _api.Requests);
    }

    [Fact]
    public async Task Review_diff_surfaces_the_other_refusals()
    {
        _api.Respond("/diff", """{"error":"The agent is offline: the diff is read from its machine.","code":"agent-offline"}""", HttpStatusCode.Conflict);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ReviewCommands.DiffAsync(_client, ["rv-1", "--round", "1"], json: false));
        Assert.Contains("agent is offline", error.Message);
    }

    [Fact]
    public async Task Approval_list_reads_each_execution_waiting_for_a_decision()
    {
        _api.Respond("/cockpit/now", """
            {"attention":[
               {"kind":"approval-pending","title":"Blog","at":"2026-10-13T08:00:00Z","target":{"workflowExecutionId":"e-1"}},
               {"kind":"run-failed","title":"x","at":"2026-10-13T08:00:00Z","target":{"runId":"r-1"}},
               {"kind":"approval-pending","title":"Deploy","at":"2026-10-13T08:00:00Z","target":{"workflowExecutionId":"e-2"}}],
             "upcoming":[],"recentMemory":[],"runContexts":{},"agents":[],"recentRuns":[]}
            """);
        _api.Respond("/workflow-executions/e-1", """
            {"id":"e-1","workflowName":"Blog","stepExecutions":[
              {"stepId":"s-1","order":0,"stepName":"Write","status":"completed","kind":"agent","outputDriveItemId":"d-1"},
              {"stepId":"s-2","order":1,"stepName":"Approve","status":"awaiting-approval","kind":"approval","outputDriveItemId":null,
               "approval":{"instructions":"Check the tone","approverRole":"member","onTimeout":"reject","documentDriveItemId":"d-1"}}]}
            """);
        _api.Respond("/workflow-executions/e-2", """
            {"id":"e-2","workflowName":"Deploy","stepExecutions":[
              {"stepId":"s-9","order":0,"stepName":"Ship","status":"awaiting-approval","kind":"agent","pendingToolApprovalId":"t-1",
               "toolApprovals":[{"id":"t-1","summary":"Bash: git push origin main","rule":"git push","approverRole":"admin","onTimeout":"deny","status":"pending"}]}]}
            """);

        Assert.Equal(0, await ApprovalCommands.ListAsync(_client, [], json: false));
        Assert.Equal([$"/api/cockpit/now?workspaceId={WorkspaceId}", "/api/workflow-executions/e-1", "/api/workflow-executions/e-2"], _api.Requests);
    }

    [Fact]
    public async Task Queue_show_reads_the_agent_of_the_session()
    {
        _api.Respond("/task-queue", """
            {"agentId":"agent-1","state":"paused","pauseReason":"blocker","pausedAt":"2026-10-13T08:00:00Z",
             "current":{"id":"i-0","taskId":"t-0","taskTitle":"Login","cli":"claude","commit":true,"ptySessionId":"task-1","startedAt":"2026-10-13T07:00:00Z"},
             "items":[{"id":"i-1","taskId":"t-1","taskTitle":"Signup","cli":"codex","commit":false,"addedAt":"2026-10-13T07:00:00Z"}],"supported":true}
            """);

        Assert.Equal(0, await QueueCommands.ShowAsync(_client, [], json: false));
        Assert.Equal("/api/agents/agent-1/task-queue", Assert.Single(_api.Requests));
    }

    [Fact]
    public async Task Outgoing_show_reads_the_given_agent()
    {
        _api.Respond("/outgoing", """
            {"agentId":"agent-2","agentName":"vps","online":true,"canPush":true,"branch":"main","remote":"origin","remoteBranch":"main",
             "hasUpstream":true,"head":"abc","ahead":1,"behind":0,"error":null,"updatedAt":"2026-10-13T08:00:00Z",
             "commits":[{"sha":"abc","subject":"Fix","review":{"id":"rv-1","status":"validated","title":"Fix login"}}],
             "commitsTruncated":false,"queueIdle":true,"ready":true}
            """);

        Assert.Equal(0, await OutgoingCommands.ShowAsync(_client, ["--agent", "agent-2"], json: false));
        Assert.Equal("/api/agents/agent-2/outgoing", Assert.Single(_api.Requests));
    }

    [Fact]
    public async Task Secret_list_reads_the_names_of_the_workspace()
    {
        _api.Respond("/secrets", """
            [{"name":"GSC_KEY","createdAt":"2026-10-01T00:00:00Z","updatedAt":"2026-10-01T00:00:00Z","lastUsedAt":null,
              "usedBy":[{"kind":"workflow","id":"wf-1","name":"SEO"}]}]
            """);

        Assert.Equal(0, await SecretCommands.ListAsync(_client, json: false));
        Assert.Equal($"/api/workspaces/{WorkspaceId}/secrets", Assert.Single(_api.Requests));
    }

    [Fact]
    public async Task A_refusal_points_to_whoami()
    {
        _api.Respond("/runs/r-1", """{"error":"This operation is not allowed with a run token."}""", HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => RunCommands.GetAsync(_client, ["r-1"], json: true));

        Assert.Contains("not allowed with a run token", error.Message);
        Assert.Contains("sidehub-cli whoami", error.Message);
    }
}
