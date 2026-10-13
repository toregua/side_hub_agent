using System.Globalization;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// The workspace's secrets, by name: to grant one to a workflow or a scheduler. Values are write-only and never come
/// back from the API; they are set from SideHub, never from a terminal.
/// </summary>
public static class SecretCommands
{
    public static async Task<int> ListAsync(SideHubApiClient client, bool json)
    {
        var secrets = await client.GetSecretsAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(secrets));
            return 0;
        }

        if (secrets.ValueKind != JsonValueKind.Array || secrets.GetArrayLength() == 0)
        {
            Console.WriteLine("No secrets in this workspace.");
            return 0;
        }

        Console.WriteLine($"{"NAME",-32} {"UPDATED",-22} {"LAST USED",-22} {"USED BY"}");
        Console.WriteLine(new string('-', 110));
        foreach (var secret in secrets.EnumerateArray())
        {
            var usedBy = secret.TryGetProperty("usedBy", out var u) && u.ValueKind == JsonValueKind.Array && u.GetArrayLength() > 0
                ? string.Join(", ", u.EnumerateArray().Select(x => $"{Prop(x, "kind")} {Prop(x, "name")} ({Prop(x, "id")})"))
                : "-";
            Console.WriteLine($"{Prop(secret, "name"),-32} {PropDate(secret, "updatedAt"),-22} {PropDate(secret, "lastUsedAt"),-22} {usedBy}");
        }
        return 0;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string PropDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm UTC") : "-";
}
