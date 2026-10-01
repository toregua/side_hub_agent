namespace SideHub.Agent.Tests;

public class BoundedLineReaderTests
{
    private static async Task<List<BoundedLine>> ReadAll(string text, int max)
    {
        var reader = new BoundedLineReader(new StringReader(text), max);
        var lines = new List<BoundedLine>();
        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            lines.Add(line);
        return lines;
    }

    [Fact]
    public async Task Lines_are_read_without_their_terminator()
    {
        var lines = await ReadAll("one\r\ntwo\n\nthree", max: 10);

        Assert.Equal(["one", "two", "", "three"], lines.Select(l => l.Text));
        Assert.DoesNotContain(lines, l => l.TooLong);
    }

    [Fact]
    public async Task A_line_at_the_limit_is_kept() =>
        Assert.Equal(new BoundedLine("abcde", false), Assert.Single(await ReadAll("abcde\r\n", max: 5)));

    [Fact]
    public async Task A_longer_line_is_skipped_whole_and_reading_goes_on()
    {
        // Longer than the reader's internal buffer, so the skip spans several reads.
        var lines = await ReadAll("ok\n" + new string('x', 10_000) + "\nnext\n" + new string('y', 6), max: 5);

        Assert.Equal([new BoundedLine("ok", false), new BoundedLine("", true), new BoundedLine("next", false), new BoundedLine("", true)],
            lines);
    }

    [Fact]
    public async Task The_end_of_the_stream_reads_as_null()
    {
        var reader = new BoundedLineReader(new StringReader(""), 5);
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public void The_synchronous_reader_reads_the_same_lines()
    {
        var reader = new BoundedLineReader(new StringReader("ok\r\n" + new string('x', 10_000) + "\nnext"), 5);
        var lines = new List<BoundedLine>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);

        Assert.Equal([new BoundedLine("ok", false), new BoundedLine("", true), new BoundedLine("next", false)], lines);
    }
}
