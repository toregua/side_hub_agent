using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SideHub.Agent;

/// <summary>
/// `sidehub-agent setup --token &lt;token&gt;`: asks SideHub which agent the token belongs to, writes
/// .sidehub/&lt;file&gt;.json in the current folder (the project), keeps it out of git, then the caller starts the agent.
/// </summary>
public static class AgentSetup
{
    public const string DefaultApi = "https://www.sidehub.io/api";

    public record SetupInfo(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sidehubUrl")] string SidehubUrl,
        [property: JsonPropertyName("agentId")] string AgentId,
        [property: JsonPropertyName("workspaceId")] string WorkspaceId,
        [property: JsonPropertyName("repositoryId")] string? RepositoryId,
        [property: JsonPropertyName("capabilities")] string[]? Capabilities);

    public static async Task<int> Run(string baseDirectory, string token, string? api, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("[SideHub] Error: a token is required: sidehub-agent setup --token <token>");
            return 1;
        }

        var apiBase = (api ?? Environment.GetEnvironmentVariable("SIDEHUB_API") ?? DefaultApi).TrimEnd('/');
        SetupInfo? info;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/agent/setup");
            request.Headers.Add("X-Agent-Token", token.Trim());
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Console.WriteLine("[SideHub] Error: this token doesn't match any agent. Copy the command again from SideHub.");
                return 1;
            }
            response.EnsureSuccessStatusCode();
            info = await response.Content.ReadFromJsonAsync<SetupInfo>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"[SideHub] Error: couldn't reach {apiBase} ({ex.Message}).");
            return 1;
        }

        if (info is null)
        {
            Console.WriteLine("[SideHub] Error: SideHub returned no configuration.");
            return 1;
        }

        var path = WriteConfig(baseDirectory, info, token.Trim());
        Console.WriteLine($"[SideHub] Agent \"{info.Name}\" configured in {path}");
        if (IgnoreInGit(baseDirectory))
            Console.WriteLine("[SideHub] Added .sidehub/ to .gitignore (the file holds the agent's token)");
        return 0;
    }

    /// <summary>
    /// Writes the config next to the others: agent.json, or the file already holding this agent, or a file named
    /// after the agent when agent.json belongs to another one. Returns the path written.
    /// </summary>
    public static string WriteConfig(string baseDirectory, SetupInfo info, string token)
    {
        var dir = Path.Combine(baseDirectory, ".sidehub");
        Directory.CreateDirectory(dir);

        var defaultPath = Path.Combine(dir, "agent.json");
        var path = ExistingFileFor(dir, info.AgentId)
            ?? (File.Exists(defaultPath) ? Path.Combine(dir, $"{Slug(info.Name)}.json") : defaultPath);

        var json = new JsonObject
        {
            ["name"] = info.Name,
            ["sidehubUrl"] = info.SidehubUrl,
            ["agentId"] = info.AgentId,
            ["workspaceId"] = info.WorkspaceId,
            ["agentToken"] = token,
            ["workingDirectory"] = ".",
            ["capabilities"] = new JsonArray((info.Capabilities ?? ["shell", "claude-code"]).Select(c => (JsonNode?)c).ToArray()),
        };
        if (!string.IsNullOrEmpty(info.RepositoryId)) json["repositoryId"] = info.RepositoryId;

        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return path;
    }

    /// <summary>Adds .sidehub/ to the project's .gitignore when the folder is a git repository and it isn't there yet.</summary>
    public static bool IgnoreInGit(string baseDirectory)
    {
        if (!Directory.Exists(Path.Combine(baseDirectory, ".git"))) return false;
        var gitignore = Path.Combine(baseDirectory, ".gitignore");
        var lines = File.Exists(gitignore) ? File.ReadAllLines(gitignore) : [];
        if (lines.Any(l => l.Trim() is ".sidehub" or ".sidehub/" or "/.sidehub" or "/.sidehub/")) return false;

        var prefix = lines.Length > 0 && !string.IsNullOrEmpty(lines[^1]) ? Environment.NewLine : "";
        File.AppendAllText(gitignore, $"{prefix}# SideHub agent (holds its token){Environment.NewLine}.sidehub/{Environment.NewLine}");
        return true;
    }

    private static string? ExistingFileFor(string dir, string agentId)
    {
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file));
                if (node?["agentId"]?.GetValue<string>() == agentId) return file;
            }
            catch (JsonException) { /* not an agent config */ }
        }
        return null;
    }

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(slug) ? "agent" : slug;
    }
}
