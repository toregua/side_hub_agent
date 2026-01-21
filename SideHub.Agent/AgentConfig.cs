using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideHub.Agent;

public class AgentConfig
{
    [JsonPropertyName("sidehubUrl")]
    public string? SidehubUrl { get; init; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("repositoryId")]
    public string? RepositoryId { get; init; }

    [JsonPropertyName("agentToken")]
    public string? AgentToken { get; init; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }

    [JsonPropertyName("capabilities")]
    public string[]? Capabilities { get; init; }

    public static AgentConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configuration file not found: {path}");
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AgentConfig>(json);

        if (config == null)
        {
            throw new InvalidOperationException("Failed to parse agent.json");
        }

        config.Validate();
        return config;
    }

    public void Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(SidehubUrl))
            errors.Add("sidehubUrl is required");

        if (string.IsNullOrWhiteSpace(AgentId))
            errors.Add("agentId is required");

        if (string.IsNullOrWhiteSpace(WorkspaceId))
            errors.Add("workspaceId is required");

        if (string.IsNullOrWhiteSpace(RepositoryId))
            errors.Add("repositoryId is required");

        if (string.IsNullOrWhiteSpace(AgentToken))
            errors.Add("agentToken is required");

        if (string.IsNullOrWhiteSpace(WorkingDirectory))
            errors.Add("workingDirectory is required");

        if (Capabilities == null || Capabilities.Length == 0)
            errors.Add("capabilities is required and must not be empty");

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Invalid configuration:\n- {string.Join("\n- ", errors)}"
            );
        }
    }

    public string GetAbsoluteWorkingDirectory(string basePath)
    {
        if (Path.IsPathRooted(WorkingDirectory!))
        {
            return WorkingDirectory!;
        }
        return Path.GetFullPath(Path.Combine(basePath, WorkingDirectory!));
    }
}
