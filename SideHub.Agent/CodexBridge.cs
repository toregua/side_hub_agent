using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Agent;

/// <summary>
/// Bridge between Codex CLI (JSON-RPC 2.0 over stdin/stdout) and the Side Hub
/// NDJSON protocol (over WebSocket via ClaudeSdkProxy).
///
/// The bridge spawns `codex app-server`, translates messages bidirectionally,
/// and presents itself to the backend as if it were a Claude CLI connecting
/// via WebSocket — so the entire backend infrastructure is reused.
/// </summary>
public class CodexBridge : IAsyncDisposable
{
    private readonly Action<string> _log;
    private readonly string _sessionId;
    private readonly string _model;
    private readonly string _workingDirectory;
    private readonly string _permissionMode;

    private Process? _process;
    private string? _threadId;
    private int _rpcId;
    private bool _disposed;

    // Track pending JSON-RPC requests to correlate responses
    private readonly ConcurrentDictionary<int, string> _pendingRequests = new();

    // Track pending approval request IDs: Codex approvalId -> our requestId
    private readonly ConcurrentDictionary<string, string> _pendingApprovals = new();

    // Callback to send NDJSON messages to the backend (through proxy)
    private Func<string, CancellationToken, Task>? _sendToBackend;
    private CancellationTokenSource? _cts;
    private Task? _stdoutReadTask;
    private Task? _stderrReadTask;
    private Task? _keepAliveTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public CodexBridge(
        string sessionId,
        string model,
        string workingDirectory,
        string permissionMode,
        Action<string> log)
    {
        _sessionId = sessionId;
        _model = model;
        _workingDirectory = workingDirectory;
        _permissionMode = permissionMode;
        _log = log;
    }

    public int? Pid => _process?.Id;
    public bool IsRunning => _process is not null && !_process.HasExited;

    /// <summary>
    /// Start the Codex app-server process and begin translating messages.
    /// </summary>
    public async Task StartAsync(Func<string, CancellationToken, Task> sendToBackend, CancellationToken ct)
    {
        _sendToBackend = sendToBackend;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Map permission mode to Codex sandbox flags
        var (sandbox, approval) = MapPermissionMode(_permissionMode);

        var startInfo = new ProcessStartInfo
        {
            FileName = "codex",
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("app-server");

        // Codex app-server uses stdio by default (not WebSocket)
        // No --listen flag needed — we communicate via stdin/stdout

        // Pass environment
        startInfo.Environment["CODEX_QUIET"] = "1";

        _log($"[CodexBridge] Starting: codex app-server (sandbox={sandbox}, approval={approval}, model={_model}, cwd={_workingDirectory})");

        _process = Process.Start(startInfo);
        if (_process is null)
            throw new InvalidOperationException("Failed to start codex app-server process");

        _log($"[CodexBridge] Codex app-server started (PID {_process.Id})");

        // Start reading stdout (JSON-RPC responses/notifications from Codex)
        _stdoutReadTask = Task.Run(() => ReadStdoutLoopAsync(_cts.Token), _cts.Token);
        _stderrReadTask = Task.Run(() => ReadStderrLoopAsync(_cts.Token), _cts.Token);

        // Start keep-alive timer (Codex doesn't send keep_alive)
        _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(_cts.Token), _cts.Token);

        // Send initialize handshake
        await SendInitializeAsync();

        // Wait briefly for the init response before sending thread/start
        await Task.Delay(500, ct);
    }

    /// <summary>
    /// Handle an NDJSON message coming from the backend (via proxy).
    /// Translate to JSON-RPC 2.0 and write to Codex stdin.
    /// </summary>
    public async Task HandleBackendMessageAsync(string ndjsonMessage, CancellationToken ct)
    {
        if (_process is null || _process.HasExited) return;

        try
        {
            using var doc = JsonDocument.Parse(ndjsonMessage);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "user":
                    await HandleUserMessageAsync(root, ct);
                    break;

                case "control_response":
                    await HandleControlResponseAsync(root, ct);
                    break;

                case "control_request":
                    if (root.TryGetProperty("request", out var req) &&
                        req.TryGetProperty("subtype", out var subtype) &&
                        subtype.GetString() == "interrupt")
                    {
                        await SendInterruptAsync(ct);
                    }
                    break;

                case "keep_alive":
                    // Codex doesn't need keep_alive, just ignore
                    break;

                default:
                    _log($"[CodexBridge] Ignoring unknown backend message type: {type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log($"[CodexBridge] Error handling backend message: {ex.Message}");
        }
    }

    /// <summary>
    /// Wait for the process to exit.
    /// </summary>
    public async Task WaitForExitAsync(CancellationToken ct)
    {
        if (_process is not null)
        {
            await _process.WaitForExitAsync(ct);
        }
    }

    public int ExitCode => _process?.ExitCode ?? -1;

    #region NDJSON -> JSON-RPC 2.0 (Backend -> Codex)

    private async Task SendInitializeAsync()
    {
        var id = NextId();
        _pendingRequests[id] = "initialize";

        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "initialize",
            ["id"] = id,
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "side_hub_agent",
                    ["title"] = "Side Hub Agent",
                    ["version"] = "1.0.0"
                },
                ["capabilities"] = new JsonObject
                {
                    ["experimentalApi"] = true
                }
            }
        };

        await WriteToStdinAsync(msg.ToJsonString(JsonOptions));
    }

    private async Task HandleUserMessageAsync(JsonElement root, CancellationToken ct)
    {
        var message = root.GetProperty("message");
        var content = message.GetProperty("content");

        string textContent;
        if (content.ValueKind == JsonValueKind.String)
        {
            textContent = content.GetString() ?? "";
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            // Extract text from content blocks
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" &&
                    block.TryGetProperty("text", out var txt))
                {
                    parts.Add(txt.GetString() ?? "");
                }
            }
            textContent = string.Join("\n", parts);
        }
        else
        {
            textContent = content.ToString();
        }

        if (_threadId is null)
        {
            // First message — start a new thread
            var id = NextId();
            _pendingRequests[id] = "thread/start";

            var (sandbox, _) = MapPermissionMode(_permissionMode);

            var msg = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "thread/start",
                ["id"] = id,
                ["params"] = new JsonObject
                {
                    ["model"] = _model,
                    ["instructions"] = textContent,
                    ["sandbox"] = sandbox
                }
            };

            await WriteToStdinAsync(msg.ToJsonString(JsonOptions));
        }
        else
        {
            // Subsequent message — new turn in existing thread
            var id = NextId();
            _pendingRequests[id] = "turn/start";

            var msg = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "turn/start",
                ["id"] = id,
                ["params"] = new JsonObject
                {
                    ["threadId"] = _threadId,
                    ["message"] = textContent
                }
            };

            await WriteToStdinAsync(msg.ToJsonString(JsonOptions));
        }
    }

    private async Task HandleControlResponseAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("response", out var response)) return;

        var requestId = response.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;
        if (requestId is null) return;

        var behavior = response.TryGetProperty("response", out var resp) &&
                       resp.TryGetProperty("behavior", out var beh)
            ? beh.GetString()
            : null;

        var approved = behavior == "allow";

        // Find the original Codex approvalId
        string? approvalId = null;
        foreach (var kv in _pendingApprovals)
        {
            if (kv.Value == requestId)
            {
                approvalId = kv.Key;
                _pendingApprovals.TryRemove(kv.Key, out _);
                break;
            }
        }

        if (approvalId is null)
        {
            _log($"[CodexBridge] No pending approval found for requestId {requestId}");
            return;
        }

        var id = NextId();
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "serverRequest/resolved",
            ["id"] = id,
            ["params"] = new JsonObject
            {
                ["decision"] = approved ? "accept" : "decline",
                ["itemId"] = approvalId
            }
        };

        await WriteToStdinAsync(msg.ToJsonString(JsonOptions));
    }

    private async Task SendInterruptAsync(CancellationToken ct)
    {
        if (_threadId is null) return;

        var id = NextId();
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "turn/cancel",
            ["id"] = id,
            ["params"] = new JsonObject()
        };

        await WriteToStdinAsync(msg.ToJsonString(JsonOptions));
    }

    #endregion

    #region JSON-RPC 2.0 -> NDJSON (Codex -> Backend)

    private async Task ReadStdoutLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _process is not null && !_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    await ProcessCodexMessageAsync(line, ct);
                }
                catch (Exception ex)
                {
                    _log($"[CodexBridge] Error processing Codex message: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[CodexBridge] Stdout read error: {ex.Message}");
        }
    }

    private async Task ProcessCodexMessageAsync(string line, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        // Check if it's a response (has "id" and "result"/"error") or a notification (has "method")
        if (root.TryGetProperty("id", out var idElement) && root.TryGetProperty("result", out var result))
        {
            await HandleRpcResponseAsync(idElement.GetInt32(), result, ct);
        }
        else if (root.TryGetProperty("id", out var errIdElement) && root.TryGetProperty("error", out var error))
        {
            var errId = errIdElement.GetInt32();
            _log($"[CodexBridge] JSON-RPC error for request {errId}: {error}");
        }
        else if (root.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.GetString() ?? "";
            var paramsEl = root.TryGetProperty("params", out var p) ? p : default;
            await HandleRpcNotificationAsync(method, paramsEl, root, ct);
        }
    }

    private async Task HandleRpcResponseAsync(int id, JsonElement result, CancellationToken ct)
    {
        if (!_pendingRequests.TryRemove(id, out var requestType)) return;

        switch (requestType)
        {
            case "initialize":
                _log($"[CodexBridge] Initialize response received");
                break;

            case "thread/start":
                // Extract threadId from result
                if (result.TryGetProperty("threadId", out var tid))
                {
                    _threadId = tid.GetString();
                }

                // Send init-like message to backend
                var initMsg = JsonSerializer.Serialize(new
                {
                    type = "system",
                    subtype = "init",
                    model = _model,
                    tools = Array.Empty<string>(),
                    session_id = _threadId ?? _sessionId
                }, JsonOptions);

                await SendToBackendAsync(initMsg, ct);
                break;

            case "turn/start":
                // Turn started — processing will come via notifications
                break;
        }
    }

    private async Task HandleRpcNotificationAsync(string method, JsonElement paramsEl, JsonElement root, CancellationToken ct)
    {
        switch (method)
        {
            case "item/agentMessage/delta":
                // Streaming text delta — accumulate and forward
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var deltaContent))
                {
                    var text = deltaContent.GetString() ?? "";
                    if (!string.IsNullOrEmpty(text))
                    {
                        var streamMsg = JsonSerializer.Serialize(new
                        {
                            type = "stream_event",
                            @event = new { type = "content_block_delta", delta = new { type = "text_delta", text } }
                        }, JsonOptions);
                        await SendToBackendAsync(streamMsg, ct);
                    }
                }
                break;

            case "item/agentMessage/completed":
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("item", out var item))
                {
                    var contentBlocks = ExtractContentBlocks(item);
                    var assistantMsg = JsonSerializer.Serialize(new
                    {
                        type = "assistant",
                        message = new
                        {
                            role = "assistant",
                            content = contentBlocks
                        }
                    }, JsonOptions);
                    await SendToBackendAsync(assistantMsg, ct);
                }
                break;

            case "item/commandExecution/requestApproval":
                await HandleApprovalRequestAsync(paramsEl, "Bash", ct);
                break;

            case "item/fileChange/requestApproval":
                await HandleApprovalRequestAsync(paramsEl, "Edit", ct);
                break;

            case "item/commandExecution/started":
                var startedMsg = JsonSerializer.Serialize(new
                {
                    type = "tool_progress",
                    tool_name = "Bash",
                    data = new { status = "started" }
                }, JsonOptions);
                await SendToBackendAsync(startedMsg, ct);
                break;

            case "item/commandExecution/completed":
            case "item/fileChange/completed":
                var toolName = method.Contains("command") ? "Bash" : "Edit";
                var summaryMsg = JsonSerializer.Serialize(new
                {
                    type = "tool_use_summary",
                    message = new
                    {
                        content = new[]
                        {
                            new { type = "tool_result", tool_use_id = Guid.NewGuid().ToString(), content = "completed" }
                        }
                    }
                }, JsonOptions);
                await SendToBackendAsync(summaryMsg, ct);
                break;

            case "turn/completed":
                var resultMsg = JsonSerializer.Serialize(new
                {
                    type = "result",
                    subtype = "success",
                    cost_usd = 0,
                    duration_ms = 0,
                    duration_api_ms = 0,
                    session_id = _threadId ?? _sessionId
                }, JsonOptions);
                await SendToBackendAsync(resultMsg, ct);
                break;

            case "turn/errored":
                var errorMsg = JsonSerializer.Serialize(new
                {
                    type = "result",
                    subtype = "error_max_turns",
                    error = "Turn errored",
                    session_id = _threadId ?? _sessionId
                }, JsonOptions);
                await SendToBackendAsync(errorMsg, ct);
                break;

            case "thread/status/changed":
                // Informational, no translation needed
                break;

            default:
                _log($"[CodexBridge] Unhandled Codex notification: {method}");
                break;
        }
    }

    private async Task HandleApprovalRequestAsync(JsonElement paramsEl, string toolName, CancellationToken ct)
    {
        if (paramsEl.ValueKind == JsonValueKind.Undefined) return;

        var approvalId = paramsEl.TryGetProperty("approvalId", out var aid) ? aid.GetString() : Guid.NewGuid().ToString();
        var requestId = Guid.NewGuid().ToString();

        // Store mapping for when the response comes back
        _pendingApprovals[approvalId ?? requestId] = requestId;

        // Build tool input based on type
        object toolInput;
        if (toolName == "Bash")
        {
            var command = paramsEl.TryGetProperty("command", out var cmd) ? cmd.GetString() : "";
            toolInput = new { command };
        }
        else
        {
            var file = paramsEl.TryGetProperty("file", out var f) ? f.GetString() : "";
            var diff = paramsEl.TryGetProperty("diff", out var d) ? d.GetString() : "";
            toolInput = new { file_path = file, diff };
        }

        var permMsg = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = requestId,
            request = new
            {
                subtype = "can_use_tool",
                tool_name = toolName,
                input = toolInput,
                tool_use_id = approvalId ?? requestId
            }
        }, JsonOptions);

        await SendToBackendAsync(permMsg, ct);
    }

    private static object[] ExtractContentBlocks(JsonElement item)
    {
        var blocks = new List<object>();

        if (item.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                blocks.Add(new { type = "text", text = content.GetString() ?? "" });
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var bt))
                    {
                        var blockType = bt.GetString();
                        if (blockType == "text" && block.TryGetProperty("text", out var txt))
                        {
                            blocks.Add(new { type = "text", text = txt.GetString() ?? "" });
                        }
                    }
                }
            }
        }

        if (blocks.Count == 0)
        {
            // Fallback: try to get any text from the item
            var rawText = item.ToString();
            blocks.Add(new { type = "text", text = rawText });
        }

        return blocks.ToArray();
    }

    #endregion

    #region Helpers

    private async Task WriteToStdinAsync(string jsonRpcMessage)
    {
        if (_process?.HasExited != false) return;

        try
        {
            await _process.StandardInput.WriteLineAsync(jsonRpcMessage);
            await _process.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            _log($"[CodexBridge] Failed to write to stdin: {ex.Message}");
        }
    }

    private async Task SendToBackendAsync(string ndjsonMessage, CancellationToken ct)
    {
        if (_sendToBackend is null) return;

        try
        {
            await _sendToBackend(ndjsonMessage, ct);
        }
        catch (Exception ex)
        {
            _log($"[CodexBridge] Failed to send to backend: {ex.Message}");
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsRunning)
            {
                await Task.Delay(10_000, ct);
                await SendToBackendAsync("{\"type\":\"keep_alive\"}", ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadStderrLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _process is not null && !_process.HasExited)
            {
                var line = await _process.StandardError.ReadLineAsync(ct);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    _log($"[CodexBridge stderr] {line}");
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private int NextId() => Interlocked.Increment(ref _rpcId);

    private static (string sandbox, string approval) MapPermissionMode(string permissionMode)
    {
        return permissionMode.ToLowerInvariant() switch
        {
            "auto" or "pipeline" or "bypasspermissions" => ("danger-full-access", "never"),
            "plan" => ("read-only", "never"),
            "manual" => ("workspace-write", "untrusted"),
            _ => ("workspace-write", "on-request") // safe / default
        };
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.Kill();
                _log($"[CodexBridge] Killed codex process (PID {_process.Id})");
            }
            catch { }
        }

        try
        {
            if (_stdoutReadTask is not null) await _stdoutReadTask;
        }
        catch { }

        try
        {
            if (_stderrReadTask is not null) await _stderrReadTask;
        }
        catch { }

        try
        {
            if (_keepAliveTask is not null) await _keepAliveTask;
        }
        catch { }

        _cts?.Dispose();
        _process?.Dispose();
    }
}
