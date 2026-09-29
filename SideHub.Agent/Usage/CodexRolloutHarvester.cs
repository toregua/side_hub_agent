using System.Globalization;
using System.Text;
using System.Text.Json;
using SideHub.Agent.Models;

namespace SideHub.Agent.Usage;

/// <summary>
/// Reads a run's usage from its Codex rollouts (<c>&lt;sessions&gt;/YYYY/MM/DD/rollout-*.jsonl</c>).
/// <para>
/// Codex has no pre-set session id, so each launch announced by the <c>codex</c> wrapper is matched to the
/// rollout whose <c>session_meta</c> has the same cwd and starts within <see cref="StartupWindow"/> of it.
/// A match counts only if it is one-to-one: a launch near two rollouts, or a rollout near a launch of
/// another PTY (two runs started together in the same directory, or a codex started outside SideHub),
/// makes the whole run unavailable rather than wrongly attributed.
/// </para>
/// <para>
/// A rollout's <c>token_count</c> events carry cumulative totals; the last one is the session's usage. It is
/// split per model at each <c>turn_context</c> (the model can change mid-session). OpenAI counts cached
/// input inside <c>input_tokens</c> and reasoning inside <c>output_tokens</c>: input is reported without
/// the cached part (as Claude does), reasoning as a subset of output.
/// </para>
/// Not counted: resumed sessions (the rollout started before the launch) and sub-agent rollouts.
/// </summary>
public sealed class CodexRolloutHarvester(string sessionsRoot) : IUsageHarvester
{
    public const string SourceName = "codex-rollout";

    /// <summary>How long after its launch codex may start its session.</summary>
    public static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(60);

    // The launch is timed when the agent reads the wrapper's notification, which can trail codex's own clock.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(5);

    // session_meta.source of top-level sessions (not the app-server's "vscode", not sub-agents).
    private static readonly HashSet<string> CliSources = ["cli", "exec"];

    private const string UnknownModel = "unknown";

    public string Source => SourceName;

    /// <summary><c>$CODEX_HOME/sessions</c>, else <c>~/.codex/sessions</c>; null when no HOME is known.</summary>
    public static string? SessionsRoot()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrEmpty(codexHome))
            return Path.Combine(codexHome, "sessions");

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".codex", "sessions");
    }

    /// <summary>
    /// Usage of the rollouts started by the run's launches; null when the run launched no codex, no
    /// rollout was found, or a rollout cannot be attributed with certainty.
    /// </summary>
    public IReadOnlyList<ModelUsageReport>? Harvest(RunUsageContext run)
    {
        if (run.Launches.Count == 0)
            return null;

        var candidates = FindSessions(
            run.Launches.Min(l => l.At) - ClockSkew,
            run.Launches.Max(l => l.At) + StartupWindow);
        var allLaunches = run.Launches.Concat(run.OtherLaunches).ToList();

        var owned = new HashSet<SessionMeta>();
        foreach (var launch in run.Launches)
        {
            var near = candidates.Where(s => IsNear(launch, s)).ToList();
            if (near.Count == 0)
                continue; // codex failed before starting a session
            if (near.Count > 1)
                return null;
            if (allLaunches.Count(l => IsNear(l, near[0])) > 1)
                return null;
            owned.Add(near[0]);
        }

        if (owned.Count == 0)
            return null;

        var perModel = new Dictionary<string, TokenTotals>(StringComparer.Ordinal);
        foreach (var session in owned)
            ReadRollout(session.Path, perModel);

        return perModel
            .Select(kv => new ModelUsageReport
            {
                Model = kv.Key,
                InputTokens = Math.Max(0, kv.Value.Input - kv.Value.Cached),
                OutputTokens = kv.Value.Output,
                CacheReadTokens = kv.Value.Cached,
                CacheWriteTokens = 0,
                ReasoningTokens = kv.Value.Reasoning,
                Requests = kv.Value.Requests,
            })
            .OrderBy(r => r.Model, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsNear(CliLaunch launch, SessionMeta session) =>
        SameDirectory(launch.Cwd, session.Cwd)
        && session.StartedAt >= launch.At - ClockSkew
        && session.StartedAt <= launch.At + StartupWindow;

    private static bool SameDirectory(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>Top-level sessions that started between <paramref name="from"/> and <paramref name="to"/>.</summary>
    private List<SessionMeta> FindSessions(DateTimeOffset from, DateTimeOffset to)
    {
        var sessions = new List<SessionMeta>();
        // Day directories follow codex's local time: look one day around the UTC range.
        for (var day = from.UtcDateTime.Date.AddDays(-1); day <= to.UtcDateTime.Date.AddDays(1); day = day.AddDays(1))
        {
            var dir = Path.Combine(sessionsRoot, day.ToString("yyyy", CultureInfo.InvariantCulture),
                day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "rollout-*.jsonl"))
            {
                // Written after the session started: an older file cannot be a candidate.
                if (File.GetLastWriteTimeUtc(file) < from.UtcDateTime)
                    continue;
                if (ReadSessionMeta(file) is { } meta && meta.StartedAt >= from && meta.StartedAt <= to)
                    sessions.Add(meta);
            }
        }
        return sessions;
    }

    /// <summary>The rollout's first line: <c>{"type":"session_meta","payload":{"id","timestamp","cwd","source",…}}</c>.</summary>
    private static SessionMeta? ReadSessionMeta(string path)
    {
        try
        {
            string? line;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
                line = reader.ReadLine();
            if (string.IsNullOrEmpty(line))
                return null;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Str(root, "type") != "session_meta"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return null;

            var cwd = Str(payload, "cwd");
            var source = Str(payload, "source");
            if (string.IsNullOrEmpty(cwd) || source is null || !CliSources.Contains(source)
                || !DateTimeOffset.TryParse(Str(payload, "timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var startedAt))
                return null;

            return new SessionMeta(path, cwd, startedAt);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Adds each <c>token_count</c>'s increase of <c>info.total_token_usage</c> to the model of the latest
    /// <c>turn_context</c>. Each increase is one model response.
    /// </summary>
    private static void ReadRollout(string path, Dictionary<string, TokenTotals> perModel)
    {
        var model = UnknownModel;
        var previous = default(TokenTotals);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("\"turn_context\"", StringComparison.Ordinal))
                {
                    if (TryParseTurnModel(line) is { } turnModel)
                        model = turnModel;
                }
                else if (line.Contains("\"token_count\"", StringComparison.Ordinal)
                    && TryParseTotal(line) is { } total
                    && total.Total > previous.Total)
                {
                    var delta = total.Minus(previous);
                    perModel[model] = perModel.TryGetValue(model, out var sum) ? sum.Plus(delta) : delta;
                    previous = total;
                }
            }
        }
        catch (IOException) { /* deleted or unreadable: count what we have */ }
        catch (UnauthorizedAccessException) { }
    }

    private static string? TryParseTurnModel(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Str(root, "type") == "turn_context"
                && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                && Str(payload, "model") is { Length: > 0 } model
                    ? model
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{…}}}}</c>; info is null before the first response.</summary>
    private static TokenTotals? TryParseTotal(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Str(root, "type") != "event_msg"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || Str(payload, "type") != "token_count"
                || !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("total_token_usage", out var u) || u.ValueKind != JsonValueKind.Object)
                return null;

            return new TokenTotals(
                Long(u, "input_tokens"),
                Long(u, "cached_input_tokens"),
                Long(u, "output_tokens"),
                Long(u, "reasoning_output_tokens"),
                Requests: 1);
        }
        catch (JsonException)
        {
            return null; // corrupted or half-written line
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0
            ? n : 0;

    private sealed record SessionMeta(string Path, string Cwd, DateTimeOffset StartedAt);

    private readonly record struct TokenTotals(long Input, long Cached, long Output, long Reasoning, int Requests)
    {
        public long Total => Input + Output;

        public TokenTotals Minus(TokenTotals other) => new(
            Math.Max(0, Input - other.Input),
            Math.Max(0, Cached - other.Cached),
            Math.Max(0, Output - other.Output),
            Math.Max(0, Reasoning - other.Reasoning),
            Requests);

        public TokenTotals Plus(TokenTotals other) => new(
            Input + other.Input,
            Cached + other.Cached,
            Output + other.Output,
            Reasoning + other.Reasoning,
            Requests + other.Requests);
    }
}
