using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SideHub.Agent.Tests;

public class DaemonEnvironmentPolicyTests
{
    [Fact]
    public void Restrict_keeps_only_allowlisted_variables()
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/u",
            ["LC_ALL"] = "C",
            ["XDG_RUNTIME_DIR"] = "/run/user/1",
            [AgentSetup.TokenEnvVar] = "sh_agent_secret",
            ["SIDEHUB_AGENT_TOKEN"] = "sh_agent_secret",
            ["OPENAI_API_KEY"] = "sk-secret",
            ["AWS_SECRET_ACCESS_KEY"] = "secret",
        };

        DaemonEnvironmentPolicy.Restrict(env);

        Assert.Equal(["HOME", "LC_ALL", "PATH", "XDG_RUNTIME_DIR"], env.Keys.Order().ToArray());
    }

    [Fact]
    public void Names_are_compared_case_insensitively()
    {
        Assert.True(DaemonEnvironmentPolicy.IsAllowed("Path"));
        Assert.True(DaemonEnvironmentPolicy.IsAllowed("SystemRoot"));
        Assert.False(DaemonEnvironmentPolicy.IsAllowed("sidehub_setup_token"));
    }

    [Fact]
    public void Allowlist_matches_the_pty_helper()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "SideHub.Agent", "pty-helper", "index.js"));
        var block = Regex.Match(source, @"const ALLOWED_ENV_NAMES = new Set\(\[(.*?)\]\);", RegexOptions.Singleline).Groups[1].Value;
        var names = Regex.Matches(block, "'([^']+)'").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.True(DaemonEnvironmentPolicy.IsAllowed(name), name));
        Assert.Contains("const ALLOWED_ENV_PREFIXES = ['LC_', 'XDG_'];", source);
    }

    [Fact]
    public async Task Command_execute_does_not_inherit_daemon_secrets()
    {
        if (OperatingSystem.IsWindows()) return;

        var name = $"SIDEHUB_TEST_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "secret");
        try
        {
            var output = new List<string>();
            var exitCode = await new CommandExecutor(Path.GetTempPath()).ExecuteAsync(
                "env", "sh",
                (_, line) => { lock (output) output.Add(line); return Task.CompletedTask; },
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains(output, line => line.StartsWith("PATH=", StringComparison.Ordinal));
            Assert.DoesNotContain(output, line => line.StartsWith(name, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));
}
