using System.Collections.Concurrent;
using SideHub.Agent.Models;

namespace SideHub.Agent;

/// <summary>
/// The last answer of each PTY's CLI, reported to the backend as <c>pty.cli-message</c> for the phone's Reading mode:
/// a message that repeats the one already sent is not sent again, the latest is replayed after a backend reconnect, and
/// it is forgotten when the PTY ends.
/// </summary>
public sealed class CliMessageTracker
{
    /// <summary>Longest text sent, in characters: a longer answer is cut (the terminal still has all of it).</summary>
    public const int MaxLength = 64_000;

    private readonly ConcurrentDictionary<string, PtyCliMessage> _messages = new();

    /// <summary>The text as sent: cut to <see cref="MaxLength"/>, with a mark where it was cut.</summary>
    public static string Cap(string text) => text.Length <= MaxLength ? text : text[..MaxLength] + "\n\n…";

    /// <summary>Records an answer; returns the message to send, or null when it is the one already sent.</summary>
    public PtyCliMessage? Record(string ptySessionId, string provider, string? cliSessionId, string text, DateTime at)
    {
        text = Cap(text);
        if (_messages.TryGetValue(ptySessionId, out var current) && current.Text == text && current.CliSessionId == cliSessionId)
            return null;
        var message = new PtyCliMessage
        {
            PtySessionId = ptySessionId, Provider = provider, CliSessionId = cliSessionId, Text = text, At = at,
        };
        _messages[ptySessionId] = message;
        return message;
    }

    public PtyCliMessage? Current(string ptySessionId) => _messages.GetValueOrDefault(ptySessionId);

    public void Clear(string ptySessionId) => _messages.TryRemove(ptySessionId, out _);

    public void ClearAll() => _messages.Clear();
}
