using System.Text.Json;
using System.Text.RegularExpressions;
using SideHub.Agent.Models;

namespace SideHub.Agent;

/// <summary>
/// Which MCP servers of <c>pty.start.mcpServers</c> (the workspace servers a run may use) the agent hands to the
/// terminal, in <c>$SIDEHUB_PTY_MCP_SERVERS</c>, for <c>sidehub-cli launch</c> to give the CLI. The backend is not
/// trusted to make the CLI read the machine's environment: a value may only reference, as <c>${NAME}</c>, a workspace
/// secret admitted in this PTY (never <c>${SIDEHUB_AGENT_TOKEN}</c>, <c>${PATH}</c> or <c>${HOME}</c>), and an http
/// server must use https (http only to this machine). A stdio server may also get some of those secrets in a private
/// file (<see cref="McpSecretsFiles"/>): <c>${secrets_file}</c> in its arguments and variables becomes the file's path.
/// </summary>
public static partial class McpServerPolicy
{
    public const string EnvironmentKey = "SIDEHUB_PTY_MCP_SERVERS";

    [GeneratedRegex("^[a-z0-9_-]{1,64}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\$\{([A-Z][A-Z0-9_]*)\}")]
    private static partial Regex Reference();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]+$")]
    private static partial Regex HeaderNamePattern();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The <c>$SIDEHUB_PTY_MCP_SERVERS</c> value (JSON array) for the servers that pass, null when none does.
    /// </summary>
    /// <param name="admittedSecrets">The workspace secrets of this PTY's environment (secretKeys the environment
    /// policy admitted).</param>
    /// <param name="writeSecretsFile">Writes the secrets file of a server that has one (checked) and returns its path;
    /// throws <see cref="InvalidOperationException"/> or <see cref="IOException"/> when it cannot. Null: such servers are
    /// left out.</param>
    /// <param name="rejected">The servers left out, as <c>name (reason)</c>, for logging: no value.</param>
    public static string? ToEnvironmentValue(
        IReadOnlyList<PtyMcpServer>? servers, IReadOnlyCollection<string> admittedSecrets,
        Func<PtyMcpServer, string>? writeSecretsFile, out IReadOnlyList<string> rejected)
    {
        var kept = new List<object>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var refused = new List<string>();
        foreach (var server in servers ?? [])
        {
            var name = server.Name ?? "";
            if ((Problem(server, admittedSecrets) ?? (names.Add(name) ? null : "duplicate name")) is { } problem)
            {
                refused.Add($"{(NamePattern().IsMatch(name) ? name : "?")} ({problem})");
                continue;
            }
            var args = server.Args ?? [];
            var env = server.Env ?? [];
            if (server.SecretsFile is not null)
            {
                string path;
                try
                {
                    path = writeSecretsFile?.Invoke(server) ?? throw new InvalidOperationException("secrets files unavailable");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    refused.Add($"{name} (secrets file: {ex.Message})");
                    continue;
                }
                args = args.Select(arg => arg.Replace(McpSecretsFiles.Reference, path, StringComparison.Ordinal)).ToList();
                env = env.ToDictionary(e => e.Key, e => e.Value.Replace(McpSecretsFiles.Reference, path, StringComparison.Ordinal));
            }
            kept.Add(server.Transport == "stdio"
                ? new { name, transport = "stdio", command = server.Command, args, env }
                : new { name, transport = "http", url = server.Url, headers = server.Headers ?? [] });
        }
        rejected = refused;
        return kept.Count > 0 ? JsonSerializer.Serialize(kept, JsonOptions) : null;
    }

    private static string? Problem(PtyMcpServer server, IReadOnlyCollection<string> admittedSecrets)
    {
        if (server.Name is null || !NamePattern().IsMatch(server.Name))
            return "invalid name";

        IEnumerable<string> values;
        switch (server.Transport)
        {
            case "stdio":
                if (string.IsNullOrWhiteSpace(server.Command) || server.Command.Contains('$'))
                    return "invalid command";
                if (server.Env?.Keys.Any(key => !EnvKeyPattern().IsMatch(key)) == true)
                    return "invalid variable name";
                values = (server.Args ?? []).Concat(server.Env?.Values ?? Enumerable.Empty<string>());
                if (server.SecretsFile is not null)
                {
                    if (SecretsFileProblem(server, admittedSecrets) is { } fileProblem)
                        return fileProblem;
                    values = values.Select(value => value.Replace(McpSecretsFiles.Reference, "", StringComparison.Ordinal));
                }
                break;
            case "http":
                if (server.SecretsFile is not null)
                    return "a secrets file is only for a stdio server";
                if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var url)
                    || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)))
                    return "url must be https (http only to localhost)";
                if (server.Headers?.Keys.Any(key => !HeaderNamePattern().IsMatch(key)) == true)
                    return "invalid header name";
                if (server.Headers?.Values.Any(value => value.Any(char.IsControl)) == true)
                    return "invalid header value";
                values = server.Headers?.Values.Append(server.Url!) ?? [server.Url!];
                break;
            default:
                return "unknown transport";
        }

        foreach (var value in values)
        {
            if (value.Contains('\0'))
                return "invalid value";
            foreach (Match reference in Reference().Matches(value))
            {
                if (!admittedSecrets.Contains(reference.Groups[1].Value))
                    return $"references {reference.Groups[1].Value}, not a secret of this run";
            }
            // ${...} other than a secret reference: ${VAR:-default}, ${lower}, an unclosed ${.
            if (Reference().Replace(value, "").Contains("${", StringComparison.Ordinal))
                return "invalid ${...} reference";
        }
        return null;
    }

    private static string? SecretsFileProblem(PtyMcpServer server, IReadOnlyCollection<string> admittedSecrets)
    {
        var secrets = server.Secrets ?? [];
        if (server.SecretsFile is not (McpSecretsFiles.Dotenv or McpSecretsFiles.Raw))
            return "unknown secrets file format";
        if (secrets.Count == 0 || (server.SecretsFile == McpSecretsFiles.Raw && secrets.Count != 1))
            return "wrong number of secrets for its secrets file";
        if (secrets.Distinct(StringComparer.Ordinal).Count() != secrets.Count)
            return "a secret is listed twice for its secrets file";
        // Only the run's workspace secrets go in the file: never the run token or a variable of the machine.
        return secrets.FirstOrDefault(secret => !admittedSecrets.Contains(secret)) is { } unknown
            ? $"secrets file with {(Reference().IsMatch($"${{{unknown}}}") ? unknown : "?")}, not a secret of this run"
            : null;
    }
}
