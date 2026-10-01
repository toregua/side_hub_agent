namespace SideHub.Agent.Tests;

public class PendingFileWritesTests
{
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private PendingFileWrites Create(long maxFileBytes = FileWritePolicy.MaxFileBytes, long maxTotalBytes = PendingFileWrites.MaxTotalBytes) =>
        new(() => _now, maxFileBytes, maxTotalBytes);

    private static async Task<string> Content(PendingFileWrite write)
    {
        using var stream = new MemoryStream();
        await write.WriteToAsync(stream, CancellationToken.None);
        return System.Text.Encoding.ASCII.GetString(stream.ToArray());
    }

    [Fact]
    public async Task Chunks_are_decoded_and_buffered_until_the_write_is_taken()
    {
        var writes = Create();
        Assert.True(writes.TryStart("c1", "/repo/a.png", null, "pty1"));

        Assert.Equal(FileWriteChunkResult.Appended, writes.Append("c1", "QUJD"));
        Assert.Equal(FileWriteChunkResult.Appended, writes.Append("c1", "REVG"));

        Assert.True(writes.TryTake("c1", out var write));
        Assert.Equal("ABCDEF", await Content(write));
        Assert.True(write.IsComplete);
        Assert.Equal("pty1", write.PtySessionId);
        Assert.False(writes.TryTake("c1", out _));
    }

    [Fact]
    public void Chunk_for_an_unknown_write_is_ignored()
    {
        Assert.Equal(FileWriteChunkResult.Unknown, Create().Append("nope", "QUJD"));
    }

    [Fact]
    public async Task Chunks_split_mid_group_are_joined()
    {
        var writes = Create();
        writes.TryStart("c1", "/repo/a.txt", null, null);
        // "hello, world\n" = aGVsbG8sIHdvcmxkCg==
        foreach (var chunk in new[] { "aGV", "sbG8s", "I", "HdvcmxkCg==" })
            Assert.Equal(FileWriteChunkResult.Appended, writes.Append("c1", chunk));

        Assert.True(writes.TryTake("c1", out var write));
        Assert.Equal("hello, world\n", await Content(write));
    }

    [Fact]
    public void A_write_ending_mid_group_is_incomplete()
    {
        var writes = Create();
        writes.TryStart("c1", "/repo/a.txt", null, null);
        writes.Append("c1", "QUJDRE");

        Assert.True(writes.TryTake("c1", out var write));
        Assert.False(write.IsComplete);
    }

    [Theory]
    [InlineData("QU*D")]
    [InlineData("QQ==QUJD")]
    public void Invalid_base64_drops_the_write(string chunk)
    {
        var writes = Create();
        writes.TryStart("c1", "/repo/a.txt", null, null);

        Assert.Equal(FileWriteChunkResult.Invalid, writes.Append("c1", chunk));
        Assert.False(writes.TryTake("c1", out _));
        Assert.Equal(0, writes.TotalBytes);
    }

    [Fact]
    public void Data_after_padding_drops_the_write()
    {
        var writes = Create();
        writes.TryStart("c1", "/repo/a.txt", null, null);

        Assert.Equal(FileWriteChunkResult.Appended, writes.Append("c1", "QQ=="));
        Assert.Equal(FileWriteChunkResult.Invalid, writes.Append("c1", "QUJD"));
        Assert.Equal(0, writes.TotalBytes);
    }

    [Fact]
    public void Write_exceeding_the_size_limit_is_dropped()
    {
        var writes = Create(maxFileBytes: 6);
        writes.TryStart("c1", "/repo/a.png", null, null);

        Assert.Equal(FileWriteChunkResult.Appended, writes.Append("c1", "QUJDREVG"));
        Assert.Equal(FileWriteChunkResult.TooLarge, writes.Append("c1", "Rw=="));
        Assert.False(writes.TryTake("c1", out _));
        Assert.Equal(FileWriteChunkResult.Unknown, writes.Append("c1", "Rw=="));
        Assert.Equal(0, writes.TotalBytes);
    }

    [Fact]
    public void Writes_share_a_byte_budget_freed_when_a_write_is_disposed()
    {
        var writes = Create(maxFileBytes: 6, maxTotalBytes: 9);
        writes.TryStart("c1", "/repo/a.png", null, null);
        writes.TryStart("c2", "/repo/b.png", null, null);

        Assert.Equal(FileWriteChunkResult.Appended, writes.Append("c1", "QUJDREVG")); // 6 bytes
        Assert.Equal(FileWriteChunkResult.OverBudget, writes.Append("c2", "QUJDREVG"));
        Assert.False(writes.TryTake("c2", out _));
        Assert.Equal(6, writes.TotalBytes);

        Assert.True(writes.TryTake("c1", out var taken));
        Assert.Equal(6, writes.TotalBytes); // still held until written
        taken.Dispose();
        Assert.Equal(0, writes.TotalBytes);

        writes.TryStart("c3", "/repo/c.png", null, null);
        Assert.Equal(FileWriteChunkResult.Appended, writes.Append("c3", "QUJDREVG"));
    }

    [Fact]
    public void A_chunk_for_a_taken_write_is_not_counted()
    {
        var writes = Create();
        writes.TryStart("c1", "/repo/a.png", null, null);
        writes.TryTake("c1", out var taken);
        taken.Dispose();

        Assert.Equal(FileWriteChunkResult.Unknown, writes.Append("c1", "QUJD"));
        Assert.Equal(0, writes.TotalBytes);
    }

    [Fact]
    public void Restarting_or_expiring_a_write_frees_its_bytes()
    {
        var writes = Create();
        writes.TryStart("c1", "/repo/a.png", null, null);
        writes.Append("c1", "QUJD");
        writes.TryStart("c1", "/repo/a.png", null, null);
        Assert.Equal(0, writes.TotalBytes);

        writes.Append("c1", "QUJD");
        _now += PendingFileWrites.Expiry + TimeSpan.FromSeconds(1);
        writes.RemoveExpired();
        Assert.Equal(0, writes.TotalBytes);
    }

    [Fact]
    public void Concurrent_writes_are_capped()
    {
        var writes = Create();
        for (var i = 0; i < PendingFileWrites.MaxConcurrentWrites; i++)
            Assert.True(writes.TryStart($"c{i}", "/repo/a.png", null, null));

        Assert.False(writes.TryStart("extra", "/repo/a.png", null, null));
        // Restarting a write already in progress does not count as a new one.
        Assert.True(writes.TryStart("c0", "/repo/b.png", null, null));
    }

    [Fact]
    public void Idle_writes_expire_and_active_ones_are_kept()
    {
        var writes = Create();
        writes.TryStart("idle", "/repo/a.png", null, null);
        writes.TryStart("active", "/repo/b.png", null, null);

        _now += PendingFileWrites.Expiry - TimeSpan.FromSeconds(1);
        writes.Append("active", "QUJD");
        _now += TimeSpan.FromSeconds(2);

        Assert.Equal(["idle"], writes.RemoveExpired());
        Assert.Equal(1, writes.Count);
        Assert.True(writes.TryTake("active", out _));
    }

    [Fact]
    public void Expired_writes_free_their_slot()
    {
        var writes = Create();
        for (var i = 0; i < PendingFileWrites.MaxConcurrentWrites; i++)
            writes.TryStart($"c{i}", "/repo/a.png", null, null);

        _now += PendingFileWrites.Expiry + TimeSpan.FromSeconds(1);
        writes.RemoveExpired();

        Assert.True(writes.TryStart("next", "/repo/a.png", null, null));
    }
}
