namespace SideHub.Agent;

public class AgentRunner
{
    private readonly AgentConfig _config;
    private readonly string _baseDirectory;
    private readonly string _displayName;

    public AgentRunner(AgentConfig config, string baseDirectory)
    {
        _config = config;
        _baseDirectory = baseDirectory;
        _displayName = config.GetDisplayName();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var workingDir = _config.GetAbsoluteWorkingDirectory(_baseDirectory);

        Log($"Agent ID: {_config.AgentId}");
        Log($"Workspace ID: {_config.WorkspaceId}");
        Log($"Repository ID: {_config.RepositoryId}");
        Log($"Working directory: {workingDir}");
        Log($"Capabilities: {string.Join(", ", _config.Capabilities!)}");

        if (!Directory.Exists(workingDir))
        {
            Log($"Error: Working directory does not exist: {workingDir}");
            return;
        }

        var executor = new CommandExecutor(workingDir);
        await using var client = new WebSocketClient(_config, executor, workingDir, _displayName);

        await client.RunAsync(ct);

        Log("Shutdown complete");
    }

    private void Log(string message)
    {
        Console.WriteLine($"[{_displayName}] {message}");
    }
}
