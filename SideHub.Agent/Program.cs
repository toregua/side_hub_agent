using SideHub.Agent;

const string ConfigFileName = "agent.json";

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[Agent] Received shutdown signal...");
    cts.Cancel();
};

try
{
    var configPath = Path.Combine(Directory.GetCurrentDirectory(), ConfigFileName);
    Console.WriteLine($"[Agent] Loading configuration from {configPath}");

    var config = AgentConfig.Load(configPath);
    var workingDir = config.GetAbsoluteWorkingDirectory(Directory.GetCurrentDirectory());

    Console.WriteLine($"[Agent] Agent ID: {config.AgentId}");
    Console.WriteLine($"[Agent] Workspace ID: {config.WorkspaceId}");
    Console.WriteLine($"[Agent] Repository ID: {config.RepositoryId}");
    Console.WriteLine($"[Agent] Working directory: {workingDir}");
    Console.WriteLine($"[Agent] Capabilities: {string.Join(", ", config.Capabilities!)}");

    if (!Directory.Exists(workingDir))
    {
        Console.WriteLine($"[Agent] Error: Working directory does not exist: {workingDir}");
        return 1;
    }

    var executor = new CommandExecutor(workingDir);
    await using var client = new WebSocketClient(config, executor);

    await client.RunAsync(cts.Token);

    Console.WriteLine("[Agent] Shutdown complete");
    return 0;
}
catch (FileNotFoundException ex)
{
    Console.WriteLine($"[Agent] Error: {ex.Message}");
    Console.WriteLine($"[Agent] Please create an {ConfigFileName} file in the current directory.");
    return 1;
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"[Agent] Configuration error: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.WriteLine($"[Agent] Unexpected error: {ex.Message}");
    return 1;
}
