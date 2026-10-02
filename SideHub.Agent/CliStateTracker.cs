using System.Collections.Concurrent;
using SideHub.Agent.Models;

namespace SideHub.Agent;

/// <summary>What a coding CLI is doing in a terminal, as its hooks report it (<c>sidehub-cli cli-state</c>).</summary>
public static class CliStates
{
    /// <summary>A turn is in progress: the CLI is thinking or running tools.</summary>
    public const string Working = "working";

    /// <summary>The CLI needs a human: a permission prompt or a question it asked.</summary>
    public const string WaitingInput = "waiting-input";

    /// <summary>The turn is over.</summary>
    public const string Idle = "idle";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Working, WaitingInput, Idle };
}

/// <summary>A CLI's last reported state in a PTY, and when the agent learned it (UTC).</summary>
public sealed record PtyCliState(string Provider, string State, string? CliSessionId, DateTime At)
{
    public PtyCliStateMessage Message(string ptySessionId) => new()
    {
        PtySessionId = ptySessionId,
        CliSessionId = CliSessionId,
        Provider = Provider,
        State = State,
        At = At,
    };
}

/// <summary>
/// The latest CLI state of each PTY, reported to the backend as <c>pty.cli-state</c>: a report that repeats the
/// last one (same state, same session) is not sent again, the latest state is replayed after a backend reconnect,
/// and the state is forgotten when the CLI or the PTY ends (the backend clears its own on the CLI's end).
/// </summary>
public sealed class CliStateTracker
{
    private readonly ConcurrentDictionary<string, PtyCliState> _states = new();

    /// <summary>Records a report; returns the message to send, or null when it repeats the PTY's current state.</summary>
    public PtyCliStateMessage? Record(string ptySessionId, string provider, string state, string? cliSessionId, DateTime at)
    {
        if (!CliStates.All.Contains(state))
            throw new ArgumentException($"Unknown CLI state '{state}'.", nameof(state));
        if (_states.TryGetValue(ptySessionId, out var current)
            && current.State == state && current.Provider == provider && current.CliSessionId == cliSessionId)
            return null;
        var next = new PtyCliState(provider, state, cliSessionId, at);
        _states[ptySessionId] = next;
        return next.Message(ptySessionId);
    }

    /// <summary>The PTY's current state, to replay after a backend reconnect; null when none is known.</summary>
    public PtyCliStateMessage? Current(string ptySessionId) =>
        _states.TryGetValue(ptySessionId, out var state) ? state.Message(ptySessionId) : null;

    /// <summary>The CLI exited or the PTY closed: nothing is known any more.</summary>
    public void Clear(string ptySessionId) => _states.TryRemove(ptySessionId, out _);

    public void ClearAll() => _states.Clear();
}
