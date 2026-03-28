using System.Net.Http.Json;
using System.Text.Json;

namespace SideHub.Cli;

public class SideHubApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _workspaceId;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public SideHubApiClient(string apiUrl, string agentToken, string workspaceId)
    {
        _workspaceId = workspaceId;
        _http = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Add("X-Agent-Token", agentToken);
    }

    // --- Drive ---

    public async Task<JsonElement> GetDriveTreeAsync()
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/drive");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetDriveItemAsync(string itemId)
    {
        var resp = await _http.GetAsync($"api/drive/{itemId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CreateDriveItemAsync(string title, string? content, string? parentId)
    {
        var body = new Dictionary<string, object?> { ["title"] = title, ["type"] = "page" };
        if (content is not null) body["content"] = content;
        if (parentId is not null) body["parentId"] = parentId;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/drive", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateDriveItemAsync(string itemId, string? title, string? content)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null) body["title"] = title;
        if (content is not null) body["content"] = content;

        var resp = await _http.PutAsJsonAsync($"api/drive/{itemId}", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- Tasks ---

    public async Task<JsonElement> GetTasksAsync(string? status)
    {
        var url = $"api/workspaces/{_workspaceId}/tasks";
        if (!string.IsNullOrEmpty(status)) url += $"?status={Uri.EscapeDataString(status)}";

        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CreateTaskAsync(string title, string? description, string? type)
    {
        var body = new Dictionary<string, object?> { ["title"] = title };
        if (description is not null) body["description"] = description;
        if (type is not null) body["type"] = type;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/tasks", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> AddCommentAsync(string taskId, string text)
    {
        var body = new { text };
        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/comments", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> AddBlockerAsync(string taskId, string reason)
    {
        var body = new { reason };
        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/blocker", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public void Dispose() => _http.Dispose();

    public static string Serialize(JsonElement element) =>
        JsonSerializer.Serialize(element, JsonOptions);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        string? serverMessage = null;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.TryGetProperty("error", out var err))
                serverMessage = err.GetString();
        }
        catch { /* body is not JSON */ }

        var message = (int)response.StatusCode switch
        {
            401 => serverMessage ?? "Authentication failed. Check your SIDEHUB_AGENT_TOKEN.",
            403 => serverMessage ?? "Access denied.",
            404 => serverMessage ?? "Resource not found.",
            _ => serverMessage ?? $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
        };

        throw new HttpRequestException(message);
    }
}
