using System.Collections.Concurrent;
using System.Text;

namespace SideHub.Agent;

public sealed class PendingFileWrite(string path, string? ptyPaste, string? ptySessionId, DateTimeOffset now)
{
    public string Path { get; } = path;
    public string? PtyPaste { get; } = ptyPaste;
    public string? PtySessionId { get; } = ptySessionId;
    public StringBuilder Data { get; } = new();
    internal DateTimeOffset LastActivity { get; set; } = now;
}

public enum FileWriteChunkResult { Appended, Unknown, TooLarge }

/// <summary>
/// The base64 chunks of the <c>file.write</c>s in progress (start → chunks → end). Bounded so a
/// backend cannot fill the agent's memory: at most <see cref="MaxConcurrentWrites"/> writes, at
/// most <see cref="FileWritePolicy.MaxBase64Length"/> characters each, and a write that receives
/// nothing for <see cref="Expiry"/> is dropped.
/// </summary>
public sealed class PendingFileWrites(Func<DateTimeOffset>? clock = null, long maxBase64Length = FileWritePolicy.MaxBase64Length)
{
    public const int MaxConcurrentWrites = 8;
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, PendingFileWrite> _writes = new();

    public int Count => _writes.Count;

    /// <summary>Starts (or restarts) a write. False when too many writes are already in progress.</summary>
    public bool TryStart(string commandId, string path, string? ptyPaste, string? ptySessionId)
    {
        if (!_writes.ContainsKey(commandId) && _writes.Count >= MaxConcurrentWrites)
            return false;
        _writes[commandId] = new PendingFileWrite(path, ptyPaste, ptySessionId, _clock());
        return true;
    }

    /// <summary>Appends a chunk. A write that would exceed the size limit is dropped (<see cref="FileWriteChunkResult.TooLarge"/>).</summary>
    public FileWriteChunkResult Append(string commandId, string data)
    {
        if (!_writes.TryGetValue(commandId, out var write))
            return FileWriteChunkResult.Unknown;

        lock (write)
        {
            if (write.Data.Length + (long)data.Length > maxBase64Length)
            {
                _writes.TryRemove(KeyValuePair.Create(commandId, write));
                return FileWriteChunkResult.TooLarge;
            }
            write.Data.Append(data);
            write.LastActivity = _clock();
        }
        return FileWriteChunkResult.Appended;
    }

    public bool TryTake(string commandId, out PendingFileWrite write) => _writes.TryRemove(commandId, out write!);

    /// <summary>Drops the writes idle for longer than <see cref="Expiry"/> and returns their command ids.</summary>
    public IReadOnlyList<string> RemoveExpired()
    {
        var now = _clock();
        var expired = new List<string>();
        foreach (var (commandId, write) in _writes)
        {
            bool idle;
            lock (write) idle = now - write.LastActivity > Expiry;
            if (idle && _writes.TryRemove(KeyValuePair.Create(commandId, write)))
                expired.Add(commandId);
        }
        return expired;
    }
}
