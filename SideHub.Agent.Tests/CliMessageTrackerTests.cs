using SideHub.Agent.Models;

namespace SideHub.Agent.Tests;

/// <summary>The last answer of each PTY's CLI, sent once for the phone's Reading mode.</summary>
public class CliMessageTrackerTests
{
    private const string Session = "8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14";
    private static readonly DateTime T0 = new(2026, 10, 11, 17, 0, 0, DateTimeKind.Utc);

    private readonly CliMessageTracker _tracker = new();

    [Fact]
    public void A_new_answer_is_sent_and_kept_for_a_reconnect()
    {
        var message = _tracker.Record("pty", "claude", Session, "## Done", T0);

        Assert.NotNull(message);
        Assert.Equal("pty.cli-message", message.Type);
        Assert.Equal("## Done", message.Text);
        Assert.Same(message, _tracker.Current("pty"));
    }

    [Fact]
    public void The_same_answer_is_not_sent_twice_but_a_new_one_is()
    {
        _tracker.Record("pty", "codex", Session, "first", T0);

        Assert.Null(_tracker.Record("pty", "codex", Session, "first", T0.AddSeconds(5)));
        Assert.NotNull(_tracker.Record("pty", "codex", Session, "second", T0.AddSeconds(9)));
    }

    [Fact]
    public void A_long_answer_is_cut_with_a_mark()
    {
        var message = _tracker.Record("pty", "claude", Session, new string('x', CliMessageTracker.MaxLength + 10), T0)!;

        Assert.Equal(CliMessageTracker.MaxLength + 3, message.Text.Length);
        Assert.EndsWith("\n\n…", message.Text);
    }

    [Fact]
    public void The_answer_is_forgotten_with_its_pty()
    {
        _tracker.Record("pty", "claude", Session, "answer", T0);

        _tracker.Clear("pty");

        Assert.Null(_tracker.Current("pty"));
        Assert.NotNull(_tracker.Record("pty", "claude", Session, "answer", T0));
    }
}
