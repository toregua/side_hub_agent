using System.Text.Json.Nodes;
using SideHub.Agent;

namespace SideHub.Agent.Tests;

/// <summary>
/// The agent token lives outside the project: configs written by older versions held it inline in .sidehub/*.json,
/// in the working directory of every terminal. Loading them moves it out.
/// </summary>
public class AgentTokenStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-tokens-").FullName;
    private string SidehubDir => Path.Combine(_dir, "project", ".sidehub");
    private AgentTokenStore Tokens => new(Path.Combine(_dir, "user-config", "sidehub", "tokens"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string LegacyConfig(string agentId, string? token)
    {
        Directory.CreateDirectory(SidehubDir);
        var json = new JsonObject
        {
            ["name"] = "vps",
            ["sidehubUrl"] = "wss://api.sidehub.io/ws/agent",
            ["agentId"] = agentId,
            ["workspaceId"] = "w1",
            ["workingDirectory"] = ".",
            ["capabilities"] = new JsonArray("shell"),
        };
        if (token is not null) json["agentToken"] = token;
        var path = Path.Combine(SidehubDir, "agent.json");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    [Fact]
    public async Task A_config_holding_its_token_inline_has_it_moved_to_the_store()
    {
        var path = LegacyConfig("a1", "sh_agent_legacy");
        var warnings = new List<string>();

        var config = (await AgentConfig.LoadAllAsync(Path.GetDirectoryName(SidehubDir)!, warnings.Add, Tokens)).Single();

        Assert.Equal("sh_agent_legacy", config.AgentToken);
        Assert.Equal("sh_agent_legacy", Tokens.Read("a1"));
        Assert.DoesNotContain("sh_agent_legacy", File.ReadAllText(path));
        Assert.Equal("vps", JsonNode.Parse(File.ReadAllText(path))!["name"]!.GetValue<string>());
        Assert.Contains(warnings, w => w.Contains("Moved the agent token"));

        // Next start: read from the store, nothing left to move.
        warnings.Clear();
        Assert.Equal("sh_agent_legacy", AgentConfig.Load(path, Tokens, warnings.Add).AgentToken);
        Assert.Empty(warnings);
    }

    [Fact]
    public void An_inline_token_replaces_the_stored_one()
    {
        Tokens.Save("a1", "sh_agent_old");
        var path = LegacyConfig("a1", "sh_agent_new");

        Assert.Equal("sh_agent_new", AgentConfig.Load(path, Tokens).AgentToken);
        Assert.Equal("sh_agent_new", Tokens.Read("a1"));
    }

    [Fact]
    public void A_config_without_a_stored_token_says_where_it_is_expected()
    {
        var path = LegacyConfig("a1", token: null);

        var error = Assert.Throws<InvalidOperationException>(() => AgentConfig.Load(path, Tokens));

        Assert.Contains(Tokens.PathFor("a1"), error.Message);
        Assert.Contains("sidehub-agent setup", error.Message);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    [InlineData("")]
    public void An_agent_id_never_names_a_path_out_of_the_store(string agentId)
    {
        Assert.Throws<InvalidOperationException>(() => Tokens.PathFor(agentId));
    }
}

/// <summary>Changes XDG_CONFIG_HOME for the whole process: never run alongside other tests.</summary>
[CollectionDefinition(nameof(UserConfigFolderCollection), DisableParallelization = true)]
public class UserConfigFolderCollection;

[Collection(nameof(UserConfigFolderCollection))]
public class UserConfigFolderTests
{
    [Fact]
    public void A_config_folder_not_created_yet_still_receives_the_token()
    {
        // A fresh server has no ~/.config: setup failed there with "Can't locate the user's configuration folder"
        if (OperatingSystem.IsWindows())
            return;

        var dir = Directory.CreateTempSubdirectory("sidehub-xdg-").FullName;
        var configHome = Path.Combine(dir, "missing-config");
        var original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        try
        {
            var tokens = AgentTokenStore.ForCurrentUser();
            tokens.Save("a1", "sh_agent_token");

            Assert.Equal(Path.Combine(configHome, "sidehub", "tokens"), tokens.Directory);
            Assert.Equal("sh_agent_token", tokens.Read("a1"));
            Assert.StartsWith(configHome, McpSecretsFiles.ForAgent("a1").Directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original);
            Directory.Delete(dir, recursive: true);
        }
    }
}
