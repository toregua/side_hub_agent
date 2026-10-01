using System.Text.Json.Nodes;
using SideHub.Agent;

namespace SideHub.Agent.Tests;

public class AgentSetupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-setup-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AgentSetup.SetupInfo Info(string id = "a1", string name = "Rose d'Or mac") =>
        new(name, "wss://api.sidehub.io/ws/agent", id, "w1", "r1", ["shell", "claude-code"]);

    [Fact]
    public void Writes_agent_json_with_the_token_and_repository()
    {
        var path = AgentSetup.WriteConfig(_dir, Info(), "sh_agent_x");

        Assert.Equal(Path.Combine(_dir, ".sidehub", "agent.json"), path);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("sh_agent_x", json["agentToken"]!.GetValue<string>());
        Assert.Equal("r1", json["repositoryId"]!.GetValue<string>());
        Assert.Equal("a1", AgentConfig.Load(path).AgentId);
    }

    [Fact]
    public void Running_setup_again_rewrites_the_same_file()
    {
        AgentSetup.WriteConfig(_dir, Info(), "old");
        var path = AgentSetup.WriteConfig(_dir, Info(), "new");

        Assert.Single(Directory.GetFiles(Path.Combine(_dir, ".sidehub")));
        Assert.Equal("new", JsonNode.Parse(File.ReadAllText(path))!["agentToken"]!.GetValue<string>());
    }

    [Fact]
    public void A_second_agent_gets_its_own_file()
    {
        AgentSetup.WriteConfig(_dir, Info("a1"), "t1");
        var path = AgentSetup.WriteConfig(_dir, Info("a2", "Rose d'Or VPS"), "t2");

        Assert.Equal(Path.Combine(_dir, ".sidehub", "rose-d-or-vps.json"), path);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_dir, ".sidehub")).Length);
    }

    [Fact]
    public void Sidehub_folder_is_ignored_once_in_a_git_repository()
    {
        Assert.False(AgentSetup.IgnoreInGit(_dir));
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "node_modules");

        Assert.True(AgentSetup.IgnoreInGit(_dir));
        Assert.False(AgentSetup.IgnoreInGit(_dir));
        var lines = File.ReadAllLines(Path.Combine(_dir, ".gitignore"));
        Assert.Contains(".sidehub/", lines);
        Assert.Equal("node_modules", lines[0]);
    }

    [Theory]
    [InlineData(null, "https://api.sidehub.io/api")]
    [InlineData("https://api.sidehub.io", "https://api.sidehub.io/api")]
    [InlineData("https://api.sidehub.io/", "https://api.sidehub.io/api")]
    [InlineData("https://api.sidehub.io/api", "https://api.sidehub.io/api")]
    [InlineData("http://localhost:5000/api/", "http://localhost:5000/api")]
    public void Api_base_accepts_the_host_or_the_api_root(string? value, string expected) =>
        Assert.Equal(expected, AgentSetup.ApiBase(value));

    [Theory]
    [InlineData("sh_flag", false, "sh_stdin\n", "sh_env", "sh_flag")]
    [InlineData(null, false, "sh_stdin\n", "sh_env", "sh_env")]
    [InlineData(null, true, "  sh_stdin  \nrest", "sh_env", "sh_stdin")]
    [InlineData("-", false, "sh_stdin\n", "sh_env", "sh_stdin")]
    [InlineData(null, true, "", "sh_env", "")]
    [InlineData(null, false, "", null, "")]
    public void Token_comes_from_stdin_then_the_flag_then_the_environment(
        string? flag, bool fromStdin, string stdin, string? env, string expected) =>
        Assert.Equal(expected, AgentSetup.ResolveToken(flag, fromStdin, new StringReader(stdin), env));
}
