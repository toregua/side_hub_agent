using SideHub.Agent;

namespace SideHub.Agent.Tests;

public class CliReportTests
{
    [Fact]
    public void Lines_ShouldSayHowEachCliIsSignedInAndThatSideHubNeedsNoKey()
    {
        var lines = CliReport.Lines(
            new Dictionary<string, string> { ["claude"] = "2.1.296", ["codex"] = "0.162.1", ["agy"] = "1.0.0" },
            new Dictionary<string, bool> { ["claude"] = true, ["codex"] = false },
            new Dictionary<string, CliBilling> { ["claude"] = new(CliBilling.Subscription, "max") },
            new Dictionary<string, CliAccount>());

        Assert.Contains(lines, l => l.Contains("claude 2.1.296") && l.Contains("logged in, with a Claude Max subscription"));
        Assert.Contains(lines, l => l.Contains("codex 0.162.1") && l.Contains("not logged in") && l.Contains("codex login"));
        Assert.Contains(lines, l => l.Contains("agy 1.0.0") && l.Contains("login not checked"));
        Assert.Contains(lines, l => l.Contains("never asks for a model key"));
        Assert.Contains(lines, l => l.Contains("sidehub-agent restart"));
    }

    [Fact]
    public void Lines_ShouldSayAnApiKeyIsBilledPerUse()
    {
        var lines = CliReport.Lines(
            new Dictionary<string, string> { ["codex"] = "0.162.1" },
            new Dictionary<string, bool> { ["codex"] = true },
            new Dictionary<string, CliBilling> { ["codex"] = new(CliBilling.Api, null) },
            null);

        Assert.Contains(lines, l => l.Contains("with an API key (billed per use"));
        Assert.DoesNotContain(lines, l => l.Contains("sidehub-agent restart"));
    }

    [Fact]
    public void Lines_ShouldExplainWhatToInstallWhenNoCliIsFound()
    {
        var lines = CliReport.Lines(new Dictionary<string, string>(), null, null, null);

        Assert.Contains(lines, l => l.Contains("No coding CLI found"));
        Assert.Contains(lines, l => l.Contains("codex login"));
        Assert.Contains(lines, l => l.Contains("sidehub-agent restart"));
    }
}
