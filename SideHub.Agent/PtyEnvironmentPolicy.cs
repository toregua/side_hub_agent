using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// What the backend may change in a PTY it asks the agent to start: which <c>additionalEnv</c>
/// keys are merged into the shell environment, whether the workspace token is replaced by a
/// run token, and which working directory is used. The backend is not trusted to override
/// the variables that decide what code the shell runs (PATH, LD_PRELOAD, rcfiles…).
/// </summary>
public static partial class PtyEnvironmentPolicy
{
    public const string AgentTokenKey = "SIDEHUB_AGENT_TOKEN";
    private const string SideHubPrefix = "SIDEHUB_";
    private const string RunPtyPrefix = "run-";

    /// <summary>Non-SIDEHUB_* keys the backend may set (run correlation for telemetry).</summary>
    private static readonly HashSet<string> AllowedExtraKeys = new(StringComparer.Ordinal)
    {
        "OTEL_RESOURCE_ATTRIBUTES",
        "OTEL_SERVICE_NAME",
    };

    /// <summary>SIDEHUB_* keys owned by the agent: they point at the API, the notify FIFO, the
    /// wrappers and the bash rcfile, so overriding them would redirect the token or run code.</summary>
    private static readonly HashSet<string> AgentOwnedKeys = new(StringComparer.Ordinal)
    {
        "SIDEHUB_PTY_SESSION_ID",
        "SIDEHUB_PTY_NOTIFY_FIFO",
        "SIDEHUB_CLI_WRAPPERS",
        "SIDEHUB_BASHRC",
        "SIDEHUB_API_URL",
        "SIDEHUB_WORKSPACE_ID",
        "SIDEHUB_AGENT_ID",
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvKeyPattern();

    public static bool IsRunPty(string ptySessionId) =>
        ptySessionId.StartsWith(RunPtyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Keeps the <paramref name="additionalEnv"/> entries the backend may set for this PTY.
    /// <c>SIDEHUB_AGENT_TOKEN</c> is accepted only for <c>run-*</c> PTYs, where it carries the
    /// run token that replaces the workspace token. Rejected keys are returned for logging;
    /// their values never leave this method.
    /// </summary>
    public static Dictionary<string, string> FilterAdditionalEnv(
        string ptySessionId,
        IReadOnlyDictionary<string, string>? additionalEnv,
        out IReadOnlyList<string> rejectedKeys)
    {
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejected = new List<string>();
        if (additionalEnv is null)
        {
            rejectedKeys = rejected;
            return allowed;
        }

        var isRun = IsRunPty(ptySessionId);
        foreach (var (key, value) in additionalEnv)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (IsAllowedKey(key, isRun))
                allowed[key] = value ?? string.Empty;
            else
                rejected.Add(key);
        }

        // An empty run token would leave the CLI without credentials: fall back to the workspace token.
        if (allowed.TryGetValue(AgentTokenKey, out var runToken) && string.IsNullOrWhiteSpace(runToken))
            allowed.Remove(AgentTokenKey);

        rejectedKeys = rejected;
        return allowed;
    }

    private static bool IsAllowedKey(string key, bool isRunPty)
    {
        if (!EnvKeyPattern().IsMatch(key)) return false;
        if (key == AgentTokenKey) return isRunPty;
        if (AgentOwnedKeys.Contains(key)) return false;
        return key.StartsWith(SideHubPrefix, StringComparison.Ordinal) || AllowedExtraKeys.Contains(key);
    }

    /// <summary>
    /// The directory a PTY starts in: <paramref name="requested"/> when it is the agent's working
    /// directory or one of its subfolders (relative paths resolve against it), else the agent's
    /// working directory. Returns false when the request was refused.
    /// </summary>
    public static bool TryResolveWorkingDirectory(string agentWorkingDirectory, string? requested, out string resolved)
    {
        var root = Path.GetFullPath(agentWorkingDirectory);
        resolved = root;
        if (string.IsNullOrWhiteSpace(requested)) return true;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));
        }
        catch
        {
            return false;
        }

        if (!IsWithin(root, candidate) || !IsWithin(RealPath(root), RealPath(candidate)))
            return false;

        resolved = candidate;
        return true;
    }

    private static bool IsWithin(string root, string path)
    {
        var trimmedRoot = root.Length > 1 ? root.TrimEnd(Path.DirectorySeparatorChar) : root;
        if (path == trimmedRoot) return true;
        var prefix = trimmedRoot.EndsWith(Path.DirectorySeparatorChar) ? trimmedRoot : trimmedRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Resolves symlinks segment by segment (missing segments are kept as is), so a link
    /// inside the working directory cannot point the shell outside it.</summary>
    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "/";
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            try
            {
                var info = new DirectoryInfo(next);
                if (info.Exists && info.LinkTarget is not null
                    && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = RealPath(target.FullName);
                    continue;
                }
            }
            catch { /* unreadable: keep the lexical path */ }
            current = next;
        }
        return current;
    }
}
