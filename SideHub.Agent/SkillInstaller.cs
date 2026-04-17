using System.Net.Http.Json;
using System.Text.Json;

namespace SideHub.Agent;

/// <summary>
/// Writes provider-specific skill files into the working directory so that
/// LLMs (Claude, Codex, Gemini) discover the sidehub-cli commands.
/// Now fetches a lightweight drive index at spawn time so agents are aware
/// of existing workspace memory without loading full content.
/// </summary>
public static class SkillInstaller
{
    private const string Marker = "# Side Hub Integration";

    private static string BuildSkillContent(string driveIndex)
    {
        return $"""
# Side Hub Integration

You have access to the `sidehub-cli` CLI to interact with the Side Hub workspace.
Environment variables are already configured in your session.

## Available commands

### Drive (workspace memory)
- `sidehub-cli drive list` — List pages/folders in the Drive
- `sidehub-cli drive read <pageId>` — Read the content of a page (text). For binary files, prints a hint to use `drive download` instead.
- `sidehub-cli drive download <pageId> [--output <path>] [--stdout] [--url-only]` — Download a binary file (image/PDF/...) from the Drive. Defaults to the current directory using the original filename, then prints the absolute path. Use `--output <path>` to choose a destination, `--stdout` to stream raw bytes, or `--url-only` to print just the presigned URL.
- `sidehub-cli drive search <query>` — Search pages by title
- `sidehub-cli drive create --title "..." --content "..."` — Create a page
- `sidehub-cli drive update <pageId> --title "..." --content "..."` — Update a page

### Tasks
- `sidehub-cli task list [--status <status>]` — List workspace tasks (filter by status)
- `sidehub-cli task create --title "..." [--description "..."] [--type <type>]` — Create a task
- `sidehub-cli task comment [<taskId>] --text "..."` — Comment on the current task
- `sidehub-cli task blocker [<taskId>] --reason "..."` — Report a blocker on the current task

### Schedulers
- `sidehub-cli scheduler list [--active | --paused]` — List scheduled prompts
- `sidehub-cli scheduler get <id>` — Show scheduler details
- `sidehub-cli scheduler create --title "..." --prompt "..." --cron "..." [--description "..."] [--provider <provider>]` — Create a scheduler
- `sidehub-cli scheduler update <id> [--title "..."] [--prompt "..."] [--cron "..."] [--description "..."] [--provider <provider>]` — Update a scheduler
- `sidehub-cli scheduler delete <id> [--yes]` — Delete a scheduler (use --yes to skip confirmation)
- `sidehub-cli scheduler pause <id>` — Pause a scheduler
- `sidehub-cli scheduler resume <id>` — Resume a paused scheduler
- `sidehub-cli scheduler trigger <id>` — Trigger immediate execution
- `sidehub-cli scheduler executions <id>` — Show execution history

## Workspace memory (Drive)

The Drive is your persistent memory across sessions. Use it to store and retrieve
knowledge that will help you and other agents work more effectively.

{driveIndex}
## When to READ from Drive

- **Before starting a task**: scan the index above — if a page title looks relevant
  to your task, read it with `sidehub-cli drive read <id>`
- **When you need context**: past decisions, architecture notes, previous results
- **When the task references concepts** you don't fully understand
- Do NOT read everything "just in case" — use the index to judge relevance first

## When to WRITE to Drive

- **After completing a task**: save learnings, decisions, or results worth reusing
- **When you discover something** that would help future tasks (patterns, gotchas, decisions)
- **When producing a deliverable** (report, analysis, documentation)
- Do NOT create pages for trivial intermediate results or debug output
- Do NOT duplicate information already in git history

## Other conventions

- **Progress**: report your progress via `sidehub-cli task comment` at each key step
- **Blocked**: if you are stuck, use `sidehub-cli task blocker` instead of spinning in loops
- **Sub-tasks**: if you identify additional work, create tasks with `sidehub-cli task create`
- Drive content in markdown
- Comments should be concise (1-3 sentences)
""";
    }

    /// <summary>
    /// Fetches the drive tree and builds a compact index (titles + IDs only).
    /// Returns an empty section if the fetch fails or the drive is empty.
    /// </summary>
    private static async Task<string> FetchDriveIndexAsync(string apiUrl, string agentToken, string workspaceId)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") };
            http.DefaultRequestHeaders.Add("X-Agent-Token", agentToken);
            http.Timeout = TimeSpan.FromSeconds(5);

            var resp = await http.GetAsync($"api/workspaces/{workspaceId}/drive");
            if (!resp.IsSuccessStatusCode)
                return "No pages currently in the workspace drive.\n";

            var tree = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (!tree.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                return "No pages currently in the workspace drive.\n";

            var lines = new List<string>();
            CollectPageTitles(items, lines, depth: 0);

            if (lines.Count == 0)
                return "No pages currently in the workspace drive.\n";

            return "Pages available in your workspace drive:\n" + string.Join('\n', lines) + "\n\nUse `sidehub-cli drive read <id>` to load any page you need.\n";
        }
        catch
        {
            return "Drive index unavailable (fetch failed). Use `sidehub-cli drive list` to discover pages.\n";
        }
    }

    private static void CollectPageTitles(JsonElement items, List<string> lines, int depth)
    {
        foreach (var item in items.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var type = item.GetProperty("type").GetString() ?? "";
            var title = item.GetProperty("title").GetString() ?? "";
            var indent = new string(' ', depth * 2);

            if (type == "folder")
                lines.Add($"{indent}- 📁 **{title}**/");
            else
                lines.Add($"{indent}- `{id}` — {title}");

            if (item.TryGetProperty("children", out var children) && children.GetArrayLength() > 0)
                CollectPageTitles(children, lines, depth + 1);
        }
    }

    public static async Task EnsureSkillFilesAsync(string workingDirectory, string provider,
        string apiUrl, string agentToken, string workspaceId)
    {
        try
        {
            var driveIndex = await FetchDriveIndexAsync(apiUrl, agentToken, workspaceId);
            var skillText = BuildSkillContent(driveIndex);

            switch (provider.ToLowerInvariant())
            {
                case "claude":
                    EnsureClaudeSkill(workingDirectory, skillText);
                    break;
                case "codex":
                    EnsureAppendedSkill(workingDirectory, "AGENTS.md", skillText);
                    break;
                case "gemini":
                    EnsureAppendedSkill(workingDirectory, "GEMINI.md", skillText);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: log but don't block the session
            Console.WriteLine($"[SkillInstaller] Warning: failed to install skill for {provider}: {ex.Message}");
        }
    }

    private static void EnsureClaudeSkill(string workingDirectory, string skillText)
    {
        var dir = Path.Combine(workingDirectory, ".claude", "commands");
        var filePath = Path.Combine(dir, "sidehub.md");

        // Always overwrite — the drive index may have changed since last spawn
        Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, skillText);
    }

    private static void EnsureAppendedSkill(string workingDirectory, string fileName, string skillText)
    {
        var filePath = Path.Combine(workingDirectory, fileName);

        if (File.Exists(filePath))
        {
            var existing = File.ReadAllText(filePath);
            if (existing.Contains(Marker))
            {
                // Replace existing skill section with updated content
                var markerIndex = existing.IndexOf(Marker, StringComparison.Ordinal);
                var before = existing[..markerIndex].TrimEnd();
                File.WriteAllText(filePath, (before.Length > 0 ? before + "\n\n" : "") + skillText);
                return;
            }

            // Append with separator
            File.AppendAllText(filePath, "\n\n" + skillText);
        }
        else
        {
            File.WriteAllText(filePath, skillText);
        }
    }
}
