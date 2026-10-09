using System.Text;

namespace SideHub.Agent;

/// <summary>
/// The secrets file of an MCP server (<c>pty.start.mcpServers[].secretsFile</c>), for a server that only reads its
/// secrets from a file (Playwright MCP <c>--secrets</c>, a Google service account JSON). One file per server and per PTY,
/// 0600 in a 0700 folder of the user's configuration folder (<c>~/.config/sidehub/mcp-secrets/&lt;agent&gt;/&lt;pty&gt;/</c>,
/// <c>%LOCALAPPDATA%\SideHub\mcp-secrets\…</c> on Windows), never in the project, like the agent tokens
/// (<see cref="AgentTokenStore"/>). Deleted when the PTY ends; what a crash left behind is deleted when the agent starts
/// (its PTYs die with it).
/// </summary>
public sealed class McpSecretsFiles(string directory)
{
    /// <summary>Stands for the file's path in the server's arguments and variables.</summary>
    public const string Reference = "${secrets_file}";
    /// <summary><c>NAME=value</c> lines, read by any dotenv parser.</summary>
    public const string Dotenv = "dotenv";
    /// <summary>The value of a single secret, as is.</summary>
    public const string Raw = "raw";

    public string Directory { get; } = directory;

    /// <summary>The files of one agent (its id names the folder, so agents sharing a machine never touch each other's).</summary>
    public static McpSecretsFiles ForAgent(string agentKey)
    {
        if (!NotifyFifo.IsValidPtySessionId(agentKey))
            throw new ArgumentException("Invalid agent key.", nameof(agentKey));
        // On Linux and macOS, ApplicationData is $XDG_CONFIG_HOME, else ~/.config. DoNotVerify: without it the path is
        // empty when the folder doesn't exist yet (~/.config on a fresh server), and it is created on first write.
        var root = Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.LocalApplicationData
            : Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException("Can't locate the user's configuration folder (is HOME set?)");
        return new McpSecretsFiles(Path.Combine(root, OperatingSystem.IsWindows() ? "SideHub" : "sidehub", "mcp-secrets", agentKey));
    }

    /// <summary>Writes the server's file and returns its path.</summary>
    /// <exception cref="InvalidOperationException">A value cannot be written in that format.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public string Write(string ptySessionId, string serverName, string format, IReadOnlyList<KeyValuePair<string, string>> secrets)
    {
        var content = Format(format, secrets);
        var folder = FolderFor(ptySessionId);
        // One at a time: only the folder created last would get the private mode.
        PrivateFiles.CreateDirectory(Directory);
        PrivateFiles.CreateDirectory(folder);
        // The server name is checked by McpServerPolicy: lower-case letters, digits, '-' and '_'.
        var path = Path.Combine(folder, serverName + (format == Raw ? ".secret" : ".env"));
        PrivateFiles.WriteAllText(path, content);
        return path;
    }

    /// <summary>Deletes the PTY's files. Never throws: false when they are still there.</summary>
    public bool Delete(string ptySessionId) =>
        !NotifyFifo.IsValidPtySessionId(ptySessionId) || TryDelete(FolderFor(ptySessionId));

    /// <summary>Deletes every file of this agent: at startup, the PTYs they were written for are gone. Never throws:
    /// false when they are still there.</summary>
    public bool DeleteAll() => TryDelete(Directory);

    /// <summary>
    /// The file's content. <see cref="Dotenv"/>: each value between the first quotes it can sit in as dotenv parsers
    /// read them back unchanged (<c>'</c> and <c>`</c> take anything but themselves; <c>"</c> turns <c>\n</c> into a line
    /// break), refused when none fits. <see cref="Raw"/>: the value of its only secret.
    /// </summary>
    /// <exception cref="InvalidOperationException">An unknown format, or a value that cannot be written in it.</exception>
    public static string Format(string format, IReadOnlyList<KeyValuePair<string, string>> secrets)
    {
        switch (format)
        {
            case Raw when secrets.Count == 1:
                return secrets[0].Value;
            case Raw:
                throw new InvalidOperationException("a raw secrets file holds exactly one secret");
            case Dotenv:
                var content = new StringBuilder();
                foreach (var (name, value) in secrets)
                    content.Append(name).Append('=').Append(DotenvValue(name, value)).Append('\n');
                return content.ToString();
            default:
                throw new InvalidOperationException("unknown secrets file format");
        }
    }

    private static string DotenvValue(string name, string value)
    {
        // Parsers turn \r\n into \n before reading: a carriage return would not come back.
        if (!value.Contains('\r') && !value.EndsWith('\\'))
        {
            foreach (var quote in new[] { '\'', '`', '"' })
            {
                if (value.Contains(quote))
                    continue;
                // Between double quotes, \n and \r become line breaks.
                if (quote == '"' && (value.Contains("\\n", StringComparison.Ordinal) || value.Contains("\\r", StringComparison.Ordinal)))
                    continue;
                return quote + value + quote;
            }
        }
        throw new InvalidOperationException($"the value of {name} cannot be written in a dotenv file (use the raw format)");
    }

    private string FolderFor(string ptySessionId)
    {
        if (!NotifyFifo.IsValidPtySessionId(ptySessionId))
            throw new ArgumentException("Invalid PTY session id.", nameof(ptySessionId));
        return Path.Combine(Directory, ptySessionId);
    }

    private static bool TryDelete(string folder)
    {
        try
        {
            if (System.IO.Directory.Exists(folder))
                System.IO.Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
