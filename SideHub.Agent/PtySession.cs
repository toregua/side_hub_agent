using SideHub.Agent.Models;

namespace SideHub.Agent;

/// <summary>A PTY of the agent (multi-PTY mode), keyed by its ptySessionId.</summary>
/// <param name="StartedAt">When it was spawned (UTC). The backend keeps live sessions in memory only, so every
/// report of the PTY (reattach, replay after a reconnect) carries this original time.</param>
public sealed record PtySession(NodePtyExecutor Executor, string Shell, DateTime StartedAt)
{
    /// <param name="reattached">The PTY was already running (reattach, or replay after a backend reconnect).</param>
    public PtyStartedMessage StartedMessage(string ptySessionId, bool reattached) => new()
    {
        Shell = Shell,
        PtySessionId = ptySessionId,
        Reattached = reattached,
        StartedAt = StartedAt,
    };
}
