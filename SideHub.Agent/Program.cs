using SideHub.Agent;

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[SideHub] Received shutdown signal...");
    cts.Cancel();
};

try
{
    var baseDirectory = Directory.GetCurrentDirectory();
    var configs = AgentConfig.LoadAll(baseDirectory);

    Console.WriteLine($"[SideHub] Found {configs.Count} agent(s) in .sidehub/");

    var tasks = configs.Select(config =>
    {
        var runner = new AgentRunner(config, baseDirectory);
        return runner.RunAsync(cts.Token);
    }).ToList();

    await Task.WhenAll(tasks);

    Console.WriteLine("[SideHub] All agents shut down");
    return 0;
}
catch (DirectoryNotFoundException ex)
{
    Console.WriteLine($"[SideHub] Error: {ex.Message}");
    return 1;
}
catch (FileNotFoundException ex)
{
    Console.WriteLine($"[SideHub] Error: {ex.Message}");
    return 1;
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"[SideHub] Configuration error: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.WriteLine($"[SideHub] Unexpected error: {ex.Message}");
    return 1;
}
