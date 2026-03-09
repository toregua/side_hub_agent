using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Agent;

/// <summary>
/// Bridge between Codex CLI (JSON-RPC 2.0 over stdin/stdout) and the Side Hub
/// NDJSON protocol (over WebSocket via AgentSdkProxy).
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
    private string _permissionMode;

    private Process? _process;
    private string? _threadId;
    private int _rpcId;
    private bool _disposed;

    // Store the first user message to send as turn/start after thread/start completes
    private string? _pendingUserMessage;

    // Track pending JSON-RPC requests to correlate responses
    private readonly ConcurrentDictionary<int, string> _pendingRequests = new();

    // Track pending approval request IDs: Codex approvalId -> (our requestId, jsonRpcId)
    private readonly ConcurrentDictionary<string, (string RequestId, int JsonRpcId)> _pendingApprovals = new();

    // Callback to send NDJSON messages to the backend (through proxy)
    private Func<string, CancellationToken, Task>? _sendToBackend;
    private CancellationTokenSource? _cts;
    private Task? _stdoutReadTask;
    private Task? _stderrReadTask;
    private Task? _keepAliveTask;

    // Buffer tool execution output to emit rich assistant messages with tool_use/tool_result blocks
    private readonly ConcurrentDictionary<string, ToolExecutionBuffer> _toolBuffers = new();
    private const int MaxOutputLength = 4000; // Frontend truncates display at 500 chars with "Show all"

    private class ToolExecutionBuffer
    {
        public string ToolName { get; set; } = "";
        public string? Command { get; set; }
        public string? FilePath { get; set; }
        public StringBuilder OutputBuffer { get; } = new();
        public int? ExitCode { get; set; }
        public string ToolUseId { get; set; } = "";
    }

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
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"model=\"{_model}\"");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"sandbox=\"{sandbox}\"");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"approval_policy=\"{approval}\"");

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

            _log($"[CodexBridge] Backend message received: type={type}");

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
                        req.TryGetProperty("subtype", out var subtype))
                    {
                        switch (subtype.GetString())
                        {
                            case "interrupt":
                                await SendInterruptAsync(ct);
                                break;
                            case "set_permission_mode":
                                if (req.TryGetProperty("permission_mode", out var permMode))
                                {
                                    var newMode = permMode.GetString() ?? "default";
                                    _permissionMode = newMode;
                                    _log($"[CodexBridge] Permission mode updated to {_permissionMode} (applied on next turn)");
                                }
                                break;
                        }
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
            // First message — start a new thread, then send turn/start after threadId is received
            _pendingUserMessage = textContent;

            var id = NextId();
            _pendingRequests[id] = "thread/start";

            var (sandbox, approval) = MapPermissionMode(_permissionMode);

            var msg = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "thread/start",
                ["id"] = id,
                ["params"] = new JsonObject
                {
                    ["model"] = _model,
                    ["sandbox"] = sandbox,
                    ["approvalPolicy"] = approval
                }
            };

            _log($"[CodexBridge] Sending thread/start (model={_model}, sandbox={sandbox}, approval={approval})");
            await WriteToStdinAsync(msg.ToJsonString(JsonOptions));
        }
        else
        {
            // Subsequent message — new turn in existing thread
            await SendTurnStartAsync(textContent);
        }
    }

    private async Task SendTurnStartAsync(string textContent)
    {
        var id = NextId();
        _pendingRequests[id] = "turn/start";

        var inputArray = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = textContent
            }
        };

        var (sandbox, approval) = MapPermissionMode(_permissionMode);

        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "turn/start",
            ["id"] = id,
            ["params"] = new JsonObject
            {
                ["threadId"] = _threadId,
                ["input"] = inputArray,
                ["sandbox"] = sandbox,
                ["approvalPolicy"] = approval
            }
        };

        _log($"[CodexBridge] Sending turn/start (threadId={_threadId}, sandbox={sandbox}, approval={approval}, input length={textContent.Length})");
        await WriteToStdinAsync(msg.ToJsonString(JsonOptions));
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

        // Find the original Codex approval info
        string? approvalKey = null;
        int jsonRpcId = 0;
        foreach (var kv in _pendingApprovals)
        {
            if (kv.Value.RequestId == requestId)
            {
                approvalKey = kv.Key;
                jsonRpcId = kv.Value.JsonRpcId;
                _pendingApprovals.TryRemove(kv.Key, out _);
                break;
            }
        }

        if (approvalKey is null)
        {
            _log($"[CodexBridge] No pending approval found for requestId {requestId}");
            return;
        }

        _log($"[CodexBridge] Approval response: key={approvalKey}, approved={approved}, rpcId={jsonRpcId}");

        // Send JSON-RPC response with the original request id
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = jsonRpcId,
            ["result"] = new JsonObject
            {
                ["decision"] = approved ? "accept" : "decline"
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

        var hasId = root.TryGetProperty("id", out var idElement);
        var hasMethod = root.TryGetProperty("method", out var methodElement);
        var hasResult = root.TryGetProperty("result", out var result);
        var hasError = root.TryGetProperty("error", out var error);

        if (hasId && hasResult)
        {
            // JSON-RPC response to our request
            await HandleRpcResponseAsync(idElement.GetInt32(), result, ct);
        }
        else if (hasId && hasError)
        {
            _log($"[CodexBridge] JSON-RPC error for request {idElement.GetInt32()}: {error}");
        }
        else if (hasId && hasMethod)
        {
            // Server-initiated request (e.g. approval requests) — needs JSON-RPC response
            var method = methodElement.GetString() ?? "";
            var paramsEl = root.TryGetProperty("params", out var p) ? p : default;
            var rpcId = idElement.GetInt32();
            await HandleServerRequestAsync(method, paramsEl, rpcId, ct);
        }
        else if (hasMethod)
        {
            // Notification (no id)
            var method = methodElement.GetString() ?? "";
            var paramsEl = root.TryGetProperty("params", out var p) ? p : default;
            await HandleRpcNotificationAsync(method, paramsEl, ct);
        }
    }

    private async Task HandleRpcResponseAsync(int id, JsonElement result, CancellationToken ct)
    {
        if (!_pendingRequests.TryRemove(id, out var requestType)) return;

        switch (requestType)
        {
            case "initialize":
                _log($"[CodexBridge] Initialize response received");
                // Send system/init immediately so the backend transitions to AwaitingInput
                // and the frontend moves from "connecting" to "ready"
                var initMsg = JsonSerializer.Serialize(new
                {
                    type = "system",
                    subtype = "init",
                    model = _model,
                    tools = Array.Empty<string>(),
                    session_id = _sessionId
                }, JsonOptions);
                await SendToBackendAsync(initMsg, ct);
                break;

            case "thread/start":
                // Extract threadId from result, then send turn/start with the pending user message
                if (result.TryGetProperty("thread", out var threadObj) &&
                    threadObj.TryGetProperty("id", out var tid))
                {
                    _threadId = tid.GetString();
                }
                else if (result.TryGetProperty("threadId", out var tidDirect))
                {
                    _threadId = tidDirect.GetString();
                }
                _log($"[CodexBridge] Thread started: {_threadId}");

                // Find the real Codex session file and send updated system/init
                // so the frontend gets the correct cliSessionId for resume
                if (_threadId is not null)
                {
                    var codexSessionId = FindCodexSessionFile(_threadId);
                    if (codexSessionId is not null)
                    {
                        _log($"[CodexBridge] Found Codex session file: {codexSessionId}");
                        var updatedInit = JsonSerializer.Serialize(new
                        {
                            type = "system",
                            subtype = "init",
                            model = _model,
                            tools = Array.Empty<string>(),
                            session_id = codexSessionId
                        }, JsonOptions);
                        await SendToBackendAsync(updatedInit, ct);
                    }
                }

                // Now send the actual user message as turn/start
                if (_pendingUserMessage is not null && _threadId is not null)
                {
                    var msg = _pendingUserMessage;
                    _pendingUserMessage = null;
                    await SendTurnStartAsync(msg);
                }
                break;

            case "turn/start":
                // Turn started — processing will come via notifications
                _log("[CodexBridge] Turn started");
                break;
        }
    }

    /// <summary>
    /// Handle server-initiated requests (have id + method) — need JSON-RPC response.
    /// </summary>
    private async Task HandleServerRequestAsync(string method, JsonElement paramsEl, int rpcId, CancellationToken ct)
    {
        await EmitNativeStreamEventAsync("request", method, paramsEl, ct,
            correlationId: ExtractCorrelationId(paramsEl),
            itemType: ExtractItemType(paramsEl));

        switch (method)
        {
            case "item/commandExecution/requestApproval":
                await HandleApprovalRequestAsync(paramsEl, "Bash", rpcId, ct);
                break;

            case "item/fileChange/requestApproval":
                await HandleApprovalRequestAsync(paramsEl, "Edit", rpcId, ct);
                break;

            case "applyPatchApproval":
                await HandleApprovalRequestAsync(paramsEl, "Edit", rpcId, ct);
                break;

            default:
                _log($"[CodexBridge] Unhandled server request: {method} (id={rpcId})");
                // Send error response so Codex doesn't hang waiting
                var errResp = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = rpcId,
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32601,
                        ["message"] = $"Method not supported: {method}"
                    }
                };
                await WriteToStdinAsync(errResp.ToJsonString(JsonOptions));
                break;
        }
    }

    private async Task HandleRpcNotificationAsync(string method, JsonElement paramsEl, CancellationToken ct)
    {
        await EmitNativeStreamEventAsync("notification", method, paramsEl, ct,
            correlationId: ExtractCorrelationId(paramsEl),
            itemType: ExtractItemType(paramsEl));

        switch (method)
        {
            case "item/agentMessage/delta":
                // Streaming text delta
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("delta", out var delta))
                {
                    // Delta is a string in the v2 protocol
                    var text = delta.ValueKind == JsonValueKind.String
                        ? delta.GetString() ?? ""
                        : delta.TryGetProperty("content", out var dc) ? dc.GetString() ?? "" : "";

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

            case "item/started":
                // An item (command, file change, agent message) started
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("item", out var startedItem))
                {
                    var itemType = startedItem.TryGetProperty("type", out var st) ? st.GetString() : null;
                    var itemId = startedItem.TryGetProperty("id", out var sid)
                        ? sid.GetString() ?? Guid.NewGuid().ToString()
                        : Guid.NewGuid().ToString();
                    if (itemType is "commandExecution" or "shellExecution")
                    {
                        var command = ExtractCommandFromItem(startedItem);
                        _toolBuffers[itemId] = new ToolExecutionBuffer
                        {
                            ToolName = "Bash", Command = command, ToolUseId = itemId
                        };
                        var msg = JsonSerializer.Serialize(new
                        {
                            type = "tool_progress",
                            tool_name = "Bash",
                            data = new { status = "started", command },
                            tool_input = new { command }
                        }, JsonOptions);
                        await SendToBackendAsync(msg, ct);
                    }
                    else if (itemType is "fileChange")
                    {
                        var filePath = ExtractPathFromItem(startedItem);
                        _toolBuffers[itemId] = new ToolExecutionBuffer
                        {
                            ToolName = "Edit", FilePath = filePath, ToolUseId = itemId
                        };
                        var msg = JsonSerializer.Serialize(new
                        {
                            type = "tool_progress",
                            tool_name = "Edit",
                            data = new { status = "started", file_path = filePath, path = filePath },
                            tool_input = new { file_path = filePath, path = filePath }
                        }, JsonOptions);
                        await SendToBackendAsync(msg, ct);
                    }
                }
                break;

            case "item/completed":
                // ItemCompletedNotification: {item: ThreadItem, threadId, turnId}
                // ThreadItem types: agentMessage, commandExecution, fileChange, mcpToolCall, plan, reasoning, etc.
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("item", out var completedItem))
                {
                    var itemType = completedItem.TryGetProperty("type", out var ct2) ? ct2.GetString() : null;

                    if (itemType is "agentMessage")
                    {
                        // Assistant message completed
                        var contentBlocks = ExtractContentBlocks(completedItem);
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
                    else
                    {
                        // Emit rich assistant message with tool_use/tool_result blocks
                        var completedItemId = completedItem.TryGetProperty("id", out var citemId)
                            ? citemId.GetString() ?? Guid.NewGuid().ToString()
                            : Guid.NewGuid().ToString();
                        await EmitToolBlockAssistantMessageAsync(completedItemId, completedItem, ct);

                        // Tool execution completed (command/file/tool call). Emit enriched progress so UI can show file paths.
                        var fallbackToolName = itemType is "commandExecution" or "shellExecution" ? "Bash" : null;
                        await EmitToolProgressFromItemAsync(completedItem, ct, fallbackToolName, "completed");
                        var summaryMsg = JsonSerializer.Serialize(new
                        {
                            type = "tool_use_summary",
                            message = new
                            {
                                content = new[]
                                {
                                    new { type = "tool_result", tool_use_id = completedItemId, content = "completed" }
                                }
                            }
                        }, JsonOptions);
                        await SendToBackendAsync(summaryMsg, ct);
                    }
                }
                break;

            case "turn/completed":
                // TurnCompletedNotification: {turn: {id, status, items, error?}, threadId}
                // status: "completed" | "interrupted" | "failed" | "inProgress"
                string resultSubtype = "success";
                string? errorText = null;
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("turn", out var turnEl))
                {
                    var status = turnEl.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : "completed";
                    if (status is "failed" or "interrupted")
                    {
                        resultSubtype = "error_max_turns";
                        if (turnEl.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                        {
                            errorText = errEl.TryGetProperty("message", out var em) ? em.GetString() : errEl.ToString();
                        }
                        else
                        {
                            errorText = $"Turn {status}";
                        }
                    }
                    _log($"[CodexBridge] Turn completed: status={status}{(errorText != null ? $", error={errorText}" : "")}");
                }

                var resultMsg = JsonSerializer.Serialize(new
                {
                    type = "result",
                    subtype = resultSubtype,
                    error = errorText,
                    cost_usd = 0,
                    duration_ms = 0,
                    duration_api_ms = 0,
                    session_id = _threadId ?? _sessionId
                }, JsonOptions);
                await SendToBackendAsync(resultMsg, ct);
                break;

            case "item/reasoning/textDelta":
            case "item/reasoning/summaryTextDelta":
            case "item/plan/delta":
                // Forward reasoning/plan deltas as stream events
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("delta", out var reasonDelta))
                {
                    var text = reasonDelta.ValueKind == JsonValueKind.String ? reasonDelta.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(text))
                    {
                        var msg = JsonSerializer.Serialize(new
                        {
                            type = "stream_event",
                            @event = new { type = "content_block_delta", delta = new { type = "thinking_delta", thinking = text } }
                        }, JsonOptions);
                        await SendToBackendAsync(msg, ct);
                    }
                }
                break;

            case "item/commandExecution/outputDelta":
            case "item/fileChange/outputDelta":
                // Tool output streaming — buffer for rich tool blocks
                if (paramsEl.ValueKind != JsonValueKind.Undefined)
                {
                    var outputItemId = ExtractCorrelationId(paramsEl) ?? "unknown";
                    var deltaText = paramsEl.TryGetProperty("delta", out var outputDelta) && outputDelta.ValueKind == JsonValueKind.String
                        ? outputDelta.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(deltaText) &&
                        _toolBuffers.TryGetValue(outputItemId, out var outputBuffer) &&
                        outputBuffer.OutputBuffer.Length < MaxOutputLength)
                    {
                        var remaining = MaxOutputLength - outputBuffer.OutputBuffer.Length;
                        outputBuffer.OutputBuffer.Append(deltaText.Length <= remaining
                            ? deltaText
                            : deltaText[..remaining] + "\n... (truncated)");
                    }
                }
                break;

            case "error":
                // ErrorNotification: {error: {message, codexErrorInfo?}, willRetry, threadId, turnId}
                var errorMessage = "Unknown error";
                string? errorCode = null;
                var willRetry = false;
                if (paramsEl.ValueKind != JsonValueKind.Undefined)
                {
                    willRetry = paramsEl.TryGetProperty("willRetry", out var wr) && wr.GetBoolean();

                    if (paramsEl.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object)
                    {
                        errorMessage = errObj.TryGetProperty("message", out var em) ? em.GetString() ?? "Unknown error" : "Unknown error";
                        if (errObj.TryGetProperty("codexErrorInfo", out var cei))
                        {
                            errorCode = cei.ValueKind == JsonValueKind.String ? cei.GetString() : cei.ToString();
                        }
                    }
                    else
                    {
                        errorMessage = paramsEl.ToString();
                    }
                }
                _log($"[CodexBridge] ERROR from Codex: {errorMessage} (code={errorCode}, willRetry={willRetry})");

                // Only send error to frontend if Codex won't retry
                if (!willRetry)
                {
                    var errResultMsg = JsonSerializer.Serialize(new
                    {
                        type = "result",
                        subtype = "error_max_turns",
                        error = errorMessage,
                        cost_usd = 0,
                        duration_ms = 0,
                        duration_api_ms = 0,
                        session_id = _threadId ?? _sessionId
                    }, JsonOptions);
                    await SendToBackendAsync(errResultMsg, ct);
                }
                break;

            case "codex/event/agent_message_delta":
            case "codex/event/agent_message_content_delta":
                // Streaming text delta via codex/event protocol
                // Format: params.msg.delta = text string
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("msg", out var evtDeltaMsg) &&
                    evtDeltaMsg.TryGetProperty("delta", out var evtDelta))
                {
                    var text = evtDelta.ValueKind == JsonValueKind.String ? evtDelta.GetString() ?? "" : "";
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

            case "codex/event/agent_message":
                // Completed agent message via codex/event protocol
                // Format: params.msg.message = full text
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("msg", out var evtAgentMsg))
                {
                    var messageText = evtAgentMsg.TryGetProperty("message", out var msgText) ? msgText.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(messageText))
                    {
                        var assistantEvtMsg = JsonSerializer.Serialize(new
                        {
                            type = "assistant",
                            message = new
                            {
                                role = "assistant",
                                content = new[] { new { type = "text", text = messageText } }
                            }
                        }, JsonOptions);
                        await SendToBackendAsync(assistantEvtMsg, ct);
                    }
                }
                break;

            case "codex/event/exec_command_begin":
                // Command execution started via codex/event protocol
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("msg", out var execBeginMsg))
                {
                    var command = "";
                    if (execBeginMsg.TryGetProperty("command", out var cmdArr) && cmdArr.ValueKind == JsonValueKind.Array)
                    {
                        var parts = new List<string>();
                        foreach (var part in cmdArr.EnumerateArray())
                            parts.Add(part.GetString() ?? "");
                        command = string.Join(" ", parts);
                    }
                    var execCallId = execBeginMsg.TryGetProperty("call_id", out var execCid)
                        ? execCid.GetString() ?? Guid.NewGuid().ToString()
                        : Guid.NewGuid().ToString();
                    _toolBuffers[execCallId] = new ToolExecutionBuffer
                    {
                        ToolName = "Bash", Command = command, ToolUseId = execCallId
                    };
                    var msg = JsonSerializer.Serialize(new
                    {
                        type = "tool_progress",
                        tool_name = "Bash",
                        data = new { status = "started", command },
                        tool_input = new { command }
                    }, JsonOptions);
                    await SendToBackendAsync(msg, ct);
                }
                break;

            case "codex/event/exec_command_end":
                // Command execution completed via codex/event protocol
                {
                    var toolUseId = paramsEl.TryGetProperty("msg", out var execEndMsg) &&
                                    execEndMsg.TryGetProperty("call_id", out var execEndCallId)
                        ? execEndCallId.GetString() ?? Guid.NewGuid().ToString()
                        : Guid.NewGuid().ToString();

                    // Extract exit code if available
                    int? execExitCode = null;
                    if (execEndMsg.ValueKind == JsonValueKind.Object &&
                        execEndMsg.TryGetProperty("exit_code", out var ecProp) && ecProp.TryGetInt32(out var ecVal))
                        execExitCode = ecVal;

                    // Emit rich assistant message with tool blocks
                    await EmitToolBlockAssistantMessageAsync(toolUseId, execEndMsg.ValueKind == JsonValueKind.Object ? execEndMsg : null, ct, execExitCode);

                    var summaryEvt = JsonSerializer.Serialize(new
                    {
                        type = "tool_use_summary",
                        message = new
                        {
                            content = new[] { new { type = "tool_result", tool_use_id = toolUseId, content = "completed" } }
                        }
                    }, JsonOptions);
                    await SendToBackendAsync(summaryEvt, ct);
                }
                break;

            case "codex/event/patch_apply_begin":
                // File patch started via codex/event protocol
                {
                    var detail = ExtractPathFromItem(paramsEl);
                    var patchCallId = paramsEl.TryGetProperty("msg", out var patchBeginMsg) &&
                                      patchBeginMsg.TryGetProperty("call_id", out var patchCid)
                        ? patchCid.GetString() ?? Guid.NewGuid().ToString()
                        : Guid.NewGuid().ToString();
                    _toolBuffers[patchCallId] = new ToolExecutionBuffer
                    {
                        ToolName = "Edit", FilePath = detail, ToolUseId = patchCallId
                    };
                    var patchMsg = JsonSerializer.Serialize(new
                    {
                        type = "tool_progress",
                        tool_name = "Edit",
                        data = new { status = "started", file_path = detail, path = detail },
                        tool_input = new { file_path = detail, path = detail }
                    }, JsonOptions);
                    await SendToBackendAsync(patchMsg, ct);
                }
                break;

            case "codex/event/patch_apply_end":
                // File patch completed via codex/event protocol
                {
                    var patchUseId = paramsEl.TryGetProperty("msg", out var patchEndMsg) &&
                                     patchEndMsg.TryGetProperty("call_id", out var patchCallId)
                        ? patchCallId.GetString() ?? Guid.NewGuid().ToString()
                        : Guid.NewGuid().ToString();

                    // Emit rich assistant message with tool blocks
                    await EmitToolBlockAssistantMessageAsync(patchUseId, patchEndMsg.ValueKind == JsonValueKind.Object ? patchEndMsg : null, ct);

                    var patchSummary = JsonSerializer.Serialize(new
                    {
                        type = "tool_use_summary",
                        message = new
                        {
                            content = new[] { new { type = "tool_result", tool_use_id = patchUseId, content = "completed" } }
                        }
                    }, JsonOptions);
                    await SendToBackendAsync(patchSummary, ct);
                }
                break;

            case "codex/event/exec_approval_request":
                // Approval request via codex/event protocol (notification, not server-initiated request)
                // Format: params.msg.command, params.msg.reason, params.msg.call_id
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("msg", out var approvalEvtMsg))
                {
                    var command = "";
                    if (approvalEvtMsg.TryGetProperty("command", out var cmdEvtArr) && cmdEvtArr.ValueKind == JsonValueKind.Array)
                    {
                        var parts = new List<string>();
                        foreach (var part in cmdEvtArr.EnumerateArray())
                            parts.Add(part.GetString() ?? "");
                        command = string.Join(" ", parts);
                    }
                    var callId = approvalEvtMsg.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                    var reason = approvalEvtMsg.TryGetProperty("reason", out var rn) ? rn.GetString() ?? "" : "";
                    var requestId = Guid.NewGuid().ToString();

                    _log($"[CodexBridge] codex/event approval request: command={command}, reason={reason}");

                    var permMsg = JsonSerializer.Serialize(new
                    {
                        type = "control_request",
                        request_id = requestId,
                        request = new
                        {
                            subtype = "can_use_tool",
                            tool_name = "Bash",
                            input = new { command },
                            tool_use_id = callId
                        }
                    }, JsonOptions);
                    await SendToBackendAsync(permMsg, ct);
                }
                break;

            case "codex/event/error":
                // Codex event error — log full details
                var codexError = paramsEl.ValueKind != JsonValueKind.Undefined ? paramsEl.ToString() : "no details";
                _log($"[CodexBridge] codex/event/error: {codexError}");
                break;

            case "codex/event/item_started":
                // Map to item/started behavior
                if (paramsEl.ValueKind != JsonValueKind.Undefined)
                {
                    var evtItemType = paramsEl.TryGetProperty("type", out var eit) ? eit.GetString() : null;
                    if (evtItemType is "command" or "shell" or "commandExecution" or "shellExecution")
                    {
                        var msg = JsonSerializer.Serialize(new
                        {
                            type = "tool_progress",
                            tool_name = "Bash",
                            data = new { status = "started" }
                        }, JsonOptions);
                        await SendToBackendAsync(msg, ct);
                    }
                    else if (evtItemType is "fileChange" or "file_change" or "patch")
                    {
                        var filePath = ExtractPathFromItem(paramsEl);
                        var msg = JsonSerializer.Serialize(new
                        {
                            type = "tool_progress",
                            tool_name = "Edit",
                            data = new { status = "started", file_path = filePath, path = filePath },
                            tool_input = new { file_path = filePath, path = filePath }
                        }, JsonOptions);
                        await SendToBackendAsync(msg, ct);
                    }
                }
                break;

            case "codex/event/item_completed":
                // Map to item/completed behavior
                if (paramsEl.ValueKind != JsonValueKind.Undefined)
                {
                    var evtItemType2 = paramsEl.TryGetProperty("type", out var eit2) ? eit2.GetString() : null;

                    if (evtItemType2 is "message" or "agentMessage")
                    {
                        var contentBlocks2 = ExtractContentBlocks(paramsEl);
                        var assistantMsg2 = JsonSerializer.Serialize(new
                        {
                            type = "assistant",
                            message = new { role = "assistant", content = contentBlocks2 }
                        }, JsonOptions);
                        await SendToBackendAsync(assistantMsg2, ct);
                    }
                    else
                    {
                        var fallbackToolName = evtItemType2 is "command" or "shell" or "commandExecution" or "shellExecution"
                            ? "Bash"
                            : null;
                        await EmitToolProgressFromItemAsync(paramsEl, ct, fallbackToolName, "completed");
                        var summaryUseId = paramsEl.TryGetProperty("id", out var completedEvtId)
                            ? completedEvtId.GetString() ?? Guid.NewGuid().ToString()
                            : Guid.NewGuid().ToString();
                        var summaryMsg2 = JsonSerializer.Serialize(new
                        {
                            type = "tool_use_summary",
                            message = new
                            {
                                content = new[] { new { type = "tool_result", tool_use_id = summaryUseId, content = "completed" } }
                            }
                        }, JsonOptions);
                        await SendToBackendAsync(summaryMsg2, ct);
                    }
                }
                break;

            case "codex/event/task_complete":
                // Turn/task completed via codex/event protocol
                var taskResultMsg = JsonSerializer.Serialize(new
                {
                    type = "result",
                    subtype = "success",
                    cost_usd = 0,
                    duration_ms = 0,
                    duration_api_ms = 0,
                    session_id = _threadId ?? _sessionId
                }, JsonOptions);
                await SendToBackendAsync(taskResultMsg, ct);
                break;

            case "codex/event/plan_update":
                // Plan update via codex/event protocol — forward as thinking
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("msg", out var planMsg))
                {
                    var planText = planMsg.TryGetProperty("plan", out var planProp) ? planProp.GetString() ?? "" : planMsg.ToString();
                    if (!string.IsNullOrEmpty(planText))
                    {
                        var msg = JsonSerializer.Serialize(new
                        {
                            type = "stream_event",
                            @event = new { type = "content_block_delta", delta = new { type = "thinking_delta", thinking = planText } }
                        }, JsonOptions);
                        await SendToBackendAsync(msg, ct);
                    }
                }
                break;

            case "turn/started":
            case "thread/started":
            case "thread/status/changed":
            case "thread/name/updated":
            case "thread/tokenUsage/updated":
            case "thread/compacted":
            case "turn/diff/updated":
            case "turn/plan/updated":
            case "serverRequest/resolved":
            case "codex/event/web_search_begin":
                // Web search started
                {
                    var wsMsg = JsonSerializer.Serialize(new
                    {
                        type = "tool_progress",
                        tool_name = "WebSearch",
                        data = new { status = "started" }
                    }, JsonOptions);
                    await SendToBackendAsync(wsMsg, ct);
                }
                break;

            case "codex/event/web_search_end":
                // Web search completed
                {
                    var wsUseId = paramsEl.TryGetProperty("msg", out var wsEndMsg) &&
                                   wsEndMsg.TryGetProperty("call_id", out var wsCallId)
                        ? wsCallId.GetString() ?? Guid.NewGuid().ToString()
                        : Guid.NewGuid().ToString();
                    var wsSummary = JsonSerializer.Serialize(new
                    {
                        type = "tool_use_summary",
                        message = new
                        {
                            content = new[] { new { type = "tool_result", tool_use_id = wsUseId, content = "completed" } }
                        }
                    }, JsonOptions);
                    await SendToBackendAsync(wsSummary, ct);
                }
                break;

            case "codex/event/exec_command_output_delta":
                // Buffer command output from codex/event protocol
                if (paramsEl.ValueKind != JsonValueKind.Undefined &&
                    paramsEl.TryGetProperty("msg", out var outputEvtMsg))
                {
                    var outputCallId = outputEvtMsg.TryGetProperty("call_id", out var outputCid)
                        ? outputCid.GetString() : null;
                    var outputText = outputEvtMsg.TryGetProperty("output", out var outProp) && outProp.ValueKind == JsonValueKind.String
                        ? outProp.GetString() ?? ""
                        : outputEvtMsg.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.String
                            ? dataProp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(outputCallId) && !string.IsNullOrEmpty(outputText) &&
                        _toolBuffers.TryGetValue(outputCallId, out var evtBuffer) &&
                        evtBuffer.OutputBuffer.Length < MaxOutputLength)
                    {
                        var rem = MaxOutputLength - evtBuffer.OutputBuffer.Length;
                        evtBuffer.OutputBuffer.Append(outputText.Length <= rem
                            ? outputText
                            : outputText[..rem] + "\n... (truncated)");
                    }
                }
                break;

            case "codex/event/mcp_startup_complete":
            case "codex/event/task_started":
            case "codex/event/user_message":
            case "codex/event/warning":
            case "codex/event/token_count":
            case "codex/event/turn_diff":
            case "codex/event/terminal_interaction":
            case "account/rateLimits/updated":
            case "item/commandExecution/terminalInteraction":
                // Informational, no translation needed
                break;

            default:
                // Log full content for unknown notifications to aid debugging
                var paramsStr = paramsEl.ValueKind != JsonValueKind.Undefined ? paramsEl.ToString() : "null";
                _log($"[CodexBridge] Unhandled Codex notification: {method} | params={paramsStr[..Math.Min(500, paramsStr.Length)]}");
                break;
        }
    }

    private async Task HandleApprovalRequestAsync(JsonElement paramsEl, string toolName, int rpcId, CancellationToken ct)
    {
        if (paramsEl.ValueKind == JsonValueKind.Undefined) return;

        var itemId = paramsEl.TryGetProperty("itemId", out var iid) ? iid.GetString() : null;
        var approvalId = paramsEl.TryGetProperty("approvalId", out var aid) ? aid.GetString() : null;
        var key = approvalId ?? itemId ?? Guid.NewGuid().ToString();
        var requestId = Guid.NewGuid().ToString();

        // Store mapping: key -> (our requestId for backend, JSON-RPC id for Codex response)
        _pendingApprovals[key] = (requestId, rpcId);

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

        _log($"[CodexBridge] Approval request: tool={toolName}, rpcId={rpcId}, key={key}");

        var permMsg = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = requestId,
            request = new
            {
                subtype = "can_use_tool",
                tool_name = toolName,
                input = toolInput,
                tool_use_id = key
            }
        }, JsonOptions);

        await SendToBackendAsync(permMsg, ct);
    }

    private async Task EmitNativeStreamEventAsync(
        string phase,
        string method,
        JsonElement paramsEl,
        CancellationToken ct,
        string? correlationId = null,
        string? itemType = null)
    {
        var paramsJson = paramsEl.ValueKind != JsonValueKind.Undefined ? paramsEl.GetRawText() : null;

        var nativeMsg = JsonSerializer.Serialize(new
        {
            type = "stream_event",
            @event = new
            {
                type = "codex_native",
                native = new
                {
                    provider = "codex",
                    phase,
                    method,
                    correlation_id = correlationId,
                    item_type = itemType,
                    params_json = paramsJson
                }
            }
        }, JsonOptions);

        await SendToBackendAsync(nativeMsg, ct);
    }

    private static string? ExtractCorrelationId(JsonElement paramsEl)
    {
        if (paramsEl.ValueKind != JsonValueKind.Object) return null;

        if (paramsEl.TryGetProperty("approvalId", out var approvalId)) return approvalId.GetString();
        if (paramsEl.TryGetProperty("itemId", out var itemId)) return itemId.GetString();
        if (paramsEl.TryGetProperty("id", out var id)) return id.GetString();

        if (paramsEl.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
        {
            if (item.TryGetProperty("id", out var nestedId)) return nestedId.GetString();
        }

        if (paramsEl.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.Object)
        {
            if (msg.TryGetProperty("call_id", out var callId)) return callId.GetString();
        }

        return null;
    }

    private static string? ExtractItemType(JsonElement paramsEl)
    {
        if (paramsEl.ValueKind != JsonValueKind.Object) return null;

        if (paramsEl.TryGetProperty("type", out var type)) return type.GetString();

        if (paramsEl.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
        {
            if (item.TryGetProperty("type", out var itemType)) return itemType.GetString();
        }

        return null;
    }

    private static object[] ExtractContentBlocks(JsonElement item)
    {
        var blocks = new List<object>();

        // Codex agentMessage uses "text" field (string), not "content"
        if (item.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
        {
            var t = textProp.GetString() ?? "";
            if (!string.IsNullOrEmpty(t))
                blocks.Add(new { type = "text", text = t });
        }

        // Also check "content" for compatibility
        if (blocks.Count == 0 && item.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                blocks.Add(new { type = "text", text = content.GetString() ?? "" });
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var bt)) continue;
                    var blockType = bt.GetString();

                    if (blockType == "text" && block.TryGetProperty("text", out var txt))
                    {
                        blocks.Add(new { type = "text", text = txt.GetString() ?? "" });
                        continue;
                    }

                    if (blockType is "tool_use" or "toolUse")
                    {
                        var id =
                            (block.TryGetProperty("id", out var idEl) ? idEl.GetString() : null) ??
                            (block.TryGetProperty("call_id", out var callIdEl) ? callIdEl.GetString() : null) ??
                            Guid.NewGuid().ToString();
                        var rawName =
                            (block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null) ??
                            (block.TryGetProperty("tool_name", out var toolNameEl) ? toolNameEl.GetString() : null) ??
                            "unknown";
                        var mappedName = NormalizeToolName(rawName);
                        object input = block.TryGetProperty("input", out var inputEl) ? inputEl : new { };
                        if (block.TryGetProperty("args", out var argsEl)) input = argsEl;
                        if (block.TryGetProperty("arguments", out var argumentsEl)) input = argumentsEl;

                        blocks.Add(new
                        {
                            type = "tool_use",
                            id,
                            name = mappedName,
                            input
                        });
                        continue;
                    }

                    if (blockType == "tool_result")
                    {
                        var toolUseId =
                            (block.TryGetProperty("tool_use_id", out var toolUseIdEl) ? toolUseIdEl.GetString() : null) ??
                            (block.TryGetProperty("id", out var resultIdEl) ? resultIdEl.GetString() : null) ??
                            Guid.NewGuid().ToString();
                        object contentValue = block.TryGetProperty("content", out var contentEl) ? contentEl : "";
                        var isError = block.TryGetProperty("is_error", out var isErrorEl) && isErrorEl.ValueKind == JsonValueKind.True;
                        blocks.Add(new
                        {
                            type = "tool_result",
                            tool_use_id = toolUseId,
                            content = contentValue,
                            is_error = isError
                        });
                    }
                }
            }
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new { type = "text", text = "[No content extracted]" });
        }

        return blocks.ToArray();
    }

    private async Task EmitToolProgressFromItemAsync(
        JsonElement item,
        CancellationToken ct,
        string? fallbackToolName = null,
        string status = "completed")
    {
        var toolName = ResolveToolName(item, fallbackToolName);
        if (string.IsNullOrWhiteSpace(toolName)) return;

        var filePath = ExtractPathFromItem(item);

        var payload = new JsonObject
        {
            ["type"] = "tool_progress",
            ["tool_name"] = toolName
        };

        var data = new JsonObject { ["status"] = status };
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            data["file_path"] = filePath;
            data["path"] = filePath;
            payload["tool_input"] = new JsonObject
            {
                ["file_path"] = filePath,
                ["path"] = filePath
            };
        }

        payload["data"] = data;
        await SendToBackendAsync(payload.ToJsonString(JsonOptions), ct);
    }

    private static string ResolveToolName(JsonElement item, string? fallbackToolName = null)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return NormalizeToolName(fallbackToolName);

        var type = GetString(item, "type");
        var name = GetString(item, "name")
            ?? GetString(item, "tool_name")
            ?? GetString(item, "toolName");

        if (string.Equals(type, "mcpToolCall", StringComparison.OrdinalIgnoreCase))
        {
            name ??= GetString(item, "mcpToolName")
                ?? (TryGetProperty(item, "tool", out var toolObj) ? GetString(toolObj, "name") : null);
        }

        if (TryGetProperty(item, "input", out var input))
        {
            name ??= GetString(input, "tool_name") ?? GetString(input, "toolName");
        }

        if (TryGetProperty(item, "args", out var args))
        {
            name ??= GetString(args, "tool_name") ?? GetString(args, "toolName");
        }

        var candidate = name ?? type ?? fallbackToolName;
        if (string.IsNullOrWhiteSpace(candidate))
            return "";

        if (string.Equals(type, "commandExecution", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "shellExecution", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "command", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "shell", StringComparison.OrdinalIgnoreCase))
        {
            return "Bash";
        }

        if (string.Equals(type, "fileChange", StringComparison.OrdinalIgnoreCase))
        {
            return "Edit";
        }

        return NormalizeToolName(candidate);
    }

    private static string NormalizeToolName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var key = raw.Trim().Replace("-", "_");
        return key.ToLowerInvariant() switch
        {
            "read" or "read_file" => "Read",
            "write" or "write_file" => "Write",
            "edit" or "apply_patch" or "patch_apply" or "file_change" => "Edit",
            "multiedit" or "multi_edit" => "MultiEdit",
            "notebookedit" or "notebook_edit" => "NotebookEdit",
            "glob" => "Glob",
            "grep" or "search" => "Grep",
            "bash" or "commandexecution" or "shellexecution" or "command" or "shell" => "Bash",
            _ => raw.Trim()
        };
    }

    private static string? ExtractPathFromItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var candidates = new[]
        {
            GetString(item, "file_path"),
            GetString(item, "path"),
            GetString(item, "target_file"),
            GetString(item, "filePath"),
            GetString(item, "file"),
            GetFirstString(item, "paths"),
            GetFirstString(item, "files"),
            TryGetProperty(item, "input", out var input) ? ExtractPathFromItem(input) : null,
            TryGetProperty(item, "args", out var args) ? ExtractPathFromItem(args) : null,
            TryGetProperty(item, "arguments", out var arguments) ? ExtractPathFromItem(arguments) : null,
            TryGetProperty(item, "change", out var change) ? ExtractPathFromItem(change) : null,
            TryGetProperty(item, "changes", out var changes) ? GetFirstPathFromArray(changes) : null,
            TryGetProperty(item, "msg", out var msg) ? ExtractPathFromItem(msg) : null
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            return candidate.Trim().Trim('"', '\'');
        }

        return null;
    }

    private static string? GetFirstPathFromArray(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String) return entry.GetString();
            if (entry.ValueKind == JsonValueKind.Object)
            {
                var nested = ExtractPathFromItem(entry);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        return obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static bool TryGetProperty(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(propertyName, out value))
            return true;
        value = default;
        return false;
    }

    private static string? GetFirstString(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var array) || array.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) return item.GetString();
        }
        return null;
    }

    private async Task EmitToolBlockAssistantMessageAsync(
        string itemId,
        JsonElement? completedItem,
        CancellationToken ct,
        int? overrideExitCode = null)
    {
        if (!_toolBuffers.TryRemove(itemId, out var buffer)) return;

        // Extract exit code from completed item if not overridden
        if (overrideExitCode.HasValue)
        {
            buffer.ExitCode = overrideExitCode;
        }
        else if (completedItem.HasValue)
        {
            if (completedItem.Value.TryGetProperty("exitCode", out var ec) && ec.TryGetInt32(out var code))
                buffer.ExitCode = code;
            else if (completedItem.Value.TryGetProperty("exit_code", out var ec2) && ec2.TryGetInt32(out var code2))
                buffer.ExitCode = code2;
        }

        // Build tool_use input based on tool type
        object toolInput = buffer.ToolName switch
        {
            "Bash" => new { command = buffer.Command ?? "" },
            _ => new { file_path = buffer.FilePath ?? "" }
        };

        // Build tool_result output
        var output = buffer.OutputBuffer.ToString();
        if (string.IsNullOrWhiteSpace(output))
            output = buffer.ExitCode.HasValue ? $"Exit code: {buffer.ExitCode}" : "completed";

        var isError = buffer.ExitCode.HasValue && buffer.ExitCode != 0;

        var blocks = new object[]
        {
            new { type = "tool_use", id = buffer.ToolUseId, name = buffer.ToolName, input = toolInput },
            new { type = "tool_result", tool_use_id = buffer.ToolUseId, content = output, is_error = isError }
        };

        var msg = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { role = "assistant", content = blocks }
        }, JsonOptions);
        await SendToBackendAsync(msg, ct);
    }

    private static string? ExtractCommandFromItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        // Direct string command
        if (item.TryGetProperty("command", out var cmd))
        {
            if (cmd.ValueKind == JsonValueKind.String)
                return cmd.GetString();
            if (cmd.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var part in cmd.EnumerateArray())
                    parts.Add(part.GetString() ?? "");
                return string.Join(" ", parts);
            }
        }

        // Nested in input/args
        if (item.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            var nested = ExtractCommandFromItem(input);
            if (nested != null) return nested;
        }
        if (item.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object)
        {
            var nested = ExtractCommandFromItem(args);
            if (nested != null) return nested;
        }

        return null;
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
            "safe" or "default" or _ => ("workspace-write", "on-request")
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

        _toolBuffers.Clear();
        _cts?.Dispose();
        _process?.Dispose();
    }

    /// <summary>
    /// Search ~/.codex/sessions/ for a session file whose name contains the given thread ID.
    /// Returns the filename without extension (= the session ID used by the picker).
    /// </summary>
    private string? FindCodexSessionFile(string threadId)
    {
        try
        {
            var sessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            if (!Directory.Exists(sessionsDir)) return null;

            var files = Directory.GetFiles(sessionsDir, "*.jsonl", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains(threadId))
                    return name;
            }
        }
        catch (Exception ex)
        {
            _log($"[CodexBridge] Error searching for Codex session file: {ex.Message}");
        }
        return null;
    }
}
