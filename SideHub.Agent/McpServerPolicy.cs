using System.Text.Json;
using System.Text.RegularExpressions;
using SideHub.Agent.Models;

namespace SideHub.Agent;

/// <summary>
/// Which MCP servers of <c>pty.start.mcpServers</c> (the workspace servers a run may use) the agent hands to the
/// terminal, in <c>$SIDEHUB_PTY_MCP_SERVERS</c>, for <c>sidehub-cli launch</c> to give the CLI. The backend is not
/// trusted to make the CLI read the machine's environment: a value may only reference, as <c>${NAME}</c>, a workspace
/// secret admitted in this PTY (never <c>${SIDEHUB_AGENT_TOKEN}</c>, <c>${PATH}</c> or <c>${HOME}</c>), and an http
/// server must use https (http only to this machine).
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
    /// <param name="rejected">The servers left out, as <c>name (reason)</c>, for logging: no value.</param>
    public static string? ToEnvironmentValue(
        IReadOnlyList<PtyMcpServer>? servers, IReadOnlyCollection<string> admittedSecrets, out IReadOnlyList<string> rejected)
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
            kept.Add(server.Transport == "stdio"
                ? new { name, transport = "stdio", command = server.Command, args = server.Args ?? [], env = server.Env ?? [] }
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
                break;
            case "http":
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
}
