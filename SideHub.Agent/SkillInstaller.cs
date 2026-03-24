namespace SideHub.Agent;

/// <summary>
/// Writes provider-specific skill files into the working directory so that
/// LLMs (Claude, Codex, Gemini) discover the sidehub-cli commands.
/// Idempotent: skips if the skill content is already present.
/// </summary>
public static class SkillInstaller
{
    private const string Marker = "# Side Hub Integration";

    private const string SkillContent = """
        # Side Hub Integration

        You have access to the `sidehub-cli` CLI to interact with the Side Hub workspace.
        Environment variables are already configured in your session.

        ## Available commands

        ### Drive (documentation, deliverables)
        - `sidehub-cli drive list` — List pages/folders in the Drive
        - `sidehub-cli drive read <pageId>` — Read the content of a page
        - `sidehub-cli drive create --title "..." --content "..."` — Create a page
        - `sidehub-cli drive update <pageId> --title "..." --content "..."` — Update a page

        ### Tasks
        - `sidehub-cli task list` — List workspace tasks
        - `sidehub-cli task create --title "..." --description "..."` — Create a task
        - `sidehub-cli task comment --text "..."` — Comment on the current task
        - `sidehub-cli task blocker --reason "..."` — Report a blocker on the current task

        ## When to use these commands

        - **Deliverables**: when you produce a significant result (report, analysis, documentation),
          create a Drive page with `sidehub-cli drive create`
        - **Progress**: report your progress via `sidehub-cli task comment` at each key step
        - **Blocked**: if you are stuck, use `sidehub-cli task blocker` instead of spinning in loops
        - **Sub-tasks**: if you identify additional work, create tasks with `sidehub-cli task create`

        ## Conventions
        - Drive content in markdown
        - Comments should be concise (1-3 sentences)
        - Do not create Drive pages for trivial intermediate results
        """;

    // Dedented version (remove leading 8-space indentation from raw string)
    private static readonly string SkillText = string.Join('\n',
        SkillContent.Split('\n').Select(l => l.Length > 8 ? l[8..] : l.TrimStart()));

    public static void EnsureSkillFiles(string workingDirectory, string provider)
    {
        try
        {
            switch (provider.ToLowerInvariant())
            {
                case "claude":
                    EnsureClaudeSkill(workingDirectory);
                    break;
                case "codex":
                    EnsureAppendedSkill(workingDirectory, "AGENTS.md");
                    break;
                case "gemini":
                    EnsureAppendedSkill(workingDirectory, "GEMINI.md");
                    break;
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: log but don't block the session
            Console.WriteLine($"[SkillInstaller] Warning: failed to install skill for {provider}: {ex.Message}");
        }
    }

    private static void EnsureClaudeSkill(string workingDirectory)
    {
        var dir = Path.Combine(workingDirectory, ".claude", "commands");
        var filePath = Path.Combine(dir, "sidehub.md");

        if (File.Exists(filePath))
        {
            var existing = File.ReadAllText(filePath);
            if (existing.Contains(Marker))
                return; // Already installed
        }

        Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, SkillText);
    }

    private static void EnsureAppendedSkill(string workingDirectory, string fileName)
    {
        var filePath = Path.Combine(workingDirectory, fileName);

        if (File.Exists(filePath))
        {
            var existing = File.ReadAllText(filePath);
            if (existing.Contains(Marker))
                return; // Already installed

            // Append with separator
            File.AppendAllText(filePath, "\n\n" + SkillText);
        }
        else
        {
            File.WriteAllText(filePath, SkillText);
        }
    }
}
