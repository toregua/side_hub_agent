using System.Net;
using SideHub.Cli;

namespace SideHub.Agent.Tests;

/// <summary>sidehub-cli sends X-Agent-Token on every request: never in clear to a remote host, never across a redirect.</summary>
public class CliTransportTests
{
    [Theory]
    [InlineData("https://api.sidehub.io", true)]
    [InlineData("http://localhost:5000", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://api.sidehub.io", false)]
    [InlineData("http://10.0.0.5:5000", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    public void Api_url_must_be_https_unless_local(string url, bool allowed) =>
        Assert.Equal(allowed, ApiUrlPolicy.RejectionReason(url) is null);

    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Token, HttpMethod Method)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, request.Headers.TryGetValues("X-Agent-Token", out var v) ? v.Single() : null, request.Method));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpClient Client(Recorder recorder)
    {
        var http = new HttpClient(new SameOriginRedirectHandler(recorder)) { BaseAddress = new Uri("https://api.sidehub.io/") };
        http.DefaultRequestHeaders.Add("X-Agent-Token", "sh_run_secret");
        return http;
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_not_followed()
    {
        var recorder = new Recorder(_ => Redirect(HttpStatusCode.Found, "https://evil.example/steal"));

        using var response = await Client(recorder).GetAsync("api/drive/1");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(recorder.Requests);
    }

    [Theory]
    [InlineData("http://api.sidehub.io/api/drive/2")]
    [InlineData("https://api.sidehub.io:8443/api/drive/2")]
    public async Task A_redirect_changing_scheme_or_port_is_not_followed(string location)
    {
        var recorder = new Recorder(_ => Redirect(HttpStatusCode.MovedPermanently, location));

        using var response = await Client(recorder).GetAsync("api/drive/1");

        Assert.Single(recorder.Requests);
    }

    [Fact]
    public async Task A_same_origin_redirect_is_followed_with_the_token()
    {
        var recorder = new Recorder(r => r.RequestUri!.AbsolutePath == "/api/drive/1"
            ? Redirect(HttpStatusCode.TemporaryRedirect, "/api/drive/2")
            : new HttpResponseMessage(HttpStatusCode.OK));

        using var response = await Client(recorder).PostAsync("api/drive/1", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, recorder.Requests.Count);
        Assert.Equal(new Uri("https://api.sidehub.io/api/drive/2"), recorder.Requests[1].Uri);
        Assert.Equal("sh_run_secret", recorder.Requests[1].Token);
        Assert.Equal(HttpMethod.Post, recorder.Requests[1].Method);
    }

    [Fact]
    public async Task Redirect_loops_stop()
    {
        var recorder = new Recorder(_ => Redirect(HttpStatusCode.Found, "/api/loop"));

        using var response = await Client(recorder).GetAsync("api/loop");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(11, recorder.Requests.Count);
    }
}
