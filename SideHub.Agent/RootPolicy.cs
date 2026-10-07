namespace SideHub.Agent;

/// <summary>
/// Refuses to set up or run the agent as root (Unix). The backend drives the agent's terminals, so
/// running it as root hands the backend the whole machine; `curl … | sudo bash` with a token used to do
/// exactly that. <c>--allow-root</c> (or <see cref="AllowEnvVar"/>=1) opts in explicitly.
/// </summary>
public static class RootPolicy
{
    public const string AllowFlag = "--allow-root";

    /// <summary>For service managers and scripts that cannot add a flag (the daemon gets the flag forwarded).</summary>
    public const string AllowEnvVar = "SIDEHUB_ALLOW_ROOT";

    /// <summary>Commands that neither write the config nor run the agent, so root may use them (e.g. to stop a
    /// daemon). Everything else is guarded, including unknown commands, which run as <c>start</c>.</summary>
    private static readonly HashSet<string> UnguardedCommands = new(StringComparer.Ordinal)
    {
        "stop", "status", "logs", "help", "--help", "-h",
    };

    public static bool IsGuarded(string command) => !UnguardedCommands.Contains(command);

    public static bool IsAllowedExplicitly(string[] args, string? envValue) =>
        args.Contains(AllowFlag) || envValue is "1" || string.Equals(envValue, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The error to print, or null when the command may run.</summary>
    public static string? Check(string command, string[] args, bool isRoot, string? envValue)
    {
        if (!isRoot || !IsGuarded(command) || IsAllowedExplicitly(args, envValue))
            return null;

        return "Refusing to run as root: the SideHub backend drives this agent's terminals, so as root it would control the whole machine.\n" +
               "[SideHub] Run it as an unprivileged user that owns the project (`sidehub-agent service install` then starts it at boot),\n" +
               $"[SideHub] or pass {AllowFlag} (or set {AllowEnvVar}=1) if you really mean it.";
    }

    /// <summary>The warning printed when root was allowed explicitly.</summary>
    public const string Warning =
        "WARNING: running as root (explicitly allowed): the SideHub backend has full control of this machine.";

    /// <summary>Effective UID 0 on Unix. Windows (elevated or not) is not guarded.</summary>
    public static bool IsCurrentUserRoot() => !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess;
}
