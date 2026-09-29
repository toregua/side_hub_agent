namespace SideHub.Cli.Commands;

public static class RepositoryCommands
{
    public static async Task<int> ListAsync(SideHubApiClient client, bool json)
    {
        var result = await client.GetRepositoriesAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (!result.TryGetProperty("repositories", out var repositories) || repositories.GetArrayLength() == 0)
        {
            Console.WriteLine("No repositories found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"BRANCH",-14} {"NAME"}");
        Console.WriteLine(new string('-', 80));
        foreach (var repository in repositories.EnumerateArray())
        {
            var id = repository.GetProperty("id").GetString() ?? "";
            var branch = repository.TryGetProperty("defaultBranch", out var b) ? b.GetString() ?? "" : "";
            var name = repository.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            Console.WriteLine($"{id,-38} {branch,-14} {name}");
        }
        return 0;
    }
}
