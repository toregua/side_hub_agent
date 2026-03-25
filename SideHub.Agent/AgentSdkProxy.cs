using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace SideHub.Agent;

/// <summary>
/// Local WebSocket proxy for Claude SDK sessions.
/// CLI connects locally (stable), agent relays to backend (reconnectable).
/// When backend drops, CLI keeps running and messages are buffered.
/// </summary>
public class AgentSdkProxy : IAsyncDisposable
{
    private HttpListener? _listener;
    private int _port;
    private readonly ConcurrentDictionary<string, ProxySession> _sessions = new();
    private readonly Action<string> _log;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoopTask;
    private const int MaxBufferedMessages = 1000;
    private const int BufferMessageTtlMs = 120_000; // 2 minutes
    private const int BackendReconnectDelayMs = 3000;
    private const int CliKeepAliveIntervalMs = 10000;
    private const int InactivityTimeoutMs = 300_000; // 5 minutes
    private const int InactivityCheckIntervalMs = 60_000; // Check every minute
    private const int MaxWebSocketMessageSize = 50 * 1024 * 1024; // 50 MB

    private Action<string>? _onSessionTimeout;

    public AgentSdkProxy(Action<string> log)
    {
        _log = log;
    }

    private static string GenerateConnectionToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    private static string StripTokenFromUrl(string url)
    {
        var uriBuilder = new UriBuilder(url);
        var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
        query.Remove("token");
        uriBuilder.Query = query.ToString();
        return uriBuilder.Uri.ToString();
    }

    /// <summary>
    /// Called when a session is reaped due to inactivity. The callback receives the sessionId.
    /// </summary>
    public void OnSessionTimeout(Action<string> callback) => _onSessionTimeout = callback;

    public int Port => _port;
    public bool IsRunning => _listener?.IsListening == true;

    public IReadOnlyCollection<ActiveSessionInfo> GetActiveSessions()
    {
        return _sessions.Values
            .Where(s => s.CliConnected)
            .Select(s => new ActiveSessionInfo(s.SessionId, s.Token, s.CliSessionId))
            .ToList();
    }

    public async Task StartAsync()
    {
        // Find an available port by binding to port 0
        using var tempSocket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tempSocket.Start();
        _port = ((IPEndPoint)tempSocket.LocalEndpoint).Port;
        tempSocket.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();

        _listenerCts = new CancellationTokenSource();
        _acceptLoopTask = AcceptLoopAsync(_listenerCts.Token);

        // Start inactivity reaper
        _ = Task.Run(() => InactivityReaperLoopAsync(_listenerCts.Token), _listenerCts.Token);

        _log($"[Proxy] Local WebSocket server started on port {_port}");
    }

    public string GetLocalUrl(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return $"ws://127.0.0.1:{_port}/ws/agent/{sessionId}?connectionToken={session.ConnectionToken}";
        return $"ws://127.0.0.1:{_port}/ws/agent/{sessionId}";
    }

    public void RegisterSession(string sessionId, string backendUrl, string token, string permissionMode)
    {
        RemoveSession(sessionId);
        var connectionToken = GenerateConnectionToken();
        var session = new ProxySession
        {
            SessionId = sessionId,
            BackendUrl = StripTokenFromUrl(backendUrl),
            Token = token,
            PermissionMode = permissionMode,
            ConnectionToken = connectionToken
        };
        _sessions[sessionId] = session;
        _log($"[Proxy] Session {sessionId} registered (mode={permissionMode})");
    }

    public void RegisterLocalTerminalSession(string sessionId, string permissionMode = "default")
    {
        RemoveSession(sessionId);
        var connectionToken = GenerateConnectionToken();
        var session = new ProxySession
        {
            SessionId = sessionId,
            BackendUrl = "",
            Token = "",
            PermissionMode = permissionMode,
            ConnectionToken = connectionToken,
            LocalOnly = true
        };
        _sessions[sessionId] = session;
        _log($"[Proxy] Local terminal session {sessionId} registered");
    }

    /// <summary>
    /// Called when the agent's main WebSocket reconnects to the backend.
    /// The auto-reconnect loop in ConnectToBackendAsync handles reconnection,
    /// so this just logs for visibility.
    /// </summary>
    public Task ReconnectAllToBackendAsync(CancellationToken ct)
    {
        var activeSessions = _sessions.Values.Where(s => s.CliConnected).ToList();
        if (activeSessions.Count > 0)
            _log($"[Proxy] {activeSessions.Count} active session(s) will auto-reconnect to backend");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Register a virtual CLI connection for Codex sessions.
    /// Instead of a real CLI WebSocket, the CodexBridge provides callbacks for message exchange.
    /// </summary>
    public void RegisterVirtualSession(
        string sessionId,
        string backendUrl,
        string token,
        string permissionMode,
        Func<string, CancellationToken, Task> onBackendMessage)
    {
        var connectionToken = GenerateConnectionToken();
        var session = new ProxySession
        {
            SessionId = sessionId,
            BackendUrl = StripTokenFromUrl(backendUrl),
            Token = token,
            PermissionMode = permissionMode,
            ConnectionToken = connectionToken,
            CliConnected = true,           // Virtual CLI is always "connected"
            IsVirtual = true,
            VirtualOnBackendMessage = onBackendMessage
        };
        _sessions[sessionId] = session;
        _log($"[Proxy] Virtual session {sessionId} registered (mode={permissionMode})");

        // Connect to backend immediately (no CLI WebSocket to wait for)
        _ = Task.Run(() => ConnectToBackendAsync(session, _listenerCts?.Token ?? CancellationToken.None));
    }

    /// <summary>
    /// Send an NDJSON message to the backend for a virtual session (called by CodexBridge).
    /// </summary>
    public async Task SendVirtualMessageToBackendAsync(string sessionId, string message, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        if (session.BackendConnected && session.BackendSocket?.State == WebSocketState.Open)
        {
            // Cache system/init for reconnection
            CacheSystemInit(session, message);
            await SendToBackendAsync(session, message, ct);
        }
        else
        {
            CacheSystemInit(session, message);
            BufferMessage(session, message);
        }
    }

    public void RemoveSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Dispose();
            _log($"[Proxy] Session {sessionId} removed");
        }
    }

    public bool IsCliConnected(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) && session.CliConnected;

    public string? GetCliSessionId(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.CliSessionId : null;

    public async Task<bool> SendToCliSessionAsync(string sessionId, string message, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.CliSocket?.State != WebSocketState.Open)
            return false;

        _log($"[Proxy] Sending message to CLI session {sessionId} (chars={message.Length}, cliSessionId={session.CliSessionId ?? "<none>"})");
        await SendToCliAsync(session, message, ct);
        return true;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                // Extract sessionId from path: /ws/agent/{sessionId}
                var path = context.Request.Url?.AbsolutePath ?? "";
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length != 3 || segments[0] != "ws" || segments[1] != "agent")
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                var sessionId = segments[2];
                if (!_sessions.TryGetValue(sessionId, out var session))
                {
                    _log($"[Proxy] CLI connected for unknown session {sessionId}");
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                // Verify connection token to prevent unauthorized local connections
                var queryToken = context.Request.QueryString["connectionToken"];
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(queryToken ?? ""),
                        Encoding.UTF8.GetBytes(session.ConnectionToken)))
                {
                    _log($"[Proxy] Rejected CLI connection for session {sessionId}: invalid connectionToken");
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);
                session.CliSocket = wsContext.WebSocket;
                session.CliConnected = true;

                _log($"[Proxy] CLI connected for session {sessionId} (localOnly={session.LocalOnly})");

                // Start CLI receive loop and keepalive in background
                _ = Task.Run(() => CliReceiveLoopAsync(session, ct), ct);
                _ = Task.Run(() => CliKeepAliveLoopAsync(session, ct), ct);

                // Connect to backend for managed SDK sessions only.
                if (!session.LocalOnly)
                    _ = Task.Run(() => ConnectToBackendAsync(session, ct), ct);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"[Proxy] Accept error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Receives messages from CLI and forwards to backend (or buffers if disconnected).
    /// </summary>
    private async Task CliReceiveLoopAsync(ProxySession session, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new List<byte>();

        try
        {
            while (session.CliSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await session.CliSocket.ReceiveAsync(buffer, ct);
                }
                catch (WebSocketException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.AddRange(buffer.Take(result.Count));

                    if (messageBuffer.Count > MaxWebSocketMessageSize)
                    {
                        _log($"[Proxy] CLI message exceeded {MaxWebSocketMessageSize / (1024 * 1024)}MB limit for session {session.SessionId}, closing");
                        messageBuffer.Clear();
                        if (session.CliSocket?.State == WebSocketState.Open)
                            await session.CliSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", ct);
                        break;
                    }

                    if (result.EndOfMessage)
                    {
                        var rawMessage = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();

                        if (session.LocalOnly)
                        {
                            var preview = rawMessage.Replace('\n', ' ').Replace('\r', ' ');
                            if (preview.Length > 240) preview = preview[..240] + "...";
                            _log($"[Proxy] Local CLI message for {session.SessionId}: {preview}");
                        }

                        // Cache system/init message for replay on reconnect
                        CacheSystemInit(session, rawMessage);

                        // Local-only terminal sessions don't relay upstream.
                        if (session.LocalOnly)
                        {
                            session.LastActivity = DateTime.UtcNow;
                        }
                        else if (session.BackendConnected && session.BackendSocket?.State == WebSocketState.Open)
                        {
                            await SendToBackendAsync(session, rawMessage, ct);
                        }
                        else
                        {
                            BufferMessage(session, rawMessage);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[Proxy] CLI receive error for {session.SessionId}: {ex.Message}");
        }
        finally
        {
            session.CliConnected = false;
            _log($"[Proxy] CLI disconnected for session {session.SessionId}");
        }
    }

    /// <summary>
    /// Sends keep_alive to CLI to prevent timeout on the local connection.
    /// </summary>
    private async Task CliKeepAliveLoopAsync(ProxySession session, CancellationToken ct)
    {
        try
        {
            while (session.CliConnected && !ct.IsCancellationRequested)
            {
                await Task.Delay(CliKeepAliveIntervalMs, ct);

                if (session.CliSocket?.State == WebSocketState.Open)
                {
                    await SendToCliAsync(session, "{\"type\":\"keep_alive\"}\n", ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[Proxy] CLI keepalive error for {session.SessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Periodically checks for inactive sessions and reaps them.
    /// </summary>
    private async Task InactivityReaperLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(InactivityCheckIntervalMs, ct);

                var now = DateTime.UtcNow;
                var toReap = _sessions.Values
                    .Where(s => (now - s.LastActivity).TotalMilliseconds > InactivityTimeoutMs)
                    .Select(s => s.SessionId)
                    .ToList();

                foreach (var sessionId in toReap)
                {
                    _log($"[Proxy] Session {sessionId} inactive for >{InactivityTimeoutMs / 1000}s, reaping");
                    RemoveSession(sessionId);
                    _onSessionTimeout?.Invoke(sessionId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Connects the proxy to the backend WebSocket and starts relaying backend → CLI.
    /// Automatically reconnects with backoff when backend drops.
    /// </summary>
    private async Task ConnectToBackendAsync(ProxySession session, CancellationToken ct)
    {
        var attempt = 0;
        var consecutive409Count = 0;
        const int max409Retries = 5;

        while (session.CliConnected && !ct.IsCancellationRequested)
        {
            // Disconnect any existing backend socket
            if (session.BackendSocket != null)
            {
                try
                {
                    if (session.BackendSocket.State == WebSocketState.Open)
                        await session.BackendSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", ct);
                }
                catch { }
                session.BackendSocket.Dispose();
                session.BackendSocket = null;
                session.BackendConnected = false;
            }

            try
            {
                var ws = new ClientWebSocket();
                if (!string.IsNullOrEmpty(session.Token))
                    ws.Options.SetRequestHeader("X-Session-Token", session.Token);
                var uri = new Uri(session.BackendUrl);

                _log($"[Proxy] Connecting to backend for session {session.SessionId}...{(attempt > 0 ? $" (attempt {attempt + 1})" : "")}");
                await ws.ConnectAsync(uri, ct);

                session.BackendSocket = ws;
                session.BackendConnected = true;
                attempt = 0;
                consecutive409Count = 0; // Reset on successful connection
                _log($"[Proxy] Backend connected for session {session.SessionId}");

                // Replay system/init if we have it cached (reconnection scenario)
                if (session.SystemInitMessage != null && session.HasBeenConnectedBefore)
                {
                    _log($"[Proxy] Replaying system/init for session {session.SessionId}");
                    await SendToBackendAsync(session, session.SystemInitMessage, ct);
                }

                session.HasBeenConnectedBefore = true;

                // Replay buffered messages
                await ReplayBufferedMessagesAsync(session, ct);

                // Start backend receive loop (backend → CLI relay)
                await BackendReceiveLoopAsync(session, ct);

                // BackendReceiveLoopAsync returned — backend disconnected, loop to reconnect
            }
            catch (OperationCanceledException) { return; }
            catch (WebSocketException ex) when (ex.Message.Contains("404"))
            {
                // Session no longer exists on backend (e.g. after redeploy) — stop retrying
                _log($"[Proxy] Backend returned 404 for session {session.SessionId}, session lost — giving up");
                session.BackendConnected = false;
                RemoveSession(session.SessionId);
                _onSessionTimeout?.Invoke(session.SessionId);
                return;
            }
            catch (WebSocketException ex) when (ex.Message.Contains("409"))
            {
                consecutive409Count++;
                _log($"[Proxy] Backend returned 409 for session {session.SessionId} ({consecutive409Count}/{max409Retries})");
                session.BackendConnected = false;

                if (consecutive409Count >= max409Retries)
                {
                    _log($"[Proxy] Session {session.SessionId} got {max409Retries} consecutive 409s — abandoning session");
                    RemoveSession(session.SessionId);
                    _onSessionTimeout?.Invoke(session.SessionId);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log($"[Proxy] Backend connection failed for {session.SessionId}: {ex.Message}");
                session.BackendConnected = false;
            }

            // Backoff before reconnecting
            if (session.CliConnected && !ct.IsCancellationRequested)
            {
                var delay = Math.Min(BackendReconnectDelayMs * (1 << Math.Min(attempt, 5)), 60_000);
                attempt++;
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Receives messages from backend and forwards to CLI.
    /// </summary>
    private async Task BackendReceiveLoopAsync(ProxySession session, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new List<byte>();

        try
        {
            while (session.BackendSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await session.BackendSocket.ReceiveAsync(buffer, ct);
                }
                catch (WebSocketException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.AddRange(buffer.Take(result.Count));

                    if (messageBuffer.Count > MaxWebSocketMessageSize)
                    {
                        _log($"[Proxy] Backend message exceeded {MaxWebSocketMessageSize / (1024 * 1024)}MB limit for session {session.SessionId}, closing");
                        messageBuffer.Clear();
                        if (session.BackendSocket?.State == WebSocketState.Open)
                            await session.BackendSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", ct);
                        break;
                    }

                    if (result.EndOfMessage)
                    {
                        var rawMessage = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();

                        // Forward backend message to CLI (or virtual bridge)
                        if (session.IsVirtual && session.VirtualOnBackendMessage is not null)
                        {
                            await session.VirtualOnBackendMessage(rawMessage, ct);
                        }
                        else if (session.CliSocket?.State == WebSocketState.Open)
                        {
                            await SendToCliAsync(session, rawMessage, ct);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[Proxy] Backend receive error for {session.SessionId}: {ex.Message}");
        }
        finally
        {
            session.BackendConnected = false;
            _log($"[Proxy] Backend disconnected for session {session.SessionId} (CLI still running: {session.CliConnected})");
        }
    }

    private async Task ReplayBufferedMessagesAsync(ProxySession session, CancellationToken ct)
    {
        var count = 0;
        var expired = 0;
        var now = DateTime.UtcNow;

        while (session.BufferedMessages.TryDequeue(out var entry))
        {
            if (session.BackendSocket?.State != WebSocketState.Open) break;

            if ((now - entry.BufferedAt).TotalMilliseconds > BufferMessageTtlMs)
            {
                expired++;
                continue;
            }

            await SendToBackendAsync(session, entry.Message, ct);
            count++;
        }

        if (expired > 0)
            _log($"[Proxy] Dropped {expired} expired buffered message(s) for session {session.SessionId}");
        if (count > 0)
            _log($"[Proxy] Replayed {count} buffered message(s) for session {session.SessionId}");
    }

    private void CacheSystemInit(ProxySession session, string rawMessage)
    {
        // NDJSON: check each line for system/init
        foreach (var line in rawMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("\"type\"") && trimmed.Contains("\"system\"") && trimmed.Contains("\"init\""))
            {
                session.SystemInitMessage = rawMessage;

                // Extract cliSessionId from init message
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                    if (doc.RootElement.TryGetProperty("session_id", out var sid))
                        session.CliSessionId = sid.GetString();
                }
                catch { }

                _log($"[Proxy] Cached system/init for session {session.SessionId} (cliSessionId={session.CliSessionId})");
                break;
            }
        }
    }

    private void BufferMessage(ProxySession session, string rawMessage)
    {
        if (session.BufferedMessages.Count >= MaxBufferedMessages)
        {
            _log($"[Proxy] Buffer full for {session.SessionId}, dropping oldest message");
            session.BufferedMessages.TryDequeue(out _);
        }
        session.BufferedMessages.Enqueue((rawMessage, DateTime.UtcNow));
    }

    private static async Task SendToBackendAsync(ProxySession session, string message, CancellationToken ct)
    {
        if (session.BackendSocket?.State != WebSocketState.Open) return;

        await session.BackendSendLock.WaitAsync(ct);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await session.BackendSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            session.LastActivity = DateTime.UtcNow;
        }
        finally
        {
            session.BackendSendLock.Release();
        }
    }

    private static async Task SendToCliAsync(ProxySession session, string message, CancellationToken ct)
    {
        if (session.CliSocket?.State != WebSocketState.Open) return;

        await session.CliSendLock.WaitAsync(ct);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await session.CliSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            session.LastActivity = DateTime.UtcNow;
        }
        finally
        {
            session.CliSendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listenerCts?.Cancel();

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();

        if (_listener != null)
        {
            _listener.Stop();
            _listener.Close();
        }

        if (_acceptLoopTask != null)
        {
            try { await _acceptLoopTask; } catch { }
        }

        _listenerCts?.Dispose();
        _log("[Proxy] Disposed");
    }

    internal class ProxySession : IDisposable
    {
        public required string SessionId { get; init; }
        public required string BackendUrl { get; set; }
        public required string Token { get; init; }
        public required string PermissionMode { get; init; }
        public required string ConnectionToken { get; init; }

        public WebSocket? CliSocket { get; set; }
        public ClientWebSocket? BackendSocket { get; set; }
        public bool CliConnected { get; set; }
        public bool BackendConnected { get; set; }
        public bool HasBeenConnectedBefore { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public bool LocalOnly { get; init; }

        // Virtual session support (Codex bridge — no real CLI WebSocket)
        public bool IsVirtual { get; init; }
        public Func<string, CancellationToken, Task>? VirtualOnBackendMessage { get; init; }

        public string? SystemInitMessage { get; set; }
        public string? CliSessionId { get; set; }
        public ConcurrentQueue<(string Message, DateTime BufferedAt)> BufferedMessages { get; } = new();

        public SemaphoreSlim CliSendLock { get; } = new(1, 1);
        public SemaphoreSlim BackendSendLock { get; } = new(1, 1);

        public void Dispose()
        {
            try
            {
                if (CliSocket?.State == WebSocketState.Open)
                    CliSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Proxy disposing", CancellationToken.None)
                        .GetAwaiter().GetResult();
            }
            catch { }

            try
            {
                if (BackendSocket?.State == WebSocketState.Open)
                    BackendSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Proxy disposing", CancellationToken.None)
                        .GetAwaiter().GetResult();
                BackendSocket?.Dispose();
            }
            catch { }

            CliSendLock.Dispose();
            BackendSendLock.Dispose();
        }
    }
}

public record ActiveSessionInfo(string SessionId, string Token, string? CliSessionId);
