using System.Text;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>Plain text out of a terminal's output: escape sequences and control characters removed.</summary>
public static partial class TerminalText
{
    /// <summary>How much of the end of the output is read: the last lines never need more.</summary>
    private const int ScannedChars = 16 * 1024;

    /// <summary>
    /// The last non-empty lines of <paramref name="output"/> (raw PTY output, already masked), at most
    /// <paramref name="maxLines"/> lines and <paramref name="maxChars"/> characters (the end is kept). A line redrawn with
    /// <c>\r</c> keeps its final state; the shell's own <c>exit</c> echo at the end is dropped. Null when nothing is left.
    /// </summary>
    public static string? LastLines(string? output, int maxLines = 10, int maxChars = 400)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        var tail = output.Length > ScannedChars ? output[^ScannedChars..] : output;
        var text = EscapeSequence().Replace(tail, "");

        var lines = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var redraw = line.LastIndexOf('\r');
            if (redraw >= 0)
                line = line[(redraw + 1)..];
            line = Printable(line).TrimEnd();
            if (line.Length > 0)
                lines.Add(line);
        }
        while (lines.Count > 0 && lines[^1].Trim() == "exit")
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0)
            return null;

        var result = string.Join('\n', lines.Skip(Math.Max(0, lines.Count - maxLines)));
        return result.Length > maxChars ? "…" + result[^(maxChars - 1)..] : result;
    }

    private static string Printable(string line)
    {
        var builder = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c == '\t')
                builder.Append(' ');
            else if (!char.IsControl(c))
                builder.Append(c);
        }
        return builder.ToString();
    }

    // OSC (title, hyperlinks) up to BEL or ST, CSI (colors, cursor), charset selection, then any other two-character escape.
    [GeneratedRegex(@"\x1b\][^\x07\x1b]*(\x07|\x1b\\)?|\x1b\[[0-?]*[ -/]*[@-~]|\x1b[()][0-9A-Za-z]|\x1b[@-_]")]
    private static partial Regex EscapeSequence();
}
