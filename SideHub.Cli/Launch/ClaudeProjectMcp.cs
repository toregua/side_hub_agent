using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>
/// The MCP servers a project declares for claude (<c>.mcp.json</c> in the folder or one of its parents) are approved one
/// by one: the first interactive launch asks about each server nobody decided on yet, "Continue without using this MCP
/// server" selected. What SideHub launches while nobody watches (see <see cref="ClaudeWorkspaceTrust"/>) gets that
/// answer through <c>--settings</c> (<c>disabledMcpjsonServers</c>), for this launch only: no settings file is written,
/// and a server someone already enabled or disabled is left to that decision.
/// </summary>
public static class ClaudeProjectMcp
{
    /// <summary>The servers declared for <paramref name="directory"/> that no settings decide on yet, in declaration order.</summary>
    /// <param name="home">The user's home (<c>~/.claude/settings.json</c>, <c>~/.claude.json</c>), null when unknown.</param>
    public static IReadOnlyList<string> Undecided(string directory, string? home)
    {
        var declared = new List<string>();
        for (var folder = new DirectoryInfo(directory); folder is not null; folder = folder.Parent)
            declared.AddRange(DeclaredIn(Path.Combine(folder.FullName, ".mcp.json")));
        if (declared.Count == 0)
            return [];

        var decided = new HashSet<string>(StringComparer.Ordinal);
        var settings = new List<JsonObject?>
        {
            Read(Path.Combine(directory, ".claude", "settings.json")),
            Read(Path.Combine(directory, ".claude", "settings.local.json")),
        };
        if (!string.IsNullOrEmpty(home))
        {
            settings.Add(Read(Path.Combine(home, ".claude", "settings.json")));
            // Older claude versions kept the decisions per project in their global config.
            settings.Add(Read(Path.Combine(home, ".claude.json"))?["projects"]?[ClaudeWorkspaceTrust.ProjectKey(directory)] as JsonObject);
        }
        foreach (var source in settings.OfType<JsonObject>())
        {
            if (source["enableAllProjectMcpServers"] is JsonValue all && all.TryGetValue<bool>(out var enabled) && enabled)
                return [];
            foreach (var key in new[] { "enabledMcpjsonServers", "disabledMcpjsonServers" })
                if (source[key] is JsonArray names)
                    foreach (var name in names)
                        if (name is JsonValue value && value.TryGetValue<string>(out var text))
                            decided.Add(text);
        }
        return declared.Distinct(StringComparer.Ordinal).Where(name => !decided.Contains(name)).ToList();
    }

    private static IEnumerable<string> DeclaredIn(string mcpJson) =>
        Read(mcpJson)?["mcpServers"] is JsonObject servers ? servers.Select(s => s.Key) : [];

    /// <summary>A JSON object file; null when missing or unreadable (a broken file decides nothing).</summary>
    private static JsonObject? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
