using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>
/// What a coding CLI waits for, read from the hook that reported <c>waiting-input</c>, so a phone can answer it with a
/// tap instead of the arrow keys. Two kinds, from Claude Code hooks only:
/// <list type="bullet">
/// <item><c>question</c>: <c>PreToolUse</c> of <c>AskUserQuestion</c> with a single single-choice question. Its menu
/// selects an option on its number key, and the number after the last option opens "Type something".</item>
/// <item><c>permission</c>: <c>PermissionRequest</c>. Its menu always starts with "Yes" (key 1); the other options
/// depend on the tool and are not in the hook, so only the tool and what it would run are given.</item>
/// </list>
/// Anything else (several questions, multiple choice, an input cut short) gives no prompt: the phone keeps its arrows.
/// Texts are cut to fixed lengths so a line through the FIFO stays small.
/// </summary>
public static class CliPrompt
{
    public const string Question = "question";
    public const string Permission = "permission";

    // The agent reads FIFO lines of 8192 characters at most: these keep a prompt well under it.
    public const int MaxQuestion = 300;
    public const int MaxDescription = 200;
    public const int MaxLabel = 120;
    public const int MaxHeader = 60;
    public const int MaxTool = 100;
    public const int MaxDetail = 500;
    /// <summary>AskUserQuestion offers 2 to 4 options.</summary>
    public const int MaxOptions = 4;

    /// <summary>tool_input fields that say what a tool would act on, in order of preference.</summary>
    private static readonly string[] DetailFields = ["command", "file_path", "notebook_path", "url", "pattern", "path", "query"];

    /// <summary>The prompt of a Claude Code hook input, as the JSON object written to the agent; null when there is none.</summary>
    public static JsonObject? FromHookInput(byte[]? input)
    {
        if (input is null || input.Length == 0)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var hookEvent = String(root, "hook_event_name");
            var tool = String(root, "tool_name");
            var toolInput = root.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object ? ti : default;
            return hookEvent switch
            {
                "PreToolUse" when tool == "AskUserQuestion" && toolInput.ValueKind == JsonValueKind.Object => QuestionPrompt(toolInput),
                "PermissionRequest" when tool is { Length: > 0 } => PermissionPrompt(tool, toolInput),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null; // cut short (MaxHookInputBytes) or not JSON
        }
    }

    private static JsonObject? QuestionPrompt(JsonElement toolInput)
    {
        if (!toolInput.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array
            || questions.GetArrayLength() != 1)
            return null;
        var question = questions[0];
        if (question.ValueKind != JsonValueKind.Object
            || (question.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True))
            return null;
        var text = String(question, "question");
        if (string.IsNullOrWhiteSpace(text)
            || !question.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return null;
        var count = options.GetArrayLength();
        if (count is < 1 or > MaxOptions)
            return null;

        var list = new JsonArray();
        foreach (var option in options.EnumerateArray())
        {
            var label = option.ValueKind == JsonValueKind.Object ? String(option, "label") : null;
            if (string.IsNullOrWhiteSpace(label))
                return null;
            var entry = new JsonObject { ["label"] = Cut(label, MaxLabel) };
            if (String(option, "description") is { Length: > 0 } description)
                entry["description"] = Cut(description, MaxDescription);
            list.Add(entry);
        }

        var prompt = new JsonObject { ["kind"] = Question, ["question"] = Cut(text, MaxQuestion), ["options"] = list };
        if (String(question, "header") is { Length: > 0 } header)
            prompt["header"] = Cut(header, MaxHeader);
        return prompt;
    }

    private static JsonObject PermissionPrompt(string tool, JsonElement toolInput)
    {
        var prompt = new JsonObject { ["kind"] = Permission, ["tool"] = Cut(tool, MaxTool) };
        if (toolInput.ValueKind != JsonValueKind.Object)
            return prompt;
        foreach (var field in DetailFields)
        {
            if (String(toolInput, field) is { Length: > 0 } detail)
            {
                prompt["detail"] = Cut(detail, MaxDetail);
                break;
            }
        }
        if (String(toolInput, "description") is { Length: > 0 } description)
            prompt["description"] = Cut(description, MaxDescription);
        return prompt;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Cut(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
