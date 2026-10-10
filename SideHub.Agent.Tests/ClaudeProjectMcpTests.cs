using System.Text.Json;
using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>What SideHub launches while nobody watches starts without the project MCP servers nobody decided on yet,
/// instead of stopping on claude's approval dialog.</summary>
public sealed class ClaudeProjectMcpTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sidehub-mcp-").FullName;
    private string Repo => Path.Combine(_root, "repo");
    private string Home => Path.Combine(_root, "home");

    public ClaudeProjectMcpTests()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".claude"));
        Directory.CreateDirectory(Path.Combine(Home, ".claude"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void Servers_of_the_folder_and_of_its_parents_are_declined_unless_decided()
    {
        Write(Path.Combine(Repo, ".mcp.json"), """{ "mcpServers": { "db": {}, "browser": {}, "docs": {} } }""");
        Write(Path.Combine(_root, ".mcp.json"), """{ "mcpServers": { "tessl": { "command": "tessl" } } }""");
        Write(Path.Combine(Repo, ".claude", "settings.local.json"), """{ "enabledMcpjsonServers": ["db"] }""");
        Write(Path.Combine(Home, ".claude.json"), $$"""{ "projects": { "{{Repo.Replace("\\", "/")}}": { "disabledMcpjsonServers": ["docs"] } } }""");

        Assert.Equal(["browser", "tessl"], ClaudeProjectMcp.Undecided(Repo, Home));
    }

    [Fact]
    public void Nothing_is_declined_when_every_project_server_is_enabled()
    {
        Write(Path.Combine(Repo, ".mcp.json"), """{ "mcpServers": { "db": {} } }""");
        Write(Path.Combine(Home, ".claude", "settings.json"), """{ "enableAllProjectMcpServers": true }""");

        Assert.Empty(ClaudeProjectMcp.Undecided(Repo, Home));
    }

    [Fact]
    public void A_broken_file_declares_and_decides_nothing()
    {
        Write(Path.Combine(Repo, ".mcp.json"), """{ "mcpServers": { "db": {} } }""");
        Write(Path.Combine(Repo, ".claude", "settings.json"), "{ not json");
        Write(Path.Combine(_root, ".mcp.json"), "[1, 2]");

        Assert.Equal(["db"], ClaudeProjectMcp.Undecided(Repo, home: null));
    }

    [Fact]
    public void Declined_servers_go_in_the_claude_settings_with_the_hooks_or_alone()
    {
        var withHooks = CliLaunchPlan.For("claude", [], "hi", null, Guid.NewGuid,
            new CliLaunchPlan.StateReporting("/usr/bin/sidehub-cli", false), declinedProjectMcpServers: ["tessl"]);
        var alone = CliLaunchPlan.For("claude", [], "hi", null, Guid.NewGuid, declinedProjectMcpServers: ["tessl"]);
        var none = CliLaunchPlan.For("claude", [], "hi", null, Guid.NewGuid);

        JsonElement Settings(CliLaunchPlan plan) =>
            JsonDocument.Parse(plan.Arguments[plan.Arguments.ToList().IndexOf("--settings") + 1]).RootElement;
        Assert.Equal("tessl", Settings(withHooks).GetProperty("disabledMcpjsonServers")[0].GetString());
        Assert.True(Settings(withHooks).GetProperty("hooks").TryGetProperty("Stop", out _));
        Assert.Equal("tessl", Settings(alone).GetProperty("disabledMcpjsonServers")[0].GetString());
        Assert.DoesNotContain("--settings", none.Arguments);
    }
}
