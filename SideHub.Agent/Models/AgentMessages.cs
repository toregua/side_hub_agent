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

    [JsonPropertyName("repositoryId")]
    public required string RepositoryId { get; init; }

    [JsonPropertyName("capabilities")]
    public required string[] Capabilities { get; init; }

    [JsonPropertyName("defaultShell")]
    public required string DefaultShell { get; init; }

    [JsonPropertyName("availableShells")]
    public required string[] AvailableShells { get; init; }
}

public class AgentHeartbeatMessage
{
    [JsonPropertyName("type")]
    public string Type => "agent.heartbeat";
}
