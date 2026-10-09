using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SideHub.Agent;

namespace SideHub.Agent.Tests;

/// <summary>Which account Copilot runs with, read from its headless JSON-RPC server, never with its token.</summary>
public class CopilotAuthProbeTests
{
    private const string Connected = """{"ok":true,"protocolVersion":3,"version":"1.0.94"}""";
    private const string MethodNotFound = """{"code":-32601,"message":"Method not found"}""";

    private const string UserAuth = """
        {"authInfo":{"type":"user","host":"https://github.com","login":"octocat","copilotUser":{
          "login":"octocat","access_type_sku":"copilot_for_business_seat_quota","copilot_plan":"business",
          "analytics_tracking_id":"abc","organization_login_list":["acme","acme-labs"],
          "organization_list":[{"id":1,"login":"acme","name":"Acme"}],"enterprise_list":[{"id":42},null],
          "quota_reset_date":"2026-11-01"}}}
        """;

    private const string GhCliAuth = """
        {"authInfo":{"type":"gh-cli","host":"https://github.com","login":"octocat","token":"gho_secret",
          "copilotUser":{"copilot_plan":"individual_pro","access_type_sku":"plus_yearly_subscriber_quota",
          "organization_login_list":[],"enterprise_list":[]}}}
        """;

    private const string EnvAuth = """
        {"authInfo":{"type":"env","host":"https://github.com","token":"ghp_secret","envVar":"GH_TOKEN",
          "copilotUser":{"login":"octocat","copilot_plan":"individual","access_type_sku":"free_limited_copilot",
          "organization_list":[{"id":7,"login":"acme"},null]}}}
        """;

    [Fact]
    public void An_empty_auth_info_means_nobody_is_logged_in()
    {
        Assert.Equal(new CopilotAuth(false, null), Parse("""{"authInfo":{}}"""));
        Assert.Equal(new CopilotAuth(false, null), Parse("{}"));
    }

    [Fact]
    public void A_user_login_reports_its_plan_organizations_and_enterprises()
    {
        var auth = Parse(UserAuth);

        Assert.True(auth.LoggedIn);
        Assert.Equal(new CliAccount("user", "https://github.com", "octocat", "business", "copilot_for_business_seat_quota",
            ["acme", "acme-labs"], ["42"]), auth.Account);
    }

    [Theory]
    [InlineData(GhCliAuth, "gho_secret")]
    [InlineData(EnvAuth, "ghp_secret")]
    public void A_token_carried_by_the_auth_info_is_never_kept(string json, string token)
    {
        var account = Parse(json).Account;

        Assert.NotNull(account);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(account));
    }

    [Fact]
    public void An_env_token_without_login_takes_it_from_the_copilot_user()
    {
        Assert.Equal(new CliAccount("env", "https://github.com", "octocat", "individual", "free_limited_copilot", ["acme"], []),
            Parse(EnvAuth).Account);
    }

    [Fact]
    public void A_new_auth_type_without_copilot_user_is_still_logged_in()
    {
        Assert.Equal(new CopilotAuth(true, new CliAccount("token-provider", "https://github.com", null, null, null, [], [])),
            Parse("""{"authInfo":{"type":"token-provider","host":"https://github.com"}}"""));
    }

    [Fact]
    public async Task Connects_then_reads_the_current_auth()
    {
        var auth = await QueryAsync(method => method switch
        {
            "connect" => Result(Connected),
            "account.getCurrentAuth" => Result(GhCliAuth),
            _ => Error(MethodNotFound),
        }, notifyFirst: true);

        Assert.Equal("gh-cli", auth?.Account?.Source);
    }

    [Fact]
    public async Task Falls_back_to_ping_when_connect_is_unknown()
    {
        var auth = await QueryAsync(method => method switch
        {
            "ping" => Result("""{"message":"pong","timestamp":"t","protocolVersion":2}"""),
            "account.getCurrentAuth" => Result("""{"authInfo":{}}"""),
            _ => Error(MethodNotFound),
        });

        Assert.Equal(new CopilotAuth(false, null), auth);
    }

    [Fact]
    public async Task A_server_without_connect_nor_ping_is_unknown()
    {
        Assert.Null(await QueryAsync(_ => Error(MethodNotFound)));
    }

    [Fact]
    public async Task A_server_that_exits_is_unknown()
    {
        Assert.Null(await QueryAsync(_ => null, closeInsteadOfHanging: true));
    }

    [Fact]
    public async Task A_server_that_does_not_answer_times_out()
    {
        var started = DateTime.UtcNow;

        Assert.Null(await QueryAsync(_ => null, timeout: TimeSpan.FromMilliseconds(200)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    private static CopilotAuth Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CopilotAuthProbe.ParseCurrentAuth(document.RootElement);
    }

    private static string Result(string json) => $$"""{"result":{{json}}}""";

    private static string Error(string json) => $$"""{"error":{{json}}}""";

    /// <summary>
    /// Runs the probe against a fake server answering each method with <paramref name="answer"/> (a "result" or "error"
    /// member), or nothing when it returns null: the server then hangs, or closes its output.
    /// </summary>
    private static async Task<CopilotAuth?> QueryAsync(
        Func<string, string?> answer, bool notifyFirst = false, bool closeInsteadOfHanging = false, TimeSpan? timeout = null)
    {
        using var requests = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverInput = new AnonymousPipeClientStream(PipeDirection.In, requests.ClientSafePipeHandle);
        using var responses = new AnonymousPipeServerStream(PipeDirection.Out);
        using var probeInput = new AnonymousPipeClientStream(PipeDirection.In, responses.ClientSafePipeHandle);
        using var stop = new CancellationTokenSource();

        var server = Task.Run(async () =>
        {
            var reader = new StreamReader(serverInput, Encoding.ASCII);
            try
            {
                while (true)
                {
                    var length = 0;
                    while (await reader.ReadLineAsync(stop.Token) is { } line && line.Length > 0)
                        length = int.Parse(line["Content-Length:".Length..]);
                    if (length == 0)
                        return;
                    var body = new char[length];
                    await reader.ReadBlockAsync(body, stop.Token);
                    using var request = JsonDocument.Parse(new string(body));
                    var id = request.RootElement.GetProperty("id").GetInt32();
                    var method = request.RootElement.GetProperty("method").GetString()!;

                    if (notifyFirst)
                        await WriteAsync(responses, """{"jsonrpc":"2.0","method":"session.event","params":{}}""");
                    if (answer(method) is not { } member)
                    {
                        if (closeInsteadOfHanging)
                            responses.Dispose();
                        return;
                    }
                    await WriteAsync(responses, $$"""{"jsonrpc":"2.0","id":{{id}},{{member[1..^1]}}}""");
                }
            }
            catch (Exception) when (stop.IsCancellationRequested || closeInsteadOfHanging) { }
        });

        var auth = await CopilotAuthProbe.QueryAsync(requests, probeInput, timeout ?? TimeSpan.FromSeconds(5), CancellationToken.None);
        stop.Cancel();
        return auth;
    }

    private static async Task WriteAsync(Stream stream, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
