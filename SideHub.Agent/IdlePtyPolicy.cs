namespace SideHub.Agent;

/// <summary>
/// When the reaper may stop a terminal left alone (not a run: the backend ends those). A terminal counts as used while
/// someone types in it or it prints something (a build, a CLI turn). A CLI that is working or waiting for an answer
/// keeps its terminal up to <see cref="BusyCliTimeout"/> without input or output: its state can stay <c>working</c>
/// after a turn interrupted with Escape (Claude's Stop hook does not fire). The terminal of a queued task, or of a fix,
/// whose review round is still open is never stopped: its work is not reported yet.
/// </summary>
public static class IdlePtyPolicy
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan BusyCliTimeout = TimeSpan.FromHours(4);

    /// <summary>Whether the terminal can be stopped now.</summary>
    public static bool ShouldReap(DateTime now, DateTime lastInput, DateTime? lastOutput, string? cliState, bool openReviewRound)
    {
        if (openReviewRound)
            return false;
        var timeout = cliState is CliStates.Working or CliStates.WaitingInput ? BusyCliTimeout : IdleTimeout;
        return now - LastUse(lastInput, lastOutput) > timeout;
    }

    /// <summary>The last input or output of the terminal.</summary>
    public static DateTime LastUse(DateTime lastInput, DateTime? lastOutput) =>
        lastOutput is { } output && output > lastInput ? output : lastInput;
}
