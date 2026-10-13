namespace SideHub.Cli.Commands;

/// <summary>
/// <c>--work-review on|off</c> of <c>workflow update</c> and <c>scheduler update</c>: whether the runs go « À relire »
/// when they change the agent's folder. Off for a workflow or prompt that only writes to the Drive or a report, so that
/// what another session changes in the folder meanwhile never lands in a review of its own.
/// </summary>
public static class WorkReviewOption
{
    public const string Flag = "--work-review";

    /// <summary>The <c>skipWorkReview</c> value to send: null when the flag is absent, error when it is neither on nor off.</summary>
    public static bool TryParse(string? value, out bool? skipWorkReview, out string? error)
    {
        skipWorkReview = null;
        error = null;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null:
                return true;
            case "on":
                skipWorkReview = false;
                return true;
            case "off":
                skipWorkReview = true;
                return true;
            default:
                error = $"Error: {Flag} takes on or off.";
                return false;
        }
    }

    /// <summary>How <c>get</c> shows the setting of a workflow or scheduler.</summary>
    public static string Describe(bool skipWorkReview) => skipWorkReview ? "off" : "on";
}
