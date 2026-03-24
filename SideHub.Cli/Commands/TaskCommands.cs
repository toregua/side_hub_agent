using System.Text.Json;

namespace SideHub.Cli.Commands;

public static class TaskCommands
{
    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var status = GetOption(args, "--status");
        var result = await client.GetTasksAsync(status);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (!result.TryGetProperty("tasks", out var tasks) || tasks.GetArrayLength() == 0)
        {
            Console.WriteLine("No tasks found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STATUS",-14} {"TYPE",-12} {"TITLE"}");
        Console.WriteLine(new string('-', 100));
        foreach (var task in tasks.EnumerateArray())
        {
            var id = task.GetProperty("id").GetString() ?? "";
            var s = task.TryGetProperty("status", out var sv) ? sv.GetString() ?? "" : "";
            var type = task.TryGetProperty("type", out var tv) ? tv.GetString() ?? "" : "";
            var title = task.TryGetProperty("title", out var ttv) ? ttv.GetString() ?? "" : "";
            Console.WriteLine($"{id,-38} {s,-14} {type,-12} {title}");
        }
        return 0;
    }

    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var title = GetOption(args, "--title");
        var description = GetOption(args, "--description");
        var type = GetOption(args, "--type");

        if (string.IsNullOrEmpty(title))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task create --title \"...\" [--description \"...\"] [--type <type>]");
            return 1;
        }

        var result = await client.CreateTaskAsync(title, description, type);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var id = result.TryGetProperty("id", out var i) ? i.GetString() : "";
        Console.WriteLine($"Created task: {id}");
        return 0;
    }

    public static async Task<int> CommentAsync(SideHubApiClient client, string[] args, string? envTaskId, bool json)
    {
        var text = GetOption(args, "--text");
        // taskId: first positional arg (non-flag), or fallback to SIDEHUB_TASK_ID
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--") && a != text) ?? envTaskId;

        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(text))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task comment [<taskId>] --text \"...\"");
            Console.Error.WriteLine("If taskId is omitted, SIDEHUB_TASK_ID is used.");
            return 1;
        }

        var result = await client.AddCommentAsync(taskId, text);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Comment added to task: {taskId}");
        return 0;
    }

    public static async Task<int> BlockerAsync(SideHubApiClient client, string[] args, string? envTaskId, bool json)
    {
        var reason = GetOption(args, "--reason");
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--") && a != reason) ?? envTaskId;

        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(reason))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task blocker [<taskId>] --reason \"...\"");
            Console.Error.WriteLine("If taskId is omitted, SIDEHUB_TASK_ID is used.");
            return 1;
        }

        var result = await client.AddBlockerAsync(taskId, reason);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Blocker added to task: {taskId}");
        return 0;
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }
}
