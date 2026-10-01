using System.Text;

namespace SideHub.Agent.Usage;

/// <summary>
/// The lines of a CLI transcript (Claude session JSONL, Codex rollout), read through a
/// <see cref="BoundedLineReader"/>: anything in the PTY can write those files, so a line is never buffered past
/// <see cref="MaxLineLength"/>. Lines that long (a pasted file, an image) carry no usage and are skipped.
/// </summary>
public static class TranscriptLines
{
    /// <summary>Longest transcript line read, in characters (32 MB in memory).</summary>
    public const int MaxLineLength = 16 * 1024 * 1024;

    /// <summary>Opens <paramref name="path"/> for reading while the CLI may still write, rename or delete it.</summary>
    public static StreamReader Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);

    /// <summary>The lines of <paramref name="reader"/> no longer than <paramref name="maxLineLength"/>, in order.</summary>
    public static IEnumerable<string> Read(TextReader reader, int maxLineLength = MaxLineLength)
    {
        var lines = new BoundedLineReader(reader, maxLineLength);
        while (lines.ReadLine() is { } line)
        {
            if (!line.TooLong)
                yield return line.Text;
        }
    }
}
