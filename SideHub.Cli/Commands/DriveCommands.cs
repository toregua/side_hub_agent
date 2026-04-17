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
        {
            Console.WriteLine(content);
        }
        else if (result.TryGetProperty("downloadUrl", out var du) && !string.IsNullOrEmpty(du.GetString()))
        {
            var mime = result.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
            var size = result.TryGetProperty("fileSize", out var fs) && fs.ValueKind == JsonValueKind.Number
                ? fs.GetInt64().ToString() + " bytes"
                : "unknown size";
            var label = string.IsNullOrEmpty(mime) ? size : $"{mime}, {size}";
            Console.WriteLine($"Binary file ({label}). Use `sidehub-cli drive download {pageId}` to fetch it.");
        }
        return 0;
    }

    public static async Task<int> DownloadAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--") && !a.StartsWith("-"));
        if (string.IsNullOrEmpty(pageId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive download <pageId> [--output <path>] [--stdout] [--url-only]");
            return 1;
        }

        var output = GetOption(args, "--output") ?? GetOption(args, "-o");
        var toStdout = args.Contains("--stdout");
        var urlOnly = args.Contains("--url-only");

        if (toStdout && output is not null)
        {
            Console.Error.WriteLine("Error: --stdout and --output are mutually exclusive");
            return 1;
        }
        if (urlOnly && (toStdout || output is not null))
        {
            Console.Error.WriteLine("Error: --url-only cannot be combined with --stdout or --output");
            return 1;
        }

        var info = await client.GetDriveDownloadInfoAsync(pageId);

        if (urlOnly)
        {
            if (json)
            {
                var payload = new
                {
                    id = pageId,
                    fileName = info.FileName,
                    mimeType = info.MimeType,
                    fileSize = info.FileSize,
                    downloadUrl = info.DownloadUrl
                };
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine(info.DownloadUrl);
            }
            return 0;
        }

        if (toStdout)
        {
            using var stdout = Console.OpenStandardOutput();
            await client.DownloadToStreamAsync(info.DownloadUrl, stdout);
            return 0;
        }

        string targetPath;
        if (output is null)
        {
            targetPath = Path.Combine(Directory.GetCurrentDirectory(), info.FileName);
        }
        else if (Directory.Exists(output))
        {
            targetPath = Path.Combine(output, info.FileName);
        }
        else
        {
            targetPath = output;
            var parent = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        }

        long bytesWritten;
        await using (var fs = File.Create(targetPath))
        {
            await client.DownloadToStreamAsync(info.DownloadUrl, fs);
            bytesWritten = fs.Length;
        }

        var absolutePath = Path.GetFullPath(targetPath);

        if (json)
        {
            var payload = new
            {
                id = pageId,
                fileName = info.FileName,
                mimeType = info.MimeType,
                fileSize = info.FileSize,
                downloadUrl = info.DownloadUrl,
                savedTo = absolutePath,
                bytesWritten
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Saved {bytesWritten} bytes to {absolutePath}");
        }
        return 0;
    }

    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var title = GetOption(args, "--title");
        var content = GetOption(args, "--content");
        var parentId = GetOption(args, "--parent");
        var type = GetOption(args, "--type");

        if (string.IsNullOrEmpty(title))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive create --title \"...\" [--content \"...\"] [--parent <id>] [--type page|spreadsheet]");
            return 1;
        }

        if (type is not null && type != "page" && type != "spreadsheet")
        {
            Console.Error.WriteLine("Error: --type must be 'page' or 'spreadsheet'");
            return 1;
        }

        if (content is not null && content.Length > 100 * 1024)
        {
            Console.Error.WriteLine("Error: content exceeds 100KB limit");
            return 1;
        }

        var result = await client.CreateDriveItemAsync(title, content, parentId, type);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var id = result.TryGetProperty("id", out var i) ? i.GetString() : "";
        var label = type == "spreadsheet" ? "spreadsheet" : "page";
        Console.WriteLine($"Created {label}: {id}");
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

    public static async Task<int> SearchAsync(SideHubApiClient client, string[] args, bool json)
    {
        var query = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(query))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive search <query>");
            return 1;
        }

        var result = await client.GetDriveTreeAsync();

        if (!result.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
        {
            Console.WriteLine("No items in drive.");
            return 0;
        }

        var matches = new List<(string Id, string Type, string Title, string Updated)>();
        CollectMatches(items, query, matches);

        if (matches.Count == 0)
        {
            Console.WriteLine($"No pages matching \"{query}\".");
            return 0;
        }

        if (json)
        {
            var jsonArray = matches.Select(m => new { id = m.Id, type = m.Type, title = m.Title, updatedAt = m.Updated });
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(jsonArray, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"TYPE",-8} {"TITLE",-40} {"UPDATED"}");
        Console.WriteLine(new string('-', 110));
        foreach (var m in matches)
            Console.WriteLine($"{m.Id,-38} {m.Type,-8} {Truncate(m.Title, 40),-40} {m.Updated}");

        return 0;
    }

    private static void CollectMatches(JsonElement items, string query, List<(string Id, string Type, string Title, string Updated)> matches)
    {
        foreach (var item in items.EnumerateArray())
        {
            var title = item.GetProperty("title").GetString() ?? "";
            if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                var id = item.GetProperty("id").GetString() ?? "";
                var type = item.GetProperty("type").GetString() ?? "";
                var updated = item.TryGetProperty("updatedAt", out var u) ? u.GetString()?[..10] ?? "" : "";
                matches.Add((id, type, title, updated));
            }

            if (item.TryGetProperty("children", out var children) && children.GetArrayLength() > 0)
                CollectMatches(children, query, matches);
        }
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
