using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideHub.Agent;

public class AgentConfig
{
    private const string ConfigFolder = ".sidehub";

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

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonIgnore]
    public string? ConfigFilePath { get; private set; }

    public static List<AgentConfig> LoadAll(string baseDirectory)
    {
        var configDir = Path.Combine(baseDirectory, ConfigFolder);

        if (!Directory.Exists(configDir))
        {
            throw new DirectoryNotFoundException(
                $"Configuration directory not found: {configDir}\n" +
                $"Please create a .sidehub folder with agent configuration files (*.json)");
        }

        var configFiles = Directory.GetFiles(configDir, "*.json");

        if (configFiles.Length == 0)
        {
            throw new FileNotFoundException(
                $"No agent configuration files found in {configDir}\n" +
                "Please create at least one .json configuration file");
        }

        var configs = new List<AgentConfig>();

        foreach (var file in configFiles)
        {
            var config = Load(file);
            configs.Add(config);
        }

        return configs;
    }

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
            throw new InvalidOperationException($"Failed to parse {Path.GetFileName(path)}");
        }

        config.ConfigFilePath = path;
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

        // repositoryId is optional (agents are now at workspace level)

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

    public string GetDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(Name))
            return Name;

        if (!string.IsNullOrWhiteSpace(ConfigFilePath))
            return Path.GetFileNameWithoutExtension(ConfigFilePath);

        return AgentId ?? "unknown";
    }
}
