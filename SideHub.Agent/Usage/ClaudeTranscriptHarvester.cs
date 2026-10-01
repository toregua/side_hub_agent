using System.Text.Json;
using SideHub.Agent.Models;

namespace SideHub.Agent.Usage;

/// <summary>
/// Reads a run's usage from its Claude session transcripts
/// (<c>&lt;projects&gt;/&lt;encoded cwd&gt;/&lt;cliSessionId&gt;.jsonl</c>), per session:
/// <list type="bullet">
/// <item>Once the CLI has exited, the session's last <c>cost-state</c> line holds Claude's own totals per
/// model — what <c>/cost</c> shows, sub-agents and calls that never reach the transcript included.</item>
/// <item>While the CLI is still running (step ended, CLI left open), there is no up-to-date
/// <c>cost-state</c>: <c>message.usage</c> is summed per <c>message.model</c> over the session file and its
/// sub-agent files (<c>&lt;cliSessionId&gt;/subagents/*.jsonl</c>). A streamed message is written several
/// times under the same <c>message.id</c>, output growing until the last write, so each id counts once
/// with its largest counters. Slightly below <c>/cost</c>, which the exit report then replaces.</item>
/// </list>
/// </summary>
public sealed class ClaudeTranscriptHarvester(string projectsRoot) : IUsageHarvester
{
    public const string SourceName = "claude-transcript";

    // Claude's placeholder for locally generated messages (e.g. an interrupted turn): no API call.
    private const string SyntheticModel = "<synthetic>";

    public string Source => SourceName;

    public IReadOnlyList<ModelUsageReport>? Harvest(RunUsageContext run)
    {
        var reports = new List<ModelUsageReport>();
        // Shared across sessions: a forked session repeats its parent's messages under the same ids.
        var summedMessages = new Dictionary<string, MessageUsage>();
        var found = false;

        // The ids come from the notification FIFO and name files: only UUIDs, whatever the caller checked.
        foreach (var sessionId in run.CliSessionIds.Distinct().Where(FifoNotification.IsValidCliSessionId))
        {
            var sessionFile = FindSessionFile(run.Cwd, sessionId);
            if (sessionFile is null)
                continue;
            found = true;

            var messages = new Dictionary<string, MessageUsage>();
            var finalCost = ReadFile(sessionFile, messages);
            foreach (var subagentFile in FindSubagentFiles(sessionFile, sessionId))
                ReadFile(subagentFile, messages);

            if (finalCost is not null)
            {
                reports.AddRange(finalCost.Select(c => c.WithRequests(messages.Values)));
            }
            else
            {
                foreach (var (id, usage) in messages)
                    summedMessages[id] = summedMessages.TryGetValue(id, out var seen) ? seen.Max(usage) : usage;
            }
        }

        if (!found)
            return null;

        reports.AddRange(summedMessages.Values
            .GroupBy(m => m.Model)
            .Select(g => new ModelUsageReport
            {
                Model = g.Key,
                InputTokens = g.Sum(m => m.Input),
                OutputTokens = g.Sum(m => m.Output),
                CacheReadTokens = g.Sum(m => m.CacheRead),
                CacheWriteTokens = g.Sum(m => m.CacheWrite),
                Requests = g.Count(),
            }));

        // Several sessions may report the same model.
        return reports
            .GroupBy(r => r.Model, StringComparer.Ordinal)
            .Select(g => new ModelUsageReport
            {
                Model = g.Key,
                InputTokens = g.Sum(r => r.InputTokens),
                OutputTokens = g.Sum(r => r.OutputTokens),
                CacheReadTokens = g.Sum(r => r.CacheReadTokens),
                CacheWriteTokens = g.Sum(r => r.CacheWriteTokens),
                Requests = g.Sum(r => r.Requests),
            })
            .OrderBy(r => r.Model, StringComparer.Ordinal)
            .ToList();
    }

    private string? FindSessionFile(string cwd, string cliSessionId)
    {
        var fileName = cliSessionId + ".jsonl";
        var expected = Path.Combine(ClaudeProjectPaths.ProjectDirectory(projectsRoot, cwd), fileName);
        if (File.Exists(expected))
            return expected;

        // Session ids are unique: find the file wherever Claude put it (very long cwds get a
        // hashed directory name, symlinked cwds may be encoded differently).
        if (!Directory.Exists(projectsRoot))
            return null;
        return Directory.EnumerateDirectories(projectsRoot)
            .Select(dir => Path.Combine(dir, fileName))
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> FindSubagentFiles(string sessionFile, string cliSessionId)
    {
        var subagents = Path.Combine(Path.GetDirectoryName(sessionFile)!, cliSessionId, "subagents");
        return Directory.Exists(subagents)
            ? Directory.EnumerateFiles(subagents, "*.jsonl").Order(StringComparer.Ordinal)
            : [];
    }

    /// <summary>
    /// Adds the file's assistant messages to <paramref name="messages"/> and returns its final cost —
    /// the last <c>cost-state</c>, if no assistant message follows it — or null.
    /// </summary>
    private static List<CostEntry>? ReadFile(string path, Dictionary<string, MessageUsage> messages)
    {
        List<CostEntry>? cost = null;
        var costLine = 0;
        var lastAssistantLine = 0;
        try
        {
            using var reader = TranscriptLines.Open(path);
            var lineNumber = 0;
            foreach (var line in TranscriptLines.Read(reader))
            {
                lineNumber++;
                if (line.Contains("\"cost-state\"", StringComparison.Ordinal) && TryParseCostState(line) is { } parsed)
                {
                    cost = parsed;
                    costLine = lineNumber;
                }
                else if (line.Contains("\"usage\"", StringComparison.Ordinal)
                    && TryParseAssistant(line, out var messageId, out var usage))
                {
                    lastAssistantLine = lineNumber;
                    var key = messageId ?? $"{path}:{lineNumber}";
                    messages[key] = messages.TryGetValue(key, out var seen) ? seen.Max(usage) : usage;
                }
            }
        }
        catch (IOException) { /* deleted or unreadable: count what we have */ }
        catch (UnauthorizedAccessException) { }

        return cost is not null && costLine > lastAssistantLine ? cost : null;
    }

    private static bool TryParseAssistant(string line, out string? messageId, out MessageUsage usage)
    {
        messageId = null;
        usage = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Str(root, "type") != "assistant"
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
                return false;

            var model = Str(message, "model");
            if (string.IsNullOrEmpty(model) || model == SyntheticModel)
                return false;

            messageId = Str(message, "id");
            usage = new MessageUsage(
                model,
                Long(u, "input_tokens"),
                Long(u, "output_tokens"),
                Long(u, "cache_read_input_tokens"),
                Long(u, "cache_creation_input_tokens"));
            return true;
        }
        catch (JsonException)
        {
            return false; // corrupted or half-written line
        }
    }

    /// <summary><c>{"type":"cost-state", "modelUsage": {"&lt;model&gt;": {"inputTokens": …}}}</c></summary>
    private static List<CostEntry>? TryParseCostState(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Str(root, "type") != "cost-state"
                || !root.TryGetProperty("modelUsage", out var models) || models.ValueKind != JsonValueKind.Object)
                return null;

            return models.EnumerateObject()
                .Where(m => m.Value.ValueKind == JsonValueKind.Object && m.Name.Length > 0)
                .Select(m => new CostEntry(
                    m.Name,
                    Long(m.Value, "inputTokens"),
                    Long(m.Value, "outputTokens"),
                    Long(m.Value, "cacheReadInputTokens"),
                    Long(m.Value, "cacheCreationInputTokens")))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0
            ? n : 0;

    private readonly record struct MessageUsage(string Model, long Input, long Output, long CacheRead, long CacheWrite)
    {
        public MessageUsage Max(MessageUsage other) => this with
        {
            Input = Math.Max(Input, other.Input),
            Output = Math.Max(Output, other.Output),
            CacheRead = Math.Max(CacheRead, other.CacheRead),
            CacheWrite = Math.Max(CacheWrite, other.CacheWrite),
        };
    }

    private sealed record CostEntry(string Model, long Input, long Output, long CacheRead, long CacheWrite)
    {
        /// <summary>
        /// <c>cost-state</c> has no request count: count the transcript's messages for this model. Its key
        /// may be the alias the CLI was started with (<c>claude-opus-5[1m]</c>) rather than the API id the
        /// messages carry (<c>claude-opus-5-5</c>), so an alias matches the ids it prefixes.
        /// </summary>
        public ModelUsageReport WithRequests(IEnumerable<MessageUsage> messages)
        {
            var alias = Model.Split('[')[0];
            return new ModelUsageReport
            {
                Model = Model,
                InputTokens = Input,
                OutputTokens = Output,
                CacheReadTokens = CacheRead,
                CacheWriteTokens = CacheWrite,
                Requests = messages.Count(m => m.Model.StartsWith(alias, StringComparison.Ordinal)),
            };
        }
    }
}
