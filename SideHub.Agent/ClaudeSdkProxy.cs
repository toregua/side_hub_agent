using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace SideHub.Agent;

/// <summary>
/// Local WebSocket proxy for Claude SDK sessions.
/// CLI connects locally (stable), agent relays to backend (reconnectable).
/// When backend drops, CLI keeps running and messages are buffered.
/// </summary>
public class ClaudeSdkProxy : IAsyncDisposable
{
    private HttpListener? _listener;
    private int _port;
    private readonly ConcurrentDictionary<string, ProxySession> _sessions = new();
    private readonly Action<string> _log;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoopTask;
    private const int MaxBufferedMessages = 1000;
    private const int BackendReconnectDelayMs = 3000;
    private const int CliKeepAliveIntervalMs = 10000;

    public ClaudeSdkProxy(Action<string> log)
    {
        _log = log;
    }

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

        _log($"[Proxy] Local WebSocket server started on port {_port}");
    }

    public string GetLocalUrl(string sessionId) => $"ws://127.0.0.1:{_port}/ws/claude/{sessionId}";

    public void RegisterSession(string sessionId, string backendUrl, string token, string permissionMode)
    {
        var session = new ProxySession
        {
            SessionId = sessionId,
            BackendUrl = backendUrl,
            Token = token,
            PermissionMode = permissionMode
        };
        _sessions[sessionId] = session;
        _log($"[Proxy] Session {sessionId} registered (mode={permissionMode})");
    }

    /// <summary>
    /// Called when the agent's main WebSocket reconnects to the backend.
    /// Reconnects all active proxy sessions to the backend.
    /// </summary>
    public async Task ReconnectAllToBackendAsync(CancellationToken ct)
    {
        var activeSessions = _sessions.Values.Where(s => s.CliConnected).ToList();
        if (activeSessions.Count == 0) return;

        _log($"[Proxy] Reconnecting {activeSessions.Count} active session(s) to backend...");

        var tasks = activeSessions.Select(s => ConnectToBackendAsync(s, ct));
        await Task.WhenAll(tasks);
    }

    public void RemoveSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Dispose();
            _log($"[Proxy] Session {sessionId} removed");
        }
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

                // Extract sessionId from path: /ws/claude/{sessionId}
                var path = context.Request.Url?.AbsolutePath ?? "";
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length != 3 || segments[0] != "ws" || segments[1] != "claude")
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

                var wsContext = await context.AcceptWebSocketAsync(null);
                session.CliSocket = wsContext.WebSocket;
                session.CliConnected = true;

                _log($"[Proxy] CLI connected for session {sessionId}");

                // Start CLI receive loop and keepalive in background
                _ = Task.Run(() => CliReceiveLoopAsync(session, ct), ct);
                _ = Task.Run(() => CliKeepAliveLoopAsync(session, ct), ct);

                // Connect to backend
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

                    if (result.EndOfMessage)
                    {
                        var rawMessage = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();

                        // Cache system/init message for replay on reconnect
                        CacheSystemInit(session, rawMessage);

                        // Forward to backend or buffer
                        if (session.BackendConnected && session.BackendSocket?.State == WebSocketState.Open)
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
    /// Connects the proxy to the backend WebSocket and starts relaying backend → CLI.
    /// Handles reconnection with backoff when backend drops.
    /// </summary>
    private async Task ConnectToBackendAsync(ProxySession session, CancellationToken ct)
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
            var uri = new Uri(session.BackendUrl);

            _log($"[Proxy] Connecting to backend for session {session.SessionId}...");
            await ws.ConnectAsync(uri, ct);

            session.BackendSocket = ws;
            session.BackendConnected = true;
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[Proxy] Backend connection failed for {session.SessionId}: {ex.Message}");
            session.BackendConnected = false;
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

                    if (result.EndOfMessage)
                    {
                        var rawMessage = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();

                        // Forward backend message to CLI
                        if (session.CliSocket?.State == WebSocketState.Open)
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
        while (session.BufferedMessages.TryDequeue(out var msg))
        {
            if (session.BackendSocket?.State != WebSocketState.Open) break;
            await SendToBackendAsync(session, msg, ct);
            count++;
        }
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
        session.BufferedMessages.Enqueue(rawMessage);
    }

    private static async Task SendToBackendAsync(ProxySession session, string message, CancellationToken ct)
    {
        if (session.BackendSocket?.State != WebSocketState.Open) return;

        await session.BackendSendLock.WaitAsync(ct);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await session.BackendSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
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

        public WebSocket? CliSocket { get; set; }
        public ClientWebSocket? BackendSocket { get; set; }
        public bool CliConnected { get; set; }
        public bool BackendConnected { get; set; }
        public bool HasBeenConnectedBefore { get; set; }

        public string? SystemInitMessage { get; set; }
        public string? CliSessionId { get; set; }
        public ConcurrentQueue<string> BufferedMessages { get; } = new();

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
