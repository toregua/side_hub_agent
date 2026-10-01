namespace SideHub.Agent.Tests;

public class PendingFileWritesTests
{
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private PendingFileWrites Create(long maxBase64Length = FileWritePolicy.MaxBase64Length) =>
        new(() => _now, maxBase64Length);

    [Fact]
    public void Chunks_are_buffered_until_the_write_is_taken()
    {
        var writes = Create();
        Assert.True(writes.TryStart("c1", "/repo/a.png", null, "pty1"));

        Assert.Equal(FileWriteChunkResult.Appended, writes.Append("c1", "QUJD"));
        Assert.Equal(FileWriteChunkResult.Appended, writes.Append("c1", "REVG"));

        Assert.True(writes.TryTake("c1", out var write));
        Assert.Equal("QUJDREVG", write.Data.ToString());
        Assert.Equal("pty1", write.PtySessionId);
        Assert.False(writes.TryTake("c1", out _));
    }

    [Fact]
    public void Chunk_for_an_unknown_write_is_ignored()
    {
        Assert.Equal(FileWriteChunkResult.Unknown, Create().Append("nope", "QUJD"));
    }

    [Fact]
    public void Write_exceeding_the_size_limit_is_dropped()
    {
        var writes = Create(maxBase64Length: 8);
        writes.TryStart("c1", "/repo/a.png", null, null);

        Assert.Equal(FileWriteChunkResult.Appended, writes.Append("c1", "QUJDREVG"));
        Assert.Equal(FileWriteChunkResult.TooLarge, writes.Append("c1", "Rw=="));
        Assert.False(writes.TryTake("c1", out _));
        Assert.Equal(FileWriteChunkResult.Unknown, writes.Append("c1", "Rw=="));
    }

    [Fact]
    public void Default_limit_matches_the_maximum_file_size()
    {
        Assert.Equal(FileWritePolicy.MaxBase64Length, Convert.ToBase64String(new byte[FileWritePolicy.MaxFileBytes]).Length);
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
