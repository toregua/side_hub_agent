using System.Text.Json;

namespace SideHub.Agent.Tests;

public class ShellPolicyTests
{
    private static readonly string[] Dirs = ["/bin", "/usr/bin"];
    private static readonly HashSet<string> Existing = ["/bin/bash", "/usr/bin/zsh", "/bin/sh", "/tmp/bash", "/usr/bin/python3"];

    private static bool Resolve(string? requested, out string resolved) =>
        ShellPolicy.TryResolveUnix(requested, Dirs, Existing.Contains, out resolved);

    [Theory]
    [InlineData("bash", "/bin/bash")]
    [InlineData("zsh", "/usr/bin/zsh")]
    [InlineData(" sh ", "/bin/sh")]
    [InlineData("/usr/bin/zsh", "/usr/bin/zsh")]
    public void Allowlisted_shells_resolve_from_system_directories(string requested, string expected)
    {
        Assert.True(Resolve(requested, out var resolved));
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("python3")]
    [InlineData("/usr/bin/python3")]
    [InlineData("/tmp/bash")]
    [InlineData("/bin/../tmp/bash")]
    [InlineData("bash -c 'id'")]
    [InlineData("fish")]
    [InlineData("./bash")]
    public void Other_binaries_are_refused(string requested)
    {
        Assert.False(Resolve(requested, out var resolved));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void Empty_request_uses_the_platform_default()
    {
        Assert.True(ShellPolicy.TryResolveUnix(null, Dirs, p => p == "/bin/bash" || p == "/bin/zsh", out var resolved));
        Assert.StartsWith("/bin/", resolved);
    }

    [Fact]
    public void Command_execute_and_file_write_are_allowed_by_default()
    {
        var config = JsonSerializer.Deserialize<AgentConfig>("{}")!;

        Assert.True(config.AllowCommandExecute);
        Assert.True(config.AllowFileWrite);
    }

    [Fact]
    public void Command_execute_and_file_write_can_be_disabled()
    {
        var config = JsonSerializer.Deserialize<AgentConfig>("""{"allowCommandExecute":false,"allowFileWrite":false}""")!;

        Assert.False(config.AllowCommandExecute);
        Assert.False(config.AllowFileWrite);
    }
}
