namespace SideHub.Agent;

/// <summary>
/// Which daemon environment variables a child shell inherits. The daemon may have been started
/// from a shell holding secrets (API keys, cloud credentials, the setup token) that a command must
/// not see; what the shell needs beyond this list comes from the user's own profile (login shell).
/// Same list as the PTYs: keep in sync with <c>ALLOWED_ENV_NAMES</c> in <c>pty-helper/index.js</c>.
/// Names are compared in upper case (Windows environment names are case-insensitive).
/// </summary>
public static class DaemonEnvironmentPolicy
{
    private static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
    {
        // Identity, paths, locale
        "HOME", "USER", "LOGNAME", "SHELL", "PATH", "LANG", "LANGUAGE", "TZ", "TMPDIR", "EDITOR", "VISUAL", "PAGER",
        // Session plumbing (sockets, not secrets)
        "SSH_AUTH_SOCK", "DBUS_SESSION_BUS_ADDRESS", "WSL_DISTRO_NAME", "WSL_INTEROP",
        // Network proxies, needed by the CLIs behind a corporate proxy
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY",
        // Toolchain locations
        "NVM_DIR", "NVM_BIN", "NVM_INC", "VOLTA_HOME", "PNPM_HOME", "BUN_INSTALL",
        "DOTNET_ROOT", "JAVA_HOME", "GOPATH", "GOROOT", "CARGO_HOME", "RUSTUP_HOME",
        "HOMEBREW_PREFIX", "HOMEBREW_CELLAR", "HOMEBREW_REPOSITORY",
        // CLI configuration directories
        "CLAUDE_CONFIG_DIR", "CODEX_HOME",
        // Windows system variables
        "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "COMSPEC", "PATHEXT", "OS", "TEMP", "TMP",
        "USERNAME", "USERDOMAIN", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "COMPUTERNAME",
        "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432",
        "COMMONPROGRAMFILES", "COMMONPROGRAMFILES(X86)", "PUBLIC",
        "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS",
    };

    private static readonly string[] AllowedPrefixes = ["LC_", "XDG_"];

    public static bool IsAllowed(string name)
    {
        var upper = name.ToUpperInvariant();
        return AllowedNames.Contains(upper) || AllowedPrefixes.Any(prefix => upper.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Drops every variable of <paramref name="environment"/> (a <c>ProcessStartInfo.Environment</c>,
    /// seeded from the daemon's) that is not on the allowlist.</summary>
    public static void Restrict(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(name => !IsAllowed(name)).ToList())
            environment.Remove(name);
    }

    /// <summary>Removes the setup token (the agent token) from this process's environment once it
    /// has been read, so no child (daemon, git, command.execute, pty-helper) inherits it.</summary>
    public static void ClearSetupToken() =>
        Environment.SetEnvironmentVariable(AgentSetup.TokenEnvVar, null);
}
