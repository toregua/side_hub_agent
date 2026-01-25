using System.Text;

namespace SideHub.Agent;

/// <summary>
/// Thread-safe ring buffer for storing PTY output history.
/// When the buffer is full, oldest data is automatically overwritten.
/// </summary>
public class PtyOutputBuffer
{
    private readonly byte[] _buffer;
    private readonly int _capacity;
    private int _head = 0;
    private int _tail = 0;
    private int _size = 0;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new PTY output buffer with the specified capacity.
    /// </summary>
    /// <param name="capacityBytes">Buffer capacity in bytes. Default is 1MB.</param>
    public PtyOutputBuffer(int capacityBytes = 1024 * 1024)
    {
        _capacity = capacityBytes;
        _buffer = new byte[_capacity];
    }

    /// <summary>
    /// Writes data to the buffer. If the buffer is full, oldest data is overwritten.
    /// </summary>
    public void Write(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var bytes = Encoding.UTF8.GetBytes(data);
        lock (_lock)
        {
            foreach (var b in bytes)
            {
                _buffer[_head] = b;
                _head = (_head + 1) % _capacity;

                if (_size < _capacity)
                {
                    _size++;
                }
                else
                {
                    // Buffer is full, move tail to overwrite oldest data
                    _tail = (_tail + 1) % _capacity;
                }
            }
        }
    }

    /// <summary>
    /// Returns all buffered content as a string.
    /// Handles incomplete UTF-8 sequences at the start by skipping them.
    /// </summary>
    public string GetAll()
    {
        lock (_lock)
        {
            if (_size == 0) return string.Empty;

            var result = new byte[_size];
            if (_tail < _head)
            {
                // Continuous segment
                Array.Copy(_buffer, _tail, result, 0, _size);
            }
            else
            {
                // Wrapped around - need to copy two segments
                var firstPartLength = _capacity - _tail;
                Array.Copy(_buffer, _tail, result, 0, firstPartLength);
                Array.Copy(_buffer, 0, result, firstPartLength, _head);
            }

            // Handle potential incomplete UTF-8 sequence at the start
            var startOffset = FindValidUtf8Start(result);
            if (startOffset > 0)
            {
                var trimmed = new byte[result.Length - startOffset];
                Array.Copy(result, startOffset, trimmed, 0, trimmed.Length);
                result = trimmed;
            }

            return Encoding.UTF8.GetString(result);
        }
    }

    /// <summary>
    /// Clears all buffered content.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _tail = 0;
            _size = 0;
        }
    }

    /// <summary>
    /// Gets the current size of buffered data in bytes.
    /// </summary>
    public int Size
    {
        get
        {
            lock (_lock)
            {
                return _size;
            }
        }
    }

    /// <summary>
    /// Gets the total capacity of the buffer in bytes.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Finds the start of valid UTF-8 content, skipping any incomplete sequence at the beginning.
    /// </summary>
    private static int FindValidUtf8Start(byte[] data)
    {
        if (data.Length == 0) return 0;

        // Check if first byte is a continuation byte (10xxxxxx)
        // If so, skip until we find a valid start byte
        for (int i = 0; i < Math.Min(4, data.Length); i++)
        {
            var b = data[i];
            // Check if this is a valid UTF-8 start byte
            if ((b & 0x80) == 0x00 ||  // ASCII (0xxxxxxx)
                (b & 0xE0) == 0xC0 ||  // 2-byte sequence start (110xxxxx)
                (b & 0xF0) == 0xE0 ||  // 3-byte sequence start (1110xxxx)
                (b & 0xF8) == 0xF0)    // 4-byte sequence start (11110xxx)
            {
                return i;
            }
            // Otherwise it's a continuation byte (10xxxxxx), skip it
        }

        return 0;
    }
}
