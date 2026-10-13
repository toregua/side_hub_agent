using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
    private readonly FakeApi _api = new();
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

    [Fact]
    public async Task A_refusal_points_to_whoami()
    {
        _api.Respond("/runs/r-1", """{"error":"This operation is not allowed with a run token."}""", HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => RunCommands.GetAsync(_client, ["r-1"], json: true));

        Assert.Contains("not allowed with a run token", error.Message);
        Assert.Contains("sidehub-cli whoami", error.Message);
    }

    /// <summary>
    /// An API on the loopback interface: answers the longest path suffix registered with <see cref="Respond"/> (the
    /// path without its query string) and records each request.
    /// </summary>
    private sealed class FakeApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ConcurrentDictionary<string, (string Body, HttpStatusCode Status)> _responses = new();
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly ConcurrentQueue<string> _methods = new();

        public string Url { get; }
        public IReadOnlyList<string> Requests => _requests.ToList();
        public IReadOnlyList<string> Methods => _methods.ToList();

        public FakeApi()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = ServeAsync();
        }

        public void Respond(string pathSuffix, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            _responses[pathSuffix] = (body, status);

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; }

                var pathAndQuery = context.Request.Url!.PathAndQuery;
                var path = context.Request.Url.AbsolutePath;
                _requests.Enqueue(pathAndQuery);
                _methods.Enqueue($"{context.Request.HttpMethod} {path}");

                var match = _responses.Keys.Where(path.EndsWith).OrderByDescending(k => k.Length).FirstOrDefault();
                var (body, status) = match is null ? ("""{"error":"not found"}""", HttpStatusCode.NotFound) : _responses[match];
                context.Response.StatusCode = (int)status;
                if (body.Length > 0)
                {
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        public void Dispose() => _listener.Close();
    }
}
