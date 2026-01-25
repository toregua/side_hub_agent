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

    private const int MinReconnectDelayMs = 1000;
    private const int MaxReconnectDelayMs = 30000;
    private const double BackoffMultiplier = 1.5;
    private const int HeartbeatIntervalMs = 30000;

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

    private async Task HandlePtyStartAsync(IncomingMessage message, CancellationToken ct)
    {
        if (_ptyExecutor?.IsRunning == true)
        {
            Log("PTY session already running");
            return;
        }

        var shell = message.Shell ?? "zsh";
        var columns = message.Columns ?? 120;
        var rows = message.Rows ?? 30;

        Log($"Starting PTY session with {shell} ({columns}x{rows})");

        try
        {
            // Create fresh NodePtyExecutor for each session (uses node-pty for correct terminal dimensions)
            _ptyExecutor = new NodePtyExecutor(_workingDirectory);
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

    private static int CalculateReconnectDelay(int attempts)
    {
        var delay = (int)(MinReconnectDelayMs * Math.Pow(BackoffMultiplier, attempts));
        return Math.Min(delay, MaxReconnectDelayMs);
    }

    public async ValueTask DisposeAsync()
    {
        StopHeartbeat();
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
