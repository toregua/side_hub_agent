using System.Text;

namespace SideHub.Agent;

/// <summary>
/// Forwards everything to another writer and keeps the last lines written: what a command printed just before it
/// failed (its error message), to report it (<see cref="DiagnosticReporter"/>).
/// </summary>
public sealed class OutputTail(TextWriter inner, int maxLines) : TextWriter
{
    private const string Prefix = "[SideHub] ";
    private readonly Queue<string> _lines = new();
    private readonly StringBuilder _current = new();

    public override Encoding Encoding => inner.Encoding;

    /// <summary>The last non-empty lines, without their "[SideHub] " prefix, oldest first.</summary>
    public string Text
    {
        get
        {
            var current = _current.ToString().Trim();
            var lines = current.Length > 0 ? _lines.Append(current) : _lines;
            return string.Join('\n', lines.Select(l => l.StartsWith(Prefix, StringComparison.Ordinal) ? l[Prefix.Length..] : l)
                .TakeLast(maxLines));
        }
    }

    public override void Write(char value)
    {
        inner.Write(value);
        if (value == '\n')
            EndLine();
        else if (value != '\r')
            _current.Append(value);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        foreach (var c in value) Write(c);
    }

    public override void Flush() => inner.Flush();

    private void EndLine()
    {
        var line = _current.ToString().Trim();
        _current.Clear();
        if (line.Length == 0) return;
        _lines.Enqueue(line);
        while (_lines.Count > maxLines) _lines.Dequeue();
    }
}
