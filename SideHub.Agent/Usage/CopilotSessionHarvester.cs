using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SideHub.Agent.Models;

namespace SideHub.Agent.Usage;

/// <summary>
/// Reads the usage of GitHub Copilot CLI sessions from their event log, <c>&lt;root&gt;/session-state/&lt;sessionId&gt;/events.jsonl</c>:
/// one event per line. The session id is the one <c>sidehub-cli launch</c> gives copilot (<c>--session-id</c>, or the
/// one resumed with <c>--resume</c>), so the file is found by id alone.
/// <list type="bullet">
/// <item>Each model call is a <c>session.usage_record</c> event (<c>data.usage</c>: <c>model</c>, <c>inputTokens</c>,
/// <c>outputTokens</c>, <c>cacheReadTokens</c>, <c>cacheWriteTokens</c>, <c>reasoningTokens</c>). Copilot counts cache
/// reads and writes inside <c>inputTokens</c>: input is reported without them, as for Claude. Reasoning is a subset of
/// output.</item>
/// <item>Copilot names Claude models with a dot (<c>claude-haiku-4.5</c>): turned into Anthropic's id
/// (<c>claude-haiku-4-5</c>), as for agy.</item>
/// <item>A run counts the calls made since it started: a session resumed with <c>--resume</c> keeps its earlier
/// events.</item>
/// </list>
/// </summary>
public sealed partial class CopilotSessionHarvester(string root) : IUsageHarvester, ICliSessionUsageHarvester, IFinalMessageReader
{
    public const string SourceName = "copilot-events";

    private const string UnknownModel = "unknown";

    // File times and the agent's clock may disagree slightly.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(5);

    public string Source => SourceName;

    /// <summary><c>$COPILOT_HOME</c>, else <c>~/.copilot</c>; null when no HOME is known.</summary>
    public static string? Root()
    {
        var copilotHome = Environment.GetEnvironmentVariable("COPILOT_HOME");
        if (!string.IsNullOrEmpty(copilotHome))
            return copilotHome;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".copilot");
    }

    public IReadOnlyList<ModelUsageReport>? Harvest(RunUsageContext run)
    {
        var since = run.StartedAt - ClockSkew;
        var perModel = new Dictionary<string, Totals>(StringComparer.Ordinal);
        var found = false;
        foreach (var file in RunEventLogs(run))
        {
            found = true;
            Read(file, since, perModel, out _);
        }
        return found ? Reports(perModel) : null;
    }

    public string? ReadFinalMessage(RunUsageContext run) =>
        RunEventLogs(run)
            .OrderBy(File.GetLastWriteTimeUtc)
            .Select(LastAnswer)
            .LastOrDefault(text => text is not null);

    public IReadOnlyList<ModelUsageReport>? HarvestSession(string cwd, string cliSessionId)
    {
        if (EventLog(cliSessionId) is not { } file)
            return null;
        var perModel = new Dictionary<string, Totals>(StringComparer.Ordinal);
        Read(file, null, perModel, out _);
        return Reports(perModel);
    }

    public IReadOnlyList<string> SessionFiles(string cwd, string cliSessionId) =>
        EventLog(cliSessionId) is { } file ? [file] : [];

    public string? LastMessage(string cwd, string cliSessionId) =>
        EventLog(cliSessionId) is { } file ? LastAnswer(file) : null;

    private IEnumerable<string> RunEventLogs(RunUsageContext run) =>
        run.CliSessionIds.Distinct()
            .Select(EventLog)
            .Where(file => file is not null && (run.StartedAt is not { } started
                || File.GetLastWriteTimeUtc(file) >= started.UtcDateTime - ClockSkew))
            .Select(file => file!);

    /// <summary>The session's event log, or null. The id comes from the notification FIFO and names a folder: only
    /// UUIDs.</summary>
    private string? EventLog(string cliSessionId)
    {
        if (!FifoNotification.IsValidCliSessionId(cliSessionId))
            return null;
        var path = Path.Combine(root, "session-state", cliSessionId, "events.jsonl");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Adds the log's model calls made since <paramref name="since"/> (all when null) to
    /// <paramref name="perModel"/>; <paramref name="lastAnswer"/> is the text of its last assistant message that has any.</summary>
    private static void Read(string path, DateTimeOffset? since, Dictionary<string, Totals> perModel, out string? lastAnswer)
    {
        lastAnswer = null;
        try
        {
            using var reader = TranscriptLines.Open(path);
            foreach (var line in TranscriptLines.Read(reader))
            {
                if (!line.Contains("\"session.usage_record\"", StringComparison.Ordinal) && !line.Contains("\"assistant.message\"", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var evt = doc.RootElement;
                    if (evt.ValueKind != JsonValueKind.Object
                        || !evt.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                        continue;
                    switch (Str(evt, "type"))
                    {
                        case "assistant.message":
                            if (Str(data, "content") is { } text && !string.IsNullOrWhiteSpace(text))
                                lastAnswer = text;
                            break;
                        case "session.usage_record":
                            if (!data.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                                break;
                            if (since is { } from && Timestamp(evt) is { } at && at < from)
                                break;
                            var model = ModelId(Str(usage, "model")) ?? UnknownModel;
                            var cacheRead = Long(usage, "cacheReadTokens");
                            var cacheWrite = Long(usage, "cacheWriteTokens");
                            var calls = perModel.GetValueOrDefault(model);
                            perModel[model] = new Totals(
                                calls.Input + Math.Max(0, Long(usage, "inputTokens") - cacheRead - cacheWrite),
                                calls.CacheRead + cacheRead, calls.CacheWrite + cacheWrite,
                                calls.Output + Long(usage, "outputTokens"), calls.Reasoning + Long(usage, "reasoningTokens"),
                                calls.Requests + 1);
                            break;
                    }
                }
                catch (JsonException) { /* a line being written */ }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? LastAnswer(string path)
    {
        Read(path, null, new Dictionary<string, Totals>(StringComparer.Ordinal), out var answer);
        return answer;
    }

    /// <summary>
    /// The model id copilot reports, with Claude's dots turned into dashes (<c>claude-haiku-4.5</c> →
    /// <c>claude-haiku-4-5</c>); other ids as they are (<c>gpt-5.4</c>). Null for anything that is not an id.
    /// </summary>
    public static string? ModelId(string? model)
    {
        var id = model?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id))
            return null;
        if (id.StartsWith("claude-", StringComparison.Ordinal))
            id = id.Replace('.', '-');
        return id.Length <= 100 && ModelIdPattern().IsMatch(id) ? id : null;
    }

    private static List<ModelUsageReport> Reports(Dictionary<string, Totals> perModel) =>
        perModel
            .Select(kv => new ModelUsageReport
            {
                Model = kv.Key,
                InputTokens = kv.Value.Input,
                OutputTokens = kv.Value.Output,
                CacheReadTokens = kv.Value.CacheRead,
                CacheWriteTokens = kv.Value.CacheWrite,
                ReasoningTokens = kv.Value.Reasoning,
                Requests = kv.Value.Requests,
            })
            .OrderBy(r => r.Model, StringComparer.Ordinal)
            .ToList();

    private static DateTimeOffset? Timestamp(JsonElement evt) =>
        Str(evt, "timestamp") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? Math.Max(0, n) : 0;

    private readonly record struct Totals(long Input, long CacheRead, long CacheWrite, long Output, long Reasoning, int Requests);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]*$")]
    private static partial Regex ModelIdPattern();
}
