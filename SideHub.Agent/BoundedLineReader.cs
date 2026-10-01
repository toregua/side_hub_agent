using System.Text;

namespace SideHub.Agent;

/// <summary>
/// Reads lines like <see cref="TextReader.ReadLineAsync(CancellationToken)"/>, but never buffers more than
/// <c>maxLineLength</c> characters: a longer line is skipped up to its newline and reported as
/// <see cref="BoundedLine.TooLong"/>. For streams an untrusted writer feeds (the notification FIFO), where an
/// endless line would otherwise grow the agent's memory without bound.
/// </summary>
public sealed class BoundedLineReader(TextReader reader, int maxLineLength)
{
    private readonly char[] _buffer = new char[4096];
    private int _position;
    private int _length;

    /// <summary>The next line without its terminator (<c>\n</c> or <c>\r\n</c>), or null at the end of the stream.</summary>
    public async Task<BoundedLine?> ReadLineAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        var tooLong = false;
        while (true)
        {
            if (_position == _length)
            {
                _position = 0;
                _length = await reader.ReadAsync(_buffer.AsMemory(), ct);
                if (_length == 0)
                    return line.Length == 0 && !tooLong ? null : Complete(line, tooLong);
            }

            var span = _buffer.AsSpan(_position, _length - _position);
            var newline = span.IndexOf('\n');
            var chunk = newline >= 0 ? span[..newline] : span;
            if (!tooLong)
            {
                if (line.Length + chunk.Length > maxLineLength + 1) // +1: a '\r' before the '\n'
                {
                    tooLong = true;
                    line.Clear();
                }
                else
                {
                    line.Append(chunk);
                }
            }
            _position += chunk.Length;

            if (newline >= 0)
            {
                _position++;
                return Complete(line, tooLong);
            }
        }
    }

    private BoundedLine Complete(StringBuilder line, bool tooLong)
    {
        if (line.Length > 0 && line[^1] == '\r')
            line.Length--;
        if (line.Length > maxLineLength)
            tooLong = true;
        return tooLong ? new BoundedLine(string.Empty, TooLong: true) : new BoundedLine(line.ToString(), TooLong: false);
    }
}

/// <param name="Text">The line; empty when <paramref name="TooLong"/>.</param>
public readonly record struct BoundedLine(string Text, bool TooLong);
