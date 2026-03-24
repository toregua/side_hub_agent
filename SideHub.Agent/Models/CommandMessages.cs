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

    // agent-sdk fields
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("sdkUrl")]
    public string? SdkUrl { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("permissionMode")]
    public string? PermissionMode { get; init; }

    [JsonPropertyName("resumeCliSessionId")]
    public string? ResumeCliSessionId { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("sdkToken")]
    public string? SdkToken { get; init; }

    [JsonPropertyName("taskId")]
    public string? TaskId { get; init; }

    [JsonPropertyName("taskTitle")]
    public string? TaskTitle { get; init; }
}

public class PtyOutputMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.output";

    [JsonPropertyName("data")]
    public required string Data { get; init; }
}

public class PtyStartedMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.started";

    [JsonPropertyName("shell")]
    public required string Shell { get; init; }
}

public class PtyExitedMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.exited";

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }
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
}

public class AgentSdkSpawnedMessage
{
    [JsonPropertyName("type")]
    public string Type => "agent-sdk.spawned";

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("pid")]
    public required int Pid { get; init; }
}

public class AgentSdkSpawnFailedMessage
{
    [JsonPropertyName("type")]
    public string Type => "agent-sdk.spawn-failed";

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("error")]
    public required string Error { get; init; }
}

public class AgentSdkExitedMessage
{
    [JsonPropertyName("type")]
    public string Type => "agent-sdk.exited";

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }
}

public class AgentSdkSessionsAliveMessage
{
    [JsonPropertyName("type")]
    public string Type => "agent-sdk.sessions-alive";

    [JsonPropertyName("sessions")]
    public required List<Dictionary<string, string?>> Sessions { get; init; }
}
