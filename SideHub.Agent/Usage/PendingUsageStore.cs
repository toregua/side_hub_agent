using System.Text.Json;
using SideHub.Agent.Models;

namespace SideHub.Agent.Usage;

/// <summary>
/// <c>run.usage</c> reports that could not be sent (backend disconnected), one file per run
/// (<c>{runId}.json</c>), replayed at the next connection. A newer report for the same run overwrites the older one.
/// </summary>
public sealed class PendingUsageStore(string directory)
{
    public string Directory => directory;

    public void Save(RunUsageMessage report)
    {
        System.IO.Directory.CreateDirectory(directory);
        var path = PathFor(report.RunId);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(report));
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(Guid runId)
    {
        try { File.Delete(PathFor(runId)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Pending reports, oldest first. Unreadable files are skipped (and left for inspection).</summary>
    public IReadOnlyList<RunUsageMessage> LoadAll(Action<string>? log = null)
    {
        if (!System.IO.Directory.Exists(directory))
            return [];

        var reports = new List<RunUsageMessage>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderBy(f => f.LastWriteTimeUtc))
        {
            try
            {
                var report = JsonSerializer.Deserialize<RunUsageMessage>(File.ReadAllText(file.FullName));
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

    private string PathFor(Guid runId) => Path.Combine(directory, $"{runId}.json");
}
