using System.Collections.Concurrent;

namespace SideHub.Agent;

/// <summary>
/// A <c>file.write</c> in progress. Its base64 chunks are decoded as they arrive and kept as bytes (a quarter of
/// the memory the text takes as UTF-16), never joined into one string. Disposing it gives its bytes back to the
/// <see cref="PendingFileWrites"/> budget.
/// </summary>
public sealed class PendingFileWrite : IDisposable
{
    private readonly List<byte[]> _chunks = [];
    private readonly Action<long> _release;
    // Base64 decodes by groups of 4 characters: the 0-3 trailing ones of a chunk wait for the next.
    private readonly char[] _carry = new char[4];
    private int _carryLength;
    // Padding ('=') ends the base64 text: any data after it is invalid, as it was for Convert.FromBase64String.
    private bool _padded;
    private long _released;

    internal PendingFileWrite(string path, string? ptyPaste, string? ptySessionId, DateTimeOffset now, Action<long> release)
    {
        Path = path;
        PtyPaste = ptyPaste;
        PtySessionId = ptySessionId;
        LastActivity = now;
        _release = release;
    }

    public string Path { get; }
    public string? PtyPaste { get; }
    public string? PtySessionId { get; }

    /// <summary>Decoded bytes so far.</summary>
    public long Length { get; private set; }

    /// <summary>False when the base64 text stopped mid-group: the upload was cut short.</summary>
    public bool IsComplete => _carryLength == 0;

    internal DateTimeOffset LastActivity { get; set; }

    /// <summary>Taken, dropped or expired: a chunk racing its removal must not be added (its bytes would never be released).</summary>
    internal bool IsDisposed { get; private set; }

    /// <summary>Decodes <paramref name="data"/>; null when it is not valid base64 (or follows padding).</summary>
    internal byte[]? Decode(string data)
    {
        var text = data.AsSpan();
        if (text.ContainsAny(" \t\r\n"))
            text = string.Concat(data.Where(c => !char.IsWhiteSpace(c))).AsSpan();
        if (text.IsEmpty)
            return [];
        if (_padded)
            return null;

        var total = _carryLength + text.Length;
        var whole = total - total % 4;
        var chars = new char[whole];
        var fromCarry = Math.Min(_carryLength, whole);
        _carry.AsSpan(0, fromCarry).CopyTo(chars);
        text[..(whole - fromCarry)].CopyTo(chars.AsSpan(fromCarry));

        var bytes = new byte[whole / 4 * 3];
        if (!Convert.TryFromBase64Chars(chars, bytes, out var written))
            return null;

        // What did not fill a group of 4 waits for the next chunk.
        var rest = text[(whole - fromCarry)..];
        if (fromCarry < _carryLength)
            _carry.AsSpan(fromCarry, _carryLength - fromCarry).CopyTo(_carry);
        _carryLength -= fromCarry;
        rest.CopyTo(_carry.AsSpan(_carryLength));
        _carryLength += rest.Length;

        _padded = whole > 0 && chars[^1] == '=';
        return written == bytes.Length ? bytes : bytes[..written];
    }

    internal void Add(byte[] bytes)
    {
        if (bytes.Length == 0) return;
        _chunks.Add(bytes);
        Length += bytes.Length;
    }

    /// <summary>Writes the decoded file to <paramref name="destination"/>.</summary>
    public async Task WriteToAsync(Stream destination, CancellationToken ct)
    {
        foreach (var chunk in _chunks)
            await destination.WriteAsync(chunk, ct);
    }

    public void Dispose()
    {
        long toRelease;
        lock (this)
        {
            IsDisposed = true;
            toRelease = Length - _released;
            _released = Length;
            _chunks.Clear();
        }
        if (toRelease > 0)
            _release(toRelease);
    }
}

public enum FileWriteChunkResult { Appended, Unknown, TooLarge, OverBudget, Invalid }

/// <summary>
/// The decoded chunks of the <c>file.write</c>s in progress (start → chunks → end). Bounded so a
/// backend cannot fill the agent's memory: at most <see cref="MaxConcurrentWrites"/> writes, at
/// most <see cref="FileWritePolicy.MaxFileBytes"/> bytes each and <see cref="MaxTotalBytes"/> for all
/// of them, and a write that receives nothing for <see cref="Expiry"/> is dropped.
/// </summary>
public sealed class PendingFileWrites(
    Func<DateTimeOffset>? clock = null,
    long maxFileBytes = FileWritePolicy.MaxFileBytes,
    long maxTotalBytes = PendingFileWrites.MaxTotalBytes)
{
    public const int MaxConcurrentWrites = 8;

    /// <summary>Bytes buffered across all writes in progress: two files of the maximum size.</summary>
    public const long MaxTotalBytes = 2 * FileWritePolicy.MaxFileBytes;

    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, PendingFileWrite> _writes = new();
    private long _totalBytes;

    public int Count => _writes.Count;

    /// <summary>Bytes held by the writes in progress (and by taken writes not disposed yet).</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>Starts (or restarts) a write. False when too many writes are already in progress.</summary>
    public bool TryStart(string commandId, string path, string? ptyPaste, string? ptySessionId)
    {
        if (!_writes.ContainsKey(commandId) && _writes.Count >= MaxConcurrentWrites)
            return false;
        var write = new PendingFileWrite(path, ptyPaste, ptySessionId, _clock(), Release);
        PendingFileWrite? replaced = null;
        _writes.AddOrUpdate(commandId, write, (_, previous) =>
        {
            replaced = previous;
            return write;
        });
        replaced?.Dispose();
        return true;
    }

    /// <summary>
    /// Decodes and appends a chunk. A write that would exceed its size limit (<see cref="FileWriteChunkResult.TooLarge"/>)
    /// or the shared budget (<see cref="FileWriteChunkResult.OverBudget"/>), or that is not valid base64
    /// (<see cref="FileWriteChunkResult.Invalid"/>), is dropped.
    /// </summary>
    public FileWriteChunkResult Append(string commandId, string data)
    {
        if (!_writes.TryGetValue(commandId, out var write))
            return FileWriteChunkResult.Unknown;

        FileWriteChunkResult result;
        lock (write)
        {
            result = TryAppend(write, data);
            if (result == FileWriteChunkResult.Appended)
                write.LastActivity = _clock();
        }
        if (result != FileWriteChunkResult.Appended && _writes.TryRemove(KeyValuePair.Create(commandId, write)))
            write.Dispose();
        return result;
    }

    private FileWriteChunkResult TryAppend(PendingFileWrite write, string data)
    {
        if (write.IsDisposed)
            return FileWriteChunkResult.Unknown;
        // A chunk decodes to at most 3/4 of its length: refuse it before decoding when even that is too much.
        if (write.Length + (data.Length / 4 * 3) > maxFileBytes + 3)
            return FileWriteChunkResult.TooLarge;
        if (write.Decode(data) is not { } bytes)
            return FileWriteChunkResult.Invalid;
        if (write.Length + bytes.Length > maxFileBytes)
            return FileWriteChunkResult.TooLarge;
        if (Interlocked.Add(ref _totalBytes, bytes.Length) > maxTotalBytes)
        {
            Interlocked.Add(ref _totalBytes, -bytes.Length);
            return FileWriteChunkResult.OverBudget;
        }
        write.Add(bytes);
        return FileWriteChunkResult.Appended;
    }

    /// <summary>Takes a write out to finish it; the caller disposes it once written, freeing its share of the budget.</summary>
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
            {
                write.Dispose();
                expired.Add(commandId);
            }
        }
        return expired;
    }

    private void Release(long bytes) => Interlocked.Add(ref _totalBytes, -bytes);
}
