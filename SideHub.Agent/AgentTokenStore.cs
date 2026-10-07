namespace SideHub.Agent;

/// <summary>
/// Where the agent tokens live: one file per agent in the user's configuration folder (<c>~/.config/sidehub/tokens/</c>,
/// <c>$XDG_CONFIG_HOME/sidehub/tokens/</c> when set, <c>%LOCALAPPDATA%\SideHub\tokens\</c> on Windows), never in the
/// project. <c>.sidehub/</c> sits in the work tree, the working directory of every terminal: a CLI running there (prompt
/// injection, a tool listing the folder) would find the token right next to it. Files are 0600 in a 0700 folder.
/// </summary>
public sealed class AgentTokenStore(string directory)
{
    public string Directory { get; } = directory;

    public static AgentTokenStore ForCurrentUser() => new(DefaultDirectory());

    private static string DefaultDirectory()
    {
        // On Linux and macOS, ApplicationData is $XDG_CONFIG_HOME, else ~/.config.
        var root = Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.LocalApplicationData
            : Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException("Can't locate the user's configuration folder to keep the agent token (is HOME set?)");
        return Path.Combine(root, OperatingSystem.IsWindows() ? "SideHub" : "sidehub", "tokens");
    }

    public string PathFor(string agentId)
    {
        // The id comes from a config file: it names a file of the store, never a path out of it.
        if (agentId.Length == 0 || !agentId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidOperationException($"Invalid agentId '{agentId}': letters, digits, '-' and '_' only");
        return Path.Combine(Directory, $"{agentId.ToLowerInvariant()}.token");
    }

    /// <summary>The agent's token, or null when none is stored.</summary>
    public string? Read(string agentId)
    {
        var path = PathFor(agentId);
        if (!File.Exists(path))
            return null;
        PrivateFiles.EnsureTrusted(path);
        var token = File.ReadAllText(path).Trim();
        return token.Length == 0 ? null : token;
    }

    public void Save(string agentId, string token)
    {
        var path = PathFor(agentId);
        PrivateFiles.CreateDirectory(Directory);
        PrivateFiles.WriteAllText(path, token.Trim() + Environment.NewLine);
    }
}
