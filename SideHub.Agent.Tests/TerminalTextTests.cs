namespace SideHub.Agent.Tests;

public class TerminalTextTests
{
    [Fact]
    public void Keeps_the_last_lines_as_plain_text()
    {
        var output = "\x1b]0;title\x07$ sidehub-cli launch --prompt-env claude -p; exit\r\n" +
                     "\x1b[31mInvalid API key\x1b[0m · Please run /login\r\n" +
                     "exit\r\n";

        Assert.Equal("$ sidehub-cli launch --prompt-env claude -p; exit\nInvalid API key · Please run /login",
            TerminalText.LastLines(output));
    }

    [Fact]
    public void A_redrawn_line_keeps_its_final_state()
    {
        Assert.Equal("Done 100%", TerminalText.LastLines("Working 10%\rWorking 50%\rDone 100%\n"));
    }

    [Fact]
    public void Bounds_the_lines_and_keeps_the_end()
    {
        var output = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));

        Assert.Equal(string.Join('\n', Enumerable.Range(21, 10).Select(i => $"line {i}")), TerminalText.LastLines(output));
        var cut = TerminalText.LastLines(new string('x', 1000), maxChars: 50)!;
        Assert.Equal(50, cut.Length);
        Assert.StartsWith("…", cut);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\x1b[2J\x1b[H\r\n\r\nexit\r\n")]
    public void Nothing_left_is_null(string? output)
    {
        Assert.Null(TerminalText.LastLines(output));
    }
}
