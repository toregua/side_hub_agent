using System.Text.Json;
using System.Text.RegularExpressions;

namespace SideHub.Cli.Commands;

/// <summary>
/// The workspace's MCP servers (Owners and Admins): what a workflow or a scheduler may allow its runs to use. A
/// definition never holds a secret value, only <c>${NAME}</c> references to workspace secrets.
/// </summary>
public static partial class McpCommands
{
    private const string SetUsage =
        "Usage: sidehub-cli mcp set <name> --command <cmd> [--arg <a>]* [--env KEY=VALUE]* [--secret NAME]* [--secrets-file dotenv|raw] [--description \"...\"]\n" +
        "       sidehub-cli mcp set <name> --url <url> [--header KEY=VALUE]* [--description \"...\"]\n" +
        "       sidehub-cli mcp set <name> --file <json>   (the request body as is)\n" +
        "Values reference workspace secrets as ${NAME} (sidehub-cli secret list), never a literal secret.";

    private static readonly string[] ValueFlags =
        ["--command", "--arg", "--env", "--url", "--header", "--secret", "--secrets-file", "--description", "--file"];

    public static async Task<int> ListAsync(SideHubApiClient client, bool json)
    {
        var servers = await client.GetMcpServersAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(servers));
            return 0;
        }

        if (servers.ValueKind != JsonValueKind.Array || servers.GetArrayLength() == 0)
        {
            Console.WriteLine("No MCP servers in this workspace.");
            return 0;
        }

        Console.WriteLine($"{"NAME",-24} {"TRANSPORT",-9} {"COMMAND / URL",-50} {"SECRETS"}");
        Console.WriteLine(new string('-', 120));
        foreach (var server in servers.EnumerateArray())
        {
            var target = Prop(server, "transport") == "http"
                ? Prop(server, "url")
                : string.Join(" ", new[] { Prop(server, "command") }.Concat(Strings(server, "args")));
            var missing = Strings(server, "missingSecrets").ToHashSet();
            var secrets = Strings(server, "referencedSecrets").Select(s => missing.Contains(s) ? $"{s} (missing)" : s).ToList();
            Console.WriteLine($"{Prop(server, "name"),-24} {Prop(server, "transport"),-9} {target,-50} {(secrets.Count == 0 ? "-" : string.Join(", ", secrets))}");
            if (Prop(server, "description") is { } description)
                Console.WriteLine($"{"",-24} {description}");
        }
        return 0;
    }

    public static async Task<int> SetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var name = Positionals(args).FirstOrDefault();
        var file = GetOption(args, "--file");
        var command = GetOption(args, "--command");
        var url = GetOption(args, "--url");
        var stdioOnly = new[] { "--arg", "--env", "--secret", "--secrets-file" }.Where(f => args.Contains(f)).ToList();
        var httpOnly = args.Contains("--header");
        var definitionFlags = ValueFlags.Where(f => f != "--file" && args.Contains(f)).ToList();

        if (name is null
            || (file is not null && definitionFlags.Count > 0)
            || (file is null && (command is null) == (url is null))
            || (url is not null && stdioOnly.Count > 0)
            || (command is not null && httpOnly))
        {
            Console.Error.WriteLine(SetUsage);
            return 1;
        }

        object body;
        if (file is not null)
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"Error: file not found: {file}");
                return 1;
            }
            try
            {
                body = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(file));
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Error: {file} is not JSON ({ex.Message}).");
                return 1;
            }
            if (((JsonElement)body).ValueKind != JsonValueKind.Object)
            {
                Console.Error.WriteLine($"Error: {file} must hold a JSON object (transport, command or url...).");
                return 1;
            }
        }
        else
        {
            if (!WorkflowCommands.TryCollectPairs(args, "--env", out var env, out var error)
                || !WorkflowCommands.TryCollectPairs(args, "--header", out var headers, out error))
            {
                Console.Error.WriteLine($"Error: {error}");
                return 1;
            }
            foreach (var key in LiteralSecrets(env).Concat(LiteralSecrets(headers)))
                Console.Error.WriteLine($"Warning: {key} looks like a literal secret: store it as a workspace secret and reference it as ${{NAME}}.");

            body = new
            {
                transport = command is not null ? "stdio" : "http",
                command,
                args = command is not null ? Values(args, "--arg") : null,
                env = command is not null ? env : null,
                url,
                headers = url is not null ? headers : null,
                description = GetOption(args, "--description"),
                secrets = command is not null ? Values(args, "--secret") : null,
                secretsFile = GetOption(args, "--secrets-file"),
            };
        }

        var (server, created) = await client.SetMcpServerAsync(name, body);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(server));
            return 0;
        }

        var secrets = Strings(server, "referencedSecrets").ToList();
        Console.WriteLine($"{(created ? "Created" : "Updated")} MCP server {Prop(server, "name")} ({Prop(server, "transport")}, revision {Prop(server, "revision")})" +
            $"{(secrets.Count > 0 ? $", secrets: {string.Join(", ", secrets)}" : "")}.");
        Console.WriteLine("Allow it in a workflow or a scheduler for its runs to use it.");
        return 0;
    }

    public static async Task<int> DeleteAsync(SideHubApiClient client, string[] args, bool json)
    {
        var name = Positionals(args).FirstOrDefault();
        if (name is null)
        {
            Console.Error.WriteLine("Usage: sidehub-cli mcp delete <name> [--yes]");
            return 1;
        }

        if (!args.Contains("--yes"))
        {
            Console.Error.Write($"Delete MCP server {name}? Workflows and schedulers that allow it will fail to launch. [y/N] ");
            var answer = Console.ReadLine();
            if (answer is null || !answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        await client.DeleteMcpServerAsync(name);

        if (json) Console.WriteLine($"{{\"deleted\":{JsonSerializer.Serialize(name)}}}");
        else Console.WriteLine($"Deleted MCP server: {name}");
        return 0;
    }

    /// <summary>
    /// Keys of <paramref name="entries"/> whose value looks like a secret typed as is: a sensitive name and a value that
    /// references no secret. A hint only, the API stores what it is given.
    /// </summary>
    internal static IEnumerable<string> LiteralSecrets(IReadOnlyDictionary<string, string> entries) =>
        entries.Where(e => SensitiveName().IsMatch(e.Key) && !e.Value.Contains("${") && e.Value.Trim().Length > 0).Select(e => e.Key);

    [GeneratedRegex("token|secret|password|passwd|key|auth|credential|cookie", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    /// <summary>Every value of the repeatable <paramref name="flag"/>, in order (a value may itself start with <c>--</c>).</summary>
    private static List<string> Values(string[] args, string flag)
    {
        var values = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) values.Add(args[++i]);
        }
        return values;
    }

    private static List<string> Positionals(string[] args)
    {
        var result = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (ValueFlags.Contains(args[i])) { i++; continue; }
            if (!args[i].StartsWith("--")) result.Add(args[i]);
        }
        return result;
    }

    private static IEnumerable<string> Strings(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "")
            : [];

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;
}
