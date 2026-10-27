using SideHub.Cli;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>
/// <c>sidehub-cli automation</c> (SideHub doc 61): an automation is a workflow, its schedules are the schedulers that
/// launch it. Routes and bodies each command sends, against a local fake API.
/// </summary>
public sealed class CliAutomationCommandsTests : IDisposable
{
    private const string WorkspaceId = "11111111-1111-1111-1111-111111111111";
    private const string Schedules = """
        [{"id":"sc-1","workflowId":"wf-1","cronExpression":"0 9 * * 1","isActive":true},
         {"id":"sc-2","workflowId":"wf-2","cronExpression":"0 3 * * *","isActive":true},
         {"id":"sc-3","workflowId":"wf-1","cronExpression":"0 18 * * 5","isActive":false}]
        """;

    private readonly FakeSideHubApi _api = new();
    private readonly SideHubApiClient _client;

    public CliAutomationCommandsTests() => _client = new SideHubApiClient(_api.Url, "sh_pty_test", WorkspaceId, "agent-1");

    public void Dispose()
    {
        _client.Dispose();
        _api.Dispose();
    }

    [Fact]
    public async Task Create_without_a_schedule_makes_a_one_step_automation_that_completes_on_exit()
    {
        _api.Respond("/workflows", """{"id":"wf-1","name":"Weekly report"}""");
        _api.Respond("/steps", """{"id":"wf-1","steps":[{"id":"st-1"}]}""");

        Assert.Equal(0, await AutomationCommands.CreateAsync(_client,
            ["--name", "Weekly report", "--prompt", "Summarize the week", "--provider", "codex"], json: false));

        Assert.Equal([$"POST /api/workspaces/{WorkspaceId}/workflows", "POST /api/workflows/wf-1/steps"], _api.Methods);
        var step = _api.Calls[1].Json;
        Assert.Equal("Summarize the week", step.GetProperty("prompt").GetString());
        Assert.Equal("Weekly report", step.GetProperty("outputPath").GetString());
        Assert.Equal("codex", step.GetProperty("provider").GetString());
        Assert.True(step.GetProperty("completesOnExit").GetBoolean());
    }

    [Fact]
    public async Task Create_with_a_schedule_lets_SideHub_make_the_automation()
    {
        _api.Respond("/scheduled-prompts", """{"id":"sc-1","workflowId":"wf-1","isActive":true}""");

        Assert.Equal(0, await AutomationCommands.CreateAsync(_client,
            ["--name", "Weekly report", "--prompt", "Summarize the week", "--cron", "0 9 * * 1"], json: false));

        Assert.Equal($"POST /api/workspaces/{WorkspaceId}/scheduled-prompts", Assert.Single(_api.Methods));
        var body = _api.Calls[0].Json;
        Assert.Equal("Summarize the week", body.GetProperty("prompt").GetString());
        Assert.Equal("agent-1", body.GetProperty("agentId").GetString());
        Assert.False(body.TryGetProperty("workflowId", out _));
    }

    [Fact]
    public async Task Pause_pauses_only_the_schedules_of_the_automation()
    {
        _api.Respond("/scheduled-prompts", Schedules);
        _api.Respond("/pause", """{"id":"x"}""");

        Assert.Equal(0, await AutomationCommands.SetSchedulesActiveAsync(_client, ["wf-1"], json: false, active: false));

        Assert.Equal([
            $"GET /api/workspaces/{WorkspaceId}/scheduled-prompts",
            $"POST /api/workspaces/{WorkspaceId}/scheduled-prompts/sc-1/pause",
            $"POST /api/workspaces/{WorkspaceId}/scheduled-prompts/sc-3/pause",
        ], _api.Methods);
    }

    [Fact]
    public async Task Delete_removes_the_schedules_then_the_automation()
    {
        _api.Respond("/scheduled-prompts", Schedules);
        _api.Respond("/scheduled-prompts/sc-1", "");
        _api.Respond("/scheduled-prompts/sc-3", "");
        _api.Respond("/workflows/wf-1", "");

        Assert.Equal(0, await AutomationCommands.DeleteAsync(_client, ["wf-1", "--yes"], json: false));

        Assert.Equal([
            $"GET /api/workspaces/{WorkspaceId}/scheduled-prompts",
            $"DELETE /api/workspaces/{WorkspaceId}/scheduled-prompts/sc-1",
            $"DELETE /api/workspaces/{WorkspaceId}/scheduled-prompts/sc-3",
            "DELETE /api/workflows/wf-1",
        ], _api.Methods);
    }

    [Fact]
    public async Task Schedule_add_launches_the_automation_on_a_cron()
    {
        _api.Respond("/workflows/wf-1", """{"id":"wf-1","name":"Weekly report"}""");
        _api.Respond("/scheduled-prompts", """{"id":"sc-9","workflowId":"wf-1","isActive":false}""");

        Assert.Equal(0, await AutomationCommands.ScheduleAsync(_client, ["add", "wf-1", "--cron", "0 7 * * *"], json: false));

        Assert.Equal(["GET /api/workflows/wf-1", $"POST /api/workspaces/{WorkspaceId}/scheduled-prompts"], _api.Methods);
        var body = _api.Calls[1].Json;
        Assert.Equal("wf-1", body.GetProperty("workflowId").GetString());
        Assert.Equal("Weekly report", body.GetProperty("title").GetString());
        Assert.False(body.TryGetProperty("prompt", out _));
    }

    [Fact]
    public async Task Create_without_a_prompt_never_reaches_the_API()
    {
        Assert.Equal(1, await AutomationCommands.CreateAsync(_client, ["--name", "Weekly report"], json: false));
        Assert.Empty(_api.Methods);
    }
}
