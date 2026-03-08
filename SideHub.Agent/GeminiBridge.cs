using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace SideHub.Agent;

/// <summary>
/// Bridge between Gemini CLI (NDJSON over stdin/stdout with -p flag) and the Side Hub
/// NDJSON protocol (over WebSocket via AgentSdkProxy).
///
/// Unlike CodexBridge (JSON-RPC 2.0 translation), GeminiBridge does NDJSON-to-NDJSON
/// translation since Gemini CLI natively outputs stream-json. The main difference is
/// that Gemini CLI runs one turn per process invocation (-p "prompt"), so the bridge
/// spawns a new process for each user message and uses --resume for multi-turn context.
/// </summary>
public class GeminiBridge : IAsyncDisposable
{
    private readonly Action<string> _log;
    private readonly string _sessionId;
    private readonly string _model;
    private readonly string _workingDirectory;
    private string _permissionMode;

    private Process? _process;
    private bool _disposed;
    private bool _isFirstTurn = true;
    private int? _lastPid;

    // Track pending permission requests: requestId -> pending state
    private readonly ConcurrentDictionary<string, string> _pendingPermissions = new();

    // Callback to send NDJSON messages to the backend (through proxy)
    private Func<string, CancellationToken, Task>? _sendToBackend;
    private CancellationTokenSource? _cts;
    private Task? _keepAliveTask;
    private TaskCompletionSource? _processExitTcs;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public GeminiBridge(
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

    public int? Pid => _lastPid ?? _process?.Id;
    public bool IsRunning => !_disposed;
    public int ExitCode => _process?.ExitCode ?? 0;

    /// <summary>
    /// Initialize the bridge (no process spawned yet — that happens on first user message).
    /// Sends system/init to backend so the frontend transitions from "connecting" to "ready".
    /// </summary>
    public async Task StartAsync(Func<string, CancellationToken, Task> sendToBackend, CancellationToken ct)
    {
        _sendToBackend = sendToBackend;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _processExitTcs = new TaskCompletionSource();

        // Start keep-alive timer (Gemini doesn't send keep_alive)
        _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(_cts.Token), _cts.Token);

        // Send synthetic system/init so the backend knows we're ready
        var initMsg = JsonSerializer.Serialize(new
        {
            type = "system",
            subtype = "init",
            model = _model,
            tools = Array.Empty<string>(),
            session_id = _sessionId
        }, JsonOptions);
        await SendToBackendAsync(initMsg, ct);

        _log($"[GeminiBridge] Ready (model={_model}, cwd={_workingDirectory})");

        // Check that gemini is accessible
        try
        {
            var whichInfo = new ProcessStartInfo
            {
                FileName = "which",
                ArgumentList = { "gemini" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            var whichProcess = Process.Start(whichInfo);
            if (whichProcess is not null)
            {
                await whichProcess.WaitForExitAsync(ct);
                if (whichProcess.ExitCode != 0)
                    _log("[GeminiBridge] WARNING: 'gemini' not found in PATH");
                else
                    _log($"[GeminiBridge] gemini found");
            }
        }
        catch { }
    }

    /// <summary>
    /// Handle an NDJSON message coming from the backend (via proxy).
    /// </summary>
    public async Task HandleBackendMessageAsync(string ndjsonMessage, CancellationToken ct)
    {
        if (_disposed) return;

        try
        {
            using var doc = JsonDocument.Parse(ndjsonMessage);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            _log($"[GeminiBridge] Backend message received: type={type}");

            switch (type)
            {
                case "user":
                    await HandleUserMessageAsync(root, ct);
                    break;

                case "control_request":
                    if (root.TryGetProperty("request", out var req) &&
                        req.TryGetProperty("subtype", out var subtype))
                    {
                        switch (subtype.GetString())
                        {
                            case "interrupt":
                                await InterruptCurrentTurnAsync();
                                break;
                            case "set_permission_mode":
                                if (req.TryGetProperty("permission_mode", out var permMode))
                                {
                                    _permissionMode = permMode.GetString() ?? "default";
                                    _log($"[GeminiBridge] Permission mode updated to {_permissionMode}");
                                }
                                break;
                        }
                    }
                    break;

                case "control_response":
                    // Gemini uses --yolo, so no permission flow to handle for now
                    break;

                case "keep_alive":
                    break;

                default:
                    _log($"[GeminiBridge] Ignoring unknown backend message type: {type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log($"[GeminiBridge] Error handling backend message: {ex.Message}");
        }
    }

    /// <summary>
    /// Wait for the bridge to be disposed (called by WebSocketClient to monitor lifecycle).
    /// </summary>
    public async Task WaitForExitAsync(CancellationToken ct)
    {
        if (_processExitTcs is not null)
        {
            using var reg = ct.Register(() => _processExitTcs.TrySetCanceled());
            await _processExitTcs.Task;
        }
    }

    #region User message handling

    private async Task HandleUserMessageAsync(JsonElement root, CancellationToken ct)
    {
        // Extract text content from the user message
        var message = root.GetProperty("message");
        var content = message.GetProperty("content");

        string textContent;
        if (content.ValueKind == JsonValueKind.String)
        {
            textContent = content.GetString() ?? "";
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
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

        if (string.IsNullOrWhiteSpace(textContent))
        {
            _log("[GeminiBridge] Empty user message, ignoring");
            return;
        }

        // Spawn a Gemini CLI process for this turn
        await SpawnGeminiTurnAsync(textContent, ct);
    }

    private async Task SpawnGeminiTurnAsync(string prompt, CancellationToken ct)
    {
        // Kill any existing process from a previous turn that might still be running
        if (_process is not null && !_process.HasExited)
        {
            try { _process.Kill(); } catch { }
            _process.Dispose();
            _process = null;
        }

        var approvalMode = MapPermissionMode(_permissionMode);

        var startInfo = new ProcessStartInfo
        {
            FileName = "gemini",
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        // Resume previous session for multi-turn context
        if (!_isFirstTurn)
        {
            startInfo.ArgumentList.Add("--resume");
            startInfo.ArgumentList.Add("latest");
        }

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_model);
        startInfo.ArgumentList.Add("--approval-mode");
        startInfo.ArgumentList.Add(approvalMode);

        _log($"[GeminiBridge] Spawning gemini (turn={(_isFirstTurn ? "first" : "resume")}, approval={approvalMode}, prompt length={prompt.Length})");

        _process = Process.Start(startInfo);
        if (_process is null)
        {
            _log("[GeminiBridge] Failed to start gemini process");
            var errMsg = JsonSerializer.Serialize(new
            {
                type = "result",
                subtype = "error_max_turns",
                error = "Failed to start gemini process",
                cost_usd = 0,
                duration_ms = 0,
                duration_api_ms = 0,
                session_id = _sessionId
            }, JsonOptions);
            await SendToBackendAsync(errMsg, ct);
            return;
        }

        _lastPid = _process.Id;
        _isFirstTurn = false;

        _log($"[GeminiBridge] Gemini started (PID {_process.Id})");

        // Read stdout (NDJSON stream) and stderr in parallel
        var stdoutTask = Task.Run(() => ReadStdoutLoopAsync(_process, _cts?.Token ?? ct), ct);
        var stderrTask = Task.Run(() => ReadStderrLoopAsync(_process, _cts?.Token ?? ct), ct);

        // Wait for process to finish
        try
        {
            await _process.WaitForExitAsync(ct);
            var exitCode = _process.ExitCode;
            _log($"[GeminiBridge] Gemini process exited with code {exitCode}");

            await Task.WhenAll(stdoutTask, stderrTask);

            // If process exited with error, send a result message so the frontend
            // doesn't stay stuck on "Thinking..." / "Ready"
            if (exitCode != 0)
            {
                var errMsg = JsonSerializer.Serialize(new
                {
                    type = "result",
                    subtype = "error_max_turns",
                    error = $"Gemini process exited with code {exitCode}",
                    cost_usd = 0,
                    duration_ms = 0,
                    duration_api_ms = 0,
                    session_id = _sessionId
                }, JsonOptions);
                await SendToBackendAsync(errMsg, ct);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(); } catch { }
            }
        }
        catch (Exception ex)
        {
            _log($"[GeminiBridge] Error waiting for gemini process: {ex.Message}");
        }
    }

    private async Task InterruptCurrentTurnAsync()
    {
        if (_process is not null && !_process.HasExited)
        {
            _log("[GeminiBridge] Interrupting current turn");
            try { _process.Kill(); } catch { }
        }
    }

    #endregion

    #region Gemini NDJSON -> Backend NDJSON translation

    private async Task ReadStdoutLoopAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    await ProcessGeminiMessageAsync(line, ct);
                }
                catch (Exception ex)
                {
                    _log($"[GeminiBridge] Error processing Gemini message: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[GeminiBridge] Stdout read error: {ex.Message}");
        }
    }

    private async Task ProcessGeminiMessageAsync(string line, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        switch (type)
        {
            case "init":
                // Gemini session initialized — we already sent system/init, so just log
                var geminiSessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : "unknown";
                var geminiModel = root.TryGetProperty("model", out var mdl) ? mdl.GetString() : _model;
                _log($"[GeminiBridge] Gemini init: session={geminiSessionId}, model={geminiModel}");
                break;

            case "message":
                await HandleGeminiMessageAsync(root, ct);
                break;

            case "tool_use":
                await HandleGeminiToolUseAsync(root, ct);
                break;

            case "tool_result":
                await HandleGeminiToolResultAsync(root, ct);
                break;

            case "result":
                await HandleGeminiResultAsync(root, ct);
                break;

            default:
                _log($"[GeminiBridge] Unknown Gemini message type: {type}");
                break;
        }
    }

    private async Task HandleGeminiMessageAsync(JsonElement root, CancellationToken ct)
    {
        var role = root.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : null;
        var content = root.TryGetProperty("content", out var contentProp) ? contentProp.GetString() ?? "" : "";
        var isDelta = root.TryGetProperty("delta", out var deltaProp) && deltaProp.GetBoolean();

        if (role == "user")
        {
            // Echo of user message — ignore
            return;
        }

        if (role == "assistant")
        {
            if (isDelta)
            {
                // Streaming delta — translate to stream_event
                if (!string.IsNullOrEmpty(content))
                {
                    var streamMsg = JsonSerializer.Serialize(new
                    {
                        type = "stream_event",
                        @event = new
                        {
                            type = "content_block_delta",
                            delta = new { type = "text_delta", text = content }
                        }
                    }, JsonOptions);
                    await SendToBackendAsync(streamMsg, ct);
                }
            }
            else
            {
                // Complete assistant message
                if (!string.IsNullOrEmpty(content))
                {
                    var assistantMsg = JsonSerializer.Serialize(new
                    {
                        type = "assistant",
                        message = new
                        {
                            role = "assistant",
                            content = new[] { new { type = "text", text = content } }
                        }
                    }, JsonOptions);
                    await SendToBackendAsync(assistantMsg, ct);
                }
            }
        }
    }

    private async Task HandleGeminiToolUseAsync(JsonElement root, CancellationToken ct)
    {
        var toolName = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "unknown" : "unknown";
        var toolId = root.TryGetProperty("tool_id", out var tid) ? tid.GetString() ?? "" : "";

        // Map Gemini tool names to Side Hub tool names
        var mappedToolName = toolName switch
        {
            "run_shell_command" => "Bash",
            "write_file" or "replace" => "Edit",
            "read_file" => "Read",
            "list_directory" or "glob" => "Glob",
            "grep_search" => "Grep",
            _ => toolName
        };

        var progressMsg = JsonSerializer.Serialize(new
        {
            type = "tool_progress",
            tool_name = mappedToolName,
            data = new { status = "started", tool_id = toolId }
        }, JsonOptions);
        await SendToBackendAsync(progressMsg, ct);
    }

    private async Task HandleGeminiToolResultAsync(JsonElement root, CancellationToken ct)
    {
        var toolId = root.TryGetProperty("tool_id", out var tid) ? tid.GetString() ?? "" : "";
        var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";

        var summaryMsg = JsonSerializer.Serialize(new
        {
            type = "tool_use_summary",
            message = new
            {
                content = new[]
                {
                    new { type = "tool_result", tool_use_id = toolId, content = status }
                }
            }
        }, JsonOptions);
        await SendToBackendAsync(summaryMsg, ct);
    }

    private async Task HandleGeminiResultAsync(JsonElement root, CancellationToken ct)
    {
        var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "success" : "success";

        // Extract stats if available
        var durationMs = 0;
        if (root.TryGetProperty("stats", out var stats))
        {
            durationMs = stats.TryGetProperty("duration_ms", out var dur) ? dur.GetInt32() : 0;
        }

        var resultSubtype = status == "success" ? "success" : "error_max_turns";
        string? errorText = status != "success" ? $"Gemini turn {status}" : null;

        var resultMsg = JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = resultSubtype,
            error = errorText,
            cost_usd = 0,
            duration_ms = durationMs,
            duration_api_ms = 0,
            session_id = _sessionId
        }, JsonOptions);
        await SendToBackendAsync(resultMsg, ct);
    }

    #endregion

    #region Helpers

    private async Task SendToBackendAsync(string ndjsonMessage, CancellationToken ct)
    {
        if (_sendToBackend is null) return;

        try
        {
            await _sendToBackend(ndjsonMessage, ct);
        }
        catch (Exception ex)
        {
            _log($"[GeminiBridge] Failed to send to backend: {ex.Message}");
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                await Task.Delay(10_000, ct);
                await SendToBackendAsync("{\"type\":\"keep_alive\"}", ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadStderrLoopAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync(ct);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    _log($"[GeminiBridge stderr] {line}");
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static string MapPermissionMode(string permissionMode)
    {
        // Map Side Hub permission modes to Gemini --approval-mode values
        // Gemini supports: default, auto_edit, yolo, plan
        return permissionMode.ToLowerInvariant() switch
        {
            "auto" or "pipeline" or "bypasspermissions" => "yolo",
            "plan" => "plan",
            "manual" or "safe" or "default" => "default",
            "yolo" => "yolo",
            _ => "default"
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
                _log($"[GeminiBridge] Killed gemini process (PID {_process.Id})");
            }
            catch { }
        }

        try
        {
            if (_keepAliveTask is not null) await _keepAliveTask;
        }
        catch { }

        _processExitTcs?.TrySetResult();
        _cts?.Dispose();
        _process?.Dispose();
    }
}
