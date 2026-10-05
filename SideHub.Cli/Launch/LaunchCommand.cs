using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SideHub.Cli.Launch;

/// <summary>
/// <c>sidehub-cli launch [--prompt-env | --prompt-base64 &lt;b64&gt;] &lt;cli&gt; [args…]</c>: starts a coding CLI in
/// the current terminal and tells the agent which session it runs (see <see cref="CliLaunchPlan"/>).
/// <para>
/// The line SideHub types to start a CLI is then the same in bash, cmd and PowerShell: no quotes, no
/// <c>$</c>. With <c>--prompt-env</c> the prompt comes from <c>$SIDEHUB_PTY_PROMPT</c> and is handed to the CLI
/// as one argument, never parsed by a shell (cmd.exe cannot carry quotes, <c>%</c> or newlines in an argument).
/// </para>
/// </summary>
public static class LaunchCommand
{
    public const string PromptVariable = "SIDEHUB_PTY_PROMPT";
    private const string WrappersVariable = "SIDEHUB_CLI_WRAPPERS";

    public static int Run(string[] args)
    {
        if (!TryReadPrompt(ref args, out var prompt, out var problem))
        {
            Console.Error.WriteLine($"sidehub-cli launch: {problem}");
            return 2;
        }
        if (args.Length == 0 || !CliLaunchPlan.KnownClis.Contains(args[0]))
        {
            Console.Error.WriteLine($"Usage: sidehub-cli launch [--prompt-env | --prompt-base64 <utf-8 base64>] <{string.Join("|", CliLaunchPlan.KnownClis)}> [args...]");
            return 2;
        }
        var cli = args[0];

        var target = RealCli.Resolve(cli, Environment.GetEnvironmentVariable("PATH"), WrapperDirectories());
        if (target is null)
        {
            Console.Error.WriteLine($"sidehub-cli launch: '{cli}' is not installed (not found in PATH).");
            return 127;
        }

        var geminiVersion = cli == "gemini" ? RealCli.PackageVersion(target.ScriptPath, "@google/gemini-cli") : null;
        var warnings = new List<string>();
        var plan = CliLaunchPlan.For(cli, args[1..], prompt, geminiVersion, Guid.NewGuid, StateReporting(cli), McpSetup(cli, warnings),
            ToolPolicy());
        if (plan.Refusal is { } refusal)
        {
            Console.Error.WriteLine($"sidehub-cli launch: {refusal}");
            return 2;
        }
        foreach (var warning in warnings.Concat(plan.Warnings))
            Console.Error.WriteLine($"sidehub-cli launch: {warning}");

        if (plan.SessionId is { } sessionId)
            AgentNotifier.SessionStarted(cli, sessionId);

        var startInfo = new ProcessStartInfo(target.FileName) { UseShellExecute = false };
        foreach (var arg in target.LeadingArguments.Concat(plan.Arguments))
            startInfo.ArgumentList.Add(arg);
        // The CLI must not see the prompt twice (some read their environment into sub-processes).
        startInfo.Environment.Remove(PromptVariable);

        var geminiSettingsDirectory = plan.GeminiSystemSettings is { } settings ? WriteGeminiSettings(settings, startInfo) : null;
        try
        {
            return Start(cli, startInfo, target, plan);
        }
        finally
        {
            if (geminiSettingsDirectory is not null)
                try { Directory.Delete(geminiSettingsDirectory, recursive: true); } catch (IOException) { }
        }
    }

    private static int Start(string cli, ProcessStartInfo startInfo, RealCli.Target target, CliLaunchPlan plan)
    {
        // Ctrl+C reaches every process of the terminal: it is the CLI's to handle, the launcher waits.
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context => context.Cancel = true);

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // e.g. Windows caps a command line at 32,767 characters, prompt included.
            Console.Error.WriteLine($"sidehub-cli launch: could not start {target.FileName}: {ex.Message}");
            return 126;
        }
        using var _ = process;
        if (plan.ReportLaunch)
            AgentNotifier.Launched(cli, Directory.GetCurrentDirectory(), process.Id);
        process.WaitForExit();
        // The session's files are complete: the agent reports its final usage.
        if (plan.SessionId is not null || plan.ReportLaunch)
            AgentNotifier.Exited(cli, plan.SessionId);
        return process.ExitCode;
    }

    /// <summary>The CLI's hooks call this program back (<c>sidehub-cli cli-state</c>) from inside the terminal, to
    /// tell the agent what the CLI is doing. Only in a SideHub terminal: elsewhere there is no agent to tell.</summary>
    private static CliLaunchPlan.StateReporting? StateReporting(string cli)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AgentNotifier.ChannelVariable)) || HookProgram() is not { } program)
            return null;
        var codexNotifyTaken = cli == "codex" && CliStateHooks.CodexConfigDefinesNotify(CliStateHooks.CodexConfigPath());
        return new CliLaunchPlan.StateReporting(program, codexNotifyTaken);
    }

    /// <summary>The run's tool policy (<c>$SIDEHUB_TOOL_POLICY_MATCHER</c>, set by the backend for a workflow step),
    /// null when it has none. Unlike the state hooks, it needs no agent channel: only this program.</summary>
    private static CliLaunchPlan.ToolPolicy? ToolPolicy() =>
        Environment.GetEnvironmentVariable(PolicyCheckCommand.MatcherVariable) is { Length: > 0 } matcher
            ? new CliLaunchPlan.ToolPolicy(matcher, HookProgram())
            : null;

    /// <summary>This program's absolute path, for the CLI's hooks to run it; null when it cannot be run by path.</summary>
    private static string? HookProgram() =>
        Environment.ProcessPath is { } program && Path.IsPathFullyQualified(program)
            // Run through `dotnet sidehub-cli.dll` (development), the process is dotnet itself.
            && Path.GetFileNameWithoutExtension(program).Equals("sidehub-cli", StringComparison.OrdinalIgnoreCase)
            ? program
            : null;

    /// <summary>The run's MCP servers (<c>$SIDEHUB_PTY_MCP_SERVERS</c>, set by the agent), null when there are none.</summary>
    private static CliLaunchPlan.McpSetup? McpSetup(string cli, List<string> warnings)
    {
        var servers = McpServers.Parse(Environment.GetEnvironmentVariable(McpServers.Variable), warnings);
        if (servers.Count == 0)
            return null;
        string? geminiSettings = null;
        if (cli == "gemini")
        {
            var path = McpServers.GeminiSystemSettingsPath();
            try
            {
                geminiSettings = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"gemini: MCP servers left out: cannot read the machine's gemini system settings ({path}).");
                return null;
            }
        }
        return new CliLaunchPlan.McpSetup(servers, PosixShell: !OperatingSystem.IsWindows(), geminiSettings);
    }

    /// <summary>gemini's system settings for this launch, in a private temporary folder (never in the repository):
    /// they hold the servers' definitions, whose secrets stay <c>${NAME}</c> references. Returns the folder.</summary>
    private static string? WriteGeminiSettings(string settings, ProcessStartInfo startInfo)
    {
        try
        {
            var directory = Directory.CreateTempSubdirectory("sidehub-gemini-").FullName;
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, settings);
            startInfo.Environment[McpServers.GeminiSystemSettingsVariable] = path;
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"sidehub-cli launch: gemini: MCP servers left out: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The launcher's own options, before the CLI name: the prompt to append, read from <c>$SIDEHUB_PTY_PROMPT</c>
    /// (<c>--prompt-env</c>: set when the PTY was created) or given in UTF-8 base64 (<c>--prompt-base64</c>: for a
    /// shell already running; its alphabet means the same in every shell).
    /// </summary>
    public static bool TryReadPrompt(ref string[] args, out string? prompt, out string? problem)
    {
        prompt = null;
        problem = null;
        if (args.Length > 0 && args[0] == "--prompt-env")
        {
            prompt = Environment.GetEnvironmentVariable(PromptVariable);
            args = args[1..];
            if (string.IsNullOrEmpty(prompt))
                problem = $"--prompt-env given but ${PromptVariable} is empty.";
        }
        else if (args.Length > 0 && args[0] == "--prompt-base64")
        {
            try
            {
                prompt = args.Length > 1 ? new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(args[1])) : null;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                prompt = null;
            }
            args = args.Length > 1 ? args[2..] : [];
            if (string.IsNullOrEmpty(prompt))
                problem = "--prompt-base64 needs a non-empty UTF-8 text in base64.";
        }
        return problem is null;
    }

    /// <summary>SideHub's wrappers (<c>cli-wrappers/</c> next to this program, or the agent's in the terminal):
    /// they call the launcher, so resolving to one would loop.</summary>
    private static List<string> WrapperDirectories()
    {
        var dirs = new List<string> { Path.Combine(AppContext.BaseDirectory, "cli-wrappers") };
        if (Environment.GetEnvironmentVariable(WrappersVariable) is { Length: > 0 } fromAgent)
            dirs.Add(fromAgent);
        return dirs;
    }
}
