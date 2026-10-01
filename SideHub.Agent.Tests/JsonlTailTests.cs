namespace SideHub.Agent.Tests;

public sealed class JsonlTailTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sidehub-jsonl-tail-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose() => File.Delete(_path);

    private void Append(string text) => File.AppendAllText(_path, text);

    [Fact]
    public void Each_line_is_returned_once_and_only_when_complete()
    {
        var tail = new JsonlTail(_path, maxLineBytes: 100);
        Append("one\r\ntwo\nthr");

        Assert.Equal(["one", "two"], tail.ReadNewLines());
        Assert.Empty(tail.ReadNewLines());

        Append("ee\nfour\n");
        Assert.Equal(["three", "four"], tail.ReadNewLines());
    }

    [Fact]
    public void A_line_over_the_limit_is_skipped_even_across_calls()
    {
        var tail = new JsonlTail(_path, maxLineBytes: 10);
        Append("ok\n" + new string('x', 200_000));

        Assert.Equal(["ok"], tail.ReadNewLines());

        Append(new string('x', 50) + "\nnext\n");
        Assert.Equal(["next"], tail.ReadNewLines());
    }

    [Fact]
    public void Multibyte_characters_are_decoded()
    {
        var tail = new JsonlTail(_path, maxLineBytes: 100);
        Append("{\"aiTitle\":\"Réparer l’agent ✓\"}\n");

        Assert.Equal(["{\"aiTitle\":\"Réparer l’agent ✓\"}"], tail.ReadNewLines());
    }

    [Fact]
    public void A_file_that_shrank_is_read_from_the_start()
    {
        var tail = new JsonlTail(_path, maxLineBytes: 100);
        Append("a long first line\n");
        Assert.Single(tail.ReadNewLines());

        File.WriteAllText(_path, "new\n");
        Assert.Equal(["new"], tail.ReadNewLines());
    }

    [Fact]
    public void A_missing_file_throws_an_io_exception() =>
        Assert.ThrowsAny<IOException>(() => new JsonlTail(_path, 100).ReadNewLines().ToList());
}
