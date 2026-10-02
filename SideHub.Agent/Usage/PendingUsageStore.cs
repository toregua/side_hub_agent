using System.Text.Json;
using SideHub.Agent.Models;

namespace SideHub.Agent.Usage;

/// <summary>
/// <c>run.usage</c> reports that could not be sent (backend disconnected), one file per run
/// (<c>{runId}.json</c>), replayed at the next connection. A newer report for the same run overwrites the older one.
/// </summary>
public sealed class PendingUsageStore(string directory) : PendingReportStore<RunUsageMessage>(directory)
{
    public void Delete(Guid runId) => Delete(runId.ToString());

    protected override string KeyOf(RunUsageMessage report) => report.RunId.ToString();
}

/// <summary>
/// <c>cli-session.usage</c> snapshots that could not be sent, one file per CLI session (<c>{cliSessionId}.json</c>):
/// a newer snapshot of the session replaces the queued one.
/// </summary>
public sealed class PendingCliSessionUsageStore(string directory) : PendingReportStore<CliSessionUsageMessage>(directory)
{
    // The id names the file: only UUIDs (the collector tracks no other), checked again here.
    protected override string KeyOf(CliSessionUsageMessage report) =>
        FifoNotification.IsValidCliSessionId(report.CliSessionId)
            ? report.CliSessionId
            : throw new ArgumentException("Invalid CLI session id.", nameof(report));
}

/// <summary>Reports kept on disk while the backend is unreachable, one file per key, replayed at the next connection.</summary>
public abstract class PendingReportStore<TReport>(string directory) where TReport : class
{
    public string Directory => directory;

    /// <summary>Names the report's file: a newer report with the same key overwrites the older one.</summary>
    protected abstract string KeyOf(TReport report);

    public void Save(TReport report)
    {
        PrivateFiles.CreateDirectory(directory);
        var path = PathFor(KeyOf(report));
        var tmp = path + ".tmp";
        PrivateFiles.WriteAllText(tmp, JsonSerializer.Serialize(report));
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string key)
    {
        try { File.Delete(PathFor(key)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Pending reports, oldest first. Unreadable files are skipped (and left for inspection).</summary>
    public IReadOnlyList<TReport> LoadAll(Action<string>? log = null)
    {
        if (!System.IO.Directory.Exists(directory))
            return [];

        var reports = new List<TReport>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderBy(f => f.LastWriteTimeUtc))
        {
            try
            {
                var report = JsonSerializer.Deserialize<TReport>(File.ReadAllText(file.FullName));
                if (report is not null)
                    reports.Add(report);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"Skipping unreadable pending usage file {file.Name}: {ex.Message}");
            }
        }
        return reports;
    }

    private string PathFor(string key) => Path.Combine(directory, $"{key}.json");
}
