using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using SideHub.Agent;

namespace SideHub.Agent.Tests;

public class DiagnosticReporterTests
{
    private const string Token = "sh_agent_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";

    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private static Recorder Answering(HttpStatusCode status = HttpStatusCode.Accepted) => new(_ => new HttpResponseMessage(status));

    private static DiagnosticReporter Reporter(HttpMessageHandler handler) =>
        DiagnosticReporter.Create("https://api.sidehub.io/api", Token, _ => { }, handler)!;

    [Fact]
    public async Task Sends_the_token_prefix_never_the_token()
    {
        var recorder = Answering();

        Assert.True(await Reporter(recorder).ReportAsync(DiagnosticReasons.HandshakeRejected, "HTTP 401"));

        var (uri, body) = Assert.Single(recorder.Requests);
        Assert.Equal("https://api.sidehub.io/api/agent/diagnostics", uri.ToString());
        Assert.DoesNotContain(Token, body);
        var json = JsonNode.Parse(body)!;
        Assert.Equal(Token[..16], (string?)json["tokenPrefix"]);
        Assert.Equal("handshake-rejected", (string?)json["reason"]);
        Assert.Equal("HTTP 401", (string?)json["detail"]);
        Assert.False(string.IsNullOrEmpty((string?)json["agentVersion"]));
        Assert.False(string.IsNullOrEmpty((string?)json["os"]));
    }

    [Fact]
    public async Task Each_cause_is_sent_once_per_process()
    {
        var recorder = Answering(HttpStatusCode.TooManyRequests);
        var reporter = Reporter(recorder);

        await reporter.ReportAsync(DiagnosticReasons.HandshakeRejected, "HTTP 401");
        await reporter.ReportAsync(DiagnosticReasons.HandshakeRejected, "HTTP 401");
        await reporter.ReportAsync(DiagnosticReasons.CliMissing, null);

        Assert.Equal(2, recorder.Requests.Count);
    }

    [Fact]
    public async Task A_cause_that_could_not_be_sent_is_retried_a_bounded_number_of_times()
    {
        var recorder = new Recorder(_ => throw new HttpRequestException("Name or service not known"));
        var reporter = Reporter(recorder);

        for (var i = 0; i < 10; i++)
            Assert.False(await reporter.ReportAsync(DiagnosticReasons.BackendUnreachable, "dns"));

        Assert.Equal(DiagnosticReporter.MaxAttemptsPerReason, recorder.Requests.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sh_agent_x")]
    public void No_reporter_without_a_token_to_tie_the_report_to(string? token) =>
        Assert.Null(DiagnosticReporter.Create("https://api.sidehub.io/api", token, _ => { }));

    [Fact]
    public void No_reporter_over_plain_http_to_a_remote_host()
    {
        Assert.Null(DiagnosticReporter.Create("http://api.sidehub.io/api", Token, _ => { }));
        Assert.NotNull(DiagnosticReporter.Create("http://localhost:5000/api", Token, _ => { }));
    }

    [Fact]
    public void The_api_comes_from_the_config_websocket_url()
    {
        var config = new AgentConfig { SidehubUrl = "wss://api.sidehub.io/ws/agent", AgentToken = Token };

        Assert.NotNull(DiagnosticReporter.ForConfig(config, _ => { }));
        Assert.Null(DiagnosticReporter.ForConfig(new AgentConfig { SidehubUrl = "wss://api.sidehub.io/ws/agent" }, _ => { }));
    }

    [Fact]
    public void Sanitize_masks_tokens_home_folder_and_user_name()
    {
        var detail = "Error: cannot open /home/alice/project/pty.node (token sh_agent_SECRET123)\nfrom /tmp/alice/x and C:\\Users\\Alice";

        var sanitized = DiagnosticReporter.Sanitize(detail, homeDirectory: "/home/alice", userName: "alice");

        Assert.Equal("Error: cannot open ~/project/pty.node (token sh_***) from /tmp/<user>/x and C:\\Users\\<user>", sanitized);
    }

    [Fact]
    public void Sanitize_keeps_a_user_name_that_is_a_plain_word()
    {
        Assert.Equal("`sidehub-agent start` run as root without --allow-root",
            DiagnosticReporter.Sanitize("`sidehub-agent start` run as root without --allow-root", homeDirectory: "/root", userName: "root"));
        Assert.Equal("~/a and /rootfs/b", DiagnosticReporter.Sanitize("/root/a and /rootfs/b", homeDirectory: "/root", userName: "root"));
    }

    [Fact]
    public void Sanitize_bounds_the_length()
    {
        var sanitized = DiagnosticReporter.Sanitize(new string('x', 2000), "/home/alice", "alice")!;

        Assert.Equal(DiagnosticReporter.MaxDetailLength, sanitized.Length);
    }

    [Fact]
    public void A_401_handshake_is_a_rejected_token()
    {
        var (reason, detail) = DiagnosticReporter.ClassifyConnectFailure(
            new WebSocketException("The server returned status code '401' when status code '101' was expected."), HttpStatusCode.Unauthorized);

        Assert.Equal(DiagnosticReasons.HandshakeRejected, reason);
        Assert.StartsWith("HTTP 401", detail);
    }

    [Fact]
    public void Dns_failure_is_an_unreachable_backend()
    {
        var ex = new WebSocketException("Unable to connect to the remote server",
            new HttpRequestException("Name or service not known (api.sidehub.io:443)", new SocketException((int)SocketError.HostNotFound)));

        var (reason, detail) = DiagnosticReporter.ClassifyConnectFailure(ex, 0);

        Assert.Equal(DiagnosticReasons.BackendUnreachable, reason);
        Assert.StartsWith("socket HostNotFound:", detail);
        Assert.Contains("Name or service not known", detail);
    }

    [Fact]
    public void Tls_failure_is_named()
    {
        var ex = new WebSocketException("Unable to connect", new HttpRequestException("SSL failed", new AuthenticationException("certificate untrusted")));

        Assert.StartsWith("TLS:", DiagnosticReporter.ClassifyConnectFailure(ex, 0).Detail);
    }

    [Fact]
    public void Proxy_answering_an_http_error_is_an_unreachable_backend()
    {
        var (reason, detail) = DiagnosticReporter.ClassifyConnectFailure(new WebSocketException("status code '502'"), HttpStatusCode.BadGateway);

        Assert.Equal(DiagnosticReasons.BackendUnreachable, reason);
        Assert.StartsWith("HTTP 502:", detail);
    }
}

public class StartupChecksTests
{
    [Fact]
    public void The_node_error_line_is_picked_from_a_crash_output()
    {
        const string output = """
            node:internal/modules/cjs/loader:1460
              return process.dlopen(module, path.toNamespacedPath(filename));
            Error: The module '/usr/local/lib/sidehub-agent/pty-helper/node_modules/node-pty/build/Release/pty.node' was compiled against a different Node.js version using NODE_MODULE_VERSION 115.
                at Module._extensions..node (node:internal/modules/cjs/loader:1460:18)
            """;

        Assert.StartsWith("Error: The module", StartupChecks.FirstErrorLine(output));
    }

    [Fact]
    public void Without_an_error_line_the_first_line_is_kept() =>
        Assert.Equal("Segmentation fault", StartupChecks.FirstErrorLine("\nSegmentation fault\n"));
}
