using SideHub.Agent;

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[SideHub] Received shutdown signal...");
    cts.Cancel();
};

var baseDirectory = Directory.GetCurrentDirectory();

try
{
    return await RunCommand(args, baseDirectory, cts.Token);
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

static async Task<int> RunCommand(string[] args, string baseDirectory, CancellationToken ct)
{
    var command = args.Length > 0 ? args[0].ToLowerInvariant() : "start";

    return command switch
    {
        "start" => await HandleStart(args, baseDirectory, ct),
        "stop" => Commands.Stop(baseDirectory),
        "logs" => await HandleLogs(args, baseDirectory),
        "status" => Commands.Status(baseDirectory),
        "help" or "--help" or "-h" => ShowHelp(),
        "--foreground-daemon" => await Commands.RunForegroundDaemon(baseDirectory, ct),
        _ => await HandleStart(args, baseDirectory, ct) // Default: treat unknown as start with possible flags
    };
}

static async Task<int> HandleStart(string[] args, string baseDirectory, CancellationToken ct)
{
    var daemon = args.Contains("-d") || args.Contains("--daemon");
    return await Commands.Start(baseDirectory, daemon, ct);
}

static async Task<int> HandleLogs(string[] args, string baseDirectory)
{
    var follow = !args.Contains("--no-follow"); // Follow by default
    return await Commands.Logs(baseDirectory, follow);
}

static int ShowHelp()
{
    Commands.PrintHelp();
    return 0;
}
