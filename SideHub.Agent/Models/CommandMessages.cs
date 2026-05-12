using System.Text.Json.Serialization;

namespace SideHub.Agent.Models;

public class CommandExecuteMessage
{
    [JsonPropertyName("type")]
    public string Type => "command.execute";

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("shell")]
    public required string Shell { get; init; }
}

public class CommandOutputMessage
{
    [JsonPropertyName("type")]
    public string Type => "command.output";

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("stream")]
    public required string Stream { get; init; }

    [JsonPropertyName("data")]
    public required string Data { get; init; }
}

public class CommandCompletedMessage
{
    [JsonPropertyName("type")]
    public string Type => "command.completed";

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }
}

public class CommandFailedMessage
{
    [JsonPropertyName("type")]
    public string Type => "command.failed";

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("error")]
    public required string Error { get; init; }
}

public class CommandBusyMessage
{
    [JsonPropertyName("type")]
    public string Type => "command.busy";

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("reason")]
    public string Reason => "Agent is already executing a command";
}

public class IncomingMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("commandId")]
    public string? CommandId { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("shell")]
    public string? Shell { get; init; }

    [JsonPropertyName("input")]
    public string? Input { get; init; }

    [JsonPropertyName("columns")]
    public int? Columns { get; init; }

    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }

    // file.write fields
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("ptyPaste")]
    public string? PtyPaste { get; init; }

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }

    [JsonPropertyName("attachment")]
    public TerminalAttachmentPayload? Attachment { get; init; }

    // pty.start: optional dict of extra env vars to merge into the PTY environment
    // (workflow execution context, etc.). Wins on conflict with the agent's defaults.
    [JsonPropertyName("additionalEnv")]
    public Dictionary<string, string>? AdditionalEnv { get; init; }
}

public class TerminalAttachmentPayload
{
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    [JsonPropertyName("base64Data")]
    public string? Base64Data { get; init; }
}

public class PtyOutputMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.output";

    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }
}

public class PtyStartedMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.started";

    [JsonPropertyName("shell")]
    public required string Shell { get; init; }

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }

    /// <summary>
    /// True when we reused an already-running PTY for this ptySessionId.
    /// False (default) when a fresh PTY was just spawned. The frontend uses
    /// this to decide whether to auto-resume a previously-captured CLI session.
    /// </summary>
    [JsonPropertyName("reattached")]
    public bool Reattached { get; init; }
}

public class PtyExitedMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.exited";

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }
}

public class PtyHistoryMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.history";

    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("bufferSize")]
    public required int BufferSize { get; init; }

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }
}

/// <summary>
/// Emitted when a CLI session is started inside a PTY (e.g. the user typed
/// <c>claude</c> and our wrapper minted a UUID via <c>--session-id</c>). The
/// agent's FIFO reader posts this back to the backend so SideHub can link the
/// session to a task or display it in the UI.
/// </summary>
public class PtyCliSessionStartedMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.cli-session-started";

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("cliSessionId")]
    public required string CliSessionId { get; init; }
}

/// <summary>
/// Emitted when the agent observes that Claude has generated a conversation
/// title for a running CLI session (the "ai-title" line Claude writes to its
/// project JSONL). The backend forwards this over SSE so the UI can suggest it
/// as a tab label.
/// </summary>
public class PtyCliSessionTitledMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.cli-session-titled";

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    [JsonPropertyName("cliSessionId")]
    public required string CliSessionId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }
}

