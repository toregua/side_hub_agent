using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SideHub.Cli.Launch;

/// <summary>
/// The workspace MCP servers a run may use (<c>pty.start.mcpServers</c>, checked by the agent and handed over in
/// <c>$SIDEHUB_PTY_MCP_SERVERS</c>), given to the coding CLI through per-invocation options only, like
/// <see cref="CliStateHooks"/>: nothing is written in the repository and the user's settings are not touched.
/// Values reference workspace secrets as <c>${NAME}</c>: the secrets stay in the environment (masked in the
/// terminal) and the launcher never puts their values on a command line.
/// <list type="bullet">
/// <item>claude: <c>--mcp-config &lt;json&gt; --strict-mcp-config</c>. Claude expands <c>${NAME}</c> itself; strict, it
/// loads only these servers (no <c>.mcp.json</c>, user config, plugins nor claude.ai connectors).</item>
/// <item>codex: <c>-c mcp_servers.&lt;name&gt;={…}</c>. Codex expands nothing: a secret is read from the environment
/// through <c>env_vars</c>, <c>bearer_token_env_var</c> or <c>env_http_headers</c>, and in a stdio server's
/// arguments or composed variables through <c>/bin/sh -c</c> (not on Windows). Added to the user's own servers:
/// codex has no strict mode.</item>
/// <item>gemini: a system settings file outside the repository (<c>GEMINI_CLI_SYSTEM_SETTINGS_PATH</c>, the machine's
/// own system settings copied in) with these <c>mcpServers</c>, whose <c>$NAME</c> / <c>${NAME}</c> gemini expands;
/// <c>--allowed-mcp-server-names</c> keeps only them.</item>
/// <item>copilot: not supported, the servers are left out.</item>
/// </list>
/// A server a CLI cannot take that way is left out with a warning.
/// </summary>
public static partial class McpServers
{
    public const string Variable = "SIDEHUB_PTY_MCP_SERVERS";
    public const string GeminiSystemSettingsVariable = "GEMINI_CLI_SYSTEM_SETTINGS_PATH";

    public const string Stdio = "stdio";
    public const string Http = "http";

    /// <summary>One server, as in <c>pty.start.mcpServers</c>: <c>command</c>/<c>args</c>/<c>env</c> (stdio) or
    /// <c>url</c>/<c>headers</c> (http).</summary>
    public sealed record Server(
        string Name,
        string Transport,
        string? Command = null,
        IReadOnlyList<string>? Args = null,
        IReadOnlyDictionary<string, string>? Env = null,
        string? Url = null,
        IReadOnlyDictionary<string, string>? Headers = null)
    {
        public IReadOnlyList<string> Args { get; init; } = Args ?? [];
        public IReadOnlyDictionary<string, string> Env { get; init; } = Env ?? new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> Headers { get; init; } = Headers ?? new Dictionary<string, string>();
    }

    [GeneratedRegex(@"\$\{([A-Z][A-Z0-9_]*)\}")]
    private static partial Regex Reference();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The servers in <c>$SIDEHUB_PTY_MCP_SERVERS</c> (a JSON array); none when unset or unreadable.</summary>
    public static IReadOnlyList<Server> Parse(string? json, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<Server>>(json, JsonOptions)?
                .Where(s => !string.IsNullOrEmpty(s.Name) && s.Transport is Stdio or Http)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            warnings.Add($"${Variable} is not valid JSON: no MCP server given to the CLI.");
            return [];
        }
    }

    // ---- claude ----

    /// <summary><c>--mcp-config &lt;json&gt; --strict-mcp-config</c>. <c>--mcp-config</c> takes every following argument
    /// up to the next option, so <c>--strict-mcp-config</c> must come right after it.</summary>
    public static IReadOnlyList<string> ClaudeArguments(IReadOnlyList<Server> servers)
    {
        var config = new JsonObject();
        foreach (var server in servers)
        {
            config[server.Name] = server.Transport == Stdio
                ? new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = server.Command,
                    ["args"] = StringArray(server.Args),
                    ["env"] = StringMap(server.Env),
                }
                : new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = server.Url,
                    ["headers"] = StringMap(server.Headers),
                };
        }
        return ["--mcp-config", new JsonObject { ["mcpServers"] = config }.ToJsonString(), "--strict-mcp-config"];
    }

    // ---- codex ----

    /// <summary>One <c>-c mcp_servers.&lt;name&gt;={…}</c> per server codex can read its secrets for.</summary>
    /// <param name="posixShell">A stdio server may be started through <c>/bin/sh -c</c> (not on Windows).</param>
    public static IReadOnlyList<string> CodexArguments(IReadOnlyList<Server> servers, bool posixShell, List<string> warnings)
    {
        var args = new List<string>();
        foreach (var server in servers)
        {
            var table = server.Transport == Stdio
                ? CodexStdio(server, posixShell, warnings)
                : CodexHttp(server, warnings);
            if (table is not null)
                args.AddRange(["-c", $"mcp_servers.{server.Name}={table}"]);
        }
        return args;
    }

    private static string? CodexStdio(Server server, bool posixShell, List<string> warnings)
    {
        var literalEnv = server.Env.Where(e => !HasReference(e.Value)).ToDictionary();
        var secretEnv = server.Env.Where(e => HasReference(e.Value)).ToList();
        var secrets = References(server.Args.Concat(secretEnv.Select(e => e.Value))).ToList();

        // Each variable is a secret of the same name: codex hands it over from its environment.
        if (!server.Args.Any(HasReference) && secretEnv.All(e => e.Value == $"${{{e.Key}}}"))
            return TomlTable(
                ("command", TomlString(server.Command!)),
                ("args", TomlArray(server.Args)),
                ("env", literalEnv.Count > 0 ? TomlMap(literalEnv) : null),
                ("env_vars", secrets.Count > 0 ? TomlArray(secrets) : null));

        if (!posixShell)
        {
            warnings.Add($"codex: MCP server '{server.Name}' left out: on Windows codex can only pass a secret to it as a variable of the same name.");
            return null;
        }
        // The shell reads the secrets from its environment (codex forwards them): their values are on no command line codex runs.
        var script = new StringBuilder();
        foreach (var (key, value) in secretEnv)
            script.Append("export ").Append(key).Append('=').Append(ShellWord(value)).Append("; ");
        script.Append("exec ").Append(string.Join(' ', new[] { server.Command! }.Concat(server.Args).Select(ShellWord)));
        return TomlTable(
            ("command", TomlString("/bin/sh")),
            ("args", TomlArray(["-c", script.ToString()])),
            ("env", literalEnv.Count > 0 ? TomlMap(literalEnv) : null),
            ("env_vars", TomlArray(secrets)));
    }

    private static string? CodexHttp(Server server, List<string> warnings)
    {
        if (HasReference(server.Url!))
        {
            warnings.Add($"codex: MCP server '{server.Name}' left out: codex cannot read a secret in its URL from the environment.");
            return null;
        }
        var headers = new Dictionary<string, string>();
        var envHeaders = new Dictionary<string, string>();
        string? bearer = null;
        foreach (var (name, value) in server.Headers)
        {
            if (!HasReference(value))
                headers[name] = value;
            else if (WholeReference(value) is { } secret)
                envHeaders[name] = secret;
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                     && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                     && WholeReference(value["Bearer ".Length..].Trim()) is { } token)
                bearer = token;
            else
            {
                warnings.Add($"codex: MCP server '{server.Name}' left out: codex can only read a header from the environment as '${{NAME}}' or 'Bearer ${{NAME}}' ({name}).");
                return null;
            }
        }
        return TomlTable(
            ("url", TomlString(server.Url!)),
            ("bearer_token_env_var", bearer is null ? null : TomlString(bearer)),
            ("http_headers", headers.Count > 0 ? TomlMap(headers) : null),
            ("env_http_headers", envHeaders.Count > 0 ? TomlMap(envHeaders) : null));
    }

    // ---- gemini ----

    /// <summary>
    /// The system settings gemini runs with: <paramref name="systemSettings"/> (the machine's own, JSON with comments;
    /// null when there is none) with <c>mcpServers</c> replaced by the run's, and the <c>--allowed-mcp-server-names</c>
    /// argument. Null when no server is left, or when the machine's system settings cannot be read (they may hold an
    /// administrator's policy, which must not be dropped).
    /// </summary>
    public static (string Settings, IReadOnlyList<string> Arguments)? Gemini(
        IReadOnlyList<Server> servers, string? systemSettings, List<string> warnings)
    {
        JsonObject root;
        try
        {
            root = systemSettings is null
                ? new JsonObject()
                : JsonNode.Parse(systemSettings, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            warnings.Add("gemini: MCP servers left out: the machine's gemini system settings are not valid JSON.");
            return null;
        }

        var config = new JsonObject();
        foreach (var server in servers)
        {
            // gemini expands every $NAME and ${NAME} of its settings: any other '$' would read the environment.
            if (StringsOf(server).Any(value => Reference().Replace(value, "").Contains('$')))
            {
                warnings.Add($"gemini: MCP server '{server.Name}' left out: a '$' other than a ${{SECRET}} reference would be expanded by gemini.");
                continue;
            }
            config[server.Name] = server.Transport == Stdio
                ? new JsonObject
                {
                    ["command"] = server.Command,
                    ["args"] = StringArray(server.Args),
                    ["env"] = StringMap(server.Env),
                }
                : new JsonObject
                {
                    ["httpUrl"] = server.Url,
                    ["headers"] = StringMap(server.Headers),
                };
        }
        if (config.Count == 0)
            return null;

        root["mcpServers"] = config;
        var names = string.Join(',', config.Select(entry => entry.Key));
        return (root.ToJsonString(), ["--allowed-mcp-server-names", names]);
    }

    /// <summary>The system settings file gemini would read: <c>$GEMINI_CLI_SYSTEM_SETTINGS_PATH</c>, else its default
    /// for the OS.</summary>
    public static string GeminiSystemSettingsPath() =>
        Environment.GetEnvironmentVariable(GeminiSystemSettingsVariable) is { Length: > 0 } path ? path
        : OperatingSystem.IsWindows() ? @"C:\ProgramData\gemini-cli\settings.json"
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/GeminiCli/settings.json"
        : "/etc/gemini-cli/settings.json";

    // ---- helpers ----

    private static bool HasReference(string value) => value.Contains("${", StringComparison.Ordinal);

    private static IEnumerable<string> References(IEnumerable<string> values) =>
        values.SelectMany(v => Reference().Matches(v).Select(m => m.Groups[1].Value)).Distinct();

    /// <summary>The secret name when <paramref name="value"/> is exactly <c>${NAME}</c>.</summary>
    private static string? WholeReference(string value) =>
        Reference().Match(value) is { Success: true } m && m.Length == value.Length ? m.Groups[1].Value : null;

    private static IEnumerable<string> StringsOf(Server server) =>
        new[] { server.Command, server.Url }.OfType<string>()
            .Concat(server.Args).Concat(server.Env.Values).Concat(server.Headers.Values);

    /// <summary>One POSIX shell word: literal parts single-quoted, <c>${NAME}</c> references double-quoted, so the
    /// shell expands the references and nothing else.</summary>
    private static string ShellWord(string value)
    {
        var word = new StringBuilder();
        var at = 0;
        foreach (Match m in Reference().Matches(value))
        {
            if (m.Index > at)
                word.Append(SingleQuoted(value[at..m.Index]));
            word.Append('"').Append(m.Value).Append('"');
            at = m.Index + m.Length;
        }
        if (at < value.Length || word.Length == 0)
            word.Append(SingleQuoted(value[at..]));
        return word.ToString();

        static string SingleQuoted(string literal) => "'" + literal.Replace("'", @"'\''") + "'";
    }

    // TOML inline values: JSON string escapes are valid TOML basic-string escapes.
    private static string TomlString(string value) => JsonSerializer.Serialize(value);

    private static string TomlArray(IEnumerable<string> values) => "[" + string.Join(",", values.Select(TomlString)) + "]";

    private static string TomlMap(IReadOnlyDictionary<string, string> map) =>
        "{" + string.Join(",", map.Select(e => $"{TomlString(e.Key)}={TomlString(e.Value)}")) + "}";

    private static string TomlTable(params (string Key, string? Value)[] entries) =>
        "{" + string.Join(",", entries.Where(e => e.Value is not null).Select(e => $"{e.Key}={e.Value}")) + "}";

    private static JsonArray StringArray(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject StringMap(IReadOnlyDictionary<string, string> map) =>
        new(map.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)JsonValue.Create(e.Value))));
}
