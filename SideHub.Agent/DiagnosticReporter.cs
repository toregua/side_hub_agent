using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// Tells SideHub why this agent cannot start or connect (<c>POST /api/agent/diagnostics</c>), so that a failed install
/// is visible on SideHub's side instead of only in a log on the user's machine. Anonymous: the request carries the first
/// <see cref="TokenPrefixLength"/> characters of the token, never the token itself (a rejected token is one of the
/// causes). Best-effort: each cause is sent once per process, a network failure is retried at the next occurrence at
/// most <see cref="MaxAttemptsPerReason"/> times, and nothing ever waits more than <see cref="RequestTimeout"/>.
/// What is sent is listed in the README ("Failure reports").
/// </summary>
public sealed partial class DiagnosticReporter
{
    /// <summary>What the backend stores as the agent's token prefix ("sh_agent_" + 7 characters).</summary>
    public const int TokenPrefixLength = 16;
    public const int MaxDetailLength = 300;
    public const int MaxAttemptsPerReason = 3;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly Uri _endpoint;
    private readonly string _tokenPrefix;
    private readonly Action<string> _log;
    private readonly HttpMessageHandler? _handler;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _delivered = [];
    private readonly HashSet<string> _inFlight = [];
    private readonly Dictionary<string, int> _attempts = [];

    private DiagnosticReporter(Uri endpoint, string tokenPrefix, Action<string> log, HttpMessageHandler? handler)
    {
        _endpoint = endpoint;
        _tokenPrefix = tokenPrefix;
        _log = log;
        _handler = handler;
    }

    /// <summary>
    /// A reporter sending to <paramref name="apiBase"/> (an <see cref="AgentSetup.ApiBase"/> value, ending in /api), or
    /// null when there is nothing to tie a report to (no token) or the URL may not carry it (<see cref="AgentSetup.ApiRejectionReason"/>).
    /// </summary>
    public static DiagnosticReporter? Create(string? apiBase, string? token, Action<string> log, HttpMessageHandler? handler = null)
    {
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length < TokenPrefixLength || string.IsNullOrWhiteSpace(apiBase))
            return null;
        if (AgentSetup.ApiRejectionReason(apiBase) is not null)
            return null;
        return new DiagnosticReporter(new Uri($"{apiBase.TrimEnd('/')}/agent/diagnostics"), token[..TokenPrefixLength], log, handler);
    }

    /// <summary>A reporter for an agent config: the API is derived from its WebSocket URL.</summary>
    public static DiagnosticReporter? ForConfig(AgentConfig config, Action<string> log) =>
        string.IsNullOrWhiteSpace(config.SidehubUrl) || !Uri.TryCreate(config.SidehubUrl, UriKind.Absolute, out _)
            ? null
            : Create(AgentSetup.ApiBase(WebSocketClient.DeriveApiUrl(config.SidehubUrl)), config.AgentToken, log);

    /// <summary>
    /// Reports a <see cref="RootPolicy"/> refusal before exiting, for the agents this command was about: the setup token
    /// (<c>--token</c> or <see cref="AgentSetup.TokenEnvVar"/>, never stdin) or the trusted configs of the folder.
    /// Waits at most <see cref="RequestTimeout"/>.
    /// </summary>
    public static async Task ReportRootRefusalAsync(string command, string[] args, string baseDirectory)
    {
        void Quiet(string _) { }
        var reporters = new List<DiagnosticReporter>();
        if (command == "setup")
        {
            var i = Array.IndexOf(args, "--token");
            var token = i >= 0 && i + 1 < args.Length && args[i + 1] != "-" ? args[i + 1] : Environment.GetEnvironmentVariable(AgentSetup.TokenEnvVar);
            var a = Array.IndexOf(args, "--api");
            var api = a >= 0 && a + 1 < args.Length ? args[a + 1] : Environment.GetEnvironmentVariable("SIDEHUB_API");
            if (Create(AgentSetup.ApiBase(api), token, Quiet) is { } reporter)
                reporters.Add(reporter);
        }
        else
        {
            try
            {
                foreach (var config in await AgentConfig.LoadAllAsync(baseDirectory))
                    if (ForConfig(config, Quiet) is { } reporter)
                        reporters.Add(reporter);
            }
            catch (Exception) { /* no trusted config here: nothing to tie the report to */ }
        }

        // argv[0] may be anything (unknown commands run as start): only a known name leaves the machine
        var known = command is "setup" or "restart" or "--foreground-daemon" ? command : "start";
        await Task.WhenAll(reporters.Select(r => r.ReportAsync(DiagnosticReasons.RootRefused,
            $"`sidehub-agent {known}` run as root without {RootPolicy.AllowFlag}")));
    }

    /// <summary>Fire and forget: never throws, never blocks the caller.</summary>
    public void Report(string reason, string? detail) => _ = ReportAsync(reason, detail);

    /// <summary>
    /// Sends the report unless this cause was already delivered, is being sent, or failed <see cref="MaxAttemptsPerReason"/>
    /// times. Returns whether SideHub answered (whatever the status: the endpoint answers the same for every report).
    /// </summary>
    public async Task<bool> ReportAsync(string reason, string? detail)
    {
        lock (_lock)
        {
            if (_delivered.Contains(reason) || _inFlight.Contains(reason)) return false;
            var attempts = _attempts.GetValueOrDefault(reason);
            if (attempts >= MaxAttemptsPerReason) return false;
            _attempts[reason] = attempts + 1;
            _inFlight.Add(reason);
        }

        var delivered = false;
        try
        {
            using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
            http.Timeout = RequestTimeout;
            // Buffered (Content-Length), not streamed chunked: some proxies refuse chunked request bodies
            using var content = new StringContent(JsonSerializer.Serialize(new Payload(
                _tokenPrefix, reason, Sanitize(detail), VersionInfo.AgentVersion, RuntimeInformation.RuntimeIdentifier)),
                Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(_endpoint, content);
            delivered = true;
            _log($"Reported \"{reason}\" to SideHub");
        }
        catch (Exception ex)
        {
            // The backend being unreachable is often the very cause reported: say so once, quietly.
            _log($"Couldn't report \"{reason}\" to SideHub: {ex.GetBaseException().Message}");
        }
        finally
        {
            lock (_lock)
            {
                _inFlight.Remove(reason);
                if (delivered) _delivered.Add(reason);
            }
        }
        return delivered;
    }

    /// <summary>
    /// The cause of a WebSocket handshake that failed (<paramref name="statusCode"/> is the HTTP answer, 0 without one):
    /// a refused token, or a backend out of reach with what stood in the way (DNS, socket, TLS, HTTP status).
    /// </summary>
    public static (string Reason, string Detail) ClassifyConnectFailure(Exception ex, HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return (DiagnosticReasons.HandshakeRejected,
                $"HTTP {(int)statusCode}: the token matches no agent (agent deleted, or token copied incompletely)");

        var chain = new List<Exception>();
        for (var e = ex; e is not null && chain.Count < 5; e = e.InnerException)
            chain.Add(e);
        var kind = chain.OfType<AuthenticationException>().Any() ? "TLS"
            : chain.OfType<SocketException>().FirstOrDefault() is { } socket ? $"socket {socket.SocketErrorCode}"
            : statusCode != 0 ? $"HTTP {(int)statusCode}"
            : ex.GetType().Name;
        var messages = chain.Select(e => e.Message).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct();
        return (DiagnosticReasons.BackendUnreachable, $"{kind}: {string.Join(" → ", messages)}");
    }

    /// <summary>
    /// What may leave the machine from an error message: tokens masked, the home folder shown as ~ and the user name in
    /// other paths as &lt;user&gt;, on one line, at most <see cref="MaxDetailLength"/> characters.
    /// </summary>
    public static string? Sanitize(string? detail, string? homeDirectory = null, string? userName = null)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;

        var text = TokenPattern().Replace(detail, "sh_***");
        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(homeDirectory) && homeDirectory.Length > 1)
            text = Regex.Replace(text, Regex.Escape(Path.TrimEndingDirectorySeparator(homeDirectory)) + PathSegmentEnd, "~", RegexOptions.IgnoreCase);
        userName ??= Environment.UserName;
        // As a path segment only (/tmp/alice/…, C:\Users\alice): a name like "root" or "dev" is also a plain word
        if (!string.IsNullOrEmpty(userName))
            text = Regex.Replace(text, $@"(?<=[/\\]){Regex.Escape(userName)}{PathSegmentEnd}", "<user>", RegexOptions.IgnoreCase);
        text = WhitespacePattern().Replace(text, " ").Trim();
        return text.Length <= MaxDetailLength ? text : text[..(MaxDetailLength - 1)] + "…";
    }

    /// <summary>A path segment ends at a separator, a space, a quote or punctuation, or the end of the text.</summary>
    private const string PathSegmentEnd = @"(?=[/\\\s'"":,;)]|$)";

    [GeneratedRegex(@"sh_[a-z]+_[A-Za-z0-9_\-]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    private sealed record Payload(
        [property: JsonPropertyName("tokenPrefix")] string TokenPrefix,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("agentVersion")] string AgentVersion,
        [property: JsonPropertyName("os")] string Os);
}

/// <summary>Causes reported by <see cref="DiagnosticReporter"/> (and, for install-*, by the install scripts).</summary>
public static class DiagnosticReasons
{
    /// <summary>WebSocket handshake refused with 401/403: token unknown, or agent deleted in SideHub.</summary>
    public const string HandshakeRejected = "handshake-rejected";
    /// <summary>Backend not reachable before the first connection: DNS, proxy, TLS, HTTP error on the handshake.</summary>
    public const string BackendUnreachable = "backend-unreachable";
    /// <summary>pty-helper (Node + node-pty) does not start: Node missing, native module incompatible.</summary>
    public const string PtyHelperFailed = "pty-helper-failed";
    /// <summary>Started as root without --allow-root (<see cref="RootPolicy"/>).</summary>
    public const string RootRefused = "root-refused";
    /// <summary>None of claude, codex, gemini, copilot found in the PATH.</summary>
    public const string CliMissing = "cli-missing";
}
