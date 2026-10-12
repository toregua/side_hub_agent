using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SideHub.Agent.Models;

namespace SideHub.Agent.Usage;

/// <summary>
/// Reads the usage of agy (Antigravity CLI) conversations from their transcript,
/// <c>&lt;root&gt;/brain/&lt;conversationId&gt;/.system_generated/logs/transcript_full.jsonl</c>: one line per step.
/// The conversation id is the one agy's hooks report (see <c>CliStateCommand</c>), so the file is found by id alone.
/// <list type="bullet">
/// <item>Each model call is a <c>PLANNER_RESPONSE</c> step with <c>input_tokens</c>, <c>cache_read_tokens</c> and
/// <c>output_tokens</c>. Thinking is counted in <c>output_tokens</c> (agy's own total is input + output). As in Gemini's
/// API, cached input is part of <c>input_tokens</c>: input is reported without it, as for Claude and Codex.</item>
/// <item>The transcript names no model id. The model is the one of the last "Model Selection" change agy writes in a
/// <c>USER_INPUT</c> step (the first message of a conversation, then each change), by its display name
/// ("Gemini 3.8 Flash (High)"), turned back into agy's id (<c>gemini-3.8-flash-high</c>, see <see cref="ModelId"/>).</item>
/// <item>A run counts the steps written since it started: a conversation resumed with <c>--conversation</c> keeps its
/// earlier steps.</item>
/// </list>
/// </summary>
public sealed partial class AntigravityTranscriptHarvester(string root) : IUsageHarvester, ICliSessionUsageHarvester, IFinalMessageReader
{
    public const string SourceName = "agy-transcript";

    private const string UnknownModel = "unknown";

    // File times and the agent's clock may disagree slightly.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(5);

    public string Source => SourceName;

    /// <summary><c>~/.gemini/antigravity-cli</c>; null when no HOME is known.</summary>
    public static string? Root()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".gemini", "antigravity-cli");
    }

    public IReadOnlyList<ModelUsageReport>? Harvest(RunUsageContext run)
    {
        var since = run.StartedAt - ClockSkew;
        var perModel = new Dictionary<string, Totals>(StringComparer.Ordinal);
        var found = false;
        foreach (var file in RunTranscripts(run))
        {
            found = true;
            Read(file, since, perModel, out _);
        }
        return found ? Reports(perModel) : null;
    }

    public string? ReadFinalMessage(RunUsageContext run) =>
        RunTranscripts(run)
            .OrderBy(File.GetLastWriteTimeUtc)
            .Select(LastAnswer)
            .LastOrDefault(text => text is not null);

    public IReadOnlyList<ModelUsageReport>? HarvestSession(string cwd, string cliSessionId)
    {
        if (Transcript(cliSessionId) is not { } file)
            return null;
        var perModel = new Dictionary<string, Totals>(StringComparer.Ordinal);
        Read(file, null, perModel, out _);
        return Reports(perModel);
    }

    public IReadOnlyList<string> SessionFiles(string cwd, string cliSessionId) =>
        Transcript(cliSessionId) is { } file ? [file] : [];

    public string? LastMessage(string cwd, string cliSessionId) =>
        Transcript(cliSessionId) is { } file ? LastAnswer(file) : null;

    /// <summary>Longest title read: agy writes a short one, anything longer is not its title.</summary>
    private const int MaxTitleLength = 200;

    /// <summary>
    /// The title agy gave the conversation (<c>&lt;root&gt;/annotations/&lt;id&gt;.pbtxt</c>, protobuf text format:
    /// <c>title:"…"</c>, non-ASCII bytes written as octal escapes), or null when it has none yet.
    /// </summary>
    public string? Title(string cliSessionId)
    {
        if (!FifoNotification.IsValidCliSessionId(cliSessionId))
            return null;
        var path = Path.Combine(root, "annotations", cliSessionId + ".pbtxt");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024)
                return null;
            return TitleFromAnnotation(File.ReadAllText(path));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The <c>title</c> string of an annotation in protobuf text format, unescaped; null when absent.</summary>
    public static string? TitleFromAnnotation(string text)
    {
        if (AnnotationTitle().Match(text) is not { Success: true } match)
            return null;
        var escaped = match.Groups["value"].Value;
        var bytes = new List<byte>(escaped.Length);
        for (var i = 0; i < escaped.Length; i++)
        {
            var c = escaped[i];
            if (c != '\\' || i + 1 >= escaped.Length)
            {
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }
            var next = escaped[++i];
            if (next is >= '0' and <= '7')
            {
                var end = i;
                while (end < escaped.Length && end < i + 3 && escaped[end] is >= '0' and <= '7')
                    end++;
                bytes.Add((byte)Convert.ToInt32(escaped[i..end], 8));
                i = end - 1;
                continue;
            }
            bytes.Add(next switch { 'n' => (byte)'\n', 't' => (byte)'\t', 'r' => (byte)'\r', _ => (byte)next });
        }
        var title = System.Text.Encoding.UTF8.GetString(bytes.ToArray()).Trim();
        return title.Length is > 0 and <= MaxTitleLength && !title.Any(char.IsControl) ? title : null;
    }

    private IEnumerable<string> RunTranscripts(RunUsageContext run) =>
        run.CliSessionIds.Distinct()
            .Select(Transcript)
            .Where(file => file is not null && (run.StartedAt is not { } started
                || File.GetLastWriteTimeUtc(file) >= started.UtcDateTime - ClockSkew))
            .Select(file => file!);

    /// <summary>The conversation's transcript, or null. The id comes from the notification FIFO and names a folder:
    /// only UUIDs.</summary>
    private string? Transcript(string cliSessionId)
    {
        if (!FifoNotification.IsValidCliSessionId(cliSessionId))
            return null;
        var logs = Path.Combine(root, "brain", cliSessionId, ".system_generated", "logs");
        foreach (var name in new[] { "transcript_full.jsonl", "transcript.jsonl" })
        {
            var path = Path.Combine(logs, name);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>Adds the transcript's model calls written since <paramref name="since"/> (all when null) to
    /// <paramref name="perModel"/>; <paramref name="lastAnswer"/> is the text of its last model step that has any.</summary>
    private static void Read(string path, DateTimeOffset? since, Dictionary<string, Totals> perModel, out string? lastAnswer)
    {
        lastAnswer = null;
        var model = UnknownModel;
        try
        {
            using var reader = TranscriptLines.Open(path);
            foreach (var line in TranscriptLines.Read(reader))
            {
                if (!line.Contains("\"USER_INPUT\"", StringComparison.Ordinal) && !line.Contains("\"PLANNER_RESPONSE\"", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var step = doc.RootElement;
                    if (step.ValueKind != JsonValueKind.Object)
                        continue;
                    switch (Str(step, "type"))
                    {
                        case "USER_INPUT":
                            if (Str(step, "content") is { } content && ModelChange().Matches(content) is { Count: > 0 } changes)
                                model = ModelId(changes[^1].Groups["to"].Value) ?? UnknownModel;
                            break;
                        case "PLANNER_RESPONSE":
                            if (Str(step, "content") is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text))
                                lastAnswer = text;
                            if (!step.TryGetProperty("input_tokens", out _) && !step.TryGetProperty("output_tokens", out _))
                                break;
                            if (since is { } from && CreatedAt(step) is { } at && at < from)
                                break;
                            var input = Long(step, "input_tokens");
                            var cached = Long(step, "cache_read_tokens");
                            var calls = perModel.GetValueOrDefault(model);
                            perModel[model] = new Totals(
                                calls.Input + Math.Max(0, input - cached), calls.Cached + cached,
                                calls.Output + Long(step, "output_tokens"), calls.Requests + 1);
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
    /// agy's model id from its display name: "Gemini 3.8 Flash (High)" → <c>gemini-3.8-flash-high</c>, "Claude Opus 5.5
    /// (High)" → <c>claude-opus-5-5-high</c> (Claude ids have no dot), "GPT-OSS 120B (Medium)" → <c>gpt-oss-120b-medium</c>.
    /// Null for a name that would not make an id.
    /// </summary>
    public static string? ModelId(string displayName)
    {
        var words = displayName.ToLowerInvariant().Replace("(", " ").Replace(")", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var id = string.Join('-', words);
        if (id.StartsWith("claude-", StringComparison.Ordinal))
            id = id.Replace('.', '-');
        return id.Length is > 0 and <= 100 && ModelIdPattern().IsMatch(id) ? id : null;
    }

    private static List<ModelUsageReport> Reports(Dictionary<string, Totals> perModel) =>
        perModel
            .Select(kv => new ModelUsageReport
            {
                Model = kv.Key,
                InputTokens = kv.Value.Input,
                OutputTokens = kv.Value.Output,
                CacheReadTokens = kv.Value.Cached,
                CacheWriteTokens = 0,
                Requests = kv.Value.Requests,
            })
            .OrderBy(r => r.Model, StringComparer.Ordinal)
            .ToList();

    private static DateTimeOffset? CreatedAt(JsonElement step) =>
        Str(step, "created_at") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? Math.Max(0, n) : 0;

    private readonly record struct Totals(long Input, long Cached, long Output, int Requests);

    [GeneratedRegex(@"changed setting `Model Selection` from (?<from>.+?) to (?<to>[^\r\n]+?)\. ")]
    private static partial Regex ModelChange();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]*$")]
    private static partial Regex ModelIdPattern();

    [GeneratedRegex(@"(?m)^title:\s*""(?<value>(?:[^""\\]|\\.)*)""")]
    private static partial Regex AnnotationTitle();
}
