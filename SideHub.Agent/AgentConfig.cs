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

    /// <summary>Whether the backend may run one-shot commands (<c>command.execute</c>).
    /// Defaults to true. Turning it off does not sandbox the agent: PTYs still run what the
    /// backend types (see SECURITY.md).</summary>
    [JsonPropertyName("allowCommandExecute")]
    public bool AllowCommandExecute { get; init; } = true;

    /// <summary>Whether the backend may write files into the working directory
    /// (<c>file.write.*</c> and <c>terminal.attachment.enqueue</c>, used for terminal image
    /// uploads). Defaults to true.</summary>
    [JsonPropertyName("allowFileWrite")]
    public bool AllowFileWrite { get; init; } = true;

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

    /// <summary>
    /// Tightens .sidehub/ to 0700 and every config holding a token to 0600, so other users of the machine can't read
    /// the token (older versions wrote them with the umask, usually 0644). Returns one warning per file corrected.
    /// </summary>
    public static List<string> RestrictPermissions(string baseDirectory, IEnumerable<AgentConfig> configs)
    {
        var warnings = new List<string>();
        var configDir = Path.Combine(baseDirectory, ConfigFolder);
        try
        {
            if (PrivateFiles.IsExposed(configDir))
                warnings.Add($"{configDir} was accessible to other users, restricted to its owner (0700)");
            PrivateFiles.CreateDirectory(configDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"couldn't restrict {configDir} to its owner: {ex.Message}");
        }

        foreach (var path in configs.Where(c => !string.IsNullOrEmpty(c.AgentToken)).Select(c => c.ConfigFilePath))
        {
            if (path is null || !PrivateFiles.IsExposed(path)) continue;
            try
            {
                PrivateFiles.RestrictFile(path);
                warnings.Add($"{path} holds the agent token and was readable by other users, restricted to its owner (0600)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{path} holds the agent token and is readable by other users (chmod 600 it): {ex.Message}");
            }
        }
        return warnings;
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
        {
            errors.Add("sidehubUrl is required");
        }
        else if (!Uri.TryCreate(SidehubUrl, UriKind.Absolute, out var uri)
                 || (uri.Scheme != "wss" && uri.Scheme != "ws"))
        {
            errors.Add("sidehubUrl must be a valid WebSocket URL (wss:// or ws://)");
        }
        else if (uri.Scheme == "ws"
                 && uri.Host != "localhost"
                 && uri.Host != "127.0.0.1"
                 && uri.Host != "::1")
        {
            errors.Add(
                "sidehubUrl uses ws:// (unencrypted) with a remote host. " +
                "Use wss:// to protect the agent token in transit. " +
                "ws:// is only allowed for localhost development.");
        }

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
