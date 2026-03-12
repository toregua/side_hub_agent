using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideHub.Agent;

public static class InstanceRegistry
{
    private static readonly string RegistryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".sidehub",
        "instances.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<InstanceEntry> LoadAll()
    {
        if (!File.Exists(RegistryPath))
            return [];

        try
        {
            var json = File.ReadAllText(RegistryPath);
            return JsonSerializer.Deserialize<List<InstanceEntry>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            Console.WriteLine($"[SideHub] Warning: corrupted registry at {RegistryPath}, treating as empty");
            return [];
        }
    }

    public static List<InstanceEntry> LoadValid()
    {
        return LoadAll()
            .Where(e => Directory.Exists(Path.Combine(e.Directory, ".sidehub")))
            .ToList();
    }

    public static void Register(string directory)
    {
        var absPath = Path.GetFullPath(directory);
        var entries = LoadAll();

        var existing = entries.FindIndex(e =>
            string.Equals(e.Directory, absPath, StringComparison.Ordinal));

        if (existing >= 0)
        {
            entries[existing] = new InstanceEntry { Directory = absPath, RegisteredAt = DateTime.UtcNow };
        }
        else
        {
            entries.Add(new InstanceEntry { Directory = absPath, RegisteredAt = DateTime.UtcNow });
        }

        Save(entries);
    }

    public static void Unregister(string directory)
    {
        var absPath = Path.GetFullPath(directory);
        var entries = LoadAll();
        entries.RemoveAll(e => string.Equals(e.Directory, absPath, StringComparison.Ordinal));
        Save(entries);
    }

    private static void Save(List<InstanceEntry> entries)
    {
        var dir = Path.GetDirectoryName(RegistryPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(RegistryPath, json);
    }
}

public class InstanceEntry
{
    [JsonPropertyName("directory")]
    public string Directory { get; set; } = "";

    [JsonPropertyName("registeredAt")]
    public DateTime RegisteredAt { get; set; }
}
