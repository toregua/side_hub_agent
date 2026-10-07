using System.Text.Json.Nodes;
using SideHub.Agent;

namespace SideHub.Agent.Tests;

public class AgentSetupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-setup-").FullName;
    private AgentTokenStore Tokens => new(Path.Combine(_dir, "user-config", "tokens"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AgentSetup.SetupInfo Info(string id = "a1", string name = "Rose d'Or mac") =>
        new(name, "wss://api.sidehub.io/ws/agent", id, "w1", "r1", ["shell", "claude-code"]);

    [Fact]
    public void Writes_agent_json_with_the_repository_and_the_token_out_of_the_project()
    {
        var path = AgentSetup.WriteConfig(_dir, Info(), "sh_agent_x", Tokens);

        Assert.Equal(Path.Combine(_dir, ".sidehub", "agent.json"), path);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Null(json["agentToken"]);
        Assert.DoesNotContain("sh_agent_x", File.ReadAllText(path));
        Assert.Equal("r1", json["repositoryId"]!.GetValue<string>());
        Assert.Equal("sh_agent_x", Tokens.Read("a1"));
        var config = AgentConfig.Load(path, Tokens);
        Assert.Equal("a1", config.AgentId);
        Assert.Equal("sh_agent_x", config.AgentToken);
    }

    [Fact]
    public void Running_setup_again_rewrites_the_same_file()
    {
        AgentSetup.WriteConfig(_dir, Info(), "old", Tokens);
        var path = AgentSetup.WriteConfig(_dir, Info(), "new", Tokens);

        Assert.Single(Directory.GetFiles(Path.Combine(_dir, ".sidehub")));
        Assert.Equal("new", AgentConfig.Load(path, Tokens).AgentToken);
    }

    [Fact]
    public void A_second_agent_gets_its_own_file()
    {
        AgentSetup.WriteConfig(_dir, Info("a1"), "t1", Tokens);
        var path = AgentSetup.WriteConfig(_dir, Info("a2", "Rose d'Or VPS"), "t2", Tokens);

        Assert.Equal(Path.Combine(_dir, ".sidehub", "rose-d-or-vps.json"), path);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_dir, ".sidehub")).Length);
    }

    private static bool GitAvailable => !OperatingSystem.IsWindows() && ExecutableResolver.Resolve("git") is not null;

    [Fact]
    public async Task Sidehub_folder_is_excluded_once_in_a_git_repository_without_touching_gitignore()
    {
        if (!GitAvailable) return;
        Assert.False(await AgentSetup.IgnoreInGitAsync(_dir));
        Git(_dir, "init", "-q");
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "node_modules");

        Assert.True(await AgentSetup.IgnoreInGitAsync(_dir));
        Assert.False(await AgentSetup.IgnoreInGitAsync(_dir));
        Assert.Contains("/.sidehub", File.ReadAllLines(Path.Combine(_dir, ".git", "info", "exclude")));
        Assert.Equal("node_modules", File.ReadAllText(Path.Combine(_dir, ".gitignore")));
    }

    [Fact]
    public async Task Sidehub_folder_is_excluded_in_a_worktree_whose_git_is_a_file()
    {
        if (!GitAvailable) return;
        var main = Path.Combine(_dir, "main");
        var worktree = Path.Combine(_dir, "wt");
        Directory.CreateDirectory(main);
        Git(main, "init", "-q");
        Git(main, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "init");
        Git(main, "worktree", "add", "-q", worktree);
        Assert.True(File.Exists(Path.Combine(worktree, ".git")));

        Assert.True(await AgentSetup.IgnoreInGitAsync(worktree));

        Directory.CreateDirectory(Path.Combine(worktree, ".sidehub"));
        File.WriteAllText(Path.Combine(worktree, ".sidehub", "agent.json"), "{}");
        Assert.Equal("", GitOutput(worktree, "status", "--porcelain"));
    }

    private static void Git(string cwd, params string[] args) => GitOutput(cwd, args);

    private static string GitOutput(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ExecutableResolver.Resolve("git")!)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }

    [Theory]
    [InlineData("https://api.sidehub.io/api", true)]
    [InlineData("http://localhost:5000/api", true)]
    [InlineData("http://127.0.0.1:5000/api", true)]
    [InlineData("http://[::1]:5000/api", true)]
    [InlineData("http://api.sidehub.io/api", false)]
    [InlineData("http://192.168.1.10/api", false)]
    [InlineData("ftp://api.sidehub.io/api", false)]
    [InlineData("api.sidehub.io/api", false)]
    public void Api_must_be_https_unless_local(string apiBase, bool allowed) =>
        Assert.Equal(allowed, AgentSetup.ApiRejectionReason(apiBase) is null);

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
