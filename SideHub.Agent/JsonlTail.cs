using System.Text;

namespace SideHub.Agent;

/// <summary>
/// Reads the lines appended to a growing JSONL file since the previous call, so a file polled until a line shows up
/// is read once rather than from the start at every poll. A line is returned once its newline is written; one longer
/// than <c>maxLineBytes</c> is skipped without being buffered. A file that shrank (replaced) is read again from the start.
/// Not thread-safe: callers serialize <see cref="ReadNewLines"/>.
/// </summary>
public sealed class JsonlTail(string path, int maxLineBytes)
{
    /// <summary>Where the first line not returned yet starts (or, while <see cref="_skipping"/>, how far it was skipped).</summary>
    private long _offset;

    /// <summary>The line at <see cref="_offset"/> is longer than the limit: drop it up to its newline.</summary>
    private bool _skipping;

    /// <summary>The complete lines written since the last call, without their terminator. Opens the file on enumeration.</summary>
    public IEnumerable<string> ReadNewLines()
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset)
        {
            _offset = 0;
            _skipping = false;
        }
        fs.Seek(_offset, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        var position = _offset;
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', start, read - start);
                var end = newline >= 0 ? newline : read;
                if (!_skipping)
                {
                    if (line.Length + (end - start) > maxLineBytes)
                    {
                        _skipping = true;
                        line.SetLength(0);
                    }
                    else
                    {
                        line.Write(buffer, start, end - start);
                    }
                }
                position += end - start;
                start = end;

                if (newline < 0)
                    break;
                start++;
                position++;
                _offset = position;
                if (_skipping)
                {
                    _skipping = false;
                    continue;
                }
                var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                line.SetLength(0);
                yield return text.EndsWith('\r') ? text[..^1] : text;
            }
        }
        // An unterminated line being skipped is not read again; one still within the limit is, once complete.
        if (_skipping)
            _offset = position;
    }
}
