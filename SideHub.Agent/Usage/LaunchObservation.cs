namespace SideHub.Agent.Usage;

/// <summary>
/// The session files a launched CLI process was seen holding open, watched until the process exits.
/// This attributes a Codex rollout with certainty, where matching by cwd and start time can only guess.
/// </summary>
/// <param name="untilFound">Delay between looks while no file is known.</param>
/// <param name="afterFound">Delay once one is: only a new session (e.g. /new) is left to catch.</param>
public sealed class LaunchObservation(TimeSpan untilFound, TimeSpan afterFound)
{
    public LaunchObservation() : this(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)) { }

    private readonly object _gate = new();
    private readonly List<string> _files = [];
    private bool _seenAlive;
    private bool _exited;

    public IReadOnlyList<string> Files
    {
        get { lock (_gate) return _files.ToList(); }
    }

    /// <summary>
    /// True when the files are all the process ever opened: it was seen running, then exited. Otherwise
    /// (still running, or gone before the first look) a session may have been missed.
    /// </summary>
    public bool IsComplete
    {
        get { lock (_gate) return _seenAlive && _exited; }
    }

    /// <param name="probe">Files the process holds open now, or null once it is gone.</param>
    public async Task WatchAsync(Func<IReadOnlyList<string>?> probe, CancellationToken ct)
    {
        while (true)
        {
            var open = probe();
            lock (_gate)
            {
                if (open is null)
                {
                    _exited = true;
                    return;
                }
                _seenAlive = true;
                foreach (var file in open.Where(f => !_files.Contains(f)))
                    _files.Add(file);
            }
            await Task.Delay(Files.Count == 0 ? untilFound : afterFound, ct);
        }
    }
}
