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
public sealed record RunUsageContext(Guid RunId, string Cwd, IReadOnlyList<string> CliSessionIds);

/// <summary>
/// Runtimes whose usage cannot be measured (gemini, shell, or a CLI session we never saw start).
/// </summary>
public sealed class NullHarvester : IUsageHarvester
{
    public const string Unavailable = "unavailable";

    public string Source => Unavailable;

    public IReadOnlyList<ModelUsageReport> Harvest(RunUsageContext run) => [];
}
