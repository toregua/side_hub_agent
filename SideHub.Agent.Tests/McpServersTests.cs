using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SideHub.Agent.Models;
using SideHub.Cli.Launch;
using Server = SideHub.Cli.Launch.McpServers.Server;

namespace SideHub.Agent.Tests;

/// <summary>The workspace MCP servers of a run (pty.start mcpServers): checked by the agent, handed to the terminal in
/// $SIDEHUB_PTY_MCP_SERVERS, and given to each CLI by `sidehub-cli launch` through per-invocation options, the
/// secrets staying ${NAME} references read from the environment.</summary>
public class McpServersTests
{
    private static readonly Guid Minted = Guid.Parse("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14");
    private const string Session = "8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14";

    private static readonly Server GitHub = new("github", McpServers.Http,
        Url: "https://api.githubcopilot.com/mcp/", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer ${GITHUB_TOKEN}" });

    private static readonly Server Fs = new("fs", McpServers.Stdio,
        Command: "npx", Args: ["-y", "@modelcontextprotocol/server-filesystem", "."], Env: new Dictionary<string, string> { ["FS_KEY"] = "${FS_KEY}" });

    private static CliLaunchPlan Plan(string cli, string[] args, IReadOnlyList<Server> servers, string? prompt = null,
        bool posixShell = true, string? geminiSystemSettings = null) =>
        CliLaunchPlan.For(cli, args, prompt, new Version(0, 62, 0), () => Minted, null,
            new CliLaunchPlan.McpSetup(servers, posixShell, geminiSystemSettings));

    // ---- agent: what reaches the terminal ----

    private static PtyMcpServer Stdio(string name, string command, List<string>? args = null, Dictionary<string, string>? env = null) =>
        new() { Name = name, Revision = 1, Transport = "stdio", Command = command, Args = args, Env = env };

    private static PtyMcpServer Http(string name, string url, Dictionary<string, string>? headers = null) =>
        new() { Name = name, Revision = 3, Transport = "http", Url = url, Headers = headers };

    private static IReadOnlyList<Server> ThroughAgent(List<PtyMcpServer> servers, string[] secrets, out IReadOnlyList<string> rejected)
    {
        var value = McpServerPolicy.ToEnvironmentValue(servers, secrets, null, out rejected);
        var warnings = new List<string>();
        var parsed = McpServers.Parse(value, warnings);
        Assert.Empty(warnings);
        return parsed;
    }

    [Fact]
    public void Agent_hands_the_servers_to_the_launcher_with_their_references_untouched()
    {
        Assert.Equal(McpServerPolicy.EnvironmentKey, McpServers.Variable);

        var servers = ThroughAgent([
            Http("github", "https://api.githubcopilot.com/mcp/", new() { ["Authorization"] = "Bearer ${GITHUB_TOKEN}" }),
            Stdio("fs", "npx", ["-y", "@modelcontextprotocol/server-filesystem", "."], new() { ["API_KEY"] = "${FS_KEY}", ["MODE"] = "ro" }),
        ], ["GITHUB_TOKEN", "FS_KEY"], out var rejected);

        Assert.Empty(rejected);
        Assert.Equal(["github", "fs"], servers.Select(s => s.Name));
        Assert.Equal("Bearer ${GITHUB_TOKEN}", servers[0].Headers["Authorization"]);
        Assert.Equal("https://api.githubcopilot.com/mcp/", servers[0].Url);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "."], servers[1].Args);
        Assert.Equal("${FS_KEY}", servers[1].Env["API_KEY"]);
    }

    [Theory]
    [InlineData("Bearer ${SIDEHUB_AGENT_TOKEN}")]   // the run token is not a workspace secret
    [InlineData("${PATH}")]
    [InlineData("${OTHER_SECRET}")]                  // a secret this run did not get
    [InlineData("${GITHUB_TOKEN:-fallback}")]
    [InlineData("${github_token}")]
    [InlineData("Bearer ${GITHUB_TOKEN")]
    public void Agent_leaves_out_a_server_reading_anything_but_this_runs_secrets(string header)
    {
        var servers = ThroughAgent([Http("github", "https://h.example/mcp", new() { ["Authorization"] = header })], ["GITHUB_TOKEN"], out var rejected);

        Assert.Empty(servers);
        Assert.Single(rejected);
        Assert.StartsWith("github (", rejected[0]);
        Assert.DoesNotContain(header, rejected[0]);
    }

    [Theory]
    [InlineData("http://example.com/mcp", false)]
    [InlineData("ftp://example.com/mcp", false)]
    [InlineData("mcp.example.com", false)]
    [InlineData("http://127.0.0.1:8080/mcp", true)]
    [InlineData("http://localhost:8080/mcp", true)]
    [InlineData("https://example.com/mcp?key=${GITHUB_TOKEN}", true)]
    public void Agent_takes_https_servers_and_plain_http_only_to_this_machine(string url, bool kept)
    {
        var servers = ThroughAgent([Http("remote", url)], ["GITHUB_TOKEN"], out _);

        Assert.Equal(kept, servers.Count == 1);
    }

    [Fact]
    public void Agent_leaves_out_invalid_or_duplicate_servers()
    {
        var servers = ThroughAgent([
            Stdio("Bad Name", "npx"),
            Stdio("cmd", "${FS_KEY}"),
            Stdio("vars", "npx", env: new() { ["BAD-NAME"] = "x" }),
            new PtyMcpServer { Name = "sse", Transport = "sse", Url = "https://x.example" },
            Http("hdr", "https://x.example", new() { ["X-A"] = "a\r\nX-B: b" }),
            Stdio("fs", "npx"),
            Stdio("fs", "uvx"),
        ], ["FS_KEY"], out var rejected);

        Assert.Equal(["fs"], servers.Select(s => s.Name));
        Assert.Equal("npx", servers[0].Command);
        Assert.Equal(6, rejected.Count);
        Assert.Contains("? (invalid name)", rejected);
        Assert.Contains("fs (duplicate name)", rejected);
    }

    [Fact]
    public void Agent_sets_no_variable_without_servers_and_the_backend_cannot_set_it_through_additionalEnv()
    {
        Assert.Null(McpServerPolicy.ToEnvironmentValue([], ["X"], null, out _));
        Assert.Null(McpServerPolicy.ToEnvironmentValue(null, [], null, out _));

        var env = PtyEnvironmentPolicy.FilterAdditionalEnv(
            new Dictionary<string, string> { [McpServerPolicy.EnvironmentKey] = "[]" }, out var rejected);
        Assert.Empty(env);
        Assert.Equal([McpServerPolicy.EnvironmentKey], rejected);
    }

    // ---- claude ----

    [Fact]
    public void Claude_gets_only_the_runs_servers_inline_and_strict_with_the_prompt_last()
    {
        var plan = Plan("claude", ["-p", "--model", "opus"], [GitHub, Fs], "fix it");

        Assert.Equal("--mcp-config", plan.Arguments[0]);
        // --mcp-config takes every argument up to the next option: --strict-mcp-config closes it.
        Assert.Equal("--strict-mcp-config", plan.Arguments[2]);
        Assert.Equal(["--session-id", Session, "-p", "--model", "opus", "fix it"], plan.Arguments.Skip(3));
        Assert.Empty(plan.Warnings);

        var config = JsonNode.Parse(plan.Arguments[1])!["mcpServers"]!;
        Assert.Equal("http", (string?)config["github"]!["type"]);
        Assert.Equal("https://api.githubcopilot.com/mcp/", (string?)config["github"]!["url"]);
        // Claude expands ${NAME} from its environment: the secret value is never on the command line.
        Assert.Equal("Bearer ${GITHUB_TOKEN}", (string?)config["github"]!["headers"]!["Authorization"]);
        Assert.Equal("stdio", (string?)config["fs"]!["type"]);
        Assert.Equal("npx", (string?)config["fs"]!["command"]);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "."], config["fs"]!["args"]!.AsArray().Select(a => (string?)a));
        Assert.Equal("${FS_KEY}", (string?)config["fs"]!["env"]!["FS_KEY"]);
    }

    [Theory]
    [InlineData("mcp", "list")]
    [InlineData("--version", null)]
    public void No_servers_for_a_command_that_starts_no_conversation(string first, string? second)
    {
        var plan = Plan("claude", second is null ? [first] : [first, second], [GitHub]);

        Assert.DoesNotContain("--mcp-config", plan.Arguments);
    }

    [Fact]
    public void Nothing_added_without_servers()
    {
        var plan = CliLaunchPlan.For("claude", ["-p"], "go", null, () => Minted);
        var empty = Plan("codex", ["exec"], []);

        Assert.Equal(["--session-id", Session, "-p", "go"], plan.Arguments);
        Assert.Equal(["exec"], empty.Arguments);
        Assert.Null(plan.GeminiSystemSettings);
    }

    // ---- codex ----

    private static string CodexServer(CliLaunchPlan plan, string name)
    {
        var index = plan.Arguments.ToList().FindIndex(a => a.StartsWith($"mcp_servers.{name}=", StringComparison.Ordinal));
        Assert.True(index > 0, $"no -c mcp_servers.{name}");
        Assert.Equal("-c", plan.Arguments[index - 1]);
        return plan.Arguments[index][$"mcp_servers.{name}=".Length..];
    }

    [Fact]
    public void Codex_reads_a_bearer_token_and_whole_header_secrets_from_the_environment()
    {
        var server = GitHub with
        {
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer ${GITHUB_TOKEN}",
                ["X-Api-Key"] = "${API_KEY}",
                ["X-Toolsets"] = "repos,issues",
            },
        };
        var plan = Plan("codex", ["exec", "--json"], [server], "go");

        Assert.Equal(
            """{url="https://api.githubcopilot.com/mcp/",bearer_token_env_var="GITHUB_TOKEN",http_headers={"X-Toolsets"="repos,issues"},env_http_headers={"X-Api-Key"="API_KEY"}}""",
            CodexServer(plan, "github"));
        // Root options go before the subcommand; the prompt stays last.
        Assert.Equal(["exec", "--json", "go"], plan.Arguments.Skip(2));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Codex_forwards_variables_named_after_their_secret()
    {
        var server = Fs with { Env = new Dictionary<string, string> { ["FS_KEY"] = "${FS_KEY}", ["MODE"] = "ro" } };
        var plan = Plan("codex", [], [server]);

        Assert.Equal(
            """{command="npx",args=["-y","@modelcontextprotocol/server-filesystem","."],env={"MODE"="ro"},env_vars=["FS_KEY"]}""",
            CodexServer(plan, "fs"));
    }

    [Fact]
    public void Codex_starts_a_server_needing_secrets_elsewhere_through_sh_which_reads_them_from_its_environment()
    {
        if (OperatingSystem.IsWindows())
            return;
        var server = new Server("probe", McpServers.Stdio,
            Command: "/bin/sh",
            Args: ["-c", "printf '%s\\n' \"$API_KEY\" \"$MODE\" \"$@\"", "zero", "it's", "--key=${FS_KEY}", "$HOME", ""],
            Env: new Dictionary<string, string> { ["API_KEY"] = "x-${FS_KEY}", ["MODE"] = "ro" });
        var plan = Plan("codex", [], [server]);

        var table = CodexServer(plan, "probe");
        Assert.StartsWith("""{command="/bin/sh",args=["-c",""", table);
        Assert.EndsWith("""env={"MODE"="ro"},env_vars=["FS_KEY"]}""", table);

        // Run the script as codex would: /bin/sh -c <script>, with the literal env and the forwarded secret.
        const string prefix = """{command="/bin/sh",args=["-c",""";
        var script = JsonSerializer.Deserialize<string>(table[prefix.Length..table.IndexOf("],env=", StringComparison.Ordinal)]);
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script!);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.Environment["HOME"] = "/home/someone";
        start.Environment["MODE"] = "ro";
        start.Environment["FS_KEY"] = "s3cr'et $x";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal("x-s3cr'et $x\nro\nit's\n--key=s3cr'et $x\n$HOME\n\n", output);
        Assert.DoesNotContain("s3cr", table);
    }

    [Fact]
    public void Codex_leaves_out_what_it_could_only_get_with_the_secret_on_its_command_line()
    {
        var inUrl = new Server("url", McpServers.Http, Url: "https://x.example/mcp?key=${KEY}");
        var composedHeader = new Server("hdr", McpServers.Http, Url: "https://x.example/mcp",
            Headers: new Dictionary<string, string> { ["Authorization"] = "token ${KEY}" });
        var plan = Plan("codex", [], [inUrl, composedHeader, Fs]);

        Assert.Equal(["-c"], plan.Arguments.Where(a => a == "-c"));
        Assert.StartsWith("mcp_servers.fs=", plan.Arguments[1]);
        Assert.Equal(2, plan.Warnings.Count);
        Assert.Contains("'url'", plan.Warnings[0]);
        Assert.Contains("'hdr'", plan.Warnings[1]);
    }

    [Fact]
    public void Codex_on_Windows_leaves_out_a_server_needing_a_shell()
    {
        var server = Fs with { Args = ["--token", "${FS_KEY}"] };
        var plan = Plan("codex", [], [server], posixShell: false);

        Assert.Empty(plan.Arguments);
        Assert.Contains("'fs'", Assert.Single(plan.Warnings));
    }

    // ---- gemini ----

    [Fact]
    public void Gemini_gets_system_settings_with_the_servers_and_keeps_only_them()
    {
        const string machine = """
            // set by the administrator
            { "security": { "auth": { "enforcedType": "gemini-api-key" } },
              "mcpServers": { "corp": { "command": "corp-mcp" } }, }
            """;
        var plan = Plan("gemini", ["--yolo"], [GitHub, Fs], "go", geminiSystemSettings: machine);

        Assert.Equal(["--allowed-mcp-server-names", "github,fs"], plan.Arguments.Take(2));
        Assert.Equal("go", plan.Arguments[^1]);
        Assert.Empty(plan.Warnings);

        var settings = JsonNode.Parse(plan.GeminiSystemSettings!)!;
        Assert.Equal("gemini-api-key", (string?)settings["security"]!["auth"]!["enforcedType"]);
        var servers = settings["mcpServers"]!.AsObject();
        Assert.Equal(["github", "fs"], servers.Select(s => s.Key));
        Assert.Equal("https://api.githubcopilot.com/mcp/", (string?)servers["github"]!["httpUrl"]);
        Assert.Equal("Bearer ${GITHUB_TOKEN}", (string?)servers["github"]!["headers"]!["Authorization"]);
        Assert.Equal("npx", (string?)servers["fs"]!["command"]);
        Assert.Equal("${FS_KEY}", (string?)servers["fs"]!["env"]!["FS_KEY"]);
    }

    [Fact]
    public void Gemini_leaves_out_a_server_with_a_dollar_it_would_expand()
    {
        var server = Fs with { Args = ["--root", "$HOME"] };
        var plan = Plan("gemini", [], [server, GitHub]);

        Assert.Equal(["--allowed-mcp-server-names", "github"], plan.Arguments.Take(2));
        Assert.Contains("'fs'", Assert.Single(plan.Warnings));
        Assert.Null(JsonNode.Parse(plan.GeminiSystemSettings!)!["mcpServers"]!["fs"]);
    }

    [Fact]
    public void Gemini_gets_no_servers_rather_than_lose_unreadable_system_settings()
    {
        var plan = Plan("gemini", [], [GitHub], geminiSystemSettings: "{ not json");

        Assert.Null(plan.GeminiSystemSettings);
        Assert.DoesNotContain("--allowed-mcp-server-names", plan.Arguments);
        Assert.Single(plan.Warnings);
    }

    // ---- copilot ----

    [Fact]
    public void Copilot_is_told_its_servers_are_left_out()
    {
        var plan = Plan("copilot", ["-p", "go"], [GitHub]);

        Assert.Equal(["--session-id", Session, "-p", "go"], plan.Arguments);
        Assert.Contains("github", Assert.Single(plan.Warnings));
    }
}
