using System.Text.Json;

namespace SideHub.Agent.Tests;

/// <summary>pty.started carries the PTY's spawn time, the same on every report of it.</summary>
public class PtySessionTests
{
    private static readonly DateTime SpawnedAt = new(2026, 10, 2, 7, 15, 30, DateTimeKind.Utc);

    [Fact]
    public void ReattachAndReplay_SendTheOriginalSpawnTime()
    {
        var session = new PtySession(new NodePtyExecutor(Path.GetTempPath()), "bash", SpawnedAt);

        var spawned = session.StartedMessage("terminal-1", reattached: false);
        // Reattach (pty.start on a running PTY) and replay after a backend reconnect, long after the spawn.
        var reattached = session.StartedMessage("terminal-1", reattached: true);

        Assert.False(spawned.Reattached);
        Assert.True(reattached.Reattached);
        Assert.Equal(SpawnedAt, spawned.StartedAt);
        Assert.Equal(SpawnedAt, reattached.StartedAt);
        Assert.Equal("bash", reattached.Shell);
        Assert.Equal("terminal-1", reattached.PtySessionId);
    }

    [Fact]
    public void StartedAt_IsSentAsIso8601Utc()
    {
        var session = new PtySession(new NodePtyExecutor(Path.GetTempPath()), "bash", SpawnedAt);

        var json = JsonSerializer.Serialize(session.StartedMessage("terminal-1", reattached: true));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("2026-10-02T07:15:30Z", doc.RootElement.GetProperty("startedAt").GetString());
        Assert.True(doc.RootElement.GetProperty("startedAt").TryGetDateTime(out var parsed));
        Assert.Equal(SpawnedAt, parsed.ToUniversalTime());
    }
}
