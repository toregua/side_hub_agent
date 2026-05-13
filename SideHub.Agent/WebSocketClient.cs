using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SideHub.Agent.Models;

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
    private NodePtyExecutor? _ptyExecutor;
    // Multi-PTY: keyed by ptySessionId
    private readonly ConcurrentDictionary<string, (NodePtyExecutor Executor, string Shell)> _ptySessions = new();
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
    private const int PtyIdleTimeoutMinutes = 30;
    private readonly ConcurrentDictionary<string, (string Path, StringBuilder Data, string? PtyPaste, string? PtySessionId)> _pendingFileWrites = new();

    private const int MinReconnectDelayMs = 1000;
    private const int MaxReconnectDelayMs = 30000;
    private const double BackoffMultiplier = 1.5;
    private const int HeartbeatIntervalMs = 15000;
    private const int MaxMissedHeartbeatAcks = 3;
    private const int StableConnectionThresholdMs = 60000; // 60s before resetting backoff
    private const int MaxWebSocketMessageSize = 50 * 1024 * 1024; // 50 MB

    private int _missedHeartbeatAcks;
    private DateTime _connectedAt;

    public WebSocketClient(AgentConfig config, CommandExecutor executor, string workingDirectory, string? displayName = null)
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
    }

    private void Log(string message) => Console.WriteLine($"[{_displayName}] {message}");

    /// <summary>Mask sensitive query-string values (token, key, secret) in URLs for safe logging.</summary>
    private static string MaskUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (string.IsNullOrEmpty(uri.Query) || uri.Query == "?") return $"{uri.GetLeftPart(UriPartial.Path)}";
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
            return $"{uri.GetLeftPart(UriPartial.Path)}?{qs}";
        }
        catch
        {
            return "***masked-url***";
        }
    }

    /// <summary>Derive HTTP API URL from WebSocket URL (wss://host/ws/agent → https://host).</summary>
    private static string DeriveApiUrl(string wsUrl)
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
        var agentLibDir = "/usr/local/lib/sidehub-agent";
        var wrappersDir = Path.Combine(AppContext.BaseDirectory.TrimEnd('/'), "cli-wrappers");

        var pathParts = new List<string>();
        if (Directory.Exists(wrappersDir) && !currentPath.Contains(wrappersDir))
            pathParts.Add(wrappersDir);
        if (!currentPath.Contains(agentLibDir))
            pathParts.Add(agentLibDir);
        var fullPath = pathParts.Count > 0
            ? string.Join(Path.PathSeparator, pathParts) + Path.PathSeparator + currentPath
            : currentPath;

        var env = new Dictionary<string, string>
        {
            ["SIDEHUB_PTY_SESSION_ID"] = ptySessionId,
            ["SIDEHUB_PTY_NOTIFY_FIFO"] = GetFifoPath(ptySessionId),
            ["SIDEHUB_CLI_WRAPPERS"] = wrappersDir,
            ["PATH"] = fullPath,
            ["SIDEHUB_API_URL"] = DeriveApiUrl(_config.SidehubUrl!),
            ["SIDEHUB_AGENT_TOKEN"] = _config.AgentToken!,
            ["SIDEHUB_WORKSPACE_ID"] = _config.WorkspaceId!,
        };
        // Point the pty-helper at our custom rcfile so it can pass `--rcfile`
        // to bash, ensuring our PATH wins after the user's .bashrc runs.
        var sidehubBashrc = Path.Combine(wrappersDir, "sidehub.bashrc");
        if (File.Exists(sidehubBashrc))
            env["SIDEHUB_BASHRC"] = sidehubBashrc;
        if (!string.IsNullOrEmpty(_config.AgentId))
            env["SIDEHUB_AGENT_ID"] = _config.AgentId!;

        // Merge caller-supplied env (e.g. workflow execution context). Caller wins on conflict.
        if (additionalEnv is not null)
        {
            foreach (var kvp in additionalEnv)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                env[kvp.Key] = kvp.Value ?? string.Empty;
            }
        }

        return env;
    }

    /// <summary>Truncate a shell command for safe logging (first 80 chars).</summary>
    private static string TruncateCommand(string command)
    {
        if (command.Length <= 80) return command;
        return command[..80] + "...";
    }

    private static string GetFifoPath(string ptySessionId)
        => $"/tmp/sidehub-pty-{ptySessionId}.fifo";

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
                    var psi = new ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"+x {file}",
                        UseShellExecute = false,
                        RedirectStandardError = true,
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(2000);
                }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>Create the notification FIFO before the PTY starts. Best-effort:
    /// if mkfifo isn't available, we log and skip — the CLI wrapper will simply
    /// no-op its notification and the existing session behavior is preserved.</summary>
    private void EnsureNotifyFifo(string ptySessionId)
    {
        var fifoPath = GetFifoPath(ptySessionId);
        try
        {
            if (File.Exists(fifoPath))
                File.Delete(fifoPath);
        }
        catch { /* ignore */ }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "mkfifo",
                Arguments = $"-m 0600 {fifoPath}",
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
            if (p is null || p.ExitCode != 0)
            {
                Log($"mkfifo failed for {fifoPath} (exit {p?.ExitCode}); CLI session notifications disabled for this PTY");
            }
        }
        catch (Exception ex)
        {
            Log($"mkfifo unavailable ({ex.Message}); CLI session notifications disabled for this PTY");
        }
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
        StopClaudeTitleWatchers(ptySessionId);
        try
        {
            var fifoPath = GetFifoPath(ptySessionId);
            if (File.Exists(fifoPath))
                File.Delete(fifoPath);
        }
        catch { /* ignore */ }
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
    /// notification FIFO and forwards CLI-session events to the backend. We open
    /// the FIFO in read+write mode (O_RDWR) so the reader doesn't block when no
    /// writer is connected and doesn't see EOF when a writer disconnects between
    /// CLI invocations.</summary>
    private void StartFifoReader(string ptySessionId, CancellationToken ct)
    {
        var fifoPath = GetFifoPath(ptySessionId);
        if (!File.Exists(fifoPath)) return; // mkfifo failed; nothing to read

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ptyFifoReaders[ptySessionId] = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // O_RDWR keeps the FIFO open even when wrappers come and go.
                using var stream = new FileStream(fifoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!token.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (line is null)
                    {
                        // FIFO closed; small backoff before retry to avoid spinning.
                        await Task.Delay(200, token);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    await HandleFifoLineAsync(ptySessionId, line, token);
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Log($"FIFO reader for {ptySessionId} crashed: {ex.Message}");
            }
        }, token);
    }

    private async Task HandleFifoLineAsync(string ptySessionId, string line, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var ev = root.TryGetProperty("event", out var eP) ? eP.GetString() : null;
            if (ev != "cli-session-started") return;

            var provider = root.TryGetProperty("provider", out var pP) ? pP.GetString() : null;
            var cliSessionId = root.TryGetProperty("cliSessionId", out var sP) ? sP.GetString() : null;
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(cliSessionId)) return;

            Log($"CLI session started in PTY {ptySessionId}: provider={provider} cliSessionId={cliSessionId}");
            await SendAsync(new PtyCliSessionStartedMessage
            {
                PtySessionId = ptySessionId,
                Provider = provider!,
                CliSessionId = cliSessionId!,
            }, ct);

            // For Claude, watch the project JSONL for an ai-title line and
            // forward it as a tab-label suggestion.
            if (string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase))
            {
                StartClaudeTitleWatcher(ptySessionId, cliSessionId!, ct);
            }
        }
        catch (JsonException)
        {
            Log($"Malformed FIFO line on PTY {ptySessionId}: {line}");
        }
        catch (Exception ex)
        {
            Log($"FIFO line handling failed on PTY {ptySessionId}: {ex.Message}");
        }
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
        if (!_ptyCwd.TryGetValue(ptySessionId, out var cwd) || string.IsNullOrEmpty(cwd))
        {
            Log($"No cwd recorded for PTY {ptySessionId}; skipping ai-title watcher for {cliSessionId}");
            return;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            Log($"No HOME available; skipping ai-title watcher for {cliSessionId}");
            return;
        }

        var encoded = cwd.Replace('/', '-');
        var projectDir = Path.Combine(home, ".claude", "projects", encoded);
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
                async Task TryEmitAsync()
                {
                    if (emitted || token.IsCancellationRequested) return;
                    var title = TryReadClaudeTitle(jsonlPath);
                    if (string.IsNullOrEmpty(title)) return;
                    emitted = true;
                    Log($"Claude ai-title for {cliSessionId}: {title}");
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

    /// <summary>Scan a Claude project JSONL file for the first ai-title line and
    /// return the title string, or null if absent / unreadable.</summary>
    private static string? TryReadClaudeTitle(string jsonlPath)
    {
        if (!File.Exists(jsonlPath)) return null;
        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
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

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.AgentToken}");
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

                Log($"Connecting to {_config.SidehubUrl}...");
                await _ws.ConnectAsync(new Uri(_config.SidehubUrl!), ct);
                Log("Connected");

                _connectedAt = DateTime.UtcNow;

                await SendConnectedMessageAsync(ct);
                await ReportAlivePtySessionsAsync(ct);
                StartHeartbeat(ct);
                StartPtyReaper();

                Log("Waiting for commands...");
                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Log("Shutting down...");
                break;
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                StopHeartbeat();
                StopPtyReaper();

                // Only reset backoff if connection was stable for at least 60 seconds
                var connectionDuration = (DateTime.UtcNow - _connectedAt).TotalMilliseconds;
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

    private async Task SendConnectedMessageAsync(CancellationToken ct)
    {
        var defaultShell = SystemInfoProvider.GetDefaultShell();
        var availableShells = SystemInfoProvider.GetAvailableShells();
        Log($"OS: {SystemInfoProvider.GetOsPlatform()}, Default shell: {defaultShell}, Available: [{string.Join(", ", availableShells)}]");

        var message = new AgentConnectedMessage
        {
            AgentId = _config.AgentId!,
            WorkspaceId = _config.WorkspaceId!,
            Capabilities = _config.Capabilities!,
            DefaultShell = defaultShell,
            AvailableShells = availableShells,
            RootPath = _workingDirectory
        };
        await SendAsync(message, ct);
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
                var now = DateTime.UtcNow;
                foreach (var (sid, lastActivity) in _ptyLastActivity)
                {
                    if ((now - lastActivity).TotalMinutes > PtyIdleTimeoutMinutes)
                    {
                        if (_ptySessions.TryRemove(sid, out var session))
                        {
                            _ptyLastActivity.TryRemove(sid, out DateTime _);
                            Log($"Reaping idle PTY session {sid} (inactive for >{PtyIdleTimeoutMinutes}min)");
                            try { await session.Executor.DisposeAsync(); } catch { }
                            CleanupNotifyFifo(sid);
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

            switch (message.Type)
            {
                case "command.execute":
                    await HandleCommandExecuteAsync(message, ct);
                    break;
                case "pty.start":
                    await HandlePtyStartAsync(message, ct);
                    break;
                case "pty.input":
                    await HandlePtyInputAsync(message, ct);
                    break;
                case "pty.resize":
                    HandlePtyResize(message);
                    break;
                case "pty.stop":
                    await HandlePtyStopAsync(message);
                    break;
                case "pty.history.request":
                    await HandlePtyHistoryRequestAsync(message, ct);
                    break;
                case "file.write.start":
                    await HandleFileWriteStartAsync(message, ct);
                    break;
                case "file.write.chunk":
                    HandleFileWriteChunk(message);
                    break;
                case "file.write.end":
                    await HandleFileWriteEndAsync(message, ct);
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

        // Save image to file and paste [Image #N: path] into PTY.
        // The CLI running in the PTY (claude/codex/gemini) can then read the file.
        try
        {
            var bytes = Convert.FromBase64String(base64Data);
            var imagesDir = Path.Combine(_workingDirectory, ".sidehub-images");
            Directory.CreateDirectory(imagesDir);

            var ext = mimeType switch
            {
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/webp" => ".webp",
                _ => ".png"
            };
            var safeName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
            var filePath = Path.Combine(imagesDir, safeName);
            await File.WriteAllBytesAsync(filePath, bytes, ct);
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

        if (_executor.IsBusy)
        {
            Log($"Busy, rejecting command {message.CommandId}");
            await SendAsync(new CommandBusyMessage { CommandId = message.CommandId }, ct);
            return;
        }

        Log($"Executing: {TruncateCommand(message.Command)}");

        try
        {
            var exitCode = await _executor.ExecuteAsync(
                message.Command,
                message.Shell,
                async (stream, data) =>
                {
                    Log($"[{stream}] {data}");
                    await SendAsync(new CommandOutputMessage
                    {
                        CommandId = message.CommandId,
                        Stream = stream,
                        Data = data
                    }, ct);
                },
                ct
            );

            Log($"Completed (exit code {exitCode})");
            await SendAsync(new CommandCompletedMessage
            {
                CommandId = message.CommandId,
                ExitCode = exitCode
            }, ct);
        }
        catch (Exception ex)
        {
            Log($"Failed: {ex.Message}");
            await SendAsync(new CommandFailedMessage
            {
                CommandId = message.CommandId,
                ExitCode = -1,
                Error = ex.Message
            }, ct);
        }
    }

    private bool IsPathWithinWorkingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var allowedDir = Path.GetFullPath(_workingDirectory);
        if (!allowedDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
            allowedDir += Path.DirectorySeparatorChar;
        return fullPath.StartsWith(allowedDir, StringComparison.Ordinal)
            || fullPath == allowedDir.TrimEnd(Path.DirectorySeparatorChar);
    }

    private async Task HandleFileWriteStartAsync(IncomingMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.CommandId) || string.IsNullOrEmpty(message.Path))
        {
            Log("Invalid file.write.start message");
            return;
        }

        // Resolve relative paths within the working directory
        var resolvedPath = Path.IsPathRooted(message.Path)
            ? message.Path
            : Path.GetFullPath(Path.Combine(_workingDirectory, message.Path));

        if (!IsPathWithinWorkingDirectory(resolvedPath))
        {
            Log($"SECURITY: file write rejected — path '{resolvedPath}' is outside working directory '{_workingDirectory}'");
            await SendAsync(new CommandFailedMessage
            {
                CommandId = message.CommandId,
                ExitCode = -1,
                Error = $"Path '{resolvedPath}' is outside the allowed working directory"
            }, ct);
            return;
        }

        // Construct paste content: ask Claude CLI to read the image file
        var ptyPaste = !string.IsNullOrEmpty(message.PtyPaste)
            ? message.PtyPaste
            : $"\x1b[200~Please look at this image I just uploaded: {resolvedPath}\x1b[201~";

        _pendingFileWrites[message.CommandId] = (resolvedPath, new StringBuilder(), ptyPaste, message.PtySessionId);
        Log($"File write started: {resolvedPath}");
    }

    private void HandleFileWriteChunk(IncomingMessage message)
    {
        if (string.IsNullOrEmpty(message.CommandId) || string.IsNullOrEmpty(message.Data))
            return;
        if (_pendingFileWrites.TryGetValue(message.CommandId, out var state))
            state.Data.Append(message.Data);
    }

    private async Task HandleFileWriteEndAsync(IncomingMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.CommandId))
            return;

        if (!_pendingFileWrites.TryGetValue(message.CommandId, out var state))
        {
            Log("file.write.end received for unknown commandId");
            return;
        }
        _pendingFileWrites.TryRemove(message.CommandId, out _);

        try
        {
            var dir = Path.GetDirectoryName(state.Path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var bytes = Convert.FromBase64String(state.Data.ToString());
            await File.WriteAllBytesAsync(state.Path, bytes, ct);

            Log($"File written: {state.Path} ({bytes.Length} bytes)");

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
        if (_ws?.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
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
                    await SendAsync(new PtyStartedMessage { Shell = existing.Shell, PtySessionId = ptySessionId, Reattached = true }, ct);
                    return;
                }
                Log($"PTY session {ptySessionId} unhealthy, recreating");
                await existing.Executor.DisposeAsync();
                _ptySessions.TryRemove(ptySessionId, out _);
            }

            var shell = message.Shell ?? SystemInfoProvider.GetDefaultShell();
            var columns = message.Columns ?? 120;
            var rows = message.Rows ?? 30;
            var cwd = message.WorkingDirectory ?? _workingDirectory;

            // Set up the CLI-session notification FIFO BEFORE building the env,
            // because the env points the wrappers at it.
            EnsureNotifyFifo(ptySessionId);
            var ptyEnv = BuildTerminalEnvironment(ptySessionId, message.AdditionalEnv);

            // Install the SideHub skill file (CLI commands + drive index) so any LLM
            // CLI launched from this terminal discovers sidehub-cli automatically.
            var apiUrl = DeriveApiUrl(_config.SidehubUrl!);
            await SkillInstaller.EnsureSkillFilesAsync(cwd, "claude", apiUrl, _config.AgentToken!, _config.WorkspaceId!);
            await SkillInstaller.EnsureSkillFilesAsync(cwd, "codex", apiUrl, _config.AgentToken!, _config.WorkspaceId!);
            await SkillInstaller.EnsureSkillFilesAsync(cwd, "gemini", apiUrl, _config.AgentToken!, _config.WorkspaceId!);

            Log($"Starting PTY session {ptySessionId} with {shell} ({columns}x{rows}) in {cwd}");

            try
            {
                var executor = new NodePtyExecutor(cwd);
                await executor.StartAsync(
                    shell,
                    async output => await SendAsync(new PtyOutputMessage { Data = output, PtySessionId = ptySessionId }, ct),
                    async exitCode =>
                    {
                        Log($"PTY {ptySessionId} exited with code {exitCode}");
                        _ptySessions.TryRemove(ptySessionId, out _);
                        _ptyLastActivity.TryRemove(ptySessionId, out _);
                        CleanupNotifyFifo(ptySessionId);
                        await SendAsync(new PtyExitedMessage { ExitCode = exitCode, PtySessionId = ptySessionId }, ct);
                    },
                    columns,
                    rows,
                    ptyEnv,
                    ct
                );

                _ptySessions[ptySessionId] = (executor, shell);
                _ptyLastActivity[ptySessionId] = DateTime.UtcNow;
                try { _ptyCwd[ptySessionId] = Path.GetFullPath(cwd); }
                catch { _ptyCwd[ptySessionId] = cwd; }
                StartFifoReader(ptySessionId, ct);
                await SendAsync(new PtyStartedMessage { Shell = shell, PtySessionId = ptySessionId }, ct);
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
                await SendAsync(new PtyStartedMessage { Shell = _currentPtyShell ?? SystemInfoProvider.GetDefaultShell() }, ct);
                return;
            }

            Log("PTY session exists but is not healthy, stopping and creating new session");
            await _ptyExecutor.DisposeAsync();
            _ptyExecutor = null;
            _currentPtyShell = null;
        }

        {
            var shell = message.Shell ?? SystemInfoProvider.GetDefaultShell();
            var columns = message.Columns ?? 120;
            var rows = message.Rows ?? 30;
            var effectiveWorkingDirectory = message.WorkingDirectory ?? _workingDirectory;

            Log($"Starting PTY session with {shell} ({columns}x{rows}) in {effectiveWorkingDirectory}");

            try
            {
                _ptyExecutor = new NodePtyExecutor(effectiveWorkingDirectory);
                await _ptyExecutor.StartAsync(
                    shell,
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
                    ct
                );

                _currentPtyShell = shell;
                await SendAsync(new PtyStartedMessage { Shell = shell }, ct);
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
                // Log scheduler/workflow input lines so we can diagnose "claude never started"
                // issues. Single-key inputs from interactive use are skipped to avoid spam.
                if (ptySessionId.StartsWith("scheduler-") || ptySessionId.StartsWith("workflow-"))
                {
                    var preview = message.Input!.Length > 200 ? message.Input.Substring(0, 200) + "..." : message.Input;
                    var oneLine = preview.Replace('\n', '⏎');
                    Log($"PTY {ptySessionId} input: {oneLine}");
                }
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
        foreach (var (ptySessionId, (executor, shell)) in _ptySessions)
        {
            if (executor.IsRunning)
            {
                Log($"Reporting alive PTY session {ptySessionId} (shell: {shell})");
                await SendAsync(new PtyStartedMessage { Shell = shell, PtySessionId = ptySessionId }, ct);
            }
        }

        if (_ptyExecutor?.IsRunning == true)
        {
            Log("Reporting alive legacy PTY session");
            await SendAsync(new PtyStartedMessage
            {
                Shell = _currentPtyShell ?? SystemInfoProvider.GetDefaultShell()
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

        // Dispose multi-PTY sessions
        foreach (var (sid, session) in _ptySessions)
        {
            try { await session.Executor.DisposeAsync(); } catch { }
            CleanupNotifyFifo(sid);
        }
        _ptySessions.Clear();
        _ptyLastActivity.Clear();
        _ptyCwd.Clear();
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
