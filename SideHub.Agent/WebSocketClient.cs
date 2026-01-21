using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SideHub.Agent.Models;

namespace SideHub.Agent;

public class WebSocketClient : IAsyncDisposable
{
    private readonly AgentConfig _config;
    private readonly CommandExecutor _executor;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _displayName;
    private ClientWebSocket? _ws;
    private Timer? _heartbeatTimer;

    private const int MinReconnectDelayMs = 1000;
    private const int MaxReconnectDelayMs = 30000;
    private const double BackoffMultiplier = 1.5;
    private const int HeartbeatIntervalMs = 30000;

    public WebSocketClient(AgentConfig config, CommandExecutor executor, string? displayName = null)
    {
        _config = config;
        _executor = executor;
        _displayName = displayName ?? config.GetDisplayName();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    private void Log(string message) => Console.WriteLine($"[{_displayName}] {message}");

    public async Task RunAsync(CancellationToken ct)
    {
        var reconnectAttempts = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.AgentToken}");

                Log($"Connecting to {_config.SidehubUrl}...");
                await _ws.ConnectAsync(new Uri(_config.SidehubUrl!), ct);
                Log("Connected");

                reconnectAttempts = 0;

                await SendConnectedMessageAsync(ct);
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

                var delay = CalculateReconnectDelay(reconnectAttempts);
                reconnectAttempts++;

                Log($"Reconnecting in {delay}ms...");
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
        var message = new AgentConnectedMessage
        {
            AgentId = _config.AgentId!,
            WorkspaceId = _config.WorkspaceId!,
            RepositoryId = _config.RepositoryId!,
            Capabilities = _config.Capabilities!
        };
        await SendAsync(message, ct);
    }

    private void StartHeartbeat(CancellationToken ct)
    {
        _heartbeatTimer = new Timer(
            async _ =>
            {
                if (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await SendAsync(new AgentHeartbeatMessage(), ct);
                    }
                    catch
                    {
                        // Ignore heartbeat errors
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

        Log($"Executing: {message.Command}");

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

    private async Task SendAsync<T>(T message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static int CalculateReconnectDelay(int attempts)
    {
        var delay = (int)(MinReconnectDelayMs * Math.Pow(BackoffMultiplier, attempts));
        return Math.Min(delay, MaxReconnectDelayMs);
    }

    public async ValueTask DisposeAsync()
    {
        StopHeartbeat();
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
