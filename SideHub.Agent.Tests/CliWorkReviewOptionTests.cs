using System.Text.Json;
using SideHub.Cli;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>
/// <c>--work-review on|off</c> of <c>workflow update</c> and <c>scheduler update</c>: the <c>skipWorkReview</c> each
/// sends against a local fake API, and the usage errors that never reach it.
/// </summary>
public sealed class CliWorkReviewOptionTests : IDisposable
{
    private const string WorkspaceId = "11111111-1111-1111-1111-111111111111";

    private readonly FakeSideHubApi _api = new();
    private readonly SideHubApiClient _client;

    public CliWorkReviewOptionTests() => _client = new SideHubApiClient(_api.Url, "sh_pty_test", WorkspaceId, "agent-1");

    public void Dispose()
    {
        _client.Dispose();
        _api.Dispose();
    }

    [Theory]
    [InlineData("off", true)]
    [InlineData("on", false)]
    [InlineData("OFF", true)]
    public async Task Workflow_update_sends_whether_its_steps_skip_the_review(string value, bool skip)
    {
        _api.Respond("/api/workflows/wf-1", """{"id":"wf-1"}""");

        Assert.Equal(0, await WorkflowCommands.UpdateAsync(_client, ["wf-1", "--work-review", value], json: false));

        Assert.Equal("PATCH /api/workflows/wf-1", Assert.Single(_api.Methods));
        var body = _api.Calls[0].Json;
        Assert.Equal(skip, body.GetProperty("skipWorkReview").GetBoolean());
        Assert.False(body.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Scheduler_update_sends_whether_its_run_skips_the_review()
    {
        _api.Respond("/scheduled-prompts/sp-1", """{"id":"sp-1"}""");

        Assert.Equal(0, await SchedulerCommands.UpdateAsync(_client, ["sp-1", "--work-review", "off"], json: false));

        Assert.Equal($"PUT /api/workspaces/{WorkspaceId}/scheduled-prompts/sp-1", Assert.Single(_api.Methods));
        Assert.True(_api.Calls[0].Json.GetProperty("skipWorkReview").GetBoolean());
    }

    [Fact]
    public async Task Update_without_the_option_sends_no_review_setting()
    {
        _api.Respond("/scheduled-prompts/sp-1", """{"id":"sp-1"}""");

        Assert.Equal(0, await SchedulerCommands.UpdateAsync(_client, ["sp-1", "--title", "Diagnostic"], json: false));

        Assert.False(_api.Calls[0].Json.TryGetProperty("skipWorkReview", out _));
    }

    [Fact]
    public async Task A_value_other_than_on_or_off_never_reaches_the_api()
    {
        Assert.Equal(1, await WorkflowCommands.UpdateAsync(_client, ["wf-1", "--work-review", "no"], json: false));
        Assert.Equal(1, await SchedulerCommands.UpdateAsync(_client, ["sp-1", "--work-review", "yes"], json: false));

        Assert.Empty(_api.Methods);
    }
}
