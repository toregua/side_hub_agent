using System.Globalization;
using System.Text.RegularExpressions;
using SideHub.Agent.Models;

namespace SideHub.Agent;

/// <summary>Which usage window of a CLI's plan ran out (<c>cli.limit.window</c>).</summary>
public static class CliLimitWindows
{
    public const string FiveHour = "five-hour";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Unknown = "unknown";
}

/// <summary>How the agent learned that a CLI hit its limit (<c>cli.limit.source</c>).</summary>
public static class CliLimitSources
{
    /// <summary>Claude's <c>StopFailure</c> hook.</summary>
    public const string Hook = "hook";

    /// <summary>The <c>rate_limits</c> codex writes in its rollout.</summary>
    public const string Rollout = "rollout";

    /// <summary>The last lines of the terminal, for a CLI without a structured signal (<see cref="CliLimitPatterns"/>).</summary>
    public const string Output = "output";
}

/// <summary>
/// A CLI stopped because its plan's usage window ran out. <paramref name="ResetsAt"/> is null when the CLI did not say
/// when it resets; <paramref name="Message"/> is what it said, unmasked and uncut (see <see cref="ToMessage"/>).
/// </summary>
public sealed record CliLimitReport(
    string Provider, string Window, DateTimeOffset? ResetsAt, string Source, string? Message, string? CliSessionId)
{
    /// <summary>Longest message sent to the backend, in characters.</summary>
    public const int MaxMessageLength = 300;

    /// <summary>The same limit (a repeated report): same CLI, same window, same reset.</summary>
    public bool SameAs(CliLimitReport? other) =>
        other is not null && other.Provider == Provider && other.Window == Window && other.ResetsAt == ResetsAt;

    /// <param name="mask">Masks the PTY's secrets in the message (applied before it is cut).</param>
    public CliLimitMessage ToMessage(string ptySessionId, Guid? runId, Func<string, string>? mask, DateTime at)
    {
        var message = Message?.Trim();
        if (!string.IsNullOrEmpty(message) && mask is not null)
            message = mask(message);
        if (message is { Length: > MaxMessageLength })
            message = message[..(MaxMessageLength - 1)] + "…";
        return new CliLimitMessage
        {
            Provider = Provider,
            PtySessionId = ptySessionId,
            RunId = runId,
            CliSessionId = CliSessionId,
            Window = Window,
            ResetsAt = ResetsAt?.UtcDateTime,
            Source = Source,
            Message = string.IsNullOrEmpty(message) ? null : message,
            At = at,
        };
    }
}

/// <summary>
/// Reads Claude's limit message: <c>You've hit your &lt;limit&gt; · resets &lt;time&gt; (&lt;zone&gt;)</c>. The shape comes from
/// Claude Code 2.1.301 itself (the code that writes it): the limit is <c>session limit</c> (5 h window), <c>weekly limit</c>,
/// <c>Opus limit</c>, <c>Sonnet limit</c>, <c>Fable limit</c> (weekly windows) or <c>usage credit limit</c>; the time is
/// <c>3pm</c> or <c>3:30pm</c> within a day, <c>Oct 20, 9am</c> beyond (with the year when it is not the current one),
/// followed by the IANA zone it was written in. Anything else gives an unknown window or reset.
/// </summary>
public static partial class ClaudeLimitText
{
    [GeneratedRegex(@"You['’]ve hit your (?<limit>session limit|weekly limit|Opus limit|Sonnet limit|Fable limit|usage credit limit|monthly spend limit)")]
    private static partial Regex LimitPattern();

    [GeneratedRegex(@"\bresets (?:(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) (?<day>\d{1,2})(?:, (?<year>\d{4}))?, )?(?<hour>\d{1,2})(?::(?<minute>\d{2}))?(?<ampm>am|pm)(?: \((?<zone>[A-Za-z0-9_+\-/]{1,64})\))?")]
    private static partial Regex ResetPattern();

    public static string Window(string? message) =>
        message is not null && LimitPattern().Match(message) is { Success: true } match
            ? match.Groups["limit"].Value switch
            {
                "session limit" => CliLimitWindows.FiveHour,
                "usage credit limit" or "monthly spend limit" => CliLimitWindows.Monthly,
                _ => CliLimitWindows.Weekly,
            }
            : CliLimitWindows.Unknown;

    /// <summary>When the limit resets, as the message says; null when it does not say or cannot be read.</summary>
    /// <param name="now">The time the message was read.</param>
    /// <param name="localZone">The machine's zone, for a message without one.</param>
    public static DateTimeOffset? ResetsAt(string? message, DateTimeOffset now, TimeZoneInfo localZone)
    {
        if (message is null || ResetPattern().Match(message) is not { Success: true } match)
            return null;

        var zone = localZone;
        if (match.Groups["zone"].Success)
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(match.Groups["zone"].Value, out var named))
                return null;
            zone = named;
        }

        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (hour is < 1 or > 12 || minute > 59)
            return null;
        hour = hour % 12 + (match.Groups["ampm"].Value == "pm" ? 12 : 0);

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        DateTime local;
        if (match.Groups["month"].Success)
        {
            var month = DateTime.ParseExact(match.Groups["month"].Value, "MMM", CultureInfo.InvariantCulture).Month;
            var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            var year = match.Groups["year"].Success ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) : localNow.Year;
            if (day > DateTime.DaysInMonth(year, month))
                return null;
            local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        }
        else
        {
            // Within a day: the next time the clock shows it
            local = new DateTime(localNow.Year, localNow.Month, localNow.Day, hour, minute, 0, DateTimeKind.Unspecified);
            if (local <= localNow.DateTime)
                local = local.AddDays(1);
        }

        // A time skipped by a clock change does not exist: the hour after it
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The report of Claude's <c>StopFailure</c> hook (<paramref name="message"/>: its last assistant message).</summary>
    public static CliLimitReport Report(string? message, string? cliSessionId, DateTimeOffset now, TimeZoneInfo localZone) =>
        new("claude", Window(message), ResetsAt(message, now, localZone), CliLimitSources.Hook, message, cliSessionId);
}

/// <summary>
/// What a CLI prints when its plan ran out, read on the last lines of a run's terminal once it exited in failure
/// (<see cref="TerminalText.LastLines"/>). Each phrase comes from the CLI's own text (its installed binary, see the
/// fixtures of the tests), never from memory, and is specific to it: a CLI working on rate limiting code prints "rate
/// limit" all day long.
/// <list type="bullet">
/// <item>claude and codex have a structured signal (<see cref="HasStructuredSignal"/>): their phrase only gives the
/// message of a limit the signal found, never the limit itself.</item>
/// <item>agy has none: its phrase is the signal.</item>
/// <item>copilot: no phrase known yet (nothing readable in its binary), no detection.</item>
/// </list>
/// </summary>
public static class CliLimitPatterns
{
    private static readonly Dictionary<string, string[]> Phrases = new(StringComparer.Ordinal)
    {
        // Claude Code 2.1.301: `You've hit your ${limit}${reset}`
        ["claude"] = ["You've hit your ", "You’ve hit your "],
        // codex-cli 0.162.1: "You’ve hit your usage limit" (U+2019), followed by an upgrade hint or " Try again at …."
        ["codex"] = ["You’ve hit your usage limit", "You've hit your usage limit"],
        // agy 1.3.4
        ["agy"] = ["You have exhausted your quota on this model."],
    };

    /// <summary>Whether the CLI says it hit its limit other than in its output: then its output never decides.</summary>
    public static bool HasStructuredSignal(string provider) => provider is "claude" or "codex";

    /// <summary>The last line of <paramref name="lastLines"/> that holds one of the CLI's phrases, trimmed; null for none.</summary>
    public static string? FindLine(string provider, string? lastLines)
    {
        if (string.IsNullOrEmpty(lastLines) || !Phrases.TryGetValue(provider, out var phrases))
            return null;
        return lastLines.Split('\n')
            .Select(line => line.Trim())
            .LastOrDefault(line => phrases.Any(p => line.Contains(p, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The limit a run's CLI without a structured signal printed before it exited in failure; null when it did not, or
    /// when the CLI has a structured signal (its output alone never decides).
    /// </summary>
    public static CliLimitReport? FromOutput(string provider, int exitCode, string? lastLines, string? cliSessionId)
    {
        if (exitCode == 0 || HasStructuredSignal(provider) || FindLine(provider, lastLines) is not { } line)
            return null;
        return new CliLimitReport(provider, CliLimitWindows.Unknown, null, CliLimitSources.Output, line, cliSessionId);
    }
}
