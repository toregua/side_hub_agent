using System.Text;

namespace SideHub.Agent;

/// <summary>
/// Reads lines like <see cref="TextReader.ReadLine"/>, but never buffers more than <c>maxLineLength</c> characters:
/// a longer line is skipped up to its newline and reported as <see cref="BoundedLine.TooLong"/>. For streams whose
/// writer the agent does not control (the notification FIFO, a command's output, CLI transcripts), where an endless
/// line would otherwise grow the agent's memory without bound.
/// </summary>
public sealed class BoundedLineReader(TextReader reader, int maxLineLength)
{
    private readonly char[] _buffer = new char[4096];
    private readonly StringBuilder _line = new();
    private int _position;
    private int _length;
    private bool _tooLong;

    /// <summary>The next line without its terminator (<c>\n</c> or <c>\r\n</c>), or null at the end of the stream.</summary>
    public async Task<BoundedLine?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_position == _length)
            {
                _position = 0;
                _length = await reader.ReadAsync(_buffer.AsMemory(), ct);
                if (_length == 0)
                    return EndOfStream();
            }
            if (Consume() is { } line)
                return line;
        }
    }

    /// <inheritdoc cref="ReadLineAsync"/>
    public BoundedLine? ReadLine()
    {
        while (true)
        {
            if (_position == _length)
            {
                _position = 0;
                _length = reader.Read(_buffer.AsSpan());
                if (_length == 0)
                    return EndOfStream();
            }
            if (Consume() is { } line)
                return line;
        }
    }

    /// <summary>Takes the buffered characters up to the next newline; the line once it is complete, else null.</summary>
    private BoundedLine? Consume()
    {
        var span = _buffer.AsSpan(_position, _length - _position);
        var newline = span.IndexOf('\n');
        var chunk = newline >= 0 ? span[..newline] : span;
        if (!_tooLong)
        {
            if (_line.Length + chunk.Length > maxLineLength + 1) // +1: a '\r' before the '\n'
            {
                _tooLong = true;
                _line.Clear();
            }
            else
            {
                _line.Append(chunk);
            }
        }
        _position += chunk.Length;

        if (newline < 0)
            return null;
        _position++;
        return Complete();
    }

    private BoundedLine? EndOfStream() => _line.Length == 0 && !_tooLong ? null : Complete();

    private BoundedLine Complete()
    {
        if (_line.Length > 0 && _line[^1] == '\r')
            _line.Length--;
        var line = _tooLong || _line.Length > maxLineLength
            ? new BoundedLine(string.Empty, TooLong: true)
            : new BoundedLine(_line.ToString(), TooLong: false);
        _line.Clear();
        _tooLong = false;
        return line;
    }
}

/// <param name="Text">The line; empty when <paramref name="TooLong"/>.</param>
public readonly record struct BoundedLine(string Text, bool TooLong);
