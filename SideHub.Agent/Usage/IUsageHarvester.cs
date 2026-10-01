using SideHub.Agent.Models;

namespace SideHub.Agent.Usage;

/// <summary>
/// Reads the token usage of a run from what its CLI leaves on disk.
/// </summary>
public interface IUsageHarvester
{
    /// <summary>Value sent as <c>source</c> in <c>run.usage</c>.</summary>
    string Source { get; }

    /// <summary>
    /// Usage per model, or null when nothing could be read (transcript missing, unknown runtime).
    /// </summary>
    IReadOnlyList<ModelUsageReport>? Harvest(RunUsageContext run);
}

/// <param name="Cwd">Real path of the run's working directory.</param>
/// <param name="CliSessionIds">CLI sessions started in the run's PTY for this harvester's provider.</param>
/// <param name="Launches">Launches of this harvester's CLI in the run's PTY, for CLIs whose session id
/// is not known in advance (codex).</param>
/// <param name="OtherLaunches">Launches of the same CLI in the agent's other PTYs, to detect sessions
/// that could belong to either.</param>
/// <param name="StartedAt">When the agent started tracking the run; a session not written since then is not
/// the run's. Null when unknown.</param>
public sealed record RunUsageContext(
    Guid RunId,
    string Cwd,
    IReadOnlyList<string> CliSessionIds,
    IReadOnlyList<CliLaunch> Launches,
    IReadOnlyList<CliLaunch> OtherLaunches,
    DateTimeOffset? StartedAt = null);

/// <summary>A CLI started in a PTY, as announced by its wrapper (<c>cli-launched</c>).</summary>
/// <param name="Cwd">Physical directory the CLI was started in.</param>
/// <param name="Observation">Session files its process was seen holding open; null when it cannot be
/// watched (no pid announced, no <c>/proc</c>).</param>
public sealed record CliLaunch(
    string PtySessionId, string Provider, string Cwd, DateTimeOffset At, LaunchObservation? Observation = null);

/// <summary>
/// Runtimes whose usage cannot be measured (gemini, shell, or a CLI session we never saw start).
/// </summary>
public sealed class NullHarvester : IUsageHarvester
{
    public const string Unavailable = "unavailable";

    public string Source => Unavailable;

    public IReadOnlyList<ModelUsageReport> Harvest(RunUsageContext run) => [];
}
