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
    private readonly string _permissionMode;

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

        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "turn/start",
            ["id"] = id,
            ["params"] = new JsonObject
            {
                ["threadId"] = _threadId,
                ["input"] = inputArray
            }
        };

        _log($"[CodexBridge] Sending turn/start (threadId={_threadId}, input length={textContent.Length})");
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
                    if (itemType is "commandExecution" or "shellExecution")
                    {
                        var msg = JsonSerializer.Serialize(new
                        {
                            type = "tool_progress",
                            tool_name = "Bash",
                            data = new { status = "started" }
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
                        // Tool execution completed (command or file change)
                        var toolName = itemType is "commandExecution" or "shellExecution" ? "Bash" : "Edit";
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
                // Tool output streaming — forward as tool progress
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
                    var msg = JsonSerializer.Serialize(new
                    {
                        type = "tool_progress",
                        tool_name = "Bash",
                        data = new { status = "started", command }
                    }, JsonOptions);
                    await SendToBackendAsync(msg, ct);
                }
                break;

            case "codex/event/exec_command_end":
                // Command execution completed via codex/event protocol
                {
                    var summaryEvt = JsonSerializer.Serialize(new
                    {
                        type = "tool_use_summary",
                        message = new
                        {
                            content = new[] { new { type = "tool_result", tool_use_id = Guid.NewGuid().ToString(), content = "completed" } }
                        }
                    }, JsonOptions);
                    await SendToBackendAsync(summaryEvt, ct);
                }
                break;

            case "codex/event/patch_apply_begin":
                // File patch started via codex/event protocol
                {
                    var patchMsg = JsonSerializer.Serialize(new
                    {
                        type = "tool_progress",
                        tool_name = "Edit",
                        data = new { status = "started" }
                    }, JsonOptions);
                    await SendToBackendAsync(patchMsg, ct);
                }
                break;

            case "codex/event/patch_apply_end":
                // File patch completed via codex/event protocol
                {
                    var patchSummary = JsonSerializer.Serialize(new
                    {
                        type = "tool_use_summary",
                        message = new
                        {
                            content = new[] { new { type = "tool_result", tool_use_id = Guid.NewGuid().ToString(), content = "completed" } }
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
                        var summaryMsg2 = JsonSerializer.Serialize(new
                        {
                            type = "tool_use_summary",
                            message = new
                            {
                                content = new[] { new { type = "tool_result", tool_use_id = Guid.NewGuid().ToString(), content = "completed" } }
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
            case "codex/event/mcp_startup_complete":
            case "codex/event/task_started":
            case "codex/event/user_message":
            case "codex/event/warning":
            case "codex/event/exec_command_output_delta":
            case "codex/event/token_count":
            case "codex/event/turn_diff":
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
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" &&
                        block.TryGetProperty("text", out var txt))
                    {
                        blocks.Add(new { type = "text", text = txt.GetString() ?? "" });
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
