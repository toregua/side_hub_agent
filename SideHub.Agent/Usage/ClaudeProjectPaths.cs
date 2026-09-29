using System.Text.RegularExpressions;

namespace SideHub.Agent.Usage;

/// <summary>
/// Where Claude Code keeps its session transcripts:
/// <c>$CLAUDE_CONFIG_DIR|~/.claude/projects/&lt;encoded cwd&gt;/&lt;cliSessionId&gt;.jsonl</c>.
/// </summary>
public static partial class ClaudeProjectPaths
{
    /// <summary>The projects root, or null when neither CLAUDE_CONFIG_DIR nor HOME is known.</summary>
    public static string? ProjectsRoot()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(configDir))
            return Path.Combine(configDir, "projects");

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".claude", "projects");
    }

    /// <summary>
    /// Claude replaces every non-alphanumeric character of the cwd with '-'
    /// (<c>/root/Github/side_hub</c> → <c>-root-Github-side-hub</c>).
    /// </summary>
    public static string EncodeCwd(string cwd) => NonAlphanumeric().Replace(cwd, "-");

    public static string ProjectDirectory(string projectsRoot, string cwd) =>
        Path.Combine(projectsRoot, EncodeCwd(cwd));

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NonAlphanumeric();
}
