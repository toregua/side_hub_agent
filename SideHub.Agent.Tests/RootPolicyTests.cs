namespace SideHub.Agent.Tests;

public class RootPolicyTests
{
    [Theory]
    [InlineData("setup")]
    [InlineData("start")]
    [InlineData("restart")]
    [InlineData("--foreground-daemon")]
    [InlineData("-d")] // unknown commands run as start
    public void Root_is_refused_for_commands_that_configure_or_run_the_agent(string command)
    {
        Assert.NotNull(RootPolicy.Check(command, [command], isRoot: true, envValue: null));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("status")]
    [InlineData("logs")]
    [InlineData("help")]
    public void Root_may_inspect_and_stop_the_agent(string command)
    {
        Assert.Null(RootPolicy.Check(command, [command], isRoot: true, envValue: null));
    }

    [Fact]
    public void Allow_root_flag_opts_in_anywhere_in_the_arguments()
    {
        Assert.Null(RootPolicy.Check("start", ["start", "-d", "--allow-root"], isRoot: true, envValue: null));
        Assert.Null(RootPolicy.Check("--foreground-daemon", ["--foreground-daemon", "/r/log", "/r/pid", "--allow-root"], isRoot: true, envValue: null));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("yes", false)]
    public void Allow_root_environment_variable_opts_in(string value, bool allowed)
    {
        Assert.Equal(allowed, RootPolicy.Check("setup", ["setup"], isRoot: true, envValue: value) == null);
    }

    [Fact]
    public void Non_root_users_are_never_refused()
    {
        Assert.Null(RootPolicy.Check("setup", ["setup"], isRoot: false, envValue: null));
    }
}
