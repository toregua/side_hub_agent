using System.Text.Json.Serialization;

namespace SideHub.Agent.Models;

public class AgentConnectedMessage
{
    [JsonPropertyName("type")]
    public string Type => "agent.connected";

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("workspaceId")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("capabilities")]
    public required string[] Capabilities { get; init; }

    [JsonPropertyName("defaultShell")]
    public required string DefaultShell { get; init; }

    [JsonPropertyName("availableShells")]
    public required string[] AvailableShells { get; init; }

    [JsonPropertyName("rootPath")]
    public string? RootPath { get; init; }

    [JsonPropertyName("agentVersion")]
    public required string AgentVersion { get; init; }

    /// <summary>
    /// Detected CLI versions keyed by runtime ("claude", "codex", "gemini", "copilot"). Null while not probed yet:
    /// the backend keeps what it knows, and the message is sent again once the probe completes.
    /// </summary>
    [JsonPropertyName("cliVersions")]
    public IReadOnlyDictionary<string, string>? CliVersions { get; init; }
}

public class AgentHeartbeatMessage
{
    [JsonPropertyName("type")]
    public string Type => "agent.heartbeat";
}
