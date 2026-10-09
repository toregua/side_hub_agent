using SideHub.Agent.Models;
using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>The secrets file of an MCP server (pty.start mcpServers[].secretsFile): written 0600 by the agent outside the
/// project, its path put where the definition says ${secrets_file}, deleted with the PTY.</summary>
public sealed class McpSecretsFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-mcp-secrets-").FullName;
    private readonly McpSecretsFiles _files;

    public McpSecretsFilesTests() => _files = new McpSecretsFiles(Path.Combine(_dir, "agent-1"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static IReadOnlyList<KeyValuePair<string, string>> Secrets(params (string Name, string Value)[] secrets) =>
        secrets.Select(s => KeyValuePair.Create(s.Name, s.Value)).ToList();

    private static PtyMcpServer Playwright(string format = "dotenv", List<string>? secrets = null, List<string>? args = null) => new()
    {
        Name = "playwright", Revision = 1, Transport = "stdio", Command = "npx",
        Args = args ?? ["-y", "@playwright/mcp", "--secrets", "${secrets_file}"],
        Env = new() { ["QA_EMAIL"] = "${QA_EMAIL}", ["QA_PASSWORD"] = "${QA_PASSWORD}" },
        Secrets = secrets ?? ["QA_EMAIL", "QA_PASSWORD"], SecretsFile = format,
    };

    [Theory]
    [InlineData("qa@example.com", "'qa@example.com'")]
    [InlineData("it's # \"here\"", "`it's # \"here\"`")]
    [InlineData("a'b`c", "\"a'b`c\"")]
    [InlineData("line 1\nline 2", "'line 1\nline 2'")]
    public void Dotenv_quotes_each_value_so_that_dotenv_reads_it_back_unchanged(string value, string written)
    {
        Assert.Equal($"A={written}\n", McpSecretsFiles.Format("dotenv", Secrets(("A", value))));
    }

    [Theory]
    [InlineData("a'b`c\"d")]      // no quote left
    [InlineData("a'b`c\\nd")]     // double quotes would turn \n into a line break
    [InlineData("ends with \\")] // the backslash would escape the closing quote
    [InlineData("crlf\r\nvalue")] // parsers drop the carriage return
    public void Dotenv_refuses_a_value_it_cannot_write_without_naming_it(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => McpSecretsFiles.Format("dotenv", Secrets(("API_KEY", value))));

        Assert.Contains("API_KEY", error.Message);
        Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public void Raw_holds_one_value_as_is()
    {
        const string json = "{\n  \"type\": \"service_account\",\r\n  \"key\": \"a'b`c\\\"\"\n}";

        Assert.Equal(json, McpSecretsFiles.Format("raw", Secrets(("GOOGLE_SA", json))));
        Assert.Throws<InvalidOperationException>(() => McpSecretsFiles.Format("raw", Secrets(("A", "1"), ("B", "2"))));
    }

    [Fact]
    public void Write_creates_a_private_file_per_server_and_Delete_removes_the_ptys()
    {
        var path = _files.Write("run-abc", "playwright", "dotenv", Secrets(("QA_EMAIL", "qa@example.com")));
        var other = _files.Write("run-def", "playwright", "raw", Secrets(("QA_EMAIL", "qa@example.com")));

        Assert.Equal(Path.Combine(_files.Directory, "run-abc", "playwright.env"), path);
        Assert.Equal("QA_EMAIL='qa@example.com'\n", File.ReadAllText(path));
        Assert.EndsWith("playwright.secret", other);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(PrivateFiles.PrivateDirectoryMode, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
            Assert.Equal(PrivateFiles.PrivateDirectoryMode, File.GetUnixFileMode(_files.Directory));
        }

        Assert.True(_files.Delete("run-abc"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.True(File.Exists(other));

        Assert.True(_files.DeleteAll());
        Assert.False(Directory.Exists(_files.Directory));
        Assert.True(_files.DeleteAll(), "nothing to delete is fine");
    }

    [Fact]
    public void A_pty_session_id_cannot_lead_out_of_the_folder()
    {
        Assert.Throws<ArgumentException>(() => _files.Write("../x", "playwright", "raw", Secrets(("A", "1"))));
        Assert.True(_files.Delete("../.."));
        Assert.True(Directory.Exists(_dir));
    }

    // ---- policy: what reaches the terminal ----

    [Fact]
    public void Agent_writes_the_file_and_puts_its_path_where_the_definition_says()
    {
        PtyMcpServer? written = null;
        var value = McpServerPolicy.ToEnvironmentValue([Playwright()], ["QA_EMAIL", "QA_PASSWORD"],
            server => { written = server; return "/private/run-1/playwright.env"; }, out var rejected);

        Assert.Empty(rejected);
        Assert.Same("playwright", written!.Name);
        var server = Assert.Single(McpServers.Parse(value, []));
        Assert.Equal(["-y", "@playwright/mcp", "--secrets", "/private/run-1/playwright.env"], server.Args);
        Assert.Equal("${QA_PASSWORD}", server.Env["QA_PASSWORD"]);
        Assert.DoesNotContain("secrets_file", value);
    }

    [Fact]
    public void A_path_in_a_variable_is_replaced_too()
    {
        var sa = Playwright("raw", ["GOOGLE_SA"], ["-y", "mcp-google"]);
        var withVariable = new PtyMcpServer
        {
            Name = sa.Name, Transport = sa.Transport, Command = sa.Command, Args = sa.Args, Secrets = sa.Secrets, SecretsFile = sa.SecretsFile,
            Env = new() { ["GOOGLE_APPLICATION_CREDENTIALS"] = "${secrets_file}" },
        };

        var value = McpServerPolicy.ToEnvironmentValue([withVariable], ["GOOGLE_SA"], _ => "/p/sa.secret", out var rejected);

        Assert.Empty(rejected);
        Assert.Equal("/p/sa.secret", Assert.Single(McpServers.Parse(value, [])).Env["GOOGLE_APPLICATION_CREDENTIALS"]);
    }

    [Theory]
    [InlineData("dotenv", new[] { "QA_EMAIL", "SIDEHUB_AGENT_TOKEN" })] // only this run's secrets
    [InlineData("dotenv", new string[0])]
    [InlineData("raw", new[] { "QA_EMAIL", "QA_PASSWORD" })]
    [InlineData("json", new[] { "QA_EMAIL" })]
    public void Agent_leaves_out_a_server_whose_secrets_file_is_wrong_without_writing_it(string format, string[] secrets)
    {
        var writes = 0;
        var value = McpServerPolicy.ToEnvironmentValue([Playwright(format, secrets.ToList())], ["QA_EMAIL", "QA_PASSWORD"],
            _ => { writes++; return "/p"; }, out var rejected);

        Assert.Null(value);
        Assert.StartsWith("playwright (", Assert.Single(rejected));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Without_a_secrets_file_the_reference_is_refused()
    {
        var server = Playwright();
        var withoutFile = new PtyMcpServer { Name = server.Name, Transport = "stdio", Command = "npx", Args = server.Args };

        Assert.Null(McpServerPolicy.ToEnvironmentValue([withoutFile], ["QA_EMAIL"], _ => "/p", out var rejected));
        Assert.Contains("invalid ${...} reference", Assert.Single(rejected));
    }

    [Fact]
    public void A_file_that_cannot_be_written_leaves_the_server_out_with_the_reason()
    {
        var value = McpServerPolicy.ToEnvironmentValue([Playwright()], ["QA_EMAIL", "QA_PASSWORD"],
            _ => throw new InvalidOperationException("the value of QA_PASSWORD cannot be written in a dotenv file"), out var rejected);

        Assert.Null(value);
        Assert.Equal("playwright (secrets file: the value of QA_PASSWORD cannot be written in a dotenv file)", Assert.Single(rejected));
        Assert.Null(McpServerPolicy.ToEnvironmentValue([Playwright()], ["QA_EMAIL", "QA_PASSWORD"], null, out _));
    }
}
