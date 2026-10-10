using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>
/// The first time claude runs interactively in a folder, it asks whether to trust it, "No, exit" selected; headless
/// (<c>-p</c>) it never asks. What SideHub launches interactively has nobody to answer yet: an interactive workflow
/// step (its agent drafts first, and the validation SideHub types later would pick "No, exit"), a task of the agent's
/// queue (launched while nobody watches). Its folder is the agent's repository, where SideHub's headless runs already
/// work without the dialog: it is marked trusted, as accepting the dialog would
/// (<c>projects.&lt;folder&gt;.hasTrustDialogAccepted</c> in claude's global config).
/// </summary>
public static class ClaudeWorkspaceTrust
{
    public const string RunIdVariable = "SIDEHUB_RUN_ID";
    public const string TaskIdVariable = "SIDEHUB_TASK_ID";

    /// <summary>A claude launched by SideHub for a run (<c>$SIDEHUB_RUN_ID</c>) or a task (<c>$SIDEHUB_TASK_ID</c>),
    /// interactive: the one that would ask.</summary>
    public static bool Applies(string cli, IReadOnlyList<string> arguments, string? runId, string? taskId = null) =>
        cli == "claude" && (!string.IsNullOrEmpty(runId) || !string.IsNullOrEmpty(taskId))
        && !arguments.Any(a => a is "-p" or "--print");

    /// <summary><c>$CLAUDE_CONFIG_DIR/.claude.json</c>, else <c>~/.claude.json</c>; null without a home.</summary>
    public static string? ConfigPath()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configDir)
            return Path.Combine(configDir, ".claude.json");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".claude.json");
    }

    /// <summary>claude keys its projects by path with forward slashes, on Windows too.</summary>
    public static string ProjectKey(string directory) =>
        OperatingSystem.IsWindows() ? directory.Replace('\\', '/') : directory;

    /// <summary>
    /// Marks <paramref name="directory"/> trusted in <paramref name="configPath"/>, keeping everything else. Written to a
    /// private temporary file then moved over the config, so claude never reads half a file. Returns false when it already
    /// was; throws on an unreadable or invalid config (it is then left alone).
    /// </summary>
    public static bool Accept(string configPath, string directory)
    {
        var root = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                ?? throw new InvalidDataException($"{configPath} is not a JSON object.")
            : new JsonObject();
        if (root["projects"] is not JsonObject projects)
            root["projects"] = projects = new JsonObject();
        var key = ProjectKey(directory);
        if (projects[key] is not JsonObject project)
            projects[key] = project = new JsonObject();
        if (project["hasTrustDialogAccepted"] is JsonValue accepted && accepted.TryGetValue<bool>(out var yes) && yes)
            return false;
        project["hasTrustDialogAccepted"] = true;

        var temporary = $"{configPath}.sidehub-{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
                JsonSerializer.Serialize(stream, root, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, configPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return true;
    }
}
