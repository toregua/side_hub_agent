using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SideHub.Agent.Models;
using SideHub.Agent.Usage;

namespace SideHub.Agent;

public class WebSocketClient : IAsyncDisposable
{
    private readonly AgentConfig _config;
    private readonly CommandExecutor _executor;
    private readonly string _workingDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _displayName;
    private ClientWebSocket? _ws;
    private Timer? _heartbeatTimer;
    private Timer? _ptyReaperTimer;
    private string? _currentPtyShell;
    private DateTime _currentPtyStartedAt;
    private NodePtyExecutor? _ptyExecutor;
    // Multi-PTY: keyed by ptySessionId
    private readonly ConcurrentDictionary<string, PtySession> _ptySessions = new();
    private readonly ConcurrentDictionary<string, DateTime> _ptyLastActivity = new();
    private readonly ConcurrentDictionary<string, int> _ptyImageCounters = new();
    // Background tasks reading the CLI-session notification FIFO for each PTY.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _ptyFifoReaders = new();
    // Real-path cwd of each PTY session — needed to locate Claude's project JSONL
    // for the ai-title watcher.
    private readonly ConcurrentDictionary<string, string> _ptyCwd = new();
    // Background file watchers waiting for Claude's "ai-title" line. Keyed by
    // "<ptySessionId>:<cliSessionId>" so multiple Claude runs in the same PTY
    // don't collide.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _claudeTitleWatchers = new();
    private const int MaxClaudeTitleWatchersPerPty = 16;
    // An ai-title line is a few hundred bytes; longer transcript lines (tool results, images) are skipped unbuffered.
    private const int MaxClaudeTitleLineBytes = 64 * 1024;
    // Last CLI session (and its title) seen in each PTY, re-reported after a backend reconnect
    // so every device can show the terminal as that conversation.
    private readonly ConcurrentDictionary<string, PtyCliSession> _ptyCliSessions = new();
    // What the CLI of each PTY is doing (working / waiting-input / idle), reported as pty.cli-state.
    private readonly CliStateTracker _cliStates = new();
    // Codex launches still looking for their rollout (→ cwd), and the rollouts already tied to a launch.
    private readonly ConcurrentDictionary<object, string> _pendingCodexLaunches = new();
    private readonly ConcurrentDictionary<string, byte> _announcedCodexRollouts = new();
    private readonly CodexRolloutHarvester? _codexRollouts;
    private sealed record PtyCliSession(string Provider, string CliSessionId, string? Title = null);
    private const int PtyIdleTimeoutMinutes = 30;

    /// <summary>Folder of the working directory where terminal image attachments are saved.</summary>
    private const string AttachmentsDirectory = ".sidehub-images";

    // PTY sessions driven by the backend (workflow steps, scheduled prompts, runs) run
    // unattended and often produce no input for long stretches. The backend owns their
    // timeout, so the idle reaper must leave them alone.
    private static readonly string[] BackendManagedPtyPrefixes = ["workflow-", "scheduler-", "run-"];
    // Tail of the in-flight work per PTY session (key "" = legacy single PTY). Only the
    // receive loop reads/writes the tail, so messages for one session stay ordered even
    // when pty.start runs off the loop.
    private readonly ConcurrentDictionary<string, Task> _ptyPendingWork = new();
    // ClientWebSocket forbids concurrent SendAsync calls; handlers, PTY output callbacks
    // and the heartbeat timer all send, so serialize them.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly PendingFileWrites _pendingFileWrites = new();
    // Token usage of backend-launched runs (run-* PTYs), reported as run.usage.
    private readonly RunUsageCollector _usageCollector;
    // Token usage of the CLI sessions started in terminals, reported as cli-session.usage.
    private readonly CliSessionUsageCollector _cliSessionUsage;
    private Timer? _cliSessionUsageTimer;
    // Agent-owned 0700 folder holding the notification FIFOs (see NotifyFifo).
    private readonly string _fifoDirectory;
    private readonly string _fifoAgentKey;

    private const int MinReconnectDelayMs = 1000;
    private const int MaxReconnectDelayMs = 30000;
    private const double BackoffMultiplier = 1.5;
    private const int HeartbeatIntervalMs = 15000;
    private const int MaxMissedHeartbeatAcks = 3;
    private const int StableConnectionThresholdMs = 60000; // 60s before resetting backoff
    private const int MaxWebSocketMessageSize = 50 * 1024 * 1024; // 50 MB

    private int _missedHeartbeatAcks;
    private DateTime _connectedAt;
    /// <summary>Connected at least once since the process started: later failures are outages, not a broken install.</summary>
    private bool _everConnected;
    private readonly DiagnosticReporter? _diagnostics;

    /// <param name="runDirectory">The agent's .sidehub/run directory; defaults to the working directory's.</param>
    public WebSocketClient(AgentConfig config, CommandExecutor executor, string workingDirectory, string? displayName = null, string? runDirectory = null)
    {
        _config = config;
        _executor = executor;
        _workingDirectory = workingDirectory;
        _displayName = displayName ?? config.GetDisplayName();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        EnsureCliWrappersExecutable();

        var harvesters = new Dictionary<string, IUsageHarvester>();
        var sessionHarvesters = new Dictionary<string, ICliSessionUsageHarvester>();
        if (ClaudeProjectPaths.ProjectsRoot() is { } claudeProjects)
        {
            var claude = new ClaudeTranscriptHarvester(claudeProjects);
            harvesters["claude"] = claude;
            sessionHarvesters["claude"] = claude;
        }
        if (CodexRolloutHarvester.SessionsRoot() is { } codexSessions)
        {
            harvesters["codex"] = _codexRollouts = new CodexRolloutHarvester(codexSessions);
            sessionHarvesters["codex"] = _codexRollouts;
        }
        // Several agents can share a run directory: keep each agent's pending reports apart,
        // the backend only accepts a run's usage from the agent it was launched on.
        var runDir = runDirectory ?? Path.Combine(workingDirectory, ".sidehub", "run");
        var pendingDirectory = Path.Combine(runDir, "pending-usage", config.AgentId ?? "default");
        _fifoAgentKey = config.AgentId ?? "default";
        _fifoDirectory = Path.Combine(runDir, "fifo", _fifoAgentKey);
        _usageCollector = new RunUsageCollector(harvesters, new PendingUsageStore(pendingDirectory), TrySendAsync, Log);
        _cliSessionUsage = new CliSessionUsageCollector(
            sessionHarvesters, new PendingCliSessionUsageStore(Path.Combine(pendingDirectory, "cli-sessions")), TrySendAsync, Log);
        _diagnostics = DiagnosticReporter.ForConfig(config, Log);
    }

    private void Log(string message) => Console.WriteLine($"[{_displayName}] {message}");

    /// <summary>Mask sensitive query-string values (token, key, secret) and drop any user:password in URLs for safe logging.</summary>
    public static string MaskUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var left = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
            if (string.IsNullOrEmpty(uri.Query) || uri.Query == "?") return left;
            var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var sensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "token", "key", "secret", "apikey", "api_key" };
            foreach (var key in qs.AllKeys)
            {
                if (key != null && sensitiveKeys.Contains(key))
                {
                    var val = qs[key] ?? "";
                    qs[key] = val.Length > 4 ? val[..4] + "***" : "***";
                }
            }
            return $"{left}?{qs}";
        }
        catch
        {
            return "***masked-url***";
        }
    }

    /// <summary>Derive HTTP API URL from WebSocket URL (wss://host/ws/agent → https://host).</summary>
    internal static string DeriveApiUrl(string wsUrl)
    {
        var uri = new Uri(wsUrl);
        var scheme = uri.Scheme == "wss" ? "https" : "http";
        return $"{scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}";
    }

    /// <summary>Build the environment dict injected into a PTY shell:
    /// SideHub CLI env vars + sidehub-agent dir prepended to PATH so `sidehub-cli`
    /// (and the SideHub skill files) are available inside the terminal.
    /// Also prepends the cli-wrappers dir so `claude` resolves to our wrapper that
    /// pre-mints a session UUID, and exposes SIDEHUB_PTY_NOTIFY_FIFO so the
    /// wrapper can post back the session id.</summary>
    private IReadOnlyDictionary<string, string> BuildTerminalEnvironment(string ptySessionId, IReadOnlyDictionary<string, string>? additionalEnv = null)
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        // The folder the agent actually runs from (sidehub-cli ships next to it), not a hard-coded install path.
        var agentLibDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var wrappersDir = Path.Combine(agentLibDir, "cli-wrappers");

        var trusted = new[] { wrappersDir, agentLibDir }.Where(IsTrustedForPath).ToList();
        var existing = currentPath.Split(Path.PathSeparator).Select(Path.TrimEndingDirectorySeparator).ToHashSet();
        var prepended = trusted.Where(dir => !existing.Contains(dir)).ToList();
        var fullPath = prepended.Count > 0
            ? string.Join(Path.PathSeparator, prepended) + Path.PathSeparator + currentPath
            : currentPath;

        var env = new Dictionary<string, string>
        {
            ["SIDEHUB_PTY_SESSION_ID"] = ptySessionId,
            ["SIDEHUB_PTY_NOTIFY_FIFO"] = GetFifoPath(ptySessionId),
            ["PATH"] = fullPath,
            ["SIDEHUB_API_URL"] = DeriveApiUrl(_config.SidehubUrl!),
            ["SIDEHUB_WORKSPACE_ID"] = _config.WorkspaceId!,
        };
        if (trusted.Contains(wrappersDir))
        {
            env["SIDEHUB_CLI_WRAPPERS"] = wrappersDir;
            // Point the pty-helper at our custom rcfile so it can pass `--rcfile`
            // to bash, ensuring our PATH wins after the user's .bashrc runs.
            var sidehubBashrc = Path.Combine(wrappersDir, "sidehub.bashrc");
            if (File.Exists(sidehubBashrc))
                env["SIDEHUB_BASHRC"] = sidehubBashrc;
        }
        if (!string.IsNullOrEmpty(_config.AgentId))
            env["SIDEHUB_AGENT_ID"] = _config.AgentId!;

        // Merge caller-supplied env (e.g. workflow execution context), restricted to SIDEHUB_* and an
        // allow-list. The agent's own token never enters a PTY: the shell only gets the token the
        // backend scoped to it (run token or terminal session token) in SIDEHUB_AGENT_TOKEN. Without
        // one (older backend), sidehub-cli is unavailable in this terminal.
        var allowedEnv = PtyEnvironmentPolicy.FilterAdditionalEnv(additionalEnv, out var rejectedKeys);
        if (rejectedKeys.Count > 0)
            Log($"SECURITY: PTY {ptySessionId} ignored additionalEnv keys: {string.Join(", ", rejectedKeys)}");
        if (!allowedEnv.ContainsKey(PtyEnvironmentPolicy.AgentTokenKey))
            Log($"PTY {ptySessionId} has no session token: sidehub-cli is unavailable in it");
        foreach (var (key, value) in allowedEnv)
            env[key] = value;

        // Windows: cmd.exe would run a claude.exe / codex.cmd committed in the repository before
        // the one on the PATH.
        if (OperatingSystem.IsWindows())
            env[ExecutableResolver.NoCurrentDirectoryLookupVariable] = "1";

        return env;
    }

    /// <summary>The leading program name of a command line, for logs that must not carry its arguments
    /// (prompts, paths, secrets). Returns "?" when the line doesn't start with a plain program name.</summary>
    public static string ProgramNameForLog(string commandLine)
    {
        var first = commandLine.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length is > 0 and <= 40 && first.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? first
            : "?";
    }

    /// <summary>Whether <paramref name="dir"/> may go in front of the terminal PATH (see <see cref="TrustedDirectory"/>):
    /// a folder missing or writable by another user would let that user shadow every command typed there.</summary>
    private bool IsTrustedForPath(string dir)
    {
        if (TrustedDirectory.UntrustedReason(dir) is not { } reason) return true;
        Log($"Not adding {dir} to the terminal PATH: {reason}");
        return false;
    }

    /// <summary>Where the terminal's processes reach the agent (<c>$SIDEHUB_PTY_NOTIFY_FIFO</c>): a FIFO, or on
    /// Windows, which has none, a named pipe.</summary>
    private string GetFifoPath(string ptySessionId) => OperatingSystem.IsWindows()
        ? NotifyFifo.PipePrefix + NotifyFifo.PipeNameFor(_fifoAgentKey, ptySessionId)
        : NotifyFifo.PathFor(_fifoDirectory, ptySessionId);

    private static bool _cliWrappersChecked;
    private static readonly object _cliWrappersLock = new();

    /// <summary>MSBuild copies cli-wrappers/* to the output directory but drops
    /// the executable bit on Linux. Mark them executable once at startup so
    /// `exec` from inside the PTY shell works.</summary>
    private static void EnsureCliWrappersExecutable()
    {
        lock (_cliWrappersLock)
        {
            if (_cliWrappersChecked) return;
            _cliWrappersChecked = true;

            var dir = Path.Combine(AppContext.BaseDirectory.TrimEnd('/'), "cli-wrappers");
            if (!Directory.Exists(dir)) return;

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(file, File.GetUnixFileMode(file)
                            | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>Keeps an agent-owned directory inside the repository (pasted images…) out of git:
    /// a <c>.gitignore</c> holding <c>*</c> ignores the directory and itself, without touching any
    /// of the user's files.</summary>
    /// <summary>Writes <c>.gitignore</c> ("*") into a folder of the working directory the agent owns,
    /// unless one is already there. Never through a link (see <see cref="FileWritePolicy.OpenWrite"/>).</summary>
    private void EnsureIgnoredByGit(string relativeDirectory)
    {
        var gitignore = Path.Combine(relativeDirectory, ".gitignore");
        try
        {
            using var file = FileWritePolicy.OpenWrite(_workingDirectory, gitignore, FileMode.OpenOrCreate, out _);
            if (file.Length == 0)
                file.Write("# Generated by the Side Hub agent\n*\n"u8);
        }
        catch (Exception ex)
        {
            Log($"Could not write {gitignore}: {ex.Message}");
        }
    }

    /// <summary>Create the notification FIFO before the PTY starts. Best-effort:
    /// if mkfifo isn't available, we log and skip — the CLI wrapper will simply
    /// no-op its notification and the existing session behavior is preserved.</summary>
    private void EnsureNotifyFifo(string ptySessionId)
    {
        if (OperatingSystem.IsWindows())
            return; // The named pipe is created by its reader (StartFifoReader).
        if (!NotifyFifo.TryCreate(_fifoDirectory, ptySessionId, out var fifoPath, out var error))
            Log($"mkfifo failed for {fifoPath} ({error}); CLI session notifications disabled for this PTY");
    }

    /// <summary>Tear down the FIFO and stop its reader task, plus any Claude
    /// title watchers that were bound to this PTY.</summary>
    private void CleanupNotifyFifo(string ptySessionId)
    {
        if (_ptyFifoReaders.TryRemove(ptySessionId, out var cts))
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }
        _ptyCwd.TryRemove(ptySessionId, out _);
        _ptyCliSessions.TryRemove(ptySessionId, out _);
        _cliStates.Clear(ptySessionId);
        StopClaudeTitleWatchers(ptySessionId);
        if (!OperatingSystem.IsWindows())
            NotifyFifo.Delete(_fifoDirectory, ptySessionId);
    }

    /// <summary>Cancel any pending Claude ai-title watchers bound to this PTY.
    /// Keys in _claudeTitleWatchers are "<ptySessionId>:<cliSessionId>".</summary>
    private void StopClaudeTitleWatchers(string ptySessionId)
    {
        var prefix = ptySessionId + ":";
        foreach (var key in _claudeTitleWatchers.Keys.ToList())
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (_claudeTitleWatchers.TryRemove(key, out var w))
            {
                try { w.Cancel(); } catch { /* ignore */ }
                w.Dispose();
            }
        }
    }

    /// <summary>Spawn a background reader that pulls JSON lines from the
    /// notification FIFO (named pipe on Windows) and forwards CLI-session events to the backend.</summary>
    private void StartFifoReader(string ptySessionId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() && !File.Exists(GetFifoPath(ptySessionId)))
            return; // mkfifo failed; nothing to read

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ptyFifoReaders[ptySessionId] = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    await ReadNotifyPipeAsync(ptySessionId, token);
                else
                    await ReadNotifyFifoAsync(ptySessionId, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Log($"FIFO reader for {ptySessionId} crashed: {ex.Message}");
            }
        }, token);
    }

    /// <summary>We open the FIFO in read+write mode (O_RDWR) so the reader doesn't block when no
    /// writer is connected and doesn't see EOF when a writer disconnects between CLI invocations.</summary>
    private async Task ReadNotifyFifoAsync(string ptySessionId, CancellationToken token)
    {
        using var stream = new FileStream(GetFifoPath(ptySessionId), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var streamReader = new StreamReader(stream, Encoding.UTF8);
        while (!token.IsCancellationRequested)
        {
            await ReadNotificationsAsync(ptySessionId, streamReader, token);
            // FIFO closed; small backoff before retry to avoid spinning.
            await Task.Delay(200, token);
        }
    }

    /// <summary>One writer at a time (each line is a short-lived connection, writers wait up to a second).
    /// CurrentUserOnly: the pipe refuses clients of another user, and the agent never reads a pipe another
    /// user created first under the same name.</summary>
    private async Task ReadNotifyPipeAsync(string ptySessionId, CancellationToken token)
    {
        var name = NotifyFifo.PipeNameFor(_fifoAgentKey, ptySessionId);
        while (!token.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(token);
            using var streamReader = new StreamReader(pipe, Encoding.UTF8);
            await ReadNotificationsAsync(ptySessionId, streamReader, token);
        }
    }

    /// <summary>Handles each line until the writer side closes.</summary>
    private async Task ReadNotificationsAsync(string ptySessionId, StreamReader streamReader, CancellationToken token)
    {
        // Anything in the terminal can write here: never buffer an unbounded line.
        var reader = new BoundedLineReader(streamReader, FifoNotification.MaxLineLength);
        while (await reader.ReadLineAsync(token) is { } read)
        {
            if (read.TooLong)
            {
                Log($"SECURITY: FIFO line over {FifoNotification.MaxLineLength} chars ignored on PTY {ptySessionId}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(read.Text)) continue;

            await HandleFifoLineAsync(ptySessionId, read.Text, token);
        }
    }

    private async Task HandleFifoLineAsync(string ptySessionId, string line, CancellationToken ct)
    {
        var notification = FifoNotification.Parse(line, out var rejection);
        if (notification is null)
        {
            // Never log the line itself: it is whatever a process of the terminal wrote.
            Log($"SECURITY: FIFO line rejected on PTY {ptySessionId} ({rejection}, {line.Length} chars)");
            return;
        }

        try
        {
            switch (notification)
            {
                // Written by `sidehub-cli workflow step-complete|step-fail`: the step is over but the
                // CLI may stay open, so report the run's usage now (exit will report it again).
                case FifoNotification.RunStepEnded:
                    if (_usageCollector.IsTracked(ptySessionId))
                        RunInBackground("run.usage", () => _usageCollector.HarvestAsync(ptySessionId, "step-ended", final: false, CancellationToken.None));
                    return;

                // Written by `sidehub-cli launch codex`: codex has no session id to announce, so the harvester
                // needs the rollout its process holds open, or matches it by cwd and launch time. The same
                // match gives the session id announced to the backend (RecordCliLaunch).
                case FifoNotification.CliLaunched launched:
                    RecordCliLaunch(ptySessionId, launched);
                    return;

                case FifoNotification.CliSessionStarted started:
                    await RecordCliSessionAsync(ptySessionId, started, ct);
                    return;

                // Written by `sidehub-cli launch` once the CLI has exited: its session's usage is final.
                case FifoNotification.CliExited exited:
                    Log($"CLI exited in PTY {ptySessionId}: provider={exited.Provider} cliSessionId={exited.CliSessionId ?? "(unknown)"}");
                    // The backend clears the state itself when the CLI ends: only forget it here.
                    _cliStates.Clear(ptySessionId);
                    RunInBackground("cli-session.usage", () => _cliSessionUsage.CliExitedAsync(
                        ptySessionId, exited.Provider, exited.CliSessionId, CancellationToken.None));
                    return;

                // Written by the CLI's hooks through `sidehub-cli cli-state` (see CliStateHooks).
                case FifoNotification.CliStateChanged changed:
                    await ReportCliStateAsync(ptySessionId, changed, ct);
                    return;
            }
        }
        catch (Exception ex)
        {
            Log($"FIFO line handling failed on PTY {ptySessionId}: {ex.Message}");
        }
    }

    private async Task ReportCliStateAsync(string ptySessionId, FifoNotification.CliStateChanged changed, CancellationToken ct)
    {
        // The PTY may have closed while the line was in flight: a state is never reported for an unknown PTY.
        if (!_ptySessions.ContainsKey(ptySessionId)) return;
        if (_cliStates.Record(ptySessionId, changed.Provider, changed.State, changed.CliSessionId, DateTime.UtcNow) is not { } message)
            return;
        Log($"CLI state in PTY {ptySessionId}: {changed.State} (provider={changed.Provider} cliSessionId={changed.CliSessionId ?? "(unknown)"})");
        // Even when the send fails (backend away), the state stays cached and is replayed at the next connection.
        await TrySendAsync(message, ct);
    }

    private void RecordCliLaunch(string ptySessionId, FifoNotification.CliLaunched launched)
    {
        Log($"CLI launched in PTY {ptySessionId}: provider={launched.Provider} cwd={launched.Cwd} pid={launched.Pid}");

        LaunchObservation? observation = null;
        if (launched.Pid is { } launchedPid
            && launched.Provider == "codex"
            && ProcessOpenFiles.IsSupported())
        {
            // Only watch a process of this very PTY: a forged pid could otherwise attribute another
            // PTY's rollout to this run. Without proof, the harvester falls back to cwd + launch time.
            if (ProcessEnvironment.Has(launchedPid, "SIDEHUB_PTY_SESSION_ID", ptySessionId))
            {
                observation = new LaunchObservation();
                // Until the process exits, whatever happens to the PTY.
                RunInBackground("codex rollout watch", () => observation.WatchAsync(
                    () => ProcessOpenFiles.Find(launchedPid, CodexRolloutHarvester.IsRolloutPath),
                    CancellationToken.None));
            }
            else
            {
                Log($"SECURITY: pid {launchedPid} announced on PTY {ptySessionId} is not a process of that PTY; not watched");
            }
        }
        var at = DateTimeOffset.UtcNow;
        _usageCollector.RecordCliLaunch(ptySessionId, launched.Provider, launched.Cwd, at, observation);

        if (launched.Provider == "codex" && _codexRollouts is not null
            && _ptyFifoReaders.TryGetValue(ptySessionId, out var reader))
            RunInBackground("codex session id", () => AnnounceCodexSessionAsync(ptySessionId, launched.Cwd, at, observation, reader.Token));
    }

    /// <summary>How long a codex launch is matched to its rollout: codex only writes one once the conversation starts.</summary>
    private static readonly TimeSpan CodexSessionSearch = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Codex takes no pre-set session id: tie the launch to its rollout so the PTY can resume it later
    /// (<c>codex resume &lt;id&gt;</c>). The rollout its process holds open is proof; failing that (Windows, or a pid
    /// that cannot be watched), the one new rollout of that directory, when no other codex launch there is still
    /// waiting for its own.
    /// </summary>
    private async Task AnnounceCodexSessionAsync(
        string ptySessionId, string cwd, DateTimeOffset at, LaunchObservation? observation, CancellationToken ct)
    {
        var launch = new object();
        _pendingCodexLaunches[launch] = cwd;
        try
        {
            while (DateTimeOffset.UtcNow < at + CodexSessionSearch)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                var rollout = observation?.Files.FirstOrDefault(f => CodexRolloutHarvester.SessionIdOf(f) is not null);
                if (rollout is null && observation?.IsComplete == true)
                    return; // codex exited without starting a session
                if (rollout is null && _pendingCodexLaunches.Values.Count(c => c == cwd) == 1)
                {
                    var fresh = _codexRollouts!.SessionsStartedIn(cwd, at)
                        .Where(p => !_announcedCodexRollouts.ContainsKey(Path.GetFileName(p)))
                        .ToList();
                    if (fresh.Count == 1)
                        rollout = fresh[0];
                }
                if (rollout is null || CodexRolloutHarvester.SessionIdOf(rollout) is not { } cliSessionId)
                    continue;

                _announcedCodexRollouts[Path.GetFileName(rollout)] = 0;
                await RecordCliSessionAsync(ptySessionId, new FifoNotification.CliSessionStarted("codex", cliSessionId), ct);
                return;
            }
        }
        finally
        {
            _pendingCodexLaunches.TryRemove(launch, out _);
        }
    }

    private async Task RecordCliSessionAsync(string ptySessionId, FifoNotification.CliSessionStarted started, CancellationToken ct)
    {
        var (provider, cliSessionId) = (started.Provider, started.CliSessionId);
        Log($"CLI session started in PTY {ptySessionId}: provider={provider} cliSessionId={cliSessionId}");
        _usageCollector.RecordCliSession(ptySessionId, provider, cliSessionId);
        _cliSessionUsage.SessionStarted(ptySessionId, provider, cliSessionId,
            _ptyCwd.TryGetValue(ptySessionId, out var cwd) ? cwd : _workingDirectory);
        _ptyCliSessions[ptySessionId] = new PtyCliSession(provider, cliSessionId);
        await SendAsync(new PtyCliSessionStartedMessage
        {
            PtySessionId = ptySessionId,
            Provider = provider,
            CliSessionId = cliSessionId,
        }, ct);

        // For Claude, watch the project JSONL for an ai-title line and
        // forward it as a tab-label suggestion.
        if (provider == "claude")
            StartClaudeTitleWatcher(ptySessionId, cliSessionId, ct);
    }

    /// <summary>
    /// Watch the Claude project JSONL for an "ai-title" line. Claude writes
    /// something like {"type":"ai-title","aiTitle":"...","sessionId":"..."}
    /// shortly after the first user message. The file lives at
    /// <c>$HOME/.claude/projects/&lt;cwd-with-slashes-as-dashes&gt;/&lt;cliSessionId&gt;.jsonl</c>.
    /// First match wins — we emit a single PtyCliSessionTitledMessage then
    /// dispose the watcher.
    /// </summary>
    private void StartClaudeTitleWatcher(string ptySessionId, string cliSessionId, CancellationToken ct)
    {
        // The id names the watched file: only a UUID (see FifoNotification) may reach Path.Combine.
        if (!FifoNotification.IsValidCliSessionId(cliSessionId))
            return;
        // Each watcher is a task and a FileSystemWatcher: a terminal flooding the FIFO with ids must not pile them up.
        var prefix = ptySessionId + ":";
        if (_claudeTitleWatchers.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal)) >= MaxClaudeTitleWatchersPerPty)
        {
            Log($"SECURITY: PTY {ptySessionId} already has {MaxClaudeTitleWatchersPerPty} ai-title watchers; none added for {cliSessionId}");
            return;
        }

        if (!_ptyCwd.TryGetValue(ptySessionId, out var cwd) || string.IsNullOrEmpty(cwd))
        {
            Log($"No cwd recorded for PTY {ptySessionId}; skipping ai-title watcher for {cliSessionId}");
            return;
        }

        var projectsRoot = ClaudeProjectPaths.ProjectsRoot();
        if (projectsRoot is null)
        {
            Log($"No HOME available; skipping ai-title watcher for {cliSessionId}");
            return;
        }

        var projectDir = ClaudeProjectPaths.ProjectDirectory(projectsRoot, cwd);
        var jsonlPath = Path.Combine(projectDir, cliSessionId + ".jsonl");
        var watcherKey = ptySessionId + ":" + cliSessionId;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_claudeTitleWatchers.TryAdd(watcherKey, cts))
        {
            cts.Dispose();
            return; // Already watching.
        }
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // Best effort: ensure the directory exists. If it doesn't show up
                // within the lifetime of the PTY, we just give up at PTY exit.
                while (!token.IsCancellationRequested && !Directory.Exists(projectDir))
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
                    catch (OperationCanceledException) { return; }
                }
                if (token.IsCancellationRequested) return;

                using var watcher = new FileSystemWatcher(projectDir, cliSessionId + ".jsonl")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };

                var emitted = false;
                // Read from where the previous scan stopped: the poll below must not re-read the whole transcript.
                var transcript = new JsonlTail(jsonlPath, MaxClaudeTitleLineBytes);
                async Task TryEmitAsync()
                {
                    if (emitted || token.IsCancellationRequested) return;
                    string? title;
                    lock (transcript) title = TryReadClaudeTitle(transcript);
                    if (string.IsNullOrEmpty(title)) return;
                    emitted = true;
                    // Not the title itself: Claude derives it from the prompt.
                    Log($"Claude ai-title for {cliSessionId} ({title.Length} chars)");
                    if (_ptyCliSessions.TryGetValue(ptySessionId, out var cliSession) && cliSession.CliSessionId == cliSessionId)
                        _ptyCliSessions.TryUpdate(ptySessionId, cliSession with { Title = title }, cliSession);
                    try
                    {
                        await SendAsync(new PtyCliSessionTitledMessage
                        {
                            PtySessionId = ptySessionId,
                            CliSessionId = cliSessionId,
                            Title = title!,
                        }, token);
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to send pty.cli-session-titled for {cliSessionId}: {ex.Message}");
                    }
                    // Dispose ourselves — title is single-shot.
                    if (_claudeTitleWatchers.TryRemove(watcherKey, out var ownCts))
                    {
                        try { ownCts.Cancel(); } catch { }
                        ownCts.Dispose();
                    }
                }

                FileSystemEventHandler handler = (_, _) => _ = TryEmitAsync();
                watcher.Changed += handler;
                watcher.Created += handler;

                // Initial scan in case the line was already written before the
                // watcher attached.
                await TryEmitAsync();

                // Poll fallback: FileSystemWatcher on Linux can miss events on
                // certain filesystems (tmpfs, network mounts). A slow poll every
                // 3s catches anything the kernel notifications miss without
                // burning CPU.
                while (!token.IsCancellationRequested && !emitted)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
                    catch (OperationCanceledException) { break; }
                    await TryEmitAsync();
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Log($"Claude title watcher for {cliSessionId} crashed: {ex.Message}");
            }
        }, token);
    }

    /// <summary>Scan the lines appended to a Claude project JSONL file since the last call for the first
    /// ai-title line and return the title string, or null if absent / unreadable.</summary>
    private static string? TryReadClaudeTitle(JsonlTail transcript)
    {
        try
        {
            foreach (var line in transcript.ReadNewLines())
            {
                if (line.Length < 20) continue;
                if (line.IndexOf("\"ai-title\"", StringComparison.Ordinal) < 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("type", out var typeP)) continue;
                    if (typeP.GetString() != "ai-title") continue;
                    if (!root.TryGetProperty("aiTitle", out var titleP)) continue;
                    var title = titleP.GetString();
                    if (!string.IsNullOrWhiteSpace(title))
                        return title.Trim();
                }
                catch (JsonException) { /* skip malformed line */ }
            }
        }
        catch (IOException) { /* file may be mid-write, retry on next event */ }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var reconnectAttempts = 0;
        // Connected or not: a snapshot taken while disconnected is queued and replayed at the next connection.
        _cliSessionUsageTimer ??= new Timer(
            _ => RunInBackground("periodic cli-session.usage", () => _cliSessionUsage.ReportChangedAsync(ct)),
            null, CliSessionUsageCollector.ReportInterval, CliSessionUsageCollector.ReportInterval);
        RunInBackground("startup checks", () => ReportStartupProblemsAsync(ct));

        while (!ct.IsCancellationRequested)
        {
            // Cleared each attempt so a failed handshake (e.g. 401) never counts as a stable connection
            _connectedAt = default;

            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.AgentToken}");
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                // Keeps the handshake's HTTP status: a 401 is a rejected token, not an unreachable backend
                _ws.Options.CollectHttpResponseDetails = true;

                Log($"Connecting to {MaskUrl(_config.SidehubUrl!)}...");
                await _ws.ConnectAsync(new Uri(_config.SidehubUrl!), ct);
                Log("Connected");

                _connectedAt = DateTime.UtcNow;
                _everConnected = true;

                await SendConnectedMessageAsync(ct);
                await ReportAlivePtySessionsAsync(ct);
                RunInBackground("pending run.usage replay", () => _usageCollector.ReplayPendingAsync(ct));
                RunInBackground("pending cli-session.usage replay", () => _cliSessionUsage.ReplayPendingAsync(ct));
                StartHeartbeat(ct);
                StartPtyReaper();

                Log("Waiting for commands...");
                await ReceiveLoopAsync(ct);
            }
            // Only a real shutdown request stops the loop: _ws.Abort() (heartbeat watchdog) makes the pending
            // ReceiveAsync throw OperationCanceledException("Aborted") too, and that must reconnect, not exit.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log("Shutting down...");
                break;
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                StopHeartbeat();
                StopPtyReaper();

                // The handshake failed and the agent never got through since it started: likely a broken install
                if (_connectedAt == default && !_everConnected && _diagnostics is not null)
                {
                    var (reason, detail) = DiagnosticReporter.ClassifyConnectFailure(ex, _ws?.HttpStatusCode ?? 0);
                    _diagnostics.Report(reason, detail);
                }

                // Only reset backoff if connection was stable for at least 60 seconds
                var connectionDuration = _connectedAt == default ? 0 : (DateTime.UtcNow - _connectedAt).TotalMilliseconds;
                if (connectionDuration >= StableConnectionThresholdMs)
                {
                    reconnectAttempts = 0;
                }

                var delay = CalculateReconnectDelay(reconnectAttempts);
                reconnectAttempts++;

                Log($"Reconnecting in {delay}ms (attempt {reconnectAttempts}, was connected {connectionDuration / 1000:F0}s)...");
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                StopHeartbeat();
                StopPtyReaper();
                if (_ws != null)
                {
                    if (_ws.State == WebSocketState.Open)
                    {
                        try
                        {
                            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        }
                        catch
                        {
                            // Ignore close errors
                        }
                    }
                    _ws.Dispose();
                    _ws = null;
                }
            }
        }
    }

    /// <summary>Logs and reports what <see cref="StartupChecks"/> found wrong on this machine (once per process).</summary>
    private async Task ReportStartupProblemsAsync(CancellationToken ct)
    {
        foreach (var problem in await StartupChecks.RunOnceAsync(ct))
        {
            Log($"Startup check failed ({problem.Reason}): {problem.Detail}");
            if (_diagnostics is not null)
                await _diagnostics.ReportAsync(problem.Reason, problem.Detail);
        }
    }

    private async Task SendConnectedMessageAsync(CancellationToken ct)
    {
        var defaultShell = SystemInfoProvider.GetDefaultShell();
        var availableShells = SystemInfoProvider.GetAvailableShells();
        Log($"OS: {SystemInfoProvider.GetOsPlatform()}, Default shell: {defaultShell}, Available: [{string.Join(", ", availableShells)}]");
        var cliVersions = VersionInfo.CachedCliVersions;
        Log($"Agent version: {VersionInfo.AgentVersion}, CLIs: " + (cliVersions is null
            ? "probing"
            : $"[{string.Join(", ", cliVersions.Select(v => $"{v.Key} {v.Value}"))}]"));

        var message = new AgentConnectedMessage
        {
            AgentId = _config.AgentId!,
            WorkspaceId = _config.WorkspaceId!,
            Capabilities = _config.Capabilities!,
            DefaultShell = defaultShell,
            AvailableShells = availableShells,
            RootPath = _workingDirectory,
            AgentVersion = VersionInfo.AgentVersion,
            CliVersions = cliVersions
        };
        await SendAsync(message, ct);

        // agent.connected is an idempotent state report: re-send it once the CLI versions are known.
        if (cliVersions is null)
            RunInBackground("CLI version probe", async () =>
            {
                await VersionInfo.GetCliVersionsAsync(ct);
                await SendConnectedMessageAsync(ct);
            });
    }

    private void StartHeartbeat(CancellationToken ct)
    {
        _missedHeartbeatAcks = 0;

        _heartbeatTimer = new Timer(
            async _ =>
            {
                if (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    try
                    {
                        _missedHeartbeatAcks++;
                        if (_missedHeartbeatAcks > MaxMissedHeartbeatAcks)
                        {
                            Log($"No heartbeat ACK received for {MaxMissedHeartbeatAcks} consecutive heartbeats, forcing reconnection");
                            _ws?.Abort();
                            return;
                        }

                        await SendAsync(new AgentHeartbeatMessage(), ct);
                    }
                    catch (Exception ex)
                    {
                        Log($"Heartbeat failed: {ex.Message}");
                    }
                }
            },
            null,
            HeartbeatIntervalMs,
            HeartbeatIntervalMs
        );
    }

    private void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    private void StartPtyReaper()
    {
        _ptyReaperTimer = new Timer(
            async _ =>
            {
                await ExpirePendingFileWritesAsync(CancellationToken.None);
                var now = DateTime.UtcNow;
                foreach (var (sid, lastActivity) in _ptyLastActivity)
                {
                    if (IsBackendManagedPtySession(sid))
                        continue;
                    if ((now - lastActivity).TotalMinutes > PtyIdleTimeoutMinutes)
                    {
                        if (_ptySessions.TryRemove(sid, out var session))
                        {
                            _ptyLastActivity.TryRemove(sid, out DateTime _);
                            Log($"Reaping idle PTY session {sid} (inactive for >{PtyIdleTimeoutMinutes}min)");
                            try { await session.Executor.DisposeAsync(); } catch { }
                            CleanupNotifyFifo(sid);
                            // DisposeAsync doesn't fire the executor's exit callback, so tell the
                            // backend explicitly — otherwise it keeps reporting the session as
                            // running and frontends reattach to a dead PTY (blank terminal).
                            try { await SendAsync(new PtyExitedMessage { ExitCode = 0, PtySessionId = sid }, CancellationToken.None); } catch { }
                            HarvestFinalUsage(sid, "reaped", TimeSpan.FromSeconds(2));
                        }
                    }
                }
            },
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5)
        );
    }

    private void StopPtyReaper()
    {
        _ptyReaperTimer?.Dispose();
        _ptyReaperTimer = null;
    }

    private static bool IsBackendManagedPtySession(string ptySessionId) =>
        BackendManagedPtyPrefixes.Any(prefix => ptySessionId.StartsWith(prefix, StringComparison.Ordinal));

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var messageBuffer = new List<byte>();

        while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                Log("Server closed connection");
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                messageBuffer.AddRange(buffer.Take(result.Count));

                if (messageBuffer.Count > MaxWebSocketMessageSize)
                {
                    Log($"Message exceeded {MaxWebSocketMessageSize / (1024 * 1024)}MB limit, closing connection");
                    messageBuffer.Clear();
                    if (_ws?.State == WebSocketState.Open)
                        await _ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", ct);
                    break;
                }

                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    messageBuffer.Clear();
                    await HandleMessageAsync(json, ct);
                }
            }
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        try
        {
            var message = JsonSerializer.Deserialize<IncomingMessage>(json, _jsonOptions);
            if (message == null) return;

            // The PTY session id names files (notification FIFO) and keys every PTY structure:
            // anything but a plain id is refused before reaching a handler. Absent = legacy single PTY.
            if (!string.IsNullOrEmpty(message.PtySessionId) && !NotifyFifo.IsValidPtySessionId(message.PtySessionId))
            {
                Log($"SECURITY: refused {message.Type} with an invalid ptySessionId ({message.PtySessionId.Length} chars)");
                return;
            }

            // Slow handlers (command.execute, pty.start, file.write.end) run off the receive
            // loop: awaiting them here would stop heartbeat ACKs from being read and trigger
            // a false reconnection. Light PTY messages stay inline to keep keystroke order.
            switch (message.Type)
            {
                case "command.execute":
                    RunInBackground("command.execute", () => HandleCommandExecuteAsync(message, ct));
                    break;
                case "pty.start":
                    await DispatchPtyAsync(message, () => HandlePtyStartAsync(message, ct), runInBackground: true);
                    break;
                case "pty.input":
                    await DispatchPtyAsync(message, () => HandlePtyInputAsync(message, ct));
                    break;
                case "pty.resize":
                    await DispatchPtyAsync(message, () => { HandlePtyResize(message); return Task.CompletedTask; });
                    break;
                case "pty.stop":
                    await DispatchPtyAsync(message, () => HandlePtyStopAsync(message));
                    break;
                case "pty.history.request":
                    await HandlePtyHistoryRequestAsync(message, ct);
                    break;
                case "file.write.start":
                    await HandleFileWriteStartAsync(message, ct);
                    break;
                case "file.write.chunk":
                    await HandleFileWriteChunkAsync(message, ct);
                    break;
                case "file.write.end":
                    // start/chunk only touch _pendingFileWrites and stay inline so every
                    // chunk lands before its end; the decode + disk write + paste is offloaded.
                    RunInBackground("file.write.end", () => HandleFileWriteEndAsync(message, ct));
                    break;
                case "terminal.attachment.enqueue":
                    await HandleTerminalAttachmentAsync(message, ct);
                    break;
                case "agent.heartbeat.ack":
                    _missedHeartbeatAcks = 0;
                    break;
                case "agent.connected":
                    // Connection confirmed by server, no action needed
                    break;
                case "server.ping":
                    // Server keepalive ping - no response needed, just keeps the connection alive
                    break;
                default:
                    Log($"Unknown message type: {message.Type}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            Log($"Invalid JSON received: {ex.Message}");
        }
    }

    private void RunInBackground(string label, Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"{label} handler failed: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Runs a PTY message handler while preserving per-session ordering. If earlier work for
    /// the same session is still in flight (typically a slow pty.start), the handler is chained
    /// after it instead of racing it; otherwise light handlers run inline on the receive loop.
    /// </summary>
    private async Task DispatchPtyAsync(IncomingMessage message, Func<Task> work, bool runInBackground = false)
    {
        var key = message.PtySessionId ?? "";
        _ptyPendingWork.TryGetValue(key, out var pending);

        if (!runInBackground && (pending == null || pending.IsCompleted))
        {
            await work();
            return;
        }

        var previous = pending ?? Task.CompletedTask;
        var next = Task.Run(async () =>
        {
            try { await previous; } catch { /* already logged by the previous link */ }
            try { await work(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"{message.Type} handler failed for PTY {key}: {ex.Message}"); }
        });
        _ptyPendingWork[key] = next;
        _ = next.ContinueWith(
            t => _ptyPendingWork.TryRemove(new KeyValuePair<string, Task>(key, t)),
            TaskScheduler.Default);
    }

    private async Task HandleTerminalAttachmentAsync(IncomingMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.PtySessionId) || message.Attachment is null)
        {
            Log("Invalid terminal.attachment.enqueue message");
            return;
        }

        var mimeType = message.Attachment.MimeType?.Trim();
        var base64Data = message.Attachment.Base64Data?.Trim();
        if (string.IsNullOrWhiteSpace(mimeType) || string.IsNullOrWhiteSpace(base64Data))
        {
            Log($"Terminal attachment for PTY {message.PtySessionId} is missing mimeType or base64Data");
            return;
        }

        if (!_config.AllowFileWrite)
        {
            Log($"SECURITY: terminal attachment for PTY {message.PtySessionId} refused — file writes are disabled (allowFileWrite: false)");
            return;
        }

        // Save image to file and paste [Image #N: path] into PTY.
        // The CLI running in the PTY (claude/codex/gemini) can then read the file.
        try
        {
            var bytes = Convert.FromBase64String(base64Data);

            var ext = mimeType switch
            {
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/webp" => ".webp",
                _ => ".png"
            };
            // Random suffix: two images within the same millisecond must not overwrite each other
            // (CreateNew refuses an existing file rather than replacing it).
            var safeName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}{ext}";
            // Through FileWritePolicy: a committed .sidehub-images link (to a folder outside the
            // working directory, or inside it) makes the write fail instead of following it.
            string filePath;
            await using (var file = FileWritePolicy.OpenWrite(_workingDirectory,
                             Path.Combine(AttachmentsDirectory, safeName), FileMode.CreateNew, out filePath))
                await file.WriteAsync(bytes, ct);
            EnsureIgnoredByGit(AttachmentsDirectory);
            Log($"Attachment saved: {filePath} ({bytes.Length} bytes)");

            var imageNum = _ptyImageCounters.AddOrUpdate(message.PtySessionId, 1, (_, n) => n + 1);
            var pasteText = $"\x1b[200~[Image #{imageNum}: {filePath}] \x1b[201~";

            if (_ptySessions.TryGetValue(message.PtySessionId, out var session) && session.Executor.IsRunning)
            {
                await session.Executor.WriteAsync(pasteText, ct);
                Log($"Pasted image path into PTY session {message.PtySessionId}");
            }
            else if (_ptyExecutor?.IsRunning == true)
            {
                await _ptyExecutor.WriteAsync(pasteText, ct);
                Log($"Pasted image path into legacy PTY");
            }
            else
            {
                Log($"No running PTY for image paste");
            }
        }
        catch (Exception ex)
        {
            Log($"File-based attachment failed: {ex.Message}");
        }
    }

    private async Task HandleCommandExecuteAsync(IncomingMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.CommandId) ||
            string.IsNullOrEmpty(message.Command) ||
            string.IsNullOrEmpty(message.Shell))
        {
            Log("Invalid command message received");
            return;
        }

        if (!_config.AllowCommandExecute)
        {
            Log($"SECURITY: command {message.CommandId} refused — command.execute is disabled (allowCommandExecute: false)");
            await SendAsync(new CommandFailedMessage
            {
                CommandId = message.CommandId,
                ExitCode = -1,
                Error = "command.execute is disabled on this agent (allowCommandExecute: false)"
            }, ct);
            return;
        }

        if (_executor.IsBusy)
        {
            Log($"Busy, rejecting command {message.CommandId}");
            await SendAsync(new CommandBusyMessage { CommandId = message.CommandId }, ct);
            return;
        }

        // Only metadata: the command and its output may carry secrets or user data.
        Log($"Executing command {message.CommandId} ({message.Command.Length} chars)");

        try
        {
            long outputLines = 0;
            var exitCode = await _executor.ExecuteAsync(
                message.Command,
                message.Shell,
                async (stream, data) =>
                {
                    Interlocked.Increment(ref outputLines);
                    await SendAsync(new CommandOutputMessage
                    {
                        CommandId = message.CommandId,
                        Stream = stream,
                        Data = data
                    }, ct);
                },
                ct
            );

            Log($"Command {message.CommandId} completed (exit code {exitCode}, {Interlocked.Read(ref outputLines)} output lines)");
            await SendAsync(new CommandCompletedMessage
            {
                CommandId = message.CommandId,
                ExitCode = exitCode
            }, ct);
        }
        catch (Exception ex)
        {
            Log($"Command {message.CommandId} failed: {ex.Message}");
            await SendAsync(new CommandFailedMessage
            {
                CommandId = message.CommandId,
                ExitCode = -1,
                Error = ex.Message
            }, ct);
        }
    }

    private async Task HandleFileWriteStartAsync(IncomingMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.CommandId) || string.IsNullOrEmpty(message.Path))
        {
            Log("Invalid file.write.start message");
            return;
        }

        if (!_config.AllowFileWrite)
        {
            Log($"SECURITY: file write {message.CommandId} refused — file.write is disabled (allowFileWrite: false)");
            await SendFileWriteFailedAsync(message.CommandId, "file.write is disabled on this agent (allowFileWrite: false)", ct);
            return;
        }

        if (!FileWritePolicy.TryResolveTarget(_workingDirectory, message.Path, out var resolvedPath, out var error))
        {
            Log($"SECURITY: file write {message.CommandId} rejected — {error} (working directory '{_workingDirectory}')");
            await SendFileWriteFailedAsync(message.CommandId, error, ct);
            return;
        }

        // The path is pasted into the PTY: a control character in it (e.g. a committed symlink to a
        // directory named "x\e[201~\rcmd\r") would close the paste and type the rest as keystrokes.
        if (!PtyPastePolicy.IsSafeToPaste(resolvedPath))
        {
            Log($"SECURITY: file write {message.CommandId} rejected — resolved path contains control characters");
            await SendFileWriteFailedAsync(message.CommandId, "Path contains control characters", ct);
            return;
        }

        // Construct paste content: ask Claude CLI to read the image file
        var ptyPaste = PtyPastePolicy.BuildImagePaste(resolvedPath, message.PtyPaste);

        await ExpirePendingFileWritesAsync(ct);
        if (!_pendingFileWrites.TryStart(message.CommandId, resolvedPath, ptyPaste, message.PtySessionId))
        {
            Log($"SECURITY: file write {message.CommandId} refused — {PendingFileWrites.MaxConcurrentWrites} writes already in progress");
            await SendFileWriteFailedAsync(message.CommandId, $"Too many file writes in progress (max {PendingFileWrites.MaxConcurrentWrites})", ct);
            return;
        }
        Log($"File write started: {resolvedPath}");
    }

    private async Task HandleFileWriteChunkAsync(IncomingMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.CommandId) || string.IsNullOrEmpty(message.Data))
            return;
        switch (_pendingFileWrites.Append(message.CommandId, message.Data))
        {
            case FileWriteChunkResult.TooLarge:
                Log($"SECURITY: file write {message.CommandId} dropped — larger than {FileWritePolicy.MaxFileBytes} bytes");
                await SendFileWriteFailedAsync(message.CommandId, $"File is larger than the {FileWritePolicy.MaxFileBytes / (1024 * 1024)} MB limit", ct);
                break;
            case FileWriteChunkResult.OverBudget:
                Log($"SECURITY: file write {message.CommandId} dropped — file writes in progress already hold {PendingFileWrites.MaxTotalBytes} bytes");
                await SendFileWriteFailedAsync(message.CommandId, $"Too much file data in progress (max {PendingFileWrites.MaxTotalBytes / (1024 * 1024)} MB)", ct);
                break;
            case FileWriteChunkResult.Invalid:
                Log($"File write {message.CommandId} dropped — chunk is not valid base64");
                await SendFileWriteFailedAsync(message.CommandId, "File data is not valid base64", ct);
                break;
        }
    }

    /// <summary>Drops the writes whose file.write.end never came, so their chunks don't stay in memory.</summary>
    private async Task ExpirePendingFileWritesAsync(CancellationToken ct)
    {
        foreach (var commandId in _pendingFileWrites.RemoveExpired())
        {
            Log($"File write {commandId} expired — no file.write.end within {PendingFileWrites.Expiry.TotalMinutes:0} min");
            try { await SendFileWriteFailedAsync(commandId, "File write expired before file.write.end", ct); } catch { }
        }
    }

    private Task SendFileWriteFailedAsync(string commandId, string error, CancellationToken ct) =>
        SendAsync(new CommandFailedMessage { CommandId = commandId, ExitCode = -1, Error = error }, ct);

    private async Task HandleFileWriteEndAsync(IncomingMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.CommandId))
            return;

        if (!_pendingFileWrites.TryTake(message.CommandId, out var state))
        {
            Log("file.write.end received for unknown commandId");
            return;
        }

        using var _ = state;
        try
        {
            // Checked again right before writing: a link may have been planted since file.write.start.
            if (!FileWritePolicy.TryResolveTarget(_workingDirectory, state.Path, out var target, out var error) || target != state.Path)
            {
                Log($"SECURITY: file write {message.CommandId} rejected at write time — {(error.Length > 0 ? error : $"'{state.Path}' now resolves to '{target}'")}");
                await SendFileWriteFailedAsync(message.CommandId, error.Length > 0 ? error : $"Path '{state.Path}' changed during the upload", ct);
                return;
            }

            // The chunks were decoded and size-checked as they came: only a truncated last group is left to refuse.
            if (!state.IsComplete)
            {
                Log($"File write {message.CommandId} dropped — base64 data ends mid-group");
                await SendFileWriteFailedAsync(message.CommandId, "File data is not valid base64", ct);
                return;
            }
            // Opened without following any link, folders included: a link swapped in after the check
            // above makes the open fail instead of redirecting the write.
            var relativePath = Path.GetRelativePath(PathConfinement.RealPath(_workingDirectory), state.Path);
            await using (var file = FileWritePolicy.OpenWrite(_workingDirectory, relativePath, FileMode.Create, out var _))
                await state.WriteToAsync(file, ct);

            Log($"File written: {state.Path} ({state.Length} bytes)");

            // Paste into the correct PTY session (multi-PTY first, then legacy fallback)
            if (!string.IsNullOrEmpty(state.PtyPaste))
            {
                try
                {
                    var pasted = false;

                    // Try multi-PTY session first
                    if (!string.IsNullOrEmpty(state.PtySessionId) &&
                        _ptySessions.TryGetValue(state.PtySessionId, out var ptySession) &&
                        ptySession.Executor.IsRunning)
                    {
                        await ptySession.Executor.WriteAsync(state.PtyPaste, ct);
                        pasted = true;
                        Log($"Pasted image prompt into PTY session {state.PtySessionId}");
                    }

                    // Fall back to legacy single PTY
                    if (!pasted && _ptyExecutor?.IsRunning == true)
                    {
                        await _ptyExecutor.WriteAsync(state.PtyPaste, ct);
                        pasted = true;
                        Log($"Pasted image prompt into legacy PTY");
                    }

                    if (!pasted)
                        Log("No running PTY to paste image prompt into");
                }
                catch (Exception ex)
                {
                    Log($"Failed to paste image prompt into PTY: {ex.Message}");
                }
            }

            await SendAsync(new CommandCompletedMessage
            {
                CommandId = message.CommandId,
                ExitCode = 0
            }, ct);
        }
        catch (Exception ex)
        {
            Log($"File write failed: {ex.Message}");
            await SendAsync(new CommandFailedMessage
            {
                CommandId = message.CommandId,
                ExitCode = -1,
                Error = ex.Message
            }, ct);
        }
    }

    private async Task SendAsync<T>(T message, CancellationToken ct)
    {
        var ws = _ws;
        if (ws?.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Like <see cref="SendAsync{T}"/>, but reports failure instead of dropping or throwing.</summary>
    private async Task<bool> TrySendAsync<T>(T message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return false;
        try
        {
            await SendAsync(message, ct);
            return _ws?.State == WebSocketState.Open;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Send failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The cwd a pty.start asked for, confined to the agent's working directory and its
    /// subfolders; anything else falls back to the working directory.</summary>
    private string ResolvePtyWorkingDirectory(string? requested, string ptySessionId)
    {
        if (!PtyEnvironmentPolicy.TryResolveWorkingDirectory(_workingDirectory, requested, out var cwd))
            Log($"SECURITY: PTY {ptySessionId} working directory '{requested}' is outside '{_workingDirectory}', using the working directory");
        return cwd;
    }

    /// <summary>The shell a pty.start asked for, checked against <see cref="ShellPolicy"/>:
    /// <paramref name="shell"/> is the name reported back, <paramref name="shellPath"/> the binary
    /// actually spawned. A refused shell starts nothing (the backend sees no pty.started).</summary>
    private bool TryResolvePtyShell(string? requested, string ptySessionId, out string shell, out string shellPath)
    {
        if (!ShellPolicy.TryResolve(requested, out shellPath))
        {
            Log($"SECURITY: PTY {ptySessionId} refused — shell '{requested}' is not an allowed shell");
            shell = string.Empty;
            return false;
        }
        shell = Path.GetFileNameWithoutExtension(shellPath);
        return true;
    }

    private async Task HandlePtyStartAsync(IncomingMessage message, CancellationToken ct)
    {
        var ptySessionId = message.PtySessionId;

        // Multi-session mode (ptySessionId provided)
        if (!string.IsNullOrEmpty(ptySessionId))
        {
            if (_ptySessions.TryGetValue(ptySessionId, out var existing) && existing.Executor.IsRunning)
            {
                var isHealthy = await existing.Executor.IsHealthyAsync();
                if (isHealthy)
                {
                    Log($"PTY session {ptySessionId} already running, sending started event for reconnection");
                    await SendAsync(existing.StartedMessage(ptySessionId, reattached: true), ct);
                    return;
                }
                Log($"PTY session {ptySessionId} unhealthy, recreating");
                await existing.Executor.DisposeAsync();
                _ptySessions.TryRemove(ptySessionId, out _);
            }

            if (!TryResolvePtyShell(message.Shell, ptySessionId, out var shell, out var shellPath))
                return;
            var columns = message.Columns ?? 120;
            var rows = message.Rows ?? 30;
            var cwd = ResolvePtyWorkingDirectory(message.WorkingDirectory, ptySessionId);

            // Set up the CLI-session notification FIFO BEFORE building the env,
            // because the env points the wrappers at it.
            EnsureNotifyFifo(ptySessionId);
            var ptyEnv = BuildTerminalEnvironment(ptySessionId, message.AdditionalEnv);

            // Install the SideHub skill file (CLI commands + drive index) so any LLM
            // CLI launched from this terminal discovers sidehub-cli automatically.
            if (_config.AllowFileWrite)
            {
                var apiUrl = DeriveApiUrl(_config.SidehubUrl!);
                await SkillInstaller.EnsureSkillFilesAsync(cwd, apiUrl, _config.AgentToken!, _config.WorkspaceId!, Log);
            }
            else
            {
                Log($"Skill files not written for PTY {ptySessionId} — file writes are disabled (allowFileWrite: false)");
            }

            Log($"Starting PTY session {ptySessionId} with {shell} ({columns}x{rows}) in {cwd}");

            try
            {
                var executor = new NodePtyExecutor(cwd);
                var startedAt = DateTime.UtcNow;
                await executor.StartAsync(
                    shellPath,
                    async output => await SendAsync(new PtyOutputMessage { Data = output, PtySessionId = ptySessionId }, ct),
                    async exitCode =>
                    {
                        Log($"PTY {ptySessionId} exited with code {exitCode}");
                        _ptySessions.TryRemove(ptySessionId, out _);
                        _ptyLastActivity.TryRemove(ptySessionId, out _);
                        CleanupNotifyFifo(ptySessionId);
                        await SendAsync(new PtyExitedMessage { ExitCode = exitCode, PtySessionId = ptySessionId }, ct);
                        HarvestFinalUsage(ptySessionId, "exit");
                    },
                    columns,
                    rows,
                    ptyEnv,
                    SecretMasker.For(message.AdditionalEnv, message.SecretKeys),
                    ct
                );

                var session = new PtySession(executor, shell, startedAt);
                _ptySessions[ptySessionId] = session;
                _ptyLastActivity[ptySessionId] = DateTime.UtcNow;
                try { _ptyCwd[ptySessionId] = Path.GetFullPath(cwd); }
                catch { _ptyCwd[ptySessionId] = cwd; }
                if (RunUsageCollector.ResolveRunId(ptySessionId, message.AdditionalEnv) is { } runId)
                    _usageCollector.TrackRun(ptySessionId, runId, _ptyCwd[ptySessionId]);
                StartFifoReader(ptySessionId, ct);
                await SendAsync(session.StartedMessage(ptySessionId, reattached: false), ct);
                Log($"PTY session {ptySessionId} started");
            }
            catch (Exception ex)
            {
                Log($"Failed to start PTY {ptySessionId}: {ex.Message}");
                CleanupNotifyFifo(ptySessionId);
            }
            return;
        }

        // Legacy mode (no ptySessionId) — single PTY per agent
        if (_ptyExecutor?.IsRunning == true)
        {
            var isHealthy = await _ptyExecutor.IsHealthyAsync();
            if (isHealthy)
            {
                Log("PTY session already running and healthy, sending started event for reconnection");
                await SendAsync(new PtyStartedMessage
                {
                    Shell = _currentPtyShell ?? SystemInfoProvider.GetDefaultShell(),
                    StartedAt = _currentPtyStartedAt,
                }, ct);
                return;
            }

            Log("PTY session exists but is not healthy, stopping and creating new session");
            await _ptyExecutor.DisposeAsync();
            _ptyExecutor = null;
            _currentPtyShell = null;
        }

        {
            if (!TryResolvePtyShell(message.Shell, "legacy", out var shell, out var shellPath))
                return;
            var columns = message.Columns ?? 120;
            var rows = message.Rows ?? 30;
            var effectiveWorkingDirectory = ResolvePtyWorkingDirectory(message.WorkingDirectory, "legacy");

            Log($"Starting PTY session with {shell} ({columns}x{rows}) in {effectiveWorkingDirectory}");

            try
            {
                _ptyExecutor = new NodePtyExecutor(effectiveWorkingDirectory);
                await _ptyExecutor.StartAsync(
                    shellPath,
                    async output => await SendAsync(new PtyOutputMessage { Data = output }, ct),
                    async exitCode =>
                    {
                        Log($"PTY exited with code {exitCode}");
                        _currentPtyShell = null;
                        await SendAsync(new PtyExitedMessage { ExitCode = exitCode }, ct);
                    },
                    columns,
                    rows,
                    null,
                    null,
                    ct
                );

                _currentPtyShell = shell;
                _currentPtyStartedAt = DateTime.UtcNow;
                await SendAsync(new PtyStartedMessage { Shell = shell, StartedAt = _currentPtyStartedAt }, ct);
                Log("PTY session started");
            }
            catch (Exception ex)
            {
                Log($"Failed to start PTY: {ex.Message}");
            }
        }
    }

    private async Task HandlePtyInputAsync(IncomingMessage message, CancellationToken ct)
    {
        var ptySessionId = message.PtySessionId;

        if (!string.IsNullOrEmpty(ptySessionId))
        {
            if (_ptySessions.TryGetValue(ptySessionId, out var session) && session.Executor.IsRunning && !string.IsNullOrEmpty(message.Input))
            {
                // Log backend-launched input lines (runs, legacy scheduler/workflow sessions) so we can
                // diagnose "claude never started" issues. Interactive keystrokes are skipped to avoid spam.
                // Only the program name and size: the rest of the line may hold a prompt or secrets.
                if (IsBackendManagedPtySession(ptySessionId))
                    Log($"PTY {ptySessionId} input: {ProgramNameForLog(message.Input!)} ({message.Input!.Length} chars)");
                _ptyLastActivity[ptySessionId] = DateTime.UtcNow;
                try { await session.Executor.WriteAsync(message.Input, ct); }
                catch (Exception ex) { Log($"Failed to write to PTY {ptySessionId}: {ex.Message}"); }
            }
            else if (_ptySessions.ContainsKey(ptySessionId) && !string.IsNullOrEmpty(message.Input))
            {
                Log($"WARN: pty.input received for {ptySessionId} but session is not running");
            }
            return;
        }

        if (_ptyExecutor?.IsRunning != true || string.IsNullOrEmpty(message.Input))
            return;

        try
        {
            await _ptyExecutor.WriteAsync(message.Input, ct);
        }
        catch (Exception ex)
        {
            Log($"Failed to write to PTY: {ex.Message}");
        }
    }

    private void HandlePtyResize(IncomingMessage message)
    {
        var ptySessionId = message.PtySessionId;
        var columns = message.Columns ?? 120;
        var rows = message.Rows ?? 30;

        if (!string.IsNullOrEmpty(ptySessionId))
        {
            if (_ptySessions.TryGetValue(ptySessionId, out var session) && session.Executor.IsRunning)
            {
                Log($"Resizing PTY {ptySessionId} to {columns}x{rows}");
                session.Executor.Resize(columns, rows);
            }
            return;
        }

        if (_ptyExecutor?.IsRunning != true)
            return;

        Log($"Resizing PTY to {columns}x{rows}");
        _ptyExecutor.Resize(columns, rows);
    }

    private async Task HandlePtyStopAsync(IncomingMessage? message = null)
    {
        var ptySessionId = message?.PtySessionId;

        if (!string.IsNullOrEmpty(ptySessionId))
        {
            if (_ptySessions.TryRemove(ptySessionId, out var session))
            {
                _ptyLastActivity.TryRemove(ptySessionId, out _);
                Log($"Stopping PTY session {ptySessionId}");
                try { await session.Executor.DisposeAsync(); }
                catch (Exception ex) { Log($"Error stopping PTY {ptySessionId}: {ex.Message}"); }
                CleanupNotifyFifo(ptySessionId);
                Log($"PTY session {ptySessionId} stopped");
                // DisposeAsync doesn't fire the exit callback, so the run's last report is sent here,
                // once the killed CLI has had a moment to write its final cost-state.
                HarvestFinalUsage(ptySessionId, "stop", TimeSpan.FromSeconds(2));
            }
            return;
        }

        if (_ptyExecutor?.IsRunning != true)
        {
            Log("PTY session not running, ignoring stop");
            return;
        }

        Log("Stopping PTY session");
        try
        {
            await _ptyExecutor.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log($"Error stopping PTY: {ex.Message}");
        }
        _ptyExecutor = null;
        _currentPtyShell = null;
        Log("PTY session stopped");
    }

    /// <summary>The PTY is gone: last report of its run (<c>run.usage</c>) or of its CLI sessions (<c>cli-session.usage</c>).</summary>
    private void HarvestFinalUsage(string ptySessionId, string trigger, TimeSpan delay = default)
    {
        RunInBackground("final usage", async () =>
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            if (_usageCollector.IsTracked(ptySessionId))
                await _usageCollector.HarvestAsync(ptySessionId, trigger, final: true, CancellationToken.None);
            await _cliSessionUsage.PtyClosedAsync(ptySessionId, trigger, CancellationToken.None);
        });
    }

    private async Task HandlePtyHistoryRequestAsync(IncomingMessage message, CancellationToken ct)
    {
        var requestId = message.RequestId ?? Guid.NewGuid().ToString();
        var ptySessionId = message.PtySessionId;

        if (!string.IsNullOrEmpty(ptySessionId))
        {
            if (_ptySessions.TryGetValue(ptySessionId, out var session) && session.Executor.IsRunning)
            {
                var hist = session.Executor.GetBufferedOutput();
                var size = session.Executor.BufferSize;
                Log($"Sending PTY {ptySessionId} history ({size} bytes)");
                await SendAsync(new PtyHistoryMessage { Data = hist, BufferSize = size, RequestId = requestId, PtySessionId = ptySessionId }, ct);
            }
            else
            {
                await SendAsync(new PtyHistoryMessage { Data = "", BufferSize = 0, RequestId = requestId, PtySessionId = ptySessionId }, ct);
            }
            return;
        }

        if (_ptyExecutor?.IsRunning != true)
        {
            Log("PTY history requested but no session running");
            await SendAsync(new PtyHistoryMessage { Data = "", BufferSize = 0, RequestId = requestId }, ct);
            return;
        }

        var history = _ptyExecutor.GetBufferedOutput();
        var bufferSize = _ptyExecutor.BufferSize;
        Log($"Sending PTY history ({bufferSize} bytes)");
        await SendAsync(new PtyHistoryMessage { Data = history, BufferSize = bufferSize, RequestId = requestId }, ct);
    }

    /// <summary>
    /// After reconnecting to backend, report any PTY sessions still alive
    /// so the backend can restore PtyOutputNotifier state and notify frontends.
    /// </summary>
    private async Task ReportAlivePtySessionsAsync(CancellationToken ct)
    {
        foreach (var (ptySessionId, session) in _ptySessions)
        {
            if (session.Executor.IsRunning)
            {
                Log($"Reporting alive PTY session {ptySessionId} (shell: {session.Shell})");
                // Reattached: the process (and any CLI inside it) survived, so the
                // frontend must not auto-type `<provider> --resume` into it.
                await SendAsync(session.StartedMessage(ptySessionId, reattached: true), ct);
                await ReportCliSessionAsync(ptySessionId, ct);
                if (_cliStates.Current(ptySessionId) is { } cliState)
                    await SendAsync(cliState, ct);
            }
        }

        if (_ptyExecutor?.IsRunning == true)
        {
            Log("Reporting alive legacy PTY session");
            await SendAsync(new PtyStartedMessage
            {
                Shell = _currentPtyShell ?? SystemInfoProvider.GetDefaultShell(),
                Reattached = true,
                StartedAt = _currentPtyStartedAt,
            }, ct);
        }
    }

    /// <summary>The backend keeps CLI sessions in memory only: after it restarts, tell it again which
    /// conversation runs in the PTY and its title.</summary>
    private async Task ReportCliSessionAsync(string ptySessionId, CancellationToken ct)
    {
        if (!_ptyCliSessions.TryGetValue(ptySessionId, out var cliSession)) return;
        await SendAsync(new PtyCliSessionStartedMessage
        {
            PtySessionId = ptySessionId,
            Provider = cliSession.Provider,
            CliSessionId = cliSession.CliSessionId,
            Replayed = true,
        }, ct);
        if (cliSession.Title is { } title)
        {
            await SendAsync(new PtyCliSessionTitledMessage
            {
                PtySessionId = ptySessionId,
                CliSessionId = cliSession.CliSessionId,
                Title = title,
            }, ct);
        }
    }

    private static int CalculateReconnectDelay(int attempts)
    {
        var delay = (int)(MinReconnectDelayMs * Math.Pow(BackoffMultiplier, attempts));
        return Math.Min(delay, MaxReconnectDelayMs);
    }

    public async ValueTask DisposeAsync()
    {
        StopHeartbeat();
        _cliSessionUsageTimer?.Dispose();
        _cliSessionUsageTimer = null;

        // Dispose multi-PTY sessions
        foreach (var (sid, session) in _ptySessions)
        {
            try { await session.Executor.DisposeAsync(); } catch { }
            CleanupNotifyFifo(sid);
        }
        _ptySessions.Clear();
        _ptyLastActivity.Clear();
        _ptyCwd.Clear();
        _ptyCliSessions.Clear();
        _cliStates.ClearAll();
        foreach (var cts in _ptyFifoReaders.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
        _ptyFifoReaders.Clear();
        foreach (var cts in _claudeTitleWatchers.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
        _claudeTitleWatchers.Clear();

        if (_ptyExecutor != null)
        {
            await _ptyExecutor.DisposeAsync();
            _ptyExecutor = null;
        }
        if (_ws != null)
        {
            if (_ws.State == WebSocketState.Open)
            {
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
                }
                catch
                {
                    // Ignore
                }
            }
            _ws.Dispose();
        }
    }
}
