using SideHub.Agent;

namespace SideHub.Agent.Tests;

public class OutputTailTests
{
    [Fact]
    public void Everything_is_forwarded_and_the_last_lines_are_kept_without_their_prefix()
    {
        var inner = new StringWriter();
        var tail = new OutputTail(inner, maxLines: 2);

        tail.WriteLine("[SideHub] Agent \"vps\" configured in .sidehub/agent.json");
        tail.WriteLine();
        tail.WriteLine("[SideHub] Error: Daemon process exited immediately");
        tail.Write("[SideHub] Check logs for details: .sidehub/run/sidehub-agent.log\r\n");

        Assert.Equal(
            "Error: Daemon process exited immediately\nCheck logs for details: .sidehub/run/sidehub-agent.log",
            tail.Text);
        Assert.Contains("configured in", inner.ToString());
    }

    [Fact]
    public void A_line_not_ended_yet_counts()
    {
        var tail = new OutputTail(TextWriter.Null, maxLines: 2);

        tail.WriteLine("first");
        tail.WriteLine("second");
        tail.Write("[SideHub] Paste the agent token, then press Enter: ");

        Assert.Equal("second\nPaste the agent token, then press Enter:", tail.Text);
    }
}
