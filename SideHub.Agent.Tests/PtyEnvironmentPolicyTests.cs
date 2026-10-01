namespace SideHub.Agent.Tests;

public class PtyEnvironmentPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sidehub-policy-{Guid.NewGuid():N}");

    public PtyEnvironmentPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Dictionary<string, string> Filter(Dictionary<string, string> env, out IReadOnlyList<string> rejected) =>
        PtyEnvironmentPolicy.FilterAdditionalEnv(env, out rejected);

    [Fact]
    public void Run_token_is_kept()
    {
        var env = Filter(new() { ["SIDEHUB_AGENT_TOKEN"] = "sh_run_abc", ["SIDEHUB_RUN_ID"] = "id" }, out var rejected);

        Assert.Equal("sh_run_abc", env["SIDEHUB_AGENT_TOKEN"]);
        Assert.Equal("id", env["SIDEHUB_RUN_ID"]);
        Assert.Empty(rejected);
    }

    [Fact]
    public void Terminal_session_token_is_kept()
    {
        var env = Filter(new() { ["SIDEHUB_AGENT_TOKEN"] = "sh_pty_abc", ["SIDEHUB_TASK_ID"] = "t" }, out var rejected);

        Assert.Equal("sh_pty_abc", env["SIDEHUB_AGENT_TOKEN"]);
        Assert.Empty(rejected);
    }

    [Fact]
    public void Without_a_session_token_the_pty_gets_no_token()
    {
        var env = Filter(new() { ["SIDEHUB_RUN_ID"] = "id" }, out _);

        Assert.False(env.ContainsKey("SIDEHUB_AGENT_TOKEN"));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("sh_agent_abc")]
    [InlineData("some-other-secret")]
    public void Anything_but_a_scoped_token_is_rejected(string token)
    {
        var env = Filter(new() { ["SIDEHUB_AGENT_TOKEN"] = token }, out var rejected);

        Assert.False(env.ContainsKey("SIDEHUB_AGENT_TOKEN"));
        Assert.Equal(["SIDEHUB_AGENT_TOKEN"], rejected);
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("LD_PRELOAD")]
    [InlineData("LD_LIBRARY_PATH")]
    [InlineData("HOME")]
    [InlineData("SHELL")]
    [InlineData("BASH_ENV")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("SIDEHUB_BASHRC")]
    [InlineData("SIDEHUB_API_URL")]
    [InlineData("SIDEHUB_CLI_WRAPPERS")]
    [InlineData("SIDEHUB_PTY_NOTIFY_FIFO")]
    [InlineData("SIDEHUB_WORKSPACE_ID")]
    [InlineData("SIDEHUB-BAD")]
    public void Sensitive_or_unknown_keys_are_rejected(string key)
    {
        var env = Filter(new() { [key] = "/evil", ["SIDEHUB_PTY_PROMPT"] = "do it" }, out var rejected);

        Assert.False(env.ContainsKey(key));
        Assert.Equal([key], rejected);
        Assert.Equal("do it", env["SIDEHUB_PTY_PROMPT"]);
    }

    [Fact]
    public void Workflow_context_and_allow_listed_keys_pass()
    {
        var env = Filter(new()
        {
            ["SIDEHUB_WORKFLOW_EXECUTION_ID"] = "e",
            ["SIDEHUB_WORKFLOW_STEP_ID"] = "s",
            ["SIDEHUB_WORKFLOW_OUTPUT_PATH"] = "o",
            ["OTEL_RESOURCE_ATTRIBUTES"] = "sidehub.run_id=id",
        }, out var rejected);

        Assert.Equal(4, env.Count);
        Assert.Empty(rejected);
    }

    [Fact]
    public void Null_env_is_empty()
    {
        var env = PtyEnvironmentPolicy.FilterAdditionalEnv(null, out var rejected);

        Assert.Empty(env);
        Assert.Empty(rejected);
    }

    [Fact]
    public void Working_directory_defaults_to_the_agent_directory()
    {
        Assert.True(PtyEnvironmentPolicy.TryResolveWorkingDirectory(_root, null, out var cwd));
        Assert.Equal(_root, cwd);
    }

    [Fact]
    public void Working_directory_accepts_the_root_and_subfolders()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;

        Assert.True(PtyEnvironmentPolicy.TryResolveWorkingDirectory(_root, _root + "/", out var rootCwd));
        Assert.Equal(_root, rootCwd.TrimEnd('/'));
        Assert.True(PtyEnvironmentPolicy.TryResolveWorkingDirectory(_root, sub, out var subCwd));
        Assert.Equal(sub, subCwd);
        Assert.True(PtyEnvironmentPolicy.TryResolveWorkingDirectory(_root, "repo", out var relCwd));
        Assert.Equal(sub, relCwd);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/tmp")]
    [InlineData("..")]
    [InlineData("repo/../../")]
    public void Working_directory_outside_the_agent_directory_falls_back(string requested)
    {
        Assert.False(PtyEnvironmentPolicy.TryResolveWorkingDirectory(_root, requested, out var cwd));
        Assert.Equal(_root, cwd);
    }

    [Fact]
    public void Working_directory_with_the_root_as_a_name_prefix_is_rejected()
    {
        Assert.False(PtyEnvironmentPolicy.TryResolveWorkingDirectory(_root, _root + "-other", out var cwd));
        Assert.Equal(_root, cwd);
    }

    [Fact]
    public void Working_directory_through_a_symlink_leaving_the_root_is_rejected()
    {
        var outside = Directory.CreateTempSubdirectory("sidehub-outside-").FullName;
        try
        {
            var link = Path.Combine(_root, "escape");
            Directory.CreateSymbolicLink(link, outside);

            Assert.False(PtyEnvironmentPolicy.TryResolveWorkingDirectory(_root, link, out var cwd));
            Assert.Equal(_root, cwd);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
