using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SideHub.Cli;

namespace SideHub.Agent;

/// <summary>
/// `sidehub-agent setup --token &lt;token&gt;`: asks SideHub which agent the token belongs to, writes
/// .sidehub/&lt;file&gt;.json in the current folder (the project), keeps it out of git, then the caller starts the agent.
/// The token itself goes to the <see cref="AgentTokenStore"/>, outside the project.
/// </summary>
public static class AgentSetup
{
    /// <summary>www.sidehub.io serves the web app, not the API.</summary>
    public const string DefaultApi = "https://api.sidehub.io";

    /// <summary>Environment variable holding the token, so it stays out of argv (ps, shell history).
    /// Not SIDEHUB_AGENT_TOKEN: inside a SideHub terminal that one holds the terminal's scoped session token.</summary>
    public const string TokenEnvVar = "SIDEHUB_SETUP_TOKEN";

    public record SetupInfo(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sidehubUrl")] string SidehubUrl,
        [property: JsonPropertyName("agentId")] string AgentId,
        [property: JsonPropertyName("workspaceId")] string WorkspaceId,
        [property: JsonPropertyName("repositoryId")] string? RepositoryId,
        [property: JsonPropertyName("capabilities")] string[]? Capabilities);

    /// <summary>
    /// The token to set up with: read from <paramref name="stdin"/> with <c>--token-stdin</c> or <c>--token -</c>,
    /// else the <c>--token</c> value, else the <see cref="TokenEnvVar"/> environment variable.
    /// </summary>
    public static string ResolveToken(string? tokenFlag, bool fromStdin, TextReader stdin, string? envToken)
    {
        if (fromStdin || tokenFlag == "-")
            return stdin.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(tokenFlag))
            return tokenFlag.Trim();
        return envToken?.Trim() ?? "";
    }

    public static async Task<int> Run(string baseDirectory, string token, string? api, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine($"[SideHub] Error: a token is required: pipe it to `sidehub-agent setup --token-stdin`, " +
                              $"set {TokenEnvVar}, or pass `--token <token>`");
            return 1;
        }

        var apiBase = ApiBase(api ?? Environment.GetEnvironmentVariable("SIDEHUB_API"));
        if (ApiRejectionReason(apiBase) is { } apiProblem)
        {
            Console.WriteLine($"[SideHub] Error: invalid API URL {apiBase}: {apiProblem}.");
            return 1;
        }
        SetupInfo? info;
        try
        {
            // X-Agent-Token is sent: redirects are only followed within the API's origin.
            using var http = new HttpClient(new SameOriginRedirectHandler(new SocketsHttpHandler { AllowAutoRedirect = false }))
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
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
        Console.WriteLine($"[SideHub] Token kept in {AgentTokenStore.ForCurrentUser().PathFor(info.AgentId)}");
        if (await IgnoreInGitAsync(baseDirectory))
            Console.WriteLine("[SideHub] Added .sidehub/ to .git/info/exclude (it holds the agent's configuration and logs)");
        return 0;
    }

    /// <summary>
    /// The API root for REST calls. install.sh passes the host alone (https://api.sidehub.io, where downloads live
    /// under /agent), while the REST routes are under /api: accept both.
    /// </summary>
    public static string ApiBase(string? value)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(value) ? DefaultApi : value).TrimEnd('/');
        return baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/api";
    }

    /// <summary>
    /// Why the API URL must not receive the token, or null when it may: https, or http to the local machine only
    /// (development). Plain http to any other host would hand the token to whoever sits on the network path.
    /// </summary>
    public static string? ApiRejectionReason(string apiBase)
    {
        if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "it must be an absolute http:// or https:// URL";
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            return "http:// (unencrypted) is only allowed for localhost, use https://";
        return null;
    }

    /// <summary>
    /// Writes the config next to the others: agent.json, or the file already holding this agent, or a file named
    /// after the agent when agent.json belongs to another one. The token goes to <paramref name="tokens"/>
    /// (<see cref="AgentTokenStore.ForCurrentUser"/> by default), never into the config. Returns the path written.
    /// </summary>
    public static string WriteConfig(string baseDirectory, SetupInfo info, string token, AgentTokenStore? tokens = null)
    {
        (tokens ?? AgentTokenStore.ForCurrentUser()).Save(info.AgentId, token);

        var dir = Path.Combine(baseDirectory, ".sidehub");
        PrivateFiles.CreateDirectory(dir);

        var defaultPath = Path.Combine(dir, "agent.json");
        var path = ExistingFileFor(dir, info.AgentId)
            ?? (File.Exists(defaultPath) ? Path.Combine(dir, $"{Slug(info.Name)}.json") : defaultPath);

        var json = new JsonObject
        {
            ["name"] = info.Name,
            ["sidehubUrl"] = info.SidehubUrl,
            ["agentId"] = info.AgentId,
            ["workspaceId"] = info.WorkspaceId,
            ["workingDirectory"] = ".",
            ["capabilities"] = new JsonArray((info.Capabilities ?? ["shell", "claude-code"]).Select(c => (JsonNode?)c).ToArray()),
        };
        if (!string.IsNullOrEmpty(info.RepositoryId)) json["repositoryId"] = info.RepositoryId;

        PrivateFiles.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return path;
    }

    /// <summary>
    /// Keeps .sidehub/ out of git when the folder is in a work tree: adds it to the repository's info/exclude, local
    /// to the clone (never committed, unlike .gitignore). Asks git where that file is, so worktrees and submodules
    /// (whose .git is a file, not a folder) are covered too. Returns whether the pattern was added.
    /// </summary>
    public static async Task<bool> IgnoreInGitAsync(string baseDirectory)
    {
        var repository = await GitRepository.OpenAsync(baseDirectory);
        if (repository is null) return false;
        try
        {
            return await repository.ExcludeAsync(Path.Combine(baseDirectory, ".sidehub"));
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[SideHub] Warning: couldn't add .sidehub/ to {repository.ExcludeFile} ({ex.Message}): keep it out of your commits.");
            return false;
        }
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
