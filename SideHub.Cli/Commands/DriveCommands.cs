using System.Text.Json;

namespace SideHub.Cli.Commands;

public static class DriveCommands
{
    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var result = await client.GetDriveTreeAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (!result.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
        {
            Console.WriteLine("No items in drive.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"TYPE",-8} {"TITLE",-40} {"UPDATED"}");
        Console.WriteLine(new string('-', 110));
        PrintTree(items, 0);
        return 0;
    }

    private static void PrintTree(JsonElement items, int depth)
    {
        foreach (var item in items.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var type = item.GetProperty("type").GetString() ?? "";
            var title = item.GetProperty("title").GetString() ?? "";
            var updated = item.TryGetProperty("updatedAt", out var u) ? u.GetString()?[..10] ?? "" : "";
            var indent = new string(' ', depth * 2);

            Console.WriteLine($"{id,-38} {type,-8} {indent}{Truncate(title, 40 - depth * 2),-40} {updated}");

            if (item.TryGetProperty("children", out var children) && children.GetArrayLength() > 0)
                PrintTree(children, depth + 1);
        }
    }

    public static async Task<int> ReadAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(pageId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive read <pageId>");
            return 1;
        }

        var result = await client.GetDriveItemAsync(pageId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var title = result.TryGetProperty("title", out var t) ? t.GetString() : "";
        var content = result.TryGetProperty("content", out var c) ? c.GetString() : "";

        Console.WriteLine($"# {title}");
        Console.WriteLine();
        if (!string.IsNullOrEmpty(content))
            Console.WriteLine(content);
        return 0;
    }

    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var title = GetOption(args, "--title");
        var content = GetOption(args, "--content");
        var parentId = GetOption(args, "--parent");

        if (string.IsNullOrEmpty(title))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive create --title \"...\" [--content \"...\"] [--parent <id>]");
            return 1;
        }

        if (content is not null && content.Length > 100 * 1024)
        {
            Console.Error.WriteLine("Error: content exceeds 100KB limit");
            return 1;
        }

        var result = await client.CreateDriveItemAsync(title, content, parentId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var id = result.TryGetProperty("id", out var i) ? i.GetString() : "";
        Console.WriteLine($"Created page: {id}");
        return 0;
    }

    public static async Task<int> UpdateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var title = GetOption(args, "--title");
        var content = GetOption(args, "--content");

        if (string.IsNullOrEmpty(pageId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive update <pageId> [--title \"...\"] [--content \"...\"]");
            return 1;
        }

        if (content is not null && content.Length > 100 * 1024)
        {
            Console.Error.WriteLine("Error: content exceeds 100KB limit");
            return 1;
        }

        var result = await client.UpdateDriveItemAsync(pageId, title, content);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Updated page: {pageId}");
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
