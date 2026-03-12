using System.Collections.Concurrent;
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
    private string? _currentPtyShell;
    private NodePtyExecutor? _ptyExecutor;
    private readonly ConcurrentDictionary<string, (string Path, StringBuilder Data, string? PtyPaste)> _pendingFileWrites = new();
    private readonly ConcurrentDictionary<string, System.Diagnostics.Process> _claudeSdkProcesses = new();
    private readonly ConcurrentDictionary<string, CodexBridge> _codexBridges = new();
    private readonly ConcurrentDictionary<string, GeminiBridge> _geminiBridges = new();
    private AgentSdkProxy? _proxy;

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

    /// <summary>Truncate a shell command for safe logging (first 80 chars).</summary>
    private static string TruncateCommand(string command)
    {
        if (command.Length <= 80) return command;
        return command[..80] + "...";
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
                await ReportAliveSessionsAsync(ct);
                StartHeartbeat(ct);

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
                    await HandlePtyStopAsync();
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
                case "agent-sdk.spawn":
                    await HandleAgentSdkSpawnAsync(message, ct);
                    break;
                case "agent-sdk.stop":
                    await HandleAgentSdkStopAsync(message);
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

        if (!IsPathWithinWorkingDirectory(message.Path))
        {
            Log($"SECURITY: file write rejected — path '{message.Path}' is outside working directory '{_workingDirectory}'");
            await SendAsync(new CommandFailedMessage
            {
                CommandId = message.CommandId,
                ExitCode = -1,
                Error = $"Path '{message.Path}' is outside the allowed working directory"
            }, ct);
            return;
        }

        _pendingFileWrites[message.CommandId] = (message.Path, new StringBuilder(), message.PtyPaste);
        Log($"File write started: {message.Path}");
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

            // Paste path into PTY if requested and PTY is running
            if (!string.IsNullOrEmpty(state.PtyPaste) && _ptyExecutor?.IsRunning == true)
            {
                try
                {
                    await _ptyExecutor.WriteAsync(state.PtyPaste, ct);
                    Log($"Pasted path into PTY");
                }
                catch (Exception ex)
                {
                    Log($"Failed to paste path into PTY: {ex.Message}");
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

        var shell = message.Shell ?? SystemInfoProvider.GetDefaultShell();
        var columns = message.Columns ?? 120;
        var rows = message.Rows ?? 30;
        var effectiveWorkingDirectory = message.WorkingDirectory ?? _workingDirectory;

        Log($"Starting PTY session with {shell} ({columns}x{rows}) in {effectiveWorkingDirectory}");

        try
        {
            // Create fresh NodePtyExecutor for each session (uses node-pty for correct terminal dimensions)
            _ptyExecutor = new NodePtyExecutor(effectiveWorkingDirectory);
            await _ptyExecutor.StartAsync(
                shell,
                async output =>
                {
                    await SendAsync(new PtyOutputMessage { Data = output }, ct);
                },
                async exitCode =>
                {
                    Log($"PTY exited with code {exitCode}");
                    _currentPtyShell = null;
                    await SendAsync(new PtyExitedMessage { ExitCode = exitCode }, ct);
                },
                columns,
                rows,
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

    private async Task HandlePtyInputAsync(IncomingMessage message, CancellationToken ct)
    {
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
        if (_ptyExecutor?.IsRunning != true)
            return;

        var columns = message.Columns ?? 120;
        var rows = message.Rows ?? 30;

        Log($"Resizing PTY to {columns}x{rows}");
        _ptyExecutor.Resize(columns, rows);
    }

    private async Task HandlePtyStopAsync()
    {
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

        if (_ptyExecutor?.IsRunning != true)
        {
            Log("PTY history requested but no session running");
            await SendAsync(new PtyHistoryMessage
            {
                Data = "",
                BufferSize = 0,
                RequestId = requestId
            }, ct);
            return;
        }

        var history = _ptyExecutor.GetBufferedOutput();
        var bufferSize = _ptyExecutor.BufferSize;
        Log($"Sending PTY history ({bufferSize} bytes)");

        await SendAsync(new PtyHistoryMessage
        {
            Data = history,
            BufferSize = bufferSize,
            RequestId = requestId
        }, ct);
    }

    private async Task EnsureProxyStartedAsync()
    {
        if (_proxy is not null && _proxy.IsRunning) return;
        _proxy = new AgentSdkProxy(Log);
        _proxy.OnSessionTimeout(sessionId =>
        {
            if (_claudeSdkProcesses.TryGetValue(sessionId, out var process))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        Log($"Killed inactive Claude CLI process for session {sessionId}");
                    }
                }
                catch { }
                _claudeSdkProcesses.TryRemove(sessionId, out _);
            }
        });
        await _proxy.StartAsync();
    }

    /// <summary>
    /// After reconnecting to backend, report any Claude sessions still alive
    /// so the backend can recreate them and the proxy can reconnect.
    /// </summary>
    private async Task ReportAliveSessionsAsync(CancellationToken ct)
    {
        if (_proxy is null) return;

        var activeSessions = _proxy.GetActiveSessions();
        if (activeSessions.Count == 0) return;

        Log($"Reporting {activeSessions.Count} active Claude session(s) to backend...");

        var sessionsPayload = activeSessions.Select(s => new Dictionary<string, string?>
        {
            ["sessionId"] = s.SessionId,
            ["token"] = s.Token,
            ["cliSessionId"] = s.CliSessionId
        }).ToList();

        await SendAsync(new AgentSdkSessionsAliveMessage { Sessions = sessionsPayload }, ct);

        // Give backend a moment to recreate sessions before proxy reconnects
        await Task.Delay(1000, ct);
        await _proxy.ReconnectAllToBackendAsync(ct);
    }

    private async Task HandleAgentSdkSpawnAsync(IncomingMessage message, CancellationToken ct)
    {
        var sessionId = message.SessionId;
        var sdkUrl = message.SdkUrl;
        var provider = message.Provider ?? "claude";

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(sdkUrl))
        {
            Log("Invalid agent-sdk.spawn message: missing sessionId or sdkUrl");
            return;
        }

        if (provider == "codex")
        {
            await HandleCodexSpawnAsync(message, sessionId, sdkUrl, ct);
            return;
        }

        if (provider == "gemini")
        {
            await HandleGeminiSpawnAsync(message, sessionId, sdkUrl, ct);
            return;
        }

        var resumeCliSessionId = message.ResumeCliSessionId;
        Log($"Spawning Claude CLI for session {sessionId} with SDK URL: {MaskUrl(sdkUrl)}{(resumeCliSessionId != null ? $" (resume: {resumeCliSessionId})" : "")}");

        try
        {
            var model = message.Model ?? "claude-sonnet-4-20250514";
            var cwd = message.WorkingDirectory ?? _workingDirectory;

            // Map Side Hub permission modes to valid CLI permission modes.
            // CLI accepts: acceptEdits, bypassPermissions, default, dontAsk, plan
            // Side Hub uses 'pipeline', 'auto', 'safe' as custom modes handled server-side.
            var rawPermissionMode = message.PermissionMode ?? "default";
            var permissionMode = rawPermissionMode switch
            {
                "pipeline" => "bypassPermissions",
                "auto" => "bypassPermissions",
                "safe" => "default",
                _ => rawPermissionMode
            };

            // Start local WebSocket proxy so CLI connects to agent (stable)
            // instead of directly to backend (drops on deploy).
            await EnsureProxyStartedAsync();

            // Read token from dedicated field, fallback to URL parsing for backward compat
            var token = message.SdkToken ?? "";
            if (string.IsNullOrEmpty(token))
            {
                var uriObj = new Uri(sdkUrl);
                token = System.Web.HttpUtility.ParseQueryString(uriObj.Query)["token"] ?? "";
            }

            _proxy!.RegisterSession(sessionId, sdkUrl, token, rawPermissionMode);
            var localUrl = _proxy.GetLocalUrl(sessionId);

            Log($"CLI will connect to local proxy: {MaskUrl(localUrl)}");

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "claude",
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };

            // CLI connects to local proxy instead of backend directly
            startInfo.ArgumentList.Add("--sdk-url");
            startInfo.ArgumentList.Add(localUrl);
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add("--print");
            startInfo.ArgumentList.Add("--output-format");
            startInfo.ArgumentList.Add("stream-json");
            startInfo.ArgumentList.Add("--input-format");
            startInfo.ArgumentList.Add("stream-json");
            startInfo.ArgumentList.Add("--verbose");
            startInfo.ArgumentList.Add("--permission-mode");
            startInfo.ArgumentList.Add(permissionMode);

            if (!string.IsNullOrEmpty(resumeCliSessionId))
            {
                startInfo.ArgumentList.Add("--resume");
                startInfo.ArgumentList.Add(resumeCliSessionId);
            }

            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("");

            // Remove CLAUDECODE from inherited environment to prevent
            // "cannot be launched inside another Claude Code session" error.
            startInfo.Environment.Remove("CLAUDECODE");

            var spawnedAt = DateTime.UtcNow;
            var process = System.Diagnostics.Process.Start(startInfo);

            if (process is null)
            {
                Log($"Failed to start Claude CLI process for session {sessionId}");
                _proxy.RemoveSession(sessionId);
                await SendAsync(new AgentSdkSpawnFailedMessage
                {
                    SessionId = sessionId,
                    Error = "Failed to start process"
                }, ct);
                return;
            }

            _claudeSdkProcesses[sessionId] = process;

            // Keep stdin pipe open (don't close it!) - the CLI uses WebSocket for input
            // but closing stdin sends EOF which causes the CLI to exit after the first turn.

            Log($"Claude CLI started for session {sessionId} (PID {process.Id})");
            await SendAsync(new AgentSdkSpawnedMessage
            {
                SessionId = sessionId,
                Pid = process.Id
            }, ct);

            // Monitor stdout, stderr and process exit in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var stdoutTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!process.HasExited)
                            {
                                var line = await process.StandardOutput.ReadLineAsync(ct);
                                if (line is null) break;
                                if (!string.IsNullOrWhiteSpace(line))
                                    Log($"[Claude CLI stdout] {line}");
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch { }
                    }, ct);

                    var stderrTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!process.HasExited)
                            {
                                var line = await process.StandardError.ReadLineAsync(ct);
                                if (line is null) break;
                                if (!string.IsNullOrWhiteSpace(line))
                                    Log($"[Claude CLI stderr] {line}");
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch { }
                    }, ct);

                    await process.WaitForExitAsync(ct);
                    var exitCode = process.ExitCode;
                    var elapsed = DateTime.UtcNow - spawnedAt;
                    Log($"Claude CLI for session {sessionId} exited with code {exitCode} after {elapsed.TotalSeconds:F1}s");
                    _claudeSdkProcesses.TryRemove(sessionId, out _);
                    _proxy?.RemoveSession(sessionId);

                    await Task.WhenAll(stdoutTask, stderrTask);

                    if (elapsed.TotalSeconds < 5 && !string.IsNullOrEmpty(resumeCliSessionId))
                    {
                        Log($"Resume failed for session {sessionId} (CLI exited too quickly)");
                        await SendAsync(new AgentSdkSpawnFailedMessage
                        {
                            SessionId = sessionId,
                            Error = "Resume failed - CLI session may no longer exist"
                        }, ct);
                    }
                    else
                    {
                        await SendAsync(new AgentSdkExitedMessage
                        {
                            SessionId = sessionId,
                            ExitCode = exitCode
                        }, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    _claudeSdkProcesses.TryRemove(sessionId, out _);
                    _proxy?.RemoveSession(sessionId);
                }
                catch (Exception ex)
                {
                    Log($"Error monitoring Claude CLI process: {ex.Message}");
                    _claudeSdkProcesses.TryRemove(sessionId, out _);
                    _proxy?.RemoveSession(sessionId);
                }
            }, ct);
        }
        catch (Exception ex)
        {
            Log($"Failed to spawn Claude CLI for session {sessionId}: {ex.Message}");
            _proxy?.RemoveSession(sessionId);
            await SendAsync(new AgentSdkSpawnFailedMessage
            {
                SessionId = sessionId,
                Error = ex.Message
            }, ct);
        }
    }

    private async Task HandleCodexSpawnAsync(IncomingMessage message, string sessionId, string sdkUrl, CancellationToken ct)
    {
        Log($"Spawning Codex CLI for session {sessionId}");

        try
        {
            var model = message.Model ?? "gpt-5.3-codex";
            var cwd = message.WorkingDirectory ?? _workingDirectory;
            var rawPermissionMode = message.PermissionMode ?? "default";

            await EnsureProxyStartedAsync();

            // Read token from dedicated field, fallback to URL parsing for backward compat
            var token = message.SdkToken ?? "";
            if (string.IsNullOrEmpty(token))
            {
                var uriObj = new Uri(sdkUrl);
                token = System.Web.HttpUtility.ParseQueryString(uriObj.Query)["token"] ?? "";
            }

            var bridge = new CodexBridge(sessionId, model, cwd, rawPermissionMode, Log);

            // Register virtual session so bridge can send messages to backend via proxy
            _proxy!.RegisterVirtualSession(
                sessionId, sdkUrl, token, rawPermissionMode,
                (msg, cancelToken) => bridge.HandleBackendMessageAsync(msg, cancelToken));

            // Start bridge with callback to send NDJSON to backend
            await bridge.StartAsync(
                (msg, cancelToken) => _proxy.SendVirtualMessageToBackendAsync(sessionId, msg, cancelToken),
                ct);

            if (bridge.Pid is null)
            {
                _proxy.RemoveSession(sessionId);
                await SendAsync(new AgentSdkSpawnFailedMessage
                {
                    SessionId = sessionId,
                    Error = "Failed to start codex process"
                }, ct);
                return;
            }

            _codexBridges[sessionId] = bridge;

            Log($"Codex CLI started for session {sessionId} (PID {bridge.Pid})");
            await SendAsync(new AgentSdkSpawnedMessage
            {
                SessionId = sessionId,
                Pid = bridge.Pid.Value
            }, ct);

            // Monitor process exit in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await bridge.WaitForExitAsync(ct);
                    var exitCode = bridge.ExitCode;
                    Log($"Codex CLI for session {sessionId} exited with code {exitCode}");

                    _codexBridges.TryRemove(sessionId, out _);
                    _proxy?.RemoveSession(sessionId);

                    await SendAsync(new AgentSdkExitedMessage
                    {
                        SessionId = sessionId,
                        ExitCode = exitCode
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                    await bridge.DisposeAsync();
                    _codexBridges.TryRemove(sessionId, out _);
                    _proxy?.RemoveSession(sessionId);
                }
                catch (Exception ex)
                {
                    Log($"Error monitoring Codex process: {ex.Message}");
                    _codexBridges.TryRemove(sessionId, out _);
                    _proxy?.RemoveSession(sessionId);
                }
            }, ct);
        }
        catch (Exception ex)
        {
            Log($"Failed to spawn Codex CLI for session {sessionId}: {ex.Message}");
            _proxy?.RemoveSession(sessionId);
            await SendAsync(new AgentSdkSpawnFailedMessage
            {
                SessionId = sessionId,
                Error = ex.Message
            }, ct);
        }
    }

    private async Task HandleGeminiSpawnAsync(IncomingMessage message, string sessionId, string sdkUrl, CancellationToken ct)
    {
        var resumeCliSessionId = message.ResumeCliSessionId;
        Log($"Spawning Gemini CLI for session {sessionId}{(resumeCliSessionId != null ? $" (resume: {resumeCliSessionId})" : "")}");

        try
        {
            var model = message.Model ?? "gemini-2.5-pro";
            var cwd = message.WorkingDirectory ?? _workingDirectory;
            var rawPermissionMode = message.PermissionMode ?? "default";

            await EnsureProxyStartedAsync();

            // Read token from dedicated field, fallback to URL parsing for backward compat
            var token = message.SdkToken ?? "";
            if (string.IsNullOrEmpty(token))
            {
                var uriObj = new Uri(sdkUrl);
                token = System.Web.HttpUtility.ParseQueryString(uriObj.Query)["token"] ?? "";
            }

            var bridge = new GeminiBridge(sessionId, model, cwd, rawPermissionMode, Log, resumeCliSessionId);

            // Register virtual session so bridge can send messages to backend via proxy
            _proxy!.RegisterVirtualSession(
                sessionId, sdkUrl, token, rawPermissionMode,
                (msg, cancelToken) => bridge.HandleBackendMessageAsync(msg, cancelToken));

            // Start bridge with callback to send NDJSON to backend
            await bridge.StartAsync(
                (msg, cancelToken) => _proxy.SendVirtualMessageToBackendAsync(sessionId, msg, cancelToken),
                ct);

            _geminiBridges[sessionId] = bridge;

            Log($"Gemini CLI ready for session {sessionId}");
            await SendAsync(new AgentSdkSpawnedMessage
            {
                SessionId = sessionId,
                Pid = bridge.Pid ?? 0
            }, ct);

            // Monitor bridge lifecycle in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await bridge.WaitForExitAsync(ct);
                    Log($"Gemini bridge for session {sessionId} stopped");

                    _geminiBridges.TryRemove(sessionId, out _);
                    _proxy?.RemoveSession(sessionId);

                    await SendAsync(new AgentSdkExitedMessage
                    {
                        SessionId = sessionId,
                        ExitCode = bridge.ExitCode
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                    await bridge.DisposeAsync();
                    _geminiBridges.TryRemove(sessionId, out _);
                    _proxy?.RemoveSession(sessionId);
                }
                catch (Exception ex)
                {
                    Log($"Error monitoring Gemini bridge: {ex.Message}");
                    _geminiBridges.TryRemove(sessionId, out _);
                    _proxy?.RemoveSession(sessionId);
                }
            }, ct);
        }
        catch (Exception ex)
        {
            Log($"Failed to spawn Gemini CLI for session {sessionId}: {ex.Message}");
            _proxy?.RemoveSession(sessionId);
            await SendAsync(new AgentSdkSpawnFailedMessage
            {
                SessionId = sessionId,
                Error = ex.Message
            }, ct);
        }
    }

    private async Task HandleAgentSdkStopAsync(IncomingMessage message)
    {
        var sessionId = message.SessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            Log("Invalid agent-sdk.stop message: missing sessionId");
            return;
        }

        Log($"Received stop request for session {sessionId}");

        if (_claudeSdkProcesses.TryGetValue(sessionId, out var process))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    Log($"Killed Claude CLI process for session {sessionId} (stop requested)");
                }
            }
            catch (Exception ex)
            {
                Log($"Error killing Claude CLI for session {sessionId}: {ex.Message}");
            }
            _claudeSdkProcesses.TryRemove(sessionId, out _);
        }

        if (_codexBridges.TryGetValue(sessionId, out var bridge))
        {
            try
            {
                await bridge.DisposeAsync();
                Log($"Stopped Codex bridge for session {sessionId}");
            }
            catch (Exception ex)
            {
                Log($"Error stopping Codex bridge for session {sessionId}: {ex.Message}");
            }
            _codexBridges.TryRemove(sessionId, out _);
        }

        if (_geminiBridges.TryGetValue(sessionId, out var geminiBridge))
        {
            try
            {
                await geminiBridge.DisposeAsync();
                Log($"Stopped Gemini bridge for session {sessionId}");
            }
            catch (Exception ex)
            {
                Log($"Error stopping Gemini bridge for session {sessionId}: {ex.Message}");
            }
            _geminiBridges.TryRemove(sessionId, out _);
        }

        _proxy?.RemoveSession(sessionId);
    }

    private static int CalculateReconnectDelay(int attempts)
    {
        var delay = (int)(MinReconnectDelayMs * Math.Pow(BackoffMultiplier, attempts));
        return Math.Min(delay, MaxReconnectDelayMs);
    }

    public async ValueTask DisposeAsync()
    {
        StopHeartbeat();

        // Dispose proxy first (closes local WebSocket server and backend connections)
        if (_proxy != null)
        {
            await _proxy.DisposeAsync();
            _proxy = null;
        }

        // Kill any running Claude SDK processes
        foreach (var (sessionId, process) in _claudeSdkProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    Log($"Killed Claude CLI process for session {sessionId}");
                }
            }
            catch
            {
                // Ignore kill errors
            }
        }
        _claudeSdkProcesses.Clear();

        // Dispose any running Codex bridges
        foreach (var (sessionId, bridge) in _codexBridges)
        {
            try
            {
                await bridge.DisposeAsync();
                Log($"Disposed Codex bridge for session {sessionId}");
            }
            catch { }
        }
        _codexBridges.Clear();

        // Dispose any running Gemini bridges
        foreach (var (sessionId2, geminiBridge) in _geminiBridges)
        {
            try
            {
                await geminiBridge.DisposeAsync();
                Log($"Disposed Gemini bridge for session {sessionId2}");
            }
            catch { }
        }
        _geminiBridges.Clear();

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
